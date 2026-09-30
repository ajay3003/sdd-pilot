using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using BirkNext.Integrations;

namespace BirkNext.Api.Services.Integrations.EventHub;

/// <summary>Read-only Event Hubs namespace metadata and hub list. Implementations never send, receive, checkpoint or change a resource.</summary>
public interface IEventHubNamespaceSource
{
    /// <summary>Configuration-only readiness (nothing is contacted).</summary>
    IntegrationEvidenceAdapterStatus Describe(IntegrationPlatform platform);
    Task<EventHubNamespaceObservation> ReadAsync(IntegrationPlatform platform, CancellationToken ct);
}

/// <summary>
/// Event Hubs namespace metadata through Azure Resource Manager (management plane) only: HTTP GET of the namespace and of its hub list
/// (status, partition count, retention). The data plane is never used — no event is sent or received, no processor or checkpoint is
/// touched — and no PUT/PATCH/DELETE is issued. Requires Reader on the namespace for the instance's Azure identity. Failures stay typed:
/// disabled, not configured, not authorized (401/403), not found (404), network/timeout (unavailable) and error; a reason names the
/// status only, never a response body.
/// </summary>
public sealed class ArmEventHubNamespaceSource(IIntegrationAzureCredential azure, HttpClient http, ILogger<ArmEventHubNamespaceSource> logger) : IEventHubNamespaceSource
{
    public const string Adapter = "Event Hub namespace (Azure Resource Manager)";
    internal const string ApiVersion = "2024-01-01";
    private const int MaxPages = 20;

    public IntegrationEvidenceAdapterStatus Describe(IntegrationPlatform platform) =>
        azure.Credential is null ? NotConfiguredEvidence.Status(Adapter, IntegrationEvidenceSource.AzureResourceManager, IntegrationEvidenceState.NotConfigured, azure.DisabledReason)
        : platform.RuntimeEvidence?.EventHubMetadata != true ? NotConfiguredEvidence.Status(Adapter, IntegrationEvidenceSource.AzureResourceManager, IntegrationEvidenceState.NotConfigured, "Event Hub metadata is not enabled for this platform.")
        : !EventHubRuntimeSources.ResourceManager(platform)
            ? NotConfiguredEvidence.Status(Adapter, IntegrationEvidenceSource.AzureResourceManager, IntegrationEvidenceState.NotConfigured,
                "The Azure subscription id, resource group and namespace name are needed to read the namespace and its hubs.")
        : NotConfiguredEvidence.Status(Adapter, IntegrationEvidenceSource.AzureResourceManager, IntegrationEvidenceState.Available, "Configured; read-only namespace and hub-list GETs (Reader on the namespace).");

    /// <summary>The namespace resource path (subscription, resource group and namespace escaped).</summary>
    internal static string NamespacePath(IntegrationPlatform platform) =>
        $"/subscriptions/{Uri.EscapeDataString(platform.RuntimeEvidence!.SubscriptionId!.Trim())}/resourceGroups/{Uri.EscapeDataString(platform.ResourceGroup!.Trim())}" +
        $"/providers/Microsoft.EventHub/namespaces/{Uri.EscapeDataString(platform.Namespace!.Trim())}";

    public async Task<EventHubNamespaceObservation> ReadAsync(IntegrationPlatform platform, CancellationToken ct)
    {
        EventHubNamespaceObservation Missing(IntegrationEvidenceState state, string reason) => new() { State = state, Reason = reason, CapturedAt = DateTimeOffset.UtcNow, HubListState = state, HubListReason = reason };
        var readiness = Describe(platform);
        if (readiness.State != IntegrationEvidenceState.Available) return Missing(readiness.State, readiness.Reason);
        try
        {
            var token = (await azure.Credential!.GetTokenAsync(new TokenRequestContext(["https://management.azure.com/.default"]), ct)).Token;
            var root = NamespacePath(platform);
            var (nsState, nsReason, nsDocs) = await GetAsync(token, $"https://management.azure.com{root}?api-version={ApiVersion}", single: true, ct);
            if (nsState != IntegrationEvidenceState.Available) return Missing(nsState, $"Namespace {platform.Namespace}: {nsReason}");
            var ns = nsDocs.Single();
            var (hState, hReason, hubs) = await GetAsync(token, $"https://management.azure.com{root}/eventhubs?api-version={ApiVersion}", single: false, ct);
            var observed = hState == IntegrationEvidenceState.Available ? hubs.Select(Hub).Where(h => h.Name.Length > 0).ToList() : [];
            logger.LogInformation("Event Hub namespace metadata for {Namespace}: status {Status}, {Hubs} hub(s) listed ({ListState}).",
                platform.Namespace, Text(ns, "properties", "status"), observed.Count, hState);
            return new EventHubNamespaceObservation
            {
                State = IntegrationEvidenceState.Available, Reason = "Namespace metadata read through Azure Resource Manager (GET only).", CapturedAt = DateTimeOffset.UtcNow,
                Status = Text(ns, "properties", "status"), Sku = Text(ns, "sku", "name"), Location = Text(ns, "location"),
                PublicNetworkAccess = Text(ns, "properties", "publicNetworkAccess"), DisableLocalAuth = Bool(ns, "properties", "disableLocalAuth"),
                MinimumTlsVersion = Text(ns, "properties", "minimumTlsVersion"),
                PrivateEndpointConnections = Element(ns, "properties", "privateEndpointConnections") is { ValueKind: JsonValueKind.Array } pe ? pe.GetArrayLength() : null,
                HubListState = hState, HubListReason = hState == IntegrationEvidenceState.Available ? $"{observed.Count} hub(s) listed." : $"Hub list: {hReason}", Hubs = observed,
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or AuthenticationFailedException or CredentialUnavailableException or JsonException or TaskCanceledException)
        {
            var state = ex is HttpRequestException or TaskCanceledException ? IntegrationEvidenceState.Unavailable : AzureEvidence.StateOf(ex);
            logger.LogInformation("Event Hub namespace metadata for {Namespace} unavailable: {Error}.", platform.Namespace, AzureEvidence.Describe(ex));
            return Missing(state, $"Event Hub namespace metadata could not be read ({AzureEvidence.Describe(ex)}).");
        }
    }

    /// <summary>GET (never any other method), following nextLink pages for lists. Status codes map to typed states; bodies of failures are never read.</summary>
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

    /// <summary>Name, status, partitions and retention of one hub document. Retention: retentionDescription hours when present, else days × 24.</summary>
    internal static EventHubObservedHub Hub(JsonElement hub) => new()
    {
        Name = Text(hub, "name") ?? "",
        Status = Text(hub, "properties", "status"),
        PartitionCount = Element(hub, "properties", "partitionCount") is { ValueKind: JsonValueKind.Number } p && p.TryGetInt32(out var partitions) ? partitions : null,
        RetentionHours = Element(hub, "properties", "retentionDescription", "retentionTimeInHours") is { ValueKind: JsonValueKind.Number } h && h.TryGetInt64(out var hours) ? hours
            : Element(hub, "properties", "messageRetentionInDays") is { ValueKind: JsonValueKind.Number } d && d.TryGetInt64(out var days) ? days * 24 : null,
    };

    private static JsonElement? Element(JsonElement root, params string[] path)
    {
        var current = root;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out var next)) return null;
            current = next;
        }
        return current;
    }

    private static string? Text(JsonElement root, params string[] path) => Element(root, path) is { ValueKind: JsonValueKind.String } e ? e.GetString() : null;
    private static bool? Bool(JsonElement root, params string[] path) => Element(root, path) is { ValueKind: JsonValueKind.True or JsonValueKind.False } e ? e.GetBoolean() : null;
}
