using BirkNext.Api.Services.TestData;
using BirkNext.TestData;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

/// <summary>
/// Test Data catalog: assets, data sets and sources as safe summaries (aliases and metadata), plus what Test Data owns — explicit approval and
/// deprecation, registration of reviewed source candidates, inbound consumer requirements, side-effect-free matching and explicit usage
/// references. A sensitive value is never returned; a non-sensitive scalar may carry its display value, and a non-sensitive payload a bounded
/// preview on request. XML assets that declare a schema are validated with the shared XSD validator on each read; approval is never derived
/// from that result. Consumers are generic (type + stable id + display name).
/// </summary>
[ApiController]
[Route("api/test-data")]
public sealed class TestDataController(TestDataSchemaValidationService catalog, TestDataCatalogService service) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<TestDataCatalogView>> Catalog(CancellationToken ct) => Ok(await service.ViewAsync(ct));

    [HttpGet("assets/{id}")]
    public async Task<ActionResult<TestDataAssetDetail>> Asset(string id, CancellationToken ct) =>
        await catalog.DetailAsync(id, ct) is { } detail ? Ok(detail) : NotFound();

    /// <summary>Structural candidates from one exact Source Analysis snapshot (shared contract and data model evidence). Candidates are never
    /// approved, stored or used by being listed; identity fields carry a controlled-reference placeholder, never a generated identity.</summary>
    [HttpGet("candidates")]
    public async Task<ActionResult<IReadOnlyList<TestDataCandidate>>> Candidates([FromServices] TestDataCandidateService candidates, [FromQuery] Guid snapshotId, [FromQuery] string? environmentId, CancellationToken ct) =>
        await candidates.CandidatesAsync(environmentId ?? "", snapshotId, ct) is { } list ? Ok(list) : NotFound();

    /// <summary>Registers asset metadata (a reviewed candidate, re-derived on the server, or a manual entry). Never approves it.</summary>
    [HttpPost("registrations")]
    public async Task<IActionResult> Register([FromBody] TestDataRegistrationRequest request, CancellationToken ct)
    {
        var (asset, existing, error) = await service.RegisterAsync(request, ct);
        return asset is not null ? Ok(new { assetId = asset.Id }) : existing is not null ? Conflict(new { message = error, existingAssetId = existing }) : BadRequest(new { message = error });
    }

    /// <summary>Test Data → Create XML fixture: fills generated XML with Test Data values by alias (resolved here, never sent by the browser).</summary>
    [HttpPost("xml-fixtures/bind")]
    public ActionResult<TestDataXmlBindResult> BindXmlFixture([FromServices] TestDataCatalog testData, [FromBody] TestDataXmlBindRequest request)
    {
        var result = TestDataXmlBinder.Bind(request, testData.Assets());
        return result.Xml is not null ? Ok(result) : BadRequest(result);
    }

    [HttpPost("assets/{id}/approve")]
    public IActionResult Approve(string id) => service.Approve(id) is { } error ? BadRequest(new { message = error }) : Ok(new { assetId = id });

    [HttpPost("assets/{id}/deprecate")]
    public IActionResult Deprecate(string id) => service.Deprecate(id) is { } error ? BadRequest(new { message = error }) : Ok(new { assetId = id });

    /// <summary>Consumer requirements and usage references, with the test lead's overview.</summary>
    [HttpGet("consumers")]
    public async Task<ActionResult<TestDataConsumerView>> Consumers(CancellationToken ct) => Ok(await service.ConsumersAsync(ct));

    [HttpPost("requirements")]
    public IActionResult AddRequirement([FromBody] ConsumerTestDataRequirement requirement)
    {
        var (stored, error) = service.AddRequirement(requirement);
        return stored is null ? BadRequest(new { message = error }) : Ok(stored);
    }

    [HttpDelete("requirements/{id}")]
    public IActionResult RemoveRequirement(string id) => service.RemoveRequirement(id) ? NoContent() : NotFound();

    /// <summary>Suggestions for a requirement (registered or ad hoc). Side-effect free: nothing is bound.</summary>
    [HttpPost("matches")]
    public async Task<ActionResult<List<TestDataMatch>>> Matches([FromBody] ConsumerTestDataRequirement requirement, [FromQuery] bool includeDeprecated, CancellationToken ct) =>
        Ok(await service.MatchesAsync(requirement, includeDeprecated, ct));

    /// <summary>An explicit binding of an approved asset to a consumer usage (asset id + version). The value is never copied.</summary>
    [HttpPost("usages")]
    public async Task<IActionResult> Bind([FromBody] TestDataUsageReference usage, CancellationToken ct)
    {
        var (stored, error) = await service.BindAsync(usage, ct);
        return stored is null ? BadRequest(new { message = error }) : Ok(stored);
    }

    [HttpDelete("usages/{id}")]
    public IActionResult Unbind(string id) => service.Unbind(id) ? NoContent() : NotFound();
}
