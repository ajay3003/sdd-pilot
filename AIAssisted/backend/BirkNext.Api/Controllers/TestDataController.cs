using BirkNext.Api.Services.TestData;
using BirkNext.TestData;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

/// <summary>
/// Test Data catalog, read-only: assets, data sets and sources as safe summaries (aliases and metadata). A sensitive value is never returned;
/// a non-sensitive scalar may carry its display value, and a non-sensitive payload a bounded preview on request. XML assets that declare a
/// schema are validated with the shared XSD validator on each read; approval is never derived from that result.
/// </summary>
[ApiController]
[Route("api/test-data")]
public sealed class TestDataController(TestDataSchemaValidationService catalog) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<TestDataCatalogView>> Catalog(CancellationToken ct) => Ok(await catalog.ViewAsync(ct));

    [HttpGet("assets/{id}")]
    public async Task<ActionResult<TestDataAssetDetail>> Asset(string id, CancellationToken ct) =>
        await catalog.DetailAsync(id, ct) is { } detail ? Ok(detail) : NotFound();
}
