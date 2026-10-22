using System.Text.Json.Serialization;

namespace BirkNext.TestData;

// Test Data: any approved typed value, payload, file, entity reference, dataset or expected result used to prepare, execute or verify a
// test. These are the SAFE contracts a browser may see: an asset is described by alias and metadata; a sensitive value never appears here.

/// <summary>The nature of an asset, never its domain meaning.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TestDataKind { Scalar, Structured, File, EntityReference, Message, ExpectedResult, ReferenceData, Other }

/// <summary>The type of a scalar value (or of an expected scalar). <see cref="None"/> for non-scalar data.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TestDataValueType { None, String, Integer, Decimal, Boolean, Date, DateTime, Guid, Other }

/// <summary>Who may see the value. Only Public and Internal values are ever shown; Sensitive stays backend-only.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TestDataSensitivity { Public, Internal, Sensitive }

/// <summary>Whether the asset may be used. Approved for test use is not "valid" (an approved negative fixture is deliberately invalid).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TestDataApproval { Approved, Unapproved, Disabled, Expired }

/// <summary>Whether the asset is technically usable now. Independent of approval; Unknown is never read as validated.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TestDataValidation { Unknown, Validated, NeedsValidation, Invalid, Unavailable }

/// <summary>
/// The XSD an XML asset is checked against: a schema file inside a Source Analysis snapshot's retained XSD package. With
/// <see cref="SourceSnapshotId"/> the exact snapshot is used; without it the newest snapshot that contains <see cref="SchemaPath"/> is used and
/// reported. <see cref="SchemaFingerprint"/> (SHA-256 of that file), when set, must match or the schema counts as unavailable.
/// </summary>
public sealed record TestDataSchemaReference(string SchemaPath, Guid? SourceSnapshotId = null, string? SchemaFingerprint = null,
    string? RootElement = null, string? Label = null);

/// <summary>Schema validity of an XML asset. Independent of approval: an approved negative fixture is expected to be Invalid.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TestDataSchemaStatus { Validated, Invalid, SchemaUnavailable, DependencyUnresolved, NotEvaluated }

/// <summary>Result of validating an XML asset with the shared XSD validator. Issues never contain values from the XML.</summary>
public sealed record TestDataSchemaCheck(TestDataSchemaStatus Status, string Schema, string Detail, List<string> Issues,
    Guid? SourceSnapshotId = null, string? SchemaFingerprint = null, string? SchemaVersion = null)
{
    /// <summary>Declared rules from the shared contract evidence ("Derived from contract …: 12 required field(s) · 3 allowed-value list(s)").</summary>
    public string? ContractRules { get; init; }
}

/// <summary>One asset as the browser sees it: alias and metadata. <see cref="DisplayValue"/> is set only for a non-sensitive scalar.</summary>
public sealed record TestDataAssetSummary
{
    public string Id { get; init; } = "";
    public string Alias { get; init; } = "";
    public string? Label { get; init; }
    public TestDataKind Kind { get; init; }
    public TestDataValueType ValueType { get; init; }
    public string? MediaType { get; init; }
    /// <summary>For an entity reference: what it refers to (person, organization, institution …). Declared by the provider.</summary>
    public string? EntityType { get; init; }
    /// <summary>Null when the asset applies to any environment.</summary>
    public string? Environment { get; init; }
    public TestDataApproval Approval { get; init; }
    public TestDataValidation Validation { get; init; }
    public string? ValidationDetail { get; init; }
    /// <summary>Why the asset is not approved (for example a configuration problem). Approval and validation are independent facts.</summary>
    public string? ApprovalDetail { get; init; }
    public List<string> Capabilities { get; init; } = [];
    public TestDataSensitivity Sensitivity { get; init; }
    public string Source { get; init; } = "";
    /// <summary>The provider id behind <see cref="Source"/>, so the browser can relate an asset to its source card.</summary>
    public string SourceId { get; init; } = "";
    /// <summary>Where the asset came from (source identity, snapshot, schema fingerprint, origin) — labels and identifiers only, never values.</summary>
    public List<TestDataFact> Provenance { get; init; } = [];
    public bool ValueAvailable { get; init; }
    public bool IsDefault { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    /// <summary>For an expected result: the expectation is that nothing exists (for example "no record").</summary>
    public bool ExpectsAbsence { get; init; }
    /// <summary>SHA-256 of a structured or file value, so a run can say exactly which fixture it used. Never the content.</summary>
    public string? Fingerprint { get; init; }
    public string? DisplayValue { get; init; }
    /// <summary>The XSD this XML asset is validated against, when declared.</summary>
    public TestDataSchemaReference? Schema { get; init; }
    /// <summary>Shared-validator result for <see cref="Schema"/>; null until checked or when no schema is declared.</summary>
    public TestDataSchemaCheck? SchemaCheck { get; init; }
}

public sealed record TestDataRoleBinding(string Role, string AssetAlias);

/// <summary>A group of heterogeneous assets for one purpose, bound to consumer-defined roles.</summary>
public sealed record TestDataSetSummary
{
    public string Id { get; init; } = "";
    public string Alias { get; init; } = "";
    public string? Label { get; init; }
    public string? Environment { get; init; }
    public TestDataApproval Approval { get; init; }
    public List<string> Capabilities { get; init; } = [];
    public List<TestDataRoleBinding> Bindings { get; init; } = [];
    public string Source { get; init; } = "";
}

/// <summary>One labelled provenance fact (for example "Source snapshot" → an id). Never a test data value.</summary>
public sealed record TestDataFact(string Label, string Value);

/// <summary>
/// A source's state as a fact, not a judgement: Empty and NotConfigured are not failures. Unavailable means the provider could not be
/// read; its problem is scoped to that source and never hides the others.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TestDataSourceStatus { Available, Empty, NotConfigured, Unavailable }

/// <summary>A source of test data. <see cref="Detail"/> carries provider-specific explanation that the generic page shows only on request.</summary>
public sealed record TestDataSourceSummary(string Id, string Label, string Description, int AssetCount, int SetCount,
    TestDataSourceStatus Status = TestDataSourceStatus.Available, string? Detail = null, string? Problem = null)
{
    /// <summary>The provider's own wording for its state (e.g. "Registered fixtures"), or null for the generic label. Never replaces Unavailable.</summary>
    public string? StatusLabel { get; init; }
}

public sealed record TestDataCatalogView(List<TestDataAssetSummary> Assets, List<TestDataSetSummary> Sets, List<TestDataSourceSummary> Sources);

/// <summary>An asset with a safe preview of a non-sensitive structured value (bounded), or why none is shown.</summary>
public sealed record TestDataAssetDetail(TestDataAssetSummary Asset, string? Preview, string PreviewNote);

/// <summary>
/// What a consumer needs for one role. Every filter is optional except the role and kind. <see cref="Environment"/> null: any environment;
/// otherwise assets for that environment, plus environment-agnostic ones when <see cref="AllowEnvironmentAgnostic"/>.
/// </summary>
public sealed record TestDataRequirement
{
    public string Role { get; init; } = "";
    public TestDataKind Kind { get; init; }
    public TestDataValueType? ValueType { get; init; }
    public string? MediaType { get; init; }
    public string? EntityType { get; init; }
    public List<string> Capabilities { get; init; } = [];
    public string? Environment { get; init; }
    public bool AllowEnvironmentAgnostic { get; init; } = true;
    public bool RequireValidated { get; init; }
    public bool Optional { get; init; }
    /// <summary>Only assets from these sources (provider ids), e.g. when only that provider's executor can use the value. Empty: any source.</summary>
    public List<string> SourceIds { get; init; } = [];
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TestDataConstraintKind { Distinct }

/// <summary>A simple explicit constraint between roles. <see cref="TestDataConstraintKind.Distinct"/>: the roles' values must differ.</summary>
public sealed record TestDataConstraint(TestDataConstraintKind Kind, List<string> Roles);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TestDataResolutionState { Resolved, Selected, Ambiguous, Unresolved }

/// <summary>A role's outcome: the chosen alias (never a value), the candidate aliases when ambiguous, and why.</summary>
public sealed record TestDataRoleResolution(string Role, TestDataResolutionState State, string? AssetAlias, List<string> Candidates, string Reason, string? Source,
    bool Optional = false);

public sealed record TestDataResolutionResult(List<TestDataRoleResolution> Roles, List<string> ConstraintProblems, string? DataSetAlias)
{
    /// <summary>Every required role resolved (by itself or by an explicit choice) and no constraint broken.</summary>
    public bool Satisfied => ConstraintProblems.Count == 0
        && Roles.All(role => role.Optional || role.State is TestDataResolutionState.Resolved or TestDataResolutionState.Selected);

    public TestDataRoleResolution? Role(string role) => Roles.FirstOrDefault(item => string.Equals(item.Role, role, StringComparison.Ordinal));
}
