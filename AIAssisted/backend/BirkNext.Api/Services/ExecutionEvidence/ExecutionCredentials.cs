using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BirkNext.Api.Services.ContractAnalysis;
using BirkNext.Api.Services.LocalHttpsProxy;
using BirkNext.ExecutionEvidence;
using Microsoft.AspNetCore.DataProtection;

namespace BirkNext.Api.Services.ExecutionEvidence;

/// <summary>
/// Unattended API service credentials, one per Target Environment: OAuth2 client credentials (token endpoint, client id, scope, client secret) or
/// an API key (header name, key). Same storage rule as the unattended browser login: configuration in a JSON file without secrets, the secret
/// encrypted with ASP.NET Core Data Protection (a key ring dedicated to this purpose; on Windows the keys are DPAPI-protected for the current
/// user) under the backend's App_Data — never appsettings, the database, Test Data or evidence. Secrets are never returned.
/// </summary>
public sealed partial class ApiServiceCredentialStore
{
    public const string ProtectionPurpose = "BirkNext.ApiServiceCredential.Secret.v1";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
    private static readonly string[] ForbiddenHeaders = ["authorization", "cookie", "host", "content-type", "content-length", "proxy-authorization", "x-birknext-execution-id"];
    [GeneratedRegex("^[A-Za-z0-9-]{1,64}$")] private static partial Regex HeaderName();
    private readonly string _directory;
    private readonly string _settingsPath;
    private readonly IDataProtector _protector;
    private readonly object _gate = new();

    private sealed record Entry(string EnvironmentId, ExecutionAuthenticationKind Kind, string? TokenUrl, string? ClientId, string? Scope, string? HeaderName, int Version, DateTimeOffset UpdatedAt);

    public ApiServiceCredentialStore(string directory)
    {
        Directory.CreateDirectory(directory);
        _directory = directory;
        _settingsPath = System.IO.Path.Combine(directory, "api-service-credentials.json");
        var provider = DataProtectionProvider.Create(new DirectoryInfo(System.IO.Path.Combine(directory, "keys")), builder =>
        {
            builder.SetApplicationName("BirkNext.ApiServiceCredential");
            if (OperatingSystem.IsWindows()) builder.ProtectKeysWithDpapi();
        });
        _protector = provider.CreateProtector(ProtectionPurpose);
    }

    public static string DefaultDirectory(IHostEnvironment environment) => System.IO.Path.Combine(environment.ContentRootPath, "App_Data", "api-service-credentials");

    public IReadOnlyList<ApiServiceCredentialView> List() { lock (_gate) return [.. Load().Values.Select(View)]; }
    public ApiServiceCredentialView? Get(string environmentId) { lock (_gate) return Load().TryGetValue(environmentId, out var e) ? View(e) : null; }

    public (ApiServiceCredentialView? View, string? Error) Save(ApiServiceCredentialSave save, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(save.EnvironmentId) || save.EnvironmentId.Length > 200) return (null, "Select a Target Environment.");
        if (save.Kind is not (ExecutionAuthenticationKind.ClientCredentials or ExecutionAuthenticationKind.ApiKey))
            return (null, "An unattended API credential is OAuth2 client credentials or an API key.");
        lock (_gate)
        {
            var all = Load();
            all.TryGetValue(save.EnvironmentId, out var existing);
            var secret = string.IsNullOrEmpty(save.Secret) ? null : save.Secret;
            if (secret is null && (existing is null || existing.Kind != save.Kind || !File.Exists(SecretPath(save.EnvironmentId))))
                return (null, save.Kind == ExecutionAuthenticationKind.ApiKey ? "Enter the API key." : "Enter the client secret.");
            if (secret is { Length: > 4096 }) return (null, "The secret is too long.");
            if (save.Kind == ExecutionAuthenticationKind.ClientCredentials)
            {
                if (TokenEndpointProblem(save.TokenUrl) is { } problem) return (null, problem);
                if (string.IsNullOrWhiteSpace(save.ClientId) || save.ClientId.Length > 200) return (null, "Enter the client id.");
                if (save.Scope is { Length: > 500 }) return (null, "The scope is too long.");
            }
            else if (save.HeaderName is not { } header || !HeaderName().IsMatch(header) || ForbiddenHeaders.Contains(header.ToLowerInvariant()))
                return (null, "The API key header is a plain header name (letters, digits, '-') and not Authorization, Cookie, Host or a content header.");
            var entry = new Entry(save.EnvironmentId, save.Kind, save.Kind == ExecutionAuthenticationKind.ClientCredentials ? save.TokenUrl!.Trim() : null,
                save.Kind == ExecutionAuthenticationKind.ClientCredentials ? save.ClientId!.Trim() : null, save.Kind == ExecutionAuthenticationKind.ClientCredentials ? save.Scope?.Trim() : null,
                save.Kind == ExecutionAuthenticationKind.ApiKey ? save.HeaderName : null, (existing?.Version ?? 0) + 1, now);
            if (secret is not null) Write(SecretPath(save.EnvironmentId), _protector.Protect(secret));
            all[save.EnvironmentId] = entry;
            Write(_settingsPath, JsonSerializer.Serialize(all.Values.ToList(), Json));
            return (View(entry), null);
        }
    }

    public bool Delete(string environmentId)
    {
        lock (_gate)
        {
            var all = Load();
            if (!all.Remove(environmentId)) return false;
            if (File.Exists(SecretPath(environmentId))) File.Delete(SecretPath(environmentId));
            Write(_settingsPath, JsonSerializer.Serialize(all.Values.ToList(), Json));
            return true;
        }
    }

    /// <summary>The decrypted secret for one execution (backend memory only). Null when none is stored or it cannot be decrypted here.</summary>
    internal string? ReadSecret(string environmentId)
    {
        string text;
        lock (_gate) { if (!File.Exists(SecretPath(environmentId))) return null; text = File.ReadAllText(SecretPath(environmentId)); }
        try { return _protector.Unprotect(text); } catch (CryptographicException) { return null; }
    }

    /// <summary>A token endpoint is fetched by the backend, so it follows the contract-source policy and, for plain HTTP, must be loopback.</summary>
    public static string? TokenEndpointProblem(string? url)
    {
        var (uri, problem) = ContractSourcePolicy.Validate(url);
        if (uri is null) return "Token endpoint: " + problem;
        return uri.Scheme == Uri.UriSchemeHttps || uri.IsLoopback ? null : "Token endpoint: plain HTTP is allowed only for a loopback address; use HTTPS.";
    }

    public static string Reference(string environmentId, ExecutionAuthenticationKind kind, int version) => $"api-credential:{environmentId}:{kind}:v{version}";

    private static ApiServiceCredentialView View(Entry e) => new(e.EnvironmentId, e.Kind, Reference(e.EnvironmentId, e.Kind, e.Version), e.TokenUrl, e.ClientId, e.Scope, e.HeaderName,
        true, e.Version, e.UpdatedAt);

    private Dictionary<string, Entry> Load()
    {
        if (!File.Exists(_settingsPath)) return new(StringComparer.Ordinal);
        try { return (JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(_settingsPath), Json) ?? []).ToDictionary(e => e.EnvironmentId, StringComparer.Ordinal); }
        catch (JsonException) { return new(StringComparer.Ordinal); }
    }

    private string SecretPath(string environmentId) => System.IO.Path.Combine(_directory, $"secret-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(environmentId)))[..16].ToLowerInvariant()}.bin");

    private static void Write(string path, string text)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, text);
        File.Move(temp, path, overwrite: true);
    }
}

/// <summary>
/// Resolves and applies the credential of a run inside the backend. Attended: the Local HTTPS proxy credential of the run's own Target Environment
/// profile (HTTPS + approved authorities only; re-validated per attempt). Unattended: the environment's API service credential — an OAuth2
/// client-credentials token (fetched by the backend, cached in memory until shortly before it expires) or an API key — sent only over HTTPS, or
/// over HTTP to a loopback target of a Local environment. The value never leaves the request: evidence records the header as redacted and the
/// run records only the configuration reference.
/// </summary>
public sealed class ExecutionCredentialResolver(TransientAuthenticatedApiContextStore? attended, ApiServiceCredentialStore? service, IHttpClientFactory http,
    TimeProvider? clock = null, ILogger<ExecutionCredentialResolver>? logger = null)
{
    public const string TokenClientName = "api-service-token";
    public const int MaxTokenResponseBytes = 64 * 1024;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private static readonly ConcurrentDictionary<string, (string Token, DateTimeOffset ExpiresAt)> Tokens = new();

    public sealed record Prepared(string Mode, string Reference, string? Problem);

    public static string Label(ExecutionAuthenticationKind kind) => kind switch
    {
        ExecutionAuthenticationKind.AttendedSession => "Attended: the tester's own session (Local HTTPS proxy)",
        ExecutionAuthenticationKind.ClientCredentials => "Unattended: OAuth2 client credentials",
        ExecutionAuthenticationKind.ApiKey => "Unattended: API key",
        _ => "None",
    };

    public static ExecutionAuthenticationKind Effective(ExecutionRunRequest r) =>
        r.Authentication != ExecutionAuthenticationKind.None ? r.Authentication : r.UseTargetAuthentication ? ExecutionAuthenticationKind.AttendedSession : ExecutionAuthenticationKind.None;

    /// <summary>Whether the run's credential can be used for <paramref name="origin"/> now (null problem = usable). Checked before anything is sent.</summary>
    public Prepared Prepare(ExecutionRunRequest r, Uri origin)
    {
        var kind = Effective(r);
        switch (kind)
        {
            case ExecutionAuthenticationKind.None: return new("None", "none", null);
            case ExecutionAuthenticationKind.AttendedSession:
            {
                var reference = $"attended-session:{r.EnvironmentId}";
                if (attended is null) return new(Label(kind), reference, "The Local HTTPS proxy credential store is not available in this BirkNext instance.");
                if (r.ProfileId is not { Length: > 0 } profile || r.ContextFingerprint is not { Length: > 0 } fingerprint)
                    return new(Label(kind), reference, "The Target Environment's proxy identity was not sent with the run.");
                if (!string.Equals(profile, r.EnvironmentId, StringComparison.Ordinal))
                    return new(Label(kind), reference, "The attended session belongs to another Target Environment than this run.");
                if (attended.Describe(profile, fingerprint) is null)
                    return new(Label(kind), reference, "No attended session: sign in to the application through the Local HTTPS proxy, then run again.");
                return ((ITransientCredentialSink)attended).ScopeOf(profile, fingerprint)?.ContainsUri(origin) == true
                    ? new(Label(kind), reference, null)
                    : new(Label(kind), reference, "The attended session covers only HTTPS hosts approved for this Target Environment; this target is not one of them.");
            }
            default:
            {
                var view = service?.Get(r.EnvironmentId);
                var reference = view is null ? $"api-credential:{r.EnvironmentId}:{kind}:none" : view.Reference;
                if (view is null) return new(Label(kind), reference, "No unattended API credential is configured for this Target Environment.");
                if (view.Kind != kind) return new(Label(kind), reference, $"The configured unattended credential is {Label(view.Kind)}, not {Label(kind)}.");
                if (origin.Scheme != Uri.UriSchemeHttps && !(origin.IsLoopback && string.Equals(r.EnvironmentType, "Local", StringComparison.OrdinalIgnoreCase)))
                    return new(Label(kind), reference, "Unattended credentials are sent only over HTTPS (plain HTTP only to a loopback target of a Local environment).");
                return new(Label(kind), reference, null);
            }
        }
    }

    /// <summary>Applies the run's credential to one attempt. Returns the header names set (for redaction) or the reason it could not be applied.</summary>
    public async Task<(IReadOnlyList<string> Headers, string? Problem)> ApplyAsync(ExecutionRunRequest r, HttpRequestMessage message, CancellationToken ct)
    {
        var kind = Effective(r);
        if (kind == ExecutionAuthenticationKind.None) return ([], null);
        if (Prepare(r, new Uri(message.RequestUri!.GetLeftPart(UriPartial.Authority))) is { Problem: { } problem }) return ([], problem);
        switch (kind)
        {
            case ExecutionAuthenticationKind.AttendedSession:
                return ((ITransientCredentialSink)attended!).TryApply(r.ProfileId!, r.ContextFingerprint!, message)
                    ? (["Authorization"], null) : ([], "The attended session expired or no longer covers this host.");
            case ExecutionAuthenticationKind.ApiKey:
            {
                var view = service!.Get(r.EnvironmentId)!;
                if (service.ReadSecret(r.EnvironmentId) is not { } key) return ([], "The API key cannot be decrypted on this machine; save it again.");
                message.Headers.TryAddWithoutValidation(view.HeaderName!, key);
                return ([view.HeaderName!], null);
            }
            default:
            {
                var (token, tokenProblem) = await TokenAsync(r.EnvironmentId, ct);
                if (token is null) return ([], tokenProblem);
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                return (["Authorization"], null);
            }
        }
    }

    /// <summary>OAuth2 client-credentials token for the environment (RFC 6749 § 4.4), memory-only, reused until 30 s before expiry.</summary>
    private async Task<(string? Token, string? Problem)> TokenAsync(string environmentId, CancellationToken ct)
    {
        var view = service!.Get(environmentId)!;
        var cacheKey = view.Reference;
        if (Tokens.TryGetValue(cacheKey, out var cached) && cached.ExpiresAt > _clock.GetUtcNow().AddSeconds(30)) return (cached.Token, null);
        if (ApiServiceCredentialStore.TokenEndpointProblem(view.TokenUrl) is { } endpointProblem) return (null, endpointProblem);
        if (service.ReadSecret(environmentId) is not { } secret) return (null, "The client secret cannot be decrypted on this machine; save it again.");
        var form = new Dictionary<string, string> { ["grant_type"] = "client_credentials", ["client_id"] = view.ClientId!, ["client_secret"] = secret };
        if (!string.IsNullOrWhiteSpace(view.Scope)) form["scope"] = view.Scope;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            using var response = await http.CreateClient(TokenClientName).PostAsync(view.TokenUrl, new FormUrlEncodedContent(form), timeout.Token);
            if (!response.IsSuccessStatusCode) return (null, $"The token endpoint refused the client credentials (HTTP {(int)response.StatusCode}).");
            // A token response is small: read at most MaxTokenResponseBytes, never an unbounded body.
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            var buffer = new byte[MaxTokenResponseBytes + 1];
            var read = 0;
            for (int n; read < buffer.Length && (n = await stream.ReadAsync(buffer.AsMemory(read), timeout.Token)) > 0;) read += n;
            if (read > MaxTokenResponseBytes) return (null, $"The token endpoint's answer is larger than {MaxTokenResponseBytes / 1024} KB.");
            using var doc = JsonDocument.Parse(buffer.AsMemory(0, read), new JsonDocumentOptions { MaxDepth = 16 });
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("access_token", out var at) || at.ValueKind != JsonValueKind.String
                || at.GetString() is not { Length: > 0 } token)
                return (null, "The token endpoint answered without an access_token.");
            if (token.Length > 16 * 1024 || token.Any(c => c is < '!' or > '~'))
                return (null, "The token endpoint's access_token is not a usable bearer token.");
            var lifetime = doc.RootElement.TryGetProperty("expires_in", out var ei) && ei.TryGetInt32(out var seconds) ? Math.Clamp(seconds, 1, 86400) : 300;
            Tokens[cacheKey] = (token, _clock.GetUtcNow().AddSeconds(lifetime));
            logger?.LogInformation("Unattended API token obtained for {Reference}; valid {Seconds} s.", cacheKey, lifetime);
            return (token, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        { return (null, $"The token endpoint could not be reached or answered unreadably ({ex.GetType().Name})."); }
    }

    /// <summary>Forgets cached tokens of an environment (after its credential changes or is removed).</summary>
    public static void Forget(string environmentId)
    {
        foreach (var key in Tokens.Keys.Where(k => k.StartsWith($"api-credential:{environmentId}:", StringComparison.Ordinal))) Tokens.TryRemove(key, out _);
    }
}
