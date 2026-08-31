using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Pages;

public sealed class FrontendQualityReviewProductionOrchestrationTests : BunitContext
{
    [Fact]
    public void FrontendQualityReview_ProductionEngineStatusRegistration_ResolvesAndRenders()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddFrontendQualityEngineStatusApi(new Uri("http://127.0.0.1:1/"));
        Services.AddSingleton<IFrontendAnalysisContextFactory>(new FixedContextFactory(new()));
        Services.AddSingleton<IFrontendQualityReviewOrchestrator>(new SpyOrchestrator());
        Services.AddSingleton<RuntimeReviewSessionService>();
        Services.AddSingleton(Mock.Of<IWorkspaceSessionService>());
        Services.AddSingleton(Mock.Of<IReportExportService>());
        Services.AddSingleton(Mock.Of<IAuthenticatedBrowserSessionService>());

        Services.GetRequiredService<IFrontendQualityEngineStatusApiService>()
            .Should().BeOfType<FrontendQualityEngineStatusApiService>();

        var component = Render<FrontendQualityReview>();

        component.Markup.Should().Contain("Frontend Quality Review");
    }

    [Fact]
    public async Task FrontendQualityReview_RunReview_InvokesProductionOrchestrator()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var context = new FrontendAnalysisContext { TargetUrl = "https://example.com" };
        var orchestrator = new SpyOrchestrator();
        Services.AddSingleton<IFrontendAnalysisContextFactory>(new FixedContextFactory(context));
        Services.AddSingleton<IFrontendQualityReviewOrchestrator>(orchestrator);
        Services.AddSingleton<RuntimeReviewSessionService>();
        Services.AddSingleton(Mock.Of<IWorkspaceSessionService>());
        Services.AddSingleton(Mock.Of<IReportExportService>());
        Services.AddSingleton(Mock.Of<IAuthenticatedBrowserSessionService>());
        Services.AddSingleton(Mock.Of<IFrontendQualityEngineStatusApiService>());

        var component = Render<FrontendQualityReview>();
        var runButton = component.FindAll("button")
            .Single(button => button.TextContent.Contains("Run Frontend Quality Review", StringComparison.Ordinal));

        await component.InvokeAsync(() => runButton.Click());

        Assert.Equal(1, orchestrator.CallCount);
    }

    private sealed class FixedContextFactory(FrontendAnalysisContext context) : IFrontendAnalysisContextFactory
    {
        public Task<FrontendAnalysisContext> GetActiveContextAsync() => Task.FromResult(context);
    }

    private sealed class SpyOrchestrator : IFrontendQualityReviewOrchestrator
    {
        public int CallCount { get; private set; }

        public Task<FrontendQualityReviewOrchestrationResult> RunAsync(
            string targetUrl,
            FrontendAnalysisContext context,
            FrontendQualityEngineExecutionSnapshot? snapshot = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new FrontendQualityReviewOrchestrationResult(
                QualityReport: new FrontendQualityReviewReport
                {
                    TargetUrl = targetUrl,
                    Completeness = AssessmentCompleteness.Full
                }));
        }
    }
}
