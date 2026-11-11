namespace BirkNext.Api.Models;

/// <summary>
/// One Backup &amp; Transfer operation: export, import preview/apply, backup, restore. The import plan, its fingerprints, the identity mapping and
/// the reconciliation report live here. This is audit metadata, not workspace data: a preview writes only this row. Never holds a secret,
/// a passphrase or package content.
/// </summary>
public sealed class WorkspaceTransferOperationRecord
{
    public Guid Id { get; init; }
    /// <summary>Export, ImportPreview, Import, Backup, Restore.</summary>
    public string Kind { get; init; } = "";
    /// <summary>Exports/backups: CREATED / FAILED. Imports: PREVIEWED → APPROVED → APPLYING → COMPLETED | FAILED_RECOVERABLE | FAILED_ROLLED_BACK.</summary>
    public string State { get; set; } = "";
    public string Actor { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; set; }
    /// <summary>ProjectWorkspace, IntegrationWorkspace, Diagnostic, FullBackup.</summary>
    public string Scope { get; set; } = "";
    public string Summary { get; set; } = "";
    public string? PackageSha256 { get; init; }
    public string? PlanFingerprint { get; set; }
    public string? DestinationFingerprint { get; set; }
    public string? PlanJson { get; set; }
    public string? ResultJson { get; set; }
    public string? Error { get; set; }
    /// <summary>The staged package file name in the transfer staging directory (imports only); removed when the operation ends or expires.</summary>
    public string? StagedPackage { get; set; }
}

/// <summary>
/// A record imported from another BirkNext installation as history: a run, a review decision, a locator verification. It is never a destination run,
/// never re-grants an approval and never names the destination's user as the decider. Original identity, origin and timestamps are kept verbatim
/// (IMPORTED_HISTORICAL_EVIDENCE).
/// </summary>
public sealed class ImportedHistoricalRecord
{
    public Guid Id { get; init; }
    public Guid OperationId { get; init; }
    /// <summary>execution-run, integration-suite-run, critical-e2e-run, contract-review-decision, locator-verification, test-data-approval, …</summary>
    public string Kind { get; init; } = "";
    public string OriginalId { get; init; } = "";
    public string OriginInstanceId { get; init; } = "";
    public string? OriginalProject { get; init; }
    public string? OriginalEnvironmentId { get; init; }
    /// <summary>The destination environment the record was mapped to (null for project-scoped history).</summary>
    public string? EnvironmentId { get; init; }
    public DateTimeOffset? OriginalTimestamp { get; init; }
    public DateTimeOffset ImportedAt { get; init; }
    public string DocumentJson { get; init; } = "";
}
