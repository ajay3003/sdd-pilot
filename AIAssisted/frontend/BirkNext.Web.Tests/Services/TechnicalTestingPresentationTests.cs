using BirkNext.ApiReview;
using BirkNext.Applicability;
using BirkNext.CriticalE2E;
using BirkNext.Integrations;
using BirkNext.PerformanceTests;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;
using T = BirkNext.Web.Services.TechnicalTestingPresentation;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// Technical Testing derives its areas, stages and next step from stored state only. Evidence (does the area apply), configuration and the latest
/// runtime result are separate dimensions; static evidence never becomes a runtime pass; stored results keep their environment, time and identity;
/// results that may no longer describe the current source or flow are marked, not presented as current.
/// </summary>
public sealed class TechnicalTestingPresentationTests
{
    /// <summary>Applicability backed by the project's own source (an evidence line), as the evaluator reports detected capabilities.</summary>
    private static ReviewApplicability A(ApplicabilityStatus s, string? action = null) => new() { Status = s, Reason = $"reason {s}", Action = action, Evidence = ["Source: Controllers/LoansController.cs"] };
    private static RuntimeResult R(AreaRuntime state, string? stale = null) => new(state, state.ToString(), DateTimeOffset.UtcNow, "QA", null, "Module", "module", stale);

    private static AreaFacts Area(TechnicalArea a, ApplicabilityStatus? s, RuntimeResult[]? runs = null, string[]? gaps = null, StaticFact[]? facts = null) =>
        new(a, s is { } status ? A(status) : null, facts ?? [], gaps ?? [], runs ?? []);

    private static T.Facts Facts(params AreaFacts[] areas) => new("library", "Library", "QA", "https://qa.example", "Project evidence: library", areas);

    [Fact]
    public void Stages_are_the_three_technical_stages_with_existing_routes()
    {
        T.Stages.Select(s => (s.Id, s.Title, s.Route)).Should().Equal(
            ("assess", "Discover & assess", "technical-testing/assess"),
            ("configure", "Configure & prepare", "technical-testing/configure"),
            ("verify", "Verify & investigate", "technical-testing/verify"));
        T.Areas.Select(T.Title).Should().Equal("Frontend & UI", "APIs & contracts", "Integrations", "Performance", "Critical E2E");
        T.Areas.Select(T.ReviewId).Should().Equal("frontend-quality-review", "api-quality-review", "integration-quality-review", "performance-test-review", "critical-e2e-regression");
    }

    [Theory]
    [InlineData(ApplicabilityStatus.Applicable, AreaEvidence.Applies)]
    [InlineData(ApplicabilityStatus.PartiallyApplicable, AreaEvidence.PartlyApplies)]
    [InlineData(ApplicabilityStatus.NeedsConfiguration, AreaEvidence.Applies)]
    [InlineData(ApplicabilityStatus.NotApplicable, AreaEvidence.NotApplicable)]
    [InlineData(ApplicabilityStatus.Unsupported, AreaEvidence.Unsupported)]
    [InlineData(ApplicabilityStatus.NotEnoughEvidence, AreaEvidence.NotEnoughEvidence)]
    [InlineData(ApplicabilityStatus.NeedsRefresh, AreaEvidence.NeedsRefresh)]
    public void Evidence_follows_technology_coverage_applicability(ApplicabilityStatus status, AreaEvidence expected) =>
        T.Evidence(Area(TechnicalArea.Api, status)).Should().Be(expected);

    [Theory]
    [InlineData(ApplicabilityStatus.Applicable, AreaEvidence.EnvironmentOnly)]
    [InlineData(ApplicabilityStatus.PartiallyApplicable, AreaEvidence.EnvironmentOnly)]
    [InlineData(ApplicabilityStatus.NeedsConfiguration, AreaEvidence.NotEnoughEvidence)]
    public void Without_source_evidence_a_configured_environment_is_not_project_evidence(ApplicabilityStatus status, AreaEvidence expected)
    {
        // The evaluator says "Applicable" whenever a target is configured, and "Needs configuration" when nothing is known: a documentation-only
        // project with an active target must not read as "evidence available".
        var f = new AreaFacts(TechnicalArea.Frontend, new ReviewApplicability { Status = status, Reason = "A browser target is configured." }, [], [], []);
        T.Evidence(f).Should().Be(expected);
        T.SourceBacked(f).Should().BeFalse();
        T.EvidenceLabel(AreaEvidence.EnvironmentOnly).Should().Be("Environment configured · no source evidence");
        T.InScope(f).Should().Be(expected == AreaEvidence.EnvironmentOnly, "an environment-only area is listed (honestly labelled); one with no evidence is not");
        T.Next(Facts(f)).Href.Should().NotBe("technical-testing/verify?area=frontend", "only a source-backed area is suggested as the next thing to verify");
    }

    [Fact]
    public void Unread_applicability_is_not_read_never_none()
    {
        var f = Area(TechnicalArea.Api, null);
        T.Evidence(f).Should().Be(AreaEvidence.NotRead);
        T.Configuration(f).Should().Be(AreaConfiguration.NotRead);
        T.EvidenceLabel(AreaEvidence.NotRead).Should().Be("Not read");
    }

    [Fact]
    public void Static_evidence_alone_is_never_a_runtime_result()
    {
        var f = Area(TechnicalArea.Api, ApplicabilityStatus.Applicable, facts: [new("3 contracts from the analysed source")]);
        T.Evidence(f).Should().Be(AreaEvidence.Applies);
        T.Runtime(f).Should().Be(AreaRuntime.NotRun);
        T.RuntimeLabel(f).Should().Be("Not run");
        T.Tone(T.Runtime(f)).Should().Be("neutral");
    }

    [Fact]
    public void Configuration_is_needed_from_applicability_or_the_module_gaps_and_not_applicable_areas_need_none()
    {
        T.Configuration(Area(TechnicalArea.Frontend, ApplicabilityStatus.NeedsConfiguration)).Should().Be(AreaConfiguration.NeedsConfiguration);
        T.Configuration(Area(TechnicalArea.CriticalE2E, ApplicabilityStatus.Applicable, gaps: ["No critical flow is defined yet."])).Should().Be(AreaConfiguration.NeedsConfiguration);
        T.Configuration(Area(TechnicalArea.Api, ApplicabilityStatus.Applicable)).Should().Be(AreaConfiguration.Configured);
        T.Configuration(Area(TechnicalArea.Frontend, ApplicabilityStatus.NotApplicable, gaps: ["No Target Environment is active."])).Should().Be(AreaConfiguration.NotApplicable);
    }

    [Fact]
    public void Areas_that_do_not_apply_are_out_of_scope_unless_they_have_evidence_or_results()
    {
        T.InScope(Area(TechnicalArea.Frontend, ApplicabilityStatus.NotApplicable)).Should().BeFalse("a backend-only project is not sent to Frontend Quality Review");
        T.InScope(Area(TechnicalArea.Performance, ApplicabilityStatus.Unsupported)).Should().BeFalse();
        T.InScope(Area(TechnicalArea.Frontend, ApplicabilityStatus.NotApplicable, runs: [R(AreaRuntime.Passed)])).Should().BeTrue("a recorded result is shown, whatever applicability says");
        T.InScope(Area(TechnicalArea.Integration, ApplicabilityStatus.NotEnoughEvidence, facts: [new("2 declared integration journeys")])).Should().BeTrue();
    }

    [Fact]
    public void Next_without_a_project_is_choosing_one()
    {
        var next = T.Next(new T.Facts(null, null, null, null, null, []));
        next.Href.Should().Be("project-import");
        T.Steps(new T.Facts(null, null, null, null, null, [])).Should().OnlyContain(s => s.State == GuidedStepState.Waiting);
    }

    [Fact]
    public void Next_without_a_target_is_configuring_one_when_a_target_bound_area_applies()
    {
        var f = Facts(Area(TechnicalArea.Api, ApplicabilityStatus.Applicable)) with { TargetName = null };
        T.Next(f).Href.Should().Be(T.TargetEnvironmentsRoute);
        T.Next(Facts(Area(TechnicalArea.Integration, ApplicabilityStatus.Applicable)) with { TargetName = null }).Href.Should().NotBe(T.TargetEnvironmentsRoute,
            "integrations alone do not need the target application");
    }

    [Fact]
    public void Next_prefers_investigating_a_failed_result_over_configuration_and_unrun_areas()
    {
        var f = Facts(
            Area(TechnicalArea.Frontend, ApplicabilityStatus.NeedsConfiguration),
            Area(TechnicalArea.Api, ApplicabilityStatus.Applicable),
            Area(TechnicalArea.Performance, ApplicabilityStatus.Applicable, runs: [R(AreaRuntime.Failed)]));
        T.Next(f).Should().Be(new T.NextAction("Investigate Performance", "technical-testing/verify?area=performance", "The latest Performance result is failed (Module)."));
    }

    [Fact]
    public void Next_then_configuration_then_unrun_then_stale_then_review()
    {
        T.Next(Facts(Area(TechnicalArea.Frontend, ApplicabilityStatus.NeedsConfiguration), Area(TechnicalArea.Api, ApplicabilityStatus.Applicable)))
            .Href.Should().Be("technical-testing/configure?area=frontend");
        T.Next(Facts(Area(TechnicalArea.Api, ApplicabilityStatus.Applicable, runs: [R(AreaRuntime.Passed)]), Area(TechnicalArea.Integration, ApplicabilityStatus.Applicable)))
            .Href.Should().Be("technical-testing/verify?area=integration");
        T.Next(Facts(Area(TechnicalArea.Performance, ApplicabilityStatus.Applicable, runs: [R(AreaRuntime.Passed, stale: "older snapshot")])))
            .Should().Be(new T.NextAction("Re-check Performance", "technical-testing/verify?area=performance", "older snapshot"));
        T.Next(Facts(Area(TechnicalArea.Api, ApplicabilityStatus.Applicable, runs: [R(AreaRuntime.Passed)]))).Href.Should().Be(T.AssessRoute);
        T.Next(Facts(Area(TechnicalArea.Api, ApplicabilityStatus.NotApplicable))).Why.Should().Contain("No technical area is supported");
    }

    [Fact]
    public void Steps_are_guidance_never_gates()
    {
        var steps = T.Steps(Facts(Area(TechnicalArea.Api, ApplicabilityStatus.Applicable), Area(TechnicalArea.Frontend, ApplicabilityStatus.NotApplicable)));
        steps.Select(s => s.Href).Should().Equal(T.AssessRoute, T.ConfigureRoute, T.VerifyRoute);
        steps.Should().OnlyContain(s => s.DisabledReason == null);
        steps[0].StatusLabel.Should().Be("1 of 5 areas in scope");
        steps[2].State.Should().Be(GuidedStepState.Current, "an applicable area has no run and nothing needs configuration");
        steps[2].StatusLabel.Should().Be("Nothing run yet");
    }

    [Fact]
    public void Stage_links_point_at_existing_specialist_routes()
    {
        foreach (var a in T.Areas)
            foreach (var stage in new[] { "assess", "configure", "verify" })
                T.Links(a, stage).Should().NotBeEmpty().And.OnlyContain(l => !l.Href.StartsWith("technical-testing/", StringComparison.Ordinal) || l.Href.StartsWith("technical-testing/integrations", StringComparison.Ordinal),
                    "an area links to its specialist modules (and integrations to the Integration Testing flow), never back to a stage");
        T.Links(TechnicalArea.Frontend, "verify").Should().Contain(("Frontend Quality Review", "frontend-quality-review"));
        T.Links(TechnicalArea.Integration, "assess").Should().Contain(("Integration Testing", "technical-testing/integrations?stage=discover"));
        T.FromSlug("e2e").Should().Be(TechnicalArea.CriticalE2E);
        T.FromSlug("nope").Should().BeNull();
    }

    // ---- mapping of stored results ----------------------------------------------------------------------------------------------------

    [Fact]
    public void Frontend_quality_review_runs_keep_disposition_and_coverage()
    {
        var profile = new FrontendAnalysisProfile { Id = "qa", Name = "QA" };
        var at = DateTimeOffset.UtcNow;
        TechnicalTestingWorkspace.Frontend(new() { CompletedAt = at, Disposition = FrontendQualityReleaseDisposition.Blocked, RequiredAssessed = 4, RequiredTotal = 4, IssueCount = 2 }, profile)
            .Should().Match<RuntimeResult>(r => r.State == AreaRuntime.Failed && r.At == at && r.Environment == "QA" && r.Detail == "4 of 4 required checks assessed · 2 issues");
        TechnicalTestingWorkspace.Frontend(new() { Disposition = FrontendQualityReleaseDisposition.NoAutomatedBlockDetected, RequiredAssessed = 2, RequiredTotal = 4 }, profile)
            .State.Should().Be(AreaRuntime.Incomplete, "not every required check was assessed");
        TechnicalTestingWorkspace.Frontend(new() { Disposition = FrontendQualityReleaseDisposition.NoAutomatedBlockDetected, RequiredAssessed = 4, RequiredTotal = 4 }, profile)
            .Verdict.Should().Be("No blocking issue detected");
        TechnicalTestingWorkspace.Frontend(new() { Disposition = null }, profile).State.Should().Be(AreaRuntime.Incomplete);
    }

    [Fact]
    public void Api_quality_review_runs_map_blocked_findings_and_incomplete_targets()
    {
        static AreaRuntime S(int targets, int completed, int blocked, int high, int medium) =>
            TechnicalTestingWorkspace.Api(new(DateTimeOffset.UtcNow, "QA", targets, completed, blocked, high, medium, 0, 0)).State;
        S(2, 0, 2, 0, 0).Should().Be(AreaRuntime.Blocked);
        S(0, 0, 0, 0, 0).Should().Be(AreaRuntime.Blocked, "nothing was reviewed");
        S(2, 2, 0, 1, 0).Should().Be(AreaRuntime.Failed);
        S(2, 1, 1, 0, 0).Should().Be(AreaRuntime.Incomplete);
        S(2, 2, 0, 0, 3).Should().Be(AreaRuntime.ReviewRequired);
        S(2, 2, 0, 0, 0).Should().Be(AreaRuntime.Passed);
    }

    [Fact]
    public void Integration_review_runs_never_pass_with_findings_or_limitations()
    {
        RuntimeResult I(IntegrationReviewOutcome o, int findings) => TechnicalTestingWorkspace.Integration(new(Guid.NewGuid(), DateTimeOffset.UtcNow, o, 3, findings), "QA");
        I(IntegrationReviewOutcome.Completed, 0).State.Should().Be(AreaRuntime.Passed);
        I(IntegrationReviewOutcome.Completed, 2).State.Should().Be(AreaRuntime.ReviewRequired);
        I(IntegrationReviewOutcome.CompletedWithLimitations, 0).State.Should().Be(AreaRuntime.Incomplete);
        I(IntegrationReviewOutcome.ManualReviewRequired, 0).State.Should().Be(AreaRuntime.ReviewRequired);
        I(IntegrationReviewOutcome.NothingAssessed, 0).Verdict.Should().Be("Nothing assessed");
    }

    [Fact]
    public void Performance_runs_keep_identity_and_are_marked_when_from_an_earlier_source_snapshot()
    {
        var now = Guid.NewGuid();
        var run = new PerformanceTestRun { State = PerformanceRunState.Completed, Verdict = PerformanceQualityVerdict.Fail, VersionLabel = "1.4.2", SourceSnapshotId = Guid.NewGuid(),
            FinishedAt = DateTimeOffset.UtcNow, EnvironmentType = "Test" };
        var r = TechnicalTestingWorkspace.Performance(run, "QA", now);
        r.State.Should().Be(AreaRuntime.Failed);
        r.Identity.Should().Be("1.4.2");
        r.Stale.Should().Contain("earlier source snapshot");
        TechnicalTestingWorkspace.Performance(run with { SourceSnapshotId = now }, "QA", now).Stale.Should().BeNull();
        TechnicalTestingWorkspace.Performance(run with { SourceSnapshotId = null }, "QA", now).Stale.Should().BeNull("an unrecorded snapshot is not evidence of staleness");
        TechnicalTestingWorkspace.Performance(run with { State = PerformanceRunState.ExecutionFailed }, "QA", now).State.Should().Be(AreaRuntime.Incomplete,
            "a run that did not complete is not a quality failure");
        TechnicalTestingWorkspace.Performance(run with { Verdict = PerformanceQualityVerdict.NotAssessed }, "QA", now).State.Should().Be(AreaRuntime.Incomplete);
    }

    [Fact]
    public void Critical_e2e_results_put_the_most_severe_flow_first_and_mark_stale_runs()
    {
        var old = DateTimeOffset.UtcNow.AddDays(-2);
        var flows = new[]
        {
            new CriticalE2EFlowSummary { Name = "Login", LastStatus = CriticalE2EStatus.Passed, LastRunAt = DateTimeOffset.UtcNow },
            new CriticalE2EFlowSummary { Name = "Submit report", LastStatus = CriticalE2EStatus.Failed, LastRunAt = old, LastRunStale = true },
            new CriticalE2EFlowSummary { Name = "Never run", LastStatus = CriticalE2EStatus.NotRun },
        };
        var results = TechnicalTestingWorkspace.CriticalE2E(flows, "QA").ToList();
        results.Select(r => r.Detail).Should().Equal("Submit report", "Login");
        results[0].Stale.Should().NotBeNull();
        results[1].Stale.Should().BeNull();
    }
}
