using BirkNext.Api.Models;
using BirkNext.Api.Services;
using BirkNext.Api.Services.SampleProjects;
using Microsoft.AspNetCore.Mvc;
using System.Text.RegularExpressions;

namespace BirkNext.Api.Controllers;

/// <summary>
/// Sample Project catalog and read-only document access. The catalog returns each project's recursive file inventory;
/// it does not assume fixed filenames, folders or a mandatory artifact set. Artifact roles are classified on the client.
/// </summary>
[ApiController]
[Route("api/sample-projects")]
public class SampleProjectsController(ISampleProjectCatalogService catalog) : ControllerBase
{
    private static readonly Regex SafeSlug = new(@"^[a-zA-Z0-9_\-]+$", RegexOptions.Compiled);

    // ── GET /api/sample-projects ──────────────────────────────────────────────

    [HttpGet]
    public IActionResult GetProjects()
    {
        var catalogProjects = catalog.DiscoverProjects();
        var dtos = catalogProjects.Select(BuildProjectDto).ToList();
        return Ok(dtos);
    }

    // ── GET /api/sample-projects/meta ─────────────────────────────────────────

    [HttpGet("meta")]
    public IActionResult GetMeta()
    {
        var (path, source) = catalog.ResolveBaseDirectory();
        return Ok(new SampleProjectsMetaDto(
            ResolvedPath: path,
            Source: source,
            Exists: path is not null));
    }

    // ── GET /api/sample-projects/{slug}/file?filename=specs/001-x/spec.md ─────

    /// <summary>
    /// Reads one document. <paramref name="filename"/> is a project-relative path; only readable candidate documents of
    /// the project's own inventory are served, so traversal, absolute paths, links and unlisted files are refused.
    /// </summary>
    [HttpGet("{slug}/file")]
    public async Task<IActionResult> GetFile(string slug, [FromQuery] string filename, CancellationToken ct)
    {
        if (!SafeSlug.IsMatch(slug))
            return BadRequest("Invalid project slug.");
        if (SampleProjectDocumentInventory.NormalizeRelative(filename) is null)
            return BadRequest("Invalid filename.");

        var project = catalog.FindProject(slug);
        if (project is null)
            return NotFound("Project not found.");

        var file = SampleProjectDocumentInventory.FindReadableDocument(project.Inventory, filename);
        if (file is null)
            return NotFound("File not found.");

        var content = await SampleProjectDocumentInventory.ReadDocumentAsync(project.DirectoryPath, file, ct);
        return content is null ? NotFound("File not found.") : Content(content, "text/plain; charset=utf-8");
    }

    // ── GET /api/sample-projects/{slug}/documents ─────────────────────────────

    /// <summary>All readable candidate documents of one project, in path order, for client-side role classification.</summary>
    [HttpGet("{slug}/documents")]
    public async Task<IActionResult> GetDocuments(string slug, CancellationToken ct)
    {
        if (!SafeSlug.IsMatch(slug))
            return BadRequest("Invalid project slug.");

        var project = catalog.FindProject(slug);
        if (project is null)
            return NotFound("Project not found.");

        var result = new List<SampleDocumentContentDto>();
        foreach (var file in project.Inventory.Files.Where(f => f.IsDocument && f.SkipReason is null))
        {
            try
            {
                var content = await SampleProjectDocumentInventory.ReadDocumentAsync(project.DirectoryPath, file, ct);
                result.Add(new SampleDocumentContentDto(file.RelativePath, content, content is null ? "The document could not be read." : null));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                result.Add(new SampleDocumentContentDto(file.RelativePath, null, "The document could not be read."));
            }
        }
        return Ok(result);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static SampleProjectDto BuildProjectDto(SampleProjectInfo info)
    {
        var inventory = info.Inventory;
        var files = inventory.Files.Select(f => new SampleFileDto(
            Filename: f.FileName,
            Exists: true,
            ArtifactKind: null,
            ReviewerName: null,
            ReviewerRoute: null,
            IsSupported: f.IsDocument && f.SkipReason is null,
            IsContextOnly: false,
            RelativePath: f.RelativePath,
            SizeBytes: f.SizeBytes,
            LastModifiedUtc: f.LastModifiedUtc,
            SkipReason: f.SkipReason)).ToList();

        return new SampleProjectDto(
            Slug:         info.Slug,
            Name:         info.DisplayName,
            Domain:       info.Domain,
            Description:  info.Description,
            AbsolutePath: info.DirectoryPath,
            HasReadme:    !string.IsNullOrEmpty(info.Description),
            Files:        files,
            Discovery:    new SampleDiscoveryStatsDto(inventory.FilesScanned, inventory.DocumentCount,
                              inventory.IgnoredDirectories, inventory.SkippedLinks, inventory.Truncated));
    }
}
