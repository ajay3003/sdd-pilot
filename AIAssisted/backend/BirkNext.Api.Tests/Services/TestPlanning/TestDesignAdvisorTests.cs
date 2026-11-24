using System.Runtime.CompilerServices;
using System.Text.Json;
using BirkNext.Api.Services.TestPlanning;
using BirkNext.TestCoverage;
using BirkNext.TestPlanning;
using FluentAssertions;
using Xunit;
using A = BirkNext.TestPlanning.ExecutionApproach;

namespace BirkNext.Api.Tests.Services.TestPlanning;

/// <summary>
/// Intelligent test design &amp; execution readiness (2026-11-24) on real M2LB scenarios (Fixtures/test-design-benchmark.json, verbatim from
/// AIAssisted/testdata/M2LB.zip) and real requirement text. Numbers in the test names follow the task's regression list.
/// </summary>
public sealed class TestDesignAdvisorTests
{
    private sealed record Sc(string Id, string Service, string SpecificationPath, string Given, string When, string Then);
    private sealed record Fx(List<Sc> Scenarios);
    private static readonly List<Sc> Real = JsonSerializer.Deserialize<Fx>(File.ReadAllText(Path.Combine(Dir(), "test-design-benchmark.json")),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!.Scenarios;
    private static string Dir([CallerFilePath] string path = "") => Path.Combine(Path.GetDirectoryName(path)!, "Fixtures");
    private static Sc Get(string id) => Real.Single(s => s.Id.StartsWith(id + "@", StringComparison.Ordinal));

    private static TestCaseDraft Case(Sc s, string title = "case", TestCaseReviewState review = TestCaseReviewState.Generated, TestCaseEdits? edits = null,
        List<TestCaseDataLink>? data = null, CaseSourceState? source = null) => Case(s.Given, s.When, s.Then, s.Service, title, review, edits, data, source);

    private static TestCaseDraft Case(string? given, string? when, string then, string module, string title = "case", TestCaseReviewState review = TestCaseReviewState.Generated,
        TestCaseEdits? edits = null, List<TestCaseDataLink>? data = null, CaseSourceState? source = null)
    {
        var i = when is null ? null : AcceptanceScenarioInterpreter.Interpret(given, when, then, null);
        return new TestCaseDraft
        {
            Source = new ScenarioReference { Kind = ScenarioSourceKind.ProjectDiscovery, Owner = "m2lb", ScenarioId = title },
            Review = review, SourceState = source, Edits = edits ?? new TestCaseEdits(), TestData = data ?? [],
            Generated = new TestCaseContent
            {
                Title = title, Purpose = "", Preconditions = given, Steps = i is null ? [] : [i.Action], StepExpected = [then], ExpectedResult = then,
                Capability = ScenarioOwnership.Specification, GroupTitle = module, TestDataNeed = TestDataNeed.Required,
                Template = [new("given", "Given", given ?? ""), new("when", "When", when ?? ""), new("then", "Then", then)],
                OpenQuestions = i is null ? ["How this is brought about in the test environment is not stated in the source — clarify before execution."] : [.. i.Unresolved],
            },
        };
    }

    private static ExecutionSuitability Suit(TestCaseDraft c) => TestDesignAdvisor.Suitability(TestDesignAdvisor.FactsOf(c));

    // ── Execution suitability (1–8) ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void T01_typing_in_a_search_field_and_seeing_a_list_is_manual()
    {
        var s = Suit(Case(Get("Frontend/US1-1")));
        s.Approaches.Select(a => a.Approach).Should().Equal(A.Manual);
        s.Manual.Single(m => m.Letter == "C").Answer.Should().Be(Tri.Yes);
    }

    [Fact]
    public void T02_a_valid_report_is_manual_plus_technical_because_storage_and_absence_of_the_national_id_must_be_verified()
    {
        var c = Case(Get("SkoleService/US1-1"));
        var s = Suit(c);
        s.Has(A.ManualTechnical).Should().BeTrue();
        s.Has(A.Manual).Should().BeFalse("the tester cannot see what is stored, published or logged");
        var points = TestDesignAdvisor.Verification(c);
        points.Should().Contain(p => p.Surface == VerificationSurface.PersistedState);
        points.Single(p => p.Negative).Should().Match<VerificationPoint>(p => p.Surface == VerificationSurface.AbsenceAcrossSurfaces && p.Critical && !p.MethodConfirmed);
    }

    [Fact]
    public void T03_an_adapter_change_that_SkoleService_receives_is_integration()
    {
        var c = Case(Get("SkoleAdapter/US1-1"));
        Suit(c).Has(A.IntegrationE2E).Should().BeTrue();
        TestDesignAdvisor.EndToEnd(TestDesignAdvisor.FactsOf(c))!.Services.Should().Equal(["SkoleAdapter", "BiRK", "SkoleService"], "BiRK and SkoleService are named by the scenario");
    }

    [Fact]
    public void T04_identical_content_and_repetition_recommend_automation()
    {
        Suit(Case(Get("Revisjon/US1-1"))).Has(A.AutomatedRecommended).Should().BeTrue("\"content identical … (no transformation)\" is a precise comparison");
        Suit(Case(Get("SkoleService/US1-9"))).Has(A.AutomatedRecommended).Should().BeTrue("\"delivered twice\"");
    }

    [Fact]
    public void T05_T43_an_unknown_trigger_is_a_readiness_gap_and_question_A_stays_unknown()
    {
        // SkoleService US4 scenario 3 has no When at all (verbatim).
        var c = Case("a held report still unresolved after a final lookup at lock", null,
            "it is rejected with reason \"unknown child\", counted as rejected, and the identifier and content are deleted", "SkoleService");
        Suit(c).Manual.Single(m => m.Letter == "A").Answer.Should().Be(Tri.Unknown);
        TestDesignAdvisor.Readiness(c).Checks.Single(x => x.Name == "Utløser").Satisfied.Should().Be(Tri.Unknown);
    }

    [Fact]
    public void T06_T44_without_a_confirmed_observation_method_a_critical_assertion_keeps_the_case_from_ready_to_run()
    {
        var approved = Case(Get("SkoleService/US1-1"), review: TestCaseReviewState.Accepted, edits: new TestCaseEdits { EnvironmentPrerequisites = "Test route agreed" },
            data: [new TestCaseDataLink { AssetId = "a", Alias = "gyldig", Approval = "Approved" }]);
        var r = TestDesignAdvisor.Readiness(approved);
        r.Checks.Single(x => x.Name == "Verifiseringsmetode").Satisfied.Should().Be(Tri.Unknown);
        r.Level.Should().Be(ReadinessLevel.NeedsPreparation, "an approved case is not ready to run while a critical assertion cannot be verified");

        var confirmed = approved with { Edits = approved.Edits with { ObservationMethod = "Read the stored report and the audit log with the test team's read access" } };
        TestDesignAdvisor.Verification(confirmed).Should().OnlyContain(p => p.MethodConfirmed);
    }

    [Fact]
    public void T44_a_flag_assertion_without_a_written_method_is_never_reported_as_confirmed()
    {
        // Browser walkthrough finding: SkoleService US1-4 showed «Bekreftet av en person» although nobody had written how to observe the flag.
        var r = TestDesignAdvisor.Readiness(Case(Get("SkoleService/US1-4")));
        var check = r.Checks.Single(x => x.Name == "Verifiseringsmetode");
        check.Satisfied.Should().Be(Tri.Unknown);
        check.Detail.Should().Contain("kritiske påstander uten bekreftet metode");
        TestDesignAdvisor.Verification(Case(Get("SkoleService/US1-4"))).Single().Critical.Should().BeTrue("a stored quality flag is a critical assertion");
    }

    [Fact]
    public void T07_one_scenario_can_have_several_approaches()
    {
        var s = Suit(Case(Get("SkoleService/US1-9")));
        s.Approaches.Should().HaveCountGreaterThan(1);
        s.Approaches.Should().OnlyContain(a => a.Reason.Length > 0, "every classification is explained");
    }

    [Fact]
    public void T08_existing_automated_tests_never_count_as_run()
    {
        var discovered = new DiscoveredScenario
        {
            CandidateId = "x", Title = "t", ExpectedBehaviour = Get("Frontend/US1-1").Then, Steps = [new DiscoveredStep("they type a child's name in the search field", null)],
            ExistingTests = [new CandidateTestAssociation("t1", "SearchFieldTests.Types_a_name", null, "named in the requirement")],
        };
        var s = TestDesignAdvisor.Suitability(TestDesignAdvisor.FactsOf(discovered));
        s.Approaches.Should().NotContain(a => a.Reason.Contains("kjørt", StringComparison.OrdinalIgnoreCase) || a.Reason.Contains("bestått", StringComparison.OrdinalIgnoreCase));
        TestDesignAdvisor.ReadinessLabel(ReadinessLevel.ReadyToRun).Should().NotContain("bestått");
    }

    // ── Test design (9–20) ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void T09_T10_more_than_0_5_hours_is_exclusive_and_only_the_stated_side_gets_an_expected_result()
    {
        var c = Case(Get("SkoleService/US1-4"));
        var b = TestDesignAdvisor.Boundaries(c).ToList();
        b.Should().HaveCount(3);
        b.Should().OnlyContain(p => p.Basis.Contains("strengt større enn 0.5") && p.Dimension == "Grenseverdi");
        b.Single(p => p.Title.Contains("nøyaktig")).Expected.Should().BeNull("0.5 itself is outside the rule; the source does not say what happens then");
        b.Single(p => p.Title.Contains("like innenfor")).Expected.Should().Be("It is accepted and flagged \"inconsistent hour totals\".");
        b.Single(p => p.Title.Contains("like utenfor")).Expected.Should().BeNull();
    }

    [Fact]
    public void T10_minimum_is_inclusive_so_the_threshold_itself_is_inside()
    {
        var c = Case("the caseworker is on the child search page", "they type a name with a minimum of 2 characters in the search field",
            "a list of matching children appears", "Frontend");
        var at = TestDesignAdvisor.Boundaries(c).Single(p => p.Title.Contains("nøyaktig"));
        at.Basis.Should().Contain("minst 2 (2 er innenfor)");
        at.Expected.Should().Be("A list of matching children appears.");
    }

    [Fact]
    public void T11_an_expression_boundary_keeps_its_range_and_values_are_left_to_the_contract()
    {
        var c = Case(Get("SkoleService/US1-11"));
        var b = TestDesignAdvisor.Boundaries(c).ToList();
        b.Should().Contain(p => p.Basis.Contains("24 × the number of days in its date range"));
        b.Should().OnlyContain(p => p.Basis.Contains("Kontroller i kontrakten"), "precision and allowed range come from the contract, not from here");
        b.Should().Contain(p => p.Dimension == "Ugyldig inndata" && p.Expected == null, "what a negative value does is not stated");
        b.Single(p => p.Title.Contains("like innenfor")).Expected.Should().Be("It is rejected with a machine-readable reason.");
    }

    private const string Skole = "SkoleService/specs/001-skoleservice/spec.md";
    private static readonly ProjectRequirementOption[] SkoleRules =
    [
        new("FR-015", "When an absence breakdown is present and its sum differs from planned minus actual hours by more than 0.5 hours, the report MUST be accepted and flagged \"inconsistent hour totals\". This check MUST NOT be applied when FR-015a applies. [SR-FR-017]", Skole),
        new("FR-015a", "When actual hours exceed planned hours by any amount, the report MUST be accepted and flagged \"attendance above plan\". [SR-FR-017a, DEC-50]", Skole),
        new("FR-015c", "When a report's end date is after its submission date, the report MUST be accepted and flagged \"submitted before period end\". The flag belongs to the version, so a correction sent after the period has ended replaces it without the flag. [SR-FR-017c, DEC-65]", Skole),
        new("FR-016", "A report without an absence breakdown MUST be accepted; it MUST be flagged \"missing absence breakdown\" when planned hours exceed actual hours, and not flagged when they are equal. [SR-FR-019, DEC-28]", Skole),
    ];

    [Fact]
    public void T12_T13_T18_T19_the_decision_table_keeps_precedence_excludes_nothing_silently_and_invents_no_outcome()
    {
        var t = TestDesignAdvisor.Decisions(SkoleRules);

        t.Conditions.Should().HaveCount(3, "FR-016 states its outcome first and its condition after — it is not a When-rule and is left out rather than misread");
        t.Precedence.Should().ContainSingle().Which.Should().Be("FR-015a går foran FR-015 (FR-015 sier det selv).");
        t.Rows.Single(r => r.Kind == "Relevant — forrang").Outcome.Should().Be("accepted «attendance above plan»");
        t.Rows.Where(r => r.Kind == "Må vurderes").Should().OnlyContain(r => r.Outcome == null, "a combination the source does not decide gets no outcome");
        t.Rows.Where(r => r.Kind == "Relevant").Select(r => r.Outcome).Should().BeEquivalentTo(["accepted «inconsistent hour totals»", "accepted «attendance above plan»", "accepted «submitted before period end»"]);
    }

    [Fact]
    public void T18_present_and_without_the_same_thing_is_a_contradictory_combination()
    {
        var t = TestDesignAdvisor.Decisions([SkoleRules[0], new ProjectRequirementOption("FR-016x", "When a report is without an absence breakdown, the report MUST be accepted and flagged \"missing absence breakdown\".", Skole)]);
        t.Rows.Should().Contain(r => r.Kind == "Motstridende" && r.Outcome == null);
    }

    [Fact]
    public void T13_a_rejection_is_never_turned_into_acceptance_by_a_flag()
    {
        var t = TestDesignAdvisor.Decisions([SkoleRules[1], new ProjectRequirementOption("FR-020", "When an hour value is greater than 24 times the number of days in its date range, the report MUST be rejected with a machine-readable reason.", Skole)]);
        var pair = t.Rows.Single(r => r.Conditions.Count == 2);
        pair.Outcome.Should().BeNull();
        pair.Basis.Should().Contain("avvisning skal ikke bli akseptert");
    }

    [Fact]
    public void T14_a_sequence_is_proposed_only_from_states_the_source_names()
    {
        var c = Case("a held report still unresolved after a final lookup at lock", "the period locks",
            "it is rejected with reason \"unknown child\", counted as rejected, and the identifier and content are deleted", "SkoleService");
        TestDesignAdvisor.StatesNamed(TestDesignAdvisor.FactsOf(c).All).Should().Equal("held", "rejected", "deleted");
        TestDesignAdvisor.StateSequences(c).Should().ContainSingle().Which.Title.Should().Contain("held → rejected → deleted");
        TestDesignAdvisor.StateSequences(Case(Get("Frontend/US1-1"))).Should().BeEmpty();
    }

    [Fact]
    public void T15_T16_T17_repetition_correction_and_order_are_kept_apart()
    {
        var twice = TestDesignAdvisor.Repetitions(Case(Get("SkoleService/US1-9"))).ToList();
        twice.Should().ContainSingle(p => p.Dimension == "Idempotens").Which.Basis.Should().Contain("lagret tilstand").And.Contain("skiller ikke identitet fra ankomstrekkefølge");
        var correction = TestDesignAdvisor.Repetitions(Case(Get("SkoleService/US5-1"))).ToList();
        correction.Should().Contain(p => p.Dimension == "Rekkefølge");
        correction.Should().NotContain(p => p.Dimension == "Idempotens", "a correction is not a duplicate");
        var receiver = Case("a valid skolenærvær message", "the intake processes it", "SkoleService receives one request containing the message id, send time and message-chain information.", "MeldingsutvekslerReceiver");
        TestDesignAdvisor.Repetitions(receiver).Should().BeEmpty("no repetition is stated, so none is proposed");
    }

    [Fact]
    public void T20_a_proposal_whose_title_already_exists_under_the_scenario_is_not_proposed_again()
    {
        var c = Case(Get("SkoleService/US1-4"), title: "US1-4");
        var first = ScenarioPlanning.Variants(c, []);
        var existing = Case(Get("SkoleService/US1-4"), title: first[0].Title);
        ScenarioPlanning.Variants(c, [existing]).Should().NotContain(v => v.Title == first[0].Title).And.HaveCount(first.Count - 1);
        first.Select(v => v.Key).Should().OnlyHaveUniqueItems();
    }

    // ── Security (21–26) ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void T21_T22_T23_T26_access_variants_name_only_the_stated_permission_and_keep_the_negative_assertions()
    {
        var c = Case(Get("SkoleService/US2-2"));
        var v = TestDesignAdvisor.SecurityVariants(c).ToList();
        v.Should().Contain(p => p.Dimension == "Tilgang (negativ)" && p.Title.Contains("Person:SeGradertBarn") && p.Expected == null);
        v.Should().Contain(p => p.Dimension == "Konfidensialitet" && p.Basis.Contains("Fravær i én liste er ikke nok"));
        var (rows, negatives) = TestDesignAdvisor.SecurityMatrix(TestDesignAdvisor.FactsOf(c).All);
        rows.Should().Contain(r => r.Contains("Kode 6") && r.Contains("oppslag") && r.Contains("uten Person:SeGradertBarn"));
        rows.Should().HaveCountLessThanOrEqualTo(TestDesignAdvisor.MaxMatrixRows);
        negatives.Should().Contain(n => n.Contains("does not appear and is not counted"));
        rows.Should().NotContain(r => r.Contains("Person:Admin"), "no permission is invented");
    }

    [Fact]
    public void T24_T25_classification_change_and_Person_unavailable_appear_only_when_stated()
    {
        TestDesignAdvisor.SecurityMatrix("Given Person is unavailable and the child is Kode 7, when a caseworker lists children, the child is not shown.").Rows
            .Should().Contain(r => r.StartsWith("Kode 7")).And.Contain(r => r.StartsWith("gradering ukjent"));
        TestDesignAdvisor.SecurityMatrix("When the classification changed to Kode 6, the child is not counted.").Rows.Should().Contain(r => r.StartsWith("gradering endret"));
        TestDesignAdvisor.SecurityMatrix(Get("Frontend/US1-1").Then).Rows.Should().BeEmpty("no access rule is stated");
    }

    // ── E2E (27–31) ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void T27_to_T31_checkpoints_are_source_sentences_each_with_a_visible_gap_and_no_invented_route()
    {
        var c = Case(Get("SkoleAdapter/US1-1"));
        var e = TestDesignAdvisor.EndToEnd(TestDesignAdvisor.FactsOf(c))!;
        var source = $"{Get("SkoleAdapter/US1-1").Given} {Get("SkoleAdapter/US1-1").When} {Get("SkoleAdapter/US1-1").Then}";
        e.Checkpoints.Should().NotBeEmpty().And.OnlyContain(cp => source.Contains(cp.TrimEnd('.'), StringComparison.Ordinal) || cp.Contains("SkoleService"));
        e.Gaps.Should().HaveCount(e.Checkpoints.Count, "no checkpoint has a confirmed way to observe it yet");
        string.Join(" ", e.Checkpoints.Concat(e.Gaps)).Should().NotContainAny("http", "/api", "SELECT ", "queue ");
        TestDesignAdvisor.EndToEnd(TestDesignAdvisor.FactsOf(Case(Get("Frontend/US1-1")))).Should().BeNull("one service only");
    }

    [Fact]
    public void T27_an_identifier_is_not_a_service()
    {
        TestDesignAdvisor.ServicesNamed("Given `AntallTimerFaktiskNaervaer` is greater than `AntallTimerEtterPlanen`, the BirkID is stored by SkoleService.")
            .Should().Equal("SkoleService");
    }

    // ── Readiness (41–48) ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void T41_T42_required_data_without_a_linked_asset_and_unknown_access_are_never_satisfied()
    {
        var r = TestDesignAdvisor.Readiness(Case(Get("SkoleService/US1-1"), review: TestCaseReviewState.Accepted));
        r.Checks.Single(x => x.Name == "Testdata").Satisfied.Should().Be(Tri.Unknown);
        r.Checks.Single(x => x.Name == "Tilgang og miljø").Satisfied.Should().Be(Tri.Unknown);
        r.Level.Should().NotBe(ReadinessLevel.ReadyToRun);
        TestDesignAdvisor.TestData(Case(Get("SkoleService/US1-1"))).Should().OnlyContain(d => d.Availability == DataAvailability.Unknown,
            "the specification describing data does not make it available");
    }

    [Fact]
    public void T41_a_linked_asset_that_no_longer_exists_blocks_and_an_unapproved_one_must_be_prepared()
    {
        TestDesignAdvisor.Readiness(Case(Get("SkoleService/US1-1"), data: [new TestCaseDataLink { AssetId = "a", Alias = "x", Resolved = false }])).Level.Should().Be(ReadinessLevel.Blocked);
        TestDesignAdvisor.TestData(Case(Get("SkoleService/US1-1"), data: [new TestCaseDataLink { AssetId = "a", Alias = "x", Approval = "Draft" }]))
            .Should().OnlyContain(d => d.Availability == DataAvailability.MustBeCreated);
    }

    [Fact]
    public void T45_an_unresolved_source_removal_blocks()
    {
        TestDesignAdvisor.Readiness(Case(Get("SkoleService/US1-1"), source: CaseSourceState.Removed)).Level.Should().Be(ReadinessLevel.Blocked);
    }

    [Fact]
    public void T46_an_approved_case_with_everything_confirmed_is_ready_to_run_which_is_not_a_result()
    {
        var c = Case(Get("Frontend/US1-1"), review: TestCaseReviewState.Accepted,
            edits: new TestCaseEdits { ObservationMethod = "Read the result list on screen", EnvironmentPrerequisites = "Test user with access to the child search" },
            data: [new TestCaseDataLink { AssetId = "a", Alias = "barn-med-navn", Approval = "Approved" }]);
        var r = TestDesignAdvisor.Readiness(c);
        r.Missing.Should().BeEmpty();
        r.Level.Should().Be(ReadinessLevel.ReadyToRun);
        TestDesignAdvisor.ReadinessLabel(r.Level).Should().Be("Klar til kjøring");
    }

    [Fact]
    public void T47_T48_a_draft_is_ready_to_prepare_and_readiness_maps_to_the_existing_review_states_only()
    {
        TestDesignAdvisor.Readiness(Case(Get("SkoleService/US1-1"))).Level.Should().Be(ReadinessLevel.ReadyToPrepare);
        TestDesignAdvisor.Readiness(Case(Get("SkoleService/US1-1"), review: TestCaseReviewState.UnderReview)).Level.Should().Be(ReadinessLevel.ReadyForReview);
        TestDesignAdvisor.Readiness(Case(Get("SkoleService/US1-1"), review: TestCaseReviewState.NeedsClarification)).Level.Should().Be(ReadinessLevel.NeedsPreparation);
        Enum.GetValues<ReadinessLevel>().Select(TestDesignAdvisor.ReadinessLabel).Should().NotContain(l => l.Contains("bestått") || l.Contains("kjørt"));
    }

    [Fact]
    public void T34_a_scenario_without_an_expected_result_needs_assessment()
    {
        var c = Case("a report", "it arrives", "", "SkoleService");
        Suit(c).Approaches.Should().ContainSingle().Which.Approach.Should().Be(A.NeedsAssessment);
    }
}
