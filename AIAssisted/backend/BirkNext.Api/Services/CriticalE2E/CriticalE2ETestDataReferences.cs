using BirkNext.Api.Services.TestData;
using BirkNext.TestData;

namespace BirkNext.Api.Services.CriticalE2E;

/// <summary>Critical E2E flows as Test Data consumers: placeholder mappings and the flow's Test Data list. Run history is never changed.</summary>
public sealed class CriticalE2ETestDataReferences(ICriticalE2EStore store) : ITestDataReferenceSource
{
    public const string Consumer = "Critical E2E flow";

    public Task<IReadOnlyList<TestDataAssetUsage>> UsagesAsync(CancellationToken ct)
    {
        var usages = new List<TestDataAssetUsage>();
        foreach (var flow in store.Flows(""))
        {
            var mapped = new HashSet<string>(StringComparer.Ordinal);
            foreach (var mapping in flow.DataMappings ?? [])
                for (var i = 0; i < mapping.AssetAliases.Count; i++)
                {
                    if (!mapped.Add(mapping.AssetAliases[i])) continue;
                    usages.Add(new(mapping.AssetAliases[i], Consumer, flow.Id, flow.Name, $"Placeholder · {mapping.Variable}")
                        { EnvironmentId = flow.EnvironmentId, AssetId = BirkNext.TestData.TestDataReferenceText.IdAt(mapping.AssetIds, i) });
                }
            var aliases = flow.TestDataAliases ?? [];
            for (var i = 0; i < aliases.Count; i++)
                if (mapped.Add(aliases[i]))
                    usages.Add(new(aliases[i], Consumer, flow.Id, flow.Name, "Flow Test Data")
                        { EnvironmentId = flow.EnvironmentId, AssetId = BirkNext.TestData.TestDataReferenceText.IdAt(flow.TestDataAssetIds, i) });
        }
        return Task.FromResult<IReadOnlyList<TestDataAssetUsage>>(usages);
    }

    public Task<int> ReplaceAsync(string fromAlias, string toAlias, CancellationToken ct)
    {
        List<string> Swap(List<string> aliases) => [.. aliases.Select(a => a == fromAlias ? toAlias : a).Distinct(StringComparer.Ordinal)];
        var changed = 0;
        foreach (var flow in store.Flows(""))
        {
            if (!(flow.TestDataAliases ?? []).Contains(fromAlias) && !(flow.DataMappings ?? []).Any(m => m.AssetAliases.Contains(fromAlias))) continue;
            store.Save(flow with
            {
                TestDataAliases = flow.TestDataAliases is null ? null : Swap(flow.TestDataAliases), TestDataAssetIds = null,
                DataMappings = flow.DataMappings is null ? null : [.. flow.DataMappings.Select(m => m.AssetAliases.Contains(fromAlias) ? m with { AssetAliases = Swap(m.AssetAliases), AssetIds = null } : m)],
            });
            changed++;
        }
        return Task.FromResult(changed);
    }
}
