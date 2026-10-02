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
/// Event Hub runtime evidence in Target Environment → Integrations: four groups that are never mixed — runtime execution (can this instance
/// call Azure), the four counted evidence sources, supporting settings (expected consumer group, Log Analytics workspace) and the
/// evaluation policy. The verified M2LB DEV defaults, <c>$Default</c> as an assumption, checkpoint endpoint + container, Application
/// Insights without a Log Analytics workspace.
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

    private void UseRuntime(Func<IntegrationRuntimeEvidenceSettings, IntegrationRuntimeEvidenceSettings> change, bool azureEnabled = false) =>
        _api.Catalog = M2lbFixture.SeededCatalog(azureEnabled) with { Platforms = [M2lbFixture.SeededCatalog().Platforms.Single() with { RuntimeEvidence = change(M2lbFixture.SeededRuntime()) }] };

    private static IRenderedComponent<IntegrationsPane> Expand(IRenderedComponent<IntegrationsPane> cut)
    {
        cut.Find("[data-testid=ip-runtime-sources-toggle]").Click();
        return cut;
    }

    private static AngleSharp.Dom.IElement Row(IRenderedComponent<IntegrationsPane> cut, string key) => cut.Find($"[data-testid=ip-runtime-summary] [data-testid=ip-runtime-row-{key}]");

    private static string Text(IRenderedComponent<IntegrationsPane> cut, string testId) => cut.Find($"[data-testid={testId}]").TextContent.Trim();

    private static string[] SourceKeys(IRenderedComponent<IntegrationsPane> cut) =>
        cut.FindAll("[data-testid=ip-runtime-sources-list] > li").Select(li => li.GetAttribute("data-testid")!).ToArray();

    [Fact]
    public void SourcesConfiguredAndRuntimeDisabled_AreTwoSeparateStatements()
    {
        var cut = Open();
        Text(cut, "ip-runtime-sources-status").Should().Be("Sources configured: 4/4");
        Text(cut, "ip-runtime-execution-status").Should().Be("Runtime execution: Not available");
        Text(cut, "ip-summary-runtime").Should().Be("Sources configured: 4/4");
        Text(cut, "ip-summary-azure").Should().Be("Runtime execution: Not available");
        Text(cut, "ip-runtime-execution-value").Should().Be("Not available");
        Text(cut, "ip-runtime-execution-summary").Should().Be("Azure access is not enabled for this BirkNext instance.");
        Text(cut, "ip-runtime-sources-count").Should().Be("4 of 4 configured");

        cut.Markup.Should().NotContain("Runtime evidence configured", "only the sources are configured").And.NotContain("Azure runtime execution: Not configured");
        cut.FindAll("[data-testid=ip-runtime-status] .ehro-head .ehro-badge").Select(b => b.TextContent.Trim()).Should().NotContain("Configured", "a bare section-level Configured badge is ambiguous");
        cut.Find("[data-testid=ip-runtime-execution-status]").ClassList.Should().Contain("ehro-badge-attention").And.NotContain("ehro-badge-danger");
    }

    [Fact]
    public void RuntimeAvailable_DoesNotTurnConfiguredSourcesIntoObserved()
    {
        _api.Catalog = M2lbFixture.SeededCatalog(azureEnabled: true);
        var cut = Expand(Open());
        Text(cut, "ip-runtime-execution-status").Should().Be("Runtime execution: Available");
        Text(cut, "ip-summary-azure").Should().Be("Runtime execution: Available");
        Text(cut, "ip-runtime-sources-status").Should().Be("Sources configured: 4/4");
        cut.Find("[data-testid=ip-runtime-execution-group]").GetAttribute("data-available").Should().Be("true");
        foreach (var key in new[] { "metadata", "groups", "checkpoint", "appinsights" })
            Row(cut, key).GetAttribute("data-status").Should().Be(nameof(RuntimeSourceStatus.Configured));
        var list = cut.Find("[data-testid=ip-runtime-sources-list]").TextContent;
        list.Should().NotContain("Observed").And.Contain("Read only when a review runs; nothing has been read here.");
    }

    [Fact]
    public void TheCountCoversExactlyTheFourEvidenceSources()
    {
        var cut = Open();
        SourceKeys(cut).Should().Equal("ip-runtime-row-metadata", "ip-runtime-row-groups", "ip-runtime-row-checkpoint", "ip-runtime-row-appinsights");
        var platform = _api.Catalog.Platforms.Single();
        var (configured, total) = EventHubRuntimeSources.Count(platform);
        cut.FindAll("[data-testid=ip-runtime-sources-list] > li[data-status=Configured]").Should().HaveCount(configured);
        total.Should().Be(4);
        cut.Find("[data-testid=ip-runtime-sources-list]").TextContent.Should().NotContain("Expected consumer group").And.NotContain("Log Analytics").And.NotContain("policy");
        cut.Find("[data-testid=ip-runtime-supporting]").QuerySelector("[data-testid=ip-runtime-row-expected-group]").Should().NotBeNull();
        cut.Find("[data-testid=ip-runtime-supporting]").QuerySelector("[data-testid=ip-runtime-row-workspace]").Should().NotBeNull();
    }

    [Fact]
    public void OneMissingSourceIsCountedAndNamed()
    {
        UseRuntime(r => r with { CheckpointBlobEndpoint = null, CheckpointContainerName = null, CheckpointContainerUrl = null });
        var cut = Open();
        Text(cut, "ip-runtime-sources-status").Should().Be("Sources configured: 3/4");
        Text(cut, "ip-runtime-sources-count").Should().Be("3 of 4 configured");
        cut.Find("[data-testid=ip-runtime-sources-count]").ClassList.Should().Contain("ehro-badge-attention");
        var row = Row(cut, "checkpoint");
        row.GetAttribute("data-status").Should().Be(nameof(RuntimeSourceStatus.NotConfigured));
        row.QuerySelector(".ehro-badge")!.TextContent.Should().Be("Not configured");
        row.TextContent.Should().Contain("Needs the checkpoint Blob endpoint and container");
        Text(cut, "ip-runtime-execution-status").Should().Be("Runtime execution: Not available", "a missing source does not change instance execution");
    }

    [Fact]
    public void SeededDefaultsAreShownAsConfiguredSourcesWithTheirProvenance()
    {
        var cut = Expand(Open());
        Row(cut, "groups").TextContent.Should().Contain("m2lb-samhandling-dev (2fcdb9d0-22eb-43b0-b95e-7cbba08c34b0)").And.Contain("rg-m2lb-dev-integration-nwe");
        Row(cut, "metadata").TextContent.Should().Contain("evhns-m2lb-dev-nwe-001.servicebus.windows.net").And.Contain("Read-only management operations").And.Contain("Last enqueued position");
        Row(cut, "checkpoint").TextContent.Should().Contain("https://stm2bbirkdevnwe001.blob.core.windows.net/person-adapter")
            .And.Contain("From source configuration (audited, not deployment-verified)").And.Contain("EventHub__FQDN, Storage__BlobEndpoint and Storage__ContainerName").And.Contain("no checkpoint is written");
        Row(cut, "checkpoint").TextContent.Should().NotContain("Observed", "the checkpoint location is configuration, not an observed checkpoint");
        Row(cut, "appinsights").TextContent.Should().Contain("appi-m2lb-dev-nwe-001 · rg-m2lb-dev-shared-nwe").And.Contain("configured on the consumer: yes").And.Contain("never read or stored");
        Row(cut, "metadata").TextContent.Should().Contain("Not available on this BirkNext instance; nothing has been read.");
    }

    [Fact]
    public void DefaultIsAnAssumptionAndTheMappingNeedsConfirmation()
    {
        var cut = Open();
        var row = Row(cut, "expected-group");
        row.GetAttribute("data-status").Should().Be(nameof(RuntimeSourceStatus.Assumption));
        Text(cut, "ip-runtime-group-status").Should().Be("Assumption");
        Text(cut, "ip-runtime-group-value").Should().Be("$Default");
        Text(cut, "ip-runtime-group-provenance").Should().Be("Configured assumption");
        Text(cut, "ip-runtime-group-mapping").Should().Be("Needs confirmation");
        cut.Find("[data-testid=ip-runtime-group-mapping]").ClassList.Should().NotContain("ehro-badge-ready");
        Text(cut, "ip-runtime-group-note").Should().Contain("barntype").And.Contain("not confirmed");
        Text(cut, "ip-runtime-group-caveat").Should().Contain("does not confirm which group a consumer reads with").And.Contain("never a confirmed mapping");
        row.TextContent.Should().NotContain("Confirmed mapping").And.NotContain("Observed in Azure");
        cut.Markup.Should().NotContain("Confirmed mapping");
    }

    [Fact]
    public void AMissingLogAnalyticsWorkspaceIsNotRequired_NeverAWarningAndNotCounted()
    {
        var cut = Open();
        var row = Row(cut, "workspace");
        row.GetAttribute("data-status").Should().Be(nameof(RuntimeSourceStatus.NotRequired));
        row.QuerySelector(".ehro-badge")!.TextContent.Should().Be("Not required");
        row.QuerySelector(".ehro-badge")!.ClassList.Should().Contain("ehro-badge-muted").And.NotContain("ehro-badge-attention");
        row.TextContent.Should().Contain("None").And.Contain("Azure Monitor").And.Contain("Application Insights");
        row.Closest("[data-testid=ip-runtime-supporting]").Should().NotBeNull();
        Text(cut, "ip-runtime-sources-status").Should().Be("Sources configured: 4/4", "telemetry is configured through Application Insights");
    }

    [Fact]
    public void WithoutThresholdsPolicyIsObservedOnly_NeverPass()
    {
        var cut = Open();
        Text(cut, "ip-runtime-policy-window").Should().Be("24 h");
        Text(cut, "ip-runtime-policy-lag").Should().Be("Not configured");
        Text(cut, "ip-runtime-policy-age").Should().Be("Not configured");
        Text(cut, "ip-runtime-policy-semantics").Should().Be("Without thresholds, lag and checkpoint age are reported as Observed only — never Pass.");
        cut.Find("[data-testid=ip-runtime-policy]").Closest("[data-testid=ip-runtime-sources]").Should().BeNull("policy is not an evidence source");
        cut.FindAll("[data-testid=ip-runtime-status] .ehro-badge").Select(b => b.TextContent.Trim()).Should().NotContain(t => t.Contains("Pass"));
    }

    [Fact]
    public void ConfiguredThresholdsAreShownAndJudged()
    {
        UseRuntime(r => r with { MaxConsumerLagEvents = 1000, MaxCheckpointAgeMinutes = 30, ReviewWindowHours = 12 });
        var cut = Open();
        Text(cut, "ip-runtime-policy-window").Should().Be("12 h");
        Text(cut, "ip-runtime-policy-lag").Should().Be("≤ 1000 events");
        Text(cut, "ip-runtime-policy-age").Should().Be("≤ 30 min");
        Text(cut, "ip-runtime-policy-semantics").Should().Contain("judged against these thresholds").And.Contain("assumed consumer group");
        Text(cut, "ip-runtime-sources-status").Should().Be("Sources configured: 4/4", "thresholds do not change the source count");
    }

    [Fact]
    public void SourceDetailsAreCollapsedByDefaultAndToggleWithAriaExpanded()
    {
        var cut = Open();
        var toggle = cut.Find("[data-testid=ip-runtime-sources-toggle]");
        toggle.TagName.Should().Be("BUTTON");
        toggle.GetAttribute("type").Should().Be("button");
        toggle.GetAttribute("aria-expanded").Should().Be("false");
        toggle.TextContent.Should().Contain("Show details").And.Contain("of the evidence sources", "the screen-reader name says what is expanded");
        toggle.GetAttribute("aria-controls").Should().Be(cut.Find("[data-testid=ip-runtime-sources-list]").Id);
        cut.FindAll("[data-testid=ip-runtime-source-detail]").Should().HaveCount(4).And.OnlyContain(d => d.HasAttribute("hidden"));
        cut.FindAll("[data-testid=ip-runtime-sources-list] .ehro-value").Should().HaveCount(4).And.OnlyContain(v => !v.HasAttribute("hidden"), "the compact rows stay visible");

        toggle.Click();
        toggle = cut.Find("[data-testid=ip-runtime-sources-toggle]");
        toggle.GetAttribute("aria-expanded").Should().Be("true");
        toggle.TextContent.Should().Contain("Hide details");
        cut.FindAll("[data-testid=ip-runtime-source-detail]").Should().OnlyContain(d => !d.HasAttribute("hidden"));

        toggle.Click();
        cut.Find("[data-testid=ip-runtime-sources-toggle]").GetAttribute("aria-expanded").Should().Be("false");
        cut.FindAll("[data-testid=ip-runtime-source-detail]").Should().OnlyContain(d => d.HasAttribute("hidden"));
    }

    [Fact]
    public void RuntimeExecutionDetailsAreCollapsedAndNameTheInstanceSetting()
    {
        var cut = Open();
        var toggle = cut.Find("[data-testid=ip-runtime-execution-toggle]");
        toggle.GetAttribute("aria-expanded").Should().Be("false");
        toggle.TextContent.Should().Contain("View details").And.Contain("about runtime execution");
        var detail = cut.Find("[data-testid=ip-runtime-execution-detail]");
        detail.HasAttribute("hidden").Should().BeTrue();
        detail.TextContent.Should().Contain("IntegrationReview:Azure:Enabled is not true").And.Contain("not a problem with the integration").And.Contain("does not enable execution");
        toggle.Click();
        cut.Find("[data-testid=ip-runtime-execution-toggle]").GetAttribute("aria-expanded").Should().Be("true");
        cut.Find("[data-testid=ip-runtime-execution-detail]").HasAttribute("hidden").Should().BeFalse();
    }

    [Fact]
    public void ActionsSitWithTheirGroupsAndOpenTheFormWithoutSaving()
    {
        var cut = Open();
        var configure = cut.Find("[data-testid=ip-configure-runtime]");
        configure.TextContent.Should().Be("Configure Azure runtime access");
        configure.TagName.Should().Be("BUTTON");
        configure.Closest("[data-testid=ip-runtime-execution-group]").Should().NotBeNull();
        var edit = cut.Find("[data-testid=ip-runtime-edit]");
        edit.TextContent.Should().Be("Edit runtime evidence sources");
        edit.Closest("[data-testid=ip-runtime-sources]").Should().NotBeNull("the edit action belongs to the evidence sources, not after the policy");
        cut.Markup.Should().NotContain("Configure runtime evidence");

        configure.Click();
        cut.Find("[data-testid=ip-runtime-form]");
        cut.Find("[data-testid=ip-runtime-section-a] legend").TextContent.Should().Be("A · Azure runtime access");
        cut.FindAll("[data-testid=ip-runtime-summary]").Should().BeEmpty("the form replaces the groups while editing");
        Text(cut, "ip-runtime-sources-status").Should().Be("Sources configured: 4/4");
        _api.SavedPlatforms.Should().BeEmpty();
    }

    [Fact]
    public void TheFormHasFiveSectionsIdentityTextAndPrefilledValues()
    {
        var cut = Open();
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
        cut.Find("[data-testid=ip-runtime-execution]").TextContent.Should().Be("Not available");
        cut.Find("[data-testid=ip-runtime-readiness]").TextContent.Should().Contain("Not required").And.Contain("Observed only — never Pass");
        foreach (var field in cut.FindAll("[data-testid=ip-runtime-form] input"))
            field.Closest("label").Should().NotBeNull("every field has a visible label");
    }

    [Fact]
    public void SavingWithoutAWorkspaceKeepsEveryValueAndShowsAPostSaveSummary()
    {
        var cut = Open();
        cut.Find("[data-testid=ip-runtime-edit]").Click();
        cut.Find("[data-testid=ip-runtime-save]").Click();
        cut.WaitForAssertion(() => _api.SavedPlatforms.Should().ContainSingle());
        var saved = _api.SavedPlatforms.Single().RuntimeEvidence!;
        saved.Should().BeEquivalentTo(M2lbFixture.SeededRuntime(), "an unchanged save keeps every value");
        saved.TelemetryWorkspaceId.Should().BeNull("no workspace is required");
        cut.Find("[data-testid=ip-runtime-saved]").TextContent.Should()
            .Contain("Evidence sources: 4 of 4 configured").And.Contain("Runtime execution: Not available");
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
        var cut = Open();
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
        var cut = Open();
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
        Text(cut, "ip-runtime-sources-status").Should().Be("Sources configured: 0/4");
        Text(cut, "ip-runtime-sources-count").Should().Be("0 of 4 configured");
        cut.FindAll("[data-testid=ip-runtime-sources-list] > li").Should().OnlyContain(li => li.GetAttribute("data-status") == nameof(RuntimeSourceStatus.NotConfigured));
    }

    [Fact]
    public void AWorkspaceOnlyTelemetrySourceStillCountsAsTheFourthSource()
    {
        UseRuntime(r => r with { ApplicationInsightsResourceName = null, ApplicationInsightsResourceGroup = null, TelemetryWorkspaceId = "11111111-2222-3333-4444-555555555555" });
        var cut = Open();
        Text(cut, "ip-runtime-sources-status").Should().Be("Sources configured: 4/4");
        var row = Row(cut, "appinsights");
        row.GetAttribute("data-status").Should().Be(nameof(RuntimeSourceStatus.Configured));
        row.TextContent.Should().Contain("Telemetry").And.Contain("Log Analytics workspace 11111111-2222-3333-4444-555555555555");
        Row(cut, "workspace").GetAttribute("data-status").Should().Be(nameof(RuntimeSourceStatus.Configured));
    }

    [Fact]
    public void NoConnectionStringOrKeyAppearsAnywhere()
    {
        var cut = Expand(Open());
        cut.Markup.Should().NotContainAny("InstrumentationKey", "APPLICATIONINSIGHTS_CONNECTION_STRING", "AccountKey", "SharedAccessKey", "sig=");
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
