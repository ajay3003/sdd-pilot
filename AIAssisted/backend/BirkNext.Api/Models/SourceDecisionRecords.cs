namespace BirkNext.Api.Models;

/// <summary>
/// One recorded decision about imported source, append-only (never updated or deleted; the newest row per subject is current, older rows are the
/// history). Kinds:
/// <list type="bullet">
/// <item><c>AnalysisBaseline</c> — subject = logical source (repository key); <see cref="SnapshotId"/> = the snapshot that represents that source for
/// analysis, or null = cleared. Never a deployment claim.</item>
/// <item><c>DeclaredBranch</c> — subject = snapshot id; <see cref="Value"/> = a branch label a person declared (not Git evidence), or null = removed.</item>
/// </list>
/// </summary>
public class SourceDecisionRecord
{
    public Guid Id { get; set; }
    public string Kind { get; set; } = "";
    public string Subject { get; set; } = "";
    public Guid? SnapshotId { get; set; }
    public string? Value { get; set; }
    /// <summary>The selected snapshot's identity at decision time (archive name + content fingerprint), so history stays readable if a snapshot is gone.</summary>
    public string? SnapshotLabel { get; set; }
    public string? ContentFingerprint { get; set; }
    public DateTimeOffset DecidedAt { get; set; }
    /// <summary>Derived actor (<c>authenticated-user:&lt;hash&gt;</c>, <c>local-session:loopback</c>) or "not attributed"; never a name or e-mail.</summary>
    public string DecidedBy { get; set; } = "";
    public string? Reason { get; set; }
}
