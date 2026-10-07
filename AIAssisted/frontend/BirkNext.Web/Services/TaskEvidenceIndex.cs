using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>
/// Whether the workspace's Source Analysis lets implementation evidence be assessed. Shared by Implementation Review and
/// Task Explorer so both say "not assessed" for the same reasons.
/// </summary>
public static class SddSourceAssessment
{
    /// <summary>A current snapshot whose analysis is Ready or Partial.</summary>
    public static bool HasSource(SddLifecycleState lifecycle) =>
        lifecycle.SourceSnapshots.Any(x => x.Currentness == "Current" && x.AnalysisStatus is "Ready" or "Partial");

    public static bool HasFailedSource(SddLifecycleState lifecycle) =>
        lifecycle.SourceSnapshots.Any(x => x.Currentness == "Current" && x.AnalysisStatus == "Failed");

    public static bool HasPartialSource(SddLifecycleState lifecycle) =>
        lifecycle.SourceSnapshots.Any(x => x.Currentness == "Current" && x.AnalysisStatus == "Partial");

    /// <summary>
    /// Implementation evidence can be judged: some evidence is recorded, or a complete (Ready, not Partial) current source
    /// snapshot exists, so "no evidence" means nothing was found rather than nothing was looked at.
    /// </summary>
    public static bool ImplementationAssessed(SddLifecycleState lifecycle) =>
        lifecycle.ImplementationEvidence.Count > 0
        || lifecycle.SourceSnapshots.Any(x => x.Currentness == "Current" && x.AnalysisStatus == "Ready");
}

/// <summary>Implementation evidence for a requirement or a task, from the SDD lifecycle graph (never from a task checkbox).</summary>
public enum ImplementationEvidenceState
{
    /// <summary>The task references no requirement, so requirement-level evidence cannot be resolved for it.</summary>
    NoLinkedRequirement,
    /// <summary>No source was analysed (or only partially) and no evidence is recorded: nothing was looked at.</summary>
    NotAssessed,
    /// <summary>Assessed, and no implementation evidence is linked.</summary>
    None,
    /// <summary>Only evidence from earlier source snapshots or artifact revisions.</summary>
    Historical,
    /// <summary>Evidence exists but may be stale (its source changed since it was linked).</summary>
    Stale,
    /// <summary>Current implementation evidence exists. Evidence is not proof of correctness.</summary>
    Current,
}

/// <summary>Test evidence for a task, through its linked requirements: designed, executed and the results of current executions.</summary>
public sealed record TaskTestEvidence(int Designed, int Executed, int Passed, int Failed)
{
    public static readonly TaskTestEvidence Empty = new(0, 0, 0, 0);
    public bool Any => Designed > 0 || Executed > 0;
}

/// <summary>A task's evidence, resolved through the requirements it references.</summary>
public sealed record TaskEvidence(
    ImplementationEvidenceState Implementation,
    IReadOnlyList<string> Requirements,
    TaskTestEvidence Tests,
    bool TestsAssessed);

/// <summary>
/// Task Explorer's view of implementation and test evidence. It reads the same SDD lifecycle projection as Implementation
/// Review and Requirements Traceability (<see cref="SddEvidenceGraphService"/>): evidence is recorded per requirement, so a
/// task carries the evidence of the requirements it references (FR IDs). Nothing here is inferred from the task checkbox.
/// </summary>
public sealed class TaskEvidenceIndex
{
    private readonly Dictionary<string, SddRequirementGraphRow> _rows;

    private TaskEvidenceIndex(Dictionary<string, SddRequirementGraphRow> rows, bool implementationAssessed, bool testsAssessed)
    {
        _rows = rows;
        ImplementationAssessed = implementationAssessed;
        TestsAssessed = testsAssessed;
    }

    /// <summary>No lifecycle evidence available (no specification requirements, no source analysis, no test evidence).</summary>
    public static readonly TaskEvidenceIndex NotAssessed = new(new(StringComparer.OrdinalIgnoreCase), false, false);

    /// <summary>Implementation evidence was assessed for the workspace (see <see cref="SddSourceAssessment.ImplementationAssessed"/>).</summary>
    public bool ImplementationAssessed { get; }

    /// <summary>Some designed tests or test executions are recorded in the workspace lifecycle.</summary>
    public bool TestsAssessed { get; }

    public static TaskEvidenceIndex Build(SddLifecycleProjection projection, SddLifecycleState lifecycle)
    {
        var rows = projection.Rows
            .GroupBy(r => r.Requirement.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        if (rows.Count == 0) return NotAssessed;
        return new TaskEvidenceIndex(rows,
            SddSourceAssessment.ImplementationAssessed(lifecycle),
            lifecycle.TestEvidence.Count > 0 || lifecycle.TestExecutions.Count > 0);
    }

    /// <summary>The implementation state of one requirement's evidence.</summary>
    public static ImplementationEvidenceState StateOf(IEnumerable<SddImplementationEvidence> evidence, bool assessed)
    {
        var list = evidence.ToList();
        if (list.Any(x => x.Currentness == "Current")) return ImplementationEvidenceState.Current;
        if (list.Any(x => x.Currentness == "PotentiallyStale")) return ImplementationEvidenceState.Stale;
        if (list.Count > 0) return ImplementationEvidenceState.Historical;
        return assessed ? ImplementationEvidenceState.None : ImplementationEvidenceState.NotAssessed;
    }

    public TaskEvidence For(TaskNode task)
    {
        var requirements = task.ReferencedFrIds.Where(_rows.ContainsKey).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (task.ReferencedFrIds.Count == 0 || requirements.Count == 0)
            return new(task.ReferencedFrIds.Count == 0 || _rows.Count > 0 ? ImplementationEvidenceState.NoLinkedRequirement : ImplementationEvidenceState.NotAssessed,
                requirements, TaskTestEvidence.Empty, TestsAssessed);

        var rows = requirements.Select(id => _rows[id]).ToList();
        // The strongest state across the linked requirements: current evidence for any of them counts as current.
        var implementation = rows.Select(r => StateOf(r.Implementation, ImplementationAssessed)).Max();

        var designed = rows.SelectMany(r => r.DesignedTests).Where(t => t.Currentness != "Historical").DistinctBy(t => t.Id).ToList();
        var executions = rows.SelectMany(r => r.Executions).Where(e => e.Currentness == "Current").DistinctBy(e => e.Id).ToList();
        var tests = new TaskTestEvidence(designed.Count, executions.Count,
            executions.Count(e => e.Result == "Passed"), executions.Count(e => e.Result == "Failed"));
        return new(implementation, requirements, tests, TestsAssessed);
    }
}
