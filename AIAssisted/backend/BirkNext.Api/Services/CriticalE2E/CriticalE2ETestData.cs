using BirkNext.Api.Services.TestData;
using BirkNext.CriticalE2E;
using BirkNext.TestData;

namespace BirkNext.Api.Services.CriticalE2E;

/// <summary>
/// Critical E2E consumes the central Test Data catalog; it owns no reusable data of its own. A flow stores aliases (and, per
/// placeholder, which aliases fill it); the values stay in Test Data and are revealed only to the backend while a run executes.
/// Never returned to a client, logged or written to run history.
/// </summary>
public static class CriticalE2ETestData
{
    public const string ConsumerId = "critical-e2e";

    /// <summary>Every alias the flow references: flow-level profile aliases and the aliases behind each mapped placeholder.</summary>
    public static IReadOnlyList<string> Aliases(CriticalE2EFlowDefinition flow) =>
        [.. (flow.TestDataAliases ?? []).Concat((flow.DataMappings ?? []).SelectMany(m => m.AssetAliases)).Where(a => a.Length > 0).Distinct(StringComparer.Ordinal)];

    /// <summary>Problems of the referenced aliases: unmapped placeholder, unknown, not approved (expired/disabled), wrong data type, other environment.</summary>
    public static List<string> Issues(CriticalE2EFlowDefinition flow, TestDataCatalog catalog, DateTimeOffset now, string? environmentName = null, string? environmentType = null)
    {
        var all = catalog.Assets();
        var identities = catalog.Identities?.Identities() ?? [];
        var issues = new List<string>();
        foreach (var mapping in flow.DataMappings ?? [])
        {
            if (mapping.AssetAliases.Count == 0) { issues.Add($"Test Data missing for ${{data:{mapping.Variable}}} ({mapping.Target})."); continue; }
            for (var i = 0; i < mapping.AssetAliases.Count; i++)
                if (Problem(TestDataReferenceResolver.Resolve(all, identities, mapping.AssetAliases[i], TestDataReferenceText.IdAt(mapping.AssetIds, i)), all, now, flow,
                        environmentName, environmentType, mapping.DataType) is { } problem) issues.Add(problem);
        }
        var aliases = flow.TestDataAliases ?? [];
        for (var i = 0; i < aliases.Count; i++)
            if (Problem(TestDataReferenceResolver.Resolve(all, identities, aliases[i], TestDataReferenceText.IdAt(flow.TestDataAssetIds, i)), all, now, flow, environmentName, environmentType, null)
                    is { } problem && !issues.Contains(problem)) issues.Add(problem);
        // A placeholder used in the flow but mapped to nothing would reach the page literally.
        foreach (var variable in Placeholders(flow).Where(v => !(flow.DataMappings ?? []).Any(m => string.Equals(m.Variable, v, StringComparison.Ordinal))))
            issues.Add($"Test Data missing for ${{data:{variable}}} (no Test Data asset is mapped).");
        return issues;
    }

    private static string? Problem(TestDataReferenceResolution reference, IReadOnlyList<TestDataAsset> assets, DateTimeOffset now, CriticalE2EFlowDefinition flow,
        string? environmentName, string? environmentType, TestDataDataType? required)
    {
        if (TestDataReferenceResolver.AssetIn(assets, reference) is not { } asset)
            return reference.State == TestDataReferenceState.LegacyNeedsReview
                ? $"Test Data review required: {reference.StoredAlias} is a legacy reference that needs review. {reference.Detail}"
                : $"Test Data review required: {reference.StoredAlias} no longer exists in Test Data.";
        var alias = asset.Alias;
        var approval = TestDataResolver.EffectiveApproval(asset, now);
        if (approval != TestDataApproval.Approved) return $"Test Data review required: {alias} is {approval.ToString().ToLowerInvariant()}.";
        var type = TestDataDataTypes.Of(asset.Kind, asset.MediaType);
        if (required is { } needed && type != needed)
            return $"Test Data review required: {alias} is {TestDataDataTypes.Label(type)}, but the mapping needs {TestDataDataTypes.Label(needed)}.";
        if (asset.Environment is { } env && !new[] { flow.EnvironmentId, environmentName, environmentType }.Any(e => string.Equals(env, e, StringComparison.OrdinalIgnoreCase)))
            return $"Test Data review required: {alias} is scoped to {env}, not this environment.";
        if (required is not null && asset.Value is null) return $"Test Data review required: {alias} has no value available to the backend.";
        return null;
    }

    /// <summary>${data:variable} placeholders used by step values, routes and the final outcome.</summary>
    public static IReadOnlyList<string> Placeholders(CriticalE2EFlowDefinition flow) =>
        [.. flow.Steps.SelectMany(s => new[] { s.Value, s.Expected, s.ExecutionRoute, s.ExpectedDestination, s.Body, s.PathOrOperation })
            .Append(flow.FinalOutcome?.Expected)
            .SelectMany(text => CriticalE2EVariables.DataPlaceholders(text)).Distinct(StringComparer.Ordinal)];

    /// <summary>Values for one run (variable → value, first mapped alias). Transient: held by the run context only.</summary>
    public static Dictionary<string, string> Resolve(CriticalE2EFlowDefinition flow, TestDataCatalog catalog)
    {
        var all = catalog.Assets();
        var identities = catalog.Identities?.Identities() ?? [];
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var mapping in flow.DataMappings ?? [])
            if (mapping.AssetAliases.Select((alias, i) => TestDataReferenceResolver.RevealIn(all, TestDataReferenceResolver.Resolve(all, identities, alias, TestDataReferenceText.IdAt(mapping.AssetIds, i))))
                    .FirstOrDefault(v => v is not null) is { } value) values[mapping.Variable] = value;
        return values;
    }

    /// <summary>The stable ids of every reference a run uses (recorded in run history; never inferred later from an alias).</summary>
    public static List<string> AssetIds(CriticalE2EFlowDefinition flow, TestDataCatalog catalog)
    {
        var all = catalog.Assets();
        var identities = catalog.Identities?.Identities() ?? [];
        var refs = (flow.DataMappings ?? []).SelectMany(m => m.AssetAliases.Select((alias, i) => (alias, TestDataReferenceText.IdAt(m.AssetIds, i))))
            .Concat((flow.TestDataAliases ?? []).Select((alias, i) => (alias, TestDataReferenceText.IdAt(flow.TestDataAssetIds, i))));
        return [.. refs.Select(r => TestDataReferenceResolver.Resolve(all, identities, r.alias, r.Item2)).Where(r => r.Usable && r.AssetId is not null)
            .Select(r => r.AssetId!).Distinct(StringComparer.Ordinal)];
    }
}
