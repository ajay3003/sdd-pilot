using System.Diagnostics;
using BirkNext.Api.Services.ContractAnalysis;
using BirkNext.Api.Services.LocalHttpsProxy;
using BirkNext.LocalHttpsProxy;

namespace BirkNext.Api.Services.IntegrationQuality;

public sealed class IntegrationQualityReviewService : IIntegrationQualityReviewService
{
    private readonly HttpClient _client;
    private readonly ILogger<IntegrationQualityReviewService> _logger;
    private readonly IAuthenticatedReviewGateway _authenticatedReview;
    private readonly IntegrationRelationshipPopulationService _relationshipPopulation;

    // Contract analysis (Phase 3, Checkpoint 4). Optional so existing construction sites keep
    // working; when absent, contract fields stay null and read as "not captured".
    private readonly IContractDiscoveryService? _contractDiscovery;
    private readonly IMessageSchemaDiscoveryService? _messageSchemaDiscovery;
    private readonly IContractComparer _contractComparer;
    private readonly IIntegrationQualitySnapshotRepository? _snapshotRepository;
    private readonly IntegrationHistoryComparer _historyComparer = new();
    private readonly RuntimeEvidencePopulationService _runtimeEvidence = new();
    private readonly IntegrationPerformanceAnalyzer _performanceAnalyzer = new();

    private static readonly HashSet<IntegrationType> AsyncTypes =
    [
        IntegrationType.EventHub, IntegrationType.ServiceBus,
        IntegrationType.Kafka, IntegrationType.RabbitMQ
    ];

    public IntegrationQualityReviewService(
        HttpClient client,
        ILogger<IntegrationQualityReviewService> logger,
        IAuthenticatedReviewGateway authenticatedReview,
        IntegrationRelationshipPopulationService relationshipPopulation,
        IContractDiscoveryService? contractDiscovery = null,
        IMessageSchemaDiscoveryService? messageSchemaDiscovery = null,
        IContractComparer? contractComparer = null,
        IIntegrationQualitySnapshotRepository? snapshotRepository = null)
    {
        _client = client;
        _logger = logger;
        _authenticatedReview = authenticatedReview;
        _relationshipPopulation = relationshipPopulation;
        _contractDiscovery = contractDiscovery;
        _messageSchemaDiscovery = messageSchemaDiscovery;
        _contractComparer = contractComparer ?? new ContractComparer();
        _snapshotRepository = snapshotRepository;
    }

    /// <summary>
    /// Authenticated runtime layer shared with the other reviews: resolves capabilities for the active environment and, when an
    /// authenticated context is available, runs approved authenticated GET checks against each enabled integration's health/worker URL
    /// with recorded provenance. Public probes are unaffected; an unavailable/expired context is reported, never silently downgraded.
    /// </summary>
    private async Task<IntegrationAuthenticationSummary> BuildAuthenticationSummaryAsync(IntegrationQualityRequest request, CancellationToken ct)
    {
        var identity = new AuthenticatedReviewIdentity(request.AuthenticatedTestingMethod, request.ProfileId, request.ContextFingerprint);
        var capabilities = _authenticatedReview.Resolve(identity);
        var checks = new List<IntegrationAuthenticatedCheck>();
        if (capabilities.AuthenticatedRest)
        {
            foreach (var intg in request.Integrations.Where(i => i.Enabled))
            {
                foreach (var (label, url) in new[] { ("Health", intg.HealthUrl), ("Worker", intg.WorkerUrl) })
                {
                    if (string.IsNullOrWhiteSpace(url)) continue;
                    var outcome = await _authenticatedReview.ExecuteRestAsync(identity, "GET", url!, ct);
                    checks.Add(new IntegrationAuthenticatedCheck
                    {
                        IntegrationId = intg.Id, Label = label, Url = url!, ExecutionMode = outcome.Mode, Status = outcome.Status,
                        StatusCode = outcome.Result?.StatusCode ?? 0, ElapsedMs = outcome.Result?.ElapsedMs ?? 0, Outcome = outcome.Message
                    });
                }
            }
        }
        return new IntegrationAuthenticationSummary { Capabilities = capabilities, Checks = checks };
    }

    public async Task<IntegrationQualityReport> AnalyzeAsync(
        IntegrationQualityRequest request, CancellationToken ct = default)
    {
        var findings        = new List<IntegrationFinding>();
        var statuses        = new List<IntegrationStatus>();
        var recommendations = new List<string>();
        var limitations     = new List<string>();

        // Populate producer/consumer relationships with source tracking (Phase 3)
        _relationshipPopulation.PopulateRelationships(request.Integrations);

        // History is resolved BEFORE the current review runs and before anything is saved, so a
        // run can never resolve its own snapshot as its baseline.
        var environmentId = ResolveEnvironmentId(request);
        var previousSnapshot = _snapshotRepository is null || string.IsNullOrWhiteSpace(environmentId)
            ? null
            : await _snapshotRepository.GetLatestAsync(environmentId, ct);

        var baselineProvider = new SnapshotScopedBaselineProvider(previousSnapshot);
        var snapshotEntries = new List<IntegrationSnapshotEntry>();

        foreach (var intg in request.Integrations)
        {
            if (!intg.Enabled)
            {
                statuses.Add(new IntegrationStatus
                {
                    IntegrationId    = intg.Id,
                    Name             = intg.Name,
                    Type             = intg.Type,
                    Enabled          = false,
                    HasRequiredFields = true,
                    Score            = 100,
                });
                continue;
            }

            var intgFindings  = new List<IntegrationFinding>();
            var missingFields = new List<string>();

            // ── 1. Required fields ────────────────────────────────────────────
            if (string.IsNullOrWhiteSpace(intg.Name))     missingFields.Add("Name");
            if (string.IsNullOrWhiteSpace(intg.Endpoint)) missingFields.Add("Endpoint");
            if (string.IsNullOrWhiteSpace(intg.Resource)) missingFields.Add("Resource");

            if (missingFields.Count > 0)
            {
                intgFindings.Add(Finding(
                    $"req-fields-{intg.Id}", intg.Id, EffectiveName(intg),
                    $"Required fields missing: {string.Join(", ", missingFields)}",
                    "Integration configuration is incomplete. Missing required fields prevent quality scoring and may cause runtime failures.",
                    "Fill in all required fields: Name, Endpoint, and Resource.",
                    IntegrationFindingSeverity.Critical, missingFields));
            }

            // ── 2. Async integrations require consumer ────────────────────────
            if (AsyncTypes.Contains(intg.Type) && string.IsNullOrWhiteSpace(intg.Consumer))
            {
                intgFindings.Add(Finding(
                    $"async-consumer-{intg.Id}", intg.Id, EffectiveName(intg),
                    "Consumer group / subscription is not configured",
                    $"{intg.Type} integrations require a consumer group or subscription name for runtime monitoring and readiness assessment.",
                    "Set the Consumer field to the consumer group name (Kafka/EventHub) or subscription name (ServiceBus/RabbitMQ).",
                    IntegrationFindingSeverity.High, [$"{intg.Type}"]));
            }

            // ── 3. Auth type should be documented ─────────────────────────────
            if (AsyncTypes.Contains(intg.Type) && intg.AuthType == IntegrationAuthType.None)
            {
                intgFindings.Add(Finding(
                    $"auth-undocumented-{intg.Id}", intg.Id, EffectiveName(intg),
                    "Authentication type is not documented",
                    $"The {intg.Type} integration has no authentication type specified. Async messaging integrations always require some form of authentication.",
                    "Set the authentication type to the actual mechanism used (e.g. ManagedIdentity, ConnectionString, SasToken).",
                    IntegrationFindingSeverity.Medium, [$"{intg.Type}"]));
            }

            // ── 4. Owner should be documented ────────────────────────────────
            if (string.IsNullOrWhiteSpace(intg.Owner))
            {
                intgFindings.Add(Finding(
                    $"owner-missing-{intg.Id}", intg.Id, EffectiveName(intg),
                    "Integration owner is not documented",
                    "Without an owner, there is no clear point of contact for incidents, schema changes, or SLA negotiations.",
                    "Set the Owner field to a team name, Slack channel, or individual responsible for this integration.",
                    IntegrationFindingSeverity.Low, []));
            }

            // ── 5. Async integrations should have monitoring URL ──────────────
            if (AsyncTypes.Contains(intg.Type) && string.IsNullOrWhiteSpace(intg.MonitoringUrl))
            {
                intgFindings.Add(Finding(
                    $"monitoring-missing-{intg.Id}", intg.Id, EffectiveName(intg),
                    "No monitoring URL or runbook configured",
                    $"{intg.Type} integrations should have a monitoring dashboard or runbook URL for on-call responders.",
                    "Add a monitoring URL pointing to the relevant dashboard (e.g. Azure Monitor, Grafana, Datadog).",
                    IntegrationFindingSeverity.Info, []));
            }

            // ── 6. Health URL probe ───────────────────────────────────────────
            bool? healthReachable = null;
            if (!string.IsNullOrWhiteSpace(intg.HealthUrl))
            {
                healthReachable = await ProbeUrlAsync(intg.HealthUrl, ct);
                if (healthReachable == false)
                {
                    intgFindings.Add(Finding(
                        $"health-unreachable-{intg.Id}", intg.Id, EffectiveName(intg),
                        $"Health endpoint is not reachable: {intg.HealthUrl}",
                        "The configured health endpoint did not respond successfully. This may indicate the integration's worker or service is down.",
                        "Verify the health endpoint URL is correct and the service is running.",
                        IntegrationFindingSeverity.High, [intg.HealthUrl]));
                }
            }

            // ── 7. Worker URL probe ───────────────────────────────────────────
            bool? workerReachable = null;
            if (!string.IsNullOrWhiteSpace(intg.WorkerUrl))
            {
                workerReachable = await ProbeUrlAsync(intg.WorkerUrl, ct);
                if (workerReachable == false)
                {
                    intgFindings.Add(Finding(
                        $"worker-unreachable-{intg.Id}", intg.Id, EffectiveName(intg),
                        $"Worker endpoint is not reachable: {intg.WorkerUrl}",
                        "The configured worker URL did not respond successfully. This indicates the consumer/processor may not be deployed.",
                        "Verify the worker URL is correct and the consumer service is running.",
                        IntegrationFindingSeverity.High, [intg.WorkerUrl]));
                }
            }

            // ── Scoring ───────────────────────────────────────────────────────
            int penalty = intgFindings.Sum(f => f.Severity switch
            {
                IntegrationFindingSeverity.Critical => 25,
                IntegrationFindingSeverity.High     => 15,
                IntegrationFindingSeverity.Medium   => 8,
                IntegrationFindingSeverity.Low      => 3,
                _                                   => 0
            });

            var status = new IntegrationStatus
            {
                IntegrationId    = intg.Id,
                Name             = EffectiveName(intg),
                Type             = intg.Type,
                Enabled          = true,
                HasRequiredFields = missingFields.Count == 0,
                HealthReachable  = healthReachable,
                WorkerReachable  = workerReachable,
                Score            = Math.Max(0, 100 - penalty),
                MissingFields    = missingFields
            };

            var identity = IntegrationBaselineIdentity.Describe(environmentId, intg);
            status.BaselineKey = identity.Key;

            // Runtime evidence comes only from observed traffic. Health and worker probe results
            // stay on HealthReachable/WorkerReachable and never feed latency or throughput.
            var observedEvidence = _runtimeEvidence.MapObservations(intg, request.RuntimeObservations);
            status.RuntimeEvidenceSummary = _runtimeEvidence.BuildEvidenceSummary(intg.Id, observedEvidence);
            status.Performance = _performanceAnalyzer.Analyze(observedEvidence);

            var currentContract = await ApplyContractAnalysisAsync(
                intg, status, intgFindings, identity.Key, baselineProvider, ct);

            var snapshotEntry = BuildSnapshotEntry(intg, status, identity, currentContract, null);
            snapshotEntries.Add(snapshotEntry);

            status.PerformanceChanges = _performanceAnalyzer
                .Compare(status.Performance, baselineProvider.FindEntry(identity.Key)?.Performance)
                .ToList();

            statuses.Add(status);

            findings.AddRange(intgFindings);
        }

        // ── Overall score ─────────────────────────────────────────────────────
        var enabledStatuses = statuses.Where(s => s.Enabled).ToList();
        int overallScore = enabledStatuses.Count > 0
            ? (int)enabledStatuses.Average(s => s.Score)
            : 100;

        int missingConfigCount = statuses.Count(s => s.Enabled && !s.HasRequiredFields);
        bool isReady           = overallScore >= 70 && !findings.Any(f => f.Severity == IntegrationFindingSeverity.Critical);

        // ── Recommendations ───────────────────────────────────────────────────
        if (findings.Any(f => f.Severity == IntegrationFindingSeverity.Critical))
            recommendations.Add("Resolve all critical findings immediately — missing required fields will prevent integrations from being reliably monitored.");
        if (findings.Any(f => f.Id.StartsWith("async-consumer")))
            recommendations.Add("Document consumer groups and subscription names for all async integrations — these are required for consumer lag monitoring.");
        if (findings.Any(f => f.Id.StartsWith("auth-undocumented")))
            recommendations.Add("Document authentication types for all async integrations to ensure security review coverage.");
        if (findings.Any(f => f.Id.StartsWith("health-unreachable") || f.Id.StartsWith("worker-unreachable")))
            recommendations.Add("Investigate unreachable health and worker endpoints before deploying to this environment.");
        if (findings.Any(f => f.Id.StartsWith("owner-missing")))
            recommendations.Add("Assign owners to all integrations — a documented owner reduces mean time to resolve incidents.");

        if (request.Integrations.Any(i => !i.Enabled))
            limitations.Add("Disabled integrations are excluded from the readiness score and findings.");

        var authentication = await BuildAuthenticationSummaryAsync(request, ct);

        // History is computed against the snapshot loaded before the review ran.
        var historicalChanges = _historyComparer.Compare(previousSnapshot, snapshotEntries);

        foreach (var status in statuses.Where(s => !string.IsNullOrWhiteSpace(s.BaselineKey)))
            status.HistoricalChanges = historicalChanges
                .Where(c => string.Equals(c.BaselineKey, status.BaselineKey, StringComparison.Ordinal))
                .ToList();

        var report = new IntegrationQualityReport
        {
            EnvironmentName    = request.EnvironmentName,
            GeneratedAt        = DateTime.UtcNow,
            OverallScore       = overallScore,
            IntegrationCount   = request.Integrations.Count,
            EnabledCount       = enabledStatuses.Count,
            MissingConfigCount = missingConfigCount,
            IsReadyForDeployment = isReady,
            Findings           = findings,
            Statuses           = statuses,
            Recommendations    = recommendations,
            Limitations        = limitations,
            Authentication     = authentication,

            PreviousSnapshotId = previousSnapshot?.SnapshotId,
            PreviousSnapshotCapturedAt = previousSnapshot?.CapturedAt,
            BaselineAvailable = previousSnapshot is not null,
            HistoricalChanges = historicalChanges.ToList(),
            HistoricalChangeCount = historicalChanges.Count
        };

        // Saved last, after the report is finalised. The baseline used above was loaded before
        // the review began, so this save can never become its own baseline.
        await PersistSnapshotAsync(report, environmentId, previousSnapshot, snapshotEntries, limitations, ct);

        return report;
    }

    private async Task<bool> ProbeUrlAsync(string url, CancellationToken ct)
    {
        try
        {
            using var req  = new HttpRequestMessage(HttpMethod.Head, url);
            using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            return (int)resp.StatusCode < 500;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Probe failed for {Url}", url);
            return false;
        }
    }

    private static string EffectiveName(IntegrationConfigDto intg) =>
        string.IsNullOrWhiteSpace(intg.Name) ? $"{intg.Type} (unnamed)" : intg.Name;

    /// <summary>
    /// Phase 3, Checkpoint 4. Resolves contract compatibility (producer -> consumer) and drift
    /// (current -> previous baseline) for one integration and records both on the status, plus
    /// any deterministic findings. The two states are independent; neither is derived from the
    /// other. A failure here is contained to this integration and never aborts the review.
    /// </summary>
    private async Task<NormalizedContract?> ApplyContractAnalysisAsync(
        IntegrationConfigDto intg,
        IntegrationStatus status,
        List<IntegrationFinding> intgFindings,
        string baselineKey,
        SnapshotScopedBaselineProvider baselineProvider,
        CancellationToken ct)
    {
        status.ProducerService = intg.LogicalProducerService;
        status.ConsumerService = intg.LogicalConsumerService;

        var contractName = !string.IsNullOrWhiteSpace(intg.ContractName)
            ? intg.ContractName!
            : EffectiveName(intg);

        // ── Compatibility ────────────────────────────────────────────────────
        if (_contractDiscovery is not null)
        {
            try
            {
                var compatibility = await _contractDiscovery.AnalyzeAsync(intg, ct);

                status.CompatibilityState = compatibility.Status;
                status.CompatibilityComparedAt = compatibility.ComparedAt;
                status.CompatibilityDifferenceCount = compatibility.Differences.Count;
                status.CompatibilityBreakingCount = compatibility.Differences
                    .Count(d => d.Severity == ContractDifferenceSeverity.Breaking);
                status.CompatibilityDifferences = compatibility.Differences;
                status.CompatibilityReason = compatibility.ReadyReason ?? compatibility.Message;
                status.ProducerContractSource = compatibility.ProducerSource;
                status.ConsumerContractSource = compatibility.ConsumerSource;

                var finding = ContractFindingMapper.ForCompatibility(
                    compatibility, intg.Id, EffectiveName(intg));

                if (finding is not null)
                    intgFindings.Add(finding);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Contract compatibility analysis failed for integration {IntegrationId}", intg.Id);

                status.CompatibilityState = ContractCompatibilityStatus.Error;
                status.CompatibilityReason = "Contract comparison could not be completed";
            }
        }

        // ── Drift ────────────────────────────────────────────────────────────
        // Baseline persistence is owned by Checkpoint 5. Until a provider supplies one this
        // resolves to BaselineUnavailable rather than an assumed "no change".
        NormalizedContract? currentContract = null;

        try
        {
            currentContract = await ResolveCurrentContractAsync(intg, ct);

            // Resolved from the snapshot loaded before this review started, keyed by baseline key.
            var baseline = await baselineProvider.GetBaselineAsync(baselineKey, contractName, ct);

            var drift = _contractComparer.CompareForDrift(
                currentContract, baseline?.Contract, contractName, baseline?.CapturedAt);

            status.DriftState = drift.State;
            status.DriftDifferenceCount = drift.Differences.Count;
            status.DriftBreakingCount = drift.Differences
                .Count(d => d.Severity == ContractDifferenceSeverity.Breaking);
            status.DriftDifferences = drift.Differences;
            status.PreviousBaselineTimestamp = drift.BaselineCapturedAt;
            status.CurrentContractFingerprint = drift.CurrentFingerprint;
            status.PreviousContractFingerprint = drift.BaselineFingerprint;

            var driftFinding = ContractFindingMapper.ForDrift(drift, intg.Id, EffectiveName(intg));

            if (driftFinding is not null)
                intgFindings.Add(driftFinding);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Contract drift analysis failed for integration {IntegrationId}", intg.Id);

            status.DriftState = ContractDriftState.NotComparable;
        }

        return currentContract;
    }

    /// <summary>
    /// Resolves the integration's current normalized contract where a deterministic extractor
    /// exists. Messaging contracts come from the Checkpoint 3 extractor; REST and GraphQL
    /// contracts are not surfaced by the discovery service as normalized contracts yet, so they
    /// return null and drift stays unresolved for those types.
    /// </summary>
    private async Task<NormalizedContract?> ResolveCurrentContractAsync(
        IntegrationConfigDto intg,
        CancellationToken ct)
    {
        if (_messageSchemaDiscovery is null || !AsyncTypes.Contains(intg.Type))
            return null;

        var extraction = await _messageSchemaDiscovery.ExtractSchemaAsync(intg, ct);

        return extraction.State == MessageSchemaState.Available
            ? extraction.NormalizedContract
            : null;
    }

    /// <summary>
    /// Authoritative environment identity for history. ProfileId is preferred because the
    /// environment display name can be edited freely; the name is only a fallback.
    /// </summary>
    private static string ResolveEnvironmentId(IntegrationQualityRequest request) =>
        !string.IsNullOrWhiteSpace(request.ProfileId)
            ? request.ProfileId!.Trim()
            : (request.EnvironmentName ?? "").Trim();

    /// <summary>
    /// Builds the per-integration entry retained for future comparison. Endpoint values come from
    /// the canonicalised structural identity, which has user info and query stripped, so no
    /// credentials reach persisted history.
    /// </summary>
    private static IntegrationSnapshotEntry BuildSnapshotEntry(
        IntegrationConfigDto integration,
        IntegrationStatus status,
        IntegrationStructuralIdentity identity,
        NormalizedContract? currentContract,
        IntegrationAuthenticationSummary? authentication)
    {
        var checksExecuted = authentication?.Checks
            .Count(c => c.IntegrationId == integration.Id) ?? 0;

        return new IntegrationSnapshotEntry
        {
            BaselineKey = identity.Key,
            IntegrationId = integration.Id,
            DisplayName = status.Name,
            IntegrationType = integration.Type,
            CanonicalEndpoint = identity.CanonicalEndpoint,
            CanonicalResource = identity.CanonicalResource,

            Producer = integration.LogicalProducerService,
            Consumer = integration.LogicalConsumerService,
            RelationshipSource = integration.ProducerConsumerSource,

            ContractState = status.CompatibilityState,
            ContractName = integration.ContractName,
            ContractSourceType = integration.ContractSourceType,
            ContractSource = status.ProducerContractSource,
            ContractFingerprint = currentContract is null
                ? null
                : ContractComparer.Fingerprint(currentContract),
            NormalizedContract = currentContract,

            RuntimeEvidenceState = status.RuntimeEvidenceSummary is null
                ? RuntimeEvidenceState.Unknown
                : status.RuntimeEvidenceSummary.HasRuntimeEvidence
                    ? RuntimeEvidenceState.Observed
                    : RuntimeEvidenceState.NoEvidence,
            LatestObservedAt = status.RuntimeEvidenceSummary?.LastObservedAt,
            EvidenceCount = status.RuntimeEvidenceSummary?.EvidenceCount ?? 0,
            EvidenceSources = status.RuntimeEvidenceSummary?.Sources ?? [],

            AuthenticationRequired = integration.AuthType != IntegrationAuthType.None,
            AuthenticatedCapabilityAvailable = authentication?.Capabilities.AuthenticatedRest,
            AuthenticatedChecksExecuted = checksExecuted,

            // Summary only: the raw observation payload is not persisted.
            Performance = IntegrationPerformanceAnalyzer.ToSummary(status.Performance)
        };
    }

    /// <summary>
    /// Persists the completed review as an immutable snapshot.
    ///
    /// This runs only after the report has been finalised, and the previous snapshot it links to
    /// was loaded before the review began, so a run can never baseline against itself. A failure
    /// here is recorded as a typed persistence state and a limitation; the completed review, and
    /// the compatibility and drift already computed, are always preserved.
    /// </summary>
    private async Task PersistSnapshotAsync(
        IntegrationQualityReport report,
        string environmentId,
        IntegrationQualitySnapshot? previousSnapshot,
        List<IntegrationSnapshotEntry> entries,
        List<string> limitations,
        CancellationToken ct)
    {
        if (_snapshotRepository is null || string.IsNullOrWhiteSpace(environmentId))
        {
            report.SnapshotPersistenceState = SnapshotPersistenceState.NotAttempted;
            return;
        }

        // A review that produced no assessable integration is not a usable baseline.
        if (entries.Count == 0)
        {
            report.SnapshotPersistenceState = SnapshotPersistenceState.SkippedIncompleteReview;
            limitations.Add("No historical snapshot was recorded because this review assessed no integrations.");
            return;
        }

        var completeness = report.Statuses.Any(s => s.Enabled && !s.HasRequiredFields)
            ? SnapshotCompleteness.Partial
            : SnapshotCompleteness.Complete;

        var snapshot = new IntegrationQualitySnapshot
        {
            SnapshotId = Guid.NewGuid(),
            EnvironmentId = environmentId,
            CapturedAt = DateTimeOffset.UtcNow,
            SnapshotVersion = IntegrationQualitySnapshot.CurrentSnapshotVersion,
            BaselineIdentityVersion = IntegrationBaselineIdentity.Version,
            PreviousSnapshotId = previousSnapshot?.SnapshotId,
            Completeness = completeness,
            Integrations = entries
        };

        try
        {
            await _snapshotRepository.SaveAsync(snapshot, ct);

            report.CurrentSnapshotId = snapshot.SnapshotId;
            report.SnapshotPersistenceState = SnapshotPersistenceState.Saved;

            if (completeness == SnapshotCompleteness.Partial)
                limitations.Add(
                    "This review was recorded as a partial snapshot because some integrations have missing required fields. It will not be treated as a complete baseline.");
        }
        catch (Exception ex)
        {
            // The review itself succeeded. Report the persistence failure honestly rather than
            // discarding the result or implying the snapshot was stored.
            _logger.LogError(ex,
                "Failed to persist Integration Quality snapshot for environment {EnvironmentId}", environmentId);

            report.SnapshotPersistenceState = SnapshotPersistenceState.Failed;
            limitations.Add(
                "This review completed, but could not be recorded as a historical snapshot. Future reviews will not be able to compare against it.");
        }
    }

    private static IntegrationFinding Finding(
        string id, string integrationId, string integrationName,
        string title, string description, string recommendation,
        IntegrationFindingSeverity severity, IEnumerable<string> evidence) =>
        new()
        {
            Id              = id,
            IntegrationId   = integrationId,
            IntegrationName = integrationName,
            Title           = title,
            Description     = description,
            Recommendation  = recommendation,
            Severity        = severity,
            Evidence        = evidence.ToList()
        };
}

