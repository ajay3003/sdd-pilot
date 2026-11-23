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
    public async Task Reset_MakesQaActive_AndKeepsDevelopmentsConfiguredFrontendUrl()
    {
        // 2026-11-23: QA is the default (active and selected) with the QA application as its target; Development keeps its configured URL.
        var (settings, js) = await FreshAsync();
        var dev = settings.Settings.Profiles.Single(p => p.Name == "Development");
        dev.TargetUrl = "https://m2lbdev.bufetat.no";
        settings.SelectActiveProfile(dev.Id);
        await settings.SaveAsync(js);

        await settings.ResetForLocalDataResetAsync(js);

        settings.ActiveProfile!.Name.Should().Be("QA");
        settings.ActiveProfile.TargetUrl.Should().Be(FrontendAnalysisSettingsService.DefaultQaUrl);
        settings.Settings.Profiles.Single(p => p.Name == "Development").TargetUrl.Should().Be("https://m2lbdev.bufetat.no");
        settings.Settings.Profiles.Select(p => p.Name).Should().Equal("QA", "Development", "Local", "Production");
        settings.Settings.Profiles.Single(p => p.Name == "Local").TargetUrl.Should().Be("https://localhost:5001");
        js.Json.Should().Contain(settings.ActiveProfile.Id, "the reset state is saved, so a reload keeps QA active");
    }

    [Fact]
    public async Task Reset_WithoutAConfiguredDevelopmentUrl_UsesThePlaceholder_NotAProjectUrl()
    {
        var (settings, js) = await FreshAsync();
        await settings.ResetForLocalDataResetAsync(js);
        settings.ActiveProfile!.Name.Should().Be("QA");
        settings.Settings.Profiles.Single(p => p.Name == "Development").TargetUrl.Should().Be("https://example-dev.local");
        FrontendAnalysisSettingsService.IsSeedPlaceholder("https://example-qa.local").Should().BeTrue();
        FrontendAnalysisSettingsService.IsSeedPlaceholder("https://m2lbdev.bufetat.no").Should().BeFalse();
    }

    [Fact]
    public async Task The_seeded_example_url_is_not_a_target_so_runtime_reviews_do_not_look_ready()
    {
        // The seeded Development environment keeps an example URL. Nothing can be tested there, so the run gates and the applicability of
        // browser/API reviews must treat it as "no target" (2026-11-23: FQR/AQR/Performance/Critical E2E showed Ready to run on an empty DB).
        var (settings, _) = await FreshAsync();
        var dev = settings.Settings.Profiles.Single(p => p.Name == "Development");
        dev.TargetUrl.Should().Be("https://example-dev.local");
        FrontendAnalysisSettingsService.IsConfiguredTarget(dev.TargetUrl).Should().BeFalse();
        FrontendAnalysisSettingsService.IsConfiguredTarget("").Should().BeFalse();
        FrontendAnalysisSettingsService.IsConfiguredTarget("https://m2lbdev.bufetat.no").Should().BeTrue();

        var context = new FrontendAnalysisContext { TargetUrl = dev.TargetUrl };
        context.HasTargetUrl.Should().BeFalse();
        context.HasExampleTargetUrl.Should().BeTrue();
        new FrontendAnalysisContext { TargetUrl = "https://app.example.org" }.HasTargetUrl.Should().BeTrue("only the seed's example-*.local hosts are examples");

        var input = TechnologyCoveragePresentation.Input(null, dev, hasRequirements: false, hasDocumentation: false);
        (input.HasBrowserTarget, input.HasApiTarget).Should().Be((false, false));
        dev.TargetUrl = "https://m2lbdev.bufetat.no";
        input = TechnologyCoveragePresentation.Input(null, dev, hasRequirements: false, hasDocumentation: false);
        (input.HasBrowserTarget, input.HasApiTarget).Should().Be((true, true));
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
