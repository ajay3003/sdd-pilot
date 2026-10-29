using BirkNext.TestData;
using BirkNext.Web.Models;
using BirkNext.Web.Services.SchemaStudio;

namespace BirkNext.Web.Services;

/// <summary>
/// The Test Data lifecycle parts that live in the browser workspace: XML field mappings (references by alias, per test context) and generated
/// XML fixtures (Schema Studio's saved XML, registered as Test Data). The backend owns everything else. Nothing here deletes a mapping or
/// re-points one silently: usages are listed, Replace re-points only on request, and a removed asset leaves its mappings to require remapping.
/// </summary>
public static class TestDataAssetWorkspace
{
    public const string XmlMappingConsumer = "XML mapping";

    /// <summary>"Klient → Identifikator → Fodselsnummer" for a stored schema path.</summary>
    public static string PathText(string path) => string.Join(" → ", path.Split(['/', '.'], StringSplitOptions.RemoveEmptyEntries));

    public static IReadOnlyList<TestDataAssetUsage> XmlUsages(IWorkspaceArtifactRepository? repository, IReadOnlyCollection<string> knownAliases) =>
        repository is null ? [] : [.. repository.SddLifecycle.XmlFixtureSetups
            .SelectMany(setup => setup.Mappings.Where(m => m.AssetAlias is { Length: > 0 } && m.Kind == "TestDataAsset")
                .Select(m => new TestDataAssetUsage(m.AssetAlias!, XmlMappingConsumer, setup.ContextKey, setup.ContextKey, PathText(m.Path))
                    { EnvironmentId = setup.EnvironmentId, Unresolved = !knownAliases.Contains(m.AssetAlias!) }))];

    /// <summary>Re-points XML field mappings from one alias to another (Replace references). Returns how many changed.</summary>
    public static int ReplaceXmlMappings(IWorkspaceArtifactRepository? repository, string fromAlias, string toAlias)
    {
        if (repository is null) return 0;
        var changed = 0;
        foreach (var mapping in repository.SddLifecycle.XmlFixtureSetups.SelectMany(s => s.Mappings).Where(m => m.AssetAlias == fromAlias && m.Kind == "TestDataAsset"))
        {
            mapping.AssetAlias = toAlias;
            changed++;
        }
        return changed;
    }

    /// <summary>
    /// Removes the saved XML of generated fixtures registered under the given aliases (all project scopes) from the workspace artifact. Unregistered
    /// saved XML and fixtures of other aliases stay. Returns how many were removed; the caller persists the workspace.
    /// </summary>
    public static int RemoveGeneratedFixtures(IWorkspaceArtifactRepository? repository, SchemaGeneratedXmlStore? store, IReadOnlySet<string> aliases)
    {
        if (repository is null || store is null || aliases.Count == 0 || repository.Get(WorkspaceArtifactType.SchemaStudioGeneratedXml) is not { } existing) return 0;
        store.RestoreJson(existing.Text);
        var doomed = store.Documents.Where(d => d.TestData is { } registration && aliases.Contains(registration.Alias)).ToList();
        foreach (var document in doomed) store.Remove(document.Id, document.ProjectScope);
        if (doomed.Count > 0)
            repository.Set(WorkspaceArtifactType.SchemaStudioGeneratedXml, store.SnapshotJson(null, includeAllWhenScopeOmitted: true), "schema-studio-generated-xml.json", "Schema Studio saved XML artifacts");
        return doomed.Count;
    }

    /// <summary>"Used by 3" / "Unused" — distinct consumers across the backend count and the workspace's XML mappings.</summary>
    public static int Consumers(TestDataAssetSummary asset, IReadOnlyList<TestDataAssetUsage> workspaceUsages) =>
        asset.Consumers + workspaceUsages.Where(u => u.Alias == asset.Alias).Select(u => u.ConsumerId).Distinct(StringComparer.Ordinal).Count();

    public static string UsedBy(int consumers) => consumers == 0 ? "Unused" : $"Used by {consumers}";

    /// <summary>Where a usage can be opened, when the consumer has a page that accepts it.</summary>
    public static string? Href(TestDataAssetUsage usage) => usage.ConsumerType switch
    {
        "Performance scenario" => $"performance-test-review?definition={Uri.EscapeDataString(usage.ConsumerId)}",
        "Critical E2E flow" => "critical-e2e-regression",
        XmlMappingConsumer => "test-data#td-xml-fixtures",
        _ => null,
    };

    public static string OpenLabel(TestDataAssetUsage usage) => usage.ConsumerType switch
    {
        "Performance scenario" => "Open scenario",
        "Critical E2E flow" => "Open flow",
        XmlMappingConsumer => "Open XML fixture",
        _ => "Open",
    };

    /// <summary>Generated fixtures built from an earlier value of <paramref name="alias"/> (the fixture is never rewritten; it needs regeneration).</summary>
    public static IReadOnlyList<TestDataAssetSummary> FixturesNeedingRegeneration(IReadOnlyList<TestDataAssetSummary> assets, string alias) =>
        [.. assets.Where(a => a.Ownership == TestDataOwnership.Generated && TestDataValueVersions.Changed(a, assets).Any(c => c.Alias == alias))];
}
