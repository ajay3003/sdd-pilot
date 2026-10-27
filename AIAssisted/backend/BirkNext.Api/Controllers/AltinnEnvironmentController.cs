using BirkNext.Api.Services.ActiveEventTesting;
using BirkNext.Api.Services.AltinnTesting;
using BirkNext.Integrations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

/// <summary>
/// Target Environment → Altinn. Reading and detecting never write and never authenticate: Detect only adds ID-porten TEST's public discovery
/// document, read anonymously and held in memory. Non-secret values are saved only by an explicit save. A client secret and synthetic
/// identities are accepted by POST only, held in backend memory for the session, and never returned, logged, persisted or exported; their
/// errors never echo the value. Trust (the trusted execution environment) stays backend configuration and is not writable here.
/// </summary>
[ApiController]
[Route("api/target-environments/altinn")]
public sealed class AltinnEnvironmentController(AltinnEnvironmentService service, AltinnEnvironmentSettingsStore store, AltinnSessionStore session, TimeProvider clock) : ControllerBase
{
    [AllowAnonymous]
    [HttpGet]
    public async Task<ActionResult<AltinnEnvironmentView>> Get([FromQuery] string environmentId, CancellationToken ct) =>
        !AltinnEnvironmentSettingsStore.ValidEnvironmentId(environmentId) ? BadRequest(new { message = "environmentId is required." }) : Ok(await service.ViewAsync(environmentId, detect: false, ct));

    /// <summary>Detect Altinn settings: a discovery action, not an execution. Nothing is saved; proposals are applied only by an explicit save.</summary>
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    [HttpPost("detect")]
    public async Task<ActionResult<AltinnEnvironmentView>> Detect([FromQuery] string environmentId, CancellationToken ct) =>
        !AltinnEnvironmentSettingsStore.ValidEnvironmentId(environmentId) ? BadRequest(new { message = "environmentId is required." }) : Ok(await service.ViewAsync(environmentId, detect: true, ct));

    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    [HttpPut("settings")]
    public async Task<ActionResult<AltinnEnvironmentView>> Save([FromQuery] string environmentId, [FromBody] AltinnEnvironmentSettingsUpdate update, CancellationToken ct)
    {
        var (_, problems) = await store.UpdateAsync(environmentId, update, ct);
        return problems.Count > 0 ? BadRequest(new { message = string.Join(" ", problems) }) : Ok(await service.ViewAsync(environmentId, detect: false, ct));
    }

    /// <summary>Records a runtime observation (e.g. a manual TT02 submission). Evidence only: it never proves BirkNext's client, secret or downstream receipt.</summary>
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    [HttpPost("observations")]
    public async Task<ActionResult<AltinnEnvironmentView>> Observe([FromQuery] string environmentId, [FromBody] AltinnRuntimeObservationRequest request, CancellationToken ct)
    {
        if (!AltinnEnvironmentSettingsStore.ValidEnvironmentId(environmentId)) return BadRequest(new { message = "environmentId is required." });
        if (!Enum.IsDefined(request.Kind)) return BadRequest(new { message = "Unknown observation kind." });
        if (request.Note is { Length: > 500 }) return BadRequest(new { message = "The note is too long." });
        await store.RecordObservationAsync(environmentId, new AltinnRuntimeObservation
        {
            Kind = request.Kind, Succeeded = request.Succeeded, Note = request.Note?.Trim(), EvidenceSource = "Manual observation",
            RecordedBy = User.Identity?.IsAuthenticated == true ? "an authenticated user" : "the local tester", ObservedAt = clock.GetUtcNow(),
        }, ct);
        return Ok(await service.ViewAsync(environmentId, detect: false, ct));
    }

    /// <summary>Records what a browser login to Altinn showed. Authorization request parameters only — a code, state, nonce or request_uri is refused.</summary>
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    [HttpPost("browser-observation")]
    public async Task<ActionResult<AltinnEnvironmentView>> ObserveBrowser([FromQuery] string environmentId, [FromBody] AltinnBrowserObservationRequest request, CancellationToken ct)
    {
        if (!AltinnEnvironmentSettingsStore.ValidEnvironmentId(environmentId)) return BadRequest(new { message = "environmentId is required." });
        if (AltinnBrowserObservation.Problem(request) is { } problem) return BadRequest(new { message = problem });
        await store.RecordBrowserObservationAsync(environmentId, AltinnBrowserObservation.From(request, clock.GetUtcNow()), ct);
        return Ok(await service.ViewAsync(environmentId, detect: false, ct));
    }

    /// <summary>Adds (or replaces) the environment's client secret for this backend session. The response never contains it.</summary>
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    [HttpPost("session/secret")]
    public IActionResult AddSecret([FromQuery] string environmentId, [FromBody] AltinnSessionSecretRequest request) =>
        session.SetSecret(environmentId, request?.Secret) is { } problem ? BadRequest(new { message = problem }) : NoContent();

    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    [HttpDelete("session")]
    public IActionResult ClearSession([FromQuery] string environmentId)
    {
        if (!AltinnEnvironmentSettingsStore.ValidEnvironmentId(environmentId)) return BadRequest(new { message = "environmentId is required." });
        session.ClearSecret(environmentId);
        return NoContent();
    }

    /// <summary>Adds a session reporter or child (Test Data). The identifier is never echoed; a problem names the rule, never the value.</summary>
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    [HttpPost("session/identities")]
    public IActionResult AddIdentity([FromBody] AltinnSessionIdentityRequest request) =>
        request is null ? BadRequest(new { message = "An identity is required." }) : session.AddIdentity(request) is { } problem ? BadRequest(new { message = problem }) : NoContent();

    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    [HttpDelete("session/identities")]
    public IActionResult ClearIdentities()
    {
        session.ClearIdentities();
        return NoContent();
    }
}

/// <summary>Validation of a recorded browser login: only non-transient authorization request parameters are accepted.</summary>
public static class AltinnBrowserObservation
{
    private static readonly string[] Transient = ["code=", "state=", "nonce=", "request_uri=", "id_token", "access_token", "code_verifier"];

    public static string? Problem(AltinnBrowserObservationRequest request)
    {
        var values = new[] { request.Issuer, request.AuthorizationEndpoint, request.AuthenticationLevel, request.ClientId, request.RedirectUri, request.PkceMethod, request.EvidenceSource }
            .Concat(request.Scopes ?? []).Where(value => !string.IsNullOrWhiteSpace(value)).ToList();
        if (values.Count == 0) return "Nothing to record.";
        if (values.Any(value => value!.Length > 500)) return "A value is too long.";
        if (values.Any(value => Transient.Any(marker => value!.Contains(marker, StringComparison.OrdinalIgnoreCase))))
            return "Transient values (authorization code, state, nonce, request_uri, tokens) are never recorded.";
        foreach (var url in new[] { request.Issuer, request.AuthorizationEndpoint, request.RedirectUri }.Where(value => !string.IsNullOrWhiteSpace(value)))
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length > 0 || uri.Query.Length > 0)
                return "URLs must be absolute https URLs without user info or query.";
        return null;
    }

    public static AltinnBrowserAuthenticationObservation From(AltinnBrowserObservationRequest request, DateTimeOffset now) => new()
    {
        Issuer = request.Issuer?.Trim(), AuthorizationEndpoint = request.AuthorizationEndpoint?.Trim(), AuthenticationLevel = request.AuthenticationLevel?.Trim(),
        ClientId = request.ClientId?.Trim(), RedirectUri = request.RedirectUri?.Trim(), PkceMethod = request.PkceMethod?.Trim(),
        Scopes = [.. (request.Scopes ?? []).Select(scope => scope.Trim()).Where(scope => scope.Length > 0).Distinct(StringComparer.Ordinal)],
        EvidenceSource = string.IsNullOrWhiteSpace(request.EvidenceSource) ? "Browser login observation" : request.EvidenceSource.Trim(), ObservedAt = now,
    };
}
