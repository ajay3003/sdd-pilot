using BirkNext.Api.Services.TestData;

namespace BirkNext.Api.Services.AltinnTesting;

/// <summary>
/// Test Data → Edit / Delete / Reset for the Altinn test identities: the built-in (protected) reporter and child defaults and identities entered
/// for this backend session. Edits go through the same validation as entry (<see cref="AltinnSessionStore.UpdateIdentity"/>); a protected
/// default's edit is persisted (<see cref="ProtectedTestDataDefaultsStore"/>) so it survives restarts and Maintenance reset. Configured
/// identities are never edited or removed.
/// </summary>
public sealed class AltinnTestDataEditor(AltinnSessionStore identities, ProtectedTestDataDefaultsStore defaults, AltinnTestingOptions options) : ITestDataAssetEditor
{
    public string ProviderId => "altinn-local";
    public IReadOnlyList<string> RoleCapabilities { get; } = [AltinnTestDataProvider.Reporter, AltinnTestDataProvider.Child];

    private static string? RoleKey(TestDataAsset asset) =>
        asset.Capabilities.Contains(AltinnTestDataProvider.Reporter) ? AltinnTestDataProvider.Reporter
        : asset.Capabilities.Contains(AltinnTestDataProvider.Child) ? AltinnTestDataProvider.Child : null;

    public string? Role(TestDataAsset asset) => RoleKey(asset) switch
    {
        AltinnTestDataProvider.Reporter => "Reporter",
        AltinnTestDataProvider.Child => "Child",
        _ => null,
    };

    public string? Edit(TestDataAsset asset, string? label, string? value, DateTimeOffset now)
    {
        if (RoleKey(asset) is not { } role) return "This identity has no reporter or child role.";
        if (identities.UpdateIdentity(role, asset.Alias, label, value, now) is { } problem) return problem;
        Persist(role, asset.Alias, restored: false);
        return null;
    }

    public bool CanRestoreDefault(TestDataAsset asset) => RoleKey(asset) is { } role && SkoleNaervaerTestIdentityDefaults.Seed(role, asset.Alias) is not null;

    public string? RestoreDefault(TestDataAsset asset, DateTimeOffset now, out bool changed)
    {
        changed = false;
        if (RoleKey(asset) is not { } role || SkoleNaervaerTestIdentityDefaults.Seed(role, asset.Alias) is not { } seed) return "This asset has no default value.";
        changed = seed != asset.Value?.Reveal()?.Trim();
        if (changed && identities.UpdateIdentity(role, asset.Alias, null, seed, now, valueModified: false) is { } problem) return problem;
        Persist(role, asset.Alias, restored: true);
        return null;
    }

    public bool Remove(TestDataAsset asset) => RoleKey(asset) is { } role && identities.RemoveIdentity(role, asset.Alias);

    public int ClearLocal()
    {
        var count = options.UnattendedIdPortenTest.Reporters.Count(r => r.SessionExpiresAt is not null && !r.BuiltIn)
            + options.SyntheticChildren.Count(c => c.SessionExpiresAt is not null && !c.BuiltIn);
        identities.ClearIdentities();
        return count;
    }

    /// <summary>
    /// "Reset everything" (2026-11-22): the built-in reporter and child are removed — with their edits — and stay removed after restarts, until
    /// Test Data → Restore built-in defaults. Before, they were put back with their product values at once, so Reset everything looked as if it
    /// had not deleted protected Test Data. Configured identities are never touched. Returns the identities removed.
    /// </summary>
    public IReadOnlyList<string> RemoveBuiltInDefaults(DateTimeOffset now)
    {
        var removed = options.UnattendedIdPortenTest.Reporters.Where(r => r.BuiltIn).Select(r => r.Alias)
            .Concat(options.SyntheticChildren.Where(c => c.BuiltIn).Select(c => c.Alias)).Order(StringComparer.Ordinal).ToList();
        options.UnattendedIdPortenTest.Reporters.RemoveAll(r => r.BuiltIn);
        options.SyntheticChildren.RemoveAll(c => c.BuiltIn);
        defaults.Clear();
        defaults.MarkBuiltInsRemoved(now);
        return removed;
    }

    public bool BuiltInDefaultsRemoved => defaults.BuiltInsRemoved;

    /// <summary>Test Data → Restore built-in defaults: the product reporter and child exist again (product values), now and after restarts.</summary>
    public IReadOnlyList<string> RestoreBuiltInDefaults()
    {
        defaults.ClearBuiltInsRemoved();
        var before = options.UnattendedIdPortenTest.Reporters.Where(r => r.BuiltIn).Select(r => r.Alias)
            .Concat(options.SyntheticChildren.Where(c => c.BuiltIn).Select(c => c.Alias)).ToHashSet(StringComparer.Ordinal);
        SkoleNaervaerTestIdentityDefaults.Apply(options);
        return [.. options.UnattendedIdPortenTest.Reporters.Where(r => r.BuiltIn).Select(r => r.Alias)
            .Concat(options.SyntheticChildren.Where(c => c.BuiltIn).Select(c => c.Alias)).Where(a => !before.Contains(a)).Order(StringComparer.Ordinal)];
    }

    /// <summary>Only built-in identities are persisted (session identities stay memory-only, as they always were).</summary>
    private void Persist(string role, string alias, bool restored)
    {
        if (role == AltinnTestDataProvider.Reporter && options.UnattendedIdPortenTest.Reporters.FirstOrDefault(r => r.BuiltIn && r.Alias == alias) is { } reporter)
            defaults.Save(new ProtectedDefaultValue
            {
                Role = role, Alias = alias, Identifier = restored ? null : reporter.TestIdPersonIdentifier, Label = reporter.Label,
                ValueVersion = reporter.ValueVersion, UpdatedAt = reporter.ValueUpdatedAt ?? DateTimeOffset.UtcNow,
            });
        else if (role == AltinnTestDataProvider.Child && options.SyntheticChildren.FirstOrDefault(c => c.BuiltIn && c.Alias == alias) is { } child)
            defaults.Save(new ProtectedDefaultValue
            {
                Role = role, Alias = alias, Identifier = restored ? null : child.TestPersonIdentifier, Label = child.Label,
                ValueVersion = child.ValueVersion, UpdatedAt = child.ValueUpdatedAt ?? DateTimeOffset.UtcNow,
            });
    }
}
