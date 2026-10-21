using BirkNext.BrowserCompanion;
using BirkNext.LocalHttpsProxy;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// The engine execution planner: saved engine configuration + review scope + each engine's own generic prerequisite + resolved capabilities
/// → one plan per engine (surface, provider, decision, reason, release impact). Pure; no UI. Fixture = the reported Target Environment:
/// Static Security and Passive Performance Required (Public HTTP), Accessibility and Lighthouse Optional, the rest disabled.
/// </summary>
public sealed class FrontendQualityExecutionPlannerTests
{
    private static FrontendQualityActiveEngineSnapshot Engines(Action<FrontendAnalysisFeatureToggles>? toggles = null, Action<FrontendQualityEngineRequirementSettings>? requirements = null)
    {
        var features = new FrontendAnalysisFeatureToggles
        {
            EnableSecurityEngine = true, EnablePerformanceEngine = true, EnableAccessibilityEngine = true, EnableLighthouseEngine = true,
            EnableBrowserRuntimeEngine = false, EnablePassiveSecurityEngine = false, EnableBrowserQualityEngine = false, EnablePerformanceQualityEngine = false,
        };
        toggles?.Invoke(features);
        var policy = new FrontendQualityEngineRequirementSettings();
        requirements?.Invoke(policy);
        var selection = new ReviewEngineSelection();
        return FrontendQualityActiveEngines.Resolve(features, policy, selection, "dev");
    }

    private static FrontendQualityCapability Cap(FrontendQualityCapabilityKind kind, FrontendQualityCapabilityAvailability state, string? provider = null, string reason = "") =>
        new(kind, state, provider ?? kind.ToString(), reason is { Length: > 0 } ? reason : $"{kind} {state}");

    /// <summary>The reported situation: public frontend reachable, Local HTTPS proxy method with no authenticated context, no authenticated DOM, no Companion.</summary>
    private static List<FrontendQualityCapability> Reported() =>
    [
        Cap(FrontendQualityCapabilityKind.PublicHttp, FrontendQualityCapabilityAvailability.CheckedAtRun),
        Cap(FrontendQualityCapabilityKind.AuthenticatedHttp, FrontendQualityCapabilityAvailability.Unavailable, reason: FrontendQualityTargetAccess.ProxyContextMissingReason),
        Cap(FrontendQualityCapabilityKind.BrowserDom, FrontendQualityCapabilityAvailability.CheckedAtRun, FrontendQualityCapabilityResolver.PlaywrightProvider),
        Cap(FrontendQualityCapabilityKind.AuthenticatedBrowserDom, FrontendQualityCapabilityAvailability.Unsupported, reason: FrontendQualityTargetAccess.ProxyDomUnavailable),
        Cap(FrontendQualityCapabilityKind.CompanionLiveDom, FrontendQualityCapabilityAvailability.Unavailable, reason: "Browser Companion is not connected."),
        Cap(FrontendQualityCapabilityKind.NodeRuntime, FrontendQualityCapabilityAvailability.CheckedAtRun),
        Cap(FrontendQualityCapabilityKind.CdpTarget, FrontendQualityCapabilityAvailability.CheckedAtRun),
        Cap(FrontendQualityCapabilityKind.ContainerRuntime, FrontendQualityCapabilityAvailability.CheckedAtRun),
        Cap(FrontendQualityCapabilityKind.RecordedBrowserEvidence, FrontendQualityCapabilityAvailability.CheckedAtRun),
    ];

    private static List<FrontendQualityCapability> With(List<FrontendQualityCapability> caps, FrontendQualityCapability replacement) =>
        [.. caps.Where(c => c.Kind != replacement.Kind), replacement];

    private static FrontendQualityEnginePlan Plan(FrontendReviewAccessScope scope, FrontendQualityEngineId id, List<FrontendQualityCapability>? caps = null, FrontendQualityActiveEngineSnapshot? engines = null) =>
        FrontendQualityExecutionPlanner.Plan(engines ?? Engines(), scope, caps ?? Reported()).For(id)!;

    // ── The reported run ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(FrontendQualityEngineId.StaticSecurity)]
    [InlineData(FrontendQualityEngineId.PassivePerformance)]
    public void PublicAndAuthenticated_RequiredPublicHttpEngine_RunsOnPublic_AndMissingAuthenticatedContextDoesNotBlockIt(FrontendQualityEngineId id)
    {
        var plan = Plan(FrontendReviewAccessScope.PublicAndAuthenticated, id);
        plan.Decision.Should().Be(FrontendQualityPlanDecision.Run);
        plan.Policy.Should().Be(FrontendQualityEngineRequirement.Required);
        plan.Prerequisite.Should().Be("Public HTTP");
        plan.ReleaseImpact.Should().Be(FrontendQualityReleaseImpact.Satisfied);
        plan.For(FrontendQualityReviewSurface.Public)!.Should().Match<FrontendQualitySurfacePlan>(s => s.Decision == FrontendQualityPlanDecision.Run && s.Primary);
        var authenticated = plan.For(FrontendQualityReviewSurface.Authenticated)!;
        authenticated.Primary.Should().BeFalse("the authenticated surface only extends a Public HTTP engine; it never completes or blocks it");
        authenticated.Decision.Should().Be(FrontendQualityPlanDecision.NotAssessed);
        authenticated.Reason.Should().Be(FrontendQualityTargetAccess.ProxyContextMissingReason);
    }

    [Fact]
    public void PublicAndAuthenticated_OptionalEngines_StayOptional_AndUseTheSurfaceTheirPrerequisiteAllows()
    {
        var accessibility = Plan(FrontendReviewAccessScope.PublicAndAuthenticated, FrontendQualityEngineId.Accessibility);
        accessibility.Policy.Should().Be(FrontendQualityEngineRequirement.Optional, "scope never promotes an optional engine");
        accessibility.Decision.Should().Be(FrontendQualityPlanDecision.Run);
        accessibility.For(FrontendQualityReviewSurface.Public)!.Provider.Should().Be(FrontendQualityCapabilityResolver.PlaywrightProvider);
        accessibility.For(FrontendQualityReviewSurface.Authenticated)!.Decision.Should().Be(FrontendQualityPlanDecision.Unsupported);
        var lighthouse = Plan(FrontendReviewAccessScope.PublicAndAuthenticated, FrontendQualityEngineId.Lighthouse);
        lighthouse.Prerequisite.Should().Be("Node runtime + Chromium/CDP target");
        lighthouse.For(FrontendQualityReviewSurface.Public)!.Decision.Should().Be(FrontendQualityPlanDecision.Run);
        lighthouse.For(FrontendQualityReviewSurface.Authenticated)!.Decision.Should().Be(FrontendQualityPlanDecision.NotApplicable);
        lighthouse.ReleaseImpact.Should().Be(FrontendQualityReleaseImpact.NonBlocking);
    }

    [Fact]
    public void DisabledEngines_AreExcluded_NeverFailedBlockedOrCounted()
    {
        var plan = FrontendQualityExecutionPlanner.Plan(Engines(), FrontendReviewAccessScope.PublicAndAuthenticated, Reported());
        foreach (var id in new[] { FrontendQualityEngineId.BrowserRuntime, FrontendQualityEngineId.PassiveSecurity, FrontendQualityEngineId.BrowserQuality, FrontendQualityEngineId.PerformanceQuality })
            plan.For(id)!.Should().Match<FrontendQualityEnginePlan>(e => e.Decision == FrontendQualityPlanDecision.Excluded && !e.Applicable
                && e.ReleaseImpact == FrontendQualityReleaseImpact.NotApplicable);
    }

    // ── Hard planner/configuration inconsistency guard ───────────────────────────

    [Fact]
    public void ARequiredPublicHttpEngine_IsNeverAssignedOnlyAnAuthenticatedSurface_InAnyScopeOrCapabilityCombination()
    {
        var states = Enum.GetValues<FrontendQualityCapabilityAvailability>();
        foreach (var scope in new[] { FrontendReviewAccessScope.PublicOnly, FrontendReviewAccessScope.PublicAndAuthenticated })
        foreach (var authHttp in states)
        foreach (var authDom in states)
        {
            var caps = With(With(Reported(), Cap(FrontendQualityCapabilityKind.AuthenticatedHttp, authHttp)), Cap(FrontendQualityCapabilityKind.AuthenticatedBrowserDom, authDom));
            foreach (var id in new[] { FrontendQualityEngineId.StaticSecurity, FrontendQualityEngineId.PassivePerformance })
            {
                var plan = Plan(scope, id, caps);
                plan.Decision.Should().Be(FrontendQualityPlanDecision.Run, $"{id} needs Public HTTP, which is available ({scope}, authHttp {authHttp}, authDom {authDom})");
                plan.RunSurfaces.Should().Contain(FrontendQualityReviewSurface.Public);
                plan.ReleaseImpact.Should().NotBe(FrontendQualityReleaseImpact.Blocking);
            }
        }
    }

    // ── Scopes ──────────────────────────────────────────────────────────────────

    [Fact]
    public void PublicOnly_HasNoAuthenticatedPrerequisite_EvenWhenEveryAuthenticatedCapabilityIsMissing()
    {
        var plan = FrontendQualityExecutionPlanner.Plan(Engines(), FrontendReviewAccessScope.PublicOnly, Reported());
        plan.Engines.SelectMany(e => e.Surfaces).Should().NotContain(s => s.Surface == FrontendQualityReviewSurface.Authenticated);
        plan.Engines.Where(e => e.Applicable).Should().OnlyContain(e => e.Decision == FrontendQualityPlanDecision.Run);
    }

    [Fact]
    public void PublicOnly_OptionalDomEngine_RunsOnlyWhenPublicDomExists()
    {
        var noDom = With(Reported(), Cap(FrontendQualityCapabilityKind.BrowserDom, FrontendQualityCapabilityAvailability.Unavailable, reason: "Browser DOM was not available for this run."));
        var accessibility = Plan(FrontendReviewAccessScope.PublicOnly, FrontendQualityEngineId.Accessibility, noDom);
        accessibility.Decision.Should().Be(FrontendQualityPlanDecision.NotAssessed);
        accessibility.Reason.Should().Be("Browser DOM was not available for this run.");
        accessibility.ReleaseImpact.Should().Be(FrontendQualityReleaseImpact.NonBlocking, "an optional engine never blocks");
        Plan(FrontendReviewAccessScope.PublicOnly, FrontendQualityEngineId.StaticSecurity, noDom).Decision.Should().Be(FrontendQualityPlanDecision.Run);
    }

    [Fact]
    public void AuthenticatedOnly_PublicOnlyEngine_IsNotApplicable_NotBlocked_AndLeavesTheDenominators()
    {
        var lighthouse = Plan(FrontendReviewAccessScope.AuthenticatedOnly, FrontendQualityEngineId.Lighthouse);
        lighthouse.Decision.Should().Be(FrontendQualityPlanDecision.NotApplicable);
        lighthouse.Applicable.Should().BeFalse();
        lighthouse.Reason.Should().Be("Lighthouse has no authenticated mode.");
        lighthouse.ReleaseImpact.Should().Be(FrontendQualityReleaseImpact.NotApplicable);
    }

    [Fact]
    public void AuthenticatedOnly_RequiredHttpEngineWithoutAuthenticatedContext_IsAGenuineBlocker_WithItsReason()
    {
        var plan = Plan(FrontendReviewAccessScope.AuthenticatedOnly, FrontendQualityEngineId.StaticSecurity);
        plan.Decision.Should().Be(FrontendQualityPlanDecision.NotAssessed);
        plan.ReleaseImpact.Should().Be(FrontendQualityReleaseImpact.Blocking);
        plan.Reason.Should().Be(FrontendQualityTargetAccess.ProxyContextMissingReason);
        plan.RunSurfaces.Should().BeEmpty("a public assessment is never counted as authenticated coverage");
        var withContext = With(Reported(), Cap(FrontendQualityCapabilityKind.AuthenticatedHttp, FrontendQualityCapabilityAvailability.Available, FrontendQualityCapabilityResolver.ProxyGatewayProvider));
        Plan(FrontendReviewAccessScope.AuthenticatedOnly, FrontendQualityEngineId.StaticSecurity, withContext)
            .Should().Match<FrontendQualityEnginePlan>(e => e.Decision == FrontendQualityPlanDecision.Run && e.RunSurfaces.SequenceEqual(new[] { FrontendQualityReviewSurface.Authenticated }));
    }

    [Fact]
    public void RealBlocker_RequiredEngineWithoutPublicHttp_IsStillBlocking()
    {
        var down = With(Reported(), Cap(FrontendQualityCapabilityKind.PublicHttp, FrontendQualityCapabilityAvailability.Unavailable, reason: "Public HTTP unavailable."));
        var plan = Plan(FrontendReviewAccessScope.PublicOnly, FrontendQualityEngineId.StaticSecurity, down);
        plan.Decision.Should().Be(FrontendQualityPlanDecision.NotAssessed);
        plan.Reason.Should().Be("Public HTTP unavailable.");
        plan.ReleaseImpact.Should().Be(FrontendQualityReleaseImpact.Blocking, "the fix corrects planning; it never makes a review green");
    }

    // ── Settings drive planning exactly ──────────────────────────────────────────

    [Fact]
    public void FrontendReviewEngineSettings_DrivePlanning_PolicyAndEnablement()
    {
        var promoted = Engines(requirements: r => r.Accessibility = FrontendQualityEngineRequirement.Required);
        var accessibility = Plan(FrontendReviewAccessScope.PublicOnly, FrontendQualityEngineId.Accessibility, engines: promoted);
        accessibility.Policy.Should().Be(FrontendQualityEngineRequirement.Required);
        accessibility.ReleaseImpact.Should().Be(FrontendQualityReleaseImpact.Satisfied);
        var disabled = Engines(toggles: t => t.EnableSecurityEngine = false);
        Plan(FrontendReviewAccessScope.PublicOnly, FrontendQualityEngineId.StaticSecurity, engines: disabled).Decision.Should().Be(FrontendQualityPlanDecision.Excluded);
    }

    // ── Lighthouse: Node runtime and Chromium/CDP target are separate prerequisites ──

    [Fact]
    public void Lighthouse_NodeAndCdpTarget_AreSeparate_AndBrowserDomAloneNeverEnablesIt()
    {
        var noNode = With(Reported(), Cap(FrontendQualityCapabilityKind.NodeRuntime, FrontendQualityCapabilityAvailability.Unavailable, reason: FrontendQualityRuntime.NodeUnavailable));
        Plan(FrontendReviewAccessScope.PublicOnly, FrontendQualityEngineId.Lighthouse, noNode).Reason.Should().Be(FrontendQualityRuntime.NodeUnavailable);
        var noCdp = With(With(Reported(), Cap(FrontendQualityCapabilityKind.NodeRuntime, FrontendQualityCapabilityAvailability.Available)),
            Cap(FrontendQualityCapabilityKind.CdpTarget, FrontendQualityCapabilityAvailability.Unavailable, reason: FrontendQualityRuntime.CdpUnavailable));
        var plan = Plan(FrontendReviewAccessScope.PublicOnly, FrontendQualityEngineId.Lighthouse, With(noCdp, Cap(FrontendQualityCapabilityKind.BrowserDom, FrontendQualityCapabilityAvailability.Available)));
        plan.Decision.Should().Be(FrontendQualityPlanDecision.NotAssessed, "a DOM is not a DevTools execution target");
        plan.Reason.Should().Be(FrontendQualityRuntime.CdpUnavailable).And.NotContain("Browser DOM");
        Plan(FrontendReviewAccessScope.PublicOnly, FrontendQualityEngineId.Accessibility, noCdp).Decision.Should().Be(FrontendQualityPlanDecision.Run, "missing CDP never stops a DOM engine");
    }

    [Theory]
    [InlineData("Microsoft Edge 130.0.2849.56", true, true)]
    [InlineData("HeadlessChrome 130.0.6723.31", true, true)]
    [InlineData("Chromium 130.0.6723.31", true, true)]
    [InlineData("Microsoft Edge 130", false, false)]
    [InlineData("Firefox 131", true, false)]
    public void ChromiumCompatibility_IsTheProtocol_NotTheProductName(string browser, bool speaksCdp, bool expected) =>
        FrontendQualityCapabilityResolver.IsChromiumCompatible(browser, speaksCdp).Should().Be(expected);

    [Fact]
    public void RuntimeFacts_FromReadiness_SeparateNodeAndTarget_AndAcceptEdge()
    {
        var facts = FrontendQualityRuntime.From(new FrontendQualityEngineReadinessReportDto
        {
            IsAvailable = true, RuntimeComponents =
            [
                new() { Kind = "NodeRuntime", Available = true, Provider = "Node.js", Detail = "Node.js v24.16.0" },
                new() { Kind = "CdpTarget", Available = true, Provider = "Microsoft Edge (dedicated profile)", Detail = "Microsoft Edge 130.0" },
            ],
        })!;
        facts.NodeRuntimeAvailable.Should().BeTrue();
        facts.CdpTargetAvailable.Should().BeTrue("Edge is a Chromium/CDP target");
        FrontendQualityRuntime.LighthouseReason(new(NodeRuntimeAvailable: false, CdpTargetAvailable: true)).Should().Be(FrontendQualityRuntime.NodeUnavailable);
        FrontendQualityRuntime.LighthouseReason(new(NodeRuntimeAvailable: true, CdpTargetAvailable: false)).Should().Be(FrontendQualityRuntime.CdpUnavailable);
        FrontendQualityRuntime.From(new FrontendQualityEngineReadinessReportDto { IsAvailable = false }).Should().BeNull("an older backend reports no components");
    }

    // ── Browser Companion as a Browser DOM provider ──────────────────────────────

    [Fact]
    public void CompanionLiveDom_GivesAccessibilityADom_AsItsOwnSurface_NeverAsAuthenticatedCoverage()
    {
        var live = With(Reported(), Cap(FrontendQualityCapabilityKind.CompanionLiveDom, FrontendQualityCapabilityAvailability.Available, FrontendQualityCapabilityResolver.CompanionProvider));
        var plan = Plan(FrontendReviewAccessScope.AuthenticatedOnly, FrontendQualityEngineId.Accessibility, live);
        plan.Decision.Should().Be(FrontendQualityPlanDecision.Run);
        plan.For(FrontendQualityReviewSurface.Authenticated)!.Decision.Should().Be(FrontendQualityPlanDecision.Unsupported, "the proxy method still cannot give an authenticated DOM");
        var companion = plan.For(FrontendQualityReviewSurface.CompanionPage)!;
        companion.Decision.Should().Be(FrontendQualityPlanDecision.Run);
        companion.Provider.Should().Be(FrontendQualityCapabilityResolver.CompanionProvider);
        companion.Reason.Should().Contain(FrontendQualityExecutionPlanner.CompanionPageNote);
        plan.Reason.Should().NotContain("Browser DOM unavailable");
        Plan(FrontendReviewAccessScope.AuthenticatedOnly, FrontendQualityEngineId.Lighthouse, live).Decision.Should().Be(FrontendQualityPlanDecision.NotApplicable,
            "a Companion DOM is not a Chromium/CDP target");
        Plan(FrontendReviewAccessScope.AuthenticatedOnly, FrontendQualityEngineId.Accessibility).Decision.Should().Be(FrontendQualityPlanDecision.Unsupported,
            "without a live Companion page nothing provides the authenticated DOM");
    }

    [Fact]
    public void CapabilityResolver_NamesProviders_AndCompanionNeverClaimsCdp()
    {
        var profile = new FrontendAnalysisProfile { Id = "dev", TargetUrl = "https://app.example.test/" };
        profile.Authentication.AuthenticatedTestingMethod = AuthenticatedTestingMethod.LocalHttpsProxy;
        var context = new FrontendAnalysisContext { ActiveProfile = profile, TargetUrl = profile.TargetUrl, AuthenticationType = FrontendAuthenticationType.MicrosoftEntraId };
        var access = new FrontendQualityTargetAccessContext { Method = AuthenticatedTestingMethod.LocalHttpsProxy };
        var live = new BrowserCompanionLiveSession
        {
            ExtensionConnected = true,
            LivePages = [new() { PageId = "t1-a", Origin = "https://app.example.test", Route = "/admin/roles", RegisteredAt = DateTimeOffset.UtcNow }],
        };
        var caps = FrontendQualityCapabilityResolver.Resolve(context, access, live, ["https://app.example.test"]);
        caps.Single(c => c.Kind == FrontendQualityCapabilityKind.CompanionLiveDom).Should().Match<FrontendQualityCapability>(c =>
            c.State == FrontendQualityCapabilityAvailability.Available && c.Provider == FrontendQualityCapabilityResolver.CompanionProvider && c.Reason.Contains("/admin/roles"));
        caps.Single(c => c.Kind == FrontendQualityCapabilityKind.AuthenticatedBrowserDom).State.Should().Be(FrontendQualityCapabilityAvailability.Unsupported);
        caps.Single(c => c.Kind == FrontendQualityCapabilityKind.CdpTarget).Provider.Should().Be(FrontendQualityCapabilityResolver.BundledChromiumProvider);
        FrontendQualityCapabilityResolver.Resolve(context, access, live, ["https://other.example.test"])
            .Single(c => c.Kind == FrontendQualityCapabilityKind.CompanionLiveDom).State.Should().Be(FrontendQualityCapabilityAvailability.Unavailable, "only approved pages count");
    }
}
