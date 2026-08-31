using System.Text.Json.Serialization;
namespace BirkNext.Web.Models;

public enum PassiveSecurityExecutionStatusDto { NotAssessed, Assessed, EngineError, TimedOut, Skipped, AuthenticationRequired }
public enum PassiveSecurityOutcomeReasonDto { None, DisabledInSystemSettings, ReadinessUnavailable, AuthenticationModeUnsupported, TargetPolicyRejected, EngineUnavailable, EngineError, Cancelled }
public sealed record PassiveSecurityFindingDto(string PluginId, string? AlertRef, string Name, string Risk, string Confidence,
    string Description, string Url, string? Parameter, string? Evidence, string Solution, List<string> References,
    string? Cwe, string? Wasc, int InstancesCount, string Source);
public sealed record PassiveSecurityConfigurationDto(bool ActiveScan, bool Spider, bool AjaxSpider, bool AttackMode, bool Fuzzing,
    string Scope, int MaxDurationSeconds, string Invocation);
public sealed record PassiveSecurityResultDto(PassiveSecurityExecutionStatusDto ExecutionStatus,
    string EngineName, string ExecutionMode, string? ZapVersion, string? RequestedUrl, string? FinalUrl,
    DateTime? StartedAt, DateTime? CompletedAt, long? DurationMs, int HighCount, int MediumCount, int LowCount,
    int InformationalCount, List<PassiveSecurityFindingDto>? Findings, List<string>? Limitations, string? EngineError,
    string ScopeSummary, PassiveSecurityConfigurationDto? ConfigurationSummary, PassiveSecurityOutcomeReasonDto OutcomeReason = PassiveSecurityOutcomeReasonDto.None);
