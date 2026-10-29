using BirkNext.Web.Models;

namespace BirkNext.Web.Services.ProjectEvidence;

/// <summary>
/// How source consumers reuse Target Environment → Project: the exact snapshots it binds are the high-confidence scope, offered ahead of
/// discovery and never ahead of an explicit choice the consumer already holds. Reads only; never binds or saves anything.
/// </summary>
public static class ProjectBinding
{
    /// <summary>Sets the active environment's bound snapshots on a review's source state (no-op without a configured project).</summary>
    public static void Apply(ReviewSourceScopeState state, IServiceProvider services)
    {
        if (services.GetService(typeof(IFrontendAnalysisSettingsService)) is not IFrontendAnalysisSettingsService settings
            || settings.ActiveProfile is not { Project: { } project } profile)
        {
            state.ProjectBound = [];
            state.ProjectBoundLabel = null;
            return;
        }
        state.ProjectBound = ProjectEvidenceResolver.BoundSnapshots(project).ToHashSet();
        state.ProjectBoundLabel = $"{project.ProjectName} (Target Environment {profile.Name})";
    }

    /// <summary>The bound snapshots of the source-backed roles that provide <paramref name="capability"/> (application, contracts …).</summary>
    public static IReadOnlyList<Guid> SnapshotsFor(EnvironmentProject? project, ProjectEvidenceCapability capability) =>
        project is null ? [] : project.Evidence.Where(r => r.Capability == capability).SelectMany(r => r.Sources).Select(s => s.SnapshotId).OfType<Guid>().Distinct().ToList();
}
