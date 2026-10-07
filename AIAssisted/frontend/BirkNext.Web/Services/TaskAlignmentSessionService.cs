using System.Security.Cryptography;
using System.Text;
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

public sealed class TaskAlignmentSessionService
{
    public AlignmentReport? Report { get; private set; }
    public TaskAlignmentSnapshot? Snapshot { get; private set; }

    public bool HasCurrentResult(string? projectName, string specText, string tasksText) =>
        GetCurrentness(projectName, specText, tasksText) == TaskAlignmentCurrentness.Current;

    public TaskAlignmentCurrentness GetCurrentness(string? projectName, string specText, string tasksText)
    {
        if (Report is null || Snapshot is null)
            return TaskAlignmentCurrentness.NotRun;

        var loaded = CreateSnapshot(projectName, specText, tasksText);
        if (Snapshot.ProjectName != loaded.ProjectName)
            return TaskAlignmentCurrentness.NotRun;

        return Snapshot.SameArtifacts(loaded) && Snapshot.RulesVersion == TaskSpecAlignmentService.RulesVersion
            ? TaskAlignmentCurrentness.Current
            : TaskAlignmentCurrentness.Stale;
    }

    public void SaveResult(AlignmentReport report, string? projectName, string specText, string tasksText, DateTimeOffset? analyzedAtUtc = null)
    {
        Report = report;
        Snapshot = CreateSnapshot(projectName, specText, tasksText) with { AnalyzedAtUtc = analyzedAtUtc ?? DateTimeOffset.UtcNow };
    }

    public void Clear()
    {
        Report = null;
        Snapshot = null;
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

    private static string HashText(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }
}
