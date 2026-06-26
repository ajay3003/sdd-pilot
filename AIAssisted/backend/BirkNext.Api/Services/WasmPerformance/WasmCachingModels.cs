namespace BirkNext.Api.Services.WasmPerformance;

public enum CacheStatus
{
    ProperlyOptimized = 0, // long-lived cache (>= 1 day) OR immutable
    WeaklyCached      = 1, // some cache guidance but short/revalidation-only
    NotCached         = 2, // no-store, max-age=0, or no headers
    Unknown           = 3  // could not determine (error asset)
}

public enum CompressionStatus
{
    Brotli        = 0,
    Gzip          = 1,
    Other         = 2,
    NotCompressed = 3
}

public sealed class CachingAnalysisThresholds
{
    public double LargeUncompressedKB { get; init; } = 50.0;
    public int    MinCacheDurationSec  { get; init; } = 86_400;   // 1 day — threshold for ProperlyOptimized
}

public sealed class AssetCachingSummary
{
    public required string     Url               { get; init; }
    public AssetType           Type              { get; init; }
    public string?             ContentEncoding   { get; init; }
    public string?             CacheControl      { get; init; }
    public bool                HasETag           { get; init; }
    public bool                HasLastModified   { get; init; }
    public long                SizeBytes         { get; init; }
    public CacheStatus         CacheStatus       { get; init; }
    public CompressionStatus   CompressionStatus { get; init; }
    public string?             Recommendation    { get; init; }
}

public sealed class CachingMetrics
{
    public int TotalAssets               { get; init; }
    public int CompressedAssets          { get; init; }
    public int BrotliAssets              { get; init; }
    public int GzipAssets                { get; init; }
    public int CacheOptimizedAssets      { get; init; }
    public int AssetsWithoutCacheHeaders { get; init; }
    public int UncompressedLargeAssets   { get; init; }
}

public sealed class CachingAnalysisResult
{
    public CachingMetrics                  Metrics         { get; init; } = new();
    public List<AssetCachingSummary>       AssetSummaries  { get; init; } = [];
    public List<PerformanceFinding>        Findings        { get; init; } = [];
    public List<PerformanceRecommendation> Recommendations { get; init; } = [];
}
