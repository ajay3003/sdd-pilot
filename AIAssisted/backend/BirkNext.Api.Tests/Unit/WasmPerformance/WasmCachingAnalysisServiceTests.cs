using BirkNext.Api.Services.WasmPerformance;
using FluentAssertions;

namespace BirkNext.Api.Tests.Unit.WasmPerformance;

public class WasmCachingAnalysisServiceTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static DiscoveredAsset Asset(
        AssetType type,
        string? contentEncoding  = null,
        string? cacheControl     = null,
        string? eTag             = null,
        string? lastModified     = null,
        long    contentLength    = 100_000,
        int     statusCode       = 200,
        string  url              = "https://app.example.com/_framework/test.wasm")
        => new()
        {
            Url             = url,
            Type            = type,
            StatusCode      = statusCode,
            ContentEncoding = contentEncoding,
            CacheControl    = cacheControl,
            ETag            = eTag,
            LastModified    = lastModified,
            ContentLength   = contentLength
        };

    private static readonly CachingAnalysisThresholds TightThresholds = new()
    {
        LargeUncompressedKB = 10.0,
        MinCacheDurationSec = 3_600
    };

    // ── ClassifyCompression ───────────────────────────────────────────────────

    [Fact]
    public void ClassifyCompression_BrotliEncoding_ReturnsBrotli()
    {
        WasmCachingAnalysisService.ClassifyCompression("br")
            .Should().Be(CompressionStatus.Brotli);
    }

    [Fact]
    public void ClassifyCompression_GzipEncoding_ReturnsGzip()
    {
        WasmCachingAnalysisService.ClassifyCompression("gzip")
            .Should().Be(CompressionStatus.Gzip);
    }

    [Fact]
    public void ClassifyCompression_DeflateEncoding_ReturnsGzip()
    {
        WasmCachingAnalysisService.ClassifyCompression("deflate")
            .Should().Be(CompressionStatus.Gzip);
    }

    [Fact]
    public void ClassifyCompression_NullEncoding_ReturnsNotCompressed()
    {
        WasmCachingAnalysisService.ClassifyCompression(null)
            .Should().Be(CompressionStatus.NotCompressed);
    }

    [Fact]
    public void ClassifyCompression_EmptyEncoding_ReturnsNotCompressed()
    {
        WasmCachingAnalysisService.ClassifyCompression("")
            .Should().Be(CompressionStatus.NotCompressed);
    }

    [Fact]
    public void ClassifyCompression_BrotliCaseInsensitive_ReturnsBrotli()
    {
        WasmCachingAnalysisService.ClassifyCompression("BR")
            .Should().Be(CompressionStatus.Brotli);
    }

    [Fact]
    public void ClassifyCompression_MultiValueWithBrotli_ReturnsBrotli()
    {
        WasmCachingAnalysisService.ClassifyCompression("gzip, br")
            .Should().Be(CompressionStatus.Brotli);
    }

    [Fact]
    public void ClassifyCompression_UnknownEncoding_ReturnsOther()
    {
        WasmCachingAnalysisService.ClassifyCompression("zstd")
            .Should().Be(CompressionStatus.Other);
    }

    // ── ClassifyCacheStatus ───────────────────────────────────────────────────

    [Theory]
    [InlineData("max-age=31536000, immutable", false, false, CacheStatus.ProperlyOptimized)]
    [InlineData("max-age=86400",               false, false, CacheStatus.ProperlyOptimized)]
    [InlineData("max-age=86400, public",       false, false, CacheStatus.ProperlyOptimized)]
    [InlineData("public, immutable",           false, false, CacheStatus.ProperlyOptimized)]
    public void ClassifyCacheStatus_LongCacheOrImmutable_ReturnsProperlyOptimized(
        string cc, bool hasETag, bool hasLm, CacheStatus expected)
    {
        WasmCachingAnalysisService.ClassifyCacheStatus(cc, hasETag, hasLm)
            .Should().Be(expected);
    }

    [Theory]
    [InlineData("max-age=3600",  false, false, CacheStatus.WeaklyCached)]
    [InlineData("max-age=1",     false, false, CacheStatus.WeaklyCached)]
    [InlineData("no-cache",      false, false, CacheStatus.WeaklyCached)]
    [InlineData("public",        true,  false, CacheStatus.WeaklyCached)]
    [InlineData(null,            true,  false, CacheStatus.WeaklyCached)]
    [InlineData(null,            false, true,  CacheStatus.WeaklyCached)]
    public void ClassifyCacheStatus_WeakOrShortCache_ReturnsWeaklyCached(
        string? cc, bool hasETag, bool hasLm, CacheStatus expected)
    {
        WasmCachingAnalysisService.ClassifyCacheStatus(cc, hasETag, hasLm)
            .Should().Be(expected);
    }

    [Theory]
    [InlineData("no-store",   false, false, CacheStatus.NotCached)]
    [InlineData("max-age=0",  false, false, CacheStatus.NotCached)]
    [InlineData(null,         false, false, CacheStatus.NotCached)]
    [InlineData("",           false, false, CacheStatus.NotCached)]
    [InlineData("public",     false, false, CacheStatus.NotCached)]
    public void ClassifyCacheStatus_NoStoreOrNoHeaders_ReturnsNotCached(
        string? cc, bool hasETag, bool hasLm, CacheStatus expected)
    {
        WasmCachingAnalysisService.ClassifyCacheStatus(cc, hasETag, hasLm)
            .Should().Be(expected);
    }

    [Fact]
    public void ClassifyCacheStatus_NoStoreTrumpsETag()
    {
        WasmCachingAnalysisService.ClassifyCacheStatus("no-store", hasETag: true, hasLastModified: false)
            .Should().Be(CacheStatus.NotCached);
    }

    [Fact]
    public void ClassifyCacheStatus_ImmutableTrumpsShortMaxAge()
    {
        // "immutable" present even with short max-age → ProperlyOptimized
        WasmCachingAnalysisService.ClassifyCacheStatus("max-age=60, immutable", false, false)
            .Should().Be(CacheStatus.ProperlyOptimized);
    }

    // ── IsFrameworkAsset ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(AssetType.FrameworkDll,    true)]
    [InlineData(AssetType.WasmRuntime,     true)]
    [InlineData(AssetType.FrameworkJs,     true)]
    [InlineData(AssetType.ApplicationDll,  true)]
    [InlineData(AssetType.SatelliteAssembly, true)]
    [InlineData(AssetType.Css,             false)]
    [InlineData(AssetType.JavaScript,      false)]
    [InlineData(AssetType.Font,            false)]
    [InlineData(AssetType.Image,           false)]
    [InlineData(AssetType.Index,           false)]
    [InlineData(AssetType.BootManifest,    false)]
    public void IsFrameworkAsset_ReturnsExpected(AssetType type, bool expected)
    {
        WasmCachingAnalysisService.IsFrameworkAsset(type).Should().Be(expected);
    }

    // ── AnalyzeAssets ─────────────────────────────────────────────────────────

    [Fact]
    public void AnalyzeAssets_ErroredAssets_Excluded()
    {
        var assets = new[]
        {
            Asset(AssetType.FrameworkDll, statusCode: 404),
            Asset(AssetType.FrameworkDll, statusCode: 0),
            Asset(AssetType.FrameworkDll, statusCode: 200, contentEncoding: "br")
        };

        var summaries = WasmCachingAnalysisService.AnalyzeAssets(assets, new CachingAnalysisThresholds());

        summaries.Should().HaveCount(1); // only the 200 asset
    }

    [Fact]
    public void AnalyzeAssets_BrotliCompressed_ClassifiedCorrectly()
    {
        var assets = new[]
        {
            Asset(AssetType.FrameworkDll, contentEncoding: "br",
                  cacheControl: "max-age=31536000, immutable")
        };

        var s = WasmCachingAnalysisService.AnalyzeAssets(assets, new CachingAnalysisThresholds()).Single();

        s.CompressionStatus.Should().Be(CompressionStatus.Brotli);
        s.CacheStatus.Should().Be(CacheStatus.ProperlyOptimized);
        s.Recommendation.Should().BeNull(); // optimal — no recommendation needed
    }

    [Fact]
    public void AnalyzeAssets_NotCompressedNotCached_RecommendationSet()
    {
        var assets = new[]
        {
            Asset(AssetType.FrameworkDll, contentEncoding: null, cacheControl: null)
        };

        var s = WasmCachingAnalysisService.AnalyzeAssets(assets, new CachingAnalysisThresholds()).Single();

        s.CompressionStatus.Should().Be(CompressionStatus.NotCompressed);
        s.CacheStatus.Should().Be(CacheStatus.NotCached);
        s.Recommendation.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void AnalyzeAssets_ETagPresent_HasETagTrue()
    {
        var assets = new[]
        {
            Asset(AssetType.Css, eTag: "\"abc123\"")
        };

        WasmCachingAnalysisService.AnalyzeAssets(assets, new CachingAnalysisThresholds())
            .Single().HasETag.Should().BeTrue();
    }

    // ── CalculateMetrics ──────────────────────────────────────────────────────

    [Fact]
    public void CalculateMetrics_EmptyList_ReturnsZeros()
    {
        var m = WasmCachingAnalysisService.CalculateMetrics([], new CachingAnalysisThresholds());

        m.TotalAssets.Should().Be(0);
        m.CompressedAssets.Should().Be(0);
        m.BrotliAssets.Should().Be(0);
    }

    [Fact]
    public void CalculateMetrics_MixedAssets_CountsCorrectly()
    {
        var summaries = new List<AssetCachingSummary>
        {
            // Brotli + optimised
            new() { Url = "a", Type = AssetType.FrameworkDll, CompressionStatus = CompressionStatus.Brotli, CacheStatus = CacheStatus.ProperlyOptimized, SizeBytes = 500_000 },
            // GZip + weak cache
            new() { Url = "b", Type = AssetType.FrameworkDll, CompressionStatus = CompressionStatus.Gzip,   CacheStatus = CacheStatus.WeaklyCached,      SizeBytes = 200_000 },
            // No compression + no cache
            new() { Url = "c", Type = AssetType.Css,          CompressionStatus = CompressionStatus.NotCompressed, CacheStatus = CacheStatus.NotCached, SizeBytes = 80_000 }
        };

        var m = WasmCachingAnalysisService.CalculateMetrics(summaries, new CachingAnalysisThresholds());

        m.TotalAssets.Should().Be(3);
        m.CompressedAssets.Should().Be(2);
        m.BrotliAssets.Should().Be(1);
        m.GzipAssets.Should().Be(1);
        m.CacheOptimizedAssets.Should().Be(1);
        m.AssetsWithoutCacheHeaders.Should().Be(1);
    }

    [Fact]
    public void CalculateMetrics_UncompressedLargeAssets_CountsAboveThreshold()
    {
        var t = new CachingAnalysisThresholds { LargeUncompressedKB = 50.0 };
        var summaries = new List<AssetCachingSummary>
        {
            new() { Url = "big",   Type = AssetType.FrameworkDll, CompressionStatus = CompressionStatus.NotCompressed, SizeBytes = 100_000, CacheStatus = CacheStatus.NotCached },
            new() { Url = "small", Type = AssetType.Css,          CompressionStatus = CompressionStatus.NotCompressed, SizeBytes = 10_000,  CacheStatus = CacheStatus.NotCached }
        };

        var m = WasmCachingAnalysisService.CalculateMetrics(summaries, t);

        m.UncompressedLargeAssets.Should().Be(1); // only "big" exceeds 50 KB
    }

    // ── DetectFindings ────────────────────────────────────────────────────────

    [Fact]
    public void DetectFindings_AllOptimised_NoFindings()
    {
        var summaries = new List<AssetCachingSummary>
        {
            new() { Url = "a", Type = AssetType.FrameworkDll, CompressionStatus = CompressionStatus.Brotli, CacheStatus = CacheStatus.ProperlyOptimized, SizeBytes = 1_000_000 },
            new() { Url = "b", Type = AssetType.Css,          CompressionStatus = CompressionStatus.Brotli, CacheStatus = CacheStatus.ProperlyOptimized, SizeBytes = 50_000 }
        };
        var metrics = WasmCachingAnalysisService.CalculateMetrics(summaries, new CachingAnalysisThresholds());

        var findings = WasmCachingAnalysisService.DetectFindings(metrics, summaries, new CachingAnalysisThresholds());

        findings.Should().BeEmpty();
    }

    [Fact]
    public void DetectFindings_NoCompression_GeneratesCCH001()
    {
        var summaries = new List<AssetCachingSummary>
        {
            new() { Url = "a", Type = AssetType.FrameworkDll, CompressionStatus = CompressionStatus.NotCompressed, CacheStatus = CacheStatus.ProperlyOptimized, SizeBytes = 500_000 }
        };
        var metrics = WasmCachingAnalysisService.CalculateMetrics(summaries, new CachingAnalysisThresholds());

        var findings = WasmCachingAnalysisService.DetectFindings(metrics, summaries, new CachingAnalysisThresholds());

        findings.Should().Contain(f => f.Id == "CCH-001");
        findings.Single(f => f.Id == "CCH-001").Severity.Should().Be(PerformanceSeverity.High);
    }

    [Fact]
    public void DetectFindings_GZipOnlyNoBrotli_GeneratesCCH002()
    {
        var summaries = new List<AssetCachingSummary>
        {
            new() { Url = "a", Type = AssetType.FrameworkDll, CompressionStatus = CompressionStatus.Gzip, CacheStatus = CacheStatus.ProperlyOptimized, SizeBytes = 500_000 }
        };
        var metrics = WasmCachingAnalysisService.CalculateMetrics(summaries, new CachingAnalysisThresholds());

        var findings = WasmCachingAnalysisService.DetectFindings(metrics, summaries, new CachingAnalysisThresholds());

        findings.Should().Contain(f => f.Id == "CCH-002");
        findings.Should().NotContain(f => f.Id == "CCH-001");
        findings.Single(f => f.Id == "CCH-002").Severity.Should().Be(PerformanceSeverity.Medium);
    }

    [Fact]
    public void DetectFindings_BrotliPresent_NoCCH001OrCCH002()
    {
        var summaries = new List<AssetCachingSummary>
        {
            new() { Url = "a", Type = AssetType.FrameworkDll, CompressionStatus = CompressionStatus.Brotli, CacheStatus = CacheStatus.ProperlyOptimized, SizeBytes = 1_000_000 }
        };
        var metrics = WasmCachingAnalysisService.CalculateMetrics(summaries, new CachingAnalysisThresholds());

        var findings = WasmCachingAnalysisService.DetectFindings(metrics, summaries, new CachingAnalysisThresholds());

        findings.Should().NotContain(f => f.Id == "CCH-001");
        findings.Should().NotContain(f => f.Id == "CCH-002");
    }

    [Fact]
    public void DetectFindings_LargeUncompressedAsset_GeneratesCCH003()
    {
        var t = TightThresholds; // LargeUncompressedKB = 10
        var summaries = new List<AssetCachingSummary>
        {
            new() { Url = "https://app/big.wasm", Type = AssetType.WasmRuntime,
                    CompressionStatus = CompressionStatus.NotCompressed,
                    CacheStatus = CacheStatus.ProperlyOptimized, SizeBytes = 500_000 }
        };
        var metrics = WasmCachingAnalysisService.CalculateMetrics(summaries, t);

        var findings = WasmCachingAnalysisService.DetectFindings(metrics, summaries, t);

        findings.Should().Contain(f => f.Id == "CCH-003");
    }

    [Fact]
    public void DetectFindings_LargeUncompressedOverOneMB_IsHighSeverity()
    {
        var t = TightThresholds;
        var summaries = new List<AssetCachingSummary>
        {
            new() { Url = "https://app/huge.wasm", Type = AssetType.WasmRuntime,
                    CompressionStatus = CompressionStatus.NotCompressed,
                    CacheStatus = CacheStatus.ProperlyOptimized, SizeBytes = 5_000_000 }
        };
        var metrics = WasmCachingAnalysisService.CalculateMetrics(summaries, t);

        var findings = WasmCachingAnalysisService.DetectFindings(metrics, summaries, t);

        findings.Single(f => f.Id == "CCH-003").Severity.Should().Be(PerformanceSeverity.High);
    }

    [Fact]
    public void DetectFindings_FrameworkNotCached_GeneratesCCH004()
    {
        var summaries = new List<AssetCachingSummary>
        {
            new() { Url = "https://app/_framework/a.wasm", Type = AssetType.FrameworkDll,
                    CompressionStatus = CompressionStatus.Brotli,
                    CacheStatus = CacheStatus.NotCached, SizeBytes = 500_000 }
        };
        var metrics = WasmCachingAnalysisService.CalculateMetrics(summaries, new CachingAnalysisThresholds());

        var findings = WasmCachingAnalysisService.DetectFindings(metrics, summaries, new CachingAnalysisThresholds());

        findings.Should().Contain(f => f.Id == "CCH-004");
    }

    [Fact]
    public void DetectFindings_StaticAssetNotCached_GeneratesCCH005()
    {
        var summaries = new List<AssetCachingSummary>
        {
            new() { Url = "https://app/css/app.css", Type = AssetType.Css,
                    CompressionStatus = CompressionStatus.Brotli,
                    CacheStatus = CacheStatus.NotCached, SizeBytes = 20_000 }
        };
        var metrics = WasmCachingAnalysisService.CalculateMetrics(summaries, new CachingAnalysisThresholds());

        var findings = WasmCachingAnalysisService.DetectFindings(metrics, summaries, new CachingAnalysisThresholds());

        findings.Should().Contain(f => f.Id == "CCH-005");
        findings.Single(f => f.Id == "CCH-005").Severity.Should().Be(PerformanceSeverity.Low);
    }

    [Fact]
    public void DetectFindings_IndexBootManifestNotCached_NotFlaggedAsCCH005()
    {
        var summaries = new List<AssetCachingSummary>
        {
            new() { Url = "https://app/", Type = AssetType.Index, CompressionStatus = CompressionStatus.Gzip, CacheStatus = CacheStatus.NotCached, SizeBytes = 5_000 },
            new() { Url = "https://app/_framework/blazor.boot.json", Type = AssetType.BootManifest, CompressionStatus = CompressionStatus.Gzip, CacheStatus = CacheStatus.NotCached, SizeBytes = 2_000 }
        };
        var metrics = WasmCachingAnalysisService.CalculateMetrics(summaries, new CachingAnalysisThresholds());

        var findings = WasmCachingAnalysisService.DetectFindings(metrics, summaries, new CachingAnalysisThresholds());

        findings.Should().NotContain(f => f.Id == "CCH-005");
    }

    [Fact]
    public void DetectFindings_NoFrameworkAssets_NoFrameworkFindings()
    {
        var summaries = new List<AssetCachingSummary>
        {
            new() { Url = "https://app/app.css", Type = AssetType.Css, CompressionStatus = CompressionStatus.Brotli, CacheStatus = CacheStatus.NotCached, SizeBytes = 10_000 }
        };
        var metrics = WasmCachingAnalysisService.CalculateMetrics(summaries, new CachingAnalysisThresholds());

        var findings = WasmCachingAnalysisService.DetectFindings(metrics, summaries, new CachingAnalysisThresholds());

        findings.Should().NotContain(f => f.Id == "CCH-001" || f.Id == "CCH-002" || f.Id == "CCH-004");
    }

    // ── GenerateRecommendations ───────────────────────────────────────────────

    [Fact]
    public void GenerateRecommendations_CCH001_IncludesBrotliAndGZipRecs()
    {
        var findings = new[]
        {
            new PerformanceFinding { Id = "CCH-001", Title = "", Severity = PerformanceSeverity.High,
                Category = PerformanceCategory.Compression, Description = "", Recommendation = "" }
        };

        var recs = WasmCachingAnalysisService.GenerateRecommendations(findings);

        recs.Should().Contain(r => r.Title.Contains("Brotli", StringComparison.OrdinalIgnoreCase));
        recs.Should().Contain(r => r.Title.Contains("GZip",   StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GenerateRecommendations_CCH002_OnlyBrotliRec()
    {
        var findings = new[]
        {
            new PerformanceFinding { Id = "CCH-002", Title = "", Severity = PerformanceSeverity.Medium,
                Category = PerformanceCategory.Compression, Description = "", Recommendation = "" }
        };

        var recs = WasmCachingAnalysisService.GenerateRecommendations(findings);

        recs.Should().Contain(r => r.Title.Contains("Brotli", StringComparison.OrdinalIgnoreCase));
        // Should not separately recommend GZip when CCH-001 not present
        recs.Should().NotContain(r => r.Title.Contains("GZip", StringComparison.OrdinalIgnoreCase) &&
                                      r.Title.Contains("fallback", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GenerateRecommendations_CCH004_IncludesCacheRec()
    {
        var findings = new[]
        {
            new PerformanceFinding { Id = "CCH-004", Title = "", Severity = PerformanceSeverity.Medium,
                Category = PerformanceCategory.Caching, Description = "", Recommendation = "" }
        };

        var recs = WasmCachingAnalysisService.GenerateRecommendations(findings);

        recs.Should().Contain(r => r.Title.Contains("immutable", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GenerateRecommendations_AlwaysIncludesApiCacheNote()
    {
        var recs = WasmCachingAnalysisService.GenerateRecommendations([]);

        recs.Should().Contain(r => r.Title.Contains("reference-data", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GenerateRecommendations_PrioritiesAreSequential()
    {
        var findings = new[]
        {
            new PerformanceFinding { Id = "CCH-001", Title = "", Severity = PerformanceSeverity.High,
                Category = PerformanceCategory.Compression, Description = "", Recommendation = "" },
            new PerformanceFinding { Id = "CCH-004", Title = "", Severity = PerformanceSeverity.Medium,
                Category = PerformanceCategory.Caching, Description = "", Recommendation = "" }
        };

        var recs = WasmCachingAnalysisService.GenerateRecommendations(findings);

        recs.Should().BeInAscendingOrder(r => r.Priority);
        recs.Select(r => r.Priority).Should().OnlyHaveUniqueItems();
    }

    // ── GetAssetRecommendation ────────────────────────────────────────────────

    [Fact]
    public void GetAssetRecommendation_FrameworkBrotliOptimised_ReturnsNull()
    {
        WasmCachingAnalysisService.GetAssetRecommendation(
            AssetType.FrameworkDll,
            CompressionStatus.Brotli,
            CacheStatus.ProperlyOptimized,
            1_000_000,
            new CachingAnalysisThresholds())
        .Should().BeNull();
    }

    [Fact]
    public void GetAssetRecommendation_FrameworkNotCompressedNotCached_ReturnsComboRec()
    {
        var rec = WasmCachingAnalysisService.GetAssetRecommendation(
            AssetType.FrameworkDll,
            CompressionStatus.NotCompressed,
            CacheStatus.NotCached,
            1_000_000,
            new CachingAnalysisThresholds());

        rec.Should().NotBeNullOrEmpty();
        rec.Should().Contain("Brotli");
        rec.Should().Contain("immutable");
    }

    [Fact]
    public void GetAssetRecommendation_CssLargeUncompressed_ReturnsCompressionRec()
    {
        var t = new CachingAnalysisThresholds { LargeUncompressedKB = 10.0 };
        var rec = WasmCachingAnalysisService.GetAssetRecommendation(
            AssetType.Css,
            CompressionStatus.NotCompressed,
            CacheStatus.ProperlyOptimized,
            100_000, // 100 KB — above threshold
            t);

        rec.Should().NotBeNullOrEmpty();
        rec.Should().Contain("compression");
    }

    [Fact]
    public void GetAssetRecommendation_CssSmallUncompressed_ReturnsNull()
    {
        var t = new CachingAnalysisThresholds { LargeUncompressedKB = 100.0 };
        var rec = WasmCachingAnalysisService.GetAssetRecommendation(
            AssetType.Css,
            CompressionStatus.NotCompressed,
            CacheStatus.ProperlyOptimized,
            5_000, // 5 KB — below threshold
            t);

        rec.Should().BeNull();
    }

    // ── Full Analyze integration ───────────────────────────────────────────────

    [Fact]
    public void Analyze_EmptyAssets_ReturnsEmptyResult()
    {
        var svc    = new WasmCachingAnalysisService();
        var result = svc.Analyze([]);

        result.Metrics.TotalAssets.Should().Be(0);
        result.AssetSummaries.Should().BeEmpty();
        result.Findings.Should().BeEmpty();
    }

    [Fact]
    public void Analyze_UnoptimisedApp_ProducesFindings()
    {
        var svc    = new WasmCachingAnalysisService();
        var assets = new[]
        {
            Asset(AssetType.FrameworkDll,   contentEncoding: null, cacheControl: null),
            Asset(AssetType.ApplicationDll, contentEncoding: null, cacheControl: null),
            Asset(AssetType.Css,            contentEncoding: null, cacheControl: null)
        };

        var result = svc.Analyze(assets);

        result.Findings.Should().NotBeEmpty();
        result.Recommendations.Should().NotBeEmpty();
    }
}
