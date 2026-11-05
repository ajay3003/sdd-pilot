using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BirkNext.DeliveryReporting;

namespace BirkNext.Web.Services;

/// <summary>Functional Quality Scope calls (api/delivery-reporting/projects/{id}/scope). Every failure comes back as a readable message.</summary>
public interface IFunctionalQualityScopeApiService
{
    Task<(IReadOnlyList<AvailableScope> Scopes, string? Error)> AvailableAsync(Guid projectId, CancellationToken ct = default);
    Task<(ScopeResolution? Resolution, string? Error)> ResolveAsync(Guid projectId, ScopeQuery query, CancellationToken ct = default);
    /// <summary>Top-level items matching <paramref name="search"/> (bounded server-side) for the custom-scope builder.</summary>
    Task<(ScopeResolution? Resolution, string? Error)> AllItemsAsync(Guid projectId, bool includeTechnical, string? search = null, CancellationToken ct = default);
    /// <summary><paramref name="environment"/>: whose test and schema evidence (an environment id, "*" = all aggregated, null = the only one).</summary>
    Task<ScopeItemDetail?> DetailAsync(Guid projectId, string key, string? environment = null, CancellationToken ct = default);
    Task<(ScopeDefinition? Scope, string? Error)> SaveAsync(Guid projectId, ScopeDefinition definition, CancellationToken ct = default);
    Task<(ScopeDefinition? Scope, string? Error)> CloneAsync(Guid projectId, Guid scopeId, CancellationToken ct = default);
    Task<string?> DeleteAsync(Guid projectId, Guid scopeId, CancellationToken ct = default);
    Task<(ScopeDefinition? Scope, string? Error)> ReferenceAsync(Guid projectId, Guid scopeId, string itemKey, ScopeInclusion mode, CancellationToken ct = default);
    Task<(ScopeDefinition? Scope, string? Error)> RemoveReferenceAsync(Guid projectId, Guid scopeId, string itemKey, CancellationToken ct = default);
    Task<(TestDesignAssessmentView? Assessment, string? Error)> AssessAsync(Guid projectId, TestDesignAssessmentView assessment, CancellationToken ct = default);
    Task<(ScopeSnapshotView? Snapshot, string? Error)> FreezeAsync(Guid projectId, ScopeQuery query, CancellationToken ct = default);
    Task<IReadOnlyList<ScopeSnapshotView>> SnapshotsAsync(Guid projectId, CancellationToken ct = default);
}

public sealed class FunctionalQualityScopeApiService(HttpClient http) : IFunctionalQualityScopeApiService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static string Base(Guid projectId) => $"api/delivery-reporting/projects/{projectId}/scope";

    public async Task<(IReadOnlyList<AvailableScope> Scopes, string? Error)> AvailableAsync(Guid projectId, CancellationToken ct = default)
    {
        var (value, error) = await Send<List<AvailableScope>>(() => http.GetAsync($"{Base(projectId)}/available", ct), ct);
        return (value ?? [], error);
    }

    public Task<(ScopeResolution? Resolution, string? Error)> ResolveAsync(Guid projectId, ScopeQuery query, CancellationToken ct = default) =>
        Send<ScopeResolution>(() => http.PostAsJsonAsync($"{Base(projectId)}/resolve", query, Json, ct), ct);

    public Task<(ScopeResolution? Resolution, string? Error)> AllItemsAsync(Guid projectId, bool includeTechnical, string? search = null, CancellationToken ct = default) =>
        Send<ScopeResolution>(() => http.GetAsync($"{Base(projectId)}/items?includeTechnical={(includeTechnical ? "true" : "false")}&take=120{(string.IsNullOrWhiteSpace(search) ? "" : $"&search={Uri.EscapeDataString(search.Trim())}")}", ct), ct);

    public async Task<ScopeItemDetail?> DetailAsync(Guid projectId, string key, string? environment = null, CancellationToken ct = default) =>
        (await Send<ScopeItemDetail>(() => http.GetAsync($"{Base(projectId)}/items/detail?key={Uri.EscapeDataString(key)}{(string.IsNullOrEmpty(environment) ? "" : $"&environment={Uri.EscapeDataString(environment)}")}", ct), ct)).Value;

    public Task<(ScopeDefinition? Scope, string? Error)> SaveAsync(Guid projectId, ScopeDefinition definition, CancellationToken ct = default) =>
        Send<ScopeDefinition>(() => definition.Id is { } id
            ? http.PutAsJsonAsync($"{Base(projectId)}/scopes/{id}", definition, Json, ct)
            : http.PostAsJsonAsync($"{Base(projectId)}/scopes", definition, Json, ct), ct);

    public Task<(ScopeDefinition? Scope, string? Error)> CloneAsync(Guid projectId, Guid scopeId, CancellationToken ct = default) =>
        Send<ScopeDefinition>(() => http.PostAsync($"{Base(projectId)}/scopes/{scopeId}/clone", null, ct), ct);

    public async Task<string?> DeleteAsync(Guid projectId, Guid scopeId, CancellationToken ct = default)
    {
        try
        {
            using var response = await http.DeleteAsync($"{Base(projectId)}/scopes/{scopeId}", ct);
            return response.IsSuccessStatusCode ? null : await MessageAsync(response, ct);
        }
        catch (Exception e) when (BackendRequestClassifier.IsRequestFailure(e)) { return BackendRequestClassifier.FromException("Functional Quality Scope", e, "DELETE scope", ct).UserMessage; }
    }

    public Task<(ScopeDefinition? Scope, string? Error)> ReferenceAsync(Guid projectId, Guid scopeId, string itemKey, ScopeInclusion mode, CancellationToken ct = default) =>
        Send<ScopeDefinition>(() => http.PostAsJsonAsync($"{Base(projectId)}/scopes/{scopeId}/references", new ScopeReferenceRequest(itemKey, mode), Json, ct), ct);

    public Task<(ScopeDefinition? Scope, string? Error)> RemoveReferenceAsync(Guid projectId, Guid scopeId, string itemKey, CancellationToken ct = default) =>
        Send<ScopeDefinition>(() => http.DeleteAsync($"{Base(projectId)}/scopes/{scopeId}/references?key={Uri.EscapeDataString(itemKey)}", ct), ct);

    public Task<(TestDesignAssessmentView? Assessment, string? Error)> AssessAsync(Guid projectId, TestDesignAssessmentView assessment, CancellationToken ct = default) =>
        Send<TestDesignAssessmentView>(() => http.PutAsJsonAsync($"{Base(projectId)}/assessments", assessment, Json, ct), ct);

    public Task<(ScopeSnapshotView? Snapshot, string? Error)> FreezeAsync(Guid projectId, ScopeQuery query, CancellationToken ct = default) =>
        Send<ScopeSnapshotView>(() => http.PostAsJsonAsync($"{Base(projectId)}/snapshots", query, Json, ct), ct);

    public async Task<IReadOnlyList<ScopeSnapshotView>> SnapshotsAsync(Guid projectId, CancellationToken ct = default) =>
        (await Send<List<ScopeSnapshotView>>(() => http.GetAsync($"{Base(projectId)}/snapshots", ct), ct)).Value ?? [];

    private static async Task<(T? Value, string? Error)> Send<T>(Func<Task<HttpResponseMessage>> request, CancellationToken ct) where T : class
    {
        try
        {
            using var response = await request();
            return response.IsSuccessStatusCode ? (await response.Content.ReadFromJsonAsync<T>(Json, ct), null) : (null, await MessageAsync(response, ct));
        }
        catch (Exception e) when (BackendRequestClassifier.IsRequestFailure(e))
        { return (null, BackendRequestClassifier.FromException("Functional Quality Scope", e, "scope request", ct).UserMessage); }
    }

    private static async Task<string> MessageAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct);
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("message", out var m) && m.GetString() is { Length: > 0 } message) return message;
        }
        catch (JsonException) { }
        return response.StatusCode == HttpStatusCode.NotFound ? "Not found. It may have been deleted." : $"The request failed (HTTP {(int)response.StatusCode}).";
    }
}
