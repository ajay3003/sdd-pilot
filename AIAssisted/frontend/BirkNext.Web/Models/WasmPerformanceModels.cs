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
    public List<PerformanceFinding> Findings { get; init; } = [];
    public List<DiscoveredAsset> Assets { get; init; } = [];
    public List<ApiCallSummary> ApiCalls { get; init; } = [];
    public List<PerformanceMetric> Metrics { get; init; } = [];
    public List<PerformanceRecommendation> Recommendations { get; init; } = [];
    public List<string> Limitations { get; init; } = [];
    public bool IsBlazorWasm { get; init; }
    public string? ErrorMessage { get; init; }
}
