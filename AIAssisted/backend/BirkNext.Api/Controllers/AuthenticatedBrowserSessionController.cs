using BirkNext.Api.Services.AuthenticatedReview;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

[ApiController]
[Route("api/frontend-quality/auth-session")]
public sealed class AuthenticatedBrowserSessionController : ControllerBase
{
    private readonly IAuthenticatedBrowserSessionManager _sessions;

    public AuthenticatedBrowserSessionController(IAuthenticatedBrowserSessionManager sessions) => _sessions = sessions;

    [HttpPost("start")]
    public async Task<IActionResult> Start([FromBody] StartAuthenticatedBrowserSessionRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var session = await _sessions.StartAsync(new(request.ReviewSessionId, request.ProfileId, request.TargetUrl), cancellationToken);
            return Ok(ToResponse(session));
        }
        catch (AuthenticatedReviewUnavailableException ex) { return Conflict(new { message = ex.Message, code = "authenticated_review_unavailable" }); }
        catch (AuthenticatedSessionConflictException ex) { return Conflict(new { message = ex.Message, code = "session_conflict" }); }
        catch (ArgumentException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpGet("{sessionId}")]
    public async Task<IActionResult> Status(string sessionId, [FromQuery] string reviewSessionId, [FromQuery] string profileId, CancellationToken cancellationToken)
    {
        var session = await _sessions.GetStatusAsync(sessionId, reviewSessionId, profileId, cancellationToken);
        return session is null ? NotFound() : Ok(ToResponse(session));
    }

    [HttpPost("{sessionId}/authenticate")]
    public async Task<IActionResult> Authenticate(string sessionId, [FromBody] AuthenticateBrowserSessionRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var session = await _sessions.BeginAuthenticationAsync(new(
                sessionId,
                request.ReviewSessionId,
                request.ProfileId,
                request.ExpectedAuthority,
                request.SyntheticMcasOrigin), cancellationToken);
            return Ok(ToResponse(session));
        }
        catch (System.Collections.Generic.KeyNotFoundException) { return NotFound(); }
        catch (AuthenticatedReviewUnavailableException ex) { return Conflict(new { message = ex.Message, code = "authenticated_review_unavailable" }); }
        catch (AuthenticatedSessionExpiredException) { return Conflict(new { message = "Session expired.", code = "session_expired" }); }
        catch (ArgumentException ex) { return BadRequest(new { message = ex.Message }); }
        catch (AuthenticatedNavigationException) { return StatusCode(502, new { message = "Authentication navigation failed.", code = "authentication_navigation_failed" }); }
    }

    [HttpPost("{sessionId}/cancel")]
    public async Task<IActionResult> Cancel(string sessionId, [FromBody] AuthenticatedBrowserSessionOwnerRequest request, CancellationToken cancellationToken)
    {
        var cancelled = await _sessions.CancelAsync(sessionId, request.ReviewSessionId, request.ProfileId, cancellationToken);
        return cancelled ? NoContent() : NotFound();
    }

    private static AuthenticatedBrowserSessionResponse ToResponse(AuthenticatedBrowserSessionDescriptor value) =>
        new(value.SessionId, value.Status, value.TargetOrigin, value.StartedAt, value.ExpiresAt, value.FailureCategory, value.DeliveryContext, value.ApplicationValidationCurrent);
}

public sealed record StartAuthenticatedBrowserSessionRequest(string ReviewSessionId, string ProfileId, string TargetUrl);
public sealed record AuthenticatedBrowserSessionOwnerRequest(string ReviewSessionId, string ProfileId);
public sealed record AuthenticateBrowserSessionRequest(string ReviewSessionId, string ProfileId, string ExpectedAuthority, string? SyntheticMcasOrigin = null);
public sealed record AuthenticatedBrowserSessionResponse(
    string SessionId,
    AuthenticatedBrowserSessionStatus Status,
    string TargetOrigin,
    DateTimeOffset StartedAt,
    DateTimeOffset ExpiresAt,
    string? FailureCategory,
    AuthenticatedDeliveryContext DeliveryContext = AuthenticatedDeliveryContext.None,
    bool ApplicationValidationCurrent = false);
