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
    /// <summary>"contract-chain:{id}@r2" — Execution Evidence <see cref="ExecutionRunSummary.ChainRef"/> of one chain revision.</summary>
    public static string ChainRef(Guid chainId, int revision) => $"contract-chain:{chainId:N}@r{revision}";
}

/// <summary>A chain step input taken from an earlier step's output: the test input <see cref="Input"/> receives output <see cref="Output"/> of step
/// <see cref="FromStep"/> (typed, at send time).</summary>
public sealed record ContractChainInput(string Input, string FromStep, string Output);

/// <summary>
/// One step of a chain: an existing contract test (its current revision), optionally a dataset row, the outputs it provides to later steps,
/// the inputs it takes from earlier steps and any further explicit dependencies. A step runs only when every step it depends on passed.
/// </summary>
public sealed record ContractChainStep
{
    public string StepId { get; init; } = "";
    public Guid TestId { get; init; }
    public string? RowId { get; init; }
    public List<OutputExtraction> Extract { get; init; } = [];
    public List<ContractChainInput> Inputs { get; init; } = [];
    public List<string> DependsOn { get; init; } = [];
}

/// <summary>A chain of contract tests (REST and/or GraphQL) run in dependency order as one Execution Evidence run. Every save is a new immutable revision.</summary>
public sealed record ContractTestChain
{
    public Guid Id { get; init; }
    public string Name { get; init; } = "";
    public string? Description { get; init; }
    public int Revision { get; init; } = 1;
    public List<ContractChainStep> Steps { get; init; } = [];
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed record ContractChainSave
{
    public string Name { get; init; } = "";
    public string? Description { get; init; }
    public List<ContractChainStep> Steps { get; init; } = [];
    /// <summary>The revision the editor started from (null = create); a mismatch is a conflict.</summary>
    public int? ExpectedRevision { get; init; }
}

public sealed record ContractChainSaveResult(ContractTestChain? Chain, List<string> Problems, bool Conflict = false);

public sealed record ContractChainRunRequest
{
    public ContractTestEnvironment Environment { get; init; } = new();
    public bool ConfirmMutatingCalls { get; init; }
    public string? Name { get; init; }
    public string? BasePath { get; init; }
    public string? GraphQlPath { get; init; }
    public int ReauthenticationWaitSeconds { get; init; }
    public bool StopOnFirstFailure { get; init; }
}

public sealed record ApiContractSummary(Guid Id, string Key, string Name, ApiContractProtocol Protocol, int LatestVersion, int OperationCount, int TestCount,
    DateTimeOffset UpdatedAt)
{
    public ContractLifecycle Lifecycle { get; init; }
    /// <summary>The owning project (Source Analysis repository identity key); null for uploaded/URL contracts and unresolved legacy contracts.</summary>
    public string? ProjectKey { get; init; }
    /// <summary>Null = resolved; "Unscoped" = upload/URL; "NeedsReview" = legacy contract whose project a person must assign.</summary>
    public string? IdentityState { get; init; }
    /// <summary>Top-level folder of the latest source-backed version's file (module/service); null for uploads.</summary>
    public string? Module { get; init; }
}

/// <summary>A project as Contract Testing scopes contracts: one repository identity, or several a person declared to be the same repository.</summary>
public sealed record ContractProject(string Key, string DisplayName, List<string> Keys, int Snapshots, int Contracts, Guid? LatestSnapshotId, DateTimeOffset? LatestAnalyzedAt);

public sealed record ContractProjectAssignment(string ProjectKey);

/// <summary>One immutable version: what was imported (fingerprint, source, format) and how far it could be read.</summary>
public sealed record ApiContractVersionInfo(int Version, string Sha256, ContractVersionSource Source, string SourceLabel, string Format, DateTimeOffset ImportedAt,
    int OperationCount, string? DocumentVersion, DomainSupport ParseSupport, List<string> Limitations)
{
    /// <summary>Source-backed versions: the Source Analysis snapshot and the contract file inside it.</summary>
    public Guid? SourceSnapshotId { get; init; }
    public string? SourcePath { get; init; }
    /// <summary>False for source-backed versions: the snapshot keeps the parsed contract, not the document text, so full schema validation is not available.</summary>
    public bool HasDocument { get; init; } = true;
    /// <summary>Hidden versions are kept (and still referenced by their tests and evidence) but not offered for new work.</summary>
    public bool Hidden { get; init; }
}

/// <summary>A REST or GraphQL contract a Source Analysis snapshot found, and whether the store already has it.</summary>
public sealed record SourceContractCandidate(Guid SnapshotId, DateTimeOffset AnalyzedAt, string Archive, string? Commit, string ContractId, string Name, string Type, string File,
    int OperationCount, string? ImportedAs)
{
    /// <summary>The top-level folder of <see cref="File"/> (the module/service it belongs to); "(root)" for a top-level file.</summary>
    public string Module { get; init; } = "";
    public ApiContractProtocol Protocol { get; init; }
    /// <summary>New: not in the registry. UpToDate: the registry's latest version is this exact evidence. Changed: importing adds the next version of
    /// <see cref="ContractKey"/>. Archived: the contract that continues it is archived (read-only).</summary>
    public SourceContractCandidateState State { get; init; }
    /// <summary>The registry contract that continues this source contract (imported earlier from any snapshot), and its latest version.</summary>
    public string? ContractKey { get; init; }
    public int? ContractVersion { get; init; }
    /// <summary>How many of the listed snapshots hold this source contract; only the newest is offered.</summary>
    public int Snapshots { get; init; } = 1;
    /// <summary>The snapshot retained the document text: the imported version gets full schema validation (otherwise structural only).</summary>
    public bool HasDocument { get; init; }
    /// <summary>The project the snapshot belongs to (repository identity key and display name).</summary>
    public string ProjectKey { get; init; } = "";
    public string ProjectName { get; init; } = "";
    public ContractIdentityClassification Identity { get; init; } = ContractIdentityClassification.NewContract;
    /// <summary>Existing contracts of the same project this source contract may continue (relocation candidates), with evidence.</summary>
    public List<ContractIdentityCandidate> Candidates { get; init; } = [];
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SourceContractCandidateState { New, UpToDate, Changed, Archived, Moved, NeedsReview }

/// <summary>
/// How a source contract relates to the project's existing contracts. SameContract / NewVersion: same source location. MovedUnchanged: the identical
/// document at a new location (recorded as a location of the existing contract). MovedChanged: one strong candidate (same protocol, most operations
/// shared) at a new location with changes — needs a person. PossibleMatch: a weaker single candidate — needs a person. Ambiguous: several candidates —
/// needs a person. NewContract: nothing in the project matches. Never across projects.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ContractIdentityClassification { SameContract, NewVersion, MovedUnchanged, MovedChanged, PossibleMatch, NewContract, Ambiguous }

/// <summary>An existing contract of the same project that a moved/renamed source contract may continue, with the evidence for and against.</summary>
public sealed record ContractIdentityCandidate(string Key, string Name, int LatestVersion, string? LatestSourcePath, bool SameDocument, bool SameTitle, int SharedOperations,
    int CandidateOperations, int SourceOperations, double OperationOverlap, List<string> Evidence);

/// <summary>A person's decision on a relocation candidate: confirm (continue <see cref="TargetKey"/>) or reject (with a target: not that one; without:
/// import as a new contract). Recorded with who and when; the source location history is kept on the versions.</summary>
public sealed record ContractIdentityDecisionRequest(Guid SnapshotId, string SourceContractId, string? TargetKey, bool Confirm);

public sealed record SourceContractImportRequest(Guid SnapshotId, string SourceContractId, string? ContractKey, string? Name);

public sealed record SourceContractRef(Guid SnapshotId, string SourceContractId);

/// <summary>Import several source contracts at once; optionally generate their tests and synthetic test data right after (nothing is ever sent).</summary>
public sealed record SourceContractBulkImportRequest
{
    public List<SourceContractRef> Items { get; init; } = [];
    public bool GenerateTests { get; init; } = true;
    public bool GenerateTestData { get; init; } = true;
    public string? EnvironmentId { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BulkImportOutcome { Imported, NewVersion, Unchanged, Failed, Moved, NeedsReview }

/// <summary>What happened to one source contract: the registry outcome, the tests created and the test data per state (operations).</summary>
public sealed record SourceContractBulkItem(string SourceContractId, string File, string Name, BulkImportOutcome Outcome, string? Key, int? Version, string Message)
{
    public int TestsCreated { get; init; }
    public int TestsExisting { get; init; }
    public int Unsupported { get; init; }
    public int DataBound { get; init; }
    public int DataNeedsReview { get; init; }
    public int DataUnresolved { get; init; }
    public int DataNoInputs { get; init; }
    public int DataFailed { get; init; }
}

public sealed record SourceContractBulkImportResult(List<SourceContractBulkItem> Items)
{
    public int Imported => Items.Count(i => i.Outcome is BulkImportOutcome.Imported or BulkImportOutcome.NewVersion);
    public int Unchanged => Items.Count(i => i.Outcome == BulkImportOutcome.Unchanged);
    public int Failed => Items.Count(i => i.Outcome == BulkImportOutcome.Failed);
    public int Moved => Items.Count(i => i.Outcome == BulkImportOutcome.Moved);
    public int NeedsReview => Items.Count(i => i.Outcome == BulkImportOutcome.NeedsReview);
}

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

public sealed record ContractImportResult(bool Imported, bool Unchanged, string? Key, int? Version, string Message, List<string> Problems)
{
    /// <summary>Not imported: the source contract may be a moved/renamed existing contract; a person decides (identity review).</summary>
    public bool NeedsReview { get; init; }
    /// <summary>The identical document at a new location: recorded as a location of the existing contract, no new version.</summary>
    public bool Moved { get; init; }
}

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
    List<string> Provides, List<string> MissingRequired, List<string> MissingOptional, string? Environment, string? Problem, int Rows = 0);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ReadinessItemState { Ok, Warning, Missing, Blocked }

public sealed record ContractReadinessItem(string Area, ReadinessItemState State, string Text);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ContractTestReadinessState { Ready, NotReady, Blocked }

public sealed record ContractTestReadiness(ContractTestReadinessState State, List<ContractReadinessItem> Items, List<string> MissingValues);

[JsonConverter(typeof(JsonStringEnumConverter))]
/// <summary>What a newer contract version means for a test, by evidence: NoEvidence (nothing the test sends, asserts or binds changed),
/// NeedsReview (a change whose effect cannot be decided from the contract: security, servers, deprecation, a changed parameter a negative test
/// targets), Potential (the test may now fail: a response it relies on changed, a value it sends is no longer declared), Definite (the test cannot
/// pass as it is: its operation is gone, it lacks a newly required input, it asserts or selects something removed, its expected code is gone).</summary>
public enum ContractTestImpactLevel { NoEvidence, NeedsReview, Potential, Definite }

/// <summary>A reviewable update of a test to the latest contract version: the proposed definition (not saved) and how it differs from the current
/// one. Applying it creates a new revision; the old revision and its executions stay bound to the old contract version.</summary>
public sealed record ContractTestMigrationProposal(Guid TestId, int FromVersion, int ToVersion, ContractTestDefinition Proposed, List<string> Differences, List<string> Notes);

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
    /// <summary>Attended runs: how long to wait for a renewed sign-in when the session expires mid-run (see <see cref="ExecutionRunRequest.ReauthenticationWaitSeconds"/>).</summary>
    public int ReauthenticationWaitSeconds { get; init; }
    /// <summary>Data-driven tests: run only these dataset row ids (empty = every row).</summary>
    public List<string> RowIds { get; init; } = [];
    /// <summary>At most this many logical calls (rows × tests); the rest is listed as not run, never silently dropped. Null = the run limit.</summary>
    public int? MaxExecutions { get; init; }
    /// <summary>Stop at the first failed, erroneous or blocked call (see <see cref="ExecutionRunRequest.StopOnFirstFailure"/>).</summary>
    public bool StopOnFirstFailure { get; init; }
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

/// <summary>Active: normal. Deprecated: still usable, flagged. Archived: kept with all versions, tests and evidence, but read-only (no import,
/// generation or runs). Archiving never deletes anything.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ContractLifecycle { Active, Deprecated, Archived }

/// <summary>Why a version is kept: tests (any revision), execution evidence, a source snapshot it was imported from, or being the latest version.
/// Only a version with no reference may be deleted, after an explicit confirmation.</summary>
public sealed record ContractVersionRetention(int Version, bool Hidden, bool Latest, int TestRevisions, int EvidenceCalls, bool SourceBacked, bool Deletable, List<string> References);

public sealed record ContractRetention(string Key, ContractLifecycle Lifecycle, List<ContractVersionRetention> Versions, bool ContractDeletable, string Policy);

/// <summary>One coverage figure, always with what it counts over and where the numbers come from. NotAssessed counts items that could not be judged.</summary>
public sealed record ContractCoverageMetric(string Id, string Label, int Numerator, int Denominator, string Scope, int NotAssessed, string EvidenceSource, string? Note = null)
{
    /// <summary>Definition: what is defined and prepared (tests, negatives, bound data) — never evidence that the API works. Execution: what
    /// documented execution evidence shows.</summary>
    public CoverageGroup Group { get; init; } = CoverageGroup.Execution;

    /// <summary>Nothing to count over (0/0), or an execution figure with no executed item: not assessed, never 0 % and never covered.</summary>
    [JsonIgnore]
    public bool Assessed => Denominator > 0 && (Group == CoverageGroup.Definition || NotAssessed < Denominator);
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CoverageGroup { Definition, Execution }

public sealed record ContractCoverageReport(string Key, int Version, ApiContractProtocol Protocol, List<ContractCoverageMetric> Metrics, DateTimeOffset ComputedAt);

// ── Bulk environment and authentication configuration ────────────────────────────────────────────────────────────────

/// <summary>Where a proposed base path comes from: kept as bound, the contract's declared servers (path part only), or one explicit path.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BulkBasePathMode { KeepExisting, FromContractServers, Explicit }

/// <summary>AUTHORITATIVE: a binding a tester already saved. CANDIDATE: one path the contract's declared servers suggest. AMBIGUOUS: several.
/// UNRESOLVED: no evidence (the environment root would be used).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EndpointEvidenceState { Authoritative, Candidate, Ambiguous, Unresolved }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BulkBindingStatus { Applicable, Unchanged, NeedsReview, Blocked, Applied, Skipped, Failed }

public sealed record EndpointCandidate(string BasePath, string Source, string Evidence);

/// <summary>Preview or apply of one environment's endpoint + authentication for several contracts of one project. Nothing is sent to any API.</summary>
public sealed record BulkBindingRequest
{
    public string ProjectKey { get; init; } = "";
    public ContractTestEnvironment Environment { get; init; } = new();
    public List<string> ContractKeys { get; init; } = [];
    public ExecutionAuthenticationKind Authentication { get; init; }
    public BulkBasePathMode BasePathMode { get; init; } = BulkBasePathMode.KeepExisting;
    public string? BasePath { get; init; }
    public string? GraphQlPath { get; init; }
}

/// <summary>One contract in the preview: evidence, existing and proposed binding, authentication requirement and credential, status with reasons.</summary>
public sealed record BulkBindingPreviewRow(string Key, string Name, ApiContractProtocol Protocol, string? Module, ContractEndpointBinding? Existing, List<EndpointCandidate> Candidates,
    EndpointEvidenceState Endpoint, string? ProposedBasePath, string? ProposedGraphQlPath, ExecutionAuthenticationKind ProposedAuthentication, List<string> RequiredSchemes,
    string Credential, BulkBindingStatus Status, List<string> Reasons)
{
    /// <summary>Applying changes an existing binding (needs an explicit overwrite confirmation).</summary>
    public bool Overwrites { get; init; }
}

public sealed record BulkBindingPreview(string EnvironmentId, string EnvironmentType, string TargetOrigin, bool EnvironmentAllowed, string? EnvironmentProblem, List<BulkBindingPreviewRow> Rows,
    List<string> Limitations);

/// <summary>A row the tester approved: the values seen in the preview, the binding's last change time then (concurrency), and an explicit overwrite.</summary>
public sealed record BulkBindingApplyItem(string Key, string? BasePath, string? GraphQlPath, ExecutionAuthenticationKind Authentication, DateTimeOffset? ExpectedUpdatedAt, bool ConfirmOverwrite);

public sealed record BulkBindingApplyRequest
{
    public string ProjectKey { get; init; } = "";
    public ContractTestEnvironment Environment { get; init; } = new();
    /// <summary>The tester confirms that this Target Environment is the approved one for these bindings.</summary>
    public bool ConfirmEnvironment { get; init; }
    public List<BulkBindingApplyItem> Items { get; init; } = [];
}

public sealed record BulkBindingApplyRow(string Key, BulkBindingStatus Status, string Message, ContractEndpointBinding? Binding);

public sealed record BulkBindingApplyResult(List<BulkBindingApplyRow> Rows)
{
    public int Applied => Rows.Count(r => r.Status == BulkBindingStatus.Applied);
    public int NotApplied => Rows.Count(r => r.Status != BulkBindingStatus.Applied && r.Status != BulkBindingStatus.Unchanged);
}
