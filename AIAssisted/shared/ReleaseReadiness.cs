using System.Text.Json.Serialization;

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
    string? NextAction, string? Href);

/// <summary>What the assessment covers: a project (canonical scope) and, optionally, its modules. References only — never copied entities.</summary>
public sealed record ReleaseScopeReference(string Scope, string Name, IReadOnlyList<string> Modules, Guid? InterpretationId);

public sealed record ReleaseReadinessAssessment(
    ReleaseScopeReference ScopeReference, GateDecision Decision, string Reason, IReadOnlyList<ReadinessDimension> Dimensions, IReadOnlyList<string> Blockers,
    IReadOnlyList<string> NextActions, string RuleSetVersion, string PolicySetVersion, DateTimeOffset EvaluatedAt)
{
    /// <summary>The modules of the interpreted project that have feature packs (declared names), for choosing a scope.</summary>
    public IReadOnlyList<string> AvailableModules { get; init; } = [];
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SignOffOutcome { Accepted, AcceptedWithRisk, Rejected }

/// <summary>A human decision about a frozen report. It records who decided what on which report; the report's evidence and decision stay as computed.</summary>
public sealed record ReleaseSignOff(SignOffOutcome Outcome, string By, DateTimeOffset At, string? Note);

public sealed record ReleaseReadinessReport(Guid Id, string Name, DateTimeOffset FrozenAt, ReleaseReadinessAssessment Assessment, IReadOnlyList<ReleaseSignOff> SignOffs);

public sealed record ReleaseReadinessReportSummary(Guid Id, string Name, DateTimeOffset FrozenAt, GateDecision Decision, IReadOnlyList<string> Modules, Guid? InterpretationId,
    SignOffOutcome? LatestSignOff);

public sealed record FreezeReleaseReadinessRequest(string Name, IReadOnlyList<string>? Modules);

public sealed record ReleaseSignOffRequest(SignOffOutcome Outcome, string? Note);

public static class ReleaseReadinessRules
{
    public const string RuleSetVersion = "release/1";

    /// <summary>
    /// The release decision from its dimensions: a Required dimension that is Blocked blocks; a Required dimension Not assessed leaves the release Not
    /// assessed (never Ready); otherwise any limitation — a Required dimension with limitations or an exception, an Optional dimension that is blocked,
    /// limited or not assessed — gives Ready with limitations. Not applicable never counts.
    /// </summary>
    public static (GateDecision Decision, string Reason) Decide(IReadOnlyList<ReadinessDimension> dimensions)
    {
        var applicable = dimensions.Where(d => d.Applicability != DimensionApplicability.NotApplicable).ToList();
        var required = applicable.Where(d => d.Applicability == DimensionApplicability.Required).ToList();
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
