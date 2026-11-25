using BirkNext.TestCoverage;
using BirkNext.TestPlanning;
using BirkNext.Web.Services;
using FluentAssertions;
using S = BirkNext.Web.Services.ScenarioBrowserPresentation;
using W = BirkNext.Web.Services.ScenarioWorkspacePresentation;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// The User Story–aware scenario workspace (2026-11-24): stories group and never gate, requirement relations are named for what they are
/// (named / confirmed / suggested), saved cases are found by the scenario's own specification id, and readiness is six evidence-based questions
/// where unknown is never done.
/// </summary>
public sealed class ScenarioWorkspacePresentationTests
{
    private static CoverageUserStory Story(string id, int ordinal, string title, string path = "SkoleService/specs/001-skoleservice/spec.md", int scenarios = 0) => new()
    {
        Key = CoverageUserStory.KeyOf(path, $"us{ordinal}"), StoryId = id, Ordinal = ordinal, Title = title, Priority = "P1", SpecificationPath = path, Scenarios = scenarios,
        IndependentTest = "Submit a report with valid hours. Verify the report is stored with BirkID and the national ID is not stored.",
    };

    private static readonly CoverageUserStory Us1 = Story("US1", 1, "Accept a valid report without keeping the child's national ID", scenarios: 11);
    private static readonly CoverageUserStory Us2 = Story("US2", 2, "Protect graded children");

    private static DiscoveredScenario C(string id, CoverageUserStory? story, int? number, string title, string[]? requirements = null, string? then = "it is accepted") => new()
    {
        CandidateId = id, ProjectKey = "m2lb", CapabilityId = ScenarioOwnership.Specification, SourceCategory = "specification", Area = "Rapporter", Title = title,
        Purpose = title, Type = ScenarioCandidateType.BusinessFunctional, Confidence = CandidateConfidence.ExplicitSpecification, ExpectedBehaviour = then,
        Evidence = [new("specification", $"{story?.SpecificationPath ?? "contracts/api.yaml"}#{id}", "Akseptansescenario")], Story = story, NumberInStory = number,
        RequirementIds = [.. requirements ?? []], SourceIdentity = "tss-" + id,
        Template = [new("given", "Gitt", "a report for a child Person knows"), new("when", "Når", "it arrives"), new("then", "Så", then)],
    };

    private static readonly DiscoveredScenario Valid = C("US1-AS1", Us1, 1, "Valid report accepted", ["FR-001", "FR-002"], "it is accepted and stored with BirkID");
    private static readonly DiscoveredScenario Above = C("US1-AS2", Us1, 2, "Attendance above plan", ["FR-015a"], "it is flagged");
    private static readonly DiscoveredScenario Graded = C("US2-AS1", Us2, 1, "Graded child hidden", ["FR-001"]);
    private static readonly DiscoveredScenario Contract = C("post-reports", null, null, "POST /reports") with { CapabilityId = ScenarioOwnership.ApiContract, Type = ScenarioCandidateType.ApiContract };

    private static List<S.Item> Items(params DiscoveredScenario[] c) => S.Items(c, new Dictionary<string, S.Prepared> { ["US1-AS1"] = new(2, 1, false, false, false) });

    [Fact]
    public void Stories_are_listed_in_specification_order_with_declared_and_found_counts_and_scenarios_without_a_story_get_their_own_group()
    {
        var stories = W.Stories(Items(Graded, Valid, Above, Contract), new S.Filter(All: true));

        stories.Select(s => s.Label).Should().Equal("US1", "US2", "Uten brukerhistorie");
        var us1 = stories[0];
        (us1.Declared, us1.Discovered, us1.WithCases, us1.Priority).Should().Be((11, 2, 1, "P1"));
        W.ScenarioCountText(us1).Should().Be("11 akseptansescenarioer i spesifikasjonen · 2 funnet ved oppdagelsen");
        stories[1].Declared.Should().BeNull("a story without a declared count is not given one");
        W.ScenarioCountText(stories[1]).Should().Be("1 scenario");
        stories[2].Key.Should().Be(W.NoStory);
        stories.Should().OnlyContain(s => s.Feature == null, "the feature is named only when scenarios come from more than one specification");
    }

    [Fact]
    public void Manual_testing_is_the_focus_technical_stories_wait_behind_the_technical_toggle()
    {
        // M2LB has specs about the code base itself (e.g. AutorisasjonService 001-translate-source-english): stories, but not manual-testing work.
        var tech = Story("US1", 1, "Developers read the code without Norwegian knowledge", path: "AutorisasjonService/specs/001-translate-source-english/spec.md");
        var techScenario = C("T-US1-AS1", tech, 1, "Identifiers are English");
        var stories = W.Stories(Items(Valid, Above, techScenario, Contract), new S.Filter());

        stories.Single(s => s.Story == tech).Technical.Should().BeTrue();
        stories.Single(s => s.Story == Us1).Technical.Should().BeFalse();

        var (shown, hidden) = W.RailStories(stories, showTechnical: false, current: null);
        shown.Should().NotContain(s => s.Technical);
        shown.Should().NotContain(s => s.Key == W.NoStory, "the API contract scenario is technical, so the no-story group is empty for manual testing");
        hidden.Should().Be(1);
        W.RailStories(stories, showTechnical: true, current: null).Shown.Should().HaveCount(stories.Count);
        W.RailStories(stories, showTechnical: false, current: tech.Key).Shown.Should().Contain(s => s.Story == tech, "an open story never disappears from under the tester");
    }

    [Fact]
    public void The_no_story_group_filters_scenarios_without_a_story_and_a_story_never_hides_others_from_All()
    {
        var items = Items(Valid, Above, Contract);
        S.Apply(items, new S.Filter(All: true, Story: W.NoStory)).Shown.Select(i => i.Id).Should().Equal("post-reports");
        S.Apply(items, new S.Filter(All: true, Story: Us1.Key)).Shown.Should().HaveCount(2);
        S.Apply(items, new S.Filter(All: true)).Shown.Should().HaveCount(3);
    }

    [Fact]
    public void A_scenario_is_numbered_as_the_specification_numbers_it_and_other_scenarios_keep_their_type()
    {
        var items = Items(Above, Contract);
        W.ScenarioLabel(items.Single(i => i.Id == "US1-AS2")).Should().Be("AC 2");
        W.ScenarioLabel(items.Single(i => i.Id == "post-reports")).Should().Be(ScenarioCandidateTypes.Label(ScenarioCandidateType.ApiContract));
    }

    private static PreparedCaseTrace Case(string scenarioId, string title, PreparedCaseStage stage = PreparedCaseStage.Prepared, params PreparedCaseRequirementLink[] links) => new()
    {
        CaseKey = Guid.Empty.ToString("N") + "/" + title, DraftId = Guid.Parse("22222222-2222-2222-2222-222222222222"), CaseId = "case-" + title, Title = title, PlanName = "Testplan",
        Stage = stage, StageLabel = stage.ToString(), SpecificationScenarioId = scenarioId, Requirements = [.. links],
    };

    [Fact]
    public void Saved_cases_are_found_by_the_scenarios_own_specification_id_and_history_is_left_out()
    {
        var trace = new PreparedCaseTraceView { CaseList = [Case("US1-AS1", "Gyldig rapport"), Case("US1-AS1", "Gammel", PreparedCaseStage.Superseded), Case("US1-AS2", "Over plan")] };

        W.SpecificationScenarioId(Valid).Should().Be("US1-AS1");
        W.CasesOf(Valid, trace).Select(c => c.Title).Should().Equal("Gyldig rapport");
        W.CasesOf(Valid, null).Should().BeEmpty("no trace, no list — never a guess");
        W.CasesOf(Contract with { Evidence = [] }, trace).Should().BeEmpty();
        W.CaseHref(trace.CaseList[0]).Should().Be("manual-testing/plan?draft=22222222-2222-2222-2222-222222222222&case=case-Gyldig%20rapport");
    }

    private static readonly List<ProjectRequirementOption> Catalog =
    [
        new("FR-001", "The system MUST accept valid reports.", Us1.SpecificationPath!),
        new("FR-002", "The system MUST NOT store the national ID.", Us1.SpecificationPath!),
        new("FR-015", "Reports with hours that do not add up MUST be rejected.", Us1.SpecificationPath!),
        new("FR-015a", "Attendance above the planned hours MUST be flagged, not rejected.", Us1.SpecificationPath!),
        new("FR-014", "Duplicate absence codes MUST be rejected.", Us1.SpecificationPath!),
        new("FR-001", "Another service's FR-001.", "Person/specs/001-person/spec.md"),
    ];

    [Fact]
    public void Requirements_are_named_explicit_then_tester_confirmed_then_suggested_each_with_its_reason()
    {
        var cases = new List<PreparedCaseTrace>
        {
            Case("US1-AS2", "Over plan", PreparedCaseStage.Approved,
                new PreparedCaseRequirementLink("FR-014", Us1.SpecificationPath, PreparedCaseLinkBasis.ManualConfirmation, true, "Bekreftet"),
                new PreparedCaseRequirementLink("FR-015a", Us1.SpecificationPath, PreparedCaseLinkBasis.ManualConfirmation, true, "Bekreftet", null)),
        };

        var links = W.Requirements(Above, Catalog, cases);

        links[0].Should().Match<W.RequirementLink>(l => l.Id == "FR-015a" && l.Relation == W.Relation.Explicit && l.ConfirmedOn.Count == 1);
        links[0].Title.Should().Be("Attendance above the planned hours MUST be flagged, not rejected.", "the requirement text of the scenario's own specification");
        W.RelationLabel(links[0]).Should().Be("Nevnt i kilden · bekreftet");
        links.Single(l => l.Id == "FR-014").Relation.Should().Be(W.Relation.Confirmed);
        W.RelationLabel(links.Single(l => l.Id == "FR-014")).Should().Be("Bekreftet av tester");
        links.Where(l => l.Relation == W.Relation.Suggested).Should().OnlyContain(l => l.Explanation.Length > 0 && l.Level != RequirementSuggestionLevel.Explicit);
        links.Should().NotContain(l => l.Relation == W.Relation.Suggested && (l.Id == "FR-015a" || l.Id == "FR-014"), "a linked requirement is never suggested again");
        links.Where(l => l.Relation == W.Relation.Suggested).Should().OnlyContain(l => l.SpecificationPath == Us1.SpecificationPath, "suggestions stay in the scenario's own specification");
        W.Requirements(Above, null, []).Should().ContainSingle("without a catalog only what the source names is shown");
    }

    [Fact]
    public void A_rule_match_is_called_the_same_rule_and_other_suggestions_keep_their_level()
    {
        W.SuggestionLabel(RequirementSuggestionLevel.Semantic, "Same rule: flagged above plan").Should().Be("Foreslått · C · Samme regel (samme utfall)");
        W.SuggestionLabel(RequirementSuggestionLevel.Semantic, "Shared wording").Should().Be("Foreslått · C · Samme atferd beskrevet");
        W.SuggestionLabel(RequirementSuggestionLevel.Weak, "x").Should().StartWith("Foreslått · D");
    }

    [Fact]
    public void Story_coverage_lists_every_requirement_the_story_its_tasks_or_its_scenarios_name_with_honest_case_counts()
    {
        var story = Us1 with { NamedRequirements = ["FR-004"], TaskRequirements = [new("FR-016", "T010", "Validate breakdown", "tasks.md", 10)] };
        var items = Items(C("US1-AS1", story, 1, "Valid", ["FR-001", "FR-002"]), C("US1-AS2", story, 2, "Above", ["FR-015a", "FR-001"]));
        var trace = new PreparedCaseTraceView { Requirements = [new PreparedRequirementTrace { RequirementId = "FR-001", PreparedCases = 2, ApprovedCases = 1, Execution = "1 kjørt" }] };

        var rows = W.Coverage(story, items, Catalog, trace);

        rows.Select(r => r.Id).Should().Equal("FR-001", "FR-002", "FR-004", "FR-015a", "FR-016");
        rows[0].Should().Match<W.RequirementCoverageRow>(r => r.Scenarios == 2 && r.PreparedCases == 2 && r.ApprovedCases == 1 && r.Basis == "scenario 1, 2" && r.Known);
        rows.Single(r => r.Id == "FR-004").Basis.Should().Be("nevnt i brukerhistorien");
        rows.Single(r => r.Id == "FR-016").Basis.Should().Be("fra oppgavene");
        W.Coverage(story, items, Catalog, null).Should().OnlyContain(r => !r.Known, "without the trace the case counts are unknown, never zero");
    }

    [Fact]
    public void Coverage_counts_requirements_a_tester_confirmed_and_lists_suggestions_apart_never_as_coverage()
    {
        // Real SkoleService: the scenarios name no FR themselves; a tester confirmed FR-014 on scenario 2's case.
        var story = Us1 with { NamedRequirements = [] };
        var above = C("US1-AS2", story, 2, "A report where AntallTimerFaktiskNaervaer is greater than AntallTimerEtterPlanen", then: "it is accepted and flagged \"attendance above plan\"");
        var items = Items(above);
        var trace = new PreparedCaseTraceView
        {
            CaseList = [Case("US1-AS2", "Over plan", PreparedCaseStage.Prepared,
                new PreparedCaseRequirementLink("FR-014", Us1.SpecificationPath, PreparedCaseLinkBasis.ManualConfirmation, true, "Bekreftet"))],
        };
        var catalog = new List<ProjectRequirementOption>(Catalog) { new("FR-015a", "When actual hours exceed planned hours, the report MUST be accepted and flagged \"attendance above plan\".", Us1.SpecificationPath!) };

        var rows = W.Coverage(story, items, catalog, trace);
        rows.Select(r => (r.Id, r.Basis)).Should().Equal([("FR-014", "bekreftet av tester")]);

        var suggested = W.Suggested(items, catalog, trace, [.. rows.Select(r => r.Id)]);
        suggested.Select(s => s.Id).Should().Contain("FR-015a").And.NotContain("FR-014", "a confirmed link is not a suggestion");
        suggested.Single(s => s.Id == "FR-015a").Scenarios.Should().Equal("scenario 2");
        suggested.Single(s => s.Id == "FR-015a").Weak.Should().BeFalse("a same-rule suggestion is shown by default; only D-level ones wait behind a toggle");
        W.Suggested(items, null, trace, []).Should().BeEmpty();
    }

    [Fact]
    public void Rows_drop_the_story_prefix_the_label_already_shows_and_case_states_are_in_norwegian()
    {
        var item = Items(Valid).Single();
        item.Title.Should().StartWith("US1 — ");
        W.RowTitle(item).Should().Be(item.Title["US1 — ".Length..]);
        W.RowTitle(Items(Contract).Single()).Should().Be("POST /reports");
        W.DetailTitle(item).Should().Be("AC 1 — " + item.Title["US1 — ".Length..], "the detail pane names the acceptance criterion, never «US1 — »");
        W.DetailTitle(Items(Contract).Single()).Should().Be(Items(Contract).Single().Title);
        W.OtherScenarioLabel(Items(Graded).Single()).Should().Be("AC 1 (US2)");
        W.OtherScenarioLabel(Items(Contract).Single()).Should().Be("POST /reports", "an unnumbered scenario is named by its title, never its type");
        W.StageText(PreparedCaseStage.Prepared).Should().Be("Forberedt");
        W.StageText(PreparedCaseStage.Approved).Should().Be("Godkjent");
        W.ExecutionText(PreparedCaseTraceView.NotExecuted).Should().Be("Ikke kjørt");
        W.ExecutionText("1 av 2 kjørt").Should().Be("1 av 2 kjørt");
    }

    [Fact]
    public void Requirement_ids_sort_naturally_and_a_suffix_follows_its_base()
    {
        new[] { "FR-015a", "FR-10", "FR-2", "FR-015", "SC-1" }.Order(W.RequirementIdComparer.Instance).Should().Equal("FR-2", "FR-10", "FR-015", "FR-015a", "SC-1");
    }

    [Fact]
    public void Independent_test_objectives_show_which_scenarios_appear_to_cover_them_and_flag_possible_gaps()
    {
        var items = Items(Valid, Above);
        var objectives = W.Objectives(Us1, items);
        objectives.Should().NotBeEmpty();
        objectives.Should().Contain(o => !o.Covered, "an objective no scenario mentions is a possible gap");
    }

    [Fact]
    public void Readiness_is_six_questions_from_the_evidence_and_unknown_is_never_done()
    {
        var discovered = W.Checklist(TestDesignAdvisor.Readiness(TestDesignAdvisor.AsDraft(Valid)));
        discovered.Select(c => c.Key).Should().Equal("source", "data", "trigger", "verify", "env", "review");
        discovered[0].State.Should().Be(Tri.Unknown, "an expected result that only the source states is not yet confirmed");
        discovered[0].Detail.Should().Contain("bekreftes når testtilfellet godkjennes");
        discovered.Single(c => c.Key == "env").State.Should().Be(Tri.Unknown, "nobody recorded the environment");
        discovered.Single(c => c.Key == "review").State.Should().Be(Tri.No);
        discovered.Should().OnlyContain(c => c.Owner.Length > 0);

        var approved = TestDesignAdvisor.AsDraft(Valid) with
        {
            Review = TestCaseReviewState.Accepted, Edits = new TestCaseEdits { ObservationMethod = "Les rapporten i databasen", EnvironmentPrerequisites = "Testmiljø med Person" },
        };
        var ready = W.Checklist(TestDesignAdvisor.Readiness(approved));
        ready.Single(c => c.Key == "source").State.Should().Be(Tri.Yes, "approved by a person and stated in the source");
        ready.Single(c => c.Key == "review").State.Should().Be(Tri.Yes);
        ready.Single(c => c.Key == "env").State.Should().Be(Tri.Yes);

        var missing = W.Checklist(new ExecutionReadiness(ReadinessLevel.ReadyToPrepare, []));
        missing.Should().OnlyContain(c => c.State == Tri.Unknown, "a check that was not made is unknown, never satisfied");
    }

    [Theory]
    [InlineData("User Story 1 - Accept a valid report without keeping the child's national ID (Priority: P1)", "Accept a valid report without keeping the child's national ID")]
    [InlineData("User Story 8 – Follow-up lists for caseworkers (Priority: P3)", "Follow-up lists for caseworkers")]
    [InlineData("Brukerhistorie 2: Beskytt graderte barn (Prioritet: P1)", "Beskytt graderte barn")]
    [InlineData("Accept a valid report", "Accept a valid report")]
    [InlineData("User stories are reviewed (Priority: P2)", "User stories are reviewed")]
    public void A_story_title_drops_the_heading_number_and_priority_that_the_chips_already_show(string stored, string shown)
    {
        W.StoryTitle(Us1 with { Title = stored }).Should().Be(shown);
    }

    [Fact]
    public void Objectives_match_singular_and_plural_and_read_the_scenarios_own_given_when_then()
    {
        // SkoleService US1 (verbatim): the Independent Test and scenario 1's Given/When/Then.
        var story = Us1 with { IndependentTest = "Submit valid, invalid and inconsistent reports for children known to Person and verify the stored outcome, the rejection reasons, the flags, and that no child national identifier exists anywhere afterwards." };
        var scenario = C("US1-AS1", story, 1, "Valid report", then: "it is accepted, stored against the child's M2LB identity with BirkID and reporter, and the child's fødselsnummer/DUF-nummer is not stored, published or logged.")
            with { Template = [new("given", "Gitt", "a report for a child Person knows, with valid hours, dates within the open period and valid absence codes"), new("when", "Når", "it arrives"),
                new("then", "Så", "it is accepted, stored against the child's M2LB identity with BirkID and reporter, and the child's fødselsnummer/DUF-nummer is not stored, published or logged.")] };

        var objectives = W.Objectives(story, Items(scenario));

        objectives.Single(o => o.Objective.Text.Contains("valid reports", StringComparison.Ordinal) && !o.Objective.Text.Contains("invalid", StringComparison.Ordinal))
            .Covered.Should().BeTrue("\"children known to Person\" is the scenario's \"child Person knows\"");
        objectives.Single(o => o.Objective.Text.Contains("rejection", StringComparison.Ordinal)).Covered.Should().BeFalse("no scenario here rejects anything — a real gap stays a gap");
    }

    [Fact]
    public void The_feature_is_the_specification_folder()
    {
        W.FeatureOf("SkoleService/specs/001-skoleservice/spec.md").Should().Be("001-skoleservice");
        W.FeatureOf("spec.md").Should().BeNull();
        W.FeatureOf(null).Should().BeNull();
    }
}
