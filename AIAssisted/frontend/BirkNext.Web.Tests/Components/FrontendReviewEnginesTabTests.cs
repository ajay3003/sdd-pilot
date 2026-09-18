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
            .Should().Equal("Engine", "Enabled", "Policy", "Requires");

        var lighthouse = cut.FindAll("[data-testid=engine-row]").Single(r => r.GetAttribute("data-engine-id") == "Lighthouse");
        lighthouse.QuerySelector("[data-testid=engine-enabled]")!.TextContent.Trim().Should().Be("Enabled");
        lighthouse.QuerySelector("[data-testid=engine-requires]")!.TextContent.Trim().Should().Be("Node + Chrome");

        // The page says in words that Enabled is not a capability claim.
        cut.Find("[data-testid=engines-capability-note]").TextContent
            .Should().Contain("not a capability").And.Contain("live capability status");
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
        scope.Should().Contain("API Quality Review").And.Contain("Integration Quality Review").And.Contain("not affected");

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
        actions.Should().Equal("Restore default engine configuration");
        actions.Should().NotContain("Enable Core Features").And.NotContain("Disable Future Features");

        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Restore default engine configuration").Click();

        var persisted = _settings.Settings.Profiles.Single(p => p.Id == "dev").Features;
        persisted.EnableSecurityEngine.Should().BeTrue("the Required engine returns to its default");
        persisted.EnableBrowserQualityEngine.Should().BeFalse("opt-in engines return to opt-in");
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
}
