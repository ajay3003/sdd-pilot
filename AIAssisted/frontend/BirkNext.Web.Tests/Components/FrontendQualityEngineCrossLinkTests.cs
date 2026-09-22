using BirkNext.BrowserCompanion;
using BirkNext.Web.Components;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Frontend Quality Review reports engine state and points at where that state is configured; it never becomes a second
/// engine editor. Target Environment → Frontend Review Engines owns saved activation and Required/Optional policy.
/// </summary>
public sealed class FrontendQualityEngineCrossLinkTests : BunitContext
{
    private const string Url = "https://application.example.test/";
    private const string EnginesHref = "/admin/system-settings?section=target-environments&tab=features";

    private static FrontendAnalysisContext Context(Action<FrontendAnalysisFeatureToggles>? toggles = null)
    {
        var profile = new FrontendAnalysisProfile
        {
            Id = "dev", Name = "Dev", EnvironmentType = FrontendEnvironmentType.Development, TargetUrl = Url,
        };
        toggles?.Invoke(profile.Features);
        return new FrontendAnalysisContext
        {
            ActiveProfile = profile, TargetUrl = Url, FeatureToggles = profile.Features,
            EngineRequirements = profile.EngineRequirements, ReviewEngineSelection = profile.ReviewEngineSelection,
        };
    }

    private static IReadOnlyList<FrontendQualityCapabilityRow> Rows(
        FrontendAnalysisContext context, FrontendQualityEngineStatusReportDto? status = null, BrowserCompanionState? companion = null) =>
        FrontendQualityLandingPresentation.Capabilities(
            context, FrontendQualityActiveEngines.Resolve(context), status, false, false,
            FrontendQualityTargetAccess.FromContext(context), companion);

    private IRenderedComponent<FrontendQualityCapabilityList> Render(IReadOnlyList<FrontendQualityCapabilityRow> rows) =>
        Render<FrontendQualityCapabilityList>(p => p.Add(c => c.Rows, rows));

    // ── 1, 2, 10. The cross-link exists, deep-links, and is a real link ──────

    [Fact]
    public void CapabilitiesHeaderRendersAnEditEnginesLinkToTheFrontendReviewEnginesTab()
    {
        var cut = Render(Rows(Context()));

        var link = cut.Find("[data-testid=fqr-edit-engines]");
        link.TextContent.Trim().Should().Be("Edit engines");
        link.TagName.Should().Be("A", "a navigation action is a link, so keyboard and middle-click work normally");
        link.GetAttribute("href").Should().Be(EnginesHref);
        // It targets the engines tab specifically, not the generic settings page.
        link.GetAttribute("href").Should().Contain("tab=features");
    }

    [Fact]
    public void TheLinkSitsInTheCapabilityHeaderNextToTheEngineSummary()
    {
        var cut = Render(Rows(Context()));

        var head = cut.Find(".fqr-section-head");
        head.QuerySelector("[data-testid=fqr-edit-engines]").Should().NotBeNull("the action belongs with the summary, not at the page bottom");
        // The block is named once, by the disclosure that hosts it, so it carries no heading of its own.
        head.QuerySelector("h2").Should().BeNull();

        // 16, 17. The three axes, each with its own count: configuration, capability, and what is switched off.
        var summary = cut.Find("[data-testid=fqr-engine-summary]").TextContent;
        summary.Should().Be("5 enabled · 5 available · 3 disabled");
        // The disabled engines are never folded into the unavailable ones.
        summary.Should().NotContain("unavailable");
        cut.Find("[data-testid=fqr-engine-configuration-note]").TextContent
            .Should().Contain("configured per Target Environment");
    }

    // ── 3. FQR does not edit engine activation ──────────────────────────────

    [Fact]
    public void CapabilityListOffersNoEngineActivationOrPolicyEditing()
    {
        var cut = Render(Rows(Context()));

        // The only control is the per-review include opt-out, which never enables a disabled engine.
        var inputs = cut.FindAll("input");
        inputs.Should().OnlyContain(i => i.GetAttribute("data-testid") == "fqr-capability-include");
        cut.FindAll("select").Should().BeEmpty("Required/Optional policy is not edited here");
        cut.FindAll("[data-testid=engine-enabled-input]").Should().BeEmpty();
        cut.FindAll("[data-testid=frontend-review-engines-table]").Should().BeEmpty("no second configuration table");
    }

    // ── 4, 9. Enabled stays distinct from available; a disabled engine stays distinct ──

    [Fact]
    public void EnabledAndAvailableAreDistinctAndAnUnavailableEngineIsNotShownAsDisabled()
    {
        // Lighthouse is enabled by default; report it as runtime-unavailable.
        var status = new FrontendQualityEngineStatusReportDto
        {
            Engines =
            [
                new()
                {
                    EngineId = FrontendQualityEngineIdDto.Lighthouse, DisplayName = "Lighthouse",
                    Layer1Allowed = true, Layer2Enabled = true, Available = false,
                    Layer3Readiness = new() { EngineId = FrontendQualityEngineIdDto.Lighthouse, IsAvailable = false, StatusReason = "Node runtime not found." },
                }
            ],
        };
        var cut = Render(Rows(Context(), status));

        var lighthouse = cut.FindAll("[data-testid=fqr-capability]").Single(r => r.GetAttribute("data-engine-id") == "Lighthouse");
        lighthouse.QuerySelector("[data-testid=fqr-capability-state]")!.TextContent.Trim().Should().Be("Unavailable");
        lighthouse.QuerySelector("[data-testid=fqr-capability-enabled]")!.TextContent.Trim()
            .Should().Be("Enabled", "an unavailable engine must not read as switched off");
        lighthouse.QuerySelector("[data-testid=fqr-capability-summary]")!.TextContent
            .Should().Contain("not available in this environment").And.NotContain("Disabled");
    }

    [Fact]
    public void ADisabledEngineReadsAsDisabledAndOffersTheEditEnginesAction()
    {
        var cut = Render(Rows(Context(f => f.EnableLighthouseEngine = false)));

        var lighthouse = cut.FindAll("[data-testid=fqr-capability]").Single(r => r.GetAttribute("data-engine-id") == "Lighthouse");
        lighthouse.QuerySelector("[data-testid=fqr-capability-state]")!.TextContent.Trim().Should().Be("Disabled");
        lighthouse.QuerySelectorAll("[data-testid=fqr-capability-enabled]").Should().BeEmpty("it is not enabled");
        var action = lighthouse.QuerySelector("[data-testid=fqr-capability-action]")!;
        action.TextContent.Trim().Should().Be("Edit engines");
        action.GetAttribute("href").Should().Be(EnginesHref);
    }

    // ── 5. Required-but-disabled is a configuration inconsistency ────────────

    [Fact]
    public void RequiredButDisabledShowsACoverageWarningWithTheEditEnginesAction()
    {
        var cut = Render(Rows(Context(f => f.EnableSecurityEngine = false)));

        var warning = cut.Find("[data-testid=fqr-capabilities-required-disabled]");
        warning.TextContent.Should().Contain("Required engine disabled").And.Contain("coverage cannot complete");
        warning.TextContent.Should().Contain("Static Security");
        warning.GetAttribute("role").Should().Be("status", "the warning is announced, not colour-only");
        warning.QuerySelector("[data-testid=fqr-required-disabled-edit]")!.GetAttribute("href").Should().Be(EnginesHref);

        // It is a configuration problem, never an execution failure.
        warning.TextContent.Should().NotContainAny("Failed", "Error", "Crashed");
    }

    [Fact]
    public void NoWarningWhenEveryRequiredEngineIsEnabled()
    {
        var cut = Render(Rows(Context()));
        cut.FindAll("[data-testid=fqr-capabilities-required-disabled]").Should().BeEmpty();
    }

    // ── 6, 7. Browser-dependent engines keep their own states ───────────────

    [Theory]
    [InlineData(BrowserCompanionState.NotPaired, "Not configured", "Pair the Browser Companion")]
    [InlineData(BrowserCompanionState.Disconnected, "Requires browser session", "not reporting")]
    [InlineData(BrowserCompanionState.Connected, "Ready", "connected")]
    public void BrowserDependentEnginesReportPairingAndEvidenceStatesWithoutFailureWording(
        BrowserCompanionState companion, string expectedState, string expectedSummary)
    {
        var context = Context(f => { f.EnableBrowserQualityEngine = true; f.EnablePerformanceQualityEngine = true; });
        var cut = Render(Rows(context, companion: companion));

        foreach (var engineId in new[] { "BrowserQuality", "PerformanceQuality" })
        {
            var row = cut.FindAll("[data-testid=fqr-capability]").Single(r => r.GetAttribute("data-engine-id") == engineId);
            row.QuerySelector("[data-testid=fqr-capability-state]")!.TextContent.Trim().Should().Be(expectedState);
            row.QuerySelector("[data-testid=fqr-capability-summary]")!.TextContent.Should().Contain(expectedSummary);
            row.TextContent.Should().NotContainAny("Failed", "Failure", "Error");
        }
    }

    // ── 8, 9 (cross-page hygiene) ───────────────────────────────────────────

    [Fact]
    public void NoFeatureTogglesWordingAndNoApiOrIntegrationSettingsLinks()
    {
        var cut = Render(Rows(Context(f => f.EnableSecurityEngine = false)));

        cut.Markup.Should().NotContain("Feature Toggles");
        cut.Markup.Should().NotContain("Feature toggles");

        foreach (var link in cut.FindAll("a"))
        {
            var href = link.GetAttribute("href") ?? "";
            href.Should().NotContain("api-quality-review", "this control is frontend-specific");
            href.Should().NotContain("integration-quality-review");
        }
        cut.Markup.Should().NotContain("API Quality Review").And.NotContain("Integration Quality Review");
    }

    // ── Display names stay user-facing ──────────────────────────────────────

    [Fact]
    public void EngineNamesUseTheUserFacingLabels()
    {
        var cut = Render(Rows(Context()));

        cut.FindAll("[data-testid=fqr-capability] .fqr-capability-name").Select(n => n.TextContent.Trim())
            .Should().BeEquivalentTo(new[]
            {
                "Static Security", "Passive Performance", "Browser Runtime", "Accessibility",
                "Lighthouse", "Passive Security", "Browser Quality", "BirkNext Performance Quality",
            });
    }

    // ── Active-engines panel offers the same action ─────────────────────────

    [Fact]
    public void ActiveEnginesPanelRequiredDisabledWarningLinksToEngineConfiguration()
    {
        var context = Context(f => f.EnablePerformanceEngine = false);
        var snapshot = FrontendQualityActiveEngines.Resolve(context);
        var cut = Render<FrontendQualityActiveEnginesPanel>(p => p.Add(c => c.Snapshot, snapshot));

        var warning = cut.Find("[data-testid=fqr-required-disabled]");
        warning.TextContent.Should().Contain("Passive Performance");
        warning.QuerySelector("[data-testid=fqr-required-disabled-action]")!.GetAttribute("href").Should().Be(EnginesHref);
    }
}

/// <summary>The deep link opens Target Environment with the Frontend Review Engines tab already selected.</summary>
public sealed class FrontendReviewEnginesDeepLinkTests : BunitContext
{
    [Fact]
    public void InitialTabSelectsTheFrontendReviewEnginesTab()
    {
        var settings = new FrontendAnalysisSettingsService();
        Services.AddSingleton<IFrontendAnalysisSettingsService>(settings);
        Services.AddSingleton(Mock.Of<ITargetEnvironmentDetectionApiService>());
        Services.AddSingleton<IEndpointDiscoveryService, EndpointDiscoveryService>();
        JSInterop.Mode = JSRuntimeMode.Loose;
        JSInterop.Setup<string?>("birkNextStorage.getItem", _ => true).SetResult("""
            {"activeProfileId":"dev","profiles":[{"id":"dev","name":"Dev","targetUrl":"https://application.example.test"}]}
            """);

        var cut = Render<BirkNext.Web.Components.FrontendAnalysisSettings>(p => p.Add(c => c.InitialTab, "features"));

        cut.Find("#target-tab-features").GetAttribute("aria-selected").Should().Be("true");
        cut.FindAll("[data-testid=frontend-review-engines]").Should().ContainSingle();
    }

    [Fact]
    public void WithoutTheParameterTheGeneralTabStaysSelected()
    {
        var settings = new FrontendAnalysisSettingsService();
        Services.AddSingleton<IFrontendAnalysisSettingsService>(settings);
        Services.AddSingleton(Mock.Of<ITargetEnvironmentDetectionApiService>());
        Services.AddSingleton<IEndpointDiscoveryService, EndpointDiscoveryService>();
        JSInterop.Mode = JSRuntimeMode.Loose;
        JSInterop.Setup<string?>("birkNextStorage.getItem", _ => true).SetResult("""
            {"activeProfileId":"dev","profiles":[{"id":"dev","name":"Dev","targetUrl":"https://application.example.test"}]}
            """);

        var cut = Render<BirkNext.Web.Components.FrontendAnalysisSettings>();

        cut.Find("#target-tab-general").GetAttribute("aria-selected").Should().Be("true");
        cut.FindAll("[data-testid=frontend-review-engines]").Should().BeEmpty();
    }
}
