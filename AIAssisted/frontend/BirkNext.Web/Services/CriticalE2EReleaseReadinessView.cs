using BirkNext.CriticalE2E;
using BirkNext.Governance;

namespace BirkNext.Web.Services;

/// <summary>
/// How Release Readiness shows the Critical E2E gate evaluation it recorded. Presentation only: the verdict, policy version and per-flow eligibility come
/// from the backend's gate evaluation (the same one the Critical E2E page shows) and are never recomputed here — a frozen report shows what it recorded.
/// </summary>
public static class CriticalE2EReleaseReadinessView
{
    /// <summary>data-state token: not-assessed | passed | blocked | incomplete | stale.</summary>
    public static string Token(CriticalE2EReadinessEvaluation? e) => e?.State switch
    {
        CriticalE2EReadinessState.Passed => "passed",
        CriticalE2EReadinessState.Blocked => "blocked",
        CriticalE2EReadinessState.Incomplete => "incomplete",
        CriticalE2EReadinessState.Stale => "stale",
        _ => "not-assessed",
    };

    public static string Label(CriticalE2EReadinessEvaluation? e) => e?.State switch
    {
        CriticalE2EReadinessState.Passed => "Evaluated · passed",
        CriticalE2EReadinessState.Blocked => "Evaluated · blocked",
        CriticalE2EReadinessState.Incomplete when e.Disposition == CriticalE2EReleaseDisposition.ReviewRequired => "Evaluated · review required",
        CriticalE2EReadinessState.Incomplete => "Evaluated · incomplete",
        CriticalE2EReadinessState.Stale => "Stale · other build or environment",
        _ => "Not assessed",
    };

    /// <summary>Chip tone shared with the dimension chips (ready / blocked / limited / notassessed).</summary>
    public static string Tone(CriticalE2EReadinessEvaluation? e) => e?.State switch
    {
        CriticalE2EReadinessState.Passed => "ready",
        CriticalE2EReadinessState.Blocked => "blocked",
        CriticalE2EReadinessState.Incomplete or CriticalE2EReadinessState.Stale => "limited",
        _ => "notassessed",
    };

    public static string Disposition(CriticalE2EReleaseDisposition? d) => d switch
    {
        CriticalE2EReleaseDisposition.Ready => "Ready",
        CriticalE2EReleaseDisposition.Blocked => "Blocked",
        CriticalE2EReleaseDisposition.Incomplete => "Incomplete",
        CriticalE2EReleaseDisposition.ReviewRequired => "Review required",
        CriticalE2EReleaseDisposition.NotEvaluated => "Not evaluated (no build)",
        CriticalE2EReleaseDisposition.NotConfigured => "Not configured",
        _ => "Not evaluated",
    };

    public static string Result(CriticalE2EReadinessFlow f) => f.ExecutionResult == CriticalE2EStatus.NotRun
        ? "No run on this build"
        : $"{f.ExecutionResult}{(f.RunId is { Length: > 0 } run ? $" · run {run}" : "")}";

    public static string BusinessE2E(CriticalE2EReadinessFlow f) => f.BusinessE2EContribution ? "Yes" : "No";

    public static string Blocker(CriticalE2EReadinessFlow f) => f.MandatoryReleaseBlocker
        ? $"Yes ({Lower((f.BlockerReason ?? "required flow").TrimEnd('.'))})"
        : "No";

    private static string Lower(string text) => text.Length == 0 ? text : char.ToLowerInvariant(text[0]) + text[1..];

    /// <summary>The drill-down to the Critical E2E page for the same environment and build.</summary>
    public static string Href(ReadinessDimension? d, string? scope, string? build) =>
        d?.Href is { } href && href.StartsWith("critical-e2e-regression", StringComparison.Ordinal) && href.Contains('?') ? href
        : $"critical-e2e-regression?environment={Uri.EscapeDataString(scope ?? "")}{(string.IsNullOrWhiteSpace(build) ? "" : $"&build={Uri.EscapeDataString(build.Trim())}")}";

    /// <summary>
    /// Why a frozen Critical E2E evaluation cannot be compared with another one as the same evidence: recorded under another gate policy version, or for
    /// another build or environment. Null when comparable. The frozen evaluation itself is shown as recorded either way.
    /// </summary>
    public static string? Incompatibility(CriticalE2EReadinessEvaluation? frozen, CriticalE2EReadinessEvaluation? other)
    {
        if (frozen is null || other is null) return null;
        if (!string.Equals(frozen.PolicyVersion, other.PolicyVersion, StringComparison.Ordinal))
            return $"Stale: the frozen Critical E2E verdict was recorded under {frozen.PolicyVersion}; the other assessment uses {other.PolicyVersion}. The frozen verdict is kept as recorded.";
        if (!string.Equals(frozen.BuildId?.Trim(), other.BuildId?.Trim(), StringComparison.OrdinalIgnoreCase))
            return $"Stale: the frozen Critical E2E verdict is for build {frozen.BuildId ?? "(none)"}; the other assessment is for build {other.BuildId ?? "(none)"}.";
        if (!string.Equals(frozen.EnvironmentId, other.EnvironmentId, StringComparison.OrdinalIgnoreCase))
            return $"Stale: the frozen Critical E2E verdict is for environment {frozen.EnvironmentId}; the other assessment is for {other.EnvironmentId}.";
        return null;
    }
}
