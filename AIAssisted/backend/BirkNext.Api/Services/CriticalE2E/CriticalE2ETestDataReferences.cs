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
                foreach (var alias in mapping.AssetAliases.Distinct(StringComparer.Ordinal))
                {
                    mapped.Add(alias);
                    usages.Add(new(alias, Consumer, flow.Id, flow.Name, $"Placeholder · {mapping.Variable}") { EnvironmentId = flow.EnvironmentId });
                }
            foreach (var alias in (flow.TestDataAliases ?? []).Where(a => !mapped.Contains(a)).Distinct(StringComparer.Ordinal))
                usages.Add(new(alias, Consumer, flow.Id, flow.Name, "Flow Test Data") { EnvironmentId = flow.EnvironmentId });
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
                TestDataAliases = flow.TestDataAliases is null ? null : Swap(flow.TestDataAliases),
                DataMappings = flow.DataMappings is null ? null : [.. flow.DataMappings.Select(m => m.AssetAliases.Contains(fromAlias) ? m with { AssetAliases = Swap(m.AssetAliases) } : m)],
            });
            changed++;
        }
        return Task.FromResult(changed);
    }
}
