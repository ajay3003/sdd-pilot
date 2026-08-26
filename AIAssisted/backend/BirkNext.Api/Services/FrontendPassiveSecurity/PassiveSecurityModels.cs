namespace BirkNext.Api.Services.FrontendPassiveSecurity;

public enum PassiveSecurityExecutionStatus { NotAssessed, Assessed, EngineError, TimedOut, Skipped, AuthenticationRequired }
public enum PassiveSecurityReadinessState { Disabled, Ready, DockerUnavailable, ZapImageUnavailable, ZapLaunchFailed, ConfigurationInvalid }

public sealed record PassiveSecurityFinding(string PluginId, string? AlertRef, string Name, string Risk,
    string Confidence, string Description, string Url, string? Parameter, string? Evidence, string Solution,
    List<string> References, string? Cwe, string? Wasc, int InstancesCount, string Source = "ZAP Passive");

public sealed record PassiveSecurityConfiguration(bool ActiveScan = false, bool Spider = false,
    bool AjaxSpider = false, bool AttackMode = false, bool Fuzzing = false, string Scope = "Single configured target URL and trusted origin",
    int MaxDurationSeconds = 120, string Invocation = "ZAP daemon; one request through proxy; passive queue drain");

public sealed record PassiveSecurityResult(PassiveSecurityExecutionStatus ExecutionStatus = PassiveSecurityExecutionStatus.NotAssessed,
    string EngineName = "ZAP Passive", string ExecutionMode = "Passive", string? ZapVersion = null,
    string? RequestedUrl = null, string? FinalUrl = null, DateTime? StartedAt = null, DateTime? CompletedAt = null,
    long? DurationMs = null, int HighCount = 0, int MediumCount = 0, int LowCount = 0, int InformationalCount = 0,
    List<PassiveSecurityFinding>? Findings = null, List<string>? Limitations = null, string? EngineError = null,
    string ScopeSummary = "Configured target only; no spidering", PassiveSecurityConfiguration? ConfigurationSummary = null)
{
    public List<PassiveSecurityFinding> Findings { get; init; } = Findings ?? [];
    public List<string> Limitations { get; init; } = Limitations ?? [FrontendZapPassiveReviewService.PassiveLimitation];
}

public sealed record PassiveSecurityReadiness(PassiveSecurityReadinessState State, bool Available,
    string? ZapVersion = null, string ExecutionMode = "Passive", string? Image = null, string? ImageDigest = null, string? Error = null);

public sealed record PassiveSecurityReviewRequest(string TargetUrl, string EnvironmentProfileId,
    string ConfiguredBaseUrl, string EnvironmentType, bool RequiresAuthentication = false, int TimeoutSeconds = 120);
