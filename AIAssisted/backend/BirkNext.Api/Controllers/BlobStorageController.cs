using BirkNext.Api.Services.ActiveEventTesting;
using BirkNext.Api.Services.BlobStorage;
using BirkNext.BlobStorage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

/// <summary>
/// Read-only Blob Storage explorer over stores configured per environment (no store is configured by default). Every endpoint reads; nothing
/// uploads, overwrites, deletes, leases, snapshots, changes tiers or metadata, generates a SAS or fetches a caller-supplied URL. Deleting an
/// observation removes BirkNext's local record only.
/// </summary>
[ApiController]
[Route("api/blob-storage")]
public sealed class BlobStorageController(BlobExplorerService blobs) : ControllerBase
{
    private string Initiator => User.Identity?.IsAuthenticated == true && User.Identity.Name is { Length: > 0 } name ? name : "Local session";

    [HttpGet("stores")]
    public ActionResult<List<BlobStoreView>> Stores([FromQuery] string environmentId) =>
        string.IsNullOrWhiteSpace(environmentId) ? BadRequest("environmentId is required.") : Ok(blobs.Stores(environmentId));

    [HttpGet("stores/{storeId}/containers")]
    public async Task<ActionResult<List<BlobContainerView>>> Containers([FromQuery] string environmentId, string storeId, CancellationToken ct) =>
        await blobs.ContainersAsync(environmentId, storeId, ct) is var (result, error) && result is not null ? Ok(result) : UnprocessableEntity(new { message = error });

    [HttpGet("stores/{storeId}/containers/{container}/blobs")]
    public async Task<ActionResult<BlobListing>> List([FromQuery] string environmentId, string storeId, string container, [FromQuery] string? prefix, [FromQuery] string? continuation,
        [FromQuery] int pageSize = 100, CancellationToken ct = default) =>
        await blobs.ListAsync(environmentId, storeId, container, prefix, continuation, pageSize, ct) is var (result, error) && result is not null ? Ok(result) : UnprocessableEntity(new { message = error });

    [HttpGet("stores/{storeId}/containers/{container}/properties")]
    public async Task<ActionResult<BlobProperties>> Properties([FromQuery] string environmentId, string storeId, string container, [FromQuery] string name, CancellationToken ct) =>
        await blobs.PropertiesAsync(environmentId, storeId, container, name, ct) is var (result, error) && result is not null ? Ok(result) : UnprocessableEntity(new { message = error });

    [HttpGet("stores/{storeId}/containers/{container}/preview")]
    public async Task<ActionResult<BlobPreview>> Preview([FromQuery] string environmentId, string storeId, string container, [FromQuery] string name, CancellationToken ct) =>
        await blobs.PreviewAsync(environmentId, storeId, container, name, ct) is var (result, error) && result is not null ? Ok(result) : UnprocessableEntity(new { message = error });

    /// <summary>Checks an expected artifact (read-only). Saving the observation needs the write permission: use POST observations.</summary>
    [HttpPost("validate")]
    public async Task<ActionResult<BlobValidation>> Validate([FromBody] BlobValidateRequest request, CancellationToken ct)
    {
        var (validation, _, error) = await blobs.ValidateAsync(request with { Save = false }, Initiator, ct);
        return validation is null ? UnprocessableEntity(new { message = error }) : Ok(validation);
    }

    [HttpPost("observations")]
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    public async Task<ActionResult<BlobObservation>> Save([FromBody] BlobValidateRequest request, CancellationToken ct)
    {
        var (_, saved, error) = await blobs.ValidateAsync(request with { Save = true }, Initiator, ct);
        return saved is null ? UnprocessableEntity(new { message = error ?? "Nothing to save: the blob was not found." }) : Ok(saved);
    }

    [HttpGet("observations")]
    public async Task<ActionResult<List<BlobObservation>>> Observations([FromQuery] string environmentId, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(environmentId) ? BadRequest("environmentId is required.") : Ok(await blobs.ObservationsAsync(environmentId, ct));

    /// <summary>Removes BirkNext's local observation; the blob itself is never touched.</summary>
    [HttpDelete("observations/{id:guid}")]
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    public async Task<IActionResult> DeleteObservation([FromQuery] string environmentId, Guid id, CancellationToken ct) =>
        await blobs.DeleteObservationAsync(environmentId, id, ct) ? NoContent() : NotFound();
}
