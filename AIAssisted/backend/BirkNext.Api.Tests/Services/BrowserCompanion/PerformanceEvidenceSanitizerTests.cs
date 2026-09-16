using System.Text.Json;
using BirkNext.Api.Services.BrowserCompanion;
using BirkNext.Api.Services.FrontendBrowserRuntime;
using BirkNext.BrowserCompanion;

namespace BirkNext.Api.Tests.Services.BrowserCompanion;

/// <summary>
/// The BirkNext Performance Quality evidence added to the companion contract (phase, stabilization, INP, long-task blocking, resource
/// categories, timeline, Blazor breakdown, collector overhead) passes the authoritative backend sanitizer: bounded, clamped, URL-stripped,
/// and INP is never accepted unless the extension itself reported it as measured.
/// </summary>
public sealed class PerformanceEvidenceSanitizerTests
{
    private static readonly BrowserCompanionEvidenceSanitizer Sanitizer = new(new BrowserEvidenceSanitizer());

    private static BrowserPageEvidence Evidence(BrowserPerformanceSummary performance, BrowserBlazorSummary? blazor = null) => new()
    {
        ProfileId = "dev", PageOrigin = "https://m2lbdev.bufetat.no", PagePath = "/children/search", VisitStartedAt = DateTimeOffset.UtcNow, CapturedAt = DateTimeOffset.UtcNow,
        Performance = performance, Blazor = blazor, Runtime = new BrowserRuntimeSummary { ErrorsBeforeStabilization = 3 },
    };

    [Fact]
    public void PhaseStabilizationAndInteractionSurviveSanitization_UnmeasuredInpIsNeverAccepted()
    {
        var page = Sanitizer.Sanitize(Evidence(new BrowserPerformanceSummary
        {
            ObservationType = "spa-navigation", StabilizationMs = 1834.56, StabilizedBy = "quiet",
            Interaction = new BrowserInteractionSummary { Status = "insufficient-samples", InteractionCount = 2, MinimumInteractions = 3, InpMs = 999, LongestInteractionMs = 310 },
            LongTaskCount = 4, MainThreadBlockingMs = 260, LongTasksAfterStabilization = 1,
            Mutations = new BrowserDomMutationSummary { BatchCount = 12, MutationCount = 800, LargestBatch = 300, LastMutationMs = 1500, LargeBatchesAfterStabilization = 2 },
            JsHeapUsedBytes = 45_000_000,
        }));

        var perf = page.Performance!;
        Assert.Equal("spa-navigation", perf.ObservationType);
        Assert.Equal(1834.56, perf.StabilizationMs);
        Assert.Equal("quiet", perf.StabilizedBy);
        Assert.Equal("insufficient-samples", perf.Interaction!.Status);
        Assert.Null(perf.Interaction.InpMs);            // not measured → no INP value, whatever the extension sent
        Assert.Equal(310, perf.Interaction.LongestInteractionMs);
        Assert.Equal(260, perf.MainThreadBlockingMs);
        Assert.Equal(1, perf.LongTasksAfterStabilization);
        Assert.Equal(300, perf.Mutations!.LargestBatch);
        Assert.Equal(45_000_000, perf.JsHeapUsedBytes);
        Assert.Equal(3, page.Runtime!.ErrorsBeforeStabilization);
    }

    [Fact]
    public void MeasuredInpIsKept_InvalidStatusesFallBackToNotMeasured()
    {
        var measured = Sanitizer.Sanitize(Evidence(new BrowserPerformanceSummary { Interaction = new BrowserInteractionSummary { Status = "measured", InteractionCount = 5, InpMs = 180.4 } }));
        Assert.Equal(180.4, measured.Performance!.Interaction!.InpMs);
        var bogus = Sanitizer.Sanitize(Evidence(new BrowserPerformanceSummary { Interaction = new BrowserInteractionSummary { Status = "guessed", InpMs = 10 } }));
        Assert.Equal("not-measured", bogus.Performance!.Interaction!.Status);
        Assert.Null(bogus.Performance.Interaction.InpMs);
        var badPhase = Sanitizer.Sanitize(Evidence(new BrowserPerformanceSummary { ObservationType = "cold-start", StabilizedBy = "guess" }));
        Assert.Equal("initial-load", badPhase.Performance!.ObservationType);
        Assert.Null(badPhase.Performance.StabilizedBy);
    }

    [Fact]
    public void TimelineCategoriesAndBlazorBreakdownAreBoundedAndStripped()
    {
        var timeline = Enumerable.Range(0, 200).Select(i => new BrowserResourceEntry { Url = $"https://m2lbdev.bufetat.no/r{i}.js?access_token=SECRET{i}", Kind = "js", StartMs = i, DurationMs = 5, Delivery = i % 2 == 0 ? "cache" : "weird" }).ToList();
        var page = Sanitizer.Sanitize(Evidence(new BrowserPerformanceSummary
        {
            Timeline = timeline,
            Categories = [new BrowserResourceCategorySummary { Kind = "js", Count = 3, TransferBytes = 100, Largest = timeline[0] }, new BrowserResourceCategorySummary { Kind = "<script>", Count = 1 }],
            LargestResource = timeline[1], SlowestResource = timeline[2],
            Collector = new BrowserCollectorSummary { SnapshotBuildMs = 3.2, ObserverCallbacks = 14, SnapshotsSent = 2, PayloadBytes = 20_000, EntriesExamined = 120 },
        }, new BrowserBlazorSummary
        {
            Detected = true, LoadKind = "warm", AssemblyCount = 60, AssemblyBytes = 4_000_000, RuntimeResourceCount = 3, CultureResourceCount = 1, TimezoneDataObserved = true,
            FrameworkLoadStartMs = 100, FrameworkLoadEndMs = 2400, BootManifestMs = 40,
            SlowestFrameworkResource = new BrowserResourceEntry { Url = "https://m2lbdev.bufetat.no/_framework/dotnet.native.wasm?v=abc", Kind = "wasm", DurationMs = 900 },
        }));

        var perf = page.Performance!;
        Assert.Equal(BrowserCompanionLimits.MaxTimelineEntries, perf.Timeline.Count);
        Assert.All(perf.Timeline, t => Assert.DoesNotContain("SECRET", t.Url));
        Assert.Equal("cache", perf.Timeline[0].Delivery);
        Assert.Null(perf.Timeline[1].Delivery);
        Assert.Single(perf.Categories);                 // invalid kind dropped
        Assert.Equal("https://m2lbdev.bufetat.no/r0.js", perf.Categories[0].Largest!.Url);
        Assert.Equal(3.2, perf.Collector!.SnapshotBuildMs);
        Assert.Equal(120, perf.Collector.EntriesExamined);
        var blazor = page.Blazor!;
        Assert.Equal("warm", blazor.LoadKind);
        Assert.Equal(60, blazor.AssemblyCount);
        Assert.True(blazor.TimezoneDataObserved);
        Assert.Equal(2400, blazor.FrameworkLoadEndMs);
        Assert.Equal("https://m2lbdev.bufetat.no/_framework/dotnet.native.wasm", blazor.SlowestFrameworkResource!.Url);
        Assert.DoesNotContain("SECRET", JsonSerializer.Serialize(page));
        Assert.DoesNotContain("?v=", JsonSerializer.Serialize(page));
    }
}
