using BirkNext.Api.Services.ActiveEventTesting;
using BirkNext.Api.Services.SystemUnderstanding;
using BirkNext.SystemUnderstanding;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

/// <summary>
/// System Understanding guides (explanations of the system under test). Reading needs only the local session like every API; changing a draft,
/// submitting, approving and publishing need the configuration-write permission (<see cref="BirkNextPermissions.IntegrationConfigurationWritePolicy"/>).
/// Published revisions are never changed; the database refuses it as well.
/// </summary>
[ApiController]
[Route("api/system-understanding")]
public sealed class SystemUnderstandingController(SystemGuideService guides, IAuthorizationService authorization) : ControllerBase
{
    private string Actor => User.Identity is { IsAuthenticated: true, Name: { Length: > 0 } name } ? name : "Local user (this PC)";

    private async Task<bool> CanWriteAsync() =>
        (await authorization.AuthorizeAsync(User, null, BirkNextPermissions.IntegrationConfigurationWritePolicy)).Succeeded;

    [HttpGet("guides")]
    public async Task<ActionResult<IReadOnlyList<SystemGuideSummary>>> List(CancellationToken ct) => Ok(await guides.ListAsync(ct));

    [HttpGet("guides/{guideKey}")]
    public Task<ActionResult> Get(string guideKey, CancellationToken ct) => Run(async () => Ok(await guides.GetAsync(guideKey, await CanWriteAsync(), ct)));

    [HttpGet("guides/{guideKey}/revisions/{revision:int}")]
    public Task<ActionResult> Revision(string guideKey, int revision, CancellationToken ct) => Run(async () => Ok(await guides.RevisionAsync(guideKey, revision, ct)));

    [HttpGet("guides/{guideKey}/candidates")]
    public Task<ActionResult> Candidates(string guideKey, CancellationToken ct) => Run(async () => Ok(await guides.CandidatesAsync(guideKey, ct)));

    [HttpPost("guides/{guideKey}/candidates/{candidateId:guid}/draft")]
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    public Task<ActionResult> CandidateDraft(string guideKey, Guid candidateId, CancellationToken ct) =>
        Run(async () => Ok(await guides.StartDraftFromCandidateAsync(guideKey, candidateId, Actor, ct)));

    /// <summary>A reviewer's decision on one proposed change of a source candidate (accept, reject, edit, defer, needs clarification).</summary>
    [HttpPost("guides/{guideKey}/candidates/{candidateId:guid}/decisions")]
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    public Task<ActionResult> Decide(string guideKey, Guid candidateId, [FromBody] DecideGuideChangeRequest request, CancellationToken ct) =>
        Run(async () => Ok(await guides.DecideAsync(guideKey, candidateId, request, Actor, ct)));

    /// <summary>Merges the decided changes into the open draft (never overwriting it) or a new draft. Never approves or publishes.</summary>
    [HttpPost("guides/{guideKey}/candidates/{candidateId:guid}/apply")]
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    public Task<ActionResult> Apply(string guideKey, Guid candidateId, [FromBody] ApplyGuideCandidateRequest request, CancellationToken ct) =>
        Run(async () => Ok(await guides.ApplyCandidateAsync(guideKey, candidateId, request, Actor, ct)));

    /// <summary>The guide's generation jobs (pending, running, succeeded, failed with diagnostics, stale).</summary>
    [HttpGet("guides/{guideKey}/generation")]
    public Task<ActionResult> Generation(string guideKey, CancellationToken ct) => Run(async () => Ok(await guides.JobsAsync(guideKey, ct)));

    /// <summary>Runs a failed or stale generation again.</summary>
    [HttpPost("guides/{guideKey}/generation/{jobId:guid}/retry")]
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    public Task<ActionResult> Retry(string guideKey, Guid jobId, CancellationToken ct) => Run(async () => Ok(await guides.RetryJobAsync(guideKey, jobId, ct)));

    [HttpPut("guides/{guideKey}/draft")]
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    public Task<ActionResult> SaveDraft(string guideKey, [FromBody] SaveGuideDraftRequest request, CancellationToken ct) =>
        Run(async () => Ok(await guides.SaveDraftAsync(guideKey, request, Actor, ct)));

    [HttpPost("guides/{guideKey}/revisions/{revision:int}/submit")]
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    public Task<ActionResult> Submit(string guideKey, int revision, CancellationToken ct) => Run(async () => Ok(await guides.SubmitAsync(guideKey, revision, Actor, ct)));

    [HttpPost("guides/{guideKey}/revisions/{revision:int}/return-to-draft")]
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    public Task<ActionResult> ReturnToDraft(string guideKey, int revision, CancellationToken ct) => Run(async () => Ok(await guides.ReturnToDraftAsync(guideKey, revision, ct)));

    [HttpPost("guides/{guideKey}/revisions/{revision:int}/approve")]
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    public Task<ActionResult> Approve(string guideKey, int revision, CancellationToken ct) => Run(async () => Ok(await guides.ApproveAsync(guideKey, revision, Actor, ct)));

    [HttpPost("guides/{guideKey}/revisions/{revision:int}/publish")]
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    public Task<ActionResult> Publish(string guideKey, int revision, CancellationToken ct) => Run(async () => Ok(await guides.PublishAsync(guideKey, revision, ct)));

    private async Task<ActionResult> Run(Func<Task<ActionResult>> action)
    {
        try { return await action(); }
        catch (SystemGuideException e) { return StatusCode(e.Status, new { message = e.Message }); }
    }
}
