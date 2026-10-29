using BirkNext.Api.Services.TestData;
using BirkNext.PerformanceTests;
using BirkNext.TestData;

namespace BirkNext.Api.Services.PerformanceTests;

/// <summary>
/// Performance scenarios as Test Data consumers: Test Data step mappings and scenario variables of active definitions. Archived definitions and
/// completed runs are history — never listed as current usages and never re-pointed.
/// </summary>
public sealed class PerformanceTestDataReferences(PerformanceTestStore store, TimeProvider time) : ITestDataReferenceSource
{
    public const string Consumer = "Performance scenario";

    public async Task<IReadOnlyList<TestDataAssetUsage>> UsagesAsync(CancellationToken ct)
    {
        var usages = new List<TestDataAssetUsage>();
        foreach (var definition in await store.AllActiveDefinitionsAsync(ct))
        {
            var steps = definition.Scenario.Steps;
            for (var i = 0; i < steps.Count; i++)
                foreach (var mapping in steps[i].DataMappings ?? [])
                    if (mapping.Source == RuntimeValueSource.TestData)
                        foreach (var alias in mapping.AssetAliases.Distinct(StringComparer.Ordinal))
                            usages.Add(new(alias, Consumer, definition.Id, definition.Name,
                                $"Step {i + 1}{(steps[i].Name is { Length: > 0 } name ? $" ({name})" : "")} · {mapping.Variable}") { EnvironmentId = definition.EnvironmentId });
            foreach (var variable in definition.Scenario.Variables ?? [])
                if (variable is { Source: RuntimeValueSource.TestData, AssetAlias: { Length: > 0 } alias })
                    usages.Add(new(alias, Consumer, definition.Id, definition.Name, $"Scenario variable · {variable.Name}") { EnvironmentId = definition.EnvironmentId });
        }
        return usages;
    }

    /// <summary>Saves each affected definition as a new version (its runs keep the version they executed).</summary>
    public async Task<int> ReplaceAsync(string fromAlias, string toAlias, CancellationToken ct)
    {
        List<string> Swap(List<string> aliases) => [.. aliases.Select(a => a == fromAlias ? toAlias : a).Distinct(StringComparer.Ordinal)];
        var changed = 0;
        foreach (var definition in await store.AllActiveDefinitionsAsync(ct))
        {
            var scenario = definition.Scenario;
            var uses = scenario.Steps.Any(s => (s.DataMappings ?? []).Any(m => m.Source == RuntimeValueSource.TestData && m.AssetAliases.Contains(fromAlias)))
                || (scenario.Variables ?? []).Any(v => v.Source == RuntimeValueSource.TestData && v.AssetAlias == fromAlias);
            if (!uses) continue;
            await store.SaveDefinitionAsync(definition.EnvironmentId, definition with
            {
                Scenario = scenario with
                {
                    Steps = [.. scenario.Steps.Select(s => s.DataMappings is null ? s : s with
                    {
                        DataMappings = [.. s.DataMappings.Select(m => m.Source == RuntimeValueSource.TestData && m.AssetAliases.Contains(fromAlias) ? m with { AssetAliases = Swap(m.AssetAliases) } : m)],
                    })],
                    Variables = scenario.Variables is null ? null
                        : [.. scenario.Variables.Select(v => v.Source == RuntimeValueSource.TestData && v.AssetAlias == fromAlias ? v with { AssetAlias = toAlias } : v)],
                },
            }, time.GetUtcNow(), ct);
            changed++;
        }
        return changed;
    }
}
