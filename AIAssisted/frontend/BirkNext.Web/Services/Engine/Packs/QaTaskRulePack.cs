using BirkNext.Web.Models;

namespace BirkNext.Web.Services.Engine.Packs;

/// <summary>
/// QA Auditor rule pack: Task list quality checks.
/// Requires <see cref="RuleContext.Tasks"/> and optionally <see cref="RuleContext.Trace"/>.
///
/// Rules:
///   TASK-001 — Orphan tasks with no requirement or plan linkage
///   TASK-002 — No testing tasks defined
///   TASK-003 — Tasks without requirement references
/// </summary>
public sealed class QaTaskRulePack : IRulePack
{
    public string RulePackId   => "qa-task";
    public string RulePackName => "Task Quality";

    public RulePackResult Execute(RuleContext context)
    {
        var findings = new List<RuleFinding>();
        var gaps     = new List<RuleGap>();

        if (context.Tasks is null)
        {
            gaps.Add(new RuleGap
            {
                GapArea     = "Missing Task Coverage",
                Description = "Tasks not loaded — task audit unavailable",
                Severity    = "High",
            });
            return Result(findings, gaps);
        }

        var h     = context.Tasks.Health;
        var trace = context.Trace;

        // TASK-001: Orphan tasks (no requirement or plan linkage)
        if (trace is not null)
        {
            var orphans = trace.Gaps
                .Where(g => g.GapIn == ArtifactType.Task && g.Status == TraceabilityStatus.Orphaned)
                .ToList();

            if (orphans.Count > 0)
            {
                findings.Add(new RuleFinding
                {
                    RulePackId     = RulePackId,
                    RuleId         = "TASK-001",
                    Category       = "Task",
                    Title          = $"{orphans.Count} orphan task(s) with no requirement or plan coverage",
                    Description    = $"{orphans.Count} task(s) are not linked to any specification requirement or plan item.",
                    Severity       = "Medium",
                    Status         = "Failed",
                    Recommendation = "Link orphan tasks to specification requirements or plan items.",
                });
            }
        }

        // TASK-002: No testing tasks
        if (h.TotalTasks > 0 && h.TestingTasks == 0)
        {
            findings.Add(new RuleFinding
            {
                RulePackId     = RulePackId,
                RuleId         = "TASK-002",
                Category       = "Testing",
                Title          = "No testing tasks defined",
                Description    = "Task list has no test implementation tasks. Add unit test, integration test, and verification tasks.",
                Severity       = "High",
                Status         = "Failed",
                Recommendation = "Add testing tasks for each requirement and plan item.",
            });

            gaps.Add(new RuleGap
            {
                GapArea     = "Missing Testing Coverage",
                Description = "No testing tasks found in the task list",
                Severity    = "High",
            });
        }

        // TASK-003: Tasks without requirement references
        if (h.TotalTasks > 0 && h.UnlinkedTasks > 0)
        {
            findings.Add(new RuleFinding
            {
                RulePackId     = RulePackId,
                RuleId         = "TASK-003",
                Category       = "Task",
                Title          = $"{h.UnlinkedTasks} task(s) without requirement reference",
                Description    = $"{h.UnlinkedTasks} task(s) have no FR or SC references — traceability cannot be verified for these tasks.",
                Severity       = "Low",
                Status         = "Failed",
                Recommendation = "Add FR/SC references to unlinked tasks to enable requirement traceability.",
            });
        }

        return Result(findings, gaps);
    }

    private RulePackResult Result(List<RuleFinding> f, List<RuleGap> g) =>
        new() { RulePackId = RulePackId, RulePackName = RulePackName, Findings = f, Gaps = g };
}
