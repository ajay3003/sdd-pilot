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
    /// <summary>The project (Source Analysis repository identity key) that owns the contract; null for uploaded/URL contracts and for legacy contracts
    /// whose project could not be resolved. Source paths are provenance, never the identity across projects.</summary>
    public string? ProjectKey { get; set; }
    /// <summary>Null = resolved (or new). "Unscoped" = not from Source Analysis (upload/URL). "NeedsReview" = a legacy contract whose versions point to
    /// different projects (or to snapshots that no longer exist): never continued automatically until a person assigns its project.</summary>
    public string? IdentityState { get; set; }
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
    /// <summary>The project (repository identity key) of the snapshot this version was imported from; null for uploads and unresolved legacy versions.</summary>
    public string? SourceProjectKey { get; set; }
}

/// <summary>
/// A recorded identity or configuration decision: a confirmed or rejected relocation match (who, when, on which evidence), a recorded source location
/// of an unchanged moved contract, a project assignment of a legacy contract, an applied bulk endpoint binding, a rejected chain suggestion. Never
/// updated or deleted: the history of how a contract's identity was decided stays traceable.
/// </summary>
public sealed class ContractReviewDecisionRow
{
    public Guid Id { get; init; }
    /// <summary>identity-confirm, identity-reject, location, project-assignment, binding, chain-reject.</summary>
    public string Kind { get; init; } = "";
    public Guid? ContractId { get; init; }
    public string? ProjectKey { get; init; }
    /// <summary>The subject: a source contract id (identity, location), an environment id (binding) or a suggestion id (chain).</summary>
    public string SubjectKey { get; init; } = "";
    public Guid? SnapshotId { get; init; }
    public string Detail { get; init; } = "";
    public string DecidedBy { get; init; } = "";
    public DateTimeOffset DecidedAt { get; init; }
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
