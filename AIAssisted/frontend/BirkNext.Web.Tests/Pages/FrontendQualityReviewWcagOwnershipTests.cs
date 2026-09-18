using BirkNext.Web.Components;
using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// Frontend Quality Review interprets the browser evidence Browser Discovery presents. It is therefore the
/// only place that selects a WCAG assessment profile, renders conformance coverage and records manual review.
/// </summary>
public sealed class FrontendQualityReviewWcagOwnershipTests : BunitContext
{
    private readonly SpyOrchestrator _orchestrator = new();

    public FrontendQualityReviewWcagOwnershipTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<IEndpointDiscoveryService, EndpointDiscoveryService>();
        Services.AddSingleton<IFrontendAnalysisContextFactory>(new FixedContextFactory(new()
        {
            TargetUrl = "https://application.example.test",
            ActiveProfile = new FrontendAnalysisProfile { Id = "dev", Name = "Dev", TargetUrl = "https://application.example.test" }
        }));
        Services.AddSingleton<IFrontendQualityReviewOrchestrator>(_orchestrator);
        Services.AddSingleton<RuntimeReviewSessionService>();
        Services.AddSingleton(Mock.Of<IWorkspaceSessionService>());
        Services.AddSingleton(Mock.Of<IReportExportService>());
        Services.AddSingleton(Mock.Of<IAuthenticatedBrowserSessionService>());
        Services.AddSingleton(Mock.Of<IFrontendQualityEngineStatusApiService>());
    }

    private async Task<IRenderedComponent<FrontendQualityReview>> RunReview()
    {
        var page = Render<FrontendQualityReview>();
        var run = page.FindAll("button").Single(b => b.TextContent.Contains("Run Frontend Quality Review", StringComparison.Ordinal));
        await page.InvokeAsync(() => run.Click());
        return page;
    }

    [Fact]
    public async Task ReviewResultsOwnTheWcagProfileSelectorAndTheRunsOwnCoverage()
    {
        var page = await RunReview();

        page.FindComponents<WcagWorkspace>().Should().ContainSingle("the assessment surface lives in the review");
        page.FindAll("[data-testid=wcag-profile]").Should().ContainSingle();
        // The displayed coverage is the assessment this review run produced, not a live re-assessment.
        page.Find("[data-testid=wcag-assessment-title]").TextContent.Should().Contain("Norwegian");
    }

    [Fact]
    public async Task ReviewRendersConformanceWordingThatBrowserDiscoveryMustNotDuplicate()
    {
        var page = await RunReview();

        page.Markup.Should().Contain("Assessment profile");
        page.FindComponents<WcagCoverage>().Should().ContainSingle();
    }

    private sealed class FixedContextFactory(FrontendAnalysisContext context) : IFrontendAnalysisContextFactory
    {
        public Task<FrontendAnalysisContext> GetActiveContextAsync() => Task.FromResult(context);
    }

    private sealed class SpyOrchestrator : IFrontendQualityReviewOrchestrator
    {
        public Task<FrontendQualityReviewOrchestrationResult> RunAsync(
            string targetUrl, FrontendAnalysisContext context,
            FrontendQualityEngineExecutionSnapshot? snapshot = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new FrontendQualityReviewOrchestrationResult(
                QualityReport: new FrontendQualityReviewReport
                {
                    TargetUrl = targetUrl,
                    Completeness = AssessmentCompleteness.Full,
                    Wcag = WcagAssessmentEngine.Evaluate(new EndpointDiscoverySnapshot
                    {
                        Pages = [new() { PageOrigin = "https://application.example.test", PagePath = "/dashboard" }]
                    })
                }));
    }
}
