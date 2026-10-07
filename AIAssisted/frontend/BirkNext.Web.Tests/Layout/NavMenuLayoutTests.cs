using BirkNext.Applicability;
using BirkNext.Technology;
using BirkNext.Web.Layout;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Layout;

/// <summary>
/// Sidebar as a compact navigation surface: [icon] [label] [status] rows, collapsible sections that never hide the current
/// page, statuses from the shared applicability evaluation (no hard-coded M2LB or Azure), and refresh on project changes.
/// </summary>
public sealed class NavMenuLayoutTests : BunitContext
{
    private sealed class MutableCoverageApi : ITechnologyCoverageApiService
    {
        public ProjectTechnologyCoverage? Coverage { get; set; }
        public Task<ProjectTechnologyCoverage?> GetAsync(string environmentId, CancellationToken ct = default) => Task.FromResult(Coverage);
    }

    private readonly MutableCoverageApi _api = new();
    private readonly Mock<ICurrentWorkspaceProjection> _workspace = new();
    private CurrentWorkspaceSnapshot _snapshot = CurrentWorkspaceSnapshot.None();
    private FrontendAnalysisProfile? _profile = new() { Id = "env", Name = "Env" };

    public NavMenuLayoutTests()
    {
        Services.AddSingleton<FeatureVisibilityService>();
        Services.AddSingleton<NavigationSectionState>();
    }

    private void RegisterApplicability()
    {
        var context = new Mock<IFrontendAnalysisContextFactory>();
        context.Setup(c => c.GetActiveContextAsync()).ReturnsAsync(() => new FrontendAnalysisContext { ActiveProfile = _profile });
        Services.AddSingleton(context.Object);
        _workspace.Setup(w => w.GetAsync()).ReturnsAsync(() => _snapshot);
        Services.AddSingleton(_workspace.Object);
        Services.AddSingleton<ITechnologyCoverageApiService>(_api);
        Services.AddSingleton(Mock.Of<IFrontendAnalysisSettingsService>());
        Services.AddScoped<ProjectApplicabilityState>();
    }

    private static ProjectTechnologyCoverage Snapshot(List<DetectedTechnology>? technologies = null, List<string>? extensions = null) => new()
    {
        EnvironmentId = "env", SourceSnapshotId = Guid.NewGuid(), SourceArchive = "src.zip",
        Source = new SourceTechnologyCoverage { Technologies = technologies ?? [], Capabilities = [] },
        DomainExtensions = extensions ?? [],
    };

    private static DetectedTechnology Tech(string id, string name, TechnologyArea area) =>
        new(id, name, area, BirkNext.Applicability.DetectionConfidence.Confirmed, 1, ["file"]);

    private static string? Status(IRenderedComponent<NavMenu> cut, string reviewId) =>
        cut.FindAll($"[data-testid=nav-applicability-{reviewId}]").SingleOrDefault()?.TextContent;

    [Fact]
    public void Rows_AreIconLabelStatus_InThatOrder_WithFullLabelAsTitle()
    {
        RegisterApplicability();
        var cut = Render<NavMenu>();
        cut.WaitForAssertion(() => Status(cut, "quality-review").Should().Be("No evidence"));

        var link = cut.Find("a[href='integration-quality-review']");
        link.Children.Select(c => c.ClassName?.Split(' ')[0]).Should().StartWith(["nav-icon", "nav-label", "nav-applicability"]);
        link.QuerySelector(".nav-label")!.TextContent.Should().Be("Integration Quality Review");
        link.GetAttribute("title").Should().Be("Integration Quality Review");
        cut.Find("a[href='task-alignment']").GetAttribute("title").Should().Be("Implementation Review: tasks checked against the specification");
        cut.Find("a[href='implementation-traceability']").GetAttribute("title").Should().StartWith("Implementation Traceability: ");
    }

    [Fact]
    public void Status_IsSpokenInFullWords_AndTheShortBadgeIsHiddenFromScreenReaders()
    {
        RegisterApplicability();
        _api.Coverage = Snapshot();
        var cut = Render<NavMenu>();
        cut.WaitForAssertion(() => Status(cut, "dependency-review").Should().Be("N/A"));

        var link = cut.Find("a[href='dependency-review']");
        link.QuerySelector(".nav-applicability")!.GetAttribute("aria-hidden").Should().Be("true");
        link.QuerySelector(".visually-hidden")!.TextContent.Should().Be(", Not applicable");
        link.QuerySelector(".nav-applicability")!.GetAttribute("title").Should().StartWith("Not applicable to this project.");
    }

    [Fact]
    public void NeutralStates_StayDistinct()
    {
        RegisterApplicability();
        _api.Coverage = Snapshot(
        [
            Tech("lang.java", "Java", TechnologyArea.Language),
            Tech("dependency.maven", "Maven / Gradle", TechnologyArea.Dependency),
            Tech("pipeline.github-actions", "GitHub Actions", TechnologyArea.Pipeline),
            Tech("cloud.aws", "Amazon Web Services", TechnologyArea.Cloud),
        ]);
        var cut = Render<NavMenu>();
        cut.WaitForAssertion(() => Status(cut, "azure-environment").Should().Be("Unsupported"));

        Status(cut, "quality-review").Should().Be("No evidence");
        Status(cut, "pipeline-review").Should().Be("Partial");
        Status(cut, "frontend-quality-review").Should().Be("N/A");
        Status(cut, "api-quality-review").Should().Be("N/A", "an analyzed snapshot without an API");
        cut.Find("[data-testid=nav-applicability-quality-review]").GetAttribute("data-kind").Should().Be("NoEvidence");
        cut.Find("[data-testid=nav-applicability-frontend-quality-review]").GetAttribute("data-kind").Should().Be("NotApplicable");
        cut.Find("[data-testid=nav-applicability-azure-environment]").GetAttribute("title").Should().Contain("Amazon Web Services");
    }

    [Fact]
    public void SecurityClassification_ShowsExtension_OnlyWhenTheExtensionIsExplicitlyEnabled()
    {
        RegisterApplicability();
        _api.Coverage = Snapshot();
        var cut = Render<NavMenu>();
        cut.WaitForAssertion(() => Status(cut, "security-classification-review").Should().Be("N/A"));
        cut.Find("a[href='security-classification-review']").TextContent.Should().NotContain("M2LB").And.NotContain("Extension");

        _api.Coverage = Snapshot(extensions: [DomainExtensionIds.M2lbChildSecurityClassification]);
        _workspace.Raise(w => w.Changed += null);

        cut.WaitForAssertion(() => Status(cut, "security-classification-review").Should().Be("Extension"));
        var badge = cut.Find("[data-testid=nav-applicability-security-classification-review]");
        badge.GetAttribute("title").Should().StartWith("Provided by a project-specific extension enabled for this project.");
        cut.Find("a[href='security-classification-review'] .nav-label").TextContent.Should().Be("Security Classification");
    }

    [Fact]
    public void EnvironmentAnalysis_NamesAzure_OnlyWhenAzureEvidenceExists()
    {
        RegisterApplicability();
        _api.Coverage = Snapshot([Tech("cloud.azure", "Microsoft Azure", TechnologyArea.Cloud)]);
        var cut = Render<NavMenu>();

        cut.WaitForAssertion(() => Status(cut, "azure-environment").Should().Be("Azure"));
        cut.Find("a[href='azure-environment'] .nav-label").TextContent.Should().Be("Environment Analysis");
        cut.Find("a[href='azure-environment'] .visually-hidden").TextContent.Should().Be(", Provider: Azure");

        _api.Coverage = Snapshot([Tech("lang.java", "Java", TechnologyArea.Language)]);
        _workspace.Raise(w => w.Changed += null);
        cut.WaitForAssertion(() => Status(cut, "azure-environment").Should().Be("N/A"));
    }

    [Fact]
    public void EmptyWorkspace_IsNavigable_WithTruthfulNeutralStatuses()
    {
        _profile = null;
        RegisterApplicability();
        var cut = Render<NavMenu>();

        cut.WaitForAssertion(() => Status(cut, "quality-review").Should().Be("No evidence"));
        Status(cut, "security-classification-review").Should().Be("N/A");
        Status(cut, "azure-environment").Should().Be("No evidence");
        Status(cut, "api-quality-review").Should().Be("Setup");
        cut.Find("[data-testid=nav-applicability-api-quality-review]").GetAttribute("data-kind").Should().Be("Setup");
        cut.FindAll(".nav-status-extension, .nav-status-provider").Should().BeEmpty();
        cut.FindAll("a[href='admin/system-settings']").Should().ContainSingle();
    }

    [Fact]
    public void DocumentOnlyProject_ShowsNeutralStates_AndNoBadgeForAnApplicableReview()
    {
        _snapshot = BirkNext.Web.Tests.Services.WorkspaceSnapshots.Loaded("Docs", "docs", "Docs", WorkspaceArtifactType.Specification, WorkspaceArtifactType.Plan);
        RegisterApplicability();
        var cut = Render<NavMenu>();

        cut.WaitForAssertion(() => Status(cut, "dependency-review").Should().Be("No evidence"));
        Status(cut, "quality-review").Should().BeNull("an applicable review needs no badge");
        cut.FindAll(".nav-applicability").Select(b => b.GetAttribute("data-kind"))
            .Should().OnlyContain(k => k == "NoEvidence" || k == "Setup" || k == "NotApplicable");
    }

    [Fact]
    public void SectionHeadings_AreButtonsThatCollapseTheirGroup()
    {
        var cut = Render<NavMenu>();
        var quality = cut.Find("[data-testid=nav-section-quality-testing]");

        quality.TagName.Should().Be("BUTTON");
        quality.GetAttribute("aria-expanded").Should().Be("true");
        quality.GetAttribute("aria-controls").Should().Be("nav-section-quality-testing");
        cut.Find("#nav-section-quality-testing").HasAttribute("hidden").Should().BeFalse();

        quality.Click();

        cut.Find("[data-testid=nav-section-quality-testing]").GetAttribute("aria-expanded").Should().Be("false");
        cut.Find("#nav-section-quality-testing").HasAttribute("hidden").Should().BeTrue();
        cut.Find("#nav-section-source-review").HasAttribute("hidden").Should().BeFalse("other sections are unaffected");

        cut.Find("[data-testid=nav-section-quality-testing]").Click();
        cut.Find("#nav-section-quality-testing").HasAttribute("hidden").Should().BeFalse();
    }

    [Fact]
    public void CollapseState_SurvivesARemount_ForTheSession()
    {
        var first = Render<NavMenu>();
        first.Find("[data-testid=nav-section-source-review]").Click();
        first.Dispose();

        var second = Render<NavMenu>();
        second.Find("[data-testid=nav-section-source-review]").GetAttribute("aria-expanded").Should().Be("false");
    }

    [Fact]
    public void NavigatingIntoACollapsedSection_ExpandsIt_AndMarksTheRowActive()
    {
        var cut = Render<NavMenu>();
        cut.Find("[data-testid=nav-section-quality-testing]").Click();
        cut.Find("#nav-section-quality-testing").HasAttribute("hidden").Should().BeTrue();

        Services.GetRequiredService<NavigationManager>().NavigateTo("performance-test-review");

        cut.WaitForAssertion(() => cut.Find("#nav-section-quality-testing").HasAttribute("hidden").Should().BeFalse());
        cut.Find("[data-testid=nav-section-quality-testing]").GetAttribute("aria-expanded").Should().Be("true");
        cut.Find("a[href='performance-test-review']").ClassList.Should().Contain("active");
        cut.FindAll("a.active").Should().ContainSingle();
    }

    [Fact]
    public void CurrentSection_IsExpandedOnFirstRender_EvenIfPreviouslyCollapsed()
    {
        Services.GetRequiredService<NavigationSectionState>().Toggle("admin");
        Services.GetRequiredService<NavigationManager>().NavigateTo("admin/system-settings");

        var cut = Render<NavMenu>();

        cut.Find("#nav-section-admin").HasAttribute("hidden").Should().BeFalse();
    }

    [Fact]
    public void FeatureVisibility_StillDecidesWhichRowsExist_IndependentOfCollapse()
    {
        Services.GetRequiredService<FeatureVisibilityService>().ApplyLocalFlags(new FeatureVisibilityDto { CriticalE2ERegression = false });
        var cut = Render<NavMenu>();

        cut.FindAll("a[href='critical-e2e-regression']").Should().BeEmpty();
        cut.FindAll("a[href='dependency-review']").Should().ContainSingle("unsupported/N/A reviews stay visible");
    }

    [Fact]
    public void SectionToggle_DoesNotCloseTheMobileMenu()
    {
        var cut = Render<NavMenu>();
        cut.Find("[data-testid=nav-section-quality-testing]").Click();
        cut.Find(".nav-scrollable").ClassList.Should().NotContain("collapse");
    }

    [Fact]
    public void Catalog_ResolvesSections_ForPrefixAndExactRoutes()
    {
        NavigationCatalog.SectionFor("dashboard/details")!.Id.Should().Be("getting-started");
        NavigationCatalog.SectionFor("critical-e2e-regression")!.Id.Should().Be("quality-testing");
        NavigationCatalog.SectionFor("admin/system-settings")!.Id.Should().Be("admin", "the Target Environments deep link never claims the System Settings page");
        NavigationCatalog.SectionFor("plan-explorer/extra").Should().BeNull("exact-match rows do not claim sub-paths");
        NavigationCatalog.Sections.SelectMany(s => s.Items).Select(i => i.Route).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Sections_FollowTheEvidenceFlow_InputsFirst_ThenTheirConsumers()
    {
        var cut = Render<NavMenu>();

        cut.FindAll("button.nav-section").Select(b => b.GetAttribute("data-testid")!["nav-section-".Length..]).Should().Equal(
            "getting-started", "project-inputs", "document-review", "traceability", "source-review", "quality-testing", "extensions", "admin");
        cut.FindAll("button.nav-section").Select(b => b.TextContent.Trim()).Should().StartWith(["Getting Started", "Project Inputs", "Document Review"]);
    }

    [Theory]
    [InlineData("getting-started", new[] { "getting-started", "user-guide", "dashboard" })]
    [InlineData("project-inputs", new[] { "sample-projects", "source-analysis", NavigationCatalog.TargetEnvironmentsRoute })]
    [InlineData("document-review", new[] { "specification-explorer", "constitution-explorer", "data-model-explorer", "plan-explorer", "task-explorer" })]
    [InlineData("source-review", new[] { "technology-coverage", "dependency-review", "pipeline-review", "azure-environment" })]
    [InlineData("quality-testing", new[] { "quality-review", "frontend-quality-review", "api-quality-review", "integration-quality-review", "performance-test-review", "critical-e2e-regression" })]
    [InlineData("extensions", new[] { "security-classification-review" })]
    [InlineData("admin", new[] { "admin/system-settings" })]
    public void Rows_AreOrderedFromPrerequisiteToConsumer(string section, string[] routes)
    {
        var cut = Render<NavMenu>();

        cut.FindAll($"#nav-section-{section} a.nav-link").Select(a => a.GetAttribute("href")).Should().Equal(routes);
    }

    [Fact]
    public void TraceabilitySection_ListsRequirementsThenImplementation()
    {
        var cut = Render<NavMenu>();

        cut.FindAll("#nav-section-traceability a.nav-link").Select(a => a.GetAttribute("href")).Should()
            .StartWith(["artifact-traceability", "task-alignment", "implementation-traceability"]);
    }

    [Fact]
    public void EveryRowAppearsOnce_AndSourceAnalysisIsOnlyAnInput()
    {
        var routes = NavigationCatalog.Sections.SelectMany(s => s.Items).Select(i => i.Route).ToList();

        routes.Should().OnlyHaveUniqueItems();
        NavigationCatalog.Sections.Where(s => s.Items.Any(i => i.Route == "source-analysis")).Select(s => s.Id).Should().Equal("project-inputs");
        NavigationCatalog.Sections.Where(s => s.Items.Any(i => i.Route == "security-classification-review")).Select(s => s.Id).Should().Equal("extensions");
        NavigationCatalog.Sections.Select(s => s.Id).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void TargetEnvironments_IsADeepLinkIntoSystemSettings_NotASecondPage()
    {
        var cut = Render<NavMenu>();

        var link = cut.Find("#nav-section-project-inputs a[href='admin/system-settings?section=target-environments']");
        link.QuerySelector(".nav-label")!.TextContent.Should().Be("Target Environments");
        link.GetAttribute("title").Should().Contain("opens System Settings");
        cut.FindAll("#nav-section-admin a[href='admin/system-settings']").Should().ContainSingle("System Settings stays under Admin");
        DocumentationCatalog.NormalizeRoute(NavigationCatalog.TargetEnvironmentsRoute).Should().Be("/admin/system-settings");
    }

    [Fact]
    public void Groups_HaveMeaningfulAccessibleNames()
    {
        var cut = Render<NavMenu>();

        cut.Find("#nav-section-project-inputs").GetAttribute("role").Should().Be("group");
        cut.Find("#nav-section-project-inputs").GetAttribute("aria-label").Should().Be("Project Inputs navigation group");
    }

    [Fact]
    public void SecurityClassification_KeepsItsExtensionApplicability_InTheExtensionsGroup()
    {
        RegisterApplicability();
        _api.Coverage = Snapshot(extensions: [DomainExtensionIds.M2lbChildSecurityClassification]);
        var cut = Render<NavMenu>();

        cut.WaitForAssertion(() => cut.Find("#nav-section-extensions [data-testid=nav-applicability-security-classification-review]").TextContent.Should().Be("Extension"));
    }

    [Fact]
    public void ASectionWithoutVisibleRows_IsNotShown()
    {
        Services.GetRequiredService<FeatureVisibilityService>().ApplyLocalFlags(new FeatureVisibilityDto
        {
            SpecificationExplorer = false, ConstitutionExplorer = false, DataModelExplorer = false, PlanExplorer = false, TaskExplorer = false,
        });
        var cut = Render<NavMenu>();

        cut.FindAll("[data-testid=nav-section-document-review]").Should().BeEmpty();
        cut.FindAll("[data-testid=nav-section-source-review]").Should().ContainSingle("Technology Coverage is always visible");
    }

    private static IReadOnlyList<string> CurrentRows(IRenderedComponent<NavMenu> cut) =>
        cut.FindAll("a.nav-link.active").Select(a => a.GetAttribute("href")!).ToList();

    [Fact]
    public void TargetEnvironmentsShortcut_IsCurrentOnlyWhileItsSettingsSectionIsActive()
    {
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("admin/system-settings?section=target-environments");
        var cut = Render<NavMenu>();

        CurrentRows(cut).Should().Equal([NavigationCatalog.TargetEnvironmentsRoute], "the shortcut, not System Settings as well");
        cut.Find($"a[href='{NavigationCatalog.TargetEnvironmentsRoute}']").GetAttribute("aria-current").Should().Be("page");
        cut.Find("#nav-section-project-inputs").HasAttribute("hidden").Should().BeFalse();

        navigation.NavigateTo("admin/system-settings?section=performance-test-engines");

        cut.WaitForAssertion(() => CurrentRows(cut).Should().Equal("admin/system-settings"));
        cut.Find($"a[href='{NavigationCatalog.TargetEnvironmentsRoute}']").HasAttribute("aria-current").Should().BeFalse();
        cut.FindAll("[aria-current=page]").Should().ContainSingle();
    }

    [Theory]
    [InlineData("admin/system-settings", "admin/system-settings")]
    [InlineData("admin/system-settings?section=maintenance", "admin/system-settings")]
    [InlineData("admin/system-settings?section=target-environments&tab=auth", NavigationCatalog.TargetEnvironmentsRoute)]
    [InlineData("dashboard/details", "dashboard")]
    [InlineData("plan-explorer", "plan-explorer")]
    public void ExactlyOneRowIsCurrent_ForEachLocation(string location, string expected)
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo(location);
        var cut = Render<NavMenu>();

        CurrentRows(cut).Should().Equal(expected);
    }
}
