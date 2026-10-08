using BirkNext.Technology;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>The three evidence inputs BirkNext reviews. None is mandatory for every project; each unlocks different reviews.</summary>
public enum ProjectInputKind { Documents, Source, Target }

/// <summary>
/// State of one input. <see cref="Absent"/> is "not provided" (never a problem); <see cref="Partial"/> is provided but incomplete;
/// <see cref="NeedsAttention"/> needs a user decision or a refresh before its consumers can use it; <see cref="Unknown"/> could not be read.
/// </summary>
public enum ProjectInputStatus { Absent, Partial, Ready, NeedsAttention, Unknown }

/// <summary>
/// One input as Recommended Workflow shows it. <see cref="Requirement"/> says when it is needed ("Recommended start",
/// "Optional", "Required for runtime reviews"); it never claims the input is mandatory for every project.
/// </summary>
public sealed record ProjectInput(
    ProjectInputKind Kind,
    ProjectInputStatus Status,
    string Title,
    string Requirement,
    string StatusLabel,
    string Detail,
    string ActionLabel,
    string Route,
    IReadOnlyList<string> Facts)
{
    public bool IsProvided => Status is ProjectInputStatus.Ready or ProjectInputStatus.Partial or ProjectInputStatus.NeedsAttention;
}

/// <summary>The three inputs. Input completeness is evidence availability, never a quality score.</summary>
public sealed record ProjectInputs(ProjectInput Documents, ProjectInput Source, ProjectInput Target)
{
    public IReadOnlyList<ProjectInput> All => [Documents, Source, Target];
    public bool AnyProvided => All.Any(i => i.IsProvided);
}

/// <summary>
/// Derives the three inputs from their existing owners — no new state:
/// documents from the current workspace (<see cref="CurrentWorkspaceSnapshot"/>), source from the workspace's latest
/// Source Analysis snapshot (<see cref="ProjectTechnologyCoverage"/>), target from the active Target Environment profile.
/// Source needs no Target Environment: a target is runtime context only, so the source input never depends on one.
/// </summary>
public static class ProjectInputPresentation
{
    public const string SampleProjectsRoute = "sample-projects";
    public const string SourceAnalysisRoute = "source-analysis";
    /// <summary>One project ZIP for both documents and source: the recommended way to provide your own project.</summary>
    public const string ProjectImportRoute = "project-import";

    public static ProjectInputs Build(CurrentWorkspaceSnapshot workspace, FrontendAnalysisProfile? environment, ProjectTechnologyCoverage? coverage,
        bool environmentKnown = true) =>
        new(Documents(workspace), Source(coverage, environmentKnown), Target(environment, environmentKnown));

    public static ProjectInput Documents(CurrentWorkspaceSnapshot workspace)
    {
        const string title = "Project documents";
        const string requirement = "Recommended start";
        if (workspace.State == CurrentWorkspaceState.Error)
            return new(ProjectInputKind.Documents, ProjectInputStatus.Unknown, title, requirement, "Unavailable",
                "The workspace documents could not be read.", "Open Sample Projects", SampleProjectsRoute, []);
        if (workspace.AvailableRoleCount == 0)
        {
            var detail = workspace.IsImportedProject
                ? $"No supported project documents were detected in the imported project {workspace.ProjectDisplay}. Source can still be reviewed; documents can also be imported in an explorer."
                : workspace.WorkspaceLoaded
                ? $"Workspace {workspace.WorkspaceName} has no documents yet. Import the project ZIP, choose a Sample Project, or import a specification, plan or other document in an explorer."
                : "Import your project ZIP — one upload provides documents and source — or choose a Sample Project for a quick start. Specification, Constitution, Plan, Tasks and Data Model are all optional.";
            return new(ProjectInputKind.Documents, ProjectInputStatus.Absent, title, requirement, "Not provided", detail,
                "Import Project", ProjectImportRoute, []);
        }

        var facts = workspace.AvailableRoles.Select(r => r.ArtifactCount > 1 ? $"{r.Label} ({r.ArtifactCount})" : r.Label).ToList();
        if (workspace.Roles.FirstOrDefault(r => r.Selection == ArtifactRoleSelection.SelectionRequired) is { } unresolved)
            return new(ProjectInputKind.Documents, ProjectInputStatus.NeedsAttention, title, requirement, "Selection required",
                $"{workspace.RoleSummary}. Several {unresolved.Label} documents and none is selected.", $"Choose {unresolved.Label}", ExplorerRoute(unresolved.Role), facts);
        return workspace.IsImportedProject
            ? new(ProjectInputKind.Documents, ProjectInputStatus.Ready, title, requirement, "Available",
                $"{workspace.RoleSummary} in the imported project {workspace.ProjectDisplay}.", "Open Import Project", ProjectImportRoute, facts)
            : workspace.ProjectLoaded
            ? new(ProjectInputKind.Documents, ProjectInputStatus.Ready, title, requirement, "Available",
                $"{workspace.RoleSummary} in {workspace.ProjectDisplay}.", "Open Sample Projects", SampleProjectsRoute, facts)
            // The manual workspace has no project to open: importing the project ZIP is the way to a full project.
            : new(ProjectInputKind.Documents, ProjectInputStatus.Ready, title, requirement, "Available",
                $"{workspace.RoleSummary} in the manual workspace.", "Import Project", ProjectImportRoute, facts);
    }

    /// <summary>The source input. <paramref name="sourceKnown"/> is false when the source state could not be read; a missing coverage
    /// that was read is "not added". No Target Environment is needed for source.</summary>
    public static ProjectInput Source(ProjectTechnologyCoverage? coverage, bool sourceKnown = true)
    {
        const string title = "Source";
        const string requirement = "Optional · for source-based reviews";
        if (!sourceKnown)
            return new(ProjectInputKind.Source, ProjectInputStatus.Unknown, title, requirement, "Unknown",
                "Source state could not be read.", "Open Source Analysis", SourceAnalysisRoute, []);
        if (coverage?.SourceSnapshotId is null)
            return new(ProjectInputKind.Source, ProjectInputStatus.Absent, title, requirement, "Not added",
                "No source analyzed yet. Import the project ZIP — its source becomes a snapshot in the same import, no Target Environment needed — for Technology Coverage, Dependency and Pipeline Review.",
                "Open Source Analysis", SourceAnalysisRoute, []);

        var facts = new List<string>();
        if (coverage.SourceArchive is { Length: > 0 } archive) facts.Add(archive);
        if (coverage.AnalyzedAt is { } at) facts.Add($"Analyzed {at.ToLocalTime():yyyy-MM-dd HH:mm}");
        // Unsupported technologies are a tool limitation: the source is still provided.
        if (coverage.Source is null || coverage.CiCdEvidenceOutdated)
            return new(ProjectInputKind.Source, ProjectInputStatus.NeedsAttention, title, requirement, "Needs refresh",
                coverage.Source is null
                    ? "The latest snapshot predates technology inventory. Analyze the source again."
                    : "The latest snapshot predates current CI/CD evidence. Analyze the source again.",
                "Analyze again", SourceAnalysisRoute, facts);
        return new(ProjectInputKind.Source, ProjectInputStatus.Ready, title, requirement, "Analyzed",
            "Current source snapshot of the workspace.", "Open Source Analysis", SourceAnalysisRoute, facts);
    }

    public static ProjectInput Target(FrontendAnalysisProfile? environment, bool environmentKnown = true)
    {
        const string title = "Target environment";
        const string requirement = "Required for runtime reviews";
        var route = NavigationCatalog.TargetEnvironmentsRoute;
        if (!environmentKnown)
            return new(ProjectInputKind.Target, ProjectInputStatus.Unknown, title, requirement, "Unknown",
                "Target Environments could not be read.", "Configure Target", route, []);
        if (environment is null)
            return new(ProjectInputKind.Target, ProjectInputStatus.Absent, title, requirement, "Not configured",
                "Configure the application, API and integration targets to run Frontend, API, Integration, Performance and Critical E2E reviews.",
                "Configure Target", route, []);

        var facts = new List<string> { environment.Name };
        var host = HostOf(environment.TargetUrl);
        if (host is null)
            return new(ProjectInputKind.Target, ProjectInputStatus.Partial, title, requirement, "No application URL",
                $"{environment.Name} is active but has no application URL. Runtime reviews need one.", "Configure Target", route, facts);
        facts.Add(host);
        if (IsPlaceholderHost(host))
            return new(ProjectInputKind.Target, ProjectInputStatus.Partial, title, requirement, "Placeholder URL",
                $"{environment.Name} still has the example URL {host}. Set the real application URL before runtime reviews.", "Configure Target", route, facts);
        return new(ProjectInputKind.Target, ProjectInputStatus.Ready, title, requirement, "Configured",
            $"Runtime reviews run against {host}. Authentication and discovery are checked by each review.", "Open Target Environments", route, facts);
    }

    /// <summary>The generic seed's example hosts (example.local, example-dev.local, …): configured but not a real target yet.</summary>
    public static bool IsPlaceholderHost(string host) =>
        host.Equals("example.local", StringComparison.OrdinalIgnoreCase)
        || (host.StartsWith("example-", StringComparison.OrdinalIgnoreCase) && host.EndsWith(".local", StringComparison.OrdinalIgnoreCase));

    private static string? HostOf(string? url) =>
        !string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) ? uri.Host : null;

    public static string ExplorerRoute(WorkspaceArtifactType role) => role switch
    {
        WorkspaceArtifactType.Constitution => "constitution-explorer",
        WorkspaceArtifactType.Plan => "plan-explorer",
        WorkspaceArtifactType.Tasks => "task-explorer",
        WorkspaceArtifactType.DataModel => "data-model-explorer",
        _ => "specification-explorer",
    };
}
