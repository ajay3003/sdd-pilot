using BirkNext.Integrations;
using BirkNext.Web.Components;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using BirkNext.Web.Tests.Integration;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Event Hub runtime evidence in Target Environment → Integrations: the verified M2LB DEV defaults, <c>$Default</c> as a configured
/// assumption, checkpoint endpoint + container, Application Insights without a Log Analytics workspace, and configured sources kept
/// apart from Azure runtime execution.
/// </summary>
public sealed class EventHubRuntimeEvidenceUiTests : BunitContext
{
    private readonly FakeIntegrationCatalogApi _api = new() { Catalog = M2lbFixture.SeededCatalog() };
    private readonly FrontendAnalysisProfile _profile = new() { Id = "dev", Name = "M2LB DEV", EnvironmentType = FrontendEnvironmentType.Development, TargetUrl = "https://m2lbdev.bufetat.no/" };

    public EventHubRuntimeEvidenceUiTests()
    {
        Services.AddSingleton<IIntegrationCatalogApiService>(_api);
        Services.AddSingleton(new IntegrationMappingEvidenceSession());
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private IRenderedComponent<IntegrationsPane> Open() => Render<IntegrationsPane>(p => p.Add(c => c.Profile, _profile));

    private static IRenderedComponent<IntegrationsPane> Expand(IRenderedComponent<IntegrationsPane> cut)
    {
        cut.Find(".disclosure[data-testid^=ip-runtime-] > button").Click();
        return cut;
    }

    private static AngleSharp.Dom.IElement Row(IRenderedComponent<IntegrationsPane> cut, string key) => cut.Find($"[data-testid=ip-runtime-summary] [data-testid=ip-runtime-row-{key}]");

    [Fact]
    public void SeededDefaultsAreShownAsConfiguredSourcesWithTheirProvenance()
    {
        var cut = Expand(Open());
        Row(cut, "groups").TextContent.Should().Contain("m2lb-samhandling-dev (2fcdb9d0-22eb-43b0-b95e-7cbba08c34b0)").And.Contain("rg-m2lb-dev-integration-nwe");
        Row(cut, "metadata").TextContent.Should().Contain("evhns-m2lb-dev-nwe-001.servicebus.windows.net");
        Row(cut, "checkpoint").TextContent.Should().Contain("https://stm2bbirkdevnwe001.blob.core.windows.net/person-adapter")
            .And.Contain("Source/runtime configuration verified").And.Contain("EventHub__FQDN, Storage__BlobEndpoint and Storage__ContainerName");
        Row(cut, "checkpoint").TextContent.Should().NotContain("Observed", "the checkpoint location is configuration, not an observed checkpoint");
        Row(cut, "appinsights").TextContent.Should().Contain("appi-m2lb-dev-nwe-001 · rg-m2lb-dev-shared-nwe").And.Contain("configured on the consumer: yes").And.Contain("never read or stored");
        Row(cut, "policy").TextContent.Should().Contain("24 h window").And.Contain("no lag threshold").And.Contain("Observed only — never Pass");
        cut.Find("[data-testid=ip-runtime-sources-status]").TextContent.Should().Be("Configured");
        cut.Find("[data-testid=ip-runtime-summary] ~ button, [data-testid=ip-runtime-edit]").Should().NotBeNull();
    }

    [Fact]
    public void DefaultIsAConfiguredAssumptionAndTheMappingNeedsConfirmation()
    {
        var cut = Expand(Open());
        var row = Row(cut, "expected-group");
        row.GetAttribute("data-status").Should().Be(nameof(RuntimeSourceStatus.Assumption));
        row.TextContent.Should().Contain("$Default — Configured assumption · Mapping: Needs confirmation").And.Contain("barntype");
        row.TextContent.Should().NotContain("Confirmed mapping");
        cut.Markup.Should().NotContain("Confirmed mapping");
    }

    [Fact]
    public void AMissingLogAnalyticsWorkspaceIsNotRequired_NeverAWarning()
    {
        var cut = Expand(Open());
        var row = Row(cut, "workspace");
        row.GetAttribute("data-status").Should().Be(nameof(RuntimeSourceStatus.NotRequired));
        row.TextContent.Should().Contain("Not configured — not required").And.Contain("Azure Monitor");
        row.QuerySelector(".ehr-badge")!.ClassList.Should().Contain("ehr-badge-muted").And.NotContain("ehr-badge-attention");
        cut.Find("[data-testid=ip-runtime-sources-status]").TextContent.Should().Be("Configured", "telemetry is configured through Application Insights");
    }

    [Fact]
    public void SourcesConfiguredIsKeptApartFromAzureExecution()
    {
        var cut = Expand(Open());
        cut.Find("[data-testid=ip-summary-runtime]").TextContent.Should().Be("Configured");
        cut.Find("[data-testid=ip-summary-azure]").TextContent.Should().Be("Azure runtime execution: Not configured");
        cut.Find("[data-testid=ip-runtime-execution-status]").TextContent.Should().Be("Azure runtime execution: Not configured");
        Row(cut, "azure").TextContent.Should().Contain("Not configured").And.Contain("IntegrationReview:Azure:Enabled is not true");

        _api.Catalog = M2lbFixture.SeededCatalog(azureEnabled: true);
        var enabled = Expand(Open());
        enabled.Find("[data-testid=ip-summary-azure]").TextContent.Should().Be("Azure runtime execution: Enabled");
        enabled.Find("[data-testid=ip-runtime-summary] [data-testid=ip-runtime-row-azure]").GetAttribute("data-status").Should().Be(nameof(RuntimeSourceStatus.Configured));
    }

    [Fact]
    public void TheFormHasFiveSectionsIdentityTextAndPrefilledValues()
    {
        var cut = Expand(Open());
        cut.Find("[data-testid=ip-runtime-edit]").Click();
        cut.FindAll("[data-testid=ip-runtime-form] fieldset legend").Select(l => l.TextContent).Should().Equal(
            "A · Azure runtime access", "B · Event Hub and consumer evidence", "C · Checkpoint evidence", "D · Monitoring and telemetry", "E · Review policy");
        cut.Find("[data-testid=ip-runtime-identity]").TextContent.Should().Be("BirkNext uses its Azure identity for read-only metadata access. No key or connection string is stored here.");
        cut.Find("[data-testid=ip-runtime-subscription]").GetAttribute("value").Should().Be("2fcdb9d0-22eb-43b0-b95e-7cbba08c34b0");
        cut.Find("[data-testid=ip-runtime-subscription-name]").GetAttribute("value").Should().Be("m2lb-samhandling-dev");
        cut.Find("[data-testid=ip-runtime-expected-group]").GetAttribute("value").Should().Be("$Default");
        cut.Find("[data-testid=ip-runtime-blob-endpoint]").GetAttribute("value").Should().Be("https://stm2bbirkdevnwe001.blob.core.windows.net/");
        cut.Find("[data-testid=ip-runtime-container]").GetAttribute("value").Should().Be("person-adapter");
        cut.Find("[data-testid=ip-runtime-checkpoint-derived]").TextContent.Should().Contain("https://stm2bbirkdevnwe001.blob.core.windows.net/person-adapter");
        cut.Find("[data-testid=ip-runtime-appinsights]").GetAttribute("value").Should().Be("appi-m2lb-dev-nwe-001");
        cut.Find("[data-testid=ip-runtime-workspace]").GetAttribute("value").Should().BeNullOrEmpty();
        cut.Find("[data-testid=ip-runtime-section-a]").TextContent.Should().Contain("Norway East · Premium, 1 PU");
        cut.Find("[data-testid=ip-runtime-execution]").TextContent.Should().Be("Not configured");
        cut.Find("[data-testid=ip-runtime-readiness]").TextContent.Should().Contain("Not configured — not required");
        foreach (var field in cut.FindAll("[data-testid=ip-runtime-form] input"))
            field.Closest("label").Should().NotBeNull("every field has a visible label");
    }

    [Fact]
    public void SavingWithoutAWorkspaceKeepsEveryValueAndShowsAPostSaveSummary()
    {
        var cut = Expand(Open());
        cut.Find("[data-testid=ip-runtime-edit]").Click();
        cut.Find("[data-testid=ip-runtime-save]").Click();
        cut.WaitForAssertion(() => _api.SavedPlatforms.Should().ContainSingle());
        var saved = _api.SavedPlatforms.Single().RuntimeEvidence!;
        saved.Should().BeEquivalentTo(M2lbFixture.SeededRuntime(), "an unchanged save keeps every value");
        saved.TelemetryWorkspaceId.Should().BeNull("no workspace is required");
        cut.Find("[data-testid=ip-runtime-saved]").TextContent.Should()
            .Contain("4 of 4 runtime evidence sources configured").And.Contain("Azure runtime execution: Not configured");
        cut.FindAll("[data-testid=ip-runtime-form]").Should().BeEmpty();
    }

    [Theory]
    [InlineData("ip-runtime-blob-endpoint", "https://stm2bbirkdevnwe001.blob.core.windows.net/?sv=2024-01-01&sig=abc")]
    [InlineData("ip-runtime-blob-endpoint", "https://user:pw@stm2bbirkdevnwe001.blob.core.windows.net/")]
    [InlineData("ip-runtime-blob-endpoint", "https://stm2bbirkdevnwe001.blob.core.windows.net/?token=abc")]
    [InlineData("ip-runtime-appinsights", "InstrumentationKey=00000000-0000-0000-0000-000000000000;IngestionEndpoint=https://x")]
    [InlineData("ip-runtime-appinsights-rg", "DefaultEndpointsProtocol=https;AccountName=a;AccountKey=abc==")]
    [InlineData("ip-runtime-subscription-name", "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.sig")]
    [InlineData("ip-runtime-expected-group", "Endpoint=sb://x.servicebus.windows.net/;SharedAccessKeyName=k;SharedAccessKey=v")]
    public void ACredentialIsRejectedAndNothingIsStored(string field, string value)
    {
        var cut = Expand(Open());
        cut.Find("[data-testid=ip-runtime-edit]").Click();
        cut.Find($"[data-testid={field}]").Change(value);
        cut.Find("[data-testid=ip-runtime-save]").Click();
        cut.Find("[data-testid=ip-runtime-error]").GetAttribute("role").Should().Be("alert");
        _api.SavedPlatforms.Should().BeEmpty();
        cut.Find("[data-testid=ip-runtime-form]").Should().NotBeNull("the edits stay in the form");
    }

    [Fact]
    public void ALegacyContainerUrlIsShownAsEndpointAndContainer()
    {
        _api.Catalog = M2lbFixture.Catalog() with
        {
            Platforms = [M2lbFixture.Platform() with { RuntimeEvidence = new() { CheckpointContainerUrl = "https://acct.blob.core.windows.net/checkpoints" } }],
        };
        var cut = Expand(Open());
        cut.Find("[data-testid=ip-runtime-edit]").Click();
        cut.Find("[data-testid=ip-runtime-blob-endpoint]").GetAttribute("value").Should().Be("https://acct.blob.core.windows.net/");
        cut.Find("[data-testid=ip-runtime-container]").GetAttribute("value").Should().Be("checkpoints");
    }

    [Fact]
    public void WithoutSettingsNothingIsAssumed()
    {
        _api.Catalog = M2lbFixture.Catalog();
        var cut = Expand(Open());
        Row(cut, "expected-group").TextContent.Should().Contain("Not configured").And.NotContain("$Default");
        Row(cut, "workspace").GetAttribute("data-status").Should().Be(nameof(RuntimeSourceStatus.NotRequired));
        cut.Find("[data-testid=ip-runtime-sources-status]").TextContent.Should().Be("Not configured");
    }

    [Fact]
    public void NoConnectionStringOrKeyAppearsAnywhere()
    {
        var cut = Expand(Open());
        cut.Find("[data-testid=ip-runtime-edit]").Click();
        cut.Markup.Should().NotContainAny("InstrumentationKey", "APPLICATIONINSIGHTS_CONNECTION_STRING", "AccountKey", "SharedAccessKey", "sig=");
    }
}

/// <summary>The mapping-evidence card and the review readiness wording for an assumed consumer group.</summary>
public sealed class EventHubConsumerGroupAssumptionTests : BunitContext
{
    public EventHubConsumerGroupAssumptionTests() => JSInterop.Mode = JSRuntimeMode.Loose;

    [Fact]
    public void TheMappingCardShowsExpectedVersusObservedAndNeedsConfirmation()
    {
        var cut = Render<IntegrationMappingEvidencePanel>(p => p
            .Add(c => c.Result, new IntegrationMappingEvidenceCheck
            {
                IntegrationId = "dev:eventhub:birk-cdc:dbo.Person", Topic = "m2lb-cdc-dev.BirkM2LB.dbo.Person", SuggestedConsumer = "Person Adapter",
                OverallState = IntegrationMappingEvidenceState.PartialEvidence, CompletedAt = DateTimeOffset.UtcNow,
                ExpectedConsumerGroup = "$Default", ExpectedConsumerGroupProvenance = IntegrationValueProvenance.ConfiguredAssumption,
                ObservedConsumerGroups = ["$Default"], MappingState = ConsumerMappingState.Confirmed,
            })
            .Add(c => c.ConsumerState, ConsumerMappingState.Confirmed));
        cut.Find("[data-testid=mapping-group-expected]").TextContent.Should().Be("$Default — Configured assumption");
        cut.Find("[data-testid=mapping-group-observed]").TextContent.Should().Be("$Default");
        cut.Find("[data-testid=mapping-group-mapping]").TextContent.Should().Be("Mapping: Needs confirmation", "an expected group never confirms the consumer-group mapping");
    }

    [Fact]
    public void GroupsNotReadAreNotShownAsNone()
    {
        var cut = Render<IntegrationMappingEvidencePanel>(p => p.Add(c => c.Result, new IntegrationMappingEvidenceCheck { OverallState = IntegrationMappingEvidenceState.NotTestable }));
        cut.Find("[data-testid=mapping-group-expected]").TextContent.Should().Be("Not configured");
        cut.Find("[data-testid=mapping-group-observed]").TextContent.Should().Be("Not read");
        cut.Find("[data-testid=mapping-group-mapping]").TextContent.Should().Be("Mapping: Needs confirmation");
    }

    [Fact]
    public void ReadinessWordingNamesTheAssumptionAndNoLongerDemandsAWorkspace()
    {
        var system = new IntegrationSystemScope { SystemName = "BIRK CDC / Debezium", Kind = IntegrationKind.EventHub, Topics = 16, ConsumerGroupsAssumed = 16, ExpectedConsumerGroup = "$Default" };
        IntegrationReviewPrerunPresentation.ConsumerGroups(system).Should().Be("Expected $Default for 16 topics (configured assumption · Mapping: Needs confirmation)");
        var readiness = new IntegrationReviewReadiness { CanRun = true, Headline = "Can run with limitations", Systems = [system] };
        IntegrationReviewPrerunPresentation.NeedsAttention(readiness, "dev").Single(i => i.Key == "groups").Value.Should().Be("$Default assumed for 16 topics — needs confirmation");

        var catalog = M2lbFixture.SeededCatalog();
        var person = catalog.Integrations.Single(i => i.Id.EndsWith("dbo.Person"));
        var (_, detail) = IntegrationsPanePresentation.ReviewReadiness(person, catalog.Platforms.Single());
        detail.Should().NotContain("runtime telemetry", "Application Insights is configured by resource; no workspace is required")
            .And.NotContain("checkpoint evidence").And.Contain("$Default is a configured assumption");
    }
}
