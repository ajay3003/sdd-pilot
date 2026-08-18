using SysPath = System.IO.Path;
using SysDir = System.IO.Directory;
using SysFile = System.IO.File;

namespace BirkNext.Api.Services;

/// <summary>
/// Canonical Sample Project catalog discovery and metadata service.
///
/// Single source of truth for:
/// - SampleData directory resolution
/// - Project enumeration
/// - Artifact file detection
/// - README metadata extraction
///
/// Does NOT own presentation mapping; controller and page-model builders
/// consume this and format results for their respective clients.
/// </summary>
public sealed class SampleProjectCatalogService : ISampleProjectCatalogService
{
    private readonly IConfiguration _config;

    public SampleProjectCatalogService(IConfiguration config)
    {
        _config = config;
    }

    /// <summary>
    /// Discover all valid Sample Projects from the filesystem.
    /// Returns canonical project metadata: slug, display name, description, artifact status.
    /// </summary>
    public IReadOnlyList<SampleProjectInfo> DiscoverProjects()
    {
        var (basePath, _) = ResolveBaseDirectory();
        if (basePath is null || !SysDir.Exists(basePath))
            return [];

        var projects = new List<SampleProjectInfo>();
        var projectDirs = SysDir.GetDirectories(basePath).OrderBy(d => d).ToList();

        foreach (var projectDir in projectDirs)
        {
            try
            {
                var project = BuildProjectInfo(projectDir);
                if (project is not null)
                    projects.Add(project);
            }
            catch
            {
                // Skip invalid projects; continue discovery
            }
        }

        return projects;
    }

    /// <summary>
    /// Resolve the SampleData base directory.
    /// Config override takes precedence; falls back to walking up the directory tree.
    /// </summary>
    public (string? Path, string Source) ResolveBaseDirectory()
    {
        var configured = _config["SampleProjects:BaseDirectory"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var full = SysPath.GetFullPath(configured, AppContext.BaseDirectory);
            return (SysDir.Exists(full) ? full : null, "config");
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = SysPath.Combine(dir.FullName, "SampleData");
            if (SysDir.Exists(candidate))
                return (candidate, "auto");
            dir = dir.Parent;
        }

        return (null, "auto");
    }

    private SampleProjectInfo? BuildProjectInfo(string projectDir)
    {
        var slug = SysPath.GetFileName(projectDir);
        var displayName = ToTitleCase(slug);
        var readmePath = FindReadme(projectDir);
        var domain = readmePath is not null ? ExtractDomain(readmePath) : string.Empty;
        var description = readmePath is not null ? ExtractDescription(readmePath) : string.Empty;

        var allMd = SysDir.GetFiles(projectDir, "*.md", SearchOption.TopDirectoryOnly)
            .Select(f => SysPath.GetFileName(f))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var supportedArtifacts = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
        {
            ["constitution.md"] = allMd.Contains("constitution.md"),
            ["spec.md"] = allMd.Contains("spec.md"),
            ["data-model.md"] = allMd.Contains("data-model.md"),
            ["plan.md"] = allMd.Contains("plan.md"),
            ["tasks.md"] = allMd.Contains("tasks.md"),
        };

        var contextOnlyFiles = allMd
            .Where(f => !supportedArtifacts.ContainsKey(f) &&
                       !f.Equals("README.md", StringComparison.OrdinalIgnoreCase))
            .ToList();

        return new SampleProjectInfo(
            Slug: slug,
            DisplayName: displayName,
            Domain: domain,
            Description: description,
            DirectoryPath: SysPath.GetFullPath(projectDir),
            SupportedArtifacts: supportedArtifacts,
            ContextOnlyFiles: contextOnlyFiles);
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

public interface ISampleProjectCatalogService
{
    IReadOnlyList<SampleProjectInfo> DiscoverProjects();
    (string? Path, string Source) ResolveBaseDirectory();
}

/// <summary>
/// Canonical metadata for a discovered Sample Project.
/// Used by controller and page-model builder to format endpoint-specific responses.
/// </summary>
public sealed record SampleProjectInfo(
    string Slug,
    string DisplayName,
    string Domain,
    string Description,
    string DirectoryPath,
    IReadOnlyDictionary<string, bool> SupportedArtifacts,
    IReadOnlyList<string> ContextOnlyFiles);
