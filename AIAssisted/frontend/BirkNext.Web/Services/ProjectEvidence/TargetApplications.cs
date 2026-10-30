using BirkNext.Web.Models;

namespace BirkNext.Web.Services.ProjectEvidence;

/// <summary>
/// Target Application owns the logical application/system identity of a Target Environment (WHERE = environment, WHAT = Target Application,
/// WHICH EVIDENCE = Project). Pure rules:
/// <list type="bullet">
/// <item><see cref="Of"/> — the identity; a profile saved before this separation reads it from its Project (no rewrite, no migration).</item>
/// <item><see cref="Options"/> — application/system candidates (the Project picker's resolver): infrastructure, contract, shared-library
/// and generated-docs packages are never applications, whatever their file name.</item>
/// <item><see cref="Set"/> — changing the application archives the previous application's Project evidence (never deletes it) and
/// restores an archived one for an application chosen again. Never activates the environment.</item>
/// </list>
/// <see cref="EnvironmentProject.ProjectName"/> stays as a legacy mirror of the identity for existing consumers; it is written only from here.
/// </summary>
public static class TargetApplications
{
    public static TargetApplicationIdentity? Of(FrontendAnalysisProfile? profile) =>
        profile?.TargetApplication is { Name.Length: > 0 } identity ? identity
        : profile?.Project is { ProjectName.Length: > 0 } legacy
            ? new TargetApplicationIdentity
            {
                Name = legacy.ProjectName, ImportId = legacy.ProjectImportId, ArchiveSha256 = legacy.ArchiveSha256, Manual = legacy.Manual,
                Description = legacy.Description, SetAt = legacy.SelectedAt, Legacy = true,
            }
            : null;

    public static IReadOnlyList<ProjectOption> Options(IReadOnlyList<ProjectEvidencePackage> packages, IEnumerable<string> knownNames) =>
        ProjectSetupPlanner.Options(packages, knownNames);

    public static TargetApplicationIdentity FromOption(ProjectOption option, DateTimeOffset now) => new()
    {
        Name = option.Name.Trim(), ImportId = option.Package?.ImportId, ArchiveSha256 = option.Package?.ArchiveSha256, Manual = option.Package is null, SetAt = now,
    };

    public static TargetApplicationIdentity Manual(string name, string? description, DateTimeOffset now) => new()
    {
        Name = name.Trim(), Manual = true, Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(), SetAt = now,
    };

    public static bool SameApplication(TargetApplicationIdentity? a, string? name) =>
        a is not null && name is not null && string.Equals(a.Name.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>The Project evidence that would be archived if the application changed to <paramref name="name"/> (null when none or the same).</summary>
    public static EnvironmentProject? WouldArchive(FrontendAnalysisProfile profile, string name) =>
        profile.Project is { } project && !SameApplication(Of(profile), name) ? project : null;

    /// <summary>Sets the identity. Returns false when nothing changed. Imports, snapshots and documents are never touched.</summary>
    public static bool Set(FrontendAnalysisProfile profile, TargetApplicationIdentity next, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(next.Name) || next.Name.Trim().Length > 120) return false;
        next.Name = next.Name.Trim();
        var current = Of(profile);
        if (SameApplication(current, next.Name))
        {
            var unchanged = profile.TargetApplication is { } stored && stored.ImportId == next.ImportId && stored.Description == next.Description && stored.Manual == next.Manual;
            profile.TargetApplication = next;
            if (profile.Project is { } same) same.ProjectName = next.Name;
            return !unchanged;
        }
        // A different application: its evidence is not this application's evidence. Set it aside (recoverable), never reuse it silently.
        if (profile.Project is { } previous)
        {
            previous.ArchivedAt = now;
            (profile.ArchivedProjects ??= []).Add(previous);
            profile.Project = null;
        }
        // Choosing an application again restores its archived Project evidence (the user's earlier decisions).
        if (profile.ArchivedProjects?.LastOrDefault(p => string.Equals(p.ProjectName, next.Name, StringComparison.OrdinalIgnoreCase)) is { } restored)
        {
            profile.ArchivedProjects.Remove(restored);
            restored.ArchivedAt = null;
            profile.Project = restored;
            if (profile.ArchivedProjects.Count == 0) profile.ArchivedProjects = null;
        }
        profile.TargetApplication = next;
        return true;
    }

    /// <summary>The Target Application tab of one environment.</summary>
    public static string TabHref(string? environmentId) =>
        "/admin/system-settings?section=target-environments&tab=target" + (string.IsNullOrWhiteSpace(environmentId) ? "" : "&profile=" + Uri.EscapeDataString(environmentId));
}
