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

    Task<(GitConnectionStatus? Status, string? Error)> DeviceCodeAsync(CancellationToken ct = default) => Task.FromResult<(GitConnectionStatus?, string?)>((null, "Not available."));
    /// <summary>Discovered + entered organizations; Problem says why discovery was not possible (the entered ones are still listed).</summary>
    Task<(IReadOnlyList<GitOrganization> Items, string? Problem, string? Error)> OrganizationsAsync(CancellationToken ct = default) =>
        Task.FromResult<(IReadOnlyList<GitOrganization>, string?, string?)>(([], null, "Not available."));
    Task<(GitConnectionStatus? Status, string? Error)> SelectOrganizationsAsync(IReadOnlyList<string> organizationUrls, CancellationToken ct = default) =>
        Task.FromResult<(GitConnectionStatus?, string?)>((null, "Not available."));
    Task<(GitProjectPage? Page, string? Error)> ProjectsAsync(string organizationUrl, string? continuation, CancellationToken ct = default) =>
        Task.FromResult<(GitProjectPage?, string?)>((null, "Not available."));
    Task<(IReadOnlyList<GitRepository> Items, string? Error)> ProjectRepositoriesAsync(string organizationUrl, string projectId, CancellationToken ct = default) =>
        Task.FromResult<(IReadOnlyList<GitRepository>, string?)>(([], "Not available."));
    Task<(IReadOnlyList<GitBranch> Items, string? Error)> BranchesInAsync(string organizationUrl, string repositoryId, CancellationToken ct = default) =>
        Task.FromResult<(IReadOnlyList<GitBranch>, string?)>(([], "Not available."));
    Task<(GitCommitInfo? Commit, string? Error)> CommitAsync(string organizationUrl, string repositoryId, string commit, CancellationToken ct = default) =>
        Task.FromResult<(GitCommitInfo?, string?)>((null, "Not available."));
    Task<(IReadOnlyList<GitSourceLink> Items, string? Error)> LinksAsync(string environmentId, CancellationToken ct = default) =>
        Task.FromResult<(IReadOnlyList<GitSourceLink>, string?)>(([], null));
    Task<(GitSourceLink? Link, string? Error)> LinkAsync(GitLinkRequest request, CancellationToken ct = default) => Task.FromResult<(GitSourceLink?, string?)>((null, "Not available."));
    Task<string?> UnlinkAsync(string linkId, string environmentId, CancellationToken ct = default) => Task.FromResult<string?>("Not available.");
    Task<(GitSourceLink? Link, string? Error)> CheckAsync(string linkId, string environmentId, CancellationToken ct = default) => Task.FromResult<(GitSourceLink?, string?)>((null, "Not available."));
    Task<(IReadOnlyList<GitSourceMatch> Items, string? Error)> MatchesAsync(string linkId, string environmentId, CancellationToken ct = default) =>
        Task.FromResult<(IReadOnlyList<GitSourceMatch>, string?)>(([], null));
    Task<(GitSourceLink? Link, string? Error)> DecideAsync(string linkId, string environmentId, GitAssociationRequest request, CancellationToken ct = default) =>
        Task.FromResult<(GitSourceLink?, string?)>((null, "Not available."));
}

/// <summary>The organizations listing as the API returns it.</summary>
public sealed record GitOrganizationsView(List<GitOrganization> Organizations, string? Problem);

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

    public Task<(GitConnectionStatus?, string?)> DeviceCodeAsync(CancellationToken ct = default) => SendAsync<GitConnectionStatus>(HttpMethod.Post, "api/git-sources/sign-in/device-code", new { }, ct);

    public async Task<(IReadOnlyList<GitOrganization>, string?, string?)> OrganizationsAsync(CancellationToken ct = default)
    {
        var (view, error) = await SendAsync<GitOrganizationsView>(HttpMethod.Get, "api/git-sources/organizations", null, ct);
        return (view?.Organizations ?? [], view?.Problem, error);
    }

    public Task<(GitConnectionStatus?, string?)> SelectOrganizationsAsync(IReadOnlyList<string> organizationUrls, CancellationToken ct = default) =>
        SendAsync<GitConnectionStatus>(HttpMethod.Put, "api/git-sources/organizations", organizationUrls, ct);

    public Task<(GitProjectPage?, string?)> ProjectsAsync(string organizationUrl, string? continuation, CancellationToken ct = default) =>
        SendAsync<GitProjectPage>(HttpMethod.Get, $"api/git-sources/projects?organization={Uri.EscapeDataString(organizationUrl)}{(continuation is null ? "" : $"&continuation={Uri.EscapeDataString(continuation)}")}", null, ct);

    public async Task<(IReadOnlyList<GitRepository>, string?)> ProjectRepositoriesAsync(string organizationUrl, string projectId, CancellationToken ct = default)
    {
        var (items, error) = await SendAsync<List<GitRepository>>(HttpMethod.Get,
            $"api/git-sources/projects/{Uri.EscapeDataString(projectId)}/repositories?organization={Uri.EscapeDataString(organizationUrl)}", null, ct);
        return (items ?? [], error);
    }

    public async Task<(IReadOnlyList<GitBranch>, string?)> BranchesInAsync(string organizationUrl, string repositoryId, CancellationToken ct = default)
    {
        var (items, error) = await SendAsync<List<GitBranch>>(HttpMethod.Get,
            $"api/git-sources/repositories/{Uri.EscapeDataString(repositoryId)}/branches?organization={Uri.EscapeDataString(organizationUrl)}", null, ct);
        return (items ?? [], error);
    }

    public Task<(GitCommitInfo?, string?)> CommitAsync(string organizationUrl, string repositoryId, string commit, CancellationToken ct = default) =>
        SendAsync<GitCommitInfo>(HttpMethod.Get,
            $"api/git-sources/repositories/{Uri.EscapeDataString(repositoryId)}/commits/{Uri.EscapeDataString(commit.Trim())}?organization={Uri.EscapeDataString(organizationUrl)}", null, ct);

    public async Task<(IReadOnlyList<GitSourceLink>, string?)> LinksAsync(string environmentId, CancellationToken ct = default)
    {
        var (items, error) = await SendAsync<List<GitSourceLink>>(HttpMethod.Get, $"api/git-sources/links?environmentId={Uri.EscapeDataString(environmentId)}", null, ct);
        return (items ?? [], error);
    }

    public Task<(GitSourceLink?, string?)> LinkAsync(GitLinkRequest request, CancellationToken ct = default) =>
        SendAsync<GitSourceLink>(HttpMethod.Put, "api/git-sources/links", request, ct);

    public async Task<string?> UnlinkAsync(string linkId, string environmentId, CancellationToken ct = default)
    {
        try
        {
            using var response = await http.DeleteAsync($"api/git-sources/links/{Uri.EscapeDataString(linkId)}?environmentId={Uri.EscapeDataString(environmentId)}", ct);
            return response.IsSuccessStatusCode ? null : $"{Title} request failed ({(int)response.StatusCode}).";
        }
        catch (Exception e) when (BackendRequestClassifier.IsRequestFailure(e)) { return BackendRequestClassifier.FromException(Title, e, "DELETE link", ct).UserMessage; }
    }

    public Task<(GitSourceLink?, string?)> CheckAsync(string linkId, string environmentId, CancellationToken ct = default) =>
        SendAsync<GitSourceLink>(HttpMethod.Post, $"api/git-sources/links/{Uri.EscapeDataString(linkId)}/check?environmentId={Uri.EscapeDataString(environmentId)}", new { }, ct);

    public async Task<(IReadOnlyList<GitSourceMatch>, string?)> MatchesAsync(string linkId, string environmentId, CancellationToken ct = default)
    {
        var (items, error) = await SendAsync<List<GitSourceMatch>>(HttpMethod.Get,
            $"api/git-sources/links/{Uri.EscapeDataString(linkId)}/matches?environmentId={Uri.EscapeDataString(environmentId)}", null, ct);
        return (items ?? [], error);
    }

    public Task<(GitSourceLink?, string?)> DecideAsync(string linkId, string environmentId, GitAssociationRequest request, CancellationToken ct = default) =>
        SendAsync<GitSourceLink>(HttpMethod.Post, $"api/git-sources/links/{Uri.EscapeDataString(linkId)}/association?environmentId={Uri.EscapeDataString(environmentId)}", request, ct);

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
