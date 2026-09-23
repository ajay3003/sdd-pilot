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

/// <summary>
/// One thing to DO, and everything this review found that asks for it.
/// </summary>
/// <param name="Priority">The worst severity among the issues this theme fixes. Work size never raises priority.</param>
public sealed record FrontendQualityRecommendationTheme(
    string Key,
    string Title,
    FrontendQualitySeverity Priority,
    FrontendQualityCategory PrimaryCategory,
    IReadOnlyList<FrontendQualityLogicalIssue> Issues,
    IReadOnlyList<string> AffectedPages,
    int SourceFindingCount)
{
    /// <summary>"2 logical issues · 4 affected pages · 8 source findings" — only the parts that say something.</summary>
    public string ScaleLabel => string.Join(" · ", new[]
    {
        Issues.Count > 1 ? $"{Issues.Count} logical issues" : "1 logical issue",
        AffectedPages.Count > 1 ? $"{AffectedPages.Count} affected pages" : null,
        $"{SourceFindingCount} source finding{(SourceFindingCount == 1 ? "" : "s")}",
    }.Where(part => part is not null));

    public string PriorityLabel => Priority switch
    {
        FrontendQualitySeverity.Critical or FrontendQualitySeverity.High => "High priority",
        FrontendQualitySeverity.Medium => "Medium priority",
        _ => "Low priority",
    };
}

/// <summary>
/// How much of the intended review actually happened, on the dimensions that can differ. A single "Assessment
/// completeness: Full" was read off required coverage alone, so a review with three optional engines that never ran and
/// a manual accessibility assessment still outstanding described itself as complete.
/// </summary>
public enum FrontendQualityOptionalCoverageState
{
    /// <summary>No optional engine was active in this review; there is nothing missing.</summary>
    NotApplicable,
    /// <summary>Every active optional engine assessed the target.</summary>
    Complete,
    /// <summary>Some active optional engines assessed the target and some did not.</summary>
    Partial,
    /// <summary>No active optional engine assessed the target.</summary>
    None,
}

public enum FrontendQualityManualAssessmentState
{
    /// <summary>Nothing in this review needs a person.</summary>
    NotRequired,
    /// <summary>Work remains that no engine can do.</summary>
    Required,
}

/// <summary>
/// The four independent answers to "how complete is this?". They are never merged: a review can execute perfectly,
/// cover everything required, be missing optional depth, and still owe a manual assessment.
/// </summary>
public sealed record FrontendQualityCompleteness(
    FrontendQualityResultState Execution,
    FrontendQualityRequiredCoverageState RequiredCoverage,
    FrontendQualityOptionalCoverageState OptionalCoverage,
    FrontendQualityManualAssessmentState ManualAssessment,
    int RequiredAssessed,
    int RequiredTotal,
    int OptionalAssessed,
    int OptionalTotal)
{
    public string ExecutionLabel => FrontendQualityResultStates.IsCompleted(Execution) ? "Completed" : FrontendQualityResultStates.Label(Execution);

    public string RequiredCoverageLabel => RequiredCoverage switch
    {
        FrontendQualityRequiredCoverageState.AllRequiredAssessed => "Complete",
        FrontendQualityRequiredCoverageState.SomeRequiredNotAssessed => "Incomplete",
        _ => "None",
    };

    public string OptionalCoverageLabel => OptionalCoverage switch
    {
        FrontendQualityOptionalCoverageState.NotApplicable => "No optional engine active",
        FrontendQualityOptionalCoverageState.Complete => "Complete",
        FrontendQualityOptionalCoverageState.Partial => "Partial",
        _ => "None",
    };

    public string ManualAssessmentLabel => ManualAssessment == FrontendQualityManualAssessmentState.Required ? "Required" : "Not required";

    /// <summary>True only when every dimension is genuinely finished — the one case a single "Full" would have been honest.</summary>
    public bool IsFullyComplete =>
        FrontendQualityResultStates.IsCompleted(Execution) &&
        RequiredCoverage == FrontendQualityRequiredCoverageState.AllRequiredAssessed &&
        OptionalCoverage is FrontendQualityOptionalCoverageState.Complete or FrontendQualityOptionalCoverageState.NotApplicable &&
        ManualAssessment == FrontendQualityManualAssessmentState.NotRequired;
}

/// <param name="FindingCount">
/// Null when nothing assessed this domain. A domain with no evidence has an UNKNOWN number of issues, not zero.
/// </param>
/// <param name="LogicalIssueCount">
/// Distinct problems in this domain, after grouping. Null when nothing assessed it. Shown beside the source count
/// because they answer different questions: how many things to fix, and how much evidence there is for them.
/// </param>
/// <param name="Derived">
/// This domain reports conclusions drawn from other domains' evidence rather than observations of its own. Its items
/// are indicators, not new findings, and they never enter the source-finding total.
/// </param>
public sealed record FrontendQualityDomainResult(
    FrontendQualityCategory Category,
    string Title,
    FrontendQualityDomainResultState State,
    string Summary,
    string? Limitation,
    int? FindingCount,
    int? LogicalIssueCount = null,
    bool Derived = false,
    bool ManualAssessmentRequired = false,
    int DerivedIndicatorCount = 0,
    int ContributedToOtherDomains = 0,
    IReadOnlyList<string>? ContributedDomainLabels = null)
{
    /// <summary>The state label, or "Derived" for a domain that draws conclusions rather than making observations.</summary>
    public string StateLabel => Derived ? "Derived" : FrontendQualityDomainResultStates.Label(State);

    /// <summary>"3 logical issues · 30 source findings", or "3 indicators" for a derived domain.</summary>
    public string? CountLabel => FindingCount is not { } findings ? null
        : Derived ? $"{findings} indicator{(findings == 1 ? "" : "s")}"
        : (LogicalIssueCount is { } issues
            ? $"{issues} logical issue{(issues == 1 ? "" : "s")} · {findings} source finding{(findings == 1 ? "" : "s")}"
            : $"{findings} source finding{(findings == 1 ? "" : "s")}")
          + (DerivedIndicatorCount > 0 ? $" · {DerivedIndicatorCount} derived indicator{(DerivedIndicatorCount == 1 ? "" : "s")}" : "");

    /// <summary>
    /// Why a domain can show source findings and few or no logical issues: its findings were grouped into issues owned by
    /// another primary domain. Only from the grouping; null when nothing was contributed.
    /// </summary>
    public string? ContributionNote => ContributedToOtherDomains <= 0 ? null
        : $"{ContributedToOtherDomains} source finding{(ContributedToOtherDomains == 1 ? "" : "s")} contributed to logical issues grouped under "
          + $"{string.Join(", ", ContributedDomainLabels ?? [])}, where {(ContributedToOtherDomains == 1 ? "it is" : "they are")} counted once.";
}

/// <summary>
/// The accessibility result's criteria counts.
///
/// The primary summary is built on ONE partition of the profile: every criterion either has execution evidence or does
/// not, and those two numbers add up to the profile. Everything else — how many carry failure evidence, how many need a
/// person — is a property of criteria inside that partition, so it overlaps on purpose and is labelled as such.
///
/// The old summary put five numbers side by side with no stated relationship ("5 Failed", "5 Require manual review",
/// "10 of 48 Manual assessment required", "38 Not yet assessed", "35 Criteria with evidence"), two of which were near
/// synonyms and two of which appeared to contradict each other. Nothing is removed here; the overlapping states move
/// into the criteria view, where each criterion shows its own.
/// </summary>
/// <param name="WithFailureEvidence">Criteria whose assessment recorded an actual FAILURE. Never derived from a source flag alone.</param>
/// <param name="RequireManualAssessment">
/// Criteria a person still has to assess: the manual-only ones plus any the assessment explicitly marked review-required.
/// A union, so a criterion that is both is counted once.
/// </param>
/// <param name="ExplicitReviewRequired">The subset whose assessment carries an explicit review-required result. Detail, never a peer of the above.</param>
public sealed record FrontendQualityAccessibilityResult(
    string ProfileLabel,
    int CriteriaInScope,
    int Failed,
    int RequireManualReview,
    int ManualOnly,
    int NotAssessed,
    int WithEvidence,
    int RequireManualAssessment = 0,
    int ExplicitReviewRequired = 0)
{
    /// <summary>Criteria whose assessment recorded a failure.</summary>
    public int WithFailureEvidence => Failed;

    /// <summary>
    /// The other half of the partition. Derived by subtraction so the two can never disagree with the profile size,
    /// and deliberately NOT "not yet assessed": a criterion can have execution evidence without a final determination,
    /// and calling that "not assessed" contradicted the evidence count next to it.
    /// </summary>
    public int WithoutExecutionEvidence => Math.Max(0, CriteriaInScope - WithEvidence);

    /// <summary>
    /// Criteria with execution evidence but no completed determination — the honest reading of the old "not yet
    /// assessed" number, which counted these among criteria that had in fact been executed against.
    /// </summary>
    public int EvidencedWithoutDetermination => Math.Max(0, NotAssessed - WithoutExecutionEvidence);

    /// <summary>
    /// The one sentence the accessibility result leads with. Zero automated failures is stated as exactly that — an
    /// absence of detected violations in the evidence available — and never as conformance.
    /// </summary>
    public string Statement => Failed == 0
        ? "No automated WCAG violations were detected in the available evidence. Manual assessment is still required."
        // Automated failure EVIDENCE, not "failed": partial automation does not establish a criterion-level outcome.
        : $"{Failed} {(Failed == 1 ? "criterion has" : "criteria have")} automated failure evidence. Manual assessment is still required.";

    /// <summary>Said wherever the counts are. An absence of detected violations establishes nothing about conformance.</summary>
    public const string ConformanceCaveat =
        "No automated failure detected does not establish WCAG conformance.";
}

/// <param name="ProfileDrift">
/// Set when the accessibility profile currently selected is not the one this result was produced under. The result keeps
/// the profile it ran with; this line asks for a new run rather than relabelling the old one.
/// </param>
/// <param name="SourceFindingCount">
/// Individual engine observations. What the review SAW — never how many problems there are, because one problem can be
/// seen many times.
/// </param>
/// <param name="LogicalIssueCount">Distinct actionable problems after grouping. What there is to fix.</param>
/// <param name="CriticalHighSourceCount">Critical/high source observations, kept as an observation count and labelled as one.</param>
/// <param name="DerivedIndicatorCount">Conclusions drawn from the observations above; never added to them.</param>
/// <param name="InformationalIssueCount">Observations that ask for nothing. Kept out of the actionable count, kept in the result.</param>
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
    string? ProfileDrift = null,
    FrontendQualityCompleteness? Completeness = null,
    FrontendQualityReleaseDecision? Release = null,
    int SourceFindingCount = 0,
    int LogicalIssueCount = 0,
    int CriticalHighSourceCount = 0,
    int DerivedIndicatorCount = 0,
    int InformationalIssueCount = 0)
{
    public string StateLabel => FrontendQualityResultStates.Label(State);
    public FrontendQualityDomainResult Domain(FrontendQualityCategory category) => Domains.Single(d => d.Category == category);
}

/// <summary>
/// The release decision, stated where the decision is made rather than at the bottom of the technical details. It is
/// never an approval: the strongest thing this review can say is that its automated evidence found nothing blocking,
/// which is not the same as "ready to release".
/// </summary>
/// <param name="Reasons">Why, in the reader's terms — built from logical issues, not from source occurrence counts.</param>
public sealed record FrontendQualityReleaseDecision(
    FrontendQualityReleaseDisposition Disposition,
    string Label,
    string Statement,
    IReadOnlyList<string> Reasons)
{
    public string Tone => Disposition switch
    {
        FrontendQualityReleaseDisposition.Blocked => "attention",
        FrontendQualityReleaseDisposition.ReviewRequired => "warning",
        _ => "ready",
    };
}
