using BirkNext.Api.Services.TestData;
using BirkNext.TestData;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

/// <summary>
/// Test Data catalog, read-only: assets, data sets and sources as safe summaries (aliases and metadata). A sensitive value is never returned;
/// a non-sensitive scalar may carry its display value, and a non-sensitive payload a bounded preview on request.
/// </summary>
[ApiController]
[Route("api/test-data")]
public sealed class TestDataController(TestDataCatalog catalog) : ControllerBase
{
    [HttpGet]
    public ActionResult<TestDataCatalogView> Catalog() => Ok(catalog.View());

    [HttpGet("assets/{id}")]
    public ActionResult<TestDataAssetDetail> Asset(string id) => catalog.Detail(id) is { } detail ? Ok(detail) : NotFound();
}
