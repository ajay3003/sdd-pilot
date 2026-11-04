using System.Text.Json.Serialization;

namespace BirkNext.TestData;

// Test Data: any approved typed value, payload, file, entity reference, dataset or expected result used to prepare, execute or verify a
// test. These are the SAFE contracts a browser may see: an asset is described by alias and metadata; a sensitive value never appears here.

/// <summary>The nature of an asset, never its domain meaning.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TestDataKind { Scalar, Structured, File, EntityReference, Message, ExpectedResult, ReferenceData, Other }

/// <summary>
/// What shape of data a consumer needs (and an asset offers): the five reusable data types every consumer (Performance, API Quality Review,
/// IQR, Schema Studio, E2E) speaks. Derived from the asset's kind and media type — one generic model, not five stores.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TestDataDataType { SimpleValues, JsonPayload, XmlPayload, FileFixture, ExpectedResult }

public static class TestDataDataTypes
{
    public static string Label(TestDataDataType type) => type switch
    {
        TestDataDataType.SimpleValues => "Simple values / parameters",
        TestDataDataType.JsonPayload => "JSON payload",
        TestDataDataType.XmlPayload => "XML payload",
        TestDataDataType.FileFixture => "File / binary fixture",
        _ => "Expected result / reference data",
    };

    /// <summary>The data type an asset offers (an expected result is never an input payload).</summary>
    public static TestDataDataType Of(TestDataKind kind, string? mediaType) => kind switch
    {
        TestDataKind.File => TestDataDataType.FileFixture,
        TestDataKind.ExpectedResult or TestDataKind.ReferenceData => TestDataDataType.ExpectedResult,
        TestDataKind.Structured or TestDataKind.Message => ForMediaType(mediaType) is var t && t == TestDataDataType.SimpleValues ? TestDataDataType.JsonPayload : t,
        _ => TestDataDataType.SimpleValues,
    };

    public static TestDataDataType Of(TestDataAssetSummary asset) => Of(asset.Kind, asset.MediaType);

    /// <summary>The data type a request body of this media type needs: JSON, XML, multipart/binary = file; anything else is simple values.</summary>
    public static TestDataDataType ForMediaType(string? mediaType)
    {
        var media = (mediaType ?? "").Split(';')[0].Trim().ToLowerInvariant();
        return media.Contains("json") ? TestDataDataType.JsonPayload
            : media.Contains("xml") ? TestDataDataType.XmlPayload
            : media.StartsWith("multipart/", StringComparison.Ordinal) || media == "application/octet-stream" || media.StartsWith("image/", StringComparison.Ordinal)
                || media == "application/pdf" ? TestDataDataType.FileFixture
            : TestDataDataType.SimpleValues;
    }
}

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
    /// <summary>Where the server found the XSD: a stored Source Analysis snapshot, or an explicitly associated schema artifact. Null: none found.</summary>
    public TestDataSchemaOrigin? Origin { get; init; }
    /// <summary>Explicit association id when <see cref="Origin"/> is <see cref="TestDataSchemaOrigin.ExplicitAssociation"/>.</summary>
    public string? AssociationId { get; init; }
    public string? TargetNamespace { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TestDataSchemaOrigin { SourceSnapshot, ExplicitAssociation }

/// <summary>
/// How much a validation result can be trusted. Only the two Server* levels mean the backend itself validated the XML against an XSD it holds;
/// a client-reported "Valid" is never shown or counted as server-confirmed.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TestDataValidationTrust
{
    /// <summary>The backend validated against the XSD retained in a stored Source Analysis snapshot (pinned or unambiguous, fingerprint-checked).</summary>
    ServerVerifiedSchema,
    /// <summary>The backend validated against an XSD a user explicitly associated (stored server-side with its hash).</summary>
    ServerExplicitSchemaReference,
    /// <summary>Only the browser reported a result when the XML was generated; the backend could not validate it.</summary>
    ClientReportedOnly,
    /// <summary>No XSD is available to the backend and nothing was reported.</summary>
    SchemaUnavailable,
    /// <summary>The XML is invalid against its XSD (server-checked, or reported invalid at generation).</summary>
    ValidationFailed,
    /// <summary>Not an XML asset, no value available, or no schema declared.</summary>
    NotPerformed,
}

/// <summary>One persisted validation of one asset version: the exact schema identity, engine and result. Never the XML value.</summary>
public sealed record TestDataValidationEvidence(
    TestDataValidationTrust Trust, TestDataSchemaStatus? Result, int? AssetVersion, string? SchemaPath, Guid? SourceSnapshotId, string? AssociationId,
    string? SchemaFingerprint, string? TargetNamespace, string? DeclaredVersion, string Engine, string EngineVersion, DateTimeOffset ValidatedAt, string Detail);

public static class TestDataValidationTrusts
{
    public const string Engine = "BirkNext.Schemas.XmlSchemaPackageAnalyzer", EngineVersion = "1";

    /// <summary>
    /// Trust from the server check and the browser's recorded result. The server result always wins; a recorded "Valid" without a server check
    /// is ClientReportedOnly (never verified); a recorded failure is ValidationFailed.
    /// </summary>
    public static TestDataValidationTrust Of(TestDataSchemaCheck? check, string? recordedValidation) => check?.Status switch
    {
        TestDataSchemaStatus.Validated => check.Origin == TestDataSchemaOrigin.ExplicitAssociation
            ? TestDataValidationTrust.ServerExplicitSchemaReference : TestDataValidationTrust.ServerVerifiedSchema,
        TestDataSchemaStatus.Invalid => TestDataValidationTrust.ValidationFailed,
        TestDataSchemaStatus.SchemaUnavailable or TestDataSchemaStatus.DependencyUnresolved => recordedValidation switch
        {
            null => TestDataValidationTrust.SchemaUnavailable,
            "Valid" => TestDataValidationTrust.ClientReportedOnly,
            _ => TestDataValidationTrust.ValidationFailed,
        },
        _ => recordedValidation switch
        {
            "Valid" => TestDataValidationTrust.ClientReportedOnly,
            null => TestDataValidationTrust.NotPerformed,
            _ => TestDataValidationTrust.ValidationFailed,
        },
    };

    public static string Label(TestDataValidationTrust trust) => trust switch
    {
        TestDataValidationTrust.ServerVerifiedSchema => "Server-validated (verified schema)",
        TestDataValidationTrust.ServerExplicitSchemaReference => "Server-validated (explicit schema reference)",
        TestDataValidationTrust.ClientReportedOnly => "Client-reported only",
        TestDataValidationTrust.SchemaUnavailable => "Schema unavailable",
        TestDataValidationTrust.ValidationFailed => "Validation failed",
        _ => "Validation not performed",
    };

    public static bool ServerConfirmed(TestDataValidationTrust trust) =>
        trust is TestDataValidationTrust.ServerVerifiedSchema or TestDataValidationTrust.ServerExplicitSchemaReference;
}

/// <summary>One asset as the browser sees it: alias and metadata. <see cref="DisplayValue"/> is set only for a non-sensitive scalar.</summary>
public sealed record TestDataAssetSummary
{
    /// <summary>Legacy, alias-derived id (<c>{provider}:{alias}</c>): kept for compatibility; it changes when the alias is renamed.</summary>
    public string Id { get; init; } = "";
    /// <summary>Immutable TestDataAssetId: generated once, independent of alias and provider label; what new references store.</summary>
    public string? AssetId { get; init; }
    /// <summary>Aliases this asset was known by before (historical lookup); never re-used by another asset's history.</summary>
    public List<string>? PreviousAliases { get; init; }
    /// <summary>Set when the legacy id could not be mapped unambiguously (several assets shared it): kept separate, needs review.</summary>
    public string? IdentityReview { get; init; }
    /// <summary>Trust of the current version's validation (server-confirmed vs client-reported vs unavailable).</summary>
    public TestDataValidationTrust? ValidationTrust { get; init; }
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

    // ── Catalog facts (set by the catalog view; null in summaries built before them) ─────────────────────────────────────────────
    /// <summary>The asset-level data type (JSON payload, simple values, XML payload, file fixture, expected result).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public TestDataDataType? DataType { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public TestDataAssetStatus? Status { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public TestDataContractStatus? ContractStatus { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? ContractNote { get; init; }
    /// <summary>A contract/schema reference for non-XSD assets (OpenAPI schema, GraphQL input type, data model entity …).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? SchemaRef { get; init; }
    /// <summary>Safe structure: field / parameter names (and types), never values.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<string>? Fields { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<string>? Tags { get; init; }
    /// <summary>How many consumers reference the asset (explicit usage references).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int Consumers { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public DateTimeOffset? ApprovedAt { get; init; }
    /// <summary>The fingerprint the approval was given for; a different current fingerprint means the asset changed after approval.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? ApprovedFingerprint { get; init; }
    /// <summary>The source candidate this asset was registered from (lineage), when it was.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? CandidateId { get; init; }
    /// <summary>The asset can be approved or deprecated in Test Data (its source does not own approval exclusively).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool Reviewable { get; init; }
    /// <summary>Who owns the asset (declared by its provider). Decides whether Delete and Maintenance reset may remove it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public TestDataOwnership Ownership { get; init; }
    [JsonIgnore] public bool Deletable => TestDataOwnerships.Deletable(Ownership) && ProtectionKind is null;
    [JsonIgnore] public bool ProtectedDefault => Ownership == TestDataOwnership.ProtectedDefault;
    /// <summary>Persisted protection (<see cref="TestDataProtectionKinds"/>): a protected asset is never deleted and survives Maintenance reset with
    /// its current content. Protection is not approval, and protected content still evolves through its normal version/draft flow.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? ProtectionKind { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? ProtectionReason { get; init; }
    [JsonIgnore] public bool IsProtected => ProtectedDefault || ProtectionKind is not null;
    /// <summary>"Protected default" (a seeded scalar default) or "Protected fixture" (a canonical payload fixture).</summary>
    [JsonIgnore] public string? ProtectionLabel => ProtectionKind == TestDataProtectionKinds.CanonicalFixture ? "Protected fixture" : ProtectedDefault ? "Protected default" : null;
    /// <summary>Why the asset cannot be deleted in Test Data, or null when it can (ownership and protection, never the alias).</summary>
    [JsonIgnore] public string? WhyNotDeletable => TestDataOwnerships.WhyNotDeletable(Ownership, ProtectionKind);
    /// <summary>Safe change marker of the value (incremented on each edit/restore; never derived from the value). Generated fixtures record it per
    /// mapped alias so a later value change is detected without storing or hashing the value.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int ValueVersion { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public DateTimeOffset? ValueUpdatedAt { get; init; }
    /// <summary>A protected default whose value was edited ("Protected default · Modified").</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool ValueModified { get; init; }
    /// <summary>The actual value can be edited in Test Data.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool ValueEditable { get; init; }
    /// <summary>What the value represents (<see cref="TestDataSemanticRoles"/>): declared by the provider or the registration, never guessed from a name.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? SemanticRole { get; init; }
    /// <summary>Where the asset is relevant (<see cref="TestDataContexts"/> ids) — metadata for suggestions and filters, never a consumer binding.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<string>? AppliesTo { get; init; }
    /// <summary>The role cannot be changed in Test Data (declared by a protected default's source).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool MetadataFixed { get; init; }

    // ── Payload versions (one alias, one stable id; content evolves through versions) ───────────────────────────────────────────
    /// <summary>The version this summary describes: the current approved version, else the latest. Null for unversioned assets.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? Version { get; init; }
    /// <summary>How many versions exist (current approved + older + a draft), when the source versions its content.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int VersionCount { get; init; }
    /// <summary>A newer, not yet approved version (a regenerated draft). Consumers keep using <see cref="Version"/> until it is approved.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? DraftVersion { get; init; }
    /// <summary>The draft's local XSD validation result at generation ("Valid" / "Invalid"), when recorded.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? DraftValidation { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public DateTimeOffset? DraftCreatedAt { get; init; }
    /// <summary>When the described version was generated/registered.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public DateTimeOffset? VersionCreatedAt { get; init; }
    /// <summary>"Regeneration recommended: …" — an input the payload was generated from changed since (a mapped asset's value). Never rewritten.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Staleness { get; init; }
}

/// <summary>
/// What a Test Data value represents. Extensible (a string id): providers and registrations may declare others; matching is on the exact role,
/// so a Child is never offered for a PersonId requirement only because both are identifiers.
/// </summary>
public static class TestDataSemanticRoles
{
    public const string Reporter = "Reporter", Child = "Child", PersonId = "PersonId", MessageId = "MessageId", CaseId = "CaseId", Payload = "Payload",
        ReportXml = "ReportXml", GenericParameter = "GenericParameter";
    public static readonly IReadOnlyList<string> All = [Reporter, Child, PersonId, MessageId, CaseId, Payload, ReportXml, GenericParameter];

    public static string Label(string? role) => role switch
    {
        null or "" => "Not specified",
        PersonId => "Person ID",
        MessageId => "Message ID",
        CaseId => "Case ID",
        ReportXml => "Report XML",
        GenericParameter => "Generic parameter",
        _ => role,
    };
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

/// <summary>How ready a contract's XML fixture is for test use (Test Data readiness only, never runtime delivery).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
/// <summary>… RegenerationRequired: approved and schema-valid, but generated from Test Data values that changed since (its content is never
/// rewritten; a new draft version is generated and approved).</summary>
public enum TestDataXmlReadinessState { Missing, NotApproved, Ready, Outdated, Invalid, NotValidated, SchemaUnavailable, RegenerationRequired }

/// <summary>One saved XML fixture as the readiness rule needs it: approval, the schema check's status and the snapshot it was checked against.</summary>
public sealed record TestDataXmlFixtureFacts(string Alias, bool Approved, TestDataSchemaStatus? SchemaStatus, Guid? SchemaSnapshotId, bool InputsChanged = false);

/// <summary>
/// The one XML fixture readiness rule, shared by Test Data and its consumers (browser) and Setup Library preflight (backend), so a fixture
/// never reads Ready in one place and Invalid in another. Only approved fixtures count; a positive fixture is Ready when its schema check is
/// Validated against the current schema snapshot (when one is known). Outdated: validated against another snapshot. Invalid wins over
/// not-validated. Schema-valid is not business-valid.
/// </summary>
public static class TestDataXmlReadiness
{
    /// <summary>A positive XML fixture for the contract: XML media type, schema file (or label) named exactly the contract, not a negative test.</summary>
    public static bool IsFixtureFor(string? mediaType, TestDataSchemaReference? schema, IEnumerable<string> capabilities, string contract) =>
        string.Equals(mediaType, "application/xml", StringComparison.OrdinalIgnoreCase) && schema is not null
        && (string.Equals(Stem(schema.SchemaPath), contract, StringComparison.OrdinalIgnoreCase) || string.Equals(Stem(schema.Label), contract, StringComparison.OrdinalIgnoreCase))
        && !capabilities.Contains("negative-test", StringComparer.OrdinalIgnoreCase);

    private static string Stem(string? path) => path is { Length: > 0 } ? System.IO.Path.GetFileNameWithoutExtension(path.Replace('\\', '/').Split('/').Last()) : "";

    /// <summary>The state and the fixture it is about (the ready one, else the most relevant one); null fixture only when Missing.</summary>
    public static (TestDataXmlReadinessState State, TestDataXmlFixtureFacts? Fixture) Evaluate(IReadOnlyList<TestDataXmlFixtureFacts> fixtures, Guid? currentSnapshot = null)
    {
        if (fixtures.Count == 0) return (TestDataXmlReadinessState.Missing, null);
        var approved = fixtures.Where(f => f.Approved).ToList();
        if (approved.Count == 0) return (TestDataXmlReadinessState.NotApproved, fixtures[0]);
        var validated = approved.Where(f => f.SchemaStatus == TestDataSchemaStatus.Validated).ToList();
        // An approved fixture whose mapped Test Data changed since it was generated is not ready: it still holds the old values.
        var current = validated.Where(f => !f.InputsChanged).ToList();
        if (current.FirstOrDefault(f => currentSnapshot is null || f.SchemaSnapshotId is null || f.SchemaSnapshotId == currentSnapshot) is { } ready)
            return (TestDataXmlReadinessState.Ready, ready);
        if (current.Count > 0) return (TestDataXmlReadinessState.Outdated, current[0]);
        if (validated.Count > 0) return (TestDataXmlReadinessState.RegenerationRequired, validated[0]);
        if (approved.FirstOrDefault(f => f.SchemaStatus == TestDataSchemaStatus.Invalid) is { } invalid) return (TestDataXmlReadinessState.Invalid, invalid);
        if (approved.FirstOrDefault(f => f.SchemaStatus is TestDataSchemaStatus.SchemaUnavailable or TestDataSchemaStatus.DependencyUnresolved) is { } unavailable)
            return (TestDataXmlReadinessState.SchemaUnavailable, unavailable);
        return (TestDataXmlReadinessState.NotValidated, approved[0]);
    }
}
