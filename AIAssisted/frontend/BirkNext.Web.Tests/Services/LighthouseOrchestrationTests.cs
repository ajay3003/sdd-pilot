using BirkNext.Web.Models;
using BirkNext.Web.Services;

namespace BirkNext.Web.Tests.Services;

public sealed class LighthouseOrchestrationTests
{
    [Fact]
    public async Task LighthouseDisabled_DoesNotInvokeEngine()
    {
        var spy = new LighthouseSpy();
        await Create(spy).RunAsync("https://example.com", Context(false));
        Assert.Equal(0, spy.CallCount);
    }

    [Fact]
    public async Task LighthouseEnabled_PreflightReady_InvokesOnce_AndPreservesLabSemantics()
    {
        var spy = new LighthouseSpy();
        var result = await Create(spy).RunAsync("https://example.com", Context(true));
        Assert.Equal(1, spy.CallCount);
        Assert.Equal("Lab", result.LighthouseReport?.MeasurementType);
        Assert.False(result.LighthouseReport!.FieldDataAvailable);
        Assert.Contains(result.LighthouseReport.Metrics!, m => m.Name == "INP" && m.Status == LighthouseMetricStatusDto.FieldDataRequired);
        Assert.Equal(result.LighthouseReport, result.QualityReport!.LighthouseReport);
    }

    [Fact]
    public async Task LighthouseEnabled_PreflightBlocked_DoesNotInvokeEngine()
    {
        var spy = new LighthouseSpy();
        await Create(spy, PreflightStatus.Unreachable).RunAsync("https://example.com", Context(true));
        Assert.Equal(0, spy.CallCount);
    }

    [Fact]
    public async Task LighthouseEngineError_PreservesSuccessfulEngines_AndProducesPartialAssessment()
    {
        var spy = new LighthouseSpy(LighthouseExecutionStatusDto.EngineError);
        var result = await Create(spy).RunAsync("https://example.com", Context(true));
        Assert.Equal(1, spy.CallCount);
        Assert.NotNull(result.SecurityReport);
        Assert.NotNull(result.PerformanceReport);
        var lighthouse = Assert.IsType<LighthouseResultDto>(result.LighthouseReport);
        Assert.Equal(LighthouseExecutionStatusDto.EngineError, lighthouse.ExecutionStatus);
        Assert.Contains("Lighthouse", result.QualityReport!.FailedEngines);
        Assert.Equal(AssessmentCompleteness.Partial, result.QualityReport.Completeness);
        Assert.Null(lighthouse.PerformanceScore);
        Assert.NotNull(result.QualityReport.SecurityScore);
        Assert.NotNull(result.QualityReport.PerformanceScore);
    }

    [Fact]
    public void Export_LabelsLighthouseAsSyntheticLab_AndExcludesFieldClaims()
    {
        var report = new FrontendQualityReviewReport { LighthouseReport = LighthouseSpy.Result(LighthouseExecutionStatusDto.Assessed) };
        var html = new ReportExportService().ExportFrontendQualityReview(report, null);
        Assert.Contains("Lighthouse Lab Performance", html);
        Assert.Contains("Synthetic / Lab", html);
        Assert.Contains("Field data is not included", html);
        Assert.DoesNotContain("Core Web Vitals passed", html, StringComparison.OrdinalIgnoreCase);
    }

    private static FrontendQualityReviewOrchestrator Create(LighthouseSpy spy, PreflightStatus status = PreflightStatus.Ready) =>
        new(new Security(), new Performance(), new Preflight(status), new FrontendQualityReviewService(), null, null, spy);
    private static FrontendAnalysisContext Context(bool enabled) => new()
    {
        TargetUrl = "https://example.com",
        FeatureToggles = new() { EnableSecurityEngine = true, EnablePerformanceEngine = true, EnableLighthouseEngine = enabled }
    };
    private sealed class LighthouseSpy(LighthouseExecutionStatusDto status = LighthouseExecutionStatusDto.Assessed) : IFrontendLighthouseReviewApiService
    {
        public int CallCount { get; private set; }
        public Task<LighthouseResultDto> ReviewAsync(string targetUrl, bool requiresAuthentication, CancellationToken cancellationToken = default)
        { CallCount++; return Task.FromResult(Result(status)); }
        public static LighthouseResultDto Result(LighthouseExecutionStatusDto status) => new(status,
            LighthouseVersion: status == LighthouseExecutionStatusDto.Assessed ? "12.2.1" : null,
            NodeVersion: "v24.16.0", BrowserName: "Chromium", BrowserVersion: "130.0.6723.31",
            PerformanceScore: status == LighthouseExecutionStatusDto.Assessed ? 84 : null,
            Metrics: [new("LCP", 2100, "ms", LighthouseMetricStatusDto.Good), new("INP", Status: LighthouseMetricStatusDto.FieldDataRequired)],
            Limitations: ["Lighthouse provides synthetic lab measurements. Field data and real-user Core Web Vitals are not included."],
            EngineError: status == LighthouseExecutionStatusDto.EngineError ? "deterministic Lighthouse failure" : null);
    }
    private sealed class Security : ISecurityScanner
    { public Task<(WasmSecurityReviewReport?, string?)> ScanAsync(WasmScanRequest request) => Task.FromResult<(WasmSecurityReviewReport?, string?)>((new() { Health = new() { Score = 90 } }, null)); }
    private sealed class Performance : IBlazorWasmPerformanceReviewService
    {
        public Task<WasmPerformanceReviewReport> RunReviewAsync(string targetUrl, FrontendPerformanceThresholds? thresholds = null, CancellationToken cancellationToken = default) => Task.FromResult(new WasmPerformanceReviewReport());
        public Task<WasmAssetDiscoveryResult> DiscoverAssetsAsync(string targetUrl, FrontendPerformanceThresholds? thresholds = null, CancellationToken cancellationToken = default) => Task.FromResult(new WasmAssetDiscoveryResult());
        public WasmPerformanceReviewReport? GetCached() => null; public void ClearCache() { }
    }
    private sealed class Preflight(PreflightStatus status) : ITargetPreflightService
    { public Task<TargetPreflightResult> CheckTargetAsync(string targetUrl) => Task.FromResult(new TargetPreflightResult { Status = status, Message = "test" }); }
}
