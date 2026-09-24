using BirkNext.BrowserCompanion;
using BirkNext.LocalHttpsProxy;
using BirkNext.ManagedEdge;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// The landing presentation is a pure projection of authoritative state: activation snapshot, backend capability model,
/// target access decision and Browser Companion state. It never invents a state and keeps every typed state distinct.
/// </summary>
public sealed class FrontendQualityLandingPresentationTests
{
    private const string Url = "https://m2lbdev.example.test/";

    private static FrontendAnalysisContext Context(Action<FrontendAnalysisFeatureToggles>? toggles = null, bool requiresAuth = false,
        AuthenticatedTestingMethod method = AuthenticatedTestingMethod.ManagedEdgeCdp, string url = Url)
    {
        var profile = new FrontendAnalysisProfile { Id = "dev", Name = "M2LB DEV", EnvironmentType = FrontendEnvironmentType.Development, TargetUrl = url };
        profile.Authentication.RequiresAuthentication = requiresAuth;
        profile.Authentication.AuthenticationType = requiresAuth ? FrontendAuthenticationType.MicrosoftEntraId : FrontendAuthenticationType.None;
        profile.Authentication.AuthenticatedTestingMethod = method;
        toggles?.Invoke(profile.Features);
        return new FrontendAnalysisContext
        {
            ActiveProfile = profile, TargetUrl = url, FeatureToggles = profile.Features, EngineRequirements = profile.EngineRequirements,
            ReviewEngineSelection = profile.ReviewEngineSelection, RequiresAuthentication = requiresAuth, AuthenticationType = profile.Authentication.AuthenticationType,
        };
    }

    private static FrontendQualityEngineStatusDto Engine(FrontendQualityEngineIdDto id, bool layer1 = true, bool layer2 = true, bool? ready = true, string? reason = null, bool authSupported = true) => new()
    {
        EngineId = id, DisplayName = id.ToString(), Layer1Allowed = layer1, Layer2Enabled = layer2, AuthModeSupported = authSupported,
        Available = layer1 && layer2 && ready != false,
        Layer3Readiness = ready is null ? null : new FrontendQualityEngineReadinessDto { EngineId = id, IsAvailable = ready.Value, StatusReason = reason },
    };

    private static FrontendQualityEngineStatusReportDto Status(params FrontendQualityEngineStatusDto[] engines) => new() { Engines = engines.ToList() };

    private static IReadOnlyList<FrontendQualityCapabilityRow> Capabilities(FrontendAnalysisContext context, FrontendQualityEngineStatusReportDto? status = null,
        bool statusPending = false, bool readinessPending = false, FrontendQualityTargetAccessContext? access = null, BrowserCompanionState? companion = null) =>
        FrontendQualityLandingPresentation.Capabilities(context, FrontendQualityActiveEngines.Resolve(context), status, statusPending, readinessPending,
            access ?? FrontendQualityTargetAccess.FromContext(context), companion);

    private static FrontendQualityCapabilityRow Row(IEnumerable<FrontendQualityCapabilityRow> rows, FrontendQualityEngineId id) => rows.Single(r => r.EngineId == id);

    // ── Readiness ────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Readiness_LoadingWhenNoContext()
    {
        var readiness = FrontendQualityLandingPresentation.Readiness(null, null, [], false);
        readiness.Level.Should().Be(FrontendQualityReviewReadinessLevel.Loading);
        readiness.CanRun.Should().BeFalse();
    }

    [Fact]
    public void Readiness_BlockedWhenNoActiveTarget_UsesTheErrorAsTitle()
    {
        var readiness = FrontendQualityLandingPresentation.Readiness(new FrontendAnalysisContext { ActiveTargetError = "No active Target Environment" }, null, [], false);
        readiness.Level.Should().Be(FrontendQualityReviewReadinessLevel.Blocked);
        readiness.Title.Should().Be("No active Target Environment");
        readiness.ActionText.Should().Be("Open Target Environments");
        readiness.CanRun.Should().BeFalse();
    }

    [Fact]
    public void Readiness_BlockedWhenTargetUrlMissing_ListsTheMissingRequirement()
    {
        var context = Context(url: "");
        var readiness = FrontendQualityLandingPresentation.Readiness(context, FrontendQualityActiveEngines.Resolve(context), [], false);
        readiness.Level.Should().Be(FrontendQualityReviewReadinessLevel.Blocked);
        readiness.Title.Should().Be(FrontendQualityLandingPresentation.BlockedTitle);
        readiness.Details.Should().ContainSingle().Which.Should().Be("Frontend target URL");
    }

    [Fact]
    public void Readiness_BlockedWhenNoActiveEngines()
    {
        var context = Context(t => { foreach (var set in Setters) set(t, false); });
        var readiness = FrontendQualityLandingPresentation.Readiness(context, FrontendQualityActiveEngines.Resolve(context), [], false);
        readiness.Level.Should().Be(FrontendQualityReviewReadinessLevel.Blocked);
        readiness.Title.Should().Be(FrontendQualityActiveEngines.NoActiveEnginesMessage);
    }

    [Fact]
    public void Readiness_CheckingWhileStatusPending_CannotRunYet()
    {
        var context = Context();
        var rows = Capabilities(context, statusPending: true);
        var readiness = FrontendQualityLandingPresentation.Readiness(context, FrontendQualityActiveEngines.Resolve(context), rows, statusPending: true);
        readiness.Level.Should().Be(FrontendQualityReviewReadinessLevel.Checking);
        readiness.Message.Should().StartWith("Checking 3 active engines");
        readiness.CanRun.Should().BeFalse();
    }

    [Fact]
    public void Readiness_ReadyOnlyWhenEverythingEnabledIsAvailable()
    {
        var context = Context();
        var rows = Capabilities(context, Status(Engine(FrontendQualityEngineIdDto.Accessibility), Engine(FrontendQualityEngineIdDto.Lighthouse), Engine(FrontendQualityEngineIdDto.PassiveSecurity)));
        var readiness = FrontendQualityLandingPresentation.Readiness(context, FrontendQualityActiveEngines.Resolve(context), rows, false);
        readiness.Level.Should().Be(FrontendQualityReviewReadinessLevel.Ready);
        readiness.Title.Should().Be(FrontendQualityLandingPresentation.ReadyTitle);
        readiness.CanRun.Should().BeTrue();
    }

    [Fact]
    public void Readiness_LimitedWhenOptionalEngineUnavailable_ParsedConfigIsNotGreen()
    {
        var context = Context();
        var rows = Capabilities(context, Status(Engine(FrontendQualityEngineIdDto.Accessibility), Engine(FrontendQualityEngineIdDto.Lighthouse, ready: false, reason: "not installed"), Engine(FrontendQualityEngineIdDto.PassiveSecurity)));
        var readiness = FrontendQualityLandingPresentation.Readiness(context, FrontendQualityActiveEngines.Resolve(context), rows, false);
        readiness.Level.Should().Be(FrontendQualityReviewReadinessLevel.Limited);
        // 2. The capability is NAMED in the one line shown before the decision to run. Finding out WHICH capability
        // is unavailable must not require expanding a disclosure and scrolling.
        readiness.Message.Should().StartWith("Lighthouse is currently unavailable.");
        readiness.Message.Should().NotContain("1 enabled optional capability");
        // 5, 35. Optional depth, not a blocked review.
        readiness.Message.Should().NotContainAny("cannot run", "cannot start");
        // What cannot run, then what is switched off — listed apart, never counted together.
        readiness.Details.Should().Contain("Lighthouse: Unavailable");
        readiness.Details.Should().OnlyContain(d => d.Contains("Unavailable") || d.Contains("Disabled"));
        readiness.CanRun.Should().BeTrue();
    }

    // 1, 3. A switched-off engine never appears as an unavailable capability, nor in the count of them.
    [Fact]
    public void Readiness_DoesNotCountOrNameADisabledEngineAmongTheUnavailable()
    {
        // Browser Runtime off (the factory default), Lighthouse enabled but not installed.
        var context = Context(t => t.EnableBrowserRuntimeEngine = false);
        var rows = Capabilities(context, Status(Engine(FrontendQualityEngineIdDto.Accessibility), Engine(FrontendQualityEngineIdDto.Lighthouse, ready: false, reason: "not installed"), Engine(FrontendQualityEngineIdDto.PassiveSecurity)));

        var readiness = FrontendQualityLandingPresentation.Readiness(context, FrontendQualityActiveEngines.Resolve(context), rows, false);

        readiness.Level.Should().Be(FrontendQualityReviewReadinessLevel.Limited);
        readiness.Message.Should().StartWith("Lighthouse is currently unavailable.");
        readiness.Message.Should().NotContain("Browser Runtime");
        readiness.Message.Should().NotContain("2 optional capabilities");
        // It is still listed, as what it is: switched off, not broken.
        readiness.Details.Should().Contain("Browser Runtime: Disabled");
        readiness.CanRun.Should().BeTrue();
    }

    [Fact]
    public void Readiness_LimitedNamesRequiredEnginesThatCannotRun()
    {
        var context = Context(requiresAuth: true, method: AuthenticatedTestingMethod.ManagedEdgeCdp);
        var access = FrontendQualityTargetAccess.Build(context, null, false, ManagedEdgeState.TargetTabNotInspectable, null);
        var rows = Capabilities(context, Status(Engine(FrontendQualityEngineIdDto.Accessibility), Engine(FrontendQualityEngineIdDto.Lighthouse), Engine(FrontendQualityEngineIdDto.PassiveSecurity)), access: access);
        // Public-HTTP engines (Static Security, Passive Performance, Passive Security) stay available for a protected app because they
        // review the public shell; the DOM engine is blocked by browser protection and Lighthouse has no authenticated mode.
        var readiness = FrontendQualityLandingPresentation.Readiness(context, FrontendQualityActiveEngines.Resolve(context), rows, false);
        readiness.Level.Should().Be(FrontendQualityReviewReadinessLevel.Limited);
        // 2. Several unavailable: named, grouped by state, so the reader learns which ones without expanding.
        readiness.Message.Should().Be("Accessibility is currently unavailable. Lighthouse is not supported for this target. All required capabilities are available, so the review can run.");
        readiness.Message.Should().Contain("Accessibility").And.Contain("Lighthouse");
        readiness.Details.Should().Contain(["Accessibility: Unavailable", "Lighthouse: Not supported for this target"]);
        readiness.Details.Where(d => !d.Contains("Disabled")).Should().HaveCount(2);
        Row(rows, FrontendQualityEngineId.StaticSecurity).IsAvailable.Should().BeTrue();
        Row(rows, FrontendQualityEngineId.PassivePerformance).IsAvailable.Should().BeTrue();
        Row(rows, FrontendQualityEngineId.PassiveSecurity).State.Should().Be(FrontendQualityCapabilityState.Ready, "ZAP passively scans the public shell");

        var requiredBlocked = FrontendQualityLandingPresentation.Readiness(context, FrontendQualityActiveEngines.Resolve(context),
            rows.Select(r => r.EngineId == FrontendQualityEngineId.StaticSecurity ? r with { State = FrontendQualityCapabilityState.Unavailable } : r).ToList(), false);
        requiredBlocked.Message.Should().Be("Static Security and Accessibility are currently unavailable. Lighthouse is not supported for this target. Required coverage will stay incomplete: Static Security.");
    }

    [Fact]
    public void Readiness_ValidationWarningsMakeItLimited()
    {
        var context = Context(t => { foreach (var set in Setters) set(t, false); t.EnableSecurityEngine = true; t.EnablePerformanceEngine = true; });
        context.ValidationWarnings = ["Target URL uses http, not https."];
        var rows = Capabilities(context);
        var readiness = FrontendQualityLandingPresentation.Readiness(context, FrontendQualityActiveEngines.Resolve(context), rows, false);
        readiness.Level.Should().Be(FrontendQualityReviewReadinessLevel.Limited);
        readiness.Details.Should().Contain("Target URL uses http, not https.");
    }

    private static readonly Action<FrontendAnalysisFeatureToggles, bool>[] Setters =
    [
        (t, v) => t.EnableSecurityEngine = v, (t, v) => t.EnablePerformanceEngine = v, (t, v) => t.EnableBrowserRuntimeEngine = v, (t, v) => t.EnableAccessibilityEngine = v,
        (t, v) => t.EnableLighthouseEngine = v, (t, v) => t.EnablePassiveSecurityEngine = v, (t, v) => t.EnableBrowserQualityEngine = v, (t, v) => t.EnablePerformanceQualityEngine = v,
    ];

    // ── Capabilities ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Capabilities_OrderRequiredFirst_AndReflectSavedActivation()
    {
        var context = Context();
        context.ReviewEngineSelection.AccessibilitySelected = false;
        var rows = Capabilities(context, Status());

        rows.Take(2).Select(r => r.EngineId).Should().Equal(FrontendQualityEngineId.StaticSecurity, FrontendQualityEngineId.PassivePerformance);
        rows.Should().HaveCount(8);
        Row(rows, FrontendQualityEngineId.BrowserRuntime).State.Should().Be(FrontendQualityCapabilityState.Disabled);
        Row(rows, FrontendQualityEngineId.BrowserRuntime).ActionText.Should().Be("Edit engines");
        Row(rows, FrontendQualityEngineId.BrowserRuntime).ActionHref.Should().Be(FrontendQualityLandingPresentation.FrontendReviewEnginesHref, "a disabled engine is fixed where activation is saved");
        Row(rows, FrontendQualityEngineId.Accessibility).State.Should().Be(FrontendQualityCapabilityState.NotSelected);
        Row(rows, FrontendQualityEngineId.Accessibility).SelectableEngineId.Should().Be(FrontendQualityEngineIdDto.Accessibility, "a deselected enabled engine can be re-included");
        Row(rows, FrontendQualityEngineId.BrowserRuntime).SelectableEngineId.Should().BeNull("a disabled engine is never selectable");
        Row(rows, FrontendQualityEngineId.StaticSecurity).SelectableEngineId.Should().BeNull("HTTP engines have no per-review opt-out");
    }

    [Fact]
    public void Capabilities_LayerStates_MapToDistinctTypedStates()
    {
        var context = Context();
        var rows = Capabilities(context, Status(
            Engine(FrontendQualityEngineIdDto.Accessibility, layer1: false),
            Engine(FrontendQualityEngineIdDto.Lighthouse, layer2: false),
            Engine(FrontendQualityEngineIdDto.PassiveSecurity, ready: false, reason: "Container runtime is unavailable.")));

        var accessibility = Row(rows, FrontendQualityEngineId.Accessibility);
        accessibility.State.Should().Be(FrontendQualityCapabilityState.Unavailable);
        accessibility.Summary.Should().Be("The Accessibility engine is not available in this environment.");
        accessibility.TechnicalReason.Should().Be("Blocked by deployment policy.");
        accessibility.SelectableEngineId.Should().BeNull("a policy-blocked engine offers no include checkbox");

        var lighthouse = Row(rows, FrontendQualityEngineId.Lighthouse);
        lighthouse.State.Should().Be(FrontendQualityCapabilityState.DisabledInSystemSettings);
        lighthouse.ActionHref.Should().Be(FrontendQualityLandingPresentation.SystemSettingsHref);

        var passive = Row(rows, FrontendQualityEngineId.PassiveSecurity);
        passive.State.Should().Be(FrontendQualityCapabilityState.Unavailable);
        passive.Summary.Should().Be("The Passive Security engine is not available in this environment.");
        passive.TechnicalReason.Should().Be("Container runtime is unavailable.");
    }

    [Fact]
    public void Capabilities_ReadyOnlyWhenRuntimeConfirmed_EnabledWhileReadinessPending()
    {
        var context = Context();
        var ready = Capabilities(context, Status(Engine(FrontendQualityEngineIdDto.Accessibility), Engine(FrontendQualityEngineIdDto.Lighthouse), Engine(FrontendQualityEngineIdDto.PassiveSecurity)));
        Row(ready, FrontendQualityEngineId.Accessibility).State.Should().Be(FrontendQualityCapabilityState.Ready);
        Row(ready, FrontendQualityEngineId.StaticSecurity).State.Should().Be(FrontendQualityCapabilityState.Enabled, "HTTP engines are validated at run time, never claimed Ready");

        var pending = Capabilities(context, Status(Engine(FrontendQualityEngineIdDto.Accessibility, ready: null)), readinessPending: true);
        Row(pending, FrontendQualityEngineId.Accessibility).State.Should().Be(FrontendQualityCapabilityState.Enabled);
        Row(pending, FrontendQualityEngineId.Accessibility).Summary.Should().Contain("being checked");

        var checking = Capabilities(context, null, statusPending: true);
        Row(checking, FrontendQualityEngineId.Accessibility).State.Should().Be(FrontendQualityCapabilityState.Checking);

        var failed = Capabilities(context, null);
        Row(failed, FrontendQualityEngineId.Accessibility).State.Should().Be(FrontendQualityCapabilityState.Enabled);
        Row(failed, FrontendQualityEngineId.Accessibility).Summary.Should().Contain("could not be checked");
    }

    [Fact]
    public void Capabilities_AccessDecisions_ManagedEdgeWithoutSession()
    {
        var context = Context(requiresAuth: true);
        var rows = Capabilities(context, Status(Engine(FrontendQualityEngineIdDto.Accessibility), Engine(FrontendQualityEngineIdDto.Lighthouse), Engine(FrontendQualityEngineIdDto.PassiveSecurity)));

        var accessibility = Row(rows, FrontendQualityEngineId.Accessibility);
        accessibility.State.Should().Be(FrontendQualityCapabilityState.RequiresBrowserSession);
        accessibility.ActionText.Should().Be("Sign in for review");
        accessibility.TechnicalReason.Should().Be(FrontendQualityTargetAccess.SessionRequiredReason);
        Row(rows, FrontendQualityEngineId.Lighthouse).State.Should().Be(FrontendQualityCapabilityState.NotSupported);
        Row(rows, FrontendQualityEngineId.StaticSecurity).State.Should().Be(FrontendQualityCapabilityState.Enabled);
        Row(rows, FrontendQualityEngineId.StaticSecurity).Summary.Should().Contain("public frontend");
    }

    [Fact]
    public void Capabilities_AccessDecisions_ProxyAndManualMethods()
    {
        var proxyContext = Context(requiresAuth: true, method: AuthenticatedTestingMethod.LocalHttpsProxy);
        var proxyRows = Capabilities(proxyContext, Status(Engine(FrontendQualityEngineIdDto.Accessibility)),
            access: FrontendQualityTargetAccess.Build(proxyContext, new AuthenticatedReviewCapabilities { Method = AuthenticatedTestingMethod.LocalHttpsProxy, ContextStatus = AuthenticatedApiContextStatus.Available, AuthenticatedApi = true }, false, null, LocalHttpsProxyState.Ready));
        Row(proxyRows, FrontendQualityEngineId.Accessibility).State.Should().Be(FrontendQualityCapabilityState.NotSupported);
        Row(proxyRows, FrontendQualityEngineId.Accessibility).TechnicalReason.Should().Be(FrontendQualityTargetAccess.ProxyDomUnavailable);
        Row(proxyRows, FrontendQualityEngineId.StaticSecurity).State.Should().Be(FrontendQualityCapabilityState.Enabled);
        Row(proxyRows, FrontendQualityEngineId.StaticSecurity).TechnicalReason.Should().Contain("Authenticated HTTP (Local HTTPS Proxy)");

        var manualContext = Context(requiresAuth: true, method: AuthenticatedTestingMethod.ManualOnly);
        var manualRows = Capabilities(manualContext, Status(Engine(FrontendQualityEngineIdDto.Accessibility)));
        Row(manualRows, FrontendQualityEngineId.Accessibility).State.Should().Be(FrontendQualityCapabilityState.NotSupported);
        Row(manualRows, FrontendQualityEngineId.Accessibility).TechnicalReason.Should().Be(FrontendQualityTargetAccess.ManualOnlyReason);
    }

    [Fact]
    public void Capabilities_EnterpriseBlocked_IsUnavailableNotAnApplicationDefect()
    {
        var context = Context(requiresAuth: true);
        var access = FrontendQualityTargetAccess.Build(context, null, false, ManagedEdgeState.TargetTabNotInspectable, null);
        var rows = Capabilities(context, Status(Engine(FrontendQualityEngineIdDto.Accessibility)), access: access);
        var accessibility = Row(rows, FrontendQualityEngineId.Accessibility);
        accessibility.State.Should().Be(FrontendQualityCapabilityState.Unavailable);
        accessibility.Summary.Should().Contain("enterprise browser protection").And.NotContainAny("error", "failed");
        accessibility.TechnicalReason.Should().Contain(FrontendQualityTargetAccess.EnterpriseBlockedReason);
    }

    [Theory]
    [InlineData(BrowserCompanionState.Connected, FrontendQualityCapabilityState.Ready)]
    [InlineData(BrowserCompanionState.Disconnected, FrontendQualityCapabilityState.RequiresBrowserSession)]
    [InlineData(BrowserCompanionState.Expired, FrontendQualityCapabilityState.RequiresBrowserSession)]
    [InlineData(BrowserCompanionState.NotPaired, FrontendQualityCapabilityState.NotConfigured)]
    [InlineData(BrowserCompanionState.PairingPending, FrontendQualityCapabilityState.NotConfigured)]
    [InlineData(null, FrontendQualityCapabilityState.Enabled)]
    public void Capabilities_CompanionEngines_FollowTheCompanionSession(BrowserCompanionState? companion, FrontendQualityCapabilityState expected)
    {
        var context = Context(t => { t.EnableBrowserQualityEngine = true; t.EnablePerformanceQualityEngine = true; });
        var rows = Capabilities(context, Status(), companion: companion);
        Row(rows, FrontendQualityEngineId.BrowserQuality).State.Should().Be(expected);
        Row(rows, FrontendQualityEngineId.PerformanceQuality).State.Should().Be(expected);
    }

    [Fact]
    public void CapabilityStateLabels_AreDistinctForEveryTypedState()
    {
        var labels = Enum.GetValues<FrontendQualityCapabilityState>().Select(FrontendQualityCapabilityStates.Label).ToList();
        labels.Should().OnlyHaveUniqueItems();
        labels.Should().NotContain(l => l.Contains("inactive", StringComparison.OrdinalIgnoreCase));
        // 17. Runtime vocabulary only: "Enabled" is the CONFIGURATION word and is shown by its own chip, so no runtime
        // state may borrow it. An engine whose readiness was never probed is "Available", not "Enabled".
        labels.Should().Contain(["Available", "Disabled", "Unavailable", "Not configured", "Ready", "Requires browser session", "Not selected", "Disabled in System Settings"]);
        FrontendQualityCapabilityStates.Label(FrontendQualityCapabilityState.Enabled).Should().Be("Available");
        labels.Should().NotContain("Enabled");
        FrontendQualityCapabilityStates.IsActive(FrontendQualityCapabilityState.Disabled).Should().BeFalse();
        FrontendQualityCapabilityStates.IsActive(FrontendQualityCapabilityState.NotSelected).Should().BeFalse();
        FrontendQualityCapabilityStates.IsAvailable(FrontendQualityCapabilityState.Unavailable).Should().BeFalse();
        FrontendQualityCapabilityStates.IsAvailable(FrontendQualityCapabilityState.Checking).Should().BeTrue();
    }

    // ── Dimensions ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Dimensions_SixCards_InCategoryOrder_WithTypedStates()
    {
        var context = Context(t => { foreach (var set in Setters) set(t, false); t.EnableSecurityEngine = true; t.EnablePerformanceEngine = true; });
        var cards = FrontendQualityLandingPresentation.Dimensions(Capabilities(context));

        cards.Select(c => c.Title).Should().Equal("Performance", "Security", "Accessibility", "Standards Compliance", "Blazor / WASM", "QA Readiness");
        cards.Single(c => c.Category == FrontendQualityCategory.Security).State.Should().Be(FrontendQualityDimensionState.Included);
        // Accessibility is scoped by its profile, so disabling both its engines leaves it in the review on reduced evidence.
        cards.Single(c => c.Category == FrontendQualityCategory.Accessibility).State.Should().Be(FrontendQualityDimensionState.PartialEvidence);
        cards.Single(c => c.Category == FrontendQualityCategory.Readiness).State.Should().Be(FrontendQualityDimensionState.Included);
        cards.Should().OnlyContain(c => c.Purpose.Length > 0);
        cards.Select(c => c.Purpose).Should().NotContain(p => p.Contains("CDP") || p.Contains("proxy") || p.Contains("Playwright"), "no implementation vocabulary on the overview");
    }

    [Fact]
    public void Dimensions_UnavailableEngineBecomesALimitation_NotAnUnavailableDimension_WhenOthersRemain()
    {
        var context = Context();
        var rows = Capabilities(context, Status(Engine(FrontendQualityEngineIdDto.Accessibility), Engine(FrontendQualityEngineIdDto.Lighthouse, ready: false), Engine(FrontendQualityEngineIdDto.PassiveSecurity, layer1: false)));
        var cards = FrontendQualityLandingPresentation.Dimensions(rows);

        var performance = cards.Single(c => c.Category == FrontendQualityCategory.Performance);
        performance.State.Should().Be(FrontendQualityDimensionState.Limited);
        performance.Limitation.Should().Be("Passive Performance is included. Lighthouse is unavailable.");
        // 5, 12, 36. Limited, and the baseline that still runs is named FIRST — the old line led with what was
        // missing, which beside the word "Limited" read as "security cannot be reviewed".
        var security = cards.Single(c => c.Category == FrontendQualityCategory.Security);
        security.State.Should().Be(FrontendQualityDimensionState.Limited);
        security.Limitation.Should().StartWith("Static Security is included.");
        security.Limitation.Should().Contain("Passive Security is unavailable");
        security.Limitation.Should().NotContainAny("cannot be reviewed", "Unavailable.", "Failed");

        // 6, 13, 37. Included with a separate manual-assessment requirement, phrased so it is not a fault of automation.
        var accessibility = cards.Single(c => c.Category == FrontendQualityCategory.Accessibility);
        accessibility.State.Should().Be(FrontendQualityDimensionState.Included);
        accessibility.ManualAssessmentRequired.Should().BeTrue();
        accessibility.Limitation.Should().Be("Automated accessibility checks are included.");
        accessibility.Limitation.Should().NotContain("partial");
    }

    [Fact]
    public void Dimensions_EveryAccessibilityEngineUnavailable_StillLeavesTheDomainInTheReview()
    {
        // The engines are the automated evidence, not the assessment. With none of them available the domain keeps
        // its profile scope and its manual requirement; it must never read as an unavailable domain.
        var context = Context(t => t.EnableBrowserQualityEngine = false);
        var rows = Capabilities(context, Status(Engine(FrontendQualityEngineIdDto.Accessibility, layer1: false), Engine(FrontendQualityEngineIdDto.Lighthouse), Engine(FrontendQualityEngineIdDto.PassiveSecurity)));
        var accessibility = FrontendQualityLandingPresentation.Dimensions(rows).Single(c => c.Category == FrontendQualityCategory.Accessibility);
        accessibility.State.Should().Be(FrontendQualityDimensionState.PartialEvidence);
        accessibility.ManualAssessmentRequired.Should().BeTrue();
        accessibility.ManualAssessmentRequired.Should().BeTrue();
        accessibility.Limitation.Should().NotContain("Accessibility unavailable");
    }

    // ── Coverage ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Coverage_PublicTarget()
    {
        var rows = FrontendQualityLandingPresentation.Coverage(FrontendQualityTargetAccess.FromContext(Context()), null);
        rows.Select(r => r.Label).Should().Equal("Public frontend", "Authenticated application", "Browser-rendered DOM", "Authenticated API traffic", "Automatic engines");
        rows.Select(r => r.State).Should().Equal(FrontendQualityCoverageState.Available, FrontendQualityCoverageState.NotRequired, FrontendQualityCoverageState.Available, FrontendQualityCoverageState.NotRequired, FrontendQualityCoverageState.Available);
        rows[2].Detail.Should().Be("Available for public pages.");
    }

    [Fact]
    public void Coverage_ManagedEdge_WithAndWithoutSession()
    {
        var context = Context(requiresAuth: true);
        var without = FrontendQualityLandingPresentation.Coverage(FrontendQualityTargetAccess.Build(context, null, false, null, null), null);
        without.Single(r => r.Label == "Authenticated application").State.Should().Be(FrontendQualityCoverageState.NotAvailable);
        without.Single(r => r.Label == "Browser-rendered DOM").Detail.Should().Contain("Sign in for review");
        without.Single(r => r.Label == "Automatic engines").State.Should().Be(FrontendQualityCoverageState.PublicOnly);

        var with = FrontendQualityLandingPresentation.Coverage(FrontendQualityTargetAccess.Build(context, null, true, ManagedEdgeState.ConnectedAuthenticated, null), null);
        with.Single(r => r.Label == "Authenticated application").State.Should().Be(FrontendQualityCoverageState.Available);
        with.Single(r => r.Label == "Browser-rendered DOM").State.Should().Be(FrontendQualityCoverageState.Available);
        with.Single(r => r.Label == "Authenticated API traffic").State.Should().Be(FrontendQualityCoverageState.NotAvailable, "the browser method provides no authenticated API context");
        with.Single(r => r.Label == "Automatic engines").State.Should().Be(FrontendQualityCoverageState.Available);
    }

    [Fact]
    public void Coverage_Proxy_ApiAvailableNeverGrantsDom_ExpiredIsExplained()
    {
        var context = Context(requiresAuth: true, method: AuthenticatedTestingMethod.LocalHttpsProxy);
        var caps = new AuthenticatedReviewCapabilities { Method = AuthenticatedTestingMethod.LocalHttpsProxy, ContextStatus = AuthenticatedApiContextStatus.Available, AuthenticatedApi = true, AuthenticatedRest = true, AuthenticatedGraphQlQuery = true };
        var available = FrontendQualityLandingPresentation.Coverage(FrontendQualityTargetAccess.Build(context, caps, false, null, LocalHttpsProxyState.Ready), caps);
        available.Single(r => r.Label == "Authenticated API traffic").State.Should().Be(FrontendQualityCoverageState.Available);
        available.Single(r => r.Label == "Authenticated API traffic").Detail.Should().Contain("REST and GraphQL query traffic observed");
        available.Single(r => r.Label == "Browser-rendered DOM").State.Should().Be(FrontendQualityCoverageState.NotAvailable);

        var expired = new AuthenticatedReviewCapabilities { Method = AuthenticatedTestingMethod.LocalHttpsProxy, ContextStatus = AuthenticatedApiContextStatus.Expired };
        var expiredRows = FrontendQualityLandingPresentation.Coverage(FrontendQualityTargetAccess.Build(context, expired, false, null, LocalHttpsProxyState.Listening), expired);
        expiredRows.Single(r => r.Label == "Authenticated API traffic").Detail.Should().Contain("expired");
        expiredRows.Single(r => r.Label == "Automatic engines").State.Should().Be(FrontendQualityCoverageState.PublicOnly);
    }

    [Fact]
    public void Coverage_ManualOnly_IsPublicOnlyNotAFailure()
    {
        var context = Context(requiresAuth: true, method: AuthenticatedTestingMethod.ManualOnly);
        var rows = FrontendQualityLandingPresentation.Coverage(FrontendQualityTargetAccess.Build(context, null, false, null, null), null);
        rows.Single(r => r.Label == "Automatic engines").State.Should().Be(FrontendQualityCoverageState.PublicOnly);
        rows.Single(r => r.Label == "Authenticated application").Detail.Should().Contain("Manual verification only");
    }

    // ── Authenticated review ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AuthenticatedReview_NullWhenNotRequired()
    {
        var context = Context();
        FrontendQualityLandingPresentation.AuthenticatedReview(context, FrontendQualityTargetAccess.FromContext(context), null, null).Should().BeNull();
    }

    [Fact]
    public void AuthenticatedReview_ManagedEdge_UsesSessionStateAndExistingSignInWorkflow()
    {
        var context = Context(requiresAuth: true);
        var notConnected = FrontendQualityLandingPresentation.AuthenticatedReview(context, FrontendQualityTargetAccess.FromContext(context), null, null)!;
        notConnected.Method.Should().Be(AuthenticatedTestingMethod.ManagedEdgeCdp);
        notConnected.Connected.Should().BeFalse();
        notConnected.StatusLabel.Should().Be("Not connected");
        notConnected.Steps.Should().HaveCount(3).And.Contain(s => s.Contains("Sign in for review"));
        notConnected.Evidence.Select(e => e.Label).Should().Equal("Browser session", "Authenticated DOM", "Browser runtime");

        var connected = FrontendQualityLandingPresentation.AuthenticatedReview(context, FrontendQualityTargetAccess.FromContext(context), null, new AuthenticatedBrowserSession { IsAuthenticated = true })!;
        connected.Connected.Should().BeTrue();
        connected.StatusLabel.Should().Be("Connected");
    }

    [Fact]
    public void AuthenticatedReview_Proxy_DescribesProxyWorkflowAndTrafficEvidence()
    {
        var context = Context(requiresAuth: true, method: AuthenticatedTestingMethod.LocalHttpsProxy);
        var caps = new AuthenticatedReviewCapabilities { Method = AuthenticatedTestingMethod.LocalHttpsProxy, ContextStatus = AuthenticatedApiContextStatus.Available, AuthenticatedApi = true, AuthenticatedRest = true };
        var model = FrontendQualityLandingPresentation.AuthenticatedReview(context, FrontendQualityTargetAccess.Build(context, caps, false, null, LocalHttpsProxyState.Ready), caps, null)!;
        model.Connected.Should().BeTrue();
        model.StatusLabel.Should().Be("Connected");
        model.Steps.Should().HaveCount(4);
        model.Steps[0].Should().Contain("start the Local HTTPS proxy");
        model.Evidence.Single(e => e.Label == "REST traffic").Value.Should().Be("Observed");
        model.Evidence.Single(e => e.Label == "GraphQL traffic").Value.Should().Be("Not observed");
        model.Evidence.Single(e => e.Label == "Authenticated DOM").Available.Should().BeFalse();
        model.Note.Should().Contain("cannot assess the signed-in application with this method");
    }

    [Fact]
    public void AuthenticatedReview_ManualOnly_SeparatesManualVerificationFromAutomatedAccess()
    {
        var context = Context(requiresAuth: true, method: AuthenticatedTestingMethod.ManualOnly);
        context.ManualVerificationStatus = ManualAuthenticationVerificationStatus.Passed;
        var model = FrontendQualityLandingPresentation.AuthenticatedReview(context, FrontendQualityTargetAccess.FromContext(context), null, null)!;
        model.StatusLabel.Should().Be("Manual verification: Passed");
        model.Connected.Should().BeFalse("a passed manual verification never grants automated access");
        model.Evidence.Single(e => e.Label == "Manual verification").Available.Should().BeTrue();
        model.Evidence.Single(e => e.Label == "Authenticated DOM").Available.Should().BeFalse();
        model.Note.Should().Contain("does not give automated engines access");
    }

    // ── Target summary / technical fields ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TargetSummary_HumanLabels_AndTechnicalFieldsKeepExactValues()
    {
        var context = Context(requiresAuth: true);
        var summary = FrontendQualityLandingPresentation.TargetSummary(context);
        summary.Environment.Should().Be("M2LB DEV");
        summary.EnvironmentType.Should().Be("Dev");
        summary.Authentication.Should().Be("Microsoft Entra ID");
        summary.TargetStatus.Should().Be("Ready");
        summary.TargetReady.Should().BeTrue();

        var fields = FrontendQualityLandingPresentation.TechnicalTargetFields(context);
        fields.Should().Contain(f => f.Label == "Environment type" && f.Value == "Development");
        fields.Should().Contain(f => f.Label == "Authentication type" && f.Value == "MicrosoftEntraId");
        fields.Should().Contain(f => f.Label == "Lighthouse" && f.Value == "Enabled (synthetic lab measurement)");
        fields.Should().Contain(f => f.Label == "Browser Runtime" && f.Value == "Disabled");

        FrontendQualityLandingPresentation.TargetSummary(Context(url: "")).TargetStatus.Should().Be("Frontend URL missing");
        FrontendQualityLandingPresentation.AuthenticationLabel(FrontendAuthenticationType.MicrosoftEntraId, requiresAuthentication: false).Should().Be("Not required");
    }

    [Fact]
    public void ReviewScope_IsGroupedWithoutComplianceClaimsAndWithoutACheckCount()
    {
        FrontendQualityLandingPresentation.CheckGroups.Select(g => g.Title).Should().Equal("Security", "Performance", "Accessibility", "Blazor / WASM", "Standards / QA readiness");
        // 23. The scope is the group names, derived from the groups themselves — never a count of the entries, which
        // are not comparable units and move whenever a group is reworded.
        FrontendQualityLandingPresentation.ReviewScopeSummary
            .Should().Be(string.Join(" · ", FrontendQualityLandingPresentation.CheckGroups.Select(g => g.Title)))
            .And.NotMatchRegex(@"\d");
        FrontendQualityLandingPresentation.CheckGroups.SelectMany(g => g.Checks).Should().NotContain(c => c.Contains("OWASP", StringComparison.OrdinalIgnoreCase));
        FrontendQualityCategoryEngines.For(FrontendQualityCategory.Security).Should().Equal(FrontendQualityEngineId.StaticSecurity, FrontendQualityEngineId.PassiveSecurity);
    }

    // 34, 35, 36, 37. Scope boundaries, named as such; none of them reads as a gap in this review.
    [Fact]
    public void OutsideThisReview_NamesScopeBoundariesRatherThanMissingCoverage()
    {
        var items = FrontendQualityLandingPresentation.NotAssessed;
        items.Select(n => n.Title).Should().Equal("Core Web Vitals", "Testability", "Observability");
        FrontendQualityLandingPresentation.OutsideReviewSummary.Should().Be("Core Web Vitals · Testability · Observability");

        // 35. Core Web Vitals is a separate measurement path, not something this review failed to measure.
        items[0].Description.Should().Contain("Measured separately").And.Contain("field");
        // 36, 37. The other two say where they belong instead.
        items[1].Description.Should().Be("Not part of Frontend Quality Review.");
        items[2].Description.Should().Be("Not part of Frontend Quality Review.");
        // 38. Nothing here is phrased as a failure.
        items.Should().NotContain(i => i.Description.Contains("fail", StringComparison.OrdinalIgnoreCase)
                                    || i.Description.Contains("missing", StringComparison.OrdinalIgnoreCase));
    }

    // 4, 5, 6, 34, 35. What blocks a run and what merely limits it are different things, and the difference is the
    // whole point of Limited. An optional capability that cannot start reduces DEPTH; only a missing prerequisite —
    // no target URL, no active engine — stops the review.
    [Fact]
    public void OnlyAMissingPrerequisiteBlocksTheRun_NeverAnUnavailableCapability()
    {
        // 4. Everything enabled is available → Ready, Run enabled.
        var ready = Context();
        FrontendQualityLandingPresentation.Readiness(ready, FrontendQualityActiveEngines.Resolve(ready),
            Capabilities(ready, Status(Engine(FrontendQualityEngineIdDto.Accessibility), Engine(FrontendQualityEngineIdDto.Lighthouse), Engine(FrontendQualityEngineIdDto.PassiveSecurity))), false)
            .CanRun.Should().BeTrue();

        // 5. An OPTIONAL capability that cannot start: limited depth, and the run goes ahead.
        var optional = Context();
        var optionalReadiness = FrontendQualityLandingPresentation.Readiness(optional, FrontendQualityActiveEngines.Resolve(optional),
            Capabilities(optional, Status(Engine(FrontendQualityEngineIdDto.Accessibility), Engine(FrontendQualityEngineIdDto.Lighthouse), Engine(FrontendQualityEngineIdDto.PassiveSecurity, ready: false, reason: "Container runtime unavailable."))), false);
        optionalReadiness.Level.Should().Be(FrontendQualityReviewReadinessLevel.Limited);
        optionalReadiness.CanRun.Should().BeTrue("optional depth is not a prerequisite");
        optionalReadiness.Message.Should().StartWith("Passive Security is currently unavailable.");

        // 6. A missing PREREQUISITE is what blocks, and it keeps the existing blocked semantics.
        var noUrl = Context(url: "");
        var blocked = FrontendQualityLandingPresentation.Readiness(noUrl, FrontendQualityActiveEngines.Resolve(noUrl), [], false);
        blocked.Level.Should().Be(FrontendQualityReviewReadinessLevel.Blocked);
        blocked.CanRun.Should().BeFalse();
        blocked.Title.Should().Be(FrontendQualityLandingPresentation.BlockedTitle);
    }
}
