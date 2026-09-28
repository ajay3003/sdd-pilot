using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using BirkNext.Integrations;

namespace BirkNext.Api.Services.Integrations.ServiceBus;

/// <summary>Read-only Service Bus runtime metadata of one namespace. Implementations never send, receive, peek or settle, and never change entities.</summary>
public interface IServiceBusMetadataSource
{
    /// <summary>Configuration-only readiness (nothing is contacted).</summary>
    IntegrationEvidenceAdapterStatus Describe(IntegrationPlatform platform);
    Task<ServiceBusRuntimeEvidence> ReadAsync(IntegrationPlatform platform, CancellationToken ct);
}

/// <summary>
/// Service Bus management metadata through Azure Resource Manager (management plane) only: HTTP GET of the namespace, its queues, topics and
/// each topic's subscriptions. The entity documents carry configured properties and point-in-time count details (active, dead-letter,
/// scheduled, transfer). The data plane is never used — no message is sent, received, peeked, completed, abandoned, dead-lettered or
/// deferred — and no PUT/PATCH/DELETE is ever issued. Requires Reader on the namespace for the instance's Azure identity.
/// Failures stay typed: disabled, not configured, not authorized (401/403), not found (404), network/timeout (unavailable) and error.
/// </summary>
public sealed class ArmServiceBusMetadataSource(IIntegrationAzureCredential azure, HttpClient http, ILogger<ArmServiceBusMetadataSource> logger) : IServiceBusMetadataSource
{
    public const string Adapter = "Service Bus metadata (Azure Resource Manager)";
    internal const string ApiVersion = "2021-11-01";
    private const int MaxPages = 20;

    public IntegrationEvidenceAdapterStatus Describe(IntegrationPlatform platform) =>
        azure.Credential is null ? NotConfiguredEvidence.Status(Adapter, IntegrationEvidenceSource.AzureResourceManager, IntegrationEvidenceState.NotConfigured, azure.DisabledReason)
        : !Guid.TryParse(platform.RuntimeEvidence?.SubscriptionId, out _) || string.IsNullOrWhiteSpace(platform.ResourceGroup) || string.IsNullOrWhiteSpace(platform.Namespace)
            ? NotConfiguredEvidence.Status(Adapter, IntegrationEvidenceSource.AzureResourceManager, IntegrationEvidenceState.NotConfigured,
                "The Azure subscription id, resource group and namespace name of the Service Bus namespace are needed to read its metadata.")
        : NotConfiguredEvidence.Status(Adapter, IntegrationEvidenceSource.AzureResourceManager, IntegrationEvidenceState.Available, "Configured; read-only metadata GETs (Reader on the namespace).");

    /// <summary>The namespace resource path (subscription, resource group and namespace escaped); entity paths are appended to it.</summary>
    internal static string NamespacePath(IntegrationPlatform platform) =>
        $"/subscriptions/{Uri.EscapeDataString(platform.RuntimeEvidence!.SubscriptionId!)}/resourceGroups/{Uri.EscapeDataString(platform.ResourceGroup!)}" +
        $"/providers/Microsoft.ServiceBus/namespaces/{Uri.EscapeDataString(platform.Namespace!)}";

    public async Task<ServiceBusRuntimeEvidence> ReadAsync(IntegrationPlatform platform, CancellationToken ct)
    {
        ServiceBusRuntimeEvidence Missing(IntegrationEvidenceState state, string reason) =>
            new() { PlatformId = platform.Id, Namespace = platform.Namespace, State = state, Reason = reason, CapturedAt = DateTimeOffset.UtcNow };
        var readiness = Describe(platform);
        if (readiness.State != IntegrationEvidenceState.Available) return Missing(readiness.State, readiness.Reason);
        try
        {
            var token = (await azure.Credential!.GetTokenAsync(new TokenRequestContext(["https://management.azure.com/.default"]), ct)).Token;
            var root = NamespacePath(platform);
            var (nsState, nsReason, nsDocs) = await GetAsync(token, $"https://management.azure.com{root}?api-version={ApiVersion}", single: true, ct);
            if (nsState != IntegrationEvidenceState.Available) return Missing(nsState, $"Namespace {platform.Namespace}: {nsReason}");
            var ns = nsDocs.Single();
            var entities = new List<ServiceBusEntityObservation>();
            var failures = new Dictionary<string, string>();

            var (qState, qReason, queues) = await GetAsync(token, $"https://management.azure.com{root}/queues?api-version={ApiVersion}", single: false, ct);
            if (qState == IntegrationEvidenceState.Available) entities.AddRange(queues.Select(q => Observation(ServiceBusEntityType.Queue, q, null)));
            else failures["Queues"] = $"{IntegrationReviewLabels.EvidenceState(qState)}: {qReason}";

            var (tState, tReason, topics) = await GetAsync(token, $"https://management.azure.com{root}/topics?api-version={ApiVersion}", single: false, ct);
            if (tState == IntegrationEvidenceState.Available)
            {
                foreach (var topic in topics)
                {
                    var observed = Observation(ServiceBusEntityType.Topic, topic, null);
                    entities.Add(observed);
                    var (sState, sReason, subscriptions) = await GetAsync(token,
                        $"https://management.azure.com{root}/topics/{Uri.EscapeDataString(observed.Name)}/subscriptions?api-version={ApiVersion}", single: false, ct);
                    if (sState == IntegrationEvidenceState.Available) entities.AddRange(subscriptions.Select(s => Observation(ServiceBusEntityType.Subscription, s, observed.Name)));
                    else failures[$"Subscriptions of {observed.Name}"] = $"{IntegrationReviewLabels.EvidenceState(sState)}: {sReason}";
                }
            }
            else failures["Topics"] = $"{IntegrationReviewLabels.EvidenceState(tState)}: {tReason}";

            logger.LogInformation("Service Bus metadata for {Namespace}: {Queues} queue(s), {Topics} topic(s), {Subscriptions} subscription(s), {Failures} list failure(s).",
                platform.Namespace, entities.Count(e => e.EntityType == ServiceBusEntityType.Queue), entities.Count(e => e.EntityType == ServiceBusEntityType.Topic),
                entities.Count(e => e.EntityType == ServiceBusEntityType.Subscription), failures.Count);
            return new ServiceBusRuntimeEvidence
            {
                PlatformId = platform.Id, Namespace = platform.Namespace, State = IntegrationEvidenceState.Available, CapturedAt = DateTimeOffset.UtcNow,
                Reason = "Management metadata read through Azure Resource Manager (GET only).",
                NamespaceStatus = Text(ns, "properties", "status"), Sku = Text(ns, "sku", "name"), Entities = entities, ListFailures = failures,
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or AuthenticationFailedException or CredentialUnavailableException or JsonException or TaskCanceledException)
        {
            var state = ex is HttpRequestException or TaskCanceledException ? IntegrationEvidenceState.Unavailable : AzureEvidence.StateOf(ex);
            logger.LogInformation("Service Bus metadata for {Namespace} unavailable: {Error}.", platform.Namespace, AzureEvidence.Describe(ex));
            return Missing(state, $"Service Bus metadata could not be read ({AzureEvidence.Describe(ex)}).");
        }
    }

    /// <summary>GET (never any other method), following nextLink pages for lists. Status codes map to typed states.</summary>
    private async Task<(IntegrationEvidenceState State, string Reason, List<JsonElement> Documents)> GetAsync(string token, string url, bool single, CancellationToken ct)
    {
        var documents = new List<JsonElement>();
        for (var page = 0; url is not null && page < MaxPages; page++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await http.SendAsync(request, ct);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return (IntegrationEvidenceState.NotAuthorized, $"not authorized (HTTP {(int)response.StatusCode}).", []);
            if (response.StatusCode == HttpStatusCode.NotFound) return (IntegrationEvidenceState.NotFound, "not found in Azure Resource Manager (HTTP 404).", []);
            if (!response.IsSuccessStatusCode) return (IntegrationEvidenceState.Error, $"read failed (HTTP {(int)response.StatusCode}).", []);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (single) { documents.Add(document.RootElement.Clone()); break; }
            if (document.RootElement.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array)
                documents.AddRange(value.EnumerateArray().Select(v => v.Clone()));
            url = document.RootElement.TryGetProperty("nextLink", out var next) && next.ValueKind == JsonValueKind.String && next.GetString() is { } link
                  && link.StartsWith("https://management.azure.com/", StringComparison.OrdinalIgnoreCase) ? link : null!;
        }
        return (IntegrationEvidenceState.Available, "", documents);
    }

    private static readonly string[] PropertyNames =
    [
        "maxDeliveryCount", "lockDuration", "defaultMessageTimeToLive", "requiresSession", "deadLetteringOnMessageExpiration",
        "deadLetteringOnFilterEvaluationExceptions", "requiresDuplicateDetection", "duplicateDetectionHistoryTimeWindow", "maxSizeInMegabytes",
        "enablePartitioning", "autoDeleteOnIdle", "forwardTo", "forwardDeadLetteredMessagesTo", "enableBatchedOperations",
    ];

    /// <summary>One entity document reduced to its name, status, known properties and count details. Nothing else is kept.</summary>
    internal static ServiceBusEntityObservation Observation(ServiceBusEntityType type, JsonElement document, string? topic)
    {
        var properties = document.TryGetProperty("properties", out var p) && p.ValueKind == JsonValueKind.Object ? p : default;
        var counts = properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty("countDetails", out var c) && c.ValueKind == JsonValueKind.Object ? c : default;
        long? Count(string name) => counts.ValueKind == JsonValueKind.Object && counts.TryGetProperty(name, out var v) && v.TryGetInt64(out var n) ? n : null;
        DateTimeOffset? Time(string name) => properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(v.GetString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var t) ? t : null;
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (properties.ValueKind == JsonValueKind.Object)
            foreach (var name in PropertyNames)
                if (properties.TryGetProperty(name, out var v))
                    values[name] = v.ValueKind switch { JsonValueKind.String => v.GetString(), JsonValueKind.True => "true", JsonValueKind.False => "false", JsonValueKind.Number => v.GetRawText(), _ => null };
        return new ServiceBusEntityObservation
        {
            EntityType = type, Name = document.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "", Topic = topic,
            Status = properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty("status", out var s) ? s.GetString() : null, Properties = values,
            ActiveMessageCount = Count("activeMessageCount"), DeadLetterMessageCount = Count("deadLetterMessageCount"), ScheduledMessageCount = Count("scheduledMessageCount"),
            TransferMessageCount = Count("transferMessageCount"), TransferDeadLetterMessageCount = Count("transferDeadLetterMessageCount"),
            UpdatedAt = Time("updatedAt"), AccessedAt = Time("accessedAt"),
        };
    }

    private static string? Text(JsonElement document, string section, string name) =>
        document.TryGetProperty(section, out var s) && s.ValueKind == JsonValueKind.Object && s.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
