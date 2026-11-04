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

    /// <summary>Compares a Live Discovery value with approved Test Data in the backend; returns an alias or nothing (never a value). Not stored.</summary>
    [HttpPost("observed-value/match")]
    [RequestSizeLimit(16 * 1024)]
    public ActionResult<ObservedValueMatchResult> MatchObservedValue([FromBody] ObservedValueMatchRequest request) => Ok(service.MatchObservedValue(request));

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

    /// <summary>
    /// Test Data → Create (missing) XML fixture / Regenerate for a canonical fixture: stores the validated XML as the next version of the one
    /// canonical asset (same alias, same id) in the backend canonical fixture store — never in a browser workspace. 409 when it is not canonical
    /// or not valid; approval is separate.
    /// </summary>
    [HttpPost("canonical-fixtures")]
    public ActionResult SaveCanonicalFixture([FromBody] System.Text.Json.Nodes.JsonObject item)
    {
        var (version, error) = service.SaveCanonicalFixture(item);
        return version is { } stored ? Ok(new { version = stored }) : Conflict(new { message = error });
    }

    /// <summary>Approves one version (default: the current one). Invalid XML is refused; a draft version becomes current under the same asset id.</summary>
    [HttpPost("assets/{id}/approve")]
    public async Task<IActionResult> Approve(string id, [FromQuery] int? version, CancellationToken ct) =>
        await service.ApproveAsync(id, version, ct) is { } error ? BadRequest(new { message = error }) : Ok(new { assetId = id, version });

    /// <summary>Renames a registered asset's alias; its TestDataAssetId and references are kept, the old alias stays as history.</summary>
    [HttpPost("assets/{id}/rename")]
    public ActionResult<TestDataAssetSummary> Rename([FromServices] TestDataLifecycleService lifecycle, string id, [FromBody] TestDataRenameRequest request)
    {
        var (asset, error) = lifecycle.RenameAlias(id, request.Alias);
        return asset is not null ? Ok(asset) : error == "Unknown asset." ? NotFound(new { message = error }) : Conflict(new { message = error });
    }

    /// <summary>The stable identity of an asset: TestDataAssetId, legacy ids, alias history and persisted validation evidence (no values).</summary>
    [HttpGet("identities/{reference}")]
    public ActionResult<TestDataAssetIdentity> Identity([FromServices] TestDataCatalog testData, string reference) =>
        testData.Identities?.Resolve(reference) is { } identity ? Ok(identity) : NotFound();

    /// <summary>Validates an asset version on the server and records the evidence (trust level, schema identity, engine, timestamp).</summary>
    [HttpPost("assets/{id}/validate")]
    public async Task<ActionResult<TestDataValidationEvidence>> Validate([FromServices] TestDataCatalog testData, [FromServices] TimeProvider clock, string id, [FromQuery] int? version, CancellationToken ct)
    {
        if (testData.ResolveReference(id) is not { } asset || testData.VersionOf(asset, version) is not { } target) return NotFound();
        return await catalog.ValidateAndRecordAsync(target with { AssetId = asset.AssetId }, clock, ct) is { } evidence ? Ok(evidence) : BadRequest(new { message = "The asset declares no XSD." });
    }

    [HttpGet("schema-associations")]
    public ActionResult<IReadOnlyList<TestDataSchemaAssociationView>> Associations([FromServices] TestDataSchemaAssociationStore store) =>
        Ok(store.All().Select(TestDataSchemaAssociationStore.View).ToList());

    /// <summary>Explicitly associates an XSD package (text files) with a schema path, stored server-side with fingerprints. Used only when no stored
    /// Source Analysis snapshot retains the schema; results are labelled "explicit schema reference", never "verified schema".</summary>
    [HttpPost("schema-associations")]
    public ActionResult<TestDataSchemaAssociationView> Associate([FromServices] TestDataSchemaAssociationStore store, [FromServices] TimeProvider clock, [FromBody] TestDataSchemaAssociationRequest request)
    {
        var (association, error) = store.Associate(request, User.Identity?.IsAuthenticated == true ? User.Identity.Name ?? "an authenticated user" : "the local tester", clock.GetUtcNow());
        return association is null ? BadRequest(new { message = error }) : Ok(TestDataSchemaAssociationStore.View(association));
    }

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

    // ── Asset lifecycle: references, Edit, Restore default value, Delete, Replace references, Reset Test Data ──────────────────────

    /// <summary>Every place an asset alias is referenced (performance scenarios, Critical E2E flows, explicit bindings), unresolved ones marked.</summary>
    [HttpGet("references")]
    public async Task<ActionResult<IReadOnlyList<TestDataAssetUsage>>> References([FromServices] TestDataLifecycleService lifecycle, CancellationToken ct) =>
        Ok(await lifecycle.UsagesAsync(ct));

    /// <summary>The Edit surface. Carries the actual value only for an asset whose value Test Data may edit (protected default, session entry).</summary>
    [HttpGet("assets/{id}/edit")]
    public async Task<ActionResult<TestDataEditView>> EditView([FromServices] TestDataLifecycleService lifecycle, string id, CancellationToken ct) =>
        await lifecycle.EditViewAsync(id, ct) is { } view ? Ok(view) : NotFound();

    [HttpPut("assets/{id}")]
    [RequestSizeLimit(16 * 1024)]
    public async Task<ActionResult<TestDataEditResult>> Edit([FromServices] TestDataLifecycleService lifecycle, string id, [FromBody] TestDataEditRequest request, CancellationToken ct)
    {
        var (result, notFound) = await lifecycle.EditAsync(id, request, ct);
        return notFound ? NotFound(result) : result.Updated || result.Message == "Nothing changed." ? Ok(result) : UnprocessableEntity(result);
    }

    [HttpPost("assets/{id}/restore-default")]
    public async Task<ActionResult<TestDataEditResult>> RestoreDefault([FromServices] TestDataLifecycleService lifecycle, string id, CancellationToken ct)
    {
        var (result, notFound) = await lifecycle.RestoreDefaultAsync(id, ct);
        return notFound ? NotFound(result) : result.Updated ? Ok(result) : Conflict(result);
    }

    /// <summary>Deletes a local asset. Protected defaults and configuration-owned assets are always refused; an asset in use is refused unless
    /// <paramref name="leaveReferencesUnresolved"/> is set, and then its references stay (unresolved) — consumers are never deleted.</summary>
    [HttpDelete("assets/{id}")]
    public async Task<ActionResult<TestDataDeleteResult>> Delete([FromServices] TestDataLifecycleService lifecycle, string id, [FromQuery] bool leaveReferencesUnresolved, CancellationToken ct)
    {
        var (result, status) = await lifecycle.DeleteAsync(id, leaveReferencesUnresolved, ct);
        return StatusCode(status, result);
    }

    /// <summary>The compatible replacements of an asset (same type, data type and role, compatible scope, approved). Never a "closest" guess.</summary>
    [HttpGet("assets/{id}/replacements")]
    public ActionResult<IReadOnlyList<TestDataAssetSummary>> Replacements([FromServices] TestDataLifecycleService lifecycle, string id) => Ok(lifecycle.Replacements(id));

    /// <summary>Re-points every reference of the asset to a compatible approved replacement (aliases only; no value is copied).</summary>
    [HttpPost("assets/{id}/replace")]
    public async Task<ActionResult<TestDataReplaceResult>> Replace([FromServices] TestDataLifecycleService lifecycle, string id, [FromBody] TestDataReplaceRequest request, CancellationToken ct)
    {
        var (result, status) = await lifecycle.ReplaceAsync(id, request.ReplacementAssetId, ct);
        return StatusCode(status, result);
    }

    [HttpGet("reset/preview")]
    public async Task<ActionResult<TestDataResetPreview>> ResetPreview([FromServices] TestDataLifecycleService lifecycle, CancellationToken ct) =>
        Ok(await lifecycle.PreviewResetAsync(ct));

    /// <summary>Reset Test Data only (Maintenance): protected defaults keep their current values; local assets are removed; consumers stay.</summary>
    [HttpPost("reset")]
    public async Task<ActionResult<TestDataResetResult>> Reset([FromServices] TestDataLifecycleService lifecycle, [FromQuery] int generatedFixturesRemoved, CancellationToken ct) =>
        Ok(await lifecycle.ResetAsync(Math.Max(0, generatedFixturesRemoved), ct));
}
