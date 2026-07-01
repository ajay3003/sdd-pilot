using System.Diagnostics;

namespace BirkNext.Api.Services.IntegrationQuality;

public sealed class IntegrationQualityReviewService : IIntegrationQualityReviewService
{
    private readonly HttpClient _client;
    private readonly ILogger<IntegrationQualityReviewService> _logger;

    private static readonly HashSet<IntegrationType> AsyncTypes =
    [
        IntegrationType.EventHub, IntegrationType.ServiceBus,
        IntegrationType.Kafka, IntegrationType.RabbitMQ
    ];

    public IntegrationQualityReviewService(HttpClient client, ILogger<IntegrationQualityReviewService> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task<IntegrationQualityReport> AnalyzeAsync(
        IntegrationQualityRequest request, CancellationToken ct = default)
    {
        var findings        = new List<IntegrationFinding>();
        var statuses        = new List<IntegrationStatus>();
        var recommendations = new List<string>();
        var limitations     = new List<string>();

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

            statuses.Add(new IntegrationStatus
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
            });

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
            Limitations        = limitations
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
