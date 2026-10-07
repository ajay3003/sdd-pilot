using System.Globalization;

namespace BirkNext.Web.Services;

/// <summary>
/// Where a review gate stands for the artifact revisions it reads now. Availability, review and approval are separate facts:
/// an available artifact is only <see cref="ReadyToReview"/>, a reviewed one is not approved, and a decision on an earlier
/// revision is <see cref="Stale"/>, never current.
/// </summary>
public enum ArtifactReviewState
{
    /// <summary>A role the gate requires is absent: it does not apply, is not counted and is not recommended.</summary>
    NotApplicable,
    /// <summary>Applies, but cannot be decided yet: no single artifact is chosen for a role, or an earlier gate is not approved.</summary>
    Blocked,
    ReadyToReview,
    /// <summary>Opened, nothing decided.</summary>
    InReview,
    /// <summary>Inspected by a person; no approval decision.</summary>
    Reviewed,
    Approved,
    NeedsChanges,
    /// <summary>Decided on an earlier revision of the same artifacts; nothing decided on the current one.</summary>
    Stale,
}

/// <summary>One gate's review status in words: a short label, a status line, and the revisions a decision applies to.</summary>
public sealed record ArtifactReviewStatus(ArtifactReviewState State, string Label, string Summary, string? Revisions, string Icon)
{
    /// <summary>A decision exists on the current revisions.</summary>
    public bool IsDecided => State is ArtifactReviewState.Reviewed or ArtifactReviewState.Approved or ArtifactReviewState.NeedsChanges;

    /// <summary>The gate applies to this project (it is counted in review totals).</summary>
    public bool Applies => State != ArtifactReviewState.NotApplicable;

    /// <summary>A review decision can be recorded now.</summary>
    public bool CanDecide => State is not (ArtifactReviewState.NotApplicable or ArtifactReviewState.Blocked);
}

/// <summary>
/// The one reading of a workflow step's review state, shared by Recommended Workflow (step badges, status lines, manual review
/// totals) and the Dashboard (artifact cards, Governance), so both always agree for the same artifact revision.
/// </summary>
public static class ArtifactReviewPresentation
{
    public static ArtifactReviewStatus Of(WorkflowStepViewModel step)
    {
        var revisions = string.IsNullOrWhiteSpace(step.ArtifactReferences) ? null : step.ArtifactReferences;
        return step.Status switch
        {
            WorkflowStepStatus.NotApplicable => new(ArtifactReviewState.NotApplicable, "N/A",
                $"Not applicable · {Reason(step, "a required artifact is absent")}", null, "–"),
            WorkflowStepStatus.Locked => new(ArtifactReviewState.Blocked, "Blocked", Reason(step, "Complete the previous step first"), revisions, "🔒"),
            WorkflowStepStatus.Approved => new(ArtifactReviewState.Approved, "Approved",
                $"Approved{On(step.DecidedAt)} · current revision", revisions, "✓"),
            WorkflowStepStatus.NeedsAttention => new(ArtifactReviewState.NeedsChanges, "Needs changes",
                $"Needs changes{On(step.DecidedAt)} · current revision", revisions, "!"),
            WorkflowStepStatus.Stale => new(ArtifactReviewState.Stale, "Review stale", StaleSummary(step), revisions, "⟳"),
            WorkflowStepStatus.Reviewed => new(ArtifactReviewState.Reviewed, "Reviewed",
                $"Reviewed{On(step.DecidedAt)}{(step.RequiresApproval ? " · awaiting approval" : "")}", revisions, "◐"),
            WorkflowStepStatus.InProgress => new(ArtifactReviewState.InReview, "In review", "Opened · Not reviewed", revisions, "○"),
            _ => new(ArtifactReviewState.ReadyToReview, "Ready to review", "Available · Not reviewed", revisions, "○"),
        };
    }

    private static string StaleSummary(WorkflowStepViewModel step)
    {
        var previous = (step.PreviousDecision ?? "Reviewed").ToLowerInvariant();
        return step.PreviousArtifactReferences is null
            ? $"Review stale · the earlier decision ({previous}{On(step.PreviousDecisionAt)}) names no artifact revision"
            : $"Review stale · the artifact changed since it was {previous}{On(step.PreviousDecisionAt)}";
    }

    private static string Reason(WorkflowStepViewModel step, string fallback) =>
        string.IsNullOrWhiteSpace(step.DisabledReason) ? fallback : step.DisabledReason;

    private static string On(DateTimeOffset? at) =>
        at is { } value ? " on " + value.ToLocalTime().ToString("d MMM yyyy", CultureInfo.InvariantCulture) : "";
}
