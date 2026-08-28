using System.Text.Json.Serialization;

namespace BirkNext.Web.Models;

/// <summary>
/// Frontend DTOs for the Frontend Quality Engines capability model (Phase 2/3).
/// Distinct from FrontendQualityCoverageModels.cs (Quality Review execution model).
/// Enums use integer-backed serialization matching backend FrontendQualityEngineId order.
/// </summary>

/// <summary>Authentication mode for review execution.</summary>
public enum ReviewAuthenticationModeDto
{
    Anonymous = 0,
    Authenticated = 1,
}

/// <summary>Engine selection context for status queries.</summary>
public sealed class ReviewEngineSelectionDto
{
    [JsonPropertyName("selected")]
    public Dictionary<FrontendQualityEngineIdDto, bool> Selected { get; set; } = new();
}

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

/// <summary>Readiness status for a single engine (revalidation result).</summary>
public class FrontendQualityEngineReadinessReportDto
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

/// <summary>Execution snapshot captured at Quality Review run start.</summary>
public sealed class FrontendQualityEngineExecutionSnapshot
{
    [JsonPropertyName("layer1Allowed")]
    public Dictionary<FrontendQualityEngineIdDto, bool> Layer1Allowed { get; set; } = new();

    [JsonPropertyName("layer2Enabled")]
    public Dictionary<FrontendQualityEngineIdDto, bool> Layer2Enabled { get; set; } = new();

    [JsonPropertyName("selectedEngines")]
    public Dictionary<FrontendQualityEngineIdDto, bool> SelectedEngines { get; set; } = new();

    [JsonPropertyName("authModeSupported")]
    public Dictionary<FrontendQualityEngineIdDto, bool> AuthModeSupported { get; set; } = new();

    [JsonPropertyName("authMode")]
    public ReviewAuthenticationModeDto AuthMode { get; set; } = ReviewAuthenticationModeDto.Anonymous;

    [JsonPropertyName("capturedAtUtc")]
    public DateTime CapturedAtUtc { get; set; } = DateTime.UtcNow;
}
