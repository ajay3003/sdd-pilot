using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using BirkNext.GitSources;

namespace BirkNext.Api.Services.GitSources;

/// <summary>A provider failure with its diagnostic kind (never one generic "connection failed").</summary>
public sealed class GitProviderException(GitFailureKind kind, string? detail = null) : Exception(GitSourcePolicy.Describe(kind) + (detail is null ? "" : $" ({detail})"))
{
    public GitFailureKind Kind { get; } = kind;
}

/// <summary>Read-only Azure DevOps Git REST calls as the signed-in person. Nothing here writes to the remote.</summary>
public interface IAzureDevOpsGitClient
{
    Task<IReadOnlyList<GitRepository>> RepositoriesAsync(string organizationUrl, string token, CancellationToken ct);
    /// <summary>Branches with their head commit (date, author, message), latest first.</summary>
    Task<IReadOnlyList<GitBranch>> BranchesAsync(string organizationUrl, string repositoryId, string token, CancellationToken ct);
    /// <summary>The archive of exactly one commit (never a branch name: the SHA is resolved first). Bounded by <paramref name="maxBytes"/>.</summary>
    Task<byte[]> CommitArchiveAsync(string organizationUrl, string repositoryId, string commitSha, string token, long maxBytes, CancellationToken ct);

    /// <summary>The organizations the signed-in person is a member of (Azure DevOps profile + accounts). Throws when discovery is not possible.</summary>
    Task<IReadOnlyList<GitOrganization>> OrganizationsAsync(string token, CancellationToken ct) => throw new NotSupportedException();
    /// <summary>One page of the organization's projects the person can see; <c>Next</c> is the continuation token (null = last page).</summary>
    Task<GitProjectPage> ProjectsAsync(string organizationUrl, string? continuation, string token, CancellationToken ct) => throw new NotSupportedException();
    /// <summary>Repositories of one Azure DevOps project.</summary>
    Task<IReadOnlyList<GitRepository>> ProjectRepositoriesAsync(string organizationUrl, string projectId, string token, CancellationToken ct) => throw new NotSupportedException();
    /// <summary>One commit's metadata (validates a chosen revision); null when the commit does not exist in the repository.</summary>
    Task<GitCommitInfo?> CommitAsync(string organizationUrl, string repositoryId, string commitSha, string token, CancellationToken ct) => throw new NotSupportedException();
}

public sealed class AzureDevOpsGitClient(HttpClient http) : IAzureDevOpsGitClient
{
    private const string ApiVersion = "7.1";

    public async Task<IReadOnlyList<GitRepository>> RepositoriesAsync(string organizationUrl, string token, CancellationToken ct)
    {
        using var doc = await GetJsonAsync($"{organizationUrl}/_apis/git/repositories?api-version={ApiVersion}", token, GitFailureKind.OrganizationNotFound, ct);
        return Repositories(doc);
    }

    /// <summary>The Azure DevOps "global" host: profile and organization membership (not an organization).</summary>
    public const string ProfileHost = "https://app.vssps.visualstudio.com";

    public async Task<IReadOnlyList<GitOrganization>> OrganizationsAsync(string token, CancellationToken ct)
    {
        string memberId;
        using (var profile = await GetJsonAsync($"{ProfileHost}/_apis/profile/profiles/me?api-version={ApiVersion}", token, GitFailureKind.AccessDenied, ct))
            memberId = Str(profile.RootElement, "id") ?? throw new GitProviderException(GitFailureKind.Unexpected, "no profile id");
        using var accounts = await GetJsonAsync($"{ProfileHost}/_apis/accounts?memberId={Uri.EscapeDataString(memberId)}&api-version={ApiVersion}", token, GitFailureKind.AccessDenied, ct);
        return [.. Values(accounts).Select(a => (Name: Str(a, "accountName"), Id: Str(a, "accountId")))
            .Where(a => a.Name is { Length: > 0 } && GitSourcePolicy.NormalizeOrganizationUrl($"https://dev.azure.com/{a.Name}") is not null)
            .Select(a => new GitOrganization(a.Id ?? a.Name!, a.Name!, $"https://dev.azure.com/{a.Name}", Discovered: true))
            .OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase)];
    }

    public async Task<GitProjectPage> ProjectsAsync(string organizationUrl, string? continuation, string token, CancellationToken ct)
    {
        var url = $"{organizationUrl}/_apis/projects?$top=100&api-version={ApiVersion}{(string.IsNullOrEmpty(continuation) ? "" : $"&continuationToken={Uri.EscapeDataString(continuation)}")}";
        using var response = await SendAsync(url, token, "application/json", ct);
        await ThrowIfFailedAsync(response, GitFailureKind.OrganizationNotFound);
        if (response.StatusCode == HttpStatusCode.NonAuthoritativeInformation || response.Content.Headers.ContentType?.MediaType?.Contains("json") != true)
            throw new GitProviderException(GitFailureKind.SignInRequired);
        var next = response.Headers.TryGetValues("x-ms-continuationtoken", out var values) ? values.FirstOrDefault() : null;
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return new([.. Values(doc).Select(p => new GitProject(Str(p, "id") ?? "", Str(p, "name") ?? "", Str(p, "state"))).Where(p => p.Id.Length > 0)
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)], string.IsNullOrWhiteSpace(next) ? null : next);
    }

    public async Task<IReadOnlyList<GitRepository>> ProjectRepositoriesAsync(string organizationUrl, string projectId, string token, CancellationToken ct)
    {
        using var doc = await GetJsonAsync($"{organizationUrl}/{Uri.EscapeDataString(projectId)}/_apis/git/repositories?api-version={ApiVersion}", token, GitFailureKind.RepositoryNotFound, ct);
        return Repositories(doc);
    }

    public async Task<GitCommitInfo?> CommitAsync(string organizationUrl, string repositoryId, string commitSha, string token, CancellationToken ct)
    {
        if (!GitSourcePolicy.IsCommitSha(commitSha)) return null;
        try
        {
            using var doc = await GetJsonAsync($"{organizationUrl}/_apis/git/repositories/{Uri.EscapeDataString(repositoryId)}/commits/{commitSha}?api-version={ApiVersion}",
                token, GitFailureKind.RepositoryNotFound, ct);
            var c = doc.RootElement;
            var committer = c.TryGetProperty("committer", out var cm) ? cm : default;
            DateTimeOffset? date = committer.ValueKind == JsonValueKind.Object && Str(committer, "date") is { } s && DateTimeOffset.TryParse(s, out var d) ? d : null;
            return new(Str(c, "commitId") ?? commitSha, date, committer.ValueKind == JsonValueKind.Object ? Str(committer, "name") : null, Str(c, "comment"));
        }
        catch (GitProviderException e) when (e.Kind == GitFailureKind.RepositoryNotFound) { return null; }
    }

    private static IReadOnlyList<GitRepository> Repositories(JsonDocument doc) =>
        [.. Values(doc).Select(r => new GitRepository(
            Str(r, "id") ?? "", Str(r, "name") ?? "", r.TryGetProperty("project", out var p) ? Str(p, "name") ?? "" : "",
            Str(r, "defaultBranch") is { } d ? GitSourcePolicy.BranchName(d) : null, Str(r, "webUrl"),
            r.TryGetProperty("isDisabled", out var dis) && dis.ValueKind == JsonValueKind.True)
            { ProjectId = r.TryGetProperty("project", out var pid) ? Str(pid, "id") : null })
            .Where(r => r.Id.Length > 0).OrderBy(r => r.Project, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)];

    public async Task<IReadOnlyList<GitBranch>> BranchesAsync(string organizationUrl, string repositoryId, string token, CancellationToken ct)
    {
        // Branch statistics carry each branch's head commit with committer date: one call, sorted here latest first.
        using var doc = await GetJsonAsync($"{organizationUrl}/_apis/git/repositories/{Uri.EscapeDataString(repositoryId)}/stats/branches?api-version={ApiVersion}",
            token, GitFailureKind.RepositoryNotFound, ct);
        return GitSourcePolicy.LatestFirst(Values(doc).Select(b =>
        {
            var commit = b.TryGetProperty("commit", out var c) ? c : default;
            var committer = commit.ValueKind == JsonValueKind.Object && commit.TryGetProperty("committer", out var cm) ? cm : default;
            DateTimeOffset? date = committer.ValueKind == JsonValueKind.Object && Str(committer, "date") is { } s && DateTimeOffset.TryParse(s, out var d) ? d : null;
            return new GitBranch(Str(b, "name") ?? "", commit.ValueKind == JsonValueKind.Object ? Str(commit, "commitId") ?? "" : "", date,
                committer.ValueKind == JsonValueKind.Object ? Str(committer, "name") : null, commit.ValueKind == JsonValueKind.Object ? Str(commit, "comment") : null,
                b.TryGetProperty("isBaseVersion", out var bv) && bv.ValueKind == JsonValueKind.True);
        }).Where(b => b.Name.Length > 0));
    }

    public async Task<byte[]> CommitArchiveAsync(string organizationUrl, string repositoryId, string commitSha, string token, long maxBytes, CancellationToken ct)
    {
        if (!GitSourcePolicy.IsCommitSha(commitSha)) throw new ArgumentException("A full commit SHA is required.", nameof(commitSha));
        var url = $"{organizationUrl}/_apis/git/repositories/{Uri.EscapeDataString(repositoryId)}/items?path=%2F&versionDescriptor.version={commitSha}"
                  + $"&versionDescriptor.versionType=commit&%24format=zip&download=true&api-version={ApiVersion}";
        using var response = await SendAsync(url, token, "application/zip", ct);
        await ThrowIfFailedAsync(response, GitFailureKind.RepositoryNotFound);
        if (response.Content.Headers.ContentLength is { } length && length > maxBytes) throw new GitProviderException(GitFailureKind.TooLarge, $"{length} bytes");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > maxBytes) throw new GitProviderException(GitFailureKind.TooLarge);
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private async Task<JsonDocument> GetJsonAsync(string url, string token, GitFailureKind notFound, CancellationToken ct)
    {
        using var response = await SendAsync(url, token, "application/json", ct);
        await ThrowIfFailedAsync(response, notFound);
        // Azure DevOps answers an unauthenticated call with a sign-in page (203 / HTML), not JSON.
        if (response.StatusCode == HttpStatusCode.NonAuthoritativeInformation || response.Content.Headers.ContentType?.MediaType?.Contains("json") != true)
            throw new GitProviderException(GitFailureKind.SignInRequired);
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
    }

    private async Task<HttpResponseMessage> SendAsync(string url, string token, string accept, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        try { return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct); }
        catch (HttpRequestException e) { throw new GitProviderException(GitFailureKind.Unreachable, e.HttpRequestError.ToString()); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new GitProviderException(GitFailureKind.Unreachable, "timeout"); }
    }

    private static Task ThrowIfFailedAsync(HttpResponseMessage response, GitFailureKind notFound) => response.StatusCode switch
    {
        HttpStatusCode.Unauthorized => throw new GitProviderException(GitFailureKind.SignInRequired),
        HttpStatusCode.Forbidden => throw new GitProviderException(GitFailureKind.AccessDenied),
        HttpStatusCode.NotFound => throw new GitProviderException(notFound),
        HttpStatusCode.TooManyRequests => throw new GitProviderException(GitFailureKind.Throttled),
        _ when (int)response.StatusCode >= 400 => throw new GitProviderException(GitFailureKind.Unexpected, $"HTTP {(int)response.StatusCode}"),
        _ => Task.CompletedTask,
    };

    private static IEnumerable<JsonElement> Values(JsonDocument doc) =>
        doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : [];

    private static string? Str(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
