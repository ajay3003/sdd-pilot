using BirkNext.ApiReview;
using BirkNext.LocalHttpsProxy;
using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// The performance policy a review ran with: profile identity AND effective values are captured at Run, shown from the result's own
/// snapshot (never today's settings), and an older result without a recorded profile says so instead of guessing.
/// </summary>
public sealed partial class ApiQualityReviewLandingUITests
{
    private static FrontendAnalysisContext WithThresholds(FrontendAnalysisContext context, FrontendPerformanceThresholds thresholds)
    {
        context.PerformanceThresholds = thresholds;
        return context;
    }

    [Fact]
    public async Task Run_CapturesProfileIdentityAndEffectiveValues()
    {
        ApiReviewRunRequest? sent = null;
        var strict = new FrontendAnalysisSettingsService().GetStrictThresholds();
        Register(WithThresholds(Context(), strict), true, AutorisasjonEndpoints(), r => { sent = r; return LimitedEvidence(r); });
        await RenderAndRun();

        var policy = sent!.Policy;
        policy.PerformanceProfile.Should().Be(ApiReviewPerformanceProfile.Strict);
        (policy.SlowWarningMs, policy.SlowPoorMs, policy.LatencySource).Should().Be((1000, (int?)null, "Single Request Latency"));
        policy.AverageLatencyWarningMs.Should().Be(300);
        policy.RestPayloadWarningBytes.Should().Be(250L * 1024);
        policy.GraphQlPayloadWarningBytes.Should().Be(500L * 1024);
        policy.CompressionMinimumBytes.Should().Be(1024);
    }

    [Fact]
    public async Task AResultShowsTheProfileItRanWith_NotTheCurrentSettings()
    {
        // Settings are Custom now; the result was produced under Strict and must still say Strict with Strict's values.
        var custom = new FrontendAnalysisSettingsService().GetDefaultThresholds();
        custom.Mode = FrontendThresholdMode.Custom;
        custom.MaxSingleRequestLatencyMs = 4000;
        Register(WithThresholds(Context(), custom), true, AutorisasjonEndpoints(), r => LimitedEvidence(r) with
        {
            Policy = new ApiReviewPolicy { PerformanceProfile = ApiReviewPerformanceProfile.Strict, SlowWarningMs = 1000, LatencySource = "Single Request Latency", AverageLatencyWarningMs = 300,
                RestPayloadWarningBytes = 250L * 1024, GraphQlPayloadWarningBytes = 500L * 1024, CompressionMinimumBytes = 1024 },
        });
        var page = await RenderAndRun();
        page.Find("[data-testid=aqr-tab-performance]").Click();

        page.Find("[data-testid=aqr-performance-policy] .disclosure-hint").TextContent.Should().Be("Strict");
        var policy = page.Find("[data-testid=aqr-performance-policy-list]").TextContent;
        policy.Should().Contain("warning > 1000 ms (Single Request Latency)").And.Contain("warning > 300 ms").And.Contain("256,000 bytes (250 KB)")
            .And.Contain("512,000 bytes (500 KB)").And.Contain("1,024 bytes (1 KB)").And.NotContain("4000");
    }

    [Fact]
    public async Task AnOlderResult_SaysProfileNotRecorded_AndNeverGuessesFromValues()
    {
        // Values identical to today's Default preset — still not "Default", because the profile was never recorded.
        Register(Context(), true, AutorisasjonEndpoints(), r => LimitedEvidence(r) with
        {
            Policy = new ApiReviewPolicy { SlowWarningMs = 500, SlowPoorMs = 1000, LargePayloadBytes = 1024 * 1024 },
        });
        var page = await RenderAndRun();
        page.Find("[data-testid=aqr-tab-performance]").Click();
        page.Find("[data-testid=aqr-performance-policy] .disclosure-hint").TextContent.Should().Be("Profile not recorded");
        var policy = page.Find("[data-testid=aqr-performance-policy-list]").TextContent;
        policy.Should().Contain("warning > 500 ms · poor > 1000 ms").And.Contain("Not recorded").And.NotContain("Default");
    }
}
