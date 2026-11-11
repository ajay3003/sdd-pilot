using System.Text.Json.Serialization;

namespace BirkNext.SourceBaselines;

/// <summary>Where a snapshot's branch name comes from. Only Git metadata is evidence; a declared label is a person's statement; otherwise unknown.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SourceBranchKind { Verified, Declared, Unknown }

/// <summary>
/// How a logical source (repository key) is represented in analysis:
/// <list type="bullet">
/// <item><c>Selected</c> — a person chose the analysis baseline snapshot;</item>
/// <item><c>SingleSnapshot</c> — only one snapshot exists, nothing to choose;</item>
/// <item><c>IdenticalContent</c> — several imports, all with the same canonical content (one effective version);</item>
/// <item><c>NotSelected</c> — alternative versions with different content exist and none is chosen: modules fall back to the newest import and say
/// so; implementation-specific conclusions need a baseline;</item>
/// <item><c>SelectedUnavailable</c> — the chosen baseline snapshot no longer exists; nothing is substituted.</item>
/// </list>
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SourceBaselineState { Selected, SingleSnapshot, IdenticalContent, NotSelected, SelectedUnavailable }

public sealed record SourceSnapshotView(
    Guid SnapshotId, string ArchiveName, string ArchiveSha256, string? ContentFingerprint, DateTimeOffset AnalyzedAt, string Status,
    SourceBranchKind BranchKind, string? Branch, string? Commit, string? ImportId, bool IsBaseline, bool IsNewest, bool UsedForAnalysis,
    int AnalyzerVersion, int FilesAnalyzed, IReadOnlyList<string> Components, int SameContentAs);

/// <summary>A logical source (repository identity) with its snapshots, newest first. <see cref="IdentityBasis"/> says how snapshots were grouped
/// (e.g. "Archive file name" = grouped by name only, not verified repository identity).</summary>
public sealed record LogicalSourceView(
    string RepositoryKey, string DisplayName, string IdentityBasis, SourceBaselineState State, Guid? BaselineSnapshotId, Guid? UsedSnapshotId,
    string StateDetail, int SnapshotCount, int DistinctContents, IReadOnlyList<SourceSnapshotView> Snapshots);

public sealed record SourceDecisionView(Guid Id, string Kind, string Subject, Guid? SnapshotId, string? Value, string? SnapshotLabel, string? ContentFingerprint,
    DateTimeOffset DecidedAt, string DecidedBy, string? Reason);

/// <summary>The shared source context: every logical source with the snapshot each module uses. <see cref="ContextFingerprint"/> changes whenever any
/// used snapshot changes, so callers can key caches and detect a stale page.</summary>
public sealed record SourceContextView(IReadOnlyList<LogicalSourceView> Sources, int LogicalSources, int Snapshots, int NeedsBaseline, string ContextFingerprint);

public sealed record SourceBaselineRequest(string RepositoryKey, Guid? SnapshotId, string? Reason = null);

public sealed record DeclaredBranchRequest(Guid SnapshotId, string? Branch);

public sealed record SourceDecisionResult(bool Changed, string Message, SourceDecisionView? Decision);
