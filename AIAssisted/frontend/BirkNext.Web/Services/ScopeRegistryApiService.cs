using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BirkNext.Scopes;

namespace BirkNext.Web.Services;

/// <summary>Shared Scope Registry calls (api/scopes). Every call names the project; a write names the version it was based on.</summary>
public interface IScopeRegistryApiService
{
    Task<(IReadOnlyList<ScopeSummary> Items, string? Error)> ListAsync(string projectKey, CancellationToken ct = default);
    Task<(ScopeDetail? Detail, string? Error)> GetAsync(string projectKey, Guid scopeId, CancellationToken ct = default);
    Task<(ScopeDetail? Detail, string? Error, bool Conflict)> CreateAsync(CreateScopeRequest request, CancellationToken ct = default);
    Task<(ScopeDetail? Detail, string? Error, bool Conflict)> SaveAsync(string projectKey, Guid scopeId, SaveScopeDraftRequest request, CancellationToken ct = default);
    Task<(ScopeDetail? Detail, string? Error, bool Conflict)> ApproveAsync(string projectKey, Guid scopeId, ScopeDecisionRequest request, CancellationToken ct = default);
    Task<(ScopeComparison? Comparison, string? Error)> CompareAsync(string projectKey, Guid scopeId, int from, int to, CancellationToken ct = default);
    Task<(ScopeSelection? Selection, string? Error)> SelectionAsync(string projectKey, IReadOnlyList<Guid> scopeIds, CancellationToken ct = default);
}

public sealed class ScopeRegistryApiService(HttpClient http) : IScopeRegistryApiService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<(IReadOnlyList<ScopeSummary> Items, string? Error)> ListAsync(string projectKey, CancellationToken ct = default)
    {
        try { return (await http.GetFromJsonAsync<List<ScopeSummary>>($"api/scopes?projectKey={Uri.EscapeDataString(projectKey)}", Json, ct) ?? [], null); }
        catch (Exception ex) when (BackendRequestClassifier.IsRequestFailure(ex)) { return ([], BackendRequestClassifier.FromException("Scopes", ex).UserMessage); }
    }

    public async Task<(ScopeDetail? Detail, string? Error)> GetAsync(string projectKey, Guid scopeId, CancellationToken ct = default)
    {
        try { return (await http.GetFromJsonAsync<ScopeDetail>($"api/scopes/{scopeId}?projectKey={Uri.EscapeDataString(projectKey)}", Json, ct), null); }
        catch (Exception ex) when (BackendRequestClassifier.IsRequestFailure(ex)) { return (null, BackendRequestClassifier.FromException("Scope", ex).UserMessage); }
    }

    public Task<(ScopeDetail? Detail, string? Error, bool Conflict)> CreateAsync(CreateScopeRequest request, CancellationToken ct = default) =>
        Write(() => http.PostAsJsonAsync("api/scopes", request, Json, ct), ct);

    public Task<(ScopeDetail? Detail, string? Error, bool Conflict)> SaveAsync(string projectKey, Guid scopeId, SaveScopeDraftRequest request, CancellationToken ct = default) =>
        Write(() => http.PutAsJsonAsync($"api/scopes/{scopeId}?projectKey={Uri.EscapeDataString(projectKey)}", request, Json, ct), ct);

    public Task<(ScopeDetail? Detail, string? Error, bool Conflict)> ApproveAsync(string projectKey, Guid scopeId, ScopeDecisionRequest request, CancellationToken ct = default) =>
        Write(() => http.PostAsJsonAsync($"api/scopes/{scopeId}/approve?projectKey={Uri.EscapeDataString(projectKey)}", request, Json, ct), ct);

    public async Task<(ScopeComparison? Comparison, string? Error)> CompareAsync(string projectKey, Guid scopeId, int from, int to, CancellationToken ct = default)
    {
        try { return (await http.GetFromJsonAsync<ScopeComparison>($"api/scopes/{scopeId}/compare?projectKey={Uri.EscapeDataString(projectKey)}&from={from}&to={to}", Json, ct), null); }
        catch (Exception ex) when (BackendRequestClassifier.IsRequestFailure(ex)) { return (null, BackendRequestClassifier.FromException("Scope comparison", ex).UserMessage); }
    }

    public async Task<(ScopeSelection? Selection, string? Error)> SelectionAsync(string projectKey, IReadOnlyList<Guid> scopeIds, CancellationToken ct = default)
    {
        var query = string.Concat(scopeIds.Select(id => $"&scope={id}"));
        try { return (await http.GetFromJsonAsync<ScopeSelection>($"api/scopes/selection?projectKey={Uri.EscapeDataString(projectKey)}{query}", Json, ct), null); }
        catch (Exception ex) when (BackendRequestClassifier.IsRequestFailure(ex)) { return (null, BackendRequestClassifier.FromException("Scope selection", ex).UserMessage); }
    }

    private static async Task<(ScopeDetail?, string?, bool)> Write(Func<Task<HttpResponseMessage>> send, CancellationToken ct)
    {
        try
        {
            using var response = await send();
            if (response.IsSuccessStatusCode) return (await response.Content.ReadFromJsonAsync<ScopeDetail>(Json, ct), null, false);
            var message = await ReadMessage(response, ct);
            return response.StatusCode switch
            {
                HttpStatusCode.Conflict => (null, message ?? "The scope was changed since you opened it. Reload it and apply your change again.", true),
                HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized => (null, "Approving a scope is a configuration decision; this session is not allowed to make it.", false),
                _ => (null, message ?? $"The scope could not be saved (HTTP {(int)response.StatusCode}).", false),
            };
        }
        catch (Exception ex) when (BackendRequestClassifier.IsRequestFailure(ex)) { return (null, BackendRequestClassifier.FromException("Scope", ex).UserMessage, false); }
    }

    private static async Task<string?> ReadMessage(HttpResponseMessage response, CancellationToken ct)
    {
        try { return (await response.Content.ReadFromJsonAsync<JsonElement>(Json, ct)).TryGetProperty("message", out var m) ? m.GetString() : null; }
        catch (Exception ex) when (ex is JsonException or NotSupportedException) { return null; }
    }
}
