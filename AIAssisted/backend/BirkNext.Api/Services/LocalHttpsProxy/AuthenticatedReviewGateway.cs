using BirkNext.LocalHttpsProxy;

namespace BirkNext.Api.Services.LocalHttpsProxy;

/// <summary>
/// The single entry point the quality reviews (API, Integration, Front-end) use for anything authenticated. It resolves, from the
/// active Target Environment's saved <see cref="AuthenticatedTestingMethod"/> and the memory-only proxy context, a capability matrix and
/// executes approved authenticated API checks. Reviews never see a token, an Authorization header, cookies or a session id: they pass an
/// <see cref="AuthenticatedReviewIdentity"/> and receive sanitized results or a typed "unavailable/expired" outcome (never a silent
/// downgrade to a public request reported as authenticated).
/// </summary>
public interface IAuthenticatedReviewGateway
{
    AuthenticatedReviewCapabilities Resolve(AuthenticatedReviewIdentity identity);
    Task<AuthenticatedReviewExecutionOutcome> ExecuteRestAsync(AuthenticatedReviewIdentity identity, string httpMethod, string url, CancellationToken cancellationToken = default);
    Task<AuthenticatedReviewExecutionOutcome> ExecuteGraphQlQueryAsync(AuthenticatedReviewIdentity identity, string endpointUrl, string query, CancellationToken cancellationToken = default);
}

public sealed class AuthenticatedReviewGateway(IAuthenticatedApiExecutionService execution, ILocalHttpsProxyStatusQuery status, ILogger<AuthenticatedReviewGateway>? logger = null) : IAuthenticatedReviewGateway
{
    private const string ProxyDomReason = "The Local HTTPS proxy provides authenticated API access only; authenticated DOM and browser-runtime inspection require a CDP browser context, which is unavailable.";
    private const string StartProxyReason = "Start the local proxy, sign in to the target application in the proxy-configured browser, and perform an authenticated action.";
    private const string ExpiredReason = "Authenticated API session expired. Continue using the target application in the proxy-configured browser to refresh the session.";
    private const string CdpReason = "This environment uses the Managed Edge (CDP) browser context. Authenticated API execution for reviews is available only with the Local HTTPS proxy method.";
    private const string ManualReason = "Manual verification only. No authenticated automation is available for this environment.";

    public AuthenticatedReviewCapabilities Resolve(AuthenticatedReviewIdentity identity)
    {
        switch (identity.Method)
        {
            case AuthenticatedTestingMethod.LocalHttpsProxy:
            {
                var proxy = HasIdentity(identity) ? status.StatusForProfile(identity.ProfileId!, identity.ContextFingerprint!) : null;
                if (proxy is { AuthenticatedCredentialAvailable: true })
                    return new AuthenticatedReviewCapabilities
                    {
                        Method = identity.Method, ContextStatus = AuthenticatedApiContextStatus.Available,
                        PublicApi = true, AuthenticatedApi = true, AuthenticatedRest = true, AuthenticatedGraphQlQuery = true,
                        AuthenticatedBrowserDom = false, AuthenticatedBrowserRuntime = false,
                        ObservedHost = proxy.CredentialObservedHost, ExpiresAt = proxy.CredentialExpiresAt,
                        Reason = $"Authenticated via Local HTTPS Proxy (memory only). {ProxyDomReason}"
                    };
                var expired = proxy is { CredentialExpired: true };
                return new AuthenticatedReviewCapabilities
                {
                    Method = identity.Method,
                    ContextStatus = expired ? AuthenticatedApiContextStatus.Expired : AuthenticatedApiContextStatus.WaitingForAuthenticatedTraffic,
                    PublicApi = true, AuthenticatedApi = false, AuthenticatedRest = false, AuthenticatedGraphQlQuery = false,
                    AuthenticatedBrowserDom = false, AuthenticatedBrowserRuntime = false,
                    Reason = expired ? ExpiredReason : StartProxyReason
                };
            }
            case AuthenticatedTestingMethod.ManualOnly:
                return new AuthenticatedReviewCapabilities
                {
                    Method = identity.Method, ContextStatus = AuthenticatedApiContextStatus.NotApplicable,
                    PublicApi = true, AuthenticatedApi = false, AuthenticatedRest = false, AuthenticatedGraphQlQuery = false,
                    AuthenticatedBrowserDom = false, AuthenticatedBrowserRuntime = false, Reason = ManualReason
                };
            default: // ManagedEdgeCdp
                return new AuthenticatedReviewCapabilities
                {
                    Method = identity.Method, ContextStatus = AuthenticatedApiContextStatus.NotApplicable,
                    PublicApi = true, AuthenticatedApi = false, AuthenticatedRest = false, AuthenticatedGraphQlQuery = false,
                    AuthenticatedBrowserDom = false, AuthenticatedBrowserRuntime = false, Reason = CdpReason
                };
        }
    }

    public Task<AuthenticatedReviewExecutionOutcome> ExecuteRestAsync(AuthenticatedReviewIdentity identity, string httpMethod, string url, CancellationToken cancellationToken = default) =>
        ExecuteAsync(identity, (p, f) => execution.ExecuteRestForProfileAsync(p, f, httpMethod, url, cancellationToken));

    public Task<AuthenticatedReviewExecutionOutcome> ExecuteGraphQlQueryAsync(AuthenticatedReviewIdentity identity, string endpointUrl, string query, CancellationToken cancellationToken = default) =>
        ExecuteAsync(identity, (p, f) => execution.ExecuteGraphQlQueryForProfileAsync(p, f, endpointUrl, query, cancellationToken));

    private async Task<AuthenticatedReviewExecutionOutcome> ExecuteAsync(AuthenticatedReviewIdentity identity, Func<string, string, Task<AuthenticatedApiExecutionResult>> execute)
    {
        if (identity.Method == AuthenticatedTestingMethod.ManualOnly)
            return new() { Status = AuthenticatedExecutionStatus.MethodNotProxy, Mode = ReviewExecutionMode.ManualNotExecuted, Message = ManualReason };
        if (identity.Method != AuthenticatedTestingMethod.LocalHttpsProxy)
            return new() { Status = AuthenticatedExecutionStatus.MethodNotProxy, Mode = ReviewExecutionMode.AuthenticatedUnavailable, Message = CdpReason };
        if (!HasIdentity(identity))
            return new() { Status = AuthenticatedExecutionStatus.NoContext, Mode = ReviewExecutionMode.AuthenticatedUnavailable, Message = StartProxyReason };
        try
        {
            var result = await execute(identity.ProfileId!, identity.ContextFingerprint!);
            return new() { Status = AuthenticatedExecutionStatus.Executed, Mode = ReviewExecutionMode.AuthenticatedViaLocalHttpsProxy, Result = result, Message = result.Outcome };
        }
        catch (AuthenticatedContextUnavailableException ex)
        {
            return new() { Status = ex.Status, Mode = ReviewExecutionMode.AuthenticatedUnavailable, Message = ex.Status == AuthenticatedExecutionStatus.Expired ? ExpiredReason : ex.Message };
        }
        catch (ArgumentException ex)
        {
            // Unsafe method / non-approved query / bad URL: never falls back to an unauthenticated request.
            return new() { Status = AuthenticatedExecutionStatus.Rejected, Mode = ReviewExecutionMode.AuthenticatedUnavailable, Message = ex.Message };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            logger?.LogWarning("Authenticated review execution failed with {ExceptionType}.", ex.GetType().Name);
            return new() { Status = AuthenticatedExecutionStatus.Rejected, Mode = ReviewExecutionMode.AuthenticatedUnavailable, Message = "The authenticated request did not complete." };
        }
    }

    private static bool HasIdentity(AuthenticatedReviewIdentity identity) =>
        !string.IsNullOrWhiteSpace(identity.ProfileId) && !string.IsNullOrWhiteSpace(identity.ContextFingerprint);
}
