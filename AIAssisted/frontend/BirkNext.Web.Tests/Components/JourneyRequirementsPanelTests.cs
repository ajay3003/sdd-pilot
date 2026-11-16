using BirkNext.Integrations;
using BirkNext.IntegrationSuites;
using BirkNext.TestPlanning;
using BirkNext.Web.Components;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Requirements a journey verifies (Integration Testing → Prepare): linked requirements by title with their state and history, a search over the
/// project's own specification requirements, confirm / reject / remove as the rules allow, and the suites prepared for the journey as derived
/// coverage — never shown as tested. The trace panel shows the same links apart from the manual counts.
/// </summary>
public sealed class JourneyRequirementsPanelTests : BunitContext
{
    private const string Project = "import:lendo";
    private readonly Mock<IDeclaredIntegrationJourneyApiService> _api = new();
    private static readonly DeclaredIntegrationJourney Journey = new() { ProjectKey = Project, JourneyId = "loan-flow", DisplayName = "Loan flow", Components = ["Web", "Api"], Revision = 2 };
    private JourneyRequirementLinkRequest? _linked;

    private static JourneyRequirementLink Link(string id, JourneyRequirementStatus status, int version = 1) => new()
    {
        Id = Guid.NewGuid(), ProjectKey = Project, JourneyId = "loan-flow", JourneyRevision = 2, RequirementId = id, SpecificationPath = "spec.md",
        Title = id == "FR-001" ? "The system MUST register a loan." : "The system MUST email a receipt.", Status = status, LinkedBy = "Kari", LinkedAt = DateTimeOffset.UtcNow,
        Version = version, History = [new(DateTimeOffset.UtcNow, "Kari", status, null)],
    };

    private IRenderedComponent<JourneyRequirementsPanel> Open(List<JourneyRequirementLink> links, IReadOnlyList<IntegrationSuiteSummary>? suites = null)
    {
        _api.Setup(a => a.RequirementLinksAsync(Project, "loan-flow", It.IsAny<CancellationToken>())).ReturnsAsync(((IReadOnlyList<JourneyRequirementLink>?)links, (string?)null));
        _api.Setup(a => a.LinkRequirementAsync(Project, "loan-flow", It.IsAny<JourneyRequirementLinkRequest>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, JourneyRequirementLinkRequest, CancellationToken>((_, _, r, _) => _linked = r)
            .ReturnsAsync((string _, string _, JourneyRequirementLinkRequest r, CancellationToken _) => (Link(r.RequirementId, JourneyRequirementStatus.Confirmed), (string?)null));
        _api.Setup(a => a.SetRequirementStatusAsync(Project, "loan-flow", It.IsAny<Guid>(), It.IsAny<JourneyRequirementStatusRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string _, Guid id, JourneyRequirementStatusRequest r, CancellationToken _) => (links.Single(l => l.Id == id) with { Status = r.Status, Version = r.Version + 1 }, (string?)null));
        Services.AddSingleton(_api.Object);
        var cut = Render<JourneyRequirementsPanel>(p => p.Add(x => x.ProjectKey, Project).Add(x => x.Journey, Journey).Add(x => x.Suites, suites ?? []));
        cut.WaitForAssertion(() => cut.Find("[data-testid=itx-req]").GetAttribute("aria-busy").Should().Be("false"));
        return cut;
    }

    [Fact]
    public void Linked_requirements_show_by_title_with_state_history_and_the_suites_prepared_for_the_journey()
    {
        var suite = new IntegrationSuiteSummary(Guid.NewGuid(), "k", 3, "api", "Loans API", Guid.NewGuid(), "lendo.zip", DateTimeOffset.UtcNow, SuiteStatus.NeedsReview, 4, 2, 1)
            { PreparedForJourneys = [new("project.import:lendo:loan-flow", 2, "Loan flow")] };
        var cut = Open([Link("FR-001", JourneyRequirementStatus.Confirmed), Link("FR-002", JourneyRequirementStatus.Proposed)], [suite]);

        var items = cut.FindAll("[data-testid=itx-req-item]");
        items.Select(i => i.GetAttribute("data-status")).Should().Equal("Confirmed", "Proposed");
        items[0].TextContent.Should().Contain("The system MUST register a loan.").And.Contain("FR-001").And.Contain("journey revision 2");
        cut.Find("[data-testid=itx-req-suites]").TextContent.Should().Contain("Loans API r3").And.Contain("Needs review", "a prepared suite is shown as prepared, not tested");
        items[1].QuerySelector("[data-testid=itx-req-status]")!.TextContent.Should().Contain("needs a decision");
        items[1].QuerySelectorAll("button").Select(b => b.TextContent).Should().Equal("Confirm", "Reject");
        items[0].QuerySelectorAll("button").Select(b => b.TextContent).Should().Equal("Remove");
    }

    [Fact]
    public void A_proposal_is_confirmed_and_a_link_removed_with_the_version_the_tester_saw()
    {
        var links = new List<JourneyRequirementLink> { Link("FR-002", JourneyRequirementStatus.Proposed, version: 4) };
        var cut = Open(links);
        cut.Find("[data-testid=itx-req-confirmed]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=itx-req-item]").GetAttribute("data-status").Should().Be("Confirmed"));
        _api.Verify(a => a.SetRequirementStatusAsync(Project, "loan-flow", links[0].Id,
            It.Is<JourneyRequirementStatusRequest>(r => r.Version == 4 && r.Status == JourneyRequirementStatus.Confirmed), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void Without_specification_requirements_the_panel_says_how_to_get_them_and_offers_no_search()
    {
        var cut = Open([]);
        cut.Find("[data-testid=itx-req-none]").TextContent.Should().Contain("No requirement is linked");
        cut.Find("[data-testid=itx-req-no-spec]").TextContent.Should().Contain("Import the project's specification");
        cut.FindAll("[data-testid=itx-req-search]").Should().BeEmpty();
    }

    [Fact]
    public void The_trace_panel_shows_integration_links_apart_from_the_manual_counts()
    {
        var view = new PreparedCaseTraceView
        {
            ProjectKey = Project,
            Integration =
            [
                new("FR-001", "spec.md", "The system MUST register a loan.", "project.import:lendo:loan-flow", "Loan flow", 1, 2,
                    [new(Guid.NewGuid(), "Loans API", 3, "Failed", true, "integration-suites?suite=x")]),
                new("FR-002", "spec.md", "Email a receipt.", "project.import:lendo:loan-flow", "Loan flow", 2, 2, []),
            ],
        };
        var cut = Render<PreparedCaseTracePanel>(p => p.Add(x => x.View, view));
        var rows = cut.FindAll("[data-testid=pct-integration-row]");
        rows.Should().HaveCount(2);
        rows[0].QuerySelector("[data-testid=pct-integration-suite]")!.TextContent.Should().Contain("Loans API r3").And.Contain("Failed");
        rows[0].QuerySelector("[data-testid=pct-integration-revised]")!.TextContent.Should().Contain("linked at revision 1");
        rows[1].QuerySelector("[data-testid=pct-integration-no-suite]")!.TextContent.Should().Be("No suite prepared yet");
        rows[0].QuerySelector("[data-testid=pct-integration-journey]")!.GetAttribute("href").Should().Contain("stage=prepare&journey=project.import%3Alendo%3Aloan-flow");
    }
}
