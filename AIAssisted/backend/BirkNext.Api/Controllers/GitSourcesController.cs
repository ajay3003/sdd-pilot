using BirkNext.Api.Services.GitSources;
using BirkNext.GitSources;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

/// <summary>
/// System Settings → Git Sources. Status reads only the in-memory sign-in state; every Azure DevOps call is behind an explicit action (sign in, test,
/// refresh repositories / branches, import). Read-only against the remote; responses never carry a token.
/// </summary>
[ApiController]
[Route("api/git-sources")]
public sealed class GitSourcesController(GitSourcesService service) : ControllerBase
{
    [HttpGet("status")]
    public Task<GitConnectionStatus> Status(CancellationToken ct) => service.StatusAsync(ct);

    [HttpPut("settings")]
    public ActionResult<GitConnectionStatus> Save([FromBody] GitSourceSettings settings)
    {
        try { return service.SaveSettings(settings); }
        catch (GitSettingsException e) { return BadRequest(new { message = e.Message }); }
    }

    [HttpPost("sign-in")]
    public Task<GitConnectionStatus> SignIn(CancellationToken ct) => service.SignInAsync(ct);

    /// <summary>Device-code sign-in: the fallback when the dedicated browser cannot be opened (the code appears in status).</summary>
    [HttpPost("sign-in/device-code")]
    public Task<GitConnectionStatus> DeviceCode(CancellationToken ct) => service.StartDeviceCodeAsync(ct);

    [HttpGet("organizations")]
    public async Task<ActionResult<object>> Organizations(CancellationToken ct)
    {
        var (organizations, problem) = await service.OrganizationsAsync(ct);
        return new { organizations, problem };
    }

    [HttpPut("organizations")]
    public ActionResult<GitConnectionStatus> SelectOrganizations([FromBody] List<string> organizationUrls)
    {
        try { return service.SelectOrganizations(organizationUrls); }
        catch (GitSettingsException e) { return BadRequest(new { message = e.Message }); }
    }

    [HttpGet("projects")]
    public Task<ActionResult<GitProjectPage>> Projects([FromQuery] string organization, [FromQuery] string? continuation, CancellationToken ct) =>
        Run(() => service.ProjectsAsync(organization, continuation, ct));

    [HttpGet("projects/{projectId}/repositories")]
    public Task<ActionResult<IReadOnlyList<GitRepository>>> ProjectRepositories(string projectId, [FromQuery] string organization, CancellationToken ct) =>
        Run(() => service.ProjectRepositoriesAsync(organization, projectId, ct));

    [HttpGet("repositories/{repositoryId}/commits/{commit}")]
    public async Task<ActionResult<GitCommitInfo>> Commit(string repositoryId, string commit, [FromQuery] string? organization, CancellationToken ct)
    {
        var result = await Run(() => service.CommitAsync(organization ?? "", repositoryId, commit, ct));
        if (result.Result is { } failure) return failure;
        return result.Value is { } info ? info : NotFound(new { message = GitSourcePolicy.Describe(GitFailureKind.RevisionNotFound) });
    }

    [HttpGet("links")]
    public IReadOnlyList<GitSourceLink> Links([FromQuery] string? environmentId) => service.Links(environmentId);

    [HttpPut("links")]
    public Task<ActionResult<GitSourceLink>> Link([FromBody] GitLinkRequest request, CancellationToken ct) => Run(() => service.LinkAsync(request, ct));

    /// <summary>Removes the link only; snapshots, reports and Project Evidence stay.</summary>
    [HttpDelete("links/{linkId}")]
    public IActionResult Unlink(string linkId, [FromQuery] string environmentId) => service.Unlink(linkId, environmentId) ? NoContent() : NotFound();

    [HttpPost("links/{linkId}/check")]
    public Task<ActionResult<GitSourceLink>> Check(string linkId, [FromQuery] string environmentId, CancellationToken ct) =>
        Run(() => service.CheckForUpdatesAsync(linkId, environmentId, ct));

    [HttpGet("links/{linkId}/matches")]
    public Task<ActionResult<IReadOnlyList<GitSourceMatch>>> Matches(string linkId, [FromQuery] string environmentId, CancellationToken ct) =>
        Run(() => service.MatchesAsync(linkId, environmentId, ct));

    [HttpPost("links/{linkId}/association")]
    public ActionResult<GitSourceLink> Decide(string linkId, [FromQuery] string environmentId, [FromBody] GitAssociationRequest request) =>
        service.Decide(linkId, environmentId, request) is { } link ? link : NotFound();

    [HttpPost("disconnect")]
    public Task<GitConnectionStatus> Disconnect(CancellationToken ct) => service.DisconnectAsync(ct);

    [HttpPost("test")]
    public Task<GitTestResult> Test(CancellationToken ct) => service.TestAsync(ct);

    [HttpGet("repositories")]
    public Task<ActionResult<IReadOnlyList<GitRepository>>> Repositories(CancellationToken ct) => Run(() => service.RepositoriesAsync(ct));

    [HttpGet("repositories/{repositoryId}/branches")]
    public Task<ActionResult<IReadOnlyList<GitBranch>>> Branches(string repositoryId, [FromQuery] string? organization, CancellationToken ct) =>
        Run(() => service.BranchesAsync(repositoryId, ct, organization));

    /// <summary>Stages one branch's current commit through the Project Import preview (then committed with the usual import commit).</summary>
    [HttpPost("import")]
    public Task<ActionResult<GitImportResult>> Import([FromBody] GitImportRequest request, CancellationToken ct) => Run(() => service.ImportAsync(request, ct));

    private async Task<ActionResult<T>> Run<T>(Func<Task<T>> action)
    {
        try { return await action(); }
        catch (GitSettingsException e) { return BadRequest(new { message = e.Message }); }
        catch (NotSupportedException) { return StatusCode(StatusCodes.Status501NotImplemented, new { message = "Not available in this BirkNext instance." }); }
        catch (GitProviderException e)
        {
            var status = e.Kind switch
            {
                GitFailureKind.SignInRequired => StatusCodes.Status401Unauthorized,
                GitFailureKind.AccessDenied => StatusCodes.Status403Forbidden,
                GitFailureKind.OrganizationNotFound or GitFailureKind.RepositoryNotFound or GitFailureKind.BranchNotFound => StatusCodes.Status404NotFound,
                GitFailureKind.TooLarge => StatusCodes.Status413PayloadTooLarge,
                GitFailureKind.RevisionNotFound => StatusCodes.Status404NotFound,
                GitFailureKind.Throttled => StatusCodes.Status429TooManyRequests,
                GitFailureKind.Unreachable => StatusCodes.Status502BadGateway,
                _ => StatusCodes.Status502BadGateway,
            };
            return StatusCode(status, new { kind = e.Kind.ToString(), message = e.Message });
        }
    }
}
