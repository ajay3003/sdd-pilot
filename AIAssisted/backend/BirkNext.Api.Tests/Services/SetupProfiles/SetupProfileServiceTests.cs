using BirkNext.Api.Services.ActiveEventTesting;
using BirkNext.Api.Services.IntegrationJourneys;
using BirkNext.Api.Services.IntegrationJourneys.Packs.SkoleNaervaer;
using BirkNext.Api.Services.Integrations;
using BirkNext.Api.Services.SetupProfiles;
using BirkNext.Api.Tests.Services.ActiveEventTesting;
using BirkNext.Api.Tests.Services.AltinnTesting;
using BirkNext.Api.Tests.Services.IntegrationJourneys;
using BirkNext.Integrations;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace BirkNext.Api.Tests.Services.SetupProfiles;

/// <summary>
/// Setup profiles: validate and preview never mutate; apply changes only selected safe values through their owners; secrets, identities, trust,
/// approvals and verification state are rejected, never stripped silently; conflicts are never overwritten; a technical integration that already
/// represents a journey is reused, never duplicated; export is sanitized and re-imports as "Same".
/// </summary>
public sealed class SetupProfileServiceTests : IAsyncLifetime
{
    private const string Env = ActiveEventTestHarness.Env;
    private readonly IntegrationJourneyHarness _harness = new();
    private readonly Catalog _catalog = new();
    private readonly Bindings _bindings = new();

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync() => await _harness.DisposeAsync();

    /// <summary>A catalog that records writes (create/update) so tests can prove what was — and was not — changed.</summary>
    private sealed class Catalog : IIntegrationCatalogService
    {
        public List<IntegrationDefinition> Items { get; } = [];
        public List<IntegrationPlatform> Platforms { get; } =
            [new() { Id = "dev:eventhub:m2lb", EnvironmentId = Env, Name = "Event Hubs", Kind = IntegrationKind.EventHub, NamespaceFqdn = ActiveEventTestHarness.Fqdn },
             new() { Id = "dev:servicebus:m2lb", EnvironmentId = Env, Name = "Service Bus", Kind = IntegrationKind.ServiceBus }];
        public int Writes { get; private set; }
        public Task<IntegrationCatalog> GetAsync(string environmentId, string? environmentType, string? targetUrl, CancellationToken ct = default) =>
            Task.FromResult(new IntegrationCatalog { EnvironmentId = environmentId, Platforms = [.. Platforms], Integrations = [.. Items] });
        public Task<IntegrationTemplateApplyResult?> ApplyTemplateAsync(string environmentId, string templateId, string? environmentType, string? targetUrl, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IntegrationDefinition> CreateAsync(string environmentId, IntegrationDefinition definition, CancellationToken ct = default)
        {
            Writes++;
            var created = definition with { Id = $"dev:{definition.Kind}:{Items.Count}" };
            Items.Add(created);
            return Task.FromResult(created);
        }
        public Task<IntegrationDefinition?> UpdateAsync(string environmentId, string id, IntegrationDefinition definition, CancellationToken ct = default)
        {
            Writes++;
            var index = Items.FindIndex(item => item.Id == id);
            Items[index] = definition;
            return Task.FromResult<IntegrationDefinition?>(definition);
        }
        public Task<IntegrationDefinition?> SetEnabledAsync(string environmentId, string id, bool enabled, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(string environmentId, string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IntegrationPlatform?> UpdatePlatformAsync(string environmentId, string id, IntegrationPlatform platform, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> ImportLegacyAsync(string environmentId, IReadOnlyList<BirkNext.Api.Services.IntegrationQuality.IntegrationConfigDto> legacy, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class Bindings : Api.Services.Integrations.SourceEvidence.IIntegrationEvidenceBindingStore
    {
        public List<IntegrationEvidenceBinding> Items { get; } = [];
        public List<IqrSourceSnapshot> Snapshots { get; set; } = [];
        public Task<IReadOnlyList<IntegrationEvidenceBinding>> ListAsync(string environmentId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<IntegrationEvidenceBinding>>([.. Items]);
        public Task<(IntegrationEvidenceBinding? Binding, string? Error)> SetAsync(string environmentId, IntegrationEvidenceBindingRequest request, CancellationToken ct = default)
        {
            var snapshot = Snapshots.Single(item => item.Id == request.SnapshotId);
            var binding = IntegrationEvidenceBinding.For(environmentId, request.IntegrationId, snapshot, DateTimeOffset.UtcNow);
            Items.RemoveAll(item => item.IntegrationId == request.IntegrationId);
            Items.Add(binding);
            return Task.FromResult<(IntegrationEvidenceBinding?, string?)>((binding, null));
        }
    }

    private SkoleNaervaerJourneyPack Pack()
    {
        var kit = new AltinnTestKit();
        return new(_harness.MessageFlows, kit.Policy, kit.Contracts(_harness.MessageFlows, _harness.Sources));
    }

    private SetupProfileService Service() => new(new IntegrationJourneyPackRegistry([Pack()]), _catalog, _bindings, _harness.Sources,
        TrustedExecutionEnvironmentRegistry.From(_harness.Active.Configuration()), NullLogger<SetupProfileService>.Instance);

    private Task<SetupProfilePreview> Preview(string content, string file = "profile.yaml") => Service().PreviewAsync(Env, SkoleNaervaerJourneyPack.Id, new(file, content), default);

    private IqrSourceSnapshot Snapshot(string archive, string sha)
    {
        var snapshot = new IqrSourceSnapshot { Archive = new(archive, sha, 1), AnalyzedAt = DateTimeOffset.UtcNow };
        _harness.Sources.Snapshots.Add(snapshot);
        _bindings.Snapshots = [.. _harness.Sources.Snapshots];
        return snapshot;
    }

    private static readonly IntegrationDefinition SkoleReports = new()
    {
        Id = "dev:servicebus:skole-reports", EnvironmentId = Env, PlatformId = "dev:servicebus:m2lb", DisplayName = "skole.reports", Kind = IntegrationKind.ServiceBus, Enabled = true,
        EndpointOrTopic = "skole.reports", Producer = "M2LB.SkoleService.Api",
    };

    private static readonly IntegrationDefinition AltinnIntake = new()
    {
        Id = "dev:http:altinn", EnvironmentId = Env, DisplayName = "Skolenærvær – Altinn report intake", Kind = IntegrationKind.HttpApi, Enabled = true,
        Producer = "Altinn", Consumer = new IntegrationConsumer { DisplayName = "Meldingsutveksleren (MU)" }, EndpointOrTopic = "https://mu.example.test/api/altinn/webhook",
    };

    private const string Profile = """
        version: 1
        profile:
          name: "Skolenærvær Development"
          testPack: "skolenaervaer"
          environment: "Development"
        journeys:
          periodeavslutning:
            integration:
              logicalName: "Skolenærvær – Period Closed"
              type: "service-bus"
              producerRole: "Skolenærværsrapport"
              producerImplementation: "M2LB.SkoleService.Api"
              channel:
                name: "skole.reports"
              consumer:
                logicalName: "Bufdata"
            sourceEvidence:
              snapshotName: "M2LB.zip"
          rapportmottak:
            integration:
              logicalName: "Skolenærvær – Altinn report intake"
              type: "http-api"
              producer: "Altinn"
              consumer: "Meldingsutveksleren (MU)"
              endpoint: "https://mu.example.test/api/altinn/webhook"
            sourceEvidence:
              snapshotName: "Tiltak 2.zip"
        aliases:
          - logical: "Skolenærværsrapport"
            technical: "M2LB.SkoleService.Api"
        """;

    // ── Parsing and validation ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ValidYamlAndEquivalentJson_PreviewTheSamePlan_AndNothingIsWritten()
    {
        _catalog.Items.AddRange([SkoleReports, AltinnIntake]);
        Snapshot("M2LB.zip", "sha-m2lb");
        var yaml = await Preview(Profile);
        var json = await Preview("""
            {"version": 1, "profile": {"name": "Skolenærvær Development", "testPack": "skolenaervaer", "environment": "Development"},
             "journeys": {"periodeavslutning": {"integration": {"logicalName": "Skolenærvær – Period Closed", "type": "service-bus", "producerRole": "Skolenærværsrapport",
               "producerImplementation": "M2LB.SkoleService.Api", "channel": {"name": "skole.reports"}, "consumer": {"logicalName": "Bufdata"}}, "sourceEvidence": {"snapshotName": "M2LB.zip"}},
               "rapportmottak": {"integration": {"logicalName": "Skolenærvær – Altinn report intake", "type": "http-api", "producer": "Altinn", "consumer": "Meldingsutveksleren (MU)",
               "endpoint": "https://mu.example.test/api/altinn/webhook"}, "sourceEvidence": {"snapshotName": "Tiltak 2.zip"}}},
             "aliases": [{"logical": "Skolenærværsrapport", "technical": "M2LB.SkoleService.Api"}]}
            """, "profile.json");

        yaml.Valid.Should().BeTrue(string.Join("; ", yaml.Issues.Select(i => i.Message)));
        json.Valid.Should().BeTrue(string.Join("; ", json.Issues.Select(i => i.Message)));
        json.Format.Should().Be(SetupProfileFormat.Json);
        yaml.Changes.Select(c => (c.Id, c.Classification)).Should().BeEquivalentTo(json.Changes.Select(c => (c.Id, c.Classification)));
        _catalog.Writes.Should().Be(0, "validation and preview never mutate");
        _bindings.Items.Should().BeEmpty();
    }

    [Theory]
    [InlineData("version: 1\nprofile:\n  name: [unclosed\n", "profile.yaml")]
    [InlineData("{\"version\": 1, \"profile\": {", "profile.json")]
    public async Task InvalidSyntax_IsAPreciseError(string content, string file)
    {
        var preview = await Preview(content, file);
        preview.Valid.Should().BeFalse();
        preview.Issues.Should().Contain(i => i.Severity == SetupIssueSeverity.Error);
    }

    [Fact]
    public async Task UnsupportedOrMissingVersion_UnknownJourney_UnsupportedType_BadUrlAndResource_AreErrors()
    {
        (await Preview("version: 2\nprofile:\n  name: \"x\"\n")).Issues.Should().Contain(i => i.Message == "Profile version 2 is not supported by this BirkNext build (supported: 1).");
        (await Preview("profile:\n  name: \"x\"\n")).Issues.Should().Contain(i => i.Message.Contains("unversioned profile is never accepted"));
        (await Preview("version: 1\nprofile:\n  name: \"x\"\njourneys:\n  nope:\n    integration:\n      type: \"http-api\"\n")).Issues
            .Should().Contain(i => i.Message.StartsWith("Journey \"nope\" is not part of test pack"));
        (await Preview("version: 1\nprofile:\n  name: \"x\"\njourneys:\n  rapportmottak:\n    integration:\n      type: \"ftp\"\n")).Issues
            .Should().Contain(i => i.Message.Contains("\"ftp\" is not supported"));
        (await Preview("version: 1\nprofile:\n  name: \"x\"\njourneys:\n  rapportmottak:\n    integration:\n      type: \"http-api\"\n      endpoint: \"not a url\"\n")).Issues
            .Should().Contain(i => i.Message.Contains("is not an absolute http(s) URL"));
        (await Preview("version: 1\nprofile:\n  name: \"x\"\njourneys:\n  utdanningsdata:\n    integration:\n      type: \"event-hub\"\n      sourceResource: \"Utdanning\"\n")).Issues
            .Should().Contain(i => i.Message.Contains("is not schema.table or database.schema.table"));
    }

    [Fact]
    public async Task DuplicateKeys_TagsAndAnchors_AndOversizedFiles_AreRefused()
    {
        (await Preview("{\"version\": 1, \"version\": 1, \"profile\": {\"name\": \"x\"}}", "p.json")).Issues.Should().Contain(i => i.Message == "Duplicate property \"version\".");
        (await Preview("version: 1\nprofile: &base\n  name: \"x\"\nother: *base\n")).Issues.Should().Contain(i => i.Message.Contains("anchors and aliases are not allowed"));
        (await Preview("version: 1\nprofile:\n  name: !!python/object \"x\"\n")).Issues.Should().Contain(i => i.Message.Contains("tags"));
        (await Preview(new string('#', SetupProfileParser.MaxBytes + 1))).Issues.Should().Contain(i => i.Message.Contains("larger than"));
        (await Preview("version: 1", "setup.zip")).Issues.Should().Contain(i => i.Message.Contains("Only YAML"));
    }

    // ── Safety ───────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Secrets_Identities_TrustAndVerificationState_AreRejectedExplicitly_NeverEchoed_AndNeverApplied()
    {
        _catalog.Items.Add(AltinnIntake with { EndpointOrTopic = null });
        var content = """
            version: 1
            profile:
              name: "Malicious"
              testPack: "skolenaervaer"
            journeys:
              rapportmottak:
                integration:
                  type: "http-api"
                  producer: "Altinn"
                  consumer: "Meldingsutveksleren (MU)"
                  endpoint: "https://mu.example.test/hook"
                  clientSecret: "s3cr3t-value"
                  accessToken: "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0In0.c2lnbmF0dXJl"
                  trusted: "true"
                  runtimeVerified: "true"
                  mappingConfirmed: "true"
                altinn:
                  appId: "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0In0.c2lnbmF0dXJl"
            securityClassification:
              testData:
                level2ChildAlias: "12345678901"
                approvedByTestLead: "true"
            """;
        var preview = await Preview(content);
        var rejected = preview.Issues.Where(i => i.Severity == SetupIssueSeverity.Rejected).ToList();
        rejected.Select(i => i.Path).Should().Contain(["journeys.rapportmottak.integration.clientSecret", "journeys.rapportmottak.integration.accessToken",
            "journeys.rapportmottak.integration.trusted", "journeys.rapportmottak.integration.runtimeVerified", "journeys.rapportmottak.integration.mappingConfirmed",
            "journeys.rapportmottak.altinn.appId", "securityClassification.testData.level2ChildAlias", "securityClassification.testData.approvedByTestLead"]);
        rejected.Single(i => i.Path.EndsWith("clientSecret")).Message.Should().Be("Field \"clientSecret\" cannot be imported because secrets are not allowed in setup profiles. It is configured in the backend secret store.");
        string.Join(" ", preview.Issues.Select(i => i.Message)).Should().NotContain("s3cr3t").And.NotContain("eyJhbGci").And.NotContain("12345678901");
        preview.Changes.Should().NotContain(c => c.Setting.Contains("secret", StringComparison.OrdinalIgnoreCase) || c.Proposed == "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0In0.c2lnbmF0dXJl");
        preview.SecretsAccepted.Should().Be(0);
        preview.Summary.Rejected.Should().Be(rejected.Count);

        var apply = await Service().ApplyAsync(Env, SkoleNaervaerJourneyPack.Id, new("p.yaml", content, [.. preview.Changes.Select(c => c.Id)], [.. preview.Changes.Select(c => c.Id)]), default);
        apply.SecretsImported.Should().Be(0);
        apply.ActiveExecutionUnchanged.Should().BeTrue();
        _catalog.Items.Single().EndpointOrTopic.Should().Be("https://mu.example.test/hook", "the one safe value may apply");
        _catalog.Items.Single().Consumer.MappingState.Should().NotBe(ConsumerMappingState.Confirmed, "a mapping is never confirmed by a profile");
        _harness.MessageFlows.Configuration.AppIdentifier.Should().NotContain("eyJ");
    }

    [Fact]
    public async Task ExplicitValues_AreConflicts_NeverOverwritten_AndConfirmationIsRequiredForCreation()
    {
        _catalog.Items.Add(AltinnIntake with { EndpointOrTopic = "https://explicit.example.test/hook" });
        var content = Profile.Replace("https://mu.example.test/api/altinn/webhook", "https://other.example.test/hook");
        var preview = await Preview(content);
        var endpoint = preview.Changes.Single(c => c.Id == "rapportmottak.integration.EndpointOrTopic");
        (endpoint.Classification, endpoint.Preselected, endpoint.SafeToApply).Should().Be((SetupChangeClassification.Conflict, false, false));
        endpoint.Reason.Should().Contain("Conflict · manual review required");
        var create = preview.Changes.Single(c => c.Id == "periodeavslutning.integration");
        (create.Classification, create.RequiresConfirmation, create.Preselected).Should().Be((SetupChangeClassification.RequiresConfirmation, true, false));

        var apply = await Service().ApplyAsync(Env, SkoleNaervaerJourneyPack.Id, new("p.yaml", content, [endpoint.Id, create.Id]), default);
        apply.Items.Single(i => i.ChangeId == endpoint.Id).Outcome.Should().Be(SetupApplyOutcome.Refused);
        apply.Items.Single(i => i.ChangeId == create.Id).Outcome.Should().Be(SetupApplyOutcome.Refused, "selected but not confirmed");
        _catalog.Items.Single().EndpointOrTopic.Should().Be("https://explicit.example.test/hook");
        _catalog.Items.Should().ContainSingle("nothing was created without confirmation");
    }

    [Fact]
    public async Task ExistingSkoleReports_IsThePeriodClosedIntegration_NoDuplicateIsProposed()
    {
        _catalog.Items.Add(SkoleReports);
        var preview = await Preview(Profile);
        preview.Changes.Should().NotContain(c => c.Id == "periodeavslutning.integration", "the technical integration already represents the journey");
        preview.Changes.Single(c => c.Id == "periodeavslutning.integration.EndpointOrTopic").Classification.Should().Be(SetupChangeClassification.Same);
        var consumer = preview.Changes.Single(c => c.Id == "periodeavslutning.integration.Consumer");
        (consumer.Classification, consumer.Kind).Should().Be((SetupChangeClassification.New, SetupValueKind.ExpectedMapping));
        consumer.Provenance.Should().Contain("not source verified");
    }

    [Fact]
    public async Task SourceSnapshots_MissingAmbiguousAndConflicting_AreNeverResolvedByDate()
    {
        _catalog.Items.AddRange([SkoleReports, AltinnIntake]);
        Snapshot("M2LB.zip", "sha-a");
        Snapshot("M2LB.zip", "sha-b");
        var bound = Snapshot("Other.zip", "sha-o");
        _bindings.Items.Add(IntegrationEvidenceBinding.For(Env, AltinnIntake.Id, bound, DateTimeOffset.UtcNow));
        var preview = await Preview(Profile);
        var m2lb = preview.Changes.Single(c => c.Id == "periodeavslutning.sourceEvidence");
        (m2lb.Classification, m2lb.SafeToApply).Should().Be((SetupChangeClassification.RequiresConfirmation, false));
        m2lb.Reason.Should().Contain("the newest is never chosen");
        var mu = preview.Changes.Single(c => c.Id == "rapportmottak.sourceEvidence");
        mu.Classification.Should().Be(SetupChangeClassification.CannotImport);
        mu.Reason.Should().Be("Journey \"rapportmottak\" references source snapshot \"Tiltak 2.zip\", but no matching Source Analysis snapshot exists. Import it first.");

        Snapshot("Tiltak 2.zip", "sha-t");
        (await Preview(Profile)).Changes.Single(c => c.Id == "rapportmottak.sourceEvidence").Classification.Should().Be(SetupChangeClassification.Conflict,
            "the integration is bound to another snapshot; the binding changes only in IQR Evidence");
    }

    [Fact]
    public async Task Apply_ChangesOnlySelectedSafeValues_ThroughTheirOwners_AndReimportIsSame()
    {
        _catalog.Items.AddRange([SkoleReports, AltinnIntake]);
        Snapshot("M2LB.zip", "sha-m2lb");
        var preview = await Preview(Profile);
        var safe = preview.Changes.Where(c => c.Preselected).Select(c => c.Id).ToList();
        safe.Should().Contain(["periodeavslutning.sourceEvidence", "periodeavslutning.integration.Consumer"]);
        var apply = await Service().ApplyAsync(Env, SkoleNaervaerJourneyPack.Id, new("p.yaml", Profile, safe), default);
        apply.Failed.Should().Be(0);
        apply.Applied.Should().Be(safe.Count);
        _bindings.Items.Should().ContainSingle(b => b.IntegrationId == SkoleReports.Id && b.ArchiveName == "M2LB.zip");
        _catalog.Items.Single(i => i.Id == SkoleReports.Id).Consumer.DisplayName.Should().Be("Bufdata");

        var again = await Preview(Profile);
        again.Changes.Where(c => c.Classification is not (SetupChangeClassification.ReferenceOnly or SetupChangeClassification.CannotImport))
            .Should().OnlyContain(c => c.Classification == SetupChangeClassification.Same, "importing the same profile twice changes nothing");
        _catalog.Items.Should().HaveCount(2, "no duplicate integration");
        _bindings.Items.Should().HaveCount(1, "no duplicate binding");
    }

    [Fact]
    public async Task RunConfiguration_FillsOnlyEmptyFields_AndNeverExplicitOnes()
    {
        _harness.MessageFlows.Configuration = _harness.MessageFlows.Configuration with { Environment = ExternalTestEnvironment.Unknown, AppIdentifier = "explicit/app", BaseUrlReference = null };
        var content = "version: 1\nprofile:\n  name: \"x\"\n  testPack: \"skolenaervaer\"\njourneys:\n  rapportmottak:\n    altinn:\n      environment: \"Test\"\n      appId: \"buf/skolenaervaer\"\n      baseUrl: \"https://buf.apps.tt02.altinn.no/buf/skolenaervaer\"\n";
        var preview = await Preview(content);
        preview.Changes.Single(c => c.Id == "rapportmottak.altinn.appId").Classification.Should().Be(SetupChangeClassification.Conflict);
        var apply = await Service().ApplyAsync(Env, SkoleNaervaerJourneyPack.Id, new("p.yaml", content, [.. preview.Changes.Select(c => c.Id)]), default);
        apply.Items.Single(i => i.ChangeId == "rapportmottak.altinn.environment").Outcome.Should().Be(SetupApplyOutcome.Applied);
        _harness.MessageFlows.Configuration.Environment.Should().Be(ExternalTestEnvironment.Test);
        _harness.MessageFlows.Configuration.AppIdentifier.Should().Be("explicit/app", "an explicit value is never overwritten");
        _harness.MessageFlows.Configuration.BaseUrlReference.Should().Be("https://buf.apps.tt02.altinn.no/buf/skolenaervaer");
    }

    // ── Export ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Export_IsSanitized_NamesManualRequirements_AndRoundTripsAsSame()
    {
        _catalog.Items.AddRange([SkoleReports with { Consumer = new IntegrationConsumer { DisplayName = "Bufdata" } }, AltinnIntake]);
        var m2lb = Snapshot("M2LB.zip", "sha-m2lb");
        _bindings.Items.Add(IntegrationEvidenceBinding.For(Env, SkoleReports.Id, m2lb, DateTimeOffset.UtcNow));
        var export = await Service().ExportAsync(Env, SkoleNaervaerJourneyPack.Id, SetupProfileFormat.Yaml, default);
        export.Content.Should().Contain("containsSecrets: false").And.Contain("sanitized: true").And.Contain("\"trustedExecution\"").And.Contain("\"skole.reports\"");
        export.Content.Should().NotContainAny("secret:", "token", "password", "trusted:", "verified:", "approved:", "confirmed:", ActiveEventTestHarness.Sentinel);
        export.Excluded.Should().Contain("Execution trust and active-execution permission");

        var reimport = await Preview(export.Content, export.FileName);
        reimport.Valid.Should().BeTrue(string.Join("; ", reimport.Issues.Select(i => $"{i.Path}: {i.Message}")));
        reimport.Changes.Where(c => c.Classification is not SetupChangeClassification.ReferenceOnly)
            .Should().OnlyContain(c => c.Classification == SetupChangeClassification.Same, "an export re-imports as the same safe setup");
        var json = await Service().ExportAsync(Env, SkoleNaervaerJourneyPack.Id, SetupProfileFormat.Json, default);
        (await Preview(json.Content, json.FileName)).Valid.Should().BeTrue();
    }

    [Fact]
    public async Task Example_HasPlaceholdersOnly_AndValidates()
    {
        var example = SetupProfileService.Example(SkoleNaervaerJourneyPack.Id);
        example.Should().NotContainAny("secret:", "token:", "password");
        (await Preview(example, "skolenaervaer-development.example.yaml")).Valid.Should().BeTrue();
    }
}
