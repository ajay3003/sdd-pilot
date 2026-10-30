using BirkNext.TestData;

namespace BirkNext.Api.Services.TestData;

/// <summary>
/// A provider that owns values Test Data may edit or remove (for example identities entered for a session, or protected defaults whose value a
/// tester may change). Generic Test Data never knows which domain an editor serves: it asks the editor of the asset's source.
/// </summary>
public interface ITestDataAssetEditor
{
    /// <summary>The <see cref="ITestDataProvider.Id"/> whose assets this editor handles.</summary>
    string ProviderId { get; }
    /// <summary>Capabilities that are a semantic role ("one of these replaces only the same one").</summary>
    IReadOnlyList<string> RoleCapabilities { get; }
    /// <summary>The asset's role in words ("Reporter"), or null.</summary>
    string? Role(TestDataAsset asset);
    /// <summary>Edits the label and/or value in place (same id, type, role, scope, origin). Returns the problem or null.</summary>
    string? Edit(TestDataAsset asset, string? label, string? value, DateTimeOffset now);
    bool CanRestoreDefault(TestDataAsset asset);
    /// <summary>"Restore default value" (explicit). Returns the problem or null; <paramref name="changed"/> tells whether the value changed.</summary>
    string? RestoreDefault(TestDataAsset asset, DateTimeOffset now, out bool changed);
    /// <summary>Removes one deletable asset. False when it cannot.</summary>
    bool Remove(TestDataAsset asset);
    /// <summary>Reset Test Data: removes every deletable asset this editor owns (protected and configured ones stay). Returns how many.</summary>
    int ClearLocal();
}

/// <summary>A consumer that references Test Data aliases (performance scenarios, release flows …): where, and how to re-point them.</summary>
public interface ITestDataReferenceSource
{
    Task<IReadOnlyList<TestDataAssetUsage>> UsagesAsync(CancellationToken ct);
    /// <summary>Re-points every reference from one alias to another (aliases only; history stays as it was). Returns how many consumers changed.</summary>
    Task<int> ReplaceAsync(string fromAlias, string toAlias, CancellationToken ct);
}

/// <summary>
/// Test Data asset lifecycle beyond approval: usages (where an asset is referenced), Edit, Restore default value, Delete, Replace references and
/// Reset Test Data. Ownership is declared by each provider (<see cref="TestDataOwnership"/>); protected defaults and backend configuration are
/// never deleted or reset, server-side. Nothing here deletes a consumer or silently re-points a reference: Delete leaves references unresolved
/// (each consumer shows its own missing state) unless the tester chose Replace references, which only accepts a compatible approved asset.
/// </summary>
public sealed class TestDataLifecycleService(TestDataCatalog catalog, TestDataRegistryStore registry, IEnumerable<ITestDataAssetEditor> editors,
    IEnumerable<ITestDataReferenceSource> references, TimeProvider time)
{
    public const string BindingConsumer = "Consumer binding";
    private readonly IReadOnlyList<ITestDataAssetEditor> _editors = [.. editors];
    private readonly IReadOnlyList<ITestDataReferenceSource> _references = [.. references];

    private ITestDataAssetEditor? EditorOf(TestDataAsset asset) => _editors.FirstOrDefault(e => e.ProviderId == asset.SourceId);

    // ── Usages ───────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Every reference to a Test Data alias the backend knows (each reference source, plus explicit consumer bindings). XML field mappings live in
    /// the browser workspace and are added there. A reference whose alias no longer resolves is marked unresolved.
    /// </summary>
    public async Task<IReadOnlyList<TestDataAssetUsage>> UsagesAsync(CancellationToken ct)
    {
        var assets = catalog.Assets();
        var aliases = assets.Select(a => a.Alias).ToHashSet(StringComparer.Ordinal);
        var byId = assets.ToDictionary(a => a.Id, a => a.Alias, StringComparer.Ordinal);
        var usages = new List<TestDataAssetUsage>();
        foreach (var source in _references) usages.AddRange(await source.UsagesAsync(ct));
        foreach (var usage in registry.Usages())
            usages.Add(new(byId.GetValueOrDefault(usage.AssetId) ?? AliasOf(usage.AssetId), BindingConsumer, usage.Consumer.Key, usage.Consumer.DisplayName, usage.Usage)
                { EnvironmentId = usage.EnvironmentId });
        return [.. usages.Select(u => aliases.Contains(u.Alias) ? u : u with { Unresolved = true })];
    }

    public async Task<IReadOnlyList<TestDataAssetUsage>> UsagesAsync(string alias, CancellationToken ct) =>
        [.. (await UsagesAsync(ct)).Where(u => string.Equals(u.Alias, alias, StringComparison.Ordinal))];

    private static string AliasOf(string assetId) => assetId.Contains(':') ? assetId[(assetId.LastIndexOf(':') + 1)..] : assetId;

    private TestDataAsset? Asset(string assetId) => catalog.Assets().FirstOrDefault(a => a.Id == assetId);

    // ── Edit ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    public async Task<TestDataEditView?> EditViewAsync(string assetId, CancellationToken ct)
    {
        if (Asset(assetId) is not { } asset) return null;
        var editor = EditorOf(asset);
        var valueEditable = asset.ValueEditable && editor is not null;
        return new TestDataEditView
        {
            Asset = catalog.Summary(asset),
            Role = editor?.Role(asset),
            // Only here, and only for a value Test Data owns and may edit (the asset table never carries it).
            Value = valueEditable ? asset.Value?.Reveal() : null,
            LabelEditable = valueEditable || asset.Ownership == TestDataOwnership.Registered,
            ValueEditable = valueEditable,
            ValueNote = valueEditable ? null : ValueNote(asset),
            CanRestoreDefault = asset.Ownership == TestDataOwnership.ProtectedDefault && editor?.CanRestoreDefault(asset) == true,
            MetadataEditable = asset.Ownership == TestDataOwnership.Registered && !asset.MetadataFixed,
            Usages = await UsagesAsync(asset.Alias, ct),
        };
    }

    private static string ValueNote(TestDataAsset asset) => asset.Ownership switch
    {
        TestDataOwnership.Configuration => "Supplied by the backend's configuration; change it there. BirkNext never edits explicit configuration.",
        TestDataOwnership.Registered => "A registered asset records metadata and structure only; its value is supplied by a configured value source under the same alias.",
        TestDataOwnership.Generated => "An XML/JSON payload is never changed in place. Regenerate it as a new draft from Test Data → Create XML fixture; the approved version stays unchanged.",
        _ => "This asset's value cannot be edited in Test Data.",
    };

    /// <summary>
    /// Edits the label and/or the value. The asset id, alias, type, role, scope and origin never change, so every consumer keeps referencing the
    /// same asset; future uses resolve the new value, while completed runs and generated fixtures keep what they recorded. Payloads are never
    /// edited in place.
    /// </summary>
    public async Task<(TestDataEditResult Result, bool NotFound)> EditAsync(string assetId, TestDataEditRequest request, CancellationToken ct)
    {
        if (Asset(assetId) is not { } asset) return (new(false, "The asset no longer exists in Test Data.", false, null, []), true);
        var label = request.Label?.Trim();
        var value = request.Value?.Trim();
        if (label is { Length: > 100 }) return (new(false, "The name is too long (at most 100 characters).", false, null, []), false);
        var valueChanged = value is { Length: > 0 } && value != asset.Value?.Reveal()?.Trim();
        var labelChanged = label is { Length: > 0 } && label != asset.Label;
        var role = request.SemanticRole is null ? asset.SemanticRole : Role(request.SemanticRole);
        var contexts = request.AppliesTo is null ? asset.AppliesTo : Contexts(request.AppliesTo) ?? [];
        var roleChanged = role != asset.SemanticRole;
        var metadataChanged = roleChanged || !contexts.SequenceEqual(asset.AppliesTo);
        if (!valueChanged && !labelChanged && !metadataChanged) return (new(false, "Nothing changed.", false, catalog.Summary(asset), []), false);
        if (metadataChanged)
        {
            if (asset.MetadataFixed || asset.Ownership != TestDataOwnership.Registered)
                return (new(false, "The semantic role and Applies to of this asset are declared by its source and cannot be changed here.", false, null, []), false);
            if (request.SemanticRole is { Length: > 0 } && role is null) return (new(false, "The semantic role must be letters only (at most 40), e.g. Reporter or PersonId.", false, null, []), false);
            // A role change that makes bound consumers incompatible is shown first and needs explicit confirmation; bindings are never changed here.
            var bound = (await UsagesAsync(asset.Alias, ct)).Where(u => u.ConsumerType == BirkNext.Integrations.ConnectedSystemTestDataMatcher.ConsumerType).ToList();
            if (roleChanged && bound.Count > 0 && !request.ConfirmRoleImpact)
                return (new(false, $"Changing the role affects {bound.Count} bound consumer(s): {string.Join("; ", bound.Select(b => $"{b.ConsumerName} · {b.Location}"))}. "
                    + "They stay bound and will show Needs review. Confirm to change the role.", false, null, bound), false);
            registry.UpdateMetadata(asset.Alias, role, contexts);
        }

        if (asset.ValueEditable && EditorOf(asset) is { } editor)
        {
            if (editor.Edit(asset, labelChanged ? label : null, valueChanged ? value : null, time.GetUtcNow()) is { } problem) return (new(false, problem, false, null, []), false);
        }
        else if (asset.Ownership == TestDataOwnership.Registered && !valueChanged)
        {
            if (labelChanged) registry.UpdateLabel(asset.Alias, label!);
        }
        else if (!labelChanged && !valueChanged) { }
        else
            return (new(false, valueChanged ? ValueNote(asset) : "This asset's name is owned by its source and cannot be edited here.", false, null, []), false);

        var updated = Asset(assetId);
        var usages = valueChanged ? await UsagesAsync(asset.Alias, ct) : [];
        var message = valueChanged
            ? $"Test Data asset updated. {asset.Alias} keeps its id; future uses resolve the new value. Completed runs and generated fixtures are not rewritten."
            : "Test Data asset updated.";
        return (new(true, message, valueChanged, updated is null ? null : catalog.Summary(updated), usages), false);
    }

    /// <summary>"Restore default value": a protected default goes back to its seeded value (explicit, separate from reset).</summary>
    public async Task<(TestDataEditResult Result, bool NotFound)> RestoreDefaultAsync(string assetId, CancellationToken ct)
    {
        if (Asset(assetId) is not { } asset) return (new(false, "The asset no longer exists in Test Data.", false, null, []), true);
        if (asset.Ownership != TestDataOwnership.ProtectedDefault || EditorOf(asset) is not { } editor || !editor.CanRestoreDefault(asset))
            return (new(false, "Only a protected default has a default value to restore.", false, null, []), false);
        if (editor.RestoreDefault(asset, time.GetUtcNow(), out var changed) is { } problem) return (new(false, problem, false, null, []), false);
        var updated = Asset(assetId);
        return (new(true, changed ? "Default value restored." : "The default value is already in use.", changed, updated is null ? null : catalog.Summary(updated),
            changed ? await UsagesAsync(asset.Alias, ct) : []), false);
    }

    /// <summary>A semantic role id (letters only, at most 40) or null; "" clears it.</summary>
    public static string? Role(string? role) => role?.Trim() is { Length: > 0 and <= 40 } r && r.All(char.IsLetter) ? r : null;

    /// <summary>Known context ids only (unknown ids are dropped, never stored as free text); null when none.</summary>
    public static List<string>? Contexts(IEnumerable<string>? ids) =>
        ids?.Select(i => i.Trim()).Where(TestDataContexts.Valid).Distinct(StringComparer.Ordinal).ToList() is { Count: > 0 } list ? list : null;

    // ── Delete / Replace ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Deletes one local asset. Protected defaults and configuration-owned assets are refused (whatever the caller); an asset in use is refused
    /// unless <paramref name="leaveReferencesUnresolved"/> — then its references stay, unresolved, and the consumers show their missing state.
    /// Generated fixtures live in the browser workspace and are removed there.
    /// </summary>
    public async Task<(TestDataDeleteResult Result, int Status)> DeleteAsync(string assetId, bool leaveReferencesUnresolved, CancellationToken ct)
    {
        if (Asset(assetId) is not { } asset) return (new(assetId, AliasOf(assetId), false, "The asset no longer exists in Test Data.", []), 404);
        if (TestDataOwnerships.WhyNotDeletable(asset.Ownership) is { } why) return (new(asset.Id, asset.Alias, false, why, []), 409);
        var usages = await UsagesAsync(asset.Alias, ct);
        if (usages.Count > 0 && !leaveReferencesUnresolved)
            return (new(asset.Id, asset.Alias, false, $"{asset.Alias} is used by {usages.Count} reference{(usages.Count == 1 ? "" : "s")}. Replace them, or confirm deleting and leaving them unresolved.", usages), 409);
        var removed = asset.Ownership switch
        {
            TestDataOwnership.Registered => registry.RemoveAsset(asset.Alias),
            TestDataOwnership.SessionEntered => EditorOf(asset)?.Remove(asset) == true,
            _ => false,
        };
        if (!removed)
            return (new(asset.Id, asset.Alias, false, asset.Ownership == TestDataOwnership.Generated
                ? "A generated fixture is removed from its saved XML in the browser workspace."
                : "The asset could not be deleted.", []), 409);
        registry.ForgetLifecycle(asset.Id);
        return (new(asset.Id, asset.Alias, true, usages.Count == 0 ? $"Deleted {asset.Alias}."
            : $"Deleted {asset.Alias}. {usages.Count} reference{(usages.Count == 1 ? " is" : "s are")} now unresolved and require remapping.",
            [.. usages.Select(u => u with { Unresolved = true })]), 200);
    }

    /// <summary>Why <paramref name="replacement"/> cannot stand in for <paramref name="original"/>, or null: same type and data type, the same semantic
    /// role, a compatible scope, approved and not deprecated, and not the asset itself.</summary>
    public string? Incompatibility(TestDataAssetSummary original, TestDataAssetSummary replacement)
    {
        if (replacement.Id == original.Id) return "An asset cannot replace itself.";
        if (replacement.Kind != original.Kind || replacement.DataType != original.DataType) return "Different type: only an asset of the same type and data type can replace it.";
        var roles = _editors.SelectMany(e => e.RoleCapabilities).ToHashSet(StringComparer.Ordinal);
        if (original.Capabilities.Where(roles.Contains).Any(role => !replacement.Capabilities.Contains(role))) return "Different role: an asset is replaced only by one with the same role.";
        if (original.EntityType is not null && replacement.EntityType != original.EntityType) return "Different entity type.";
        if (replacement.Environment is not null && !string.Equals(replacement.Environment, original.Environment, StringComparison.OrdinalIgnoreCase))
            return "Scoped to another environment.";
        if (replacement.Status is TestDataAssetStatus.Deprecated || replacement.Approval != TestDataApproval.Approved) return "Not approved: only an approved asset can take over references.";
        return null;
    }

    /// <summary>The compatible replacements for an asset (Replace references picker): never the closest guess, only what passes every rule.</summary>
    public IReadOnlyList<TestDataAssetSummary> Replacements(string assetId)
    {
        if (Asset(assetId) is not { } asset) return [];
        var original = catalog.Summary(asset);
        return [.. catalog.Assets().Select(catalog.Summary).Where(candidate => Incompatibility(original, candidate) is null).OrderBy(a => a.Alias, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Re-points every reference of an asset to a compatible replacement through each reference source, plus explicit bindings (XML field
    /// mappings are re-pointed in the browser workspace). Aliases only — no value is copied; history stays as it was.
    /// </summary>
    public async Task<(TestDataReplaceResult Result, int Status)> ReplaceAsync(string assetId, string replacementId, CancellationToken ct)
    {
        if (Asset(assetId) is not { } asset) return (new(false, "The asset no longer exists in Test Data.", 0, []), 404);
        if (Asset(replacementId) is not { } replacement) return (new(false, "The replacement asset does not exist.", 0, []), 404);
        if (Incompatibility(catalog.Summary(asset), catalog.Summary(replacement)) is { } why) return (new(false, $"{replacement.Alias} cannot replace {asset.Alias}: {why}", 0, []), 409);
        var before = await UsagesAsync(asset.Alias, ct);
        foreach (var source in _references) await source.ReplaceAsync(asset.Alias, replacement.Alias, ct);
        registry.ReplaceUsages(asset.Id, replacement.Id);
        return (new(true, $"{before.Count} reference{(before.Count == 1 ? "" : "s")} now use {replacement.Alias} instead of {asset.Alias}.", before.Count,
            [.. before.Select(u => u with { Alias = replacement.Alias })]), 200);
    }

    // ── Reset Test Data ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>What Reset Test Data would preserve, remove and leave unresolved. Ownership decides, never the alias.</summary>
    public async Task<TestDataResetPreview> PreviewResetAsync(CancellationToken ct)
    {
        var assets = catalog.Assets();
        var removable = assets.Where(a => TestDataOwnerships.Deletable(a.Ownership)).ToList();
        var removedAliases = removable.Select(a => a.Alias).ToHashSet(StringComparer.Ordinal);
        return new TestDataResetPreview
        {
            ProtectedDefaults = [.. assets.Where(a => a.Ownership == TestDataOwnership.ProtectedDefault).Select(a => a.Alias).Order(StringComparer.Ordinal)],
            ConfigurationAssets = assets.Count(a => a.Ownership == TestDataOwnership.Configuration),
            AssetsToRemove = [.. removable.Select(a => a.Alias).Order(StringComparer.Ordinal)],
            GeneratedFixturesToRemove = removable.Count(a => a.Ownership == TestDataOwnership.Generated),
            // Explicit bindings are removed with the registry; every other reference to a removed asset stays and becomes unresolved.
            AffectedReferences = [.. (await UsagesAsync(ct)).Where(u => removedAliases.Contains(u.Alias) && u.ConsumerType != BindingConsumer)],
        };
    }

    private int ClearLocal() => registry.ClearAll() + _editors.Sum(e => e.ClearLocal());

    /// <summary>
    /// Reset Test Data: removes every registered asset (manual, source-candidate, observed) with its lifecycle, requirements and bindings, and every
    /// deletable asset an editor owns. Protected defaults keep their current (possibly edited) values; backend configuration is untouched.
    /// Generated fixtures are removed from the browser workspace by the caller (counted in <paramref name="generatedFixturesRemoved"/>).
    /// Consumers are not deleted: their references to removed assets are reported as requiring remapping. Source Analysis evidence is not touched,
    /// so candidates can be rediscovered.
    /// </summary>
    public async Task<TestDataResetResult> ResetAsync(int generatedFixturesRemoved, CancellationToken ct)
    {
        var preview = await PreviewResetAsync(ct);
        var removed = ClearLocal();
        var unresolved = (await UsagesAsync(ct)).Where(u => u.Unresolved).ToList();
        return new(new TestDataResetSummary
        {
            ProtectedDefaultsPreserved = preview.ProtectedDefaults.Count,
            ConfigurationAssetsPreserved = preview.ConfigurationAssets,
            AssetsRemoved = removed + generatedFixturesRemoved,
            GeneratedFixturesRemoved = generatedFixturesRemoved,
            ReferencesRequiringRemapping = unresolved.Count,
        }, unresolved);
    }

    /// <summary>
    /// The Test Data part of the full Maintenance reset, after the database (consumers, generated fixtures, workspaces) was cleared. <paramref name="before"/>
    /// is the preview taken before the database was cleared, so the counts are real.
    /// </summary>
    public async Task<TestDataResetSummary> CompleteFullResetAsync(TestDataResetPreview before, CancellationToken ct)
    {
        var removed = ClearLocal();
        var unresolved = (await UsagesAsync(ct)).Count(u => u.Unresolved);
        return new TestDataResetSummary
        {
            ProtectedDefaultsPreserved = before.ProtectedDefaults.Count,
            ConfigurationAssetsPreserved = before.ConfigurationAssets,
            AssetsRemoved = removed + before.GeneratedFixturesToRemove,
            GeneratedFixturesRemoved = before.GeneratedFixturesToRemove,
            ReferencesRemovedWithConsumers = before.AffectedReferences.Count,
            ReferencesRequiringRemapping = unresolved,
        };
    }
}
