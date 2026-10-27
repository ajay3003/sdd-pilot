using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BirkNext.Api.Services.Integrations;
using BirkNext.Integrations;

namespace BirkNext.Api.Services.AltinnTesting;

public sealed class AltinnSignInException(string message) : Exception(message);

/// <summary>Refuses any request outside the Altinn testing policy, whatever code built it. Applied to the one named client used here.</summary>
public sealed class AltinnTestingRequestGuard(AltinnTestingPolicy policy) : DelegatingHandler
{
    public const string ClientName = "altinn-testing";

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        request.RequestUri is { } uri && policy.RequestBlock(uri) is null
            ? base.SendAsync(request, cancellationToken)
            : throw new AltinnSignInException($"Blocked request: {(request.RequestUri is { } blocked ? policy.RequestBlock(blocked) : "no URL")}");
}

public interface IAltinnTestTokens
{
    /// <summary>The Altinn TT02 token of this person for this environment, or null when they must sign in (again). Never logged or persisted.</summary>
    string? Get(string initiator, string environmentId);
}

/// <summary>Process memory only: pending sign-ins (single-use state → PKCE verifier) and held Altinn tokens per person and environment.</summary>
public sealed class AltinnTestSessionCache(TimeProvider clock) : IAltinnTestTokens
{
    internal sealed record Pending(string Initiator, string EnvironmentId, string Verifier, Uri TokenEndpoint, string ClientId, DateTimeOffset Created);
    internal sealed record Held(string AltinnToken, DateTimeOffset ExpiresAt);

    internal static readonly TimeSpan PendingLifetime = TimeSpan.FromMinutes(10);
    internal ConcurrentDictionary<string, Pending> PendingByState { get; } = new(StringComparer.Ordinal);
    internal ConcurrentDictionary<(string Initiator, string EnvironmentId), Held> Tokens { get; } = new();
    /// <summary>Unattended reporter tokens, keyed by environment and synthetic reporter alias — never by child, execution context or across reporters.</summary>
    internal ConcurrentDictionary<(string EnvironmentId, string Alias), Held> UnattendedTokens { get; } = new();

    internal Held? Unattended((string EnvironmentId, string Alias) key) =>
        UnattendedTokens.TryGetValue(key, out var held) && held.ExpiresAt > clock.GetUtcNow() ? held : null;

    internal void HoldUnattended((string EnvironmentId, string Alias) key, string altinnToken, DateTimeOffset expiresAt)
    {
        Prune();
        UnattendedTokens[key] = new Held(altinnToken, expiresAt);
    }

    public string? Get(string initiator, string environmentId) =>
        Tokens.TryGetValue((initiator, environmentId), out var held) && held.ExpiresAt > clock.GetUtcNow() ? held.AltinnToken : null;

    public DateTimeOffset? ExpiresAt(string initiator, string environmentId) =>
        Tokens.TryGetValue((initiator, environmentId), out var held) && held.ExpiresAt > clock.GetUtcNow() ? held.ExpiresAt : null;

    internal void Prune()
    {
        var now = clock.GetUtcNow();
        foreach (var (state, pending) in PendingByState) if (now - pending.Created > PendingLifetime) PendingByState.TryRemove(state, out _);
        foreach (var (key, held) in Tokens) if (held.ExpiresAt <= now) Tokens.TryRemove(key, out _);
        foreach (var (key, held) in UnattendedTokens) if (held.ExpiresAt <= now) UnattendedTokens.TryRemove(key, out _);
    }
}

/// <summary>
/// ID-porten TEST sign-in for Altinn TT02, done by the tester in their own browser (TestID): BirkNext is an OIDC client using the
/// authorization-code flow with PKCE. The authorize URL is built only from ID-porten TEST's discovery document (issuer pinned, hosts
/// allowlisted). The code is exchanged server-side, the ID-porten access token is exchanged for an Altinn token, and only that token is kept —
/// in memory, per BirkNext execution context and environment, until it expires or the person signs out. This is the attended fallback: no
/// login_hint is sent, so the tester picks or enters the synthetic person in TestID themselves. Unattended runs use the documented headless
/// TestID login instead (<see cref="UnattendedIdPortenTestAuthentication"/>). Codes, tokens and claims are never logged, persisted or returned.
/// </summary>
public sealed class IdPortenTestSignIn(
    IHttpClientFactory http,
    AltinnTestingPolicy policy,
    IIntegrationMessageFlowStore messageFlows,
    AltinnTestSessionCache sessions,
    TimeProvider clock,
    ILogger<IdPortenTestSignIn> logger)
{
    private static readonly TimeSpan MaxTokenLifetime = TimeSpan.FromMinutes(30);

    public async Task<AltinnSignInStatus> StatusAsync(string initiator, string environmentId, CancellationToken ct)
    {
        var config = policy.Effective((await messageFlows.GetAsync(environmentId, ct)).AltinnConfiguration, environmentId);
        var problems = Problems(config);
        var expires = sessions.ExpiresAt(initiator, environmentId);
        return new AltinnSignInStatus
        {
            Configured = problems.Count == 0, ConfigurationProblems = problems, SignedIn = expires is not null, ExpiresAt = expires,
            Detail = expires is not null ? "Signed in to ID-porten TEST; an Altinn TT02 token is held in backend memory for you and this environment."
                : problems.Count > 0 ? "ID-porten TEST sign-in is not configured." : "Not signed in. Sign in with TestID before running an Altinn submission.",
        };
    }

    public async Task<AltinnSignInStart> StartAsync(string initiator, string environmentId, CancellationToken ct)
    {
        var config = policy.Effective((await messageFlows.GetAsync(environmentId, ct)).AltinnConfiguration, environmentId);
        if (Problems(config) is { Count: > 0 } problems) throw new AltinnSignInException(string.Join(" ", problems));
        var discovery = await DiscoverAsync(ct);
        var secret = policy.Options.IdPortenTest.ClientSecret;
        var method = !string.IsNullOrEmpty(secret) ? "client_secret_post" : "none";
        if (!discovery.AuthMethods.Contains(method, StringComparer.Ordinal))
            throw new AltinnSignInException($"ID-porten TEST does not advertise the client authentication method {method}.");

        sessions.Prune();
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(48));
        var state = Base64Url(RandomNumberGenerator.GetBytes(32));
        var nonce = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var created = clock.GetUtcNow();
        sessions.PendingByState[state] = new AltinnTestSessionCache.Pending(initiator, environmentId, verifier, discovery.TokenEndpoint, policy.ClientId(config, out _, out _)!, created);

        var query = new List<KeyValuePair<string, string>>
        {
            new("response_type", "code"), new("client_id", policy.ClientId(config, out _, out _)!), new("redirect_uri", policy.Options.IdPortenTest.RedirectUri!),
            new("scope", string.Join(' ', policy.Scopes(config))),
            new("state", state), new("nonce", nonce), new("code_challenge", challenge), new("code_challenge_method", "S256"),
        };
        // form_post keeps the authorization code out of URLs (and so out of browser history, proxies and request logs) when ID-porten offers it.
        if (discovery.ResponseModes.Contains("form_post", StringComparer.Ordinal)) query.Add(new("response_mode", "form_post"));
        if (!string.IsNullOrWhiteSpace(policy.Options.IdPortenTest.AcrValues)) query.Add(new("acr_values", policy.Options.IdPortenTest.AcrValues.Trim()));
        var url = $"{discovery.AuthorizationEndpoint.GetLeftPart(UriPartial.Path)}?{string.Join('&', query.Select(item => $"{Uri.EscapeDataString(item.Key)}={Uri.EscapeDataString(item.Value)}"))}";
        return new AltinnSignInStart(url, created + AltinnTestSessionCache.PendingLifetime);
    }

    /// <summary>Completes the browser redirect. The state is single-use and binds the result to the person who started the sign-in.</summary>
    public async Task<string> CompleteAsync(string? code, string? state, string? error, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(state) || !sessions.PendingByState.TryRemove(state, out var pending) || clock.GetUtcNow() - pending.Created > AltinnTestSessionCache.PendingLifetime)
            throw new AltinnSignInException("This sign-in response is unknown or expired. Start the sign-in again from BirkNext.");
        if (!string.IsNullOrEmpty(error)) throw new AltinnSignInException("ID-porten TEST did not complete the sign-in.");
        if (string.IsNullOrEmpty(code)) throw new AltinnSignInException("ID-porten TEST returned no authorization code.");

        var client = http.CreateClient(AltinnTestingRequestGuard.ClientName);
        var form = new List<KeyValuePair<string, string>>
        {
            new("grant_type", "authorization_code"), new("code", code), new("redirect_uri", policy.Options.IdPortenTest.RedirectUri!),
            new("client_id", pending.ClientId), new("code_verifier", pending.Verifier),
        };
        if (!string.IsNullOrEmpty(policy.Options.IdPortenTest.ClientSecret)) form.Add(new("client_secret", policy.Options.IdPortenTest.ClientSecret));
        string idPortenToken;
        int expiresIn;
        using (var response = await client.PostAsync(pending.TokenEndpoint, new FormUrlEncodedContent(form), ct))
        {
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("ID-porten TEST token request failed with HTTP {Status}.", (int)response.StatusCode);
                throw new AltinnSignInException($"ID-porten TEST refused the token request (HTTP {(int)response.StatusCode}).");
            }
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            idPortenToken = json.RootElement.TryGetProperty("access_token", out var token) && token.GetString() is { Length: > 0 } value ? value
                : throw new AltinnSignInException("ID-porten TEST returned no access token.");
            expiresIn = json.RootElement.TryGetProperty("expires_in", out var expires) && expires.TryGetInt32(out var seconds) ? seconds : 120;
        }

        using var exchange = new HttpRequestMessage(HttpMethod.Get, policy.Endpoints.TokenExchange);
        exchange.Headers.Authorization = new AuthenticationHeaderValue("Bearer", idPortenToken);
        using var exchanged = await client.SendAsync(exchange, ct);
        if (!exchanged.IsSuccessStatusCode)
        {
            logger.LogWarning("Altinn TT02 token exchange failed with HTTP {Status}.", (int)exchanged.StatusCode);
            throw new AltinnSignInException($"Altinn TT02 refused the ID-porten TEST token exchange (HTTP {(int)exchanged.StatusCode}).");
        }
        var altinnToken = (await exchanged.Content.ReadAsStringAsync(ct)).Trim().Trim('"');
        if (altinnToken.Length == 0) throw new AltinnSignInException("Altinn TT02 returned no token.");
        var lifetime = TimeSpan.FromSeconds(Math.Clamp(expiresIn, 30, (int)MaxTokenLifetime.TotalSeconds));
        sessions.Tokens[(pending.Initiator, pending.EnvironmentId)] = new AltinnTestSessionCache.Held(altinnToken, clock.GetUtcNow() + lifetime);
        logger.LogInformation("ID-porten TEST sign-in completed for environment {EnvironmentId}.", pending.EnvironmentId);
        return pending.EnvironmentId;
    }

    public void SignOut(string initiator, string environmentId) => sessions.Tokens.TryRemove((initiator, environmentId), out _);

    private IReadOnlyList<string> Problems(AltinnTestConfiguration config) =>
        [.. AltinnTestingPolicy.AltinnEnvironmentBlock(config) is { } environment ? [environment] : Array.Empty<string>(), .. policy.SignInProblems(config)];

    private async Task<IdPortenTestDiscovery.Document> DiscoverAsync(CancellationToken ct) =>
        await IdPortenTestDiscovery.ReadAsync(http.CreateClient(AltinnTestingRequestGuard.ClientName), ct);

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
