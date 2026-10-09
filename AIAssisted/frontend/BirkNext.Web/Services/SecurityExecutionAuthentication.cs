using System.Net.Http.Headers;

namespace BirkNext.Web.Services;

/// <summary>
/// Configuration hooks for signing BirkNext users in before protected security execution (wwwroot/appsettings.json
/// <c>BirkNextAuthentication</c>). Values are deployment-specific and are never invented: empty means "not configured". A Blazor
/// WebAssembly app is a public client — there is deliberately no client secret field.
/// </summary>
public sealed class SecurityExecutionAuthOptions
{
    public const string SectionName = "BirkNextAuthentication";
    public string? TenantId { get; set; }
    public string? SpaClientId { get; set; }
    public string? ApiScope { get; set; }
    public string? Authority { get; set; }
    public string? RedirectUri { get; set; }
    public string? PostLogoutRedirectUri { get; set; }

    /// <summary>Tenant, SPA client id and API scope are all present.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(TenantId) && !string.IsNullOrWhiteSpace(SpaClientId) && !string.IsNullOrWhiteSpace(ApiScope);

    /// <summary>The authority to use: the explicit one, else the Entra authority derived from the tenant.</summary>
    public string? EffectiveAuthority => !string.IsNullOrWhiteSpace(Authority) ? Authority
        : !string.IsNullOrWhiteSpace(TenantId) ? $"https://login.microsoftonline.com/{TenantId}" : null;
}

/// <summary>Obtains a bearer token for protected security execution calls. Tokens stay in memory and are never written to storage.</summary>
public interface ISecurityExecutionTokenProvider
{
    bool IsConfigured { get; }
    Task<string?> GetAccessTokenAsync(CancellationToken ct = default);
}

/// <summary>
/// The default provider: no sign-in library is bundled with this build, so no token is available even when the hooks are filled in.
/// A deployment that enables user authentication replaces this registration with an MSAL-backed provider.
/// </summary>
public sealed class UnavailableSecurityExecutionTokenProvider(SecurityExecutionAuthOptions options) : ISecurityExecutionTokenProvider
{
    public bool IsConfigured => options.IsConfigured;
    public Task<string?> GetAccessTokenAsync(CancellationToken ct = default) => Task.FromResult<string?>(null);
}

/// <summary>
/// Attaches the BirkNext user's bearer token to protected security execution requests only (authorization scenarios and fuzzing runs),
/// when a provider returns one. Other API calls are untouched; nothing is persisted.
/// </summary>
public sealed class SecurityExecutionBearerHandler(ISecurityExecutionTokenProvider tokens) : DelegatingHandler
{
    private static readonly string[] ProtectedPaths = ["/api/api-quality/authorization/", "/api/api-quality/fuzzing/runs"];

    public static bool IsProtected(Uri? uri) => uri is not null && ProtectedPaths.Any(p => uri.AbsolutePath.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (IsProtected(request.RequestUri) && request.Headers.Authorization is null && tokens.IsConfigured
            && await tokens.GetAccessTokenAsync(cancellationToken) is { Length: > 0 } token)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await base.SendAsync(request, cancellationToken);
    }
}
