using BirkNext.Api.Services.ActiveEventTesting;
using BirkNext.Api.Services.AltinnTesting;
using BirkNext.Api.Services.ExternalIntegrations;
using BirkNext.Integrations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

/// <summary>
/// Target Environment → External Integrations and its scope-aware setup import/export. Reading, detecting, exporting and preflight never
/// write; create/update/delete and Apply are explicit. A secret is accepted by POST only, held for the session and never returned.
/// </summary>
[ApiController]
[Route("api/target-environments/external-integrations")]
public sealed class ExternalIntegrationsController(ExternalIntegrationService integrations, EnvironmentSetupService setup) : ControllerBase
{
    [AllowAnonymous]
    [HttpGet("providers")]
    public ActionResult<IReadOnlyList<ExternalProviderDescriptor>> Providers() => Ok(ExternalProviderCatalog.Descriptors);

    [AllowAnonymous]
    [HttpGet]
    public async Task<ActionResult<ExternalIntegrationsView>> List([FromQuery] string environmentId, CancellationToken ct) =>
        Valid(environmentId) ? Ok(await integrations.ViewAsync(environmentId, detect: false, ct)) : BadRequest(new { message = "environmentId is required." });

    /// <summary>Detect integrations and settings: discovery only (proposals are accepted explicitly; nothing is saved).</summary>
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    [HttpPost("detect")]
    public async Task<ActionResult<ExternalIntegrationsView>> Detect([FromQuery] string environmentId, CancellationToken ct) =>
        Valid(environmentId) ? Ok(await integrations.ViewAsync(environmentId, detect: true, ct)) : BadRequest(new { message = "environmentId is required." });

    [AllowAnonymous]
    [HttpGet("{id}")]
    public async Task<ActionResult<ExternalIntegrationView>> Get(string id, [FromQuery] string environmentId, CancellationToken ct) =>
        await integrations.ViewOneAsync(environmentId, id, detect: false, ct) is { } view ? Ok(view) : NotFound(new { message = "The integration does not exist." });

    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    [HttpPost("{id}/detect")]
    public async Task<ActionResult<ExternalIntegrationView>> DetectOne(string id, [FromQuery] string environmentId, CancellationToken ct) =>
        await integrations.ViewOneAsync(environmentId, id, detect: true, ct) is { } view ? Ok(view) : NotFound(new { message = "The integration does not exist." });

    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    [HttpPost]
    public Task<ActionResult<ExternalIntegrationView>> Create([FromQuery] string environmentId, [FromBody] ExternalIntegrationRequest request, CancellationToken ct) =>
        SaveAsync(environmentId, null, request, ct);

    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    [HttpPut("{id}")]
    public Task<ActionResult<ExternalIntegrationView>> Update(string id, [FromQuery] string environmentId, [FromBody] ExternalIntegrationRequest request, CancellationToken ct) =>
        SaveAsync(environmentId, id, request, ct);

    private async Task<ActionResult<ExternalIntegrationView>> SaveAsync(string environmentId, string? id, ExternalIntegrationRequest request, CancellationToken ct)
    {
        var (saved, problems) = await integrations.SaveAsync(environmentId, id, request, "Configured manually", ct);
        return saved is null ? BadRequest(new { message = string.Join(" ", problems) }) : Ok(await integrations.ViewOneAsync(environmentId, saved.Id, detect: false, ct));
    }

    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, [FromQuery] string environmentId, CancellationToken ct) =>
        await integrations.DeleteAsync(environmentId, id, ct) ? NoContent() : NotFound(new { message = "The integration does not exist." });

    /// <summary>Adds the integration's secret for this backend session. The response never contains it.</summary>
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    [HttpPost("{id}/session-secret")]
    public async Task<IActionResult> AddSecret(string id, [FromQuery] string environmentId, [FromBody] AltinnSessionSecretRequest request, CancellationToken ct)
    {
        if (await integrations.GetAsync(environmentId, id, ct) is not { } integration) return NotFound(new { message = "The integration does not exist." });
        return integrations.SetSecret(integration, request?.Secret) is { } problem ? BadRequest(new { message = problem }) : NoContent();
    }

    /// <summary>Exports the sanitized setup of one integration, one provider or the whole environment (YAML or JSON).</summary>
    [AllowAnonymous]
    [HttpGet("setup/export")]
    public async Task<ActionResult<SetupPackageExport>> Export([FromQuery] string environmentId, [FromQuery] SetupScope scope = SetupScope.Environment,
        [FromQuery] ExternalProviderType? provider = null, [FromQuery] string? integrationId = null, [FromQuery] SetupPackageFormat format = SetupPackageFormat.Yaml,
        [FromQuery] string[]? testPack = null, CancellationToken ct = default)
    {
        if (!Valid(environmentId)) return BadRequest(new { message = "environmentId is required." });
        var (export, problem) = await setup.ExportAsync(environmentId, scope, provider, integrationId, format, testPack, ct);
        return export is null ? BadRequest(new { message = problem }) : Ok(export);
    }

    /// <summary>Validates a package and builds its complete change plan and dependency resolution for this environment. Never writes.</summary>
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    [HttpPost("setup/preflight")]
    [RequestSizeLimit(256 * 1024)]
    public async Task<ActionResult<SetupPreflightResult>> Preflight([FromQuery] string environmentId, [FromBody] SetupPreflightRequest request, CancellationToken ct) =>
        Valid(environmentId) ? Ok(await setup.PreflightAsync(environmentId, request, ct)) : BadRequest(new { message = "environmentId is required." });

    /// <summary>Applies a package all-or-nothing after re-running preflight; refused while anything required is unresolved or a conflict is undecided.</summary>
    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    [HttpPost("setup/apply")]
    [RequestSizeLimit(256 * 1024)]
    public async Task<ActionResult<SetupApplyPackageResult>> Apply([FromQuery] string environmentId, [FromBody] SetupApplyPackageRequest request, CancellationToken ct)
    {
        if (!Valid(environmentId)) return BadRequest(new { message = "environmentId is required." });
        var name = SetupPackageSchema.Parse(request.FileName, request.Content ?? "").Model?.Name ?? "setup package";
        return Ok(await setup.ApplyAsync(environmentId, request, $"Imported from file: {name}", ct));
    }

    private static bool Valid(string? environmentId) => AltinnEnvironmentSettingsStore.ValidEnvironmentId(environmentId);
}

/// <summary>Setup Library: reusable sanitized packages, immutable versions, compare, export, and preflight/apply through the same pipeline.</summary>
[ApiController]
[Route("api/setup-library")]
public sealed class SetupLibraryController(SetupLibraryService library) : ControllerBase
{
    [AllowAnonymous]
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<SetupLibraryItemView>>> List([FromQuery] ExternalProviderType? provider, [FromQuery] string? environmentType,
        [FromQuery] string? testPack, [FromQuery] SetupScope? setupType, [FromQuery] SetupLibraryStatus? status, CancellationToken ct) =>
        Ok(await library.ListAsync(provider, environmentType, testPack, setupType, status, ct));

    [AllowAnonymous]
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<SetupLibraryItemView>> Get(Guid id, CancellationToken ct) => await library.GetAsync(id, ct) is { } item ? Ok(item) : NotFound();

    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    [HttpPost]
    public async Task<ActionResult<SetupLibraryItemView>> Save([FromBody] SetupLibrarySaveRequest request, CancellationToken ct)
    {
        var (item, problem) = await library.SaveAsync(request, ct);
        return item is null ? BadRequest(new { message = problem }) : Ok(item);
    }

    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    [HttpPost("{id:guid}/versions")]
    public async Task<ActionResult<SetupLibraryItemView>> NewVersion(Guid id, [FromBody] SetupLibrarySaveRequest request, CancellationToken ct)
    {
        var (item, problem) = await library.NewVersionAsync(id, request, ct);
        return item is null ? BadRequest(new { message = problem }) : Ok(item);
    }

    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    [HttpPost("{id:guid}/duplicate")]
    public async Task<ActionResult<SetupLibraryItemView>> Duplicate(Guid id, [FromQuery] string? name, CancellationToken ct) =>
        await library.DuplicateAsync(id, name, ct) is { } item ? Ok(item) : NotFound();

    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    [HttpPost("{id:guid}/archive")]
    public async Task<IActionResult> Archive(Guid id, CancellationToken ct) => await library.ArchiveAsync(id, ct) ? NoContent() : NotFound();

    [AllowAnonymous]
    [HttpGet("{id:guid}/export")]
    public async Task<ActionResult<SetupPackageExport>> Export(Guid id, [FromQuery] int? version, [FromQuery] SetupPackageFormat format = SetupPackageFormat.Yaml, CancellationToken ct = default) =>
        await library.ExportAsync(id, version, format, ct) is { } export ? Ok(export) : NotFound();

    [AllowAnonymous]
    [HttpGet("{id:guid}/compare")]
    public async Task<ActionResult<IReadOnlyList<SetupVersionDifference>>> Compare(Guid id, [FromQuery] int from, [FromQuery] int to, CancellationToken ct) =>
        await library.CompareAsync(id, from, to, ct) is { } differences ? Ok(differences) : NotFound();

    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    [HttpPost("{id:guid}/preflight")]
    public async Task<ActionResult<SetupPreflightResult>> Preflight(Guid id, [FromQuery] string environmentId, [FromQuery] int? version, CancellationToken ct) =>
        AltinnEnvironmentSettingsStore.ValidEnvironmentId(environmentId) && await library.PreflightAsync(id, version, environmentId, ct) is { } result ? Ok(result) : NotFound();

    [Authorize(Policy = BirkNextPermissions.IntegrationConfigurationWritePolicy)]
    [HttpPost("{id:guid}/apply")]
    public async Task<ActionResult<SetupApplyPackageResult>> Apply(Guid id, [FromQuery] string environmentId, [FromQuery] int? version, [FromBody] Dictionary<string, bool>? decisions, CancellationToken ct) =>
        AltinnEnvironmentSettingsStore.ValidEnvironmentId(environmentId) && await library.ApplyAsync(id, version, environmentId, decisions, ct) is { } result ? Ok(result) : NotFound();
}
