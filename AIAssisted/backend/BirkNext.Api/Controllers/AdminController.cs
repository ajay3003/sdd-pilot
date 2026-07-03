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

    [HttpPost("system-settings")]
    public async Task<IActionResult> SaveSystemSettings([FromBody] SaveSettingsRequest request)
    {
        if (!_adminService.IsEnabled)
            return NotFound();

        var (valid, validationError) = _adminService.ValidateSettingsUpdate(request);
        if (!valid)
            return BadRequest(new SaveSettingsResponse { Success = false, Message = validationError });

        var (success, message) = await _adminService.SaveSettingsAsync(request);

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

        var (success, message) = await _adminService.ResetLocalDatabaseAsync();

        if (!success)
            return BadRequest(new ResetDatabaseResponse { Success = false, Message = message });

        return Ok(new ResetDatabaseResponse { Success = true, Message = message });
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
