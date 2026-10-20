using BirkNext.Api.Services.IntegrationJourneys;
using BirkNext.Api.Services.IntegrationJourneys.Packs.SkoleNaervaer;
using BirkNext.Api.Tests.Services.ActiveEventTesting;
using BirkNext.Api.Tests.Services.AltinnTesting;
using BirkNext.Integrations;
using BirkNext.SourceArchitecture;
using FluentAssertions;

namespace BirkNext.Api.Tests.Services.IntegrationJourneys;

/// <summary>
/// Add-integration templates: safe deterministic prefill from test-pack metadata, environment-aware endpoints (never production, never
/// guessed), the evidence source resolved from the pack's own source requirements (never bound by reading), and a generic second pack.
/// </summary>
public sealed class IntegrationTemplateTests
{
    private static SkoleNaervaerJourneyPack Pack(IntegrationJourneyHarness harness)
    {
        var kit = new AltinnTestKit();
        return new(harness.MessageFlows, kit.Policy, kit.Contracts(harness.MessageFlows, harness.Sources));
    }

    private static IqrSourceSnapshot Repo(string archive, char fill, params string[] components) => new()
    {
        Id = Guid.NewGuid(), Archive = new(archive, new string(fill, 64), 1), AnalyzedAt = DateTimeOffset.UtcNow,
        Architecture = new ArchitectureSnapshot { Components = [.. components.Select(name => new ArchitectureComponent { Id = name, Name = name, ComponentType = ArchitectureComponentType.Api })] },
    };

    private static async Task<IReadOnlyList<IntegrationTemplateView>> ReadAsync(IntegrationJourneyHarness harness, string? environmentType = null, string? environmentId = null) =>
        await harness.Service(Pack(harness)).IntegrationTemplatesAsync(environmentId ?? ActiveEventTestHarness.Env, environmentType, default);

    private static IntegrationTemplateView Template(IReadOnlyList<IntegrationTemplateView> templates, string key) => templates.Single(item => item.Key == key);

    [Fact]
    public async Task ReportIntakeTemplate_PrefillsTheKnownSafeFields_WithProvenance()
    {
        await using var harness = new IntegrationJourneyHarness();
        var templates = await ReadAsync(harness, "Development", environmentId: "");

        templates.Select(item => item.Key).Should().Equal("skolenaervaer-report-intake", "skolenaervaer-education-cdc", "skolenaervaer-period-closed");
        var intake = Template(templates, "skolenaervaer-report-intake");
        intake.Kind.Should().Be(IntegrationKind.HttpApi);
        intake.Values["DisplayName"].Value.Should().Be("Skolenærvær – Altinn report intake");
        intake.Values["SystemName"].Value.Should().Be("Altinn → Meldingsutveksleren (MU)");
        intake.Values["Producer"].Value.Should().Be("Altinn");
        intake.Values["Consumer"].Value.Should().Be("Meldingsutveksleren (MU)");
        intake.Values["EndpointOrTopic"].Should().Be(new IntegrationTemplateValue("https://buf-meldings-utveksler.azurewebsites.net/api/altinn/webhook", "Template"));
        intake.Journeys.Should().Equal("Rapportmottak");
        intake.EvidenceSource!.Role.Key.Should().Be("mu");
        intake.NeedsConfiguration.Should().BeEmpty();
    }

    [Fact]
    public async Task TemplatesNeverCarrySecretsCredentialsOrPersonalData()
    {
        await using var harness = new IntegrationJourneyHarness();
        var values = (await ReadAsync(harness, "Development", "")).SelectMany(item => item.Values.Values).Select(value => value.Value).ToArray();

        values.Should().NotContain(value => value.Contains("SharedAccessKey", StringComparison.OrdinalIgnoreCase) || value.Contains("password", StringComparison.OrdinalIgnoreCase)
            || value.Contains("secret", StringComparison.OrdinalIgnoreCase) || value.Contains("Bearer ", StringComparison.Ordinal) || value.Contains("Endpoint=sb://", StringComparison.OrdinalIgnoreCase)
            || value.Contains('@') || System.Text.RegularExpressions.Regex.IsMatch(value, @"\b\d{11}\b"));
    }

    [Fact]
    public async Task UnknownEndpointsStayEmpty_AndProductionNeverGetsATemplateEndpoint()
    {
        await using var harness = new IntegrationJourneyHarness();
        var cdc = Template(await ReadAsync(harness, "QA", ""), "skolenaervaer-education-cdc");
        cdc.Values.Should().NotContainKey("EndpointOrTopic", "the Event Hub name varies per environment and is not known to the pack");
        cdc.NeedsConfiguration.Should().Contain(["EndpointOrTopic", "SourceResource"]);

        var production = await ReadAsync(harness, "Production", "");
        Template(production, "skolenaervaer-report-intake").Values.Should().NotContainKey("EndpointOrTopic");
        Template(production, "skolenaervaer-report-intake").NeedsConfiguration.Should().Contain("EndpointOrTopic");
        Template(production, "skolenaervaer-period-closed").Values.Should().NotContainKey("EndpointOrTopic");
    }

    [Fact]
    public async Task EnvironmentOverridesResolveDeterministically_ByEnvironmentKind()
    {
        var template = new IntegrationTemplateDefinition("t", "T", "", IntegrationKind.HttpApi)
        {
            EndpointOrTopic = "https://shared.example.test/hook",
            EndpointByEnvironment = new Dictionary<string, string> { ["Development"] = "https://dev.example.test/hook", ["QA"] = "https://qa.example.test/hook" },
        };
        await using var harness = new IntegrationJourneyHarness();
        var pack = new TemplatePack(template);
        var context = new JourneyPackContext("", null, "", new IntegrationCatalog(), []);

        IntegrationTemplates.Resolve(pack, context, template, "development").Values["EndpointOrTopic"].Should().Be(new IntegrationTemplateValue("https://dev.example.test/hook", "Template · Development"));
        IntegrationTemplates.Resolve(pack, context, template, "QA").Values["EndpointOrTopic"].Value.Should().Be("https://qa.example.test/hook");
        IntegrationTemplates.Resolve(pack, context, template, "Test").Values["EndpointOrTopic"].Should().Be(new IntegrationTemplateValue("https://shared.example.test/hook", "Template"));
        IntegrationTemplates.Resolve(pack, context, template, "Production").Values.Should().NotContainKey("EndpointOrTopic");
    }

    [Fact]
    public async Task EvidenceSource_IsMissingAvailableOrAmbiguous_AndReadingNeverBinds()
    {
        await using var harness = new IntegrationJourneyHarness();
        Template(await ReadAsync(harness), "skolenaervaer-report-intake").EvidenceSource!.State.Should().Be(TestPackSourceState.Missing);

        harness.Sources.Snapshots.Add(Repo("Tiltak 2 - Sikkert digitalt økosystem (1).zip", 'a', "Buf.Meldingsutveksler.Service"));
        var available = Template(await ReadAsync(harness), "skolenaervaer-report-intake").EvidenceSource!;
        available.State.Should().Be(TestPackSourceState.AvailableNotBound);
        available.Candidates.Single().Bound.Should().BeFalse();
        available.Candidates.Single().ArchiveFileName.Should().Be("Tiltak 2 - Sikkert digitalt økosystem (1).zip");

        harness.Sources.Snapshots.Add(Repo("mu-source.zip", 'b', "Buf.Meldingsutveksler.Api"));
        var ambiguous = Template(await ReadAsync(harness), "skolenaervaer-report-intake").EvidenceSource!;
        ambiguous.State.Should().Be(TestPackSourceState.ReviewRequired, "several matches are the tester's choice, never the newest");
        harness.Bindings.Items.Should().BeEmpty("reading templates never creates a binding (SetAsync would throw)");
    }

    [Fact]
    public async Task AnExistingRecordThatFulfilsTheRequirement_IsReportedAsAlreadyConfigured()
    {
        await using var harness = new IntegrationJourneyHarness();
        harness.Active.Catalog.Extra.Add(new IntegrationDefinition
        {
            Id = "dev:http:intake", EnvironmentId = ActiveEventTestHarness.Env, DisplayName = "Report intake", Kind = IntegrationKind.HttpApi, Enabled = true,
            Producer = "Altinn", Consumer = new IntegrationConsumer { DisplayName = "MU" },
        });
        Template(await ReadAsync(harness), "skolenaervaer-report-intake").AlreadyConfigured.Should().Equal("Report intake");
    }

    [Fact]
    public async Task AnotherPack_UsesTheSameTemplateMechanism()
    {
        var template = new IntegrationTemplateDefinition("ledger-events", "Ledger – posting events", "Ledger publishes postings.", IntegrationKind.ServiceBus)
        {
            Producer = "Ledger", Consumer = "Reporting", EndpointOrTopic = "ledger-postings",
        };
        await using var harness = new IntegrationJourneyHarness();
        var templates = await harness.Service(new TemplatePack(template)).IntegrationTemplatesAsync("", "Development", default);
        var view = templates.Single();
        (view.PackId, view.Kind, view.Values["EndpointOrTopic"].Value, view.Values["Consumer"].Value).Should().Be(("demo.ledger", IntegrationKind.ServiceBus, "ledger-postings", "Reporting"));
        view.EvidenceSource.Should().BeNull("this template declares no evidence source");
    }

    private sealed class TemplatePack(params IntegrationTemplateDefinition[] templates) : IIntegrationJourneyPack
    {
        public string PackId => "demo.ledger";
        public string PackVersion => "1";
        public string DisplayName => "Ledger";
        public string Description => "A generic pack with one integration template.";
        public IReadOnlyList<string> Limitations => [];
        public IReadOnlyList<IntegrationJourneyDefinition> Journeys { get; } = [];
        public IReadOnlyList<IntegrationTemplateDefinition> IntegrationTemplates { get; } = templates;
        public IReadOnlyList<ArchitectureResponsibilityRule> Rules => [];
        public bool Matches(JourneyIntegrationRequirement requirement, IntegrationDefinition integration) => false;
        public Task<IReadOnlyList<JourneyPrerequisite>> PrerequisitesAsync(IntegrationJourneyDefinition journey, JourneyPackContext context, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<JourneyPrerequisite>>([]);
    }
}
