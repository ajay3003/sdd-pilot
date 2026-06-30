using BirkNext.Api.Models;
using Microsoft.AspNetCore.Mvc;
using System.Text.RegularExpressions;
using SysPath = System.IO.Path;
using SysFile = System.IO.File;
using SysDir  = System.IO.Directory;

namespace BirkNext.Api.Controllers;

[ApiController]
[Route("api/sample-projects")]
public class SampleProjectsController(IConfiguration config) : ControllerBase
{
    private static readonly Dictionary<string, (string Kind, string Reviewer, string Route)> SupportedFiles =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["constitution.md"] = ("constitution", "Constitution Explorer", "/constitution-explorer"),
            ["spec.md"]         = ("spec",         "Specification Review",  "/extract"),
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
        var baseDir = config["SampleProjects:BaseDirectory"];
        if (string.IsNullOrWhiteSpace(baseDir))
            return Ok(Array.Empty<SampleProjectDto>());

        var fullBase = SysPath.GetFullPath(baseDir, AppContext.BaseDirectory);
        if (!SysDir.Exists(fullBase))
            return Ok(Array.Empty<SampleProjectDto>());

        var projects = SysDir
            .GetDirectories(fullBase)
            .OrderBy(d => d)
            .Select(dir => BuildProject(dir, fullBase))
            .ToList();

        return Ok(projects);
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

    private SampleProjectDto BuildProject(string dir, string baseDir)
    {
        var slug        = SysPath.GetFileName(dir);
        var name        = ToTitleCase(slug);
        var absPath     = SysPath.GetFullPath(dir);
        var readmePath  = FindReadme(dir);
        var hasReadme   = readmePath is not null;
        var domain      = hasReadme ? ExtractDomain(readmePath!) : string.Empty;
        var description = hasReadme ? ExtractDescription(readmePath!) : string.Empty;

        var allMd = SysDir.GetFiles(dir, "*.md", SearchOption.TopDirectoryOnly)
            .Select(f => SysPath.GetFileName(f))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var files = new List<SampleFileDto>();

        // Supported artifacts (fixed order)
        foreach (var (filename, (kind, reviewer, route)) in SupportedFiles)
        {
            var exists = allMd.Contains(filename);
            files.Add(new SampleFileDto(
                Filename:      filename,
                Exists:        exists,
                ArtifactKind:  kind,
                ReviewerName:  reviewer,
                ReviewerRoute: route,
                IsSupported:   true,
                IsContextOnly: false));
        }

        // Context-only: any .md not in supported list and not README
        var contextFiles = allMd
            .Where(f => !SupportedFiles.ContainsKey(f) &&
                        !f.Equals("README.md", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f);

        foreach (var filename in contextFiles)
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
            Slug:         slug,
            Name:         name,
            Domain:       domain,
            Description:  description,
            AbsolutePath: absPath,
            HasReadme:    hasReadme,
            Files:        files);
    }

    private string? ResolveProjectDir(string slug)
    {
        var baseDir = config["SampleProjects:BaseDirectory"];
        if (string.IsNullOrWhiteSpace(baseDir)) return null;

        var fullBase   = SysPath.GetFullPath(baseDir, AppContext.BaseDirectory);
        if (!SysDir.Exists(fullBase)) return null;

        var projectDir = SysPath.GetFullPath(SysPath.Combine(fullBase, slug));
        if (!projectDir.StartsWith(fullBase + SysPath.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return null;

        return SysDir.Exists(projectDir) ? projectDir : null;
    }

    private static string? FindReadme(string dir) =>
        SysDir.GetFiles(dir, "README.md", SearchOption.TopDirectoryOnly)
            .FirstOrDefault() ??
        SysDir.GetFiles(dir, "readme.md", SearchOption.TopDirectoryOnly)
            .FirstOrDefault();

    private static string ExtractDomain(string readmePath)
    {
        try
        {
            foreach (var line in SysFile.ReadLines(readmePath).Take(20))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("**Domain:**", StringComparison.OrdinalIgnoreCase))
                    return trimmed[11..].Trim().TrimEnd('*').Trim();
                if (trimmed.StartsWith("Domain:", StringComparison.OrdinalIgnoreCase))
                    return trimmed[7..].Trim();
            }
        }
        catch { /* ignore read errors */ }
        return string.Empty;
    }

    private static string ExtractDescription(string readmePath)
    {
        try
        {
            var nonHeading = SysFile.ReadLines(readmePath)
                .SkipWhile(l => l.TrimStart().StartsWith('#') || string.IsNullOrWhiteSpace(l))
                .FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
            if (nonHeading is not null)
                return nonHeading.Trim().TrimStart('>', '-', '*', ' ');
        }
        catch { /* ignore read errors */ }
        return string.Empty;
    }

    private static string ToTitleCase(string slug) =>
        string.Join(' ', slug.Replace('-', ' ').Replace('_', ' ')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Length > 0 ? char.ToUpperInvariant(w[0]) + w[1..] : w));
}
