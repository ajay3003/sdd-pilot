using BirkNext.TestData;

namespace BirkNext.Api.Services.TestData;

/// <summary>
/// Resolves a consumer's persisted Test Data reference. A stored TestDataAssetId is authoritative (renames are followed; a deleted asset stays
/// deleted, never re-pointed). A legacy alias-only reference maps to an identity only when that is provable: exactly one identity ever carried
/// the alias. A reused alias (an earlier asset with that alias was deleted) or an alias shared by several identities is surfaced for review,
/// never guessed; an alias no asset has is unresolved and preserved as written.
/// </summary>
public static class TestDataReferenceResolver
{
    public static TestDataReferenceResolution Resolve(TestDataCatalog catalog, string alias, string? assetId) =>
        Resolve(catalog.Assets(), catalog.Identities?.Identities() ?? [], alias, assetId);

    public static TestDataReferenceResolution Resolve(IReadOnlyList<TestDataAsset> assets, IReadOnlyList<TestDataAssetIdentity> identities, string alias, string? assetId)
    {
        if (!string.IsNullOrWhiteSpace(assetId))
        {
            if (assets.FirstOrDefault(a => a.AssetId == assetId) is { } current)
                return new(alias, assetId, TestDataReferenceState.Resolved, assetId, current.Alias,
                    current.Alias == alias ? "Stable Test Data reference." : $"Stable Test Data reference (alias renamed from {alias} to {current.Alias}).");
            return identities.FirstOrDefault(i => i.AssetId == assetId) is { Status: TestDataIdentityStatus.Removed }
                ? new(alias, assetId, TestDataReferenceState.Removed, assetId, null, $"The Test Data asset {alias} was deleted; the reference is kept unresolved.")
                : new(alias, assetId, TestDataReferenceState.Unresolved, assetId, null, $"The Test Data asset {alias} is not in Test Data.");
        }

        var now = assets.Where(a => a.Alias == alias).ToList();
        // No identity store (stable ids not assigned): the alias is the only identity there is, exactly as before stable ids.
        if (now.All(a => a.AssetId is null) && identities.Count == 0)
            return now.Count switch
            {
                0 => new(alias, null, TestDataReferenceState.Unresolved, null, null, $"No Test Data asset has the alias {alias}."),
                1 => new(alias, null, TestDataReferenceState.LegacyUnique, null, alias, "Alias reference (stable ids are not available in this host)."),
                _ => new(alias, null, TestDataReferenceState.LegacyNeedsReview, null, null, $"The alias {alias} matches more than one asset."),
            };
        var holders = identities.Where(i => i.Aliases.Any(p => p.Alias == alias)).ToList();
        var removed = holders.Where(i => i.Status == TestDataIdentityStatus.Removed).ToList();
        var renamedActive = holders.Where(i => i.Status == TestDataIdentityStatus.Active && i.CurrentAlias != alias).ToList();
        var candidates = now.Select(a => a.AssetId).Concat(renamedActive.Select(i => (string?)i.AssetId)).Distinct().ToList();

        if (candidates.Count == 0)
            return new(alias, null, TestDataReferenceState.Unresolved, null, null,
                removed.Count > 0 ? $"Legacy reference: the Test Data asset {alias} was deleted." : $"Legacy reference: no Test Data asset has the alias {alias}.");
        if (candidates.Count > 1 || removed.Count > 0)
            return new(alias, null, TestDataReferenceState.LegacyNeedsReview, null, null, removed.Count > 0
                ? $"Legacy reference: the alias {alias} was used by a deleted asset and is now used by another one. It is not attached automatically — choose the asset again."
                : $"Legacy reference: the alias {alias} matches more than one asset. Choose the asset again.");
        var id = candidates[0];
        var asset = assets.FirstOrDefault(a => a.AssetId == id);
        if (id is null || asset is null)
            return new(alias, null, TestDataReferenceState.Unresolved, null, null, $"Legacy reference: the Test Data asset {alias} could not be identified.");
        return new(alias, null, TestDataReferenceState.LegacyUnique, id, asset.Alias, "Legacy alias reference; it maps to exactly one asset and is migrated to its stable id.");
    }

    /// <summary>The asset a reference uses, from an already-read asset list (null when it is not usable).</summary>
    public static TestDataAsset? AssetIn(IReadOnlyList<TestDataAsset> assets, TestDataReferenceResolution resolution) =>
        !resolution.Usable ? null
        : resolution.AssetId is { } id ? assets.FirstOrDefault(a => a.AssetId == id)
        : assets.FirstOrDefault(a => a.Alias == resolution.CurrentAlias);

    /// <summary>The value behind a usable reference, for a run only — never of a disabled asset.</summary>
    public static string? RevealIn(IReadOnlyList<TestDataAsset> assets, TestDataReferenceResolution resolution) =>
        AssetIn(assets, resolution) is { Approval: not TestDataApproval.Disabled } asset ? asset.Value?.Reveal() : null;

    /// <summary>The asset a reference uses at run time (null when it is not usable).</summary>
    public static TestDataAsset? Asset(TestDataCatalog catalog, TestDataReferenceResolution resolution) =>
        !resolution.Usable ? null
        : resolution.AssetId is { } id ? catalog.Assets().FirstOrDefault(a => a.AssetId == id)
        : catalog.Assets().FirstOrDefault(a => a.Alias == resolution.CurrentAlias);
}
