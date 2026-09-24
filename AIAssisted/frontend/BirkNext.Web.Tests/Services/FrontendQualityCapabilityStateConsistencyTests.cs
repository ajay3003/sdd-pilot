using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;
using Xunit;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// One truth for whether a Frontend Quality Review capability is off, broken, or working.
///
/// "Disabled" and "unavailable" had been merged: an engine switched off in System Settings counted as active, so it was
/// reported among the unavailable capabilities and it pushed its domains to Limited. And a Limited domain explained
/// itself with a sentence fixed per category, so Performance said "Optional browser evidence is unavailable" while four
/// pages of browser evidence existed and Lighthouse was the engine that could not start.
/// </summary>
public sealed class FrontendQualityCapabilityStateConsistencyTests
{
    private static FrontendQualityCapabilityRow Row(
        FrontendQualityEngineId id, FrontendQualityCapabilityState state,
        FrontendQualityEngineRequirement policy = FrontendQualityEngineRequirement.Optional, string? name = null) =>
        new(id, name ?? id.ToString(), policy, state, null, null, Enabled: state != FrontendQualityCapabilityState.Disabled);

    /// <summary>The engine set from the live M2LB DEV state: two required available, and the optional spread.</summary>
    private static List<FrontendQualityCapabilityRow> LiveEngines() =>
    [
        Row(FrontendQualityEngineId.StaticSecurity, FrontendQualityCapabilityState.Enabled, FrontendQualityEngineRequirement.Required, "Static Security"),
        Row(FrontendQualityEngineId.PassivePerformance, FrontendQualityCapabilityState.Enabled, FrontendQualityEngineRequirement.Required, "Passive Performance"),
        Row(FrontendQualityEngineId.BrowserRuntime, FrontendQualityCapabilityState.Disabled, name: "Browser Runtime"),
        Row(FrontendQualityEngineId.Accessibility, FrontendQualityCapabilityState.Ready, name: "Accessibility"),
        Row(FrontendQualityEngineId.Lighthouse, FrontendQualityCapabilityState.Unavailable, name: "Lighthouse"),
        Row(FrontendQualityEngineId.PassiveSecurity, FrontendQualityCapabilityState.Unavailable, name: "Passive Security"),
        Row(FrontendQualityEngineId.BrowserQuality, FrontendQualityCapabilityState.Ready, name: "Browser Quality"),
        Row(FrontendQualityEngineId.PerformanceQuality, FrontendQualityCapabilityState.Ready, name: "BirkNext Performance Quality"),
    ];

    // ── §48. Disabled is not unavailable ────────────────────────────────────

    // 40, 41.
    [Theory]
    [InlineData(FrontendQualityCapabilityState.Disabled)]
    [InlineData(FrontendQualityCapabilityState.NotSelected)]
    [InlineData(FrontendQualityCapabilityState.DisabledInSystemSettings)]
    public void AnEngineSomebodySwitchedOffIsNotActiveAndNotUnavailable(FrontendQualityCapabilityState state)
    {
        FrontendQualityCapabilityStates.IsDisabled(state).Should().BeTrue();
        FrontendQualityCapabilityStates.IsActive(state).Should().BeFalse("a capability that is off is not part of this review");
        FrontendQualityCapabilityStates.IsAvailable(state).Should().BeFalse();
    }

    [Theory]
    [InlineData(FrontendQualityCapabilityState.Unavailable)]
    [InlineData(FrontendQualityCapabilityState.NotConfigured)]
    [InlineData(FrontendQualityCapabilityState.RequiresBrowserSession)]
    public void AnEnabledEngineThatCannotRunIsActiveAndUnavailable(FrontendQualityCapabilityState state)
    {
        FrontendQualityCapabilityStates.IsDisabled(state).Should().BeFalse();
        FrontendQualityCapabilityStates.IsActive(state).Should().BeTrue("it is part of the review and something is wrong");
        FrontendQualityCapabilityStates.IsAvailable(state).Should().BeFalse();
    }

    /// <summary>The state model keeps every dimension apart: off, active-but-broken, and working are three answers.</summary>
    [Fact]
    public void NoStateIsBothDisabledAndActive()
    {
        foreach (var state in Enum.GetValues<FrontendQualityCapabilityState>())
        {
            (FrontendQualityCapabilityStates.IsDisabled(state) && FrontendQualityCapabilityStates.IsActive(state))
                .Should().BeFalse($"{state} cannot be both off and part of the review");
            if (FrontendQualityCapabilityStates.IsAvailable(state))
                FrontendQualityCapabilityStates.IsActive(state).Should().BeTrue($"{state} is available, so it must be active");
        }
    }

    // ── §42. One canonical count across every surface ───────────────────────

    // 1, 2, 3, 4, 5, 8, 9, 43, 44, 45, 46.
    [Fact]
    public void EveryCountComesFromTheSameRowsAndAgrees()
    {
        var rows = LiveEngines();
        var summary = FrontendQualityLandingPresentation.CapabilitySummary(rows);

        rows.Should().HaveCount(8);
        rows.Count(r => r.Enabled).Should().Be(7);
        rows.Count(r => r.IsAvailable).Should().Be(5, "two required plus Accessibility, Browser Quality and Performance Quality");
        rows.Count(r => r.IsActive && !r.IsAvailable).Should().Be(2, "Lighthouse and Passive Security");
        rows.Count(r => FrontendQualityCapabilityStates.IsDisabled(r.State)).Should().Be(1, "Browser Runtime");

        // The collapsed row, the expanded headline and the active-engine summary all read from this one model.
        summary.AvailableNowCount.Should().Be(5);
        summary.DisabledCount.Should().Be(1, "Browser Runtime is switched off, which is not the same as unavailable");
        summary.Headline.Should().Contain("2 required available").And.Contain("3 optional ready");

        // 27, 40. Three axes, three counts, none of them merged: configuration, capability and the switched-off engine.
        summary.Collapsed.Should().Be("2 required available · 3 optional ready · 2 optional unavailable · 1 optional disabled");
    }

    // 3, 40. The hard invariant: a disabled engine never reaches the unavailable count, on any surface.
    [Fact]
    public void ADisabledEngineIsNeverCountedOrDescribedAsUnavailable()
    {
        var rows = LiveEngines();
        var summary = FrontendQualityLandingPresentation.CapabilitySummary(rows);
        var disabled = rows.Where(r => FrontendQualityCapabilityStates.IsDisabled(r.State)).ToList();

        disabled.Should().NotBeEmpty();
        disabled.Should().OnlyContain(r => !r.IsAvailable, "it is off, so it cannot run");
        disabled.Should().OnlyContain(r => !r.IsActive, "and it is not part of this review");

        // Enabled + unavailable is the only thing that counts as unavailable.
        var unavailable = rows.Count(r => r.IsActive && !r.IsAvailable);
        unavailable.Should().Be(2, "Lighthouse and Passive Security — not Browser Runtime");
        (summary.EnabledCount - summary.AvailableNowCount).Should().Be(unavailable);

        summary.Collapsed.Should().NotContain("2 unavailable");
    }

    // 6, 7.
    [Fact]
    public void TheReadinessCardCountsOnlyEnabledCapabilitiesThatCannotRun()
    {
        var rows = LiveEngines();

        var unavailable = rows.Where(r => r.IsActive && !r.IsAvailable).ToList();

        unavailable.Should().HaveCount(2);
        unavailable.Select(r => r.DisplayName).Should().BeEquivalentTo(["Lighthouse", "Passive Security"]);
        unavailable.Should().NotContain(r => r.DisplayName == "Browser Runtime", "it is disabled, not unavailable");
    }

    // ── §43/§44/§45. Domain state and the reason it gives ───────────────────

    // 13, 14, 15, 16.
    [Fact]
    public void ADomainNamesTheContributorThatIsActuallyMissing()
    {
        var dimensions = FrontendQualityLandingPresentation.Dimensions(LiveEngines());
        var performance = dimensions.Single(d => d.Category == FrontendQualityCategory.Performance);

        // Whatever the domain rule concludes, it must not blame evidence that exists.
        performance.Limitation?.Should().NotContain("browser evidence");
        if (performance.State == FrontendQualityDimensionState.Limited)
            performance.Limitation.Should().Contain("Lighthouse", "Lighthouse is the enabled contributor that cannot run");
    }

    // 42.
    [Fact]
    public void ADisabledOptionalEngineDoesNotLimitItsDomains()
    {
        var withDisabled = LiveEngines();
        var withoutIt = withDisabled.Where(r => r.EngineId != FrontendQualityEngineId.BrowserRuntime).ToList();

        var a = FrontendQualityLandingPresentation.Dimensions(withDisabled).Select(d => (d.Category, d.State)).ToList();
        var b = FrontendQualityLandingPresentation.Dimensions(withoutIt).Select(d => (d.Category, d.State)).ToList();

        a.Should().BeEquivalentTo(b, "an engine switched off by configuration changes no domain's state");
    }

    // 24, 25, 26, 27.
    [Fact]
    public void SecurityIsLimitedAndSaysWhichCapabilityIsMissing()
    {
        var security = FrontendQualityLandingPresentation.Dimensions(LiveEngines())
            .Single(d => d.Category == FrontendQualityCategory.Security);

        security.State.Should().Be(FrontendQualityDimensionState.Limited);
        security.Limitation.Should().Contain("Passive Security");
    }

    // 20, 21, 22.
    [Fact]
    public void ManualAssessmentRequirementIsIndependentOfCapabilityAvailability()
    {
        var accessibility = FrontendQualityLandingPresentation.Dimensions(LiveEngines())
            .Single(d => d.Category == FrontendQualityCategory.Accessibility);

        accessibility.ManualAssessmentRequired.Should().BeTrue("automated checks cannot establish WCAG conformance");

        // With every accessibility contributor available, the manual obligation alone must not make the domain Limited.
        var allAvailable = LiveEngines()
            .Select(r => r.State == FrontendQualityCapabilityState.Unavailable ? r with { State = FrontendQualityCapabilityState.Ready } : r)
            .ToList();
        var withEverything = FrontendQualityLandingPresentation.Dimensions(allAvailable)
            .Single(d => d.Category == FrontendQualityCategory.Accessibility);

        withEverything.State.Should().Be(FrontendQualityDimensionState.Included);
        withEverything.ManualAssessmentRequired.Should().BeTrue("it is a separate dimension and does not depend on the engines");
    }

    // ── §46. Coverage: not required is not missing ──────────────────────────

    // 33, 34.
    [Fact]
    public void CoverageDistinguishesNotRequiredFromNotAvailable()
    {
        var rows = new List<FrontendQualityCoverageRow>
        {
            new("Public frontend", FrontendQualityCoverageState.Available, null),
            new("Authenticated application", FrontendQualityCoverageState.NotRequired, null),
            new("Browser-rendered DOM", FrontendQualityCoverageState.Available, null),
            new("Authenticated API traffic", FrontendQualityCoverageState.NotRequired, null),
            new("Automatic engines", FrontendQualityCoverageState.Available, null),
        };

        var summary = FrontendQualityLandingPresentation.CoverageSummary(rows);

        summary.AvailableCount.Should().Be(3);
        summary.NotRequiredCount.Should().Be(2);
        summary.NotAvailableCount.Should().Be(0);
        // 18, 39. The exact case from the screenshot: 3 available + 2 not required. Nothing is missing, so the line
        // says so in words. "3 available · 2 not required" was arithmetic the reader had to finish themselves.
        summary.Headline.Should().Be("All required access paths available");
        summary.Headline.Should().NotContainAny("3 available", "2 not required", "of 5");
    }

    [Fact]
    public void CoverageStillReportsSomethingGenuinelyMissing()
    {
        var rows = new List<FrontendQualityCoverageRow>
        {
            new("Public frontend", FrontendQualityCoverageState.Available, null),
            new("Browser-rendered DOM", FrontendQualityCoverageState.NotAvailable, null),
            new("Authenticated API traffic", FrontendQualityCoverageState.NotRequired, null),
        };

        var summary = FrontendQualityLandingPresentation.CoverageSummary(rows);

        // 20. Something genuinely unavailable is still reported as unavailable, and is what the line leads with.
        summary.Headline.Should().Be("1 access path not available");
        summary.NotAvailableCount.Should().Be(1);
        summary.NotRequiredCount.Should().Be(1);
    }

    // An access path that reaches only the public frontend is neither available nor unavailable, so it may not be
    // folded into either bucket — and it must stop the summary claiming that everything required is available.
    [Fact]
    public void CoverageDoesNotClaimFullAccessWhenEnginesSeeOnlyThePublicFrontend()
    {
        var rows = new List<FrontendQualityCoverageRow>
        {
            new("Public frontend", FrontendQualityCoverageState.Available, null),
            new("Authenticated application", FrontendQualityCoverageState.NotRequired, null),
            new("Automatic engines", FrontendQualityCoverageState.PublicOnly, null),
        };

        var summary = FrontendQualityLandingPresentation.CoverageSummary(rows);

        summary.PublicOnlyCount.Should().Be(1);
        summary.AvailableCount.Should().Be(1);
        summary.NotAvailableCount.Should().Be(0);
        summary.Headline.Should().Be("Automated review limited to the public frontend");
    }

    [Fact]
    public void CoverageWithNothingOptionalSaysSoPlainly()
    {
        var rows = new List<FrontendQualityCoverageRow>
        {
            new("Public frontend", FrontendQualityCoverageState.Available, null),
            new("Automatic engines", FrontendQualityCoverageState.Available, null),
        };

        FrontendQualityLandingPresentation.CoverageSummary(rows).Headline.Should().Be("All required access paths available");
    }
}
