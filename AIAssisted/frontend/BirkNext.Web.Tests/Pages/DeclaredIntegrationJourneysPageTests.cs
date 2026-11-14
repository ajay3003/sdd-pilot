using BirkNext.Integrations;
using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// Declared Integration Journeys (the Integration Journeys owner's form): per project, a name and the ordered component chain; validated before
/// sending (names only), listed from the backend after saving (so a reload shows the same), editable as a new revision (earlier revisions shown,
/// never changed) and removable (retired; history kept). Without a project nothing can be declared.
/// </summary>
public sealed class DeclaredIntegrationJourneysPageTests : BunitContext
{
    private readonly DeclaredJourneyApiFake _api = new();

    private IRenderedComponent<DeclaredIntegrationJourneys> Page(string? projectSlug = "import:acc", string? query = null)
    {
        Services.AddSingleton<IDeclaredIntegrationJourneyApiService>(_api);
        Services.AddSingleton(BirkNext.Web.Tests.Services.WorkspaceSnapshots.Projection(BirkNext.Web.Tests.Services.WorkspaceSnapshots.Loaded("Imported", projectSlug, "Journey fixture")).Object);
        if (query is not null) Services.GetRequiredService<NavigationManager>().NavigateTo("integration-journeys/declare" + query);
        var page = Render<DeclaredIntegrationJourneys>();
        page.WaitForAssertion(() => page.Find("[data-testid=ijd-page]").GetAttribute("aria-busy").Should().Be("false"));
        return page;
    }

    [Fact]
    public void Declaring_sends_the_ordered_chain_for_the_project_and_lists_the_stored_journey()
    {
        var page = Page();
        page.Find("[data-testid=ijd-empty]");
        page.Find("[data-testid=ijd-overview]").GetAttribute("href").Should().Be("integration-journeys");
        page.Find("[data-testid=ijd-save]").HasAttribute("disabled").Should().BeTrue("nothing is declared yet");

        page.Find("[data-testid=ijd-name]").Input("Order notification");
        page.Find("[data-testid=ijd-components]").Input("Orders.Adapter\nOrders.Api\n\norder-events\nOrders.Notifier\n");
        page.Find("[data-testid=ijd-save]").HasAttribute("disabled").Should().BeTrue("who declares it is required");
        page.Find("[data-testid=ijd-declared-by]").Input("Kari");
        page.Find("[data-testid=ijd-preview]").TextContent.Should().Contain("Orders.Adapter → Orders.Api → order-events → Orders.Notifier");
        page.Find("[data-testid=ijd-save]").Click();

        _api.Sent.Should().ContainSingle().Which.Should().Match<DeclaredIntegrationJourneyRequest>(r => r.ProjectKey == "import:acc" && r.DeclaredBy == "Kari" && r.ExpectedRevision == null
            && r.Components.SequenceEqual(new[] { "Orders.Adapter", "Orders.Api", "order-events", "Orders.Notifier" }));
        page.WaitForAssertion(() => page.Find("[data-testid=ijd-journey]").GetAttribute("data-id").Should().Be("project.import:acc:order-notification"));
        page.Find("[data-testid=ijd-journey-chain]").TextContent.Should().Be("Orders.Adapter → Orders.Api → order-events → Orders.Notifier");
        page.Find("[data-testid=ijd-journey-revision]").TextContent.Should().Be("revision 1");
        page.Find("[data-testid=ijd-status]").TextContent.Should().Contain("project.import:acc:order-notification");
        _api.ListedFor.Should().OnlyContain(k => k == "import:acc").And.HaveCountGreaterThan(1, "the list is read from the backend on load and after saving");
    }

    [Fact]
    public void A_url_is_refused_before_anything_is_sent()
    {
        var page = Page();
        page.Find("[data-testid=ijd-name]").Input("Bad");
        page.Find("[data-testid=ijd-components]").Input("Orders.Api\nhttps://orders.example/api");
        page.Find("[data-testid=ijd-declared-by]").Input("Kari");
        page.Find("[data-testid=ijd-save]").HasAttribute("disabled").Should().BeTrue();
        page.Find("[data-testid=ijd-invalid]").TextContent.Should().Contain("never a URL");
        _api.Sent.Should().BeEmpty();
    }

    [Fact]
    public void A_stored_journey_is_shown_on_load_and_can_be_removed()
    {
        _api.Seed("import:acc", "Order notification", "Kari", new[] { "A", "B" });
        _api.Seed("import:other", "Elsewhere", "Ola", new[] { "A", "B" });
        var page = Page();

        page.FindAll("[data-testid=ijd-journey]").Should().ContainSingle("only this project's journeys are listed");
        page.Find("[data-testid=ijd-remove]").Click();
        page.WaitForAssertion(() => page.FindAll("[data-testid=ijd-journey]").Should().BeEmpty());
        page.Find("[data-testid=ijd-status]").TextContent.Should().Contain("revision history is kept");
        _api.Stored.Single(j => j.ProjectKey == "import:acc").RetiredAt.Should().NotBeNull("removing retires the journey; nothing is deleted");
        _api.Stored.Single(j => j.ProjectKey == "import:other").RetiredAt.Should().BeNull();
    }

    [Fact]
    public void Editing_from_the_query_saves_a_new_revision_and_shows_the_history()
    {
        _api.Seed("import:acc", "Order notification", "Kari", new[] { "Orders.Api", "order-events" });
        var page = Page(query: "?journey=order-notification");

        page.WaitForAssertion(() => page.Find("[data-testid=ijd-form]").GetAttribute("data-mode").Should().Be("edit"));
        page.Find("[data-testid=ijd-editing]").TextContent.Should().Contain("revision 1").And.Contain("project.import:acc:order-notification");
        page.Find("[data-testid=ijd-name]").GetAttribute("value").Should().Be("Order notification");
        page.FindAll("[data-testid=ijd-revision]").Should().ContainSingle();

        page.Find("[data-testid=ijd-components]").Input("Orders.Api\norder-events\nOrders.Notifier");
        page.Find("[data-testid=ijd-declared-by]").Input("Ola");
        page.Find("[data-testid=ijd-save]").TextContent.Should().Be("Save new revision");
        page.Find("[data-testid=ijd-save]").Click();

        var (project, journeyId, request) = _api.Revised.Should().ContainSingle().Subject;
        (project, journeyId, request.ExpectedRevision).Should().Be(("import:acc", "order-notification", (int?)1));
        _api.Sent.Should().BeEmpty("an edit never declares a second journey");
        page.WaitForAssertion(() => page.FindAll("[data-testid=ijd-revision]").Select(r => r.GetAttribute("data-revision")).Should().Equal("2", "1"));
        page.Find("[data-testid=ijd-status]").TextContent.Should().Contain("Saved revision 2");
        page.Find("[data-testid=ijd-journey-revision]").TextContent.Should().Be("revision 2");
        page.Find("[data-testid=ijd-revision][data-revision='1']").TextContent.Should().Contain("Orders.Api → order-events").And.NotContain("Orders.Notifier", "an earlier revision is shown as it was");
    }

    [Fact]
    public void The_edit_button_opens_the_journey_and_cancel_returns_to_declaring()
    {
        _api.Seed("import:acc", "Order notification", "Kari", new[] { "A", "B" }, new[] { "A", "B", "C" });
        var page = Page();

        page.Find("[data-testid=ijd-edit]").Click();
        page.WaitForAssertion(() => page.FindAll("[data-testid=ijd-revision]").Should().HaveCount(2));
        page.Find("[data-testid=ijd-editing]").TextContent.Should().Contain("Saving adds revision 3");
        page.Find("[data-testid=ijd-cancel-edit]").Click();
        page.Find("[data-testid=ijd-form]").GetAttribute("data-mode").Should().Be("declare");
        page.FindAll("[data-testid=ijd-revisions]").Should().BeEmpty();
    }

    [Fact]
    public void Without_a_project_nothing_can_be_declared()
    {
        var page = Page(projectSlug: null);
        page.Find("[data-testid=ijd-no-project]");
        page.FindAll("[data-testid=ijd-form]").Should().BeEmpty();
        _api.ListedFor.Should().BeEmpty();
    }
}
