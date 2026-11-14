using BirkNext.TaskAlignment;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>
/// Maps a Task Alignment report to the persisted run shape and back. The task's full line (<see cref="TaskFinding.TaskText"/>) is not stored:
/// together the lines would copy the Tasks document body. Everything the analysis concluded per task is kept.
/// </summary>
public static class TaskAlignmentRunMapper
{
    public static TaskAlignmentRunCounts Counts(AlignmentReport report) => new()
    {
        TotalTasks = report.TotalTasks,
        LinkedTasks = report.LinkedTasks,
        TechnicalOnlyTasks = report.TechnicalOnlyTasks,
        DesignLinkedTasks = report.DesignLinkedTasks,
        TestImplementationTasks = report.TestImplementationTasks,
        NeedsReviewTasks = report.NeedsReviewTasks,
        PossibleDeviations = report.PossibleDeviations,
        HighImpactTasks = report.HighImpactTasks,
        MediumImpactTasks = report.MediumImpactTasks,
        LowImpactTasks = report.LowImpactTasks,
        UnknownImpactTasks = report.UnknownImpactTasks,
        RegressionCandidates = report.RegressionCandidates,
    };

    public static TaskAlignmentRunTask ToRunTask(TaskFinding f) => new()
    {
        TaskId = f.TaskId,
        Title = f.Title,
        Status = f.Status.ToString(),
        Risk = f.Risk.ToString(),
        Purpose = f.Purpose.ToString(),
        Reason = f.Reason,
        RecommendedAction = f.RecommendedAction,
        ClassificationBasis = f.ClassificationBasis,
        ClassificationSignal = f.ClassificationSignal,
        LinkedReferences = [.. f.Matches.Select(m => new TaskAlignmentRunReference(m.ItemId, m.Title, m.MatchType.ToString()))],
        DesignLinks = [.. f.DesignLinks.Select(d => new TaskAlignmentRunDesignLink(d.ArtifactType, d.Name, d.Source))],
        Deviation = f.Deviation is { } d ? new TaskAlignmentRunDeviation(d.Rule, d.TaskSays, d.AuthoredReference, d.AuthoredSays) : null,
        UnlinkedBusinessTask = f.UnlinkedBusinessTask,
        UnresolvedReferences = [.. f.UnresolvedReferences],
        AffectedAreas = [.. f.AffectedAreas.Select(a => a.ToString())],
        RecommendedTests = [.. f.RecommendedTests],
        ImpactLevel = f.ImpactLevel.ToString(),
        MatchReason = f.MatchReason,
        RiskReason = f.RiskReason,
        IsRegressionCandidate = f.IsRegressionCandidate,
        IsFinding = f.IsFinding,
    };

    public static AlignmentReport ToReport(TaskAlignmentRun run)
    {
        var c = run.Summary.Counts;
        return new AlignmentReport
        {
            TotalTasks = c.TotalTasks,
            LinkedTasks = c.LinkedTasks,
            TechnicalOnlyTasks = c.TechnicalOnlyTasks,
            DesignLinkedTasks = c.DesignLinkedTasks,
            TestImplementationTasks = c.TestImplementationTasks,
            NeedsReviewTasks = c.NeedsReviewTasks,
            PossibleDeviations = c.PossibleDeviations,
            HighImpactTasks = c.HighImpactTasks,
            MediumImpactTasks = c.MediumImpactTasks,
            LowImpactTasks = c.LowImpactTasks,
            UnknownImpactTasks = c.UnknownImpactTasks,
            RegressionCandidates = c.RegressionCandidates,
            Findings = [.. run.Tasks.Select(ToFinding)],
        };
    }

    public static TaskFinding ToFinding(TaskAlignmentRunTask t) => new()
    {
        TaskId = t.TaskId,
        Title = t.Title,
        // An unknown stored status is never upgraded to a link: it reads as Needs review.
        Status = Parse(t.Status, AlignmentStatus.NeedsReview),
        Risk = Parse(t.Risk, AlignmentRisk.Medium),
        Purpose = Parse(t.Purpose, TaskPurpose.Unknown),
        Reason = t.Reason,
        RecommendedAction = t.RecommendedAction,
        ClassificationBasis = t.ClassificationBasis,
        ClassificationSignal = t.ClassificationSignal,
        Matches = [.. t.LinkedReferences.Select(r => new SpecMatch { ItemId = r.ItemId, Title = r.Title, MatchType = Parse(r.MatchType, SpecMatchType.None) })],
        DesignLinks = [.. t.DesignLinks.Select(d => new DesignArtifactLink(d.ArtifactType, d.Name, d.Source))],
        Deviation = t.Deviation is { } d ? new DeviationEvidence(d.Rule, d.TaskSays, d.AuthoredReference, d.AuthoredSays) : null,
        UnlinkedBusinessTask = t.UnlinkedBusinessTask,
        UnresolvedReferences = [.. t.UnresolvedReferences],
        AffectedAreas = [.. t.AffectedAreas.Select(a => Enum.TryParse<AffectedArea>(a, out var v) ? (AffectedArea?)v : null).OfType<AffectedArea>()],
        RecommendedTests = [.. t.RecommendedTests],
        ImpactLevel = Parse(t.ImpactLevel, ImpactLevel.Unknown),
        MatchReason = t.MatchReason,
        RiskReason = t.RiskReason,
        IsRegressionCandidate = t.IsRegressionCandidate,
    };

    private static T Parse<T>(string? value, T fallback) where T : struct, Enum =>
        Enum.TryParse<T>(value, out var parsed) && Enum.IsDefined(parsed) ? parsed : fallback;
}
