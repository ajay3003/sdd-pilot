using BirkNext.AzureEnvironment;
using BirkNext.Integrations;
using BirkNext.SourceDomains;
using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// Azure Environment Analysis page: Not configured requirements, dedicated-profile / device-code sign-in, PIM guidance, scope selection,
/// capability matrix with Not authorized (never Failed), grouped resources with portal links, confirmed vs inferred topology, eligible vs
/// active roles, neutral observations, declared-vs-observed grouped by state, and Target Environment suggestions that are never saved.
/// Generic "Contoso Orders" fixture — no project-specific names.
/// </summary>
public sealed class AzureEnvironmentAnalysisPageTests : BunitContext
{
    private const string Sub = "11111111-1111-1111-1111-111111111111";
    private const string Rg = "/subscriptions/" + Sub + "/resourceGroups/rg-orders-dev";
    private const string Site = Rg + "/providers/Microsoft.Web/sites/app-orders-dev";
    private const string Hub = Rg + "/providers/Microsoft.EventHub/namespaces/evhns-orders-dev";
    private const string Insights = Rg + "/providers/Microsoft.Insights/components/appi-orders-dev";
    private readonly Mock<IAzureEnvironmentApiService> _api = new();
    private readonly Mock<IIntegrationCatalogApiService> _sources = new();
    private AzureConnectionStatus _status = new() { State = AzureConnectionState.SignedOut, DedicatedEdgeAvailable = true, DeviceCodeAllowed = true };
    private List<AzureEnvironmentSnapshotSummary> _summaries = [];

    public AzureEnvironmentAnalysisPageTests()
    {
        _api.Setup(a => a.StatusAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => _status);
        _api.Setup(a => a.SnapshotsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(() => _summaries);
        _api.Setup(a => a.SnapshotAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(Snapshot());
        _api.Setup(a => a.SubscriptionsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new AzureSubscriptionsResult(
            [new(Sub, "Contoso Orders Dev", "Enabled", "t")], new(AzureCapabilityArea.Subscriptions, AzureCapabilityState.Available, "1 visible", "GET /subscriptions"), null));
        var context = new Mock<IFrontendAnalysisContextFactory>();
        context.Setup(c => c.GetActiveContextAsync()).ReturnsAsync(new FrontendAnalysisContext { ActiveProfile = new FrontendAnalysisProfile { Id = "orders-dev", Name = "Orders DEV" } });
        Services.AddSingleton(_api.Object);
        Services.AddSingleton(_sources.Object);
        Services.AddSingleton(context.Object);
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private static AzureEnvironmentSnapshot Snapshot() => new()
    {
        Id = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"), EnvironmentId = "orders-dev", CapturedAt = DateTimeOffset.Parse("2026-10-02T09:00:00Z"), TenantId = "tenant-1",
        Status = AzureAnalysisStatus.Partial, Scope = new([Sub], [], SourceEnvironments.Normalize("dev")), Subscriptions = [new(Sub, "Contoso Orders Dev", "Enabled", "tenant-1")],
        Resources =
        [
            new() { Id = Site, Name = "app-orders-dev", Type = "microsoft.web/sites", Category = InfrastructureCategory.Compute, CategoryDetail = "App Service app", ResourceKind = InfrastructureResourceKind.ComputeApp,
                ResourceGroup = "rg-orders-dev", Location = "norwayeast", Properties = [new("httpsOnly", "true", "Security"), new("serverFarmId", "/x", "Reference")], Tags = [new("owner-token", "[value not shown]")] },
            new() { Id = Hub, Name = "evhns-orders-dev", Type = "microsoft.eventhub/namespaces", Category = InfrastructureCategory.Messaging, CategoryDetail = "Event Hubs namespace", ResourceKind = InfrastructureResourceKind.EventHubNamespace, ResourceGroup = "rg-orders-dev" },
            new() { Id = Insights, Name = "appi-orders-dev", Type = "microsoft.insights/components", Category = InfrastructureCategory.Observability, CategoryDetail = "Application Insights", ResourceKind = InfrastructureResourceKind.TelemetryComponent, ResourceGroup = "rg-orders-dev" },
        ],
        Relationships =
        [
            new(Insights, Site, ObservedRelationshipKind.LikelyTelemetryFor, ObservedRelationshipConfidence.Inferred, "names share \"orders-dev\"; not confirmed"),
            new(Site, Rg + "/providers/Microsoft.Web/serverfarms/plan-orders-dev", ObservedRelationshipKind.HostedOn, ObservedRelationshipConfidence.Confirmed, "serverFarmId", TargetInScope: false),
        ],
        Capabilities =
        [
            new(AzureCapabilityArea.ResourceGraph, AzureCapabilityState.Available, "3 resource(s).", "POST Microsoft.ResourceGraph/resources (predefined query)"),
            new(AzureCapabilityArea.RoleAssignments, AzureCapabilityState.NotAuthorized, "Role assignments could not be read for every subscription.", "GET Microsoft.Authorization/roleAssignments"),
        ],
        Access = new()
        {
            Permissions = [new(Sub, true, true, ["*"], AzureEnvironmentText.ReadOnlyWithWriteAccess)],
            SignedInRoles = [new("/subscriptions/" + Sub, "Owner", PimRoleStatus.Eligible, "Group", null), new("/subscriptions/" + Sub, "Reader", PimRoleStatus.Active, "Group", null)],
        },
        Observations = [new(ObservationArea.Network, Hub, "evhns-orders-dev", "publicNetworkAccess", "Public network access is enabled.", true)],
        Limitations = ["App settings, connection strings, Key Vault secrets/keys/certificates, blob contents and messages are never read."],
    };

    private IRenderedComponent<AzureEnvironmentAnalysis> Page() => Render<AzureEnvironmentAnalysis>();

    [Fact]
    public void Not_configured_shows_the_app_registration_requirements_and_no_sign_in()
    {
        _status = new() { State = AzureConnectionState.NotConfigured, Requirements = ["Set AzureEnvironment:ClientId to a public client.", "No client secret is used."], Message = "Not enabled." };
        var page = Page();
        page.Find("[data-testid=az-connection-state]").TextContent.Should().Be("Not configured");
        page.FindAll("[data-testid=az-requirements] li").Should().HaveCount(2);
        page.FindAll("[data-testid=az-sign-in-edge]").Should().BeEmpty();
        page.Find("[data-testid=az-boundary]").TextContent.Should().Contain("Nothing was created, changed or deleted");
        page.Find("[data-testid=az-no-snapshots]").TextContent.Should().Contain("Sign in");
    }

    [Fact]
    public void Signed_out_offers_the_dedicated_profile_and_device_code_with_PIM_guidance()
    {
        var page = Page();
        page.Find("[data-testid=az-sign-in-edge]").TextContent.Should().Contain("dedicated Edge profile");
        page.Find("[data-testid=az-sign-in-device]");
        page.Find("[data-testid=az-pim-guidance]").TextContent.Should().Contain("never activates PIM roles").And.Contain("never infers permissions from group names");
        page.FindAll("[data-testid=az-scope]").Should().BeEmpty("subscriptions are read only after sign-in");

        _status = _status with { State = AzureConnectionState.AwaitingDeviceCode, Method = AzureSignInMethod.DeviceCode, DeviceCode = new("ABCD-1234", "https://microsoft.com/devicelogin", DateTimeOffset.UtcNow.AddMinutes(10)) };
        _api.Setup(a => a.SignInDeviceCodeAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => _status);
        page.Find("[data-testid=az-sign-in-device]").Click();
        page.WaitForAssertion(() => page.Find("[data-testid=az-device-code]").TextContent.Should().Contain("ABCD-1234"));
        page.FindAll("[data-testid=az-sign-in-edge]").Should().BeEmpty("no second sign-in while one is pending");
    }

    [Fact]
    public void Signed_in_selects_subscriptions_and_analyzes_with_the_stated_scope()
    {
        _status = new() { State = AzureConnectionState.SignedIn, Method = AzureSignInMethod.DedicatedEdgeProfile, Account = "person@contoso.example", TenantId = "tenant-1", DedicatedEdgeAvailable = true };
        AzureAnalysisRequest? sent = null;
        _api.Setup(a => a.AnalyzeAsync(It.IsAny<AzureAnalysisRequest>(), It.IsAny<CancellationToken>())).Callback<AzureAnalysisRequest, CancellationToken>((r, _) => sent = r).ReturnsAsync((Snapshot(), (string?)null));
        var page = Page();
        page.Find("[data-testid=az-account]").TextContent.Should().Be("person@contoso.example");
        page.Find("[data-testid=az-refresh]");
        page.Find("[data-testid=az-analyze]").HasAttribute("disabled").Should().BeTrue();
        page.Find($"[data-testid=az-sub-{Sub}]").Change(true);
        page.Find("[data-testid=az-groups]").Change("rg-orders-dev, rg-shared");
        page.Find("[data-testid=az-environment]").Change("dev");
        page.Find("[data-testid=az-analyze]").Click();
        page.WaitForAssertion(() => page.Find("[data-testid=az-status]").TextContent.Should().Contain("Partial"));
        sent.Should().BeEquivalentTo(new AzureAnalysisRequest { EnvironmentId = "orders-dev", SubscriptionIds = [Sub], ResourceGroups = ["rg-orders-dev", "rg-shared"], EnvironmentLabel = "dev" });
    }

    [Fact]
    public void The_overview_shows_the_capability_matrix_with_not_authorized_and_limitations()
    {
        _summaries = [new(Snapshot().Id, "orders-dev", Snapshot().CapturedAt, AzureAnalysisStatus.Partial, ["Contoso Orders Dev"], 3, 2, "dev")];
        var page = Page();
        page.Find("[data-testid=az-provenance]").TextContent.Should().Contain("2026-10-02 09:00 UTC").And.Contain("represents dev");
        var rows = page.FindAll("[data-testid=az-capability-row]");
        rows.Should().HaveCount(2);
        rows[1].TextContent.Should().Contain("Not authorized").And.NotContain("Failed");
        page.Find("[data-testid=az-limitations]").TextContent.Should().Contain("never read");
    }

    [Fact]
    public void Resources_group_by_category_with_safe_details_and_portal_links()
    {
        _summaries = [new(Snapshot().Id, "orders-dev", Snapshot().CapturedAt, AzureAnalysisStatus.Partial, ["Contoso Orders Dev"], 3, 2, "dev")];
        var page = Page();
        page.Find("[data-testid=az-tab-resources]").Click();
        page.Find("[data-testid=az-resources-Compute]").TextContent.Should().Contain("app-orders-dev").And.Contain("App Service app");
        page.Find("[data-testid=az-resources-Messaging]");
        page.FindAll("[data-testid=az-resource-row] button")[0].Click();
        var detail = page.Find("[data-testid=az-resource-detail]");
        detail.TextContent.Should().Contain("httpsOnly").And.Contain("[value not shown]").And.NotContain("serverFarmId");
        page.Find("[data-testid=az-portal-link]").GetAttribute("href").Should().Be($"https://portal.azure.com/#@tenant-1/resource{Site}");
        page.Find("[data-testid=az-filter]").Input("evhns");
        page.FindAll("[data-testid=az-resources-Compute]").Should().BeEmpty();
    }

    [Fact]
    public void Topology_access_and_observations_keep_confirmed_inferred_eligible_and_active_apart()
    {
        _summaries = [new(Snapshot().Id, "orders-dev", Snapshot().CapturedAt, AzureAnalysisStatus.Partial, ["Contoso Orders Dev"], 3, 2, "dev")];
        var page = Page();
        page.Find("[data-testid=az-tab-topology]").Click();
        page.Find($"[data-testid=az-rel-{ObservedRelationshipKind.LikelyTelemetryFor}]").TextContent.Should().Contain("Inferred").And.Contain("not confirmed");
        page.Find($"[data-testid=az-rel-{ObservedRelationshipKind.HostedOn}]").TextContent.Should().Contain("outside the analyzed scope").And.Contain("Confirmed");

        page.Find("[data-testid=az-tab-access]").Click();
        page.Find("[data-testid=az-permissions]").TextContent.Should().Contain("BirkNext still only reads");
        var roles = page.Find("[data-testid=az-roles]").TextContent;
        roles.Should().Contain("Eligible — not active").And.Contain("Active");

        page.Find("[data-testid=az-tab-observations]").Click();
        page.Find($"[data-testid=az-obs-{ObservationArea.Network}]").TextContent.Should().Contain("For review").And.Contain("Public network access is enabled.");
    }

    [Fact]
    public void Declared_vs_observed_compares_a_chosen_source_snapshot_and_groups_by_state()
    {
        _summaries = [new(Snapshot().Id, "orders-dev", Snapshot().CapturedAt, AzureAnalysisStatus.Partial, ["Contoso Orders Dev"], 3, 2, "dev")];
        var sourceId = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");
        _sources.Setup(s => s.ListSourceSnapshotsAsync("orders-dev", It.IsAny<CancellationToken>())).ReturnsAsync([new IqrSourceSnapshot
        {
            Id = sourceId, Archive = new("orders.zip", "abc123", 4), AnalyzedAt = DateTimeOffset.Parse("2026-10-01T09:00:00Z"),
            EvidenceDomains = new SourceEvidenceDomainsSnapshot { Infrastructure = new InfrastructureEvidence { Resources = [new InfrastructureResource { Id = "x", ResourceType = "azurerm_linux_web_app" }] } },
        }]);
        _api.Setup(a => a.ComparisonAsync("orders-dev", Snapshot().Id, sourceId, "dev", It.IsAny<CancellationToken>())).ReturnsAsync((new DeclaredObservedComparison
        {
            SourceSnapshotId = sourceId, SourceFingerprint = "abc12345ffff", InfrastructureAnalyzerVersion = 2, AzureSnapshotId = Snapshot().Id, AzureCapturedAt = Snapshot().CapturedAt,
            Environment = SourceEnvironments.Normalize("dev"), EnvironmentBasis = "stated when the Azure scope was analyzed",
            Items =
            [
                new() { State = DeclaredObservedState.ConfigurationDiffers, Name = "order-events", Kind = InfrastructureResourceKind.EventHub, DeclaredFile = "infra/main.tf", DeclaredLine = 14,
                    Differences = [new("partition_count", "4", "2")], Reason = "1 declared setting(s) differ from what Azure reports." },
                new() { State = DeclaredObservedState.ObservedOnly, Name = "plan-orders-dev", Reason = "Observed in Azure; no matching declaration in this source snapshot (it may be managed elsewhere)." },
            ],
            Counts = new() { [DeclaredObservedState.ConfigurationDiffers] = 1, [DeclaredObservedState.ObservedOnly] = 1 },
        }, (string?)null));
        var page = Page();
        page.Find("[data-testid=az-tab-declared]").Click();
        page.WaitForAssertion(() => page.Find("[data-testid=az-declared-source]"));
        page.Find("[data-testid=az-compare]").Click();
        page.WaitForAssertion(() => page.Find("[data-testid=az-compare-provenance]").TextContent.Should().Contain("abc12345").And.Contain("environment dev"));
        var differs = page.Find($"[data-testid=az-compare-{DeclaredObservedState.ConfigurationDiffers}]");
        differs.HasAttribute("open").Should().BeTrue();
        differs.TextContent.Should().Contain("partition_count").And.Contain("declared 4").And.Contain("observed 2");
        page.Find($"[data-testid=az-compare-{DeclaredObservedState.ObservedOnly}]").HasAttribute("open").Should().BeFalse();
    }

    [Fact]
    public void Target_Environment_suggestions_are_copy_only()
    {
        _summaries = [new(Snapshot().Id, "orders-dev", Snapshot().CapturedAt, AzureAnalysisStatus.Partial, ["Contoso Orders Dev"], 3, 2, "dev")];
        _api.Setup(a => a.TargetSuggestionsAsync("orders-dev", Snapshot().Id, It.IsAny<CancellationToken>())).ReturnsAsync(
            [new AzureTargetSuggestion("Application URL", "https://app-orders-dev.azurewebsites.net", Site, "app-orders-dev", "Observed … Not saved: copy it into the Target Environment yourself if it is right.")]);
        var page = Page();
        page.Find("[data-testid=az-tab-suggestions]").Click();
        page.WaitForAssertion(() => page.Find("[data-testid=az-suggestions-table]").TextContent.Should().Contain("https://app-orders-dev.azurewebsites.net"));
        page.Find("[data-testid=az-suggestions]").TextContent.Should().Contain("BirkNext does not save them");
        page.FindAll("[data-testid=az-suggestions] button").Should().BeEmpty("there is no save or apply action");
    }
}
