using System.Text.Json;
using System.Text.Json.Serialization;
using BirkNext.BrowserCompanion;
using BirkNext.LocalHttpsProxy;
using BirkNext.Web.Models;
using Microsoft.JSInterop;

namespace BirkNext.Web.Services;

/// <summary>A configuration-discovered backend integration the browser proxy cannot observe (messaging, event streaming, database).</summary>
public sealed record BackendIntegration(string Name, string Protocol, string? Resource, string Source, bool RuntimeObserved);

public interface IEndpointDiscoveryService
{
    void ConfigureTarget(FrontendAnalysisProfile profile) { }
    WcagAssessment GetAssessment(string profileId, PageAnalysis? page = null) => BrowserQualityAssessmentService.ForPage(BrowserQualityAssessmentService.Assess(GetSnapshot(profileId)), page);
    Task SaveWcagSettingsAsync(IJSRuntime js, string profileId, WcagSettings settings);
    Task RecordWcagReviewAsync(IJSRuntime js, string profileId, string? pageIdentity, WcagManualReview review);
    Task LoadAsync(IJSRuntime js);
    EndpointDiscoverySnapshot GetSnapshot(string profileId);
    /// <summary>Folds the current runtime's observed endpoints into the persisted per-page snapshot and persists. Returns true when anything changed.</summary>
    Task<bool> MergeObservedAsync(IJSRuntime js, string profileId, IReadOnlyList<ObservedNetworkEndpoint> observed);
    /// <summary>Folds Browser Companion page evidence into the same per-page snapshot (one PageAnalysis per page identity for proxy and browser evidence alike) and persists. Returns true when anything changed.</summary>
    Task<bool> MergeBrowserEvidenceAsync(IJSRuntime js, string profileId, IReadOnlyList<BrowserPageEvidence> pages);
    Task DeletePageAsync(IJSRuntime js, string profileId, string pageIdentity);
    /// <summary>
    /// Refresh analysis for exactly one existing page: keep the page entry and identity, start a new analysis generation, reset its current
    /// endpoint evidence, and mark it waiting for fresh traffic. Old observations do not reappear; only traffic observed after the refresh
    /// boundary repopulates the page. Never touches other pages, shared traffic, the proxy, the authenticated context or configuration.
    /// </summary>
    Task RefreshPageAsync(IJSRuntime js, string profileId, string pageIdentity);
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
    public void ConfigureTarget(FrontendAnalysisProfile profile)
    {
        var snapshot = GetSnapshot(profile.Id);
        snapshot.ApplicationOrigins = BrowserCompanionScope.ApprovedOrigins(profile).ToList();
        EndpointDiscoveryMerge.Reclassify(snapshot);
        BrowserQualityAssessmentService.Assess(snapshot);
    }
    public Task SaveWcagSettingsAsync(IJSRuntime js, string profileId, WcagSettings settings) => MutateWcagAsync(js, profileId, s =>
    {
        BrowserQualityAssessmentService.SelectProfile(s, settings);
        return true;
    });

    public Task RecordWcagReviewAsync(IJSRuntime js, string profileId, string? pageIdentity, WcagManualReview review) => MutateWcagAsync(js, profileId, s =>
    {
        BrowserQualityAssessmentService.RecordReview(s, pageIdentity, review);
        return true;
    });
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private Dictionary<string, EndpointDiscoverySnapshot> _byProfile = new(StringComparer.Ordinal);

    // A recorded approval must not appear saved when storage failed. Keep existing discovery's best-effort behavior separate.
    private async Task MutateWcagAsync(IJSRuntime js, string profileId, Func<EndpointDiscoverySnapshot, bool> mutate)
    {
        var snapshot = GetSnapshot(profileId);
        var oldSettings = snapshot.Wcag;
        var oldAt = snapshot.UpdatedAt;
        var oldApplication = snapshot.WcagApplicationReviews.ToList();
        var oldReviews = snapshot.Pages.ToDictionary(p => p, p => p.WcagReviews.ToList());
        mutate(snapshot);
        BrowserQualityAssessmentService.Assess(snapshot);
        snapshot.UpdatedAt = DateTimeOffset.UtcNow;
        try { await js.InvokeVoidAsync("birkNextStorage.setDiscovery", JsonSerializer.Serialize(_byProfile, JsonOptions)); }
        catch
        {
            snapshot.Wcag = oldSettings; snapshot.UpdatedAt = oldAt;
            snapshot.WcagApplicationReviews = oldApplication;
            foreach (var (page, reviews) in oldReviews) page.WcagReviews = reviews;
            BrowserQualityAssessmentService.Assess(snapshot);
            throw new InvalidOperationException("WCAG review could not be persisted. Check browser storage and retry.");
        }
    }

    private Task? _loadTask;
    public Task LoadAsync(IJSRuntime js) => _loadTask ??= LoadCoreAsync(js);
    private async Task LoadCoreAsync(IJSRuntime js)
    {
        try
        {
            var json = await js.InvokeAsync<string?>("birkNextStorage.getDiscovery");
            if (!string.IsNullOrWhiteSpace(json))
            {
                _byProfile = JsonSerializer.Deserialize<Dictionary<string, EndpointDiscoverySnapshot>>(json, JsonOptions) ?? new(StringComparer.Ordinal);
                using var document = JsonDocument.Parse(json);
                foreach (var (id, snapshot) in _byProfile)
                {
                    var raw = document.RootElement.GetProperty(id);
                    if (raw.TryGetProperty("wcag", out var old) && !old.TryGetProperty("profileId", out _))
                    {
                        var version = old.TryGetProperty("version", out var v) ? v.GetString() : null;
                        var level = old.TryGetProperty("level", out var l) ? l.GetString() : null;
                        snapshot.Wcag = new WcagSettings { ProfileId = version is "Wcag21" or "Wcag22" && level is "A" or "AA"
                            ? $"legacy-{(version == "Wcag21" ? "21" : "22")}-{level}" : "legacy-unknown" };
                    }
                    EndpointDiscoveryMerge.Reclassify(snapshot);
                    BrowserQualityAssessmentService.Assess(snapshot);
                }
            }
        }
        catch { /* corrupt or unavailable storage: start empty rather than fail the page */ }
    }

    public EndpointDiscoverySnapshot GetSnapshot(string profileId)
    {
        if (!_byProfile.TryGetValue(profileId, out var snapshot)) _byProfile[profileId] = snapshot = new();
        return snapshot;
    }

    public async Task<bool> MergeObservedAsync(IJSRuntime js, string profileId, IReadOnlyList<ObservedNetworkEndpoint> observed)
    {
        if (observed.Count == 0) return false;
        var snapshot = _byProfile.TryGetValue(profileId, out var existing) ? existing : new EndpointDiscoverySnapshot();
        var changed = EndpointDiscoveryMerge.Merge(snapshot, observed, DateTimeOffset.UtcNow);
        if (!changed) return false;
        BrowserQualityAssessmentService.Assess(snapshot);
        _byProfile[profileId] = snapshot;
        await PersistAsync(js);
        return true;
    }

    public async Task<bool> MergeBrowserEvidenceAsync(IJSRuntime js, string profileId, IReadOnlyList<BrowserPageEvidence> pages)
    {
        if (pages.Count == 0) return false;
        var snapshot = _byProfile.TryGetValue(profileId, out var existing) ? existing : new EndpointDiscoverySnapshot();
        var changed = EndpointDiscoveryMerge.MergeBrowserEvidence(snapshot, pages, DateTimeOffset.UtcNow);
        if (!changed) return false;
        BrowserQualityAssessmentService.Assess(snapshot);
        _byProfile[profileId] = snapshot;
        await PersistAsync(js);
        return true;
    }

    public Task DeletePageAsync(IJSRuntime js, string profileId, string pageIdentity) => MutateAsync(js, profileId, s =>
        s.Pages.RemoveAll(p => p.Identity == pageIdentity) > 0);

    public Task RefreshPageAsync(IJSRuntime js, string profileId, string pageIdentity) => MutateAsync(js, profileId, s =>
    {
        var page = s.Pages.FirstOrDefault(p => p.Identity == pageIdentity);
        if (page is null) return false;   // the page entry is never deleted/recreated by a refresh
        page.AnalysisGeneration++;
        page.RefreshedAtUtc = DateTimeOffset.UtcNow;
        page.Endpoints.Clear();
        page.BrowserEvidence = null;      // browser evidence starts a fresh generation too; it repopulates only from a visit after the boundary
        page.LastObservedAt = default;    // no traffic observed yet in the new generation; never show the old "last observed" as current
        return true;
    });

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
        BrowserQualityAssessmentService.Assess(snapshot);
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
    public static void Reclassify(EndpointDiscoverySnapshot snapshot)
    {
        foreach (var page in snapshot.Pages.Where(p => !ApplicationPagePolicy.IsApplicationOrigin(p.PageOrigin, snapshot.ApplicationOrigins)).ToList())
        {
            foreach (var endpoint in page.Endpoints) Upsert(snapshot.Shared, endpoint with { PageOrigin = null, PagePath = null });
            snapshot.ExcludedPageHistory.Add(page);
            snapshot.Pages.Remove(page);
        }
        snapshot.SchemaVersion = 2;
    }

    public static bool Merge(EndpointDiscoverySnapshot snapshot, IReadOnlyList<ObservedNetworkEndpoint> observed, DateTimeOffset now)
    {
        var changed = false;
        foreach (var endpoint in observed)
        {
            if (endpoint.PageOrigin is null || endpoint.PagePath is null || !ApplicationPagePolicy.IsApplicationOrigin(endpoint.PageOrigin, snapshot.ApplicationOrigins))
            {
                changed |= Upsert(snapshot.Shared, endpoint with { PageOrigin = null, PagePath = null });
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
            // Refresh boundary: a page whose analysis was refreshed only accepts traffic observed at or after the refresh, so old cumulative
            // runtime observations (still held by the live proxy session) never repopulate a just-refreshed page.
            if (page.RefreshedAtUtc is { } boundary && endpoint.LastObservedAt < boundary)
                continue;
            var scoped = page.RefreshedAtUtc is { } refreshed ? RestrictToGeneration(endpoint, refreshed) : endpoint;
            changed |= Upsert(page.Endpoints, scoped);
            if (page.FirstObservedAt == default || scoped.FirstObservedAt < page.FirstObservedAt) page.FirstObservedAt = scoped.FirstObservedAt;
            if (scoped.LastObservedAt > page.LastObservedAt) page.LastObservedAt = scoped.LastObservedAt;
            PerformanceHistoryRecorder.Record(page);
        }
        if (changed) snapshot.UpdatedAt = now;
        return changed;
    }

    /// <summary>
    /// The live proxy registry is cumulative across the session; after "Refresh analysis" only the request samples observed at or after the
    /// boundary belong to the new generation. When samples exist, counts, statuses and latency aggregates are recomputed from them, so old
    /// calls never inflate the refreshed page. Without samples (legacy traffic) the cumulative counts are kept as before.
    /// </summary>
    public static ObservedNetworkEndpoint RestrictToGeneration(ObservedNetworkEndpoint endpoint, DateTimeOffset boundary)
    {
        if (endpoint.Samples.Count == 0) return endpoint with { FirstObservedAt = endpoint.FirstObservedAt < boundary ? boundary : endpoint.FirstObservedAt };
        var samples = endpoint.Samples.Where(s => s.At >= boundary).ToList();
        if (samples.Count == 0) return endpoint with { Samples = [], Count = 0, FirstObservedAt = boundary, TotalDurationMs = 0, MinDurationMs = null, MaxDurationMs = null, LastDurationMs = null, ErrorCount = 0, AuthRejectedCount = 0, NotModifiedCount = 0 };
        return endpoint with
        {
            Samples = samples,
            Count = samples.Count < endpoint.Samples.Count ? samples.Count : endpoint.Count,
            FirstObservedAt = samples.Min(s => s.At) < boundary ? boundary : samples.Min(s => s.At),
            TotalDurationMs = samples.Sum(s => s.DurationMs), MinDurationMs = samples.Min(s => s.DurationMs), MaxDurationMs = samples.Max(s => s.DurationMs), LastDurationMs = samples[0].DurationMs,
            ErrorCount = samples.Count(s => s.Status >= 400), AuthRejectedCount = samples.Count(s => s.Status is 401 or 403), NotModifiedCount = samples.Count(s => s.Status == 304),
        };
    }

    /// <summary>
    /// Browser Companion evidence converges on the same page identity as proxy traffic (origin + normalized path), so a page seen by both
    /// is one PageAnalysis, never two. Respects the refresh boundary through the evidence's VisitStartedAt: a visit that started before
    /// "Refresh analysis" never repopulates the refreshed page. Other pages are untouched.
    /// </summary>
    public static bool MergeBrowserEvidence(EndpointDiscoverySnapshot snapshot, IReadOnlyList<BrowserPageEvidence> pages, DateTimeOffset now)
    {
        var changed = false;
        foreach (var evidence in pages)
        {
            if (!ApplicationPagePolicy.IsApplicationOrigin(evidence.PageOrigin, snapshot.ApplicationOrigins) || string.IsNullOrWhiteSpace(evidence.PageOrigin) || string.IsNullOrWhiteSpace(evidence.PagePath) || evidence.VisitStartedAt == default) continue;
            var identity = evidence.Identity;
            var page = snapshot.Pages.FirstOrDefault(p => p.Identity == identity);
            if (page is null)
            {
                page = new PageAnalysis { PageOrigin = evidence.PageOrigin, PagePath = evidence.PagePath, FirstObservedAt = evidence.VisitStartedAt, LastObservedAt = evidence.CapturedAt };
                snapshot.Pages.Add(page);
                changed = true;
            }
            if (page.RefreshedAtUtc is { } boundary && evidence.VisitStartedAt < boundary)
                continue;
            var current = page.BrowserEvidence;
            if (current is not null && (current.VisitStartedAt > evidence.VisitStartedAt ||
                (current.VisitStartedAt == evidence.VisitStartedAt && current.SnapshotSequence >= evidence.SnapshotSequence && current.CapturedAt >= evidence.CapturedAt)))
                continue;
            page.BrowserEvidence = evidence;
            if (string.IsNullOrWhiteSpace(page.DisplayName) && !string.IsNullOrWhiteSpace(evidence.DocumentTitle)) page.DisplayName = evidence.DocumentTitle;
            if (page.FirstObservedAt == default || evidence.VisitStartedAt < page.FirstObservedAt) page.FirstObservedAt = evidence.VisitStartedAt;
            if (evidence.CapturedAt > page.LastObservedAt) page.LastObservedAt = evidence.CapturedAt;
            PerformanceHistoryRecorder.Record(page);
            changed = true;
        }
        if (changed) snapshot.UpdatedAt = now;
        return changed;
    }

    /// <summary>
    /// Collapse identity within a page/shared list: category + scheme + host + port + path + method, plus the operation for GraphQL so
    /// distinct operations to one endpoint (GetChildren, GetRoles) are separate rows with their own counts and latency samples.
    /// </summary>
    public static string Key(ObservedNetworkEndpoint e) => e.Category == ObservedTrafficCategory.GraphQl
        ? $"{e.Category}|{e.Scheme}|{e.Host}|{e.Port}|{e.Path}|{e.Method}|{e.OperationType}|{e.OperationName}"
        : $"{e.Category}|{e.Scheme}|{e.Host}|{e.Port}|{e.Path}|{e.Method}";

    private static bool Upsert(List<ObservedNetworkEndpoint> list, ObservedNetworkEndpoint incoming)
    {
        var key = Key(incoming);
        var index = list.FindIndex(e => Key(e) == key);
        if (index < 0) { list.Add(incoming); return true; }
        var existing = list[index];
        // The live registry is cumulative, so an incoming record with at least as many observations carries the freshest performance
        // metadata (samples, statuses, cache directives); a smaller count (proxy restarted) keeps the persisted aggregates.
        var takeIncoming = incoming.Count >= existing.Count;
        list[index] = existing with
        {
            Count = Math.Max(existing.Count, incoming.Count),
            LastStatus = incoming.LastStatus,
            AuthObserved = existing.AuthObserved || incoming.AuthObserved,
            Confidence = (ObservedEndpointConfidence)Math.Max((int)existing.Confidence, (int)incoming.Confidence),
            FirstObservedAt = incoming.FirstObservedAt < existing.FirstObservedAt ? incoming.FirstObservedAt : existing.FirstObservedAt,
            LastObservedAt = incoming.LastObservedAt > existing.LastObservedAt ? incoming.LastObservedAt : existing.LastObservedAt,
            OperationType = incoming.OperationType != GraphQlOperationType.None ? incoming.OperationType : existing.OperationType,
            OperationName = incoming.OperationName ?? existing.OperationName,
            Samples = takeIncoming && incoming.Samples.Count > 0 ? incoming.Samples : existing.Samples,
            LastDurationMs = incoming.LastDurationMs ?? existing.LastDurationMs,
            MinDurationMs = takeIncoming && incoming.MinDurationMs is not null ? incoming.MinDurationMs : existing.MinDurationMs,
            MaxDurationMs = takeIncoming && incoming.MaxDurationMs is not null ? incoming.MaxDurationMs : existing.MaxDurationMs,
            TotalDurationMs = takeIncoming && incoming.TotalDurationMs > 0 ? incoming.TotalDurationMs : existing.TotalDurationMs,
            ErrorCount = takeIncoming ? incoming.ErrorCount : existing.ErrorCount,
            AuthRejectedCount = takeIncoming ? incoming.AuthRejectedCount : existing.AuthRejectedCount,
            NotModifiedCount = takeIncoming ? incoming.NotModifiedCount : existing.NotModifiedCount,
            CacheDirectives = incoming.CacheDirectives ?? existing.CacheDirectives,
            HasEtag = existing.HasEtag || incoming.HasEtag,
            HasLastModified = existing.HasLastModified || incoming.HasLastModified,
            LastResponseBytes = incoming.LastResponseBytes ?? existing.LastResponseBytes,
        };
        return true;
    }
}

/// <summary>
/// Records the compact per-generation performance history of a page (BirkNext Performance Quality). Called whenever browser or proxy
/// evidence of the page changes; the current generation's entry is replaced, older generations are kept (bounded) for comparison.
/// Threshold-independent raw numbers only.
/// </summary>
public static class PerformanceHistoryRecorder
{
    public static void Record(PageAnalysis page)
    {
        var perf = page.BrowserEvidence?.Performance;
        var api = page.Endpoints.Where(e => e.Category is ObservedTrafficCategory.Rest or ObservedTrafficCategory.GraphQl).ToList();
        if (perf is null && api.Count == 0) return;
        var entry = new PagePerformanceHistoryEntry
        {
            Generation = page.AnalysisGeneration,
            ObservationType = perf?.ObservationType ?? "unknown",
            CapturedAt = page.BrowserEvidence?.CapturedAt ?? page.LastObservedAt,
            LcpMs = perf?.LcpMs, Cls = perf?.Cls, StabilizationMs = perf?.StabilizationMs,
            TransferredBytes = perf?.TransferredBytes, LongTaskCount = perf?.LongTaskCount, ResourceCount = perf?.ResourceCount,
            ApiCalls = api.Count == 0 ? null : api.Sum(e => e.Count),
            GraphQlCalls = api.Count == 0 ? null : api.Where(e => e.Category == ObservedTrafficCategory.GraphQl).Sum(e => e.Count),
            WasmBytes = page.BrowserEvidence?.Blazor?.WasmBytes ?? perf?.WasmBytes, FrameworkBytes = page.BrowserEvidence?.Blazor?.FrameworkBytes,
        };
        page.PerformanceHistory.RemoveAll(h => h.Generation == entry.Generation);
        page.PerformanceHistory.Add(entry);
        page.PerformanceHistory = page.PerformanceHistory.OrderByDescending(h => h.Generation).Take(PagePerformanceHistoryEntry.MaxEntries).OrderBy(h => h.Generation).ToList();
    }
}
