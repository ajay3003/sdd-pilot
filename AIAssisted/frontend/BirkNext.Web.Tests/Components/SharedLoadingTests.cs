using BirkNext.Web.Models;
using BirkNext.Web.Services.Operations;
using BirkNext.Web.Shared.Components.Loading;
using Bunit;
using FluentAssertions;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Shared loading, progress and operation lifecycle: delayed presentation by real time (LoadingThresholds; the timer only reveals an element
/// the caller still renders, so nothing is left stuck), multi-step progress reads the operation's real steps, nothing reads Running after
/// completion, and long-running work shows measured progress only.
/// </summary>
public sealed class SharedLoadingTests : BunitContext
{
    private static string Css(string file) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "BirkNext.Web", "wwwroot", "css", file));

    [Fact]
    public void Thresholds_AreShared_AndHiddenUntilRevealedIsTheOnlyCssRule()
    {
        LoadingThresholds.Inline.Should().Be(TimeSpan.FromMilliseconds(300));
        LoadingThresholds.Detail.Should().Be(TimeSpan.FromSeconds(2));
        LoadingThresholds.Slow.Should().Be(TimeSpan.FromSeconds(10));
        var css = Css("components.css");
        css.Should().Contain(".loading-delayed { visibility: hidden; }").And.Contain(".loading-delayed.loading-revealed { visibility: visible; }");
        css.Should().NotContain("loading-reveal 0s", "a CSS animation delay lags real time while WebAssembly blocks the main thread");
        css.Should().Contain("prefers-reduced-motion");
    }

    [Fact]
    public async Task DelayedPresentation_AdvancesByRealTime_AndStopsWhenDisposed()
    {
        var changes = 0;
        using (var phases = new DelayedPresentation(() => { changes++; return Task.CompletedTask; }, TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(80), TimeSpan.FromMilliseconds(120)))
        {
            phases.Phase.Should().Be(0, "nothing is visible at first");
            for (var waited = 0; phases.Phase < 3 && waited < 10_000; waited += 20) await Task.Delay(20);
            phases.Phase.Should().Be(3);
            changes.Should().Be(3);
        }

        var stopped = new DelayedPresentation(() => Task.CompletedTask, TimeSpan.FromMilliseconds(60));
        stopped.Dispose();
        await Task.Delay(200);
        stopped.Phase.Should().Be(0, "an operation that ended (element removed) is never revealed");
    }

    [Fact]
    public void InlineLoading_IsAnAnnouncedDelayedStatus_AndDisappearsWhenTheCallerStopsRenderingIt()
    {
        var cut = Render<InlineLoading>(p => p.Add(x => x.Text, "Loading test data…").Add(x => x.TestId, "td-loading").Add(x => x.Class, "td-muted"));
        var status = cut.Find("[data-testid=td-loading]");
        status.TagName.Should().Be("P");
        status.GetAttribute("role").Should().Be("status");
        status.GetAttribute("aria-live").Should().Be("polite");
        status.ClassList.Should().Contain(["loading-inline", "loading-delayed", "td-muted"]);
        status.ClassList.Should().NotContain("loading-revealed", "below the threshold nothing is visible");
        status.TextContent.Should().Be("Loading test data…");
        status.QuerySelector(".loading-spinner")!.GetAttribute("aria-hidden").Should().Be("true");
        cut.WaitForAssertion(() => cut.Find("[data-testid=td-loading]").ClassList.Should().Contain("loading-revealed"), TimeSpan.FromSeconds(3));

        Render<InlineLoading>(p => p.Add(x => x.Inline, true)).Find(".loading-inline").TagName.Should().Be("SPAN");
    }

    [Fact]
    public void LoadingSkeleton_HasItsShape_AndAScreenReaderStatus()
    {
        var cut = Render<LoadingSkeleton>(p => p.Add(x => x.Lines, 4).Add(x => x.Text, "Loading the backlog…").Add(x => x.TestId, "sk"));
        cut.FindAll(".loading-skeleton-line").Should().HaveCount(4).And.OnlyContain(l => l.GetAttribute("aria-hidden") == "true");
        cut.Find("[data-testid=sk] .visually-hidden").TextContent.Should().Be("Loading the backlog…");
        cut.Find("[data-testid=sk]").ClassList.Should().Contain("loading-delayed");
    }

    private static IReadOnlyList<ReviewStage> Steps(params ReviewStageState[] states) =>
        [.. states.Select((s, i) => new ReviewStage($"s{i}", $"Step {(char)('A' + i)}", s))];

    [Fact]
    public void OperationProgress_IsCompactFirst_NamesTheCurrentRealStep_AndDelaysTheFullList()
    {
        var cut = Render<OperationProgress>(p => p.Add(x => x.Title, "Assessing…")
            .Add(x => x.Stages, Steps(ReviewStageState.Done, ReviewStageState.Running, ReviewStageState.Pending)));

        var section = cut.Find("[data-testid=op-progress]");
        section.GetAttribute("aria-busy").Should().Be("true");
        section.ClassList.Should().Contain(["op-running", "loading-delayed"]);
        cut.Find("[data-testid=op-current]").TextContent.Should().Be("Step 2 of 3 · Step B");
        cut.FindAll("[data-testid=op-slow]").Should().BeEmpty("\"still working\" only after LoadingThresholds.Slow");
        cut.Find(".op-steps").ClassList.Should().Contain("op-steps-delayed", "the full list appears after LoadingThresholds.Detail");
        section.ClassList.Should().NotContain("loading-revealed");
        cut.WaitForAssertion(() => cut.Find("[data-testid=op-progress]").ClassList.Should().Contain("loading-revealed"), TimeSpan.FromSeconds(3));
        cut.WaitForAssertion(() => cut.Find(".op-steps").ClassList.Should().NotContain("op-steps-delayed"), TimeSpan.FromSeconds(5));
        cut.FindAll("[data-testid=op-stage]").Select(s => s.GetAttribute("data-state")).Should().Equal("Done", "Running", "Pending");
        cut.Markup.Should().NotContain("%", "no fabricated percentage");
    }

    [Fact]
    public void OperationProgress_Failure_ShowsWhereItFailed_AtOnce_WithRetry()
    {
        var retried = 0;
        var cut = Render<OperationProgress>(p => p.Add(x => x.Title, "Assessing…").Add(x => x.Error, "The evidence could not be loaded.")
            .Add(x => x.FailedTitle, "Readiness could not be fully assessed").Add(x => x.OnRetry, () => retried++)
            .Add(x => x.Stages, Steps(ReviewStageState.Done, ReviewStageState.Failed, ReviewStageState.Skipped)));

        var section = cut.Find("[data-testid=op-progress]");
        section.ClassList.Should().Contain("op-failed").And.NotContain("loading-delayed", "a failure is never delayed");
        section.GetAttribute("aria-busy").Should().Be("false");
        cut.Find("h2").TextContent.Should().Contain("Readiness could not be fully assessed");
        cut.Find("[data-testid=op-error]").TextContent.Should().Be("The evidence could not be loaded.");
        cut.Find(".op-steps").ClassList.Should().NotContain("op-steps-delayed");
        cut.FindAll("[data-testid=op-current]").Should().BeEmpty();
        cut.Find("[data-testid=op-retry]").Click();
        retried.Should().Be(1);
    }

    [Fact]
    public async Task Finish_LeavesNoStepPendingOrRunning_AfterAnEarlyOutcomeOrCancellation()
    {
        using var a = new ReviewAssessment(1, [("one", "One"), ("two", "Two"), ("three", "Three")]);
        await a.StageAsync("one", _ => Task.FromResult(1));
        a.Finish();
        a.Stages.Select(s => (s.State, s.Detail)).Should().Equal((ReviewStageState.Done, null), (ReviewStageState.Skipped, "not needed"), (ReviewStageState.Skipped, "not needed"));

        using var cancelled = new ReviewAssessment(2, [("slow", "Slow"), ("after", "After")]);
        var running = cancelled.StageAsync("slow", async ct => { await Task.Delay(Timeout.Infinite, ct); return 0; });
        cancelled.Stages[0].State.Should().Be(ReviewStageState.Running);
        cancelled.Cancel();
        await FluentActions.Awaiting(() => running).Should().ThrowAsync<OperationCanceledException>();
        cancelled.Finish();
        cancelled.Stages.Should().NotContain(s => s.State == ReviewStageState.Running || s.State == ReviewStageState.Pending, "nothing reads Running after completion");
        cancelled.Stages[0].Detail.Should().Be("stopped");
    }

    private static OperationSnapshot Op(OperationStatus status, long? done = null, long? total = null, string? unit = null, bool canCancel = false, string? error = null) => new()
    {
        Id = Guid.NewGuid(), Kind = OperationKind.DeliveryReportGeneration, Title = "Generating weekly report", Status = status, Stage = "Comparing snapshots",
        Done = done, Total = total, Unit = unit, CanCancel = canCancel, Error = error, StartedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public void PersistentOperationStatus_ShowsMeasuredProgressOnly_AndCancelOnlyWhereAllowed()
    {
        var measured = Render<PersistentOperationStatus>(p => p.Add(x => x.Operation, Op(OperationStatus.Running, 3, 12, "documents")));
        measured.Find("progress").GetAttribute("value").Should().Be("25");
        measured.Find("[data-testid=op-persistent-stage]").TextContent.Should().Be("Comparing snapshots");
        measured.Find("[data-testid=op-persistent]").GetAttribute("aria-busy").Should().Be("true");
        measured.FindAll("[data-testid=op-persistent-cancel]").Should().BeEmpty("this operation does not allow cancelling");

        Render<PersistentOperationStatus>(p => p.Add(x => x.Operation, Op(OperationStatus.Running))).FindAll("progress").Should().BeEmpty("no total, no percentage");
        Render<PersistentOperationStatus>(p => p.Add(x => x.Operation, Op(OperationStatus.Running, 2, 5, "stages"))).FindAll("progress").Should().BeEmpty("stage n of m is not a share of the work");

        var operations = new BackgroundOperationService();
        Render<PersistentOperationStatus>(p => p.Add(x => x.Operation, Op(OperationStatus.Running, canCancel: true)).Add(x => x.Operations, operations))
            .Find("[data-testid=op-persistent-cancel]").TextContent.Should().Be("Cancel");

        var failed = Render<PersistentOperationStatus>(p => p.Add(x => x.Operation, Op(OperationStatus.Failed, error: "The report could not be generated.")));
        failed.Find("[data-testid=op-persistent-state]").TextContent.Should().Be("Failed");
        failed.Find("[data-testid=op-persistent-error]").TextContent.Should().Be("The report could not be generated.");
        failed.FindAll(".loading-spinner").Should().BeEmpty("a finished operation shows no spinner");
        Render<PersistentOperationStatus>().Markup.Trim().Should().BeEmpty();
    }
}
