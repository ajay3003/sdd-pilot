using BirkNext.Api.Services.LocalSecurity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

/// <summary>
/// Local-session bootstrap (docs/security/localhost-session-design-2026-11-06.md). GET tells this browser whether it is connected (no secret);
/// POST exchanges the launch code shown in the BirkNext start window for an HttpOnly, SameSite=Strict session cookie (proof of possession — never an
/// anonymous token issuance); DELETE disconnects this browser; POST rotate replaces the code and disconnects every browser.
/// </summary>
[ApiController]
[Route("api/local-session")]
public sealed class LocalSessionController(LocalSessionService sessions, IConfiguration configuration) : ControllerBase
{
    public sealed record ExchangeRequest(string? Code, string? BootstrapToken = null);
    public sealed record BootstrapRequest(string? Code);
    public sealed record BootstrapResponse(string Token, int ExpiresInSeconds);

    /// <summary>The value of <see cref="LocalSessionService.ClientHeader"/> the Start-BirkNext launcher sends.</summary>
    public const string LauncherClient = "launcher";
    public sealed record SessionStatus(bool Connected);

    [HttpGet, AllowAnonymous]
    public ActionResult<SessionStatus> Status() =>
        new SessionStatus(Request.Cookies.TryGetValue(LocalSessionService.CookieName, out var token) && sessions.Validate(token));

    [HttpPost, AllowAnonymous]
    public ActionResult Exchange([FromBody] ExchangeRequest request)
    {
        // Defence in depth (not the authentication): the code is only accepted from the BirkNext frontend.
        var frontend = configuration["FRONTEND_ORIGIN"] ?? "http://localhost:5173";
        if (!LocalRequestGuardMiddleware.SameOrigin(Request.Headers.Origin.ToString(), frontend) || !Request.Headers.ContainsKey(LocalSessionService.ClientHeader))
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "The code can only be entered in the BirkNext application." });
        var (result, token) = string.IsNullOrEmpty(request.BootstrapToken) ? sessions.Exchange(request.Code) : sessions.ExchangeBootstrap(request.BootstrapToken);
        if (result == LocalSessionService.ExchangeResult.Throttled)
            return StatusCode(StatusCodes.Status429TooManyRequests, new { message = "Too many wrong codes. Wait a minute and try again." });
        if (result == LocalSessionService.ExchangeResult.Rejected)
            return Unauthorized(new { message = string.IsNullOrEmpty(request.BootstrapToken)
                ? "That code is not right. Use the code shown in the BirkNext start window."
                : "The automatic connection link has expired or was already used. Enter the code shown in the BirkNext start window." });
        Response.Cookies.Append(LocalSessionService.CookieName, token!, new CookieOptions
        {
            HttpOnly = true, SameSite = SameSiteMode.Strict, Path = "/", IsEssential = true, Secure = Request.IsHttps,
            MaxAge = LocalSessionService.AbsoluteLifetime,
        });
        return Ok(new SessionStatus(true));
    }

    /// <summary>
    /// The launcher (not a browser) exchanges the launch code it read from the owner-only file for a one-time bootstrap token, then opens the
    /// browser with that token in the URL fragment. Refused with an Origin header (every browser POST carries one) or without the launcher client
    /// header; loopback Host only (the shared guard). The code itself never leaves the launcher and this request.
    /// </summary>
    [HttpPost("bootstrap"), AllowAnonymous]
    public ActionResult<BootstrapResponse> Bootstrap([FromBody] BootstrapRequest request)
    {
        if (Request.Headers.ContainsKey("Origin") || Request.Headers[LocalSessionService.ClientHeader].ToString() != LauncherClient)
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "Only the BirkNext launcher may request an automatic connection." });
        var (result, token) = sessions.IssueBootstrap(request.Code);
        if (result == LocalSessionService.ExchangeResult.Throttled) return StatusCode(StatusCodes.Status429TooManyRequests, new { message = "Too many wrong codes. Wait a minute and try again." });
        if (result == LocalSessionService.ExchangeResult.Rejected) return Unauthorized(new { message = "That code is not right." });
        return new BootstrapResponse(token!, (int)LocalSessionService.BootstrapLifetime.TotalSeconds);
    }

    [HttpDelete]
    public ActionResult Disconnect()
    {
        if (Request.Cookies.TryGetValue(LocalSessionService.CookieName, out var token)) sessions.Revoke(token);
        Response.Cookies.Delete(LocalSessionService.CookieName, new CookieOptions { Path = "/", SameSite = SameSiteMode.Strict });
        return NoContent();
    }

    [HttpPost("rotate")]
    public ActionResult Rotate()
    {
        sessions.Rotate();
        Response.Cookies.Delete(LocalSessionService.CookieName, new CookieOptions { Path = "/", SameSite = SameSiteMode.Strict });
        return Ok(new { message = "A new code was created. Every browser must connect again with the new code from the start window." });
    }
}
