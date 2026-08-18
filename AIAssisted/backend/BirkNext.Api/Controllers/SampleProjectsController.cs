using BirkNext.Api.Models;
using BirkNext.Api.Services;
using Microsoft.AspNetCore.Mvc;
using System.Text.RegularExpressions;
using SysPath = System.IO.Path;
using SysFile = System.IO.File;

namespace BirkNext.Api.Controllers;

[ApiController]
[Route("api/sample-projects")]
public class SampleProjectsController(ISampleProjectCatalogService catalog) : ControllerBase
{
    private static readonly Dictionary<string, (string Kind, string Reviewer, string Route)> SupportedFiles =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["constitution.md"] = ("constitution", "Constitution Explorer", "/constitution-explorer"),
            ["spec.md"]         = ("spec",         "Specification Explorer", "/specification-explorer"),
            ["data-model.md"]   = ("datamodel",    "Data Model Explorer",   "/data-model-explorer"),
            ["plan.md"]         = ("plan",         "Plan Explorer",         "/plan-explorer"),
            ["tasks.md"]        = ("tasks",        "Task Explorer",         "/task-explorer"),
        };

    private static readonly Regex SafeSlug    = new(@"^[a-zA-Z0-9_\-]+$", RegexOptions.Compiled);
    private static readonly Regex SafeFilename = new(@"^[a-zA-Z0-9_\-\.]+$", RegexOptions.Compiled);

    // ── GET /api/sample-projects ──────────────────────────────────────────────

    [HttpGet]
    public IActionResult GetProjects()
    {
        var catalogProjects = catalog.DiscoverProjects();
        var dtos = catalogProjects.Select(p => BuildProjectDto(p)).ToList();
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

    // ── GET /api/sample-projects/{slug}/file?filename=spec.md ─────────────────

    [HttpGet("{slug}/file")]
    public async Task<IActionResult> GetFile(string slug, [FromQuery] string filename)
    {
        if (!SafeSlug.IsMatch(slug))
            return BadRequest("Invalid project slug.");

        if (string.IsNullOrWhiteSpace(filename) || !SafeFilename.IsMatch(filename) ||
            filename.Contains(".."))
            return BadRequest("Invalid filename.");

        var projectDir = ResolveProjectDir(slug);
        if (projectDir is null)
            return NotFound("Project not found.");

        var filePath = SysPath.GetFullPath(SysPath.Combine(projectDir, filename));
        if (!filePath.StartsWith(projectDir + SysPath.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return Forbid();

        if (!SysFile.Exists(filePath))
            return NotFound("File not found.");

        var content = await SysFile.ReadAllTextAsync(filePath);
        return Content(content, "text/plain; charset=utf-8");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private SampleProjectDto BuildProjectDto(SampleProjectInfo info)
    {
        var files = new List<SampleFileDto>();

        // Supported artifacts (fixed order)
        foreach (var (filename, (kind, reviewer, route)) in SupportedFiles)
        {
            var exists = info.SupportedArtifacts.TryGetValue(filename, out var hasFile) && hasFile;
            files.Add(new SampleFileDto(
                Filename:      filename,
                Exists:        exists,
                ArtifactKind:  kind,
                ReviewerName:  reviewer,
                ReviewerRoute: route,
                IsSupported:   true,
                IsContextOnly: false));
        }

        // Context-only files
        foreach (var filename in info.ContextOnlyFiles.OrderBy(f => f))
        {
            files.Add(new SampleFileDto(
                Filename:      filename,
                Exists:        true,
                ArtifactKind:  null,
                ReviewerName:  null,
                ReviewerRoute: null,
                IsSupported:   false,
                IsContextOnly: true));
        }

        return new SampleProjectDto(
            Slug:         info.Slug,
            Name:         info.DisplayName,
            Domain:       info.Domain,
            Description:  info.Description,
            AbsolutePath: info.DirectoryPath,
            HasReadme:    !string.IsNullOrEmpty(info.Description),
            Files:        files);
    }

    private string? ResolveProjectDir(string slug)
    {
        var catalogProjects = catalog.DiscoverProjects();
        var project = catalogProjects.FirstOrDefault(p => p.Slug.Equals(slug, StringComparison.OrdinalIgnoreCase));
        if (project is null)
            return null;

        return SysPath.Exists(project.DirectoryPath) ? project.DirectoryPath : null;
    }
}
