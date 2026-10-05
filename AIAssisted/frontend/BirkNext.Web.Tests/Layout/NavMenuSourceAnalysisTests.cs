using BirkNext.Web.Layout;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Web.Tests.Layout;

/// <summary>Source Analysis is one optional Analysis menu item (after Implementation Traceability), controlled by Feature Visibility.</summary>
public sealed class NavMenuSourceAnalysisTests : BunitContext
{
    public NavMenuSourceAnalysisTests() => Services.AddSingleton<FeatureVisibilityService>();

    private static IReadOnlyList<string> Entries(IRenderedComponent<NavMenu> cut) =>
        cut.FindAll("nav .nav-section, nav .nav-item").Select(e => (e.ClassList.Contains("nav-section") ? "§" : "") + e.TextContent.Trim()).ToList();

    [Fact]
    public void SourceAnalysisThenAzureEnvironmentCloseTheAnalysisGroup_AndAppearOnce()
    {
        // The backend's default configuration: Impact Analysis and Spec Drift are disabled.
        Services.GetRequiredService<FeatureVisibilityService>().ApplyLocalFlags(new FeatureVisibilityDto { ImpactAnalysis = false, SpecDrift = false });
        var cut = Render<NavMenu>();
        var entries = Entries(cut);

        cut.FindAll("a[href='source-analysis']").Should().ContainSingle();
        cut.FindAll("a").Count(a => a.TextContent.Trim() == "Source Analysis").Should().Be(1);
        entries[0].Should().Be("§Getting Started", "Source Analysis is no longer a special item above the groups");
        var analysis = entries.ToList().IndexOf("§Analysis");
        var quality = entries.ToList().IndexOf("§Quality");
        entries.Skip(analysis + 1).Take(quality - analysis - 1).Should().Equal("Requirements Traceability", "Implementation Review", "Implementation Traceability", "Source Analysis", "Technology Coverage", "Environment Analysis");
        cut.FindAll("a[href='azure-environment']").Should().ContainSingle();
        cut.Find("a[href='source-analysis'] .nav-icon-source-analysis").GetAttribute("aria-hidden").Should().Be("true");
    }

    [Fact]
    public void HiddenWhenDisabled_AndBackWhenReEnabled()
    {
        var flags = Services.GetRequiredService<FeatureVisibilityService>();
        flags.ApplyLocalFlags(new FeatureVisibilityDto { SourceAnalysis = false });
        var cut = Render<NavMenu>();

        cut.FindAll("a[href='source-analysis']").Should().BeEmpty();
        Entries(cut).Should().Contain("Implementation Traceability", "the rest of the Analysis group is unchanged");

        flags.ApplyLocalFlags(new FeatureVisibilityDto { SourceAnalysis = true });
        cut.Render();
        cut.FindAll("a[href='source-analysis']").Should().ContainSingle();
    }

    [Fact]
    public void AzureEnvironmentIsHiddenByItsOwnFlag_WithoutAffectingSourceAnalysis()
    {
        var flags = Services.GetRequiredService<FeatureVisibilityService>();
        flags.ApplyLocalFlags(new FeatureVisibilityDto { AzureEnvironmentAnalysis = false });
        var cut = Render<NavMenu>();
        cut.FindAll("a[href='azure-environment']").Should().BeEmpty();
        cut.FindAll("a[href='source-analysis']").Should().ContainSingle();

        flags.ApplyLocalFlags(new FeatureVisibilityDto { AzureEnvironmentAnalysis = true });
        cut.Render();
        cut.FindAll("a[href='azure-environment']").Should().ContainSingle();
    }

    [Fact]
    public void TheAnalysisHeadingShowsWhenSourceAnalysisIsItsOnlyVisibleItem()
    {
        Services.GetRequiredService<FeatureVisibilityService>().ApplyLocalFlags(new FeatureVisibilityDto
        {
            ArtifactTraceability = false, ImplementationReview = false, ImplementationTraceability = false, ImpactAnalysis = false, SpecDrift = false, SourceAnalysis = true,
        });
        var cut = Render<NavMenu>();

        Entries(cut).Should().ContainInConsecutiveOrder("§Analysis", "Source Analysis");
    }

    [Fact]
    public void MissingFlagInAnOlderBackendResponse_DefaultsToVisible()
    {
        var dto = System.Text.Json.JsonSerializer.Deserialize<FeatureVisibilityDto>("{\"implementationTraceability\":true}")!;
        dto.SourceAnalysis.Should().BeTrue();
    }
}
