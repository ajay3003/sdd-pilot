using System.Text.Json.Serialization;

namespace BirkNext.Parquet;

// Parquet data explorer: a local file (or, later, a blob read through the same engine) is opened read-only; metadata comes from the footer, rows
// from bounded, projected reads. Metadata and row values, samples and full scans, exact and unavailable figures are kept apart and labelled.

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ParquetFigure { Exact, FromMetadata, NotAvailable }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ParquetFilterOperator { Equals, NotEquals, Contains, GreaterThan, LessThan, Between, IsNull, NotNull, In }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ParquetRuleKind { ColumnExists, TypeIs, NotNull, Unique, AllowedValues, Range, RecordCount }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ParquetRuleOutcome { Pass, Fail, Inconclusive }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ParquetRecordChange { Added, Removed, Changed, Unchanged, Ambiguous }

public sealed record ParquetSchemaNode
{
    public string Path { get; init; } = "";
    public string Name { get; init; } = "";
    /// <summary>BOOLEAN, INT32, INT64, INT96, FLOAT, DOUBLE, BYTE_ARRAY, FIXED_LEN_BYTE_ARRAY; null for a group.</summary>
    public string? PhysicalType { get; init; }
    /// <summary>The logical annotation as written (STRING, DATE, TIMESTAMP(MICROS, UTC), DECIMAL(10,2), …); null when none.</summary>
    public string? LogicalType { get; init; }
    public string Repetition { get; init; } = "";
    public int? TypeLength { get; init; }
    public bool IsGroup { get; init; }
    /// <summary>Whether this explorer reads the column's values (flat, supported type and encodings); otherwise why not.</summary>
    public bool Readable { get; init; }
    public string? NotReadableReason { get; init; }
    public List<ParquetSchemaNode> Children { get; init; } = [];
}

public sealed record ParquetColumnSummary(string Path, string PhysicalType, string? LogicalType, bool Optional, long? NullCount, ParquetFigure NullCountFigure,
    string? Min, string? Max, ParquetFigure MinMaxFigure, List<string> Codecs, List<string> Encodings, long CompressedBytes, long UncompressedBytes, bool Readable, string? NotReadableReason);

public sealed record ParquetRowGroupSummary(int Index, long Rows, long TotalBytes, long CompressedBytes);

public sealed record ParquetOverview
{
    public Guid SessionId { get; init; }
    public string FileName { get; init; } = "";
    public long SizeBytes { get; init; }
    public string Sha256 { get; init; } = "";
    public int FormatVersion { get; init; }
    public string? CreatedBy { get; init; }
    public long Rows { get; init; }
    public List<ParquetRowGroupSummary> RowGroups { get; init; } = [];
    public List<ParquetColumnSummary> Columns { get; init; } = [];
    public ParquetSchemaNode Schema { get; init; } = new();
    /// <summary>SHA-256 over the schema (paths, physical and logical types, repetition): equal fingerprints = the same schema.</summary>
    public string SchemaFingerprint { get; init; } = "";
    public List<ParquetKeyValue> KeyValueMetadata { get; init; } = [];
    public List<string> Limitations { get; init; } = [];
    public DateTimeOffset OpenedAt { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
}

public sealed record ParquetKeyValue(string Key, string? Value);

public sealed record ParquetFilter(string Column, ParquetFilterOperator Operator, string? Value = null, string? Value2 = null, List<string>? Values = null);

public sealed record ParquetRowsRequest
{
    public long Offset { get; init; }
    public int Count { get; init; } = 50;
    public List<string> Columns { get; init; } = [];
    public List<ParquetFilter> Filters { get; init; } = [];
    /// <summary>False: filters apply to the requested page only. True: an explicit full scan of the file (bounded), counting every match.</summary>
    public bool FullScan { get; init; }
}

public sealed record ParquetRowsResult
{
    public List<string> Columns { get; init; } = [];
    /// <summary>Display values (type-aware formatting); null = null value.</summary>
    public List<List<string?>> Rows { get; init; } = [];
    /// <summary>1-based row numbers of the returned rows in the file.</summary>
    public List<long> RowNumbers { get; init; } = [];
    public long TotalRows { get; init; }
    public long ScannedRows { get; init; }
    /// <summary>Matching rows: exact for a full scan, page-only otherwise (never presented as the file's count).</summary>
    public long? MatchingRows { get; init; }
    public string Scope { get; init; } = "";
    public bool Truncated { get; init; }
}

public sealed record ParquetColumnProfile(string Column, long Rows, long Nulls, long NonNull, long? Distinct, string DistinctFigure, string? Min, string? Max, double? Mean, string Note);

public sealed record ParquetProfile(List<ParquetColumnProfile> Columns, long ScannedRows, string Scope, double Seconds);

public sealed record ParquetRule
{
    public string Id { get; init; } = "";
    public ParquetRuleKind Kind { get; init; }
    public string? Column { get; init; }
    /// <summary>TypeIs: physical or logical type; RecordCount: expected count; AllowedValues: values; Range: Min / Max.</summary>
    public string? Expected { get; init; }
    public string? Min { get; init; }
    public string? Max { get; init; }
    public List<string> Values { get; init; } = [];
}

public sealed record ParquetRuleSet
{
    public string Name { get; init; } = "";
    public int Version { get; init; } = 1;
    public List<ParquetRule> Rules { get; init; } = [];
}

public sealed record ParquetRuleResult(string RuleId, ParquetRuleKind Kind, string? Column, ParquetRuleOutcome Outcome, string Expected, string Actual, string Scope, long RowsExamined,
    List<long> FailingRowNumbers, string Explanation);

public sealed record ParquetValidation(string RuleSetName, int RuleSetVersion, List<ParquetRuleResult> Results, long RowsExamined, string Scope);

public sealed record ParquetSchemaChange(string Path, string Change, string? Baseline, string? Current);

public sealed record ParquetRecordDiff(string Key, ParquetRecordChange Change, List<string> ChangedFields);

public sealed record ParquetComparison
{
    public Guid BaselineSessionId { get; init; }
    public Guid CurrentSessionId { get; init; }
    public string BaselineFile { get; init; } = "";
    public string CurrentFile { get; init; } = "";
    public List<ParquetSchemaChange> SchemaChanges { get; init; } = [];
    public long BaselineRows { get; init; }
    public long CurrentRows { get; init; }
    public string? KeyColumn { get; init; }
    public Dictionary<string, long> RecordCounts { get; init; } = [];
    /// <summary>Bounded list of record differences (changed field names only; values are not shown unless permitted).</summary>
    public List<ParquetRecordDiff> Records { get; init; } = [];
    public long DuplicateKeysBaseline { get; init; }
    public long DuplicateKeysCurrent { get; init; }
    public List<string> Limitations { get; init; } = [];
}

/// <summary>What is kept after an inspection: metadata, fingerprints, rule results, comparison counts — never row values.</summary>
public sealed record ParquetInspectionEvidence
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string EnvironmentId { get; init; } = "";
    public string FileName { get; init; } = "";
    public string Sha256 { get; init; } = "";
    public long SizeBytes { get; init; }
    public long Rows { get; init; }
    public int Columns { get; init; }
    public string SchemaFingerprint { get; init; } = "";
    public ParquetValidation? Validation { get; init; }
    public ParquetComparison? Comparison { get; init; }
    /// <summary>A run the tester declared this file belongs to (checked to exist in Execution Evidence); declared, not proven by content.</summary>
    public Guid? LinkedRunId { get; init; }
    public string? Note { get; init; }
    public DateTimeOffset SavedAt { get; init; }
    public string SavedBy { get; init; } = "";
}

public sealed record ParquetEvidenceRequest(Guid SessionId, string EnvironmentId, ParquetRuleSet? Rules, Guid? CompareWith, string? KeyColumn, Guid? LinkedRunId, string? Note);

public sealed record ParquetCompareRequest(Guid BaselineSessionId, Guid CurrentSessionId, string? KeyColumn);
