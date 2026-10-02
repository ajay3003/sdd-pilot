using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BirkNext.Api.Services.AzureEnvironment;

/// <summary>An Azure Resource Manager answer reduced to status, JSON body and error code. Status 0 = refused locally (never sent).</summary>
public sealed record ArmResult(int Status, JsonElement? Body, string? ErrorCode, TimeSpan? RetryAfter = null)
{
    public bool Ok => Status is >= 200 and < 300 && Body is not null;
    public bool NotAuthorized => Status is 401 or 403;
    public bool Throttled => Status == 429;
    public static ArmResult Refused(string reason) => new(0, null, reason);
    public static ArmResult NotSignedIn => new(401, null, "NotSignedIn");
}

/// <summary>
/// A predefined Azure Resource Graph query. The constructor is private: every query BirkNext can run is one of the static instances below,
/// so no user input ever becomes KQL. Scope (subscriptions) travels in the request body, and resource-group narrowing is applied to results.
/// </summary>
public sealed class AzureGraphQuery
{
    private AzureGraphQuery(string name, string kql) { Name = name; Kql = kql; }
    public string Name { get; }
    public string Kql { get; }

    public static readonly AzureGraphQuery Resources = new("resources",
        "Resources | project id, name, type, kind, location, resourceGroup, subscriptionId, tags, sku, identity, managedBy, properties | order by id asc");

    public static readonly AzureGraphQuery ResourceGroups = new("resource-groups",
        "ResourceContainers | where type =~ 'microsoft.resources/subscriptions/resourcegroups' | project id, name, type, location, resourceGroup = name, subscriptionId, tags | order by id asc");

    public static IReadOnlyList<AzureGraphQuery> All => [Resources, ResourceGroups];
}

public interface IAzureManagementClient
{
    /// <summary>A GET on Azure Resource Manager (relative URL incl. api-version), refused locally unless <see cref="AzureReadOnlyPolicy"/> allows it.</summary>
    Task<ArmResult> GetAsync(string relativeUrl, CancellationToken ct);
    /// <summary>One page of a predefined Resource Graph query (the only non-GET request: Resource Graph reads with POST).</summary>
    Task<ArmResult> QueryAsync(AzureGraphQuery query, IReadOnlyList<string> subscriptionIds, string? skipToken, CancellationToken ct);
}

/// <summary>
/// The read-only boundary. GET only (plus the predefined Resource Graph read), management host only, and never an operation that returns or
/// changes secrets, keys, credentials, app settings or connection strings — even when the signed-in person has Owner or Contributor.
/// </summary>
public static class AzureReadOnlyPolicy
{
    public const string ResourceGraphPath = "/providers/Microsoft.ResourceGraph/resources?api-version=2022-10-01";

    // Path segments whose GET (or any method) returns secret material or configuration values, or is a data-plane/content read.
    private static readonly Regex DeniedSegment = new(
        @"/(list\w*|regenerate\w*|secrets|keys|certificates|credentials|publishingcredentials|publishxml|hostkeys|functionkeys|backup|restore|" +
        @"appsettings|connectionstrings|authsettings|authsettingsv2|metadata|pushsettings|azurestorageaccounts|sitecontainers|messages|blobs|" +
        @"authorizationrules|sharedaccesspolicies|accesskeys|sas|token|tokens|activate|deactivate|start|stop|restart|failover|export|import)(/|$|\?)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Config = new(@"/config/(?<name>[^/?]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Null when the GET may be sent; otherwise why it is refused.</summary>
    public static string? CheckGet(string relativeUrl)
    {
        if (string.IsNullOrWhiteSpace(relativeUrl) || !relativeUrl.StartsWith('/') || relativeUrl.StartsWith("//", StringComparison.Ordinal) || relativeUrl.Contains("://", StringComparison.Ordinal))
            return "Only relative Azure Resource Manager paths are read.";
        if (relativeUrl.Contains("..", StringComparison.Ordinal) || relativeUrl.Contains('\\') || relativeUrl.Any(char.IsControl)) return "Path traversal is refused.";
        if (!relativeUrl.Contains("api-version=", StringComparison.OrdinalIgnoreCase)) return "An api-version is required.";
        var path = relativeUrl.Split('?')[0];
        if (DeniedSegment.IsMatch(path)) return "This operation can return secret values or content and is never called.";
        foreach (Match m in Config.Matches(path))
            if (!string.Equals(m.Groups["name"].Value, "web", StringComparison.OrdinalIgnoreCase)) return "Only the non-secret site configuration (config/web) is read.";
        return null;
    }

    /// <summary>A server-provided nextLink as a relative path on the management host, or null when it points anywhere else.</summary>
    public static string? RelativeNextLink(string? nextLink)
    {
        if (string.IsNullOrWhiteSpace(nextLink) || !Uri.TryCreate(nextLink, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttps || !string.Equals(uri.Host, new Uri(AzureEnvironmentOptions.ManagementEndpoint).Host, StringComparison.OrdinalIgnoreCase) || !uri.IsDefaultPort) return null;
        var relative = uri.PathAndQuery;
        return CheckGet(relative) is null ? relative : null;
    }
}

public sealed class HttpAzureManagementClient(HttpClient http, IAzureSignInService signIn, ILogger<HttpAzureManagementClient> logger) : IAzureManagementClient
{
    private static readonly TimeSpan MaxRetryWait = TimeSpan.FromSeconds(15);
    private const int MaxAttempts = 3;

    public Task<ArmResult> GetAsync(string relativeUrl, CancellationToken ct)
    {
        if (AzureReadOnlyPolicy.CheckGet(relativeUrl) is { } refused)
        {
            logger.LogWarning("Refused an Azure read outside the read-only policy: {Reason}", refused);
            return Task.FromResult(ArmResult.Refused(refused));
        }
        return SendAsync(() => new HttpRequestMessage(HttpMethod.Get, AzureEnvironmentOptions.ManagementEndpoint + relativeUrl), relativeUrl.Split('?')[0], ct);
    }

    public Task<ArmResult> QueryAsync(AzureGraphQuery query, IReadOnlyList<string> subscriptionIds, string? skipToken, CancellationToken ct)
    {
        var requestOptions = new Dictionary<string, object> { ["$top"] = 1000, ["resultFormat"] = "objectArray" };
        if (skipToken is not null) requestOptions["$skipToken"] = skipToken;
        var body = JsonSerializer.Serialize(new Dictionary<string, object> { ["subscriptions"] = subscriptionIds, ["query"] = query.Kql, ["options"] = requestOptions });
        // The single non-GET request: Azure Resource Graph is queried with POST; the KQL is a predefined constant.
        return SendAsync(() => new HttpRequestMessage(HttpMethod.Post, AzureEnvironmentOptions.ManagementEndpoint + AzureReadOnlyPolicy.ResourceGraphPath)
        { Content = new StringContent(body, Encoding.UTF8, "application/json") }, $"resource-graph:{query.Name}", ct);
    }

    private async Task<ArmResult> SendAsync(Func<HttpRequestMessage> build, string label, CancellationToken ct)
    {
        var refreshed = false;
        for (var attempt = 1; ; attempt++)
        {
            var token = await signIn.AccessTokenAsync(forceRefresh: false, ct);
            if (token is null) return ArmResult.NotSignedIn;
            using var request = build();
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            HttpResponseMessage response;
            try { response = await http.SendAsync(request, ct); }
            catch (HttpRequestException ex)
            {
                logger.LogInformation("Azure read {Label} failed: {Error}.", label, ex.GetType().Name);
                return new ArmResult(503, null, ex.GetType().Name);
            }
            using (response)
            {
                var status = (int)response.StatusCode;
                if (status == 401 && !refreshed)
                {
                    refreshed = true;
                    if (await signIn.AccessTokenAsync(forceRefresh: true, ct) is not null) continue;
                }
                var retryAfter = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date is { } at ? at - DateTimeOffset.UtcNow : null);
                if (status is 429 or 503 && attempt < MaxAttempts && (retryAfter ?? TimeSpan.FromSeconds(2)) <= MaxRetryWait)
                {
                    await Task.Delay(retryAfter is { } wait && wait > TimeSpan.Zero ? wait : TimeSpan.FromSeconds(2 * attempt), ct);
                    continue;
                }
                JsonElement? body = null;
                string? code = null;
                var text = await response.Content.ReadAsStringAsync(ct);
                if (text.Length > 0)
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(text);
                        body = doc.RootElement.Clone();
                        if (body.Value.ValueKind == JsonValueKind.Object && body.Value.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object && error.TryGetProperty("code", out var c))
                            code = c.GetString();
                    }
                    catch (JsonException) { code = "NonJsonResponse"; }
                }
                if (status == 401) signIn.MarkExpired("Azure rejected the token. Sign in again.");
                if (status >= 400) logger.LogInformation("Azure read {Label}: HTTP {Status} {Code}.", label, status, code);
                return new ArmResult(status, status is >= 200 and < 300 ? body : null, code, retryAfter);
            }
        }
    }
}
