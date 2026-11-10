using BirkNext.CriticalE2E;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Pages;

/// <summary>Suggested tests panel: generation summary, the four areas, expected result shown as missing (never invented), locator acceptance only
/// after a unique verification, approval problems surfaced, approved tests listed with their execution state. The panel never runs a test.</summary>
public sealed class CriticalE2ESuggestionsTests : BunitContext
{
    private readonly Mock<ICriticalE2EApiService> _api = new();
    private static readonly Guid Snap = Guid.Parse("aaaaaaaa-6666-0000-0000-000000000001");

    private static SuggestedStep Click(LocatorVerification? _ = null) => new()
    {
        StepId = "s2", Description = "Click \"Save\"", Action = CompanionActionKind.Click, ControlId = "c1", Mutating = true,
        Locators = [new(new CompanionSelector { Kind = CompanionSelectorKind.TestId, Value = "save" }, 1, "data-testid in source"), new(new CompanionSelector { Kind = CompanionSelectorKind.Role, Role = "button", Name = "Save" }, 2, "role + accessible name from source")],
    };

    private static TestSuggestion Valid(params StepVerification[] verifications) => new()
    {
        SuggestionId = "sug-valid", Title = "New child: a valid submission", Kind = SuggestionKind.ValidSubmission, Route = "/children/new", Confidence = "Low", RuleVersion = "suggest-v1",
        Origins = [SuggestionOrigin.SourceImplementationDerived], SourceRefs = ["Web/Pages/NewChild.razor:11"], TestDataNeeds = ["Reference: a value from approved Test Data (identifier-like)"],
        Steps = [new() { StepId = "s1", Description = "Open /children/new", Action = CompanionActionKind.Navigate, Value = "/children/new" }, Click()],
        States = [SuggestionState.NeedsDomVerification, SuggestionState.NeedsTestData, SuggestionState.NeedsExpectedResult], Verifications = [.. verifications],
    };

    private static TestSuggestion Approved() => Valid() with { SuggestionId = "sug-ok", Title = "New child opens", Kind = SuggestionKind.PageOpens, ApprovedFlowId = "flow-9", ApprovedFlowVersion = 1,
        States = [SuggestionState.Approved, SuggestionState.Executed], Coverage = SuggestionCoverage.AlreadyCovered, CoveredByFlowId = "flow-9", ExpectedResult = "The page heading is visible." };

    private SuggestionGenerationResult Result(params TestSuggestion[] s) => new()
    {
        ProjectKey = "synth.ui", SnapshotId = Snap, PagesAnalyzed = 2, Suggestions = [.. s],
        Summary = new BirkNext.CriticalE2E.SuggestionSummary(s.Length, s.Count(x => x.Coverage != SuggestionCoverage.NewSuggestion), s.Count(x => x.Coverage == SuggestionCoverage.NewSuggestion), 1, 1, 1, 0, 1),
        Impact = [new(SuggestionImpactKind.LocatorRevalidationNeeded, "flow-9", "New child opens", "s2", "testid=\"page-title\" is no longer declared in the source")],
    };

    public CriticalE2ESuggestionsTests()
    {
        Services.AddSingleton(_api.Object);
        _api.Setup(a => a.SuggestionSnapshotsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([new SuggestionSnapshotOption(Snap, "synth.ui", "Synth.Ui.zip", DateTimeOffset.UnixEpoch, true, 2, 2, 12)]);
        _api.Setup(a => a.GenerateSuggestionsAsync(It.IsAny<SuggestionGenerateRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(Result(Valid(), Approved()));
    }

    private IRenderedComponent<CriticalE2ESuggestions> Open(string? route = "/children/new")
    {
        var cut = Render<CriticalE2ESuggestions>(p => p.Add(x => x.EnvironmentId, "env-1").Add(x => x.ProfileId, "p1").Add(x => x.CurrentRoute, route));
        cut.Find("[data-testid=sg-generate]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=sg-summary]"));
        return cut;
    }

    [Fact]
    public void Generation_shows_counts_areas_and_impact_without_running_anything()
    {
        var cut = Open();
        cut.Find("[data-testid=sg-count-suggestions] dd").TextContent.Should().Be("2");
        cut.FindAll("[data-testid^=sg-area-]").Select(a => a.TextContent).Should().Equal("Test Suggestions", "DOM Mapping", "Test Data & Readiness", "Execution Results");
        cut.FindAll("[data-testid=sg-item]").Select(i => i.GetAttribute("data-coverage")).Should().Equal("NewSuggestion", "AlreadyCovered");
        cut.Find("[data-testid=sg-impact-item]").GetAttribute("data-kind").Should().Be("LocatorRevalidationNeeded");
        cut.Find("[data-testid=sg-message]").TextContent.Should().Contain("Nothing was executed");
        _api.Verify(a => a.RunAsync(It.IsAny<CriticalE2ERunFlowRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void An_unknown_expected_result_is_shown_as_missing_and_the_reviewer_may_state_a_route()
    {
        var cut = Open();
        cut.Find("[data-testid=sg-item][data-id=sug-valid] [data-testid=sg-review]").Click();
        cut.Find("[data-testid=sg-expected]").TextContent.Should().StartWith("NEEDS EXPECTED RESULT");
        cut.Find("[data-testid=sg-data-needs]").TextContent.Should().Contain("identifier-like");
        cut.FindAll("[data-testid=sg-expected-route]").Should().ContainSingle();
        cut.Find("[data-testid=sg-step][data-step=s2]").TextContent.Should().Contain("changes data");
    }

    [Fact]
    public void A_locator_is_accepted_only_after_a_unique_verification_and_ambiguity_is_never_accepted()
    {
        var cut = Open();
        cut.Find("[data-testid=sg-item][data-id=sug-valid] [data-testid=sg-review]").Click();
        cut.Find("[data-testid=sg-step][data-step=s2] [data-testid=sg-accept]").HasAttribute("disabled").Should().BeTrue("not verified yet");

        var ambiguous = new StepVerification { StepId = "s2", State = LocatorVerification.Ambiguous, Checks = [new() { Selector = Click().Locators[1].Selector, MatchCount = 2, Visible = true }] };
        _api.Setup(a => a.VerifySuggestionAsync(It.IsAny<SuggestionVerifyRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(new SuggestionVerifyResult(Valid(ambiguous), "0 of 1 verified", true));
        cut.Find("[data-testid=sg-verify]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=sg-step][data-step=s2]").GetAttribute("data-verification").Should().Be("Ambiguous"));
        cut.Find("[data-testid=sg-step][data-step=s2] [data-testid=sg-accept]").HasAttribute("disabled").Should().BeTrue();
        cut.Find("[data-testid=sg-step][data-step=s2] [data-testid=sg-accept]").GetAttribute("title").Should().Contain("never accepted");

        var unique = new StepVerification { StepId = "s2", State = LocatorVerification.VerifiedUnique, Selector = Click().Locators[0].Selector, ObservedOrigin = "http://127.0.0.1:5097", ObservedRoute = "/children/new",
            VerifiedAt = DateTimeOffset.UnixEpoch, Checks = [new() { Selector = Click().Locators[0].Selector, MatchCount = 1, Visible = true }, new() { Selector = Click().Locators[1].Selector, MatchCount = 2, Visible = true }] };
        _api.Setup(a => a.VerifySuggestionAsync(It.IsAny<SuggestionVerifyRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(new SuggestionVerifyResult(Valid(unique), "1 of 1 verified", true));
        cut.Find("[data-testid=sg-verify]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=sg-step][data-step=s2] [data-testid=sg-accept]").HasAttribute("disabled").Should().BeFalse());
        cut.FindAll("[data-testid=sg-step][data-step=s2] [data-testid=sg-locator]").Select(l => l.GetAttribute("data-matches")).Should().Equal("1", "2");
        cut.FindAll("[data-testid=sg-choose]").Should().BeEmpty("the ambiguous alternative cannot be chosen");
        SuggestionDecisionRequest? sent = null;
        _api.Setup(a => a.DecideSuggestionAsync(It.IsAny<SuggestionDecisionRequest>(), It.IsAny<CancellationToken>()))
            .Callback((SuggestionDecisionRequest r, CancellationToken _) => sent = r).ReturnsAsync((Valid(unique) with { Decisions = [new("s2", LocatorDecision.Accepted, unique.Selector, "Local session", DateTimeOffset.UnixEpoch, null)] }, (string?)null));
        cut.Find("[data-testid=sg-step][data-step=s2] [data-testid=sg-accept]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=sg-step][data-step=s2]").GetAttribute("data-decision").Should().Be("Accepted"));
        sent!.Decision.Should().Be(LocatorDecision.Accepted);
    }

    [Fact]
    public void Verification_needs_the_attended_page_on_the_suggestion_route_and_approval_problems_are_shown()
    {
        var cut = Open(route: "/elsewhere");
        cut.Find("[data-testid=sg-item][data-id=sug-valid] [data-testid=sg-review]").Click();
        cut.Find("[data-testid=sg-route-state]").TextContent.Should().Contain("Open /children/new").And.Contain("/elsewhere");
        _api.Setup(a => a.ApproveSuggestionAsync(It.IsAny<SuggestionApproveRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SuggestionApproveResult(null, ["Test Data needed first: Reference.", "No expected result: the source does not state one."]));
        cut.Find("[data-testid=sg-approve]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=sg-error]").TextContent.Should().Contain("Test Data needed first").And.Contain("No expected result"));
    }

    [Fact]
    public void Approved_suggestions_are_listed_with_their_execution_state_and_the_existing_test()
    {
        var cut = Open();
        cut.Find("[data-testid=sg-area-results]").Click();
        cut.Find("[data-testid=sg-result]").GetAttribute("data-state").Should().Be("Executed");
        cut.Find("[data-testid=sg-open-results]").GetAttribute("href").Should().Contain("rsrc=BrowserE2E");
        cut.Find("[data-testid=sg-area-suggestions]").Click();
        cut.Find("[data-testid=sg-item][data-id=sug-ok] [data-testid=sg-review]").Click();
        cut.Find("[data-testid=sg-open-existing]").GetAttribute("href").Should().EndWith("#e2e-flow-flow-9");
        cut.Find("[data-testid=sg-approve]").HasAttribute("disabled").Should().BeTrue("already approved");
    }
}
