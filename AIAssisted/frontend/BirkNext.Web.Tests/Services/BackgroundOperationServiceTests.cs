using BirkNext.Web.Services.Operations;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>The shared operation manager: lifecycle, measured progress, subscribers that never own the work, explicit cancellation,
/// finalization, safe failures, duplicate prevention, isolation, retry linkage and retention.</summary>
public sealed class BackgroundOperationServiceTests
{
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static OperationRequest Request(string project = "M2LB", string? key = null, bool cancellable = true, Guid? retryOf = null) =>
        new(OperationKind.ProjectImport, $"Importing {project}", new OperationContext(project, project.ToLowerInvariant()) { Branch = "main", Baseline = "Monday" }, "project-import")
        { IdempotencyKey = key, Cancellable = cancellable, RetryOf = retryOf };

    [Fact]
    public async Task Start_RunsTheWork_UniqueIds_QueuedToCompleted_WithResultRoute()
    {
        var ops = new BackgroundOperationService();
        var gate = new TaskCompletionSource();
        var statuses = new List<OperationStatus>();
        ops.Changed += () => statuses.Add(ops.Operations[0].Status);

        var a = ops.Start(Request(), async (p, ct) => { p.Stage("Uploading", null, 50, 100, "bytes"); await gate.Task; return OperationOutcome.Success("project-import?done", "Open"); });
        var b = ops.Start(Request("Other"), (_, _) => Task.FromResult(OperationOutcome.Success()));

        a.Operation.Id.Should().NotBe(b.Operation.Id);
        ops.Find(a.Operation.Id)!.Status.Should().Be(OperationStatus.Running);
        ops.Find(a.Operation.Id)!.Percent.Should().Be(50);
        gate.SetResult();
        var done = await a.Completion;

        done.Status.Should().Be(OperationStatus.Completed);
        done.ResultRoute.Should().Be("project-import?done");
        done.CompletedAt.Should().NotBeNull();
        statuses.Should().Contain(OperationStatus.Running);
    }

    [Theory]
    [InlineData(OperationStatus.Completed, OperationStatus.Running, false)]
    [InlineData(OperationStatus.Cancelled, OperationStatus.Completed, false)]
    [InlineData(OperationStatus.Failed, OperationStatus.Running, false)]
    [InlineData(OperationStatus.Cancelling, OperationStatus.Running, false)]
    [InlineData(OperationStatus.Cancelling, OperationStatus.Completed, false)]
    [InlineData(OperationStatus.Running, OperationStatus.Completed, true)]
    [InlineData(OperationStatus.Queued, OperationStatus.Preparing, true)]
    [InlineData(OperationStatus.Cancelling, OperationStatus.Cancelled, true)]
    public void StateMachine_TerminalStatesNeverChange_CancellingNeverGoesBackToRunning(OperationStatus from, OperationStatus to, bool allowed) =>
        BackgroundOperationService.CanTransition(from, to).Should().Be(allowed);

    [Fact]
    public async Task Percent_OnlyWhenMeasured_StagesWithoutTotalAreIndeterminate()
    {
        var ops = new BackgroundOperationService();
        var gate = new TaskCompletionSource();
        var run = ops.Start(Request(), async (p, _) => { p.Stage("Analyzing source architecture", "Source Analysis stage 3 of 17"); await gate.Task; return OperationOutcome.Success(); });
        ops.Find(run.Operation.Id)!.Percent.Should().BeNull("a stage count is not a share of the work");
        ops.Find(run.Operation.Id)!.Detail.Should().Be("Source Analysis stage 3 of 17");
        gate.SetResult();
        await run.Completion;
    }

    [Fact]
    public async Task Subscribers_ComeAndGo_WithoutAffectingTheWork()
    {
        var ops = new BackgroundOperationService();
        var gate = new TaskCompletionSource();
        var first = 0; var second = 0;
        Action a = () => first++, b = () => second++;
        ops.Changed += a; ops.Changed += b;
        var run = ops.Start(Request(), async (_, _) => { await gate.Task; return OperationOutcome.Success(); });
        ops.Changed -= a; // the page that started it is left
        ops.Changed += () => throw new InvalidOperationException("a broken subscriber");
        gate.SetResult();

        (await run.Completion).Status.Should().Be(OperationStatus.Completed);
        second.Should().BeGreaterThan(first, "the remaining subscriber keeps receiving updates");
    }

    [Fact]
    public async Task Cancel_IsExplicit_ShowsCancelling_ThenCancelled_AndNeverPublishesASuccess()
    {
        var ops = new BackgroundOperationService();
        var seen = new List<OperationStatus>();
        var published = false;
        var run = ops.Start(Request(), async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            published = true;
            return OperationOutcome.Success();
        });
        ops.Changed += () => seen.Add(ops.Find(run.Operation.Id)!.Status);
        var other = ops.Start(Request("Other"), async (_, ct) => { await Task.Delay(50, ct); return OperationOutcome.Success(); });

        ops.Cancel(run.Operation.Id).Should().BeTrue();
        var done = await run.Completion;

        seen.Should().Contain(OperationStatus.Cancelling);
        seen.IndexOf(OperationStatus.Cancelling).Should().BeLessThan(seen.IndexOf(OperationStatus.Cancelled), "Cancelling is shown before cancellation is confirmed");
        done.Status.Should().Be(OperationStatus.Cancelled);
        done.CanRetry.Should().BeTrue();
        published.Should().BeFalse();
        (await other.Completion).Status.Should().Be(OperationStatus.Completed, "cancelling one operation never stops another");
        ops.Cancel(run.Operation.Id).Should().BeFalse("a finished operation cannot be cancelled");
    }

    [Fact]
    public async Task Finalization_RefusesALateCancel_AndACancelBeforeFinalizationBlocksIt()
    {
        var ops = new BackgroundOperationService();
        var finalizing = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var run = ops.Start(Request(), async (p, _) =>
        {
            p.BeginFinalization("Activating").Should().BeTrue();
            finalizing.SetResult();
            await release.Task;
            return OperationOutcome.Success();
        });
        await finalizing.Task;
        ops.Find(run.Operation.Id)!.CanCancel.Should().BeFalse();
        ops.Cancel(run.Operation.Id).Should().BeFalse("results are being written");
        release.SetResult();
        (await run.Completion).Status.Should().Be(OperationStatus.Completed);

        bool? began = null;
        var gate = new TaskCompletionSource();
        var early = ops.Start(Request("Early"), async (p, ct) =>
        {
            await gate.Task;
            began = p.BeginFinalization("Activating");
            ct.ThrowIfCancellationRequested();
            return OperationOutcome.Success();
        });
        ops.Cancel(early.Operation.Id).Should().BeTrue();
        gate.SetResult();
        (await early.Completion).Status.Should().Be(OperationStatus.Cancelled);
        began.Should().BeFalse();
    }

    [Fact]
    public async Task Failure_IsSafe_NeverTheExceptionText()
    {
        var ops = new BackgroundOperationService();
        var run = ops.Start(Request(), (_, _) => throw new InvalidOperationException("https://dev.azure.com/x?token=secret-value"));
        var done = await run.Completion;
        done.Status.Should().Be(OperationStatus.Failed);
        done.Error.Should().NotContain("secret-value").And.Contain("InvalidOperationException");

        var reported = await ops.Start(Request("Other"), (_, _) => Task.FromResult(OperationOutcome.Failure("Preview expired.", canRetry: false))).Completion;
        reported.Error.Should().Be("Preview expired.");
        reported.CanRetry.Should().BeFalse();
    }

    [Fact]
    public async Task SameIdempotencyKey_WhileActive_ReturnsTheRunningOperation_NoSecondRun()
    {
        var ops = new BackgroundOperationService();
        var runs = 0;
        var gate = new TaskCompletionSource();
        Func<IOperationProgress, CancellationToken, Task<OperationOutcome>> work = async (_, _) => { runs++; await gate.Task; return OperationOutcome.Success(); };

        var first = ops.Start(Request(key: "project-import"), work);
        var again = ops.Start(Request(key: "project-import"), work);
        var otherProject = ops.Start(Request("Other", key: "other-import"), work);

        again.Duplicate.Should().BeTrue();
        again.Operation.Id.Should().Be(first.Operation.Id);
        otherProject.Duplicate.Should().BeFalse("independent work is not blocked");
        runs.Should().Be(2);
        gate.SetResult();
        await Task.WhenAll(first.Completion, otherProject.Completion);
        ops.Start(Request(key: "project-import"), (_, _) => Task.FromResult(OperationOutcome.Success())).Duplicate.Should().BeFalse("a finished one does not block a new run");
    }

    [Fact]
    public async Task Context_IsCapturedAtStart_AndStaysWithTheOperation()
    {
        var ops = new BackgroundOperationService();
        var context = new OperationContext("M2LB", "m2lb") { Repository = "M2LB", Branch = "main", Baseline = "Monday", Revision = "abc1234" };
        var done = await ops.Start(new(OperationKind.DeliveryReportGeneration, "Generating", context, "delivery-reporting"), (_, _) => Task.FromResult(OperationOutcome.Success())).Completion;
        done.Context.Should().Be(context);
        ops.Latest(OperationKind.DeliveryReportGeneration, "m2lb")!.Id.Should().Be(done.Id);
        ops.Latest(OperationKind.DeliveryReportGeneration, "other").Should().BeNull("results never belong to another project");
    }

    [Fact]
    public async Task Retry_LinksToTheFailedOperation()
    {
        var ops = new BackgroundOperationService();
        var failed = await ops.Start(Request(), (_, _) => Task.FromResult(OperationOutcome.Failure("Backend unavailable."))).Completion;
        var retried = await ops.Start(Request(retryOf: failed.Id), (_, _) => Task.FromResult(OperationOutcome.Success())).Completion;
        retried.RetryOf.Should().Be(failed.Id);
        ops.Find(failed.Id)!.Error.Should().Be("Backend unavailable.", "the original failure stays visible");
    }

    [Fact]
    public async Task Retention_KeepsFinishedForTwoHours_AtMostTwenty_DismissOnlyFinished()
    {
        var clock = new Clock(new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero));
        var ops = new BackgroundOperationService(clock);
        for (var i = 0; i < 25; i++) await ops.Start(Request($"P{i}"), (_, _) => Task.FromResult(OperationOutcome.Success())).Completion;
        ops.Operations.Should().HaveCount(BackgroundOperationService.MaxFinished);

        var gate = new TaskCompletionSource();
        var active = ops.Start(Request("Active"), async (_, _) => { await gate.Task; return OperationOutcome.Success(); });
        ops.Dismiss(active.Operation.Id).Should().BeFalse("an active operation cannot be dismissed");
        ops.Operations[0].Id.Should().Be(active.Operation.Id, "active first");

        clock.Now = clock.Now.AddHours(3);
        ops.Operations.Should().ContainSingle().Which.Id.Should().Be(active.Operation.Id);
        gate.SetResult();
        var done = await active.Completion;
        ops.Dismiss(done.Id).Should().BeTrue();
        ops.Operations.Should().BeEmpty();
    }
}
