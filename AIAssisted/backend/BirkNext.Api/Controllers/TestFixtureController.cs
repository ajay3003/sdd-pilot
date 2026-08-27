using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

/// <summary>
/// Test fixture endpoints for deterministic testing of authenticated target scenarios.
/// Used ONLY in test/development environments. Do NOT expose in production.
/// </summary>
[ApiController]
[Route("test-fixture")]
public sealed class TestFixtureController : ControllerBase
{
    /// <summary>
    /// Simulates an authenticated target by returning an HTML page with meta-redirect to /login.
    /// The preflight service will detect the redirect to /login and mark as AuthenticationRequired.
    /// </summary>
    [HttpGet("auth-required")]
    public IActionResult AuthRequired()
    {
        var html = @"
<!DOCTYPE html>
<html>
<head>
    <title>Redirecting to login...</title>
    <meta http-equiv='refresh' content='0; url=/login'>
</head>
<body>
    <p>This application requires authentication. Redirecting to login...</p>
</body>
</html>";
        return Content(html, "text/html");
    }

    /// <summary>
    /// Simulates a successful frontend response (no authentication required).
    /// Returns a minimal HTML page without authentication requirements.
    /// </summary>
    [HttpGet("no-auth-required")]
    public IActionResult NoAuthRequired()
    {
        var html = @"
<!DOCTYPE html>
<html>
<head>
    <title>Test Frontend Application</title>
</head>
<body>
    <h1>Test Frontend Application</h1>
    <p>This application does not require authentication.</p>
</body>
</html>";
        return Content(html, "text/html");
    }
}
