using BirkNext.Api.Services.GitSources;
using BirkNext.GitSources;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

/// <summary>
/// Git Sources, Git-only (2026-11-19): the person's own Git and its credential helper connect and authenticate; BirkNext stores no credential and
/// needs no Microsoft Entra app registration. Status never contacts a remote; every remote operation is an explicit action (add, fetch latest,
/// check for updates, import) and read-only against the remote. Responses never carry a credential.
/// </summary>
[ApiController]
[Route("api/git-sources")]
public sealed class GitSourcesController(GitSourcesService service) : ControllerBase
{
    /// <summary>Git version, path and credential helper name(s); no remote call.</summary>
    [HttpGet("status")]
    public Task<GitConnectionStatus> Status(CancellationToken ct) => service.StatusAsync(ct);

    /// <summary>Git repositories below one folder the person chose (bounded; links are not followed; nothing is changed).</summary>
    [HttpPost("scan")]
    public Task<ActionResult<GitScanResult>> Scan([FromBody] GitScanRequest request, CancellationToken ct) => Run(() => service.ScanAsync(request, ct));

    [HttpPost("inspect")]
    public Task<ActionResult<GitLocalRepository>> Inspect([FromBody] GitScanRequest request, CancellationToken ct) => Run(() => service.InspectLocalAsync(request.Folder, ct));

    /// <summary>Adds a local repository or a Git URL for a BirkNext project and fetches its branches into BirkNext-owned storage.</summary>
    [HttpPost("repositories")]
    public Task<ActionResult<GitSourceLink>> Add([FromBody] GitAddRepositoryRequest request, CancellationToken ct) => Run(() => service.AddRepositoryAsync(request, ct));

    [HttpGet("links")]
    public IReadOnlyList<GitSourceLink> Links([FromQuery] string? environmentId) => service.Links(environmentId);

    public sealed record LinkUpdate(string? Branch, string? PinnedCommit, List<string>? Roles);

    [HttpPut("links/{linkId}")]
    public Task<ActionResult<GitSourceLink>> Update(string linkId, [FromQuery] string environmentId, [FromBody] LinkUpdate update) =>
        Run(() => Task.FromResult(service.UpdateLink(linkId, environmentId, update.Branch, update.PinnedCommit, update.Roles)));

    /// <summary>Removes the link only; snapshots, reports and Project Evidence stay.</summary>
    [HttpDelete("links/{linkId}")]
    public IActionResult Unlink(string linkId, [FromQuery] string environmentId) => service.Unlink(linkId, environmentId) ? NoContent() : NotFound();

    /// <summary>Fetch latest into BirkNext-owned storage (never pull, merge, rebase or push).</summary>
    [HttpPost("links/{linkId}/fetch")]
    public Task<ActionResult<GitSourceLink>> Fetch(string linkId, [FromQuery] string environmentId, CancellationToken ct) => Run(() => service.FetchAsync(linkId, environmentId, ct));

    [HttpGet("links/{linkId}/branches")]
    public Task<ActionResult<IReadOnlyList<GitBranch>>> Branches(string linkId, [FromQuery] string environmentId, CancellationToken ct) =>
        Run(() => service.BranchesAsync(linkId, environmentId, ct));

    [HttpPost("links/{linkId}/check")]
    public Task<ActionResult<GitSourceLink>> Check(string linkId, [FromQuery] string environmentId, CancellationToken ct) =>
        Run(() => service.CheckForUpdatesAsync(linkId, environmentId, ct));

    [HttpGet("links/{linkId}/matches")]
    public Task<ActionResult<IReadOnlyList<GitSourceMatch>>> Matches(string linkId, [FromQuery] string environmentId, CancellationToken ct) =>
        Run(() => service.MatchesAsync(linkId, environmentId, ct));

    [HttpPost("links/{linkId}/association")]
    public ActionResult<GitSourceLink> Decide(string linkId, [FromQuery] string environmentId, [FromBody] GitAssociationRequest request) =>
        service.Decide(linkId, environmentId, request) is { } link ? link : NotFound();

    /// <summary>Stages one exact commit through the Project Import preview (then committed with the usual import commit).</summary>
    [HttpPost("import")]
    public Task<ActionResult<GitImportResult>> Import([FromBody] GitImportRequest request, CancellationToken ct) => Run(() => service.ImportAsync(request, ct));

    private async Task<ActionResult<T>> Run<T>(Func<Task<T>> action)
    {
        try { return await action(); }
        catch (GitSettingsException e) { return BadRequest(new { message = e.Message }); }
        catch (GitProviderException e)
        {
            var status = e.Kind switch
            {
                GitFailureKind.SignInRequired or GitFailureKind.AuthenticationCancelled => StatusCodes.Status401Unauthorized,
                GitFailureKind.AccessDenied or GitFailureKind.UntrustedRepository or GitFailureKind.HostKeyNotTrusted => StatusCodes.Status403Forbidden,
                GitFailureKind.RepositoryNotFound or GitFailureKind.BranchNotFound or GitFailureKind.RevisionNotFound or GitFailureKind.NotARepository => StatusCodes.Status404NotFound,
                GitFailureKind.UnsupportedUrl => StatusCodes.Status400BadRequest,
                GitFailureKind.TooLarge => StatusCodes.Status413PayloadTooLarge,
                GitFailureKind.GitMissing => StatusCodes.Status503ServiceUnavailable,
                GitFailureKind.TimedOut => StatusCodes.Status504GatewayTimeout,
                GitFailureKind.Cancelled => StatusCodes.Status499ClientClosedRequest,
                _ => StatusCodes.Status502BadGateway,
            };
            return StatusCode(status, new { kind = e.Kind.ToString(), message = e.Message });
        }
    }
}
