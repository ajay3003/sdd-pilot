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
    Task<(ScopeDetail? Detail, string? Error, bool Conflict)> SubmitAsync(string projectKey, Guid scopeId, ScopeDecisionRequest request, CancellationToken ct = default) =>
        Task.FromResult<(ScopeDetail?, string?, bool)>((null, "Submitting for review is not available.", false));
    Task<(ScopeDetail? Detail, string? Error, bool Conflict)> ArchiveAsync(string projectKey, Guid scopeId, ScopeDecisionRequest request, CancellationToken ct = default) =>
        Task.FromResult<(ScopeDetail?, string?, bool)>((null, "Archiving is not available.", false));
    /// <summary>Each member of a revision checked against its catalog now (stale members are shown, never replaced).</summary>
    Task<(IReadOnlyList<ScopeMemberStatus> Items, string? Error)> CheckAsync(string projectKey, Guid scopeId, int? revision, string? catalogScope, CancellationToken ct = default) =>
        Task.FromResult<(IReadOnlyList<ScopeMemberStatus>, string?)>(([], "Member checks are not available."));
    Task<(IReadOnlyList<ScopeMemberStatus> Items, string? Error)> CheckMembersAsync(ScopeMemberCheckRequest request, CancellationToken ct = default) =>
        Task.FromResult<(IReadOnlyList<ScopeMemberStatus>, string?)>(([], "Member checks are not available."));
    /// <summary>A searchable page of one member kind from its owning catalog.</summary>
    Task<(ScopeCatalogPage? Page, string? Error)> CatalogAsync(ScopeMemberKind kind, string? catalogScope, string? search, int skip = 0, int take = 25, CancellationToken ct = default) =>
        Task.FromResult<(ScopeCatalogPage?, string?)>((null, "Member catalogs are not available."));
    /// <summary>Systems, services and connections for one Target Environment (canonical interpretation + Integration Topology, offline).</summary>
    Task<(SystemsOverviewView? Overview, string? Error)> SystemsAsync(string catalogScope, CancellationToken ct = default) =>
        Task.FromResult<(SystemsOverviewView?, string?)>((null, "The systems overview is not available."));
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

    public Task<(ScopeDetail? Detail, string? Error, bool Conflict)> SubmitAsync(string projectKey, Guid scopeId, ScopeDecisionRequest request, CancellationToken ct = default) =>
        Write(() => http.PostAsJsonAsync($"api/scopes/{scopeId}/submit?projectKey={Uri.EscapeDataString(projectKey)}", request, Json, ct), ct);

    public Task<(ScopeDetail? Detail, string? Error, bool Conflict)> ArchiveAsync(string projectKey, Guid scopeId, ScopeDecisionRequest request, CancellationToken ct = default) =>
        Write(() => http.PostAsJsonAsync($"api/scopes/{scopeId}/archive?projectKey={Uri.EscapeDataString(projectKey)}", request, Json, ct), ct);

    public async Task<(IReadOnlyList<ScopeMemberStatus> Items, string? Error)> CheckAsync(string projectKey, Guid scopeId, int? revision, string? catalogScope, CancellationToken ct = default)
    {
        var query = $"projectKey={Uri.EscapeDataString(projectKey)}{(revision is { } r ? $"&revision={r}" : "")}{(string.IsNullOrWhiteSpace(catalogScope) ? "" : $"&catalogScope={Uri.EscapeDataString(catalogScope)}")}";
        try { return (await http.GetFromJsonAsync<List<ScopeMemberStatus>>($"api/scopes/{scopeId}/check?{query}", Json, ct) ?? [], null); }
        catch (Exception ex) when (BackendRequestClassifier.IsRequestFailure(ex)) { return ([], BackendRequestClassifier.FromException("Scope member check", ex).UserMessage); }
    }

    public async Task<(IReadOnlyList<ScopeMemberStatus> Items, string? Error)> CheckMembersAsync(ScopeMemberCheckRequest request, CancellationToken ct = default)
    {
        try
        {
            using var response = await http.PostAsJsonAsync("api/scopes/check-members", request, Json, ct);
            if (!response.IsSuccessStatusCode) return ([], await ReadMessage(response, ct) ?? $"The members could not be checked (HTTP {(int)response.StatusCode}).");
            return (await response.Content.ReadFromJsonAsync<List<ScopeMemberStatus>>(Json, ct) ?? [], null);
        }
        catch (Exception ex) when (BackendRequestClassifier.IsRequestFailure(ex)) { return ([], BackendRequestClassifier.FromException("Scope member check", ex).UserMessage); }
    }

    public async Task<(ScopeCatalogPage? Page, string? Error)> CatalogAsync(ScopeMemberKind kind, string? catalogScope, string? search, int skip = 0, int take = 25, CancellationToken ct = default)
    {
        var query = $"kind={kind}&skip={skip}&take={take}{(string.IsNullOrWhiteSpace(catalogScope) ? "" : $"&catalogScope={Uri.EscapeDataString(catalogScope)}")}{(string.IsNullOrWhiteSpace(search) ? "" : $"&search={Uri.EscapeDataString(search.Trim())}")}";
        try { return (await http.GetFromJsonAsync<ScopeCatalogPage>($"api/scopes/catalog?{query}", Json, ct), null); }
        catch (Exception ex) when (BackendRequestClassifier.IsRequestFailure(ex)) { return (null, BackendRequestClassifier.FromException("Scope catalog", ex).UserMessage); }
    }

    public async Task<(SystemsOverviewView? Overview, string? Error)> SystemsAsync(string catalogScope, CancellationToken ct = default)
    {
        try { return (await http.GetFromJsonAsync<SystemsOverviewView>($"api/scopes/systems?catalogScope={Uri.EscapeDataString(catalogScope)}", Json, ct), null); }
        catch (Exception ex) when (BackendRequestClassifier.IsRequestFailure(ex)) { return (null, BackendRequestClassifier.FromException("Systems", ex).UserMessage); }
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
                HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized => (null, "Approving or archiving a scope is a configuration decision; this session is not allowed to make it.", false),
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
