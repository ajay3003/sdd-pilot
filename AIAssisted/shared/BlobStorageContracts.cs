using System.Text.Json.Serialization;

namespace BirkNext.BlobStorage;

// Read-only Blob Storage explorer. A configured store is either an Azure storage account (identity-based, read-only SDK calls, disabled unless
// explicitly configured for an environment) or a SYNTHETIC LOCAL store (Development only: a configured folder read as containers/blobs, labelled
// as not Azure everywhere). No operation writes, deletes, leases, snapshots, tiers, generates SAS or fetches an arbitrary URL.

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BlobStoreKind { AzureStorageAccount, SyntheticLocal }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BlobStoreReadiness { Ready, NotConfigured, Disabled, PermissionDenied, Unavailable }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BlobPreviewKind { Json, Xml, Csv, Ndjson, Text, Parquet, Binary }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BlobCheckLevel { Exists, Metadata, Readable, Syntax, Content, Correlation }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BlobCheckOutcome { Pass, Fail, NotAssessed }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BlobCorrelationState { Confirmed, NotCorrelated }

public sealed record BlobStoreView(string Id, string EnvironmentId, string Name, BlobStoreKind Kind, string Endpoint, BlobStoreReadiness Readiness, string Detail, List<string> AllowedContainers);

public sealed record BlobContainerView(string Name, DateTimeOffset? LastModified);

public sealed record BlobItemView
{
    public string Name { get; init; } = "";
    /// <summary>A virtual folder (name prefix ending in "/") — not a directory unless the account is hierarchical.</summary>
    public bool IsPrefix { get; init; }
    public long? Size { get; init; }
    public string? ContentType { get; init; }
    public DateTimeOffset? LastModified { get; init; }
    public string? ETag { get; init; }
    public string? BlobType { get; init; }
    public string? AccessTier { get; init; }
}

public sealed record BlobListing(string Container, string? Prefix, List<BlobItemView> Items, string? ContinuationToken, DateTimeOffset ListedAt, string Scope);

public sealed record BlobProperties
{
    public string Container { get; init; } = "";
    public string Name { get; init; } = "";
    public long Size { get; init; }
    public string? ContentType { get; init; }
    public string? ContentEncoding { get; init; }
    public DateTimeOffset? LastModified { get; init; }
    public DateTimeOffset? CreatedOn { get; init; }
    public string? ETag { get; init; }
    public string? BlobType { get; init; }
    public string? AccessTier { get; init; }
    public string? VersionId { get; init; }
    /// <summary>User metadata after redaction (sensitive keys and values masked).</summary>
    public Dictionary<string, string> Metadata { get; init; } = [];
}

public sealed record BlobPreview
{
    public BlobPreviewKind Kind { get; init; }
    /// <summary>Redacted, bounded text (JSON pretty-printed, XML, CSV, NDJSON lines, text); null for binary and Parquet.</summary>
    public string? Text { get; init; }
    public bool Truncated { get; init; }
    public long BytesRead { get; init; }
    public int RedactedValues { get; init; }
    /// <summary>Syntax check of what was read: null = not checked (binary), true/false otherwise; never "valid" for a truncated document.</summary>
    public bool? SyntaxValid { get; init; }
    public List<string> Diagnostics { get; init; } = [];
    /// <summary>CSV: header columns; NDJSON: union of top-level properties; JSON: top-level properties; Parquet: column paths.</summary>
    public List<string> Columns { get; init; } = [];
    /// <summary>Records counted in what was read (CSV data rows, NDJSON lines); a measured count of the preview, not of the blob unless complete.</summary>
    public long? RecordsRead { get; init; }
    public bool Complete { get; init; }
    /// <summary>Parquet: the same engine as the Parquet Data Explorer (metadata row count, first rows).</summary>
    public long? ParquetRows { get; init; }
    public List<List<string?>>? Rows { get; init; }
}

/// <summary>What a test expects to find: identity, format and content conditions. Versioned by the caller.</summary>
public sealed record BlobExpectation
{
    public string Container { get; init; } = "";
    /// <summary>Exact blob name, or a prefix ending in "/" (the newest matching blob is checked).</summary>
    public string Path { get; init; } = "";
    public BlobPreviewKind? Format { get; init; }
    public string? ContentType { get; init; }
    public long? MinSize { get; init; }
    public long? MaxSize { get; init; }
    public List<string> RequiredFields { get; init; } = [];
    public long? ExpectedRecords { get; init; }
    /// <summary>The produced blob must be newer than this (observation window start).</summary>
    public DateTimeOffset? NotBefore { get; init; }
    /// <summary>An Execution Evidence run that should have produced the blob; correlation needs its execution id in the blob's metadata or name.</summary>
    public Guid? RunId { get; init; }
}

public sealed record BlobCheck(BlobCheckLevel Level, BlobCheckOutcome Outcome, string Detail);

public sealed record BlobValidation
{
    public string StoreId { get; init; } = "";
    public string Container { get; init; } = "";
    public string? BlobName { get; init; }
    public List<BlobCheck> Checks { get; init; } = [];
    public BlobCorrelationState Correlation { get; init; } = BlobCorrelationState.NotCorrelated;
    public string CorrelationDetail { get; init; } = "";
    public Guid? CorrelatedRunId { get; init; }
}

/// <summary>Saved observation: identity, redacted metadata, hash of the bytes read, checks, correlation — never the content.</summary>
public sealed record BlobObservation
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string EnvironmentId { get; init; } = "";
    public string StoreId { get; init; } = "";
    public BlobStoreKind StoreKind { get; init; }
    public string Container { get; init; } = "";
    public string BlobName { get; init; } = "";
    public string? ETag { get; init; }
    public long Size { get; init; }
    public string? Sha256OfBytesRead { get; init; }
    public bool WholeBlobRead { get; init; }
    public Dictionary<string, string> Metadata { get; init; } = [];
    public BlobValidation? Validation { get; init; }
    public DateTimeOffset ObservedAt { get; init; }
    public string ObservedBy { get; init; } = "";
}

public sealed record BlobValidateRequest(string EnvironmentId, string StoreId, BlobExpectation Expectation, bool Save);
