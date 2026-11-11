using BirkNext.Api.Models.Admin;
using BirkNext.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

[ApiController]
[Route("api/admin")]
public class AdminController : ControllerBase
{
    private readonly AdminService _adminService;
    private readonly IEnvironmentDiagnosticsService _diagnosticsService;
    private readonly IConfigurationHealthService _configHealthService;

    public AdminController(
        AdminService adminService,
        IEnvironmentDiagnosticsService diagnosticsService,
        IConfigurationHealthService configHealthService)
    {
        _adminService = adminService;
        _diagnosticsService = diagnosticsService;
        _configHealthService = configHealthService;
    }

    [HttpGet("system-settings")]
    public IActionResult GetSystemSettings()
    {
        if (!_adminService.IsEnabled)
            return NotFound();

        var settings = _adminService.BuildSettings();
        return Ok(settings);
    }

    [HttpGet("feature-visibility")]
    public IActionResult GetFeatureVisibility()
    {
        var flags = _adminService.BuildFeatureVisibility();
        return Ok(flags);
    }

    [HttpGet("editable-settings")]
    public IActionResult GetEditableSettings()
    {
        if (!_adminService.IsEnabled)
            return NotFound();

        var settings = _adminService.BuildEditableSettings();
        return Ok(settings);
    }

    /// <summary>Whether this session may change configuration (System Settings shows it; the save endpoint enforces it).</summary>
    [HttpGet("configuration-access")]
    public IActionResult GetConfigurationAccess([FromServices] BirkNext.Api.Services.ActiveEventTesting.LocalTrustedExecution local)
    {
        var (allowed, reason) = BirkNext.Api.Services.LocalSecurity.SystemConfigurationPermissions.Evaluate(User, local, HttpContext);
        return Ok(new { allowed, reason });
    }

    /// <summary>Recent configuration changes (append-only audit: actor, feature switches before → after, other sections changed).</summary>
    [HttpGet("configuration-changes")]
    public IActionResult GetConfigurationChanges() => _adminService.IsEnabled ? Ok(_adminService.ReadConfigurationAudit()) : NotFound();

    [HttpPost("system-settings")]
    [Microsoft.AspNetCore.Authorization.Authorize(Policy = BirkNext.Api.Services.LocalSecurity.SystemConfigurationPermissions.Policy)]
    public async Task<IActionResult> SaveSystemSettings([FromBody] SaveSettingsRequest request, [FromServices] BirkNext.Api.Services.ActiveEventTesting.LocalTrustedExecution local)
    {
        if (!_adminService.IsEnabled)
            return NotFound();

        var (valid, validationError) = _adminService.ValidateSettingsUpdate(request);
        if (!valid)
            return BadRequest(new SaveSettingsResponse { Success = false, Message = validationError });

        var granted = BirkNext.Api.Services.LocalSecurity.SystemConfigurationPermissions.Evaluate(User, local, HttpContext);
        var actor = BirkNext.Api.Services.LocalSecurity.SystemConfigurationPermissions.Actor(User,
            local.EvaluateFor(BirkNext.Api.Services.LocalSecurity.SystemConfigurationPermissions.LocalSetting,
                BirkNext.Api.Services.LocalSecurity.SystemConfigurationPermissions.LocalCapability, HttpContext).Allowed);
        var (success, message) = await _adminService.SaveSettingsAsync(request, granted.Allowed ? actor : null);

        return success
            ? Ok(new SaveSettingsResponse { Success = true, Message = message })
            : StatusCode(500, new SaveSettingsResponse { Success = false, Message = message });
    }

    [HttpPost("reset-local-database")]
    public async Task<IActionResult> ResetLocalDatabase([FromBody] ResetDatabaseRequest request)
    {
        if (!_adminService.IsEnabled)
            return NotFound();

        if (request.Confirmation != "RESET")
            return BadRequest(new ResetDatabaseResponse
            {
                Success = false,
                Message = "Confirmation text must be exactly 'RESET'."
            });

        var coordinator = HttpContext.RequestServices.GetRequiredService<BirkNext.Api.Services.LocalDataReset.LocalDataResetCoordinator>();
        var result = await coordinator.ResetAsync(HttpContext.RequestAborted);
        var response = new ResetDatabaseResponse
        {
            Success = result.Success, Message = result.Message, Status = result.Status, ResetId = result.ResetId, DeletedRows = result.DeletedRows,
            ResetAtUtc = result.ResetAt, ResetEpoch = result.ResetEpoch, DatabaseCleared = result.DatabaseCleared, BackendStateCleared = result.BackendStateCleared,
            ClearedDomains = result.ClearedDomains, PreservedDomains = result.PreservedDomains, Warnings = result.Warnings,
        };
        return result.Success ? Ok(response) : result.Status == "Blocked" ? Conflict(response) : BadRequest(response);
    }

    /// <summary>The reviewable impact of one reset scope (read-only). Nothing is deleted.</summary>
    [HttpGet("data-reset/plan")]
    public async Task<IActionResult> DataResetPlan([FromQuery] BirkNext.Maintenance.DataResetMode mode, CancellationToken ct)
    {
        if (!_adminService.IsEnabled) return NotFound();
        return Ok(await HttpContext.RequestServices.GetRequiredService<BirkNext.Api.Services.LocalDataReset.DataResetService>().PlanAsync(mode, ct));
    }

    /// <summary>Runs one explicitly confirmed reset scope. Refused, blocked and failed resets never report success.</summary>
    [HttpPost("data-reset")]
    public async Task<IActionResult> DataReset([FromBody] BirkNext.Maintenance.DataResetRequest request, CancellationToken ct)
    {
        if (!_adminService.IsEnabled) return NotFound();
        var result = await HttpContext.RequestServices.GetRequiredService<BirkNext.Api.Services.LocalDataReset.DataResetService>().ResetAsync(request, ct);
        return result.Status switch
        {
            "Completed" or "PartiallyCompleted" => Ok(result),
            "Blocked" => Conflict(result),
            "Refused" => BadRequest(result),
            _ => StatusCode(500, result),
        };
    }

    [HttpPost("environment-diagnostics")]
    public async Task<IActionResult> RunEnvironmentDiagnostics()
    {
        if (!_adminService.IsEnabled)
            return NotFound();

        var report = await _diagnosticsService.RunDiagnosticsAsync();
        return Ok(report);
    }

    [HttpGet("configuration-health")]
    public async Task<IActionResult> GetConfigurationHealth()
    {
        if (!_adminService.IsEnabled)
            return NotFound();

        var report = await _configHealthService.GetConfigurationHealthAsync();
        return Ok(report);
    }
}
