using BirkNext.Web.Pages.Admin;
using Bunit;
using FluentAssertions;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// System Settings → Frontend Engine Capabilities is the admin/diagnostic answer to "what can this installation run?".
/// Target Environment → Frontend Review Engines answers "what does this profile want?", and Frontend Quality Review
/// answers "what is available for this review, and what ran?". This surface must never read as a second profile editor.
/// </summary>
public partial class SystemSettingsEnvironmentDiagnosticsTests
{
    private const string ReviewEnginesHref = "/admin/system-settings?section=target-environments&tab=features";

    private IRenderedComponent<SystemSettings> OpenCapabilities()
    {
        var cut = Render<SystemSettings>();
        cut.WaitForAssertion(() => FindButton(cut, "Frontend Engine Capabilities").Should().NotBeNull());
        FindButton(cut, "Frontend Engine Capabilities")!.Click();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=fec-intro]").Should().NotBeEmpty());
        return cut;
    }

    // ── 1–2. Naming ─────────────────────────────────────────────────────────

    [Fact]
    public void NavigationAndCardTitleUseTheCapabilityWording()
    {
        var cut = OpenCapabilities();

        FindButton(cut, "Frontend Engine Capabilities").Should().NotBeNull("the navigation item is renamed");
        cut.FindAll(".settings-card-title").Select(t => t.TextContent.Trim())
            .Should().Contain("Frontend Engine Capabilities");

        // The old name must not linger anywhere on the rendered page.
        cut.Markup.Should().NotContain("Frontend Quality Engines");
    }

    // ── 3. Copy distinguishes installation capability from profile activation ─

    [Fact]
    public void IntroSeparatesInstallationCapabilityFromTargetEnvironmentActivation()
    {
        var cut = OpenCapabilities();

        var intro = cut.Find("[data-testid=fec-intro]").TextContent;
        intro.Should().Contain("what this BirkNext installation can run");
        intro.Should().Contain("Installation policy and runtime readiness");
        intro.Should().Contain("configured separately under Frontend Review Engines");
    }

    [Fact]
    public void LayerLegendNamesAllFourLayersAndTheirOwners()
    {
        var cut = OpenCapabilities();

        var layers = cut.Find("[data-testid=fec-layers]");
        layers.QuerySelectorAll("dt").Select(d => d.TextContent.Trim()).Should().Equal(
            "Installation policy", "Deployment setting", "Profile activation", "Runtime readiness", "Effective availability");

        layers.TextContent.Should().Contain("Whether this deployment permits the engine");
        layers.TextContent.Should().Contain("Configured under Frontend Review Engines",
            "profile activation is explicitly owned elsewhere");
    }

    // ── 4. No per-Target-Environment controls here ──────────────────────────

    [Fact]
    public void NoTargetEnvironmentEngineTogglesAreIntroducedOnThisPage()
    {
        var cut = OpenCapabilities();

        // The engine-activation table belongs to Target Environment, not here.
        cut.FindAll("[data-testid=frontend-review-engines-table]").Should().BeEmpty();
        cut.FindAll("[data-testid=engine-enabled-input]").Should().BeEmpty();
        cut.FindAll("[data-testid=frontend-review-engines]").Should().BeEmpty();

        // The one editable row is deployment-scoped and labelled as such.
        cut.FindAll("[data-testid=fqe-deployment-setting]").Should().NotBeEmpty();
        cut.Markup.Should().NotContain(">System setting<", "the ambiguous label was renamed");
    }

    // ── 5–9. Capability diagnostics are preserved ───────────────────────────

    [Fact]
    public void CapabilityCardsKeepEveryDiagnosticRow()
    {
        var cut = OpenCapabilities();

        foreach (var row in new[]
                 {
                     "fqe-installation-policy", "fqe-deployment-setting",
                     "fqe-runtime-readiness", "fqe-authenticated-review", "fqe-effective-availability",
                 })
            cut.FindAll($"[data-testid={row}]").Should().NotBeEmpty(row);

        // Distinct vocabularies, never merged into one status.
        cut.Markup.Should().Contain("Allowed").And.Contain("Not allowed");
        cut.Markup.Should().Contain("Ready").And.Contain("Unavailable");
        cut.Markup.Should().Contain("Supported").And.Contain("Not supported");
        cut.Markup.Should().Contain("Available");
    }

    [Fact]
    public void ReasonsRenderForUnavailableEngines()
    {
        var cut = OpenCapabilities();

        // The fixture has Passive Security blocked by deployment policy and disabled in System Settings.
        var reasons = cut.FindAll(".fqe-reasons");
        reasons.Should().NotBeEmpty("an unavailable engine explains itself here");
        var text = string.Join(" ", reasons.Select(r => r.TextContent));
        text.Should().Contain("Not allowed on this installation").And.Contain("Disabled in System Settings");
    }

    [Fact]
    public void EngineStatesRemainIndividuallyDistinguishable()
    {
        var cut = OpenCapabilities();

        var sections = cut.FindAll(".dev-diag-section");
        string Card(string engine) => sections.Single(s => s.QuerySelector(".dev-diag-section-title")!.TextContent.Trim() == engine).TextContent;

        // Allowed + ready + available.
        Card("Browser Runtime").Should().Contain("Allowed").And.Contain("Ready").And.Contain("Available");
        // Allowed but not runtime-ready.
        Card("Accessibility").Should().Contain("Allowed").And.Contain("Unavailable");
        // Not allowed by installation policy.
        Card("Passive Security (ZAP)").Should().Contain("Not allowed").And.Contain("Unavailable");
        // Authenticated-review support is its own axis.
        Card("Lighthouse").Should().Contain("Not supported");
    }

    // ── 10. Cross-link to the profile configuration ─────────────────────────

    [Fact]
    public void LinksToFrontendReviewEnginesUsingTheExistingTabRouting()
    {
        var cut = OpenCapabilities();

        var link = cut.Find("[data-testid=fec-open-review-engines]");
        link.TagName.Should().Be("A");
        link.TextContent.Trim().Should().Be("Edit Target Environment engines");
        link.GetAttribute("href").Should().Be(ReviewEnginesHref);
    }

    // ── 12. Frontend-specific: no API / Integration review implications ─────

    [Fact]
    public void NothingImpliesThisPageControlsApiOrIntegrationQualityReview()
    {
        var cut = OpenCapabilities();

        var card = cut.FindAll(".settings-card-full-width")
            .Single(c => c.QuerySelector(".settings-card-title")?.TextContent.Trim() == "Frontend Engine Capabilities");

        card.TextContent.Should().NotContain("API Quality Review");
        card.TextContent.Should().NotContain("Integration Quality Review");
        foreach (var link in card.QuerySelectorAll("a"))
        {
            var href = link.GetAttribute("href") ?? "";
            href.Should().NotContain("api-quality-review").And.NotContain("integration-quality-review");
        }
    }
}
