namespace BirkNext.Api.Models;

/// <summary>
/// The text of one OpenAPI or GraphQL schema document of an immutable Source Analysis snapshot (bounded; same retention point as the XSD-only
/// <see cref="SourceSchemaArtifactRecord"/>). The snapshot itself keeps the parsed, redacted contract model; this record exists only so a contract
/// imported from the snapshot into the Contract Registry can be validated against its full schema (constraints, composition, formats) instead of
/// structurally. Never logged or exported; generated test data never copies values (e.g. examples) from it.
/// </summary>
public sealed class SourceContractDocumentRecord
{
    public Guid Id { get; init; }
    public Guid SnapshotId { get; init; }
    /// <summary>Archive-relative path with forward slashes, as the contract model's <c>File</c>.</summary>
    public string Path { get; init; } = "";
    public string Sha256 { get; init; } = "";
    public string Text { get; init; } = "";
    public DateTimeOffset CapturedAt { get; init; }
}
