using System.Security.Cryptography;
using System.Text;
using BirkNext.TaskAlignment;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>The Specification and Task artifact fingerprints an Implementation Review result was produced from.</summary>
public sealed record TaskAlignmentSnapshot(
    string ProjectName,
    string SpecificationHash,
    string TasksHash)
{
    public DateTimeOffset AnalyzedAtUtc { get; init; }
    public string RulesVersion { get; init; } = TaskSpecAlignmentService.RulesVersion;
    /// <summary>Full path of the Tasks document within the project; another document with the same file name is never this result.</summary>
    public string? TasksPath { get; init; }
    /// <summary>The persisted run this result was saved as or restored from (null while not saved).</summary>
    public Guid? RunId { get; init; }

    /// <summary>Whether this snapshot was taken from the same artifacts (project + both fingerprints); time and rules version are provenance only.</summary>
    public bool SameArtifacts(TaskAlignmentSnapshot other) =>
        ProjectName == other.ProjectName && SpecificationHash == other.SpecificationHash && TasksHash == other.TasksHash;
}

/// <summary>Whether a stored Implementation Review result still matches the loaded Specification and Task artifacts.</summary>
public enum TaskAlignmentCurrentness
{
    /// <summary>No result for this project.</summary>
    NotRun,
    /// <summary>The result was produced from exactly the loaded Specification and Task artifacts with the current rules.</summary>
    Current,
    /// <summary>A result exists for this project, but the Specification, the Task artifact or the rules changed since.</summary>
    Stale,
}

/// <summary>Whether the shown result is persisted: it survives a browser reload only when Saved.</summary>
public enum TaskAlignmentSaveState { NotSaved, Saved, Failed }

/// <summary>What restoring the latest persisted run found.</summary>
public enum TaskAlignmentRestoreOutcome { NotAvailable, NoResult, Restored, Incomplete, Failed }

/// <summary>Everything a persisted run needs besides the report: identity and provenance of the analysed documents (never their bodies).</summary>
public sealed record TaskAlignmentRunContext(
    string ProjectKey,
    string TasksPath,
    string DesignInputsKey,
    string? SpecPath = null,
    string? Module = null,
    string? FeatureKey = null,
    Guid? SourceSnapshotId = null,
    string? ProjectName = null);

public sealed class TaskAlignmentSessionService
{
    public AlignmentReport? Report { get; private set; }
    public TaskAlignmentSnapshot? Snapshot { get; private set; }
    public TaskAlignmentSaveState SaveState { get; private set; }
    public string? SaveError { get; private set; }

    /// <summary>The identity a result is bound to (with the two fingerprints): the project key plus the design inputs it read.</summary>
    public static string ContextKey(string projectKey, string designInputsKey) => $"{projectKey}|design:{designInputsKey}";

    public bool HasCurrentResult(string? projectName, string specText, string tasksText) =>
        GetCurrentness(projectName, specText, tasksText) == TaskAlignmentCurrentness.Current;

    public TaskAlignmentCurrentness GetCurrentness(string? projectName, string specText, string tasksText) =>
        GetCurrentness(projectName, specText, tasksText, null);

    /// <summary>
    /// As above, and when both the held result and <paramref name="tasksPath"/> name a Tasks document, a different full path is a different
    /// document (NotRun), never a stale version of this one.
    /// </summary>
    public TaskAlignmentCurrentness GetCurrentness(string? projectName, string specText, string tasksText, string? tasksPath)
    {
        if (Report is null || Snapshot is null)
            return TaskAlignmentCurrentness.NotRun;

        var loaded = CreateSnapshot(projectName, specText, tasksText);
        if (Snapshot.ProjectName != loaded.ProjectName)
            return TaskAlignmentCurrentness.NotRun;
        if (Snapshot.TasksPath is not null && tasksPath is not null && !TaskAlignmentRunRules.SamePath(Snapshot.TasksPath, tasksPath))
            return TaskAlignmentCurrentness.NotRun;

        return Snapshot.SameArtifacts(loaded) && Snapshot.RulesVersion == TaskSpecAlignmentService.RulesVersion
            ? TaskAlignmentCurrentness.Current
            : TaskAlignmentCurrentness.Stale;
    }

    public void SaveResult(AlignmentReport report, string? projectName, string specText, string tasksText, DateTimeOffset? analyzedAtUtc = null, string? tasksPath = null)
    {
        Report = report;
        Snapshot = CreateSnapshot(projectName, specText, tasksText) with
        {
            AnalyzedAtUtc = analyzedAtUtc ?? DateTimeOffset.UtcNow,
            TasksPath = tasksPath is null ? null : TaskAlignmentRunRules.NormalizePath(tasksPath),
        };
        SaveState = TaskAlignmentSaveState.NotSaved;
        SaveError = null;
    }

    /// <summary>
    /// Persists the held result as an immutable completed run. On success the server's analyzed-at time and run id become the result's
    /// provenance; on failure the result stays shown but is marked not saved (it will not survive a reload).
    /// </summary>
    public async Task<bool> PersistAsync(ITaskAlignmentRunApiService api, TaskAlignmentRunContext context, CancellationToken ct = default)
    {
        if (Report is not { } report || Snapshot is not { } snapshot) return false;
        var request = new SaveTaskAlignmentRunRequest
        {
            ProjectKey = context.ProjectKey,
            ProjectName = string.IsNullOrWhiteSpace(context.ProjectName) ? null : context.ProjectName.Trim(),
            Module = context.Module,
            FeatureKey = context.FeatureKey,
            TasksPath = TaskAlignmentRunRules.NormalizePath(context.TasksPath),
            TasksHash = snapshot.TasksHash,
            SpecPath = context.SpecPath is null ? null : TaskAlignmentRunRules.NormalizePath(context.SpecPath),
            SpecHash = snapshot.SpecificationHash,
            SourceSnapshotId = context.SourceSnapshotId,
            RulesVersion = snapshot.RulesVersion,
            DesignInputsKey = context.DesignInputsKey,
            Completeness = TaskAlignmentRunRules.Complete,
            Counts = TaskAlignmentRunMapper.Counts(report),
            Tasks = [.. report.Findings.Select(TaskAlignmentRunMapper.ToRunTask)],
        };
        (TaskAlignmentRunSummary? Run, string? Error) result;
        try { result = await api.SaveAsync(request, ct); }
        catch (Exception ex) { result = (null, ex.Message); }
        // A newer analysis may have replaced the result while saving: never mark that one as saved.
        if (!ReferenceEquals(Report, report)) return result.Run is not null;
        if (result.Run is not { } saved)
        {
            SaveState = TaskAlignmentSaveState.Failed;
            SaveError = result.Error ?? "The result could not be saved.";
            return false;
        }
        Snapshot = snapshot with { AnalyzedAtUtc = saved.AnalyzedAt, RunId = saved.Id, TasksPath = saved.TasksPath };
        SaveState = TaskAlignmentSaveState.Saved;
        SaveError = null;
        return true;
    }

    /// <summary>
    /// Restores the latest persisted run of exactly this project and Tasks document path (after a reload the session is empty). A result already
    /// held for the same project and document is kept. An incomplete stored run is never used as a result.
    /// </summary>
    public async Task<TaskAlignmentRestoreOutcome> RestoreLatestAsync(ITaskAlignmentRunApiService api, string projectKey, string tasksPath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(projectKey) || string.IsNullOrWhiteSpace(tasksPath)) return TaskAlignmentRestoreOutcome.NotAvailable;
        if (Report is not null && Snapshot is { } held && held.ProjectName.StartsWith(projectKey + "|", StringComparison.Ordinal)
            && TaskAlignmentRunRules.SamePath(held.TasksPath, tasksPath))
            return TaskAlignmentRestoreOutcome.Restored;

        (TaskAlignmentRun? Run, string? Error) result;
        try { result = await api.LatestAsync(projectKey, TaskAlignmentRunRules.NormalizePath(tasksPath), ct); }
        catch (Exception ex) { result = (null, ex.Message); }
        if (result.Error is not null) return TaskAlignmentRestoreOutcome.Failed;
        if (result.Run is not { } run || !string.Equals(run.Summary.ProjectKey, projectKey, StringComparison.Ordinal)
            || !TaskAlignmentRunRules.SamePath(run.Summary.TasksPath, tasksPath))
            return TaskAlignmentRestoreOutcome.NoResult;
        if (!TaskAlignmentRunRules.IsComplete(run)) return TaskAlignmentRestoreOutcome.Incomplete;

        Report = TaskAlignmentRunMapper.ToReport(run);
        Snapshot = new TaskAlignmentSnapshot(ContextKey(projectKey, run.Summary.DesignInputsKey), run.Summary.SpecHash, run.Summary.TasksHash)
        {
            AnalyzedAtUtc = run.Summary.AnalyzedAt,
            RulesVersion = run.Summary.RulesVersion,
            TasksPath = run.Summary.TasksPath,
            RunId = run.Summary.Id,
        };
        SaveState = TaskAlignmentSaveState.Saved;
        SaveError = null;
        return TaskAlignmentRestoreOutcome.Restored;
    }

    public void Clear()
    {
        Report = null;
        Snapshot = null;
        SaveState = TaskAlignmentSaveState.NotSaved;
        SaveError = null;
    }

    public static TaskAlignmentSnapshot CreateSnapshot(string? projectName, string specText, string tasksText) =>
        new(
            NormalizeProjectName(projectName),
            HashText(specText),
            HashText(tasksText));

    /// <summary>Short display form of an artifact fingerprint.</summary>
    public static string ShortHash(string hash) => hash.Length <= 8 ? hash.ToLowerInvariant() : hash[..8].ToLowerInvariant();

    private static string NormalizeProjectName(string? projectName) =>
        string.IsNullOrWhiteSpace(projectName) ? string.Empty : projectName.Trim();

    /// <summary>SHA-256 of a document's text (upper-case hex): the fingerprint a result is bound to.</summary>
    public static string HashText(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }
}
