using System.Text.Json.Serialization;

namespace BirkNext.TestData;

/// <summary>How a consumer's persisted Test Data reference resolves today.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TestDataReferenceState
{
    /// <summary>The stored TestDataAssetId resolves to an existing asset (whatever its alias is now).</summary>
    Resolved,
    /// <summary>A legacy alias-only reference that maps to exactly one asset identity (no reuse, no ambiguity): safe to migrate.</summary>
    LegacyUnique,
    /// <summary>A legacy alias-only reference whose alias was reused or matches several identities: never guessed, needs review.</summary>
    LegacyNeedsReview,
    /// <summary>The stored TestDataAssetId belongs to an asset that was deleted.</summary>
    Removed,
    /// <summary>Nothing resolves (unknown id, or a legacy alias that no asset has).</summary>
    Unresolved,
}

/// <summary>One consumer reference as resolved now. <see cref="CurrentAlias"/> is the asset's alias today (after renames).</summary>
public sealed record TestDataReferenceResolution(string StoredAlias, string? StoredAssetId, TestDataReferenceState State, string? AssetId, string? CurrentAlias, string Detail)
{
    public bool Usable => State is TestDataReferenceState.Resolved or TestDataReferenceState.LegacyUnique;
}

public static class TestDataReferenceText
{
    public const string HistoricalIdentityNotRecorded = "Historical Test Data identity not recorded";

    public static string Label(TestDataReferenceState state) => state switch
    {
        TestDataReferenceState.Resolved => "Stable reference",
        TestDataReferenceState.LegacyUnique => "Legacy alias reference · migratable",
        TestDataReferenceState.LegacyNeedsReview => "Legacy Test Data reference · Needs review",
        TestDataReferenceState.Removed => "Test Data asset deleted",
        _ => "Unresolved Test Data reference",
    };

    /// <summary>Aligns a stored id list with its alias list (missing entries = legacy alias-only).</summary>
    public static string? IdAt(IReadOnlyList<string?>? ids, int index) => ids is not null && index < ids.Count ? ids[index] : null;
}
