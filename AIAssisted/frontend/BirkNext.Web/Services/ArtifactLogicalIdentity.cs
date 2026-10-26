using System.Text.RegularExpressions;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>One content version of a logical artifact: the first revision that carried that content, and every revision (import) carrying it.</summary>
public sealed record ArtifactContentVersion(int Version, string Fingerprint, SddArtifactRevision First, IReadOnlyList<SddArtifactRevision> Revisions)
{
    public SddArtifactRevision Latest => Revisions[^1];
}

/// <summary>The history of one logical artifact (role + project + stable path) across imports.</summary>
public sealed record ArtifactHistory(string Key, string Role, string Path, string Scope, IReadOnlyList<ArtifactContentVersion> Versions, SddArtifactRevision LatestRevision)
{
    /// <summary>The content version of the most recently captured revision (content may return to an earlier version).</summary>
    public ArtifactContentVersion Current => Versions.First(v => v.Revisions.Contains(LatestRevision));
}

/// <summary>
/// Logical artifact identity for history, authority and baselines: role + project (or manual workspace scope) + the archive-independent path
/// (<see cref="DocumentationDrift.StablePath"/>). <c>Person/specs/001-person-module/tasks.md</c> and <c>Frontend/specs/005-…/tasks.md</c> are
/// different artifacts — never revisions of each other. The same path in a later import of the same project is the same artifact: the same
/// fingerprint is the same content version, a different fingerprint a new one. A renamed file is a different artifact (continuity is not guessed).
/// </summary>
public static partial class ArtifactLogicalIdentity
{
    public static string Key(SddLifecycleState state, SddArtifactRevision revision)
    {
        var importId = revision.ProjectImportId ?? ProjectImportScope.ImportIdOf(revision.WorkspaceScope);
        var project = importId is null ? null : state.ProjectImports.FirstOrDefault(i => i.ImportId == importId)?.ProjectName;
        var path = DocumentationDrift.StablePath(state, importId, revision.SourceReference ?? revision.FileName);
        return $"{revision.Role}|{project ?? revision.WorkspaceScope ?? "manual"}|{path.ToLowerInvariant()}";
    }

    public static string PathOf(SddLifecycleState state, SddArtifactRevision revision)
    {
        var importId = revision.ProjectImportId ?? ProjectImportScope.ImportIdOf(revision.WorkspaceScope);
        return DocumentationDrift.StablePath(state, importId, revision.SourceReference ?? revision.FileName);
    }

    /// <summary>"Artifact Pack 001-person-module" for a file inside specs/NNN-x/, else "Shared (module or project)".</summary>
    public static string ScopeOf(string path) => PackFolder().Match(path.Replace('\\', '/')) is { Success: true } m
        ? $"Artifact Pack {m.Groups[1].Value}" : "Shared (module or project)";

    public static bool SameArtifact(SddLifecycleState state, SddArtifactRevision a, SddArtifactRevision b) =>
        string.Equals(Key(state, a), Key(state, b), StringComparison.Ordinal);

    public static IReadOnlyList<ArtifactHistory> Histories(SddLifecycleState state) =>
        [.. state.Revisions.GroupBy(r => Key(state, r), StringComparer.Ordinal).Select(g =>
        {
            var ordered = g.OrderBy(r => r.CapturedAt).ThenBy(r => r.Revision).ToList();
            var versions = ordered.GroupBy(r => r.Fingerprint, StringComparer.Ordinal)
                .Select((v, i) => new ArtifactContentVersion(i + 1, v.Key, v.First(), [.. v])).ToList();
            var path = PathOf(state, ordered[0]);
            return new ArtifactHistory(g.Key, ordered[0].Role, path, ScopeOf(path), versions, ordered[^1]);
        }).OrderBy(h => h.Role, StringComparer.Ordinal).ThenBy(h => h.Path, StringComparer.OrdinalIgnoreCase)];

    [GeneratedRegex(@"(?:^|/)specs/([^/]+)/", RegexOptions.IgnoreCase)]
    private static partial Regex PackFolder();
}
