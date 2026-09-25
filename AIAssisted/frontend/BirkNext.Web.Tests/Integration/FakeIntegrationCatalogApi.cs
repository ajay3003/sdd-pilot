using BirkNext.Integrations;
using BirkNext.Web.Models;
using BirkNext.Web.Services;

namespace BirkNext.Web.Tests.Integration;

/// <summary>In-memory <see cref="IIntegrationCatalogApiService"/> for component tests; records calls, performs no HTTP.</summary>
public sealed class FakeIntegrationCatalogApi : IIntegrationCatalogApiService
{
    public IntegrationCatalog Catalog { get; set; } = new();
    public IntegrationReviewReadiness Readiness { get; set; } = new() { Headline = "Cannot run", Reasons = ["No enabled integration is configured."] };
    public IntegrationReviewResult? Result { get; set; }
    public List<IntegrationReviewRunSummary> History { get; } = [];
    public Exception? LoadFailure { get; set; }
    public List<string> Calls { get; } = [];
    public List<IntegrationDefinition> Saved { get; } = [];
    public List<IntegrationContractArtifact> Contracts { get; } = [];
    public List<IntegrationPlatform> SavedPlatforms { get; } = [];
    /// <summary>When set, every mutation fails with this exception (Save failed path).</summary>
    public Exception? SaveFailure { get; set; }
    /// <summary>When set, contract uploads are rejected with this validation reason.</summary>
    public string? ContractRejection { get; set; }

    private void Mutating() { if (SaveFailure is not null) throw SaveFailure; }

    public Task<IntegrationCatalog> GetCatalogAsync(FrontendAnalysisProfile profile, CancellationToken ct = default)
    {
        Calls.Add("get");
        return LoadFailure is null ? Task.FromResult(Catalog with { EnvironmentId = profile.Id }) : Task.FromException<IntegrationCatalog>(LoadFailure);
    }

    public Task<IntegrationDefinition> CreateAsync(string environmentId, IntegrationDefinition definition, CancellationToken ct = default)
    {
        Calls.Add("create");
        Mutating();
        var created = definition with { Id = $"{environmentId}:manual:{Catalog.Integrations.Count + 1}", EnvironmentId = environmentId, Origin = IntegrationRecordOrigin.Manual };
        Catalog = Catalog with { Integrations = [.. Catalog.Integrations, created] };
        Saved.Add(created);
        return Task.FromResult(created);
    }

    public Task<IntegrationDefinition> UpdateAsync(string environmentId, IntegrationDefinition definition, CancellationToken ct = default)
    {
        Calls.Add("update");
        Mutating();
        var updated = definition with { UserModified = true };
        Catalog = Catalog with { Integrations = Catalog.Integrations.Select(i => i.Id == definition.Id ? updated : i).ToList() };
        Saved.Add(updated);
        return Task.FromResult(updated);
    }

    public Task<IntegrationDefinition> SetEnabledAsync(string environmentId, string id, bool enabled, CancellationToken ct = default)
    {
        Calls.Add($"enabled:{id}:{enabled}");
        Mutating();
        var target = Catalog.Integrations.Single(i => i.Id == id) with { Enabled = enabled };
        Catalog = Catalog with { Integrations = Catalog.Integrations.Select(i => i.Id == id ? target : i).ToList() };
        return Task.FromResult(target);
    }

    public Task DeleteAsync(string environmentId, string id, CancellationToken ct = default)
    {
        Calls.Add($"delete:{id}");
        Mutating();
        Catalog = Catalog with { Integrations = Catalog.Integrations.Where(i => i.Id != id).ToList() };
        return Task.CompletedTask;
    }

    public Task<IntegrationPlatform> UpdatePlatformAsync(string environmentId, IntegrationPlatform platform, CancellationToken ct = default)
    {
        Calls.Add("platform");
        Mutating();
        SavedPlatforms.Add(platform);
        Catalog = Catalog with { Platforms = Catalog.Platforms.Select(x => x.Id == platform.Id ? platform : x).ToList() };
        return Task.FromResult(platform);
    }

    public Task<IReadOnlyList<IntegrationContractArtifact>> ContractsAsync(string environmentId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<IntegrationContractArtifact>>(Contracts.ToList());

    public Task<(IntegrationContractArtifact? Artifact, string? Error)> SaveContractAsync(IntegrationContractUpload upload, CancellationToken ct = default)
    {
        Calls.Add($"contract:{upload.IntegrationId}:{upload.Role}");
        Mutating();
        if (ContractRejection is { } reason) return Task.FromResult<(IntegrationContractArtifact?, string?)>((null, reason));
        var artifact = new IntegrationContractArtifact
        {
            EnvironmentId = upload.EnvironmentId, IntegrationId = upload.IntegrationId, Role = upload.Role, FileName = upload.FileName,
            ContentHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", FieldCount = 3, ImportedAt = DateTimeOffset.UtcNow,
        };
        Contracts.RemoveAll(c => c.IntegrationId == upload.IntegrationId && c.Role == upload.Role);
        Contracts.Add(artifact);
        return Task.FromResult<(IntegrationContractArtifact?, string?)>((artifact, null));
    }

    public Task RemoveContractAsync(string environmentId, string integrationId, IntegrationContractRole role, CancellationToken ct = default)
    {
        Calls.Add($"contract-remove:{integrationId}:{role}");
        Mutating();
        Contracts.RemoveAll(c => c.IntegrationId == integrationId && c.Role == role);
        return Task.CompletedTask;
    }

    public Task<int> ImportLegacyAsync(FrontendAnalysisProfile profile, CancellationToken ct = default)
    {
        Calls.Add("import");
        return Task.FromResult(0);
    }

    public Task<IntegrationReviewReadiness> ReadinessAsync(FrontendAnalysisProfile profile, CancellationToken ct = default)
    {
        Calls.Add("readiness");
        return LoadFailure is null ? Task.FromResult(Readiness) : Task.FromException<IntegrationReviewReadiness>(LoadFailure);
    }

    public Task<IntegrationReviewResult> RunAsync(FrontendAnalysisProfile profile, CancellationToken ct = default)
    {
        Calls.Add("run");
        return Task.FromResult(Result ?? throw new InvalidOperationException("No result configured."));
    }

    public Task<IReadOnlyList<IntegrationReviewRunSummary>> HistoryAsync(string environmentId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<IntegrationReviewRunSummary>>(History);

    public Task<IntegrationReviewResult?> GetRunAsync(Guid runId, CancellationToken ct = default) => Task.FromResult(Result);
}

/// <summary>An M2LB DEV-shaped catalog, readiness and configuration-only review result, mirroring the backend seed and engine.</summary>
public static class M2lbFixture
{
    public const string PlatformId = "dev:eventhub:m2lb";
    public const string System = "BIRK CDC / Debezium";

    public static readonly string[] Tables =
        ["Person", "Tiltak", "Bestilling", "TjenesteType", "TiltaksStatusType", "AvslutningsGrunnType", "Barn", "BarnStatusType", "BarnType", "Kommune", "KjønnType", "TvangsProtokoll", "HjemmelType", "TvangsProtokollStatusType", "Romning", "RomningKategoriType"];

    private static readonly Dictionary<string, string> Suggested = new()
    {
        ["Barn"] = "PersonBiRKAdapter", ["Tiltak"] = "Tjeneste API", ["Bestilling"] = "Tjeneste API", ["TjenesteType"] = "Tjeneste API",
        ["TiltaksStatusType"] = "Tjeneste API", ["AvslutningsGrunnType"] = "Tjeneste API", ["TvangsProtokoll"] = "Hendelse BiRK Adapter", ["Romning"] = "Hendelse BiRK Adapter",
    };

    public static IntegrationPlatform Platform() => new()
    {
        Id = PlatformId, EnvironmentId = "dev", Name = "M2LB DEV Event Hubs", Kind = IntegrationKind.EventHub, Region = "nwe",
        Namespace = "evhns-m2lb-dev-nwe-001", NamespaceFqdn = "evhns-m2lb-dev-nwe-001.servicebus.windows.net", ResourceGroup = "rg-m2lb-dev-integration-nwe",
        TechnicalOwner = "platform-team", MonitoringProvider = "Application Insights", ProducerTechnology = "Debezium SQL Server connector",
        ProducerAuthentication = IntegrationAuthMechanism.Sas, DefaultConsumerAuthentication = IntegrationAuthMechanism.ManagedIdentity,
        SourceDatabase = "BirkM2LB", SourceHost = "10.31.19.31", SourcePort = 50806, TopicPrefix = "m2lb-cdc-dev", DefaultPartitionCount = 1, DefaultRetentionDays = 7,
        TechnicalTopics = new[] { "m2lb-cdc-dev", "schemahistory", "connect-configs", "connect-offsets", "connect-status" }.Select(n => new TechnicalTopic { Name = n, Purpose = "Platform support" }).ToList(),
        Origin = IntegrationRecordOrigin.Seed,
    };

    public static IntegrationDefinition Topic(string table) => new()
    {
        Id = $"dev:eventhub:birk-cdc:dbo.{table}", EnvironmentId = "dev", PlatformId = PlatformId, DisplayName = $"BIRK {table} CDC", Kind = IntegrationKind.EventHub,
        SystemName = System, SourceSystem = "BIRK", SourceResource = $"BirkM2LB.dbo.{table}", EndpointOrTopic = $"m2lb-cdc-dev.BirkM2LB.dbo.{table}",
        Producer = "Debezium SQL Server connector", ProducerAuthentication = IntegrationAuthMechanism.Sas, ConsumerAuthentication = IntegrationAuthMechanism.ManagedIdentity,
        PartitionCount = 1, RetentionDays = 7, Origin = IntegrationRecordOrigin.Seed,
        Consumer = table == "Person"
            ? new() { DisplayName = "Person Adapter", LogicalName = "person-adapter", ContainerApp = "ca-m2lb-person-adp-dev-nwe-001", ManagedIdentity = "id-m2lb-person-adp-dev-nwe", MappingState = ConsumerMappingState.Confirmed, MappingSource = "Confirmed M2LB DEV mapping" }
            : Suggested.TryGetValue(table, out var name)
                ? new() { DisplayName = name, MappingState = ConsumerMappingState.Suggested, MappingSource = "Audited M2LB source (QA-context audit)" }
                : new() { MappingState = ConsumerMappingState.NeedsConfirmation },
    };

    public static IntegrationCatalog Catalog() => new() { EnvironmentId = "dev", Platforms = [Platform()], Integrations = Tables.Select(Topic).ToList() };

    public static IntegrationReviewReadiness Readiness() => new()
    {
        EnvironmentId = "dev", ConfiguredIntegrations = 16, EnabledIntegrations = 16, CanRun = true, Headline = "Can run with limitations",
        Reasons = ["Runtime evidence (hub metadata, checkpoints, telemetry) is not connected.", "No contract reference is configured."],
        Systems = [new() { SystemName = System, PlatformId = PlatformId, PlatformName = "M2LB DEV Event Hubs", Kind = IntegrationKind.EventHub, Topics = 16, Enabled = 16,
            ConsumersConfirmed = 1, ConsumersSuggested = 8, ConsumersNeedingConfirmation = 7, ConsumerGroupsUnknown = 16, DomainReviewSupported = true }],
        Domains = Enum.GetValues<IntegrationReviewDomain>().Select(d => new IntegrationDomainReadinessRow(d,
            d == IntegrationReviewDomain.Configuration ? IntegrationDomainReadiness.Ready
            : d is IntegrationReviewDomain.Connectivity or IntegrationReviewDomain.Security or IntegrationReviewDomain.Observability ? IntegrationDomainReadiness.Limited
            : IntegrationDomainReadiness.NotAssessable, $"{d} explanation")).ToList(),
    };

    private static IntegrationCheck Check(string id, IntegrationReviewDomain domain, IntegrationCheckScope scope, string subject, IntegrationCheckStatus status, string explanation, IntegrationEvidenceSource source = IntegrationEvidenceSource.Configuration) => new()
    {
        CheckId = id, Domain = domain, Scope = scope, SubjectId = subject, Title = id, Status = status, Explanation = explanation, Provenance = source,
        CapturedAt = new DateTimeOffset(2026, 9, 25, 10, 0, 0, TimeSpan.Zero),
    };

    /// <summary>A configuration-only run where the namespace was unreachable: one grouped finding over all 16 topics.</summary>
    public static IntegrationReviewResult Result()
    {
        var catalog = Catalog();
        var topics = catalog.Integrations.Select(i => new IntegrationTopicResult
        {
            IntegrationId = i.Id, DisplayName = i.DisplayName, Topic = i.EndpointOrTopic, Consumer = i.Consumer.DisplayName, MappingState = i.Consumer.MappingState,
            Checks =
            [
                Check("cfg-topic", IntegrationReviewDomain.Configuration, IntegrationCheckScope.Topic, i.Id, IntegrationCheckStatus.Pass, "Topic configured."),
                Check("cfg-consumer", IntegrationReviewDomain.Configuration, IntegrationCheckScope.Topic, i.Id,
                    i.Consumer.MappingState == ConsumerMappingState.Confirmed ? IntegrationCheckStatus.Pass : IntegrationCheckStatus.NeedsConfirmation,
                    i.Consumer.MappingState == ConsumerMappingState.Confirmed ? "Consumer confirmed." : "Consumer mapping is not confirmed."),
                Check("err-deserialization", IntegrationReviewDomain.ErrorHandling, IntegrationCheckScope.Topic, i.Id, IntegrationCheckStatus.NotAssessed, "No consumer telemetry source is connected."),
                Check("flow-consumer", IntegrationReviewDomain.MessageFlow, IntegrationCheckScope.Topic, i.Id, IntegrationCheckStatus.NotAssessed, "Configured flow is not observed flow."),
            ],
        }).ToList();
        var result = new IntegrationReviewResult
        {
            RunId = Guid.Parse("11111111-2222-3333-4444-555555555555"), EnvironmentId = "dev", EnvironmentName = "M2LB DEV",
            StartedAt = new DateTimeOffset(2026, 9, 25, 10, 0, 0, TimeSpan.Zero), CompletedAt = new DateTimeOffset(2026, 9, 25, 10, 0, 5, TimeSpan.Zero),
            Outcome = IntegrationReviewOutcome.ManualReviewRequired, ConfigurationSnapshot = catalog, Freshness = IntegrationEvidenceFreshness.Current,
            EvidenceSources = [IntegrationEvidenceSource.Configuration, IntegrationEvidenceSource.NetworkProbe],
            Systems = [new() { SystemName = System, PlatformId = PlatformId, Kind = IntegrationKind.EventHub, DomainReviewSupported = true, Topics = topics,
                PlatformChecks = [Check("conn-namespace", IntegrationReviewDomain.Connectivity, IntegrationCheckScope.Platform, PlatformId, IntegrationCheckStatus.Fail, "TLS handshake to the namespace failed.", IntegrationEvidenceSource.NetworkProbe)] }],
            Findings = [new() { Key = "namespace-unreachable:" + PlatformId, RuleId = "namespace-unreachable", Domain = IntegrationReviewDomain.Connectivity, Severity = IntegrationFindingSeverityV2.High,
                Title = "Event Hub namespace is not reachable", Subject = "evhns-m2lb-dev-nwe-001.servicebus.windows.net", Evidence = ["TLS handshake failed."], Recommendation = "Check network path to the namespace.",
                AffectedIntegrations = catalog.Integrations.Select(i => i.Id).ToList() }],
            ManualFollowUp = [new("Confirm consumer mappings", "Suggested and unknown consumers need confirmation.", 15)],
            Limitations = ["No runtime evidence source is connected; runtime domains are Not assessed."],
        };
        return result with
        {
            Domains = Enum.GetValues<IntegrationReviewDomain>().Select(d =>
            {
                var checks = result.AllChecks.Where(c => c.Domain == d).ToList();
                var assessed = checks.Count(c => IntegrationReviewLabels.IsAssessed(c.Status));
                return new IntegrationDomainResult
                {
                    Domain = d, ChecksTotal = checks.Count, ChecksAssessed = assessed, Findings = result.Findings.Count(f => f.Domain == d),
                    StateLabel = checks.Count == 0 || assessed == 0 ? "Not assessed" : assessed == checks.Count ? "Assessed" : "Partially assessed",
                    KeyLimitation = assessed == 0 ? "No evidence source for this domain." : null,
                };
            }).ToList(),
        };
    }
}
