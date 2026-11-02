using BirkNext.TestData;

namespace BirkNext.Api.Services.TestData;

/// <summary>A protection recorded in the Test Data registry: the stable asset id a canonical fixture resolved to, once, with why it is protected.</summary>
public sealed record ProtectedTestDataRecord(string AssetId, string CanonicalKey, string Kind, string Reason, string Source, DateTimeOffset ProtectedAt);

/// <summary>
/// A canonical payload fixture declared by its owner (e.g. a Test Pack): the alias it is registered under, the capability its owner records on it
/// when it is created for that purpose, and why it is baseline data. Identity is alias AND declared capability AND positive intent — a payload with
/// the same alias that was not created for that capability is never protected.
/// </summary>
public sealed record TestDataCanonicalFixture(string Key, string Alias, string Capability, string Reason, string Source);

/// <summary>
/// What a payload created for a declared fixture capability represents: its semantic role and "Applies to" contexts, in the canonical Test Data
/// vocabulary. Applied to positive generated fixtures that carry the capability (recorded when the fixture was created for it) and declare no
/// role of their own — so they are found by Role / Applies to filters and offered for the matching requirement. Never a binding.
/// </summary>
public sealed record TestDataFixtureMetadata(string Capability, string SemanticRole, IReadOnlyList<string> AppliesTo);

/// <summary>Declares canonical fixtures (implemented by owners outside the generic Test Data model).</summary>
public interface ITestDataCanonicalFixtures
{
    IReadOnlyList<TestDataCanonicalFixture> Fixtures { get; }
    IReadOnlyList<TestDataFixtureMetadata> Metadata => [];
}

/// <summary>
/// Persisted, generic protection. Protected defaults are declared by their provider (ownership); a canonical fixture is resolved deterministically to
/// exactly one asset and its stable id is recorded once (migration for existing installations, idempotent seeding) — never several, never by
/// display name. Runtime behaviour (badge, Delete, reset) reads the recorded protection only.
/// </summary>
public static class TestDataProtection
{
    public const string NegativeTest = "negative-test";

    /// <summary>Records the protection of each canonical fixture that resolves to exactly one asset and has none yet. True when something was recorded.</summary>
    public static bool Ensure(TestDataRegistryStore registry, IEnumerable<TestDataCanonicalFixture> fixtures, IReadOnlyList<TestDataAsset> assets, DateTimeOffset now)
    {
        var changed = false;
        var recorded = registry.ProtectedAssets();
        foreach (var fixture in fixtures)
        {
            if (recorded.Any(p => p.CanonicalKey == fixture.Key)) continue;
            var candidates = assets.Where(a => a.Ownership == TestDataOwnership.Generated && string.Equals(a.Alias, fixture.Alias, StringComparison.Ordinal)
                && a.Capabilities.Contains(fixture.Capability, StringComparer.Ordinal) && !a.Capabilities.Contains(NegativeTest, StringComparer.Ordinal)).ToList();
            // Ambiguity (several candidates) is never resolved by guessing: nothing is protected until exactly one remains.
            if (candidates.Count != 1) continue;
            changed |= registry.Protect(new ProtectedTestDataRecord(candidates[0].Id, fixture.Key, TestDataProtectionKinds.CanonicalFixture, fixture.Reason, fixture.Source, now));
        }
        return changed;
    }

    /// <summary>Role and "Applies to" of a positive generated fixture from its declared capability (first matching declaration); unchanged otherwise.</summary>
    public static TestDataAsset Describe(TestDataAsset asset, IReadOnlyList<TestDataFixtureMetadata> metadata)
    {
        if (metadata.Count == 0 || asset.Ownership != TestDataOwnership.Generated || asset.SemanticRole is not null
            || asset.Capabilities.Contains(NegativeTest, StringComparer.Ordinal)) return asset;
        if (metadata.FirstOrDefault(m => asset.Capabilities.Contains(m.Capability, StringComparer.Ordinal)) is not { } declared) return asset;
        return asset with { SemanticRole = declared.SemanticRole, AppliesTo = [.. declared.AppliesTo], MetadataFixed = true };
    }

    /// <summary>The asset with its protection: protected defaults by ownership, canonical fixtures by their recorded stable id.</summary>
    public static TestDataAsset Apply(TestDataAsset asset, IReadOnlyList<ProtectedTestDataRecord> recorded) =>
        recorded.FirstOrDefault(p => p.AssetId == asset.Id) is { } record
            ? asset with { ProtectionKind = record.Kind, ProtectionReason = record.Reason }
            : asset.Ownership == TestDataOwnership.ProtectedDefault
                ? asset with { ProtectionKind = TestDataProtectionKinds.ProtectedDefault, ProtectionReason = asset.ProtectionReason ?? "System-seeded default." }
                : asset;
}
