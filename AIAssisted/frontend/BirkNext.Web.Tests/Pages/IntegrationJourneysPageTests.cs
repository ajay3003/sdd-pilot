using BirkNext.Integrations;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// Integration Journeys (/integration-journeys, no sidebar entry): the journeys declared for the current project, each with its current revision
/// and its revision history on demand, a clear "Declare journey" action to the form, an Edit link that opens the form for a new revision, and the
/// legacy-file import outcome when it found something. Another project's journeys are never shown.
/// </summary>
public sealed class IntegrationJourneysPageTests : BunitContext
{
    private readonly DeclaredJourneyApiFake _api = new();

    private IRenderedComponent<IntegrationJourneys> Page(string? projectSlug = "import:acc")
    {
        Services.AddSingleton<IDeclaredIntegrationJourneyApiService>(_api);
        Services.AddSingleton(BirkNext.Web.Tests.Services.WorkspaceSnapshots.Projection(BirkNext.Web.Tests.Services.WorkspaceSnapshots.Loaded("Imported", projectSlug, "Journey fixture")).Object);
        var page = Render<IntegrationJourneys>();
        page.WaitForAssertion(() => page.Find("[data-testid=ijl-page]").GetAttribute("aria-busy").Should().Be("false"));
        return page;
    }

    [Fact]
    public void An_empty_project_offers_the_declare_action()
    {
        var page = Page();
        page.Find("[data-testid=ijl-declared]");
        page.Find("[data-testid=ijl-empty]");
        page.Find("[data-testid=ijl-declare]").GetAttribute("href").Should().Be("integration-journeys/declare");
        page.Find("[data-testid=ijl-declare]").TextContent.Should().Be("Declare journey");
        page.FindAll("[data-testid=ijl-migration]").Should().BeEmpty("nothing was imported");
    }

    [Fact]
    public void Declared_journeys_of_this_project_are_listed_with_their_revision_history()
    {
        _api.Seed("import:acc", "Order notification", "Kari", new[] { "Orders.Api", "order-events" }, new[] { "Orders.Api", "order-events", "Orders.Notifier" });
        _api.Seed("import:other", "Elsewhere", "Ola", new[] { "A", "B" });
        var page = Page();

        var journey = page.FindAll("[data-testid=ijl-journey]").Should().ContainSingle("only this project's journeys are listed").Subject;
        journey.GetAttribute("data-id").Should().Be("project.import:acc:order-notification");
        page.Find("[data-testid=ijl-journey-revision]").TextContent.Should().Be("Revision 2");
        page.Find("[data-testid=ijl-journey-chain]").TextContent.Should().Be("Orders.Api → order-events → Orders.Notifier");
        page.Find("[data-testid=ijl-edit]").GetAttribute("href").Should().Be("integration-journeys/declare?journey=order-notification");
        page.FindAll("[data-testid=ijl-revisions]").Should().BeEmpty("history is shown on demand");

        page.Find("[data-testid=ijl-revisions-toggle]").Click();
        page.WaitForAssertion(() => page.FindAll("[data-testid=ijl-revision]").Select(r => r.GetAttribute("data-revision")).Should().Equal("2", "1"));
        page.Find("[data-testid=ijl-revision][data-revision='2']").TextContent.Should().Contain("(current)").And.Contain("edited");
        page.Find("[data-testid=ijl-revision][data-revision='1']").TextContent.Should().Contain("Orders.Api → order-events").And.NotContain("Orders.Notifier");
        _api.HistoryRequests.Should().Equal("import:acc|order-notification");

        page.Find("[data-testid=ijl-revisions-toggle]").Click();
        page.FindAll("[data-testid=ijl-revisions]").Should().BeEmpty();
    }

    [Fact]
    public void Legacy_import_issues_for_this_project_are_shown()
    {
        _api.Migration = new DeclaredIntegrationJourneyMigrationReport
        {
            Status = "CompletedWithIssues", Read = 3, Imported = 1, Conflicts = 1, Invalid = 1, RanAt = DateTimeOffset.UtcNow,
            Items =
            [
                new() { ProjectKey = "import:acc", JourneyId = "order-notification", Outcome = DeclaredIntegrationJourneyRules.Conflict, Detail = "The database already has a different journey with this id." },
                new() { ProjectKey = "import:acc", JourneyId = "order-archive", Outcome = DeclaredIntegrationJourneyRules.Imported, Detail = "Imported as revision 1." },
                new() { ProjectKey = "import:other", JourneyId = "x", Outcome = DeclaredIntegrationJourneyRules.Invalid, Detail = "Bad." },
            ],
        };
        var page = Page();

        page.Find("[data-testid=ijl-migration-summary]").TextContent.Should().Contain("1 imported").And.Contain("never changed or deleted");
        page.FindAll("[data-testid=ijl-migration-issue]").Should().ContainSingle("only this project's conflicts and invalid entries are listed")
            .Which.GetAttribute("data-outcome").Should().Be(DeclaredIntegrationJourneyRules.Conflict);
    }

    [Fact]
    public void Without_a_project_nothing_is_listed()
    {
        var page = Page(projectSlug: null);
        page.Find("[data-testid=ijl-no-project]");
        page.FindAll("[data-testid=ijl-declared]").Should().BeEmpty();
        _api.ListedFor.Should().BeEmpty();
    }
}
