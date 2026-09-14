using System.Text.Json;
using System.Text.Json.Serialization;
using BirkNext.LocalHttpsProxy;
using BirkNext.Web.Models;
using Microsoft.JSInterop;

namespace BirkNext.Web.Services;

/// <summary>A configuration-discovered backend integration the browser proxy cannot observe (messaging, event streaming, database).</summary>
public sealed record BackendIntegration(string Name, string Protocol, string? Resource, string Source, bool RuntimeObserved);

public interface IEndpointDiscoveryService
{
    Task LoadAsync(IJSRuntime js);
    EndpointDiscoverySnapshot GetSnapshot(string profileId);
    /// <summary>Folds the current runtime's observed endpoints into the persisted per-page snapshot and persists. Returns true when anything changed.</summary>
    Task<bool> MergeObservedAsync(IJSRuntime js, string profileId, IReadOnlyList<ObservedNetworkEndpoint> observed);
    Task DeletePageAsync(IJSRuntime js, string profileId, string pageIdentity);
    Task ClearPageAsync(IJSRuntime js, string profileId, string pageIdentity);
    /// <summary>Re-analyze: keep the page identity but reset its observation window so fresh traffic repopulates it.</summary>
    Task ReanalyzePageAsync(IJSRuntime js, string profileId, string pageIdentity);
    /// <summary>Delete all browser-observed analyses (pages + shared). Configured backend integrations are computed from config and are never affected.</summary>
    Task DeleteAllAsync(IJSRuntime js, string profileId);
}

/// <summary>
/// App-scoped store of safe, page-oriented endpoint discovery per Target Environment, persisted to <c>birknext:endpoint-discovery</c>.
/// Never holds or persists a credential, cookie, request/response body, sensitive query string or proxy session id. Independent of the
/// live authenticated runtime: discovery history survives a restart while the authenticated context does not.
/// </summary>
public sealed class EndpointDiscoveryService : IEndpointDiscoveryService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private Dictionary<string, EndpointDiscoverySnapshot> _byProfile = new(StringComparer.Ordinal);

    public async Task LoadAsync(IJSRuntime js)
    {
        try
        {
            var json = await js.InvokeAsync<string?>("birkNextStorage.getDiscovery");
            if (!string.IsNullOrWhiteSpace(json))
                _byProfile = JsonSerializer.Deserialize<Dictionary<string, EndpointDiscoverySnapshot>>(json, JsonOptions) ?? new(StringComparer.Ordinal);
        }
        catch { /* corrupt or unavailable storage: start empty rather than fail the page */ }
    }

    public EndpointDiscoverySnapshot GetSnapshot(string profileId) =>
        _byProfile.TryGetValue(profileId, out var snapshot) ? snapshot : new EndpointDiscoverySnapshot();

    public async Task<bool> MergeObservedAsync(IJSRuntime js, string profileId, IReadOnlyList<ObservedNetworkEndpoint> observed)
    {
        if (observed.Count == 0) return false;
        var snapshot = _byProfile.TryGetValue(profileId, out var existing) ? existing : new EndpointDiscoverySnapshot();
        var changed = EndpointDiscoveryMerge.Merge(snapshot, observed, DateTimeOffset.UtcNow);
        if (!changed) return false;
        _byProfile[profileId] = snapshot;
        await PersistAsync(js);
        return true;
    }

    public Task DeletePageAsync(IJSRuntime js, string profileId, string pageIdentity) => MutateAsync(js, profileId, s =>
        s.Pages.RemoveAll(p => p.Identity == pageIdentity) > 0);

    public Task ClearPageAsync(IJSRuntime js, string profileId, string pageIdentity) => MutateAsync(js, profileId, s =>
    {
        var page = s.Pages.FirstOrDefault(p => p.Identity == pageIdentity);
        if (page is null || page.Endpoints.Count == 0) return page is not null;
        page.Endpoints.Clear();
        return true;
    });

    public Task ReanalyzePageAsync(IJSRuntime js, string profileId, string pageIdentity) => ClearPageAsync(js, profileId, pageIdentity);

    public Task DeleteAllAsync(IJSRuntime js, string profileId) => MutateAsync(js, profileId, s =>
    {
        var had = s.Pages.Count > 0 || s.Shared.Count > 0;
        s.Pages.Clear();
        s.Shared.Clear();
        return had;
    });

    private async Task MutateAsync(IJSRuntime js, string profileId, Func<EndpointDiscoverySnapshot, bool> mutate)
    {
        if (!_byProfile.TryGetValue(profileId, out var snapshot)) return;
        if (!mutate(snapshot)) return;
        snapshot.UpdatedAt = DateTimeOffset.UtcNow;
        await PersistAsync(js);
    }

    private async Task PersistAsync(IJSRuntime js)
    {
        try { await js.InvokeVoidAsync("birkNextStorage.setDiscovery", JsonSerializer.Serialize(_byProfile, JsonOptions)); }
        catch { /* persistence best-effort; the in-memory view stays correct for this session */ }
    }

    /// <summary>Configured backend integrations (messaging, event streaming, database) the browser proxy cannot observe. Computed live from config; never browser-observed.</summary>
    public static IReadOnlyList<BackendIntegration> BackendIntegrationsFor(FrontendAnalysisProfile? profile)
    {
        if (profile is null) return [];
        return profile.Integrations
            .Where(i => i.Enabled && i.Type is IntegrationType.RabbitMQ or IntegrationType.EventHub or IntegrationType.ServiceBus or IntegrationType.Kafka or IntegrationType.File or IntegrationType.SOAP)
            .Select(i => new BackendIntegration(
                string.IsNullOrWhiteSpace(i.Name) ? i.Type.ToString() : i.Name,
                Protocol(i.Type),
                SafeResource(i),
                "Configuration discovery",
                RuntimeObserved: false))
            .ToList();
    }

    private static string Protocol(IntegrationType type) => type switch
    {
        IntegrationType.RabbitMQ => "AMQP",
        IntegrationType.EventHub => "Event Hub / AMQP",
        IntegrationType.ServiceBus => "Service Bus / AMQP",
        IntegrationType.Kafka => "Kafka",
        IntegrationType.File => "File",
        IntegrationType.SOAP => "SOAP",
        _ => type.ToString()
    };

    // The resource/host only — never a connection string or credential. Bare hosts pass through; anything containing credential markers is dropped.
    private static string? SafeResource(IntegrationConfig integration)
    {
        var value = integration.Resource ?? integration.Endpoint;
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        if (trimmed.Contains('=') || trimmed.Contains(';') || trimmed.Contains('@') || trimmed.Contains("://"))
            return null;   // looks like a connection string / credential-bearing URI: never surfaced
        return trimmed;
    }
}

/// <summary>Pure merge/collapse core for endpoint discovery, kept separate from JS/storage so it is directly unit-testable.</summary>
public static class EndpointDiscoveryMerge
{
    public static bool Merge(EndpointDiscoverySnapshot snapshot, IReadOnlyList<ObservedNetworkEndpoint> observed, DateTimeOffset now)
    {
        var changed = false;
        foreach (var endpoint in observed)
        {
            if (endpoint.PageOrigin is null || endpoint.PagePath is null)
            {
                changed |= Upsert(snapshot.Shared, endpoint);
                continue;
            }
            var identity = $"{endpoint.PageOrigin}{endpoint.PagePath}";
            var page = snapshot.Pages.FirstOrDefault(p => p.Identity == identity);
            if (page is null)
            {
                page = new PageAnalysis { PageOrigin = endpoint.PageOrigin, PagePath = endpoint.PagePath, FirstObservedAt = endpoint.FirstObservedAt, LastObservedAt = endpoint.LastObservedAt };
                snapshot.Pages.Add(page);
                changed = true;
            }
            changed |= Upsert(page.Endpoints, endpoint);
            if (endpoint.FirstObservedAt < page.FirstObservedAt) page.FirstObservedAt = endpoint.FirstObservedAt;
            if (endpoint.LastObservedAt > page.LastObservedAt) page.LastObservedAt = endpoint.LastObservedAt;
        }
        if (changed) snapshot.UpdatedAt = now;
        return changed;
    }

    /// <summary>Collapse identity within a page/shared list: category + scheme + host + port + path + method.</summary>
    public static string Key(ObservedNetworkEndpoint e) => $"{e.Category}|{e.Scheme}|{e.Host}|{e.Port}|{e.Path}|{e.Method}";

    private static bool Upsert(List<ObservedNetworkEndpoint> list, ObservedNetworkEndpoint incoming)
    {
        var key = Key(incoming);
        var index = list.FindIndex(e => Key(e) == key);
        if (index < 0) { list.Add(incoming); return true; }
        var existing = list[index];
        list[index] = existing with
        {
            Count = Math.Max(existing.Count, incoming.Count),
            LastStatus = incoming.LastStatus,
            AuthObserved = existing.AuthObserved || incoming.AuthObserved,
            Confidence = (ObservedEndpointConfidence)Math.Max((int)existing.Confidence, (int)incoming.Confidence),
            FirstObservedAt = incoming.FirstObservedAt < existing.FirstObservedAt ? incoming.FirstObservedAt : existing.FirstObservedAt,
            LastObservedAt = incoming.LastObservedAt > existing.LastObservedAt ? incoming.LastObservedAt : existing.LastObservedAt,
            OperationType = incoming.OperationType != GraphQlOperationType.None ? incoming.OperationType : existing.OperationType,
            OperationName = incoming.OperationName ?? existing.OperationName
        };
        return true;
    }
}
