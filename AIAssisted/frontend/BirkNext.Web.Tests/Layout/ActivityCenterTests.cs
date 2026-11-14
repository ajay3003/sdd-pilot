using BirkNext.Web.Layout;
using BirkNext.Web.Services.Operations;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Web.Tests.Layout;

/// <summary>Activity Center: one small permanent indicator for all long-running work; measured progress only; cancel/open/dismiss; no
/// secrets; it observes and never owns the work.</summary>
public sealed class ActivityCenterTests : BunitContext
{
    private readonly BackgroundOperationService _ops = new(new TickingClock());

    /// <summary>Strictly increasing time (one millisecond per read): finish order is the order the transitions happened, never a clock tie.</summary>
    private sealed class TickingClock : TimeProvider
    {
        private readonly DateTimeOffset _start = DateTimeOffset.UtcNow;
        private long _ticks;
        public override DateTimeOffset GetUtcNow() => _start.AddMilliseconds(Interlocked.Increment(ref _ticks));
    }

    public ActivityCenterTests()
    {
        Services.AddSingleton(_ops);
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private (OperationRun Run, TaskCompletionSource Gate) Start(string project, OperationKind kind, Action<IOperationProgress>? report = null)
    {
        var gate = new TaskCompletionSource();
        var run = _ops.Start(new(kind, $"Importing project", new OperationContext(project, project) { Branch = "main" }, "project-import"), async (p, ct) =>
        {
            report?.Invoke(p);
            // Not on xUnit's synchronization context: the test thread may block while waiting for this to finish.
            await gate.Task.WaitAsync(ct).ConfigureAwait(false);
            return OperationOutcome.Success("project-import?done", "Open the imported project");
        });
        return (run, gate);
    }

    [Fact]
    public void NothingRunning_ShowsActivity_AndAnEmptyPanel()
    {
        var cut = Render<ActivityCenter>();
        cut.Find("[data-testid=activity-summary]").TextContent.Should().Be("Activity");
        cut.Find("[data-testid=activity-toggle]").GetAttribute("aria-expanded").Should().Be("false");
        cut.Find("[data-testid=activity-toggle]").Click();
        cut.Find("[data-testid=activity-panel]").GetAttribute("role").Should().Be("region");
        cut.Find("[data-testid=activity-empty]").Should().NotBeNull();
    }

    [Fact]
    public void OneOperation_ShowsProjectStageAndMeasuredPercent_TwoShowACount()
    {
        var cut = Render<ActivityCenter>();
        var (_, a) = Start("M2LB", OperationKind.ProjectImport, p => p.Stage("Importing project", null, 65, 100, "documents"));
        cut.WaitForAssertion(() => cut.Find("[data-testid=activity-summary]").TextContent.Should().Be("M2LB — Importing project · 65%"), TimeSpan.FromSeconds(5));

        var (_, b) = Start("Skolenærvær", OperationKind.DeliveryReportGeneration, p => p.Stage("Analyzing delivery", "Source Analysis stage 3 of 17"));
        cut.WaitForAssertion(() => cut.Find("[data-testid=activity-summary]").TextContent.Should().Be("2 operations running"), TimeSpan.FromSeconds(5));
        cut.Find("[data-testid=activity-toggle]").Click();
        var items = cut.FindAll("[data-testid=activity-item]");
        items.Should().HaveCount(2);
        cut.FindAll("[data-testid=activity-percent]").Should().ContainSingle("a stage count is never shown as a percentage");
        cut.Find("[data-testid=activity-item][data-kind=DeliveryReportGeneration]").TextContent.Should().Contain("Skolenærvær").And.Contain("Source Analysis stage 3 of 17");
        a.SetResult(); b.SetResult();
    }

    [Fact]
    public void Cancel_AffectsOnlyThatOperation_AndCompletedOffersItsResult_ThenDismiss()
    {
        var (first, _) = Start("M2LB", OperationKind.ProjectImport, p => p.Stage("Uploading", null, 1, 10, "bytes"));
        var (second, gate) = Start("Other", OperationKind.ProjectImport, p => p.Stage("Uploading", null, 1, 10, "bytes"));
        var cut = Render<ActivityCenter>();
        cut.Find("[data-testid=activity-toggle]").Click();

        cut.FindAll("[data-testid=activity-item]").Single(i => i.TextContent.Contains("M2LB")).QuerySelector("[data-testid=activity-cancel]")!.Click();
        SpinWait.SpinUntil(() => _ops.Find(first.Operation.Id)!.Status == OperationStatus.Cancelled, TimeSpan.FromSeconds(10)).Should().BeTrue("the cancelled operation ends as Cancelled");
        _ops.Find(second.Operation.Id)!.IsActive.Should().BeTrue();

        gate.SetResult();
        cut.WaitForAssertion(() => cut.Find("[data-testid=activity-item][data-status=Completed] [data-testid=activity-open]").TextContent.Should().Be("Open the imported project"), TimeSpan.FromSeconds(30)); // generous: under a full parallel run the completion can take seconds to propagate
        cut.Find("[data-testid=activity-item][data-status=Completed] [data-testid=activity-open]").GetAttribute("href").Should().Be("project-import?done");
        // The live region announces the finishes the last refresh observed, in finish order (see the deterministic test below).
        cut.WaitForAssertion(() => cut.Find("[data-testid=activity-live]").TextContent.Should().Contain("Completed"), TimeSpan.FromSeconds(10));
        cut.Find("[data-testid=activity-item][data-status=Completed] [data-testid=activity-dismiss]").Click();
        // Dismiss re-renders through the operations' Changed event (InvokeAsync): wait for it like the assertions above, never assume it is synchronous.
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=activity-item][data-status=Completed]").Should().BeEmpty(), TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// Regression for the full-suite flake of the test above. Changed is marshalled with InvokeAsync, so when the "Cancelled" notification runs
    /// only after the other operation already completed (a loaded thread pool), one Refresh observes both finishes. The live region used to
    /// keep whichever finished operation the list visited last (newest started first, so the older "Cancelled"), never announcing "Completed".
    /// Holding the renderer's dispatcher forces that interleaving deterministically.
    /// </summary>
    [Fact]
    public void FinishesObservedInOneRefresh_AreAllAnnouncedInFinishOrder()
    {
        var (first, _) = Start("M2LB", OperationKind.ProjectImport, p => p.Stage("Uploading", null, 1, 10, "bytes"));
        var (second, gate) = Start("Other", OperationKind.ProjectImport, p => p.Stage("Uploading", null, 1, 10, "bytes"));
        var cut = Render<ActivityCenter>();

        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        // Occupies the dispatcher from another thread: the component's InvokeAsync calls queue behind it instead of running inline.
        var hold = Task.Run(() => cut.InvokeAsync(() => { entered.Set(); release.Wait(); }));
        entered.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue("the dispatcher is held before anything finishes");
        try
        {
            _ops.Cancel(first.Operation.Id).Should().BeTrue();
            first.Completion.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
            gate.SetResult();
            second.Completion.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
        }
        finally { release.Set(); }
        hold.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();

        cut.WaitForAssertion(() => cut.Find("[data-testid=activity-live]").TextContent.Should().Be("Importing project: Cancelled. Importing project: Completed"), TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void Failure_ShowsTheSafeMessage_AndEscapeCloses()
    {
        _ops.Start(new(OperationKind.DeliveryAzureDevOpsImport, "Azure DevOps snapshot", new("M2LB"), "delivery-reporting"),
            (_, _) => throw new HttpRequestException("https://dev.azure.com/x?pat=secret-token"));
        var cut = Render<ActivityCenter>();
        cut.Find("[data-testid=activity-toggle]").Click();
        cut.Find("[data-testid=activity-error]").TextContent.Should().NotContain("secret-token");
        cut.Markup.Should().NotContain("secret-token");
        cut.Find("[data-testid=activity-panel]").KeyDown("Escape");
        cut.FindAll("[data-testid=activity-panel]").Should().BeEmpty();
    }

    [Fact]
    public void ClosingTheCenter_OrItsDisposal_NeverStopsTheWork()
    {
        var (run, gate) = Start("M2LB", OperationKind.ProjectImport);
        var cut = Render<ActivityCenter>();
        cut.Find("[data-testid=activity-toggle]").Click();
        cut.Find("[data-testid=activity-close]").Click();
        DisposeComponentsAsync().GetAwaiter().GetResult();
        _ops.Find(run.Operation.Id)!.IsActive.Should().BeTrue();
        gate.SetResult();
    }

    [Fact]
    public void WithoutTheOperationManager_TheLayoutStillRenders()
    {
        using var bare = new BunitContext();
        bare.Render<ActivityCenter>().Markup.Trim().Should().BeEmpty();
    }
}
