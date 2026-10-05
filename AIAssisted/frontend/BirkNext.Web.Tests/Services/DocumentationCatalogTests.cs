using BirkNext.Web.Services;
using Xunit;

namespace BirkNext.Web.Tests.Services;

public sealed class DocumentationCatalogTests
{
    private static DocumentationFeature Feature(string id = "alpha", string label = "Alpha", string route = "/alpha", string anchor = "alpha") =>
        new(id, label, route, anchor, "Analysis");

    [Fact]
    public void Current_navigation_is_documented_and_routes_are_real()
    {
        var findings = DocumentationCatalog.Check(DocumentationCatalog.CurrentNavigation, DocumentationCatalog.Features,
            DocumentationCatalog.DiscoverRoutes(typeof(BirkNext.Web.Pages.UserGuide).Assembly),
            ["overview", "concepts", "getting-started", "source-analysis", "technology-coverage", "environment-analysis", "traceability", "quality-reviews", "pipeline", "testing", "system-settings", "feature-matrix", "workflows", "where", "scenarios", "discovery", "validation", "faq", "glossary", "ecosystem", "optional-capabilities", "feature-details", "about"],
            DocumentationCatalog.SettingsSections, DocumentationCatalog.TargetEnvironmentTabs, DocumentationCatalog.SourceAnalysisDomains,
            DocumentationCatalog.ReadGuideSource(typeof(BirkNext.Web.Pages.UserGuide).Assembly));
        Assert.DoesNotContain(findings, f => f.Severity == "Fail");
    }

    [Fact]
    public void Missing_current_feature_documentation_is_a_failure()
    {
        var findings = Check([Feature()], []);
        Assert.Contains(findings, f => f.Category == "Feature coverage" && f.FeatureId == "alpha" && f.Severity == "Fail");
    }

    [Fact]
    public void Invalid_route_and_missing_anchor_are_reported()
    {
        var findings = Check([Feature()], [Feature()], routes: ["/elsewhere"], anchors: []);
        Assert.Contains(findings, f => f.Category == "Routes");
        Assert.Contains(findings, f => f.Category == "Anchors");
    }

    [Fact]
    public void Stable_feature_id_allows_display_label_rename()
    {
        var findings = Check([Feature(label: "Renamed")], [Feature(label: "Old label")]);
        Assert.DoesNotContain(findings, f => f.Severity == "Fail" && (f.Category is "Feature coverage" or "Route mismatch"));
    }

    [Fact]
    public void Legacy_reference_duplicate_ids_and_orphan_docs_are_detected()
    {
        var findings = Check([Feature(), Feature()], [Feature(), Feature("retired", "Retired", "/retired")], guideText: ["QA Auditor"]);
        Assert.Contains(findings, f => f.Category == "Registry");
        Assert.Contains(findings, f => f.Category == "Orphan documentation");
        Assert.Contains(findings, f => f.Category == "Legacy terminology");
    }

    [Fact]
    public void Missing_settings_target_tab_and_source_domain_are_detected()
    {
        var findings = Check([], [], settingsDocs: [], targetTabDocs: [], sourceDomainDocs: []);
        Assert.Contains(findings, f => f.Category == "System Settings" && f.Message.Contains("General"));
        Assert.Contains(findings, f => f.Category == "Target Environment tabs" && f.Message.Contains("Authentication"));
        Assert.Contains(findings, f => f.Category == "Source Analysis domains" && f.Message.Contains("CI/CD"));
    }

    [Fact]
    public void Guide_source_broken_routes_and_anchors_are_detected()
    {
        var findings = DocumentationCatalog.Check([], [], [], [], [], [], [], ["<a href=\"/missing\">route</a><a href=\"#gone\">anchor</a>"]);
        Assert.Contains(findings, f => f.Category == "Cross-links" && f.Route == "/missing");
        Assert.Contains(findings, f => f.Category == "Anchors" && f.DocumentSection == "gone");
    }

    private static IReadOnlyList<DocumentationFinding> Check(IEnumerable<DocumentationFeature> current,
        IEnumerable<DocumentationFeature> docs, IEnumerable<string>? routes = null, IEnumerable<string>? anchors = null,
        IEnumerable<string>? settingsDocs = null, IEnumerable<string>? targetTabDocs = null,
        IEnumerable<string>? sourceDomainDocs = null, IEnumerable<string>? guideText = null) =>
        DocumentationCatalog.Check(current, docs, routes ?? ["/alpha", "/retired"], anchors ?? ["alpha"],
            settingsDocs ?? DocumentationCatalog.SettingsSections, targetTabDocs ?? DocumentationCatalog.TargetEnvironmentTabs,
            sourceDomainDocs ?? DocumentationCatalog.SourceAnalysisDomains, guideText);
}
