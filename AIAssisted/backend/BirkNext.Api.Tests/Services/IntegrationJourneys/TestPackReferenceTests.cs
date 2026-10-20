using BirkNext.Api.Services.IntegrationJourneys;
using BirkNext.Api.Services.IntegrationJourneys.Packs.SkoleNaervaer;
using BirkNext.Api.Tests.Services.AltinnTesting;
using BirkNext.Api.Tests.Services.SourceAnalysis.Evidence;
using BirkNext.Integrations;
using BirkNext.SourceArchitecture;
using BirkNext.SourceDomains;
using FluentAssertions;

namespace BirkNext.Api.Tests.Services.IntegrationJourneys;

/// <summary>
/// Test Pack references: metadata a pack ships itself, distinct from imported Source Analysis evidence. A reference loads (or is unavailable
/// with the reason), is never a snapshot, binding or runtime fact, and the same model serves any pack.
/// </summary>
public sealed class TestPackReferenceTests
{
    private static SkoleNaervaerJourneyPack Skolenaervaer(IntegrationJourneyHarness harness)
    {
        var kit = new AltinnTestKit();
        return new(harness.MessageFlows, kit.Policy, kit.Contracts(harness.MessageFlows, harness.Sources));
    }

    private static TestPackSourceStatus Source(SourceSetupView setup, string pack, string role) =>
        setup.Packs.Single(item => item.PackId == pack).Sources.Single(item => item.Role.Key == role);

    [Fact]
    public void TheBuiltInAltinnReference_LoadsFromTheBuild_WithItsSafeFacts()
    {
        var (view, loaded) = TestPackReferences.Load(SkoleNaervaerAltinnReference.Reference("altinn-app"));
        loaded.Should().BeTrue(string.Join(" ", view.Diagnostics));
        view.Kind.Should().Be("OpenAPI");
        view.Facts.Select(fact => (fact.Label, fact.Value)).Should().Equal(
            ("App identifier", "buf/skolenaervaer"), ("API title", "Altinn 3 App API for School attendance"),
            ("Server · Local", "http://local.altinn.cloud"), ("Server · TT02", "https://buf.apps.tt02.altinn.no"), ("Server · Production", "https://buf.apps.altinn.no"),
            ("App base URL · TT02", "https://buf.apps.tt02.altinn.no/buf/skolenaervaer"),
            ("Form data type", "bufdir_barnevern_tiltak_skolenaervaer_v2_0_0_altinn"), ("Content type", "application/xml"),
            ("Process task", "Task_1"), ("Task name", "Utfylling"), ("Authentication scheme", "Bearer JWT"), ("Security scheme name", "AltinnToken"),
            ("Token providers", "maskinporten, id-porten"));
        view.Provenance.Should().Equal("OpenAPI 3.0.4", "Document version (info.version) 8.12.7.264");
        view.Fingerprint.Should().Be(OpenApiConfigurationEvidenceTests.FixtureSha256);
        view.Limitation.Should().Contain("not canonical source evidence").And.Contain("does not verify the deployed application");
        view.NotProven.Should().Contain(item => item.Contains("client secret", StringComparison.Ordinal)).And.Contain(item => item.Contains("runtime health", StringComparison.Ordinal));
        var text = System.Text.Json.JsonSerializer.Serialize(view);
        text.Should().NotContain("testdata").And.NotContain("skolenaervaer.json", "the internal fixture path is never part of the reference");
        view.Facts.Should().NotContain(fact => fact.Label.Contains("Client", StringComparison.OrdinalIgnoreCase) || fact.Label.Contains("Scope", StringComparison.OrdinalIgnoreCase)
            || fact.Label.Contains("Reporter", StringComparison.OrdinalIgnoreCase), "nothing the document does not declare is added");
    }

    [Fact]
    [Trait("Category", "LocalFixture")]
    public void EveryReferenceFact_IsWhatTheGenericOpenApiReaderExtractsFromTheFixture()
    {
        if (OpenApiConfigurationEvidenceTests.Fixture() is not { } text) return;
        var values = SourceEvidenceFixtures.Analyze(("skolenaervaer.json", text)).ConfigurationValues!.Values;
        var (view, _) = TestPackReferences.Load(SkoleNaervaerAltinnReference.Reference("altinn-app"));
        foreach (var fact in view.Facts.Where(fact => fact.SemanticKey is not null && fact.DerivedFrom.Count > 0))
        {
            // A composed fact is exactly its parts as the document declares them (server + "/" + app route), never an invented address.
            var parts = fact.DerivedFrom.Select(key => values.Single(value => value.SemanticKey == key).Value!.TrimEnd('/')).ToArray();
            fact.Value.Should().Be(string.Join("/", parts), $"{fact.Label} is composed from {string.Join(" + ", fact.DerivedFrom)}");
        }
        foreach (var fact in view.Facts.Where(fact => fact.SemanticKey is not null && fact.DerivedFrom.Count == 0))
        {
            var declared = values.Where(value => value.SemanticKey == fact.SemanticKey).Select(value => value.Value).Distinct().ToArray();
            string.Join(", ", declared).Should().Be(fact.Value, $"{fact.Label} must be exactly what the document declares");
        }
        values.Single(value => value.SemanticKey == SourceConfigurationKeys.AuthenticationScheme).Attributes["Name"].Should().Be("AltinnToken");
    }

    [Fact]
    public async Task TheAltinnApp_IsAvailableAsATestPackReference_WithoutSnapshotBindingOrRuntimeState()
    {
        await using var harness = new IntegrationJourneyHarness();
        var setup = await harness.Service(Skolenaervaer(harness)).SourceSetupAsync(ActiveEventTesting.ActiveEventTestHarness.Env, default);
        var altinn = Source(setup, SkoleNaervaerJourneyPack.Id, "altinn-app");
        (altinn.Role.Provider, altinn.State).Should().Be((TestPackSourceProvider.TestPackReference, TestPackSourceState.ReferenceAvailable));
        altinn.Role.Label.Should().Be("Altinn application/API");
        altinn.Reference!.Facts.Should().NotBeEmpty();
        altinn.Detail.Should().Contain("No separate import is required");
        altinn.Candidates.Should().BeEmpty();
        altinn.NearMisses.Should().BeEmpty();
        setup.Snapshots.Should().BeEmpty("a reference creates no Source Analysis snapshot");
        harness.Bindings.Items.Should().BeEmpty("a reference creates no IQR binding");
        Source(setup, SkoleNaervaerJourneyPack.Id, "mu").State.Should().Be(TestPackSourceState.Missing, "MU stays a required imported source");
        Source(setup, SkoleNaervaerJourneyPack.Id, "m2lb").State.Should().Be(TestPackSourceState.Missing, "M2LB stays a required imported source");
        Source(setup, SkoleNaervaerJourneyPack.Id, "infrastructure").State.Should().Be(TestPackSourceState.NotProvided, "Infrastructure as Code stays optional");
    }

    // ── Genericity: another pack with an imported source, a reference and an optional source ──────────────────────────────

    private sealed class LedgerPack(Func<TestPackReferenceDocument> load) : IIntegrationJourneyPack
    {
        public string PackId => "demo.ledger";
        public string PackVersion => "1";
        public string DisplayName => "Ledger";
        public string Description => "A generic pack with one imported source, one built-in reference and one optional source.";
        public IReadOnlyList<string> Limitations => [];
        public IReadOnlyList<IntegrationJourneyDefinition> Journeys { get; } = [];
        public IReadOnlyList<ArchitectureResponsibilityRule> Rules => [];
        public IReadOnlyList<TestPackSourceRole> SourceRoles { get; } =
        [
            new("ledger-service", "Ledger service", "The service repository.") { ComponentTokens = ["Ledger.Service"] },
            new("ledger-api", "Ledger API", "The ledger's API description.") { Provider = TestPackSourceProvider.TestPackReference },
            new("ledger-infra", "Ledger infrastructure", "Declared infrastructure.") { RequiresInfrastructureAsCode = true, BindingRequired = false, Requirement = TestPackSourceRequirement.Optional },
        ];
        public IReadOnlyList<TestPackReference> References => [new("ledger-api", "OpenAPI", "Ledger API reference", load)];
        public bool Matches(JourneyIntegrationRequirement requirement, IntegrationDefinition integration) => false;
        public Task<IReadOnlyList<JourneyPrerequisite>> PrerequisitesAsync(IntegrationJourneyDefinition journey, JourneyPackContext context, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<JourneyPrerequisite>>([]);
    }

    private static readonly TestPackReferenceDocument LedgerDocument = new([new("API title", "Ledger API") { SemanticKey = SourceConfigurationKeys.ApplicationDisplayName }]);

    [Fact]
    public async Task AnotherPack_DeclaresImportedReferenceAndOptionalSources_WithTheSameModel()
    {
        await using var harness = new IntegrationJourneyHarness();
        var setup = await harness.Service(new LedgerPack(() => LedgerDocument)).SourceSetupAsync("", default);
        Source(setup, "demo.ledger", "ledger-service").State.Should().Be(TestPackSourceState.Missing);
        Source(setup, "demo.ledger", "ledger-api").State.Should().Be(TestPackSourceState.ReferenceAvailable);
        Source(setup, "demo.ledger", "ledger-api").Reference!.Facts.Single().Value.Should().Be("Ledger API");
        Source(setup, "demo.ledger", "ledger-infra").State.Should().Be(TestPackSourceState.NotProvided);
    }

    [Theory]
    [InlineData("throws")]
    [InlineData("empty")]
    [InlineData("credential")]
    public async Task AReferenceThatCannotBeLoaded_IsUnavailable_WithTheReason_NeverMissingSource(string failure)
    {
        Func<TestPackReferenceDocument> load = failure switch
        {
            "throws" => () => throw new InvalidDataException("The built-in reference ledger.reference.json is not valid JSON."),
            "empty" => () => new TestPackReferenceDocument([]),
            _ => () => new TestPackReferenceDocument([new("Client secret", "client_secret=abc123")]),
        };
        await using var harness = new IntegrationJourneyHarness();
        var setup = await harness.Service(new LedgerPack(load)).SourceSetupAsync("", default);
        var api = Source(setup, "demo.ledger", "ledger-api");
        api.State.Should().Be(TestPackSourceState.ReferenceUnavailable);
        api.Reference!.Diagnostics.Should().NotBeEmpty();
        api.Reference.Facts.Should().BeEmpty("an unavailable reference supplies nothing");
        api.Detail.Should().Contain("could not be loaded").And.NotContain("Import");
        api.NextAction.Should().BeEmpty("importing is not the recovery path for a built-in reference");
        if (failure == "credential") System.Text.Json.JsonSerializer.Serialize(api).Should().NotContain("abc123", "a rejected value is never echoed");
    }

    [Fact]
    public void AMissingEmbeddedReference_IsReportedAsNotPartOfTheBuild()
    {
        var act = () => TestPackReferences.Embedded("does-not-exist.reference.json");
        act.Should().Throw<FileNotFoundException>().WithMessage("*not part of this build*");
    }
}
