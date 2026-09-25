using BirkNext.LocalHttpsProxy;
using BirkNext.ManagedEdge;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// The Frontend Quality Review access scope: Target Environment configuration → available access paths → what the
/// runnable engines cover → what the assessed engines actually covered. "Authentication configured", "authenticated
/// access included", "authenticated access available" and "authenticated review executed" stay four different facts.
/// </summary>
public sealed class FrontendQualityReviewScopeTests
{
    private const string Url = "https://m2lbdev.example.test/";

    private static FrontendAnalysisContext Context(bool requiresAuth,
        FrontendAuthenticationType type = FrontendAuthenticationType.MicrosoftEntraId,
        AuthenticatedTestingMethod method = AuthenticatedTestingMethod.ManagedEdgeCdp)
    {
        var profile = new FrontendAnalysisProfile { Id = "dev", Name = "M2LB DEV", EnvironmentType = FrontendEnvironmentType.Development, TargetUrl = Url };
        profile.Authentication.RequiresAuthentication = requiresAuth;
        profile.Authentication.AuthenticationType = type;
        profile.Authentication.AuthenticatedTestingMethod = method;
        return new FrontendAnalysisContext
        {
            ActiveProfile = profile, TargetUrl = Url, RequiresAuthentication = requiresAuth, AuthenticationType = type,
            FeatureToggles = profile.Features, EngineRequirements = profile.EngineRequirements, ReviewEngineSelection = profile.ReviewEngineSelection,
        };
    }

    private static FrontendQualityTargetAccessContext Access(FrontendAnalysisContext context, bool session = false,
        AuthenticatedReviewCapabilities? caps = null) =>
        FrontendQualityTargetAccess.Build(context, caps, session, session ? ManagedEdgeState.ConnectedAuthenticated : null, null);

    private static FrontendQualityCapabilityRow Row(FrontendQualityEngineId id,
        FrontendQualityCapabilityState state = FrontendQualityCapabilityState.Enabled) =>
        new(id, id.ToString(), FrontendQualityEngineRequirement.Required, state, null, null);

    private static readonly FrontendQualityCapabilityRow[] PublicAndDomEngines =
    [
        Row(FrontendQualityEngineId.StaticSecurity),
        Row(FrontendQualityEngineId.PassivePerformance),
        Row(FrontendQualityEngineId.Accessibility),
    ];

    // ── A. Public only ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_PublicOnly_IsPublicOnlyAndReady()
    {
        var context = Context(requiresAuth: false, type: FrontendAuthenticationType.None);
        var scope = FrontendQualityReviewScopes.Resolve(context, Access(context), PublicAndDomEngines);

        scope.Configured.Should().Be(FrontendReviewAccessScope.PublicOnly);
        scope.Effective.Should().Be(FrontendReviewAccessScope.PublicOnly);
        scope.PublicAccess.Should().Be(FrontendQualityAccessPathState.Available);
        scope.AuthenticatedAccess.Should().Be(FrontendQualityAccessPathState.NotIncluded);
        scope.Engines.Should().OnlyContain(e => e.Path == FrontendQualityEngineAccessPath.Public, "every engine runs anonymously");
        FrontendQualityReviewScopes.ScopeSummary(scope).Should().Be("Public only");
        FrontendQualityReviewScopes.AuthenticatedAccessLabel(scope).Should().Be("Not included in this review");
        FrontendQualityReviewScopes.TargetStatus(scope).Should().Be(("Ready", true));
        FrontendQualityReviewScopes.LimitationSentence(scope).Should().BeNull();
    }

    // ── B. Authenticated only ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void B_OnlyAuthenticatedEnginesRunnable_IsAuthenticatedOnly_AndSaysThePublicPartIsNotCovered()
    {
        var context = Context(requiresAuth: true);
        var scope = FrontendQualityReviewScopes.Resolve(context, Access(context, session: true),
            [Row(FrontendQualityEngineId.StaticSecurity, FrontendQualityCapabilityState.Disabled), Row(FrontendQualityEngineId.Accessibility)]);

        scope.Effective.Should().Be(FrontendReviewAccessScope.AuthenticatedOnly);
        scope.Narrowed.Should().BeTrue();
        FrontendQualityReviewScopes.ScopeSummary(scope).Should().Be("Authenticated only · public scope unavailable");
        FrontendQualityReviewScopes.TargetStatus(scope).Label.Should().Be("Ready with limitations");
    }

    // ── C. Public + authenticated ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void C_BothPathsAvailable_IsPublicPlusAuthenticated_SplitByEngine()
    {
        var context = Context(requiresAuth: true);
        var scope = FrontendQualityReviewScopes.Resolve(context, Access(context, session: true), PublicAndDomEngines);

        scope.Configured.Should().Be(FrontendReviewAccessScope.PublicAndAuthenticated);
        scope.Effective.Should().Be(FrontendReviewAccessScope.PublicAndAuthenticated);
        scope.AuthenticatedAccess.Should().Be(FrontendQualityAccessPathState.Available);
        // Split by engine: the HTTP engines review the public shell, the DOM engine the signed-in pages.
        scope.Engines.Single(e => e.EngineId == FrontendQualityEngineId.StaticSecurity).Path.Should().Be(FrontendQualityEngineAccessPath.Public);
        scope.Engines.Single(e => e.EngineId == FrontendQualityEngineId.Accessibility).Path.Should().Be(FrontendQualityEngineAccessPath.Authenticated);
        FrontendQualityReviewScopes.ScopeSummary(scope).Should().Be("Public + authenticated");
        FrontendQualityReviewScopes.AuthenticatedAccessLabel(scope).Should().Be("Available");
        FrontendQualityReviewScopes.TargetStatus(scope).Should().Be(("Ready", true));
    }

    // ── D. Both configured, authenticated unavailable ──────────────────────────────────────────────────────────────

    [Fact]
    public void D_BothConfiguredButNoSession_IsReadyWithLimitations_AndPublicNeverMasksTheGap()
    {
        var context = Context(requiresAuth: true);
        var scope = FrontendQualityReviewScopes.Resolve(context, Access(context, session: false),
            [Row(FrontendQualityEngineId.StaticSecurity), Row(FrontendQualityEngineId.Accessibility, FrontendQualityCapabilityState.RequiresBrowserSession)]);

        scope.Effective.Should().Be(FrontendReviewAccessScope.PublicOnly);
        scope.Narrowed.Should().BeTrue();
        scope.AuthenticatedAccess.Should().Be(FrontendQualityAccessPathState.Unavailable);
        FrontendQualityReviewScopes.ScopeSummary(scope).Should().Be("Public only · authenticated scope unavailable");
        FrontendQualityReviewScopes.TargetStatus(scope).Should().Be(("Ready with limitations", false));
        FrontendQualityReviewScopes.LimitationSentence(scope).Should()
            .Be($"Public scope is available. Authenticated coverage is unavailable: {FrontendQualityReviewScopes.SessionMissingDetail}");

        // A "Ready" banner becomes Limited with that sentence; a Limited one gains it once.
        var ready = new FrontendQualityReviewReadiness(FrontendQualityReviewReadinessLevel.Ready, "Ready to review", "ok", []);
        var applied = FrontendQualityReviewScopes.ApplyTo(ready, scope);
        applied.Level.Should().Be(FrontendQualityReviewReadinessLevel.Limited);
        applied.Message.Should().StartWith("Public scope is available.").And.NotContain("not required");
        var limited = new FrontendQualityReviewReadiness(FrontendQualityReviewReadinessLevel.Limited, "x", "Accessibility requires a browser session.", []);
        FrontendQualityReviewScopes.ApplyTo(FrontendQualityReviewScopes.ApplyTo(limited, scope), scope).Message
            .Should().Be($"{FrontendQualityReviewScopes.LimitationSentence(scope)} Accessibility requires a browser session.");
    }

    [Fact]
    public void D_NothingCanRun_IsBlocked()
    {
        var context = Context(requiresAuth: true);
        var scope = FrontendQualityReviewScopes.Resolve(context, Access(context),
            [Row(FrontendQualityEngineId.Accessibility, FrontendQualityCapabilityState.RequiresBrowserSession)]);
        scope.Effective.Should().BeNull();
        FrontendQualityReviewScopes.TargetStatus(scope).Should().Be(("Blocked", false));
    }

    // ── E. Authentication configured, public scope ─────────────────────────────────────────────────────────────────

    [Fact]
    public void E_EntraConfiguredButPublicScope_AuthenticatedIsNotIncluded_AndASessionIsNamedAsSuch()
    {
        var context = Context(requiresAuth: false, type: FrontendAuthenticationType.MicrosoftEntraId);
        var without = FrontendQualityReviewScopes.Resolve(context, Access(context), PublicAndDomEngines);
        without.Configured.Should().Be(FrontendReviewAccessScope.PublicOnly, "a configured provider never changes the scope");
        FrontendQualityReviewScopes.AuthenticatedAccessLabel(without).Should().Be("Not included in this review");

        var with = FrontendQualityReviewScopes.Resolve(context, Access(context, session: true), PublicAndDomEngines);
        with.Effective.Should().Be(FrontendReviewAccessScope.PublicOnly, "the session is not used in a public-only review");
        FrontendQualityReviewScopes.AuthenticatedAccessLabel(with).Should().Be("Available · not included in this review");

        var summary = FrontendQualityLandingPresentation.TargetSummary(context, with);
        summary.ReviewScope.Should().Be("Public only");
        summary.AuthenticatedAccess.Should().Be("Available · not included in this review");
        FrontendQualityLandingPresentation.TechnicalTargetFields(context, with)
            .Should().Contain(f => f.Label == "Authentication configured" && f.Value == "Microsoft Entra ID")
            .And.NotContain(f => f.Value == "Not required");
    }

    // ── Not authenticated coverage by association ───────────────────────────────────────────────────────────────────

    [Fact]
    public void AnAuthenticatedApiContextIsNotAnAuthenticatedFrontend()
    {
        var context = Context(requiresAuth: true, method: AuthenticatedTestingMethod.LocalHttpsProxy);
        var caps = new AuthenticatedReviewCapabilities { Method = AuthenticatedTestingMethod.LocalHttpsProxy, ContextStatus = AuthenticatedApiContextStatus.Available, AuthenticatedApi = true };
        var scope = FrontendQualityReviewScopes.Resolve(context, Access(context, caps: caps), PublicAndDomEngines);

        scope.AuthenticatedAccess.Should().Be(FrontendQualityAccessPathState.Unavailable);
        scope.AuthenticatedDetail.Should().Be(FrontendQualityReviewScopes.ProxyApiOnlyDetail);
        scope.Engines.Single(e => e.EngineId == FrontendQualityEngineId.StaticSecurity).Path.Should().Be(FrontendQualityEngineAccessPath.Public);
        scope.Effective.Should().Be(FrontendReviewAccessScope.PublicOnly);
    }

    [Fact]
    public void ASessionNoActiveEngineUsesIsNotAuthenticatedCoverage()
    {
        var context = Context(requiresAuth: true);
        var scope = FrontendQualityReviewScopes.Resolve(context, Access(context, session: true),
            [Row(FrontendQualityEngineId.StaticSecurity), Row(FrontendQualityEngineId.Accessibility, FrontendQualityCapabilityState.Disabled)]);
        scope.AuthenticatedAccess.Should().Be(FrontendQualityAccessPathState.Available);
        scope.Effective.Should().Be(FrontendReviewAccessScope.PublicOnly);
        scope.AuthenticatedDetail.Should().Be(FrontendQualityReviewScopes.NoAuthenticatedEngineDetail);
    }

    [Fact]
    public void BrowserCompanionEvidenceProvesNeitherPath()
    {
        var context = Context(requiresAuth: true);
        var scope = FrontendQualityReviewScopes.Resolve(context, Access(context, session: true),
            [Row(FrontendQualityEngineId.BrowserQuality, FrontendQualityCapabilityState.Ready)]);
        scope.Engines.Single().Path.Should().Be(FrontendQualityEngineAccessPath.CompanionEvidence);
        scope.Effective.Should().BeNull("a connected Companion is not authenticated coverage");
        scope.CompanionOnly.Should().BeTrue();
        FrontendQualityReviewScopes.ScopeSummary(scope).Should().Be("Browser Companion evidence only · sign-in state not recorded");
        FrontendQualityReviewScopes.TargetStatus(scope).Label.Should().Be("Ready with limitations");
    }

    [Fact]
    public void SignInRequiredWithoutAProvider_IsNotConfigured()
    {
        var context = Context(requiresAuth: true, type: FrontendAuthenticationType.None);
        FrontendQualityReviewScopes.Resolve(context, Access(context), PublicAndDomEngines).AuthenticatedAccess
            .Should().Be(FrontendQualityAccessPathState.NotConfigured);
    }

    // ── Engine access matrix ───────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(FrontendQualityEngineId.StaticSecurity, "Public", "Public")]
    [InlineData(FrontendQualityEngineId.PassivePerformance, "Public", "Public")]
    [InlineData(FrontendQualityEngineId.PassiveSecurity, "Public", "Public")]
    [InlineData(FrontendQualityEngineId.Accessibility, "Public", "Authenticated")]
    [InlineData(FrontendQualityEngineId.BrowserRuntime, "Public", "Authenticated")]
    [InlineData(FrontendQualityEngineId.Lighthouse, "Public", "Unsupported")]
    [InlineData(FrontendQualityEngineId.BrowserQuality, "CompanionEvidence", "CompanionEvidence")]
    [InlineData(FrontendQualityEngineId.PerformanceQuality, "CompanionEvidence", "CompanionEvidence")]
    public void EngineAccessMatrix(FrontendQualityEngineId id, string publicScope, string authenticatedScope)
    {
        static string PathOf(FrontendQualityEngineAccessDecision d) =>
            d.State == FrontendQualityEngineAccessState.Unsupported ? "Unsupported" : FrontendQualityReviewScopes.PathFor(d.AccessKind).ToString();
        var engine = FrontendQualityEngineAccessRegistry.For(id);
        PathOf(FrontendQualityTargetAccess.Decide(engine, Access(Context(requiresAuth: false)))).Should().Be(publicScope);
        PathOf(FrontendQualityTargetAccess.Decide(engine, Access(Context(requiresAuth: true), session: true))).Should().Be(authenticatedScope);
    }

    // ── Post-run: selected vs executed ─────────────────────────────────────────────────────────────────────────────

    private static FrontendQualityEngineOutcome Outcome(FrontendQualityEngineId id, FrontendQualityEngineAccessKind kind,
        FrontendQualityEngineExecutionState state = FrontendQualityEngineExecutionState.Assessed) =>
        new() { EngineId = id, DisplayName = id.ToString(), Enabled = true, ExecutionState = state, AccessKind = kind };

    private static FrontendQualityReviewReport Report(bool requiresAuth, params FrontendQualityEngineOutcome[] outcomes) => new()
    {
        TargetUrl = Url,
        EngineOutcomes = outcomes.ToList(),
        TargetAccess = new FrontendQualityTargetAccessContext { RequiresAuthentication = requiresAuth, AuthenticationType = FrontendAuthenticationType.MicrosoftEntraId },
    };

    [Fact]
    public void Executed_BothPathsAssessed_ReportsBoth()
    {
        var executed = FrontendQualityReviewScopes.Executed(Report(true,
            Outcome(FrontendQualityEngineId.StaticSecurity, FrontendQualityEngineAccessKind.PublicHttp),
            Outcome(FrontendQualityEngineId.Accessibility, FrontendQualityEngineAccessKind.AuthenticatedBrowserSession)));
        executed.Executed.Should().Be(FrontendReviewAccessScope.PublicAndAuthenticated);
        executed.Partial.Should().BeFalse();
        executed.PublicEngines.Should().Equal("StaticSecurity");
        executed.AuthenticatedEngines.Should().Equal("Accessibility");
    }

    [Fact]
    public void Executed_AuthenticatedPortionNotAssessed_IsPartial_NotFullScopeSuccess()
    {
        var report = Report(true,
            Outcome(FrontendQualityEngineId.StaticSecurity, FrontendQualityEngineAccessKind.PublicHttp),
            Outcome(FrontendQualityEngineId.Accessibility, FrontendQualityEngineAccessKind.AuthenticatedBrowserSession, FrontendQualityEngineExecutionState.NotApplicable),
            Outcome(FrontendQualityEngineId.BrowserQuality, FrontendQualityEngineAccessKind.BrowserCompanion));
        var executed = FrontendQualityReviewScopes.Executed(report);
        executed.Configured.Should().Be(FrontendReviewAccessScope.PublicAndAuthenticated);
        executed.Executed.Should().Be(FrontendReviewAccessScope.PublicOnly);
        executed.Partial.Should().BeTrue();
        executed.AuthenticatedEngines.Should().BeEmpty();
        executed.CompanionEngines.Should().Equal("BrowserQuality");

        var html = new ReportExportService().ExportFrontendQualityReview(report, "Test");
        html.Should().Contain("<strong>Review scope:</strong></dt><dd>Public + authenticated</dd>")
            .And.Contain("<strong>Access used:</strong></dt><dd>Public only (partial: the configured scope was not fully assessed)</dd>")
            .And.Contain("<strong>Signed-in pages reviewed by:</strong></dt><dd>No engine assessed this path</dd>");
    }

    [Fact]
    public void Executed_PublicReview_IsNotPartial()
    {
        var executed = FrontendQualityReviewScopes.Executed(Report(false,
            Outcome(FrontendQualityEngineId.StaticSecurity, FrontendQualityEngineAccessKind.PublicHttp),
            Outcome(FrontendQualityEngineId.Accessibility, FrontendQualityEngineAccessKind.BrowserRuntime)));
        executed.Executed.Should().Be(FrontendReviewAccessScope.PublicOnly);
        executed.Partial.Should().BeFalse();
    }
}
