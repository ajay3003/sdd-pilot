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

    [HttpPost("disconnect")]
    public Task<GitConnectionStatus> Disconnect(CancellationToken ct) => service.DisconnectAsync(ct);

    [HttpPost("test")]
    public Task<GitTestResult> Test(CancellationToken ct) => service.TestAsync(ct);

    [HttpGet("repositories")]
    public Task<ActionResult<IReadOnlyList<GitRepository>>> Repositories(CancellationToken ct) => Run(() => service.RepositoriesAsync(ct));

    [HttpGet("repositories/{repositoryId}/branches")]
    public Task<ActionResult<IReadOnlyList<GitBranch>>> Branches(string repositoryId, CancellationToken ct) => Run(() => service.BranchesAsync(repositoryId, ct));

    /// <summary>Stages one branch's current commit through the Project Import preview (then committed with the usual import commit).</summary>
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
                GitFailureKind.SignInRequired => StatusCodes.Status401Unauthorized,
                GitFailureKind.AccessDenied => StatusCodes.Status403Forbidden,
                GitFailureKind.OrganizationNotFound or GitFailureKind.RepositoryNotFound or GitFailureKind.BranchNotFound => StatusCodes.Status404NotFound,
                GitFailureKind.TooLarge => StatusCodes.Status413PayloadTooLarge,
                GitFailureKind.Unreachable => StatusCodes.Status502BadGateway,
                _ => StatusCodes.Status502BadGateway,
            };
            return StatusCode(status, new { kind = e.Kind.ToString(), message = e.Message });
        }
    }
}
