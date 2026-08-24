using System.Text.Json.Serialization;

namespace BirkNext.Web.Models;

public enum PerformanceSeverity { Critical, High, Medium, Low, Info }

public enum PerformanceCategory
{
    Startup,
    Assets,
    ApiCalls,
    Caching,
    Compression,
    BlazorRuntime,
    Network,
    Configuration
}

public enum AssetType
{
    Index             = 0,
    FrameworkJs       = 1,
    BootManifest      = 2,
    FrameworkDll      = 3,
    ApplicationDll    = 4,
    SatelliteAssembly = 5,
    WasmRuntime       = 6,
    Css               = 7,
    JavaScript        = 8,
    Font              = 9,
    Image             = 10,
    Other             = 11
}

public sealed class DiscoveredAsset
{
    [JsonPropertyName("url")]
    public string Url { get; init; } = "";

    [JsonPropertyName("type")]
    public AssetType Type { get; init; }

    [JsonPropertyName("contentLength")]
    public long? ContentLength { get; init; }

    [JsonPropertyName("downloadedBytes")]
    public long DownloadedBytes { get; init; }

    [JsonPropertyName("contentEncoding")]
    public string? ContentEncoding { get; init; }

    [JsonPropertyName("contentType")]
    public string? ContentType { get; init; }

    [JsonPropertyName("cacheControl")]
    public string? CacheControl { get; init; }

    [JsonPropertyName("eTag")]
    public string? ETag { get; init; }

    [JsonPropertyName("lastModified")]
    public string? LastModified { get; init; }

    [JsonPropertyName("statusCode")]
    public int StatusCode { get; init; }

    [JsonPropertyName("downloadTimeMs")]
    public double DownloadTimeMs { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }
}

public sealed class WasmAssetDiscoveryRequest
{
    [JsonPropertyName("targetUrl")]
    public string TargetUrl { get; init; } = "";

    [JsonPropertyName("thresholds")]
    public PerformanceThresholdsPayload? Thresholds { get; init; }
}

public sealed class PerformanceThresholdsPayload
{
    [JsonPropertyName("maxStartupRequests")]
    public int? MaxStartupRequests { get; init; }

    [JsonPropertyName("maxStartupDownloadMB")]
    public double? MaxStartupDownloadMB { get; init; }

    [JsonPropertyName("maxFrameworkMB")]
    public double? MaxFrameworkMB { get; init; }

    [JsonPropertyName("maxApplicationMB")]
    public double? MaxApplicationMB { get; init; }

    [JsonPropertyName("maxIndividualAssetMB")]
    public double? MaxIndividualAssetMB { get; init; }
}

public enum PerformanceReadinessState
{
    Ready            = 0,
    MostlyReady      = 1,
    NeedsImprovement = 2,
    HighRisk         = 3,
    NotAssessed      = 4
}

public sealed class PerformanceCategorySummary
{
    [JsonPropertyName("categoryName")]
    public string CategoryName { get; init; } = "";

    [JsonPropertyName("category")]
    public PerformanceCategory Category { get; init; }

    [JsonPropertyName("score")]
    public int Score { get; init; }

    [JsonPropertyName("state")]
    public PerformanceReadinessState State { get; init; }

    [JsonPropertyName("findingsCount")]
    public int FindingsCount { get; init; }

    [JsonPropertyName("criticalCount")]
    public int CriticalCount { get; init; }

    [JsonPropertyName("highCount")]
    public int HighCount { get; init; }

    [JsonPropertyName("mediumCount")]
    public int MediumCount { get; init; }

    [JsonPropertyName("lowCount")]
    public int LowCount { get; init; }

    [JsonPropertyName("wasAssessed")]
    public bool WasAssessed { get; init; }
}

public sealed class PerformanceReadinessHealth
{
    [JsonPropertyName("overallScore")]
    public int OverallScore { get; init; }

    [JsonPropertyName("startupScore")]
    public int StartupScore { get; init; }

    [JsonPropertyName("apiScore")]
    public int ApiScore { get; init; }

    [JsonPropertyName("graphQlScore")]
    public int GraphQlScore { get; init; }

    [JsonPropertyName("cachingScore")]
    public int CachingScore { get; init; }

    [JsonPropertyName("compressionScore")]
    public int CompressionScore { get; init; }

    [JsonPropertyName("architectureScore")]
    public int ArchitectureScore { get; init; }

    [JsonPropertyName("criticalFindings")]
    public int CriticalFindings { get; init; }

    [JsonPropertyName("highFindings")]
    public int HighFindings { get; init; }

    [JsonPropertyName("mediumFindings")]
    public int MediumFindings { get; init; }

    [JsonPropertyName("lowFindings")]
    public int LowFindings { get; init; }
}

public sealed class PerformanceReadinessReport
{
    [JsonPropertyName("overallScore")]
    public int OverallScore { get; init; }

    [JsonPropertyName("overallState")]
    public PerformanceReadinessState OverallState { get; init; }

    [JsonPropertyName("categories")]
    public List<PerformanceCategorySummary> Categories { get; init; } = [];

    [JsonPropertyName("topRisks")]
    public List<PerformanceFinding> TopRisks { get; init; } = [];

    [JsonPropertyName("topRecommendations")]
    public List<PerformanceRecommendation> TopRecommendations { get; init; } = [];

    [JsonPropertyName("health")]
    public PerformanceReadinessHealth Health { get; init; } = new();

    [JsonPropertyName("hasData")]
    public bool HasData { get; init; }
}

public sealed class WasmAssetDiscoveryResult
{
    [JsonPropertyName("targetUrl")]
    public string TargetUrl { get; init; } = "";

    [JsonPropertyName("discoveredAt")]
    public DateTime DiscoveredAt { get; init; }

    [JsonPropertyName("isBlazorWasm")]
    public bool IsBlazorWasm { get; init; }

    [JsonPropertyName("assets")]
    public List<DiscoveredAsset> Assets { get; init; } = [];

    [JsonPropertyName("startupMetrics")]
    public StartupMetrics? StartupMetrics { get; init; }

    [JsonPropertyName("findings")]
    public List<PerformanceFinding> Findings { get; init; } = [];

    [JsonPropertyName("metrics")]
    public List<PerformanceMetric> Metrics { get; init; } = [];

    [JsonPropertyName("recommendations")]
    public List<PerformanceRecommendation> Recommendations { get; init; } = [];

    [JsonPropertyName("apiAnalysis")]
    public ApiAnalysisResult? ApiAnalysis { get; init; }

    [JsonPropertyName("cachingAnalysis")]
    public CachingAnalysisResult? CachingAnalysis { get; init; }

    [JsonPropertyName("readinessReport")]
    public PerformanceReadinessReport? ReadinessReport { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }
}

public sealed class StartupMetrics
{
    [JsonPropertyName("startupDownloadBytes")]
    public long StartupDownloadBytes { get; init; }

    [JsonPropertyName("frameworkDownloadBytes")]
    public long FrameworkDownloadBytes { get; init; }

    [JsonPropertyName("applicationDownloadBytes")]
    public long ApplicationDownloadBytes { get; init; }

    [JsonPropertyName("startupRequestCount")]
    public int StartupRequestCount { get; init; }

    [JsonPropertyName("frameworkAssemblyCount")]
    public int FrameworkAssemblyCount { get; init; }

    [JsonPropertyName("applicationAssemblyCount")]
    public int ApplicationAssemblyCount { get; init; }

    [JsonPropertyName("satelliteAssemblyCount")]
    public int SatelliteAssemblyCount { get; init; }

    [JsonPropertyName("javaScriptCount")]
    public int JavaScriptCount { get; init; }

    [JsonPropertyName("cssCount")]
    public int CssCount { get; init; }

    [JsonPropertyName("fontCount")]
    public int FontCount { get; init; }

    [JsonPropertyName("imageCount")]
    public int ImageCount { get; init; }

    [JsonPropertyName("largestAssetUrl")]
    public string? LargestAssetUrl { get; init; }

    [JsonPropertyName("largestAssetBytes")]
    public long LargestAssetBytes { get; init; }

    [JsonPropertyName("largestAssetType")]
    public AssetType LargestAssetType { get; init; }
}

public sealed class PerformanceFinding
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    [JsonPropertyName("title")]
    public string Title { get; init; } = "";

    [JsonPropertyName("severity")]
    public PerformanceSeverity Severity { get; init; }

    [JsonPropertyName("category")]
    public PerformanceCategory Category { get; init; }

    [JsonPropertyName("description")]
    public string Description { get; init; } = "";

    [JsonPropertyName("recommendation")]
    public string Recommendation { get; init; } = "";

    [JsonPropertyName("evidence")]
    public List<string> Evidence { get; init; } = [];
}

public sealed class PerformanceMetric
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("value")]
    public string Value { get; init; } = "";

    [JsonPropertyName("unit")]
    public string Unit { get; init; } = "";

    [JsonPropertyName("threshold")]
    public string? Threshold { get; init; }

    [JsonPropertyName("status")]
    public string Status { get; init; } = "";
}

public sealed class PerformanceRecommendation
{
    [JsonPropertyName("priority")]
    public int Priority { get; init; }

    [JsonPropertyName("title")]
    public string Title { get; init; } = "";

    [JsonPropertyName("description")]
    public string Description { get; init; } = "";

    [JsonPropertyName("category")]
    public PerformanceCategory Category { get; init; }
}

public enum CacheStatus
{
    ProperlyOptimized = 0,
    WeaklyCached      = 1,
    NotCached         = 2,
    Unknown           = 3
}

public enum CompressionStatus
{
    Brotli        = 0,
    Gzip          = 1,
    Other         = 2,
    NotCompressed = 3
}

public sealed class AssetCachingSummary
{
    [JsonPropertyName("url")]
    public string Url { get; init; } = "";

    [JsonPropertyName("type")]
    public AssetType Type { get; init; }

    [JsonPropertyName("contentEncoding")]
    public string? ContentEncoding { get; init; }

    [JsonPropertyName("cacheControl")]
    public string? CacheControl { get; init; }

    [JsonPropertyName("hasETag")]
    public bool HasETag { get; init; }

    [JsonPropertyName("hasLastModified")]
    public bool HasLastModified { get; init; }

    [JsonPropertyName("sizeBytes")]
    public long SizeBytes { get; init; }

    [JsonPropertyName("cacheStatus")]
    public CacheStatus CacheStatus { get; init; }

    [JsonPropertyName("compressionStatus")]
    public CompressionStatus CompressionStatus { get; init; }

    [JsonPropertyName("recommendation")]
    public string? Recommendation { get; init; }
}

public sealed class CachingMetrics
{
    [JsonPropertyName("totalAssets")]
    public int TotalAssets { get; init; }

    [JsonPropertyName("compressedAssets")]
    public int CompressedAssets { get; init; }

    [JsonPropertyName("brotliAssets")]
    public int BrotliAssets { get; init; }

    [JsonPropertyName("gzipAssets")]
    public int GzipAssets { get; init; }

    [JsonPropertyName("cacheOptimizedAssets")]
    public int CacheOptimizedAssets { get; init; }

    [JsonPropertyName("assetsWithoutCacheHeaders")]
    public int AssetsWithoutCacheHeaders { get; init; }

    [JsonPropertyName("uncompressedLargeAssets")]
    public int UncompressedLargeAssets { get; init; }
}

public sealed class CachingAnalysisResult
{
    [JsonPropertyName("metrics")]
    public CachingMetrics Metrics { get; init; } = new();

    [JsonPropertyName("assetSummaries")]
    public List<AssetCachingSummary> AssetSummaries { get; init; } = [];

    [JsonPropertyName("findings")]
    public List<PerformanceFinding> Findings { get; init; } = [];

    [JsonPropertyName("recommendations")]
    public List<PerformanceRecommendation> Recommendations { get; init; } = [];
}

public enum GraphQLOperationType { Query = 0, Mutation = 1, Subscription = 2, Unknown = 3 }

public sealed class GraphQLOperationSummary
{
    [JsonPropertyName("operationName")]
    public string OperationName { get; init; } = "";

    [JsonPropertyName("type")]
    public GraphQLOperationType Type { get; init; }

    [JsonPropertyName("calls")]
    public int Calls { get; init; }

    [JsonPropertyName("averageLatencyMs")]
    public double AverageLatencyMs { get; init; }

    [JsonPropertyName("largestResponseBytes")]
    public long LargestResponseBytes { get; init; }

    [JsonPropertyName("requestPayloadBytes")]
    public long RequestPayloadBytes { get; init; }

    [JsonPropertyName("errorCount")]
    public int ErrorCount { get; init; }

    [JsonPropertyName("isCompressed")]
    public bool IsCompressed { get; init; }

    [JsonPropertyName("recommendation")]
    public string? Recommendation { get; init; }
}

public sealed class RestEndpointSummary
{
    [JsonPropertyName("path")]
    public string Path { get; init; } = "";

    [JsonPropertyName("method")]
    public string Method { get; init; } = "";

    [JsonPropertyName("summary")]
    public string? Summary { get; init; }

    [JsonPropertyName("hasAuthRequirement")]
    public bool HasAuthRequirement { get; init; }
}

public sealed class ApiAnalysisResult
{
    [JsonPropertyName("hasGraphQL")]
    public bool HasGraphQL { get; init; }

    [JsonPropertyName("graphQLEndpoint")]
    public string? GraphQLEndpoint { get; init; }

    [JsonPropertyName("graphQLIntrospectionEnabled")]
    public bool GraphQLIntrospectionEnabled { get; init; }

    [JsonPropertyName("graphQLResponseCompressed")]
    public bool GraphQLResponseCompressed { get; init; }

    [JsonPropertyName("hasOpenApi")]
    public bool HasOpenApi { get; init; }

    [JsonPropertyName("openApiUrl")]
    public string? OpenApiUrl { get; init; }

    [JsonPropertyName("restEndpointCount")]
    public int RestEndpointCount { get; init; }

    [JsonPropertyName("graphQLOperations")]
    public List<GraphQLOperationSummary> GraphQLOperations { get; init; } = [];

    [JsonPropertyName("restEndpoints")]
    public List<RestEndpointSummary> RestEndpoints { get; init; } = [];

    [JsonPropertyName("findings")]
    public List<PerformanceFinding> Findings { get; init; } = [];

    [JsonPropertyName("recommendations")]
    public List<PerformanceRecommendation> Recommendations { get; init; } = [];

    [JsonPropertyName("error")]
    public string? Error { get; init; }
}

public sealed class ApiCallSummary
{
    public string Endpoint { get; init; } = "";
    public string Method { get; init; } = "";
    public int EstimatedCalls { get; init; }
    public string? Notes { get; init; }
}

public sealed class PerformanceHealth
{
    public int Score { get; init; }
    public int Critical { get; init; }
    public int High { get; init; }
    public int Medium { get; init; }
    public int Low { get; init; }
    public int Info { get; init; }
    public int FindingsCount { get; init; }
    public int AssetsDiscovered { get; init; }
    public long TotalTransferBytes { get; init; }
}

public sealed class WasmPerformanceReviewReport
{
    public string TargetUrl { get; init; } = "";
    public DateTime ReviewedAt { get; init; }
    public PerformanceHealth Health { get; init; } = new();
    public StartupMetrics? StartupMetrics { get; init; }
    public ApiAnalysisResult? ApiAnalysis { get; init; }
    public CachingAnalysisResult? CachingAnalysis { get; init; }
    public List<PerformanceFinding> Findings { get; init; } = [];
    public List<DiscoveredAsset> Assets { get; init; } = [];
    public List<ApiCallSummary> ApiCalls { get; init; } = [];
    public List<PerformanceMetric> Metrics { get; init; } = [];
    public List<PerformanceRecommendation> Recommendations { get; init; } = [];
    public PerformanceReadinessReport? ReadinessReport { get; init; }
    public List<string> Limitations { get; init; } = [];
    public bool IsBlazorWasm { get; init; }
    public string? ErrorMessage { get; init; }
}
