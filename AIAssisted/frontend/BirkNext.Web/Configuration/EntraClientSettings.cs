using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Configuration;
using System.Security.Claims;

namespace BirkNext.Web.Configuration;

/// <summary>
/// Browser-side Entra settings (<c>Authentication:Entra</c> in wwwroot/appsettings.json, supplied by deployment): TenantId, ClientId,
/// optional Authority (defaults to the tenant's v2 authority), the BirkNext API scopes (ActiveEventApiScope, optional ConfigurationApiScope),
/// optional RedirectUri / PostLogoutRedirectUri. Configured only when tenant, client and the Active Event scope are all present.
/// </summary>
public sealed record EntraClientSettings(bool Configured, string Authority, string ClientId, IReadOnlyList<string> ApiScopes, string? RedirectUri, string? PostLogoutRedirectUri)
{
    public static EntraClientSettings From(IConfiguration configuration)
    {
        string? Value(string key) => configuration[$"Authentication:Entra:{key}"] is { } value && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
        var tenant = Value("TenantId");
        var client = Value("ClientId");
        var activeEventScope = Value("ActiveEventApiScope");
        var scopes = new[] { activeEventScope, Value("ConfigurationApiScope") }.OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
        var configured = tenant is not null && client is not null && activeEventScope is not null;
        return new EntraClientSettings(configured, Value("Authority") ?? (tenant is null ? "" : $"https://login.microsoftonline.com/{tenant}"), client ?? "", scopes,
            Value("RedirectUri"), Value("PostLogoutRedirectUri"));
    }
}

/// <summary>The authentication state when Entra is not configured: always anonymous. Nothing can request a token.</summary>
public sealed class AnonymousAuthenticationStateProvider : AuthenticationStateProvider
{
    private static readonly Task<AuthenticationState> Anonymous = Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity())));
    public override Task<AuthenticationState> GetAuthenticationStateAsync() => Anonymous;
}
