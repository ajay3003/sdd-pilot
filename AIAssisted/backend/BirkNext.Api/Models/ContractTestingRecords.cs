namespace BirkNext.Api.Models;

/// <summary>An API contract in the contract registry: a stable key and name; its content lives in immutable versions.</summary>
public sealed class ApiContractRow
{
    public Guid Id { get; init; }
    public string Key { get; init; } = "";
    public string Name { get; set; } = "";
    public string Protocol { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; set; }
    /// <summary>Active, Deprecated or Archived (ContractLifecycle).</summary>
    public string Lifecycle { get; set; } = "Active";
}

/// <summary>One imported version of a contract. Never updated: a changed document is a new version; an identical one is not stored again.</summary>
public sealed class ApiContractVersionRow
{
    public Guid Id { get; init; }
    public Guid ContractId { get; init; }
    public int Version { get; init; }
    public string Sha256 { get; init; } = "";
    public string Source { get; init; } = "";
    public string SourceLabel { get; init; } = "";
    public string Format { get; init; } = "";
    /// <summary>The imported document as received (bounded); kept so a version can be re-read and compared.</summary>
    public string DocumentText { get; init; } = "";
    /// <summary>The parsed contract (shared SourceContract model) as JSON.</summary>
    public string ContractJson { get; init; } = "";
    public int OperationCount { get; init; }
    public DateTimeOffset ImportedAt { get; init; }
    /// <summary>Source-backed versions: the Source Analysis snapshot and the contract file path inside it (provenance; the snapshot is never changed).</summary>
    public Guid? SourceSnapshotId { get; init; }
    public string? SourcePath { get; init; }
    public bool Hidden { get; set; }
}

/// <summary>A contract test definition (ContractTestDefinition as JSON) with its searchable columns.</summary>
/// <summary>One immutable revision of a chain (the latest revision is the current chain; evidence names the revision it ran).</summary>
public sealed class ContractTestChainRow
{
    public Guid Id { get; init; }
    public Guid ChainId { get; init; }
    public int Revision { get; init; }
    public string Name { get; init; } = "";
    public string DocumentJson { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; }
}

public sealed class ContractTestRow
{
    public Guid Id { get; init; }
    public Guid ContractId { get; init; }
    public string Operation { get; set; } = "";
    public string Name { get; set; } = "";
    public string Kind { get; init; } = "";
    public int Revision { get; set; }
    public string DocumentJson { get; set; } = "";
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>A contract's endpoint path(s) and authentication in one Target Environment (the environment's origin stays authoritative).</summary>
public sealed class ContractEndpointBindingRow
{
    public Guid Id { get; init; }
    public Guid ContractId { get; init; }
    public string EnvironmentId { get; init; } = "";
    public string? BasePath { get; set; }
    public string? GraphQlPath { get; set; }
    public string Authentication { get; set; } = "None";
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>One immutable revision of a contract test (the full ContractTestDefinition as JSON). Never updated or deleted with the test's edits.</summary>
public sealed class ContractTestRevisionRow
{
    public Guid Id { get; init; }
    public Guid TestId { get; init; }
    public int Revision { get; init; }
    public string DocumentJson { get; init; } = "";
    public string? ChangeNote { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}
