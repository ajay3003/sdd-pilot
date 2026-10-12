using System.Text.Json.Serialization;

namespace BirkNext.Integrations;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// Altinn app testing: the contract of one Altinn 3 app as BirkNext knows it, and the tester's ID-porten TEST sign-in state. The contract
// comes from two read-only sources — the app's repository uploaded to Source Analysis (authoritative) and the app's public TT02 metadata —
// and is never guessed: a part that neither source supplies stays missing and keeps execution Not ready. No URL, secret, token or payload
// is representable here.
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AltinnContractSourceKind { SourceAnalysisSnapshot, PublicTt02Metadata }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AltinnContractPartKind { ApplicationMetadata, FormDataType, DataModelSchema, ProcessDefinition }

/// <summary>One field of the form data model, flattened from its schema (dot-separated path, JSON schema type/format).</summary>
public sealed record AltinnSchemaField(string Path, string Type, string? Format, bool Required);

/// <summary>A process task in order. <see cref="TaskType"/> is the Altinn task type (data, confirmation, signing, payment, feedback).</summary>
public sealed record AltinnProcessTask(string TaskId, string TaskType);

/// <summary>What one source said about the app. Null parts were not supplied by that source.</summary>
public sealed record AltinnContractSnapshot
{
    public AltinnContractSourceKind Source { get; init; }
    public string AppId { get; init; } = "";
    public DateTimeOffset CapturedAt { get; init; }
    /// <summary>SHA-256 over the source documents (hex), so a later run can tell which contract it used.</summary>
    public string Fingerprint { get; init; } = "";
    public IReadOnlyList<string> Documents { get; init; } = [];
    public bool HasApplicationMetadata { get; init; }
    /// <summary>The single data type the app binds its form model to (an app-logic data type). Null when absent or ambiguous.</summary>
    public string? FormDataTypeId { get; init; }
    public IReadOnlyList<string> FormDataTypeCandidates { get; init; } = [];
    public IReadOnlyList<string> AllowedContentTypes { get; init; } = [];
    public IReadOnlyList<AltinnSchemaField>? Fields { get; init; }
    public IReadOnlyList<AltinnProcessTask>? ProcessTasks { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = [];
    /// <summary>From the form schema (@xsdRootElement, default @xsdNamespaces, ElementFormDefault): how the form is written as XML.</summary>
    public string? XmlRootElement { get; init; }
    public string? XmlNamespace { get; init; }
    public bool XmlElementsQualified { get; init; }
    /// <summary>For a Source Analysis source: the snapshot and archive the app files were read from.</summary>
    public Guid? SourceSnapshotId { get; init; }
    public string? SourceArchive { get; init; }
}

/// <summary>Both sources as stored with the Integration Quality Review message-flow configuration of one environment.</summary>
public sealed record AltinnAppContractRecord(AltinnContractSnapshot? SourceAnalysis, AltinnContractSnapshot? PublicMetadata);

/// <summary>A Source Analysis snapshot that contains the source of one or more Altinn apps.</summary>
public sealed record AltinnSourceCandidate(Guid SnapshotId, string ArchiveName, DateTimeOffset AnalyzedAt, IReadOnlyList<string> AppIds);

public sealed record AltinnContractPartStatus(AltinnContractPartKind Part, bool Present, AltinnContractSourceKind? Source, string Detail);

/// <summary>A domain field the submission needs, and whether the app's schema really contains it (exact path and compatible type).</summary>
public sealed record AltinnFieldBinding(string Key, string Label, string ExpectedPath, string ExpectedType, bool Present, string Detail);

/// <summary>The effective contract: per part, the Source Analysis app source wins over the public metadata. Complete only when nothing is missing.</summary>
public sealed record AltinnAppContractView
{
    public string EnvironmentId { get; init; } = "";
    /// <summary>The app id configured in Integration Quality Review (Altinn test configuration).</summary>
    public string? ConfiguredAppId { get; init; }
    public bool AppApproved { get; init; }
    public string AppDetail { get; init; } = "";
    public string? FormDataTypeId { get; init; }
    public IReadOnlyList<AltinnContractPartStatus> Parts { get; init; } = [];
    public IReadOnlyList<AltinnFieldBinding> Bindings { get; init; } = [];
    /// <summary>The effective form schema fields (from the same source as the form data type).</summary>
    public IReadOnlyList<AltinnSchemaField> Fields { get; init; } = [];
    /// <summary>How the form data is submitted: application/json when the data type allows it, otherwise application/xml written from the
    /// schema's own root element and namespace. Null when neither is possible.</summary>
    public string? SubmissionContentType { get; init; }
    public string? XmlRootElement { get; init; }
    public string? XmlNamespace { get; init; }
    public bool XmlElementsQualified { get; init; }
    public IReadOnlyList<AltinnProcessTask> ProcessTasks { get; init; } = [];
    public IReadOnlyList<AltinnSchemaField> RequiredUnboundFields { get; init; } = [];
    public IReadOnlyList<AltinnContractSnapshot> Sources { get; init; } = [];
    /// <summary>Source Analysis snapshots of this environment that contain the configured app's source.</summary>
    public IReadOnlyList<AltinnSourceCandidate> SourceCandidates { get; init; } = [];
    public bool Complete { get; init; }
    public IReadOnlyList<string> Missing { get; init; } = [];
}

/// <summary>The tester's ID-porten TEST sign-in for one environment. The token itself never leaves the backend.</summary>
public sealed record AltinnSignInStatus
{
    public bool Configured { get; init; }
    public IReadOnlyList<string> ConfigurationProblems { get; init; } = [];
    public bool SignedIn { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public string Detail { get; init; } = "";
}

/// <summary>Where the browser goes to sign in. Built by the backend from approved ID-porten TEST endpoints only.</summary>
public sealed record AltinnSignInStart(string AuthorizationUrl, DateTimeOffset ExpiresAt);
