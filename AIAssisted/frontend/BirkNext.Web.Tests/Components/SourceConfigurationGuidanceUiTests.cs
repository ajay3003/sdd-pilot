using System.Net;
using BirkNext.Integrations;
using BirkNext.SourceArchitecture;
using BirkNext.SourceDomains;
using BirkNext.Web.Components;
using BirkNext.Web.Components.SourceDomains;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Source-declared configuration in the UI: declared in source ≠ suggested ≠ configured ≠ runtime verified. States are words (never colour
/// only), provenance names the artifact and its location, a suggestion fills a form only on an explicit click and is never saved by itself,
/// a conflict offers no value, and an unbound snapshot offers nothing. Generic names only (a parcel API), except where a provider panel is tested.
/// </summary>
public sealed class SourceConfigurationGuidanceUiTests : BunitContext
{
    public SourceConfigurationGuidanceUiTests() => JSInterop.Mode = JSRuntimeMode.Loose;

    private static SourceConfigurationValue Declared(string key, string value, string artifact = "api/parcels.json", string location = "servers[0]",
        SourceConfigurationEvidenceKind kind = SourceConfigurationEvidenceKind.OpenApi, SourceConfigurationGroup group = SourceConfigurationGroup.Environment) => new()
    {
        SemanticKey = key, Value = value, Group = group, Environment = SourceEnvironmentRole.Test, EvidenceKind = kind, Artifact = artifact, Location = location, Basis = "server description \"Test\"",
    };

    private static SourceConfigurationRequirement Requirement(string? field = "Base URL") =>
        new("parcel-test-server", "Parcel API test server", "Environment.Server.Test", "Parcel API", "OpenAPI / Swagger document", "Where bookings go.", "Demo settings") { ConsumerField = field };

    private static SourceConfigurationSuggestion Suggested(string? configured = null) => new(Requirement(), SourceSuggestionState.Suggested)
    {
        Value = "https://parcels.test.example.invalid", Candidates = [new("https://parcels.test.example.invalid", [Declared("Environment.Server.Test", "https://parcels.test.example.invalid")])],
        BoundSources = ["parcels.zip"], ConfiguredValue = configured,
    };

    // ── Presentation ────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Every_state_is_a_distinct_word_and_runtime_is_never_verified_by_source()
    {
        Enum.GetValues<SourceSuggestionState>().Select(SourceGuidancePresentation.StateLabel).Should().OnlyHaveUniqueItems()
            .And.BeEquivalentTo(["Not in the bound source", "Available but not bound", "Suggested from bound source", "Configuration conflict", "Required source missing", "From Test Pack reference"]);
        Suggested().RuntimeVerified.Should().BeFalse();
        SourceGuidancePresentation.Configured(Suggested()).Should().Be("Not stored — known from: Canonical source evidence");
        SourceGuidancePresentation.Configured(new(Requirement(), SourceSuggestionState.SourceMissing)).Should().Be("Not configured");
        SourceGuidancePresentation.Configured(Suggested("https://other.invalid")).Should().Be("https://other.invalid (differs from source)");
        SourceGuidancePresentation.Configured(Suggested() with { Requirement = Requirement(null) }).Should().Be("No stored field — known from: Canonical source evidence",
            "a known value is never shown as missing or as a generic 'no field'");
        SourceGuidancePresentation.Configured(new(Requirement(null) with { OwnerAction = "Target Environment → Integrations" }, SourceSuggestionState.SourceMissing))
            .Should().Be("Unresolved — Target Environment → Integrations", "an unresolved value names where it is configured");
        SourceGuidancePresentation.Provenance(Suggested()).Should().Equal("OpenAPI · api/parcels.json → servers[0]");
    }

    [Fact]
    public void The_next_action_names_where_it_is_done_and_never_a_particular_file()
    {
        SourceGuidancePresentation.NextAction(Suggested()).Label.Should().Be("Review and use the suggested value in Demo settings → Base URL");
        SourceGuidancePresentation.NextAction(new(Requirement(), SourceSuggestionState.AvailableNotBound)).Href.Should().Be("/integration-quality-review?view=evidence");
        var missing = SourceGuidancePresentation.NextAction(new(Requirement(), SourceSuggestionState.NotDeclared));
        missing.Label.Should().Be("Import the OpenAPI / Swagger document of Parcel API in Source Analysis");
        missing.Href.Should().Be("/source-analysis");
    }

    [Theory]
    [InlineData(JourneyPrerequisiteCategory.SourceEvidence, null, false, "Source evidence")]
    [InlineData(JourneyPrerequisiteCategory.Integration, null, false, "Configuration")]
    [InlineData(JourneyPrerequisiteCategory.Authentication, null, false, "Configuration")]
    [InlineData(JourneyPrerequisiteCategory.SyntheticData, null, false, "Test Data")]
    [InlineData(JourneyPrerequisiteCategory.Authorization, null, false, "External attestation")]
    [InlineData(JourneyPrerequisiteCategory.Contract, "Source Analysis", false, "Source evidence")]
    [InlineData(JourneyPrerequisiteCategory.Contract, "Live metadata", false, "Runtime evidence")]
    [InlineData(JourneyPrerequisiteCategory.Observation, null, false, "Runtime evidence")]
    [InlineData(JourneyPrerequisiteCategory.Integration, null, true, "Source-derived configuration")]
    public void A_prerequisite_says_what_kind_of_input_it_needs(JourneyPrerequisiteCategory category, string? source, bool sourceDerived, string expected) =>
        SourceGuidancePresentation.RequirementKind(new JourneyPrerequisite("k", "K", category, JourneyReadinessState.NotReady, "d") { Source = source }, sourceDerived).Should().Be(expected);

    [Fact]
    public void An_unresolved_connection_names_the_source_that_would_state_it()
    {
        var guidance = new TestPackSourceGuidance("p", [])
        {
            Sources =
            [
                new(new TestPackSourceRole("hub", "Hub", "") { ComponentTokens = ["Hub"] }, TestPackSourceState.Bound) { BoundArchives = ["hub.zip"] },
                new(new TestPackSourceRole("wh", "Warehouse", "") { ComponentTokens = ["Stock", "Warehouse"] }, TestPackSourceState.AvailableNotBound)
                    { AvailableArchives = ["warehouse (2).zip"], NextAction = "Bind it in IQR Evidence" },
            ],
        };
        SourceGuidancePresentation.ConnectionHint(SourceGuidancePresentation.ForComponents(guidance, "Hub", "Stock"))
            .Should().Be("Required source: Warehouse. Available: warehouse (2).zip — not bound to a relevant integration. Next: Bind it in IQR Evidence.",
                "the consuming component's source would state the connection");
        SourceGuidancePresentation.ForComponents(guidance, "Shop", "Bank").Should().BeNull("no source claims these components");
    }

    // ── Guidance list ───────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_guidance_list_shows_value_source_configured_runtime_and_provenance_and_announces_a_conflict()
    {
        var conflict = new SourceConfigurationSuggestion(Requirement(), SourceSuggestionState.Conflict)
        {
            Candidates = [new("https://a.invalid", [Declared("Environment.Server.Test", "https://a.invalid")]), new("https://b.invalid", [Declared("Environment.Server.Test", "https://b.invalid", "appsettings.Test.json", "Parcels:BaseUrl (line 1)", SourceConfigurationEvidenceKind.ApplicationConfig)])],
            BoundSources = ["parcels.zip"],
        };
        var unbound = new SourceConfigurationSuggestion(Requirement() with { Key = "other" }, SourceSuggestionState.AvailableNotBound) { UnboundSources = ["newer.zip"] };
        var cut = Render<SourceGuidanceList>(p => p.Add(x => x.Suggestions, [Suggested(), conflict, unbound]));

        var items = cut.FindAll("[data-testid=sgl-item]");
        items.Select(item => item.QuerySelector("[data-testid=sgl-state]")!.TextContent).Should().Equal("Suggested from bound source", "Configuration conflict", "Available but not bound");
        items[0].QuerySelector("[data-testid=sgl-value]")!.TextContent.Should().Be("https://parcels.test.example.invalid");
        items[0].QuerySelector("[data-testid=sgl-runtime]")!.TextContent.Should().Be("No — declared in source only");
        items[0].QuerySelector("[data-testid=sgl-current]")!.TextContent.Should().Be("parcels.zip (bound)");
        items[1].QuerySelector("[data-testid=sgl-value]").Should().BeNull("a conflict offers no value");
        items[1].QuerySelector("[role=status]")!.TextContent.Should().Contain("Configuration conflict");
        items[1].TextContent.Should().Contain("Configuration · appsettings.Test.json → Parcels:BaseUrl (line 1) = https://b.invalid");
        items[2].QuerySelector("[data-testid=sgl-value]").Should().BeNull("an unbound snapshot offers nothing");
        items[2].QuerySelector("[data-testid=sgl-current]")!.TextContent.Should().Be("newer.zip (not bound)");
        cut.FindAll("button").Where(button => button.TextContent.Contains("Use", StringComparison.Ordinal)).Should().BeEmpty("the guidance list is read-only");
    }

    // ── Field hint ──────────────────────────────────────────────────────────────────────────────────────────────────

    private static SourceConfigurationResolution Resolution(SourceConfigurationResolutionState state, params string[] values) =>
        new("Application.Route", SourceEnvironmentRole.Any, state, values.Select(value => new SourceConfigurationCandidate(value, [Declared("Application.Route", value, location: "paths", group: SourceConfigurationGroup.Application)])).ToArray());

    [Fact]
    public void Use_suggested_value_fills_the_field_only_on_an_explicit_click()
    {
        string? used = null;
        var cut = Render<SourceValueHint>(p => p.Add(x => x.Resolution, Resolution(SourceConfigurationResolutionState.Declared, "shop/parcels"))
            .Add(x => x.Current, "").Add(x => x.FieldLabel, "Route").Add(x => x.OnUse, EventCallback.Factory.Create<string>(this, value => used = value)));
        cut.Find("[data-testid=source-value-hint-value]").TextContent.Should().Be("shop/parcels");
        cut.Markup.Should().Contain("OpenAPI · api/parcels.json → paths").And.Contain("not runtime verified");
        used.Should().BeNull("nothing is applied by rendering");
        cut.Find("[data-testid=source-value-hint-use]").Click();
        used.Should().Be("shop/parcels");
    }

    [Fact]
    public void A_different_stored_value_is_kept_a_matching_one_needs_no_action_and_a_conflict_offers_nothing()
    {
        var differs = Render<SourceValueHint>(p => p.Add(x => x.Resolution, Resolution(SourceConfigurationResolutionState.Declared, "shop/parcels")).Add(x => x.Current, "shop/other"));
        differs.Find("[data-testid=source-value-hint-differs]").TextContent.Should().Contain("It is kept unless you choose otherwise");
        var agrees = Render<SourceValueHint>(p => p.Add(x => x.Resolution, Resolution(SourceConfigurationResolutionState.Declared, "shop/parcels")).Add(x => x.Current, "shop/parcels"));
        agrees.FindAll("[data-testid=source-value-hint-use]").Should().BeEmpty();
        agrees.Find("[data-testid=source-value-hint-agrees]").Should().NotBeNull();
        var conflict = Render<SourceValueHint>(p => p.Add(x => x.Resolution, Resolution(SourceConfigurationResolutionState.Conflict, "shop/a", "shop/b")).Add(x => x.Current, ""));
        conflict.FindAll("button").Should().BeEmpty();
        conflict.Find("[role=status]").TextContent.Should().Contain("Configuration conflict").And.Contain("shop/a vs shop/b");
    }

    // ── Source Analysis → Configuration → Declared values ──────────────────────────────────────────────────────────

    [Fact]
    public void Declared_values_are_grouped_with_provenance_and_shown_even_without_configuration_files()
    {
        var evidence = new SourceEvidenceDomainsSnapshot
        {
            Configuration = new ConfigurationEvidence { Domain = SourceEvidenceDomain.Configuration, Status = SourceDomainStatus.NotDetected },
            ConfigurationValues = new SourceConfigurationValuesEvidence
            {
                Values =
                [
                    Declared("Environment.Server.Test", "https://parcels.test.example.invalid"),
                    Declared("Application.Route", "shop/parcels", location: "paths", group: SourceConfigurationGroup.Application),
                    Declared("Endpoint.ProcessNext", "PUT /shop/parcels/instances/{a}/{b}/process/next", location: "paths./shop/parcels/instances/{a}/{b}/process/next.put", group: SourceConfigurationGroup.ApiEndpoints),
                    Declared("Messaging.ServiceBus.Topic", "period-closed", "infra/main.tf", "azurerm_servicebus_topic.closed (line 7)", SourceConfigurationEvidenceKind.InfrastructureAsCode, SourceConfigurationGroup.Messaging),
                ],
                Limitations = ["Declared in source: not configured, not reachable, not runtime verified."],
            },
        };
        var cut = Render<ConfigurationWorkspace>(p => p.Add(x => x.Evidence, evidence));
        cut.Find("[data-testid=sd-config-declared-note]").TextContent.Should().Contain("Runtime is not assessed here");
        cut.FindAll("[data-testid=sd-config-declared-runtime]").Should().OnlyContain(c => c.TextContent == "Not assessed", "runtime is its own column");
        cut.FindAll("[data-testid=sd-config-declared-group]").Should().HaveCount(4);
        var test = cut.Find("[data-testid=sd-config-declared-row][data-key='Environment.Server.Test']");
        test.TextContent.Should().Contain("https://parcels.test.example.invalid").And.Contain("OpenAPI · api/parcels.json → servers[0]").And.Contain("Declared in source").And.Contain("Not assessed");
        cut.Markup.Should().Contain("Infrastructure as Code · infra/main.tf → azurerm_servicebus_topic.closed (line 7)");
    }

    [Fact]
    public void A_conflict_between_artifacts_is_announced_in_declared_values()
    {
        var evidence = new SourceEvidenceDomainsSnapshot
        {
            Configuration = new ConfigurationEvidence { Domain = SourceEvidenceDomain.Configuration, Status = SourceDomainStatus.NotDetected },
            ConfigurationValues = new SourceConfigurationValuesEvidence
            {
                Values = [Declared("Environment.Server.Test", "https://a.invalid"), Declared("Environment.Server.Test", "https://b.invalid", "api/other.json")],
            },
        };
        var cut = Render<ConfigurationWorkspace>(p => p.Add(x => x.Evidence, evidence));
        cut.Find("[data-testid=sd-config-declared-conflicts]").TextContent.Should().Contain("1 configuration conflict");
        cut.Find("[data-testid=sd-config-declared-row]").GetAttribute("data-state").Should().Be("Conflict");
    }

    // ── Altinn provider: prefill hints from the pack's bound source guidance ────────────────────────────────────────────

    private static SourceConfigurationSuggestion Field(string key, string field, string semanticKey, string? value, SourceSuggestionState state = SourceSuggestionState.Suggested,
        params string[] conflicting) => new(new SourceConfigurationRequirement(key, field, semanticKey, "App", "Configuration", "p", "IQR") { ConsumerField = field, AutoApply = true }, state)
    {
        Value = value,
        Candidates = (conflicting.Length > 0 ? conflicting : [value!]).Select(item => new SourceConfigurationCandidate(item,
            [Declared(semanticKey, item, "Hub/appsettings.Development.json", "AltinnWebhookSettings:Subscriptions:0:SourceFilter (line 36)", SourceConfigurationEvidenceKind.ApplicationConfig)])).ToArray(),
        BoundSources = ["hub.zip"],
    };

    private (Mock<IIntegrationCatalogApiService> Api, IRenderedComponent<MessageFlowReviewPanel> Cut) RenderPanel(AltinnTestConfiguration config, TestPackSourceGuidance? guidance, bool bound = true)
    {
        var api = new Mock<IIntegrationCatalogApiService>();
        api.Setup(x => x.GetMessageFlowReviewAsync("pilot", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IntegrationMessageFlowPackage(new MessageFlowDefinition { EnvironmentId = "pilot", Name = "Message flow review" }, config, new(false, 0, 0, [], []), [], []));
        api.Setup(x => x.IqrSourceScopeAsync("pilot", null, It.IsAny<CancellationToken>())).ReturnsAsync(new BirkNext.SourceEvidence.ReviewSourceOptions());
        api.Setup(x => x.ListEvidenceBindingsAsync("pilot", It.IsAny<CancellationToken>())).ReturnsAsync(bound
            ? [new IntegrationEvidenceBinding { EnvironmentId = "pilot", IntegrationId = "pilot:report-intake", SnapshotId = Guid.NewGuid(), ArchiveName = "hub.zip", Fingerprint = new string('e', 64) }]
            : []);
        var journeys = new Mock<IIntegrationJourneysApiService>();
        journeys.Setup(x => x.PacksAsync("pilot", It.IsAny<CancellationToken>())).ReturnsAsync(new ActiveEventApiResult<IReadOnlyList<IntegrationJourneyPackView>>(
            [new IntegrationJourneyPackView { PackId = "demo", Journeys = [new IntegrationJourneyView { MatchedIntegrations = [new("report-intake", "pilot:report-intake", "Report intake", IntegrationKind.HttpApi, true, "Configured.")] }] }],
            null, HttpStatusCode.OK));
        journeys.Setup(x => x.SourceGuidanceAsync("pilot", "demo", It.IsAny<CancellationToken>())).ReturnsAsync(new ActiveEventApiResult<TestPackSourceGuidance>(guidance, null, HttpStatusCode.OK));
        Services.AddSingleton(api.Object);
        Services.AddSingleton(journeys.Object);
        return (api, Render<MessageFlowReviewPanel>(p => p.Add(x => x.EnvironmentId, "pilot")));
    }

    private static TestPackSourceGuidance HubGuidance() => new("demo",
    [
        Field("env", "Environment", "Application.Environment", "Test"),
        Field("route", "App identifier", "Application.Route", "shop/parcels"),
        Field("base", "Safe base URL reference", "Application.BaseUrl", "https://shop.apps.test.example.invalid/shop/parcels"),
        Field("hook", "Endpoint reference", "Endpoint.EventWebhook", "https://hub.example.invalid/api/hook"),
    ]);

    private static string Input(IRenderedComponent<MessageFlowReviewPanel> cut, string label) =>
        cut.FindAll("label").Single(item => item.TextContent.Trim().StartsWith(label, StringComparison.Ordinal)).QuerySelector("input, select")!.GetAttribute("value") ?? "";

    [Fact]
    public void Every_field_shows_its_bound_suggestion_and_using_them_fills_only_empty_fields_without_saving()
    {
        var (api, cut) = RenderPanel(new AltinnTestConfiguration { EndpointReference = "https://explicit.example.invalid/hook" }, HubGuidance());
        cut.WaitForAssertion(() => cut.Find("[data-testid=mfr-app-suggestion-value]").TextContent.Should().Be("shop/parcels"));
        cut.Find("[data-testid=mfr-environment-suggestion-value]").TextContent.Should().Be("Test");
        cut.Find("[data-testid=mfr-base-url-suggestion-value]").TextContent.Should().Be("https://shop.apps.test.example.invalid/shop/parcels");
        cut.Find("[data-testid=mfr-endpoint-suggestion-differs]").TextContent.Should().Contain("It is kept");
        cut.Markup.Should().Contain("Configuration · Hub/appsettings.Development.json → AltinnWebhookSettings:Subscriptions:0:SourceFilter (line 36)");
        cut.Find("[data-testid=mfr-use-source-values]").TextContent.Should().Be("Use 3 safe source values");
        cut.Find("[data-testid=mfr-use-source-values]").Click();
        Input(cut, "App identifier").Should().Be("shop/parcels");
        Input(cut, "Safe base URL reference").Should().Be("https://shop.apps.test.example.invalid/shop/parcels");
        Input(cut, "Endpoint reference").Should().Be("https://explicit.example.invalid/hook", "an explicit value is never replaced");
        cut.FindAll("label").Single(item => item.TextContent.Trim().StartsWith("Environment", StringComparison.Ordinal)).QuerySelector("select")!
            .QuerySelector("option[selected]")?.GetAttribute("value").Should().Be("Test");
        api.Verify(x => x.SaveMessageFlowReviewDetailedAsync(It.IsAny<string>(), It.IsAny<IntegrationMessageFlowPackage>(), It.IsAny<CancellationToken>()), Times.Never,
            "filling the form saves nothing");
    }

    [Fact]
    public void A_conflict_offers_no_value_and_production_is_never_offered_for_the_environment()
    {
        var guidance = new TestPackSourceGuidance("demo",
        [
            Field("env", "Environment", "Application.Environment", "Production"),
            Field("hook", "Endpoint reference", "Endpoint.EventWebhook", null, SourceSuggestionState.Conflict, "https://a.invalid/hook", "https://b.invalid/hook"),
        ]);
        var (_, cut) = RenderPanel(new AltinnTestConfiguration(), guidance);
        cut.WaitForAssertion(() => cut.Find("[data-testid=mfr-endpoint-suggestion]").GetAttribute("data-state").Should().Be("Conflict"));
        cut.FindAll("[data-testid=mfr-environment-suggestion]").Should().BeEmpty("only Test is ever offered from source");
        cut.FindAll("[data-testid=mfr-use-source-values]").Should().BeEmpty();
    }

    [Fact]
    public void Without_a_binding_no_source_value_is_offered_or_requested()
    {
        var (_, cut) = RenderPanel(new AltinnTestConfiguration(), HubGuidance(), bound: false);
        cut.WaitForAssertion(() => cut.Find("[data-testid=mfr-source-unbound]"));
        cut.FindAll("[data-testid=mfr-app-suggestion]").Should().BeEmpty();
        cut.FindAll("[data-testid=mfr-use-source-values]").Should().BeEmpty("the newest snapshot never stands in for a binding");
    }

    [Fact]
    public void Without_a_binding_each_required_source_says_its_shared_state_and_available_is_never_missing()
    {
        var guidance = HubGuidance() with
        {
            Sources =
            [
                new(new TestPackSourceRole("hub", "Hub", "") { Journeys = ["intake"] }, TestPackSourceState.AvailableNotBound)
                    { Candidates = [new(Guid.NewGuid(), "hub.zip", new string('a', 64), DateTimeOffset.UnixEpoch)] },
                new(new TestPackSourceRole("shop", "Shop", "") { Journeys = ["intake"] }, TestPackSourceState.Missing),
                new(new TestPackSourceRole("app", "App", "") { Journeys = ["intake"], Provider = TestPackSourceProvider.TestPackReference }, TestPackSourceState.ReferenceAvailable),
            ],
        };
        var (_, cut) = RenderPanel(new AltinnTestConfiguration(), guidance, bound: false);
        // The reference is not an imported source and is never listed as missing.
        cut.WaitForAssertion(() => cut.Find("[data-testid=mfr-source-states]").TextContent.Should().Be("Hub available · needs binding · Shop missing"));
    }

    // ── One shared resolution in IQR: known values shown with their origin, owned values read (not copied) ──────────────────

    private const string Webhook = "https://mu.example.test/api/altinn/webhook";

    private static SourceConfigurationSuggestion Tier(string key, string field, string semanticKey, SourceSuggestionState state = SourceSuggestionState.SourceMissing,
        string? reference = null, string? integration = null, string? fallback = null, string? configured = null) =>
        SourceConfigurationPrecedence.Resolve(new SourceConfigurationSuggestion(new SourceConfigurationRequirement(key, field, semanticKey, "App", "Configuration", "p", "IQR") { ConsumerField = field, AutoApply = true }, state)
        {
            ReferenceValue = reference, IntegrationValue = integration, IntegrationName = integration is null ? null : "Demo – report intake", DefaultValue = fallback, ConfiguredValue = configured,
        });

    private static TestPackSourceGuidance ResolvedGuidance() => new("demo",
    [
        Tier("env", "Environment", SourceConfigurationKeys.ApplicationEnvironment, fallback: "Test"),
        Tier("route", "App identifier", SourceConfigurationKeys.ApplicationRoute, SourceSuggestionState.Reference, reference: "demo/app"),
        Tier("base", "Safe base URL reference", SourceConfigurationKeys.ApplicationBaseUrl, SourceSuggestionState.Reference, reference: "https://demo.apps.test.invalid/demo/app"),
        Tier("hook", "Endpoint reference", SourceConfigurationKeys.EndpointEventWebhook, integration: Webhook),
    ]);

    [Fact]
    public void Iqr_shows_the_known_values_with_their_origin_and_only_the_unresolved_ones_as_still_to_configure()
    {
        var (api, cut) = RenderPanel(new AltinnTestConfiguration(), ResolvedGuidance(), bound: false);
        cut.WaitForAssertion(() => cut.Find("[data-testid=mfr-known-count]").TextContent.Should().Be("4 of 6 known"));
        var known = cut.FindAll("[data-testid=mfr-known-item]").ToDictionary(item => item.GetAttribute("data-field")!, item => item.TextContent);
        known["Environment"].Should().Contain("Test").And.Contain("Test Pack default");
        known["App identifier"].Should().Contain("demo/app").And.Contain("Test Pack reference");
        known["Safe base URL reference"].Should().Contain("https://demo.apps.test.invalid/demo/app");
        known["Endpoint reference"].Should().Contain(Webhook).And.Contain("Configured integration Demo – report intake");
        cut.Find("[data-testid=mfr-unresolved]").TextContent.Should().Be("Still to configure here: Altinn product / service type, Submission mode");

        // The integration-owned endpoint is read, never copied: no "use" button, and filling only touches the other empty fields.
        cut.Find("[data-testid=mfr-endpoint-suggestion-owned]").TextContent.Should().Contain("Read from its owner");
        cut.FindAll("[data-testid=mfr-endpoint-suggestion-use]").Should().BeEmpty();
        cut.Find("[data-testid=mfr-use-source-values]").Click();
        Input(cut, "App identifier").Should().Be("demo/app");
        Input(cut, "Safe base URL reference").Should().Be("https://demo.apps.test.invalid/demo/app");
        Input(cut, "Endpoint reference").Should().BeEmpty("the integration record owns it");
        api.Verify(x => x.SaveMessageFlowReviewDetailedAsync(It.IsAny<string>(), It.IsAny<IntegrationMessageFlowPackage>(), It.IsAny<CancellationToken>()), Times.Never(), "nothing is saved by opening or filling");
    }

    [Fact]
    public void A_stored_value_wins_and_a_different_known_value_is_shown_for_review()
    {
        var guidance = new TestPackSourceGuidance("demo", [Tier("route", "App identifier", SourceConfigurationKeys.ApplicationRoute, SourceSuggestionState.Reference, reference: "demo/app", configured: "demo/other")]);
        var cut = Render<SourceGuidanceList>(p => p.Add(x => x.Suggestions, guidance.Suggestions));
        cut.Find("[data-testid=sgl-resolved]").GetAttribute("data-origin").Should().Be(nameof(ConfigurationValueOrigin.Configured));
        cut.Find("[data-testid=sgl-value]").TextContent.Should().Be("demo/other");
        cut.Find("[data-testid=sgl-disagreement]").TextContent.Should().Contain("Test Pack reference: demo/app").And.Contain("nothing is changed");
    }

    [Fact]
    public void Without_a_binding_a_Test_Pack_reference_value_is_offered_as_such_and_never_as_bound_source()
    {
        var reference = new SourceConfigurationSuggestion(Requirement("App identifier") with { SemanticKey = "Application.Route" }, SourceSuggestionState.Reference)
        {
            Value = "demo/app", ReferenceValue = "demo/app",
            Candidates = [new SourceConfigurationCandidate("demo/app", [Declared("Application.Route", "demo/app", "Test Pack reference", "App identifier", SourceConfigurationEvidenceKind.OpenApi)])],
        };
        var (api, cut) = RenderPanel(new AltinnTestConfiguration(), new TestPackSourceGuidance("demo", [reference]), bound: false);
        cut.WaitForAssertion(() => cut.Find("[data-testid=mfr-app-suggestion]"));
        var hint = cut.Find("[data-testid=mfr-app-suggestion]").TextContent;
        hint.Should().Contain("From Test Pack reference").And.Contain("demo/app").And.Contain("Test Pack reference → App identifier").And.Contain("not runtime verified");
        hint.Should().NotContain("bound source");
        cut.Find("[data-testid=mfr-app-suggestion-use]").Click();
        Input(cut, "App identifier").Should().Be("demo/app", "using it fills the form only");
        api.Verify(x => x.SaveMessageFlowReviewDetailedAsync(It.IsAny<string>(), It.IsAny<IntegrationMessageFlowPackage>(), It.IsAny<CancellationToken>()), Times.Never());
    }

    // ── Save: field-specific refusal, never the value ──────────────────────────────────────────────────────────────

    [Fact]
    public void A_refused_save_names_each_field_and_never_shows_the_value()
    {
        var (api, cut) = RenderPanel(new AltinnTestConfiguration(), null, bound: false);
        api.Setup(x => x.SaveMessageFlowReviewDetailedAsync("pilot", It.IsAny<IntegrationMessageFlowPackage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(((IntegrationMessageFlowPackage?)null, new MessageFlowSaveProblem("Credential-like values are not accepted.",
                [new ConfigurationFieldError("Authentication", "Client / app reference", "altinnConfiguration.clientReference", "Client / app reference was rejected because it appears to contain a JSON Web Token.")])));
        cut.WaitForAssertion(() => cut.Find("[data-testid=mfr-save]"));
        cut.Find("[data-testid=mfr-save]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=mfr-save-error-field]").TextContent
            .Should().Be("Authentication → Client / app reference: Client / app reference was rejected because it appears to contain a JSON Web Token."));
        cut.Find("[data-testid=mfr-save-error]").GetAttribute("role").Should().Be("alert");
        cut.Markup.Should().NotContain("Remove any credential-like value");
    }

    [Fact]
    public void A_successful_save_sends_no_snapshot_without_a_schema_contract()
    {
        var (api, cut) = RenderPanel(new AltinnTestConfiguration(), HubGuidance());
        IntegrationMessageFlowPackage? sent = null;
        api.Setup(x => x.SaveMessageFlowReviewDetailedAsync("pilot", It.IsAny<IntegrationMessageFlowPackage>(), It.IsAny<CancellationToken>()))
            .Callback<string, IntegrationMessageFlowPackage, CancellationToken>((_, package, _) => sent = package)
            .ReturnsAsync((string _, IntegrationMessageFlowPackage package, CancellationToken _) => ((IntegrationMessageFlowPackage?)package, (MessageFlowSaveProblem?)null));
        cut.WaitForAssertion(() => cut.Find("[data-testid=mfr-save]"));
        cut.FindAll("label").Single(item => item.TextContent.Trim().StartsWith("Environment", StringComparison.Ordinal)).QuerySelector("select")!.Change("Test");
        cut.Find("[data-testid=mfr-save]").Click();
        cut.WaitForAssertion(() => sent.Should().NotBeNull());
        sent!.AltinnConfiguration.Environment.Should().Be(ExternalTestEnvironment.Test);
        sent.AltinnConfiguration.SourceSnapshotId.Should().BeNull("the bound snapshot is IQR's; it is only sent with a selected schema contract");
        cut.Find("[data-testid=mfr-saved]").TextContent.Should().Be("Saved");
    }
}
