using System.Text.Json;
using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.Api.Services.Integrations.ServiceBus;
using BirkNext.Integrations;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services.Integrations.Scim;

public interface IScimEvidenceService
{
    Task<ScimEvidenceOverview> OverviewAsync(string environmentId, CancellationToken ct = default);
    Task<ScimSourceEvidence?> SourceAsync(string environmentId, CancellationToken ct = default);
    /// <summary>Source Analysis snapshots for SCIM provisioning (one snapshot per review). Read-only.</summary>
    Task<BirkNext.SourceEvidence.ReviewSourceOptions> SourceScopeAsync(string environmentId, BirkNext.SourceEvidence.ReviewSourceScopeRequest? scope, CancellationToken ct = default);
    /// <summary>Records the SCIM source evidence of exactly this Source Analysis snapshot (no upload, no substitution).</summary>
    Task<(ScimSourceEvidence? Evidence, string? Error)> UseSourceScopeAsync(string environmentId, BirkNext.SourceEvidence.ReviewSourceScopeRequest scope, CancellationToken ct = default);
    Task<(ScimSourceEvidence? Evidence, string? Error)> AnalyzeAsync(string environmentId, IReadOnlyList<(string FileName, byte[] Bytes)> archives, CancellationToken ct = default);
    /// <summary>"Run safe SCIM checks": stored source evidence + safe GET checks + Service Bus correlation. Stored as an immutable snapshot.</summary>
    Task<(ScimEvidenceCheck? Check, string? Error)> RunSafeChecksAsync(string environmentId, string platformId, string? environmentType, string? targetUrl, CancellationToken ct = default);
    /// <summary>The SCIM part of an Integration Quality Review run (not stored separately: the review result is its snapshot).</summary>
    Task<ScimEvidenceCheck> ReviewAsync(IntegrationCatalog catalog, IntegrationPlatform platform, string? environmentType, ServiceBusEvidenceCheck? serviceBus, CancellationToken ct = default);
    Task<IReadOnlyList<ScimReadiness>> ReadinessAsync(IntegrationCatalog catalog, CancellationToken ct = default);
    Task<ScimEvidenceCheck?> GetRunAsync(Guid runId, CancellationToken ct = default);
}

/// <summary>
/// SCIM identity-provisioning evidence. Source analyses and safe checks are stored as immutable rows (facts, provenance, statuses — never
/// source text, tokens or user data). The safe checks never mutate a user: POST/PATCH/DELETE are not sent, users are not listed, no
/// message is published. Synthetic mutation testing is modelled as a capability that is off by default and not executed in this version.
/// </summary>
public sealed class ScimEvidenceService(AppDbContext db, IIntegrationCatalogService catalog, IScimRuntimeProbe probe, ILogger<ScimEvidenceService> logger,
    ServiceBusEvidenceService? serviceBus = null, SourceAnalysis.IReviewSourceEvidenceProvider? sources = null) : IScimEvidenceService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public const string NoEvidence = "Analyzed before SCIM evidence was captured. Analyze the archive again in Source Analysis to create a new snapshot.";
    private SourceAnalysis.IReviewSourceEvidenceProvider Sources => sources ?? new SourceAnalysis.ReviewSourceEvidenceProvider(new SourceEvidence.IqrSourceStore(db));

    /// <summary>Source Analysis' capture step: this analyzer over one archive (never throws).</summary>
    public static ScimSourceEvidence? ExtractSnapshotEvidence(string environmentId, string name, byte[] bytes)
    {
        try
        {
            var (archive, files, error) = ScimSourceReader.Read(name, bytes);
            return error is not null || archive is null ? null : ScimSourceAnalyzer.Analyze(environmentId, [archive], files, DateTimeOffset.UtcNow);
        }
        catch (Exception e) when (e is not OutOfMemoryException) { return null; }
    }

    public static SourceAnalysis.ConsumerSourceEvidence Evidence(IqrSourceSnapshot s) => s.ScimEvidence is not { } e ? new(false, NoEvidence)
        : new(true, e.Detected ? null : "No SCIM route surface was found in this snapshot.", e.Detected ? $"{e.Operations.Count} SCIM operation(s) · {e.Requirements.Count} requirement(s)" : null);

    private static string? Problem(IqrSourceSnapshot s) => s.ScimEvidence is null ? NoEvidence : null;

    public async Task<BirkNext.SourceEvidence.ReviewSourceOptions> SourceScopeAsync(string environmentId, BirkNext.SourceEvidence.ReviewSourceScopeRequest? scope, CancellationToken ct = default)
    {
        var snapshots = Sources.SourceAnalysisEnabled ? await Sources.ListAsync(environmentId, ct) : [];
        var options = SourceAnalysis.ReviewSourceEvidenceProvider.Options(Sources.SourceAnalysisEnabled, snapshots, Evidence, scope, _ => [], Problem);
        return scope is { RelatedSnapshotIds.Count: > 0 } ? options with { Scope = null, Error = OneSnapshot } : options;
    }

    private const string OneSnapshot = "SCIM provisioning evidence is read from one source snapshot (the repository hosting the SCIM adapter).";

    public async Task<(ScimSourceEvidence? Evidence, string? Error)> UseSourceScopeAsync(string environmentId, BirkNext.SourceEvidence.ReviewSourceScopeRequest scope, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(environmentId)) return (null, "An analysis must belong to a Target Environment.");
        if (scope.RelatedSnapshotIds.Count > 0) return (null, OneSnapshot);
        var snapshots = await Sources.ListAsync(environmentId, ct);
        var (selected, error) = SourceAnalysis.ReviewSourceEvidenceProvider.Validate(scope, snapshots, Problem, Sources.SourceAnalysisEnabled);
        if (selected is null) return (null, error);
        var evidence = selected[0].ScimEvidence! with { EnvironmentId = environmentId, SourceScope = SourceAnalysis.ReviewSourceEvidenceProvider.Scope(selected, [], scope.ExcludedSuggestions) };
        db.ScimEvidence.Add(new ScimEvidenceRecord { Id = Guid.NewGuid(), EnvironmentId = environmentId, Kind = SourceKind, CreatedAt = DateTimeOffset.UtcNow, Json = JsonSerializer.Serialize(evidence, Json) });
        await db.SaveChangesAsync(ct);
        logger.LogInformation("SCIM source evidence for {EnvironmentId} taken from Source Analysis snapshot {Fingerprint}: detected {Detected}.",
            environmentId, selected[0].Archive.Sha256[..Math.Min(12, selected[0].Archive.Sha256.Length)], evidence.Detected);
        return (evidence, null);
    }
    private const string SourceKind = "source";
    private const string CheckKind = "check";

    public static bool IsScim(IntegrationPlatform platform) => platform.Kind == IntegrationKind.IdentityProvisioning;

    public async Task<ScimSourceEvidence?> SourceAsync(string environmentId, CancellationToken ct = default)
    {
        var json = await db.ScimEvidence.AsNoTracking().Where(r => r.EnvironmentId == environmentId && r.Kind == SourceKind).OrderByDescending(r => r.CreatedAt).Select(r => r.Json).FirstOrDefaultAsync(ct);
        return json is null ? null : JsonSerializer.Deserialize<ScimSourceEvidence>(json, Json);
    }

    public async Task<ScimEvidenceOverview> OverviewAsync(string environmentId, CancellationToken ct = default)
    {
        var checks = await db.ScimEvidence.AsNoTracking().Where(r => r.EnvironmentId == environmentId && r.Kind == CheckKind).OrderByDescending(r => r.CreatedAt).Take(20).ToListAsync(ct);
        var parsed = checks.Select(r => JsonSerializer.Deserialize<ScimEvidenceCheck>(r.Json, Json)).OfType<ScimEvidenceCheck>().ToList();
        return new ScimEvidenceOverview
        {
            Source = await SourceAsync(environmentId, ct), Latest = parsed.FirstOrDefault(),
            History = parsed.Select(c => new ScimCheckSummary(c.RunId, c.CompletedAt, c.PlatformId, c.OverallState, c.Findings.Count)).ToList(),
        };
    }

    public async Task<(ScimSourceEvidence? Evidence, string? Error)> AnalyzeAsync(string environmentId, IReadOnlyList<(string FileName, byte[] Bytes)> archives, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(environmentId)) return (null, "An analysis must belong to a Target Environment.");
        if (archives.Count == 0) return (null, "Upload at least one source archive (.zip).");
        var metadata = new List<SourceArchive>();
        var code = new List<ApplicationMessaging.SourceFile>();
        var documents = new List<ApplicationMessaging.SourceFile>();
        var settings = new List<ScimSettingsFile>();
        foreach (var (name, bytes) in archives)
        {
            var (archive, files, error) = ScimSourceReader.Read(name, bytes);
            if (error is not null) return (null, error);
            metadata.Add(archive!);
            code.AddRange(files.Code);
            documents.AddRange(files.Documents);
            settings.AddRange(files.Settings);
        }
        var evidence = ScimSourceAnalyzer.Analyze(environmentId, metadata, new ScimSourceSet(code, documents, settings), DateTimeOffset.UtcNow);
        db.ScimEvidence.Add(new ScimEvidenceRecord { Id = Guid.NewGuid(), EnvironmentId = environmentId, Kind = SourceKind, CreatedAt = evidence.AnalyzedAt, Json = JsonSerializer.Serialize(evidence, Json) });
        await db.SaveChangesAsync(ct);
        logger.LogInformation("SCIM source analysis for {EnvironmentId}: detected {Detected}, {Operations} operation(s), {Facts} fact(s) ({NeedsReview} need review), {Requirements} requirement(s), archives {Hashes}.",
            environmentId, evidence.Detected, evidence.Operations.Count, evidence.Facts.Count, evidence.NeedsReview.Count(), evidence.Requirements.Count, string.Join(",", metadata.Select(m => m.Sha256[..12])));
        return (evidence, null);
    }

    public async Task<(ScimEvidenceCheck? Check, string? Error)> RunSafeChecksAsync(string environmentId, string platformId, string? environmentType, string? targetUrl, CancellationToken ct = default)
    {
        var configured = await catalog.GetAsync(environmentId, environmentType, targetUrl, ct);
        if (configured.Platforms.FirstOrDefault(p => p.Id == platformId) is not { } platform) return (null, "Platform not found.");
        if (!IsScim(platform)) return (null, "Not an identity provisioning platform.");
        var outbound = configured.Platforms.FirstOrDefault(p => p.Id == platform.ScimProvisioning?.OutboundPlatformId);
        ServiceBusEvidenceCheck? bus = null;
        if (serviceBus is not null && outbound is not null && ServiceBusEvidenceService.IsServiceBus(outbound)) bus = await serviceBus.CheckAsync(outbound, null, ct);
        var check = await ReviewAsync(configured, platform, environmentType, bus, ct);
        db.ScimEvidence.Add(new ScimEvidenceRecord { Id = check.RunId, EnvironmentId = environmentId, Kind = CheckKind, PlatformId = platformId, CreatedAt = check.CompletedAt, Json = JsonSerializer.Serialize(check, Json) });
        await db.SaveChangesAsync(ct);
        return (check, null);
    }

    public async Task<ScimEvidenceCheck> ReviewAsync(IntegrationCatalog configured, IntegrationPlatform platform, string? environmentType, ServiceBusEvidenceCheck? bus, CancellationToken ct = default)
    {
        var started = DateTimeOffset.UtcNow;
        var settings = platform.ScimProvisioning ?? new ScimProvisioningSettings();
        var source = await SourceAsync(configured.EnvironmentId, ct);
        var runtime = await probe.ProbeAsync(settings, environmentType, ct);
        var outbound = configured.Platforms.FirstOrDefault(p => p.Id == settings.OutboundPlatformId);
        var check = Evaluate(platform, settings, source, runtime, outbound, bus, environmentType, started, DateTimeOffset.UtcNow);
        logger.LogInformation("SCIM check for {PlatformId}: {Overall}, runtime {Runtime}, {Checks} check(s), {Findings} finding(s), synthetic mutation {Mutation}.",
            platform.Id, check.OverallState, runtime.State, check.Checks.Count, check.Findings.Count, check.SyntheticMutation.State);
        return check;
    }

    public async Task<IReadOnlyList<ScimReadiness>> ReadinessAsync(IntegrationCatalog configured, CancellationToken ct = default)
    {
        var platforms = configured.Platforms.Where(p => p.Enabled && IsScim(p)).ToList();
        if (platforms.Count == 0) return [];
        var source = await SourceAsync(configured.EnvironmentId, ct);
        return platforms.Select(p => Readiness(p, source)).ToList();
    }

    public async Task<ScimEvidenceCheck?> GetRunAsync(Guid runId, CancellationToken ct = default)
    {
        var record = await db.ScimEvidence.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId && r.Kind == CheckKind, ct);
        return record is null ? null : JsonSerializer.Deserialize<ScimEvidenceCheck>(record.Json, Json);
    }

    public static ScimReadiness Readiness(IntegrationPlatform platform, ScimSourceEvidence? source)
    {
        var settings = platform.ScimProvisioning ?? new ScimProvisioningSettings();
        var gate = HttpScimRuntimeProbe.Gate(settings, "Development");
        return new ScimReadiness
        {
            PlatformId = platform.Id, PlatformName = platform.Name, SourceAnalyzed = source is not null, Detected = source?.Detected == true, Operations = source?.Operations.Count ?? 0,
            RequirementsImplemented = source?.Requirements.Count(r => r.Status == ScimRequirementStatus.Implemented) ?? 0, RequirementsTotal = source?.Requirements.Count ?? 0,
            NeedsReview = source?.NeedsReview.Count() ?? 0,
            RuntimeState = gate?.State ?? IntegrationEvidenceState.Available, RuntimeReason = gate?.Reason ?? "Safe GET checks can run (health, authentication challenge, metadata).",
            SyntheticMutation = Mutation(settings, null).State,
        };
    }

    // ── Evaluation (pure) ───────────────────────────────────────────────────────────────────────────────────────────

    public static ScimMutationCapability Mutation(ScimProvisioningSettings settings, string? environmentType)
    {
        var context = settings.SyntheticTest;
        var reasons = new List<string>
        {
            "Requires an explicitly enabled synthetic test context: DEV or QA only, a synthetic identity starting with " + ScimSyntheticTestContext.RequiredPrefix + ", test-lead approval and a cleanup plan.",
            "Never uses a real employee identity and never runs against Production.",
        };
        if (string.IsNullOrWhiteSpace(context.TestUserPrefix) || string.IsNullOrWhiteSpace(context.Environment))
            return new ScimMutationCapability { State = ScimMutationState.NotConfigured, Reasons = reasons };
        if (!context.Enabled || !context.ApprovedByTestLead || string.IsNullOrWhiteSpace(context.CleanupPlan))
            return new ScimMutationCapability { State = ScimMutationState.Disabled, Reasons = [.. reasons, "The context is not enabled, approved and given a cleanup plan."] };
        if (context.Validate() is { } invalid || string.Equals(environmentType, "Production", StringComparison.OrdinalIgnoreCase))
            return new ScimMutationCapability { State = ScimMutationState.NotAllowedForEnvironment, Reasons = [.. reasons, context.Validate() ?? "Production is never used."] };
        return new ScimMutationCapability { State = ScimMutationState.NotImplemented, Reasons = [.. reasons, "Executing the synthetic lifecycle (create → activate → deactivate → read back → cleanup) is not part of this version; nothing is mutated."] };
    }

    public static ScimEvidenceCheck Evaluate(IntegrationPlatform platform, ScimProvisioningSettings settings, ScimSourceEvidence? source, ScimRuntimeEvidence runtime,
        IntegrationPlatform? outbound, ServiceBusEvidenceCheck? bus, string? environmentType, DateTimeOffset started, DateTimeOffset completed)
    {
        ScimSourceFact? F(string id) => source?.Facts.FirstOrDefault(f => f.Id == id);
        bool Has(string id, params ScimEvidenceState[] states) => F(id) is { } f && (states.Length == 0 || states.Contains(f.State));
        ScimProbeObservation? P(string id) => runtime.Observations.FirstOrDefault(o => o.CheckId == id);
        var detected = source?.Detected == true;
        var noSource = source is null ? "No SCIM source analyzed." : !detected ? "SCIM is not detected in the analyzed source." : null;
        var sourceTopic = source?.Events.Select(e => e.Topic).OfType<string>().FirstOrDefault();
        var topology = outbound?.ServiceBusTopology;
        var topicExpected = topology?.Entities.FirstOrDefault(e => e.EntityType == ServiceBusEntityType.Topic && e.Name == (settings.Topic ?? sourceTopic));
        var subscriptions = topology?.Entities.Where(e => e.EntityType == ServiceBusEntityType.Subscription && e.Topic == (settings.Topic ?? sourceTopic)).ToList() ?? [];
        var runtimeTopic = bus?.Runtime?.Entities.FirstOrDefault(e => e.EntityType == ServiceBusEntityType.Topic && e.Name == (settings.Topic ?? sourceTopic));
        var runtimeSubscriptions = bus?.Runtime?.Entities.Where(e => e.EntityType == ServiceBusEntityType.Subscription && e.Topic == (settings.Topic ?? sourceTopic)).ToList() ?? [];
        var consumerFound = Has("scim-downstream-consumer", ScimEvidenceState.SourceVerified);
        var accessDecision = Has("scim-state-usage", ScimEvidenceState.SourceVerified);
        var authMissing = P("scim-auth-missing");
        var authInvalid = P("scim-auth-invalid");
        var responded = runtime.Observations.Where(o => o.StatusCode is not null).ToList();

        // ── Stages: source/configuration and runtime evidence side by side ─────────────────────────────────────────────
        var stages = new List<ScimStageEvidence>
        {
            new()
            {
                Stage = ScimStage.EntraProvisioning, Title = ScimLabels.Stage(ScimStage.EntraProvisioning),
                Source = ScimEvidenceState.NotAssessed, SourceDetail = "Entra provisioning scope, attribute mappings and cycle are configured in the Entra enterprise application; BirkNext does not read them.",
                Runtime = ScimEvidenceState.NotAssessed, RuntimeDetail = "Entra provisioning logs are not read.",
            },
            new()
            {
                Stage = ScimStage.ScimEndpoint, Title = ScimLabels.Stage(ScimStage.ScimEndpoint),
                Source = detected ? ScimEvidenceState.SourceVerified : source is null ? ScimEvidenceState.NotAssessed : ScimEvidenceState.NotFound,
                SourceDetail = detected ? $"{source!.Operations.Count} operation(s) under {source.BasePath}: {string.Join(", ", source.Operations.Select(o => $"{o.Method} {o.Path}"))}." : noSource!,
                Runtime = responded.Count > 0 ? ScimEvidenceState.Observed : RuntimeGap(runtime),
                RuntimeDetail = responded.Count > 0 ? $"{runtime.Target} answered {responded.Count} safe GET request(s)." : runtime.Reason,
            },
            new()
            {
                Stage = ScimStage.Authentication, Title = ScimLabels.Stage(ScimStage.Authentication),
                Source = Has("scim-auth-required") ? ScimEvidenceState.SourceVerified : detected ? ScimEvidenceState.NotFound : ScimEvidenceState.NotAssessed,
                SourceDetail = F("scim-auth-policy-addpublicauthentication")?.Detail ?? F("scim-auth-required")?.Detail ?? noSource ?? "No authorization requirement found.",
                Runtime = authMissing?.State == ScimEvidenceState.IssueDetected || authInvalid?.State == ScimEvidenceState.IssueDetected ? ScimEvidenceState.IssueDetected
                    : authMissing?.State == ScimEvidenceState.Verified && authInvalid?.State == ScimEvidenceState.Verified ? ScimEvidenceState.Verified
                    : authMissing is null ? RuntimeGap(runtime) : ScimEvidenceState.Partial,
                RuntimeDetail = authMissing is null ? runtime.Reason : $"Without a token: HTTP {authMissing.StatusCode?.ToString() ?? "–"}; with an invalid token: HTTP {authInvalid?.StatusCode?.ToString() ?? "–"}. A 401 challenge is not full token-validation coverage.",
            },
            new()
            {
                Stage = ScimStage.KjentBrukerPersistence, Title = ScimLabels.Stage(ScimStage.KjentBrukerPersistence),
                Source = Has("scim-persistence", ScimEvidenceState.SourceVerified) ? ScimEvidenceState.SourceVerified : detected ? ScimEvidenceState.NotFound : ScimEvidenceState.NotAssessed,
                SourceDetail = F("scim-persistence")?.Detail ?? noSource ?? "",
                Runtime = ScimEvidenceState.NotTested, RuntimeDetail = "No synthetic user was written: user mutation is off in the safe checks.",
            },
            new()
            {
                Stage = ScimStage.ServiceBusPublish, Title = ScimLabels.Stage(ScimStage.ServiceBusPublish),
                Source = Has("scim-order-publish-before-commit") ? ScimEvidenceState.NeedsReview : Has("scim-publisher") ? ScimEvidenceState.SourceVerified : detected ? ScimEvidenceState.NotFound : ScimEvidenceState.NotAssessed,
                SourceDetail = detected ? $"{string.Join(" ", source!.Events.Select(e => $"{e.EventType} → {e.Topic}."))} {(Has("scim-order-publish-before-commit") ? "Published before the KjentBruker write is committed; HTTP success only after a successful publish." : "")}".Trim() : noSource!,
                Runtime = ScimEvidenceState.NotTested, RuntimeDetail = "No event is published by BirkNext; publish success is not observed.",
            },
            new()
            {
                Stage = ScimStage.ServiceBusRoute, Title = ScimLabels.Stage(ScimStage.ServiceBusRoute),
                Source = sourceTopic is not null && topicExpected is not null && sourceTopic == topicExpected.Name ? ScimEvidenceState.Matched
                    : sourceTopic is not null && settings.Topic is not null && sourceTopic != settings.Topic ? ScimEvidenceState.IssueDetected
                    : topicExpected is not null || sourceTopic is not null ? ScimEvidenceState.Configured : ScimEvidenceState.NotConfigured,
                SourceDetail = $"Source publishes to {sourceTopic ?? "(not analyzed)"}; configured topic {settings.Topic ?? "(none)"}; Service Bus topology {(topicExpected is null ? "has no such topic" : $"has topic {topicExpected.Name} (publishers {string.Join(", ", topicExpected.Publishers.DefaultIfEmpty("none"))})")}; " +
                    (subscriptions.Count == 0 ? "no subscription on it in the configured topology." : $"subscriptions {string.Join(", ", subscriptions.Select(s => s.Name))}."),
                Runtime = runtimeTopic is not null ? ScimEvidenceState.Observed : bus?.Runtime is { State: IntegrationEvidenceState.Available } ? ScimEvidenceState.NotFound : ScimEvidenceState.NotAssessed,
                RuntimeDetail = runtimeTopic is not null ? $"Topic observed ({runtimeTopic.Status}); {runtimeSubscriptions.Count} subscription(s) on it." : bus?.Runtime is { } r ? $"Service Bus runtime metadata: {IntegrationReviewLabels.EvidenceState(r.State)}. {r.Reason}" : "No Service Bus runtime metadata in this check.",
            },
            new()
            {
                Stage = ScimStage.DownstreamProcessing, Title = ScimLabels.Stage(ScimStage.DownstreamProcessing),
                Source = consumerFound ? ScimEvidenceState.SourceVerified : detected ? ScimEvidenceState.NotFound : ScimEvidenceState.NotAssessed,
                SourceDetail = F("scim-downstream-consumer")?.Detail ?? noSource ?? "",
                Runtime = ScimEvidenceState.NotAssessed, RuntimeDetail = "No consumer telemetry for BrukerAktivert/BrukerDeaktivert is read.",
            },
            new()
            {
                Stage = ScimStage.AuthorizationState, Title = ScimLabels.Stage(ScimStage.AuthorizationState),
                Source = accessDecision ? ScimEvidenceState.SourceVerified : detected ? ScimEvidenceState.NotFound : ScimEvidenceState.NotAssessed,
                SourceDetail = F("scim-state-usage")?.Detail ?? noSource ?? "",
                Runtime = ScimEvidenceState.NotAssessed, RuntimeDetail = "The resulting authorization state of a user is not read.",
            },
        };

        // ── Checks ────────────────────────────────────────────────────────────────────────────────────────────────────
        var checks = new List<ScimCheck>
        {
            new() { CheckId = "scim-config-base-url", Area = ScimArea.Configuration, Title = "SCIM base URL", State = string.IsNullOrWhiteSpace(settings.BaseUrl) ? ScimEvidenceState.NotConfigured : ScimEvidenceState.Configured,
                Detail = string.IsNullOrWhiteSpace(settings.BaseUrl) ? "Unknown: the public tenant URL Entra calls is not configured." : $"{new Uri(settings.BaseUrl).GetLeftPart(UriPartial.Authority)}{settings.BasePath}", Provenance = IntegrationEvidenceSource.Configuration },
            new() { CheckId = "scim-config-topic", Area = ScimArea.Configuration, Title = "Outbound topic", State = stages[5].Source is ScimEvidenceState.Matched ? ScimEvidenceState.Matched : settings.Topic is null ? ScimEvidenceState.NotConfigured : stages[5].Source,
                Detail = stages[5].SourceDetail, Provenance = IntegrationEvidenceSource.Configuration },
        };
        checks.AddRange(settings.ConfigurationNotes.Select((n, i) => new ScimCheck { CheckId = $"scim-config-note-{i + 1}", Area = ScimArea.Configuration, Title = "Deployment configuration (audited)", State = ScimEvidenceState.Configured, Detail = n, Provenance = IntegrationEvidenceSource.Infrastructure }));
        if (source is not null)
            checks.AddRange(source.Facts.Select(f => new ScimCheck { CheckId = f.Id, Area = f.Area, Title = f.Title, State = f.State, Detail = f.Detail, Provenance = IntegrationEvidenceSource.SourceCode, Locations = f.Locations }));
        checks.AddRange(runtime.Observations.Select(o => new ScimCheck
        {
            CheckId = o.CheckId, Area = o.CheckId.StartsWith("scim-auth", StringComparison.Ordinal) ? ScimArea.Security : o.CheckId.StartsWith("scim-health", StringComparison.Ordinal) ? ScimArea.Observability : ScimArea.Protocol,
            Title = $"{o.Title} (GET {o.Path})", State = o.State, Detail = o.Detail + (o.CheckId == "scim-health-ready" && Has("scim-health-ready-empty") ? " Source: no dependency check is registered, so this readiness result says nothing about SQL Server or Service Bus." : ""),
            Provenance = o.CheckId.StartsWith("scim-health", StringComparison.Ordinal) ? IntegrationEvidenceSource.HealthEndpoint : IntegrationEvidenceSource.NetworkProbe,
        }));
        var deactivationState = !detected ? ScimEvidenceState.NotAssessed : !consumerFound || !accessDecision ? ScimEvidenceState.IssueDetected : ScimEvidenceState.NotTested;
        checks.Add(new ScimCheck
        {
            CheckId = "scim-deactivation-propagation", Area = ScimArea.Security, Title = "Deactivation propagation (security-critical)", State = deactivationState, Provenance = IntegrationEvidenceSource.SourceCode,
            Detail = !detected ? "SCIM source not analyzed." :
                $"Deactivation (PATCH active=false or DELETE) sets KjentBruker.IsActive = false and publishes BrukerDeaktivert. " +
                (consumerFound ? "A consumer of the topic is in the analyzed source. " : "No consumer of the topic is in the analyzed source. ") +
                (subscriptions.Count == 0 && topology is not null ? "The configured Service Bus topology has no subscription on the topic, so a published event would not be delivered to anyone. " : "") +
                (accessDecision ? "KjentBruker.IsActive is read outside the adapter. " : "No access decision on KjentBruker.IsActive was found. ") +
                "A published deactivation is not revoked access: end-to-end deactivation has not been tested.",
        });
        checks.Add(new ScimCheck { CheckId = "scim-e2e-activation", Area = ScimArea.Reliability, Title = "End-to-end activation", State = ScimEvidenceState.NotTested, Detail = "Needs a synthetic SCIM user and downstream evidence; not run.", Provenance = IntegrationEvidenceSource.Configuration });
        checks.Add(new ScimCheck { CheckId = "scim-retry-observed", Area = ScimArea.Reliability, Title = "Retry observed", State = ScimEvidenceState.NotTested, Detail = "Retry is configured in source; no runtime retry was observed (no failure injection in shared environments).", Provenance = IntegrationEvidenceSource.Configuration });

        // ── Findings ─────────────────────────────────────────────────────────────────────────────────────────────────
        var findings = new List<ScimFinding>();
        void Add(string rule, ScimFindingSeverity severity, ScimArea area, string title, string detail, IEnumerable<string> evidence, string recommendation) =>
            findings.Add(new ScimFinding { RuleId = rule, Severity = severity, Area = area, Title = title, Detail = detail, Evidence = evidence.Where(e => e.Length > 0).Distinct().Take(6).ToList(), Recommendation = recommendation });
        static string Where(ScimSourceFact? f) => f is null ? "" : string.Join(", ", f.Locations.Select(l => $"{l.File}:{l.Line}"));
        if (deactivationState == ScimEvidenceState.IssueDetected)
            Add("scim-deactivation-not-established", ScimFindingSeverity.High, ScimArea.Security, "Deactivation reaching effective access is not established",
                checks.Last(c => c.CheckId == "scim-deactivation-propagation").Detail, [Where(F("scim-downstream-consumer")), Where(F("scim-state-usage"))],
                "Identify the consumer of the topic and the access check that uses the deactivated state (upload its source or provide runtime evidence), and add a subscription for it in the configured topology.");
        if (authMissing?.State == ScimEvidenceState.IssueDetected || authInvalid?.State == ScimEvidenceState.IssueDetected)
            Add("scim-auth-not-enforced", ScimFindingSeverity.High, ScimArea.Security, "SCIM route answered without a valid token",
                $"{authMissing?.Detail} {authInvalid?.Detail}".Trim(), [$"{runtime.Target}{authMissing?.Path}"], "Require authentication on every SCIM route before any handler runs.");
        if (Has("scim-health-ready-empty") || Has("scim-health-checks", ScimEvidenceState.IssueDetected))
            Add("scim-health-without-dependencies", ScimFindingSeverity.Medium, ScimArea.Observability, "Health does not check SQL Server or Service Bus",
                $"{F("scim-health-checks")?.Detail} {F("scim-health-ready-empty")?.Detail}".Trim(), [Where(F("scim-health-checks")), Where(F("scim-health-ready-empty"))],
                "Register DbContext and Service Bus checks tagged for readiness, so a failing dependency makes readiness report Unhealthy.");
        var silentDeactivation = new[] { "scim-patch-string-boolean", "scim-patch-pathless", "scim-patch-unrecognised-200" }.Where(id => Has(id)).ToList();
        if (silentDeactivation.Count > 0)
            Add("scim-patch-silent-noop", ScimFindingSeverity.Medium, ScimArea.Security, "A deactivation PATCH in another valid form returns 200 without deactivating",
                string.Join(" ", silentDeactivation.Select(id => F(id)!.Detail)), silentDeactivation.Select(id => Where(F(id))),
                "Accept the PatchOp forms Entra sends (string booleans, path-less value objects) or reject unrecognised operations with a SCIM 400 so the failure is visible.");
        var duplicates = new[] { "scim-order-publish-before-commit", "scim-strategy-replays-publish", "scim-event-id-per-attempt", "scim-message-id-mismatch" }.Where(id => Has(id)).ToList();
        if (duplicates.Count > 0)
            Add("scim-duplicate-events", ScimFindingSeverity.Medium, ScimArea.Reliability, "Retries can publish the same state change more than once, under different ids",
                string.Join(" ", duplicates.Select(id => F(id)!.Detail)), duplicates.Select(id => Where(F(id))),
                "Derive the event id deterministically from the state change (or keep one id per request across retries) and use it as MessageId, so the consumer and Service Bus duplicate detection can de-duplicate.");
        if (Has("scim-post-generated-id"))
            Add("scim-post-without-id", ScimFindingSeverity.Medium, ScimArea.DataQuality, "POST without an id creates a new user on every repeat", F("scim-post-generated-id")!.Detail, [Where(F("scim-post-generated-id"))],
                "Resolve the user by externalId/userName (or the mapped object id) before creating, so a repeated POST is idempotent.");
        if (Has("scim-disabled-publisher"))
        {
            var fqdnSet = settings.ConfigurationNotes.Any(n => n.Contains("ServiceBus__FQDN", StringComparison.Ordinal));
            Add("scim-disabled-publisher", fqdnSet ? ScimFindingSeverity.Low : ScimFindingSeverity.Medium, ScimArea.ErrorHandling, "Service Bus can be disabled by configuration while SCIM still returns success",
                F("scim-disabled-publisher")!.Detail + (fqdnSet ? " The audited deployment configuration sets ServiceBus__FQDN, so the real publisher is expected in this environment (runtime value not observed)." : ""),
                [Where(F("scim-disabled-publisher"))], "Fail startup (or readiness) when Service Bus is not configured outside Local, instead of silently skipping events.");
        }
        if (Has("scim-unknown-user-upsert"))
            Add("scim-unknown-user", ScimFindingSeverity.Low, ScimArea.Protocol, "PATCH/DELETE of an unknown id creates a user record", F("scim-unknown-user-upsert")!.Detail, [Where(F("scim-unknown-user-upsert"))],
                "Decide whether an unknown id should be 404 (as the HTTP contract states) or an upsert, and align contract and code.");
        if (Has("scim-patch-missing-operations"))
            Add("scim-malformed-patch-500", ScimFindingSeverity.Low, ScimArea.ErrorHandling, "Malformed PatchOp returns 500", F("scim-patch-missing-operations")!.Detail, [Where(F("scim-patch-missing-operations"))],
                "Validate the PatchOp body and answer a SCIM 400 invalidSyntax error.");
        if (Has("scim-list-unsupported-filter"))
            Add("scim-filter-ignored", ScimFindingSeverity.Low, ScimArea.Privacy, "An unsupported filter returns the whole user list", F("scim-list-unsupported-filter")!.Detail, [Where(F("scim-list-unsupported-filter"))],
                "Reject unsupported filters with SCIM 400 invalidFilter.");
        if (Has("scim-metrics", ScimEvidenceState.NeedsReview))
            Add("scim-metrics-gaps", ScimFindingSeverity.Low, ScimArea.Observability, "Some operational metrics are never recorded", F("scim-metrics")!.Detail, [Where(F("scim-metrics"))],
                "Record publish failures and published events on every path.");
        foreach (var id in new[] { "scim-cors-any-origin", "scim-openapi-anonymous", "scim-auth-multitenant", "scim-persistence-identity", "scim-session-mismatch" }.Where(id => Has(id)))
            Add(id, ScimFindingSeverity.Info, F(id)!.Area, F(id)!.Title, F(id)!.Detail, [Where(F(id))], "Review.");

        // ── Missing evidence ─────────────────────────────────────────────────────────────────────────────────────────
        var missing = new List<string>();
        if (source is null) missing.Add("SCIM adapter source — upload the repository archive(s) to analyze routes, persistence, publishing and the specification.");
        if (string.IsNullOrWhiteSpace(settings.BaseUrl)) missing.Add("The public SCIM base URL (tenant URL) — needed for the safe endpoint, health and authentication checks.");
        missing.Add("An approved synthetic SCIM test context (DEV/QA, identity " + ScimSyntheticTestContext.RequiredPrefix + "…) — needed for create/activate/deactivate runtime evidence.");
        if (!consumerFound) missing.Add($"Source or runtime evidence of the {settings.Topic ?? "topic"} consumer in Autorisasjon — needed for downstream processing and final authorization state.");
        if (bus?.Runtime is not { State: IntegrationEvidenceState.Available }) missing.Add("Service Bus runtime metadata for the outbound platform (Azure subscription id) — needed to observe the topic and its subscriptions.");
        missing.Add("Application Insights telemetry for the SCIM adapter (requests, dependency calls) — not read by this check.");

        var runtimeStages = stages.Count(s => ScimLabels.IsRuntime(s.Runtime));
        var overall = stages.All(s => s.Runtime == ScimEvidenceState.Verified || s.Runtime == ScimEvidenceState.Observed) ? ScimOverallState.EndToEndVerified
            : findings.Any(f => f.Severity == ScimFindingSeverity.High) || checks.Any(c => c.State == ScimEvidenceState.IssueDetected && c.Provenance is IntegrationEvidenceSource.NetworkProbe) ? ScimOverallState.IssueDetected
            : source is null && runtimeStages == 0 ? ScimOverallState.NotTestable : ScimOverallState.Partial;
        return new ScimEvidenceCheck
        {
            RunId = Guid.NewGuid(), EnvironmentId = platform.EnvironmentId, PlatformId = platform.Id, PlatformName = platform.Name, StartedAt = started, CompletedAt = completed,
            OverallState = overall, Settings = settings, SourceAnalyzedAt = source?.AnalyzedAt, SourceArchives = source?.Archives ?? [], SourceDetected = detected,
            Stages = stages, Checks = checks, Operations = source?.Operations ?? [], Events = source?.Events ?? [], Requirements = source?.Requirements ?? [], TestCoverage = source?.TestCoverage ?? [],
            Runtime = runtime, SyntheticMutation = Mutation(settings, environmentType), Findings = findings.OrderBy(f => f.Severity).ToList(), Missing = missing,
            Limitations =
            [
                "Safe checks only: no user is created, changed, deleted or listed, and no message is published.",
                "Source evidence describes the analyzed archive, not necessarily the deployed revision.",
                .. source?.Limitations ?? [],
            ],
        };
    }

    private static ScimEvidenceState RuntimeGap(ScimRuntimeEvidence runtime) => runtime.State switch
    {
        IntegrationEvidenceState.NotConfigured => ScimEvidenceState.NotConfigured,
        IntegrationEvidenceState.NotSupported => ScimEvidenceState.NotSupported,
        IntegrationEvidenceState.Unavailable or IntegrationEvidenceState.Error => ScimEvidenceState.Unavailable,
        _ => ScimEvidenceState.NotAssessed,
    };

    // ── Integration Quality Review contribution ─────────────────────────────────────────────────────────────────────

    public static IntegrationReviewDomain Domain(ScimCheck check) => check.CheckId switch
    {
        "scim-health-live" or "scim-health-ready" => IntegrationReviewDomain.Connectivity,
        "scim-openapi" or "scim-meta-serviceproviderconfig" or "scim-meta-schemas" or "scim-meta-resourcetypes" => IntegrationReviewDomain.Contract,
        _ => check.Area switch
        {
            ScimArea.Configuration => IntegrationReviewDomain.Configuration,
            ScimArea.Protocol or ScimArea.Contract => IntegrationReviewDomain.Contract,
            ScimArea.Reliability => IntegrationReviewDomain.Reliability,
            ScimArea.ErrorHandling => IntegrationReviewDomain.ErrorHandling,
            ScimArea.Security or ScimArea.Privacy => IntegrationReviewDomain.Security,
            ScimArea.Observability => IntegrationReviewDomain.Observability,
            _ => IntegrationReviewDomain.DataQuality,
        },
    };

    /// <summary>Source facts are Detected/Configured (never Pass); a Pass is only a runtime expected-vs-observed comparison (401 challenge).</summary>
    public static IntegrationCheckStatus Status(ScimCheck check) => check.State switch
    {
        ScimEvidenceState.SourceVerified => IntegrationCheckStatus.Detected,
        ScimEvidenceState.Configured or ScimEvidenceState.Matched => IntegrationCheckStatus.Configured,
        ScimEvidenceState.Observed => IntegrationCheckStatus.Observed,
        ScimEvidenceState.Verified => IntegrationCheckStatus.Pass,
        ScimEvidenceState.IssueDetected when check.Provenance is IntegrationEvidenceSource.NetworkProbe => IntegrationCheckStatus.Fail,
        ScimEvidenceState.IssueDetected or ScimEvidenceState.NeedsReview or ScimEvidenceState.Partial or ScimEvidenceState.NotFound => IntegrationCheckStatus.Warning,
        ScimEvidenceState.NotConfigured => IntegrationCheckStatus.NotConfigured,
        ScimEvidenceState.Unavailable => IntegrationCheckStatus.Unavailable,
        _ => IntegrationCheckStatus.NotAssessed,
    };

    public static (List<IntegrationCheck> Checks, List<IntegrationReviewFinding> Findings) ReviewChecks(IntegrationPlatform platform, ScimEvidenceCheck check)
    {
        var at = check.CompletedAt;
        var checks = check.Checks.Select(c => new IntegrationCheck
        {
            CheckId = c.CheckId, Domain = Domain(c), Scope = IntegrationCheckScope.Platform, SubjectId = platform.Id, Title = c.Title, Status = Status(c),
            Expectation = c.Provenance is IntegrationEvidenceSource.SourceCode ? "What the analyzed SCIM source implements" : c.Provenance is IntegrationEvidenceSource.NetworkProbe or IntegrationEvidenceSource.HealthEndpoint ? "Safe GET observation" : "Configured SCIM provisioning",
            Evidence = c.Detail + (c.Locations.Count > 0 ? $" ({string.Join(", ", c.Locations.Take(3).Select(l => $"{l.File}:{l.Line}"))})" : ""),
            Explanation = ScimLabels.State(c.State), Provenance = c.Provenance, CapturedAt = at,
            Freshness = c.Provenance is IntegrationEvidenceSource.NetworkProbe or IntegrationEvidenceSource.HealthEndpoint ? IntegrationEvidenceItemFreshness.Current : IntegrationEvidenceItemFreshness.Unknown,
        }).ToList();
        // Message flow and performance only from runtime evidence — there is none from safe checks.
        checks.Add(new IntegrationCheck
        {
            CheckId = "scim-message-flow", Domain = IntegrationReviewDomain.MessageFlow, Scope = IntegrationCheckScope.Platform, SubjectId = platform.Id, Title = "Entra → SCIM → Service Bus → Autorisasjon flow",
            Status = IntegrationCheckStatus.NotAssessed, Expectation = "Runtime evidence of a provisioning operation reaching the topic and its consumer",
            Evidence = "No SCIM operation was performed and no topic or consumer telemetry was read.", Explanation = "Not assessed", Provenance = IntegrationEvidenceSource.Configuration, CapturedAt = at,
        });
        checks.Add(new IntegrationCheck
        {
            CheckId = "scim-performance", Domain = IntegrationReviewDomain.Performance, Scope = IntegrationCheckScope.Platform, SubjectId = platform.Id, Title = "SCIM processing latency",
            Status = IntegrationCheckStatus.NotAssessed, Expectation = "Measured SCIM request processing time",
            Evidence = "Not measured: response times of the safe GET checks (health, 401 challenge) are not SCIM processing times.", Explanation = "Not assessed", Provenance = IntegrationEvidenceSource.Configuration, CapturedAt = at,
        });
        var findings = check.Findings.Select(f => new IntegrationReviewFinding
        {
            Key = $"scim:{platform.Id}:{f.RuleId}", RuleId = f.RuleId, Domain = Domain(new ScimCheck { CheckId = f.RuleId, Area = f.Area }),
            Severity = f.Severity switch { ScimFindingSeverity.High => IntegrationFindingSeverityV2.High, ScimFindingSeverity.Medium => IntegrationFindingSeverityV2.Medium, ScimFindingSeverity.Low => IntegrationFindingSeverityV2.Low, _ => IntegrationFindingSeverityV2.Info },
            Title = f.Title, Subject = platform.Name, Evidence = [f.Detail, .. f.Evidence], Recommendation = f.Recommendation, AffectedIntegrations = [platform.Id],
        }).ToList();
        return (checks, findings);
    }
}
