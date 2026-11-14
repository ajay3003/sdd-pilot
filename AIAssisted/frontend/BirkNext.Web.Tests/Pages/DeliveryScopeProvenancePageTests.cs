using BirkNext.DeliveryReporting;
using DeliveryReportingPage = BirkNext.Web.Pages.DeliveryReporting;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using static BirkNext.Web.Tests.Pages.DeliveryReportingPageTests;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// Delivery Reports and shared scope provenance: a generated report records the shared scope selection (exact revision ids) made in the Functional
/// Quality Scope view of this browser tab; a saved report shows its frozen provenance, and one generated before scope support says it was not recorded.
/// </summary>
public sealed class DeliveryScopeProvenancePageTests : BunitContext
{
    private static readonly Guid ScopeA = Guid.Parse("aaaaaaaa-0000-0000-0000-00000000000a");
    private static readonly Guid RevisionA3 = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000003");
    private readonly FakeApi _api = new();
    private readonly DeliveryReportingSession _session = new();

    public DeliveryScopeProvenancePageTests()
    {
        Services.AddSingleton<IDeliveryReportingApiService>(_api);
        Services.AddSingleton<IDeliveryAzureDevOpsApiService>(new BirkNext.Web.Tests.Components.DeliveryAzureDevOpsImportTests.FakeApi());
        Services.AddSingleton(new BirkNext.Web.Services.Operations.BackgroundOperationService());
        Services.AddSingleton(_session);
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private static DeliveryScopeProvenance Provenance() => new()
    {
        Scopes = [new(ScopeA, "Release 1", 3, RevisionA3)], MembershipResolution = DeliveryScopeProvenance.DefaultMembershipResolution,
        CreatedAt = new(2026, 10, 30, 16, 0, 0, TimeSpan.Zero),
    };

    private IRenderedComponent<DeliveryReportingPage> RenderAt(string query)
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo($"delivery-reporting{query}");
        return Render<DeliveryReportingPage>();
    }

    [Fact]
    public void A_report_generated_before_scope_support_says_its_scope_provenance_was_not_recorded()
    {
        var page = RenderAt($"?project={ProjectId}");

        page.WaitForAssertion(() => page.Find("[data-testid=dlr-scope-provenance]"));
        var line = page.Find("[data-testid=dlr-scope-provenance]");
        line.TextContent.Trim().Should().Be("Scope provenance not recorded (created before scope support)");
        line.GetAttribute("data-recorded").Should().Be("false");
    }

    [Fact]
    public void A_new_report_without_a_selection_says_no_shared_scope_was_selected_never_created_before_scope_support()
    {
        _api.Report = SampleReport() with { ScopeProvenance = DeliveryScopeProvenanceText.WholePopulation(new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero)) };
        var page = RenderAt($"?project={ProjectId}");

        page.WaitForAssertion(() => page.Find("[data-testid=dlr-scope-provenance]"));
        page.Find("[data-testid=dlr-scope-provenance]").TextContent.Trim().Should().Be(DeliveryScopeProvenanceText.NoSelection);
    }

    [Fact]
    public void A_report_with_frozen_scope_provenance_shows_the_exact_scope_revision()
    {
        _api.Report = SampleReport() with { ScopeProvenance = Provenance() };
        var page = RenderAt($"?project={ProjectId}");

        page.WaitForAssertion(() => page.Find("[data-testid=dlr-scope-provenance]").GetAttribute("data-recorded").Should().Be("true"));
        page.Find("[data-testid=dlr-scope-provenance]").TextContent.Should().Contain("Release 1 rev 3").And.Contain("approved revisions; canonical key match type+module+feature+id");
    }

    [Fact]
    public void Generating_a_report_sends_the_shared_scope_selection_with_its_revision_ids()
    {
        _session.SetScopeProvenance(ProjectId, Provenance());
        var page = RenderAt($"?project={ProjectId}&tab=overview");

        page.WaitForAssertion(() => page.Find("[data-testid=dlr-generate-scope-provenance]").TextContent.Should().Contain("Release 1 rev 3"));
        page.Find("[data-testid=dlr-generate-run]").Click();
        page.WaitForAssertion(() => _api.LastGenerate.Should().NotBeNull());
        _api.LastGenerate!.ScopeProvenance!.Scopes.Should().ContainSingle().Which.Should().Be(new DeliveryScopeProvenanceEntry(ScopeA, "Release 1", 3, RevisionA3));
    }

    [Fact]
    public void Without_a_shared_scope_selection_no_provenance_is_sent()
    {
        var page = RenderAt($"?project={ProjectId}&tab=overview");

        page.WaitForAssertion(() => page.Find("[data-testid=dlr-generate-run]"));
        page.FindAll("[data-testid=dlr-generate-scope-provenance]").Should().BeEmpty();
        page.Find("[data-testid=dlr-generate-run]").Click();
        page.WaitForAssertion(() => _api.LastGenerate.Should().NotBeNull());
        _api.LastGenerate!.ScopeProvenance.Should().BeNull();
    }
}
