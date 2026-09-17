using AngleSharp.Dom;
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
/// Redesigned Frontend Quality Review landing view: a tester sees target, readiness, the six quality dimensions, coverage and
/// review capabilities first; every technical detail is behind a collapsed, accessible disclosure; unavailable optional
/// capabilities and deployment-policy limitations are never presented as application failures; results appear only after a run.
/// </summary>
public sealed class FrontendQualityReviewLandingUITests : BunitContext
{
    private const string Url = "https://m2lbdev.example.test/";

    private static FrontendAnalysisContext Context(
        Action<FrontendAnalysisFeatureToggles>? toggles = null,
        bool requiresAuth = false,
        AuthenticatedTestingMethod method = AuthenticatedTestingMethod.ManagedEdgeCdp,
        string? url = Url)
    {
        var profile = new FrontendAnalysisProfile { Id = "dev", Name = "M2LB DEV", EnvironmentType = FrontendEnvironmentType.Development, TargetUrl = url };
        profile.Authentication.RequiresAuthentication = requiresAuth;
        profile.Authentication.AuthenticationType = requiresAuth ? FrontendAuthenticationType.MicrosoftEntraId : FrontendAuthenticationType.None;
        profile.Authentication.AuthenticatedTestingMethod = method;
        toggles?.Invoke(profile.Features);
        return new FrontendAnalysisContext
        {
            ActiveProfile = profile, TargetUrl = url ?? "", FeatureToggles = profile.Features,
            EngineRequirements = profile.EngineRequirements, ReviewEngineSelection = profile.ReviewEngineSelection,
            RequiresAuthentication = requiresAuth, AuthenticationType = profile.Authentication.AuthenticationType,
        };
    }

    /// <summary>Only the two HTTP engines enabled: no backend status probe, Run enabled immediately.</summary>
    private static FrontendAnalysisContext HttpOnlyContext(bool requiresAuth = false, AuthenticatedTestingMethod method = AuthenticatedTestingMethod.ManagedEdgeCdp, string? url = Url) =>
        Context(t =>
        {
            t.EnableBrowserRuntimeEngine = false; t.EnableAccessibilityEngine = false; t.EnableLighthouseEngine = false;
            t.EnablePassiveSecurityEngine = false; t.EnableBrowserQualityEngine = false; t.EnablePerformanceQualityEngine = false;
        }, requiresAuth, method, url);

    private static FrontendQualityEngineStatusDto Engine(FrontendQualityEngineIdDto id, bool layer1 = true, bool layer2 = true, bool? ready = true, string? reason = null, bool authSupported = true) => new()
    {
        EngineId = id, DisplayName = id.ToString(), Layer1Allowed = layer1, Layer2Enabled = layer2, AuthModeSupported = authSupported,
        Available = layer1 && layer2 && ready != false,
        Layer3Readiness = ready is null ? null : new FrontendQualityEngineReadinessDto { EngineId = id, IsAvailable = ready.Value, StatusReason = reason },
    };

    private Mock<IFrontendQualityReviewOrchestrator> Register(
        FrontendAnalysisContext context,
        IReadOnlyList<FrontendQualityEngineStatusDto>? engines = null,
        FrontendQualityTargetAccessContext? access = null,
        Mock<IReportExportService>? export = null)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var status = new Mock<IFrontendQualityEngineStatusApiService>();
        status.Setup(s => s.GetStatusAsync(It.IsAny<ReviewAuthenticationModeDto>(), It.IsAny<ReviewEngineSelectionDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FrontendQualityEngineStatusReportDto { Engines = (engines ?? []).ToList() });
        var factory = new Mock<IFrontendAnalysisContextFactory>();
        factory.Setup(f => f.GetActiveContextAsync()).ReturnsAsync(context);
        var orchestrator = new Mock<IFrontendQualityReviewOrchestrator>();
        orchestrator.Setup(o => o.RunAsync(It.IsAny<string>(), It.IsAny<FrontendAnalysisContext>(), It.IsAny<FrontendQualityEngineExecutionSnapshot?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string url, FrontendAnalysisContext _, FrontendQualityEngineExecutionSnapshot? _, CancellationToken _) =>
                new FrontendQualityReviewOrchestrationResult(QualityReport: new FrontendQualityReviewReport { TargetUrl = url, GeneratedAt = DateTime.UtcNow, Completeness = AssessmentCompleteness.Full }));
        if (access is not null)
        {
            var resolver = new Mock<IFrontendQualityTargetAccessResolver>();
            resolver.Setup(r => r.ResolveAsync(It.IsAny<FrontendAnalysisContext>(), It.IsAny<CancellationToken>())).ReturnsAsync(access);
            Services.AddSingleton(resolver.Object);
        }
        Services.AddSingleton(status.Object);
        Services.AddSingleton(factory.Object);
        Services.AddSingleton(orchestrator.Object);
        Services.AddSingleton<RuntimeReviewSessionService>();
        Services.AddSingleton(Mock.Of<IWorkspaceSessionService>());
        Services.AddSingleton((export ?? new Mock<IReportExportService>()).Object);
        Services.AddSingleton(Mock.Of<IAuthenticatedBrowserSessionService>());
        return orchestrator;
    }

    private static IElement RunButton(IRenderedComponent<FrontendQualityReview> page) => page.Find("[data-testid=fqr-run]");

    private static IElement Capability(IRenderedComponent<FrontendQualityReview> page, FrontendQualityEngineId id) =>
        page.Find($"[data-testid=fqr-capability][data-engine-id='{id}']");

    // 1. Ready target renders a concise "Ready to review" state.
    [Fact]
    public void ReadyTarget_RendersConciseReadyToReview()
    {
        Register(HttpOnlyContext());

        var page = Render<FrontendQualityReview>();

        var readiness = page.Find("[data-testid=fqr-readiness]");
        readiness.GetAttribute("data-readiness").Should().Be("Ready");
        readiness.GetAttribute("role").Should().Be("status");
        page.Find("[data-testid=fqr-readiness-title]").TextContent.Should().Be("Ready to review");
        page.Find("[data-testid=fqr-readiness-message]").TextContent.Should().Contain("valid");
        page.Find("[data-testid=fqr-target-environment]").TextContent.Should().Contain("M2LB DEV").And.Contain("Dev");
        page.Find("[data-testid=fqr-target-url]").TextContent.Should().Be(Url);
        page.Find("[data-testid=fqr-target-authentication]").TextContent.Should().Be("Not required");
        page.Find("[data-testid=fqr-target-status]").TextContent.Should().Be("Ready");
        page.Find("[data-testid=fqr-target-review-status]").TextContent.Should().Be("Ready to review");
        RunButton(page).HasAttribute("disabled").Should().BeFalse();
        RunButton(page).TextContent.Trim().Should().Be("Run Frontend Quality Review");
        page.FindAll("[data-testid=fqr-run-disabled-reason]").Should().BeEmpty();
        page.Find("h1").TextContent.Should().Be("Frontend Quality Review");
        page.Find(".page-lead").TextContent.Should().StartWith("Review a deployed frontend");
        // Old dense presentation is gone from the first screen.
        page.Markup.Should().NotContain("Audit Target").And.NotContain("Checks Include").And.NotContain("OWASP");
    }

    // 2. Blocking configuration renders the blocking reason (and Run is disabled with a human-readable reason).
    [Fact]
    public void MissingTargetUrl_RendersBlockingReason_AndDisabledRunReason()
    {
        Register(HttpOnlyContext(url: ""));

        var page = Render<FrontendQualityReview>();

        var readiness = page.Find("[data-testid=fqr-readiness]");
        readiness.GetAttribute("data-readiness").Should().Be("Blocked");
        readiness.GetAttribute("role").Should().Be("alert");
        page.Find("[data-testid=fqr-readiness-title]").TextContent.Should().Be("Review cannot start");
        page.Find("[data-testid=fqr-readiness-details]").TextContent.Should().Contain("Frontend target URL");
        page.Find("[data-testid=fqr-readiness-action]").TextContent.Should().Be("Configure Target Environment");
        page.Find("[data-testid=fqr-target-status]").TextContent.Should().Be("Frontend URL missing");
        RunButton(page).HasAttribute("disabled").Should().BeTrue();
        page.Find("[data-testid=fqr-run-disabled-reason]").TextContent.Should().Contain("no Frontend URL");
        page.Find("[data-testid=fqr-target-review-status] .fqr-pill").ClassList.Should().NotContain("fqr-pill-ready", "a blocked review must not look green");
    }

    [Fact]
    public void NoActiveTarget_RendersBlockingReason_WithTargetEnvironmentsAction()
    {
        Register(new FrontendAnalysisContext { ActiveTargetError = "No active Target Environment" });

        var page = Render<FrontendQualityReview>();

        page.Find("[data-testid=fqr-readiness]").GetAttribute("data-readiness").Should().Be("Blocked");
        page.Find("[data-testid=fqr-readiness-title]").TextContent.Should().Be("No active Target Environment");
        page.Find("[data-testid=fqr-readiness-action]").TextContent.Should().Be("Open Target Environments");
        RunButton(page).HasAttribute("disabled").Should().BeTrue();
        page.FindAll("[data-testid=fqr-coverage]").Should().BeEmpty();
        page.FindAll("[data-testid=fqr-authenticated-review]").Should().BeEmpty();
    }

    // 3. Six quality-dimension cards render.
    [Fact]
    public void SixQualityDimensionCardsRender_WithPurposeAndState()
    {
        Register(HttpOnlyContext());

        var page = Render<FrontendQualityReview>();

        var cards = page.FindAll("[data-testid=fqr-dimension]");
        cards.Should().HaveCount(6);
        cards.Select(c => c.QuerySelector("h3")!.TextContent).Should().Equal("Performance", "Security", "Accessibility", "Standards Compliance", "Blazor / WASM", "QA Readiness");
        cards.Should().OnlyContain(c => c.QuerySelector(".fqr-dimension-purpose")!.TextContent.Length > 0);
        cards.Should().OnlyContain(c => c.QuerySelector("[data-testid=fqr-dimension-state]")!.TextContent.Length > 0, "state must be a text label, not colour only");
        page.Find("[data-testid=fqr-dimension][data-category='Security'] [data-testid=fqr-dimension-state]").TextContent.Should().Be("Enabled");
        page.Find("[data-testid=fqr-dimension][data-category='Accessibility'] [data-testid=fqr-dimension-state]").TextContent.Should().Be("Not enabled");
        page.Find("[data-testid=fqr-dimension][data-category='Readiness'] [data-testid=fqr-dimension-state]").TextContent.Should().Be("Available");
        page.Find("h2#fqr-dimensions-heading").TextContent.Should().Be("What will be analysed?");
    }

    [Fact]
    public void AccessibilityCard_CarriesManualTestingLimitation_WhenEnabled()
    {
        Register(Context(), [Engine(FrontendQualityEngineIdDto.Accessibility), Engine(FrontendQualityEngineIdDto.Lighthouse), Engine(FrontendQualityEngineIdDto.PassiveSecurity)]);

        var page = Render<FrontendQualityReview>();

        page.WaitForAssertion(() => RunButton(page).HasAttribute("disabled").Should().BeFalse());
        var card = page.Find("[data-testid=fqr-dimension][data-category='Accessibility']");
        card.QuerySelector("[data-testid=fqr-dimension-state]")!.TextContent.Should().Be("Enabled");
        card.QuerySelector("[data-testid=fqr-dimension-limitation]")!.TextContent.Should().Contain("Manual accessibility testing may still be required.");
    }

    // 4. Checks are collapsed by default.
    [Fact]
    public void Checks_CollapsedByDefault_WithCount()
    {
        Register(HttpOnlyContext());

        var page = Render<FrontendQualityReview>();

        page.Find("h2#fqr-checks-heading").TextContent.Should().Be($"Checks included ({FrontendQualityLandingPresentation.CheckCount})");
        var toggle = page.Find("[data-testid=fqr-checks-disclosure-toggle]");
        toggle.GetAttribute("aria-expanded").Should().Be("false");
        toggle.TextContent.Should().Contain("View checks");
        page.Find("[data-testid=fqr-checks-disclosure-body]").HasAttribute("hidden").Should().BeTrue();
    }

    // 5. Expanded checks are grouped correctly.
    [Fact]
    public void Checks_Expanded_AreGroupedLogically()
    {
        Register(HttpOnlyContext());

        var page = Render<FrontendQualityReview>();
        page.Find("[data-testid=fqr-checks-disclosure-toggle]").Click();

        page.Find("[data-testid=fqr-checks-disclosure-toggle]").GetAttribute("aria-expanded").Should().Be("true");
        page.Find("[data-testid=fqr-checks-disclosure-body]").HasAttribute("hidden").Should().BeFalse();
        var groups = page.FindAll("[data-testid=fqr-check-group]");
        groups.Select(g => g.GetAttribute("data-group")).Should().Equal("Security", "Performance", "Accessibility", "Blazor / WASM", "Standards / QA readiness");
        page.Find("[data-testid=fqr-check-group][data-group='Security']").TextContent.Should().Contain("Security headers").And.Contain("CORS").And.Contain("Content Security Policy");
        page.Find("[data-testid=fqr-check-group][data-group='Performance']").TextContent.Should().Contain("Bundle size").And.Contain("Compression").And.Contain("Cache headers").And.Contain("Lazy loading");
        page.Find("[data-testid=fqr-check-group][data-group='Accessibility']").TextContent.Should().Contain("axe-core").And.Contain("Manual accessibility testing remains separate");
        page.Find("[data-testid=fqr-check-group][data-group='Blazor / WASM']").TextContent.Should().Contain("boot resources").And.Contain("Service worker");
        page.Markup.Should().NotContain("OWASP");
        // Not assessed items are informational, not failures.
        var notAssessed = page.FindAll("[data-testid=fqr-not-assessed-item]");
        notAssessed.Select(i => i.QuerySelector("strong")!.TextContent).Should().Equal("Core Web Vitals", "Testability", "Observability");
        notAssessed[0].TextContent.Should().Contain("Requires production field data");
        page.Find("[data-testid=fqr-not-assessed]").QuerySelectorAll(".fqr-pill-attention, .fqr-pill-warning").Should().BeEmpty();
    }

    // 6. Coverage summary renders public/authenticated/browser states correctly.
    [Fact]
    public void Coverage_PublicTarget_RendersStates()
    {
        Register(HttpOnlyContext());

        var page = Render<FrontendQualityReview>();

        page.WaitForAssertion(() => page.Find("[data-testid=fqr-coverage]"));
        Row(page, "Public frontend").Should().Be("Available");
        Row(page, "Authenticated application").Should().Be("Not required");
        Row(page, "Browser-rendered DOM").Should().Be("Available");
        page.Find("[data-testid=fqr-coverage-row][data-coverage='Browser-rendered DOM'] .fqr-coverage-detail").TextContent.Should().Be("Available for public pages.");
        Row(page, "Authenticated API traffic").Should().Be("Not required");
        Row(page, "Automatic engines").Should().Be("Available");
    }

    [Fact]
    public void Coverage_ProtectedTargetWithoutSession_RendersNotAvailableWithNextStep()
    {
        Register(HttpOnlyContext(requiresAuth: true));

        var page = Render<FrontendQualityReview>();

        page.WaitForAssertion(() => page.Find("[data-testid=fqr-coverage]"));
        Row(page, "Public frontend").Should().Be("Available");
        Row(page, "Authenticated application").Should().Be("Not available");
        Row(page, "Browser-rendered DOM").Should().Be("Not available");
        page.Find("[data-testid=fqr-coverage-row][data-coverage='Browser-rendered DOM'] .fqr-coverage-detail").TextContent.Should().Contain("Sign in for review");
        Row(page, "Automatic engines").Should().Be("Public frontend only");
    }

    [Fact]
    public void Coverage_ProxyWithAuthenticatedContext_RendersApiAvailableAndDomNotAvailable()
    {
        var context = HttpOnlyContext(requiresAuth: true, method: AuthenticatedTestingMethod.LocalHttpsProxy);
        var access = FrontendQualityTargetAccess.Build(context, new AuthenticatedReviewCapabilities
        {
            Method = AuthenticatedTestingMethod.LocalHttpsProxy, ContextStatus = AuthenticatedApiContextStatus.Available, AuthenticatedApi = true, AuthenticatedRest = true,
        }, false, null, LocalHttpsProxyState.Ready);
        Register(context, access: access);

        var page = Render<FrontendQualityReview>();

        page.WaitForAssertion(() => page.Find("[data-testid=fqr-coverage]"));
        Row(page, "Authenticated application").Should().Be("Available");
        Row(page, "Browser-rendered DOM").Should().Be("Not available");
        Row(page, "Authenticated API traffic").Should().Be("Available");
        Row(page, "Automatic engines").Should().Be("Available");
    }

    private static string Row(IRenderedComponent<FrontendQualityReview> page, string label) =>
        page.Find($"[data-testid=fqr-coverage-row][data-coverage='{label}'] [data-testid=fqr-coverage-state]").TextContent.Replace("✓", "").Replace("○", "").Trim();

    // 7. Technical coverage details are collapsed by default (and still carry the exact values).
    [Fact]
    public void TechnicalCoverageDetails_CollapsedByDefault_StillContainExactAccessValues()
    {
        Register(HttpOnlyContext());

        var page = Render<FrontendQualityReview>();

        page.WaitForAssertion(() => page.Find("[data-testid=fqr-coverage-technical-toggle]"));
        var toggle = page.Find("[data-testid=fqr-coverage-technical-toggle]");
        toggle.GetAttribute("aria-expanded").Should().Be("false");
        toggle.TextContent.Should().Contain("View technical coverage details");
        var body = page.Find("[data-testid=fqr-coverage-technical-body]");
        body.HasAttribute("hidden").Should().BeTrue();
        body.QuerySelector("[data-testid=fqr-target-access]").Should().NotBeNull("the exact access panel is moved, not removed");
        body.QuerySelector("[data-testid=fqr-access-mode]")!.TextContent.Should().Be("Public (direct)");

        toggle.Click();
        page.Find("[data-testid=fqr-coverage-technical-toggle]").GetAttribute("aria-expanded").Should().Be("true");
        page.Find("[data-testid=fqr-coverage-technical-body]").HasAttribute("hidden").Should().BeFalse();
        // Target technical details are likewise collapsed.
        page.Find("[data-testid=fqr-target-technical-toggle]").GetAttribute("aria-expanded").Should().Be("false");
        page.Find("[data-testid=fqr-target-technical-body]").HasAttribute("hidden").Should().BeTrue();
        page.Find("[data-testid=fqr-target-technical-body]").TextContent.Should().Contain("Environment type").And.Contain("Development");
    }

    // 8. Authenticated workflow appears only when relevant.
    [Fact]
    public void AuthenticatedWorkflow_HiddenForPublicTarget()
    {
        Register(HttpOnlyContext());

        var page = Render<FrontendQualityReview>();

        page.FindAll("[data-testid=fqr-authenticated-review]").Should().BeEmpty();
        page.FindAll("h2").Should().NotContain(h => h.TextContent.Contains("Authenticat"), "no authentication section for a public target");
        page.Markup.Should().NotContain("Sign in for review");
    }

    [Fact]
    public void AuthenticatedWorkflow_ManagedEdge_ShowsStepsEvidenceAndSignIn()
    {
        Register(HttpOnlyContext(requiresAuth: true));

        var page = Render<FrontendQualityReview>();

        page.WaitForAssertion(() => page.Find("[data-testid=fqr-authenticated-review]"));
        page.Find("[data-testid=fqr-auth-status]").TextContent.Should().Be("Status: Not connected");
        page.FindAll("[data-testid=fqr-auth-steps] li").Should().HaveCount(3);
        page.Find("[data-testid=fqr-auth-evidence]").TextContent.Should().Contain("Browser session").And.Contain("Authenticated DOM").And.Contain("Not connected");
        page.Find("[data-testid=fqr-auth-sign-in]").TextContent.Should().Be("Sign in for review");
        page.Find("[data-testid=fqr-target-authentication]").TextContent.Should().Be("Microsoft Entra ID");
        // Raw capability rows are behind a collapsed disclosure.
        page.Find("[data-testid=fqr-auth-technical-toggle]").GetAttribute("aria-expanded").Should().Be("false");
    }

    [Fact]
    public void AuthenticatedWorkflow_Proxy_ShowsProxyStepsAndNoBrowserSignIn()
    {
        var context = HttpOnlyContext(requiresAuth: true, method: AuthenticatedTestingMethod.LocalHttpsProxy);
        Register(context, access: FrontendQualityTargetAccess.Build(context, new AuthenticatedReviewCapabilities
        {
            Method = AuthenticatedTestingMethod.LocalHttpsProxy, ContextStatus = AuthenticatedApiContextStatus.WaitingForAuthenticatedTraffic, Reason = "Start the local proxy.",
        }, false, null, LocalHttpsProxyState.Listening));

        var page = Render<FrontendQualityReview>();

        page.WaitForAssertion(() => page.Find("[data-testid=fqr-authenticated-review]"));
        page.Find("[data-testid=fqr-auth-status]").TextContent.Should().Be("Status: Not connected");
        page.FindAll("[data-testid=fqr-auth-steps] li").Should().HaveCount(4);
        page.Find("[data-testid=fqr-auth-steps]").TextContent.Should().Contain("start the Local HTTPS proxy");
        page.Find("[data-testid=fqr-auth-evidence]").TextContent.Should().Contain("REST traffic").And.Contain("GraphQL traffic").And.Contain("Not available with this method");
        page.Find("[data-testid=fqr-auth-open-target]").TextContent.Should().Be("Open Target Environment");
        page.FindAll("[data-testid=fqr-auth-sign-in]").Should().BeEmpty();
        page.FindAll("[data-testid=fqr-proxy-method-note]").Should().ContainSingle();
    }

    // 9. Unavailable optional engine is not presented as an application failure.
    [Fact]
    public void UnavailableOptionalEngine_IsALimitation_NotAFailure()
    {
        Register(Context(),
        [
            Engine(FrontendQualityEngineIdDto.Accessibility),
            Engine(FrontendQualityEngineIdDto.Lighthouse, ready: false, reason: "Lighthouse CLI not installed on this host."),
            Engine(FrontendQualityEngineIdDto.PassiveSecurity),
        ]);

        var page = Render<FrontendQualityReview>();

        page.WaitForAssertion(() => page.Find("[data-testid=fqr-readiness]").GetAttribute("data-readiness").Should().Be("Limited"));
        page.Find("[data-testid=fqr-readiness]").GetAttribute("role").Should().Be("status");
        page.Find("[data-testid=fqr-readiness-title]").TextContent.Should().Be("Review can run with limitations");
        page.Find("[data-testid=fqr-readiness-message]").TextContent.Should().Be("1 optional capability is unavailable.");
        page.Find("[data-testid=fqr-readiness-details]").TextContent.Should().Contain("Lighthouse: Unavailable");
        RunButton(page).HasAttribute("disabled").Should().BeFalse("an unavailable optional engine never blocks the review");

        var lighthouse = Capability(page, FrontendQualityEngineId.Lighthouse);
        lighthouse.QuerySelector("[data-testid=fqr-capability-state]")!.TextContent.Should().Be("Unavailable");
        lighthouse.QuerySelector("[data-testid=fqr-capability-summary]")!.TextContent.Should().Be("The Lighthouse engine is not available in this environment.");
        lighthouse.TextContent.Should().NotContainAny("Failed", "Error", "error");
        page.Find("[data-testid=fqr-dimension][data-category='Performance'] [data-testid=fqr-dimension-state]").TextContent.Should().Be("Enabled");
        page.Find("[data-testid=fqr-dimension][data-category='Performance'] [data-testid=fqr-dimension-limitation]").TextContent.Should().Contain("Lighthouse unavailable");
    }

    // 10. Passive security deployment limitation is presented correctly (technical reason collapsed).
    [Fact]
    public void PassiveSecurity_DeploymentPolicyLimitation_PlainSummary_TechnicalReasonCollapsed()
    {
        Register(Context(),
        [
            Engine(FrontendQualityEngineIdDto.Accessibility),
            Engine(FrontendQualityEngineIdDto.Lighthouse),
            Engine(FrontendQualityEngineIdDto.PassiveSecurity, layer1: false, ready: false, reason: "Container runtime is unavailable."),
        ]);

        var page = Render<FrontendQualityReview>();

        page.WaitForAssertion(() => Capability(page, FrontendQualityEngineId.PassiveSecurity).QuerySelector("[data-testid=fqr-capability-state]")!.TextContent.Should().Be("Unavailable"));
        var row = Capability(page, FrontendQualityEngineId.PassiveSecurity);
        row.QuerySelector("[data-testid=fqr-capability-summary]")!.TextContent.Should().Be("The Passive Security engine is not available in this environment.");
        var toggle = row.QuerySelector("[data-testid='fqr-capability-technical-PassiveSecurity-toggle']")!;
        toggle.GetAttribute("aria-expanded").Should().Be("false");
        toggle.TextContent.Should().Contain("Technical reason");
        var body = row.QuerySelector("[data-testid='fqr-capability-technical-PassiveSecurity-body']")!;
        body.HasAttribute("hidden").Should().BeTrue();
        body.TextContent.Should().Contain("Blocked by deployment policy.");
        // No "Runtime: Unavailable" style implementation line in the primary presentation.
        row.QuerySelector(".fqr-capability-main")!.TextContent.Should().NotContain("Runtime");
    }

    // 11. Typed engine states retain their distinct labels.
    [Fact]
    public void TypedCapabilityStates_RetainDistinctLabels_OnTheLandingView()
    {
        var context = Context(t => t.EnableBrowserRuntimeEngine = false);
        context.ReviewEngineSelection.AccessibilitySelected = false;
        Register(context,
        [
            Engine(FrontendQualityEngineIdDto.Lighthouse, ready: false, reason: "Runtime unavailable."),
            Engine(FrontendQualityEngineIdDto.PassiveSecurity, layer2: false),
        ]);

        var page = Render<FrontendQualityReview>();

        page.WaitForAssertion(() => Capability(page, FrontendQualityEngineId.Lighthouse).QuerySelector("[data-testid=fqr-capability-state]")!.TextContent.Should().Be("Unavailable"));
        State(page, FrontendQualityEngineId.StaticSecurity).Should().Be("Enabled");
        State(page, FrontendQualityEngineId.PassivePerformance).Should().Be("Enabled");
        State(page, FrontendQualityEngineId.BrowserRuntime).Should().Be("Disabled");
        State(page, FrontendQualityEngineId.Accessibility).Should().Be("Not selected");
        State(page, FrontendQualityEngineId.Lighthouse).Should().Be("Unavailable");
        State(page, FrontendQualityEngineId.PassiveSecurity).Should().Be("Disabled in System Settings");
        page.FindAll("[data-testid=fqr-capability-state]").Select(e => e.TextContent).Should().NotContain(t => t.Contains("inactive", StringComparison.OrdinalIgnoreCase), "no vague inactive label on the landing view");
        page.FindAll("[data-testid=fqr-capability-group]").Select(g => g.GetAttribute("data-policy")).Should().Equal("Required", "Optional");
        page.Find("[data-testid=fqr-capability-group][data-policy='Required']").QuerySelectorAll("[data-testid=fqr-capability]").Should().HaveCount(2);
    }

    private static string State(IRenderedComponent<FrontendQualityReview> page, FrontendQualityEngineId id) =>
        Capability(page, id).QuerySelector("[data-testid=fqr-capability-state]")!.TextContent;

    [Fact]
    public void ManagedEdgeWithoutSession_DomEngineRequiresBrowserSession_HttpEnginesStayEnabled()
    {
        Register(Context(requiresAuth: true), [Engine(FrontendQualityEngineIdDto.Accessibility), Engine(FrontendQualityEngineIdDto.Lighthouse), Engine(FrontendQualityEngineIdDto.PassiveSecurity)]);

        var page = Render<FrontendQualityReview>();

        page.WaitForAssertion(() => State(page, FrontendQualityEngineId.Accessibility).Should().Be("Requires browser session"));
        Capability(page, FrontendQualityEngineId.Accessibility).QuerySelector("[data-testid=fqr-capability-summary]")!.TextContent.Should().Contain("Sign in for review");
        State(page, FrontendQualityEngineId.StaticSecurity).Should().Be("Enabled");
        Capability(page, FrontendQualityEngineId.StaticSecurity).QuerySelector("[data-testid=fqr-capability-summary]")!.TextContent.Should().Contain("public frontend");
        State(page, FrontendQualityEngineId.Lighthouse).Should().Be("Not supported for this target");
    }

    // 12. Review results are not shown as successful before execution.
    [Fact]
    public void BeforeExecution_NoReviewResults_NoSuccessLanguage()
    {
        Register(HttpOnlyContext());

        var page = Render<FrontendQualityReview>();

        page.FindAll("[data-testid=fqr-results-heading]").Should().BeEmpty();
        page.FindAll("[data-testid=fqr-coverage-label]").Should().BeEmpty("engine outcome table belongs to results");
        page.FindAll("[data-testid=fqr-result-target]").Should().BeEmpty();
        page.Markup.Should().NotContain("Review results").And.NotContain("Completed — no findings").And.NotContain("Passed").And.NotContain("Assessed");
    }

    // 13. Run button still invokes the existing production orchestration; results appear afterwards.
    [Fact]
    public async Task RunButton_InvokesOrchestrator_ThenShowsReviewResults()
    {
        var orchestrator = Register(HttpOnlyContext());

        var page = Render<FrontendQualityReview>();
        await page.InvokeAsync(() => RunButton(page).Click());

        orchestrator.Verify(o => o.RunAsync(Url, It.IsAny<FrontendAnalysisContext>(), It.IsAny<FrontendQualityEngineExecutionSnapshot?>(), It.IsAny<CancellationToken>()), Times.Once);
        page.WaitForAssertion(() => page.Find("[data-testid=fqr-results-heading]").TextContent.Should().Be("Review results"));
        page.Find("[data-testid=fqr-result-target]").TextContent.Should().Contain("M2LB DEV").And.Contain(Url);
        page.FindAll("[data-testid=fqr-landing]").Should().BeEmpty("capability readiness is landing-only");
        // Engines/access at review start remain available behind a collapsed disclosure when the report carries them.
        page.FindAll("button").Should().Contain(b => b.TextContent.Trim() == "Back to Frontend Quality Review");
    }

    [Fact]
    public void IncludeCheckbox_TogglesPerReviewSelection_WithoutEnablingDisabledEngines()
    {
        var context = Context();
        Register(context, [Engine(FrontendQualityEngineIdDto.Accessibility), Engine(FrontendQualityEngineIdDto.Lighthouse), Engine(FrontendQualityEngineIdDto.PassiveSecurity)]);

        var page = Render<FrontendQualityReview>();

        page.WaitForAssertion(() => RunButton(page).HasAttribute("disabled").Should().BeFalse());
        var include = Capability(page, FrontendQualityEngineId.Lighthouse).QuerySelector("[data-testid=fqr-capability-include]")!;
        include.GetAttribute("aria-label").Should().Be("Include Lighthouse in this review");
        include.Change(false);
        context.ReviewEngineSelection.LighthouseSelected.Should().BeFalse();
        State(page, FrontendQualityEngineId.Lighthouse).Should().Be("Not selected");
        Capability(page, FrontendQualityEngineId.Lighthouse).QuerySelector("[data-testid=fqr-capability-include]").Should().NotBeNull("a deselected engine can be re-included");
        Capability(page, FrontendQualityEngineId.BrowserRuntime).QuerySelector("[data-testid=fqr-capability-include]").Should().BeNull("a disabled engine cannot be selected into the review");
    }

    // 14. Open Target Environment still works.
    [Fact]
    public void OpenTargetEnvironment_LinkInHeader()
    {
        Register(HttpOnlyContext());

        var page = Render<FrontendQualityReview>();

        var link = page.Find("[data-testid=fqr-open-target-environment]");
        link.TextContent.Should().Be("Open Target Environment");
        link.GetAttribute("href").Should().Be(FrontendQualityTargetAccess.TargetEnvironmentsHref);
        page.Find("[data-testid=fqr-change-target]").GetAttribute("href").Should().Be(FrontendQualityTargetAccess.TargetEnvironmentsHref);
    }

    // 15. Existing report/export paths are unaffected.
    [Fact]
    public async Task ExportHtml_StillUsesReportExportService()
    {
        var export = new Mock<IReportExportService>();
        export.Setup(e => e.ExportFrontendQualityReview(It.IsAny<FrontendQualityReviewReport>(), It.IsAny<string>())).Returns("<html></html>");
        Register(HttpOnlyContext(), export: export);

        var page = Render<FrontendQualityReview>();
        await page.InvokeAsync(() => RunButton(page).Click());
        page.WaitForAssertion(() => page.Find("[data-testid=fqr-results-heading]"));
        await page.InvokeAsync(() => page.FindAll("button").Single(b => b.TextContent.Trim() == "Export HTML").Click());

        export.Verify(e => e.ExportFrontendQualityReview(It.Is<FrontendQualityReviewReport>(r => r.TargetUrl == Url), It.IsAny<string>()), Times.Once);
        JSInterop.VerifyInvoke("downloadHtmlFile");
    }

    // 16. Keyboard/semantic markup for expanders and buttons is correct.
    [Fact]
    public void Disclosures_AreRealButtonsWithAriaControls_AndHeadingsAreSemantic()
    {
        Register(HttpOnlyContext());

        var page = Render<FrontendQualityReview>();

        page.WaitForAssertion(() => page.Find("[data-testid=fqr-coverage-technical-toggle]"));
        var toggles = page.FindAll(".fqr-disclosure-toggle");
        toggles.Should().NotBeEmpty();
        foreach (var toggle in toggles)
        {
            toggle.TagName.Should().Be("BUTTON");
            toggle.GetAttribute("type").Should().Be("button");
            toggle.GetAttribute("aria-expanded").Should().BeOneOf("true", "false");
            var controls = toggle.GetAttribute("aria-controls");
            controls.Should().NotBeNullOrWhiteSpace();
            page.Find($"#{controls}").Should().NotBeNull("aria-controls must reference the disclosure body");
            toggle.TextContent.Trim().Should().NotBeEmpty("every disclosure button needs an accessible name");
        }
        page.FindAll("h1").Should().ContainSingle();
        page.FindAll("h2").Select(h => h.TextContent.Trim()).Should().Contain(["Target", "What will be analysed?", "Coverage", "Review capabilities"])
            .And.Contain(h => h.StartsWith("Checks included"));
        page.FindAll("button").Should().OnlyContain(b => b.TextContent.Trim().Length > 0 || b.HasAttribute("aria-label"));
        page.FindAll(".fqr-pill").Should().OnlyContain(p => p.TextContent.Trim().Length > 0, "badges carry text labels");
        page.FindAll("table").Should().BeEmpty("the landing view has no tabular data");
    }

    [Fact]
    public void EngineDetails_CollapsedByDefault_StillHoldsActiveEngineSummaryAndCards()
    {
        Register(Context(), [Engine(FrontendQualityEngineIdDto.Accessibility), Engine(FrontendQualityEngineIdDto.Lighthouse), Engine(FrontendQualityEngineIdDto.PassiveSecurity)]);

        var page = Render<FrontendQualityReview>();

        page.WaitForAssertion(() => page.FindAll(".fqr-engine-card").Should().HaveCount(3));
        page.Find("[data-testid=fqr-engine-details-toggle]").GetAttribute("aria-expanded").Should().Be("false");
        var body = page.Find("[data-testid=fqr-engine-details-body]");
        body.HasAttribute("hidden").Should().BeTrue();
        body.QuerySelector("[data-testid=fqr-active-count]")!.TextContent.Should().Be("5 enabled");
        body.QuerySelectorAll(".fqr-engine-card").Should().HaveCount(3);
        body.TextContent.Should().Contain("Engine ID").And.Contain("Deployment policy");
        // Implementation vocabulary does not leak outside the disclosure.
        page.Find("[data-testid=fqr-capabilities] .fqr-capability-groups").TextContent.Should().NotContain("Layer").And.NotContain("Engine ID");
    }
}
