using BirkNext.Api.Services.TestPlanning;
using BirkNext.TestPlanning;
using FluentAssertions;

namespace BirkNext.Api.Tests.Services.TestPlanning;

/// <summary>
/// Saved project test cases as test-design evidence (<see cref="PreparedCaseTraceability"/>): counted only from the case's own specification
/// evidence in the reviewed specification, one identity per saved case, a requirement counted once, never executed, never another project's.
/// </summary>
public sealed class PreparedCaseTraceabilityTests
{
    private const string Spec = "specs/001-library/spec.md";
    private const string Project = "library";

    private static TestCaseDraft SpecCase(string id, string title, string requirement, TestCaseReviewState review = TestCaseReviewState.Generated, string file = Spec,
        bool fromScenario = false) => new()
    {
        Source = new ScenarioReference { Kind = ScenarioSourceKind.ProjectDiscovery, Owner = Project, ScenarioId = id },
        Review = review,
        Generated = new TestCaseContent
        {
            Title = title, Capability = TestScenarioDiscoveryEngine.Specification,
            Requirements = [new TestCaseRequirement(requirement, requirement, null)],
            // As discovery writes it: a scenario names "{file}#{scenario}" and a bare requirement id; a requirement candidate names "{file}#{id}".
            Evidence = fromScenario ? [new("specification", $"{file}#US1-1"), new("requirement", requirement)] : [new("requirement", $"{file}#{requirement}")],
        },
    };

    private static TestCaseDraft Manual(string id, string title) => new()
    {
        Source = new ScenarioReference { Kind = ScenarioSourceKind.Manual, Owner = Project, ScenarioId = id },
        Generated = new TestCaseContent { Title = title },
    };

    private static TestPlanDraft Plan(string project, params TestCaseDraft[] cases) => new()
    {
        Id = Guid.NewGuid(), GuideKey = TestPlanSuggestionService.ProjectOwner(project), ProjectKey = project, PlanName = $"{project} plan", Revision = 3, Cases = [.. cases],
    };

    private static PreparedCaseTraceView Build(IEnumerable<TestPlanDraft> drafts, string[]? paths = null, string[]? ids = null) =>
        PreparedCaseTraceability.Build(Project, drafts, paths ?? [Spec], ids);

    [Fact]
    public void A_case_prepared_from_the_reviewed_specification_counts_for_its_requirement_and_links_back_to_the_case()
    {
        var plan = Plan(Project, SpecCase("tsc-1", "Reserve a book", "FR-001", TestCaseReviewState.Accepted, fromScenario: true));
        var view = Build([plan], ids: ["FR-001", "FR-002"]);

        var row = view.Requirements.Should().ContainSingle().Subject;
        row.RequirementId.Should().Be("FR-001");
        row.State.Should().Be("Approved test case");
        row.ApprovedCases.Should().Be(1);
        var c = view.CaseList.Single();
        c.CaseKey.Should().Be($"{plan.Id:N}/tsc-1", "one identity per saved case: draft + case");
        c.Href.Should().Be($"test-plan-suggestions?draft={plan.Id}&area=prepare&case=tsc-1");
        c.SpecificationScenarioId.Should().Be("US1-1", "the same acceptance scenario Test Coverage lists as a QA test");
        c.Requirements.Single().Should().Match<PreparedCaseRequirementLink>(l => l.Counted && l.SpecificationPath == Spec && l.Basis == PreparedCaseLinkBasis.SpecificationEvidence);
        view.RequirementsWithApprovedCase.Should().Be(1);
        view.LinkedCases.Should().Be(1);
    }

    [Fact]
    public void A_prepared_or_approved_case_is_never_executed()
    {
        var view = Build([Plan(Project, SpecCase("a", "Approved", "FR-001", TestCaseReviewState.Accepted), SpecCase("b", "Prepared", "FR-002"), Manual("m", "Manual"))]);

        view.CaseList.Should().OnlyContain(c => c.Execution == PreparedCaseTraceView.NotExecuted);
        view.CaseList.Select(c => c.Stage).Should().Equal(PreparedCaseStage.Approved, PreparedCaseStage.Prepared, PreparedCaseStage.Prepared);
        view.Boundary.Should().Contain("not test execution").And.Contain("none of them ran or passed");
        view.Requirements.Single(r => r.RequirementId == "FR-002").State.Should().Be("Prepared, not approved yet");
    }

    [Fact]
    public void An_unlinked_case_is_visible_but_never_counted_as_requirement_coverage()
    {
        var view = Build([Plan(Project, Manual("m-1", "Log in as a librarian"))]);

        view.Cases.Should().Be(1);
        view.UnlinkedCases.Should().Be(1);
        view.LinkedCases.Should().Be(0);
        view.Requirements.Should().BeEmpty();
        view.CaseList.Single().Should().Match<PreparedCaseTrace>(c => c.Title == "Log in as a librarian" && c.Capability == null && !c.Counted);
    }

    [Fact]
    public void A_requirement_is_counted_once_however_many_cases_and_plans_cover_it()
    {
        var first = Plan(Project, SpecCase("tsc-s", "Scenario", "FR-001", TestCaseReviewState.Accepted, fromScenario: true), SpecCase("tsc-r", "Requirement", "FR-001"));
        var second = Plan(Project, SpecCase("tsc-s", "Scenario again", "FR-001"));
        var view = Build([first, second]);

        var row = view.Requirements.Should().ContainSingle("FR-001 is one requirement").Subject;
        row.CaseKeys.Should().HaveCount(3, "three saved cases, each with its own identity");
        (row.ApprovedCases, row.PreparedCases).Should().Be((1, 2));
        view.RequirementsWithApprovedCase.Should().Be(1);
        view.RequirementsWithPreparedCaseOnly.Should().Be(0);
        view.CaseList.Select(c => c.CaseKey).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Only_this_projects_own_plans_are_read()
    {
        var mine = Plan(Project, SpecCase("a", "Mine", "FR-001"));
        var other = Plan("other", SpecCase("b", "Other project", "FR-001"));
        var guide = mine with { Id = Guid.NewGuid(), GuideKey = "skolenaervaer", ProjectKey = null };
        var spoofed = mine with { Id = Guid.NewGuid(), ProjectKey = "other" };
        var view = Build([mine, other, guide, spoofed]);

        view.Plans.Should().Be(1);
        view.CaseList.Should().ContainSingle(c => c.Title == "Mine");
    }

    [Fact]
    public void Links_outside_the_reviewed_specification_guide_rules_journey_ids_and_excluded_cases_are_shown_but_not_counted()
    {
        var guideRule = SpecCase("g", "Guide", "BR-7") with { Source = new ScenarioReference { Kind = ScenarioSourceKind.ProjectDiscovery, Owner = Project, ScenarioId = "g" } };
        guideRule = guideRule with { Generated = guideRule.Generated with { Capability = TestScenarioDiscoveryEngine.SystemGuide, Evidence = [] } };
        var journey = SpecCase("j", "Journey", "FR-003");
        journey = journey with { Generated = journey.Generated with { Capability = TestScenarioDiscoveryEngine.IntegrationJourneys, Evidence = [] } };
        var view = Build([Plan(Project,
            SpecCase("o", "Other spec", "FR-001", file: "specs/002-other/spec.md"),
            SpecCase("u", "Unknown id", "FR-099"),
            SpecCase("x", "Excluded", "FR-002", TestCaseReviewState.Excluded),
            guideRule, journey)], ids: ["FR-001", "FR-002", "FR-003"]);

        view.Requirements.Should().BeEmpty("nothing here is evidence for a requirement of the reviewed specification");
        view.UnlinkedCases.Should().Be(5);
        var links = view.CaseList.ToDictionary(c => c.CaseId, c => c.Requirements.Single());
        links["o"].Explanation.Should().Contain("not from the reviewed specification");
        links["u"].Explanation.Should().Contain("is not a requirement of the reviewed specification");
        links["x"].Explanation.Should().Contain("excluded");
        links["g"].Basis.Should().Be(PreparedCaseLinkBasis.GuideBusinessRule);
        links["j"].Basis.Should().Be(PreparedCaseLinkBasis.JourneyReference);
        view.Limitations.Should().HaveCount(2);
    }

    [Fact]
    public void Without_a_specification_no_link_is_counted_and_the_view_says_why()
    {
        var view = Build([Plan(Project, SpecCase("a", "Case", "FR-001", TestCaseReviewState.Accepted))], paths: []);

        view.Requirements.Should().BeEmpty();
        view.CaseList.Single().Requirements.Single().Explanation.Should().Be("No specification is being reviewed.");
        view.Limitations.Should().Contain(l => l.StartsWith("No specification was given"));
    }

    [Fact]
    public void A_case_prepared_from_a_scenario_counts_for_the_requirement_the_scenario_names_in_the_same_file()
    {
        // Path separators and a leading slash differ between the explorer and the stored evidence; the file is the same.
        var view = Build([Plan(Project, SpecCase("s", "Scenario", "FR-001", fromScenario: true, file: "specs\\001-library\\spec.md"))], paths: ["/specs/001-library/spec.md"]);

        view.Requirements.Single().RequirementId.Should().Be("FR-001");
    }
}
