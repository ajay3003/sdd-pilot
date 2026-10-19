using BirkNext.Integrations;
using BirkNext.SourceArchitecture;
using BirkNext.SourceDomains;
using BirkNext.Web.Components;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using BirkNext.Web.Tests.Integration;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Ownership of Target Environment → Integrations: environment-specific configuration first; Source Analysis evidence linked, never
/// re-rendered (no Wolverine package, registration, retry, outbox, telemetry, handler or route lists here); suggestions secondary; runtime
/// evidence contextual; record origin visible; Advanced collapsed.
/// </summary>
public sealed class IntegrationsOwnershipUiTests : BunitContext
{
    private readonly FakeIntegrationCatalogApi _api = new() { Catalog = M2lbFixture.Catalog() };
    private static readonly Guid Snapshot = Guid.Parse("22222222-3333-4444-5555-666666666666");

    public IntegrationsOwnershipUiTests()
    {
        Services.AddSingleton<IIntegrationCatalogApiService>(_api);
        Services.AddSingleton(new IntegrationMappingEvidenceSession());
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private IRenderedComponent<IntegrationsPane> Open() => Render<IntegrationsPane>(p => p.Add(c => c.Profile,
        new FrontendAnalysisProfile { Id = "dev", Name = "M2LB DEV", EnvironmentType = FrontendEnvironmentType.Development, TargetUrl = "https://m2lbdev.bufetat.no/" }));

    /// <summary>A source report with many unconfigured candidates (like a real M2LB snapshot) and a set of messaging evidence.</summary>
    private void UseRichSource(int candidates = 32)
    {
        _api.Messaging = new ApplicationMessagingEvidenceSet
        {
            EnvironmentId = "dev", AnalyzedAt = DateTimeOffset.UtcNow, Archives = [new SourceArchive("M2LB (2).zip", new string('a', 64), 900)],
            Applications = [new() { ApplicationId = "M2LB.Revisjon.Worker", Detection = MessagingDetection.Confirmed, HandlerMapping = MessagingFactState.Available, RetryPolicy = MessagingFactState.Configured,
                Facts = [new() { Id = "package", Label = "Wolverine package", State = MessagingFactState.Detected, Detail = "WolverineFx 6.33.0" }] }],
        };
        _api.SourceReport = catalog =>
        {
            var list = Enumerable.Range(1, candidates).Select(i => new SourceIntegrationCandidate
            {
                Id = $"c{i}", SourceSnapshotId = Snapshot, ChannelName = $"topic.{i}", SourceEntity = $"Entity{i}", Transport = IntegrationTransport.ServiceBusTopic,
                Pattern = IntegrationPattern.Messaging, ConsumerCandidate = i % 4 == 0 ? null : $"Consumer{i}",
                ConsumerEvidence = i % 4 == 0 ? ArchitectureEvidenceState.Unresolved : i % 3 == 0 ? ArchitectureEvidenceState.StronglySupported : ArchitectureEvidenceState.Inferred,
            }).ToList();
            return new SourceIntegrationsReport
            {
                EnvironmentId = "dev", SourceAnalysis = SourceDiscoveryStatus.Partial, GeneratedAt = DateTimeOffset.UtcNow,
                Discovery = new() { SourceSnapshotId = Snapshot, SourceFingerprint = new string('f', 64), ArchiveName = "M2LB (2).zip", AnalyzedAt = new DateTimeOffset(2026, 10, 1, 7, 30, 0, TimeSpan.Zero),
                    Status = SourceDiscoveryStatus.Partial, Candidates = list },
                UnconfiguredCandidateIds = [.. list.Select(c => c.Id)],
            };
        };
    }

    [Fact]
    public void TheDefaultPage_IsConfigurationFirst_AndShort()
    {
        UseRichSource();
        var cut = Open();
        cut.FindAll("h3").Select(h => h.TextContent.Trim()).Should().Equal("Configured integrations", "Suggestions from Source Analysis", "Source evidence", "Advanced");
        cut.Find("[data-testid=ip-summary-env]").TextContent.Should().Be("Development · m2lbdev.bufetat.no");
        cut.Find("[data-testid=ip-summary-suggestions]").TextContent.Should().Be("32");
        cut.FindAll("[data-testid=ip-suggestion]").Should().BeEmpty("32 candidate rows are never listed until requested");
        cut.FindAll("[data-testid=ip-advanced-body]").Should().BeEmpty("Advanced is collapsed by default");
        cut.FindAll("[data-testid=ip-platform]").Should().BeEmpty();
    }

    [Theory]
    [InlineData("Wolverine package")]
    [InlineData("Retry policy")]
    [InlineData("Outbox")]
    [InlineData("Failure disposition")]
    [InlineData("Telemetry")]
    [InlineData("Handler evidence")]
    [InlineData("Applications analyzed")]
    [InlineData("Rebuild messaging evidence")]
    [InlineData("ffffffffffff")]
    public void SourceAnalysisDetail_IsNotDuplicatedInIntegrations(string detail)
    {
        UseRichSource();
        var cut = Open();
        cut.Find("[data-testid=ip-row-view]").Click();
        cut.Markup.Should().NotContain(detail, "this is Source Analysis evidence; Integrations links to it");
        cut.FindAll("[data-testid=am-app], [data-testid=am-detail], [data-testid=am-overview], [data-testid=ip-source-detail]").Should().BeEmpty();
    }

    [Fact]
    public void TheSourceEvidenceSummary_NamesTheSnapshot_AndLinksToSourceAnalysis()
    {
        UseRichSource();
        var cut = Open();
        var card = cut.Find("[data-testid=ip-source-evidence]");
        card.TextContent.Should().Contain("M2LB (2).zip").And.Contain("Analysis: Partial").And.Contain("Updated 2026-10-01 07:30 UTC");
        cut.Find("[data-testid=ip-source-open]").GetAttribute("href").Should().Be("/source-analysis");
        cut.Find("[data-testid=ip-open-messaging-evidence]").GetAttribute("href").Should().Be("/source-analysis/application-messaging");
    }

    [Fact]
    public void AnEmptyEnvironment_ShowsASimpleStart_WithoutTechnicalPanels()
    {
        UseRichSource(5);
        _api.Catalog = new IntegrationCatalog { EnvironmentId = "dev" };
        var cut = Open();
        cut.Find("[data-testid=ip-empty]").TextContent.Should().Contain("No integrations configured").And.Contain("5 integration candidates found from Source Analysis");
        cut.Find("[data-testid=ip-empty-review]").ClassList.Should().Contain("btn-primary", "reviewing the candidates is the next action");
        cut.Find("[data-testid=ip-empty-add]").ClassList.Should().Contain("btn-secondary");
        cut.Find("[data-testid=ip-empty-review]").Click();
        cut.FindAll("[data-testid=ip-suggestion]").Should().NotBeEmpty();
        cut.Find("[data-testid=ip-summary-runtime]").TextContent.Should().Be("Not configured");
        cut.FindAll("[data-testid=ip-table], [data-testid=ip-platform], [data-testid=ip-runtime-status]").Should().BeEmpty("no giant empty tables or runtime panels before configuration exists");
        cut.Find("[data-testid=ip-empty-add]").Click();
        cut.Find("[data-testid=ip-editor]");
    }

    [Fact]
    public void EveryRow_ShowsWhereItCameFrom()
    {
        var topics = M2lbFixture.Catalog().Integrations;
        _api.Catalog = M2lbFixture.Catalog() with
        {
            Integrations =
            [
                topics[0] with { Origin = IntegrationRecordOrigin.Seed, UserModified = false },
                topics[1] with { Origin = IntegrationRecordOrigin.Seed, UserModified = true },
                topics[2] with { Origin = IntegrationRecordOrigin.Manual },
                topics[3] with { Origin = IntegrationRecordOrigin.ImportedFromBrowserProfile },
                topics[4] with { Origin = IntegrationRecordOrigin.SourceSuggestion },
            ],
        };
        var cut = Open();
        cut.FindAll("[data-testid=ip-row-origin]").Select(o => o.TextContent.Trim()).Should().BeEquivalentTo(["Seed", "Seed · modified", "Manual", "Imported", "Source suggestion"]);
    }

    private static ConfiguredSourceComparison Compared(string field, string configured, SourceComparisonState state, params string[] declared) =>
        new(M2lbFixture.PlatformId, "M2LB DEV Event Hubs", null, new SourceInfrastructureComparison
        {
            Field = field, ConfiguredValue = configured, State = state, Kind = InfrastructureResourceKind.MessagingNamespace,
            Candidates = [.. declared.Select(d => new SourceResourceCandidate("r-" + d, d, InfrastructureResourceKind.MessagingNamespace, "azurerm_eventhub_namespace", null, "tfvars", "infra/dev.tfvars", 3, ArchitectureEvidenceState.Confirmed))],
        });

    [Fact]
    public void WithoutTerraform_SeededValuesAreLabelledSeededAssumptions_NeverVerified()
    {
        UseRichSource();
        _api.Terraform = _ => new IntegrationTerraformComparison();
        var cut = Open();
        cut.Find("[data-testid=ip-terraform]").GetAttribute("data-state").Should().Be("absent");
        cut.Find("[data-testid=ip-terraform-status]").TextContent.Should().Contain("Not provided").And.Contain("not observed from source").And.Contain("seeded assumptions, not verified");
        cut.Find("[data-testid=ip-suggestions-total]").TextContent.Should().Be("32", "source suggestions still show normally without Terraform");
        cut.Find("[data-testid=ip-advanced-toggle]").Click();
        cut.FindAll("[data-testid=ip-platform-terraform]").Select(p => p.TextContent).Should().OnlyContain(t => t == "Seeded assumption");
        cut.Markup.Should().NotContain("Verified");
    }

    [Fact]
    public void WithTerraform_SeededValuesAreCompared_AndADifferenceNeedsReview_WithoutOverwriting()
    {
        UseRichSource();
        _api.Terraform = _ => new IntegrationTerraformComparison
        {
            TerraformAvailable = true, ArchiveName = "m2lb-infra.zip",
            Values = [Compared("Event Hubs namespace", "evhns-m2lb-dev-nwe-001", SourceComparisonState.Matches, "evhns-m2lb-dev-nwe-001"),
                      Compared("Event Hub", "m2lb-cdc-dev.dbo.Person", SourceComparisonState.Differs, "m2lb-cdc-dev.dbo.person-v2"),
                      Compared("Application Insights", "appi-x", SourceComparisonState.NoSourceDeclaration)],
        };
        var before = _api.Catalog;
        var cut = Open();
        cut.Find("[data-testid=ip-terraform]").GetAttribute("data-state").Should().Be("review");
        cut.Find("[data-testid=ip-terraform-status]").TextContent.Should().Contain("m2lb-infra.zip").And.Contain("1 of 2 compared values match source").And.Contain("1 needs review")
            .And.Contain("neither value is changed automatically");
        cut.Find("[data-testid=ip-terraform-review]").TextContent.Should().Contain("Event Hub").And.Contain("m2lb-cdc-dev.dbo.Person").And.Contain("m2lb-cdc-dev.dbo.person-v2").And.Contain("Source differs — needs review");
        cut.Find("[data-testid=ip-advanced-toggle]").Click();
        cut.Find("[data-testid=ip-platform-terraform]").TextContent.Should().Be("Terraform differs · Needs review");
        _api.Catalog.Should().BeSameAs(before, "a comparison never writes a configured value");
        _api.Created.Should().BeEmpty();
    }

    [Fact]
    public void WithTerraformThatAgrees_ThePlatformIsSupported_NotVerified()
    {
        _api.Terraform = _ => new IntegrationTerraformComparison { TerraformAvailable = true, ArchiveName = "infra.zip", Values = [Compared("Event Hubs namespace", "evhns-m2lb-dev-nwe-001", SourceComparisonState.Matches, "evhns-m2lb-dev-nwe-001")] };
        var cut = Open();
        cut.Find("[data-testid=ip-terraform]").GetAttribute("data-state").Should().Be("available");
        cut.FindAll("[data-testid=ip-terraform-review]").Should().BeEmpty();
        cut.Find("[data-testid=ip-advanced-toggle]").Click();
        cut.Find("[data-testid=ip-platform-terraform]").TextContent.Should().Be("Supported by Terraform");
    }

    [Fact]
    public void AConfiguredMappingThatDiffersFromSource_IsCountedAsNeedsReview()
    {
        var first = _api.Catalog.Integrations[0];
        _api.SourceReport = _ => new SourceIntegrationsReport
        {
            EnvironmentId = "dev", SourceAnalysis = SourceDiscoveryStatus.Complete,
            Matches = [new() { IntegrationId = first.Id, State = SourceMatchState.Conflict, SourceConsumer = "Other.Adapter", Detail = "Configured consumer differs from the source's consumer candidate Other.Adapter. Needs review — the configured mapping is kept." }],
        };
        var cut = Open();
        cut.Find("[data-testid=ip-source-conflicts]").TextContent.Should().Be("1 configured mapping differs from source · Needs review");
        cut.FindAll("[data-testid=ip-row-source-note]").Select(n => n.TextContent).Should().Contain("Mapping conflict · Needs review");
    }

    [Fact]
    public void AdvancedHoldsPlatformsMessagingScopeAndDiagnostics_AndIsKeyboardOperable()
    {
        UseRichSource();
        var cut = Open();
        var toggle = cut.Find("[data-testid=ip-advanced-toggle]");
        toggle.TagName.Should().Be("BUTTON");
        toggle.GetAttribute("aria-expanded").Should().Be("false");
        toggle.Click();
        cut.Find("[data-testid=ip-advanced-toggle]").GetAttribute("aria-expanded").Should().Be("true");
        var body = cut.Find("[data-testid=ip-advanced-body]");
        body.TextContent.Should().Contain("Platforms and runtime evidence setup");
        cut.FindAll("#ip-advanced-body h4").Select(h => h.TextContent).Should().StartWith(["Runtime"]).And.Contain(["Messaging", "Diagnostics"]);
        cut.Find("[data-testid=ip-messaging-semantics]").TextContent.Should().Contain("Configured (in this catalog) ≠ discovered (in source) ≠ linked (bound to a consumer here) ≠ observed (at runtime)");
        cut.Find("[data-testid=am]").GetAttribute("data-view").Should().Be("Configuration");
        cut.FindAll("[data-testid=ip-platform-origin]").Select(o => o.TextContent).Should().OnlyContain(o => o == "Seed" || o == "Seed · modified");
    }
}
