using System.Text.Json.Serialization;

namespace BirkNext.Web.Models;

/// <summary>
/// Frontend DTOs for the Frontend Quality Engines capability model (Phase 2/3).
/// Distinct from FrontendQualityCoverageModels.cs (Quality Review execution model).
/// Enums use integer-backed serialization matching backend FrontendQualityEngineId order.
/// </summary>

/// <summary>Four supported frontend quality engines (capability model, not execution outcomes).</summary>
public enum FrontendQualityEngineIdDto
{
    BrowserRuntime = 0,
    Accessibility = 1,
    Lighthouse = 2,
    PassiveSecurity = 3,
}

/// <summary>Typed reason codes for engine unavailability (no free-text reasons).</summary>
public enum FrontendQualityEngineUnavailableReasonDto
{
    None = 0,
    BlockedByDeploymentPolicy = 1,
    DisabledInSystemSettings = 2,
    RuntimeUnavailable = 3,
    RuntimeStatusUnknown = 4,
    NotApplicableToReview = 5,
    AuthenticationModeUnsupported = 6,
}

/// <summary>Layer 3 runtime readiness status for one engine.</summary>
public class FrontendQualityEngineReadinessDto
{
    [JsonPropertyName("engineId")]
    public FrontendQualityEngineIdDto EngineId { get; set; }

    [JsonPropertyName("isAvailable")]
    public bool IsAvailable { get; set; }

    [JsonPropertyName("statusReason")]
    public string? StatusReason { get; set; }

    [JsonPropertyName("checkedAtUtc")]
    public DateTime CheckedAtUtc { get; set; }
}

/// <summary>Complete capability model for one engine: all five layers + effective availability.</summary>
public class FrontendQualityEngineStatusDto
{
    [JsonPropertyName("engineId")]
    public FrontendQualityEngineIdDto EngineId { get; set; }

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("layer1Allowed")]
    public bool Layer1Allowed { get; set; }

    [JsonPropertyName("layer2Enabled")]
    public bool Layer2Enabled { get; set; }

    [JsonPropertyName("layer3Readiness")]
    public FrontendQualityEngineReadinessDto? Layer3Readiness { get; set; }

    [JsonPropertyName("authModeSupported")]
    public bool AuthModeSupported { get; set; }

    [JsonPropertyName("available")]
    public bool Available { get; set; }

    [JsonPropertyName("reasons")]
    public List<FrontendQualityEngineUnavailableReasonDto> Reasons { get; set; } = new();
}

/// <summary>Status report for all four engines at one moment in time.</summary>
public class FrontendQualityEngineStatusReportDto
{
    [JsonPropertyName("engines")]
    public List<FrontendQualityEngineStatusDto> Engines { get; set; } = new();

    [JsonPropertyName("checkedAtUtc")]
    public DateTime CheckedAtUtc { get; set; }
}
