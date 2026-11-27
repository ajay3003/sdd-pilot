using BirkNext.Api.Services.Specifications.OpenSpec;
using BirkNext.Specifications;

namespace BirkNext.Api.Services.Specifications;

/// <summary>What one routed capture did. <see cref="Catalog"/> is null when nothing was captured (no specification, or a route problem).</summary>
public sealed record SpecificationRouteOutcome(SpecificationFormat Format, SpecificationCatalogView? Catalog, string? ProblemCode = null, string? Problem = null);

/// <summary>One import's specification documents as the router receives them.</summary>
/// <param name="ConfigurationFiles">YAML/JSON files of the import (only openspec/config.yaml|yml is read, as text, by the OpenSpec route).</param>
public sealed record SpecificationRouteRequest(string? ApplicationKey, string ImportId, string? ArchiveSha256, Guid? SnapshotId,
    IReadOnlyList<SpecificationCatalogService.Document> Documents, IReadOnlyList<SpecificationCatalogService.Document>? ConfigurationFiles = null);

/// <summary>
/// The single routing boundary between specification formats (2026-11-27). Reads the application's explicitly configured format
/// (<see cref="SpecificationFormatSettingsStore"/>, default Spec Kit) and sends the import's documents to exactly one route:
/// <list type="bullet">
/// <item><b>SpecKit</b> → <see cref="SpecificationCatalogService.CaptureAsync"/> with the same arguments as before this router existed (unchanged).</item>
/// <item><b>OpenSpec</b> → <see cref="OpenSpecProcessingRoute"/>. A failure is reported; it never falls back to Spec Kit, and document content never
/// overrides the setting.</item>
/// </list>
/// Readers of captured catalogs use <see cref="FormatOfCatalog"/> (the route that produced the catalog, recorded in it), so switching the setting
/// later never reinterprets what was already captured.
/// </summary>
public sealed class SpecificationProcessingRouter(SpecificationFormatSettingsStore settings, SpecificationCatalogService catalogs, OpenSpecProcessingRoute openSpec,
    ILogger<SpecificationProcessingRouter>? logger = null)
{
    /// <summary>The configured format of an application (the authoritative selection). Unknown or empty application → Spec Kit.</summary>
    public SpecificationFormat FormatFor(string? applicationKey) => settings.FormatOf(applicationKey).Format;

    /// <summary>The route that produced a captured catalog (immutable marker in its fingerprint).</summary>
    public static SpecificationFormat FormatOfCatalog(SpecificationCatalogView? catalog) => SpecificationFormatMarker.FormatOf(catalog?.Fingerprint);

    public async Task<SpecificationRouteOutcome> CaptureAsync(SpecificationRouteRequest request, CancellationToken ct)
    {
        var format = FormatFor(request.ApplicationKey);
        if (format == SpecificationFormat.SpecKit)
            return new(format, await catalogs.CaptureAsync(request.ImportId, request.ArchiveSha256, request.SnapshotId, request.Documents, ct));
        var outcome = await openSpec.CaptureAsync(request, ct);
        if (outcome.Problem is not null)
            logger?.LogWarning("OpenSpec route produced no catalog for import {ImportId}: {Code}", request.ImportId, outcome.ProblemCode);
        return outcome;
    }
}

/// <summary>
/// The OpenSpec processing route: builds the source model (<see cref="OpenSpecModelBuilder"/>), stores it, then captures the catalog of the CURRENT
/// baseline only (proposals and archives are never catalog requirements or discovery evidence). A route problem is stored for the project so the
/// UI can show it; nothing is captured then.
/// </summary>
public sealed class OpenSpecProcessingRoute(SpecificationCatalogService catalogs, OpenSpecSourceModelStore models, TimeProvider? clock = null)
{
    private DateTimeOffset Now => (clock ?? TimeProvider.System).GetUtcNow();

    public async Task<SpecificationRouteOutcome> CaptureAsync(SpecificationRouteRequest request, CancellationToken ct)
    {
        var projectKey = SpecificationCatalogService.ProjectKeyOf(request.ImportId);
        var inputs = request.Documents.Where(d => d.Path.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            .Concat((request.ConfigurationFiles ?? []).Where(c => OpenSpecSourceDiscovery.SafePath(c.Path) is { } p && OpenSpecSourceDiscovery.RootOf(p) is { } root
                && p[(root.Length + 1)..] is var rel && (rel.Equals("config.yaml", StringComparison.OrdinalIgnoreCase) || rel.Equals("config.yml", StringComparison.OrdinalIgnoreCase))))
            .Select(d => new OpenSpecSourceDiscovery.Input(d.Path, d.Content)).ToList();
        OpenSpecModelBuilder.Built built;
        try
        {
            built = OpenSpecModelBuilder.Build(projectKey, request.ApplicationKey, request.ImportId, request.ArchiveSha256, request.SnapshotId, inputs, Now, ct);
        }
        catch (OpenSpecModelBuilder.RouteProblem problem)
        {
            models.SaveProblem(projectKey, new(problem.Code, problem.Message, Now, request.ImportId));
            return new(SpecificationFormat.OpenSpec, null, problem.Code, problem.Message);
        }
        if (built.Documents.Count == 0)
        {
            const string message = "OpenSpec-mappen har ingen gjeldende spesifikasjon (openspec/specs/<capability>/spec.md). Ingen katalog er lagret.";
            models.Save(projectKey, built.Fingerprint, built.Model);
            models.SaveProblem(projectKey, new("OPENSPEC_NO_CURRENT_SPECIFICATION", message, Now, request.ImportId));
            return new(SpecificationFormat.OpenSpec, null, "OPENSPEC_NO_CURRENT_SPECIFICATION", message);
        }
        // The model first: a catalog marked OpenSpec always has its source model (unless removed later, which is then reported).
        models.Save(projectKey, built.Fingerprint, built.Model);
        var catalog = await catalogs.CaptureRoutedAsync(request.ImportId, request.ArchiveSha256, request.SnapshotId, built.Documents, built.Entries, built.Evidence, built.Fingerprint, ct);
        return new(SpecificationFormat.OpenSpec, catalog);
    }
}
