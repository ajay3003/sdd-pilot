using BirkNext.Api.Services.ActiveEventTesting;
using BirkNext.Integrations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

/// <summary>
/// Whether this caller may execute active tests, for the UI to explain and enable Run. Read-only, executes nothing and records nothing; it
/// returns only the mode and a reason — never claims, identity, headers, addresses or secrets. It is a hint: every execution endpoint keeps
/// enforcing ActiveEventExecute itself.
/// </summary>
[ApiController]
[Route("api/execution-access")]
public sealed class ExecutionAccessController(LocalTrustedExecution local) : ControllerBase
{
    [AllowAnonymous]
    [HttpGet]
    public ActionResult<ExecutionAccessView> Get()
    {
        Response.Headers.CacheControl = "no-store";
        return Ok(ExecutionActor.Access(User, local.Evaluate(HttpContext)));
    }
}
