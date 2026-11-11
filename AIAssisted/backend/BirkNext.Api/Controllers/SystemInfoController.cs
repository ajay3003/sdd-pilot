using BirkNext.Api.Services.SystemInfo;
using BirkNext.BuildInfo;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

/// <summary>
/// The running BirkNext build: compiled identity, packaged manifest consistency, Browser Companion versions and database migration state.
/// Read-only and non-sensitive (no paths, connection strings or configuration values); behind the same local-session gate as every API.
/// Nothing is migrated or changed by reading it.
/// </summary>
[ApiController]
[Route("api/system")]
public sealed class SystemInfoController(BuildInfoService builds, RuntimeEnvironmentService? runtime = null) : ControllerBase
{
    [HttpGet("build-info")]
    public async Task<ActionResult<BuildInfoResponse>> BuildInfo(CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        return Ok(await builds.GetAsync(ct));
    }

    /// <summary>The runtime profile and what this installation runs on (database server version, k6 runner). Read-only; never switches anything.</summary>
    [HttpGet("runtime")]
    public async Task<ActionResult<RuntimeEnvironmentInfo>> Runtime(CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        if (runtime is null) return NotFound();
        return Ok(await runtime.GetAsync(ct));
    }
}
