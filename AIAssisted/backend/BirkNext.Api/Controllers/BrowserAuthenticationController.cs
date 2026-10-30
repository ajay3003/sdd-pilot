using BirkNext.Api.Services.ActiveEventTesting;
using BirkNext.Api.Services.BrowserAuthentication;
using BirkNext.BrowserAuthentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

/// <summary>
/// Unattended browser login settings (System Settings → Authentication). Reads return metadata only — never the password, the raw login
/// name, cookies or tokens. Every mutation, credential write and validation uses the integration-configuration write policy, the same
/// protection as other credential-like settings (Altinn session secret). Attended browser login has no endpoint here and is unchanged.
/// </summary>
[ApiController]
[Route("api/browser-authentication")]
public sealed class BrowserAuthenticationController(UnattendedLoginService unattended) : ControllerBase
{
    [HttpGet("unattended")]
    public ActionResult<UnattendedLoginView> Get() => unattended.View();

    [HttpGet("automated-execution")]
    public ActionResult<AutomatedBrowserExecutionReadiness> AutomatedExecution() => unattended.AutomatedReadiness();

    [HttpPut("unattended")]
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    public IActionResult Save(SaveUnattendedLoginRequest request)
    {
        var outcome = unattended.Save(request);
        return outcome.View is null ? BadRequest(new { message = string.Join(" ", outcome.Errors), errors = outcome.Errors }) : Ok(outcome.View);
    }

    [HttpPut("unattended/enabled")]
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    public IActionResult SetEnabled(SetUnattendedLoginEnabledRequest request) =>
        unattended.SetEnabled(request.Enabled) is { } view ? Ok(view) : NotFound(new { message = "Unattended login is not configured." });

    [HttpPost("unattended/validate")]
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    public async Task<ActionResult<UnattendedLoginValidationResult>> Validate(CancellationToken ct) => await unattended.ValidateAsync(ct);

    [HttpDelete("unattended")]
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    public IActionResult Clear()
    {
        unattended.Clear();
        return NoContent();
    }
}
