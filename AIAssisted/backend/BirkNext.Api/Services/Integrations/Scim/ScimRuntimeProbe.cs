using System.Diagnostics;
using System.Text.Json;
using BirkNext.Integrations;

namespace BirkNext.Api.Services.Integrations.Scim;

/// <summary>Safe runtime observations of a SCIM endpoint. GET only; never a user write, never a user list, never a message.</summary>
public interface IScimRuntimeProbe
{
    Task<ScimRuntimeEvidence> ProbeAsync(ScimProvisioningSettings settings, string? environmentType, CancellationToken ct = default);
}

/// <summary>
/// The "Run safe SCIM checks" runtime part. Every request is a GET to a fixed allow-list of paths: health (live/ready), the authentication
/// challenge on a RANDOM user id (without a token and with a deliberately invalid one — expected 401, and even a broken authentication can
/// only return "not found" for an id nobody has), the SCIM metadata resources and the anonymous API description. <c>GET /Users</c> is never
/// called, so no user is enumerated. POST/PATCH/DELETE are never sent. Response bodies are read bounded, reduced to a few derived facts
/// (status value, schema URNs, route paths) and discarded — no body, header value or token is stored or logged. Production is never contacted.
/// </summary>
public sealed class HttpScimRuntimeProbe(HttpClient http, ILogger<HttpScimRuntimeProbe> logger) : IScimRuntimeProbe
{
    public const string Adapter = "SCIM safe checks";
    public const string InvalidToken = "birknext-invalid-token";
    private const int MaxBodyBytes = 512 * 1024;
    private static readonly string[] AllowedEnvironments = ["Local", "Development", "QA", "Test", "RC"];

    /// <summary>Why the safe checks cannot contact the endpoint, or null when they can.</summary>
    public static (IntegrationEvidenceState State, string Reason)? Gate(ScimProvisioningSettings settings, string? environmentType)
    {
        if (string.Equals(environmentType, "Production", StringComparison.OrdinalIgnoreCase))
            return (IntegrationEvidenceState.NotSupported, "Production is never contacted by the SCIM checks.");
        if (environmentType is null || !AllowedEnvironments.Contains(environmentType, StringComparer.OrdinalIgnoreCase))
            return (IntegrationEvidenceState.NotSupported, $"The Target Environment type is {(string.IsNullOrWhiteSpace(environmentType) ? "unknown" : environmentType)}; safe SCIM checks run only for a known non-production environment (Local, Development, QA, Test, RC).");
        if (string.IsNullOrWhiteSpace(settings.BaseUrl))
            return (IntegrationEvidenceState.NotConfigured, "No SCIM base URL (the public tenant URL Entra calls) is configured, so the endpoint, health and authentication challenge are not observed.");
        if (settings.Validate() is { } invalid) return (IntegrationEvidenceState.NotConfigured, invalid);
        return null;
    }

    public async Task<ScimRuntimeEvidence> ProbeAsync(ScimProvisioningSettings settings, string? environmentType, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        if (Gate(settings, environmentType) is { } gate) return new ScimRuntimeEvidence { State = gate.State, Reason = gate.Reason, CapturedAt = now };
        var root = new Uri(settings.BaseUrl!.Trim().TrimEnd('/') + "/");
        var basePath = settings.BasePath.TrimEnd('/');
        var target = $"{root.Scheme}://{root.Authority}";
        var randomUser = Guid.NewGuid();
        var observations = new List<ScimProbeObservation>
        {
            await Health("scim-health-live", "Liveness endpoint", root, Path(settings.HealthLivePath), ct),
            await Health("scim-health-ready", "Readiness endpoint", root, Path(settings.HealthReadyPath), ct),
            await Challenge("scim-auth-missing", "Request without a token is rejected", root, $"{basePath}{settings.UsersResource}/{randomUser}", null, ct),
            await Challenge("scim-auth-invalid", "Request with an invalid token is rejected", root, $"{basePath}{settings.UsersResource}/{randomUser}", InvalidToken, ct),
            await Metadata("scim-meta-serviceproviderconfig", "ServiceProviderConfig", root, $"{basePath}/ServiceProviderConfig", ct),
            await Metadata("scim-meta-schemas", "Schemas", root, $"{basePath}/Schemas", ct),
            await Metadata("scim-meta-resourcetypes", "ResourceTypes", root, $"{basePath}/ResourceTypes", ct),
            await OpenApi(root, ct),
        };
        var responded = observations.Count(o => o.StatusCode is not null);
        var state = responded == 0 ? IntegrationEvidenceState.Unavailable : IntegrationEvidenceState.Available;
        logger.LogInformation("SCIM safe checks against {Target}: {Responded} of {Total} GET request(s) answered ({Statuses}).",
            target, responded, observations.Count, string.Join(",", observations.Select(o => $"{o.CheckId}={o.StatusCode?.ToString() ?? "none"}")));
        return new ScimRuntimeEvidence
        {
            State = state, Reason = state == IntegrationEvidenceState.Available ? $"{responded} of {observations.Count} safe GET request(s) answered." : "The endpoint did not answer any safe GET request.",
            Target = target, CapturedAt = now, Observations = observations,
        };
    }

    private static string Path(string? path) => string.IsNullOrWhiteSpace(path) ? "" : "/" + path.Trim().TrimStart('/');

    private async Task<(int? Status, string? Body, string? Error, double Ms)> GetAsync(Uri root, string path, string? token, bool readBody, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(root, path.TrimStart('/')));
            request.Headers.Accept.ParseAdd("application/scim+json, application/json");
            if (token is not null) request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            string? body = null;
            if (readBody && response.IsSuccessStatusCode)
            {
                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                var buffer = new byte[MaxBodyBytes];
                var read = 0;
                int n;
                while (read < buffer.Length && (n = await stream.ReadAsync(buffer.AsMemory(read), ct)) > 0) read += n;
                body = System.Text.Encoding.UTF8.GetString(buffer, 0, read);
            }
            return ((int)response.StatusCode, body, null, stopwatch.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException)
        {
            return (null, null, ex is TaskCanceledException && !ct.IsCancellationRequested ? "timed out" : ex is HttpRequestException h ? $"connection failed ({h.HttpRequestError})" : "invalid URL", stopwatch.Elapsed.TotalMilliseconds);
        }
    }

    private async Task<ScimProbeObservation> Health(string id, string title, Uri root, string path, CancellationToken ct)
    {
        if (path.Length == 0) return new ScimProbeObservation { CheckId = id, Title = title, Path = "(not configured)", Expected = "200", State = ScimEvidenceState.NotConfigured, Detail = "No path configured." };
        var (status, body, error, ms) = await GetAsync(root, path, null, true, ct);
        var reported = body is null ? null : ReadString(body, "status");
        return new ScimProbeObservation
        {
            CheckId = id, Title = title, Path = path, Expected = "200", StatusCode = status, DurationMs = ms,
            State = status is null ? ScimEvidenceState.Unavailable : status == 200 ? ScimEvidenceState.Observed : status == 503 ? ScimEvidenceState.IssueDetected : ScimEvidenceState.NeedsReview,
            Detail = status is null ? $"No response: {error}." : $"HTTP {status}{(reported is not null ? $", status \"{reported}\"" : "")}. An HTTP 200 health response is not dependency health unless the service registers dependency checks.",
        };
    }

    private async Task<ScimProbeObservation> Challenge(string id, string title, Uri root, string path, string? token, CancellationToken ct)
    {
        var (status, _, error, ms) = await GetAsync(root, path, token, false, ct);
        var display = path.Replace(path[(path.LastIndexOf('/') + 1)..], "{random-id}");
        return new ScimProbeObservation
        {
            CheckId = id, Title = title, Path = display, Expected = "401", StatusCode = status, DurationMs = ms,
            State = status switch { null => ScimEvidenceState.Unavailable, 401 => ScimEvidenceState.Verified, 200 or 404 => ScimEvidenceState.IssueDetected, _ => ScimEvidenceState.NeedsReview },
            Detail = status switch
            {
                null => $"No response: {error}.",
                401 => $"HTTP 401 {(token is null ? "without a token" : "with an invalid token")}: the request is rejected before a handler runs. This verifies the challenge only — not the full token validation.",
                200 or 404 => $"HTTP {status} {(token is null ? "without a token" : "with an invalid token")}: the request reached the handler. Authentication is not enforced on this route.",
                403 => "HTTP 403: authenticated as someone but forbidden — review which scheme accepted the request.",
                _ => $"HTTP {status}: not the expected 401.",
            },
        };
    }

    private async Task<ScimProbeObservation> Metadata(string id, string title, Uri root, string path, CancellationToken ct)
    {
        var (status, body, error, ms) = await GetAsync(root, path, null, true, ct);
        var schemas = body is null ? null : ReadString(body, "schemas");
        return new ScimProbeObservation
        {
            CheckId = id, Title = $"{title} resource", Path = path, Expected = "401 or 200 (SCIM metadata)", StatusCode = status, DurationMs = ms,
            State = status switch { null => ScimEvidenceState.Unavailable, 200 => ScimEvidenceState.Observed, 401 => ScimEvidenceState.Observed, 404 => ScimEvidenceState.NotSupported, _ => ScimEvidenceState.NeedsReview },
            Detail = status switch
            {
                null => $"No response: {error}.",
                200 => $"Served anonymously{(schemas is not null ? $" ({schemas})" : "")}.",
                401 => "Requires authentication; whether the resource exists is not visible without a token.",
                404 => "Not served (not mapped). SCIM metadata is optional unless the specification requires it.",
                _ => $"HTTP {status}.",
            },
        };
    }

    private async Task<ScimProbeObservation> OpenApi(Uri root, CancellationToken ct)
    {
        var (status, body, error, ms) = await GetAsync(root, "/openapi/v1.json", null, true, ct);
        var paths = new List<string>();
        if (body is not null)
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("paths", out var p) && p.ValueKind == JsonValueKind.Object)
                    foreach (var route in p.EnumerateObject())
                        paths.Add($"{string.Join("/", route.Value.EnumerateObject().Select(m => m.Name.ToUpperInvariant()))} {route.Name}");
            }
            catch (JsonException) { }
        return new ScimProbeObservation
        {
            CheckId = "scim-openapi", Title = "Anonymous API description", Path = "/openapi/v1.json", Expected = "(informational)", StatusCode = status, DurationMs = ms,
            State = status switch { null => ScimEvidenceState.Unavailable, 200 => ScimEvidenceState.Observed, 401 or 403 or 404 => ScimEvidenceState.NotSupported, _ => ScimEvidenceState.NeedsReview },
            Detail = status == 200 ? (paths.Count > 0 ? $"Published without a token: {string.Join("; ", paths.Take(12))}." : "Published without a token.") : status is null ? $"No response: {error}." : $"HTTP {status}: not published anonymously.",
        };
    }

    /// <summary>A string (or the first element of a string array) at a top-level property of a JSON body, bounded; never the body itself.</summary>
    private static string? ReadString(string body, string property)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty(property, out var value)) return null;
            var text = value.ValueKind == JsonValueKind.String ? value.GetString() : value.ValueKind == JsonValueKind.Array ? string.Join(", ", value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString())) : null;
            return text is null ? null : text.Length > 120 ? text[..120] : text;
        }
        catch (JsonException) { return null; }
    }
}
