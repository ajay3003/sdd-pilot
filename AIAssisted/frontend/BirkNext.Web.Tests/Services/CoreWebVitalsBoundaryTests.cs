using BirkNext.BrowserCompanion;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// Exact Core Web Vitals boundaries as the review code applies them, and the settings tab's band text derived from the same thresholds:
/// Good ≤ good; Needs improvement &gt; good and ≤ poor; Poor &gt; poor. A missing observation is Not measured — never zero.
/// </summary>
public sealed class CoreWebVitalsBoundaryTests
{
    private static readonly PerformanceThresholdSet Thresholds = PerformanceQualityRules.ResolveThresholds(new FrontendPerformanceThresholds(), new CoreWebVitalsThresholds());

    [Theory]
    [InlineData(2499, PerformanceMetricStatus.Good)]
    [InlineData(2500, PerformanceMetricStatus.Good)]
    [InlineData(2501, PerformanceMetricStatus.NeedsImprovement)]
    [InlineData(4000, PerformanceMetricStatus.NeedsImprovement)]
    [InlineData(4001, PerformanceMetricStatus.Poor)]
    public void Lcp(double ms, PerformanceMetricStatus expected) => PerformanceQualityRules.Classify(ms, Thresholds.Lcp).Should().Be(expected);

    [Theory]
    [InlineData(199, PerformanceMetricStatus.Good)]
    [InlineData(200, PerformanceMetricStatus.Good)]
    [InlineData(201, PerformanceMetricStatus.NeedsImprovement)]
    [InlineData(500, PerformanceMetricStatus.NeedsImprovement)]
    [InlineData(501, PerformanceMetricStatus.Poor)]
    public void Inp(double ms, PerformanceMetricStatus expected) => PerformanceQualityRules.Classify(ms, Thresholds.Inp).Should().Be(expected);

    [Theory]
    [InlineData(0.09, PerformanceMetricStatus.Good)]
    [InlineData(0.1, PerformanceMetricStatus.Good)]
    [InlineData(0.11, PerformanceMetricStatus.NeedsImprovement)]
    [InlineData(0.25, PerformanceMetricStatus.NeedsImprovement)]
    [InlineData(0.26, PerformanceMetricStatus.Poor)]
    public void Cls(double score, PerformanceMetricStatus expected) => PerformanceQualityRules.Classify(score, Thresholds.Cls).Should().Be(expected);

    [Fact]
    public void AMissingObservationIsNotMeasured_NeverZero()
    {
        foreach (var t in new[] { Thresholds.Lcp, Thresholds.Inp, Thresholds.Cls })
            PerformanceQualityRules.Classify(null, t).Should().Be(PerformanceMetricStatus.NotMeasured);
        PerformanceFormat.Value(null, "ms").Should().Be("—").And.NotBe("0 ms");
    }

    [Fact]
    public void TheTabsBandTextMatchesTheComparator()
    {
        var rows = CoreWebVitalsPresentation.Rows(new CoreWebVitalsThresholds()).ToDictionary(r => r.Abbreviation);
        (rows["LCP"].Good, rows["LCP"].NeedsImprovement, rows["LCP"].Poor).Should().Be(("≤ 2500 ms", "> 2500 ms and ≤ 4000 ms", "> 4000 ms"));
        (rows["INP"].Good, rows["INP"].NeedsImprovement, rows["INP"].Poor).Should().Be(("≤ 200 ms", "> 200 ms and ≤ 500 ms", "> 500 ms"));
        (rows["CLS"].Good, rows["CLS"].NeedsImprovement, rows["CLS"].Poor).Should().Be(("≤ 0.1", "> 0.1 and ≤ 0.25", "> 0.25"));
        rows["CLS"].Unit.Should().Be("unitless");
        rows.Values.Select(r => r.Good + r.NeedsImprovement + r.Poor).Where(t => t.Contains("CLS")).Should().BeEmpty();
        rows["CLS"].Good.Should().NotContain("ms");
    }

    [Fact]
    public void BandsFollowCustomThresholds_AndTheProfileIsDerived()
    {
        var custom = new CoreWebVitalsThresholds { LcpGoodMs = 1800 };
        CoreWebVitalsPresentation.Rows(custom)[0].Good.Should().Be("≤ 1800 ms");
        CoreWebVitalsPresentation.Profile(new CoreWebVitalsThresholds()).Should().Be(CoreWebVitalsProfile.Default);
        CoreWebVitalsPresentation.Profile(custom).Should().Be(CoreWebVitalsProfile.Custom);
        CoreWebVitalsPresentation.Profile(new CoreWebVitalsThresholds { ClsPoor = 0.3 }).Should().Be(CoreWebVitalsProfile.Custom);
        new FrontendAnalysisSettingsService().GetDefaultCoreWebVitals().Should().BeEquivalentTo(new CoreWebVitalsThresholds(), "the restore defaults and the model defaults agree");
    }

    [Fact]
    public void BandFormattingIsInvariant_LikeTheReviewsMetricStrings()
    {
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("nb-NO");
            CoreWebVitalsPresentation.Rows(new CoreWebVitalsThresholds())[2].Poor.Should().Be("> 0.25", "FQR renders metric numbers with the invariant culture");
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = culture; }
    }

    // Browser Quality applies the same strict-greater boundaries: LCP above good → needs improvement, above poor → poor; CLS only above poor.
    private static IReadOnlyList<string> BrowserQuality(double? lcp, double? cls) =>
        BrowserQualityRules.Evaluate(new PageAnalysis
        {
            PageOrigin = "https://app.example.test", PagePath = "/",
            BrowserEvidence = new BrowserPageEvidence { ProfileId = "dev", PageOrigin = "https://app.example.test", PagePath = "/", Performance = new BrowserPerformanceSummary { LcpMs = lcp, Cls = cls } },
        }, new FrontendPerformanceThresholds(), new CoreWebVitalsThresholds()).Select(f => f.RuleId).Where(id => id.StartsWith("perf-lcp") || id.StartsWith("perf-cls")).ToList();

    [Fact]
    public void BrowserQualityBoundariesMatch()
    {
        BrowserQuality(2500, 0.1).Should().BeEmpty();
        BrowserQuality(2501, 0.25).Should().Equal("perf-lcp-needs-improvement");
        BrowserQuality(4000, null).Should().Equal("perf-lcp-needs-improvement");
        BrowserQuality(4001, 0.26).Should().Equal("perf-lcp-poor", "perf-cls-poor");
        BrowserQuality(null, null).Should().BeEmpty("no observation is not a finding and never zero");
    }
}
