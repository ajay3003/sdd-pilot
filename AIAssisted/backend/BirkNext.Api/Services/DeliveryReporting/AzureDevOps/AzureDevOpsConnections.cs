using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BirkNext.DeliveryReporting;
using Microsoft.AspNetCore.DataProtection;
using Path = System.IO.Path;

namespace BirkNext.Api.Services.DeliveryReporting.AzureDevOps;

/// <summary>A stored connection: names only plus the PAT encrypted with Data Protection. Never serialized to a client.</summary>
internal sealed record StoredAzureDevOpsConnection
{
    public Guid Id { get; init; }
    public string DisplayName { get; init; } = "";
    public string Organization { get; init; } = "";
    public string? ProtectedPat { get; init; }
    public string? LastValidationStatus { get; init; }
    public DateTimeOffset? LastValidatedAt { get; init; }
}

/// <summary>
/// Azure DevOps connections for Delivery Reporting imports: machine-local configuration (App_Data/azure-devops), the PAT encrypted at rest with
/// ASP.NET Core Data Protection (DPAPI-protected keys on Windows). Not in appsettings, not in delivery_reporting, not in the browser, never logged
/// and never returned — only "configured" is. Deleting a connection never touches imported snapshots or reports.
/// </summary>
public sealed partial class AzureDevOpsConnectionStore
{
    public const string ProtectionPurpose = "BirkNext.DeliveryReporting.AzureDevOps.Pat.v1";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _path;
    private readonly IDataProtector _protector;
    private readonly object _gate = new();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_-]{0,49}$")] private static partial Regex OrganizationPattern();

    public AzureDevOpsConnectionStore(string directory)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "connections.json");
        var keys = new DirectoryInfo(Path.Combine(directory, "keys"));
        var provider = DataProtectionProvider.Create(keys, builder =>
        {
            builder.SetApplicationName("BirkNext.DeliveryReporting.AzureDevOps");
            if (OperatingSystem.IsWindows()) builder.ProtectKeysWithDpapi();
        });
        _protector = provider.CreateProtector(ProtectionPurpose);
    }

    public static string DefaultDirectory(IHostEnvironment environment) => Path.Combine(environment.ContentRootPath, "App_Data", "azure-devops");

    /// <summary>"Bufdir" or a dev.azure.com organization URL → the organization name; null when not a valid name (no custom hosts).</summary>
    public static string? Organization(string? input)
    {
        var value = (input ?? "").Trim().TrimEnd('/');
        if (value.StartsWith("https://dev.azure.com/", StringComparison.OrdinalIgnoreCase)) value = value["https://dev.azure.com/".Length..].Split('/')[0];
        return OrganizationPattern().IsMatch(value) ? value : null;
    }

    private List<StoredAzureDevOpsConnection> Read() =>
        File.Exists(_path) ? JsonSerializer.Deserialize<List<StoredAzureDevOpsConnection>>(File.ReadAllText(_path), Json) ?? [] : [];

    private void Write(List<StoredAzureDevOpsConnection> items) => File.WriteAllText(_path, JsonSerializer.Serialize(items, Json));

    private static AzureDevOpsConnectionView View(StoredAzureDevOpsConnection c) =>
        new(c.Id, c.DisplayName, c.Organization, c.ProtectedPat is not null, c.LastValidationStatus, c.LastValidatedAt);

    public IReadOnlyList<AzureDevOpsConnectionView> List() { lock (_gate) return [.. Read().OrderBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase).Select(View)]; }

    public (AzureDevOpsConnectionView? Connection, string? Error) Save(Guid? id, AzureDevOpsConnectionRequest request)
    {
        var name = (request.DisplayName ?? "").Trim();
        if (name.Length is 0 or > 100) return (null, "Enter a display name (at most 100 characters).");
        if (Organization(request.Organization) is not { } organization) return (null, "Enter the Azure DevOps organization name (as in https://dev.azure.com/<organization>).");
        var pat = request.Pat?.Trim();
        if (pat is { Length: > 0 } && (pat.Length > 200 || pat.Any(char.IsWhiteSpace))) return (null, "The personal access token is not valid.");
        lock (_gate)
        {
            var items = Read();
            var existing = id is { } key ? items.FirstOrDefault(c => c.Id == key) : null;
            if (id is not null && existing is null) return (null, "The connection does not exist.");
            if (existing is null && pat is not { Length: > 0 }) return (null, "Enter a personal access token with read access to Code.");
            var record = (existing ?? new StoredAzureDevOpsConnection { Id = Guid.NewGuid() }) with
            {
                DisplayName = name, Organization = organization,
                // A new token replaces the stored one; an empty field keeps it (Edit never reveals or clears it).
                ProtectedPat = pat is { Length: > 0 } ? _protector.Protect(pat) : existing!.ProtectedPat,
                LastValidationStatus = pat is { Length: > 0 } || existing?.Organization != organization ? null : existing?.LastValidationStatus,
                LastValidatedAt = pat is { Length: > 0 } || existing?.Organization != organization ? null : existing?.LastValidatedAt,
            };
            items = [.. items.Where(c => c.Id != record.Id), record];
            Write(items);
            return (View(record), null);
        }
    }

    public bool Delete(Guid id)
    {
        lock (_gate)
        {
            var items = Read();
            if (items.All(c => c.Id != id)) return false;
            Write([.. items.Where(c => c.Id != id)]);
            return true;
        }
    }

    public void RecordValidation(Guid id, string status, DateTimeOffset at)
    {
        lock (_gate)
        {
            var items = Read();
            if (items.FirstOrDefault(c => c.Id == id) is not { } c) return;
            Write([.. items.Where(x => x.Id != id), c with { LastValidationStatus = status, LastValidatedAt = at }]);
        }
    }

    /// <summary>Organization and decrypted PAT for the Git client only (never leaves the Azure DevOps connection layer).</summary>
    internal (string Organization, string Pat)? Credential(Guid id)
    {
        lock (_gate)
        {
            if (Read().FirstOrDefault(c => c.Id == id) is not { ProtectedPat: { } protectedPat } c) return null;
            try { return (c.Organization, _protector.Unprotect(protectedPat)); }
            catch (System.Security.Cryptography.CryptographicException) { return null; }
        }
    }
}

/// <summary>A safe Azure DevOps failure: what happened, never the request, the header or the token.</summary>
public sealed class AzureDevOpsException(string message, HttpStatusCode? status = null) : Exception(message)
{
    public HttpStatusCode? Status { get; } = status;
}

/// <summary>
/// Read-only Azure DevOps Git REST client: projects, repositories, branches, commit resolution and the tree archive of one commit. The PAT is put
/// in the Authorization header here only. One API version for every call. Continuation tokens are followed (bounded). No retries on 429/503: the
/// user is told to try again later. Nothing is ever written to Azure DevOps.
/// </summary>
public sealed class AzureDevOpsGitClient(IHttpClientFactory clients, AzureDevOpsConnectionStore connections, Func<string, Uri>? baseUri = null)
{
    public const string ClientName = "azure-devops-delivery";
    /// <summary>The Azure DevOps REST API version used for every request.</summary>
    public const string ApiVersion = "7.1";
    private const int MaxPages = 20;
    /// <summary>A repository archive larger than this is refused before parsing (the upload limit of ZIP imports applies alike).</summary>
    public const long MaxArchiveBytes = 100L * 1024 * 1024;

    /// <summary>
    /// Development-only seam for local fake Azure DevOps servers (acceptance runs): DeliveryReporting:AzureDevOps:DevelopmentBaseUrl, honoured only
    /// in Development and only for a loopback http(s) URL. Production always uses https://dev.azure.com/{organization}/.
    /// </summary>
    public static Func<string, Uri>? DevelopmentBaseUri(IHostEnvironment environment, IConfiguration configuration)
    {
        if (!environment.IsDevelopment()) return null;
        if (!Uri.TryCreate(configuration["DeliveryReporting:AzureDevOps:DevelopmentBaseUrl"], UriKind.Absolute, out var root) || !root.IsLoopback) return null;
        return organization => new Uri(root, $"{Uri.EscapeDataString(organization)}/");
    }

    private Uri Base(string organization) => baseUri?.Invoke(organization) ?? new Uri($"https://dev.azure.com/{Uri.EscapeDataString(organization)}/");

    private (HttpClient Client, string Organization) Client(Guid connectionId)
    {
        if (connections.Credential(connectionId) is not { } credential) throw new AzureDevOpsException("Configure the Azure DevOps connection (its credential is missing or could not be read).");
        var client = clients.CreateClient(ClientName);
        client.BaseAddress = Base(credential.Organization);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + credential.Pat)));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return (client, credential.Organization);
    }

    private static string WithVersion(string path) => path + (path.Contains('?') ? "&" : "?") + "api-version=" + ApiVersion;

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string path, CancellationToken ct, string what)
    {
        HttpResponseMessage response;
        try { response = await client.GetAsync(WithVersion(path), HttpCompletionOption.ResponseHeadersRead, ct); }
        catch (HttpRequestException) { throw new AzureDevOpsException("Unable to reach Azure DevOps. Check the network and retry."); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new AzureDevOpsException("Azure DevOps did not answer in time. Retry later."); }
        if (response.IsSuccessStatusCode && response.Content.Headers.ContentType?.MediaType is "text/html")
        {
            response.Dispose();
            throw new AzureDevOpsException("Authentication failed: Azure DevOps returned a sign-in page. Check the personal access token.", HttpStatusCode.Unauthorized);
        }
        if (response.IsSuccessStatusCode) return response;
        var status = response.StatusCode;
        response.Dispose();
        throw status switch
        {
            HttpStatusCode.Unauthorized => new AzureDevOpsException("Authentication failed: the personal access token was not accepted.", status),
            HttpStatusCode.Forbidden => new AzureDevOpsException($"The connection is valid, but access to {what} is denied.", status),
            HttpStatusCode.NotFound => new AzureDevOpsException($"{char.ToUpperInvariant(what[0])}{what[1..]} was not found or is not accessible.", status),
            HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable => new AzureDevOpsException("Azure DevOps is rate limiting requests. Wait a moment and retry.", status),
            _ => new AzureDevOpsException($"Azure DevOps refused the request for {what} (HTTP {(int)status}).", status),
        };
    }

    private static async Task<(JsonNode? Body, string? Continuation)> JsonAsync(HttpClient client, string path, CancellationToken ct, string what)
    {
        using var response = await SendAsync(client, path, ct, what);
        var continuation = response.Headers.TryGetValues("x-ms-continuationtoken", out var values) ? values.FirstOrDefault() : null;
        try { return (JsonNode.Parse(await response.Content.ReadAsStringAsync(ct)), continuation); }
        catch (JsonException) { throw new AzureDevOpsException($"Azure DevOps returned an unreadable answer for {what}."); }
    }

    public async Task<AzureDevOpsValidationResult> ValidateAsync(Guid connectionId, CancellationToken ct)
    {
        try
        {
            var (client, organization) = Client(connectionId);
            await JsonAsync(client, "_apis/projects?$top=1", ct, "the organization");
            return new(true, "Connected", $"Connected to {organization} (read access to projects).");
        }
        catch (AzureDevOpsException ex) { return new(false, ex.Status is HttpStatusCode.Unauthorized ? "AuthenticationFailed" : ex.Status is HttpStatusCode.Forbidden ? "AccessDenied" : "Failed", ex.Message); }
    }

    public async Task<List<AzureDevOpsProjectItem>> ProjectsAsync(Guid connectionId, CancellationToken ct)
    {
        var (client, _) = Client(connectionId);
        var items = new List<AzureDevOpsProjectItem>();
        string? token = null;
        for (var page = 0; page < MaxPages; page++)
        {
            var (body, next) = await JsonAsync(client, "_apis/projects?$top=100" + (token is null ? "" : "&continuationToken=" + Uri.EscapeDataString(token)), ct, "the projects");
            items.AddRange((body?["value"] as JsonArray ?? []).Select(p => new AzureDevOpsProjectItem(p?["id"]?.ToString() ?? "", p?["name"]?.ToString() ?? "")).Where(p => p.Name.Length > 0));
            if (string.IsNullOrEmpty(next) || next == token) break;
            token = next;
        }
        return [.. items.DistinctBy(p => p.Id).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)];
    }

    public async Task<List<AzureDevOpsRepositoryItem>> RepositoriesAsync(Guid connectionId, string project, CancellationToken ct)
    {
        var (client, _) = Client(connectionId);
        var (body, _) = await JsonAsync(client, $"{Uri.EscapeDataString(project)}/_apis/git/repositories", ct, $"project {project}");
        return [.. (body?["value"] as JsonArray ?? []).Select(r => new AzureDevOpsRepositoryItem(r?["id"]?.ToString() ?? "", r?["name"]?.ToString() ?? "", Friendly(r?["defaultBranch"]?.ToString())))
            .Where(r => r.Name.Length > 0).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)];
    }

    public async Task<List<AzureDevOpsBranchItem>> BranchesAsync(Guid connectionId, string project, string repository, CancellationToken ct)
    {
        var (client, _) = Client(connectionId);
        var items = new List<AzureDevOpsBranchItem>();
        string? token = null;
        for (var page = 0; page < MaxPages; page++)
        {
            var (body, next) = await JsonAsync(client, $"{Uri.EscapeDataString(project)}/_apis/git/repositories/{Uri.EscapeDataString(repository)}/refs?filter=heads/&$top=500"
                + (token is null ? "" : "&continuationToken=" + Uri.EscapeDataString(token)), ct, $"repository {repository}");
            items.AddRange((body?["value"] as JsonArray ?? []).Select(r => r?["name"]?.ToString()).OfType<string>().Select(n => new AzureDevOpsBranchItem(Friendly(n)!, n)));
            if (string.IsNullOrEmpty(next) || next == token) break;
            token = next;
        }
        return [.. items.DistinctBy(b => b.Ref).OrderBy(b => b.Name is "main" or "master" ? 0 : b.Name == "develop" ? 1 : 2).ThenBy(b => b.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>"refs/heads/release/1.2" → "release/1.2".</summary>
    public static string? Friendly(string? reference) => reference?.StartsWith("refs/heads/", StringComparison.Ordinal) == true ? reference["refs/heads/".Length..] : reference;

    /// <summary>
    /// The latest commit reachable from the branch with commit time at or before <paramref name="at"/> (server-side: itemVersion = the branch,
    /// toDate = the time, newest first, one result). Never falls forward to a later commit and never downloads history.
    /// </summary>
    public async Task<DeliveryAzureDevOpsSource?> ResolveAsync(Guid connectionId, string project, string repository, string branch, DateTimeOffset at, CancellationToken ct)
    {
        var (client, organization) = Client(connectionId);
        var path = $"{Uri.EscapeDataString(project)}/_apis/git/repositories/{Uri.EscapeDataString(repository)}/commits"
            + $"?searchCriteria.itemVersion.version={Uri.EscapeDataString(branch)}&searchCriteria.itemVersion.versionType=branch"
            + $"&searchCriteria.toDate={Uri.EscapeDataString(at.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"))}&searchCriteria.$top=1";
        var (body, _) = await JsonAsync(client, path, ct, $"branch {branch}");
        var commit = (body?["value"] as JsonArray)?.FirstOrDefault();
        if (commit?["commitId"]?.ToString() is not { Length: > 0 } id) return null;
        var when = DateTimeOffset.TryParse(commit["committer"]?["date"]?.ToString() ?? commit["author"]?["date"]?.ToString(), out var d) ? d : at;
        if (when > at) return null; // defensive: never a later commit
        var message = commit["comment"]?.ToString();
        return new(organization, project, repository, branch, id, when)
        {
            CommitAuthor = commit["author"]?["name"]?.ToString(), CommitMessage = message is null ? null : message.Length > 200 ? message[..200] : message,
        };
    }

    /// <summary>The repository tree of one commit as a ZIP (Items API, $format=zip), bounded by <see cref="MaxArchiveBytes"/>.</summary>
    public async Task<byte[]> ArchiveAsync(Guid connectionId, string project, string repository, string commitId, CancellationToken ct)
    {
        var (client, _) = Client(connectionId);
        client.DefaultRequestHeaders.Accept.Clear();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/zip"));
        using var response = await SendAsync(client, $"{Uri.EscapeDataString(project)}/_apis/git/repositories/{Uri.EscapeDataString(repository)}/items?path=/"
            + $"&versionDescriptor.version={Uri.EscapeDataString(commitId)}&versionDescriptor.versionType=commit&$format=zip&download=true", ct, $"commit {commitId[..Math.Min(7, commitId.Length)]}");
        if (response.Content.Headers.ContentLength > MaxArchiveBytes) throw new AzureDevOpsException("The repository archive is larger than the import limit.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int n;
        while ((n = await stream.ReadAsync(chunk, ct)) > 0)
        {
            buffer.Write(chunk, 0, n);
            if (buffer.Length > MaxArchiveBytes) throw new AzureDevOpsException("The repository archive is larger than the import limit.");
        }
        return buffer.ToArray();
    }
}

/// <summary>
/// Turns a selected Azure DevOps branch + time into a Delivery Reporting import: resolve the commit first (never today's head relabelled), download
/// that commit's tree, and hand the bytes to the SAME ZIP preview pipeline (same parser, same staging, same duplicate rule on commit identity).
/// </summary>
public sealed class DeliveryAzureDevOpsImportService(AzureDevOpsGitClient git, DeliveryReportingImportService imports, TimeProvider clock, ILogger<DeliveryAzureDevOpsImportService> logger)
{
    /// <summary>Resolve only (preview of the commit, no download).</summary>
    public async Task<AzureDevOpsResolveResult> ResolveAsync(AzureDevOpsResolveRequest request, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        if (request.RequestedAt is { } requested && requested > now.AddMinutes(1))
            return new(null, null, "The selected time is in the future. Choose a past time, or Current/latest for the branch head now.");
        if (string.IsNullOrWhiteSpace(request.Project) || string.IsNullOrWhiteSpace(request.Repository) || string.IsNullOrWhiteSpace(request.Branch))
            return new(null, null, "Choose a project, repository and branch.");
        try
        {
            var at = request.RequestedAt ?? now;
            var commit = await git.ResolveAsync(request.ConnectionId, request.Project, request.Repository, request.Branch, at, ct);
            if (commit is null) return new(null, null, "No commit found on this branch before the selected date/time.");
            commit = commit with { RequestedAt = request.RequestedAt ?? at };
            var gap = at - commit.CommitAt;
            var note = gap > TimeSpan.FromDays(1)
                ? $"The latest commit before the selected time is from {commit.CommitAt.ToUniversalTime():d MMM yyyy HH:mm} UTC ({(int)gap.TotalDays} day(s) earlier). No later commit existed, so this tree is the repository state at the requested time."
                : null;
            logger.LogInformation("Azure DevOps commit resolved for Delivery Reporting: {Project}/{Repository}@{Branch} → {Commit}", request.Project, request.Repository, request.Branch, commit.ShortCommit);
            return new(commit, note, null);
        }
        catch (AzureDevOpsException ex) { return new(null, null, ex.Message); }
    }

    /// <summary>Resolve, download the commit tree and preview it exactly like an uploaded ZIP.</summary>
    public async Task<(DeliveryImportPreview? Preview, string? Note, string? Error)> PreviewAsync(AzureDevOpsResolveRequest request, CancellationToken ct)
    {
        var resolved = await ResolveAsync(request, ct);
        if (resolved.Commit is not { } commit) return (null, null, resolved.Error);
        byte[] bytes;
        try { bytes = await git.ArchiveAsync(request.ConnectionId, request.Project, request.Repository, commit.CommitId, ct); }
        catch (AzureDevOpsException ex) { return (null, null, ex.Message); }
        var fileName = $"{request.Repository}@{commit.ShortCommit}.zip";
        var outcome = await imports.PreviewAsync(request.ProjectId, fileName, bytes, ct, commit);
        return outcome.Failure is { } failure ? (null, null, failure.Message) : (outcome.Preview, resolved.Note, null);
    }
}
