using BirkNext.Integrations;
using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// Declared Integration Journeys (the Integration Journeys owner's form): per project, a name and the ordered component chain; validated before
/// sending (names only), listed from the backend after saving (so a reload shows the same), removable. Without a project nothing can be declared.
/// </summary>
public sealed class DeclaredIntegrationJourneysPageTests : BunitContext
{
    private sealed class FakeApi : IDeclaredIntegrationJourneyApiService
    {
        public List<DeclaredIntegrationJourney> Stored { get; } = [];
        public List<DeclaredIntegrationJourneyRequest> Sent { get; } = [];
        public List<string> ListedFor { get; } = [];

        public Task<(IReadOnlyList<DeclaredIntegrationJourney>? Journeys, string? Error)> ListAsync(string projectKey, CancellationToken ct = default)
        {
            ListedFor.Add(projectKey);
            return Task.FromResult<(IReadOnlyList<DeclaredIntegrationJourney>?, string?)>(([.. Stored.Where(j => j.ProjectKey == projectKey)], null));
        }

        public Task<(DeclaredIntegrationJourney? Journey, string? Error)> DeclareAsync(DeclaredIntegrationJourneyRequest request, CancellationToken ct = default)
        {
            Sent.Add(request);
            var journey = new DeclaredIntegrationJourney
            {
                ProjectKey = request.ProjectKey, JourneyId = DeclaredIntegrationJourneyRules.Slug(request.DisplayName), DisplayName = request.DisplayName.Trim(),
                Components = DeclaredIntegrationJourneyRules.Chain(request.Components), DeclaredBy = request.DeclaredBy, DeclaredAt = DateTimeOffset.UtcNow,
            };
            Stored.Add(journey);
            return Task.FromResult<(DeclaredIntegrationJourney?, string?)>((journey, null));
        }

        public Task<string?> RemoveAsync(string projectKey, string journeyId, CancellationToken ct = default)
        {
            Stored.RemoveAll(j => j.ProjectKey == projectKey && j.JourneyId == journeyId);
            return Task.FromResult<string?>(null);
        }
    }

    private readonly FakeApi _api = new();

    private IRenderedComponent<DeclaredIntegrationJourneys> Page(string? projectSlug = "import:acc")
    {
        Services.AddSingleton<IDeclaredIntegrationJourneyApiService>(_api);
        Services.AddSingleton(BirkNext.Web.Tests.Services.WorkspaceSnapshots.Projection(BirkNext.Web.Tests.Services.WorkspaceSnapshots.Loaded("Imported", projectSlug, "Journey fixture")).Object);
        var page = Render<DeclaredIntegrationJourneys>();
        page.WaitForAssertion(() => page.Find("[data-testid=ijd-page]").GetAttribute("aria-busy").Should().Be("false"));
        return page;
    }

    [Fact]
    public void Declaring_sends_the_ordered_chain_for_the_project_and_lists_the_stored_journey()
    {
        var page = Page();
        page.Find("[data-testid=ijd-empty]");
        page.Find("[data-testid=ijd-save]").HasAttribute("disabled").Should().BeTrue("nothing is declared yet");

        page.Find("[data-testid=ijd-name]").Input("Order notification");
        page.Find("[data-testid=ijd-components]").Input("Orders.Adapter\nOrders.Api\n\norder-events\nOrders.Notifier\n");
        page.Find("[data-testid=ijd-save]").HasAttribute("disabled").Should().BeTrue("who declares it is required");
        page.Find("[data-testid=ijd-declared-by]").Input("Kari");
        page.Find("[data-testid=ijd-preview]").TextContent.Should().Contain("Orders.Adapter → Orders.Api → order-events → Orders.Notifier");
        page.Find("[data-testid=ijd-save]").Click();

        _api.Sent.Should().ContainSingle().Which.Should().Match<DeclaredIntegrationJourneyRequest>(r => r.ProjectKey == "import:acc" && r.DeclaredBy == "Kari"
            && r.Components.SequenceEqual(new[] { "Orders.Adapter", "Orders.Api", "order-events", "Orders.Notifier" }));
        page.WaitForAssertion(() => page.Find("[data-testid=ijd-journey]").GetAttribute("data-id").Should().Be("project.import:acc:order-notification"));
        page.Find("[data-testid=ijd-journey-chain]").TextContent.Should().Be("Orders.Adapter → Orders.Api → order-events → Orders.Notifier");
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
        _api.Stored.Add(new DeclaredIntegrationJourney { ProjectKey = "import:acc", JourneyId = "order-notification", DisplayName = "Order notification", Components = ["A", "B"], DeclaredBy = "Kari" });
        _api.Stored.Add(new DeclaredIntegrationJourney { ProjectKey = "import:other", JourneyId = "elsewhere", DisplayName = "Elsewhere", Components = ["A", "B"], DeclaredBy = "Ola" });
        var page = Page();

        page.FindAll("[data-testid=ijd-journey]").Should().ContainSingle("only this project's journeys are listed");
        page.Find("[data-testid=ijd-remove]").Click();
        page.WaitForAssertion(() => page.FindAll("[data-testid=ijd-journey]").Should().BeEmpty());
        _api.Stored.Should().ContainSingle().Which.ProjectKey.Should().Be("import:other");
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
