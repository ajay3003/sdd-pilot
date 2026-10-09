using BirkNext.Api.Services.ApiQuality;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

[ApiController]
[Route("api/api-quality")]
public class ApiQualityController : ControllerBase
{
    private readonly IApiQualityReviewService _service;
    private readonly ILogger<ApiQualityController> _logger;

    public ApiQualityController(
        IApiQualityReviewService service,
        ILogger<ApiQualityController> logger)
    {
        _service = service;
        _logger  = logger;
    }

    /// <summary>
    /// API Quality Review v2: reviews the selected REST/GraphQL targets (from Endpoint Discovery, configuration or contract) read-only,
    /// authenticated through the review gateway when the environment's proxy context exists. Never guesses paths; fails fast on a
    /// missing authenticated context; returns structural evidence only.
    /// </summary>
    [HttpPost("review")]
    public async Task<IActionResult> Review([FromBody] BirkNext.ApiReview.ApiReviewRunRequest request, [FromServices] IApiReviewEngine engine, CancellationToken ct)
    {
        if (request.Targets.Count(t => t.Selected) == 0)
            return BadRequest(new { message = "No REST or GraphQL API target is available for review." });
        Response.Headers.CacheControl = "no-store";
        try { return Ok(await engine.RunAsync(request, ct)); }
        catch (OperationCanceledException) { return StatusCode(499); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "API review failed for environment '{Name}'", request.Environment.Name);
            return StatusCode(500, new { message = "API review failed: " + ex.GetType().Name });
        }
    }

    /// <summary>
    /// Safe fuzzing eligibility and case preview for the selected targets. The backend decides environment safety, derives cases from the
    /// published contract and sends no fuzz request. Production and unknown environments return a plan blocked by safety policy.
    /// </summary>
    [HttpPost("fuzzing/plan")]
    public async Task<IActionResult> FuzzingPlan([FromBody] BirkNext.ApiReview.ApiFuzzingRunRequest request, [FromServices] BirkNext.Api.Services.ApiQuality.Fuzzing.IApiFuzzingService fuzzing, CancellationToken ct)
    {
        if (request.Review.Targets.Count(t => t.Selected) == 0)
            return BadRequest(new { message = "No REST or GraphQL API target is selected." });
        Response.Headers.CacheControl = "no-store";
        try { return Ok(await fuzzing.PlanAsync(request, ct)); }
        catch (OperationCanceledException) { return StatusCode(499); }
    }

    /// <summary>Starts a bounded safe-fuzzing run in the background (one at a time). Poll <c>fuzzing/runs/{id}</c>; cancel with <c>fuzzing/runs/{id}/cancel</c>.</summary>
    [HttpPost("fuzzing/runs")]
    public IActionResult StartFuzzing([FromBody] BirkNext.ApiReview.ApiFuzzingRunRequest request, [FromServices] BirkNext.Api.Services.ApiQuality.Fuzzing.ApiFuzzingRunCoordinator runs,
        [FromServices] BirkNext.Api.Services.ApiQuality.IApiEnvironmentSafetyPolicy safety,
        [FromServices] Microsoft.Extensions.Options.IOptions<BirkNext.Api.Services.ApiQuality.Security.SecurityTestingOptions> securityOptions)
    {
        if (request.Review.Targets.Count(t => t.Selected) == 0)
            return BadRequest(new { message = "No REST or GraphQL API target is selected." });
        if (request.Settings.BodyFuzzing && ProtectedExecutionRefusal(securityOptions.Value) is { } refusal) return refusal;
        // The run would refuse anyway; refusing here keeps a blocked environment from ever reaching the run coordinator.
        var decision = safety.EvaluateForFuzzing(request.Review);
        if (!decision.ActiveTestingAllowed)
            return UnprocessableEntity(new { message = $"Blocked by safety policy: {decision.Reason}", safety = decision });
        try { return Accepted(runs.Start(request, decision)); }
        catch (BirkNext.Api.Services.ApiQuality.Fuzzing.ApiFuzzingRunConflictException ex) { return Conflict(new { message = ex.Message }); }
    }

    [HttpGet("fuzzing/runs/{runId}")]
    public IActionResult FuzzingRun(string runId, [FromServices] BirkNext.Api.Services.ApiQuality.Fuzzing.ApiFuzzingRunCoordinator runs)
    {
        Response.Headers.CacheControl = "no-store";
        return runs.Get(runId) is { } report ? Ok(report) : NotFound(new { message = "Unknown fuzzing run." });
    }

    [HttpPost("fuzzing/runs/{runId}/cancel")]
    public IActionResult CancelFuzzing(string runId, [FromServices] BirkNext.Api.Services.ApiQuality.Fuzzing.ApiFuzzingRunCoordinator runs) =>
        runs.Cancel(runId) is { } report ? Ok(report) : NotFound(new { message = "Unknown fuzzing run." });

    /// <summary>
    /// Whether protected security execution is available and whether the Target Environment is server-registered for authorization
    /// scenarios and body fuzzing. No request is sent to any target.
    /// </summary>
    [HttpGet("security-execution")]
    public IActionResult SecurityExecution([FromQuery] string? profileId, [FromServices] Microsoft.Extensions.Options.IOptions<BirkNext.Api.Services.ApiQuality.Security.SecurityTestingOptions> options,
        [FromServices] BirkNext.Api.Services.ApiQuality.Security.ITrustedSecurityTargetRegistry trust)
    {
        Response.Headers.CacheControl = "no-store";
        return Ok(ExecutionStatus(options.Value, trust.Resolve(profileId, [])));
    }

    /// <summary>
    /// Runs explicit authorization scenarios (safe requests only) for a server-registered trusted non-production Target Environment.
    /// Each identity is compared with its own expectation; credentials are never accepted from or returned to the client.
    /// </summary>
    [HttpPost("authorization/runs")]
    public async Task<IActionResult> RunAuthorization([FromBody] BirkNext.RuntimeSecurity.AuthorizationRunRequest request,
        [FromServices] BirkNext.Api.Services.ApiQuality.Security.IAuthorizationScenarioService authorization,
        [FromServices] Microsoft.Extensions.Options.IOptions<BirkNext.Api.Services.ApiQuality.Security.SecurityTestingOptions> options, CancellationToken ct)
    {
        if (ProtectedExecutionRefusal(options.Value) is { } refusal) return refusal;
        Response.Headers.CacheControl = "no-store";
        try { return Ok(await authorization.RunAsync(request, ct)); }
        catch (OperationCanceledException) { return StatusCode(499); }
    }

    private static BirkNext.RuntimeSecurity.SecurityExecutionStatus ExecutionStatus(BirkNext.Api.Services.ApiQuality.Security.SecurityTestingOptions options, BirkNext.RuntimeSecurity.TrustedTargetDecision trust) => new()
    {
        RequireAuthenticatedUser = options.RequireAuthenticatedUser,
        // No user authentication scheme is configured on this instance; when one is required, protected execution fails closed.
        UserAuthenticationAvailable = false,
        Message = options.RequireAuthenticatedUser ? BirkNext.RuntimeSecurity.SecurityExecutionStatus.AuthenticationRequiredMessage : null,
        Trust = trust,
    };

    private ObjectResult? ProtectedExecutionRefusal(BirkNext.Api.Services.ApiQuality.Security.SecurityTestingOptions options) =>
        options.RequireAuthenticatedUser
            ? StatusCode(503, new { message = BirkNext.RuntimeSecurity.SecurityExecutionStatus.AuthenticationRequiredMessage })
            : null;

    [HttpPost("analyze")]
    public async Task<IActionResult> Analyze([FromBody] ApiQualityReviewRequest request, CancellationToken ct)
    {
        bool hasAnyApiUrl =
            !string.IsNullOrWhiteSpace(request.RestBaseUrl)     ||
            !string.IsNullOrWhiteSpace(request.HealthEndpoint)  ||
            !string.IsNullOrWhiteSpace(request.SwaggerUrl)      ||
            !string.IsNullOrWhiteSpace(request.GraphQlEndpoint);

        if (!hasAnyApiUrl)
            return BadRequest(new { message = "No API endpoints configured. Add a REST Base URL, Health Endpoint, Swagger URL, or GraphQL Endpoint to the active Target Environment." });

        var correlationId = HttpContext.Request.Headers["X-Correlation-Id"].FirstOrDefault() ?? "unknown";
        _logger.LogInformation(
            "API quality review requested for environment '{Name}' CorrelationId: {CorrelationId}",
            request.EnvironmentName, correlationId);

        try
        {
            var report = await _service.AnalyzeAsync(request, ct);
            return Ok(report);
        }
        catch (OperationCanceledException)
        {
            return StatusCode(499);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "API quality review failed for environment '{Name}'", request.EnvironmentName);
            return StatusCode(500, new { message = "API quality review failed: " + ex.Message });
        }
    }
}
