using BirkNext.Dependencies;

namespace BirkNext.Web.Services;

/// <summary>Where a package's direct project usage was found, always relative to the selected source scope.</summary>
public enum DependencyUsageState { UsedInCurrent, UsedInRelated, UsedInCurrentAndRelated, NoUsageFound, Unresolved }

/// <summary>One project that directly references a package. <see cref="Version"/> comes only from that project's own source — never from another repository.</summary>
public sealed record ProjectPackageUsage(string Repository, bool Related, string Project, string Path, string? Version);

public sealed record DependencyUsageRow(string PackageName, string Manager, IReadOnlyList<string> Versions, IReadOnlyList<string> DeclaredIn, string Renovate,
    DependencyUsageState State, IReadOnlyList<ProjectPackageUsage> Projects, IReadOnlyList<string> UnresolvedVia);

public sealed record DependencyUsageView(IReadOnlyList<DependencyUsageRow> Rows, int CurrentProjects, int RelatedProjects, bool HasRelated);

/// <summary>
/// Direct NuGet usage per project, read from the review's stored declarations: Directory.Packages.props PackageVersion entries carry the csproj
/// files that reference them, and versioned PackageReferences are owned by their project. ProjectReference is not a package and never appears;
/// transitive packages are not inventoried. Wording is scope-aware: nothing here can say a package is unused outside the selected snapshots.
/// </summary>
public static class DependencyUsagePresentation
{
    public const string OutsideScope = "Usage outside this source scope is unknown.";
    public const string TransitiveNotAssessed = "Direct project usage: assessed · Transitive package usage: not assessed (no restore graph).";

    /// <summary>Project display name: the csproj file name without extension; the full path stays available for details.</summary>
    public static string ProjectName(string path)
    {
        var file = path.Replace('\\', '/').Split('/').Last();
        return file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ? file[..^7] : file;
    }

    private static bool IsProject(string path) => path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase);

    /// <summary>Package references only (central PackageVersion or PackageReference); SDKs, tools and container images are not project usage.</summary>
    public static bool IsPackageReference(DeclaredDependency d) => d.Manager == "nuget" && d.DepType == "nuget";

    public static DependencyUsageView Build(DependencyReviewResult result)
    {
        var related = (result.SourceScope?.Related ?? []).Select(r => r.Repository).ToHashSet(StringComparer.Ordinal);
        bool IsRelated(string repository) => related.Contains(repository);
        var rows = result.Repositories
            .SelectMany(repo => repo.Dependencies.Where(IsPackageReference).Select(d => (Repo: repo.Repository, Dep: d)))
            .GroupBy(x => x.Dep.PackageName, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var projects = g.SelectMany(x => x.Dep.ReferencedBy.Where(IsProject).Select(p => new ProjectPackageUsage(x.Repo, IsRelated(x.Repo), ProjectName(p), p, x.Dep.CurrentValue)))
                    .DistinctBy(u => (u.Repository, u.Path)).OrderBy(u => u.Related).ThenBy(u => u.Project, StringComparer.Ordinal).ToList();
                var unresolved = g.SelectMany(x => x.Dep.ReferencedBy.Where(p => !IsProject(p))).Distinct(StringComparer.Ordinal).ToList();
                var current = projects.Any(p => !p.Related);
                var rel = projects.Any(p => p.Related);
                var state = current && rel ? DependencyUsageState.UsedInCurrentAndRelated : current ? DependencyUsageState.UsedInCurrent : rel ? DependencyUsageState.UsedInRelated
                    : unresolved.Count > 0 ? DependencyUsageState.Unresolved : DependencyUsageState.NoUsageFound;
                var primary = g.Where(x => !IsRelated(x.Repo)).ToList();
                return new DependencyUsageRow(g.First().Dep.PackageName, "nuget",
                    [.. (primary.Count > 0 ? primary : g.ToList()).Select(x => x.Dep.CurrentValue).OfType<string>().Distinct(StringComparer.Ordinal)],
                    [.. g.Select(x => System.IO.Path.GetFileName(x.Dep.OwnerFile.Replace('\\', '/'))).Distinct(StringComparer.Ordinal)],
                    g.All(x => x.Dep.IgnoredBy is null) ? "Processed" : "Not processed", state, projects, unresolved);
            })
            .OrderBy(r => r.PackageName, StringComparer.OrdinalIgnoreCase).ToList();
        var allProjects = result.Repositories.SelectMany(repo => repo.Dependencies.Where(IsPackageReference).SelectMany(d => d.ReferencedBy.Where(IsProject)).Select(p => (repo.Repository, p))).Distinct().ToList();
        return new DependencyUsageView(rows, allProjects.Count(p => !IsRelated(p.Repository)), allProjects.Count(p => IsRelated(p.Repository)), related.Count > 0);
    }

    public static string Label(DependencyUsageState state) => state switch
    {
        DependencyUsageState.UsedInCurrent => "Used in current source",
        DependencyUsageState.UsedInRelated => "Used in related source",
        DependencyUsageState.UsedInCurrentAndRelated => "Used in current + related sources",
        DependencyUsageState.Unresolved => "Usage unresolved",
        _ => "No usage found in selected source scope",
    };

    /// <summary>"1 project in current source scope" — the count is always qualified by the scope it was found in.</summary>
    public static string UsedBy(DependencyUsageRow row)
    {
        if (row.Projects.Count == 0) return row.State == DependencyUsageState.Unresolved ? "Referenced through a props file" : "No usage found in selected source scope";
        var scope = row.State switch
        {
            DependencyUsageState.UsedInCurrent => "current source scope",
            DependencyUsageState.UsedInRelated => "related source scope",
            _ => "selected source scope",
        };
        return $"{row.Projects.Count} project{(row.Projects.Count == 1 ? "" : "s")} in {scope}";
    }

    public static string Version(ProjectPackageUsage usage) => usage.Version ?? "Not resolved from this source relationship";

    public static bool Matches(DependencyUsageRow row, string? search, string? state, string? renovate) =>
        (string.IsNullOrWhiteSpace(search) || row.PackageName.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase)
            || row.Projects.Any(p => p.Project.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase)))
        && (string.IsNullOrEmpty(state) || state switch
        {
            "Current" => row.State is DependencyUsageState.UsedInCurrent or DependencyUsageState.UsedInCurrentAndRelated,
            "Related" => row.State is DependencyUsageState.UsedInRelated or DependencyUsageState.UsedInCurrentAndRelated,
            _ => row.State.ToString() == state,
        })
        && (string.IsNullOrEmpty(renovate) || row.Renovate == renovate);
}
