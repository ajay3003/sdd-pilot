using BirkNext.Api.Filters;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

/// <summary>
/// Test fixture endpoints for deterministic testing of authenticated target scenarios.
/// Used ONLY in test/development environments.
/// SECURITY: Restricted to Development environment via DevelopmentOnlyControllerFilter.
/// Returns 404 in Production-like runtime.
/// </summary>
[ApiController]
[Route("")]
[ServiceFilter(typeof(DevelopmentOnlyControllerFilter))]
public sealed class TestFixtureController : ControllerBase
{
    /// <summary>
    /// Simulates an authenticated target by redirecting to /login.
    /// Returns HTTP 302 redirect so the detector can identify it as AuthenticationRequired.
    /// </summary>
    [HttpGet("auth-required")]
    public IActionResult AuthRequired()
    {
        return Redirect("/login");
    }

    /// <summary>
    /// Simulates a successful frontend response (no authentication required).
    /// Returns a minimal HTML page without authentication requirements.
    /// </summary>
    [HttpGet("test-fixture/no-auth-required")]
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

    /// <summary>
    /// Simulates a login page that requires authentication.
    /// Returns 401 Unauthorized to signal authentication is required.
    /// </summary>
    [HttpGet("login")]
    public IActionResult Login()
    {
        return Unauthorized();
    }
}
