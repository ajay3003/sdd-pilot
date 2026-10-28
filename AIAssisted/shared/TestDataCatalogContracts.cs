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
