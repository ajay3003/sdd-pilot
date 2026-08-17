using BirkNext.Web.GraphQL;
using BirkNext.Web.Layout;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Pages;

public class AnalysisNavigationTests : BunitContext
{
    public AnalysisNavigationTests()
    {
        Services.AddSingleton<FeatureVisibilityService>();
        // SpecDrift injects IBirkNextClient; register a basic mock so DI succeeds
        // (LoadAsync catches all exceptions so a bare mock is sufficient for structure tests)
        Services.AddSingleton(new Mock<IBirkNextClient>().Object);
    }

    [Fact]
    public void Navigation_ContainsOnlyFourAnalysisItems()
    {
        var cut = Render<NavMenu>();
        var nav = cut.Find("nav").TextContent;

        nav.Should().Contain("Spec Drift");
        nav.Should().Contain("Impact Analysis");
        nav.Should().Contain("Implementation Review");
        nav.Should().Contain("Task Explorer");

        nav.Should().NotContain("Spec Comparison");
        nav.Should().NotContain("Specification Deltas");
        nav.Should().NotContain("Task Deltas");

        cut.FindAll("a[href='spec-drift']").Should().HaveCount(1);
        cut.FindAll("a[href='impact-analysis']").Should().HaveCount(1);
        cut.FindAll("a[href='task-alignment']").Should().HaveCount(1);
        cut.FindAll("a[href='task-explorer']").Should().HaveCount(1);
    }

    [Fact]
    public void SpecDrift_IncludesChangesTab()
    {
        var cut = Render<SpecDrift>();

        cut.Find("[data-testid='sd-changes-tab-btn']").Should().NotBeNull();
        cut.Find("[data-testid='sd-changes-tab-btn']").TextContent.Trim().Should().Be("Changes");
    }

    [Fact]
    public void OldRoutes_RedirectToSpecDrift()
    {
        Render<CompareSpecs>();

        var nav = Services.GetRequiredService<NavigationManager>();
        nav.Uri.Should().Contain("/spec-drift");
        nav.Uri.Should().Contain("tab=changes");
    }

    [Fact]
    public void TaskDeltas_IsFullyRemoved()
    {
        var dto = new FeatureVisibilityDto();
        dto.TaskDeltas.Should().BeFalse("Task Deltas is deprecated — defaults to hidden");

        var cut = Render<NavMenu>();
        cut.FindAll("a[href='task-deltas']").Should().BeEmpty();
    }

    [Fact]
    public void NoDuplicateSpecComparisonEntryExists()
    {
        var cut = Render<NavMenu>();

        cut.FindAll("a[href='compare']").Should().BeEmpty();
        cut.FindAll("a[href='compare/reviews']").Should().BeEmpty();
    }

    [Fact]
    public void Sidebar_IsConsistentWithNewModel()
    {
        var cut = Render<NavMenu>();
        var nav = cut.Find("nav").TextContent;

        nav.IndexOf("Dashboard", StringComparison.Ordinal)
            .Should().BeLessThan(nav.IndexOf("Specification Explorer", StringComparison.Ordinal));
        nav.IndexOf("Specification Explorer", StringComparison.Ordinal)
            .Should().BeLessThan(nav.IndexOf("Constitution Explorer", StringComparison.Ordinal));
        nav.IndexOf("Constitution Explorer", StringComparison.Ordinal)
            .Should().BeLessThan(nav.IndexOf("Data Model Explorer", StringComparison.Ordinal));
        nav.IndexOf("Data Model Explorer", StringComparison.Ordinal)
            .Should().BeLessThan(nav.IndexOf("Plan Explorer", StringComparison.Ordinal));
        nav.IndexOf("Plan Explorer", StringComparison.Ordinal)
            .Should().BeLessThan(nav.IndexOf("Task Explorer", StringComparison.Ordinal));

        nav.IndexOf("Spec Drift", StringComparison.Ordinal)
            .Should().BeLessThan(nav.IndexOf("Impact Analysis", StringComparison.Ordinal));
        nav.IndexOf("Impact Analysis", StringComparison.Ordinal)
            .Should().BeLessThan(nav.IndexOf("Requirements Traceability", StringComparison.Ordinal));
        nav.IndexOf("Requirements Traceability", StringComparison.Ordinal)
            .Should().BeLessThan(nav.IndexOf("Implementation Review", StringComparison.Ordinal));
        nav.IndexOf("Implementation Review", StringComparison.Ordinal)
            .Should().BeLessThan(nav.IndexOf("Implementation Traceability", StringComparison.Ordinal));
    }

    [Fact]
    public void Sidebar_ContainsOnlyFourAnalysisItems()
    {
        var cut = Render<NavMenu>();

        cut.FindAll("a[href='spec-drift']").Should().HaveCount(1);
        cut.FindAll("a[href='impact-analysis']").Should().HaveCount(1);
        cut.FindAll("a[href='task-alignment']").Should().HaveCount(1);
        cut.FindAll("a[href='task-explorer']").Should().HaveCount(1);

        cut.FindAll("a[href='compare']").Should().BeEmpty();
        cut.FindAll("a[href='compare/reviews']").Should().BeEmpty();
        cut.FindAll("a[href='task-deltas']").Should().BeEmpty();
    }

    [Fact]
    public void Sidebar_NoLegacyComparisonEntries()
    {
        var cut = Render<NavMenu>();
        var nav = cut.Find("nav").TextContent;

        nav.Should().NotContain("Spec Comparison");
        nav.Should().NotContain("Compare Specifications");
        cut.FindAll("a[href='compare']").Should().BeEmpty();
        cut.FindAll("a[href='compare/reviews']").Should().BeEmpty();
    }

    [Fact]
    public void Sidebar_NoDeltaEntriesExist()
    {
        var cut = Render<NavMenu>();
        var nav = cut.Find("nav").TextContent;

        nav.Should().NotContain("Specification Deltas");
        nav.Should().NotContain("Task Deltas");
        cut.FindAll("a[href='task-deltas']").Should().BeEmpty();
    }

    [Fact]
    public void Navigation_AllRoutesResolveToNewModel()
    {
        RoutesFor<SpecDrift>().Should().Contain("/spec-drift");
        RoutesFor<ImpactAnalysis>().Should().Contain("/impact-analysis");
        RoutesFor<TaskToSpecAlignment>().Should().Contain("/task-alignment");
        RoutesFor<TaskExplorer>().Should().Contain("/task-explorer");
    }

    [Fact]
    public void SpecDriftIsOnlySpecChangeEntryPoint()
    {
        var cut = Render<NavMenu>();

        cut.FindAll("a[href='spec-drift']").Should().HaveCount(1);
        cut.FindAll("a[href='compare']").Should().BeEmpty();
        cut.FindAll("a[href='compare/reviews']").Should().BeEmpty();
        cut.Find("nav").TextContent.Should().Contain("Spec Drift");
        cut.Find("nav").TextContent.Should().NotContain("Specification Deltas");
        cut.Find("nav").TextContent.Should().NotContain("Spec Comparison");
    }

    private static IReadOnlyList<string> RoutesFor<TComponent>() =>
        typeof(TComponent)
            .GetCustomAttributes(typeof(RouteAttribute), inherit: false)
            .Cast<RouteAttribute>()
            .Select(a => a.Template)
            .ToList();
}

