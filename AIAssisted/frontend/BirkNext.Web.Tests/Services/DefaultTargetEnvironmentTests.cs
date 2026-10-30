using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// Default Target Environment: Development is active (and therefore selected) on a fresh installation and after a complete data reset; Local is
/// available but never chosen automatically. The reset keeps the Development environment's own configured frontend URL (never a built-in
/// project URL). A saved selection is never overridden by startup.
/// </summary>
public sealed class DefaultTargetEnvironmentTests
{
    private static async Task<(FrontendAnalysisSettingsService Settings, ActiveTargetEnvironmentTests.Storage Js)> FreshAsync()
    {
        var js = new ActiveTargetEnvironmentTests.Storage(null);
        var settings = new FrontendAnalysisSettingsService();
        await settings.LoadAsync(js);
        return (settings, js);
    }

    [Fact]
    public async Task Reset_MakesDevelopmentActive_AndKeepsItsConfiguredFrontendUrl()
    {
        var (settings, js) = await FreshAsync();
        var dev = settings.Settings.Profiles.Single(p => p.Name == "Development");
        dev.TargetUrl = "https://m2lbdev.bufetat.no";
        settings.SelectActiveProfile(settings.Settings.Profiles.Single(p => p.Name == "Local").Id);
        await settings.SaveAsync(js);

        await settings.ResetForLocalDataResetAsync(js);

        settings.ActiveProfile!.Name.Should().Be("Development");
        settings.ActiveProfile.TargetUrl.Should().Be("https://m2lbdev.bufetat.no");
        settings.Settings.Profiles.Select(p => p.Name).Should().Equal("Local", "Development", "QA", "Production");
        settings.Settings.Profiles.Single(p => p.Name == "Local").TargetUrl.Should().Be("https://localhost:5001");
        js.Json.Should().Contain(settings.ActiveProfile.Id, "the reset state is saved, so a reload keeps Development active");
    }

    [Fact]
    public async Task Reset_WithoutAConfiguredDevelopmentUrl_UsesThePlaceholder_NotAProjectUrl()
    {
        var (settings, js) = await FreshAsync();
        await settings.ResetForLocalDataResetAsync(js);
        settings.ActiveProfile!.Name.Should().Be("Development");
        settings.ActiveProfile.TargetUrl.Should().Be("https://example-dev.local");
        FrontendAnalysisSettingsService.IsSeedPlaceholder("https://example-qa.local").Should().BeTrue();
        FrontendAnalysisSettingsService.IsSeedPlaceholder("https://m2lbdev.bufetat.no").Should().BeFalse();
    }

    [Fact]
    public async Task SwitchingToQaOrProduction_PersistsAcrossReload_AndStartupNeverResetsIt()
    {
        var (settings, js) = await FreshAsync();
        await settings.ResetForLocalDataResetAsync(js);
        var qa = settings.Settings.Profiles.Single(p => p.Name == "QA");
        settings.SelectActiveProfile(qa.Id);
        await settings.SaveAsync(js);

        var reloaded = new FrontendAnalysisSettingsService();
        await reloaded.LoadAsync(js);
        reloaded.ActiveProfile!.Name.Should().Be("QA", "an explicit saved selection wins over the default");

        var prod = reloaded.Settings.Profiles.Single(p => p.Name == "Production");
        reloaded.SelectActiveProfile(prod.Id);
        await reloaded.SaveAsync(js);
        var again = new FrontendAnalysisSettingsService();
        await again.LoadAsync(js);
        again.ActiveProfile!.Name.Should().Be("Production");
    }

    [Fact]
    public async Task Reset_CreatesNoDuplicateEnvironments()
    {
        var (settings, js) = await FreshAsync();
        await settings.ResetForLocalDataResetAsync(js);
        await settings.ResetForLocalDataResetAsync(js);
        settings.Settings.Profiles.Should().HaveCount(4);
        settings.Settings.Profiles.Count(p => p.EnvironmentType == FrontendEnvironmentType.Development).Should().Be(1);
    }
}
