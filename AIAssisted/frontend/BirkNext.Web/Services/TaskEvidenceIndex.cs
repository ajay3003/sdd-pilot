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
    /// Which Source Analysis snapshot this workspace's reviews may use, by evidence-safe precedence: (1) a snapshot explicitly recorded for the
    /// workspace (CodeLinks / source tests bound to it), (2) the snapshot the current Project Import created or reused for the same archive,
    /// (3) none. Several explicitly recorded current snapshots are reported as ambiguous — none is chosen. Target Environment bindings are not
    /// used here: Implementation Evidence Review is documentation-to-source, not environment-bound.
    /// </summary>
    public static SourceLinkage Linkage(SddLifecycleState lifecycle)
    {
        var recorded = lifecycle.SourceSnapshots.Where(x => x.Currentness == "Current").ToList();
        if (recorded.Select(x => x.SnapshotId).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            return new(SourceLinkageState.Ambiguous, null, $"{recorded.Count} source snapshots are recorded as current; none is chosen automatically.");
        if (recorded.FirstOrDefault() is { } snapshot)
            return snapshot.AnalysisStatus == "Failed"
                ? new(SourceLinkageState.Failed, snapshot.SnapshotId, "The recorded Source Analysis failed.")
                : new(SourceLinkageState.Recorded, snapshot.SnapshotId, $"Source snapshot recorded for this workspace ({snapshot.AnalysisStatus}).");
        if (lifecycle.ProjectImports.FirstOrDefault(i => i.ImportId == lifecycle.CurrentProjectImportId) is { SourceSnapshotId: { } imported } import
            && import.SourceState is "Created" or "Reused")
            return new(SourceLinkageState.ImportedProject, imported.ToString(), $"Source snapshot from Project Import of {import.ArchiveFileName}.");
        return new(SourceLinkageState.Unavailable, null, "No Source Analysis snapshot is linked to this project.");
    }

    /// <summary>A requirement's implementation evidence state in tester words — never a failure claim.</summary>
    public static string RequirementImplementation(SourceLinkage linkage, IReadOnlyCollection<SddImplementationEvidence> evidence, bool packMapped)
    {
        if (evidence.Any(x => x.Currentness == "Current" && x.Confidence is "Confirmed" or "StronglySupported")) return "Implementation evidence linked";
        if (evidence.Any(x => x.Currentness == "Current")) return "Implementation evidence linked (inferred only)";
        if (evidence.Any(x => x.Currentness == "PotentiallyStale")) return "Evidence stale — from another source snapshot or artifact revision";
        if (evidence.Count > 0) return "Historical implementation evidence only";
        return linkage.State switch
        {
            SourceLinkageState.Unavailable => "Source snapshot unavailable — implementation not assessed",
            SourceLinkageState.Failed => "Source Analysis failed — implementation not assessed",
            SourceLinkageState.Ambiguous => "Source snapshot ambiguous — implementation not assessed",
            _ when !packMapped => "Source snapshot available — Artifact Pack not mapped to source; implementation not assessed",
            _ => "Source snapshot available — no explicit implementation link for this requirement",
        };
    }

    /// <summary>
    /// Source tests in tester words, kept apart from executed results: not assessed (no snapshot, or definitions not recorded), none discovered,
    /// discovered without explicit requirement references, or linked. Designed ≠ source test ≠ executed ≠ passed.
    /// </summary>
    public static string SourceTests(SddLifecycleState lifecycle)
    {
        var linkage = Linkage(lifecycle);
        var current = lifecycle.TestDefinitions?.Where(d => d.Currentness == "Current").ToList() ?? [];
        var executed = lifecycle.TestExecutions.Count > 0 ? "" : " Executed evidence not imported.";
        if (current.Count == 0)
        {
            var recordedSnapshot = lifecycle.SourceSnapshots.Any(x => x.Currentness == "Current");
            return linkage.State switch
            {
                SourceLinkageState.Unavailable => "Source tests not assessed — no Source Analysis snapshot is linked to this project.",
                SourceLinkageState.ImportedProject => "Source tests not assessed — the Project Import source snapshot is available; record its test definitions below.",
                _ when recordedSnapshot && (lifecycle.TestDefinitions?.Count ?? 0) > 0 => "No current source tests — earlier definitions are historical.",
                _ => "Source tests not assessed — record the selected snapshot's test definitions below.",
            } + executed;
        }
        var referencing = current.Count(d => d.Definition.References.Count > 0);
        return (referencing == 0
            ? $"Tests discovered; no explicit requirement link ({current.Count} source test{(current.Count == 1 ? "" : "s")})."
            : $"Linked source tests found: {referencing} of {current.Count} source tests reference a requirement or acceptance criterion explicitly.") + executed;
    }

    /// <summary>
    /// Implementation evidence can be judged: some evidence is recorded, or a complete (Ready, not Partial) current source
    /// snapshot exists, so "no evidence" means nothing was found rather than nothing was looked at.
    /// </summary>
    public static bool ImplementationAssessed(SddLifecycleState lifecycle) =>
        lifecycle.ImplementationEvidence.Count > 0
        || lifecycle.SourceSnapshots.Any(x => x.Currentness == "Current" && x.AnalysisStatus == "Ready");
}

public enum SourceLinkageState { Unavailable, ImportedProject, Recorded, Failed, Ambiguous }

/// <summary>The Source Analysis snapshot a review may use and why (<see cref="SddSourceAssessment.Linkage"/>).</summary>
public sealed record SourceLinkage(SourceLinkageState State, string? SnapshotId, string Basis)
{
    public bool Available => State is SourceLinkageState.ImportedProject or SourceLinkageState.Recorded;
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
