using System.Text.Json.Serialization;

namespace BirkNext.Api.Services.WasmPerformance;

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
    public required string Url          { get; init; }
    public AssetType       Type         { get; init; }
    public long?           ContentLength  { get; init; }
    public long            DownloadedBytes { get; init; }
    public string?         ContentEncoding { get; init; }
    public string?         ContentType   { get; init; }
    public string?         CacheControl  { get; init; }
    public string?         ETag          { get; init; }
    public string?         LastModified  { get; init; }
    public int             StatusCode    { get; init; }
    public double          DownloadTimeMs { get; init; }
    public string?         Error         { get; init; }
}

public sealed class WasmAssetDiscoveryRequest
{
    public required string TargetUrl { get; init; }
}

public sealed class WasmAssetDiscoveryResult
{
    public required string                         TargetUrl        { get; init; }
    public DateTime                                DiscoveredAt     { get; init; }
    public bool                                    IsBlazorWasm     { get; init; }
    public List<DiscoveredAsset>                   Assets           { get; init; } = [];
    public StartupMetrics?                         StartupMetrics   { get; init; }
    public List<PerformanceFinding>                Findings         { get; init; } = [];
    public List<PerformanceMetric>                 Metrics          { get; init; } = [];
    public List<PerformanceRecommendation>         Recommendations  { get; init; } = [];
    public ApiAnalysisResult?                      ApiAnalysis      { get; init; }
    public CachingAnalysisResult?                  CachingAnalysis  { get; init; }
    public PerformanceReadinessReport?             ReadinessReport  { get; init; }
    public string?                                 Error            { get; init; }
}

// ── Startup analysis models ───────────────────────────────────────────────────

public enum PerformanceSeverity { Critical = 0, High = 1, Medium = 2, Low = 3, Info = 4 }

public enum PerformanceCategory
{
    Startup       = 0,
    Assets        = 1,
    ApiCalls      = 2,
    Caching       = 3,
    Compression   = 4,
    BlazorRuntime = 5,
    Network       = 6,
    Configuration = 7
}

public sealed class StartupAnalysisThresholds
{
    public double MaxStartupDownloadMB      { get; init; } = 5.0;
    public int    MaxStartupRequests        { get; init; } = 150;
    public double MaxFrameworkMB            { get; init; } = 3.0;
    public double MaxApplicationMB          { get; init; } = 1.0;
    public double MaxIndividualAssetMB      { get; init; } = 0.5;
    public double MaxUserJavaScriptKB       { get; init; } = 200.0;
    public double MaxCssKB                  { get; init; } = 200.0;
    public double MaxSatelliteResourcesMB   { get; init; } = 0.5;
}

public sealed class StartupMetrics
{
    public long      StartupDownloadBytes     { get; init; }
    public long      FrameworkDownloadBytes   { get; init; }
    public long      ApplicationDownloadBytes { get; init; }
    public int       StartupRequestCount      { get; init; }
    public int       FrameworkAssemblyCount   { get; init; }
    public int       ApplicationAssemblyCount { get; init; }
    public int       SatelliteAssemblyCount   { get; init; }
    public int       JavaScriptCount          { get; init; }
    public int       CssCount                 { get; init; }
    public int       FontCount                { get; init; }
    public int       ImageCount               { get; init; }
    public string?   LargestAssetUrl          { get; init; }
    public long      LargestAssetBytes        { get; init; }
    public AssetType LargestAssetType         { get; init; }
}

public sealed class PerformanceFinding
{
    public required string          Id             { get; init; }
    public required string          Title          { get; init; }
    public PerformanceSeverity      Severity       { get; init; }
    public PerformanceCategory      Category       { get; init; }
    public required string          Description    { get; init; }
    public required string          Recommendation { get; init; }
    public List<string>             Evidence       { get; init; } = [];
}

public sealed class PerformanceMetric
{
    public required string Name      { get; init; }
    public required string Value     { get; init; }
    public string          Unit      { get; init; } = "";
    public string?         Threshold { get; init; }
    public string          Status    { get; init; } = "";
}

public sealed class PerformanceRecommendation
{
    public int                 Priority    { get; init; }
    public required string     Title       { get; init; }
    public required string     Description { get; init; }
    public PerformanceCategory Category    { get; init; }
}

public sealed class StartupAnalysisResult
{
    public StartupMetrics                   StartupMetrics  { get; init; } = new();
    public IReadOnlyList<PerformanceFinding>       Findings        { get; init; } = [];
    public IReadOnlyList<PerformanceMetric>        DisplayMetrics  { get; init; } = [];
    public IReadOnlyList<PerformanceRecommendation> Recommendations { get; init; } = [];
}

// ── Blazor boot manifest JSON models ──────────────────────────────────────────

public sealed class BlazorBootManifest
{
    [JsonPropertyName("mainAssemblyName")]
    public string? MainAssemblyName { get; init; }

    [JsonPropertyName("resources")]
    public BlazorBootResources? Resources { get; init; }

    [JsonPropertyName("cacheBootResources")]
    public bool CacheBootResources { get; init; }

    [JsonPropertyName("debugLevel")]
    public int DebugLevel { get; init; }

    [JsonPropertyName("globalizationMode")]
    public string? GlobalizationMode { get; init; }
}

public sealed class BlazorBootResources
{
    [JsonPropertyName("hash")]
    public string? Hash { get; init; }

    [JsonPropertyName("jsModuleNative")]
    public Dictionary<string, string>? JsModuleNative { get; init; }

    [JsonPropertyName("jsModuleRuntime")]
    public Dictionary<string, string>? JsModuleRuntime { get; init; }

    [JsonPropertyName("wasmNative")]
    public Dictionary<string, string>? WasmNative { get; init; }

    [JsonPropertyName("icu")]
    public Dictionary<string, string>? Icu { get; init; }

    [JsonPropertyName("coreAssembly")]
    public Dictionary<string, string>? CoreAssembly { get; init; }

    [JsonPropertyName("assembly")]
    public Dictionary<string, string>? Assembly { get; init; }

    [JsonPropertyName("pdb")]
    public Dictionary<string, string>? Pdb { get; init; }

    [JsonPropertyName("satelliteResources")]
    public Dictionary<string, Dictionary<string, string>>? SatelliteResources { get; init; }
}
