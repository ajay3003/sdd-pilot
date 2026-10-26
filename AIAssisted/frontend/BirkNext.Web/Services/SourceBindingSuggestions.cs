using BirkNext.Integrations;

namespace BirkNext.Web.Services;

/// <summary>A source project that may belong to an Artifact Pack's module, with the deterministic reason. A suggestion, never a binding.</summary>
public sealed record SourceBindingSuggestion(string ProjectName, string ProjectPath, bool IsTest, string Reason);

/// <summary>
/// Suggested source bindings for an Artifact Pack: Source Analysis projects whose folder is the pack's module folder (strong) or whose
/// project name starts with the module name (weaker). Nothing is saved or trusted: the user binds explicit CodeLinks, and only those become
/// implementation evidence. A file name similar to a task's wording never counts.
/// </summary>
public static class SourceBindingSuggestions
{
    public static IReadOnlyList<SourceBindingSuggestion> For(string? module, IqrSourceSnapshot? snapshot)
    {
        if (string.IsNullOrWhiteSpace(module) || snapshot is null) return [];
        var root = module.Replace('\\', '/').Trim('/');
        var name = root[(root.LastIndexOf('/') + 1)..];
        var result = new List<SourceBindingSuggestion>();
        foreach (var project in snapshot.Projects)
        {
            var path = project.Path.Replace('\\', '/').TrimStart('/');
            if (path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))
                result.Add(new(project.Name, project.Path, project.IsTest, $"Project folder is inside module folder {root}/"));
            else if (project.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || project.Name.StartsWith(name + ".", StringComparison.OrdinalIgnoreCase))
                result.Add(new(project.Name, project.Path, project.IsTest, $"Project name starts with module name {name} (weaker: name only)"));
        }
        return [.. result.OrderBy(r => r.Reason.Contains("weaker", StringComparison.Ordinal) ? 1 : 0).ThenBy(r => r.ProjectPath, StringComparer.Ordinal)];
    }

    /// <summary>The module of a requirement scope ("workspace|module|pack"), or null.</summary>
    public static string? ModuleOf(string? requirementScope) =>
        requirementScope?.Split('|') is { Length: >= 2 } parts && parts[1].Length > 0 ? parts[1] : null;
}
