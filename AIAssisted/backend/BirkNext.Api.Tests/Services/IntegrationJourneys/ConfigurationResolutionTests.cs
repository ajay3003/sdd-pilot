using BirkNext.Api.Services.IntegrationJourneys;
using BirkNext.Integrations;
using BirkNext.SourceArchitecture;
using BirkNext.SourceDomains;
using FluentAssertions;

namespace BirkNext.Api.Tests.Services.IntegrationJourneys;

/// <summary>
/// The one shared precedence for safe configuration values (stored → configured integration → canonical source → Test Pack reference → pack
/// default), its visible disagreements, and a non-Skolenærvær pack that uses the same resolver with environment-scoped integration records.
/// </summary>
public sealed class ConfigurationResolutionTests
{
    private static readonly SourceConfigurationRequirement Requirement = new("endpoint", "Endpoint", SourceConfigurationKeys.EndpointEventWebhook, "Ledger", "Configuration", "p", "IQR");

    private static SourceConfigurationSuggestion Suggestion(string? configured = null, string? integration = null, string? canonical = null, string? reference = null, string? fallback = null) =>
        new(Requirement, canonical is null ? SourceSuggestionState.NotDeclared : SourceSuggestionState.Suggested)
        {
            ConfiguredValue = configured, IntegrationValue = integration, Value = canonical, ReferenceValue = reference, DefaultValue = fallback,
        };

    [Theory]
    [InlineData("https://configured.test", "https://integration.test", "https://source.test", "https://reference.test", "https://default.test", ConfigurationValueOrigin.Configured)]
    [InlineData(null, "https://integration.test", "https://source.test", "https://reference.test", "https://default.test", ConfigurationValueOrigin.ConfiguredIntegration)]
    [InlineData(null, null, "https://source.test", "https://reference.test", "https://default.test", ConfigurationValueOrigin.CanonicalSource)]
    [InlineData(null, null, null, "https://reference.test", "https://default.test", ConfigurationValueOrigin.TestPackReference)]
    [InlineData(null, null, null, null, "https://default.test", ConfigurationValueOrigin.TestPackDefault)]
    [InlineData(null, null, null, null, null, ConfigurationValueOrigin.None)]
    public void EachTier_WinsOnlyWhenEveryHigherTierIsEmpty(string? configured, string? integration, string? canonical, string? reference, string? fallback, ConfigurationValueOrigin expected)
    {
        var resolved = SourceConfigurationPrecedence.Resolve(Suggestion(configured, integration, canonical, reference, fallback));
        resolved.ResolvedFrom.Should().Be(expected);
        resolved.ResolvedValue.Should().Be(configured ?? integration ?? canonical ?? reference ?? fallback);
    }

    [Fact]
    public void LowerTiersThatDisagree_StayVisible_AndADefaultNeverDisagrees()
    {
        var resolved = SourceConfigurationPrecedence.Resolve(Suggestion(configured: "https://x.test", canonical: "https://y.test", reference: "https://z.test", fallback: "https://d.test"));
        (resolved.ResolvedValue, resolved.ResolvedFrom).Should().Be(("https://x.test", ConfigurationValueOrigin.Configured));
        resolved.Disagreements.Should().Equal(new ConfigurationAlternative(ConfigurationValueOrigin.CanonicalSource, "https://y.test"),
            new ConfigurationAlternative(ConfigurationValueOrigin.TestPackReference, "https://z.test"));
        resolved.ReviewRequired.Should().BeTrue("the person sees configured X, source Y and reference Z — nothing is merged");

        var agreeing = SourceConfigurationPrecedence.Resolve(Suggestion(configured: "https://x.test/", reference: "HTTPS://X.TEST", fallback: "https://other.test"));
        agreeing.Disagreements.Should().BeEmpty("the same value written differently agrees, and a default only fills gaps");
        agreeing.ReviewRequired.Should().BeFalse();
    }

    [Fact]
    public void ACanonicalSourceConflict_IsReviewRequired_EvenWhenAReferenceFillsTheValue()
    {
        var resolved = SourceConfigurationPrecedence.Resolve(new SourceConfigurationSuggestion(Requirement, SourceSuggestionState.Conflict) { ReferenceValue = "https://reference.test" });
        resolved.ResolvedFrom.Should().Be(ConfigurationValueOrigin.TestPackReference, "a conflict offers no canonical value");
        resolved.ReviewRequired.Should().BeTrue();
    }

    // ── A non-Skolenærvær pack: integration-owned value, reference, default — environment-scoped ──────────────────────────────

    private sealed class LedgerPack : IIntegrationJourneyPack
    {
        public string PackId => "demo.ledger";
        public string PackVersion => "1";
        public string DisplayName => "Ledger";
        public string Description => "A generic pack whose configuration values come from an integration record, a built-in reference and a default.";
        public IReadOnlyList<string> Limitations => [];
        public IReadOnlyList<IntegrationJourneyDefinition> Journeys { get; } =
        [
            new() { PackId = "demo.ledger", JourneyId = "posting", DisplayName = "Posting", Order = 1,
                Integrations = [new JourneyIntegrationRequirement("ledger-feed", "Ledger feed", IntegrationKind.HttpApi, "Ledger", "Reporting", "Postings to reporting.")] },
        ];
        public IReadOnlyList<ArchitectureResponsibilityRule> Rules => [];
        public IReadOnlyList<TestPackSourceRole> SourceRoles { get; } = [new("ledger-api", "Ledger API", "API description") { Provider = TestPackSourceProvider.TestPackReference }];
        public IReadOnlyList<TestPackReference> References => [new("ledger-api", "OpenAPI", "Ledger API reference",
            () => new TestPackReferenceDocument([new("App identifier", "ledger/app") { SemanticKey = SourceConfigurationKeys.ApplicationRoute }]))];
        public IReadOnlyList<SourceConfigurationRequirement> RequiredConfiguration { get; } =
        [
            new("feed-endpoint", "Feed endpoint", SourceConfigurationKeys.EndpointEventWebhook, "Ledger", "Configuration", "Where postings go.", "Integrations")
                { IntegrationRequirementKey = "ledger-feed", IntegrationField = nameof(IntegrationDefinition.EndpointOrTopic), SourceRoles = ["ledger-api"] },
            new("app-route", "App identifier", SourceConfigurationKeys.ApplicationRoute, "Ledger", "OpenAPI", "Which app.", "Ledger settings") { SourceRoles = ["ledger-api"] },
            new("environment", "Environment", SourceConfigurationKeys.ApplicationEnvironment, "Ledger", "Configuration", "Which platform.", "Ledger settings") { DefaultValue = "Test", SourceRoles = ["ledger-api"] },
        ];
        public bool Matches(JourneyIntegrationRequirement requirement, IntegrationDefinition integration) =>
            requirement.Key == "ledger-feed" && integration.Producer == "Ledger";
        public Task<IReadOnlyList<JourneyPrerequisite>> PrerequisitesAsync(IntegrationJourneyDefinition journey, JourneyPackContext context, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<JourneyPrerequisite>>([]);
    }

    private static JourneyPackContext Context(string environment, string? endpoint) => new(environment, null, "", new IntegrationCatalog
    {
        EnvironmentId = environment,
        Integrations = endpoint is null ? [] : [new IntegrationDefinition { Id = $"{environment}:ledger", EnvironmentId = environment, DisplayName = $"Ledger feed {environment}", Kind = IntegrationKind.HttpApi, Enabled = true, Producer = "Ledger", EndpointOrTopic = endpoint }],
    }, []);

    [Fact]
    public async Task AnotherPack_ResolvesIntegrationReferenceAndDefault_PerEnvironment_WithoutLeaking()
    {
        await using var harness = new IntegrationJourneyHarness();
        var pack = new LedgerPack();
        var service = harness.Service(pack);

        var dev = (await service.ResolveConfigurationAsync(pack, Context("dev", "https://ledger-dev.test/feed"), default)).ToDictionary(item => item.Requirement.Key);
        var qa = (await service.ResolveConfigurationAsync(pack, Context("qa", "https://ledger-qa.test/feed"), default)).ToDictionary(item => item.Requirement.Key);
        var prod = (await service.ResolveConfigurationAsync(pack, Context("prod", null), default)).ToDictionary(item => item.Requirement.Key);

        (dev["feed-endpoint"].ResolvedValue, dev["feed-endpoint"].ResolvedFrom, dev["feed-endpoint"].IntegrationName).Should().Be(("https://ledger-dev.test/feed", ConfigurationValueOrigin.ConfiguredIntegration, "Ledger feed dev"));
        qa["feed-endpoint"].ResolvedValue.Should().Be("https://ledger-qa.test/feed", "QA reads QA's record, never Development's");
        prod["feed-endpoint"].ResolvedValue.Should().BeNull("an environment without a record stays unresolved — nothing is borrowed or guessed");
        (dev["app-route"].ResolvedValue, dev["app-route"].ResolvedFrom).Should().Be(("ledger/app", ConfigurationValueOrigin.TestPackReference));
        (dev["environment"].ResolvedValue, dev["environment"].ResolvedFrom).Should().Be(("Test", ConfigurationValueOrigin.TestPackDefault));
        dev.Values.Should().OnlyContain(item => !item.RuntimeVerified);
        harness.Bindings.Items.Should().BeEmpty("resolving never binds");
    }
}
