using BirkNext.Api.Configuration;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;

namespace BirkNext.Api.Filters;

/// <summary>
/// Action filter enforcing HTTPS policy for Target Detection API endpoint. Policy is configuration-driven, never by hosting environment:
/// - HTTPS is always accepted;
/// - plain HTTP is accepted from loopback (localhost, 127.0.0.1, ::1) while Local Tester Mode is active — the local BirkNext's own frontend and
///   backend on this computer, in development, a local Release run or the Windows Tester Package alike;
/// - any other HTTP request is rejected (426), and RequireHttps = false turns the check off.
/// </summary>
public sealed class RequireTargetDetectionHttpsFilter : IActionFilter
{
    private readonly IOptions<TargetDetectionOptions> _optionsSnapshot;
    private readonly ILogger<RequireTargetDetectionHttpsFilter> _logger;
    private readonly BirkNext.Api.Services.ActiveEventTesting.LocalTrustedExecution _local;

    public RequireTargetDetectionHttpsFilter(
        IOptions<TargetDetectionOptions> optionsSnapshot,
        ILogger<RequireTargetDetectionHttpsFilter> logger,
        BirkNext.Api.Services.ActiveEventTesting.LocalTrustedExecution local)
    {
        _optionsSnapshot = optionsSnapshot;
        _logger = logger;
        _local = local;
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

        // HTTP from loopback: the local BirkNext's own frontend and backend on this computer (Local Tester Mode), in any hosting environment.
        if (_local.ModeActive && IsRequestFromLoopback(request))
        {
            _logger.LogDebug("Allowing HTTP request from loopback in Local Tester Mode: {Host}", request.Host.Host);
            return;
        }

        // Reject insecure request
        _logger.LogWarning(
            "Rejecting insecure HTTP request to Target Detection API. " +
            "Host: {Host}, IsHttps: {IsHttps}, LocalTesterMode: {Mode}",
            request.Host.Host,
            request.IsHttps,
            _local.ModeDescription);

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
