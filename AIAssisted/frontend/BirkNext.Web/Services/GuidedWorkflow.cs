namespace BirkNext.Web.Services;

/// <summary>Domain-neutral state of one guided-workflow step (the Authentication tab keeps its own <see cref="AuthWorkflowStepState"/>).</summary>
public enum GuidedStepState { Done, Current, Waiting, Blocked, NotApplicable }

/// <summary>
/// One step of a guided page workflow: what it is, its state in words, one sentence, the evidence it rests on, and at most one action — an
/// in-page action id (raised to the host) or a link to the page that owns the fix. Pages derive their steps from their own authoritative state;
/// this record holds no business rules.
/// </summary>
/// <param name="StateKey">Optional page-specific state word written to <c>data-state</c> instead of <see cref="State"/>, for a page whose own
/// vocabulary is finer than the five shared states. The shared state still drives styling and the single current step.</param>
/// <param name="Status">Optional semantic status for the badge instead of the one derived from <see cref="State"/>.</param>
public sealed record GuidedStep(string Id, string Title, GuidedStepState State, string StatusLabel, string Explanation,
    string? Evidence = null, string? ActionLabel = null, string? ActionId = null, string? Href = null, string? DisabledReason = null,
    string? StateKey = null, SemanticStatus? Status = null);

public static class GuidedWorkflow
{
    /// <summary>The single next step: the first current step (with or without an action).</summary>
    public static GuidedStep? Next(IReadOnlyList<GuidedStep> steps) => steps.FirstOrDefault(s => s.State == GuidedStepState.Current);

    public static SemanticStatus Status(GuidedStepState state) => state switch
    {
        GuidedStepState.Done => SemanticStatus.Completed,
        GuidedStepState.Current => SemanticStatus.NeedsReview,
        GuidedStepState.Blocked => SemanticStatus.Blocked,
        GuidedStepState.NotApplicable => SemanticStatus.NotApplicable,
        _ => SemanticStatus.NotRun,
    };
}
