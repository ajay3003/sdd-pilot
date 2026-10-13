using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BirkNext.Integrations;

namespace BirkNext.Api.Services.AltinnTesting;

/// <summary>Where reporter authentication stopped. Each stage is reported on its own; an ID-porten failure is never an Altinn failure.</summary>
public enum AltinnAuthenticationStage
{
    Configuration,
    IdPortenDiscovery,
    IdPortenAuthorization,
    IdPortenRedirect,
    IdPortenToken,
    IdPortenTokenValidation,
    AltinnExchange,
    Authenticated,
}

/// <summary>What a run asks for: an Altinn TT02 token for this environment and reporter. <paramref name="Actor"/> is the BirkNext execution
/// context (used only by the interactive fallback); <paramref name="Reporter"/> is an approved alias or the interactive fallback value.</summary>
public sealed record AltinnAuthenticationRequest(string EnvironmentId, string? Actor, string Reporter, AltinnTestConfiguration Configuration);

/// <summary>
/// The outcome of reporter authentication. The token is held only in memory for the run and is deliberately not part of any printed or
/// serialized representation (no record members, no ToString).
/// </summary>
public sealed class AltinnAuthenticationResult
{
    private AltinnAuthenticationResult(bool success, AltinnAuthenticationStage stage, string detail, string mode, string? token, DateTimeOffset? expiresAt)
    {
        Success = success; Stage = stage; Detail = detail; Mode = mode; Token = token; ExpiresAt = expiresAt;
    }

    public bool Success { get; }
    public AltinnAuthenticationStage Stage { get; }
    public string Detail { get; }
    /// <summary>"unattended" or "interactive".</summary>
    public string Mode { get; }
    internal string? Token { get; }
    public DateTimeOffset? ExpiresAt { get; }

    public static AltinnAuthenticationResult Failed(string mode, AltinnAuthenticationStage stage, string detail) => new(false, stage, detail, mode, null, null);
    internal static AltinnAuthenticationResult Authenticated(string mode, string token, DateTimeOffset expiresAt, string detail) =>
        new(true, AltinnAuthenticationStage.Authenticated, detail, mode, token, expiresAt);

    public override string ToString() => $"{Mode} {Stage}: {Detail}";

    public static string StageLabel(AltinnAuthenticationStage stage) => stage switch
    {
        AltinnAuthenticationStage.Configuration => "Authentication configuration",
        AltinnAuthenticationStage.IdPortenDiscovery => "ID-porten TEST discovery",
        AltinnAuthenticationStage.IdPortenAuthorization => "ID-porten TEST authorization",
        AltinnAuthenticationStage.IdPortenRedirect => "ID-porten TEST authorization redirect",
        AltinnAuthenticationStage.IdPortenToken => "ID-porten TEST token request",
        AltinnAuthenticationStage.IdPortenTokenValidation => "ID-porten TEST token validation",
        AltinnAuthenticationStage.AltinnExchange => "Altinn TT02 token exchange",
        _ => "Authenticated",
    };
}

/// <summary>One way to obtain the Altinn TT02 token a run uses. Owns authentication only — never instances, forms or journeys.</summary>
public interface IAltinnTestAuthenticationProvider
{
    string Mode { get; }
    bool Handles(string reporter);
    Task<AltinnAuthenticationResult> AuthenticateAsync(AltinnAuthenticationRequest request, CancellationToken ct);
}

/// <summary>Chooses the provider for the run's reporter. The one entry point the Altinn executor and the Storage observer use.</summary>
public sealed class AltinnTestAuthenticator(IEnumerable<IAltinnTestAuthenticationProvider> providers)
{
    public Task<AltinnAuthenticationResult> AuthenticateAsync(AltinnAuthenticationRequest request, CancellationToken ct) =>
        providers.FirstOrDefault(provider => provider.Handles(request.Reporter)) is { } provider
            ? provider.AuthenticateAsync(request, ct)
            : Task.FromResult(AltinnAuthenticationResult.Failed("none", AltinnAuthenticationStage.Configuration, "No authentication provider handles the selected reporter."));
}

/// <summary>The attended fallback: the TestID sign-in the tester completed in their own browser, held for their BirkNext execution context.</summary>
public sealed class InteractiveIdPortenTestAuthentication(IAltinnTestTokens tokens, TimeProvider clock) : IAltinnTestAuthenticationProvider
{
    public string Mode => "interactive";
    public bool Handles(string reporter) => reporter == AltinnTestingPolicy.InteractiveReporter;

    public Task<AltinnAuthenticationResult> AuthenticateAsync(AltinnAuthenticationRequest request, CancellationToken ct) =>
        Task.FromResult(request.Actor is { Length: > 0 } actor && tokens.Get(actor, request.EnvironmentId) is { } token
            ? AltinnAuthenticationResult.Authenticated(Mode, token, clock.GetUtcNow().AddMinutes(1), "Interactive fallback: the tester's own ID-porten TEST sign-in.")
            : AltinnAuthenticationResult.Failed(Mode, AltinnAuthenticationStage.Configuration,
                "Not signed in to ID-porten TEST for this environment (interactive fallback: Skolenærvær Testing → Rapportmottak → Alternative sign-in)."));
}

/// <summary>ID-porten TEST discovery, pinned: the issuer must be ID-porten TEST and every endpoint an allowlisted ID-porten TEST host.</summary>
internal static class IdPortenTestDiscovery
{
    internal sealed record Document(Uri AuthorizationEndpoint, Uri TokenEndpoint, IReadOnlyList<string> AuthMethods, IReadOnlyList<string> ResponseModes);

    public static async Task<Document> ReadAsync(HttpClient client, CancellationToken ct)
    {
        using var response = await client.GetAsync(AltinnTestingPolicy.IdPortenTestDiscovery, ct);
        if (!response.IsSuccessStatusCode) throw new AltinnSignInException($"ID-porten TEST discovery is unavailable (HTTP {(int)response.StatusCode}).");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = json.RootElement;
        if (!string.Equals(root.TryGetProperty("issuer", out var issuer) ? issuer.GetString()?.TrimEnd('/') : null, AltinnTestingPolicy.IdPortenTestIssuer, StringComparison.Ordinal))
            throw new AltinnSignInException("The discovery document is not ID-porten TEST's (unexpected issuer).");
        Uri Endpoint(string name) => root.TryGetProperty(name, out var value) && Uri.TryCreate(value.GetString(), UriKind.Absolute, out var uri) && AltinnTestingPolicy.IsIdPortenTestHost(uri)
            ? uri : throw new AltinnSignInException($"ID-porten TEST discovery has no approved {name}.");
        string[] Strings(string name) => root.TryGetProperty(name, out var values) && values.ValueKind == JsonValueKind.Array
            ? values.EnumerateArray().Select(item => item.GetString() ?? "").ToArray() : [];
        return new Document(Endpoint("authorization_endpoint"), Endpoint("token_endpoint"), Strings("token_endpoint_auth_methods_supported"), Strings("response_modes_supported"));
    }
}

/// <summary>
/// Unattended reporter authentication through ID-porten TEST's documented headless login for API testing
/// (docs.digdir.no/docs/idporten/oidc/oidc_func_apitesting): an ordinary authorization-code request with PKCE (S256), state, nonce,
/// acr_values and <c>login_hint=testid:&lt;synthetic identifier&gt;_&lt;security level&gt;</c>, sent server-side with redirects disabled. ID-porten
/// answers 302 with the code in <c>Location</c>; the location is checked against the registered redirect URI, state and issuer and is never
/// followed. The code is exchanged once at the token endpoint with client_secret_basic; the id_token is checked (issuer, audience, nonce,
/// amr TestID, the configured synthetic person) and the access token is exchanged for an Altinn TT02 token. No browser, form, TestID page or
/// copied token is involved. The Altinn token is held in process memory per (environment, reporter alias) until it expires; the ID-porten
/// tokens, code, verifier and login_hint are never stored, logged or returned.
/// </summary>
public sealed class UnattendedIdPortenTestAuthentication(IHttpClientFactory http, AltinnTestingPolicy policy, AltinnTestSessionCache cache, TimeProvider clock,
    ILogger<UnattendedIdPortenTestAuthentication> logger) : IAltinnTestAuthenticationProvider
{
    private static readonly TimeSpan MaxTokenLifetime = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan ExpirySafety = TimeSpan.FromSeconds(10);
    private readonly ConcurrentDictionary<(string Environment, string Alias), SemaphoreSlim> _locks = new();

    public string Mode => "unattended";
    public bool Handles(string reporter) => reporter != AltinnTestingPolicy.InteractiveReporter;

    public async Task<AltinnAuthenticationResult> AuthenticateAsync(AltinnAuthenticationRequest request, CancellationToken ct)
    {
        var config = request.Configuration;
        if (AltinnTestingPolicy.AltinnEnvironmentBlock(config) is { } environment) return Fail(AltinnAuthenticationStage.Configuration, environment);
        if (policy.UnattendedProblems(config) is { Count: > 0 } problems) return Fail(AltinnAuthenticationStage.Configuration, string.Join(" ", problems));
        if (policy.Reporter(request.Reporter, out var reporterReason) is not { } reporter) return Fail(AltinnAuthenticationStage.Configuration, reporterReason);

        var key = (request.EnvironmentId, reporter.Alias);
        if (cache.Unattended(key) is { } held) return AltinnAuthenticationResult.Authenticated(Mode, held.AltinnToken, held.ExpiresAt, Authenticated(reporter, reused: true));
        var gate = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (cache.Unattended(key) is { } again) return AltinnAuthenticationResult.Authenticated(Mode, again.AltinnToken, again.ExpiresAt, Authenticated(reporter, reused: true));
            return await AcquireAsync(request, reporter, key, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (AltinnSignInException ex) { return Fail(AltinnAuthenticationStage.IdPortenAuthorization, ex.Message); }
        catch (HttpRequestException) { return Fail(AltinnAuthenticationStage.IdPortenAuthorization, "ID-porten TEST or Altinn TT02 could not be reached; nothing further was sent."); }
        catch (TaskCanceledException) { return Fail(AltinnAuthenticationStage.IdPortenAuthorization, "ID-porten TEST or Altinn TT02 did not answer in time; nothing further was sent."); }
        finally { gate.Release(); }
    }

    private async Task<AltinnAuthenticationResult> AcquireAsync(AltinnAuthenticationRequest request, SyntheticReporterOptions reporter, (string, string) key, CancellationToken ct)
    {
        var client = http.CreateClient(AltinnTestingRequestGuard.ClientName);
        var config = request.Configuration;
        var clientId = policy.ClientId(config, out _, out _)!;
        var redirect = policy.UnattendedRedirect()!;
        var secret = policy.UnattendedClientSecret!;

        IdPortenTestDiscovery.Document discovery;
        try { discovery = await IdPortenTestDiscovery.ReadAsync(client, ct); }
        catch (AltinnSignInException ex) { return Fail(AltinnAuthenticationStage.IdPortenDiscovery, ex.Message); }
        catch (JsonException) { return Fail(AltinnAuthenticationStage.IdPortenDiscovery, "ID-porten TEST discovery is not readable JSON."); }
        if (!discovery.AuthMethods.Contains("client_secret_basic", StringComparer.Ordinal))
            return Fail(AltinnAuthenticationStage.IdPortenDiscovery, "ID-porten TEST does not advertise client_secret_basic, the client authentication of the documented headless flow.");

        // 1. Authorization request (headless): state, nonce and PKCE are fresh, single-use and never leave this method except to ID-porten.
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(48));
        var state = Base64Url(RandomNumberGenerator.GetBytes(32));
        var nonce = Base64Url(RandomNumberGenerator.GetBytes(32));
        var query = new List<KeyValuePair<string, string>>
        {
            new("response_type", "code"), new("client_id", clientId), new("redirect_uri", redirect.ToString()),
            new("scope", string.Join(' ', config.ConfiguredScopes.Select(item => item.Trim()).Where(item => item.Length > 0).Distinct(StringComparer.Ordinal))),
            new("state", state), new("nonce", nonce), new("acr_values", reporter.AcrValue),
            new("code_challenge", Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))), new("code_challenge_method", "S256"),
            new("login_hint", $"testid:{reporter.TestIdPersonIdentifier!.Trim()}_{reporter.AcrValue}"),
        };
        var authorize = new Uri($"{discovery.AuthorizationEndpoint.GetLeftPart(UriPartial.Path)}?{string.Join('&', query.Select(item => $"{Uri.EscapeDataString(item.Key)}={Uri.EscapeDataString(item.Value)}"))}");
        Uri location;
        using (var response = await client.GetAsync(authorize, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            if (response.StatusCode is not (HttpStatusCode.Found or HttpStatusCode.SeeOther or HttpStatusCode.Redirect or HttpStatusCode.TemporaryRedirect))
            {
                logger.LogWarning("ID-porten TEST headless authorization answered HTTP {Status} instead of a redirect.", (int)response.StatusCode);
                return Fail(AltinnAuthenticationStage.IdPortenAuthorization, $"ID-porten TEST did not authenticate the synthetic reporter (HTTP {(int)response.StatusCode}, no redirect). Check the client registration, scopes, the reporter's TestID identifier and the security level.");
            }
            if (response.Headers.Location is not { } raw) return Fail(AltinnAuthenticationStage.IdPortenRedirect, "ID-porten TEST redirected without a location.");
            location = raw.IsAbsoluteUri ? raw : new Uri(discovery.AuthorizationEndpoint, raw);
        }

        // 2. The redirect is read, never followed: exact registered redirect URI, matching state, ID-porten TEST issuer.
        if (!SameEndpoint(location, redirect))
            return Fail(AltinnAuthenticationStage.IdPortenRedirect, "ID-porten TEST redirected somewhere other than the registered redirect URI; it was not followed.");
        var parameters = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(location.Query);
        string? Param(string name) => parameters.TryGetValue(name, out var value) && value.Count == 1 ? value.ToString() : null;
        if (Param("error") is { } error)
            return Fail(AltinnAuthenticationStage.IdPortenRedirect, $"ID-porten TEST refused the headless authorization ({SafeCode(error)}).");
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Param("state") ?? ""), Encoding.ASCII.GetBytes(state)))
            return Fail(AltinnAuthenticationStage.IdPortenRedirect, "The authorization response's state does not match the request; the code was not used.");
        if (Param("iss") is { } iss && !string.Equals(iss.TrimEnd('/'), AltinnTestingPolicy.IdPortenTestIssuer, StringComparison.Ordinal))
            return Fail(AltinnAuthenticationStage.IdPortenRedirect, "The authorization response names another issuer than ID-porten TEST; the code was not used.");
        if (Param("code") is not { Length: > 0 } code) return Fail(AltinnAuthenticationStage.IdPortenRedirect, "ID-porten TEST returned no authorization code.");

        // 3. Token request: the code is exchanged exactly once, with client_secret_basic and the PKCE verifier.
        string accessToken;
        int expiresIn;
        using (var tokenRequest = new HttpRequestMessage(HttpMethod.Post, discovery.TokenEndpoint))
        {
            tokenRequest.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Uri.EscapeDataString(clientId)}:{Uri.EscapeDataString(secret)}")));
            tokenRequest.Content = new FormUrlEncodedContent(
            [
                new("grant_type", "authorization_code"), new("code", code), new("redirect_uri", redirect.ToString()),
                new("client_id", clientId), new("code_verifier", verifier),
            ]);
            using var response = await client.SendAsync(tokenRequest, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("ID-porten TEST token request failed with HTTP {Status}.", (int)response.StatusCode);
                return Fail(AltinnAuthenticationStage.IdPortenToken, $"ID-porten TEST refused the token request (HTTP {(int)response.StatusCode}).");
            }
            JsonDocument json;
            try { json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)); }
            catch (JsonException) { return Fail(AltinnAuthenticationStage.IdPortenToken, "ID-porten TEST answered the token request with an unreadable document."); }
            using (json)
            {
                var root = json.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("access_token", out var access) || access.GetString() is not { Length: > 0 } accessValue)
                    return Fail(AltinnAuthenticationStage.IdPortenToken, "ID-porten TEST returned no access token.");
                if (root.TryGetProperty("token_type", out var type) && !string.Equals(type.GetString(), "Bearer", StringComparison.OrdinalIgnoreCase))
                    return Fail(AltinnAuthenticationStage.IdPortenToken, "ID-porten TEST returned a token that is not a bearer token.");
                if (!root.TryGetProperty("expires_in", out var expires) || !expires.TryGetInt32(out expiresIn) || expiresIn <= 0)
                    return Fail(AltinnAuthenticationStage.IdPortenToken, "ID-porten TEST returned no usable token lifetime (expires_in).");
                if (IdTokenProblem(root.TryGetProperty("id_token", out var id) ? id.GetString() : null, clientId, nonce, reporter.TestIdPersonIdentifier!.Trim()) is { } problem)
                    return Fail(AltinnAuthenticationStage.IdPortenTokenValidation, problem);
                accessToken = accessValue;
            }
        }

        // 4. Altinn TT02 exchange: the ID-porten access token (not the id_token) for an Altinn token.
        using var exchange = new HttpRequestMessage(HttpMethod.Get, AltinnTestingPolicy.AltinnTokenExchange);
        exchange.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var exchanged = await client.SendAsync(exchange, ct);
        if (!exchanged.IsSuccessStatusCode)
        {
            logger.LogWarning("Altinn TT02 token exchange failed with HTTP {Status}.", (int)exchanged.StatusCode);
            return Fail(AltinnAuthenticationStage.AltinnExchange, exchanged.StatusCode == HttpStatusCode.Unauthorized
                ? "Altinn TT02 refused the ID-porten TEST token (HTTP 401). The synthetic reporter may have no Altinn profile in TT02 (Altinn requires one)."
                : $"Altinn TT02 refused the ID-porten TEST token exchange (HTTP {(int)exchanged.StatusCode}).");
        }
        var altinnToken = (await exchanged.Content.ReadAsStringAsync(ct)).Trim().Trim('"');
        if (altinnToken.Length == 0 || altinnToken.Any(char.IsWhiteSpace)) return Fail(AltinnAuthenticationStage.AltinnExchange, "Altinn TT02 returned no token.");

        var lifetime = TimeSpan.FromSeconds(Math.Min(expiresIn, MaxTokenLifetime.TotalSeconds)) - ExpirySafety;
        var expiresAt = clock.GetUtcNow() + (lifetime > TimeSpan.Zero ? lifetime : TimeSpan.Zero);
        cache.HoldUnattended(key, altinnToken, expiresAt);
        logger.LogInformation("Unattended ID-porten TEST authentication completed for reporter alias {Alias} in environment {EnvironmentId}.", reporter.Alias, request.EnvironmentId);
        return AltinnAuthenticationResult.Authenticated(Mode, altinnToken, expiresAt, Authenticated(reporter, reused: false));
    }

    /// <summary>
    /// The id_token came straight from the TEST token endpoint over TLS, so its claims are checked without a signature lookup: issuer, audience,
    /// nonce, amr=TestID and the configured synthetic person. A mismatch means the token is not the one this run asked for.
    /// </summary>
    internal static string? IdTokenProblem(string? idToken, string clientId, string nonce, string expectedPid)
    {
        var parts = idToken?.Split('.');
        if (parts is not { Length: 3 }) return "ID-porten TEST returned no id_token; the authenticated person cannot be checked.";
        JsonElement claims;
        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            using var json = JsonDocument.Parse(Convert.FromBase64String(payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=')));
            claims = json.RootElement.Clone();
        }
        catch (Exception ex) when (ex is FormatException or JsonException) { return "ID-porten TEST returned an unreadable id_token."; }
        static IEnumerable<string> Values(JsonElement claims, string name) => !claims.TryGetProperty(name, out var value) ? []
            : value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().Select(item => item.GetString() ?? "") : [value.GetString() ?? ""];
        if (!Values(claims, "iss").Select(item => item.TrimEnd('/')).Contains(AltinnTestingPolicy.IdPortenTestIssuer, StringComparer.Ordinal)) return "The id_token was not issued by ID-porten TEST.";
        if (!Values(claims, "aud").Contains(clientId, StringComparer.Ordinal)) return "The id_token is not addressed to this client.";
        if (!Values(claims, "nonce").Contains(nonce, StringComparer.Ordinal)) return "The id_token does not carry this request's nonce.";
        if (!Values(claims, "amr").Contains("TestID", StringComparer.Ordinal)) return "The id_token does not show a TestID authentication (amr=TestID); only synthetic TestID reporters are allowed.";
        if (!Values(claims, "pid").Contains(expectedPid, StringComparer.Ordinal)) return "The id_token names another person than the configured synthetic reporter.";
        return null;
    }

    private static bool SameEndpoint(Uri actual, Uri expected) =>
        actual.IsAbsoluteUri && actual.Scheme == expected.Scheme && string.Equals(actual.Host, expected.Host, StringComparison.OrdinalIgnoreCase)
        && actual.Port == expected.Port && actual.AbsolutePath == expected.AbsolutePath && string.IsNullOrEmpty(actual.UserInfo);

    /// <summary>An OAuth error code is a short token; anything else is not echoed.</summary>
    private static string SafeCode(string error) => error.Length <= 40 && error.All(c => char.IsAsciiLetterLower(c) || c == '_') ? error : "error";

    private static string Authenticated(SyntheticReporterOptions reporter, bool reused) =>
        $"Synthetic TEST reporter {reporter.Alias} authenticated unattended with ID-porten TEST ({reporter.AcrValue}); Altinn TT02 token {(reused ? "reused from backend memory" : "obtained")}.";

    private AltinnAuthenticationResult Fail(AltinnAuthenticationStage stage, string detail) => AltinnAuthenticationResult.Failed(Mode, stage, detail);

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
