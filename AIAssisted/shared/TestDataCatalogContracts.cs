using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace BirkNext.TestData;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// The authoritative reusable Test Data catalog. Test Data owns reusable assets (type, status, scope, provenance, schema linkage, safe
// metadata); consumers own how assets are used. Consumers are generic (string-typed: consumer type + stable consumer id + display name) —
// there is no list of known consumers and no consumer-specific logic here. Requirements come in from consumers; matching is side-effect
// free; a binding is an explicit usage reference (asset id + version), never a copy of the value.
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>An asset's lifecycle state. Provenance is never approval; approval is explicit and bound to the approved version.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TestDataAssetStatus { Draft, NeedsReview, Approved, Deprecated }

/// <summary>An asset's relationship to its declared schema/contract. Not "valid" because a content type or extension matches.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TestDataContractStatus { Valid, Compatible, NeedsReview, Invalid, NotAssessed, NotApplicable }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TestDataMatchState { Exact, Compatible, Possible, NoMatch, Conflict }

/// <summary>A consumer's need, as Test Data sees it. Resolved = an explicit usage reference binds an approved asset.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TestDataRequirementStatus { Resolved, Missing, NeedsReview }

/// <summary>A usage reference's state: the bound asset is still the approved version (Resolved), or something changed (NeedsReview).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TestDataUsageStatus { Resolved, NeedsReview }

/// <summary>Who needs or uses Test Data. Generic: <see cref="ConsumerType"/> is any word ("Review", "Test pack", "Journey", "Extension" …),
/// <see cref="ConsumerId"/> is the consumer's own stable id, <see cref="Parent"/> an optional display hierarchy ("Skolenærvær Testing › Rapportmottak").</summary>
public sealed record TestDataConsumer
{
    public string ConsumerType { get; init; } = "";
    public string ConsumerId { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string? Parent { get; init; }
    public string Key => $"{ConsumerType.Trim().ToLowerInvariant()}|{ConsumerId.Trim()}";
}

/// <summary>
/// An inbound need registered by a consumer: what shape of data, for which usage, in which environment, required or optional. Test Data
/// shows whether it is resolved; the consumer decides whether it is runnable.
/// </summary>
public sealed record ConsumerTestDataRequirement
{
    public string Id { get; init; } = "";
    public TestDataConsumer Consumer { get; init; } = new();
    /// <summary>Environment id (Target Environment identity) or null for any environment.</summary>
    public string? EnvironmentId { get; init; }
    /// <summary>Extensible usage word: "Request body", "Path parameter", "GraphQL variable", "Identity reference", "Browser form value" …</summary>
    public string Usage { get; init; } = "";
    public bool Required { get; init; } = true;
    public TestDataDataType RequestedDataType { get; init; }
    public string? SchemaRef { get; init; }
    public List<string> RequestedFields { get; init; } = [];
    public string? Description { get; init; }
    public DateTimeOffset RegisteredAt { get; init; }
    /// <summary>Computed for the view: Resolved when an explicit usage reference binds an approved asset to this requirement.</summary>
    public TestDataRequirementStatus Status { get; init; } = TestDataRequirementStatus.Missing;
    public string? BoundAssetId { get; init; }
}

/// <summary>A consumer's explicit use of an asset: a reference to the asset id and the version (fingerprint) it was bound to. Never a value.
/// <see cref="Mapping"/> is display metadata only (e.g. "{personId} ← personId"); runtime logic stays in the consumer.</summary>
public sealed record TestDataUsageReference
{
    public string Id { get; init; } = "";
    public string AssetId { get; init; } = "";
    public string? AssetFingerprint { get; init; }
    public TestDataConsumer Consumer { get; init; } = new();
    public string Usage { get; init; } = "";
    public string? EnvironmentId { get; init; }
    public string? RequirementId { get; init; }
    public string? Mapping { get; init; }
    public DateTimeOffset BoundAt { get; init; }
    public TestDataUsageStatus Status { get; init; } = TestDataUsageStatus.Resolved;
    public string? StatusReason { get; init; }
}

/// <summary>One asset considered for a requirement and why. A suggestion only — nothing is bound by matching.</summary>
public sealed record TestDataMatch(string AssetId, string Alias, TestDataMatchState State, string Reason);

/// <summary>Register an asset's metadata in the catalog (from a source candidate or manually). No value: values stay with their sources.</summary>
public sealed record TestDataRegistrationRequest
{
    /// <summary>Lowercase letters, digits and hyphens (the stable reference consumers use).</summary>
    public string Alias { get; init; } = "";
    public string? Label { get; init; }
    public TestDataDataType DataType { get; init; }
    /// <summary>Environment id or null for Shared.</summary>
    public string? EnvironmentId { get; init; }
    /// <summary>Register a source candidate: the backend re-derives it from this exact snapshot (the client never supplies structure).</summary>
    public Guid? SnapshotId { get; init; }
    public string? CandidateId { get; init; }
    /// <summary>Manual registration: safe field/parameter names only (never values).</summary>
    public List<string> Fields { get; init; } = [];
    public List<string> Tags { get; init; } = [];
    /// <summary>What the asset represents (<see cref="TestDataSemanticRoles"/>); optional.</summary>
    public string? SemanticRole { get; init; }
    /// <summary>"Applies to" context ids (<see cref="TestDataContexts"/>); optional suggestion metadata.</summary>
    public List<string>? AppliesTo { get; init; }
}

/// <summary>
/// Backend-only comparison of a value observed by Live Discovery with approved Test Data. The value travels in the request body only (never a URL,
/// log or export) and is not stored; the answer is an alias or nothing — never a value.
/// </summary>
public sealed record ObservedValueMatchRequest(string Value, TestDataDataType DataType, string? EnvironmentId);

public sealed record ObservedValueMatchResult(bool Matched, string? Alias, string Detail);

/// <summary>The test lead's overview: counts only, from the enriched catalog and the registered requirements.</summary>
public sealed record TestDataOverview(int Approved, int NeedsReview, int Draft, int Deprecated, int OpenRequirements, int RequiredResolved, int RequiredTotal,
    int EnvironmentMismatches, int ContractMismatches);

/// <summary>Requirements and usages registered by consumers, plus the overview.</summary>
public sealed record TestDataConsumerView(List<ConsumerTestDataRequirement> Requirements, List<TestDataUsageReference> Usages, TestDataOverview Overview);

public static class TestDataCatalogRules
{
    public const string Shared = "Shared";

    public static string ScopeLabel(string? environment) => string.IsNullOrWhiteSpace(environment) ? Shared : environment.Trim();

    public static string Label(TestDataAssetStatus status) => status switch
    {
        TestDataAssetStatus.NeedsReview => "Needs review",
        _ => status.ToString(),
    };

    public static string Label(TestDataContractStatus status) => status switch
    {
        TestDataContractStatus.NeedsReview => "Needs review",
        TestDataContractStatus.NotAssessed => "Not assessed",
        TestDataContractStatus.NotApplicable => "Not applicable",
        _ => status.ToString(),
    };

    public static string Label(TestDataMatchState state) => state switch
    {
        TestDataMatchState.Exact => "Exact match",
        TestDataMatchState.Possible => "Possible match",
        TestDataMatchState.NoMatch => "No match",
        _ => state.ToString(),
    };

    /// <summary>
    /// The lifecycle state from the facts: deprecated wins; an approved asset whose content changed since approval needs review; otherwise
    /// approved; an unapproved asset is a draft until it came from a candidate review or changed (needs review).
    /// </summary>
    public static TestDataAssetStatus Status(TestDataApproval approval, bool deprecated, string? approvedFingerprint, string? currentFingerprint, bool reviewRequested)
    {
        if (deprecated) return TestDataAssetStatus.Deprecated;
        if (approval == TestDataApproval.Approved)
            return approvedFingerprint is not null && currentFingerprint is not null && approvedFingerprint != currentFingerprint ? TestDataAssetStatus.NeedsReview : TestDataAssetStatus.Approved;
        return approval is TestDataApproval.Expired or TestDataApproval.Disabled || reviewRequested ? TestDataAssetStatus.NeedsReview : TestDataAssetStatus.Draft;
    }

    /// <summary>A stable content/metadata fingerprint (SHA-256, hex) — the version consumers bind to. Never the content itself.</summary>
    public static string Fingerprint(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    /// <summary>Usable in the requirement's environment: shared assets anywhere; scoped assets only in their own environment.</summary>
    public static bool InScope(TestDataAssetSummary asset, string? environment) =>
        asset.Environment is null || environment is null || string.Equals(asset.Environment, environment, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Deterministic matching of one requirement against the catalog. Exact = same data type + same schema reference; Compatible = same data
    /// type and the requested fields/variable appear in the asset's fields or alias; Possible = same data type only (needs confirmation);
    /// Conflict = same data type but another environment, or not approved; deprecated assets are left out unless asked for. Never binds.
    /// </summary>
    public static List<TestDataMatch> Match(ConsumerTestDataRequirement requirement, IEnumerable<TestDataAssetSummary> assets, bool includeDeprecated = false)
    {
        var matches = new List<TestDataMatch>();
        foreach (var asset in assets)
        {
            var type = asset.DataType ?? TestDataDataTypes.Of(asset);
            if (type != requirement.RequestedDataType) continue;
            var status = asset.Status ?? Status(asset.Approval, false, null, null, false);
            if (status == TestDataAssetStatus.Deprecated && !includeDeprecated) continue;
            if (!InScope(asset, requirement.EnvironmentId))
            {
                matches.Add(new(asset.Id, asset.Alias, TestDataMatchState.Conflict, $"Scoped to {asset.Environment}, not {requirement.EnvironmentId}; never reused across environments."));
                continue;
            }
            if (status is not TestDataAssetStatus.Approved)
            {
                matches.Add(new(asset.Id, asset.Alias, TestDataMatchState.Conflict, $"{Label(status)}: approve it before a consumer uses it."));
                continue;
            }
            var schema = asset.Schema?.SchemaPath ?? asset.SchemaRef;
            if (requirement.SchemaRef is { Length: > 0 } wanted && schema is { Length: > 0 } && SameSchema(wanted, schema))
            {
                matches.Add(new(asset.Id, asset.Alias, TestDataMatchState.Exact, $"Same data type and schema ({wanted})."));
                continue;
            }
            var names = (asset.Fields ?? []).Append(asset.Alias).ToList();
            var requested = requirement.RequestedFields.Where(f => f.Length > 0).ToList();
            if (requested.Count > 0 && requested.All(f => names.Any(n => n.Contains(f, StringComparison.OrdinalIgnoreCase))))
                matches.Add(new(asset.Id, asset.Alias, TestDataMatchState.Compatible, $"Same data type; provides {string.Join(", ", requested)}."));
            else
                matches.Add(new(asset.Id, asset.Alias, TestDataMatchState.Possible, "Same data type only — confirm it fits before binding."));
        }
        return [.. matches.OrderBy(m => m.State).ThenBy(m => m.Alias, StringComparer.Ordinal)];
    }

    private static bool SameSchema(string a, string b)
    {
        static string Norm(string s) => s.Replace('\\', '/').Trim().ToLowerInvariant();
        return Norm(a) == Norm(b) || Norm(a).EndsWith("/" + Norm(b), StringComparison.Ordinal) || Norm(b).EndsWith("/" + Norm(a), StringComparison.Ordinal)
            || Norm(a) == Norm(b).Split('#')[0];
    }

    /// <summary>Requirement status from the usages bound to it: an approved, unchanged binding resolves it; a changed one needs review.</summary>
    public static TestDataRequirementStatus RequirementStatus(ConsumerTestDataRequirement requirement, IEnumerable<TestDataUsageReference> usages)
    {
        var bound = usages.Where(u => u.RequirementId == requirement.Id).ToList();
        if (bound.Count == 0) return TestDataRequirementStatus.Missing;
        return bound.Any(u => u.Status == TestDataUsageStatus.Resolved) ? TestDataRequirementStatus.Resolved : TestDataRequirementStatus.NeedsReview;
    }

    /// <summary>A usage's state against the asset as it is now: deprecated, not approved or changed since binding → needs review (with why).</summary>
    public static (TestDataUsageStatus Status, string? Reason) UsageStatus(TestDataUsageReference usage, TestDataAssetSummary? asset) => asset switch
    {
        null => (TestDataUsageStatus.NeedsReview, "The asset no longer exists in the catalog."),
        { Status: TestDataAssetStatus.Deprecated } => (TestDataUsageStatus.NeedsReview, "The asset is deprecated."),
        { Status: not TestDataAssetStatus.Approved } => (TestDataUsageStatus.NeedsReview, "Used · approval missing."),
        _ when usage.AssetFingerprint is not null && Version(asset) is { } current && usage.AssetFingerprint != current
            => (TestDataUsageStatus.NeedsReview, "The asset changed after it was bound."),
        _ => (TestDataUsageStatus.Resolved, null),
    };

    /// <summary>The version a usage binds to: the content fingerprint when the asset has a value, else the approved version.</summary>
    public static string? Version(TestDataAssetSummary asset) => asset.Fingerprint ?? asset.ApprovedFingerprint;

    public static readonly System.Text.RegularExpressions.Regex AliasPattern = new("^[a-z0-9][a-z0-9-]{0,63}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
}

/// <summary>One Test Data value to place in a generated XML fixture: the asset alias and the element path under the root (local names, dots).</summary>
public sealed record TestDataXmlBinding(string Alias, string ElementPath);

/// <summary>Fill generated XML with Test Data values (Test Data → Create XML fixture). The backend resolves each alias; values are never sent by the
/// browser and never logged.</summary>
public sealed record TestDataXmlBindRequest(string Xml, IReadOnlyList<TestDataXmlBinding> Bindings);

public sealed record TestDataXmlBindResult(string? Xml, IReadOnlyList<string> Problems);

/// <summary>
/// Who owns an asset, declared by its provider — never inferred from its alias. Only <see cref="ProtectedDefault"/> (system-seeded canonical
/// defaults) and <see cref="Configuration"/> (explicit backend configuration, changed only by editing it) are kept by Delete and Maintenance reset;
/// session-entered, registered (manual, source candidate, observed) and generated assets are local data.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TestDataOwnership { Unknown, ProtectedDefault, Configuration, SessionEntered, Registered, Generated }

public static class TestDataOwnerships
{
    /// <summary>Local Test Data that Delete may remove and Maintenance reset removes.</summary>
    public static bool Deletable(TestDataOwnership ownership) => ownership is TestDataOwnership.SessionEntered or TestDataOwnership.Registered or TestDataOwnership.Generated;

    public static string Label(TestDataOwnership ownership) => ownership switch
    {
        TestDataOwnership.ProtectedDefault => "Protected default",
        TestDataOwnership.Configuration => "Backend configuration",
        TestDataOwnership.SessionEntered => "Entered for this session",
        TestDataOwnership.Registered => "Registered in Test Data",
        TestDataOwnership.Generated => "Generated fixture",
        _ => "Unknown owner",
    };

    /// <summary>Why an asset cannot be deleted in Test Data, or null when it can.</summary>
    public static string? WhyNotDeletable(TestDataOwnership ownership) => ownership switch
    {
        TestDataOwnership.ProtectedDefault => "Protected default: system-seeded and always available. It cannot be deleted and survives a reset.",
        TestDataOwnership.Configuration => "Supplied by the backend's configuration; remove it there. BirkNext never edits explicit configuration.",
        TestDataOwnership.Unknown => "Its owner is unknown, so BirkNext does not delete it.",
        _ => null,
    };
}

/// <summary>
/// One place that references a Test Data asset by alias (or, for an explicit binding, by id): a performance scenario step or variable, a
/// Critical E2E flow, an explicit consumer binding, or (added by the browser) an XML field mapping. Names and locations only, never values.
/// </summary>
public sealed record TestDataAssetUsage(string Alias, string ConsumerType, string ConsumerId, string ConsumerName, string Location)
{
    public string? EnvironmentId { get; init; }
    /// <summary>The alias no longer resolves to any asset in the catalog: the consumer shows "Test Data reference missing".</summary>
    public bool Unresolved { get; init; }
}

/// <summary>Result of deleting one asset: what was removed and which references were left unresolved (never deleted or silently re-pointed).</summary>
public sealed record TestDataDeleteResult(string AssetId, string Alias, bool Deleted, string Message, IReadOnlyList<TestDataAssetUsage> UnresolvedReferences);

/// <summary>Re-points every reference of an asset to a compatible replacement before (or instead of) deleting it.</summary>
public sealed record TestDataReplaceRequest(string ReplacementAssetId);

public sealed record TestDataReplaceResult(bool Replaced, string Message, int ReferencesUpdated, IReadOnlyList<TestDataAssetUsage> Updated);

/// <summary>What a Maintenance reset did to Test Data. Counts only.</summary>
public sealed record TestDataResetSummary
{
    public int ProtectedDefaultsPreserved { get; init; }
    public int ConfigurationAssetsPreserved { get; init; }
    public int AssetsRemoved { get; init; }
    public int GeneratedFixturesRemoved { get; init; }
    /// <summary>References whose consumers were removed by the same reset (performance scenarios, Critical E2E flows, XML setups).</summary>
    public int ReferencesRemovedWithConsumers { get; init; }
    /// <summary>References that survived the reset but now point at a removed asset (require remapping). Expected to be zero.</summary>
    public int ReferencesRequiringRemapping { get; init; }
}

/// <summary>
/// The Edit surface of one asset. <see cref="Value"/> is filled only here, and only for an asset whose value Test Data may edit (a protected
/// default or a session entry) — never in the asset table. Type, role, scope and the asset id are shown read-only.
/// </summary>
public sealed record TestDataEditView
{
    public TestDataAssetSummary Asset { get; init; } = new();
    /// <summary>"Reporter" or "Child" for identity defaults; null otherwise.</summary>
    public string? Role { get; init; }
    public string? Value { get; init; }
    public bool LabelEditable { get; init; }
    public bool ValueEditable { get; init; }
    /// <summary>Why the value cannot be edited here (configuration-owned, metadata-only registration, payload → new draft).</summary>
    public string? ValueNote { get; init; }
    /// <summary>A protected default with a seeded value to go back to ("Restore default value").</summary>
    public bool CanRestoreDefault { get; init; }
    /// <summary>Semantic role and "Applies to" can be changed here (registered assets); otherwise declared by the source.</summary>
    public bool MetadataEditable { get; init; }
    public IReadOnlyList<TestDataAssetUsage> Usages { get; init; } = [];
}

public sealed record TestDataEditRequest(string? Label, string? Value)
{
    /// <summary>New semantic role (registered assets only); null = unchanged, "" = not specified.</summary>
    public string? SemanticRole { get; init; }
    /// <summary>New "Applies to" context ids (registered assets only); null = unchanged.</summary>
    public List<string>? AppliesTo { get; init; }
    /// <summary>The person confirmed a role change that affects bound consumers.</summary>
    public bool ConfirmRoleImpact { get; init; }
}

public sealed record TestDataEditResult(bool Updated, string Message, bool ValueChanged, TestDataAssetSummary? Asset, IReadOnlyList<TestDataAssetUsage> AffectedUsages);

/// <summary>What "Reset Test Data" would do, before it runs (counts and the references it would leave unresolved).</summary>
public sealed record TestDataResetPreview
{
    public IReadOnlyList<string> ProtectedDefaults { get; init; } = [];
    public int ConfigurationAssets { get; init; }
    public IReadOnlyList<string> AssetsToRemove { get; init; } = [];
    public int GeneratedFixturesToRemove { get; init; }
    public IReadOnlyList<TestDataAssetUsage> AffectedReferences { get; init; } = [];
}

/// <summary>Result of "Reset Test Data": the summary plus the references that now require remapping.</summary>
public sealed record TestDataResetResult(TestDataResetSummary Summary, IReadOnlyList<TestDataAssetUsage> ReferencesRequiringRemapping);

/// <summary>
/// The Test Data value versions a generated fixture was built from ("child-default@v2; observed-id@v0"): asset references plus each asset's safe
/// change marker at generation time. A later edit of a mapped asset is detected by comparing versions — no value is stored or hashed.
/// </summary>
public static class TestDataValueVersions
{
    public const string Label = "Test Data value versions";

    public static string Format(IEnumerable<TestDataAssetSummary> assets) =>
        string.Join("; ", assets.DistinctBy(a => a.Alias).OrderBy(a => a.Alias, StringComparer.Ordinal).Select(a => $"{a.Alias}@v{a.ValueVersion}"));

    public static IReadOnlyDictionary<string, int> Parse(string? text)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var part in (text ?? "").Split("; ", StringSplitOptions.RemoveEmptyEntries))
            if (part.LastIndexOf("@v", StringComparison.Ordinal) is var at and > 0 && int.TryParse(part[(at + 2)..], out var version)) result[part[..at]] = version;
        return result;
    }

    /// <summary>The mapped assets whose value changed since the fixture was generated (or that no longer exist). Empty when nothing was recorded.</summary>
    public static IReadOnlyList<(string Alias, bool Missing)> Changed(TestDataAssetSummary fixture, IEnumerable<TestDataAssetSummary> current)
    {
        var recorded = Parse(fixture.Provenance.FirstOrDefault(p => p.Label == Label)?.Value);
        if (recorded.Count == 0) return [];
        var byAlias = current.GroupBy(a => a.Alias, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        return [.. recorded.Where(r => !byAlias.TryGetValue(r.Key, out var asset) || asset.ValueVersion != r.Value)
            .Select(r => (r.Key, !byAlias.ContainsKey(r.Key)))];
    }
}
