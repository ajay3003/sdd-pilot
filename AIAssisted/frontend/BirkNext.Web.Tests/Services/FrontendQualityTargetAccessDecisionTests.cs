using BirkNext.LocalHttpsProxy;
using BirkNext.ManagedEdge;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// Engine access requirements are matched against the resolved Target Environment access before any request is made.
/// Every known blocker must fail fast with its precise reason; no engine may be forced onto an access method it cannot use.
/// </summary>
public sealed class FrontendQualityTargetAccessDecisionTests
{
    private static FrontendAnalysisContext Context(bool requiresAuth, AuthenticatedTestingMethod method, bool sessionAvailable = false,
        ManualAuthenticationVerificationEvidence? manual = null, AuthenticationVerificationMode verification = AuthenticationVerificationMode.Automated)
    {
        var profile = new FrontendAnalysisProfile { Id = "dev", Name = "M2LB DEV", EnvironmentType = FrontendEnvironmentType.Development, TargetUrl = "https://m2lbdev.example.test/" };
        profile.Authentication.RequiresAuthentication = requiresAuth;
        profile.Authentication.AuthenticationType = requiresAuth ? FrontendAuthenticationType.MicrosoftEntraId : FrontendAuthenticationType.None;
        profile.Authentication.AuthenticatedTestingMethod = method;
        profile.Authentication.VerificationMode = verification;
        profile.ManualVerification = manual;
        return new FrontendAnalysisContext
        {
            ActiveProfile = profile, TargetUrl = profile.TargetUrl!, RequiresAuthentication = requiresAuth,
            AuthenticationType = profile.Authentication.AuthenticationType, IsAuthenticatedSessionAvailable = sessionAvailable,
        };
    }

    private static AuthenticatedReviewCapabilities ProxyCaps(AuthenticatedApiContextStatus status) => new()
    {
        Method = AuthenticatedTestingMethod.LocalHttpsProxy, ContextStatus = status, AuthenticatedApi = status == AuthenticatedApiContextStatus.Available,
        AuthenticatedRest = status == AuthenticatedApiContextStatus.Available, Reason = status == AuthenticatedApiContextStatus.Available ? "Authenticated via Local HTTPS Proxy (memory only)." : "Start the local proxy and sign in.",
    };

    private static FrontendQualityEngineAccessRequirements ProxyCapableHttpEngine() =>
        FrontendQualityEngineAccessRegistry.For(FrontendQualityEngineId.StaticSecurity) with
        {
            RequiresPublicHttp = false, RequiresAuthenticatedHttp = true, SupportsProxyAuthenticatedContext = true,
        };

    [Fact]
    public void Registry_DeclaresAuditedRequirementsForEveryEngine()
    {
        FrontendQualityEngineAccessRegistry.All.Select(r => r.EngineId).Should().BeEquivalentTo(Enum.GetValues<FrontendQualityEngineId>());
        var staticSecurity = FrontendQualityEngineAccessRegistry.For(FrontendQualityEngineId.StaticSecurity);
        staticSecurity.RequiresPublicHttp.Should().BeTrue();
        staticSecurity.RequiresBrowserDom.Should().BeFalse();
        staticSecurity.RequiresAuthenticatedHttp.Should().BeFalse();
        var passivePerformance = FrontendQualityEngineAccessRegistry.For(FrontendQualityEngineId.PassivePerformance);
        passivePerformance.RequiresPublicHttp.Should().BeTrue();
        passivePerformance.RequiresBrowserRuntime.Should().BeFalse("runtime metrics are explicitly out of scope for the passive engine");
        FrontendQualityEngineAccessRegistry.For(FrontendQualityEngineId.BrowserRuntime).RequiresBrowserDom.Should().BeTrue();
        FrontendQualityEngineAccessRegistry.For(FrontendQualityEngineId.Accessibility).RequiresBrowserDom.Should().BeTrue();
        FrontendQualityEngineAccessRegistry.All.Where(r => r.SupportsProxyAuthenticatedContext).Select(r => r.EngineId)
            .Should().BeEquivalentTo([FrontendQualityEngineId.StaticSecurity, FrontendQualityEngineId.PassivePerformance],
                "only the HTTP engines consume the proxy's authenticated API-surface probes; a bearer never signs a browser into the SPA");
    }

    [Fact]
    public void PublicTarget_EveryEngineReadyOnDirectPublicAccess()
    {
        var access = FrontendQualityTargetAccess.Build(Context(false, AuthenticatedTestingMethod.ManagedEdgeCdp), null, false, null, null);

        access.Mode.Should().Be(FrontendQualityTargetAccessMode.PublicDirect);
        var decisions = FrontendQualityTargetAccess.DecideAll(access);
        decisions.Values.Should().OnlyContain(d => d.IsReady);
        decisions[FrontendQualityEngineId.StaticSecurity].AccessKind.Should().Be(FrontendQualityEngineAccessKind.PublicHttp);
        decisions[FrontendQualityEngineId.BrowserRuntime].AccessKind.Should().Be(FrontendQualityEngineAccessKind.BrowserRuntime);
    }

    [Fact]
    public void ProxyMethodWithContext_HttpEnginesReady_DomEnginesUnsupportedWithProxyReason()
    {
        var access = FrontendQualityTargetAccess.Build(Context(true, AuthenticatedTestingMethod.LocalHttpsProxy), ProxyCaps(AuthenticatedApiContextStatus.Available), false, null, LocalHttpsProxyState.Ready);

        access.Mode.Should().Be(FrontendQualityTargetAccessMode.LocalHttpsProxy);
        access.AuthenticatedApiAvailable.Should().BeTrue();
        access.AuthenticatedBrowserDomAvailable.Should().BeFalse("the proxy never enables DOM");
        var decisions = FrontendQualityTargetAccess.DecideAll(access);
        decisions[FrontendQualityEngineId.StaticSecurity].IsReady.Should().BeTrue();
        decisions[FrontendQualityEngineId.StaticSecurity].AccessKind.Should().Be(FrontendQualityEngineAccessKind.PublicHttp);
        decisions[FrontendQualityEngineId.PassivePerformance].IsReady.Should().BeTrue();
        foreach (var dom in new[] { FrontendQualityEngineId.BrowserRuntime, FrontendQualityEngineId.Accessibility })
        {
            decisions[dom].State.Should().Be(FrontendQualityEngineAccessState.Unsupported);
            decisions[dom].OutcomeReason.Should().Be(FrontendQualityEngineOutcomeReason.BrowserDomUnavailableForMethod);
            decisions[dom].Reason.Should().Contain("Authenticated DOM unavailable with Local HTTPS Proxy");
            decisions[dom].RequiredAction.Should().Contain("Managed Edge (CDP)");
        }
    }

    [Fact]
    public void ProxyMethodWithoutContext_ProxyCapableHttpEngineBlockedImmediately()
    {
        var access = FrontendQualityTargetAccess.Build(Context(true, AuthenticatedTestingMethod.LocalHttpsProxy), ProxyCaps(AuthenticatedApiContextStatus.WaitingForAuthenticatedTraffic), false, null, LocalHttpsProxyState.Stopped);

        access.Mode.Should().Be(FrontendQualityTargetAccessMode.AuthRequiredContextMissing);
        var decision = FrontendQualityTargetAccess.Decide(ProxyCapableHttpEngine(), access);
        decision.State.Should().Be(FrontendQualityEngineAccessState.Blocked);
        decision.OutcomeReason.Should().Be(FrontendQualityEngineOutcomeReason.AuthenticatedContextUnavailable);
        decision.Reason.Should().StartWith("Authenticated context not available");
        decision.Reason.Should().NotContain("timeout");
    }

    [Fact]
    public void ProxyMethodWithExpiredContext_ReportsExpiredNotMissing()
    {
        var access = FrontendQualityTargetAccess.Build(Context(true, AuthenticatedTestingMethod.LocalHttpsProxy), ProxyCaps(AuthenticatedApiContextStatus.Expired), false, null, null);

        var decision = FrontendQualityTargetAccess.Decide(ProxyCapableHttpEngine(), access);
        decision.OutcomeReason.Should().Be(FrontendQualityEngineOutcomeReason.AuthenticatedContextExpired);
    }

    [Fact]
    public void ProxyMethodWithContext_ProxyCapableHttpEngineReadyOnAuthenticatedHttp()
    {
        var access = FrontendQualityTargetAccess.Build(Context(true, AuthenticatedTestingMethod.LocalHttpsProxy), ProxyCaps(AuthenticatedApiContextStatus.Available), false, null, null);

        var decision = FrontendQualityTargetAccess.Decide(ProxyCapableHttpEngine(), access);
        decision.IsReady.Should().BeTrue();
        decision.AccessKind.Should().Be(FrontendQualityEngineAccessKind.AuthenticatedHttp);
        decision.AccessLabel.Should().Contain("Local HTTPS Proxy");
    }

    [Fact]
    public void CdpMethodWithSession_DomEnginesReadyOnAuthenticatedBrowserSession()
    {
        var access = FrontendQualityTargetAccess.Build(Context(true, AuthenticatedTestingMethod.ManagedEdgeCdp, sessionAvailable: true), null, true, ManagedEdgeState.ConnectedAuthenticated, null);

        access.Mode.Should().Be(FrontendQualityTargetAccessMode.ManagedEdgeCdp);
        access.AuthenticatedBrowserDomAvailable.Should().BeTrue();
        var decisions = FrontendQualityTargetAccess.DecideAll(access);
        decisions[FrontendQualityEngineId.BrowserRuntime].IsReady.Should().BeTrue();
        decisions[FrontendQualityEngineId.BrowserRuntime].AccessKind.Should().Be(FrontendQualityEngineAccessKind.AuthenticatedBrowserSession);
        decisions[FrontendQualityEngineId.Accessibility].IsReady.Should().BeTrue();
        decisions[FrontendQualityEngineId.Lighthouse].State.Should().Be(FrontendQualityEngineAccessState.Unsupported, "Lighthouse has no authenticated mode");
    }

    [Fact]
    public void CdpMethodEnterpriseBlocked_DomEnginesBlockedByEnterpriseProtectionNotTimeout()
    {
        var access = FrontendQualityTargetAccess.Build(Context(true, AuthenticatedTestingMethod.ManagedEdgeCdp), null, false, ManagedEdgeState.TargetTabNotInspectable, null);

        access.Mode.Should().Be(FrontendQualityTargetAccessMode.EnterpriseBlocked);
        access.EnterpriseBlocked.Should().BeTrue();
        var decision = FrontendQualityTargetAccess.DecideAll(access)[FrontendQualityEngineId.BrowserRuntime];
        decision.State.Should().Be(FrontendQualityEngineAccessState.Blocked);
        decision.OutcomeReason.Should().Be(FrontendQualityEngineOutcomeReason.EnterpriseBrowserProtectionBlocked);
        decision.Reason.Should().Contain("enterprise browser protection").And.NotContain("timeout");
    }

    [Fact]
    public void CdpMethodWithoutSession_DomEnginesBlockedUntilSignIn()
    {
        var access = FrontendQualityTargetAccess.Build(Context(true, AuthenticatedTestingMethod.ManagedEdgeCdp), null, false, ManagedEdgeState.NotConnected, null);

        access.Mode.Should().Be(FrontendQualityTargetAccessMode.AuthRequiredContextMissing);
        var decision = FrontendQualityTargetAccess.DecideAll(access)[FrontendQualityEngineId.Accessibility];
        decision.State.Should().Be(FrontendQualityEngineAccessState.Blocked);
        decision.OutcomeReason.Should().Be(FrontendQualityEngineOutcomeReason.AuthenticationRequired);
        decision.RequiredAction.Should().Be("Sign in for review");
        FrontendQualityTargetAccess.DecideAll(access)[FrontendQualityEngineId.StaticSecurity].IsReady.Should().BeTrue();
    }

    [Fact]
    public void ManualOnly_AuthenticatedEnginesUnsupported_PublicHttpEnginesReady()
    {
        var access = FrontendQualityTargetAccess.Build(Context(true, AuthenticatedTestingMethod.ManualOnly), null, false, null, null);

        access.Mode.Should().Be(FrontendQualityTargetAccessMode.ManualOnly);
        access.ManualVerificationStatus.Should().Be(ManualAuthenticationVerificationStatus.Required);
        var decisions = FrontendQualityTargetAccess.DecideAll(access);
        decisions[FrontendQualityEngineId.BrowserRuntime].State.Should().Be(FrontendQualityEngineAccessState.Unsupported);
        decisions[FrontendQualityEngineId.BrowserRuntime].OutcomeReason.Should().Be(FrontendQualityEngineOutcomeReason.ManualOnlyMethod);
        decisions[FrontendQualityEngineId.BrowserRuntime].Reason.Should().Contain("manual verification only");
        decisions[FrontendQualityEngineId.StaticSecurity].IsReady.Should().BeTrue();
        FrontendQualityTargetAccess.Decide(ProxyCapableHttpEngine(), access).OutcomeReason.Should().Be(FrontendQualityEngineOutcomeReason.ManualOnlyMethod);
    }

    [Fact]
    public void ManualVerificationPassed_NeverGrantsAutomatedAccess()
    {
        var context = Context(true, AuthenticatedTestingMethod.ManualOnly);
        context.ActiveProfile.ManualVerification = ManualAuthenticationVerificationEvidence.Record(context.ActiveProfile, ManualAuthenticationVerificationStatus.Passed);

        var access = FrontendQualityTargetAccess.Build(context, null, false, null, null);

        access.ManualVerificationStatus.Should().Be(ManualAuthenticationVerificationStatus.Passed);
        access.AuthenticatedBrowserDomAvailable.Should().BeFalse();
        access.AuthenticatedApiAvailable.Should().BeFalse();
        FrontendQualityTargetAccess.AutomatedAccessLabel(access).Should().StartWith("Unavailable");
        FrontendQualityTargetAccess.ManualVerificationLabel(access.ManualVerificationStatus).Should().Be("Passed");
        FrontendQualityTargetAccess.DecideAll(access)[FrontendQualityEngineId.BrowserRuntime].IsReady.Should().BeFalse();
    }

    [Fact]
    public void ManualVerificationRecordedForOtherSettings_IsStale()
    {
        var context = Context(true, AuthenticatedTestingMethod.ManagedEdgeCdp);
        var other = new FrontendAnalysisProfile { Id = "dev", TargetUrl = "https://other.example.test/" };
        context.ActiveProfile.ManualVerification = ManualAuthenticationVerificationEvidence.Record(other, ManualAuthenticationVerificationStatus.Passed);

        FrontendQualityTargetAccess.Build(context, null, false, null, null).ManualVerificationStatus
            .Should().Be(ManualAuthenticationVerificationStatus.Stale);
    }

    [Fact]
    public void Labels_DistinguishContextDomAndAutomationPerMode()
    {
        var proxy = FrontendQualityTargetAccess.Build(Context(true, AuthenticatedTestingMethod.LocalHttpsProxy), ProxyCaps(AuthenticatedApiContextStatus.Available), false, null, null);
        FrontendQualityTargetAccess.ApiContextLabel(proxy).Should().Be("Available — memory only");
        FrontendQualityTargetAccess.BrowserDomLabel(proxy).Should().Be("Not available with Local HTTPS Proxy");
        FrontendQualityTargetAccess.AutomatedAccessLabel(proxy).Should().Contain("no DOM");

        var missing = FrontendQualityTargetAccess.Build(Context(true, AuthenticatedTestingMethod.LocalHttpsProxy), ProxyCaps(AuthenticatedApiContextStatus.WaitingForAuthenticatedTraffic), false, null, null);
        FrontendQualityTargetAccess.ApiContextLabel(missing).Should().StartWith("Not available");
        FrontendQualityTargetAccess.ModeLabel(missing.Mode).Should().Contain("context not available");

        var blocked = FrontendQualityTargetAccess.Build(Context(true, AuthenticatedTestingMethod.ManagedEdgeCdp), null, false, ManagedEdgeState.TargetTabNotInspectable, null);
        FrontendQualityTargetAccess.BrowserDomLabel(blocked).Should().Contain("enterprise browser protection");
    }

    [Fact]
    public void FromContext_UsesConfigurationAndSessionFlagOnly()
    {
        var access = FrontendQualityTargetAccess.FromContext(Context(true, AuthenticatedTestingMethod.ManagedEdgeCdp, sessionAvailable: true));

        access.Method.Should().Be(AuthenticatedTestingMethod.ManagedEdgeCdp);
        access.AuthenticatedBrowserDomAvailable.Should().BeTrue();
        access.ApiContextStatus.Should().Be(AuthenticatedApiContextStatus.NotApplicable);
        access.ManagedEdgeState.Should().BeNull();
    }
}
