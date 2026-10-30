using BirkNext.Web.Layout;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Web.Tests.Layout;

/// <summary>Import Project is a Workspace input; Source Analysis and generic source tooling such as Schema Studio live under Source & Architecture.</summary>
public sealed class NavMenuSourceAnalysisTests : BunitContext
{
    public NavMenuSourceAnalysisTests() => Services.AddSingleton<FeatureVisibilityService>();

    private static IReadOnlyList<string> Entries(IRenderedComponent<NavMenu> cut) =>
        cut.FindAll("nav .nav-section, nav .nav-item").Select(e => (e.ClassList.Contains("nav-section") ? "§" : "") + (e.QuerySelector(".nav-label")?.TextContent ?? e.TextContent).Trim()).ToList();

    [Fact]
    public void SourceAnalysisIsAProjectInput_AndItsConsumersFormTheSourceReviewGroup_EachAppearingOnce()
    {
        // The backend's default configuration: Impact Analysis and Documentation Drift are disabled.
        Services.GetRequiredService<FeatureVisibilityService>().ApplyLocalFlags(new FeatureVisibilityDto { ImpactAnalysis = false, SpecDrift = false });
        var cut = Render<NavMenu>();
        var entries = Entries(cut);

        cut.FindAll("a[href='source-analysis']").Should().ContainSingle();
        cut.FindAll("a").Count(a => a.TextContent.Trim() == "Source Analysis").Should().Be(1);
        entries[0].Should().Be("§Workspace", "Source Analysis is no longer a special item above the groups");
        entries.Should().ContainInConsecutiveOrder("Import Project", "Demo Projects", "Target Environments", "§Delivery Management");
        entries.Should().ContainInConsecutiveOrder("§Traceability & Impact", "Requirements Traceability", "§Source & Architecture");
        entries.Should().NotContain("Change Traceability", "optional Azure DevOps provenance is off by default");
        entries.Should().ContainInConsecutiveOrder("§Source & Architecture", "Source Analysis", "Schema Studio", "Generated Documentation Explorer", "Generated Documentation Health", "Technology Coverage", "Dependency Review", "Pipeline Review", "Environment Analysis");
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
        Entries(cut).Should().ContainInConsecutiveOrder("Import Project", "Demo Projects", "Target Environments");

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
    public void ImportProjectStaysInProjectInputs_WhenSourceAnalysisIsTheOnlyVisibleSourceInput()
    {
        Services.GetRequiredService<FeatureVisibilityService>().ApplyLocalFlags(new FeatureVisibilityDto
        {
            SampleProjects = false, AdminSystemSettings = false, SourceAnalysis = true,
        });
        var cut = Render<NavMenu>();

        // Import Project feeds Source Analysis too, so it stays visible with it; Source Analysis itself is a source review.
        Entries(cut).Should().ContainInConsecutiveOrder("Recommended Workflow", "Import Project", "§Delivery Management");
        Entries(cut).Should().ContainInConsecutiveOrder("§Source & Architecture", "Source Analysis", "Schema Studio", "Generated Documentation Explorer", "Generated Documentation Health", "Technology Coverage");
    }

    [Fact]
    public void MissingFlagInAnOlderBackendResponse_DefaultsToVisible()
    {
        var dto = System.Text.Json.JsonSerializer.Deserialize<FeatureVisibilityDto>("{\"implementationTraceability\":true}")!;
        dto.SourceAnalysis.Should().BeTrue();
    }
}
