using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;

namespace BirkNext.Api.Services.LocalSecurity;

/// <summary>Authenticates the local-session cookie. Grants identity only — never a permission claim, so named policies keep their requirements.</summary>
public sealed class LocalSessionAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
    LocalSessionService sessions) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "LocalSession";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Cookies.TryGetValue(LocalSessionService.CookieName, out var token)) return Task.FromResult(AuthenticateResult.NoResult());
        if (!sessions.Validate(token)) return Task.FromResult(AuthenticateResult.Fail("The local session expired or was revoked."));
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "local-user"), new Claim("birknext:session", "local")], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}

/// <summary>
/// One central guard for <c>/api</c> and <c>/graphql</c> (docs/security/localhost-session-design-2026-11-06.md): a loopback Host (DNS rebinding),
/// an authenticated caller (local-session cookie or an Entra bearer token), and for cookie-authenticated unsafe requests and every GraphQL POST the
/// exact frontend Origin plus the <see cref="LocalSessionService.ClientHeader"/> header (CSRF; the custom header forces a CORS preflight). Extension
/// origins are accepted only on the Browser Companion extension routes. Named authorization policies run afterwards, unchanged.
/// </summary>
public sealed class LocalRequestGuardMiddleware(RequestDelegate next, IConfiguration configuration)
{
    private const string ExtensionRoutes = "/api/browser-companion/extension";
    private readonly string _frontendOrigin = (configuration["FRONTEND_ORIGIN"] ?? "http://localhost:5173").TrimEnd('/');

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path;
        var graphQl = path.StartsWithSegments("/graphql");
        if (!path.StartsWithSegments("/api") && !graphQl || HttpMethods.IsOptions(context.Request.Method)) { await next(context); return; }

        if (!IsLoopbackHost(context.Request.Host.Host)) { await Reject(context, StatusCodes.Status400BadRequest, "This service only answers requests addressed to localhost."); return; }

        var origin = context.Request.Headers.Origin.ToString();
        var extensionRoute = path.StartsWithSegments(ExtensionRoutes);
        if (IsExtensionOrigin(origin) && !extensionRoute) { await Reject(context, StatusCodes.Status403Forbidden, "Extension origins may only call the Browser Companion extension routes."); return; }
        // The Browser Companion extension routes keep their own loopback, origin, pairing and session checks.
        if (extensionRoute || IsSessionBootstrap(context.Request)) { await next(context); return; }

        var bearer = context.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase);
        if (bearer)
        {
            // A presented bearer token decides alone: an invalid one is rejected, never silently replaced by the ambient session cookie.
            if (context.User.Identity?.IsAuthenticated != true && (await context.AuthenticateAsync(JwtBearerDefaults.AuthenticationScheme)) is { Succeeded: true } jwt)
                context.User = jwt.Principal!;
        }
        else if ((await context.AuthenticateAsync(LocalSessionAuthenticationHandler.SchemeName)) is { Succeeded: true } session)
            context.User = session.Principal!;
        if (context.User.Identity?.IsAuthenticated != true)
        {
            context.Response.Headers.WWWAuthenticate = LocalSessionService.ChallengeScheme;
            await Reject(context, StatusCodes.Status401Unauthorized, "Connect this browser to BirkNext: enter the code shown in the BirkNext start window.");
            return;
        }
        // CSRF: an ambient cookie is not enough for a state change. A bearer token is not ambient and is exempt.
        var cookieAuthenticated = context.User.Identity?.AuthenticationType == LocalSessionAuthenticationHandler.SchemeName;
        var unsafeRequest = !(HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method)) || graphQl && HttpMethods.IsPost(context.Request.Method);
        if (cookieAuthenticated && unsafeRequest && (!SameOrigin(origin, _frontendOrigin) || !context.Request.Headers.ContainsKey(LocalSessionService.ClientHeader)))
        {
            await Reject(context, StatusCodes.Status403Forbidden, "The request did not come from the BirkNext application.");
            return;
        }
        await next(context);
    }

    private static bool IsSessionBootstrap(HttpRequest request) =>
        request.Path.Equals("/api/local-session", StringComparison.OrdinalIgnoreCase) && (HttpMethods.IsGet(request.Method) || HttpMethods.IsPost(request.Method))
        // The launcher's one-time token request: the controller requires the launch code, the launcher client header and no Origin.
        || request.Path.Equals("/api/local-session/bootstrap", StringComparison.OrdinalIgnoreCase) && HttpMethods.IsPost(request.Method);

    public static bool IsLoopbackHost(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host == "127.0.0.1" || host is "[::1]" or "::1";

    public static bool IsExtensionOrigin(string origin) =>
        origin.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase) || origin.StartsWith("moz-extension://", StringComparison.OrdinalIgnoreCase)
        || origin.StartsWith("extension://", StringComparison.OrdinalIgnoreCase);

    public static bool SameOrigin(string origin, string expected) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var a) && Uri.TryCreate(expected, UriKind.Absolute, out var b)
        && string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase) && string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase) && a.Port == b.Port;

    private static Task Reject(HttpContext context, int status, string message)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(new { message });
    }
}

/// <summary>Supported local mode binds loopback only: every configured URL must name localhost or a loopback address.</summary>
public static class LoopbackBinding
{
    public const string AllowSetting = "Hosting:AllowNonLoopbackBinding";

    /// <summary>The configured URLs that would listen beyond loopback (empty = safe). "*", "+", 0.0.0.0, [::], LAN addresses and host names are refused.</summary>
    public static IReadOnlyList<string> Violations(string? urls) =>
        [.. (urls ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(u => !IsLoopbackUrl(u))];

    /// <summary>The addresses .NET 8 binds for HTTP_PORTS / HTTPS_PORTS (ASPNETCORE_ or DOTNET_ prefixed): every interface, "scheme://*:port".</summary>
    public static IReadOnlyList<string> PortOnlyUrls(string? http, string? https) =>
        [.. new[] { ("http", http), ("https", https) }.SelectMany(p => (p.Item2 ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(port => $"{p.Item1}://*:{port}"))];

    public static bool IsLoopbackUrl(string url)
    {
        var normalized = url.Replace("://*", "://wildcard").Replace("://+", "://wildcard");
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri)) return false;
        if (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        return System.Net.IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) && System.Net.IPAddress.IsLoopback(ip);
    }
}
