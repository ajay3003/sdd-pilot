using BirkNext.Api.Models.Admin;
using BirkNext.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

[ApiController]
[Route("api/admin")]
public class AdminController : ControllerBase
{
    private readonly AdminService _adminService;

    public AdminController(AdminService adminService)
    {
        _adminService = adminService;
    }

    [HttpGet("system-settings")]
    public IActionResult GetSystemSettings()
    {
        if (!_adminService.IsEnabled)
            return NotFound();

        var settings = _adminService.BuildSettings();
        return Ok(settings);
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
}
