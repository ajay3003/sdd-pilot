using System.Text.Json.Serialization;

namespace BirkNext.ProjectImport;

// ── Project Import: one archive, two owners ──────────────────────────────────────────────────────────────────────────
// One ZIP is uploaded, validated and staged once. Its Markdown documents go to the Shared Artifact Repository (document roles,
// owned by the frontend workspace); its source goes to Source Analysis (immutable source snapshot, owned by the backend). The
// two results share only this provenance — the import identity and the exact archive fingerprint — never one evidence model.

/// <summary>
/// Where a source snapshot or an artifact revision came from when it was created by Project Import. <see cref="ImportId"/> is derived from
/// the SHA-256 of the exact uploaded bytes, so the same archive always has the same import identity; the file name is metadata only.
/// </summary>
public sealed record ProjectImportProvenance
{
    public string ImportId { get; init; } = "";
    public string ArchiveFileName { get; init; } = "";
    public string ArchiveSha256 { get; init; } = "";
    public DateTimeOffset ImportedAt { get; init; }
}

/// <summary>How the project display name was derived. Never hardcoded: the single archive root folder, else the archive file name.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProjectNameBasis { ArchiveRoot, ArchiveFileName }

/// <summary>The accepted archive. Sizes are the uploaded bytes and the archive's entry count.</summary>
public sealed record ProjectImportArchive(string FileName, string Sha256, long SizeBytes, long EntryCount);

/// <summary>One readable Markdown document of the archive: a candidate for artifact-role classification (the frontend classifier decides the role).</summary>
public sealed record ProjectImportDocument(string RelativePath, string FileName, long SizeBytes, string Content);

/// <summary>A Markdown document that was not read (too large, binary, over the document limit). Reported, never silently dropped.</summary>
public sealed record ProjectImportSkippedDocument(string RelativePath, string Reason);

/// <summary>A technology seen in the archive (path inventory and project/configuration markers only — a preview, not the analysis).</summary>
public sealed record ProjectImportTechnology(string TechnologyId, string DisplayName, string Area, bool SourceAnalysisSupported, int Files);

/// <summary>
/// Whether the archive contains source that Source Analysis reads. <see cref="Detected"/> = code, project/solution or dependency manifests,
/// pipelines, infrastructure as code or contracts. Configuration files alone, scripts and documents are not source. Not detected is a
/// neutral outcome (a documents-only project), never a failure.
/// </summary>
public sealed record ProjectImportSourceDetection
{
    public bool Detected { get; init; }
    public int SourceFiles { get; init; }
    public int UnsupportedSourceFiles { get; init; }
    public List<ProjectImportTechnology> Technologies { get; init; } = [];
    public List<string> Limitations { get; init; } = [];
}

/// <summary>The staged, validated archive and what it contains. Nothing is activated or persisted by a preview.</summary>
public sealed record ProjectImportPreview
{
    /// <summary>Identity of this staging (one upload). Commit and retry reference it; it expires with the staged archive.</summary>
    public Guid StagingId { get; init; }
    public string ImportId { get; init; } = "";
    public ProjectImportArchive Archive { get; init; } = new("", "", 0, 0);
    public string ProjectName { get; init; } = "";
    public ProjectNameBasis ProjectNameBasis { get; init; }
    public List<ProjectImportDocument> Documents { get; init; } = [];
    public List<ProjectImportSkippedDocument> SkippedDocuments { get; init; } = [];
    public ProjectImportSourceDetection Source { get; init; } = new();
    /// <summary>Files that are neither readable documents nor source Source Analysis reads (images, scripts, binaries…). A count, not a failure.</summary>
    public int OtherFiles { get; init; }
    public DateTimeOffset StagedAt { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
}

/// <summary>What happened to the source part of an import. Each value is a distinct fact; none is a review result.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProjectImportSourceState
{
    /// <summary>The archive has no source Source Analysis reads. Neutral.</summary>
    NotDetected,
    /// <summary>A new immutable source snapshot was created from the staged archive.</summary>
    Created,
    /// <summary>The same archive (same fingerprint) is already the workspace's current Source Analysis snapshot: it is reused, not duplicated.</summary>
    Reused,
    /// <summary>Source was detected but no snapshot was created because Source Analysis is turned off. A Target Environment is never required.</summary>
    NotCreated,
    /// <summary>Source analysis or the snapshot save failed. Recoverable without re-upload while staged; no snapshot exists.</summary>
    Failed,
}

/// <summary>The source outcome of a commit. Snapshot status is Source Analysis' own coverage status, never "review passed".</summary>
public sealed record ProjectImportSourceResult
{
    public ProjectImportSourceState State { get; init; }
    public Guid? SnapshotId { get; init; }
    public BirkNext.Integrations.SourceAnalysisStatus? SnapshotStatus { get; init; }
    public DateTimeOffset? AnalyzedAt { get; init; }
    public int? FilesAnalyzed { get; init; }
    public List<ProjectImportTechnology> Technologies { get; init; } = [];
    public List<string> Limitations { get; init; } = [];
    public string? Code { get; init; }
    public string? Message { get; init; }
    /// <summary>The staged archive is kept so the source part can be retried without choosing the ZIP again.</summary>
    public bool CanRetry { get; init; }
}

/// <summary>Commit result: the source part only. Documents are activated by the frontend from the preview (the repository is frontend-owned).</summary>
public sealed record ProjectImportCommitResult
{
    public Guid StagingId { get; init; }
    public ProjectImportProvenance Provenance { get; init; } = new();
    public ProjectImportSourceResult Source { get; init; } = new();
    public DateTimeOffset? StagedUntil { get; init; }
}
