using BirkNext.AiCodeReview;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>
/// Pure presentation for the AI-Generated Code Review page, plus the client-side summary of workspace evidence the review needs.
/// The summary is read from the existing requirement graph (<see cref="SddEvidenceGraphService"/>) and the Tasks artifact — it never
/// links requirements by text similarity and never treats a checked task box as implementation.
/// </summary>
public static class AiCodeReviewPresentation
{
    public static AiWorkspaceEvidence WorkspaceEvidence(IWorkspaceSessionService workspace, IReadOnlyList<SddRequirementGraphRow> rows, string? projectName)
    {
        var requirements = rows.Where(r => !string.IsNullOrWhiteSpace(r.Requirement.Id)).GroupBy(r => r.Requirement.Id, StringComparer.Ordinal).Select(g => g.First()).Select(r =>
        {
            var implementation = r.Implementation.ToList();
            return new AiRequirementEvidence(r.Requirement.Id, implementation.Count > 0,
                implementation.Select(i => i.FilePath).Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => f!).Distinct(StringComparer.Ordinal).ToList(),
                implementation.Select(i => i.SourceSnapshotId).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                r.DesignedTests.Count > 0, r.Executions.Count > 0);
        }).ToList();

        var completedWithout = new List<AiTaskEvidence>();
        var unreferenced = 0;
        if (workspace.Tasks is { } tasks)
        {
            var byTask = rows.SelectMany(r => r.TaskReferences.Select(t => (Task: t, Requirement: r.Requirement.Id))).GroupBy(x => x.Task, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Select(x => x.Requirement).Distinct(StringComparer.Ordinal).ToList(), StringComparer.OrdinalIgnoreCase);
            var withEvidence = requirements.Where(r => r.HasImplementationEvidence).Select(r => r.RequirementId).ToHashSet(StringComparer.Ordinal);
            foreach (var task in Flatten(TaskExplorerService.Parse(tasks.Text).Roots).Where(t => t.NodeType == TaskNodeType.Task && t.IsCompleted && !string.IsNullOrWhiteSpace(t.TaskId)))
            {
                if (!byTask.TryGetValue(task.TaskId!, out var reqs) || reqs.Count == 0) { unreferenced++; continue; }
                if (!reqs.Any(withEvidence.Contains)) completedWithout.Add(new(task.TaskId!, reqs));
            }
        }

        var lifecycle = workspace.SddLifecycle;
        AiTestExecutionSummary? execution = null;
        if (lifecycle.TestRuns.Count > 0 || lifecycle.TestExecutions.Count > 0)
        {
            var executions = lifecycle.TestExecutions;
            execution = new(Math.Max(lifecycle.TestRuns.Count, executions.Count > 0 ? 1 : 0), executions.Count,
                executions.Count(e => e.Result.Equals("Passed", StringComparison.OrdinalIgnoreCase)), executions.Count(e => e.Result.Equals("Failed", StringComparison.OrdinalIgnoreCase)),
                executions.Count(e => e.Result is "Skipped" or "NotExecuted"), null);
        }

        return new AiWorkspaceEvidence
        {
            ProjectName = projectName, SpecificationAvailable = workspace.Specification is not null, ConstitutionAvailable = workspace.Constitution is not null,
            TasksAvailable = workspace.Tasks is not null, RequirementCount = requirements.Count, Requirements = requirements,
            CompletedTasksWithoutEvidence = completedWithout, CompletedTasksWithoutRequirementReferences = unreferenced, TestExecution = execution,
        };
    }

    private static IEnumerable<TaskNode> Flatten(IEnumerable<TaskNode> nodes) => nodes.SelectMany(n => new[] { n }.Concat(Flatten(n.Children)));

    public static string Tone(AiCategoryStatus status) => status switch
    {
        AiCategoryStatus.Findings => "attention", AiCategoryStatus.NoIndicators => "clear", AiCategoryStatus.NotAssessed => "neutral",
        AiCategoryStatus.Unsupported => "neutral", _ => "muted",
    };

    public static string Tone(AiFindingSeverity severity) => severity switch
    {
        AiFindingSeverity.High => "high", AiFindingSeverity.Medium => "medium", AiFindingSeverity.Low => "low", _ => "info",
    };

    public static string Tone(AiRuleExecutionState state) => state switch
    {
        AiRuleExecutionState.Executed => "clear", AiRuleExecutionState.Failed => "attention", _ => "neutral",
    };

    public static string Label(AiEvidenceSource source) => source switch
    {
        AiEvidenceSource.GeneratedDocumentation => "Generated documentation",
        AiEvidenceSource.SnapshotDiff => "Snapshot diff",
        _ => source.ToString(),
    };

    public static string Label(AiChangeKind? change) => change switch
    {
        AiChangeKind.Introduced => "Introduced", AiChangeKind.Removed => "Removed", AiChangeKind.Changed => "Changed", AiChangeKind.Unchanged => "Unchanged", _ => "—",
    };

    public static string Location(AiFindingLocation l) => l.Line > 0 ? $"{l.File}:{l.Line}" : l.File;

    /// <summary>The page's run gate: a current snapshot must be chosen; changed-files scope needs a baseline.</summary>
    public static string? RunBlockedReason(Guid? current, Guid? baseline, AiReviewScope scope, bool busy) =>
        busy ? "A review is running." : current is null ? "Choose the current Source Analysis snapshot."
        : baseline == current ? "The baseline must be a different snapshot." : scope == AiReviewScope.ChangedFiles && baseline is null ? "Changed-files scope needs a baseline snapshot." : null;
}
