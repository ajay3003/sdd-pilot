using BirkNext.Api.Services.BrowserAuthentication;
using BirkNext.Api.Services.LocalHttpsProxy;
using BirkNext.BrowserAuthentication;
using BirkNext.LocalHttpsProxy;
using BirkNext.RuntimeAuthentication;

namespace BirkNext.Api.Services.RuntimeAuthentication;

/// <summary>
/// The shared authentication capability: per runtime operation and environment, which of Auto / Attended / Unattended / None applies and
/// what state it is in — read from the existing services, never a new store. Attended API state is the Local HTTPS proxy's memory-only
/// credential for exactly this profile + context fingerprint; attended browser state is "sign in when the run starts" (an open session is
/// never assumed); unattended state is the validated unattended login, only for the application it is configured for. Reading state
/// never starts a browser, a login or the proxy, and nothing returned is a credential.
/// </summary>
public sealed class RuntimeAuthenticationResolver(ILocalHttpsProxyStatusQuery proxy, UnattendedLoginService unattended)
{
    public IReadOnlyList<RuntimeAuthenticationResolution> Resolve(IEnumerable<RuntimeAuthenticationRequest> requests)
    {
        UnattendedLoginView? view = null;
        var results = new List<RuntimeAuthenticationResolution>();
        foreach (var request in requests)
        {
            var operation = RuntimeAuthenticationCatalog.Find(request.OperationKey);
            if (operation is null) continue;
            view ??= unattended.View();
            results.Add(RuntimeAuthenticationPolicy.Resolve(operation, request.Mode, request.EnvironmentId, Facts(operation, request, view)));
        }
        return results;
    }

    internal RuntimeAuthenticationFacts Facts(RuntimeAuthenticationOperation operation, RuntimeAuthenticationRequest request, UnattendedLoginView view)
    {
        var (attended, attendedReason, expiresAt) = Attended(operation, request);
        var (state, reason, identity) = Unattended(request, view);
        return new RuntimeAuthenticationFacts
        {
            SignInRequired = request.SignInRequired,
            Attended = attended, AttendedReason = attendedReason, AttendedExpiresAt = expiresAt,
            AttendedIdentity = operation.AttendedSource == AttendedAuthenticationSource.LocalHttpsProxy ? "Your browser session (via the Local HTTPS proxy)" : "Your browser session",
            Unattended = state, UnattendedReason = reason, UnattendedIdentity = identity,
        };
    }

    private (RuntimeAuthenticationState, string, DateTimeOffset?) Attended(RuntimeAuthenticationOperation operation, RuntimeAuthenticationRequest request)
    {
        if (operation.AttendedSource == AttendedAuthenticationSource.InteractiveBrowser)
            return (RuntimeAuthenticationState.Configured, "You sign in in the browser when the run starts. An already open session is not assumed.", null);
        if (operation.AttendedSource != AttendedAuthenticationSource.LocalHttpsProxy)
            return (RuntimeAuthenticationState.Unsupported, "Not applicable.", null);
        if (request.TestingMethod != AuthenticatedTestingMethod.LocalHttpsProxy)
            return (RuntimeAuthenticationState.NotConfigured, request.TestingMethod == AuthenticatedTestingMethod.ManualOnly
                ? "This environment is set to manual verification only. Choose Local HTTPS proxy in Target Environments → Authentication."
                : "Authenticated API access needs the Local HTTPS proxy method. Choose it in Target Environments → Authentication.", null);
        var status = string.IsNullOrWhiteSpace(request.EnvironmentId) || string.IsNullOrWhiteSpace(request.ContextFingerprint) ? null
            : proxy.StatusForProfile(request.EnvironmentId, request.ContextFingerprint);
        if (status is { AuthenticatedCredentialAvailable: true })
            return (RuntimeAuthenticationState.Authenticated, $"A credential from your browser was observed for {status.CredentialObservedHost ?? "this environment"} (memory only).", status.CredentialExpiresAt);
        if (status is { CredentialExpired: true })
            return (RuntimeAuthenticationState.Expired, "The observed credential expired. Keep using the application in the proxy-configured browser to refresh it.", status.CredentialExpiresAt);
        return (RuntimeAuthenticationState.Configured, "Start the Local HTTPS proxy and sign in to the application in your browser. No credential is observed yet.", null);
    }

    private (RuntimeAuthenticationState, string, string?) Unattended(RuntimeAuthenticationRequest request, UnattendedLoginView view)
    {
        var identity = view.TestIdentity ?? view.IdentityAlias;
        if (!view.Configured) return (RuntimeAuthenticationState.NotConfigured, "Configure unattended login in System Settings → Authentication.", null);
        if (!view.Enabled) return (RuntimeAuthenticationState.NotConfigured, "Unattended login is disabled in System Settings → Authentication.", identity);
        if (unattended.NotApplicableReason(request.FrontendUrl) is { } notApplicable) return (RuntimeAuthenticationState.NotConfigured, notApplicable, identity);
        return view.State switch
        {
            UnattendedLoginState.Ready => (RuntimeAuthenticationState.Authenticated,
                $"Validated{(view.LastValidation is { } v ? $" {v.ValidatedAt:yyyy-MM-dd HH:mm} UTC" : "")}. Each run signs in fresh with {identity} in an isolated browser.", identity),
            UnattendedLoginState.ValidationFailed when view.ConnectionStatus == UnattendedConnectionStatus.RequiresInteraction => (RuntimeAuthenticationState.Failed,
                "The identity provider requires interaction (for example MFA or Conditional Access). Unattended login does not bypass it.", identity),
            UnattendedLoginState.ValidationFailed => (RuntimeAuthenticationState.Failed,
                $"The last validation failed{(view.LastValidation is { } f ? $": {BrowserAuthenticationText.Outcome(f.Outcome)}" : "")}.", identity),
            _ => (RuntimeAuthenticationState.Configured, "Configured, but not validated with the current configuration. Validate it in System Settings → Authentication before use.", identity),
        };
    }
}
