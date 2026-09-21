using BirkNext.Web.Components;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// The six FQR cards describe review DOMAINS, not engine states. A domain draws on several engines, so one optional
/// engine being unavailable is a limitation in evidence — never an unavailable domain. Engine vocabulary
/// (Enabled/Unavailable/Needs pairing) belongs to Review capabilities and must not leak into these cards.
/// </summary>
public sealed class FrontendQualityDomainSemanticsTests : BunitContext
{
    private const string Url = "https://application.example.test/";

    private static FrontendAnalysisContext Context(Action<FrontendAnalysisFeatureToggles>? toggles = null)
    {
        var profile = new FrontendAnalysisProfile { Id = "dev", Name = "Dev", EnvironmentType = FrontendEnvironmentType.Development, TargetUrl = Url };
        toggles?.Invoke(profile.Features);
        return new FrontendAnalysisContext
        {
            ActiveProfile = profile, TargetUrl = Url, FeatureToggles = profile.Features,
            EngineRequirements = profile.EngineRequirements, ReviewEngineSelection = profile.ReviewEngineSelection,
        };
    }

    private static FrontendQualityEngineStatusDto Engine(FrontendQualityEngineIdDto id, bool available = true) => new()
    {
        EngineId = id, DisplayName = id.ToString(), Layer1Allowed = available, Layer2Enabled = true,
        AuthModeSupported = true, Available = available,
        Layer3Readiness = new FrontendQualityEngineReadinessDto { EngineId = id, IsAvailable = available },
    };

    private static IReadOnlyList<FrontendQualityDimensionCard> Cards(
        FrontendAnalysisContext context, WcagAssessmentProfile? profile = null, params FrontendQualityEngineStatusDto[] engines)
    {
        var rows = FrontendQualityLandingPresentation.Capabilities(
            context, FrontendQualityActiveEngines.Resolve(context),
            new FrontendQualityEngineStatusReportDto { Engines = engines.ToList() }, false, false,
            FrontendQualityTargetAccess.FromContext(context), BirkNext.BrowserCompanion.BrowserCompanionState.Connected);
        return FrontendQualityLandingPresentation.Dimensions(rows, profile);
    }

    private static FrontendQualityDimensionCard Card(IReadOnlyList<FrontendQualityDimensionCard> cards, FrontendQualityCategory category) =>
        cards.Single(c => c.Category == category);

    private IRenderedComponent<FrontendQualityDimensionCards> Render(IReadOnlyList<FrontendQualityDimensionCard> cards) =>
        Render<FrontendQualityDimensionCards>(p => p.Add(c => c.Cards, cards));

    // ── 1–2, 5. Six domains, scope vocabulary, concise copy ─────────────────

    [Fact]
    public void AllSixDomainsRenderWithScopeVocabularyRatherThanEngineVocabulary()
    {
        var cut = Render(Cards(Context()));

        cut.FindAll("[data-testid=fqr-dimension]").Should().HaveCount(6);
        cut.FindAll("[data-testid=fqr-dimension] h3").Select(h => h.TextContent.Trim())
            .Should().Equal("Performance", "Security", "Accessibility", "Standards Compliance", "Blazor / WASM", "QA Readiness");

        var states = cut.FindAll("[data-testid=fqr-dimension-state]").Select(s => s.TextContent.Trim()).ToList();
        states.Should().OnlyContain(s => s == "Included" || s == "Limited" || s == "Partial evidence" || s == "Not included");
        // Engine vocabulary never becomes a domain status.
        foreach (var engineWord in new[] { "Enabled", "Disabled", "Unavailable", "Ready", "Needs pairing", "Needs evidence", "Not selected" })
            states.Should().NotContain(engineWord);

        // Copy stays short: one purpose line and at most one limitation line.
        cut.FindAll("[data-testid=fqr-dimension-limitation]")
            .Should().OnlyContain(l => l.TextContent.Trim().Length <= 120);
    }

    [Fact]
    public void DomainStatusVocabularyIsExactlyTheScopeLayer()
    {
        Enum.GetValues<FrontendQualityDimensionState>().Select(FrontendQualityDimensionStates.Label)
            .Should().BeEquivalentTo(new[] { "Included", "Limited", "Partial evidence", "Not included" });
    }

    // ── 6–9. Performance survives every optional engine being unavailable ───

    [Theory]
    [InlineData(FrontendQualityEngineIdDto.Lighthouse)]
    public void PerformanceStaysInTheReviewWhenAnOptionalEngineIsUnavailable(FrontendQualityEngineIdDto unavailable)
    {
        var cards = Cards(Context(), null, Engine(unavailable, available: false));
        var performance = Card(cards, FrontendQualityCategory.Performance);

        performance.State.Should().Be(FrontendQualityDimensionState.Limited);
        performance.State.Should().NotBe(FrontendQualityDimensionState.NotIncluded);
        performance.Limitation.Should().Be("Lighthouse evidence is unavailable.", "the limitation names the contributor that is missing");
    }

    [Fact]
    public void PerformanceStaysIncludedWhenBrowserQualityAndPerformanceQualityAreBothOff()
    {
        // Both are opt-in and off by default, so they are not even active — the baseline still carries the domain.
        var cards = Cards(Context(t => { t.EnableBrowserQualityEngine = false; t.EnablePerformanceQualityEngine = false; }));

        Card(cards, FrontendQualityCategory.Performance).State.Should().Be(FrontendQualityDimensionState.Included);
    }

    [Fact]
    public void PerformanceIsNeverUnavailableBecauseOptionalEvidenceIsMissing()
    {
        var cards = Cards(Context(), null,
            Engine(FrontendQualityEngineIdDto.Lighthouse, available: false),
            Engine(FrontendQualityEngineIdDto.BrowserRuntime, available: false));

        Card(cards, FrontendQualityCategory.Performance).State.Should().NotBe(FrontendQualityDimensionState.NotIncluded);
        // The limitation names the contributor that is missing; the technical reason stays in Review capabilities.
        Card(cards, FrontendQualityCategory.Performance).Limitation.Should().Be("Lighthouse evidence is unavailable.");
    }

    // ── 10–12. Security ────────────────────────────────────────────────────

    [Fact]
    public void SecurityStaysInTheReviewWhenPassiveSecurityIsUnavailable()
    {
        var cards = Cards(Context(), null, Engine(FrontendQualityEngineIdDto.PassiveSecurity, available: false));
        var security = Card(cards, FrontendQualityCategory.Security);

        security.State.Should().Be(FrontendQualityDimensionState.Limited);
        security.Limitation.Should().Be("Passive Security evidence is unavailable.");
        // The exact engine diagnostic is not duplicated onto the card.
        security.Limitation.Should().NotContain("Passive Security engine").And.NotContain("Blocked by deployment policy");
    }

    [Fact]
    public void TheExactEngineReasonStaysInCapabilitiesNotOnTheDomainCard()
    {
        var context = Context();
        var rows = FrontendQualityLandingPresentation.Capabilities(
            context, FrontendQualityActiveEngines.Resolve(context),
            new FrontendQualityEngineStatusReportDto { Engines = [Engine(FrontendQualityEngineIdDto.PassiveSecurity, available: false)] },
            false, false, FrontendQualityTargetAccess.FromContext(context), null);

        // Capability row keeps the engine-level truth…
        var row = rows.Single(r => r.EngineId == FrontendQualityEngineId.PassiveSecurity);
        row.Summary.Should().Contain("not available in this environment");
        // …while the domain card says only what it means for the review.
        FrontendQualityLandingPresentation.Dimensions(rows).Single(c => c.Category == FrontendQualityCategory.Security)
            .Limitation.Should().Be("Passive Security evidence is unavailable.");
    }

    // ── 13–20. Accessibility ───────────────────────────────────────────────

    [Fact]
    public void AccessibilityStaysInTheReviewWhenItsEngineIsUnavailable()
    {
        var cards = Cards(Context(), null, Engine(FrontendQualityEngineIdDto.Accessibility, available: false));
        var accessibility = Card(cards, FrontendQualityCategory.Accessibility);

        accessibility.State.Should().NotBe(FrontendQualityDimensionState.NotIncluded);
        accessibility.ManualReviewRequired.Should().BeTrue();
    }

    [Fact]
    public void AccessibilityStaysInTheReviewWhenBrowserQualityIsUnavailable()
    {
        var cards = Cards(Context(t => t.EnableBrowserQualityEngine = false), null, Engine(FrontendQualityEngineIdDto.Accessibility));

        Card(cards, FrontendQualityCategory.Accessibility).State.Should().Be(FrontendQualityDimensionState.Included);
    }

    [Theory]
    [InlineData(WcagProfiles.NorwegianId, "Norwegian public-sector requirements — WCAG 2.1")]
    [InlineData(WcagProfiles.Wcag21AaId, "WCAG 2.1 AA — Full standard")]
    [InlineData(WcagProfiles.ExtendedId, "WCAG 2.2 AA — Extended review")]
    public void AccessibilityCardShowsTheSelectedProfileWhicheverItIs(string profileId, string expected)
    {
        var cards = Cards(Context(), WcagProfiles.Resolve(profileId), Engine(FrontendQualityEngineIdDto.Accessibility));

        Card(cards, FrontendQualityCategory.Accessibility).ScopeNote.Should().Be(expected);

        var cut = Render(cards);
        var card = cut.FindAll("[data-testid=fqr-dimension]").Single(c => c.GetAttribute("data-category") == "Accessibility");
        card.QuerySelector("[data-testid=fqr-dimension-scope]")!.TextContent.Trim().Should().Be(expected);
        // No other profile's label is hard-coded onto the card.
        foreach (var other in WcagProfiles.Available.Where(p => p.ProfileId != profileId))
            card.TextContent.Should().NotContain(other.Label);
    }

    [Fact]
    public void ManualReviewRequirementIsExplicitAndRenderedAsText()
    {
        var cut = Render(Cards(Context(), null, Engine(FrontendQualityEngineIdDto.Accessibility)));
        var card = cut.FindAll("[data-testid=fqr-dimension]").Single(c => c.GetAttribute("data-category") == "Accessibility");

        card.QuerySelector("[data-testid=fqr-dimension-manual]")!.TextContent.Trim().Should().Be("Manual review required");
        // Only Accessibility carries it.
        cut.FindAll("[data-testid=fqr-dimension-manual]").Should().ContainSingle();
    }

    [Fact]
    public void NoEngineStateBecomesAComplianceOrFailureClaim()
    {
        var cut = Render(Cards(Context(), null,
            Engine(FrontendQualityEngineIdDto.Accessibility, available: false),
            Engine(FrontendQualityEngineIdDto.PassiveSecurity, available: false)));

        foreach (var word in new[] { "WCAG compliant", "Compliant", "Passed", "Failed", "0 issues", "No accessibility problems" })
            cut.Markup.Should().NotContain(word);
    }

    // ── 21–23. Blazor / WASM ───────────────────────────────────────────────

    [Fact]
    public void BlazorWasmStaysInTheReviewWhenBrowserEvidenceIsUnavailable()
    {
        // Browser Runtime must be ENABLED for its absence to be a limitation; an opt-in engine left off is not missing.
        var cards = Cards(Context(t => t.EnableBrowserRuntimeEngine = true), null, Engine(FrontendQualityEngineIdDto.BrowserRuntime, available: false));
        var blazor = Card(cards, FrontendQualityCategory.BlazorWasm);

        blazor.State.Should().Be(FrontendQualityDimensionState.Limited);
        blazor.Limitation.Should().Be("Browser Runtime evidence is unavailable.");
        blazor.Purpose.Should().Contain("Static Blazor");
    }

    [Fact]
    public void BlazorWasmIsIncludedOnItsStaticBaselineAlone()
    {
        Card(Cards(Context()), FrontendQualityCategory.BlazorWasm).State.Should().Be(FrontendQualityDimensionState.Included);
    }

    // ── 24–26. QA Readiness ────────────────────────────────────────────────

    [Fact]
    public void QaReadinessUsesDomainScopeLanguageNotEngineState()
    {
        var readiness = Card(Cards(Context()), FrontendQualityCategory.Readiness);

        readiness.State.Should().Be(FrontendQualityDimensionState.Included);
        readiness.Purpose.Should().Contain("Derived");
        readiness.Limitation.Should().BeNull("nothing is missing, so there is nothing to say");
    }

    [Fact]
    public void QaReadinessReportsPartialEvidenceWhenItsBasisIsUnavailable()
    {
        var cards = Cards(Context(t => t.EnablePerformanceEngine = false));
        var readiness = Card(cards, FrontendQualityCategory.Readiness);

        readiness.State.Should().BeOneOf(FrontendQualityDimensionState.PartialEvidence, FrontendQualityDimensionState.NotIncluded);
        FrontendQualityDimensionStates.Label(readiness.State).Should().NotBe("Unavailable");
    }

    // ── 27–28. Standards ───────────────────────────────────────────────────

    [Fact]
    public void StandardsUsesScopeWordingAndSurvivesOptionalEngineAbsence()
    {
        var cards = Cards(Context(), null,
            Engine(FrontendQualityEngineIdDto.PassiveSecurity, available: false),
            Engine(FrontendQualityEngineIdDto.Lighthouse, available: false));
        var standards = Card(cards, FrontendQualityCategory.Standards);

        standards.State.Should().Be(FrontendQualityDimensionState.Included,
            "standards is derived from the security-header checks, which still run");
        FrontendQualityDimensionStates.Label(standards.State).Should().NotBe("Enabled").And.NotBe("Unavailable");
    }

    // ── 29–32. Copy deduplication ──────────────────────────────────────────

    [Fact]
    public void DomainCardsCarryImpactWordingAndNeverTheEngineInventory()
    {
        var cut = Render(Cards(Context(), null,
            Engine(FrontendQualityEngineIdDto.PassiveSecurity, available: false),
            Engine(FrontendQualityEngineIdDto.Lighthouse, available: false)));

        var section = cut.Find("[data-testid=fqr-dimensions]").TextContent;

        // A limitation names the contributor that is missing. The previous rule kept engine names off these cards
        // entirely, which forced one fixed sentence per category — and a fixed sentence goes stale: Performance said
        // "Optional browser evidence is unavailable" while four pages of browser evidence existed and Lighthouse was
        // the engine that could not start. The engine is named once, in the limitation; the technical reason and the
        // engine inventory still belong to Review capabilities.
        foreach (var available in new[] { "Browser Quality", "BirkNext Performance Quality", "Static Security" })
            section.Should().NotContain(available, "only a contributor that is actually missing is named");

        // Still one short sentence per card, and no card lists more than its own missing contributors.
        var limitations = cut.FindAll("[data-testid=fqr-dimension-limitation]").Select(l => l.TextContent.Trim()).ToList();
        limitations.Should().OnlyContain(l => l.Count(c => c == '.') <= 1, "one sentence, not an engine report");
        limitations.Should().OnlyContain(l => !l.Contains("Optional browser evidence is unavailable"),
            "the generic sentence that could not stay true is gone");
    }

    // ── 29 (accessibility of the UI). ──────────────────────────────────────

    [Fact]
    public void EachCardHasAHeadingAndATextualStatus()
    {
        var cut = Render(Cards(Context(), null, Engine(FrontendQualityEngineIdDto.PassiveSecurity, available: false)));

        foreach (var card in cut.FindAll("[data-testid=fqr-dimension]"))
        {
            card.QuerySelector("h3").Should().NotBeNull();
            card.QuerySelector("[data-testid=fqr-dimension-state]")!.TextContent.Trim().Should().NotBeEmpty();
        }
        cut.Find("#fqr-dimensions-heading").TagName.Should().Be("H2");
    }
}
