using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using BirkNext.Integrations;

namespace BirkNext.Api.Services.ActiveEventTesting;

/// <summary>
/// <b>Local Tester Mode</b> — BirkNext's supported operating model: one local instance, on one computer, used by the one tester connected to it,
/// with a local API, a local web application and no remote listener. In this mode the tester's QA capabilities (active test execution,
/// integration-configuration and test-plan writes, System Settings changes, Backup &amp; Transfer) are granted to the connected local tester —
/// identically in local development, a local Release run and the Windows Tester Package. It is decided by explicit configuration, never by the
/// .NET hosting environment: the backend may run as Production (normal runtime behaviour) without losing any QA function.
///
/// It is a capability grant, never an identity and never claims, and it applies to a request only when ALL of these hold:
/// <list type="bullet">
/// <item><c>BirkNext:LocalTesterMode</c> is true (set in the shipped appsettings.json; false turns every local grant off);</item>
/// <item>BirkNext listens on loopback addresses only (checked against the server's actual addresses at startup: one non-loopback listener —
/// e.g. <c>http://0.0.0.0:5000</c> — and the mode grants nothing, because the instance is then reachable from other machines);</item>
/// <item>the capability is not switched off explicitly (<c>Authorization:Local…</c> = false);</item>
/// <item>the socket peer is loopback on both ends (no forwarded-header middleware is registered, so X-Forwarded-For is never consulted);</item>
/// <item>the Host header is exactly <c>localhost</c> or a loopback IP literal (a DNS-rebinding page arrives with its own host name);</item>
/// <item>an Origin header is present and equals the configured <c>FRONTEND_ORIGIN</c> exactly (scheme, host and port), and that configured
/// origin is itself a well-formed loopback http(s) origin — a malformed or non-loopback configuration grants nothing.</item>
/// </list>
/// The API additionally requires the connected local session (launch code) for every call, so the tester is the one browser connected to this
/// instance. An authenticated principal with the matching Entra permission is granted independently of this mode.
/// </summary>
public sealed class LocalTrustedExecution(IHostEnvironment environment, IConfiguration configuration)
{
    public const string ModeSetting = "BirkNext:LocalTesterMode";
    public const string Setting = "Authorization:LocalTrustedExecution";

    public sealed record Decision(bool Allowed, string Reason);

    private volatile string? _nonLoopbackListener;

    /// <summary>The configured mode (before the listener check).</summary>
    public bool ModeConfigured => bool.TryParse(configuration[ModeSetting], out var on) && on;

    /// <summary>The mode applies to this instance: configured, and no listener beyond loopback.</summary>
    public bool ModeActive => ModeConfigured && _nonLoopbackListener is null;

    /// <summary>Why the mode is (not) active, for diagnostics.</summary>
    public string ModeDescription => !ModeConfigured ? $"Local Tester Mode is off for this backend ({ModeSetting} is not true)."
        : _nonLoopbackListener is { } address ? $"Local Tester Mode grants nothing: BirkNext also listens on {address}, which is not a loopback address."
        : $"Local Tester Mode: loopback-only BirkNext for the connected local tester (backend environment {environment.EnvironmentName}).";

    /// <summary>Records the server's listening addresses once it has started; any non-loopback address disables every local grant.</summary>
    public void ObserveListeners(IEnumerable<string> addresses) => _nonLoopbackListener = addresses.FirstOrDefault(a => !IsLoopbackListener(a));

    /// <summary>A Kestrel listening address on loopback only ("http://localhost:5480", "http://127.0.0.1:5097", "http://[::1]:5000"). Wildcards
    /// ("+", "*", "0.0.0.0", "[::]") and host names other than localhost are not. Pure.</summary>
    internal static bool IsLoopbackListener(string address)
    {
        var withoutScheme = address.Contains("://", StringComparison.Ordinal) ? address[(address.IndexOf("://", StringComparison.Ordinal) + 3)..] : address;
        var hostPort = withoutScheme.Split('/')[0];
        var host = hostPort.StartsWith('[') ? hostPort[..(hostPort.IndexOf(']') + 1)] : hostPort.Split(':')[0];
        return IsLoopbackHostName(host);
    }

    public bool Applies(HttpContext? context) => Evaluate(context).Allowed;

    public Decision Evaluate(HttpContext? context) => EvaluateFor(Setting, "Local trusted execution", context);

    /// <summary>
    /// The Local Tester Mode grant for one capability (<paramref name="setting"/> may switch that capability off explicitly; it is on by default
    /// while the mode is on). Every condition above applies; the hosting environment is never consulted.
    /// </summary>
    public Decision EvaluateFor(string setting, string capability, HttpContext? context)
    {
        if (!ModeActive) return new(false, ModeDescription);
        if (configuration[setting] is { } value && (!bool.TryParse(value, out var enabled) || !enabled))
            return new(false, $"{capability} is switched off for this backend ({setting} = {value}; only true or absent grants it).");
        if (context is null) return new(false, "No request.");
        var connection = context.Connection;
        if (connection.RemoteIpAddress is not { } remote || !IsLoopback(remote) || (connection.LocalIpAddress is { } local && !IsLoopback(local)))
            return new(false, $"{capability} needs a loopback connection to the backend.");
        if (!IsLoopbackHostName(context.Request.Host.Host))
            return new(false, $"{capability} needs the backend to be addressed as localhost or a loopback address.");
        if (ConfiguredOrigin(configuration["FRONTEND_ORIGIN"]) is not { } allowed)
            return new(false, $"The configured FRONTEND_ORIGIN is not a loopback http(s) origin, so {capability.ToLowerInvariant()} is unavailable.");
        var origin = context.Request.Headers.Origin;
        if (origin.Count != 1 || ParseOrigin(origin.ToString()) is not { } actual || !SameOrigin(actual, allowed))
            return new(false, $"{capability} is only granted to requests from the configured BirkNext frontend origin.");
        return new(true, $"{capability}: Local Tester Mode — loopback-only BirkNext, request from the configured BirkNext frontend.");
    }

    internal static bool IsLoopback(IPAddress address) =>
        IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);

    internal static bool IsLoopbackHostName(string? host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
        || (host is { Length: > 0 } && IPAddress.TryParse(host.StartsWith('[') && host.EndsWith(']') ? host[1..^1] : host, out var address) && IsLoopback(address)
            && !host.Contains('%'));

    /// <summary>The configured frontend origin when it is exactly scheme://host[:port] (optional trailing slash) with an http(s) scheme and a loopback host.</summary>
    internal static Uri? ConfiguredOrigin(string? value) =>
        ParseOrigin(value) is { } origin && IsLoopbackHostName(origin.Host) ? origin : null;

    /// <summary>A browser Origin value (or configured origin): absolute http(s), no credentials, no path beyond "/", no query or fragment. "null", file: and wildcards are refused.</summary>
    internal static Uri? ParseOrigin(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('*') || value.Trim() != value) return null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;
        if (!string.IsNullOrEmpty(uri.UserInfo) || uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.Fragment.Length > 0) return null;
        if (value.TrimEnd('/').Count(c => c == '/') != 2) return null;
        return uri;
    }

    private static bool SameOrigin(Uri a, Uri b) =>
        a.Scheme == b.Scheme && string.Equals(a.IdnHost, b.IdnHost, StringComparison.OrdinalIgnoreCase) && a.Port == b.Port;
}

/// <summary>
/// The stable, safe identifier of the BirkNext execution context that starts a run (journey history, per-context in-memory state such as an
/// interactive TEST sign-in). It is derived, never taken from input: a hash of the token's issuer and subject for an authenticated principal
/// holding ActiveEventExecute, or the fixed local session for the local trusted mode. It never contains an e-mail, UPN, name, raw subject,
/// IP address, machine or Windows user name, token, or the synthetic TestID reporter (an Altinn actor, not a BirkNext user).
/// </summary>
public static class ExecutionActor
{
    public const string LocalSession = "local-session:loopback";
    public const string AuthenticatedPrefix = "authenticated-user:";

    /// <summary>The execution decision for this request: authenticated permission first, then local trust. Grants no claims.</summary>
    public static ExecutionAccessView Access(ClaimsPrincipal user, LocalTrustedExecution.Decision local)
    {
        if (BirkNextPermissions.Holds(user, BirkNextPermissions.ActiveEventExecute))
            return new(ExecutionAccessMode.Authenticated, true, "Authenticated: this session holds the ActiveEventExecute permission.");
        if (local.Allowed) return new(ExecutionAccessMode.LocalTrusted, true, local.Reason);
        return new(ExecutionAccessMode.None, false, user.Identity?.IsAuthenticated == true
            ? "Signed in, but without the ActiveEventExecute permission."
            : $"Execution is not permitted for this session. {local.Reason}");
    }

    public static string? Resolve(ClaimsPrincipal user, bool localTrusted)
    {
        if (BirkNextPermissions.Holds(user, BirkNextPermissions.ActiveEventExecute)
            && (user.FindFirst("oid")?.Value ?? user.FindFirst("sub")?.Value ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value) is { Length: > 0 } subject)
        {
            var issuer = user.FindFirst("iss")?.Value ?? "";
            return AuthenticatedPrefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{issuer}|{subject}")))[..16].ToLowerInvariant();
        }
        return localTrusted ? LocalSession : null;
    }
}
