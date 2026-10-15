namespace BirkNext.Api.Models;

/// <summary>
/// Bounded XSD-only source artifact captured with its immutable Source Analysis snapshot.
/// The source snapshot still contains redacted evidence only; this separate record exists
/// solely so users can reopen schema contracts without uploading the project again.
/// </summary>
public sealed class SourceSchemaArtifactRecord
{
    public Guid SnapshotId { get; init; }
    public string SnapshotFingerprint { get; init; } = "";
    public string SourceArchiveName { get; init; } = "";
    public string? ProjectImportId { get; init; }
    public DateTimeOffset CapturedAt { get; init; }
    public bool IsComplete { get; init; }
    public int OmittedFiles { get; init; }
    public string PackageJson { get; init; } = "";
}
