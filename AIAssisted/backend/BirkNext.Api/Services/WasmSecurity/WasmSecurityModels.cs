namespace BirkNext.Api.Services.WasmSecurity;

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
    public required string Key { get; init; }
    public required string MaskedValue { get; init; }
    public required string Context { get; init; }
}

public sealed class WasmSecurityFinding
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public WasmSecuritySeverity Severity { get; init; }
    public WasmSecurityCategory Category { get; init; }
    public WasmSecurityStatus Status { get; init; }
    public required string Description { get; init; }
    public required string Recommendation { get; init; }
    public List<WasmSecurityEvidence> Evidence { get; init; } = [];
    public string? ConstitutionRule { get; init; }
    public string? ConstitutionRuleTitle { get; init; }
}

public sealed class WasmSecurityCheck
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public WasmSecurityCategory Category { get; init; }
    public required string Description { get; init; }
}

public sealed class WasmDiscoveredAsset
{
    public required string Url { get; init; }
    public required string AssetType { get; init; }
    public required string Status { get; init; }
    public long? SizeBytes { get; init; }
    public bool Analyzed { get; init; }
}

public sealed class DiscoveredEndpoint
{
    public required string Url { get; init; }
    public required string Classification { get; init; }
    public required string FoundIn { get; init; }
}

public sealed class ConfigurationEntry
{
    public required string Key { get; init; }
    public required string MaskedValue { get; init; }
    public bool HasFinding { get; init; }
    public WasmSecuritySeverity? FindingSeverity { get; init; }
}

public sealed class SecurityHeaderResult
{
    public required string Header { get; init; }
    public required string Status { get; init; }
    public string? Value { get; init; }
    public required string Recommendation { get; init; }
}

public sealed class WasmSecurityHealth
{
    public int Score { get; init; }
    public int Critical { get; init; }
    public int High { get; init; }
    public int Medium { get; init; }
    public int Low { get; init; }
    public int Info { get; init; }
    public int AssetsScanned { get; init; }
    public int FindingsCount { get; init; }
    public int EndpointsDiscovered { get; init; }
    public int HeadersChecked { get; init; }
}

public sealed class WasmSecurityReviewReport
{
    public required string TargetUrl { get; init; }
    public DateTime ScannedAt { get; init; }
    public WasmSecurityHealth Health { get; init; } = new();
    public List<WasmSecurityFinding> Findings { get; init; } = [];
    public List<WasmDiscoveredAsset> Assets { get; init; } = [];
    public List<DiscoveredEndpoint> Endpoints { get; init; } = [];
    public List<ConfigurationEntry> ConfigurationSummary { get; init; } = [];
    public List<SecurityHeaderResult> Headers { get; init; } = [];
    public List<string> Recommendations { get; init; } = [];
    public List<string> Limitations { get; init; } = [];
    public bool IsBlazorWasm { get; init; }
    public string? ErrorMessage { get; init; }
}

public sealed class WasmScanRequest
{
    public required string TargetUrl { get; init; }
    public string? EnvironmentName { get; init; }
    public string? ExpectedApiGatewayBasePath { get; init; }
    public List<string> AllowedBackendHostnames { get; init; } = [];
    public string? AllowedAuthority { get; init; }
    public List<string> AllowedClientIds { get; init; } = [];
    public List<string> KnownSafeDomains { get; init; } = [];
}
