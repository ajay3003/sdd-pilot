using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using BirkNext.Integrations;

namespace BirkNext.Api.Services.ActiveEventTesting;

/// <summary>
/// The local, single-user BirkNext execution mode: a backend that runs on a developer/tester machine without an identity provider. It is a
/// capability grant for ActiveEventExecute — never an identity, never claims — and it applies only when ALL of these hold for the request:
/// <list type="bullet">
/// <item>the backend setting <c>Authorization:LocalTrustedExecution</c> is true (off by default; the same backend-setting pattern as
/// <c>Authorization:LocalConfigurationWrites</c>; no request header or value can turn it on);</item>
/// <item>the host environment is Development (not Production, Staging, QA or any other name);</item>
/// <item>the socket peer is loopback on both ends (no forwarded-header middleware is registered, so X-Forwarded-For is never consulted);</item>
/// <item>the Host header is exactly <c>localhost</c> or a loopback IP literal (a DNS-rebinding page arrives with its own host name);</item>
/// <item>an Origin header is present and equals the configured <c>FRONTEND_ORIGIN</c> exactly (scheme, host and port), and that
/// configured origin is itself a well-formed loopback http(s) origin — a malformed or non-loopback configuration grants nothing.</item>
/// </list>
/// </summary>
public sealed class LocalTrustedExecution(IHostEnvironment environment, IConfiguration configuration)
{
    public const string Setting = "Authorization:LocalTrustedExecution";

    public sealed record Decision(bool Allowed, string Reason);

    public bool Applies(HttpContext? context) => Evaluate(context).Allowed;

    public Decision Evaluate(HttpContext? context)
    {
        if (!bool.TryParse(configuration[Setting], out var enabled) || !enabled)
            return new(false, "Local trusted execution is not enabled for this backend.");
        if (!environment.IsDevelopment())
            return new(false, "Local trusted execution is only available when the backend runs in the Development environment.");
        if (context is null) return new(false, "No request.");
        var connection = context.Connection;
        if (connection.RemoteIpAddress is not { } remote || !IsLoopback(remote) || (connection.LocalIpAddress is { } local && !IsLoopback(local)))
            return new(false, "Local trusted execution needs a loopback connection to the backend.");
        if (!IsLoopbackHostName(context.Request.Host.Host))
            return new(false, "Local trusted execution needs the backend to be addressed as localhost or a loopback address.");
        if (ConfiguredOrigin(configuration["FRONTEND_ORIGIN"]) is not { } allowed)
            return new(false, "The configured FRONTEND_ORIGIN is not a loopback http(s) origin, so local trusted execution is unavailable.");
        var origin = context.Request.Headers.Origin;
        if (origin.Count != 1 || ParseOrigin(origin.ToString()) is not { } actual || !SameOrigin(actual, allowed))
            return new(false, "Local trusted execution is only granted to requests from the configured BirkNext frontend origin.");
        return new(true, "Local trusted execution: loopback-only Development backend, request from the configured BirkNext frontend.");
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
