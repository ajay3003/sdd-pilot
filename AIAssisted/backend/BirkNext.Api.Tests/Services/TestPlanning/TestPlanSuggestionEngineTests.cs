using BirkNext.Api.Services.SystemUnderstanding;
using BirkNext.Api.Services.TestPlanning;
using BirkNext.SystemUnderstanding;
using BirkNext.TestPlanning;
using FluentAssertions;
using Xunit;
using E = BirkNext.Api.Services.TestPlanning.TestPlanSuggestionEngine;

namespace BirkNext.Api.Tests.Services.TestPlanning;

/// <summary>
/// The pure Test Plan Suggestion rules on the real curated Skolenærvær guide and small synthetic guides: drafts use the guide's own words, an
/// unreviewed or unclear expected result stays explicit, suites come from business-process stages, links to executable definitions need explicit
/// evidence (never title similarity), nothing is "ready" because it is documented, snapshot identity ignores bookkeeping, and a delivery
/// comparison separates behaviour, wording, review, test data and rule-version differences.
/// </summary>
public sealed class TestPlanSuggestionEngineTests
{
    private static readonly SystemGuideContent Guide = SystemGuideService.LoadSeed("SystemGuide.skolenaervaer.guide.json");
    private static GuideTestScenario S(string id) => Guide.Scenarios.Single(s => s.Id == id);

    private static readonly IReadOnlyList<ExecutableScenarioOption> Executables =
    [
        new(new ScenarioReference { Kind = ScenarioSourceKind.IntegrationJourney, Owner = "skolenaervaer/rapportmottak", ScenarioId = "valid-report" }, "Rapportmottak · E2E-01 Valid report", "Runnable"),
        new(new ScenarioReference { Kind = ScenarioSourceKind.IntegrationJourney, Owner = "skolenaervaer/rapportmottak", ScenarioId = "duplicate-report" }, "Rapportmottak · Duplicate report", "Not assessed"),
        new(new ScenarioReference { Kind = ScenarioSourceKind.IntegrationJourney, Owner = "skolenaervaer/rapportmottak", ScenarioId = "correction" }, "Rapportmottak · E2E-07 Correction", "Not assessed"),
    ];

    [Fact]
    public void A_draft_uses_the_guides_own_words_and_classifies_an_unreviewed_expected_result()
    {
        var c = E.Generate(Guide, S("gyldig-rapport"), reviewedGuide: false);
        c.Title.Should().Be("Gyldig rapport");
        c.Purpose.Should().Be(S("gyldig-rapport").Objective);
        c.ExpectedResult.Should().Be(S("gyldig-rapport").ExpectedOutcome);
        c.ExpectedBasis.Should().Be(ExpectedResultBasis.UnreviewedGuide, "the shipped guide is a curated draft, not reviewed here");
        c.Steps.Should().HaveCount(4).And.OnlyContain(step => step.Length > 0);
        c.Steps[0].Should().StartWith(Guide.Steps.Single(s => s.Id == "01").Title + ": ").And.Contain("TT02", "the step's own 'how to verify'");
        c.Requirements.Select(r => r.RuleId).Should().Contain("r-svar", "the guide rule that names the scenario");
        c.StageId.Should().Be("s-innsending");
        E.Generate(Guide, S("gyldig-rapport"), reviewedGuide: true).ExpectedBasis.Should().Be(ExpectedResultBasis.ReviewedGuide);
    }

    [Fact]
    public void A_scenario_the_guide_marks_unclear_stays_NEEDS_CLARIFICATION_and_a_missing_expected_result_is_never_invented()
    {
        var unclear = E.Assess(new TestCaseDraft { Generated = E.Generate(Guide, S("ukjent-xml-versjon"), true) });
        unclear.Generated.ExpectedBasis.Should().Be(ExpectedResultBasis.NeedsClarification);
        unclear.Missing.Should().Contain(m => m.Contains("NEEDS CLARIFICATION"));
        unclear.Testability.ExpectedBehavior.Should().Be(TestabilityFact.Missing);

        var synthetic = new GuideTestScenario { Id = "x", Title = "Without outcome", Objective = "o" };
        var missing = E.Assess(new TestCaseDraft { Generated = E.Generate(new SystemGuideContent { Scenarios = [synthetic] }, synthetic, true) });
        missing.Generated.ExpectedResult.Should().BeNull();
        missing.Generated.ExpectedBasis.Should().Be(ExpectedResultBasis.Missing);
        missing.Missing.Should().Contain("Expected result: NEEDS CLARIFICATION (none in the source)");
        missing.Missing.Should().Contain(m => m.StartsWith("Observation method")).And.Contain(m => m.StartsWith("Test data")).And.Contain(m => m.StartsWith("Requirement"));
    }

    [Fact]
    public void Suites_follow_the_guides_business_process_stages_in_stage_order_and_ungrouped_scenarios_are_shown_for_placement()
    {
        var cases = new[] { "periodelaas", "gyldig-rapport", "manglende-barn", "bufdata-fil", "duplikat-melding" }.Select(id => E.Generate(Guide, S(id), false)).ToList();
        var suites = E.ProposeSuites(Guide, cases);
        suites.Select(s => s.StageId).Should().Equal(Guide.Stages.Select(s => s.Id).Where(id => cases.Any(c => c.StageId == id)), "the guide's stage order");
        suites.Should().OnlyContain(s => s.Origin == "stage" && Guide.Stages.Any(st => st.Title == s.Name), "names are the guide's own stage titles, never directories");
        var lone = new GuideTestScenario { Id = "lone", Title = "No steps", Objective = "o" };
        E.ProposeSuites(new SystemGuideContent { Scenarios = [lone] }, [E.Generate(new SystemGuideContent(), lone, false)]).Single().Id.Should().Be(E.UngroupedSuiteId);
    }

    [Fact]
    public void Executable_links_come_only_from_an_explicit_identifier_never_from_a_similar_title_and_duplicates_are_ambiguous()
    {
        var links = E.Correlate(S("gyldig-rapport").ExistingTests, Executables);
        links.Should().ContainSingle().Which.Should().Match<ScenarioCorrelation>(l => l.Target.ScenarioId == "valid-report" && l.State == CorrelationState.Candidate && !l.Ambiguous);
        E.Correlate(S("duplikat-melding").ExistingTests, Executables).Should().ContainSingle(l => l.Target.ScenarioId == "duplicate-report", "the guide names 'BirkNext: duplicate-report'");
        E.Correlate(S("korrigert-rapport").ExistingTests, Executables).Should().BeEmpty("'Korrigert rapport' and 'Correction' are similar meanings, not evidence");
        var twice = Executables.Append(new(new ScenarioReference { Kind = ScenarioSourceKind.CriticalE2EFlow, Owner = "env", ScenarioId = "valid-report" }, "Critical E2E · valid", "Automated")).ToList();
        E.Correlate(S("gyldig-rapport").ExistingTests, twice).Should().HaveCount(2).And.OnlyContain(l => l.Ambiguous && l.State == CorrelationState.Candidate);
    }

    [Fact]
    public void A_documented_scenario_is_not_testable_until_input_data_expected_and_observation_are_established_and_environment_is_never_claimed()
    {
        var documented = E.Assess(new TestCaseDraft { Generated = E.Generate(Guide, S("gyldig-rapport"), false), Correlations = E.Correlate(S("gyldig-rapport").ExistingTests, Executables) });
        documented.Testability.Ready.Should().BeFalse();
        documented.Testability.Blockers.Should().Contain(b => b.Contains("input method")).And.Contain(b => b.Contains("Test Data")).And.Contain(b => b.Contains("reviewed source"));
        documented.Testability.NeedsTechnicalAssistance.Should().BeTrue("an unconfirmed candidate link is not an authorized input method");

        var prepared = E.Assess(documented with
        {
            Generated = documented.Generated with { ExpectedBasis = ExpectedResultBasis.ReviewedGuide },
            Correlations = [documented.Correlations[0] with { State = CorrelationState.Confirmed }],
            TestData = [new TestCaseDataLink { AssetId = "tda_1", Alias = "child", Approval = "Approved" }],
        });
        prepared.Testability.Ready.Should().BeTrue();
        prepared.Testability.Environment.Should().Be(TestabilityFact.NotVerified);
        prepared.Testability.Permissions.Should().Be(TestabilityFact.NotVerified);
        prepared.Testability.Summary.Should().Contain("not verified here");
    }

    [Fact]
    public void Snapshot_identity_ignores_bookkeeping_but_not_a_review_decision()
    {
        var plan = Plan("skolenaervaer", ("gyldig-rapport", "200 Mottatt", TestCaseReviewState.Generated));
        var resaved = plan with { Revision = 9, UpdatedAt = DateTimeOffset.UtcNow, UpdatedBy = "someone", Id = Guid.NewGuid() };
        E.ContentFingerprint(resaved).Should().Be(E.ContentFingerprint(plan));
        E.ContentFingerprint(plan with { Cases = [plan.Cases[0] with { Review = TestCaseReviewState.Accepted }] }).Should().NotBe(E.ContentFingerprint(plan));
    }

    [Fact]
    public void Delivery_comparison_finds_added_removed_changed_and_unchanged_and_only_calls_a_reviewed_expected_change_a_potential_rule_change()
    {
        var a = Snap(1, Plan("skolenaervaer", ("manglende-barn", "Rapporten holdes til barnet finnes", TestCaseReviewState.Accepted), ("gyldig-rapport", "200", TestCaseReviewState.Generated), ("ugyldig-xml", "Avvist", TestCaseReviewState.Generated)), reviewed: true);
        var b = Snap(2, Plan("skolenaervaer", ("manglende-barn", "Rapporten avvises", TestCaseReviewState.Accepted), ("gyldig-rapport", "200", TestCaseReviewState.Generated), ("periodelaas", "Låst", TestCaseReviewState.Generated)), reviewed: true);
        var result = E.Compare(a, b);
        result.Comparable.Should().BeTrue();
        result.Cases.Single(c => c.ScenarioId == "periodelaas").Kind.Should().Be(CaseChangeKind.Added);
        result.Cases.Single(c => c.ScenarioId == "ugyldig-xml").Kind.Should().Be(CaseChangeKind.Removed);
        result.Cases.Single(c => c.ScenarioId == "gyldig-rapport").Kind.Should().Be(CaseChangeKind.Unchanged);
        var changed = result.Cases.Single(c => c.ScenarioId == "manglende-barn");
        changed.Kind.Should().Be(CaseChangeKind.Changed);
        changed.Changes.Should().Contain(x => x.Category == "ExpectedResult" && x.Before == "Rapporten holdes til barnet finnes" && x.After == "Rapporten avvises");
        changed.PotentialBehaviourChange.Should().BeTrue();
        result.Recommendations.Should().Contain(r => r.Kind == "Expected result update" && r.ScenarioId == "manglende-barn").And.Contain(r => r.Kind == "New Test Case" && r.ScenarioId == "periodelaas");

        var unreviewed = E.Compare(Snap(1, a.Plan, reviewed: false), Snap(2, b.Plan, reviewed: false)).Cases.Single(c => c.ScenarioId == "manglende-barn");
        unreviewed.PotentialBehaviourChange.Should().BeFalse("a text change of an unreviewed expected result is not a confirmed behaviour change");
        unreviewed.Assessment.Should().Contain("needs review");
    }

    [Fact]
    public void A_rule_version_only_difference_is_context_not_a_case_change_and_different_systems_are_not_compared()
    {
        var plan = Plan("skolenaervaer", ("gyldig-rapport", "200", TestCaseReviewState.Generated));
        var result = E.Compare(Snap(1, plan, true), Snap(2, plan with { RuleVersion = "tps-2" }, true));
        result.Cases.Should().OnlyContain(c => c.Kind == CaseChangeKind.Unchanged);
        result.Context.Should().ContainSingle(c => c.Category == "Suggestion rules").Which.Meaning.Should().Contain("not an application behaviour change");
        var other = E.Compare(Snap(1, plan, true), Snap(2, plan with { GuideKey = "other-system" }, true));
        other.Comparable.Should().BeFalse();
        other.Cases.Should().BeEmpty();
    }

    [Fact]
    public void An_unchanged_high_risk_case_stays_in_regression()
    {
        var plan = Plan("skolenaervaer", ("gyldig-rapport", "200", TestCaseReviewState.Generated));
        plan = plan with { Cases = [plan.Cases[0] with { Risk = TestCaseRisk.High }] };
        E.Compare(Snap(1, plan, true), Snap(2, plan, true)).Recommendations.Should().ContainSingle(r => r.Kind == "Regression inclusion" && r.Reason.Contains("high risk"));
    }

    [Fact]
    public void The_export_is_BirkNexts_own_format_with_the_boundary_unresolved_questions_and_no_excluded_case_and_never_claims_Azure_DevOps_readiness()
    {
        var plan = Plan("skolenaervaer", ("gyldig-rapport", "200", TestCaseReviewState.Accepted), ("ugyldig-xml", "Avvist", TestCaseReviewState.Excluded));
        var export = E.Export(Snap(3, plan, true));
        export.Format.Should().Be("birknext.test-plan-proposal/v1");
        export.Json.Should().Contain("Gyldig rapport").And.NotContain("Ugyldig XML", "excluded cases are not exported");
        export.Text.Should().Contain("## Suite:").And.Contain("Expected: 200");
        export.Boundary.Should().Contain("not an Azure DevOps Test Plan").And.Contain("not verified");
        (export.Json + export.Text).Should().NotContainEquivalentOf("ADO-ready");
        export.FileName.Should().EndWith("-3.json");
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────────────────

    private static TestPlanDraft Plan(string guide, params (string Id, string Expected, TestCaseReviewState Review)[] cases) => new()
    {
        Id = Guid.NewGuid(), GuideKey = guide, PlanName = "Skolenærvær – Funksjonell testing", RuleVersion = E.RuleVersion,
        Basis = new TestPlanSourceBasis { GuideKey = guide, GuideRevision = 1, GuideFingerprint = "f" },
        Suites = [new TestSuiteDraft("suite:a", "Rapportmottak", "stage", "s-a")],
        Cases = [.. cases.Select(c => E.Assess(new TestCaseDraft
        {
            Source = new ScenarioReference { Kind = ScenarioSourceKind.SystemGuide, Owner = guide, ScenarioId = c.Id, Revision = 1, Fingerprint = "fp-" + c.Id },
            SuiteId = "suite:a", Review = c.Review,
            Generated = new TestCaseContent { Title = Guide.Scenarios.FirstOrDefault(s => s.Id == c.Id)?.Title ?? c.Id, Purpose = "p", ExpectedResult = c.Expected, ExpectedBasis = ExpectedResultBasis.UnreviewedGuide },
        }))],
    };

    private static TestPlanSnapshot Snap(int sequence, TestPlanDraft plan, bool reviewed)
    {
        var basis = reviewed ? ExpectedResultBasis.ReviewedGuide : ExpectedResultBasis.UnreviewedGuide;
        plan = plan with { Cases = [.. plan.Cases.Select(c => c with { Generated = c.Generated with { ExpectedBasis = basis } })] };
        return new TestPlanSnapshot
        {
            Plan = plan,
            Summary = new TestPlanSnapshotSummary(Guid.NewGuid(), plan.Id, plan.GuideKey, sequence, plan.PlanName, null, 1, E.ContentFingerprint(plan), plan.RuleVersion, 1, plan.Cases.Count, "t", DateTimeOffset.UtcNow, null),
        };
    }
    // ── Test type, execution method and performance cases ─────────────────────────────────────────────────────────

    [Fact]
    public void A_performance_case_without_thresholds_workload_or_environment_says_so_and_nothing_is_assumed()
    {
        var draft = new TestCaseDraft { Generated = E.Generate(Guide, S("gyldig-rapport"), true), Edits = new TestCaseEdits { TestType = TestCaseType.Performance } };
        var open = E.Assess(draft).Missing;
        open.Should().Contain("Acceptance criteria not defined — review required (performance thresholds)")
            .And.Contain(m => m.StartsWith("Workload: not described")).And.Contain("Performance environment: not confirmed");

        var described = draft with { Edits = draft.Edits with { Performance = new PerformanceCaseDetails
        {
            Subtype = PerformanceTestSubtype.Load, Workload = "Schools submit reports at the end of a period", AcceptanceCriteria = "p95 under 2 s", TargetEnvironment = "Test",
        } } };
        E.Assess(described).Missing.Should().NotContain(m => m.Contains("Acceptance criteria") || m.StartsWith("Workload:") || m.StartsWith("Performance environment"));

        E.Assess(draft with { Edits = new TestCaseEdits { TestType = TestCaseType.Functional } }).Missing.Should().NotContain(m => m.Contains("Acceptance criteria"),
            "performance questions apply only to performance cases");
        draft.EffectiveExecutionMethod.Should().Be(TestExecutionMethod.NotDecided, "a performance type never implies automated execution");
    }

    [Fact]
    public void Performance_details_are_exported_as_stated_and_type_method_and_workload_changes_are_compared()
    {
        E.PerformanceText(null).Should().BeEmpty();
        E.PerformanceText(new PerformanceCaseDetails { Workload = "50 schools" }).Should().Be("- Workload: 50 schools\n- Acceptance criteria: NOT DEFINED — review required",
            "only what the tester wrote, and an undefined threshold is said, never filled in");

        var plan = Plan("skolenaervaer", ("gyldig-rapport", "200", TestCaseReviewState.Generated));
        var before = plan with { Cases = [plan.Cases[0] with { Edits = new TestCaseEdits { TestType = TestCaseType.Performance, Performance = new PerformanceCaseDetails { Workload = "50 schools" } } }] };
        var after = plan with { Cases = [plan.Cases[0] with { Edits = new TestCaseEdits { TestType = TestCaseType.Performance, ExecutionMethod = TestExecutionMethod.ToolAssisted,
            Performance = new PerformanceCaseDetails { Workload = "200 schools" } } }] };
        var changes = E.Compare(Snap(1, before, true), Snap(2, after, true)).Cases.Single().Changes.Select(c => c.Category);
        changes.Should().Contain("Performance").And.Contain("ExecutionMethod").And.NotContain("TestType");
    }
}
