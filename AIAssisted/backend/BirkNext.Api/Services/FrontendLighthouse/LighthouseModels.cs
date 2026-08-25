namespace BirkNext.Api.Services.FrontendLighthouse;

public enum LighthouseExecutionStatus { NotAssessed, Assessed, EngineError, Skipped, AuthenticationRequired, TimedOut }
public enum LighthouseReadinessState { Disabled, Ready, NodeUnavailable, LighthouseUnavailable, ChromiumUnavailable, LaunchFailed, ConfigurationInvalid }
public enum LighthouseMetricStatus { Measured, Good, NeedsImprovement, Poor, NotAvailable, FieldDataRequired }

public sealed record LighthouseMetric(
    string Name,
    double? ObservedValue = null,
    string? Unit = null,
    LighthouseMetricStatus Status = LighthouseMetricStatus.NotAvailable,
    string Source = "Lighthouse",
    string MeasurementType = "Lab",
    string? AuditId = null,
    double? Threshold = null,
    string? ThresholdSource = null);

public sealed record LighthouseAuditFinding(
    string AuditId,
    string Title,
    string? Description = null,
    double? Score = null,
    string? DisplayValue = null,
    List<string>? Sources = null);

public sealed record LighthouseEffectiveConfiguration(
    string FormFactor = "Desktop",
    int ViewportWidth = 1350,
    int ViewportHeight = 940,
    string ThrottlingMode = "simulate",
    double CpuSlowdownMultiplier = 1,
    int NetworkRttMs = 40,
    int NetworkThroughputKbps = 10240,
    string Locale = "en-US",
    string CacheState = "Cleared",
    string NavigationMode = "Navigation",
    List<string>? Categories = null);

public sealed record LighthouseReviewResult(
    LighthouseExecutionStatus ExecutionStatus = LighthouseExecutionStatus.NotAssessed,
    string EngineName = "Lighthouse Lab Performance",
    string MeasurementType = "Lab",
    bool FieldDataAvailable = false,
    string? LighthouseVersion = null,
    string? NodeVersion = null,
    string? BrowserName = null,
    string? BrowserVersion = null,
    string? RequestedUrl = null,
    string? FinalUrl = null,
    DateTime? StartedAt = null,
    DateTime? CompletedAt = null,
    long? DurationMs = null,
    int? PerformanceScore = null,
    List<LighthouseMetric>? Metrics = null,
    List<LighthouseAuditFinding>? Audits = null,
    List<string>? Limitations = null,
    string? EngineError = null,
    LighthouseEffectiveConfiguration? EffectiveConfiguration = null)
{
    public List<LighthouseMetric> Metrics { get; init; } = Metrics ?? [];
    public List<LighthouseAuditFinding> Audits { get; init; } = Audits ?? [];
    public List<string> Limitations { get; init; } = Limitations ?? [];
}

public sealed record LighthouseReadinessResult(
    LighthouseReadinessState State,
    bool Available,
    string? LighthouseVersion = null,
    string? NodeVersion = null,
    string? BrowserName = null,
    string? BrowserVersion = null,
    string? Error = null);

public sealed record LighthouseReviewOptions(
    int TimeoutMs = 90000,
    string EnvironmentType = "Public");
