namespace BirkNext.Web.Models;

/// <summary>
/// Presentation records for the Frontend Quality Review RESULT view. They project a completed
/// <see cref="FrontendQualityReviewReport"/> — nothing here recomputes a review, invents a verdict the report does not
/// carry, or reads live configuration. A result belongs to the run that produced it.
/// </summary>
public enum FrontendQualityResultState
{
    /// <summary>The review could not execute: no required engine produced a trustworthy assessment, or it errored outright.</summary>
    FailedToRun,
    /// <summary>The target could not be reached or required authentication that was not available, so there is nothing to judge.</summary>
    Blocked,
    /// <summary>The review ran, but some required coverage is missing, so the picture is incomplete.</summary>
    Incomplete,
    /// <summary>The review completed and explicitly leaves criteria or items for a human to assess.</summary>
    CompletedWithManualReview,
    /// <summary>The review completed, but at least one active evidence source did not contribute.</summary>
    CompletedWithLimitations,
    /// <summary>The review completed with every active evidence source contributing.</summary>
    Completed,
}

public static class FrontendQualityResultStates
{
    /// <summary>
    /// Result-level wording. Deliberately absent: "Passed", "Compliant", "Secure", "Healthy" — the report model carries
    /// no such claim, and a review that found nothing has not proven anything.
    /// </summary>
    public static string Label(FrontendQualityResultState state) => state switch
    {
        FrontendQualityResultState.FailedToRun => "Review failed to run",
        FrontendQualityResultState.Blocked => "Review blocked",
        FrontendQualityResultState.Incomplete => "Incomplete",
        FrontendQualityResultState.CompletedWithManualReview => "Completed with manual review required",
        FrontendQualityResultState.CompletedWithLimitations => "Completed with limitations",
        _ => "Completed",
    };

    /// <summary>Visual tone only; the label always carries the meaning.</summary>
    public static string Tone(FrontendQualityResultState state) => state switch
    {
        FrontendQualityResultState.Completed => "ready",
        FrontendQualityResultState.FailedToRun or FrontendQualityResultState.Blocked => "attention",
        _ => "warning",
    };

    /// <summary>The review produced judgeable output. False means the page is reporting an execution problem, not quality.</summary>
    public static bool IsCompleted(FrontendQualityResultState state) =>
        state is FrontendQualityResultState.Completed
            or FrontendQualityResultState.CompletedWithLimitations
            or FrontendQualityResultState.CompletedWithManualReview;
}

/// <summary>
/// Result state of ONE review domain. Separate from <see cref="FrontendQualityDimensionState"/>, which is pre-run scope:
/// before a run a domain is "Included", after a run it has either produced evidence or explained why it did not.
/// </summary>
public enum FrontendQualityDomainResultState
{
    /// <summary>Every active evidence source for this domain contributed.</summary>
    Completed,
    /// <summary>The domain was reviewed, but at least one active evidence source did not contribute.</summary>
    CompletedWithLimitedEvidence,
    /// <summary>The domain was in scope but nothing produced evidence for it. Never the same as "no issues".</summary>
    NoEvidence,
    /// <summary>The domain had no active evidence source in this run.</summary>
    NotAssessed,
    /// <summary>An evidence source for this domain errored, timed out or was cancelled — an execution problem, not a finding.</summary>
    FailedToRun,
}

public static class FrontendQualityDomainResultStates
{
    public static string Label(FrontendQualityDomainResultState state) => state switch
    {
        FrontendQualityDomainResultState.Completed => "Completed",
        FrontendQualityDomainResultState.CompletedWithLimitedEvidence => "Completed with limited evidence",
        FrontendQualityDomainResultState.NoEvidence => "No evidence",
        FrontendQualityDomainResultState.NotAssessed => "Not assessed",
        _ => "Failed to run",
    };

    public static string Tone(FrontendQualityDomainResultState state) => state switch
    {
        FrontendQualityDomainResultState.Completed => "ready",
        FrontendQualityDomainResultState.NotAssessed => "muted",
        FrontendQualityDomainResultState.FailedToRun => "attention",
        _ => "warning",
    };

    /// <summary>A finding count may only be shown when a review actually ran for this domain.</summary>
    public static bool CarriesFindings(FrontendQualityDomainResultState state) =>
        state is FrontendQualityDomainResultState.Completed or FrontendQualityDomainResultState.CompletedWithLimitedEvidence;
}

/// <param name="FindingCount">
/// Null when nothing assessed this domain. A domain with no evidence has an UNKNOWN number of issues, not zero.
/// </param>
public sealed record FrontendQualityDomainResult(
    FrontendQualityCategory Category,
    string Title,
    FrontendQualityDomainResultState State,
    string Summary,
    string? Limitation,
    int? FindingCount);

/// <summary>
/// The accessibility result's own counts, each naming a distinct WCAG status. They are criteria counts — never mixed with
/// finding counts or engine counts — and "not assessed" is never folded into either a pass or a failure.
/// </summary>
public sealed record FrontendQualityAccessibilityResult(
    string ProfileLabel,
    int CriteriaInScope,
    int Failed,
    int RequireManualReview,
    int ManualOnly,
    int NotAssessed,
    int WithEvidence)
{
    /// <summary>
    /// The one sentence the accessibility result leads with. Zero automated failures is stated as exactly that — an
    /// absence of detected violations in the evidence available — and never as conformance.
    /// </summary>
    public string Statement => Failed == 0
        ? "No automated WCAG violations were detected in the available evidence. Manual review is still required."
        : $"{Failed} of {CriteriaInScope} criteria in scope are recorded as failed. Manual review is still required.";
}

/// <param name="ProfileDrift">
/// Set when the accessibility profile currently selected is not the one this result was produced under. The result keeps
/// the profile it ran with; this line asks for a new run rather than relabelling the old one.
/// </param>
public sealed record FrontendQualityResultView(
    FrontendQualityResultState State,
    string Summary,
    string Environment,
    string EnvironmentType,
    string Url,
    DateTime? CompletedAt,
    string? ProfileLabel,
    int? CriteriaInScope,
    IReadOnlyList<FrontendQualityDomainResult> Domains,
    FrontendQualityAccessibilityResult? Accessibility,
    string? ProfileDrift = null)
{
    public string StateLabel => FrontendQualityResultStates.Label(State);
    public FrontendQualityDomainResult Domain(FrontendQualityCategory category) => Domains.Single(d => d.Category == category);
}
