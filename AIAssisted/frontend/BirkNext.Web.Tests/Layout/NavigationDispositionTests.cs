using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Layout;

/// <summary>
/// Feature-portfolio audit (docs/audits/birknext-feature-ownership-matrix-2026-11-15.md): every module of the registry has a home. It is listed in at
/// least one specialised workspace, or it is on an explicit, reasoned All-modules-only list. A new module therefore cannot silently end up reachable
/// only from All modules, and the explicit list cannot keep a module that has since been placed.
/// </summary>
public sealed class NavigationDispositionTests
{
    /// <summary>Modules intentionally reachable only from All modules (and search), each with its reason.</summary>
    private static readonly Dictionary<string, string> AllModulesOnly = new(StringComparer.OrdinalIgnoreCase)
    {
        ["all-modules"] = "the catalog itself; always shown in every workspace",
        ["workspace-transfer"] = "administration (backup, restore, transfer), not a testing or delivery task",
        ["security-classification-review"] = "M2LB Kode 6/7 domain extension, not a general workflow",
        ["extensions/skolenaervaer"] = "Skolenærvær project extension, not a general workflow",
        ["traceability"] = "legacy Traceability & Coverage, off by default (replaced by Requirements Traceability and Test Coverage)",
        ["traceability/suggestions"] = "legacy traceability suggestions, off by default",
        ["code-traceability"] = "legacy code traceability, off by default",
        ["ai-change-auditor"] = "AI Change Review, off by default (legacy traceability store, roadmap R2)",
    };

    private static IReadOnlyList<string> CatalogRoutes => [.. NavigationCatalog.Sections.SelectMany(s => s.Items).Select(i => i.Route)];

    private static HashSet<string> Placed => new(WorkspaceViews.All.Where(v => !v.IsAllModules).SelectMany(v => v.Modules), StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Every_module_is_in_a_specialised_workspace_or_explicitly_All_modules_only()
    {
        var homeless = CatalogRoutes.Where(r => !Placed.Contains(r) && !AllModulesOnly.ContainsKey(r)).ToList();
        homeless.Should().BeEmpty("each module needs a workspace home, or a stated reason to be reachable only from All modules");
    }

    [Fact]
    public void The_All_modules_only_list_names_real_unplaced_modules()
    {
        AllModulesOnly.Keys.Should().OnlyContain(r => CatalogRoutes.Contains(r, StringComparer.OrdinalIgnoreCase), "the list names registry modules");
        AllModulesOnly.Keys.Where(Placed.Contains).Should().BeEmpty("a module placed in a workspace no longer belongs on the All-modules-only list");
    }

    [Theory]
    [InlineData("demo-projects", WorkspaceView.Developer)]
    [InlineData("generated-documentation-health", WorkspaceView.Architect)]
    [InlineData("generated-documentation-health", WorkspaceView.Developer)]
    public void The_audit_placed_the_previously_unplaced_modules(string route, WorkspaceView view) =>
        WorkspaceViews.Of(view).Modules.Should().Contain(route);
}
