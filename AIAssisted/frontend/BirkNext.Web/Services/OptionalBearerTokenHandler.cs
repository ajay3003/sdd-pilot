using System.Net.Http.Headers;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;

namespace BirkNext.Web.Services;

/// <summary>
/// Attaches a BirkNext API access token to backend requests when one can be obtained without interaction; otherwise the request goes out
/// without one and the API's 401/403 is shown as-is ("Sign-in required" / "Not authorized"). It never redirects the page, so read-only
/// pages keep working for signed-out users, and it never caches or logs the token (the MSAL library owns token storage).
/// </summary>
public sealed class OptionalBearerTokenHandler(IAccessTokenProvider tokens, Uri backendBase, IReadOnlyList<string> scopes) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri is { } uri && backendBase.IsBaseOf(uri) && request.Headers.Authorization is null && scopes.Count > 0)
        {
            var result = await tokens.RequestAccessToken(new AccessTokenRequestOptions { Scopes = scopes });
            if (result.TryGetToken(out var token)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);
        }
        return await base.SendAsync(request, cancellationToken);
    }
}
