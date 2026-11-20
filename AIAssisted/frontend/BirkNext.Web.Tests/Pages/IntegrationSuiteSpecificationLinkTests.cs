using BirkNext.IntegrationSuites;
using BirkNext.Web.Pages;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// A Kode 6/7 question the project's specification already answers: the review item shows the proposed requirement with its wording and lets the
/// tester confirm or reject it (nothing is decided by the page); a requirement that names the matter as open (OQ) can only be rejected; each
/// scenario shows clarified / implemented / verified as three separate states.
/// </summary>
public sealed class IntegrationSuiteSpecificationLinkTests : BunitContext
{
    private static readonly SuiteSpecificationLink Fr008 = new("FR-008", "SkoleService/specs/001/spec.md", "Reports for Kode 6/7 children MUST be processed like any other report.",
        new string('a', 64), SuiteSpecificationAnswer.Answers, "Proposed by BirkNext: the wording contains \"kode 6/7\", \"report\". Not a decision until a tester confirms it.");
    private static readonly SuiteSpecificationLink Fr008B = new("FR-008B", "SkoleService/specs/001/spec.md", "Whether and how they are delivered later is open (OQ-26).",
        new string('b', 64), SuiteSpecificationAnswer.OpenInSpecification, "Proposed by BirkNext.", "OQ-26");

    private static IntegrationSuite Suite() => new()
    {
        Review =
        [
            new() { Key = "security:intake-rule", Area = "Security (Kode 6/7)", Question = "What must happen at intake?", Why = "Needed by K67-01.", SpecificationLinks = [Fr008] },
            new() { Key = "security:period-lock", Area = "Security (Kode 6/7)", Question = "Are the rows ever delivered?", Why = "Needed by K67-09.", SpecificationLinks = [Fr008B] },
        ],
        Security =
        [
            new() { ScenarioId = "K67-01", Title = "Kode 6 child reported through Altinn", Triage = SuiteTriage.NeedsDomainDecision, ReviewKey = "security:intake-rule",
                OpenQuestion = "What must happen at intake?", Clarification = SuiteClarification.ProposedFromSpecification, ClarificationDetail = "FR-008 appears to answer this.",
                Implementation = SuiteImplementationEvidence.SourceEvidence, ImplementationDetail = "2 classification symbol(s).",
                Verification = SuiteVerificationState.NotRun, VerificationDetail = "No run of this scenario is recorded." },
        ],
    };

    private (IRenderedComponent<IntegrationSuiteDetail> Cut, List<(string Key, string RequirementId, bool Confirm)> Sent) Render(string tab)
    {
        var sent = new List<(string, string, bool)>();
        var cut = Render<IntegrationSuiteDetail>(p => p.Add(c => c.Suite, Suite()).Add(c => c.Tab, tab)
            .Add(c => c.OnSpecificationLink, EventCallback.Factory.Create<(string, string, bool)>(this, x => sent.Add(x))));
        return (cut, sent);
    }

    [Fact]
    public void A_proposed_answer_is_shown_with_its_wording_and_the_tester_confirms_it()
    {
        var (cut, sent) = Render("Review");
        var link = cut.Find("[data-testid=its-review-item][data-key='security:intake-rule'] [data-testid=its-spec-link]");

        link.GetAttribute("data-requirement").Should().Be("FR-008");
        link.TextContent.Should().Contain("Proposed answer").And.Contain("processed like any other report").And.Contain("Not a decision until a tester confirms it");
        cut.Find("[data-key='security:intake-rule'] [data-testid=its-spec-confirm]").Click();

        sent.Should().Equal(("security:intake-rule", "FR-008", true));
    }

    [Fact]
    public void A_requirement_that_names_the_question_open_cannot_be_confirmed_only_rejected()
    {
        var (cut, sent) = Render("Review");
        var item = cut.Find("[data-testid=its-review-item][data-key='security:period-lock']");

        item.TextContent.Should().Contain("Open in the specification (OQ-26)");
        item.QuerySelectorAll("[data-testid=its-spec-confirm]").Should().BeEmpty();
        cut.Find("[data-key='security:period-lock'] [data-testid=its-spec-reject]").Click();
        sent.Should().Equal(("security:period-lock", "FR-008B", false));
    }

    [Fact]
    public void A_scenario_shows_clarified_implemented_and_verified_separately()
    {
        var (cut, _) = Render("Kode 6/7");
        var scenario = cut.Find("[data-testid=its-sec][data-id='K67-01']");

        scenario.QuerySelector("[data-testid=its-sec-clarified]")!.GetAttribute("data-state").Should().Be("ProposedFromSpecification");
        scenario.QuerySelector("[data-testid=its-sec-implemented]")!.GetAttribute("data-state").Should().Be("SourceEvidence");
        scenario.QuerySelector("[data-testid=its-sec-verified]")!.GetAttribute("data-state").Should().Be("NotRun");
        scenario.TextContent.Should().Contain("Proposed from the specification").And.Contain("Not run");
    }
}
