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
    /// <summary>The Git revision the archive was retrieved from (Git Sources import); null for a chosen ZIP. The commit SHA is the identity, not the branch.</summary>
    public BirkNext.GitSources.GitSourceRevision? Git { get; init; }
}

/// <summary>How the project display name was derived. Never hardcoded: the single archive root folder, else the archive file name.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProjectNameBasis { ArchiveRoot, ArchiveFileName }

/// <summary>The accepted archive. Sizes are the uploaded bytes and the archive's entry count.</summary>
/// <param name="ContentSha256">Canonical content fingerprint (same files = same value, whatever the archive name, entry order or compression).</param>
public sealed record ProjectImportArchive(string FileName, string Sha256, long SizeBytes, long EntryCount, string? ContentSha256 = null);

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

/// <summary>Elapsed time of the preview's phases on the backend, in milliseconds (measured, never estimated).</summary>
public sealed record ProjectImportTimings(long ValidationMs, long SourceDetectionMs, long ClassificationMs, long StagingMs);

/// <summary>The staged, validated archive and what it contains. Nothing is activated by a preview; it only stages the archive for the commit.</summary>
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
    /// <summary>
    /// Text of non-Markdown generated-documentation artifacts (OpenAPI/AsyncAPI YAML or JSON, GraphQL SDL, XSD, proto) the archive holds, so the
    /// browser can keep generated-documentation content for them. Never project documents: they are not classified into artifact roles.
    /// </summary>
    public List<ProjectImportDocument> GeneratedArtifactTexts { get; init; } = [];
    public ProjectImportSourceDetection Source { get; init; } = new();
    /// <summary>Files that are neither readable documents nor source files that Source Analysis reads (images, scripts, binaries…). A count, not a failure.</summary>
    public int OtherFiles { get; init; }
    public DateTimeOffset StagedAt { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
    /// <summary>
    /// Document roles classified on the backend by the same artifact classifier the browser uses (its source is compiled into both), one
    /// entry per document in <see cref="Documents"/>. Null from a backend that does not classify: the browser then classifies itself.
    /// Classification never selects among several candidates of a role.
    /// </summary>
    public List<BirkNext.Web.Services.SampleProjects.DiscoveredDocument>? Discovery { get; init; }
    public ProjectImportTimings? Timings { get; init; }
}

/// <summary>What happened to the source part of an import. Each value is a distinct fact; none is a review result.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProjectImportSourceState
{
    /// <summary>The archive has no source files that Source Analysis reads. Neutral.</summary>
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
    /// <summary>
    /// The generated documents the snapshot's own generated-documentation evidence identified (origin Generated), as references: path, module,
    /// kind, content fingerprint and generator when proven. Null when the snapshot holds no generated-documentation evidence. The browser
    /// groups them into the import's Generated Documentation Set; the snapshot keeps fingerprints only, never document text.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<ProjectImportGeneratedDocument>? GeneratedDocuments { get; init; }
}

/// <summary>One generated document of an imported project's source snapshot: identity and provenance only (no content).</summary>
public sealed record ProjectImportGeneratedDocument(string RelativePath, string ModuleId, string ModuleRoot, BirkNext.GeneratedDocumentation.GeneratedDocumentKind Kind,
    string Fingerprint, string? Generator);

/// <summary>Commit result: the source part only. Documents are activated by the frontend from the preview (the repository is frontend-owned).</summary>
public sealed record ProjectImportCommitResult
{
    public Guid StagingId { get; init; }
    public ProjectImportProvenance Provenance { get; init; } = new();
    public ProjectImportSourceResult Source { get; init; } = new();
    public DateTimeOffset? StagedUntil { get; init; }
    /// <summary>The implementation-gap evaluation made from this import's SDD documents and snapshot; null when it has no tasks document or failed.</summary>
    public Guid? ImplementationGapEvaluationId { get; init; }
    /// <summary>The application identity the source snapshot got, and how (null when no source snapshot was created).</summary>
    public ProjectImportApplicationOutcome? Application { get; init; }
}

// ── Application identity at import (2026-11-23) ─────────────────────────────────────────────────────────────────────
// The tester says whether a ZIP is a new application or a new version of one already imported. BirkNext never infers it from the file name:
// without a choice the archive's own identity (root solution, else archive name) is used, as before.

/// <summary>"Ny applikasjon" or "Ny versjon av eksisterende applikasjon".</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProjectImportApplicationMode { NewApplication, NewVersion }

/// <summary>An application already imported (one logical source identity over its imports).</summary>
public sealed record KnownApplication
{
    public string Key { get; init; } = "";
    public string Name { get; init; } = "";
    public string? LatestArchive { get; init; }
    public DateTimeOffset LatestImportedAt { get; init; }
    public int Imports { get; init; }
}

/// <summary>What the import page needs to ask the question: the archive's own identity and the applications already imported.</summary>
public sealed record ProjectImportApplicationOptions
{
    /// <summary>The identity the archive has on its own (root solution, else the archive file name).</summary>
    public string ArchiveKey { get; init; } = "";
    public string ArchiveName { get; init; } = "";
    public string ArchiveBasis { get; init; } = "";
    /// <summary>The known application with the same identity as the archive, if any (a plain re-import would join it).</summary>
    public string? SameIdentityAs { get; init; }
    public List<KnownApplication> Applications { get; init; } = [];
    /// <summary>The tester's choice recorded on this staged import, if any.</summary>
    public ProjectImportApplicationChoice? Choice { get; init; }
}

/// <param name="ApplicationKey">For <see cref="ProjectImportApplicationMode.NewVersion"/>: the chosen application's key (from the options).</param>
/// <param name="UseAsCurrent">For a new version: record this import as the application's analysis baseline (the existing Kildeversjoner decision).</param>
public sealed record ProjectImportApplicationChoice(ProjectImportApplicationMode Mode, string? ApplicationKey = null, bool UseAsCurrent = true)
{
    /// <summary>Set by the server: a warning when the archive looks unrelated to the chosen application (no project in common). Never blocks.</summary>
    public string? Warning { get; init; }
    /// <summary>Set by the server: the name the application identity will have.</summary>
    public string? ApplicationName { get; init; }
}

/// <summary>What the import decided about the application identity.</summary>
public sealed record ProjectImportApplicationOutcome
{
    public ProjectImportApplicationMode Mode { get; init; }
    public string ApplicationKey { get; init; } = "";
    public string ApplicationName { get; init; } = "";
    /// <summary>The archive's own identity, kept for traceability when it differs from the application's.</summary>
    public string ArchiveKey { get; init; } = "";
    public string ArchiveName { get; init; } = "";
    /// <summary>True when this import was recorded as the application's analysis baseline.</summary>
    public bool BaselineSelected { get; init; }
    /// <summary>True when the choice was made explicitly (false = archive identity without a choice, e.g. an older client).</summary>
    public bool Explicit { get; init; }
}

/// <summary>
/// A real update from the existing Project Import commit pipeline. Progress updates identify the stage currently executing; they never
/// estimate completion from time. Terminal events carry either the existing commit result or a safe failure summary.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProjectImportCommitEventKind { Progress, Completed, Failed }

public sealed record ProjectImportCommitEvent
{
    public ProjectImportCommitEventKind Kind { get; init; }
    public int Sequence { get; init; }
    public int? StageNumber { get; init; }
    public int? StageCount { get; init; }
    public string Stage { get; init; } = "";
    public string Detail { get; init; } = "";
    public ProjectImportCommitResult? Result { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorStage { get; init; }
    public string? ErrorMessage { get; init; }
    public string? CorrelationId { get; init; }
}
