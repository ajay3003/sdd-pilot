using BirkNext.Api.Services.RuntimeAuthentication;
using BirkNext.RuntimeAuthentication;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

/// <summary>
/// Read-only authentication capability for runtime operations: the catalog of what each operation supports, and the resolved mode/state
/// for an environment. Never starts a browser, login or proxy; never returns a credential.
/// </summary>
[ApiController]
[Route("api/runtime-authentication")]
public sealed class RuntimeAuthenticationController(RuntimeAuthenticationResolver resolver) : ControllerBase
{
    [HttpGet("catalog")]
    public ActionResult<IReadOnlyList<RuntimeAuthenticationOperation>> Catalog() => Ok(RuntimeAuthenticationCatalog.All);

    [HttpPost("resolve")]
    public ActionResult<IReadOnlyList<RuntimeAuthenticationResolution>> Resolve(RuntimeAuthenticationResolveRequest request) =>
        request.Requests is not { Count: > 0 and <= 20 } ? BadRequest(new { message = "Send between 1 and 20 operations." }) : Ok(resolver.Resolve(request.Requests));
}
