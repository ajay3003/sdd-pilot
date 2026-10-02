using System.Text.RegularExpressions;
using BirkNext.PipelineReview;
using BirkNext.SourceDomains;

namespace BirkNext.Api.Services.PipelineReview;

/// <summary>
/// Path-filter semantics of CI triggers, evaluated against repository-relative paths. Azure Pipelines: an include/exclude entry is a path prefix
/// (segment boundary) or a wildcard pattern; no include list = every path; an exclude wins. The archive may wrap the repository in a top folder,
/// which is removed when no filter mentions it.
/// </summary>
internal static class PipelinePathFilters
{
    public static string RepositoryRoot(IEnumerable<string> archivePaths, IEnumerable<string> filters)
    {
        var paths = archivePaths.Where(p => p.Length > 0).ToList();
        if (paths.Count == 0) return "";
        var first = paths.Select(p => p.Split('/')[0]).Distinct(StringComparer.Ordinal).ToList();
        if (first.Count != 1 || paths.Any(p => !p.Contains('/'))) return "";
        var root = first[0];
        return filters.Any(f => Normalize(f).StartsWith(root + "/", StringComparison.OrdinalIgnoreCase) || Normalize(f).Equals(root, StringComparison.OrdinalIgnoreCase)) ? "" : root + "/";
    }

    public static string Relative(string path, string root) => root.Length > 0 && path.StartsWith(root, StringComparison.Ordinal) ? path[root.Length..] : path;

    public static string Normalize(string filter) => filter.Trim().Trim('\'', '"').Replace('\\', '/').TrimStart('/').TrimEnd('/');

    public static bool Matches(string filter, string path)
    {
        var f = Normalize(filter);
        if (f.Length == 0 || f is "*" or "**") return true;
        if (f.Contains('*') || f.Contains('?'))
        {
            var pattern = "^" + Regex.Escape(f).Replace(@"\*\*/", "(.*/)?").Replace(@"\*\*", ".*").Replace(@"\*", "[^/]*").Replace(@"\?", "[^/]") + "(/.*)?$";
            return Regex.IsMatch(path, pattern, RegexOptions.IgnoreCase);
        }
        return path.Equals(f, StringComparison.OrdinalIgnoreCase) || path.StartsWith(f + "/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether a change to <paramref name="path"/> starts a trigger (branch filters aside).</summary>
    public static bool Starts(PipelineTrigger trigger, string path) =>
        (trigger.PathsInclude.Count == 0 || trigger.PathsInclude.Any(f => Matches(f, path))) && !trigger.PathsExclude.Any(f => Matches(f, path));

    /// <summary>Coverage of a whole folder: Yes (any change), Partial (only some sub-paths, or some are excluded), No.</summary>
    public static TriggerCoverage Covers(PipelineTrigger trigger, string folder)
    {
        var sample = folder.TrimEnd('/') + "/__any__";
        var starts = Starts(trigger, sample);
        var includeInside = trigger.PathsInclude.Any(f => Normalize(f).StartsWith(folder.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase));
        var excludeInside = trigger.PathsExclude.Any(f => Normalize(f).StartsWith(folder.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase));
        return starts ? excludeInside ? TriggerCoverage.Partial : TriggerCoverage.Yes : includeInside ? TriggerCoverage.Partial : TriggerCoverage.No;
    }

    public static bool ChangeTriggered(PipelineTrigger t) => t.Type is "push" or "pull-request";

    /// <summary>Best coverage over a pipeline's change triggers (push and pull request), with the basis in words.</summary>
    public static (TriggerCoverage Coverage, string Basis) Pipeline(PipelineDefinition p, string folder)
    {
        var triggers = p.Triggers.Where(ChangeTriggered).ToList();
        if (triggers.Count == 0)
            return (TriggerCoverage.No, p.Triggers.Any(t => t.Type == "pipeline-resource") ? "starts only when another pipeline completes"
                : p.Triggers.Any(t => t.Type == "schedule") ? "runs on a schedule only" : "no automatic change trigger (manual start)");
        var best = triggers.Select(t => (Coverage: Covers(t, folder), Trigger: t)).OrderBy(x => x.Coverage switch { TriggerCoverage.Yes => 0, TriggerCoverage.Partial => 1, _ => 2 }).First();
        var t = best.Trigger;
        var kind = t.Type == "pull-request" ? "pull requests" : "pushes";
        var basis = t.PathsInclude.Count == 0 && t.PathsExclude.Count == 0 ? $"no path filter on {kind}: every change starts it"
            : best.Coverage switch
            {
                TriggerCoverage.Yes => $"path filter on {kind} includes it ({string.Join(", ", t.PathsInclude.DefaultIfEmpty("all paths"))}{(t.PathsExclude.Count > 0 ? $"; excludes {string.Join(", ", t.PathsExclude)}" : "")})",
                TriggerCoverage.Partial => $"path filter on {kind} covers only part of it ({string.Join(", ", t.PathsInclude.Concat(t.PathsExclude.Select(x => "not " + x)))})",
                _ => $"path filter on {kind} does not include it (includes {string.Join(", ", t.PathsInclude.DefaultIfEmpty("all paths"))}{(t.PathsExclude.Count > 0 ? $"; excludes {string.Join(", ", t.PathsExclude)}" : "")})",
            };
        return (best.Coverage, basis);
    }

    public static bool HasPathFilters(PipelineDefinition p) => p.Triggers.Where(ChangeTriggered).Any(t => t.PathsInclude.Count > 0 || t.PathsExclude.Count > 0);
}
