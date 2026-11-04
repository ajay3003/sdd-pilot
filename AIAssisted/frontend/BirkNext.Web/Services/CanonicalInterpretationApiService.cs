using System.Net.Http.Json;
using System.Text.Json;
using BirkNext.Interpretation;

namespace BirkNext.Web.Services;

/// <summary>
/// Canonical interpretation calls (api/canonical/{scope}). The scope is the active Target Environment profile id: every read and decision is
/// project-scoped, and the browser only ever loads one page of entities or relations at a time.
/// </summary>
public interface ICanonicalInterpretationApiService
{
    Task<(InterpretationSnapshotView? Snapshot, string? Error)> InterpretAsync(string scope, InterpretRequest request, CancellationToken ct = default);
    Task<(IReadOnlyList<InterpretationSnapshotView> Items, string? Error)> SnapshotsAsync(string scope, CancellationToken ct = default);
    Task<(InterpretationComparison? Comparison, string? Error)> CompareAsync(string scope, Guid from, Guid to, CancellationToken ct = default);
    Task<(CanonicalPage<CanonicalEntityView>? Page, string? Error)> EntitiesAsync(string scope, CanonicalEntityType? type, string? search, int skip = 0, int take = 25, CancellationToken ct = default);
    Task<(CanonicalEntityDetail? Detail, string? Error)> EntityAsync(string scope, Guid id, CancellationToken ct = default);
    Task<(CorrelationChainView? Chain, string? Error)> ChainAsync(string scope, Guid id, CancellationToken ct = default);
    Task<(CanonicalPage<CanonicalRelationView>? Page, string? Error)> ReviewQueueAsync(string scope, string? rule, int skip = 0, int take = 25, CancellationToken ct = default);
    Task<(AliasResolution? Resolution, string? Error)> ResolveAliasAsync(string scope, string name, CancellationToken ct = default);
    Task<(IReadOnlyList<CanonicalEntityDetail> Items, string? Error)> OperationsAsync(string scope, string method, string path, CancellationToken ct = default);
    Task<(IReadOnlyList<ReviewDecisionView> Items, string? Error)> DecisionsAsync(string scope, string? target = null, CancellationToken ct = default);
    Task<(ReviewDecisionView? Decision, string? Error)> DecideAsync(string scope, ReviewDecisionRequest request, CancellationToken ct = default);
    /// <summary>The shared review queue (decisions or suggestions, optionally one type), ranked and paged.</summary>
    Task<(ReviewQueueView? Queue, string? Error)> ReviewItemsAsync(string scope, ReviewItemKind kind, ReviewItemType? type, int skip = 0, int take = 20, CancellationToken ct = default) =>
        Task.FromResult<(ReviewQueueView?, string?)>((null, "The shared review queue is not available."));
    Task<(TraceSummaryView? Summary, string? Error)> TraceSummaryAsync(string scope, CancellationToken ct = default) =>
        Task.FromResult<(TraceSummaryView?, string?)>((null, null));
    Task<(IReadOnlyList<TraceFeatureRow> Items, string? Error)> TraceFeaturesAsync(string scope, string? module, CancellationToken ct = default) =>
        Task.FromResult<(IReadOnlyList<TraceFeatureRow>, string?)>(([], null));
    Task<(TraceFeatureDetail? Detail, string? Error)> TraceFeatureAsync(string scope, Guid featureId, CancellationToken ct = default) =>
        Task.FromResult<(TraceFeatureDetail?, string?)>((null, null));
    /// <summary>Summary-first counts of the latest interpretation (the same numbers every consumer shows).</summary>
    Task<(CanonicalSummaryView? Summary, string? Error)> SummaryAsync(string scope, CancellationToken ct = default) =>
        Task.FromResult<(CanonicalSummaryView?, string?)>((null, null));
}

public sealed class CanonicalInterpretationApiService(HttpClient http) : ICanonicalInterpretationApiService
{
    private const string Title = "Correlation review";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static string Base(string scope) => $"api/canonical/{Uri.EscapeDataString(scope)}";

    public Task<(InterpretationSnapshotView?, string?)> InterpretAsync(string scope, InterpretRequest request, CancellationToken ct = default) =>
        PostAsync<InterpretationSnapshotView>($"{Base(scope)}/interpret", request, ct);

    public async Task<(IReadOnlyList<InterpretationSnapshotView>, string?)> SnapshotsAsync(string scope, CancellationToken ct = default)
    {
        var (items, error) = await GetAsync<List<InterpretationSnapshotView>>($"{Base(scope)}/snapshots?take=20", ct);
        return (items ?? [], error);
    }

    public Task<(InterpretationComparison?, string?)> CompareAsync(string scope, Guid from, Guid to, CancellationToken ct = default) =>
        GetAsync<InterpretationComparison>($"{Base(scope)}/snapshots/compare?from={from}&to={to}", ct);

    public Task<(CanonicalPage<CanonicalEntityView>?, string?)> EntitiesAsync(string scope, CanonicalEntityType? type, string? search, int skip = 0, int take = 25, CancellationToken ct = default) =>
        GetAsync<CanonicalPage<CanonicalEntityView>>($"{Base(scope)}/entities?skip={skip}&take={take}{(type is { } t ? $"&type={t}" : "")}{(string.IsNullOrWhiteSpace(search) ? "" : $"&search={Uri.EscapeDataString(search)}")}", ct);

    public Task<(CanonicalEntityDetail?, string?)> EntityAsync(string scope, Guid id, CancellationToken ct = default) => GetAsync<CanonicalEntityDetail>($"{Base(scope)}/entities/{id}", ct);

    public Task<(CorrelationChainView?, string?)> ChainAsync(string scope, Guid id, CancellationToken ct = default) => GetAsync<CorrelationChainView>($"{Base(scope)}/entities/{id}/chain", ct);

    public Task<(CanonicalSummaryView?, string?)> SummaryAsync(string scope, CancellationToken ct = default) => GetAsync<CanonicalSummaryView>($"{Base(scope)}/summary", ct);

    public Task<(ReviewQueueView?, string?)> ReviewItemsAsync(string scope, ReviewItemKind kind, ReviewItemType? type, int skip = 0, int take = 20, CancellationToken ct = default) =>
        GetAsync<ReviewQueueView>($"{Base(scope)}/review?kind={kind}&skip={skip}&take={take}{(type is { } t ? $"&type={t}" : "")}", ct);

    public Task<(TraceSummaryView?, string?)> TraceSummaryAsync(string scope, CancellationToken ct = default) => GetAsync<TraceSummaryView>($"{Base(scope)}/trace/summary", ct);

    public async Task<(IReadOnlyList<TraceFeatureRow>, string?)> TraceFeaturesAsync(string scope, string? module, CancellationToken ct = default)
    {
        var (items, error) = await GetAsync<List<TraceFeatureRow>>($"{Base(scope)}/trace/features{(string.IsNullOrWhiteSpace(module) ? "" : $"?module={Uri.EscapeDataString(module)}")}", ct);
        return (items ?? [], error);
    }

    public Task<(TraceFeatureDetail?, string?)> TraceFeatureAsync(string scope, Guid featureId, CancellationToken ct = default) =>
        GetAsync<TraceFeatureDetail>($"{Base(scope)}/trace/features/{featureId}", ct);

    public Task<(CanonicalPage<CanonicalRelationView>?, string?)> ReviewQueueAsync(string scope, string? rule, int skip = 0, int take = 25, CancellationToken ct = default) =>
        GetAsync<CanonicalPage<CanonicalRelationView>>($"{Base(scope)}/relations?needsReview=true&skip={skip}&take={take}{(string.IsNullOrWhiteSpace(rule) ? "" : $"&rule={Uri.EscapeDataString(rule)}")}", ct);

    public Task<(AliasResolution?, string?)> ResolveAliasAsync(string scope, string name, CancellationToken ct = default) =>
        GetAsync<AliasResolution>($"{Base(scope)}/aliases/resolve?name={Uri.EscapeDataString(name)}", ct);

    public async Task<(IReadOnlyList<CanonicalEntityDetail>, string?)> OperationsAsync(string scope, string method, string path, CancellationToken ct = default)
    {
        var (items, error) = await GetAsync<List<CanonicalEntityDetail>>($"{Base(scope)}/operations?method={Uri.EscapeDataString(method)}&path={Uri.EscapeDataString(path)}", ct);
        return (items ?? [], error);
    }

    public async Task<(IReadOnlyList<ReviewDecisionView>, string?)> DecisionsAsync(string scope, string? target = null, CancellationToken ct = default)
    {
        var (items, error) = await GetAsync<List<ReviewDecisionView>>($"{Base(scope)}/decisions?take=100{(target is null ? "" : $"&target={Uri.EscapeDataString(target)}")}", ct);
        return (items ?? [], error);
    }

    public Task<(ReviewDecisionView?, string?)> DecideAsync(string scope, ReviewDecisionRequest request, CancellationToken ct = default) =>
        PostAsync<ReviewDecisionView>($"{Base(scope)}/decisions", request, ct);

    private async Task<(T?, string?)> GetAsync<T>(string url, CancellationToken ct) where T : class
    {
        try
        {
            using var response = await http.GetAsync(url, ct);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return (null, null);
            return response.IsSuccessStatusCode ? (await response.Content.ReadFromJsonAsync<T>(Json, ct), null) : (null, await MessageAsync(response, ct));
        }
        catch (Exception e) when (BackendRequestClassifier.IsRequestFailure(e))
        { return (null, BackendRequestClassifier.FromException(Title, e, $"GET {url}", ct).UserMessage); }
    }

    private async Task<(T?, string?)> PostAsync<T>(string url, object body, CancellationToken ct) where T : class
    {
        try
        {
            using var response = await http.PostAsJsonAsync(url, body, Json, ct);
            return response.IsSuccessStatusCode ? (await response.Content.ReadFromJsonAsync<T>(Json, ct), null) : (null, await MessageAsync(response, ct));
        }
        catch (Exception e) when (BackendRequestClassifier.IsRequestFailure(e))
        { return (null, BackendRequestClassifier.FromException(Title, e, $"POST {url}", ct).UserMessage); }
    }

    private static async Task<string> MessageAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json, ct);
            if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("error", out var error) && error.GetString() is { Length: > 0 } text) return text;
        }
        catch (JsonException) { }
        return $"{Title} request failed ({(int)response.StatusCode}).";
    }
}
