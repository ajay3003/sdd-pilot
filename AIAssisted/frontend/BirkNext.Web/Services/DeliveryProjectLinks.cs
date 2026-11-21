using BirkNext.DeliveryReporting;
using BirkNext.Web.Models;
using BirkNext.Web.Services.ProjectImport;

namespace BirkNext.Web.Services;

/// <summary>
/// Delivery Snapshots ↔ the projects created by Import Project (2026-11-21). Delivery Reporting keeps its own projects, snapshots and frozen
/// reports (a re-import or a moved ZIP never changes a report); a workspace project only remembers which reporting project was created for it
/// (<see cref="SddApplicationContext.DeliveryProjectId"/>), so Delivery Snapshots can follow the Project list and create a reporting project
/// from an imported one instead of a second hand-typed "M2LB". Pure.
/// </summary>
public static class DeliveryProjectLinks
{
    /// <summary>The workspace projects (with imports), by name.</summary>
    public static IReadOnlyList<SddApplicationContext> WorkspaceProjects(SddLifecycleState lifecycle) =>
        [.. (lifecycle.Applications ?? []).Where(a => ApplicationContexts.Imports(lifecycle, a.Id).Count > 0).OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)];

    /// <summary>The reporting project of a workspace project: the linked one, else one with the same name (case-insensitive); null when none.</summary>
    public static DeliveryProject? For(SddApplicationContext application, IReadOnlyList<DeliveryProject> projects) =>
        (application.DeliveryProjectId is { } linked ? projects.FirstOrDefault(p => p.Id == linked) : null)
        ?? projects.FirstOrDefault(p => string.Equals(p.Name.Trim(), application.Name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The reporting project Delivery Snapshots opens with when the link names none: the current project's, else null.</summary>
    public static DeliveryProject? ForCurrent(SddLifecycleState lifecycle, IReadOnlyList<DeliveryProject> projects) =>
        ApplicationContexts.Current(lifecycle) is { } current ? For(current, projects) : null;

    /// <summary>Workspace projects that have no reporting project yet (offered by "+ New reporting project").</summary>
    public static IReadOnlyList<SddApplicationContext> WithoutReportingProject(SddLifecycleState lifecycle, IReadOnlyList<DeliveryProject> projects) =>
        [.. WorkspaceProjects(lifecycle).Where(a => For(a, projects) is null)];

    /// <summary>Remembers the reporting project created for a workspace project. False when the project no longer exists.</summary>
    public static bool Link(SddLifecycleState lifecycle, string applicationId, Guid deliveryProjectId)
    {
        if (ApplicationContexts.Find(lifecycle, applicationId) is not { } application) return false;
        application.DeliveryProjectId = deliveryProjectId;
        return true;
    }
}
