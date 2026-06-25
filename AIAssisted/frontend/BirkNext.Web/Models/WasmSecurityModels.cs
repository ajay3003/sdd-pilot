using System.Text.Json.Serialization;

namespace BirkNext.Web.Models;

public enum WasmSecuritySeverity { Critical, High, Medium, Low, Info }

public enum WasmSecurityCategory
{
    SecretsExposure,
    BackendEndpointExposure,
    AuthenticationConfiguration,
    TokenStorage,
    BrowserStorage,
    SourceMapExposure,
    DebugArtifactExposure,
    SecurityHeaders,
    CorsConfiguration,
    SensitiveDataExposure,
    BlazorSpecific,
    DevelopmentArtifact,
    ConfigurationExposure,
}

public enum WasmSecurityStatus { Pass, Warning, Fail, NotApplicable, NotTested }

public sealed class WasmSecurityEvidence
{
    [JsonPropertyName("key")]         public string Key         { get; init; } = "";
    [JsonPropertyName("maskedValue")] public string MaskedValue { get; init; } = "";
    [JsonPropertyName("context")]     public string Context     { get; init; } = "";
}

public sealed class WasmSecurityFinding
{
    [JsonPropertyName("id")]                   public string                    Id                   { get; init; } = "";
    [JsonPropertyName("title")]                public string                    Title                { get; init; } = "";
    [JsonPropertyName("severity")]             public WasmSecuritySeverity      Severity             { get; init; }
    [JsonPropertyName("category")]             public WasmSecurityCategory      Category             { get; init; }
    [JsonPropertyName("status")]               public WasmSecurityStatus        Status               { get; init; }
    [JsonPropertyName("description")]          public string                    Description          { get; init; } = "";
    [JsonPropertyName("recommendation")]       public string                    Recommendation       { get; init; } = "";
    [JsonPropertyName("evidence")]             public List<WasmSecurityEvidence> Evidence            { get; init; } = [];
    [JsonPropertyName("constitutionRule")]     public string?                   ConstitutionRule     { get; init; }
    [JsonPropertyName("constitutionRuleTitle")] public string?                  ConstitutionRuleTitle { get; init; }
}

public sealed class WasmDiscoveredAsset
{
    [JsonPropertyName("url")]       public string  Url       { get; init; } = "";
    [JsonPropertyName("assetType")] public string  AssetType { get; init; } = "";
    [JsonPropertyName("status")]    public string  Status    { get; init; } = "";
    [JsonPropertyName("sizeBytes")] public long?   SizeBytes { get; init; }
    [JsonPropertyName("analyzed")]  public bool    Analyzed  { get; init; }
}

public sealed class DiscoveredEndpoint
{
    [JsonPropertyName("url")]            public string Url            { get; init; } = "";
    [JsonPropertyName("classification")] public string Classification { get; init; } = "";
    [JsonPropertyName("foundIn")]        public string FoundIn        { get; init; } = "";
}

public sealed class ConfigurationEntry
{
    [JsonPropertyName("key")]             public string                Key             { get; init; } = "";
    [JsonPropertyName("maskedValue")]     public string                MaskedValue     { get; init; } = "";
    [JsonPropertyName("hasFinding")]      public bool                  HasFinding      { get; init; }
    [JsonPropertyName("findingSeverity")] public WasmSecuritySeverity? FindingSeverity { get; init; }
}

public sealed class SecurityHeaderResult
{
    [JsonPropertyName("header")]         public string  Header         { get; init; } = "";
    [JsonPropertyName("status")]         public string  Status         { get; init; } = "";
    [JsonPropertyName("value")]          public string? Value          { get; init; }
    [JsonPropertyName("recommendation")] public string  Recommendation { get; init; } = "";
}

public sealed class WasmSecurityHealth
{
    [JsonPropertyName("score")]               public int Score              { get; init; }
    [JsonPropertyName("critical")]            public int Critical           { get; init; }
    [JsonPropertyName("high")]                public int High               { get; init; }
    [JsonPropertyName("medium")]              public int Medium             { get; init; }
    [JsonPropertyName("low")]                 public int Low                { get; init; }
    [JsonPropertyName("info")]                public int Info               { get; init; }
    [JsonPropertyName("assetsScanned")]       public int AssetsScanned      { get; init; }
    [JsonPropertyName("findingsCount")]       public int FindingsCount      { get; init; }
    [JsonPropertyName("endpointsDiscovered")] public int EndpointsDiscovered { get; init; }
    [JsonPropertyName("headersChecked")]      public int HeadersChecked     { get; init; }
}

public sealed class WasmSecurityReviewReport
{
    [JsonPropertyName("targetUrl")]           public string                       TargetUrl            { get; init; } = "";
    [JsonPropertyName("scannedAt")]           public DateTime                     ScannedAt            { get; init; }
    [JsonPropertyName("health")]              public WasmSecurityHealth           Health               { get; init; } = new();
    [JsonPropertyName("findings")]            public List<WasmSecurityFinding>    Findings             { get; init; } = [];
    [JsonPropertyName("assets")]              public List<WasmDiscoveredAsset>    Assets               { get; init; } = [];
    [JsonPropertyName("endpoints")]           public List<DiscoveredEndpoint>     Endpoints            { get; init; } = [];
    [JsonPropertyName("configurationSummary")] public List<ConfigurationEntry>   ConfigurationSummary { get; init; } = [];
    [JsonPropertyName("headers")]             public List<SecurityHeaderResult>   Headers              { get; init; } = [];
    [JsonPropertyName("recommendations")]     public List<string>                 Recommendations      { get; init; } = [];
    [JsonPropertyName("limitations")]         public List<string>                 Limitations          { get; init; } = [];
    [JsonPropertyName("isBlazorWasm")]        public bool                         IsBlazorWasm         { get; init; }
    [JsonPropertyName("errorMessage")]        public string?                      ErrorMessage         { get; init; }
}

public sealed class WasmScanRequest
{
    [JsonPropertyName("targetUrl")]                public string       TargetUrl                { get; init; } = "";
    [JsonPropertyName("environmentName")]          public string?      EnvironmentName          { get; init; }
    [JsonPropertyName("expectedApiGatewayBasePath")] public string?   ExpectedApiGatewayBasePath { get; init; }
    [JsonPropertyName("allowedBackendHostnames")]  public List<string> AllowedBackendHostnames   { get; init; } = [];
    [JsonPropertyName("allowedAuthority")]         public string?      AllowedAuthority          { get; init; }
    [JsonPropertyName("allowedClientIds")]         public List<string> AllowedClientIds           { get; init; } = [];
    [JsonPropertyName("knownSafeDomains")]         public List<string> KnownSafeDomains          { get; init; } = [];
}
