using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using BirkNext.Web.Tests.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Moq;
using TargetSettings = BirkNext.Web.Components.FrontendAnalysisSettings;

namespace BirkNext.Web.Tests.Pages;

public sealed class ActiveTargetEnvironmentUITests : BunitContext
{
    private readonly FrontendAnalysisSettingsService _settings = new();
    private readonly ActiveTargetEnvironmentTests.Storage _storage = new();
    private readonly Mock<IFrontendQualityReviewOrchestrator> _orchestrator = new();

    private async Task Register(string? active = "dev")
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<IJSRuntime>(_storage);
        await _settings.LoadAsync(_storage);
        _settings.Settings.ActiveProfileId = active;
        Services.AddSingleton<IFrontendAnalysisSettingsService>(_settings);
        Services.AddSingleton<IFrontendAnalysisContextFactory>(ActiveTargetEnvironmentTests.Factory(_settings, _storage));
        Services.AddSingleton(Mock.Of<ITargetEnvironmentDetectionApiService>());
        Services.AddSingleton(_orchestrator.Object);
        Services.AddSingleton<RuntimeReviewSessionService>();
        Services.AddSingleton(Mock.Of<IWorkspaceSessionService>());
        Services.AddSingleton<IReportExportService>(new ReportExportService());
        Services.AddSingleton(Mock.Of<IAuthenticatedBrowserSessionService>());
        var status = new Mock<IFrontendQualityEngineStatusApiService>();
        status.Setup(s => s.GetStatusAsync(It.IsAny<ReviewAuthenticationModeDto>(), It.IsAny<ReviewEngineSelectionDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FrontendQualityEngineStatusReportDto { Engines = [] });
        Services.AddSingleton(status.Object);
    }

    [Theory]
    [InlineData("dev", "QA", "M2LB DEV")]
    [InlineData("qa", "M2LB DEV", "QA")]
    public async Task SelectedSettingsProfileDoesNotChangeFqrActiveTarget(string active, string selectedName, string activeName)
    {
        await Register(active);
        var settingsPage = Render<TargetSettings>();
        settingsPage.FindAll(".fa-profile-chip").Single(b => b.TextContent.Contains(selectedName)).Click();
        settingsPage.Find(".fa-detail-name").TextContent.Should().Be(selectedName);
        settingsPage.Find(".fa-active-card-name").TextContent.Should().Be(activeName);
        settingsPage.Find(".fa-profile-chip .fa-active-badge").ParentElement!.TextContent.Should().Contain(activeName);
        var review = Render<FrontendQualityReview>();
        review.Find("[data-testid=fqr-access-target]").TextContent.Should().Contain(activeName);
        _settings.Settings.ActiveProfileId.Should().Be(active);
    }

    [Fact]
    public async Task SetAsActiveUpdatesBadgeAndPersistsImmediatelyWithoutDetection()
    {
        await Register("qa");
        var page = Render<TargetSettings>();
        page.FindAll(".fa-profile-chip").Single(b => b.TextContent.Contains("M2LB DEV")).Click();
        page.FindAll("button").Single(b => b.TextContent.Trim() == "Set as Active").Click();
        page.WaitForAssertion(() => page.Find(".fa-active-card-name").TextContent.Should().Be("M2LB DEV"));
        page.Markup.Should().Contain("Active environment");
        _settings.Settings.ActiveProfileId.Should().Be("dev");
        var restarted = new FrontendAnalysisSettingsService();
        var context = await ActiveTargetEnvironmentTests.Factory(restarted, _storage).GetActiveContextAsync();
        context.ActiveProfile.Id.Should().Be("dev");
        Render<FrontendQualityReview>().Find("[data-testid=fqr-access-url]").TextContent.Should().Be("https://m2lbdev.bufetat.no/");
    }

    [Fact]
    public async Task FailedActivationSaveRestoresPreviousActiveTargetAndReportsFailure()
    {
        await Register("dev");
        var page = Render<TargetSettings>();
        page.FindAll(".fa-profile-chip").Single(b => b.TextContent.Contains("QA")).Click();
        _storage.FailWrites = true;
        page.FindAll("button").Single(b => b.TextContent.Trim() == "Set as Active").Click();
        page.WaitForAssertion(() => page.Markup.Should().Contain("could not be saved"));
        _settings.Settings.ActiveProfileId.Should().Be("dev");
        page.Find(".fa-active-card-name").TextContent.Should().Be("M2LB DEV");
        (await ActiveTargetEnvironmentTests.Factory(new(), _storage).GetActiveContextAsync()).ActiveProfile.Id.Should().Be("dev");
    }

    [Theory]
    [InlineData(null, "No active Target Environment")]
    [InlineData("missing", "Active Target Environment is unavailable.")]
    public async Task MissingActiveBlocksRunAndShowsSettingsAction(string? active, string message)
    {
        await Register(active);
        var page = Render<FrontendQualityReview>();
        page.Markup.Should().Contain(message);
        page.FindAll("button").Single(b => b.TextContent.Contains("Run Frontend Quality Review")).HasAttribute("disabled").Should().BeTrue();
        page.FindAll("a").Should().Contain(a => a.TextContent.Contains("Open Target Environments"));
        page.FindAll("[data-testid=fqr-target-access]").Should().BeEmpty();
        _orchestrator.Invocations.Should().BeEmpty();
        var settingsPage = Render<TargetSettings>();
        settingsPage.FindAll(".fa-profile-chip .fa-active-badge").Should().BeEmpty();
    }

    [Fact]
    public async Task RunWithActiveRemovedAfterLanding_ShowsReviewNotStartedBannerAndDoesNotRun()
    {
        await Register("dev");
        var page = Render<FrontendQualityReview>();
        page.FindAll("[data-testid=fqr-run-not-started]").Should().BeEmpty();

        // Active target disappears between landing and clicking Run (e.g. deleted in another tab).
        _settings.Settings.ActiveProfileId = null;
        var run = page.FindAll("button").Single(b => b.TextContent.Contains("Run Frontend Quality Review"));
        run.HasAttribute("disabled").Should().BeFalse("landing was rendered while the target was still active");
        await run.ClickAsync(new());

        page.WaitForAssertion(() =>
        {
            var banner = page.Find("[data-testid=fqr-run-not-started]");
            banner.GetAttribute("role").Should().Be("alert");
            banner.TextContent.Should().Contain("Review not started")
                .And.Contain("No active Target Environment")
                .And.Contain("Select a Target Environment and choose Set as Active.");
            banner.QuerySelectorAll("a").Should().Contain(a => a.TextContent.Contains("Open Target Environments"));
        });
        _orchestrator.Invocations.Should().BeEmpty();
        page.FindAll("button").Single(b => b.TextContent.Contains("Run Frontend Quality Review")).HasAttribute("disabled").Should().BeTrue();

        // Transient: dismiss removes it; the landing configuration state still explains the missing target.
        page.Find("[data-testid=fqr-run-not-started] button").Click();
        page.FindAll("[data-testid=fqr-run-not-started]").Should().BeEmpty();
        page.Markup.Should().Contain("No active Target Environment");
    }

    [Fact]
    public async Task RunReresolvesActiveAndKeepsSnapshotUntilCompletionThenNextRunUsesNewActive()
    {
        await Register("qa");
        var pending = new TaskCompletionSource<FrontendQualityReviewOrchestrationResult>();
        FrontendAnalysisContext? execution = null;
        _orchestrator.Setup(o => o.RunAsync(It.IsAny<string>(), It.IsAny<FrontendAnalysisContext>(), It.IsAny<FrontendQualityEngineExecutionSnapshot?>(), It.IsAny<CancellationToken>()))
            .Callback<string, FrontendAnalysisContext, FrontendQualityEngineExecutionSnapshot?, CancellationToken>((_, context, _, _) => execution = context)
            .Returns(() => pending.Task);
        var page = Render<FrontendQualityReview>();
        _settings.SelectActiveProfile("dev"); // change after landing, before clicking Run
        var run = page.FindAll("button").Single(b => b.TextContent.Contains("Run Frontend Quality Review"));
        var running = page.InvokeAsync(() => run.Click());
        page.WaitForAssertion(() => execution!.ActiveProfile.Id.Should().Be("dev"));
        _settings.SelectActiveProfile("qa"); // change while orchestration is suspended
        _settings.Settings.Profiles.Single(p => p.Id == "dev").Name = "Edited during run";
        execution!.ActiveProfile.Name.Should().Be("M2LB DEV");
        pending.SetResult(new(QualityReport: new() { TargetUrl = execution.TargetUrl, GeneratedAt = DateTime.UtcNow }));
        await running;
        page.WaitForAssertion(() => page.Find("[data-testid=fqr-result-meta]").TextContent.Should().Contain("M2LB DEV").And.Contain("https://m2lbdev.bufetat.no/"));
        var stored = Services.GetRequiredService<RuntimeReviewSessionService>().QualityReview.Report!;
        new ReportExportService().ExportFrontendQualityReview(stored, "test").Should().Contain("M2LB DEV").And.NotContain("example-qa.local");
        page.FindAll("button").Single(b => b.TextContent.Trim() == "Back to Frontend Quality Review").Click();
        page.WaitForAssertion(() => page.Find("[data-testid=fqr-access-target]").TextContent.Should().Contain("QA"));
        page.FindAll("button").Single(b => b.TextContent.Contains("Run Frontend Quality Review")).Click();
        page.WaitForAssertion(() => execution!.ActiveProfile.Id.Should().Be("qa"));
    }
}
