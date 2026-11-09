using System.Text.Json.Serialization;
using BirkNext.TestData;

namespace BirkNext.ContractTesting;

// Automatic contract test data: typed synthetic values derived from the exact stored contract version, validated with the same contract validator
// runs use, persisted as a versioned key-value set in Test Data and pinned to the operation's tests. A value is CERTAIN only when the contract
// defines it sufficiently (enum, const, default, type/format/bounds); identifiers of existing records, sensitive fields and values the generator
// cannot satisfy are named for a human instead. Only fully certain, schema-valid data is approved and bound automatically. Offline: nothing is
// sent anywhere while generating.

/// <summary>Where a generated value comes from.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GeneratedValueOrigin
{
    /// <summary>Explicitly defined by the contract: an enum member or a const.</summary>
    ContractConstraint,
    /// <summary>The schema's declared default.</summary>
    SchemaDefault,
    /// <summary>A seeded synthetic value that satisfies the declared type, format and bounds (inferred, not specified by the contract).</summary>
    SyntheticRule,
    /// <summary>An existing approved Test Data value.</summary>
    ExistingTestData,
    /// <summary>A value a tester reviewed or entered.</summary>
    ReviewedUserValue,
}

/// <summary>Whether a value can be used without a human.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GeneratedValueCertainty
{
    /// <summary>Derived from the contract; safe to use.</summary>
    Certain,
    /// <summary>A value is proposed, but a human must confirm it (e.g. it must reference an existing record in the target).</summary>
    NeedsReview,
    /// <summary>No value is generated (sensitive field, unsupported type or constraint); a human or an approved provider must supply it.</summary>
    Unresolved,
}

/// <summary>One generated input value with its provenance. <see cref="Preview"/> is the synthetic value as text (never a real identity).</summary>
public sealed record ContractGeneratedValue(string Name, string In, bool Required, TestDataPrimitive? Type, string? Preview, GeneratedValueOrigin Origin,
    GeneratedValueCertainty Certainty, string Rule, string? Reason = null);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ContractTestDataState
{
    /// <summary>Generated, schema-valid, approved and pinned to the operation's tests.</summary>
    Bound,
    /// <summary>The tests already use a dataset (kept; never replaced).</summary>
    AlreadyBound,
    /// <summary>The operation needs no input values.</summary>
    NoInputs,
    /// <summary>Generated as a draft: at least one value needs review or validation did not pass; named fields need a human.</summary>
    NeedsReview,
    /// <summary>At least one required value could not be generated.</summary>
    Unresolved,
    /// <summary>The operation has no test to generate data for.</summary>
    NotSupported,
    Failed,
}

/// <summary>The data generated for one operation (shared by its positive and negative tests).</summary>
public sealed record ContractTestDataResult
{
    public string Operation { get; init; } = "";
    public string ContractRef { get; init; } = "";
    public ContractTestDataState State { get; init; }
    /// <summary>alias@vN of the Test Data key-value set, when one exists.</summary>
    public string? Dataset { get; init; }
    public bool Created { get; init; }
    public bool Approved { get; init; }
    public List<ContractGeneratedValue> Values { get; init; } = [];
    /// <summary>Valid / Invalid / NotAssessed — the contract validator's verdict on the materialized request (not the generator's opinion).</summary>
    public string Validation { get; init; } = "NotAssessed";
    public List<string> Findings { get; init; } = [];
    public List<string> Unsupported { get; init; } = [];
    public List<Guid> BoundTests { get; init; } = [];
    public string Summary { get; init; } = "";
}

public sealed record ContractTestDataReport(string ContractKey, int Version, int Seed, List<ContractTestDataResult> Operations)
{
    public int Bound => Operations.Count(o => o.State == ContractTestDataState.Bound);
    public int NeedsReview => Operations.Count(o => o.State is ContractTestDataState.NeedsReview or ContractTestDataState.Unresolved);
}

public sealed record ContractTestDataRequest(int? Version = null, List<string>? Operations = null, int Seed = ContractTestDataDefaults.Seed, string? EnvironmentId = null);

public static class ContractTestDataDefaults
{
    public const int Seed = 20261109;
    public const string AliasPrefix = "gen-";
}
