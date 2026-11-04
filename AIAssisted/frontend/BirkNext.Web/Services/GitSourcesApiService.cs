using System.Net.Http.Json;
using System.Text.Json;
using BirkNext.GitSources;

namespace BirkNext.Web.Services;

/// <summary>System Settings → Git Sources and the Git mode of Import Project (api/git-sources). Responses never carry a token.</summary>
public interface IGitSourcesApiService
{
    Task<(GitConnectionStatus? Status, string? Error)> StatusAsync(CancellationToken ct = default);
    Task<(GitConnectionStatus? Status, string? Error)> SaveSettingsAsync(GitSourceSettings settings, CancellationToken ct = default);
    Task<(GitConnectionStatus? Status, string? Error)> SignInAsync(CancellationToken ct = default);
    Task<(GitConnectionStatus? Status, string? Error)> DisconnectAsync(CancellationToken ct = default);
    Task<(GitTestResult? Result, string? Error)> TestAsync(CancellationToken ct = default);
    Task<(IReadOnlyList<GitRepository> Items, string? Error)> RepositoriesAsync(CancellationToken ct = default);
    Task<(IReadOnlyList<GitBranch> Items, string? Error)> BranchesAsync(string repositoryId, CancellationToken ct = default);
    Task<(GitImportResult? Result, string? Error)> ImportAsync(GitImportRequest request, CancellationToken ct = default);
}

public sealed class GitSourcesApiService(HttpClient http) : IGitSourcesApiService
{
    private const string Title = "Git Sources";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task<(GitConnectionStatus?, string?)> StatusAsync(CancellationToken ct = default) => SendAsync<GitConnectionStatus>(HttpMethod.Get, "api/git-sources/status", null, ct);
    public Task<(GitConnectionStatus?, string?)> SaveSettingsAsync(GitSourceSettings settings, CancellationToken ct = default) =>
        SendAsync<GitConnectionStatus>(HttpMethod.Put, "api/git-sources/settings", settings, ct);
    public Task<(GitConnectionStatus?, string?)> SignInAsync(CancellationToken ct = default) => SendAsync<GitConnectionStatus>(HttpMethod.Post, "api/git-sources/sign-in", new { }, ct);
    public Task<(GitConnectionStatus?, string?)> DisconnectAsync(CancellationToken ct = default) => SendAsync<GitConnectionStatus>(HttpMethod.Post, "api/git-sources/disconnect", new { }, ct);
    public Task<(GitTestResult?, string?)> TestAsync(CancellationToken ct = default) => SendAsync<GitTestResult>(HttpMethod.Post, "api/git-sources/test", new { }, ct);

    public async Task<(IReadOnlyList<GitRepository>, string?)> RepositoriesAsync(CancellationToken ct = default)
    {
        var (items, error) = await SendAsync<List<GitRepository>>(HttpMethod.Get, "api/git-sources/repositories", null, ct);
        return (items ?? [], error);
    }

    public async Task<(IReadOnlyList<GitBranch>, string?)> BranchesAsync(string repositoryId, CancellationToken ct = default)
    {
        var (items, error) = await SendAsync<List<GitBranch>>(HttpMethod.Get, $"api/git-sources/repositories/{Uri.EscapeDataString(repositoryId)}/branches", null, ct);
        return (items ?? [], error);
    }

    public Task<(GitImportResult?, string?)> ImportAsync(GitImportRequest request, CancellationToken ct = default) =>
        SendAsync<GitImportResult>(HttpMethod.Post, "api/git-sources/import", request, ct);

    private async Task<(T?, string?)> SendAsync<T>(HttpMethod method, string url, object? body, CancellationToken ct) where T : class
    {
        try
        {
            using var request = new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body, options: Json) };
            using var response = await http.SendAsync(request, ct);
            if (response.IsSuccessStatusCode) return (await response.Content.ReadFromJsonAsync<T>(Json, ct), null);
            try
            {
                var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Json, ct);
                if (problem.ValueKind == JsonValueKind.Object && problem.TryGetProperty("message", out var m) && m.GetString() is { Length: > 0 } message) return (null, message);
            }
            catch (JsonException) { }
            return (null, $"{Title} request failed ({(int)response.StatusCode}).");
        }
        catch (Exception e) when (BackendRequestClassifier.IsRequestFailure(e))
        { return (null, BackendRequestClassifier.FromException(Title, e, $"{method} {url}", ct).UserMessage); }
    }
}
