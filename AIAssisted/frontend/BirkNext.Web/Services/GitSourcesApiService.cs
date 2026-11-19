using System.Net.Http.Json;
using System.Text.Json;
using BirkNext.GitSources;

namespace BirkNext.Web.Services;

/// <summary>
/// Git Sources, Git-only (api/git-sources): the person's own Git and its credential helper connect and authenticate. Status never contacts a remote;
/// add, fetch, check and import are explicit actions. Responses never carry a credential.
/// </summary>
public interface IGitSourcesApiService
{
    Task<(GitConnectionStatus? Status, string? Error)> StatusAsync(CancellationToken ct = default);
    /// <summary>Git repositories below one folder the person chose (bounded, read-only).</summary>
    Task<(GitScanResult? Result, string? Error)> ScanAsync(GitScanRequest request, CancellationToken ct = default);
    /// <summary>One local folder: is it a Git repository, its branch, head and whether it has uncommitted changes (read-only).</summary>
    Task<(GitLocalRepository? Repository, string? Error)> InspectAsync(string folder, CancellationToken ct = default);
    /// <summary>Adds a local repository or a Git URL for the BirkNext project and fetches its branches into BirkNext-owned storage.</summary>
    Task<(GitSourceLink? Link, string? Error)> AddAsync(GitAddRepositoryRequest request, CancellationToken ct = default);
    /// <summary>The linked repositories of one BirkNext project, or of every project when <paramref name="environmentId"/> is null (System Settings).</summary>
    Task<(IReadOnlyList<GitSourceLink> Items, string? Error)> LinksAsync(string? environmentId, CancellationToken ct = default);
    Task<(GitSourceLink? Link, string? Error)> UpdateLinkAsync(string linkId, string environmentId, string? branch, string? pinnedCommit, IReadOnlyList<string>? roles, CancellationToken ct = default);
    Task<string?> UnlinkAsync(string linkId, string environmentId, CancellationToken ct = default);
    /// <summary>Fetch latest into BirkNext-owned storage (never pull, merge or push).</summary>
    Task<(GitSourceLink? Link, string? Error)> FetchAsync(string linkId, string environmentId, CancellationToken ct = default);
    Task<(IReadOnlyList<GitBranch> Items, string? Error)> BranchesAsync(string linkId, string environmentId, CancellationToken ct = default);
    Task<(GitSourceLink? Link, string? Error)> CheckAsync(string linkId, string environmentId, CancellationToken ct = default);
    Task<(IReadOnlyList<GitSourceMatch> Items, string? Error)> MatchesAsync(string linkId, string environmentId, CancellationToken ct = default);
    Task<(GitSourceLink? Link, string? Error)> DecideAsync(string linkId, string environmentId, GitAssociationRequest request, CancellationToken ct = default);
    Task<(GitImportResult? Result, string? Error)> ImportAsync(GitImportRequest request, CancellationToken ct = default);
}

public sealed class GitSourcesApiService(HttpClient http) : IGitSourcesApiService
{
    private const string Title = "Git Sources";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static string Link(string linkId, string environmentId, string? action = null) =>
        $"api/git-sources/links/{Uri.EscapeDataString(linkId)}{(action is null ? "" : "/" + action)}?environmentId={Uri.EscapeDataString(environmentId)}";

    public Task<(GitConnectionStatus?, string?)> StatusAsync(CancellationToken ct = default) => SendAsync<GitConnectionStatus>(HttpMethod.Get, "api/git-sources/status", null, ct);

    public Task<(GitScanResult?, string?)> ScanAsync(GitScanRequest request, CancellationToken ct = default) =>
        SendAsync<GitScanResult>(HttpMethod.Post, "api/git-sources/scan", request, ct);

    public Task<(GitLocalRepository?, string?)> InspectAsync(string folder, CancellationToken ct = default) =>
        SendAsync<GitLocalRepository>(HttpMethod.Post, "api/git-sources/inspect", new GitScanRequest(folder), ct);

    public Task<(GitSourceLink?, string?)> AddAsync(GitAddRepositoryRequest request, CancellationToken ct = default) =>
        SendAsync<GitSourceLink>(HttpMethod.Post, "api/git-sources/repositories", request, ct);

    public async Task<(IReadOnlyList<GitSourceLink>, string?)> LinksAsync(string? environmentId, CancellationToken ct = default)
    {
        var url = environmentId is null ? "api/git-sources/links" : $"api/git-sources/links?environmentId={Uri.EscapeDataString(environmentId)}";
        var (items, error) = await SendAsync<List<GitSourceLink>>(HttpMethod.Get, url, null, ct);
        return (items ?? [], error);
    }

    public Task<(GitSourceLink?, string?)> UpdateLinkAsync(string linkId, string environmentId, string? branch, string? pinnedCommit, IReadOnlyList<string>? roles, CancellationToken ct = default) =>
        SendAsync<GitSourceLink>(HttpMethod.Put, Link(linkId, environmentId), new { branch, pinnedCommit, roles }, ct);

    public async Task<string?> UnlinkAsync(string linkId, string environmentId, CancellationToken ct = default)
    {
        try
        {
            using var response = await http.DeleteAsync(Link(linkId, environmentId), ct);
            return response.IsSuccessStatusCode ? null : $"{Title} request failed ({(int)response.StatusCode}).";
        }
        catch (Exception e) when (BackendRequestClassifier.IsRequestFailure(e)) { return BackendRequestClassifier.FromException(Title, e, "DELETE link", ct).UserMessage; }
    }

    public Task<(GitSourceLink?, string?)> FetchAsync(string linkId, string environmentId, CancellationToken ct = default) =>
        SendAsync<GitSourceLink>(HttpMethod.Post, Link(linkId, environmentId, "fetch"), new { }, ct);

    public async Task<(IReadOnlyList<GitBranch>, string?)> BranchesAsync(string linkId, string environmentId, CancellationToken ct = default)
    {
        var (items, error) = await SendAsync<List<GitBranch>>(HttpMethod.Get, Link(linkId, environmentId, "branches"), null, ct);
        return (items ?? [], error);
    }

    public Task<(GitSourceLink?, string?)> CheckAsync(string linkId, string environmentId, CancellationToken ct = default) =>
        SendAsync<GitSourceLink>(HttpMethod.Post, Link(linkId, environmentId, "check"), new { }, ct);

    public async Task<(IReadOnlyList<GitSourceMatch>, string?)> MatchesAsync(string linkId, string environmentId, CancellationToken ct = default)
    {
        var (items, error) = await SendAsync<List<GitSourceMatch>>(HttpMethod.Get, Link(linkId, environmentId, "matches"), null, ct);
        return (items ?? [], error);
    }

    public Task<(GitSourceLink?, string?)> DecideAsync(string linkId, string environmentId, GitAssociationRequest request, CancellationToken ct = default) =>
        SendAsync<GitSourceLink>(HttpMethod.Post, Link(linkId, environmentId, "association"), request, ct);

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
