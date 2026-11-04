using System.Text.Json.Serialization;

namespace BirkNext.Interpretation;

// ── Graph impact analysis over the canonical model (rule set impact/1) ────────────────────────────────────────────────────
// "If X changes, what depends on it?" A bounded, cycle-safe walk over canonical relations in their impact direction. Every impacted entity
// carries its relation path; nothing is impacted without one. Alias / same-as / supersedes relations are terminology or identity, never impact.

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ImpactLevel
{
    /// <summary>One established relation away.</summary>
    Direct,
    /// <summary>Further away, every relation on the path established.</summary>
    Downstream,
    /// <summary>The path includes a candidate (not yet confirmed) relation.</summary>
    Potential,
}

/// <summary>Retest need, only from relation evidence: an established VERIFIES = must retest, a candidate VERIFIES = should retest.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RetestLevel { MustRetest, ShouldRetest }

public sealed record ImpactPathStep(string Relation, bool Reverse, string EntityKey, string EntityType, string EntityName, string Resolution);

public sealed record ImpactedEntity(string Key, string Type, string Name, ImpactLevel Level, int Depth, IReadOnlyList<ImpactPathStep> Path);

public sealed record CanonicalImpactedTest(string Key, string Name, RetestLevel Retest, string Why);

public sealed record CanonicalImpactView
{
    public const string RuleSetVersion = "impact/1";
    public string Scope { get; init; } = "";
    public string RootKey { get; init; } = "";
    public string RootName { get; init; } = "";
    public string RootType { get; init; } = "";
    public List<ImpactedEntity> Entities { get; init; } = [];
    public List<CanonicalImpactedTest> Tests { get; init; } = [];
    /// <summary>Impacted requirements / stories / scenarios that no test verifies (explicitly or as a candidate): a test gap, not a failure.</summary>
    public List<ImpactedEntity> MissingTests { get; init; } = [];
    /// <summary>Unresolved relations met during the walk: their impact is Unknown and is counted, never guessed.</summary>
    public int Unknown { get; init; }
    public bool Truncated { get; init; }
    public List<string> Limitations { get; init; } = [];
    public string Rules { get; init; } = RuleSetVersion;
}

public static class CanonicalImpactRules
{
    public const int MaxDepth = 3, MaxEntities = 500;

    /// <summary>Relations whose SOURCE depends on their target: a change of the target impacts the source (walked target → source).</summary>
    public static readonly HashSet<CanonicalRelationType> Reverse =
    [
        CanonicalRelationType.Implements, CanonicalRelationType.References, CanonicalRelationType.DependsOn, CanonicalRelationType.Consumes,
        CanonicalRelationType.Uses, CanonicalRelationType.Verifies, CanonicalRelationType.DerivedFrom, CanonicalRelationType.OccurrenceOf,
        CanonicalRelationType.BelongsTo,
    ];

    /// <summary>Relations whose TARGET is part of / produced by the source: a change of the source impacts the target (walked source → target).</summary>
    public static readonly HashSet<CanonicalRelationType> Forward = [CanonicalRelationType.Contains, CanonicalRelationType.Exposes, CanonicalRelationType.Produces];

    /// <summary>Entities a test gap is reported for when impacted.</summary>
    public static readonly HashSet<string> Testable = [nameof(CanonicalEntityType.Requirement), nameof(CanonicalEntityType.UserStory), nameof(CanonicalEntityType.Scenario)];

    public static string LevelLabel(ImpactLevel level) => level switch
    {
        ImpactLevel.Direct => "Direct",
        ImpactLevel.Downstream => "Downstream",
        _ => "Potential (unconfirmed relation)",
    };

    public static string RetestLabel(RetestLevel level) => level == RetestLevel.MustRetest ? "Must retest" : "Should retest";
}
