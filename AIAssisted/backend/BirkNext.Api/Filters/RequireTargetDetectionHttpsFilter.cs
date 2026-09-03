using BirkNext.Api.Configuration;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;

namespace BirkNext.Api.Filters;

/// <summary>
/// Action filter enforcing HTTPS policy for Target Detection API endpoint.
/// Policy is configuration-driven and environment-aware.
///
/// DEFAULT (Production):
/// - HTTPS required: true
/// - HTTP rejected
///
/// DEVELOPMENT/TEST:
/// - HTTPS still preferred
/// - HTTP loopback (localhost, 127.0.0.1, ::1) explicitly allowed if configured
/// - HTTP non-loopback remains rejected
/// </summary>
public sealed class RequireTargetDetectionHttpsFilter : IActionFilter
{
    private readonly IOptions<TargetDetectionOptions> _optionsSnapshot;
    private readonly ILogger<RequireTargetDetectionHttpsFilter> _logger;
    private readonly IWebHostEnvironment _environment;

    public RequireTargetDetectionHttpsFilter(
        IOptions<TargetDetectionOptions> optionsSnapshot,
        ILogger<RequireTargetDetectionHttpsFilter> logger,
        IWebHostEnvironment environment)
    {
        _optionsSnapshot = optionsSnapshot;
        _logger = logger;
        _environment = environment;
    }

    public void OnActionExecuting(ActionExecutingContext context)
    {
        // Skip if HTTPS is not required by configuration
        if (!_optionsSnapshot.Value.RequireHttps)
        {
            return;
        }

        var request = context.HttpContext.Request;

        // HTTPS is always acceptable
        if (request.IsHttps)
        {
            return;
        }

        // Check if HTTP from loopback is explicitly allowed
        // This is only intended for local development/test where frontend and backend are both on localhost
        if (_environment.IsDevelopment() && IsRequestFromLoopback(request))
        {
            _logger.LogDebug("Allowing HTTP request from loopback in Development environment: {Host}", request.Host.Host);
            return;
        }

        // Reject insecure request
        _logger.LogWarning(
            "Rejecting insecure HTTP request to Target Detection API. " +
            "Host: {Host}, IsHttps: {IsHttps}, Environment: {Environment}, AllowLoopback: {AllowLoopback}",
            request.Host.Host,
            request.IsHttps,
            _environment.EnvironmentName,
            _environment.IsDevelopment());

        context.Result = new StatusCodeResult(StatusCodes.Status426UpgradeRequired);
    }

    public void OnActionExecuted(ActionExecutedContext context) { }

    /// <summary>
    /// Determines if request is from loopback address.
    /// Trusted representations:
    /// - localhost (DNS)
    /// - 127.x.x.x (IPv4 loopback)
    /// - ::1 (IPv6 loopback)
    /// </summary>
    private static bool IsRequestFromLoopback(HttpRequest request)
    {
        var host = request.Host.Host.ToLowerInvariant();

        // DNS loopback
        if (host == "localhost")
            return true;

        // IPv4 loopback (127.0.0.0/8)
        if (host.StartsWith("127."))
            return true;

        // IPv6 loopback
        if (host == "::1" || host == "[::1]")
            return true;

        return false;
    }
}
