using BirkNext.Api.Services.WasmPerformance;
using FluentAssertions;

namespace BirkNext.Api.Tests.Unit.WasmPerformance;

public class WasmStartupAnalysisServiceTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static DiscoveredAsset Asset(
        AssetType type,
        long? contentLength = null,
        long downloadedBytes = 0,
        int statusCode = 200,
        string url = "https://app.example.com/_framework/test.wasm")
        => new()
        {
            Url             = url,
            Type            = type,
            ContentLength   = contentLength,
            DownloadedBytes = downloadedBytes,
            StatusCode      = statusCode
        };

    private static StartupAnalysisThresholds TightThresholds => new()
    {
        MaxStartupDownloadMB    = 1.0,
        MaxStartupRequests      = 10,
        MaxFrameworkMB          = 0.5,
        MaxApplicationMB        = 0.3,
        MaxIndividualAssetMB    = 0.2,
        MaxUserJavaScriptKB     = 50.0,
        MaxCssKB                = 50.0,
        MaxSatelliteResourcesMB = 0.1
    };

    // ── CalculateMetrics ──────────────────────────────────────────────────────

    [Fact]
    public void CalculateMetrics_EmptyAssets_ReturnsZeroMetrics()
    {
        var result = WasmStartupAnalysisService.CalculateMetrics([]);

        result.StartupDownloadBytes.Should().Be(0);
        result.StartupRequestCount.Should().Be(0);
        result.FrameworkAssemblyCount.Should().Be(0);
        result.ApplicationAssemblyCount.Should().Be(0);
    }

    [Fact]
    public void CalculateMetrics_OnlyFailedAssets_ReturnsZeroMetrics()
    {
        var assets = new[]
        {
            Asset(AssetType.FrameworkDll, contentLength: 500_000, statusCode: 404),
            Asset(AssetType.ApplicationDll, contentLength: 100_000, statusCode: 0)
        };

        var result = WasmStartupAnalysisService.CalculateMetrics(assets);

        result.StartupDownloadBytes.Should().Be(0);
        result.StartupRequestCount.Should().Be(0);
    }

    [Fact]
    public void CalculateMetrics_FrameworkAssets_ComputesFrameworkBytes()
    {
        var assets = new[]
        {
            Asset(AssetType.FrameworkDll,  contentLength: 1_000_000),
            Asset(AssetType.WasmRuntime,   contentLength: 500_000),
            Asset(AssetType.FrameworkJs,   contentLength: 200_000),
            Asset(AssetType.BootManifest,  contentLength: 10_000),
            Asset(AssetType.Other,         contentLength: 50_000)
        };

        var result = WasmStartupAnalysisService.CalculateMetrics(assets);

        result.FrameworkDownloadBytes.Should().Be(1_760_000);
        result.ApplicationDownloadBytes.Should().Be(0);
        result.StartupDownloadBytes.Should().Be(1_760_000);
    }

    [Fact]
    public void CalculateMetrics_ApplicationAssets_ComputesAppBytes()
    {
        var assets = new[]
        {
            Asset(AssetType.ApplicationDll,    contentLength: 400_000),
            Asset(AssetType.SatelliteAssembly, contentLength: 100_000)
        };

        var result = WasmStartupAnalysisService.CalculateMetrics(assets);

        result.ApplicationDownloadBytes.Should().Be(500_000);
        result.ApplicationAssemblyCount.Should().Be(1);
        result.SatelliteAssemblyCount.Should().Be(1);
    }

    [Fact]
    public void CalculateMetrics_MixedAssets_TotalsAllSuccessful()
    {
        var assets = new[]
        {
            Asset(AssetType.FrameworkDll,  contentLength: 2_000_000),
            Asset(AssetType.ApplicationDll, contentLength: 500_000),
            Asset(AssetType.Css,           contentLength: 100_000),
            Asset(AssetType.JavaScript,    contentLength: 80_000),
            Asset(AssetType.ApplicationDll, contentLength: 200_000, statusCode: 404)
        };

        var result = WasmStartupAnalysisService.CalculateMetrics(assets);

        result.StartupDownloadBytes.Should().Be(2_680_000);
        result.StartupRequestCount.Should().Be(4);
        result.FrameworkAssemblyCount.Should().Be(1);
        result.ApplicationAssemblyCount.Should().Be(1);
        result.CssCount.Should().Be(1);
    }

    [Fact]
    public void CalculateMetrics_UsesContentLength_WhenAvailable()
    {
        var assets = new[] { Asset(AssetType.ApplicationDll, contentLength: 999_000, downloadedBytes: 1) };

        var result = WasmStartupAnalysisService.CalculateMetrics(assets);

        result.StartupDownloadBytes.Should().Be(999_000);
    }

    [Fact]
    public void CalculateMetrics_FallsBackToDownloadedBytes_WhenContentLengthNull()
    {
        var assets = new[] { Asset(AssetType.ApplicationDll, contentLength: null, downloadedBytes: 123_456) };

        var result = WasmStartupAnalysisService.CalculateMetrics(assets);

        result.StartupDownloadBytes.Should().Be(123_456);
    }

    [Fact]
    public void CalculateMetrics_LargestAsset_IdentifiedCorrectly()
    {
        var assets = new[]
        {
            Asset(AssetType.FrameworkDll,   contentLength: 3_000_000, url: "https://example.com/_framework/dotnet.runtime.wasm"),
            Asset(AssetType.ApplicationDll, contentLength: 500_000,   url: "https://example.com/_framework/MyApp.wasm")
        };

        var result = WasmStartupAnalysisService.CalculateMetrics(assets);

        result.LargestAssetBytes.Should().Be(3_000_000);
        result.LargestAssetUrl.Should().Be("https://example.com/_framework/dotnet.runtime.wasm");
        result.LargestAssetType.Should().Be(AssetType.FrameworkDll);
    }

    [Fact]
    public void CalculateMetrics_CountsImagesFontsCss_Correctly()
    {
        var assets = new[]
        {
            Asset(AssetType.Image,      contentLength: 10_000),
            Asset(AssetType.Image,      contentLength: 20_000),
            Asset(AssetType.Font,       contentLength: 30_000),
            Asset(AssetType.Css,        contentLength: 40_000),
            Asset(AssetType.Css,        contentLength: 50_000),
            Asset(AssetType.JavaScript, contentLength: 60_000)
        };

        var result = WasmStartupAnalysisService.CalculateMetrics(assets);

        result.ImageCount.Should().Be(2);
        result.FontCount.Should().Be(1);
        result.CssCount.Should().Be(2);
    }

    // ── DetectFindings ────────────────────────────────────────────────────────

    [Fact]
    public void DetectFindings_SmallApp_NoFindings()
    {
        var metrics = new StartupMetrics
        {
            StartupDownloadBytes     = 500_000,
            FrameworkDownloadBytes   = 300_000,
            ApplicationDownloadBytes = 100_000,
            StartupRequestCount      = 5,
            LargestAssetBytes        = 150_000
        };

        var findings = WasmStartupAnalysisService.DetectFindings(metrics, [], new StartupAnalysisThresholds());

        findings.Should().BeEmpty();
    }

    [Fact]
    public void DetectFindings_LargeStartupPayload_GeneratesSTA001()
    {
        var t = TightThresholds;
        var metrics = new StartupMetrics
        {
            StartupDownloadBytes = MbToBytes(2.0),
            LargestAssetBytes    = 100_000
        };

        var findings = WasmStartupAnalysisService.DetectFindings(metrics, [], t);

        findings.Should().Contain(f => f.Id == "STA-001");
    }

    [Fact]
    public void DetectFindings_VeryLargeStartupPayload_IsHighSeverity()
    {
        var t = TightThresholds; // MaxStartupDownloadMB = 1.0
        var metrics = new StartupMetrics
        {
            StartupDownloadBytes = MbToBytes(2.0), // 2x threshold → High
            LargestAssetBytes    = 100_000
        };

        var findings = WasmStartupAnalysisService.DetectFindings(metrics, [], t);
        var sta001   = findings.Single(f => f.Id == "STA-001");

        sta001.Severity.Should().Be(PerformanceSeverity.High);
    }

    [Fact]
    public void DetectFindings_ModerateStartupPayload_IsMediumSeverity()
    {
        var t = TightThresholds; // MaxStartupDownloadMB = 1.0
        var metrics = new StartupMetrics
        {
            StartupDownloadBytes = MbToBytes(1.2), // just over threshold → Medium
            LargestAssetBytes    = 100_000
        };

        var findings = WasmStartupAnalysisService.DetectFindings(metrics, [], t);
        var sta001   = findings.Single(f => f.Id == "STA-001");

        sta001.Severity.Should().Be(PerformanceSeverity.Medium);
    }

    [Fact]
    public void DetectFindings_LargeFramework_GeneratesSTA002()
    {
        var t = TightThresholds; // MaxFrameworkMB = 0.5
        var metrics = new StartupMetrics
        {
            FrameworkDownloadBytes = MbToBytes(1.0),
            LargestAssetBytes      = 100_000
        };

        var findings = WasmStartupAnalysisService.DetectFindings(metrics, [], t);

        findings.Should().Contain(f => f.Id == "STA-002");
    }

    [Fact]
    public void DetectFindings_LargeAppAssemblies_GeneratesSTA003()
    {
        var t = TightThresholds; // MaxApplicationMB = 0.3
        var metrics = new StartupMetrics
        {
            ApplicationDownloadBytes = MbToBytes(0.5),
            ApplicationAssemblyCount = 3,
            LargestAssetBytes        = 100_000
        };

        var findings = WasmStartupAnalysisService.DetectFindings(metrics, [], t);

        findings.Should().Contain(f => f.Id == "STA-003");
    }

    [Fact]
    public void DetectFindings_LargeSatelliteResources_GeneratesSTA004()
    {
        var t = TightThresholds; // MaxSatelliteResourcesMB = 0.1
        var assets = new[]
        {
            Asset(AssetType.SatelliteAssembly, contentLength: 300_000, url: "https://app.example.com/_framework/en-US/MyApp.resources.wasm"),
        };
        var metrics = new StartupMetrics
        {
            SatelliteAssemblyCount = 1,
            LargestAssetBytes      = 100_000
        };

        var findings = WasmStartupAnalysisService.DetectFindings(metrics, assets, t);

        findings.Should().Contain(f => f.Id == "STA-004");
    }

    [Fact]
    public void DetectFindings_LargeUserJavaScript_GeneratesSTA005()
    {
        var t = TightThresholds; // MaxUserJavaScriptKB = 50
        var assets = new[]
        {
            Asset(AssetType.JavaScript, contentLength: 200_000, url: "https://app.example.com/js/app.js")
        };
        var metrics = new StartupMetrics { LargestAssetBytes = 100_000 };

        var findings = WasmStartupAnalysisService.DetectFindings(metrics, assets, t);

        findings.Should().Contain(f => f.Id == "STA-005");
    }

    [Fact]
    public void DetectFindings_LargeCSS_GeneratesSTA006()
    {
        var t = TightThresholds; // MaxCssKB = 50
        var assets = new[]
        {
            Asset(AssetType.Css, contentLength: 300_000, url: "https://app.example.com/css/app.css")
        };
        var metrics = new StartupMetrics { LargestAssetBytes = 100_000 };

        var findings = WasmStartupAnalysisService.DetectFindings(metrics, assets, t);

        findings.Should().Contain(f => f.Id == "STA-006");
    }

    [Fact]
    public void DetectFindings_TooManyRequests_GeneratesSTA007()
    {
        var t = TightThresholds; // MaxStartupRequests = 10
        var metrics = new StartupMetrics
        {
            StartupRequestCount = 25,
            LargestAssetBytes   = 100_000
        };

        var findings = WasmStartupAnalysisService.DetectFindings(metrics, [], t);

        findings.Should().Contain(f => f.Id == "STA-007");
    }

    [Fact]
    public void DetectFindings_LargeIndividualAsset_GeneratesSTA008()
    {
        var t = TightThresholds; // MaxIndividualAssetMB = 0.2
        var assets = new[]
        {
            Asset(AssetType.ApplicationDll, contentLength: MbToBytes(0.5),
                  url: "https://app.example.com/_framework/Big.wasm")
        };
        var metrics = WasmStartupAnalysisService.CalculateMetrics(assets);

        var findings = WasmStartupAnalysisService.DetectFindings(metrics, assets, t);

        findings.Should().Contain(f => f.Id == "STA-008");
    }

    [Fact]
    public void DetectFindings_LargeIndividualAsset_EvidenceListsTopFive()
    {
        var t      = TightThresholds;
        var assets = Enumerable.Range(1, 8)
            .Select(i => Asset(
                AssetType.ApplicationDll,
                contentLength: MbToBytes(0.3 + i * 0.05),
                url: $"https://app.example.com/_framework/Assembly{i}.wasm"))
            .ToArray();
        var metrics = WasmStartupAnalysisService.CalculateMetrics(assets);

        var findings = WasmStartupAnalysisService.DetectFindings(metrics, assets, t);
        var sta008   = findings.Single(f => f.Id == "STA-008");

        sta008.Evidence.Should().HaveCount(5); // capped at 5
    }

    [Fact]
    public void DetectFindings_VeryLargeIndividualAsset_IsHighSeverity()
    {
        var t = TightThresholds; // MaxIndividualAssetMB = 0.2
        var assets = new[]
        {
            Asset(AssetType.ApplicationDll, contentLength: MbToBytes(1.0), // 5x threshold
                  url: "https://app.example.com/_framework/Huge.wasm")
        };
        var metrics = WasmStartupAnalysisService.CalculateMetrics(assets);

        var findings = WasmStartupAnalysisService.DetectFindings(metrics, assets, t);
        var sta008   = findings.Single(f => f.Id == "STA-008");

        sta008.Severity.Should().Be(PerformanceSeverity.High);
    }

    [Fact]
    public void DetectFindings_ErroredAssets_NotCountedAsLargeIndividual()
    {
        var t = TightThresholds;
        var assets = new[]
        {
            Asset(AssetType.ApplicationDll, contentLength: MbToBytes(1.0), statusCode: 404,
                  url: "https://app.example.com/_framework/Missing.wasm")
        };
        var metrics = WasmStartupAnalysisService.CalculateMetrics(assets);

        var findings = WasmStartupAnalysisService.DetectFindings(metrics, assets, t);

        findings.Should().NotContain(f => f.Id == "STA-008");
    }

    // ── GenerateRecommendations ───────────────────────────────────────────────

    [Fact]
    public void GenerateRecommendations_NoFindings_ReturnsEmpty()
    {
        var recs = WasmStartupAnalysisService.GenerateRecommendations([]);
        recs.Should().BeEmpty();
    }

    [Fact]
    public void GenerateRecommendations_STA001Present_FirstRecIsCompression()
    {
        var findings = new[]
        {
            new PerformanceFinding { Id = "STA-001", Title = "Large startup", Severity = PerformanceSeverity.High,
                Category = PerformanceCategory.Startup, Description = "", Recommendation = "" }
        };

        var recs = WasmStartupAnalysisService.GenerateRecommendations(findings);

        recs.Should().NotBeEmpty();
        recs[0].Priority.Should().Be(1);
        recs[0].Title.Should().Contain("Brotli");
    }

    [Fact]
    public void GenerateRecommendations_STA003Present_LazyLoadingRecIncluded()
    {
        var findings = new[]
        {
            new PerformanceFinding { Id = "STA-003", Title = "Large app", Severity = PerformanceSeverity.Medium,
                Category = PerformanceCategory.Startup, Description = "", Recommendation = "" }
        };

        var recs = WasmStartupAnalysisService.GenerateRecommendations(findings);

        recs.Should().Contain(r => r.Title.Contains("lazy loading", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GenerateRecommendations_STA002WithoutSTA001_ILTrimmingRecIncluded()
    {
        var findings = new[]
        {
            new PerformanceFinding { Id = "STA-002", Title = "Large framework", Severity = PerformanceSeverity.Medium,
                Category = PerformanceCategory.Startup, Description = "", Recommendation = "" }
        };

        var recs = WasmStartupAnalysisService.GenerateRecommendations(findings);

        recs.Should().Contain(r => r.Title.Contains("IL trimming", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GenerateRecommendations_STA002WithSTA001_ILTrimmingRecNotDuplicated()
    {
        var findings = new[]
        {
            new PerformanceFinding { Id = "STA-001", Title = "Large startup", Severity = PerformanceSeverity.High,
                Category = PerformanceCategory.Startup, Description = "", Recommendation = "" },
            new PerformanceFinding { Id = "STA-002", Title = "Large framework", Severity = PerformanceSeverity.Medium,
                Category = PerformanceCategory.Startup, Description = "", Recommendation = "" }
        };

        var recs = WasmStartupAnalysisService.GenerateRecommendations(findings);

        // STA-001 already covers trimming in the Brotli rec; STA-002 standalone rec should be skipped
        recs.Count(r => r.Title.Contains("trimming", StringComparison.OrdinalIgnoreCase)).Should().Be(1);
    }

    [Fact]
    public void GenerateRecommendations_PrioritiesAreSequential()
    {
        var findings = new[]
        {
            new PerformanceFinding { Id = "STA-001", Title = "", Severity = PerformanceSeverity.High,
                Category = PerformanceCategory.Startup, Description = "", Recommendation = "" },
            new PerformanceFinding { Id = "STA-003", Title = "", Severity = PerformanceSeverity.Medium,
                Category = PerformanceCategory.Startup, Description = "", Recommendation = "" },
            new PerformanceFinding { Id = "STA-007", Title = "", Severity = PerformanceSeverity.Medium,
                Category = PerformanceCategory.Startup, Description = "", Recommendation = "" }
        };

        var recs = WasmStartupAnalysisService.GenerateRecommendations(findings);

        recs.Should().BeInAscendingOrder(r => r.Priority);
        recs.Select(r => r.Priority).Should().OnlyHaveUniqueItems();
    }

    // ── BuildDisplayMetrics ───────────────────────────────────────────────────

    [Fact]
    public void BuildDisplayMetrics_SmallApp_AllStatusGood()
    {
        var m = new StartupMetrics
        {
            StartupDownloadBytes     = 500_000,
            FrameworkDownloadBytes   = 300_000,
            ApplicationDownloadBytes = 100_000,
            StartupRequestCount      = 5,
            LargestAssetBytes        = 100_000
        };

        var display = WasmStartupAnalysisService.BuildDisplayMetrics(m, new StartupAnalysisThresholds());

        display.Should().AllSatisfy(metric => metric.Status.Should().Be("good"));
    }

    [Fact]
    public void BuildDisplayMetrics_ExceedsThreshold_StatusIsPoor()
    {
        var t = TightThresholds;
        var m = new StartupMetrics
        {
            StartupDownloadBytes = MbToBytes(2.0), // exceeds MaxStartupDownloadMB = 1.0
            LargestAssetBytes    = 100_000
        };

        var display = WasmStartupAnalysisService.BuildDisplayMetrics(m, t);
        var totalMetric = display.Single(d => d.Name == "Total startup download");

        totalMetric.Status.Should().Be("poor");
    }

    [Fact]
    public void BuildDisplayMetrics_ApproachingThreshold_StatusIsWarning()
    {
        var t = new StartupAnalysisThresholds { MaxStartupDownloadMB = 10.0 };
        var m = new StartupMetrics
        {
            StartupDownloadBytes = MbToBytes(8.0), // 80% of threshold → warning
            LargestAssetBytes    = 100_000
        };

        var display = WasmStartupAnalysisService.BuildDisplayMetrics(m, t);
        var totalMetric = display.Single(d => d.Name == "Total startup download");

        totalMetric.Status.Should().Be("warning");
    }

    [Fact]
    public void BuildDisplayMetrics_Returns7Metrics()
    {
        var display = WasmStartupAnalysisService.BuildDisplayMetrics(new StartupMetrics(), new StartupAnalysisThresholds());
        display.Should().HaveCount(7);
    }

    // ── Analyze integration ───────────────────────────────────────────────────

    [Fact]
    public void Analyze_EmptyAssets_ReturnsEmptyResult()
    {
        var svc    = new WasmStartupAnalysisService();
        var result = svc.Analyze([]);

        result.StartupMetrics.StartupDownloadBytes.Should().Be(0);
        result.Findings.Should().BeEmpty();
        result.Recommendations.Should().BeEmpty();
        result.DisplayMetrics.Should().HaveCount(7);
    }

    [Fact]
    public void Analyze_TypicalApp_ProducesCompleteResult()
    {
        var svc    = new WasmStartupAnalysisService();
        var t      = TightThresholds;
        var assets = new[]
        {
            Asset(AssetType.FrameworkDll,   contentLength: MbToBytes(1.5)),
            Asset(AssetType.ApplicationDll, contentLength: MbToBytes(0.5)),
            Asset(AssetType.WasmRuntime,    contentLength: MbToBytes(2.0)),
            Asset(AssetType.Css,            contentLength: 100_000),
            Asset(AssetType.JavaScript,     contentLength: 80_000)
        };

        var result = svc.Analyze(assets, t);

        result.StartupMetrics.StartupRequestCount.Should().Be(5);
        result.Findings.Should().NotBeEmpty();
        result.Recommendations.Should().NotBeEmpty();
        result.DisplayMetrics.Should().HaveCount(7);
    }

    [Fact]
    public void Analyze_UsesDefaultThresholds_WhenNullPassed()
    {
        var svc    = new WasmStartupAnalysisService();
        var assets = new[]
        {
            Asset(AssetType.FrameworkDll,   contentLength: 100_000),
            Asset(AssetType.ApplicationDll, contentLength: 50_000)
        };

        var act = () => svc.Analyze(assets, null);

        act.Should().NotThrow();
    }

    // ── Utility ───────────────────────────────────────────────────────────────

    private static long MbToBytes(double mb) => (long)(mb * 1024 * 1024);
}
