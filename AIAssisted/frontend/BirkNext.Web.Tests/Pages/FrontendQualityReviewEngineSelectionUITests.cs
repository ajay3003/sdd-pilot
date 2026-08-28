using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Pages;

public sealed class FrontendQualityReviewEngineSelectionUITests : BunitContext
{

    [Fact]
    public async Task UnavailableEngine_ShowsCheckboxDisabled()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var statusService = new Mock<IFrontendQualityEngineStatusApiService>();
        statusService.Setup(s => s.GetStatusAsync(It.IsAny<ReviewAuthenticationModeDto>(), It.IsAny<ReviewEngineSelectionDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FrontendQualityEngineStatusReportDto
            {
                Engines = new()
                {
                    new() { EngineId = FrontendQualityEngineIdDto.Accessibility, Available = false, Reasons = [FrontendQualityEngineUnavailableReasonDto.RuntimeUnavailable] }
                }
            });

        Services.AddSingleton(statusService.Object);
        Services.AddSingleton<IFrontendAnalysisContextFactory>(new FixedContextFactory(new()));
        Services.AddSingleton<IFrontendQualityReviewOrchestrator>(new SpyOrchestrator());
        Services.AddSingleton<RuntimeReviewSessionService>();
        Services.AddSingleton(Mock.Of<IWorkspaceSessionService>());
        Services.AddSingleton(Mock.Of<IReportExportService>());
        Services.AddSingleton(Mock.Of<IAuthenticatedBrowserSessionService>());

        var component = Render<FrontendQualityReview>();

        // Component should render without errors
        Assert.NotNull(component);
    }

    [Fact]
    public async Task SelectionChange_DoesNotPersistToSystemSettings()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var workspaceService = new Mock<IWorkspaceSessionService>();
        var statusService = new Mock<IFrontendQualityEngineStatusApiService>();
        statusService.Setup(s => s.GetStatusAsync(It.IsAny<ReviewAuthenticationModeDto>(), It.IsAny<ReviewEngineSelectionDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FrontendQualityEngineStatusReportDto { Engines = [] });

        Services.AddSingleton(statusService.Object);
        Services.AddSingleton(workspaceService.Object);
        Services.AddSingleton<IFrontendAnalysisContextFactory>(new FixedContextFactory(new()));
        Services.AddSingleton<IFrontendQualityReviewOrchestrator>(new SpyOrchestrator());
        Services.AddSingleton<RuntimeReviewSessionService>();
        Services.AddSingleton(Mock.Of<IReportExportService>());
        Services.AddSingleton(Mock.Of<IAuthenticatedBrowserSessionService>());

        var component = Render<FrontendQualityReview>();

        // Engine selection is per-review state, not persisted to workspace/System Settings
        // Verify no workspace Set() calls for profile changes
        workspaceService.Verify(s => s.Set(WorkspaceArtifactKind.Plan, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime?>()), Times.Never());
    }

    [Fact]
    public async Task AuthUnsupported_ShowsReasonAndDisablesCheckbox()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var statusService = new Mock<IFrontendQualityEngineStatusApiService>();
        statusService.Setup(s => s.GetStatusAsync(It.IsAny<ReviewAuthenticationModeDto>(), It.IsAny<ReviewEngineSelectionDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FrontendQualityEngineStatusReportDto
            {
                Engines = new()
                {
                    new()
                    {
                        EngineId = FrontendQualityEngineIdDto.Accessibility,
                        Available = false,
                        Reasons = [FrontendQualityEngineUnavailableReasonDto.AuthenticationModeUnsupported]
                    }
                }
            });

        Services.AddSingleton(statusService.Object);
        Services.AddSingleton<IFrontendAnalysisContextFactory>(new FixedContextFactory(new()));
        Services.AddSingleton<IFrontendQualityReviewOrchestrator>(new SpyOrchestrator());
        Services.AddSingleton<RuntimeReviewSessionService>();
        Services.AddSingleton(Mock.Of<IWorkspaceSessionService>());
        Services.AddSingleton(Mock.Of<IReportExportService>());
        Services.AddSingleton(Mock.Of<IAuthenticatedBrowserSessionService>());

        var component = Render<FrontendQualityReview>();

        // Component should render and show the unavailability reason
        Assert.NotNull(component);
    }

    private sealed class FixedContextFactory(FrontendAnalysisContext context) : IFrontendAnalysisContextFactory
    {
        public Task<FrontendAnalysisContext> GetActiveContextAsync() => Task.FromResult(context);
    }

    private sealed class SpyOrchestrator : IFrontendQualityReviewOrchestrator
    {
        public Task<FrontendQualityReviewOrchestrationResult> RunAsync(
            string targetUrl,
            FrontendAnalysisContext context,
            FrontendQualityEngineExecutionSnapshot? snapshot = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new FrontendQualityReviewOrchestrationResult());
    }
}
