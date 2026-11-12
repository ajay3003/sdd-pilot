using BirkNext.TestPlanning;
using BirkNext.Web.Components;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using C = BirkNext.Web.Services.RequirementCoveragePresentation;
using P = BirkNext.Web.Services.TestPlanSuggestionsPresentation;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// MT-7 presentation (fixtures S, T and the UI side of A–C): Requirements Traceability, Test Plan Suggestions, Dashboard and Recommended Workflow show
/// the same coverage facts; unknown coverage adds no count; suggested associations are listed apart and never counted; reviewed coverage is never
/// worded as a test result.
/// </summary>
public sealed class ReviewedRequirementCoverageTests : BunitContext
{
    static RequirementScenarioAssociation Assoc(string scenario, string req, AssociationTrust trust, bool current = false, string rule = "CR-S-EXPLICIT") => new()
    {
        RelationKey = $"Scenario|system-understanding/skolenaervaer/{scenario}->Verifies->Requirement|skole/001-skoletjenesten/{req}", GuideKey = "skolenaervaer", ScenarioId = scenario,
        ScenarioTitle = scenario, RequirementKey = $"Requirement|skole/001-skoletjenesten/{req}", RequirementId = req, Trust = trust, RuleId = rule, Resolution = "Candidate",
        ReviewState = trust == AssociationTrust.ReviewedCanonicalAssociation ? "Confirmed" : "NotReviewed", Reviewer = trust == AssociationTrust.ReviewedCanonicalAssociation ? "reviewer@bufdir" : null,
        DecidedAt = new DateTimeOffset(2026, 11, 12, 10, 0, 0, TimeSpan.Zero), ScenarioReview = current ? "Accepted for preparation" : "Not reviewed", ScenarioReviewCurrent = current,
    };

    static RequirementCoverageView View()
    {
        var reviewed = Assoc("korrigert-rapport", "FR-021", AssociationTrust.ReviewedCanonicalAssociation, current: true);
        var explicitOnly = Assoc("duplikat-melding", "FR-017", AssociationTrust.ExplicitSourceReference);
        var suggested = Assoc("manglende-barn", "FR-006", AssociationTrust.SuggestedAssociation, rule: "CR-S-VIA-TEST");
        var rejected = Assoc("trekk", "FR-040", AssociationTrust.Rejected);
        RequirementCoverageRow Row(string id, RequirementCoverageState s, params RequirementScenarioAssociation[] a) =>
            new() { RequirementKey = $"Requirement|skole/001-skoletjenesten/{id}", RequirementId = id, Module = "Skole", Feature = "001-skoletjenesten", Title = id + ": text", State = s, Reason = "why", Associations = [.. a] };
        return new RequirementCoverageView
        {
            Scope = "dev", Known = true, InterpretedAt = DateTimeOffset.UtcNow, Requirements = 5, Reviewed = 1, VerifiedNotReviewed = 1, NeedsReview = 1, NoScenario = 2,
            Rows = [Row("FR-021", RequirementCoverageState.ReviewedScenarioCoverage, reviewed), Row("FR-017", RequirementCoverageState.AssociationVerifiedScenarioNotReviewed, explicitOnly),
                Row("FR-006", RequirementCoverageState.NeedsRelationshipReview, suggested), Row("FR-040", RequirementCoverageState.NoDocumentedScenarioCoverage, rejected),
                Row("FR-099", RequirementCoverageState.NoDocumentedScenarioCoverage)],
            Associations = [reviewed, explicitOnly, suggested, rejected], ScenariosWithoutRequirement = ["skolenaervaer/periodelaas"],
        };
    }

    void Register(RequirementCoverageView? view, string? profile = "dev")
    {
        var context = new Mock<IFrontendAnalysisContextFactory>();
        context.Setup(c => c.GetActiveContextAsync()).ReturnsAsync(new FrontendAnalysisContext
            { ActiveProfile = profile is null ? new FrontendAnalysisProfile() : new FrontendAnalysisProfile { Id = profile, Name = "Dev", EnvironmentType = FrontendEnvironmentType.Development } });
        Services.AddSingleton(context.Object);
        var api = new Mock<ITestPlanSuggestionsApiService>();
        api.Setup(a => a.RequirementCoverageAsync("dev", It.IsAny<CancellationToken>(), It.IsAny<bool>())).ReturnsAsync((view, (string?)null));
        Services.AddSingleton(api.Object);
    }

    [Fact]
    public void Traceability_panel_shows_the_four_states_with_their_denominator_and_never_calls_coverage_a_test_result()
    {
        Register(View());
        var cut = Render<ReviewedRequirementCoveragePanel>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=rrc-counts]"));
        cut.Find("[data-testid=rrc-count-reviewed]").TextContent.Should().Contain("1");
        cut.Find("[data-testid=rrc-count-needs]").TextContent.Should().Contain("1");
        cut.Markup.Should().Contain("Of 5 requirements");
        cut.Find("[data-testid=rrc-boundary]").TextContent.Should().Contain("not test execution");
        // Default list: requirements with an association; a rejected one is shown struck through, never counted.
        cut.FindAll("[data-testid=rrc-row]").Select(r => r.GetAttribute("data-state")).Should().BeEquivalentTo(["ReviewedScenarioCoverage", "AssociationVerifiedScenarioNotReviewed", "NeedsRelationshipReview", "NoDocumentedScenarioCoverage"]);
        cut.FindAll("[data-testid=rrc-decide]").Should().ContainSingle("only the suggested association offers a decision");
        cut.Markup.Should().NotContainAny("Passed", "Tested", "Executed");
        cut.Find("[data-testid=rrc-count-none]").Click();
        cut.FindAll("[data-testid=rrc-row]").Should().HaveCount(2);
    }

    [Fact]
    public void Unknown_coverage_or_no_project_shows_unknown_and_no_counts()
    {
        Register(new RequirementCoverageView { Scope = "dev", Known = false, UnknownReason = "No relationship interpretation exists for this project yet." });
        var cut = Render<ReviewedRequirementCoveragePanel>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=rrc-unknown]").TextContent.Should().Contain("No relationship interpretation"));
        cut.FindAll("[data-testid=rrc-counts]").Should().BeEmpty();
    }

    [Fact]
    public void Without_an_active_project_nothing_is_read_and_coverage_is_unknown()
    {
        Register(View(), profile: null);
        var cut = Render<ReviewedRequirementCoveragePanel>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=rrc-error]").TextContent.Should().Contain("select a project"));
    }

    [Fact]
    public void Scenario_associations_are_split_by_trust()
    {
        var (verified, suggested, rejected) = C.ForScenario(View(), "skolenaervaer", "manglende-barn");
        verified.Should().BeEmpty();
        suggested.Should().ContainSingle();
        rejected.Should().BeEmpty();
        C.ForScenario(View(), "skolenaervaer", "korrigert-rapport").Verified.Single().Trust.Should().Be(AssociationTrust.ReviewedCanonicalAssociation);
        C.ForScenario(View(), "skolenaervaer", "trekk").Rejected.Should().ContainSingle();
        C.ForScenario(null, "skolenaervaer", "trekk").Verified.Should().BeEmpty();
        C.Requirement(View().Associations[0]).Should().Be("FR-021 (skole/001-skoletjenesten)");
    }

    [Fact]
    public void S_T_dashboard_and_workflow_facts_equal_the_coverage_counts_and_offer_the_relationship_review_action()
    {
        var ws = new TestPlanWorkspace { GuideKey = "skolenaervaer", Candidates = [new ScenarioCandidate()] };
        var focus = P.Focus(ws, View());
        focus.Facts.Single(f => f.Id == "req-reviewed").Count.Should().Be(View().Reviewed);
        focus.Facts.Single(f => f.Id == "req-reviewed").Label.Should().Contain("of 5");
        focus.Facts.Single(f => f.Id == "req-needs").Count.Should().Be(View().NeedsReview);
        var review = focus.Actions.Single(a => a.Id == "req-associations");
        review.Route.Should().Be(C.RelationshipReviewHref);
        focus.Actions.Should().Contain(a => a.Id == "req-scenarios");

        var unknown = P.Focus(ws, new RequirementCoverageView { Known = false, UnknownReason = "x" });
        unknown.Facts.Should().NotContain(f => f.Id.StartsWith("req-"), "unknown coverage has no denominator");
        unknown.Actions.Should().NotContain(a => a.Id.StartsWith("req-"));
        P.Focus(ws).Facts.Should().BeEquivalentTo(unknown.Facts);
    }

    [Fact]
    public void An_association_made_on_an_older_scenario_text_asks_for_a_new_relationship_analysis()
    {
        var view = View() with { Associations = [Assoc("x", "FR-001", AssociationTrust.SuggestedAssociation) with { InterpretationOutdated = true }] };
        P.Focus(new TestPlanWorkspace { GuideKey = "g" }, view).Actions.Should().Contain(a => a.Id == "req-reinterpret");
    }
}
