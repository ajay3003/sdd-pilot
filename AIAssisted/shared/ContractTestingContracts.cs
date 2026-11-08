using System.Text.Json.Serialization;
using BirkNext.ExecutionEvidence;
using BirkNext.SourceDomains;

namespace BirkNext.ContractTesting;

// Contract-Driven Testing: an imported API contract (immutable versions in one registry, parsed into the shared SourceContract model) →
// operations with a stable identity → draft tests → Test Data bound by pinned version → contract-aware readiness → a run through the existing
// Execution Evidence service (its safety gates unchanged) → per-test results read back from Execution Evidence → a new contract version → which
// tests it affects. Nothing here executes on import, on opening a contract or on opening a result.

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ApiContractProtocol { Rest, GraphQl }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ContractVersionSource { Upload, Url, SourceSnapshot }

/// <summary>Stable identities. An operation is <c>"METHOD /path-template"</c> (REST) inside one contract; a reference pins the contract version.</summary>
public static class ContractIdentity
{
    /// <summary>"children-api@v2" — the run's contract reference.</summary>
    public static string ContractRef(string key, int version) => $"{key}@v{version}";
    /// <summary>"children-api@v2#GET /children/{id}" — Execution Evidence <see cref="ExecutionCallDefinition.OperationRef"/>.</summary>
    public static string OperationRef(string key, int version, string operation) => $"{ContractRef(key, version)}#{operation}";
    /// <summary>"contract-test:{id}@r3" — Execution Evidence <see cref="ExecutionCallDefinition.DefinitionVersion"/> of one test revision.</summary>
    public static string DefinitionRef(Guid testId, int revision) => $"{DefinitionPrefix(testId)}{revision}";
    public static string DefinitionPrefix(Guid testId) => $"contract-test:{testId:N}@r";
}

public sealed record ApiContractSummary(Guid Id, string Key, string Name, ApiContractProtocol Protocol, int LatestVersion, int OperationCount, int TestCount,
    DateTimeOffset UpdatedAt);

/// <summary>One immutable version: what was imported (fingerprint, source, format) and how far it could be read.</summary>
public sealed record ApiContractVersionInfo(int Version, string Sha256, ContractVersionSource Source, string SourceLabel, string Format, DateTimeOffset ImportedAt,
    int OperationCount, string? DocumentVersion, DomainSupport ParseSupport, List<string> Limitations)
{
    /// <summary>Source-backed versions: the Source Analysis snapshot and the contract file inside it.</summary>
    public Guid? SourceSnapshotId { get; init; }
    public string? SourcePath { get; init; }
    /// <summary>False for source-backed versions: the snapshot keeps the parsed contract, not the document text, so full schema validation is not available.</summary>
    public bool HasDocument { get; init; } = true;
}

/// <summary>A REST or GraphQL contract a Source Analysis snapshot found, and whether the store already has it.</summary>
public sealed record SourceContractCandidate(Guid SnapshotId, DateTimeOffset AnalyzedAt, string Archive, string? Commit, string ContractId, string Name, string Type, string File,
    int OperationCount, string? ImportedAs);

public sealed record SourceContractImportRequest(Guid SnapshotId, string SourceContractId, string? ContractKey, string? Name);

public sealed record ApiContractDetail(ApiContractSummary Contract, List<ApiContractVersionInfo> Versions, int Version, SourceContract Document);

/// <summary>Import a document as a new contract, or as the next version of <see cref="ContractKey"/>. Exactly one of <see cref="Content"/> (an
/// uploaded file's text) or <see cref="Url"/> (fetched under the contract-source policy).</summary>
public sealed record ContractImportRequest
{
    public string? ContractKey { get; init; }
    public string? Name { get; init; }
    public string FileName { get; init; } = "";
    public string? Content { get; init; }
    public string? Url { get; init; }
}

public sealed record ContractImportResult(bool Imported, bool Unchanged, string? Key, int? Version, string Message, List<string> Problems);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ContractTestKind
{
    Positive, MissingRequiredInput,
    // Negative proposals (generated on request, never run automatically)
    InvalidType, InvalidEnum, NullNotAllowed, EmptyValue, BoundaryViolation, InvalidContentType, InvalidShape, MissingAuthentication, UnsupportedMethod,
    UnknownInputField, InvalidListShape, InvalidSelection,
}

/// <summary>A value the test needs: where it goes and whether the contract requires it. Values come only from the bound Test Data.</summary>
public sealed record ContractTestInput(string Name, string In, bool Required, string? Type);

/// <summary>The Test Data a test uses: a dataset (or key-value collection) pinned to one version. Runs use exactly this version.</summary>
public sealed record ContractTestDataBinding(string Alias, int Version);

/// <summary>A test generated from one operation of one contract version. <see cref="Template"/> holds <c>{{dataset.name}}</c> placeholders;
/// it never holds a credential or a resolved Test Data value.</summary>
public sealed record ContractTestDefinition
{
    public Guid Id { get; init; }
    public string ContractKey { get; init; } = "";
    /// <summary>The contract version the test was generated from or last reviewed against.</summary>
    public int ContractVersion { get; init; }
    public string Operation { get; init; } = "";
    public string Name { get; init; } = "";
    public ContractTestKind Kind { get; init; }
    public int Revision { get; init; } = 1;
    public ExecutionCallDefinition Template { get; init; } = new();
    public List<ContractTestInput> Inputs { get; init; } = [];
    public ContractTestDataBinding? Binding { get; init; }
    /// <summary>Why the generator produced it and what it expects, in plain words.</summary>
    public string Rationale { get; init; } = "";
    /// <summary>The tester's own description (free text).</summary>
    public string? Description { get; init; }
    /// <summary>Generated tests start as Draft. Retired tests are kept with their history but never run.</summary>
    public ContractTestReviewState ReviewState { get; init; } = ContractTestReviewState.Draft;
    /// <summary>Which input or aspect a negative variant targets ("age", "status:max"); empty for the operation-level tests.</summary>
    public string Variant { get; init; } = "";
    /// <summary>A MissingAuthentication proposal is sent without the contract's authentication (never another credential).</summary>
    public bool WithoutAuthentication { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>A dataset offered by the picker for one test: its approved version, what it provides and which required values it lacks.</summary>
public sealed record ContractDatasetOption(string Alias, string? Label, string Kind, int? ApprovedVersion, int LatestVersion, bool Compatible,
    List<string> Provides, List<string> MissingRequired, List<string> MissingOptional, string? Environment, string? Problem);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ReadinessItemState { Ok, Warning, Missing, Blocked }

public sealed record ContractReadinessItem(string Area, ReadinessItemState State, string Text);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ContractTestReadinessState { Ready, NotReady, Blocked }

public sealed record ContractTestReadiness(ContractTestReadinessState State, List<ContractReadinessItem> Items, List<string> MissingValues);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ContractTestImpactLevel { Unaffected, Review, NeedsUpdate, Broken }

/// <summary>What a newer contract version means for one test (from the test's version to the latest).</summary>
public sealed record ContractTestImpact(Guid TestId, string TestName, string Operation, int FromVersion, int ToVersion, ContractTestImpactLevel Level, List<string> Reasons);

/// <summary>The latest Execution Evidence call of a test (any revision): read from Execution Evidence, never a second result store.</summary>
public sealed record ContractTestLastResult(Guid RunId, Guid CallId, ExecutionState State, int? HttpStatus, DateTimeOffset At, string? DefinitionRef, string? OperationRef,
    List<string> TestDataRefs);

public sealed record ContractTestView(ContractTestDefinition Test, ContractTestReadiness Readiness, ContractTestImpact? Impact, ContractTestLastResult? Last);

/// <summary>The environment a readiness check or run is for: the selected Target Environment (inputs to the backend safety policy).</summary>
public sealed record ContractTestEnvironment
{
    public string EnvironmentId { get; init; } = "";
    public string TargetUrl { get; init; } = "";
    public string EnvironmentType { get; init; } = "";
    public bool IsProduction { get; init; }
    public string? ProfileId { get; init; }
    public string? ContextFingerprint { get; init; }
}

public sealed record ContractTestRunRequest
{
    public List<Guid> TestIds { get; init; } = [];
    public ContractTestEnvironment Environment { get; init; } = new();
    public bool ConfirmMutatingCalls { get; init; }
    public string? Name { get; init; }
    /// <summary>Optional path prefix of the API inside the Target Environment ("/api/v1"): relative only, never a host.</summary>
    public string? BasePath { get; init; }
    /// <summary>Optional GraphQL endpoint path override ("/api/graphql"); otherwise the contract's binding, else "/graphql".</summary>
    public string? GraphQlPath { get; init; }
}

public sealed record ContractTestRunResult(ExecutionRunStartResult? Run, List<string> NotRun);

public sealed record ContractVersionDiff(string Key, int FromVersion, int ToVersion, List<SourceEvidenceChange> Changes, List<ContractTestImpact> Impacts);

public sealed record ContractTestBindRequest(string Alias, int Version);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ContractTestReviewState { Draft, Reviewed, Approved, Retired }

/// <summary>An edit of a test: the whole editable content. <see cref="ExpectedRevision"/> is the revision the editor started from; a mismatch is a
/// conflict and nothing is saved. Every successful edit is a new immutable revision.</summary>
public sealed record ContractTestEdit
{
    public int ExpectedRevision { get; init; }
    public string Name { get; init; } = "";
    public string? Description { get; init; }
    public ExecutionCallDefinition Template { get; init; } = new();
    public ContractTestReviewState ReviewState { get; init; }
    public string? ChangeNote { get; init; }
}

public sealed record ContractTestSaveResult(bool Saved, bool Conflict, ContractTestDefinition? Test, string? Message, List<string> Problems);

public sealed record ContractTestRevisionInfo(int Revision, DateTimeOffset CreatedAt, string? ChangeNote, string Name, ContractTestReviewState ReviewState, int ContractVersion,
    string? Binding);

public sealed record ContractTestRevisionDiff(Guid TestId, int From, int To, List<string> Differences);

/// <summary>
/// How one contract is reached and authenticated in one Target Environment. The Target Environment's origin stays authoritative: the binding adds
/// only a path inside it (REST base path, GraphQL endpoint path) and the authentication the tests of this contract use there.
/// </summary>
public sealed record ContractEndpointBinding(string ContractKey, string EnvironmentId, string? BasePath, string? GraphQlPath, ExecutionAuthenticationKind Authentication,
    DateTimeOffset UpdatedAt);

public sealed record ContractEndpointBindingSave(string EnvironmentId, string? BasePath, string? GraphQlPath, ExecutionAuthenticationKind Authentication);

/// <summary>A base path the contract's declared servers suggest (path only — a declared host is never used).</summary>
public sealed record ContractServerProposal(string Server, string BasePath);
