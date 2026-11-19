namespace BirkNext.Web.Services;

/// <summary>
/// Project Test Setup (/project-scopes) was removed on 2026-11-19: no test area has to be configured before testing. Old links keep working: the
/// systems and connections views open Integration Topology (components and connections found in sources and metadata); every other view opens
/// project selection. Only real routes; no parameter is passed that the destination does not read (Integration Topology has no system filter).
/// </summary>
public static class ProjectTestSetupRetirement
{
    public const string ProjectsRoute = "project-import";
    public const string SystemsRoute = "integration-topology";

    public static (string Route, string Label) RedirectFor(string? view, string? tab, string? system)
    {
        var systems = view is "systems" or "connections" || tab is "systems" or "connections" || !string.IsNullOrWhiteSpace(system);
        return systems ? (SystemsRoute, "Integration Topology") : (ProjectsRoute, "project selection");
    }
}
