namespace BirkNext.Api.Models;

/// <summary>
/// One manual test run (table <c>manual_test_runs</c>): a person executing one saved test case (plan id + case id) of one project. The run's
/// content (<see cref="ContentJson"/>, a <c>ManualTestRun</c>) pins the case's steps, expected results and content fingerprint at start, so a
/// later case edit never rewrites the result. <see cref="Revision"/> is an optimistic-concurrency token; a completed or abandoned run is final.
/// </summary>
public sealed class ManualTestRunRecord
{
    public Guid Id { get; set; }
    public string ProjectKey { get; set; } = "";
    public Guid DraftId { get; set; }
    public string CaseId { get; set; } = "";
    public string State { get; set; } = "";
    public string Outcome { get; set; } = "";
    public int Revision { get; set; }
    public string ContentJson { get; set; } = "";
    public string Tester { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// A stored test scenario discovery result (table <c>scenario_discovery_results</c>) so a project's last discovery survives an API restart. The
/// result is the discovery engine's own output (a <c>ScenarioDiscoveryResult</c>), keyed by its content-derived discovery id; project and
/// snapshot keys say which project and source snapshot it describes. Nothing here interprets evidence.
/// </summary>
public sealed class ScenarioDiscoveryRecord
{
    public string DiscoveryId { get; set; } = "";
    public string ProjectKey { get; set; } = "";
    public string SnapshotKey { get; set; } = "";
    public string RuleVersion { get; set; } = "";
    public string ContentJson { get; set; } = "";
    public DateTimeOffset DiscoveredAt { get; set; }
}

/// <summary>
/// A file attached as evidence to a manual run (table <c>manual_run_attachments</c>): screenshots, PDFs and logs, size- and type-limited, kept in the
/// database so backups and project isolation cover them. The run's evidence list references it by id; the bytes are never rewritten.
/// </summary>
public sealed class ManualRunAttachmentRecord
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }
    public string ProjectKey { get; set; } = "";
    /// <summary>The safe display and download name (see <c>EvidenceFilePolicy.SafeName</c>).</summary>
    public string FileName { get; set; } = "";
    /// <summary>The name as uploaded, metadata only (never a path or a download name).</summary>
    public string OriginalFileName { get; set; } = "";
    public string ContentType { get; set; } = "";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
    public byte[] Content { get; set; } = [];
    public string AddedBy { get; set; } = "";
    public DateTimeOffset AddedAt { get; set; }
    /// <summary>Malware scan state: "not-scanned" unless a scanner actually ran ("clean"); a rejected file is never stored.</summary>
    public string ScanStatus { get; set; } = "not-scanned";
    /// <summary>Withdrawal at a person's request: the content is removed, this record stays.</summary>
    public DateTimeOffset? WithdrawnAt { get; set; }
    public string? WithdrawnBy { get; set; }
    public string? WithdrawReason { get; set; }
}
