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
    private readonly IContractBaselineProvider? _baselineProvider;
    private readonly IContractComparer _contractComparer;

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
        IContractBaselineProvider? baselineProvider = null,
        IContractComparer? contractComparer = null)
    {
        _client = client;
        _logger = logger;
        _authenticatedReview = authenticatedReview;
        _relationshipPopulation = relationshipPopulation;
        _contractDiscovery = contractDiscovery;
        _messageSchemaDiscovery = messageSchemaDiscovery;
        _baselineProvider = baselineProvider;
        _contractComparer = contractComparer ?? new ContractComparer();
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

            await ApplyContractAnalysisAsync(intg, status, intgFindings, ct);

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

        return new IntegrationQualityReport
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
            Authentication     = authentication
        };
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
    private async Task ApplyContractAnalysisAsync(
        IntegrationConfigDto intg,
        IntegrationStatus status,
        List<IntegrationFinding> intgFindings,
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
        try
        {
            var currentContract = await ResolveCurrentContractAsync(intg, ct);

            var baseline = _baselineProvider is null
                ? null
                : await _baselineProvider.GetBaselineAsync(intg.Id, contractName, ct);

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
