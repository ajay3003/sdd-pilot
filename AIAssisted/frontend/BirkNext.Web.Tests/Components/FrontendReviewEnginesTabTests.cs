using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Component = BirkNext.Web.Components.FrontendAnalysisSettings;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// The Frontend Review Engines tab configures Frontend Quality Review and nothing else. API Quality Review derives its
/// policy from Performance Thresholds and the Environment Type and its targets from Endpoint Discovery; Integration
/// Quality Review is driven by the per-integration Enabled flag under Integrations. Neither reads an engine toggle.
/// </summary>
public sealed class FrontendReviewEnginesTabTests : BunitContext
{
    private const string Url = "https://application.example.test/";
    private readonly FrontendAnalysisSettingsService _settings = new();

    public FrontendReviewEnginesTabTests()
    {
        Services.AddSingleton<IFrontendAnalysisSettingsService>(_settings);
        Services.AddSingleton(Mock.Of<ITargetEnvironmentDetectionApiService>());
        Services.AddSingleton<IEndpointDiscoveryService, EndpointDiscoveryService>();
        JSInterop.SetupVoid("birkNextStorage.setItem", _ => true).SetVoidResult();
        JSInterop.SetupVoid("birkNextStorage.setDiscovery", _ => true).SetVoidResult();
        JSInterop.Setup<string?>("birkNextStorage.getDiscovery").SetResult(null);
    }

    private IRenderedComponent<Component> Open(string featuresJson = "{}")
    {
        JSInterop.Setup<string?>("birkNextStorage.getItem", _ => true).SetResult($$"""
        {"activeProfileId":"dev","profiles":[
          {"id":"dev","name":"Dev","environmentType":"Development","targetUrl":"{{Url}}","features":{{featuresJson}}}
        ]}
        """);
        var cut = Render<Component>();
        cut.FindAll(".fa-profile-chip").Single(b => b.TextContent.Contains("Dev")).Click();
        OpenEngines(cut);
        return cut;
    }

    private static void OpenEngines(IRenderedComponent<Component> cut) =>
        cut.FindAll("[role=tab]").Single(t => t.TextContent.Trim() == "Frontend Review Engines").Click();

    private static IReadOnlyList<string> EngineNames(IRenderedComponent<Component> cut) =>
        cut.FindAll("[data-testid=engine-row] .fa-engine-name").Select(e => e.TextContent.Trim()).ToList();

    // ── 1–2. Dead legacy flags and the dead Future section are gone ──────────

    [Fact]
    public void DeadLegacyFlagsAndFutureSectionNoLongerRender()
    {
        var cut = Open();

        foreach (var dead in new[]
                 {
                     "Asset Discovery", "Startup Analysis", "REST Analysis", "GraphQL Analysis", "Caching Review",
                     "Compression Review", "Performance Readiness", "Security Header Review",
                     "Configuration Exposure Review", "Blazor Architecture Review",
                     "Authenticated Browser Review", "Lighthouse Integration", "Playwright Runtime Inspection",
                 })
            cut.Markup.Should().NotContain(dead, $"{dead} had no consumer and was removed");

        cut.Markup.Should().NotContain("Not yet implemented");
        cut.FindAll(".fa-features-group-future").Should().BeEmpty();
    }

    // ── 3, 11, 12, 13. Only the eight real engines render, each exactly once ──

    [Fact]
    public void OnlyTheEightRealEnginesRenderAndEachAppearsOnce()
    {
        var cut = Open();

        var names = EngineNames(cut);
        names.Should().BeEquivalentTo(new[]
        {
            "Static Security", "Passive Performance", "Browser Runtime", "Accessibility",
            "Lighthouse", "Passive Security", "Browser Quality", "BirkNext Performance Quality",
        });
        names.Should().OnlyHaveUniqueItems("Lighthouse and Browser Runtime must not appear twice");
        names.Count(n => n == "Lighthouse").Should().Be(1);
        names.Should().NotContain("Playwright Runtime Inspection");
    }

    // ── 4. Enabled is separated from capability ──────────────────────────────

    [Fact]
    public void EnabledIsSeparatedFromCapabilityRequirement()
    {
        var cut = Open();

        var table = cut.Find("[data-testid=frontend-review-engines-table]");
        table.QuerySelectorAll("thead th").Select(h => h.TextContent.Trim())
            .Should().Equal("Engine", "Saved state", "Coverage policy", "Prerequisites", "Capability");

        var lighthouse = cut.FindAll("[data-testid=engine-row]").Single(r => r.GetAttribute("data-engine-id") == "Lighthouse");
        lighthouse.QuerySelector("[data-testid=engine-enabled]")!.TextContent.Trim().Should().Be("Enabled");
        lighthouse.QuerySelector("[data-testid=engine-requires]")!.TextContent.Trim().Should().Be("Node + Chrome");

        // The page says in words that Enabled is not a capability claim.
        cut.Find("[data-testid=engines-capability-note]").TextContent
            .Should().Contain("Enabled is saved target configuration, not proof that the engine is available");
        cut.Find("[data-testid=engines-open-review]").GetAttribute("href").Should().Be("/frontend-quality-review");
    }

    [Fact]
    public void EveryEngineDeclaresItsRuntimePrerequisite()
    {
        var cut = Open();

        var requires = cut.FindAll("[data-testid=engine-row]")
            .ToDictionary(r => r.GetAttribute("data-engine-id")!, r => r.QuerySelector("[data-testid=engine-requires]")!.TextContent.Trim());

        requires["StaticSecurity"].Should().Be("Public HTTP");
        requires["PassivePerformance"].Should().Be("Public HTTP");
        requires["PassiveSecurity"].Should().Contain("Container runtime");
        requires["Accessibility"].Should().Contain("Browser DOM");
        requires["BrowserRuntime"].Should().Contain("Playwright");
        requires["BrowserQuality"].Should().Contain("Browser Companion");
        requires["PerformanceQuality"].Should().Contain("Local HTTPS proxy");
        requires.Values.Should().NotContain("—");
    }

    // ── 5. Policy renders separately from Enabled ────────────────────────────

    [Fact]
    public void RequiredOptionalPolicyRendersSeparatelyAndRequiredComesFirst()
    {
        var cut = Open();

        var rows = cut.FindAll("[data-testid=engine-row]");
        var policies = rows.Select(r => r.QuerySelector("[data-testid=engine-policy]")!.TextContent.Trim()).ToList();
        policies.Take(2).Should().AllBe("Required");
        policies.Skip(2).Should().AllBe("Optional");

        // Policy is a separate cell from the enabled state, not a suffix on the name.
        rows[0].QuerySelector("[data-testid=engine-policy]").Should().NotBeSameAs(rows[0].QuerySelector("[data-testid=engine-enabled]"));
        EngineNames(cut).Should().NotContain(n => n.Contains("(Required)") || n.Contains("(Optional)"));

        cut.Find("[data-testid=engines-policy-note]").TextContent
            .Should().Contain("must be assessed").And.Contain("does not mean the engine must pass");
    }

    // ── 6. Required-but-disabled warning ─────────────────────────────────────

    [Fact]
    public void DefaultConfigurationRaisesNoRequiredButDisabledWarning()
    {
        var cut = Open();
        cut.FindAll("[data-testid=engines-required-disabled]").Should().BeEmpty("nothing is disabled by default");
    }

    /// <summary>Disabling a Required engine stays permitted — the backend models it as an inconsistency, not a block.</summary>
    [Fact]
    public void RequiredButDisabledRendersAWarningAndIsStillAllowed()
    {
        var cut = Open("""{"enableSecurityEngine": false}""");

        var warning = cut.Find("[data-testid=engines-required-disabled]").TextContent;
        warning.Should().Contain("Static Security").And.Contain("Required coverage cannot complete");
        cut.FindAll("[data-testid=engine-row]").Single(r => r.GetAttribute("data-engine-id") == "StaticSecurity")
            .QuerySelector("[data-testid=engine-enabled]")!.TextContent.Trim().Should().Be("Disabled");
    }

    // ── 7–8. The tab does not imply it controls the other two review pages ───

    [Fact]
    public void TheTabStatesItsScopeIsFrontendQualityReviewOnly()
    {
        var cut = Open();

        var scope = cut.Find("[data-testid=engines-scope-note]").TextContent;
        scope.Should().Contain("Frontend Quality Review only");
        scope.Should().Contain("API Quality Review").And.Contain("Integration Quality Review").And.Contain("use their own review configuration");

        // No engine row claims an API or Integration review concern.
        foreach (var name in EngineNames(cut))
            name.Should().NotContainAny("API Quality", "Integration Quality");
    }

    // ── 9–10. Discovery surfaces are independent of engine activation ────────

    [Fact]
    public void BrowserAndEndpointDiscoveryRemainUsableWithEveryEngineDisabled()
    {
        var cut = Open("""
        {"enableSecurityEngine":false,"enablePerformanceEngine":false,"enableBrowserRuntimeEngine":false,
         "enableAccessibilityEngine":false,"enableLighthouseEngine":false,"enablePassiveSecurityEngine":false,
         "enableBrowserQualityEngine":false,"enablePerformanceQualityEngine":false}
        """);

        cut.FindAll("[role=tab]").Single(t => t.TextContent.Trim() == "Browser Discovery").Click();
        cut.FindAll("[data-testid=browser-discovery]").Should().ContainSingle("evidence collection is not a review engine");
        cut.FindAll("[data-testid=browser-companion-panel]").Should().ContainSingle();

        cut.FindAll("[role=tab]").Single(t => t.TextContent.Trim() == "Endpoint Discovery").Click();
        cut.FindAll("[data-testid=endpoint-discovery]").Should().ContainSingle("network evidence is not a review engine");
    }

    // ── 14. One restore action, scoped to engine configuration ───────────────

    [Fact]
    public void RestoreDefaultActionIsTheOnlyActionAndOnlyResetsEngineConfiguration()
    {
        var cut = Open("""{"enableSecurityEngine": false, "enableBrowserQualityEngine": true}""");

        var actions = cut.FindAll(".fa-reset-row button").Select(b => b.TextContent.Trim()).ToList();
        actions.Should().Equal("Restore default engine selection");
        actions.Should().NotContain("Enable Core Features").And.NotContain("Disable Future Features");

        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Restore default engine selection").Click();
        _settings.Settings.Profiles.Single(p => p.Id == "dev").Features.EnableSecurityEngine.Should().BeFalse("opening the confirmation changes nothing");
        cut.Find("[data-testid=engines-restore-confirm] button").Click();

        var persisted = _settings.Settings.Profiles.Single(p => p.Id == "dev").Features;
        persisted.EnableSecurityEngine.Should().BeTrue("the Required engine returns to its default");
        persisted.EnableBrowserQualityEngine.Should().BeFalse("opt-in engines return to opt-in");
    }

    // ── 11. Two-way relationship with the system capability surface ─────────

    [Fact]
    public void EnginesTabLinksToBothTheReviewAndTheSystemCapabilityDetails()
    {
        var cut = Open();

        var review = cut.Find("[data-testid=engines-open-review]");
        review.GetAttribute("href").Should().Be("/frontend-quality-review");

        var capabilities = cut.Find("[data-testid=engines-open-capabilities]");
        capabilities.TagName.Should().Be("A");
        capabilities.TextContent.Trim().Should().Be("View system capability details");
        capabilities.GetAttribute("href").Should().Be("/admin/system-settings?section=frontend-quality-engines");
    }

    // ── Editing writes to the draft, not straight to storage ─────────────────

    [Fact]
    public void ToggllingAnEngineInEditModeChangesTheDraftOnly()
    {
        var cut = Open();
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Edit Environment").Click();
        OpenEngines(cut);

        var lighthouse = cut.FindAll("[data-testid=engine-row]").Single(r => r.GetAttribute("data-engine-id") == "Lighthouse");
        lighthouse.QuerySelector("[data-testid=engine-enabled-input]")!.Change(false);

        cut.FindAll("[data-testid=engine-row]").Single(r => r.GetAttribute("data-engine-id") == "Lighthouse")
            .QuerySelector("[data-testid=engine-enabled]")!.TextContent.Trim().Should().Be("Disabled");
        _settings.Settings.Profiles.Single(p => p.Id == "dev").Features.EnableLighthouseEngine
            .Should().BeTrue("the change lives in the draft until Save changes");
    }

    // ── Clarity cleanup: owners per column, neutral Disabled, explicit help, scoped restore ──

    private static AngleSharp.Dom.IElement Engine(IRenderedComponent<Component> cut, string id) =>
        cut.FindAll("[data-testid=engine-row]").Single(r => r.GetAttribute("data-engine-id") == id);

    [Fact]
    public void AnEnabledRequiredHttpEngineReadsAsSavedStatePolicyPrerequisiteAndFqrCapability()
    {
        var cut = Open();
        var row = Engine(cut, "StaticSecurity");
        row.QuerySelector(".fa-engine-clarification")!.TextContent.Should().Be("Anonymous HTTP security review");
        row.QuerySelector("[data-testid=engine-enabled]")!.TextContent.Trim().Should().Be("Enabled");
        row.QuerySelector("[data-testid=engine-policy]")!.TextContent.Trim().Should().Be("Required");
        row.QuerySelector("[data-testid=engine-policy-help]")!.TextContent.Should().Be("Must be assessed for required coverage. It does not have to pass.");
        row.QuerySelector("[data-testid=engine-requires]")!.TextContent.Trim().Should().Be("Public HTTP");
        row.QuerySelector("[data-testid=engine-capability]")!.TextContent.Trim().Should().Be("Checked in FQR");
        foreach (var (cell, label) in new[] { (1, "Saved state"), (2, "Coverage policy"), (3, "Prerequisites"), (4, "Capability") })
            row.Children[cell].GetAttribute("data-label").Should().Be(label, "stacked rows keep their column label");
    }

    [Fact]
    public void EveryRowNamesFqrAsCapabilityOwner_NeverALiveAvailabilityState()
    {
        var cut = Open("""{"enableBrowserRuntimeEngine": false}""");
        cut.FindAll("[data-testid=engine-capability]").Select(c => c.TextContent.Trim()).Should().HaveCount(8).And.OnlyContain(t => t == "Checked in FQR");
        var table = cut.Find("[data-testid=frontend-review-engines-table]").TextContent;
        table.Should().NotContainAny("Available", "Unavailable", "Ready", "Missing", "Not installed", "Failed");
        cut.Find("[data-testid=engines-capability-help]").TextContent.Should().Be("Actual availability is checked by Frontend Quality Review at review time. This page runs no capability checks.");
        cut.FindAll("thead th").Single(h => h.TextContent.Trim() == "Capability").GetAttribute("aria-describedby").Should().Be("engines-help-capability");
        cut.Find("#engines-help-capability").Should().NotBeNull("the header's help target exists");
    }

    [Fact]
    public void ADisabledOptionalEngineRendersNeutrally()
    {
        var cut = Open("""{"enableBrowserRuntimeEngine": false}""");
        var row = Engine(cut, "BrowserRuntime");
        row.GetAttribute("data-enabled").Should().Be("false");
        row.QuerySelector("[data-testid=engine-enabled]")!.TextContent.Trim().Should().Be("Disabled");
        row.QuerySelector("[data-testid=engine-policy]")!.TextContent.Trim().Should().Be("Optional");
        row.QuerySelector("[data-testid=engine-policy-help]")!.TextContent.Should().Contain("not required for required coverage completion");
        row.QuerySelector("[data-testid=engine-requires]")!.TextContent.Trim().Should().Be("Browser DOM (Playwright)");
        row.InnerHtml.Should().NotContainAny("error", "fail", "danger", "warning", "Unavailable");
        cut.FindAll("[data-testid=engines-required-disabled]").Should().BeEmpty("disabling an Optional engine is not an inconsistency");
    }

    [Fact]
    public void ADisabledRequiredEngineKeepsItsRequiredPolicy()
    {
        var cut = Open("""{"enablePerformanceEngine": false}""");
        var row = Engine(cut, "PassivePerformance");
        (row.QuerySelector("[data-testid=engine-enabled]")!.TextContent.Trim(), row.QuerySelector("[data-testid=engine-policy]")!.TextContent.Trim()).Should().Be(("Disabled", "Required"));
        cut.Find("[data-testid=engines-required-disabled]").TextContent.Should().Contain("Passive Performance");
    }

    [Fact]
    public void HelpExplainsEnabledRequiredAndCapabilityCompactly()
    {
        var cut = Open();
        var help = cut.Find("[data-testid=engines-help]");
        help.QuerySelectorAll("dt").Select(d => d.TextContent).Should().Equal("Saved state", "Coverage policy", "Capability");
        cut.Find("[data-testid=engines-policy-note]").TextContent.Should().Contain("Required means the engine must be assessed for required coverage. It does not mean the engine must pass");
        cut.Find("[data-testid=engines-capability-note]").TextContent.Should().Contain("Controls whether the engine is selected for this target");
        cut.FindAll(".fa-engines-note").Should().BeEmpty("the long footer prose is replaced by the labelled help");
    }

    [Fact]
    public void LinksAreExplicitAndSeparate()
    {
        var cut = Open();
        var links = cut.Find("[data-testid=engines-links]").QuerySelectorAll("a");
        links.Select(a => (a.TextContent.Trim(), a.GetAttribute("href"))).Should().Equal(
            ("Open Frontend Quality Review", "/frontend-quality-review"), ("View system capability details", "/admin/system-settings?section=frontend-quality-engines"));
    }

    [Fact]
    public void RestoreNeedsConfirmation_StatesItsScope_AndCancelChangesNothing()
    {
        var cut = Open("""{"enableSecurityEngine": false}""");
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Restore default engine selection").Click();
        var confirm = cut.Find("[data-testid=engines-restore-confirm]");
        confirm.GetAttribute("role").Should().Be("group");
        cut.Find("[data-testid=engines-restore-text]").TextContent.Should().Be(
            "This restores the default Enabled/Disabled selection for this Target Environment. Coverage policy, other Target Environments, system capabilities, installed tools and Frontend Quality Review history are not changed.");
        cut.Find("[data-testid=engines-restore-cancel]").Click();
        cut.FindAll("[data-testid=engines-restore-confirm]").Should().BeEmpty();
        _settings.Settings.Profiles.Single(p => p.Id == "dev").Features.EnableSecurityEngine.Should().BeFalse("cancel leaves the saved selection as it was");
    }

    [Fact]
    public void RestoreChangesOnlyThisTargetsSelection_NotPolicyOrOtherTargets()
    {
        JSInterop.Setup<string?>("birkNextStorage.getItem", _ => true).SetResult($$$"""
        {"activeProfileId":"dev","profiles":[
          {"id":"dev","name":"Dev","environmentType":"Development","targetUrl":"{{{Url}}}","features":{"enableSecurityEngine":false,"enableLighthouseEngine":false},"engineRequirements":{"accessibility":"Required"}},
          {"id":"qa","name":"QA","environmentType":"QA","targetUrl":"https://qa.example.test/","features":{"enableSecurityEngine":false}}
        ]}
        """);
        var cut = Render<Component>();
        cut.FindAll(".fa-profile-chip").Single(b => b.TextContent.Contains("Dev")).Click();
        OpenEngines(cut);
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Restore default engine selection").Click();
        cut.Find("[data-testid=engines-restore-confirm] button").Click();

        var dev = _settings.Settings.Profiles.Single(p => p.Id == "dev");
        var qa = _settings.Settings.Profiles.Single(p => p.Id == "qa");
        (dev.Features.EnableSecurityEngine, dev.Features.EnableLighthouseEngine).Should().Be((true, true), "this target returns to the default selection");
        dev.EngineRequirements.Accessibility.Should().Be(FrontendQualityEngineRequirement.Required, "coverage policy is not part of the restore");
        qa.Features.EnableSecurityEngine.Should().BeFalse("another Target Environment is never touched");
        JSInterop.Invocations.Select(i => i.Identifier).Should().OnlyContain(i => i.StartsWith("birkNextStorage."), "load, save and restore call no capability probe");
    }

    [Fact]
    public void ControlsAreNativeAndKeyboardReachable()
    {
        var cut = Open();
        cut.Find(".fa-engines-tablewrap").GetAttribute("tabindex").Should().Be("0");
        cut.FindAll("[data-testid=engines-links] a").Should().OnlyContain(a => a.HasAttribute("href"));
        cut.FindAll(".fa-reset-row button").Should().ContainSingle().Which.GetAttribute("type").Should().Be("button");
        cut.FindAll("[data-testid=frontend-review-engines-table] button, [data-testid=frontend-review-engines-table] a").Should().BeEmpty("no nested interactive controls in view mode");
    }
}
