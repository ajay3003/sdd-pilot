using BirkNext.Api.Services.TestCoverage;
using BirkNext.Integrations;
using BirkNext.TestCoverage;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

/// <summary>
/// Test Coverage &amp; Overlap Review: what is already tested, at which boundary, what part of each system journey is not verified, and where QA
/// may repeat developer automation. Source comes only from Source Analysis (no upload); nothing is executed; no coverage percentage.
/// </summary>
[ApiController]
[Route("api/test-coverage-review")]
public sealed class TestCoverageReviewController(ITestCoverageReviewService service) : ControllerBase
{
    [HttpGet("sources")]
    public async Task<ActionResult<ReviewSourceOptions>> Sources(CancellationToken ct) => Ok(await service.SourcesAsync(ct));

    [HttpPost("runs")]
    public async Task<ActionResult<TestCoverageReviewResult>> Run([FromBody] TestCoverageReviewRequest request, CancellationToken ct)
    {
        var outcome = await service.RunAsync(request, ct);
        return outcome.Result is { } result ? Ok(result) : Problem(detail: outcome.Error, statusCode: outcome.Status);
    }

    [HttpGet("runs")]
    public async Task<ActionResult<IReadOnlyList<TestCoverageRunSummary>>> History(CancellationToken ct) => Ok(await service.HistoryAsync(ct));

    [HttpGet("runs/{runId:guid}")]
    public async Task<ActionResult<TestCoverageReviewResult>> Get(Guid runId, CancellationToken ct) =>
        await service.GetAsync(runId, ct) is { } result ? Ok(result) : NotFound();

    [HttpGet("decisions")]
    public async Task<ActionResult<IReadOnlyList<CoverageDecision>>> Decisions([FromQuery] string repositoryKey, CancellationToken ct) =>
        Ok(await service.DecisionsAsync(repositoryKey ?? "", ct));

    [HttpPost("decisions")]
    public async Task<ActionResult<CoverageDecision>> Decide([FromBody] CoverageDecision decision, CancellationToken ct)
    {
        var (stored, error) = await service.DecideAsync(decision, ct);
        return stored is null ? Problem(detail: error, statusCode: 400) : Ok(stored);
    }
}
