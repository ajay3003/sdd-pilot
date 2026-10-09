using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;

namespace BirkNext.Web.Services;

/// <summary>Attaches the configured Entra API scope and starts the MSAL login flow when no usable access token is available.</summary>
public sealed class ActiveEventAuthorizationMessageHandler : AuthorizationMessageHandler
{
    public ActiveEventAuthorizationMessageHandler(IAccessTokenProvider provider, NavigationManager navigation, string backendBase, string scope)
        : base(provider, navigation) => ConfigureHandler(authorizedUrls: [backendBase], scopes: [scope]);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try { return await base.SendAsync(request, cancellationToken); }
        catch (AccessTokenNotAvailableException exception)
        {
            exception.Redirect();
            throw;
        }
    }
}
