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
    Task SaveWcagSettingsAsync(IJSRuntime js, string profileId, WcagSettings settings) => Task.CompletedTask;
    Task RecordWcagReviewAsync(IJSRuntime js, string profileId, string? pageIdentity, WcagManualReview review) => Task.CompletedTask;
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
    public Task SaveWcagSettingsAsync(IJSRuntime js, string profileId, WcagSettings settings) => MutateAsync(js, profileId, s =>
    {
        if (!Enum.IsDefined(settings.Version) || !Enum.IsDefined(settings.Level)) throw new ArgumentException("Invalid WCAG target.");
        s.Wcag = new WcagSettings { Version = settings.Version, Level = settings.Level };
        return true;
    });

    public Task RecordWcagReviewAsync(IJSRuntime js, string profileId, string? pageIdentity, WcagManualReview review) => MutateAsync(js, profileId, s =>
    {
        var definition = WcagRegistry.For(s.Wcag).SingleOrDefault(d => d.CriterionId == review.CriterionId)
            ?? throw new ArgumentException("Unknown criterion for this target.");
        if (review.Result is not (WcagStatus.Pass or WcagStatus.Fail or WcagStatus.NotApplicable))
            throw new ArgumentException("A manual decision must be Pass, Fail or NotApplicable.");
        var page = pageIdentity is null ? null : s.Pages.SingleOrDefault(p => p.Identity == pageIdentity)
            ?? throw new ArgumentException("Page does not exist.");
        if (definition.RequiresCrossPageEvidence != (page is null)) throw new ArgumentException("Incorrect review scope.");
        // Do not silently attach an editor opened on a prior generation to a newer snapshot.
        if (review.Generation != (page?.AnalysisGeneration ?? 0) || review.Version != s.Wcag.Version ||
            review.ScopeGeneration != (page is null ? WcagAssessmentEngine.ScopeGeneration(s) : ""))
            throw new InvalidOperationException("Analysis changed. Reopen the review against the current generation.");
        var safe = review with
        {
            Comment = WcagReviewText.Validate(review.Comment, 1000),
            EvidenceNote = WcagReviewText.Validate(review.EvidenceNote, 1000),
            ReviewedBy = WcagReviewText.Validate(review.ReviewedBy, 100),
            ReviewedAt = DateTimeOffset.UtcNow,
        };
        if (string.IsNullOrWhiteSpace(safe.ReviewedBy) || string.IsNullOrWhiteSpace(safe.EvidenceNote))
            throw new ArgumentException("Reviewer and evidence note are required.");
        var reviews = page?.WcagReviews ?? s.WcagApplicationReviews;
        if (reviews.Count >= 1000) throw new InvalidOperationException("Manual review history limit reached.");
        reviews.Add(safe);
        return true;
    });
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

    public async Task<bool> MergeBrowserEvidenceAsync(IJSRuntime js, string profileId, IReadOnlyList<BrowserPageEvidence> pages)
    {
        if (pages.Count == 0) return false;
        var snapshot = _byProfile.TryGetValue(profileId, out var existing) ? existing : new EndpointDiscoverySnapshot();
        var changed = EndpointDiscoveryMerge.MergeBrowserEvidence(snapshot, pages, DateTimeOffset.UtcNow);
        if (!changed) return false;
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
            // Refresh boundary: a page whose analysis was refreshed only accepts traffic observed at or after the refresh, so old cumulative
            // runtime observations (still held by the live proxy session) never repopulate a just-refreshed page.
            if (page.RefreshedAtUtc is { } boundary && endpoint.LastObservedAt < boundary)
                continue;
            changed |= Upsert(page.Endpoints, endpoint);
            if (page.FirstObservedAt == default || endpoint.FirstObservedAt < page.FirstObservedAt) page.FirstObservedAt = endpoint.FirstObservedAt;
            if (endpoint.LastObservedAt > page.LastObservedAt) page.LastObservedAt = endpoint.LastObservedAt;
        }
        if (changed) snapshot.UpdatedAt = now;
        return changed;
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
            if (string.IsNullOrWhiteSpace(evidence.PageOrigin) || string.IsNullOrWhiteSpace(evidence.PagePath) || evidence.VisitStartedAt == default) continue;
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
            changed = true;
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
