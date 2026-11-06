using BirkNext.Applicability;
using BirkNext.PerformanceTests;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// NEXT58B: Workload inline validation and estimate, threshold lifecycle (configured ≠ valid ≠ evaluated ≠ passed), execution resources by stage
/// (configured → validated → runtime available → execution verified, a limitation never promoted) and result interpretation (partial, cancelled,
/// execution failure apart from application failure).
/// </summary>
public sealed class PerformanceTabPresentationTests
{
    private static readonly PerformanceSafetyLimits Limits = new(50, 50, 1800, 3600, 200_000, true, true);
    private static readonly List<HttpPerformanceStep> Steps = [new() { Name = "lookup", RelativePath = "/api/x" }, new() { Name = "detail", RelativePath = "/api/y", ThinkTimeMs = 500 }];

    // ── Workload ──

    [Fact]
    public void A_valid_workload_has_no_issue_and_an_estimate_with_the_request_limit()
    {
        var w = new PerformanceWorkload { VirtualUsers = 5 };
        PerformanceWorkloadPresentation.Issues(w, Limits, Steps).Should().BeEmpty();
        var estimate = PerformanceWorkloadPresentation.Estimate(w, Limits, Steps);
        estimate.DurationSeconds.Should().Be(w.TotalSeconds);
        estimate.MaxRequests.Should().Be(PerformanceTestRules.EstimatedMaxRequests(w, Steps), "one formula for readiness and the tab");
        estimate.RequestLimit.Should().Be(200_000);
        estimate.PeakLoad.Should().Be("5 concurrent virtual users");
        PerformanceWorkloadPresentation.ThinkTime(Steps).Should().Be("1 of 2 steps pause 500 ms after their request.");
        PerformanceWorkloadPresentation.ThinkTime([Steps[0]]).Should().StartWith("None");
    }

    [Fact]
    public void Each_invalid_or_unsafe_value_is_attributed_to_its_field()
    {
        Fields(new PerformanceWorkload { VirtualUsers = 0, SteadyStateSeconds = 5 }).Should().BeEquivalentTo(new[] { ("steady", false), ("vus", false) });
        Fields(new PerformanceWorkload { WarmupSeconds = -1 }).Should().BeEquivalentTo(new[] { ("warmup", false) });
        Fields(new PerformanceWorkload { VirtualUsers = 80 }).Should().BeEquivalentTo(new[] { ("vus", true) });
        Fields(new PerformanceWorkload { Mode = WorkloadMode.ArrivalRate, VirtualUsers = 10, RequestsPerSecond = 0 }).Should().Contain(("rate", false));
        Fields(new PerformanceWorkload { Mode = WorkloadMode.ArrivalRate, VirtualUsers = 10, RequestsPerSecond = 5, StartRequestsPerSecond = 9 }).Should().Contain(("start-rate", false));
        Fields(new PerformanceWorkload { Mode = WorkloadMode.ArrivalRate, VirtualUsers = 10, RequestsPerSecond = 80 }).Should().Contain(("rate", true));
        Fields(new PerformanceWorkload { SteadyStateSeconds = 4000 }).Should().Contain(("duration", true));
        PerformanceWorkloadPresentation.Issues(new PerformanceWorkload { Purpose = WorkloadPurpose.Stress }, Limits with { AllowStress = false }, Steps).Should().Contain(i => i.Field == "purpose");
        PerformanceWorkloadPresentation.Issues(new PerformanceWorkload { VirtualUsers = 50, SteadyStateSeconds = 1700 }, Limits with { MaxTotalRequests = 1000 }, Steps)
            .Should().ContainSingle(i => i.Field == "requests" && i.Safety);
    }

    private static IEnumerable<(string, bool)> Fields(PerformanceWorkload w) => PerformanceWorkloadPresentation.Issues(w, Limits, Steps).Select(i => (i.Field, i.Safety));

    // ── Thresholds ──

    private static PerformanceThreshold T(PerformanceMetric m, ThresholdOperator o, double v, ThresholdSeverity s = ThresholdSeverity.Required) =>
        new() { Metric = m, Operator = o, Value = v, Severity = s };

    [Fact]
    public void Threshold_problems_are_one_shared_rule()
    {
        var problems = PerformanceTestRules.ThresholdProblems(
        [
            T(PerformanceMetric.LatencyP95Ms, ThresholdOperator.LessThan, 500),
            T(PerformanceMetric.LatencyP95Ms, ThresholdOperator.GreaterThan, 10),
            T(PerformanceMetric.LatencyP95Ms, ThresholdOperator.LessThan, 400),
            T(PerformanceMetric.LatencyP99Ms, ThresholdOperator.LessThan, 100),
            T(PerformanceMetric.LatencyP99Ms, ThresholdOperator.GreaterThanOrEqual, 100),
            T(PerformanceMetric.ErrorRatePercent, ThresholdOperator.LessThan, 120),
            T(PerformanceMetric.LatencyMaxMs, ThresholdOperator.LessThan, -5),
            T(PerformanceMetric.FailedRequests, ThresholdOperator.LessThan, 0),
            T(PerformanceMetric.LatencyMeanMs, ThresholdOperator.LessThanOrEqual, 0),
        ]);
        problems[0].Should().BeNull();
        problems[1].Should().BeNull("an upper and a lower bound that overlap are valid");
        problems[2].Should().StartWith("Duplicates threshold 1");
        problems[4].Should().StartWith("Contradicts threshold 4");
        problems[5].Should().Be("An error rate cannot exceed 100 %.");
        problems[6].Should().Be("The value must be 0 or more.");
        problems[7].Should().Be("No measured value can be below 0.");
        problems[8].Should().BeNull("≤ 0 can be met (0 failures)");
    }

    private static PerformanceTestRun Run(int version, PerformanceRunState state, List<PerformanceThresholdResult> results, bool partial = false) => new()
    {
        DefinitionId = "def", DefinitionVersion = version, State = state, ThresholdResults = results, MetricsPartial = partial, CreatedAt = DateTimeOffset.UtcNow, FinishedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public void Configured_valid_evaluated_passed_and_failed_are_counted_apart_and_only_for_the_same_saved_version()
    {
        var a = T(PerformanceMetric.LatencyP95Ms, ThresholdOperator.LessThan, 500);
        var b = T(PerformanceMetric.ErrorRatePercent, ThresholdOperator.LessThan, 1, ThresholdSeverity.Advisory);
        var c = T(PerformanceMetric.ErrorRatePercent, ThresholdOperator.LessThan, 150);
        var run = Run(3, PerformanceRunState.Completed,
        [
            new() { ThresholdId = a.Id, Metric = a.Metric, Outcome = CheckOutcome.Pass, Severity = ThresholdSeverity.Required, Measured = 410 },
            new() { ThresholdId = b.Id, Metric = b.Metric, Outcome = CheckOutcome.Fail, Severity = ThresholdSeverity.Advisory, Measured = 2 },
        ]);

        PerformanceThresholdPresentation.EvaluatingRun([run], "def", 3, unsaved: false).Should().BeSameAs(run);
        PerformanceThresholdPresentation.EvaluatingRun([run], "def", 4, unsaved: false).Should().BeNull("a run of v3 says nothing about v4");
        PerformanceThresholdPresentation.EvaluatingRun([run], "def", 3, unsaved: true).Should().BeNull("unsaved edits are not what ran");

        var (rows, life) = PerformanceThresholdPresentation.View([a, b, c], run, 3, false);
        life.Should().Be(new ThresholdLifecycle(3, 2, 2, 1, 1, life.EvaluatedIn));
        rows[0].RunState.Should().Be(ThresholdRunState.Passed);
        rows[0].RunText.Should().Contain("410");
        rows[1].RunState.Should().Be(ThresholdRunState.AdvisoryMissed);
        rows[2].RunState.Should().Be(ThresholdRunState.NotEvaluated);
        rows[2].Impact.Should().Be("Blocks readiness until fixed");
        rows[0].Impact.Should().StartWith("Required");

        var (unsavedRows, unsavedLife) = PerformanceThresholdPresentation.View([a], null, 3, true);
        unsavedRows.Single().RunState.Should().Be(ThresholdRunState.NotEvaluated);
        unsavedLife.Evaluated.Should().Be(0);
        unsavedLife.Passed.Should().Be(0, "a configured threshold is never a passed one");

        var partial = Run(3, PerformanceRunState.Cancelled, [new() { ThresholdId = a.Id, Metric = a.Metric, Outcome = CheckOutcome.NotAssessed, Explanation = "run cancelled" }], partial: true);
        var (partialRows, partialLife) = PerformanceThresholdPresentation.View([a], partial, 3, false);
        partialRows.Single().RunState.Should().Be(ThresholdRunState.NotAssessed, "a partial run never passes a threshold");
        partialLife.Passed.Should().Be(0);
        partialLife.EvaluatedIn.Should().Contain("partial metrics");
    }

    // ── Resources ──

    private static PerformanceProviderStatus Provider(bool image = true) => new()
    {
        ProviderId = "k6", Availability = ProviderAvailability.Available, Image = "docker.io/grafana/k6:1.0.0", ImagePresent = image, ImageDigest = "sha256:abc",
        Runtime = new PerformanceRuntimeStatus { RuntimeId = "podman", DisplayName = "Podman", Availability = ProviderAvailability.Available, Version = "5.4.2" },
        Capabilities = new PerformanceProviderCapabilities { Cancellation = true },
    };

    private static PerformanceTestReadiness Readiness(string limitsStatus) => new()
    {
        Items =
        [
            new("runtime", "Execution runtime", PerformanceReadinessState.Ready, "Podman 5.4.2", false),
            new("image", "Provider image", PerformanceReadinessState.Ready, "docker.io/grafana/k6:1.0.0", false),
            new("target", "Target", PerformanceReadinessState.Ready, "https://qa.example.test", false),
            new("network", "Container network", PerformanceReadinessState.Ready, "Reached the target.", false),
            new("ca-trust", "Corporate CA trust", PerformanceReadinessState.Ready, "Not required.", false) { Status = "Not required" },
            new("container-limits", "Container resource limits", limitsStatus == "Enforceable" ? PerformanceReadinessState.Ready : PerformanceReadinessState.Optional,
                $"Requested: memory 1024 MiB · 2 CPU · 512 processes. {(limitsStatus == "Enforceable" ? "Enforceable." : "Not enforced: memory, process limits — no cgroup controller.")}", false)
                { Status = limitsStatus, Technical = "cgroup controllers: cpu" },
        ],
    };

    [Fact]
    public void Unenforced_limits_stay_limited_at_every_stage_and_never_read_as_yes()
    {
        var executed = new PerformanceTestRun
        {
            StartedAt = DateTimeOffset.UtcNow, ExecutionEvidence = new PerformanceExecutionEvidence(), RuntimeId = "podman", RuntimeVersion = "5.4.2", ContainerImage = "docker.io/grafana/k6:1.0.0",
            Reachability = new PerformanceReachability { ExecutionOrigin = "container", Network = "pasta" },
            Limitations = ["The container ran without memory/pids limits: the Podman host delegates no such cgroup controller."],
        };
        var rows = PerformanceResourcePresentation.Rows(Provider(), Readiness("Partly enforced"), true, executed, "https://qa.example.test").ToDictionary(r => r.Key);
        rows["limits"].Runtime.State.Should().Be(ResourceStageState.Warning);
        rows["limits"].Runtime.Detail.Should().StartWith("Partly enforced");
        rows["limits"].Verified.State.Should().Be(ResourceStageState.Warning, "the run reported unapplied limits");
        rows["limits"].HasWarning.Should().BeTrue();
        rows["runtime"].Verified.State.Should().Be(ResourceStageState.Yes);
        rows["network"].Verified.Detail.Should().Contain("on network pasta");
        rows["certificates"].Configured.State.Should().Be(ResourceStageState.NotApplicable);

        var enforced = PerformanceResourcePresentation.Rows(Provider(), Readiness("Enforceable"), true, executed with { Limitations = [] }, "https://qa.example.test").Single(r => r.Key == "limits");
        enforced.Runtime.State.Should().Be(ResourceStageState.Yes);
        enforced.Verified.State.Should().Be(ResourceStageState.Yes);
    }

    [Fact]
    public void Without_current_readiness_or_a_run_the_stages_are_not_checked_and_a_missing_image_is_no()
    {
        var rows = PerformanceResourcePresentation.Rows(Provider(image: false), Readiness("Enforceable"), readinessCurrent: false, lastRun: null, "https://qa.example.test").ToDictionary(r => r.Key);
        rows["image"].Validated.State.Should().Be(ResourceStageState.NotChecked, "readiness is evaluated on saved versions only");
        rows["image"].Runtime.State.Should().Be(ResourceStageState.No);
        rows["limits"].Runtime.State.Should().Be(ResourceStageState.NotChecked);
        rows.Values.Should().OnlyContain(r => r.Verified.State == ResourceStageState.NotChecked, "nothing is execution-verified before a run");
        PerformanceResourcePresentation.Warnings(Provider(), Readiness("Not enforced"), true).Should().ContainSingle(w => w.StartsWith("Container resource limits"));
        PerformanceResourcePresentation.Warnings(Provider(), Readiness("Not enforced"), false).Should().BeEmpty();
    }

    // ── Results ──

    [Fact]
    public void Partial_cancelled_execution_and_application_failures_are_told_apart()
    {
        var metrics = new PerformanceMetrics { RequestCount = 100, FailedRequests = 4, SuccessfulRequests = 96, ErrorRatePercent = 4 };
        PerformanceResultPresentation.Interpret(new PerformanceTestRun { State = PerformanceRunState.Cancelled, Metrics = metrics, MetricsPartial = true })
            .Should().Match<RunInterpretation>(i => i.Completeness == "Partial" && i.FailureKind == "Cancelled" && i.FailureDetail.Contains("not a completed run"));
        PerformanceResultPresentation.Interpret(new PerformanceTestRun { State = PerformanceRunState.ExecutionFailed, StateReason = "k6 exited with code 107" })
            .Should().Match<RunInterpretation>(i => i.Completeness == "No metrics" && i.FailureKind == "Execution failure" && i.FailureDetail.Contains("application under test was not assessed"));
        PerformanceResultPresentation.Interpret(new PerformanceTestRun { State = PerformanceRunState.TimedOut, Metrics = metrics, MetricsPartial = true })
            .Should().Match<RunInterpretation>(i => i.Completeness == "Partial" && i.FailureKind == "Execution failure");
        PerformanceResultPresentation.Interpret(new PerformanceTestRun { State = PerformanceRunState.Completed, Metrics = metrics })
            .Should().Match<RunInterpretation>(i => i.Completeness == "Complete" && i.FailureKind == "Application responses failed" && i.FailureDetail.Contains("4 of 100"));
        PerformanceResultPresentation.Interpret(new PerformanceTestRun { State = PerformanceRunState.Completed, Metrics = metrics with { FailedRequests = 0 } }).FailureKind.Should().Be("No failed requests");
        PerformanceResultPresentation.Interpret(new PerformanceTestRun { State = PerformanceRunState.Running }).Completeness.Should().Be("In progress");
    }
}
