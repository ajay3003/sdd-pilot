using BirkNext.Api.Services.ActiveEventTesting;
using BirkNext.Api.Services.AltinnTesting;
using BirkNext.Integrations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

/// <summary>
/// Altinn app testing: the app contract (read like other readiness metadata; refreshed from the app's source in Source Analysis or public TT02 metadata with
/// the Integration Quality Review write permission, since it is stored with that configuration) and the tester's ID-porten TEST sign-in
/// (ActiveEventExecute, the same execution permission as journey runs). No endpoint accepts a URL, endpoint, app id, payload or credential:
/// the app comes from Integration Quality Review and every host from the backend policy.
/// </summary>
[ApiController]
[Route("api/altinn-testing")]
public sealed class AltinnTestingController(AltinnAppContractService contracts, IdPortenTestSignIn signIn, LocalTrustedExecution local, IConfiguration configuration,
    ILogger<AltinnTestingController> logger) : ControllerBase
{
    [AllowAnonymous]
    [HttpGet("contract")]
    public async Task<ActionResult<AltinnAppContractView>> Contract([FromQuery] string environmentId, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(environmentId) ? BadRequest(new { message = "environmentId is required." }) : Ok(await contracts.ViewAsync(environmentId, ct));

    /// <summary>Reads the configured app's public TT02 metadata (three anonymous GETs: application metadata, the form schema and the deployed OpenAPI contract). Nothing is submitted; nobody is authenticated.</summary>
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    [HttpPost("contract/public-metadata")]
    public async Task<ActionResult<AltinnAppContractView>> DiscoverPublic([FromQuery] string environmentId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(environmentId)) return BadRequest(new { message = "environmentId is required." });
        try { return Ok(await contracts.DiscoverPublicAsync(environmentId, ct)); }
        catch (Exception ex) when (ex is AltinnContractException or AltinnSignInException) { return BadRequest(new { message = ex.Message }); }
        catch (HttpRequestException) { return StatusCode(StatusCodes.Status502BadGateway, new { message = "The public TT02 metadata could not be reached." }); }
        catch (System.Text.Json.JsonException) { return BadRequest(new { message = "The public TT02 metadata is not readable JSON." }); }
    }

    /// <summary>Confirms the latest live deployed contract after a person reviewed its changes (refused while it is otherwise not usable).</summary>
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    [HttpPost("contract/accept-changes")]
    public async Task<ActionResult<AltinnAppContractView>> AcceptChanges([FromQuery] string environmentId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(environmentId)) return BadRequest(new { message = "environmentId is required." });
        try { return Ok(await contracts.AcceptChangesAsync(environmentId, User.Identity?.IsAuthenticated == true ? "an authenticated user" : "the local tester", ct)); }
        catch (AltinnContractException ex) { return BadRequest(new { message = ex.Message }); }
    }

    /// <summary>Imports an OpenAPI document (JSON body, at most 2 MB) for offline analysis of the configured app. Never live-verified; never used to execute.</summary>
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    [HttpPost("contract/import-openapi")]
    [RequestSizeLimit(3 * 1024 * 1024)]
    public async Task<ActionResult<AltinnAppContractView>> ImportOpenApi([FromQuery] string environmentId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(environmentId)) return BadRequest(new { message = "environmentId is required." });
        using var reader = new StreamReader(Request.Body, System.Text.Encoding.UTF8);
        var json = await reader.ReadToEndAsync(ct);
        try { return Ok(await contracts.ImportOpenApiAsync(environmentId, json, ct)); }
        catch (AltinnContractException ex) { return BadRequest(new { message = ex.Message }); }
    }

    /// <summary>Where each Skolenærvær/Altinn setting comes from (configured, discovered, policy evidence, run input). No secret or identifier.</summary>
    [AllowAnonymous]
    [HttpGet("configuration")]
    public async Task<ActionResult<AltinnConfigurationOverview>> Configuration([FromQuery] string environmentId,
        [FromServices] BirkNext.Api.Services.Integrations.IIntegrationMessageFlowStore messageFlows, [FromServices] AltinnTestingPolicy policy, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(environmentId) ? BadRequest(new { message = "environmentId is required." })
            : Ok(await new AltinnConfigurationOverviewService(messageFlows, policy, contracts).GetAsync(environmentId, ct));

    /// <summary>Uses the configured app's source from a Source Analysis snapshot (newest containing it, or the given one) as the authoritative
    /// contract. The app repository is uploaded through Source Analysis; this endpoint accepts no file.</summary>
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    [HttpPost("contract/source-analysis")]
    public async Task<ActionResult<AltinnAppContractView>> UseSourceAnalysis([FromQuery] string environmentId, [FromQuery] Guid? snapshotId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(environmentId)) return BadRequest(new { message = "environmentId is required." });
        try { return Ok(await contracts.UseSourceAnalysisAsync(environmentId, snapshotId, ct)); }
        catch (AltinnContractException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [Authorize(Policy = BirkNextPermissions.ActiveEventExecutePolicy)]
    [HttpGet("sign-in/status")]
    public async Task<ActionResult<AltinnSignInStatus>> Status([FromQuery] string environmentId, CancellationToken ct) =>
        Caller() is not { } caller || string.IsNullOrWhiteSpace(environmentId) ? BadRequest(new { message = "environmentId and an execution context are required." })
            : Ok(await signIn.StatusAsync(caller, environmentId, ct));

    [Authorize(Policy = BirkNextPermissions.ActiveEventExecutePolicy)]
    [HttpPost("sign-in/start")]
    public async Task<ActionResult<AltinnSignInStart>> Start([FromQuery] string environmentId, CancellationToken ct)
    {
        if (Caller() is not { } caller || string.IsNullOrWhiteSpace(environmentId)) return BadRequest(new { message = "environmentId and an execution context are required." });
        try { return Ok(await signIn.StartAsync(caller, environmentId, ct)); }
        catch (AltinnSignInException ex) { return BadRequest(new { message = ex.Message }); }
        catch (HttpRequestException) { return StatusCode(StatusCodes.Status502BadGateway, new { message = "ID-porten TEST could not be reached." }); }
    }

    [Authorize(Policy = BirkNextPermissions.ActiveEventExecutePolicy)]
    [HttpPost("sign-in/sign-out")]
    public IActionResult SignOutTester([FromQuery] string environmentId)
    {
        if (Caller() is not { } caller || string.IsNullOrWhiteSpace(environmentId)) return BadRequest(new { message = "environmentId and an execution context are required." });
        signIn.SignOut(caller, environmentId);
        return NoContent();
    }

    /// <summary>
    /// The ID-porten TEST redirect target. Anonymous by necessity (a browser redirect carries no API token); the single-use state binds it to
    /// the person who started the sign-in. The code is never logged or echoed. Afterwards the browser goes back to the configured frontend.
    /// </summary>
    [AllowAnonymous]
    [HttpGet("sign-in/callback")]
    public Task<IActionResult> Callback([FromQuery] string? code, [FromQuery] string? state, [FromQuery] string? error, CancellationToken ct) =>
        CompleteSignInAsync(code, state, error, ct);

    /// <summary>The same redirect target for response_mode=form_post (the code arrives in the body, never in a URL).</summary>
    [AllowAnonymous]
    [HttpPost("sign-in/callback")]
    [Consumes("application/x-www-form-urlencoded")]
    public Task<IActionResult> CallbackFormPost([FromForm] string? code, [FromForm] string? state, [FromForm] string? error, CancellationToken ct) =>
        CompleteSignInAsync(code, state, error, ct);

    private async Task<IActionResult> CompleteSignInAsync(string? code, string? state, string? error, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        string outcome;
        try
        {
            await signIn.CompleteAsync(code, state, error, ct);
            outcome = "complete";
        }
        catch (Exception ex) when (ex is AltinnSignInException or HttpRequestException or System.Text.Json.JsonException)
        {
            logger.LogWarning("ID-porten TEST sign-in did not complete ({ExceptionType}).", ex.GetType().Name);
            outcome = "failed";
        }
        var frontend = configuration["FRONTEND_ORIGIN"] ?? "http://localhost:5173";
        return Uri.TryCreate(frontend, UriKind.Absolute, out var origin) && origin.AbsolutePath == "/"
            ? Redirect(new Uri(origin, $"extensions/skolenaervaer?altinn-sign-in={outcome}#snt-rapport").ToString())
            : Content($"<!doctype html><title>BirkNext sign-in</title><p>ID-porten TEST sign-in {(outcome == "complete" ? "completed" : "did not complete")}. Return to BirkNext.</p>", "text/html");
    }

    private string? Caller() => IntegrationJourneysController.Actor(User, local, HttpContext);
}
