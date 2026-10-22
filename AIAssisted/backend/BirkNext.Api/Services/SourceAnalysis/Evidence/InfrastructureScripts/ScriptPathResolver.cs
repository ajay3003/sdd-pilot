using System.Text.RegularExpressions;

namespace BirkNext.Api.Services.SourceAnalysis.Evidence;

/// <summary>Resolves a script path as a pipeline or hook writes it (./scripts/x.py, $(Build.SourcesDirectory)/x.sh, ${{ github.workspace }}/x.ps1,
/// relative to a working directory) to a file of the snapshot — exact, else a unique path suffix. Paths with unresolved variables stay unresolved.</summary>
internal static class ScriptPathResolver
{
    private static readonly Regex WorkspacePrefix = new(
        @"^(\$\(Build\.SourcesDirectory\)|\$\(System\.DefaultWorkingDirectory\)|\$\(Build\.Repository\.LocalPath\)|\$\{\{\s*github\.workspace\s*\}\}|\$\{?GITHUB_WORKSPACE\}?|\$\{?CI_PROJECT_DIR\}?|\$\{?WORKSPACE\}?)/",
        RegexOptions.Compiled);

    public static string Clean(string raw)
    {
        var p = raw.Trim().Trim('"', '\'').Replace('\\', '/');
        p = WorkspacePrefix.Replace(p, "");
        while (p.StartsWith("./", StringComparison.Ordinal)) p = p[2..];
        return p;
    }

    public static bool IsScript(string path) => Regex.IsMatch(path, @"\.(py|sh|bash|ps1)$", RegexOptions.IgnoreCase);

    public static string? Resolve(string raw, string? workingDirectory, IReadOnlySet<string> paths)
    {
        var p = Clean(raw);
        if (p.Length == 0 || p.Contains('$') || p.Contains('*')) return null;
        if (workingDirectory is not null && !workingDirectory.Contains('$'))
        {
            var wd = Clean(workingDirectory).TrimEnd('/');
            var combined = BirkNext.Api.Services.SourceArchitecture.ArchitectureInput.Normalize(wd.Length == 0 ? p : $"{wd}/{p}");
            if (paths.Contains(combined)) return combined;
        }
        var direct = BirkNext.Api.Services.SourceArchitecture.ArchitectureInput.Normalize(p.TrimStart('/'));
        if (paths.Contains(direct)) return direct;
        var suffix = paths.Where(x => x.EndsWith("/" + direct, StringComparison.Ordinal)).Take(2).ToList();
        return suffix.Count == 1 ? suffix[0] : null;
    }
}
