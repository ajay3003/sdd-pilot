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
            .And.BeEquivalentTo(["Not declared in source", "Available but not bound", "Suggested from bound source", "Configuration conflict"]);
        Suggested().RuntimeVerified.Should().BeFalse();
        SourceGuidancePresentation.Configured(Suggested()).Should().Be("Not configured");
        SourceGuidancePresentation.Configured(Suggested("https://other.invalid")).Should().Be("https://other.invalid (differs from source)");
        SourceGuidancePresentation.Configured(Suggested() with { Requirement = Requirement(null) }).Should().Be("No field to configure here");
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
        cut.Find("[data-testid=sd-config-declared-note]").TextContent.Should().Contain("runtime not verified");
        cut.FindAll("[data-testid=sd-config-declared-group]").Should().HaveCount(4);
        var test = cut.Find("[data-testid=sd-config-declared-row][data-key='Environment.Server.Test']");
        test.TextContent.Should().Contain("https://parcels.test.example.invalid").And.Contain("OpenAPI · api/parcels.json → servers[0]").And.Contain("Declared in source").And.Contain("Runtime not verified");
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

    // ── Altinn provider: app identifier prefill from the bound source ───────────────────────────────────────────────

    [Fact]
    public void The_Altinn_app_identifier_offers_the_bound_route_and_using_it_does_not_save()
    {
        var api = new Mock<IIntegrationCatalogApiService>();
        var config = new AltinnTestConfiguration { AppIdentifier = null };
        api.Setup(x => x.GetMessageFlowReviewAsync("pilot", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IntegrationMessageFlowPackage(new MessageFlowDefinition { EnvironmentId = "pilot", Name = "Message flow review" }, config, new(false, 0, 0, [], []), [], []));
        api.Setup(x => x.IqrSourceScopeAsync("pilot", null, It.IsAny<CancellationToken>())).ReturnsAsync(new BirkNext.SourceEvidence.ReviewSourceOptions());
        var bound = Guid.NewGuid();
        api.Setup(x => x.ListEvidenceBindingsAsync("pilot", It.IsAny<CancellationToken>()))
            .ReturnsAsync([new IntegrationEvidenceBinding { EnvironmentId = "pilot", IntegrationId = "pilot:report-intake", SnapshotId = bound, ArchiveName = "app.zip", Fingerprint = new string('e', 64) }]);
        api.Setup(x => x.SourceConfigurationAsync("pilot", "pilot:report-intake", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IntegrationSourceConfiguration("pilot:report-intake", null, [Resolution(SourceConfigurationResolutionState.Declared, "shop/parcels")]));
        var journeys = new Mock<IIntegrationJourneysApiService>();
        journeys.Setup(x => x.PacksAsync("pilot", It.IsAny<CancellationToken>())).ReturnsAsync(new ActiveEventApiResult<IReadOnlyList<IntegrationJourneyPackView>>(
            [new IntegrationJourneyPackView { Journeys = [new IntegrationJourneyView { MatchedIntegrations = [new("report-intake", "pilot:report-intake", "Report intake", IntegrationKind.HttpApi, true, "Configured.")] }] }],
            null, HttpStatusCode.OK));
        Services.AddSingleton(api.Object);
        Services.AddSingleton(journeys.Object);

        var cut = Render<MessageFlowReviewPanel>(p => p.Add(x => x.EnvironmentId, "pilot"));
        cut.WaitForAssertion(() => cut.Find("[data-testid=mfr-app-suggestion-value]").TextContent.Should().Be("shop/parcels"));
        cut.Find("[data-testid=mfr-app-suggestion-use]").Click();
        cut.FindAll("label").Single(label => label.TextContent.Trim().StartsWith("App identifier", StringComparison.Ordinal)).QuerySelector("input")!.GetAttribute("value").Should().Be("shop/parcels");
        api.Verify(x => x.SaveMessageFlowReviewAsync(It.IsAny<string>(), It.IsAny<IntegrationMessageFlowPackage>(), It.IsAny<CancellationToken>()), Times.Never,
            "a suggestion fills the form; only Save stores it");
    }

    [Fact]
    public void Without_a_binding_no_source_value_is_offered_or_requested()
    {
        var api = new Mock<IIntegrationCatalogApiService>();
        api.Setup(x => x.GetMessageFlowReviewAsync("pilot", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IntegrationMessageFlowPackage(new MessageFlowDefinition { EnvironmentId = "pilot", Name = "Message flow review" }, new AltinnTestConfiguration(), new(false, 0, 0, [], []), [], []));
        api.Setup(x => x.IqrSourceScopeAsync("pilot", null, It.IsAny<CancellationToken>())).ReturnsAsync(new BirkNext.SourceEvidence.ReviewSourceOptions());
        api.Setup(x => x.ListEvidenceBindingsAsync("pilot", It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var journeys = new Mock<IIntegrationJourneysApiService>();
        journeys.Setup(x => x.PacksAsync("pilot", It.IsAny<CancellationToken>())).ReturnsAsync(new ActiveEventApiResult<IReadOnlyList<IntegrationJourneyPackView>>(
            [new IntegrationJourneyPackView { Journeys = [new IntegrationJourneyView { MatchedIntegrations = [new("report-intake", "pilot:report-intake", "Report intake", IntegrationKind.HttpApi, true, "Configured.")] }] }],
            null, HttpStatusCode.OK));
        Services.AddSingleton(api.Object);
        Services.AddSingleton(journeys.Object);
        var cut = Render<MessageFlowReviewPanel>(p => p.Add(x => x.EnvironmentId, "pilot"));
        cut.WaitForAssertion(() => cut.Find("[data-testid=mfr-source-unbound]"));
        cut.FindAll("[data-testid=mfr-app-suggestion]").Should().BeEmpty();
        api.Verify(x => x.SourceConfigurationAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never, "the newest snapshot never stands in for a binding");
    }
}
