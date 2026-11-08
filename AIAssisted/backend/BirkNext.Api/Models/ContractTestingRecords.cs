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
}

/// <summary>A contract test definition (ContractTestDefinition as JSON) with its searchable columns.</summary>
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
