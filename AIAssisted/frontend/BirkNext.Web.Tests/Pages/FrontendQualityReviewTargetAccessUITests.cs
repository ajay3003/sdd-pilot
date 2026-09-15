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
/// The Frontend Quality Review landing view shows the selected Target Environment's access model (method, authenticated context,
/// browser DOM, manual verification vs automated access) and only offers the browser sign-in where an engine can use it.
/// </summary>
public sealed class FrontendQualityReviewTargetAccessUITests : BunitContext
{
    private static FrontendAnalysisContext Context(AuthenticatedTestingMethod method, bool requiresAuth = true)
    {
        var profile = new FrontendAnalysisProfile { Id = "dev", Name = "M2LB DEV", EnvironmentType = FrontendEnvironmentType.Development, TargetUrl = "https://m2lbdev.example.test/" };
        profile.Authentication.RequiresAuthentication = requiresAuth;
        profile.Authentication.AuthenticationType = requiresAuth ? FrontendAuthenticationType.MicrosoftEntraId : FrontendAuthenticationType.None;
        profile.Authentication.AuthenticatedTestingMethod = method;
        return new FrontendAnalysisContext { ActiveProfile = profile, TargetUrl = profile.TargetUrl!, RequiresAuthentication = requiresAuth, AuthenticationType = profile.Authentication.AuthenticationType };
    }

    private void Register(FrontendAnalysisContext context, FrontendQualityTargetAccessContext access)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var status = new Mock<IFrontendQualityEngineStatusApiService>();
        status.Setup(s => s.GetStatusAsync(It.IsAny<ReviewAuthenticationModeDto>(), It.IsAny<ReviewEngineSelectionDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FrontendQualityEngineStatusReportDto { Engines = [] });
        var resolver = new Mock<IFrontendQualityTargetAccessResolver>();
        resolver.Setup(r => r.ResolveAsync(It.IsAny<FrontendAnalysisContext>(), It.IsAny<CancellationToken>())).ReturnsAsync(access);
        var factory = new Mock<IFrontendAnalysisContextFactory>();
        factory.Setup(f => f.GetActiveContextAsync()).ReturnsAsync(context);
        Services.AddSingleton(status.Object);
        Services.AddSingleton(resolver.Object);
        Services.AddSingleton(factory.Object);
        Services.AddSingleton(Mock.Of<IFrontendQualityReviewOrchestrator>());
        Services.AddSingleton<RuntimeReviewSessionService>();
        Services.AddSingleton(Mock.Of<IWorkspaceSessionService>());
        Services.AddSingleton(Mock.Of<IReportExportService>());
        Services.AddSingleton(Mock.Of<IAuthenticatedBrowserSessionService>());
    }

    [Fact]
    public void ProxyEnvironment_ShowsAccessContextAndProxyNote_WithoutBrowserSignIn()
    {
        var context = Context(AuthenticatedTestingMethod.LocalHttpsProxy);
        var access = FrontendQualityTargetAccess.Build(context, new AuthenticatedReviewCapabilities
        {
            Method = AuthenticatedTestingMethod.LocalHttpsProxy, ContextStatus = AuthenticatedApiContextStatus.Available, AuthenticatedApi = true, Reason = "Authenticated via Local HTTPS Proxy (memory only).",
        }, false, null, LocalHttpsProxyState.Ready);
        Register(context, access);

        var page = Render<FrontendQualityReview>();

        page.WaitForAssertion(() => page.Find("[data-testid='fqr-target-access']"));
        page.Find("[data-testid='fqr-access-target']").TextContent.Should().Contain("M2LB DEV");
        page.Find("[data-testid='fqr-access-url']").TextContent.Should().Contain("https://m2lbdev.example.test/");
        page.Find("[data-testid='fqr-access-auth']").TextContent.Should().Contain("MicrosoftEntraId");
        page.Find("[data-testid='fqr-access-method']").TextContent.Should().Be(AuthenticatedTestingMethodLabels.ProxyOption);
        page.Find("[data-testid='fqr-access-context']").TextContent.Should().Be("Available — memory only");
        page.Find("[data-testid='fqr-access-dom']").TextContent.Should().Be("Not available with Local HTTPS Proxy");
        page.Find("[data-testid='fqr-access-proxy']").TextContent.Should().Be("Ready");
        page.Find("[data-testid='fqr-access-automated']").TextContent.Should().Contain("no DOM");
        page.FindAll("[data-testid='fqr-proxy-method-note']").Should().ContainSingle();
        page.Markup.Should().NotContain("Sign in for review");
        page.FindAll("[data-testid='fqr-action-endpoint-discovery']").Should().ContainSingle();
    }

    [Fact]
    public void ProxyEnvironmentWithoutContext_ShowsNotAvailableContext()
    {
        var context = Context(AuthenticatedTestingMethod.LocalHttpsProxy);
        var access = FrontendQualityTargetAccess.Build(context, new AuthenticatedReviewCapabilities
        {
            Method = AuthenticatedTestingMethod.LocalHttpsProxy, ContextStatus = AuthenticatedApiContextStatus.WaitingForAuthenticatedTraffic, Reason = "Start the local proxy, sign in to the target application in the proxy-configured browser, and perform an authenticated action.",
        }, false, null, LocalHttpsProxyState.Stopped);
        Register(context, access);

        var page = Render<FrontendQualityReview>();

        page.WaitForAssertion(() => page.Find("[data-testid='fqr-target-access']"));
        page.Find("[data-testid='fqr-access-context']").TextContent.Should().StartWith("Not available");
        page.Find("[data-testid='fqr-access-mode']").TextContent.Should().Contain("context not available");
        page.Find("[data-testid='fqr-access-reason']").TextContent.Should().Contain("Start the local proxy");
    }

    [Fact]
    public void CdpEnvironment_KeepsBrowserSignIn_AndShowsEnterpriseBlock()
    {
        var context = Context(AuthenticatedTestingMethod.ManagedEdgeCdp);
        var access = FrontendQualityTargetAccess.Build(context, null, false, BirkNext.ManagedEdge.ManagedEdgeState.TargetTabNotInspectable, null);
        Register(context, access);

        var page = Render<FrontendQualityReview>();

        page.WaitForAssertion(() => page.Find("[data-testid='fqr-target-access']"));
        page.Find("[data-testid='fqr-access-cdp']").TextContent.Should().Be("Blocked by enterprise browser protection");
        page.Find("[data-testid='fqr-access-mode']").TextContent.Should().Be("Blocked by enterprise browser protection");
        page.Markup.Should().Contain("Sign in for review");
        page.FindAll("[data-testid='fqr-proxy-method-note']").Should().BeEmpty();
    }

    [Fact]
    public void ManualOnlyEnvironment_DistinguishesManualVerificationFromAutomatedAccess()
    {
        var context = Context(AuthenticatedTestingMethod.ManualOnly);
        context.ActiveProfile.ManualVerification = ManualAuthenticationVerificationEvidence.Record(context.ActiveProfile, ManualAuthenticationVerificationStatus.Passed);
        var access = FrontendQualityTargetAccess.Build(context, null, false, null, null);
        Register(context, access);

        var page = Render<FrontendQualityReview>();

        page.WaitForAssertion(() => page.Find("[data-testid='fqr-target-access']"));
        page.Find("[data-testid='fqr-access-manual']").TextContent.Should().Be("Passed");
        page.Find("[data-testid='fqr-access-automated']").TextContent.Should().Be("Unavailable — manual verification only");
        page.FindAll("[data-testid='fqr-access-manual-note']").Should().ContainSingle();
        page.FindAll("[data-testid='fqr-manual-only-note']").Should().ContainSingle();
        page.Markup.Should().NotContain("Sign in for review");
    }

    [Fact]
    public void PublicEnvironment_ShowsPublicDirectAccessWithoutAuthenticationSections()
    {
        var context = Context(AuthenticatedTestingMethod.ManagedEdgeCdp, requiresAuth: false);
        Register(context, FrontendQualityTargetAccess.FromContext(context));

        var page = Render<FrontendQualityReview>();

        page.WaitForAssertion(() => page.Find("[data-testid='fqr-target-access']"));
        page.Find("[data-testid='fqr-access-mode']").TextContent.Should().Be("Public (direct)");
        page.Find("[data-testid='fqr-access-auth']").TextContent.Should().Be("Not required");
        page.FindAll("[data-testid='fqr-access-reason']").Should().BeEmpty();
        page.Markup.Should().NotContain("Sign in for review");
    }

    [Fact]
    public void ReportView_RendersAccessAwareEngineTable()
    {
        var policy = new FrontendQualityEngineRequirementSettings().ToPolicy();
        var security = FrontendQualityEngineOutcomeNormalizer.StaticSecurity("https://m2lbdev.example.test/", true, policy,
            new WasmSecurityReviewReport { ScannedAt = DateTime.UtcNow, Assets = [new WasmDiscoveredAsset { Url = "x", AssetType = "HTML", Status = "Unauthorized" }] }, null)
            with { AccessKind = FrontendQualityEngineAccessKind.PublicHttp, AccessLabel = "Public HTTP (frontend shell)", RequiredAction = "Open Authentication", ActionHref = "/admin/system-settings?section=target-environments" };
        var performance = FrontendQualityEngineOutcomeNormalizer.PassivePerformance("https://m2lbdev.example.test/", true, policy,
            new WasmPerformanceReviewReport { ReviewedAt = DateTime.UtcNow, Assets = [new DiscoveredAsset { Url = "x", Type = AssetType.Index, StatusCode = 200 }] }, null)
            with { AccessKind = FrontendQualityEngineAccessKind.PublicHttp, AccessLabel = "Public HTTP (frontend shell)" };
        var outcomes = new List<FrontendQualityEngineOutcome> { security, performance };
        var report = new FrontendQualityReviewReport
        {
            TargetUrl = "https://m2lbdev.example.test/", GeneratedAt = DateTime.UtcNow, EngineOutcomes = outcomes,
            Coverage = FrontendQualityCoverage.Evaluate(outcomes), ReleaseDisposition = FrontendQualityReleaseDisposition.Blocked,
        };

        var component = Render<FrontendQualityDecisionSupport>(p => p.Add(x => x.Report, report));

        component.Find("[data-testid='fqr-required-assessed']").TextContent.Trim().Should().Be("1 / 2");
        component.Find("[data-testid='fqr-coverage-summary']").TextContent.Should().Contain("1 of 2 required engines completed.").And.Contain("Static Security:").And.Contain("Blocked").And.Contain("HTTP 401");
        var securityRow = component.Find("tr[data-engine-id='StaticSecurity']");
        securityRow.QuerySelector("[data-testid='fqr-assessment']")!.TextContent.Should().Be("Not assessed");
        securityRow.QuerySelector("[data-testid='fqr-access']")!.TextContent.Should().Be("Public HTTP (frontend shell)");
        securityRow.QuerySelector("[data-testid='fqr-state']")!.TextContent.Should().Be("Blocked");
        securityRow.QuerySelector("[data-testid='fqr-outcome-reason']")!.TextContent.Should().Be("Authentication required");
        securityRow.QuerySelector("[data-testid='fqr-reason'] a")!.TextContent.Should().Be("Open Authentication");
        var perfRow = component.Find("tr[data-engine-id='PassivePerformance']");
        perfRow.QuerySelector("[data-testid='fqr-assessment']")!.TextContent.Should().Be("Assessed");
        perfRow.QuerySelector("[data-testid='fqr-state']")!.TextContent.Should().Be("Completed — no findings");
        component.Markup.Should().Contain("Required engine could not assess the target (1 of 2 required engines completed)");
        component.Markup.Should().NotContain("Not ready");
    }
}
