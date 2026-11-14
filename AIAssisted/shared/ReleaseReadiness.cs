using System.Text.Json.Serialization;
using BirkNext.CriticalE2E;

namespace BirkNext.Governance;

// ── Release readiness (rule set release/1) ────────────────────────────────────────────────────────────────────────────────
// A test lead's answer for one release scope: Ready / Ready with limitations / Blocked / Not assessed, derived from separate dimensions (each Required,
// Optional or Not applicable) — never a score, never a task-checklist percentage. A dimension BirkNext has no evidence for says Not assessed and links to
// the page that owns it; nothing is invented. A frozen report keeps its scope, evidence references and versions; a sign-off is recorded next to it and
// never changes it.

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DimensionApplicability { Required, Optional, NotApplicable }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ReadinessDimensionKey
{
    ScopeClarity, Requirements, Traceability, Implementation, TestDesign, Governance, OpenDecisions,
    FunctionalExecution, ApiReview, IntegrationReview, FrontendReview, Performance, CriticalE2E,
}

public sealed record ReadinessDimension(
    ReadinessDimensionKey Key, string Title, DimensionApplicability Applicability, GateDecision State, string Summary, IReadOnlyList<string> Evidence,
    string? NextAction, string? Href)
{
    /// <summary>
    /// Critical E2E dimension only: the authoritative, versioned Critical E2E gate evaluation it was read from (policy, disposition, build, environment,
    /// per-flow eligibility). Null for every other dimension, and in reports frozen before it was recorded. A frozen report keeps it exactly as recorded.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public CriticalE2EReadinessEvaluation? CriticalE2E { get; init; }
}

/// <summary>
/// What the release is assessed against beyond its project scope: the build being released and the environment's name and type (the same context the
/// Critical E2E page sends). Without a build, build-bound release evidence (Critical E2E) is Not assessed — never Passed.
/// </summary>
public sealed record ReleaseEvaluationContext(string? BuildId, string? EnvironmentName = null, string? EnvironmentType = null);

/// <summary>How the Critical E2E gate evaluation reads in release readiness. Stale = evaluated for another build or environment than the readiness.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CriticalE2EReadinessState { NotAssessed, Passed, Blocked, Incomplete, Stale }

/// <summary>One required Critical E2E flow as the gate evaluated it for the readiness's build: classification, readiness, execution evidence, eligibility.</summary>
public sealed record CriticalE2EReadinessFlow
{
    public string FlowId { get; init; } = "";
    public string Name { get; init; } = "";
    public string Module { get; init; } = "";
    public CriticalE2ETestType TestType { get; init; } = CriticalE2ETestType.Unknown;
    /// <summary>Criticality (Critical / Diagnostic).</summary>
    public CriticalE2EFlowKind Criticality { get; init; } = CriticalE2EFlowKind.Critical;
    public bool RequiredForRelease { get; init; }
    /// <summary>Whether the definition is release-ready, and if not, its first blocker.</summary>
    public bool ReleaseReady { get; init; }
    public string Readiness { get; init; } = "";
    /// <summary>The result of the run that is release evidence on the evaluated build; NotRun when there is none (a run on another build is not evidence).</summary>
    public CriticalE2EStatus ExecutionResult { get; init; } = CriticalE2EStatus.NotRun;
    /// <summary>The evidence run (only when it is release evidence on the evaluated build).</summary>
    public string? RunId { get; init; }
    public DateTimeOffset? RunAt { get; init; }
    public CriticalE2EGateEligibilityKind Eligibility { get; init; } = CriticalE2EGateEligibilityKind.NotExecuted;
    public string EligibilityReason { get; init; } = "";
    /// <summary>Can this flow contribute Business E2E coverage under the evaluating policy (UI smoke: never).</summary>
    public bool BusinessE2EContribution { get; init; }
    /// <summary>The flow blocks the release gate on this build (for example a failed required flow of any test type).</summary>
    public bool MandatoryReleaseBlocker { get; init; }
    public string? BlockerReason { get; init; }
}

/// <summary>
/// The Critical E2E gate evaluation recorded in release readiness: produced by the same evaluation as the Critical E2E page (never a second gate engine),
/// for the readiness's environment and build, with its provenance. A frozen report keeps it unchanged; nothing re-evaluates it.
/// </summary>
public sealed record CriticalE2EReadinessEvaluation
{
    public CriticalE2EReadinessState State { get; init; } = CriticalE2EReadinessState.NotAssessed;
    /// <summary>The gate policy that produced the verdict (critical-e2e-gate/1 for builds whose evidence predates versioned policies).</summary>
    public string PolicyVersion { get; init; } = "";
    public CriticalE2EReleaseDisposition Disposition { get; init; } = CriticalE2EReleaseDisposition.NotEvaluated;
    /// <summary>What the gate evaluated.</summary>
    public string EnvironmentId { get; init; } = "";
    public string? BuildId { get; init; }
    public string? BuildSource { get; init; }
    /// <summary>What the readiness asked for. Different from the evaluated environment or build = Stale (never Passed).</summary>
    public string ReadinessEnvironmentId { get; init; } = "";
    public string? ReadinessBuildId { get; init; }
    public DateTimeOffset EvaluatedAt { get; init; }
    /// <summary>The gate's own summary sentence.</summary>
    public string GateSummary { get; init; } = "";
    public string? StaleReason { get; init; }
    public int RequiredFlows { get; init; }
    public int BlockingFlows { get; init; }
    public int BusinessE2EFlowsCounted { get; init; }
    public List<CriticalE2EReadinessFlow> Flows { get; init; } = [];
}

/// <summary>What the assessment covers: a project (canonical scope) and, optionally, its modules. References only — never copied entities.</summary>
public sealed record ReleaseScopeReference(string Scope, string Name, IReadOnlyList<string> Modules, Guid? InterpretationId);

public sealed record ReleaseReadinessAssessment(
    ReleaseScopeReference ScopeReference, GateDecision Decision, string Reason, IReadOnlyList<ReadinessDimension> Dimensions, IReadOnlyList<string> Blockers,
    IReadOnlyList<string> NextActions, string RuleSetVersion, string PolicySetVersion, DateTimeOffset EvaluatedAt)
{
    /// <summary>The modules of the interpreted project that have feature packs (declared names), for choosing a scope.</summary>
    public IReadOnlyList<string> AvailableModules { get; init; } = [];

    /// <summary>The counts behind the dimensions for the scope. Null when nothing is analyzed (and in reports frozen before release/1 recorded them):
    /// an unknown count is never shown as 0.</summary>
    public ReleaseReadinessFacts? Facts { get; init; }

    /// <summary>The build the assessment was made for (null = none selected). Recorded so a frozen report says which build its execution evidence was about.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? BuildId { get; init; }
}

/// <summary>
/// The scope's counts as evaluated: requirements, how many have a confirmed (explicit, derived or human-confirmed) VERIFIES test, how many only a
/// suggested one (never counted), tasks and those that name existing code, governance findings and open review decisions. Test execution is not a
/// count here: it is owned by the execution evidence, never inferred from a link.
/// </summary>
public sealed record ReleaseReadinessFacts(int FeaturePacks, int Requirements, int RequirementsWithConfirmedTest, int RequirementsWithSuggestedTestOnly,
    int RequirementsWithoutTest, int Tasks, int TasksWithCode, int BlockingFindings, int Warnings, int OpenDecisions);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SignOffOutcome { Accepted, AcceptedWithRisk, Rejected }

/// <summary>A human decision about a frozen report. It records who decided what on which report; the report's evidence and decision stay as computed.</summary>
public sealed record ReleaseSignOff(SignOffOutcome Outcome, string By, DateTimeOffset At, string? Note);

public sealed record ReleaseReadinessReport(Guid Id, string Name, DateTimeOffset FrozenAt, ReleaseReadinessAssessment Assessment, IReadOnlyList<ReleaseSignOff> SignOffs);

public sealed record ReleaseReadinessReportSummary(Guid Id, string Name, DateTimeOffset FrozenAt, GateDecision Decision, IReadOnlyList<string> Modules, Guid? InterpretationId,
    SignOffOutcome? LatestSignOff);

/// <param name="BuildId">The build being released (optional): build-bound evidence such as Critical E2E is evaluated for it and frozen with it.</param>
public sealed record FreezeReleaseReadinessRequest(string Name, IReadOnlyList<string>? Modules, string? BuildId = null, string? EnvironmentName = null, string? EnvironmentType = null);

public sealed record ReleaseSignOffRequest(SignOffOutcome Outcome, string? Note);

public static class ReleaseReadinessRules
{
    public const string RuleSetVersion = "release/1";

    /// <summary>
    /// The release decision from its dimensions: a Required dimension that is Blocked blocks; a Required dimension Not assessed leaves the release Not
    /// assessed (never Ready); otherwise any limitation — a Required dimension with limitations or an exception, an Optional dimension that is blocked,
    /// limited or not assessed — gives Ready with limitations. Not applicable never counts. No required dimension at all is Not assessed.
    /// </summary>
    public static (GateDecision Decision, string Reason) Decide(IReadOnlyList<ReadinessDimension> dimensions)
    {
        var applicable = dimensions.Where(d => d.Applicability != DimensionApplicability.NotApplicable).ToList();
        var required = applicable.Where(d => d.Applicability == DimensionApplicability.Required).ToList();
        // No vacuous truth: without a single required dimension nothing was assessed, so the release is Not assessed — never Ready.
        if (required.Count == 0) return (GateDecision.NotAssessed, "No required dimension was assessed.");
        var blocked = required.Where(d => d.State == GateDecision.Blocked).ToList();
        if (blocked.Count > 0) return (GateDecision.Blocked, $"Blocked by {Names(blocked)}.");
        var unassessed = required.Where(d => d.State == GateDecision.NotAssessed).ToList();
        if (unassessed.Count > 0) return (GateDecision.NotAssessed, $"Required evidence not assessed: {Names(unassessed)}.");
        var limited = applicable.Where(d => d.State != GateDecision.Ready).ToList();
        if (limited.Count > 0) return (GateDecision.ReadyWithLimitations, $"Limitations in {Names(limited)}.");
        return (GateDecision.Ready, "Every applicable dimension is ready.");
    }

    private static string Names(IEnumerable<ReadinessDimension> dims) => string.Join(", ", dims.Select(d => d.Title.ToLowerInvariant()));

    public static string ApplicabilityLabel(DimensionApplicability a) => a == DimensionApplicability.NotApplicable ? "Not applicable" : a.ToString();
}
