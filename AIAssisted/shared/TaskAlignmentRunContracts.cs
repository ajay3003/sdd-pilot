namespace BirkNext.TaskAlignment;

// Persisted Task Alignment runs: one immutable record per completed analysis of one Tasks document against its Specification in one project.
// A run stores fingerprints, provenance, counts and the per-task results — never the Specification or Tasks document bodies. Failed or refused
// analyses are never stored. Enum-like values are text (the frontend's AlignmentStatus / AlignmentRisk / TaskPurpose / ImpactLevel names).

/// <summary>A specification item a task links to (a resolved reference, not coverage or implementation).</summary>
public sealed record TaskAlignmentRunReference(string ItemId, string Title, string MatchType);

/// <summary>An authored design artifact a task names (Data Model structure, Constitution rule). Context, never requirement coverage.</summary>
public sealed record TaskAlignmentRunDesignLink(string ArtifactType, string Name, string? Source);

/// <summary>Both authored sides of a possible deviation.</summary>
public sealed record TaskAlignmentRunDeviation(string Rule, string TaskSays, string AuthoredReference, string AuthoredSays);

/// <summary>One task's Task Alignment result. Only <see cref="IsFinding"/> results (Needs review, Possible deviation) are findings.</summary>
public sealed record TaskAlignmentRunTask
{
    public string TaskId { get; init; } = "";
    public string Title { get; init; } = "";
    public string Status { get; init; } = "";
    public string Risk { get; init; } = "";
    public string Purpose { get; init; } = "";
    public string Reason { get; init; } = "";
    public string RecommendedAction { get; init; } = "";
    public string ClassificationBasis { get; init; } = "";
    public string? ClassificationSignal { get; init; }
    public List<TaskAlignmentRunReference> LinkedReferences { get; init; } = [];
    public List<TaskAlignmentRunDesignLink> DesignLinks { get; init; } = [];
    public TaskAlignmentRunDeviation? Deviation { get; init; }
    public bool UnlinkedBusinessTask { get; init; }
    public List<string> UnresolvedReferences { get; init; } = [];
    public List<string> AffectedAreas { get; init; } = [];
    public List<string> RecommendedTests { get; init; } = [];
    public string ImpactLevel { get; init; } = "";
    public string MatchReason { get; init; } = "";
    public string RiskReason { get; init; } = "";
    public bool IsRegressionCandidate { get; init; }
    public bool IsFinding { get; init; }
}

/// <summary>The summary counts of one run (same meaning as the Task Alignment report).</summary>
public sealed record TaskAlignmentRunCounts
{
    public int TotalTasks { get; init; }
    public int LinkedTasks { get; init; }
    public int TechnicalOnlyTasks { get; init; }
    public int DesignLinkedTasks { get; init; }
    public int TestImplementationTasks { get; init; }
    public int NeedsReviewTasks { get; init; }
    public int PossibleDeviations { get; init; }
    public int HighImpactTasks { get; init; }
    public int MediumImpactTasks { get; init; }
    public int LowImpactTasks { get; init; }
    public int UnknownImpactTasks { get; init; }
    public int RegressionCandidates { get; init; }
}

/// <summary>Saves one completed run. The server sets the id and the analyzed-at time; nothing is updated in place.</summary>
public sealed record SaveTaskAlignmentRunRequest
{
    /// <summary>The workspace project key (e.g. <c>import:{id}</c>, a Sample Project slug, or <c>manual-workspace</c>).</summary>
    public string ProjectKey { get; init; } = "";
    /// <summary>The project's name across imports (Project Import's archive root name), when known. Never enough on its own to make a run current.</summary>
    public string? ProjectName { get; init; }
    public string? Module { get; init; }
    /// <summary>The artifact pack / feature key, when the Tasks document belongs to one.</summary>
    public string? FeatureKey { get; init; }
    /// <summary>Full path of the Tasks document within the project (normalized to forward slashes).</summary>
    public string TasksPath { get; init; } = "";
    public string TasksHash { get; init; } = "";
    public string? SpecPath { get; init; }
    public string SpecHash { get; init; } = "";
    public Guid? SourceSnapshotId { get; init; }
    /// <summary>The deterministic rule-set version the analysis ran with.</summary>
    public string RulesVersion { get; init; } = "";
    /// <summary>Fingerprint of the optional design inputs (Constitution, Data Model) the analysis read; "none:none" without them.</summary>
    public string DesignInputsKey { get; init; } = "";
    /// <summary>Only <see cref="TaskAlignmentRunRules.Complete"/> runs are stored.</summary>
    public string Completeness { get; init; } = "";
    public TaskAlignmentRunCounts Counts { get; init; } = new();
    public List<TaskAlignmentRunTask> Tasks { get; init; } = [];
}

/// <summary>Provenance and counts of a stored run (no per-task results).</summary>
public sealed record TaskAlignmentRunSummary
{
    public Guid Id { get; init; }
    public string ProjectKey { get; init; } = "";
    public string? ProjectName { get; init; }
    public string? Module { get; init; }
    public string? FeatureKey { get; init; }
    public string TasksPath { get; init; } = "";
    public string TasksHash { get; init; } = "";
    public string? SpecPath { get; init; }
    public string SpecHash { get; init; } = "";
    public Guid? SourceSnapshotId { get; init; }
    public string RulesVersion { get; init; } = "";
    public string DesignInputsKey { get; init; } = "";
    public string RecordVersion { get; init; } = "";
    public string Completeness { get; init; } = "";
    public DateTimeOffset AnalyzedAt { get; init; }
    public TaskAlignmentRunCounts Counts { get; init; } = new();
}

/// <summary>A stored run with its per-task results.</summary>
public sealed record TaskAlignmentRun
{
    public TaskAlignmentRunSummary Summary { get; init; } = new();
    public List<TaskAlignmentRunTask> Tasks { get; init; } = [];
}

/// <summary>How a stored run relates to the loaded Tasks document.</summary>
public enum TaskAlignmentRunCurrentness
{
    /// <summary>No stored run for this project and Tasks document.</summary>
    NoResult,
    /// <summary>The latest run was produced from exactly the loaded inputs (same project, path, fingerprints and rules).</summary>
    Current,
    /// <summary>The latest run for the same project and Tasks document path, but the inputs (or rules) changed since.</summary>
    Stale,
    /// <summary>An older run: shown only as history, never as the current result.</summary>
    Historical,
    /// <summary>The stored run is not complete (its task results do not match its counts); never used as a result.</summary>
    Incomplete,
}

public static class TaskAlignmentRunRules
{
    /// <summary>Version of the stored record shape (not the analysis rules; those are <see cref="SaveTaskAlignmentRunRequest.RulesVersion"/>).</summary>
    public const string RecordVersion = "task-alignment-run/1";
    public const string Complete = "Complete";
    public const string ManualWorkspace = "manual-workspace";
    public const int MaxTasks = 5000;
    public const int MaxKeyLength = 300;
    public const int MaxPathLength = 1000;
    public const int MaxHashLength = 128;
    public const int MaxHistory = 50;

    /// <summary>Forward slashes, no leading "./" or "/", trimmed. The full path is kept: two Tasks documents with the same file name differ.</summary>
    public static string NormalizePath(string? path)
    {
        var p = (path ?? "").Trim().Replace('\\', '/');
        while (p.StartsWith("./", StringComparison.Ordinal)) p = p[2..];
        return p.TrimStart('/');
    }

    public static bool SamePath(string? a, string? b) =>
        NormalizePath(a) is { Length: > 0 } x && string.Equals(x, NormalizePath(b), StringComparison.Ordinal);

    /// <summary>Why a run cannot be stored, or null. Failed, refused or partial runs are never stored.</summary>
    public static string? Validate(SaveTaskAlignmentRunRequest? request)
    {
        if (request is null) return "A run is required.";
        if (string.IsNullOrWhiteSpace(request.ProjectKey) || request.ProjectKey.Length > MaxKeyLength) return "projectKey is required (at most 300 characters).";
        var path = NormalizePath(request.TasksPath);
        if (path.Length == 0 || path.Length > MaxPathLength) return "tasksPath is required (at most 1000 characters).";
        if (NormalizePath(request.SpecPath).Length > MaxPathLength) return "specPath is at most 1000 characters.";
        if (!IsHash(request.TasksHash) || !IsHash(request.SpecHash)) return "tasksHash and specHash must be hexadecimal fingerprints.";
        if (string.IsNullOrWhiteSpace(request.RulesVersion) || request.RulesVersion.Length > 100) return "rulesVersion is required.";
        if ((request.DesignInputsKey ?? "").Length > 200) return "designInputsKey is at most 200 characters.";
        if ((request.Module ?? "").Length > MaxKeyLength || (request.FeatureKey ?? "").Length > MaxKeyLength) return "module and featureKey are at most 300 characters.";
        if (!string.Equals(request.Completeness, Complete, StringComparison.Ordinal)) return "Only complete Task Alignment runs are stored.";
        if (request.Tasks is null || request.Counts is null) return "Only complete Task Alignment runs are stored.";
        if (request.Tasks.Count > MaxTasks) return $"At most {MaxTasks} tasks per run.";
        if (request.Tasks.Count != request.Counts.TotalTasks) return "The run is incomplete: its task results do not match its task count.";
        if (request.Tasks.Any(t => string.IsNullOrWhiteSpace(t.TaskId) || string.IsNullOrWhiteSpace(t.Status))) return "Every task result needs a task id and a status.";
        return null;
    }

    /// <summary>
    /// How <paramref name="run"/> relates to the loaded inputs. A run of another project or another Tasks document is never this document's
    /// result (NoResult). <paramref name="specHash"/>, <paramref name="rulesVersion"/> and <paramref name="designInputsKey"/> are compared only
    /// when given; <paramref name="isLatest"/> false makes a matching run Historical.
    /// </summary>
    public static TaskAlignmentRunCurrentness Classify(TaskAlignmentRunSummary? run, string? projectKey, string? tasksPath, string? tasksHash,
        string? specHash = null, string? rulesVersion = null, string? designInputsKey = null, bool isLatest = true, string? projectName = null)
    {
        if (run is null || string.IsNullOrWhiteSpace(projectKey) || !SamePath(run.TasksPath, tasksPath)
            || !(string.Equals(run.ProjectKey, projectKey, StringComparison.Ordinal) || IsEarlierImport(run, projectKey, projectName)))
            return TaskAlignmentRunCurrentness.NoResult;
        if (!string.Equals(run.Completeness, Complete, StringComparison.Ordinal)) return TaskAlignmentRunCurrentness.Incomplete;
        if (!isLatest) return TaskAlignmentRunCurrentness.Historical;
        var same = string.Equals(run.TasksHash, tasksHash, StringComparison.OrdinalIgnoreCase)
            && (specHash is null || string.Equals(run.SpecHash, specHash, StringComparison.OrdinalIgnoreCase))
            && (rulesVersion is null || string.Equals(run.RulesVersion, rulesVersion, StringComparison.Ordinal))
            && (designInputsKey is null || string.Equals(run.DesignInputsKey, designInputsKey, StringComparison.Ordinal));
        return same ? TaskAlignmentRunCurrentness.Current : TaskAlignmentRunCurrentness.Stale;
    }

    /// <summary>
    /// A run of an earlier import of the same project: another project key, the same non-empty project name (the caller also requires the same
    /// full Tasks path). The same document lineage at another source revision — current only when the fingerprints are identical.
    /// </summary>
    public static bool IsEarlierImport(TaskAlignmentRunSummary? run, string? projectKey, string? projectName) =>
        run is not null && !string.IsNullOrWhiteSpace(projectName) && !string.IsNullOrWhiteSpace(run.ProjectName)
        && !string.Equals(run.ProjectKey, projectKey, StringComparison.Ordinal)
        && string.Equals(run.ProjectName.Trim(), projectName.Trim(), StringComparison.Ordinal);

    /// <summary>A loaded run whose task results do not match its counts is Incomplete and never shown as a result.</summary>
    public static bool IsComplete(TaskAlignmentRun run) =>
        string.Equals(run.Summary.Completeness, Complete, StringComparison.Ordinal) && run.Tasks.Count == run.Summary.Counts.TotalTasks;

    private static bool IsHash(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= MaxHashLength && value.All(Uri.IsHexDigit);
}
