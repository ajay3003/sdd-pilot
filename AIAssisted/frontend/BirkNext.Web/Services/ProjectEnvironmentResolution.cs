using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>
/// R7 — how a screen that shows both project-scoped facts (plans, saved cases, manual runs, declared journeys, keyed by the project key
/// <c>import:{importId}</c>) and environment-scoped facts (canonical interpretation, reviewed coverage, Release Readiness, topology, Integration Catalog,
/// IQR — keyed by the Target Environment profile id) relates the two. The identities are never merged: a project is resolved to an environment only
/// through the environment's own binding (<see cref="FrontendAnalysisProfile.Project"/>, set in Target Environments › Project).
/// </summary>
public static class ProjectEnvironmentResolution
{
    public enum State
    {
        /// <summary>No project selected: environment facts stand on their own.</summary>
        NoProject,
        /// <summary>No active Target Environment.</summary>
        NoEnvironment,
        /// <summary>The active environment is bound to the selected project.</summary>
        Bound,
        /// <summary>The active environment has no project binding: its facts are shown for the selected project, labelled as unconfirmed (the
        /// documented fallback of <see cref="FrontendAnalysisProfile.Project"/>).</summary>
        Unbound,
        /// <summary>The active environment is bound to another project: its facts are not this project's and are not shown as such.</summary>
        OtherProject,
    }

    /// <param name="BoundEnvironments">Names of the environments bound to the selected project (to switch to), when the active one is not.</param>
    public sealed record Result(State State, string? EnvironmentId, string? EnvironmentName, string? BoundProjectName, IReadOnlyList<string> BoundEnvironments)
    {
        /// <summary>Environment-scoped facts may be shown for the selected project (bound, or unbound and labelled).</summary>
        public bool EnvironmentFactsApply => State is State.Bound or State.Unbound or State.NoProject;

        public string? Notice => State switch
        {
            State.Unbound => $"Target Environment \"{EnvironmentName}\" is not bound to a project; its results are shown for this project but are not confirmed to belong to it. Bind it in Target Environments › Project.",
            State.OtherProject => $"Target Environment \"{EnvironmentName}\" is bound to {BoundProjectName}, not to this project, so its environment results are not shown here."
                + (BoundEnvironments.Count > 0 ? $" Environments bound to this project: {string.Join(", ", BoundEnvironments)}." : " No Target Environment is bound to this project yet."),
            State.NoEnvironment => "No Target Environment is active; environment results (relationship analysis, readiness, topology) are not available.",
            _ => null,
        };
    }

    /// <summary>The import id of a project key (<c>import:{importId}</c>); other keys have none.</summary>
    public static string? ImportIdOf(string? projectKey) =>
        projectKey is { Length: > 7 } key && key.StartsWith("import:", StringComparison.Ordinal) ? key["import:".Length..] : null;

    public static Result Resolve(string? projectKey, FrontendAnalysisProfile? active, IEnumerable<FrontendAnalysisProfile> profiles)
    {
        if (active is null || string.IsNullOrWhiteSpace(active.Id)) return new(State.NoEnvironment, null, null, null, []);
        if (string.IsNullOrWhiteSpace(projectKey)) return new(State.NoProject, active.Id, active.Name, active.Project?.ProjectName, []);
        var importId = ImportIdOf(projectKey);
        bool BoundToProject(FrontendAnalysisProfile p) => p.Project?.ProjectImportId is { Length: > 0 } id && importId is not null && string.Equals(id, importId, StringComparison.Ordinal);
        if (BoundToProject(active)) return new(State.Bound, active.Id, active.Name, active.Project!.ProjectName, []);
        if (active.Project is null) return new(State.Unbound, active.Id, active.Name, null, []);
        var bound = profiles.Where(BoundToProject).Select(p => p.Name).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.Ordinal).ToList();
        return new(State.OtherProject, active.Id, active.Name, string.IsNullOrWhiteSpace(active.Project.ProjectName) ? "another project" : active.Project.ProjectName, bound);
    }
}
