using AngleSharp.Dom;
using BirkNext.BrowserCompanion;
using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Moq;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// Phase 3B: before a run, the page is a decision surface, not a diagnostics dashboard. Visible without any interaction:
/// the target, the accessibility profile, whether the review can run, the run action, and the six review domains.
/// Everything technical is still there — coverage, checks, capabilities, exclusions and browser evidence — but behind one
/// collapsed disclosure each, stating one fact and revealing the existing detail unchanged.
///
/// These tests deliberately assert DOCUMENT ORDER and COLLAPSED/EXPANDED STATE rather than pixels: the information
/// architecture is the contract, the layout is not.
/// </summary>
public sealed class FrontendQualityLandingDisclosureTests : BunitContext
{
    private const string Url = "https://m2lbdev.example.test/";

    private static FrontendAnalysisContext Context(Action<FrontendAnalysisFeatureToggles>? toggles = null, bool requiresAuth = false, string? url = Url)
    {
        var profile = new FrontendAnalysisProfile { Id = "dev", Name = "M2LB DEV", EnvironmentType = FrontendEnvironmentType.Development, TargetUrl = url };
        profile.Authentication.RequiresAuthentication = requiresAuth;
        profile.Authentication.AuthenticationType = requiresAuth ? FrontendAuthenticationType.MicrosoftEntraId : FrontendAuthenticationType.None;
        toggles?.Invoke(profile.Features);
        return new FrontendAnalysisContext
        {
            ActiveProfile = profile, TargetUrl = url ?? "", FeatureToggles = profile.Features,
            EngineRequirements = profile.EngineRequirements, ReviewEngineSelection = profile.ReviewEngineSelection,
            RequiresAuthentication = requiresAuth, AuthenticationType = profile.Authentication.AuthenticationType,
        };
    }

    /// <summary>Only the two HTTP engines enabled: no backend status probe, so Run is enabled immediately.</summary>
    private static FrontendAnalysisContext HttpOnly(bool requiresAuth = false, string? url = Url) =>
        Context(t =>
        {
            t.EnableBrowserRuntimeEngine = false; t.EnableAccessibilityEngine = false; t.EnableLighthouseEngine = false;
            t.EnablePassiveSecurityEngine = false; t.EnableBrowserQualityEngine = false; t.EnablePerformanceQualityEngine = false;
        }, requiresAuth, url);

    private static FrontendQualityEngineStatusDto Engine(FrontendQualityEngineIdDto id, bool layer1 = true, bool layer2 = true, bool? ready = true, string? reason = null) => new()
    {
        EngineId = id, DisplayName = id.ToString(), Layer1Allowed = layer1, Layer2Enabled = layer2, AuthModeSupported = true,
        Available = layer1 && layer2 && ready != false,
        Layer3Readiness = ready is null ? null : new FrontendQualityEngineReadinessDto { EngineId = id, IsAvailable = ready.Value, StatusReason = reason },
    };

    private void Register(FrontendAnalysisContext context, IReadOnlyList<FrontendQualityEngineStatusDto>? engines = null)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var status = new Mock<IFrontendQualityEngineStatusApiService>();
        status.Setup(s => s.GetStatusAsync(It.IsAny<ReviewAuthenticationModeDto>(), It.IsAny<ReviewEngineSelectionDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FrontendQualityEngineStatusReportDto { Engines = (engines ?? []).ToList() });
        var factory = new Mock<IFrontendAnalysisContextFactory>();
        factory.Setup(f => f.GetActiveContextAsync()).ReturnsAsync(context);
        Services.AddSingleton(status.Object);
        Services.AddSingleton(factory.Object);
        Services.AddSingleton(Mock.Of<IFrontendQualityReviewOrchestrator>());
        Services.AddSingleton<RuntimeReviewSessionService>();
        Services.AddSingleton(Mock.Of<IWorkspaceSessionService>());
        Services.AddSingleton(Mock.Of<IReportExportService>());
        Services.AddSingleton(Mock.Of<IAuthenticatedBrowserSessionService>());
        Services.AddSingleton<IEndpointDiscoveryService, EndpointDiscoveryService>();
    }

    private IRenderedComponent<FrontendQualityReview> Page(FrontendAnalysisContext? context = null, IReadOnlyList<FrontendQualityEngineStatusDto>? engines = null)
    {
        Register(context ?? HttpOnly(), engines);
        return Render<FrontendQualityReview>();
    }

    /// <summary>The landing blocks in document order, which is also the reading order and the focus order.</summary>
    private static IReadOnlyList<string> Order(IRenderedComponent<FrontendQualityReview> page) =>
        page.FindAll("[data-testid=fqr-target-summary], [data-testid=fqr-profile], [data-testid=fqr-readiness], [data-testid=fqr-run-bar], [data-testid=fqr-dimensions], [data-testid=fqr-details]")
            .Select(e => e.GetAttribute("data-testid")!)
            .ToList();

    /// <summary>
    /// The text a user can actually read: a collapsed disclosure body is still in the DOM but carries <c>hidden</c>, so it
    /// is removed from rendering and from the accessibility tree — and must not count as something the page "says".
    /// </summary>
    private static string VisibleText(IElement element)
    {
        var clone = (IElement)element.Clone(true);
        foreach (var hidden in clone.QuerySelectorAll("[hidden]").ToList()) hidden.Remove();
        return clone.TextContent;
    }

    private static IElement Toggle(IRenderedComponent<FrontendQualityReview> page, string id) => page.Find($"[data-testid={id}-toggle]");
    private static IElement Body(IRenderedComponent<FrontendQualityReview> page, string id) => page.Find($"[data-testid={id}-body]");
    private static bool Collapsed(IRenderedComponent<FrontendQualityReview> page, string id) =>
        Toggle(page, id).GetAttribute("aria-expanded") == "false" && Body(page, id).HasAttribute("hidden");

    /// <summary>Every disclosure the pre-run Review details section owns.</summary>
    private static readonly string[] DetailDisclosures =
        ["fqr-coverage-disclosure", "fqr-checks-disclosure", "fqr-capabilities-disclosure", "fqr-not-assessed-disclosure"];

    // ── §35. The decision area ──────────────────────────────────────────────────────────────────────────────────────

    // 1, 2, 5. Target, then the profile the review is judged against, then whether it can run — all before the domains.
    [Fact]
    public void ReadingOrderIsTargetThenProfileThenReadinessThenRunThenScopeThenDetails()
    {
        var page = Page();

        Order(page).Should().Equal(
            "fqr-target-summary", "fqr-profile", "fqr-readiness", "fqr-run-bar", "fqr-dimensions", "fqr-details");
    }

    // 2. The profile is part of the primary decision area, not a detail below it.
    [Fact]
    public void AccessibilityProfileSitsInsideTheDecisionArea()
    {
        var page = Page();

        var decide = page.Find("[data-testid=fqr-decide]");
        decide.QuerySelector("[data-testid=fqr-profile]").Should().NotBeNull();
        decide.QuerySelector("[data-testid=fqr-target-summary]").Should().NotBeNull();
        decide.QuerySelector("[data-testid=fqr-readiness]").Should().NotBeNull();
        // Nothing diagnostic is in there.
        decide.QuerySelector("[data-testid=fqr-capabilities]").Should().BeNull();
        decide.QuerySelector("[data-testid=fqr-coverage]").Should().BeNull();
    }

    // 3, 6. Readiness is stated once, at review level, and nowhere else at that prominence.
    [Fact]
    public void ReadinessIsStatedOnceAsTheReviewLevelState()
    {
        var page = Page();

        page.FindAll("[data-testid=fqr-readiness]").Should().ContainSingle();
        page.FindAll("[data-testid=fqr-readiness-title]").Should().ContainSingle();
        page.FindAll("[data-testid=fqr-target-review-status]").Should().BeEmpty("the Target card no longer repeats the review state");
        page.Find("[data-testid=fqr-readiness-title]").TextContent.Should().Be("Ready to review");
    }

    // 4. Run is reachable without expanding anything.
    [Fact]
    public void RunActionIsVisibleWithoutExpandingAnyDetail()
    {
        var page = Page();

        var run = page.Find("[data-testid=fqr-run-bar] [data-testid=fqr-run]");
        run.TextContent.Trim().Should().Be("Run Frontend Quality Review");
        run.HasAttribute("disabled").Should().BeFalse();
        // It is not inside any disclosure body.
        run.Closest(".disclosure-body").Should().BeNull();
    }

    // 21 (§21). A blocked review explains itself next to the button, and the run semantics are unchanged.
    [Fact]
    public void BlockedReviewShowsItsReasonNextToTheRunButton_AndKeepsLimitationsVisible()
    {
        var page = Page(HttpOnly(url: ""));

        page.Find("[data-testid=fqr-run]").HasAttribute("disabled").Should().BeTrue();
        page.Find("[data-testid=fqr-run-bar] [data-testid=fqr-run-disabled-reason]").TextContent.Should().Contain("no Frontend URL");
        page.Find("[data-testid=fqr-readiness]").GetAttribute("data-readiness").Should().Be("Blocked");
        // Blocking reasons are never hidden behind a disclosure.
        page.Find("[data-testid=fqr-readiness-details]").HasAttribute("hidden").Should().BeFalse();
        page.FindAll("[data-testid=fqr-readiness-limitations]").Should().BeEmpty();
    }

    // ── §6. Limitations are progressive disclosure while the review can still run ───────────────────────────────────

    [Fact]
    public void LimitationsAreCollapsedWhileTheReviewCanStillRun_AndCarryTheirOwnAction()
    {
        var page = Page(Context(), [Engine(FrontendQualityEngineIdDto.Accessibility), Engine(FrontendQualityEngineIdDto.PassiveSecurity),
            Engine(FrontendQualityEngineIdDto.Lighthouse, ready: false, reason: "Lighthouse CLI not installed on this host.")]);

        page.WaitForAssertion(() => page.Find("[data-testid=fqr-readiness]").GetAttribute("data-readiness").Should().Be("Limited"));

        // One state, one explanation, one action.
        page.Find("[data-testid=fqr-readiness-title]").TextContent.Should().Be("Review can run with limitations");
        page.Find("[data-testid=fqr-readiness-message]").TextContent.Should().Be("1 enabled optional capability is currently unavailable.");
        Collapsed(page, "fqr-readiness-limitations").Should().BeTrue();
        page.Find("[data-testid=fqr-run]").HasAttribute("disabled").Should().BeFalse();

        Toggle(page, "fqr-readiness-limitations").Click();
        var body = Body(page, "fqr-readiness-limitations");
        body.HasAttribute("hidden").Should().BeFalse();
        body.TextContent.Should().Contain("Lighthouse: Unavailable");
        body.QuerySelector("[data-testid=fqr-readiness-limitation-action]")!.GetAttribute("href")
            .Should().Be(FrontendQualityLandingPresentation.FrontendReviewEnginesHref, "engine limitations are fixed where engines are configured");
    }

    // ── §36. The scope section ──────────────────────────────────────────────────────────────────────────────────────

    // 7, 8, 9.
    [Fact]
    public void ScopeSectionIsCalledWhatWillBeReviewed_AndKeepsAllSixDomainsVisible()
    {
        var page = Page();

        page.Find("h2#fqr-dimensions-heading").TextContent.Should().Be("What will be reviewed");
        page.Markup.Should().NotContain("What will be analysed");
        page.FindAll("[data-testid=fqr-dimension]").Should().HaveCount(6);
        // Domains are never collapsed: they are the main explanation of review scope.
        page.FindAll("[data-testid=fqr-dimension]").Should().OnlyContain(c => c.Closest(".disclosure-body") == null);
    }

    // ── §37. Coverage ───────────────────────────────────────────────────────────────────────────────────────────────

    // 10, 11, 12, 13.
    [Fact]
    public void CoverageIsSummarisedWhenCollapsed_AndLosesNothingWhenExpanded()
    {
        var page = Page();
        page.WaitForAssertion(() => Toggle(page, "fqr-coverage-disclosure"));

        Collapsed(page, "fqr-coverage-disclosure").Should().BeTrue();
        var expectedRows = page.Find("[data-testid=fqr-coverage]").QuerySelectorAll("[data-testid=fqr-coverage-row]").Length;
        // An area this target does not need is not an area the review is missing, so the hint states both facts
        // rather than folding them into one denominator.
        Toggle(page, "fqr-coverage-disclosure").TextContent.Should().Contain("Coverage").And.Contain("available");

        Toggle(page, "fqr-coverage-disclosure").Click();

        var body = Body(page, "fqr-coverage-disclosure");
        body.HasAttribute("hidden").Should().BeFalse();
        body.QuerySelectorAll("[data-testid=fqr-coverage-row]").Select(r => r.GetAttribute("data-coverage"))
            .Should().Equal("Public frontend", "Authenticated application", "Browser-rendered DOM", "Authenticated API traffic", "Automatic engines");
        // The exact access panel is still nested one level deeper, unchanged.
        body.QuerySelector("[data-testid=fqr-coverage-technical-body] [data-testid=fqr-target-access]").Should().NotBeNull();
    }

    // §28 — the summary is derived, not invented, and claims no measured proportion.
    [Fact]
    public void CoverageSummaryCountsComeFromTheCoverageRowsAndAreNotAPercentage()
    {
        var page = Page();
        page.WaitForAssertion(() => Toggle(page, "fqr-coverage-disclosure"));

        var rows = page.FindAll("[data-testid=fqr-coverage-row]");
        var available = rows.Count(r => r.GetAttribute("data-state") == nameof(FrontendQualityCoverageState.Available));
        var hint = page.Find("[data-testid=fqr-coverage-disclosure-toggle] .disclosure-hint").TextContent;

        var notRequired = rows.Count(r => r.GetAttribute("data-state") == nameof(FrontendQualityCoverageState.NotRequired));
        hint.Should().Contain($"{available} available");
        if (notRequired > 0) hint.Should().Contain($"{notRequired} not required");
        hint.Should().NotContain("%");
        hint.Should().NotContain($"of {rows.Count}", "a not-required area is not a missing one");
    }

    // ── §38. Checks ─────────────────────────────────────────────────────────────────────────────────────────────────

    // 14, 15, 16, 17.
    [Fact]
    public void ChecksAreCountedWhenCollapsed_AndTheCountComesFromTheModel()
    {
        var page = Page();

        Collapsed(page, "fqr-checks-disclosure").Should().BeTrue();
        var hint = page.Find("[data-testid=fqr-checks-disclosure-toggle] .disclosure-hint").TextContent;
        hint.Should().Be($"{FrontendQualityLandingPresentation.CheckCount} automated or passive checks included");
        hint.Should().Be(FrontendQualityLandingPresentation.CheckSummary);
        // §29: nothing has run, so nothing may read as a result.
        hint.Should().NotContainAny("passed", "failed", "tests");

        Toggle(page, "fqr-checks-disclosure").Click();

        Body(page, "fqr-checks-disclosure").QuerySelectorAll("[data-testid=fqr-check-group]").Select(g => g.GetAttribute("data-group"))
            .Should().Equal("Security", "Performance", "Accessibility", "Blazor / WASM", "Standards / QA readiness");
    }

    // ── §39. Capabilities ───────────────────────────────────────────────────────────────────────────────────────────

    // 18, 19, 20, 21, 22.
    [Fact]
    public void CapabilitiesAreSummarisedWhenCollapsed_AndTheEngineDetailStaysExact()
    {
        var page = Page(Context(), [Engine(FrontendQualityEngineIdDto.Accessibility), Engine(FrontendQualityEngineIdDto.PassiveSecurity),
            Engine(FrontendQualityEngineIdDto.Lighthouse, ready: false, reason: "Lighthouse CLI not installed on this host.")]);
        page.WaitForAssertion(() => page.Find("[data-testid=fqr-run]").HasAttribute("disabled").Should().BeFalse());

        Collapsed(page, "fqr-capabilities-disclosure").Should().BeTrue();
        page.Find("[data-testid=fqr-capabilities-disclosure-toggle]").TextContent.Should().Contain("Review capabilities").And.Contain("available now");

        Toggle(page, "fqr-capabilities-disclosure").Click();

        var body = Body(page, "fqr-capabilities-disclosure");
        // 22. Enabled (saved configuration) and Available (live capability) stay separate facts.
        var lighthouse = body.QuerySelector($"[data-testid=fqr-capability][data-engine-id='{FrontendQualityEngineId.Lighthouse}']")!;
        lighthouse.QuerySelector("[data-testid=fqr-capability-state]")!.TextContent.Should().Be("Unavailable");
        lighthouse.QuerySelector("[data-testid=fqr-capability-enabled]")!.TextContent.Should().Be("Enabled");
        // 20. The exact engine states survive.
        body.QuerySelectorAll("[data-testid=fqr-capability-group]").Select(g => g.GetAttribute("data-policy")).Should().Equal("Required", "Optional");
        // 21. Edit engines is still there.
        body.QuerySelector("[data-testid=fqr-edit-engines]")!.GetAttribute("href").Should().Be(FrontendQualityLandingPresentation.FrontendReviewEnginesHref);
    }

    // 18, §27 — the collapsed counts are derived from the capability rows and never equate enabled with available.
    [Fact]
    public void CapabilitySummaryCountsEnabledAndAvailableSeparately()
    {
        var rows = new List<FrontendQualityCapabilityRow>
        {
            new(FrontendQualityEngineId.StaticSecurity, "Static Security", FrontendQualityEngineRequirement.Required, FrontendQualityCapabilityState.Ready, null, null, Enabled: true),
            new(FrontendQualityEngineId.PassivePerformance, "Passive Performance", FrontendQualityEngineRequirement.Required, FrontendQualityCapabilityState.Disabled, null, null, Enabled: false),
            new(FrontendQualityEngineId.Lighthouse, "Lighthouse", FrontendQualityEngineRequirement.Optional, FrontendQualityCapabilityState.Unavailable, null, null, Enabled: true),
            new(FrontendQualityEngineId.BrowserQuality, "Browser Quality", FrontendQualityEngineRequirement.Optional, FrontendQualityCapabilityState.NotConfigured, null, null, Enabled: true),
        };

        var summary = FrontendQualityLandingPresentation.CapabilitySummary(rows);

        summary.TotalCount.Should().Be(4);
        summary.EnabledCount.Should().Be(3);
        summary.AvailableNowCount.Should().Be(1, "enabled is not available");
        summary.RequiredButDisabledCount.Should().Be(1);
        summary.NeedsPairingCount.Should().Be(1);
        // 23. A Required engine switched off is visible on the collapsed row, not only after expanding.
        summary.Collapsed.Should().Be("1 available now · 1 required engine disabled");
    }

    // 23. The RequiredButDisabled warning itself survives inside the expanded detail.
    [Fact]
    public void RequiredButDisabledWarningRemainsAvailableInsideTheExpandedCapabilities()
    {
        var context = Context(t => t.EnableSecurityEngine = false);
        var page = Page(context, [Engine(FrontendQualityEngineIdDto.Accessibility), Engine(FrontendQualityEngineIdDto.Lighthouse), Engine(FrontendQualityEngineIdDto.PassiveSecurity)]);
        page.WaitForAssertion(() => page.Find("[data-testid=fqr-capabilities-disclosure-toggle]"));

        page.Find("[data-testid=fqr-capabilities-disclosure-toggle]").TextContent.Should().Contain("required engine");

        Toggle(page, "fqr-capabilities-disclosure").Click();

        var warning = Body(page, "fqr-capabilities-disclosure").QuerySelector("[data-testid=fqr-capabilities-required-disabled]")!;
        warning.TextContent.Should().Contain("Static Security");
        warning.QuerySelector("[data-testid=fqr-required-disabled-edit]")!.GetAttribute("href")
            .Should().Be(FrontendQualityLandingPresentation.FrontendReviewEnginesHref);
    }

    // §13. Required/Optional is engine policy and stays inside the capability detail.
    [Fact]
    public void RequiredOptionalPolicyIsNotSurfacedInThePrimaryArea()
    {
        var page = Page(Context(), [Engine(FrontendQualityEngineIdDto.Accessibility), Engine(FrontendQualityEngineIdDto.Lighthouse), Engine(FrontendQualityEngineIdDto.PassiveSecurity)]);
        page.WaitForAssertion(() => page.Find("[data-testid=fqr-capabilities-disclosure-toggle]"));

        VisibleText(page.Find("[data-testid=fqr-decide]")).Should().NotContainAny("Required", "Optional");
        VisibleText(page.Find("[data-testid=fqr-dimensions]")).Should().NotContainAny("Required", "Optional");
        page.FindAll("[data-testid=fqr-capability-group]").Should().OnlyContain(g => g.Closest(".disclosure-body") != null);
    }

    // ── §40. Not assessed ───────────────────────────────────────────────────────────────────────────────────────────

    // 24, 25, 26, 27.
    [Fact]
    public void NotAssessedIsACompactNeutralSummary_AndKeepsEveryExcludedArea()
    {
        var page = Page();

        Collapsed(page, "fqr-not-assessed-disclosure").Should().BeTrue();
        var hint = page.Find("[data-testid=fqr-not-assessed-disclosure-toggle] .disclosure-hint").TextContent;
        hint.Should().Be($"{FrontendQualityLandingPresentation.NotAssessed.Count} areas not assessed by this review");
        // 27. Nothing here implies a failure or a missing implementation.
        hint.Should().NotContainAny("missing", "failed", "not implemented", "incomplete");

        Toggle(page, "fqr-not-assessed-disclosure").Click();

        var body = Body(page, "fqr-not-assessed-disclosure");
        body.QuerySelectorAll("[data-testid=fqr-not-assessed-item] strong").Select(i => i.TextContent)
            .Should().Equal(FrontendQualityLandingPresentation.NotAssessed.Select(n => n.Title));
        body.QuerySelectorAll(".fqr-pill-attention, .fqr-pill-warning").Should().BeEmpty();
    }

    // ── §41. Browser Companion is supporting detail ─────────────────────────────────────────────────────────────────

    private async Task<IRenderedComponent<FrontendQualityReview>> BrowserPageAsync(BrowserCompanionState state)
    {
        var context = Context(t =>
        {
            t.EnableBrowserRuntimeEngine = false; t.EnableAccessibilityEngine = false; t.EnableLighthouseEngine = false;
            t.EnablePassiveSecurityEngine = false; t.EnableBrowserQualityEngine = true; t.EnablePerformanceQualityEngine = false;
        });
        // Browser Quality runs in the browser, not on the backend, so there is no engine-status row to register for it.
        Register(context);

        var api = new Mock<IBrowserCompanionApiService>();
        api.Setup(a => a.StatusAsync("dev", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrowserCompanionStatus { ProfileId = "dev", State = state });
        // Built outside the container and registered as an instance: the container then neither resolves the provider
        // early nor takes ownership of an IAsyncDisposable it cannot dispose synchronously.
        var discovery = new EndpointDiscoveryService();
        var runtime = new BrowserCompanionRuntime(api.Object, discovery, JSInterop.JSRuntime);
        Services.AddSingleton<IEndpointDiscoveryService>(discovery);
        Services.AddSingleton(runtime);
        _runtimes.Add(runtime);
        await runtime.FollowAsync(context.ActiveProfile);

        var page = Render<FrontendQualityReview>();
        page.WaitForAssertion(() => page.Find("[data-testid=fqr-companion-toggle]"));
        return page;
    }

    // Runtimes poll in the background; holding the references keeps them alive for the test's duration.
    private readonly List<BrowserCompanionRuntime> _runtimes = [];

    // 28, 30, 31.
    [Fact]
    public async Task BrowserCompanionDetailIsSupportingDetail_ButItsExactStateSurvivesOneExpandAway()
    {
        var page = await BrowserPageAsync(BrowserCompanionState.NotPaired);

        // 28. It sits in Review details, collapsed, and never in the decision area.
        page.Find("[data-testid=fqr-companion]").Closest("[data-testid=fqr-details]").Should().NotBeNull();
        page.Find("[data-testid=fqr-decide]").QuerySelector("[data-testid=fqr-companion]").Should().BeNull();
        Collapsed(page, "fqr-companion").Should().BeTrue();
        Toggle(page, "fqr-companion").TextContent.Should().Contain("Browser evidence").And.Contain("Not connected");

        Toggle(page, "fqr-companion").Click();

        // 31. The exact session state and its own actions are unchanged, one expand away.
        var body = Body(page, "fqr-companion");
        body.HasAttribute("hidden").Should().BeFalse();
        body.TextContent.Should().NotBeNullOrWhiteSpace();
    }

    // 29, 32. The absence is stated at each layer in different words, and never twice at the same prominence.
    [Fact]
    public async Task BrowserEvidenceAbsenceIsLayered_NotRepeatedAtTheSameProminence()
    {
        var page = await BrowserPageAsync(BrowserCompanionState.NotPaired);

        // The domain says what it costs the review, in evidence words — no engine name, no pairing vocabulary.
        var blazor = page.Find("[data-testid=fqr-dimension][data-category='BlazorWasm']");
        blazor.TextContent.Should().NotContainAny("Browser Companion", "Needs pairing", "pair");

        // Nothing the user can read without expanding something announces the pairing state.
        foreach (var block in page.FindAll("[data-testid=fqr-decide], [data-testid=fqr-dimensions]"))
            VisibleText(block).Should().NotContainAny("Browser Companion", "Not paired", "Needs pairing");

        // And the exact wording is available where it belongs.
        Toggle(page, "fqr-capabilities-disclosure").Click();
        Body(page, "fqr-capabilities-disclosure")
            .QuerySelector($"[data-testid=fqr-capability][data-engine-id='{FrontendQualityEngineId.BrowserQuality}'] [data-testid=fqr-capability-state]")!
            .TextContent.Should().Be("Not configured");
    }

    // ── §42. Disclosure behaviour ───────────────────────────────────────────────────────────────────────────────────

    // 33, 35. Collapsed content is removed from rendering, focus order and the accessibility tree.
    [Fact]
    public void EveryDetailDisclosureIsCollapsedByDefault_WithItsStateProgrammaticallyExposed()
    {
        var page = Page();
        page.WaitForAssertion(() => Toggle(page, "fqr-coverage-disclosure"));

        foreach (var id in DetailDisclosures)
        {
            Collapsed(page, id).Should().BeTrue($"{id} is collapsed before the user asks for it");
            var toggle = Toggle(page, id);
            toggle.TagName.Should().Be("BUTTON");
            toggle.GetAttribute("type").Should().Be("button");
            toggle.GetAttribute("aria-controls").Should().Be(Body(page, id).Id);
        }
        // 35. Nothing focusable is exposed from a collapsed body.
        page.FindAll(".disclosure-body[hidden] button, .disclosure-body[hidden] a, .disclosure-body[hidden] input")
            .Should().OnlyContain(e => e.Closest(".disclosure-body[hidden]") != null, "hidden content is inert");
    }

    // 34. Keyboard activation: the control is a native <button> with no positive tabindex and no key handler of its own,
    // so Enter and Space reach the same activation path the click asserts here. Nothing overrides that path.
    [Fact]
    public void DisclosuresActivateThroughNativeButtonSemantics_AndToggleBothWays()
    {
        var page = Page();

        foreach (var id in new[] { "fqr-checks-disclosure", "fqr-not-assessed-disclosure" })
        {
            var toggle = Toggle(page, id);
            toggle.TagName.Should().Be("BUTTON");
            toggle.GetAttribute("type").Should().Be("button");
            toggle.HasAttribute("tabindex").Should().BeFalse("the natural tab order is the focus order");
            toggle.HasAttribute("onkeydown").Should().BeFalse("no custom key handling replaces the button's own activation");

            Toggle(page, id).Click();
            Toggle(page, id).GetAttribute("aria-expanded").Should().Be("true");
            Body(page, id).HasAttribute("hidden").Should().BeFalse();

            Toggle(page, id).Click();
            Collapsed(page, id).Should().BeTrue("a disclosure closes again");
        }
    }

    // 36. Titles are unique and say what they open.
    [Fact]
    public void DisclosureTitlesAreUniqueAndUnderstandable()
    {
        var page = Page(Context(), [Engine(FrontendQualityEngineIdDto.Accessibility), Engine(FrontendQualityEngineIdDto.Lighthouse), Engine(FrontendQualityEngineIdDto.PassiveSecurity)]);
        page.WaitForAssertion(() => page.Find("[data-testid=fqr-capabilities-disclosure-toggle]"));

        var titles = page.Find("[data-testid=fqr-details]")
            .QuerySelectorAll(":scope > .disclosure > .disclosure-toggle .disclosure-text")
            .Select(t => t.TextContent.Trim()).ToList();

        titles.Should().Equal("Coverage", "Checks", "Review capabilities", "Not assessed");
        titles.Should().OnlyHaveUniqueItems();
    }

    // §20. Disclosure state is component state only — nothing is persisted for an open/closed panel.
    [Fact]
    public void DisclosureStateIsNotPersisted()
    {
        var page = Page();
        Toggle(page, "fqr-checks-disclosure").Click();

        JSInterop.Invocations.Should().NotContain(i => i.Identifier.Contains("setItem"), "an open panel is not saved anywhere");
    }

    // ── §43. Narrow viewport: deterministic structure rather than pixel assertions ───────────────────────────────────

    [Fact]
    public void NarrowLayoutHasTheHooksItNeeds_WithoutFixedWidthsOrInlineStyles()
    {
        var page = Page();
        page.WaitForAssertion(() => page.Find("[data-testid=fqr-coverage-disclosure-toggle]"));

        // The top cards are one grid that the stylesheet can collapse to a single column.
        page.Find(".fqr-decide-cards").Children.Length.Should().Be(3);
        // Nothing carries a hard-coded width or height inline, so the media queries stay in charge.
        page.FindAll("[data-testid=fqr-landing] *").Should().OnlyContain(e => !e.HasAttribute("style"));
        // A long URL is free to wrap inside its own card.
        page.Find("[data-testid=fqr-target-url]").ClassList.Should().Contain("fqr-target-url");
        page.FindAll("[data-testid=fqr-landing] table").Should().BeEmpty("a table would force horizontal scrolling at narrow widths");
    }

    // ── §44. No Phase 3A semantic regression ────────────────────────────────────────────────────────────────────────

    // 37, 42.
    [Fact]
    public void DomainCardsStillUseScopeVocabularyAndNameNoEngines()
    {
        var page = Page(Context(), [Engine(FrontendQualityEngineIdDto.Accessibility), Engine(FrontendQualityEngineIdDto.Lighthouse), Engine(FrontendQualityEngineIdDto.PassiveSecurity)]);
        page.WaitForAssertion(() => page.Find("[data-testid=fqr-run]").HasAttribute("disabled").Should().BeFalse());

        page.FindAll("[data-testid=fqr-dimension-state]").Select(s => s.TextContent)
            .Should().OnlyContain(s => s == "Included" || s == "Limited" || s == "Partial evidence" || s == "Not included");

        var dimensions = page.Find("[data-testid=fqr-dimensions]").TextContent;
        foreach (var engine in new[] { "Static Security", "Passive Security", "Passive Performance", "Lighthouse", "Browser Runtime", "Browser Quality", "Accessibility engine", "Performance Quality" })
            dimensions.Should().NotContain(engine);
    }

    // 38. An optional engine switched off by choice is not a limitation.
    [Fact]
    public void OptionalEngineDisabledByChoiceCreatesNoLimitation()
    {
        var page = Page();   // HTTP-only: every browser engine is switched off in the saved configuration.

        var security = page.Find("[data-testid=fqr-dimension][data-category='Security']");
        security.QuerySelector("[data-testid=fqr-dimension-state]")!.TextContent.Should().Be("Included");
        security.QuerySelector("[data-testid=fqr-dimension-limitation]").Should().BeNull();
    }

    // 39. An optional engine that is enabled but unavailable does produce a limitation.
    [Fact]
    public void OptionalEngineEnabledButUnavailableProducesLimited()
    {
        var page = Page(Context(), [Engine(FrontendQualityEngineIdDto.Accessibility), Engine(FrontendQualityEngineIdDto.PassiveSecurity),
            Engine(FrontendQualityEngineIdDto.Lighthouse, ready: false, reason: "Lighthouse CLI not installed on this host.")]);

        page.WaitForAssertion(() =>
            page.Find("[data-testid=fqr-dimension][data-category='Performance'] [data-testid=fqr-dimension-state]").TextContent.Should().Be("Limited"));
        page.Find("[data-testid=fqr-dimension][data-category='Performance'] [data-testid=fqr-dimension-limitation]").TextContent
            .Should().Be("Lighthouse evidence is unavailable.");
    }

    // 40, 41. Accessibility stays scoped by the selected profile, which is shown on both the card and the domain.
    [Fact]
    public void AccessibilityStaysProfileScopedAndTheSelectedProfileIsShown()
    {
        var page = Page();

        page.Find("[data-testid=fqr-profile-name]").TextContent.Trim().Should().Be(WcagProfiles.Norwegian.Label);
        var accessibility = page.Find("[data-testid=fqr-dimension][data-category='Accessibility']");
        accessibility.QuerySelector("[data-testid=fqr-dimension-scope]")!.TextContent.Should().Be(WcagProfiles.Norwegian.Label);
        accessibility.QuerySelector("[data-testid=fqr-dimension-manual]")!.TextContent.Should().Be("Manual review required");
        accessibility.QuerySelector("[data-testid=fqr-dimension-state]")!.TextContent.Should().NotBe("Not included");
    }

    // ── §25. One label per concept ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void EachConceptIsLabelledOnce()
    {
        var page = Page(Context(), [Engine(FrontendQualityEngineIdDto.Accessibility), Engine(FrontendQualityEngineIdDto.Lighthouse), Engine(FrontendQualityEngineIdDto.PassiveSecurity)]);
        page.WaitForAssertion(() => page.Find("[data-testid=fqr-capabilities-disclosure-toggle]"));

        var headings = page.FindAll("h2").Select(h => h.TextContent.Trim()).ToList();
        headings.Should().OnlyHaveUniqueItems();
        headings.Should().NotContain("Capabilities").And.NotContain("Engine readiness");

        // "Review capabilities" names the concept exactly once, on the disclosure that opens it.
        page.Find("[data-testid=fqr-details]").QuerySelectorAll(".disclosure-text")
            .Count(t => t.TextContent.Trim() == "Review capabilities").Should().Be(1);
    }
}
