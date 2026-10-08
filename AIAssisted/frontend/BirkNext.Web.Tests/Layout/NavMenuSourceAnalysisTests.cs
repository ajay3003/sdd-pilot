using BirkNext.Web.Layout;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Web.Tests.Layout;

/// <summary>Source Analysis is one optional Project Inputs item (after Import Project and Sample Projects), controlled by Feature Visibility; its consumers are under Source Review.</summary>
public sealed class NavMenuSourceAnalysisTests : BunitContext
{
    public NavMenuSourceAnalysisTests() => Services.AddSingleton<FeatureVisibilityService>();

    private static IReadOnlyList<string> Entries(IRenderedComponent<NavMenu> cut) =>
        cut.FindAll("nav .nav-section, nav .nav-item").Select(e => (e.ClassList.Contains("nav-section") ? "§" : "") + e.TextContent.Trim()).ToList();

    [Fact]
    public void SourceAnalysisIsAProjectInput_AndItsConsumersFormTheSourceReviewGroup_EachAppearingOnce()
    {
        // The backend's default configuration: Impact Analysis and Spec Drift are disabled.
        Services.GetRequiredService<FeatureVisibilityService>().ApplyLocalFlags(new FeatureVisibilityDto { ImpactAnalysis = false, SpecDrift = false });
        var cut = Render<NavMenu>();
        var entries = Entries(cut);

        cut.FindAll("a[href='source-analysis']").Should().ContainSingle();
        cut.FindAll("a").Count(a => a.TextContent.Trim() == "Source Analysis").Should().Be(1);
        entries[0].Should().Be("§Getting Started", "Source Analysis is no longer a special item above the groups");
        entries.Should().ContainInConsecutiveOrder("§Project Inputs", "Import Project", "Sample Projects", "Source Analysis", "Target Environments");
        entries.Should().ContainInConsecutiveOrder("§Traceability", "Requirements Traceability", "Implementation Review", "Implementation Traceability");
        entries.Should().ContainInConsecutiveOrder("§Source Review", "Technology Coverage", "Dependency Review", "Pipeline Review", "Environment Analysis");
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
        Entries(cut).Should().ContainInConsecutiveOrder("§Project Inputs", "Import Project", "Sample Projects", "Target Environments");

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
    public void TheProjectInputsHeadingShowsWhenSourceAnalysisIsItsOnlyVisibleItem()
    {
        Services.GetRequiredService<FeatureVisibilityService>().ApplyLocalFlags(new FeatureVisibilityDto
        {
            SampleProjects = false, AdminSystemSettings = false, SourceAnalysis = true,
        });
        var cut = Render<NavMenu>();

        // Import Project feeds Source Analysis too, so it stays visible with it.
        Entries(cut).Should().ContainInConsecutiveOrder("§Project Inputs", "Import Project", "Source Analysis", "§Document Review");
    }

    [Fact]
    public void MissingFlagInAnOlderBackendResponse_DefaultsToVisible()
    {
        var dto = System.Text.Json.JsonSerializer.Deserialize<FeatureVisibilityDto>("{\"implementationTraceability\":true}")!;
        dto.SourceAnalysis.Should().BeTrue();
    }
}
