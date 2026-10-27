using BirkNext.LocalHttpsProxy;
using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// The review scope a fresh Frontend Quality Review starts from: capability-aware when nothing was chosen, the person's choice when
/// one was, and never silently widened, narrowed or pre-selected when nothing can run. Selecting a scope starts nothing.
/// </summary>
public sealed class FrontendQualityScopeDefaultUITests : BunitContext
{
    private const string Url = "https://m2lbdev.example.test/";

    private static FrontendAnalysisContext Context(FrontendReviewAccessScope? saved = null, string? url = Url,
        AuthenticatedTestingMethod method = AuthenticatedTestingMethod.LocalHttpsProxy)
    {
        var profile = new FrontendAnalysisProfile { Id = "dev", Name = "M2LB DEV", EnvironmentType = FrontendEnvironmentType.Development, TargetUrl = url };
        profile.Authentication.AuthenticationType = FrontendAuthenticationType.MicrosoftEntraId;
        profile.Authentication.AuthenticatedTestingMethod = method;
        profile.FrontendReviewScope = saved;
        // HTTP engines only: no backend status probe, so the page settles on first render.
        profile.Features.EnableBrowserRuntimeEngine = false; profile.Features.EnableAccessibilityEngine = false; profile.Features.EnableLighthouseEngine = false;
        profile.Features.EnablePassiveSecurityEngine = false; profile.Features.EnableBrowserQualityEngine = false; profile.Features.EnablePerformanceQualityEngine = false;
        return new FrontendAnalysisContext
        {
            ActiveProfile = profile, TargetUrl = url ?? "", FeatureToggles = profile.Features,
            EngineRequirements = profile.EngineRequirements, ReviewEngineSelection = profile.ReviewEngineSelection,
            AuthenticationType = FrontendAuthenticationType.MicrosoftEntraId,
        };
    }

    /// <summary>The reported state: Local HTTPS proxy authenticated API context ready.</summary>
    private static FrontendQualityTargetAccessContext ProxyReady(FrontendAnalysisContext context) =>
        FrontendQualityTargetAccess.Build(context, new AuthenticatedReviewCapabilities
        {
            Method = AuthenticatedTestingMethod.LocalHttpsProxy, ContextStatus = AuthenticatedApiContextStatus.Available, AuthenticatedApi = true,
        }, false, null, LocalHttpsProxyState.Ready);

    private static FrontendQualityTargetAccessContext NotReady(FrontendAnalysisContext context) =>
        FrontendQualityTargetAccess.Build(context, new AuthenticatedReviewCapabilities
        {
            Method = AuthenticatedTestingMethod.LocalHttpsProxy, ContextStatus = AuthenticatedApiContextStatus.WaitingForAuthenticatedTraffic,
        }, false, null, LocalHttpsProxyState.Listening);

    private (Mock<IFrontendQualityReviewOrchestrator> Orchestrator, Mock<IAuthenticatedBrowserSessionService> Sessions) Register(
        FrontendAnalysisContext context, Func<FrontendAnalysisContext, FrontendQualityTargetAccessContext> access)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        JSInterop.SetupModule("./js/frontendReviewScope.js").Mode = JSRuntimeMode.Loose;
        var status = new Mock<IFrontendQualityEngineStatusApiService>();
        status.Setup(s => s.GetStatusAsync(It.IsAny<ReviewAuthenticationModeDto>(), It.IsAny<ReviewEngineSelectionDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FrontendQualityEngineStatusReportDto());
        var factory = new Mock<IFrontendAnalysisContextFactory>();
        factory.Setup(f => f.GetActiveContextAsync()).ReturnsAsync(context);
        // The access resolver answers for whichever scope it is asked about, like the real one.
        var resolver = new Mock<IFrontendQualityTargetAccessResolver>();
        resolver.Setup(r => r.ResolveAsync(It.IsAny<FrontendAnalysisContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((FrontendAnalysisContext c, CancellationToken _) => access(c));
        var orchestrator = new Mock<IFrontendQualityReviewOrchestrator>();
        orchestrator.Setup(o => o.RunAsync(It.IsAny<string>(), It.IsAny<FrontendAnalysisContext>(), It.IsAny<FrontendQualityEngineExecutionSnapshot?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string url, FrontendAnalysisContext _, FrontendQualityEngineExecutionSnapshot? _, CancellationToken _) =>
                new FrontendQualityReviewOrchestrationResult(QualityReport: new FrontendQualityReviewReport { TargetUrl = url, GeneratedAt = DateTime.UtcNow, Completeness = AssessmentCompleteness.Full }));
        var sessions = new Mock<IAuthenticatedBrowserSessionService>(MockBehavior.Loose);
        Services.AddSingleton(resolver.Object);
        Services.AddSingleton(status.Object);
        Services.AddSingleton(factory.Object);
        Services.AddSingleton(orchestrator.Object);
        Services.AddSingleton<RuntimeReviewSessionService>();
        Services.AddSingleton(Mock.Of<IWorkspaceSessionService>());
        Services.AddSingleton(Mock.Of<IReportExportService>());
        Services.AddSingleton(sessions.Object);
        return (orchestrator, sessions);
    }

    private static string Scope(IRenderedComponent<FrontendQualityReview> page) => page.Find("[data-testid=fqr-target-scope]").TextContent.Trim();
    private static string? Checked(IRenderedComponent<FrontendQualityReview> page) =>
        page.FindAll("input[name=fqr-review-scope]").SingleOrDefault(i => i.HasAttribute("checked"))?.GetAttribute("value");

    [Fact]
    public void ReportedState_DefaultsToPublicAndAuthenticated_MarkedRecommended()
    {
        Register(Context(), ProxyReady);
        var page = Render<FrontendQualityReview>();

        page.WaitForAssertion(() => Scope(page).Should().Be("Public + authenticated"));
        page.Find("[data-testid=fqr-target-authenticated-access]").TextContent.Should().Be("Included — testing context ready");
        page.Find("[data-testid=fqr-change-scope]").Click();
        Checked(page).Should().Be(nameof(FrontendReviewAccessScope.PublicAndAuthenticated));
        page.FindAll("[data-testid=fqr-scope-recommended]").Should().ContainSingle()
            .Which.ParentElement!.TextContent.Should().Contain("Public + authenticated");
        page.Find("[data-testid=fqr-scope-recommendation]").TextContent.Should().Be("Recommended because both public and authenticated review access are ready.");
        page.Find("[data-testid=fqr-scope-option-PublicAndAuthenticated] small").TextContent
            .Should().Be("Review both public and authenticated application surfaces for the broadest available coverage.");
        page.Find("dialog dl").TextContent.Should().Contain("Authenticated API context available").And.Contain("Microsoft Entra ID");
    }

    [Fact]
    public void ConfiguredButNotReady_DefaultsToPublicOnly_WithNoRecommendedBadge()
    {
        Register(Context(), NotReady);
        var page = Render<FrontendQualityReview>();

        page.WaitForAssertion(() => Scope(page).Should().Be("Public only"));
        page.Find("[data-testid=fqr-change-scope]").Click();
        Checked(page).Should().Be(nameof(FrontendReviewAccessScope.PublicOnly));
        page.FindAll("[data-testid=fqr-scope-recommended]").Should().BeEmpty("the other valid choices never look wrong");
    }

    /// <summary>An explicit choice stays: reopening the dialog shows it, and a newly ready authenticated path only adds a notice.</summary>
    [Fact]
    public void ExplicitPublicOnly_IsKept_AndAuthenticatedAvailabilityIsOnlyANotice()
    {
        Register(Context(saved: FrontendReviewAccessScope.PublicOnly), ProxyReady);
        var page = Render<FrontendQualityReview>();

        page.WaitForAssertion(() => page.Find("[data-testid=fqr-scope-notice]").TextContent.Should().Be(FrontendQualityReviewScopes.AuthenticatedNowAvailable));
        Scope(page).Should().Be("Public only");
        page.Find("[data-testid=fqr-change-scope]").Click();
        Checked(page).Should().Be(nameof(FrontendReviewAccessScope.PublicOnly), "opening the dialog never re-defaults the scope");
        page.FindAll("dialog button").Single(b => b.TextContent.Trim() == "Cancel").Click();
        page.Find("[data-testid=fqr-change-scope]").Click();
        Checked(page).Should().Be(nameof(FrontendReviewAccessScope.PublicOnly));
    }

    /// <summary>A chosen combined scope whose authenticated path is lost is never claimed as full coverage.</summary>
    [Fact]
    public void ExplicitCombined_LosingAuthenticatedAccess_NeedsReview_NoFullCoverage()
    {
        Register(Context(saved: FrontendReviewAccessScope.PublicAndAuthenticated), NotReady);
        var page = Render<FrontendQualityReview>();

        page.WaitForAssertion(() => page.Find("[data-testid=fqr-scope-notice]").TextContent.Should().Be(FrontendQualityReviewScopes.ScopeNeedsReview));
        Scope(page).Should().Be("Public + authenticated");
        page.Find("[data-testid=fqr-target-authenticated-access]").TextContent.Should().Be("Requested but unavailable");
        page.Find("[data-testid=fqr-target-status]").TextContent.Should().Be("Ready with limitations");
    }

    [Fact]
    public void NothingRunnable_PreSelectsNothing_AndSaysSo()
    {
        Register(Context(url: null), NotReady);
        var page = Render<FrontendQualityReview>();

        page.WaitForAssertion(() => Scope(page).Should().Be("No runnable access scope available"));
        page.Find("[data-testid=fqr-change-scope]").Click();
        Checked(page).Should().BeNull("an unusable scope is never silently selected");
        page.Find("[data-testid=fqr-scope-recommendation]").TextContent.Should().Be(FrontendQualityReviewScopes.NoRunnableScope);
        page.FindAll("dialog button").Single(b => b.TextContent.Trim() == "Apply").HasAttribute("disabled").Should().BeTrue();
    }

    /// <summary>The scope reaches the review planner, re-checked at run time; and choosing a scope starts no sign-in.</summary>
    [Fact]
    public async Task Run_SendsTheRecommendedScopeToTheOrchestrator_WithoutStartingAuthentication()
    {
        var (orchestrator, sessions) = Register(Context(), ProxyReady);
        var page = Render<FrontendQualityReview>();
        page.WaitForAssertion(() => Scope(page).Should().Be("Public + authenticated"));

        await page.InvokeAsync(() => page.Find("[data-testid=fqr-run]").Click());

        orchestrator.Verify(o => o.RunAsync(Url,
            It.Is<FrontendAnalysisContext>(c => c.ReviewScope == FrontendReviewAccessScope.PublicAndAuthenticated && c.RequiresAuthentication),
            It.IsAny<FrontendQualityEngineExecutionSnapshot?>(), It.IsAny<CancellationToken>()), Times.Once);
        sessions.Verify(s => s.BeginAuthenticationAsync(It.IsAny<FrontendAnalysisContext>()), Times.Never);
    }
}
