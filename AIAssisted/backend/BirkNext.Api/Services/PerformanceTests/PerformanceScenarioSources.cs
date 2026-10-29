using BirkNext.Api.Services.SchemaEvidence;
using BirkNext.Api.Services.SourceAnalysis;
using BirkNext.Api.Services.TestData;
using BirkNext.PerformanceTests;
using BirkNext.SourceDomains;
using BirkNext.TestData;

namespace BirkNext.Api.Services.PerformanceTests;

/// <summary>
/// The Smart Scenario Engine's backend side: Source Analysis snapshots that hold API contracts (listed for an explicit choice, never the newest
/// in its place), the stored contracts of one exact snapshot, and drift of saved steps against a newer analysis of the same source. Reads stored
/// evidence only — nothing is re-parsed, bound or executed.
/// </summary>
public sealed class PerformanceScenarioSources(IReviewSourceEvidenceProvider sources)
{
    public async Task<IReadOnlyList<ScenarioSourceSnapshot>> SnapshotsAsync(string environmentId, CancellationToken ct)
    {
        if (!sources.SourceAnalysisEnabled) return [];
        var result = new List<ScenarioSourceSnapshot>();
        foreach (var snapshot in await sources.ListAsync(environmentId, ct))
        {
            var api = (SourceSnapshotContractProvider.Source(snapshot)?.Evidence.Contracts ?? []).Where(PerformanceScenarioEngine.IsApiContract).ToList();
            if (api.Count > 0)
                result.Add(new(snapshot.Id, snapshot.Archive.FileName, snapshot.AnalyzedAt, api.Count(c => c.Type == SourceContractType.OpenApi), api.Count(c => c.Type == SourceContractType.GraphQlSchema)));
        }
        return result;
    }

    public async Task<ScenarioContractCatalog?> ContractsAsync(string environmentId, Guid snapshotId, CancellationToken ct)
    {
        if (!sources.SourceAnalysisEnabled || await sources.ResolveAsync(environmentId, snapshotId, ct) is not { } snapshot) return null;
        var api = (SourceSnapshotContractProvider.Source(snapshot)?.Evidence.Contracts ?? []).Where(PerformanceScenarioEngine.IsApiContract).ToList();
        return new(snapshot.Id, snapshot.Archive.FileName, snapshot.AnalyzedAt, api);
    }

    /// <summary>
    /// Contract drift of each declared step: the same contract file in the newest later analysis of the same archive. A changed operation is
    /// "Contract changed · review required" (never rewritten); a removed one says so. Steps without declared provenance are not compared.
    /// </summary>
    public async Task<IReadOnlyList<(HttpPerformanceStep Step, string Text)>> DriftAsync(PerformanceTestDefinition d, CancellationToken ct)
    {
        var steps = d.Scenario.Steps.Where(s => !s.Disabled && s.Source is { SnapshotId: not null, ContractFile: not null, OperationFingerprint: not null, Modified: false }
            && s.Source.Origin is ScenarioOperationOrigin.OpenApi or ScenarioOperationOrigin.GraphQl).ToList();
        if (steps.Count == 0 || !sources.SourceAnalysisEnabled) return [];
        var all = await sources.ListAsync(d.EnvironmentId, ct);
        var result = new List<(HttpPerformanceStep, string)>();
        foreach (var group in steps.GroupBy(s => s.Source!.SnapshotId!.Value))
        {
            if (all.FirstOrDefault(s => s.Id == group.Key) is not { } original) continue;
            var newer = all.Where(s => s.AnalyzedAt > original.AnalyzedAt && string.Equals(s.Archive.FileName, original.Archive.FileName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(s => s.AnalyzedAt).FirstOrDefault();
            if (newer is null) continue;
            var contracts = SourceSnapshotContractProvider.Source(newer)?.Evidence.Contracts ?? [];
            foreach (var step in group)
            {
                var source = step.Source!;
                if (contracts.FirstOrDefault(c => string.Equals(c.File, source.ContractFile, StringComparison.Ordinal)) is not { } contract)
                { result.Add((step, $"Contract changed · review required: {source.ContractFile} is no longer in {newer.Archive.FileName} (analyzed {newer.AnalyzedAt.ToUniversalTime():yyyy-MM-dd HH:mm} UTC).")); continue; }
                var current = contract.Operations.Select(op => PerformanceScenarioEngine.Describe(contract, op)).FirstOrDefault(op => SameOperation(op.Key, source.OperationKey));
                if (current is null) { result.Add((step, $"Contract changed · review required: the operation of step '{step.Name}' is no longer declared in {contract.Name}.")); continue; }
                var drift = PerformanceScenarioEngine.Drift(source.OperationFingerprint, current.Signature);
                if (drift.Count > 0) result.Add((step, $"{PerformanceScenarioEngine.DriftText(drift)} (step '{step.Name}', newer analysis of {newer.Archive.FileName})."));
            }
        }
        return result;
    }

    /// <summary>Operation identity across analyses: the part after the contract id (method + path, or GraphQL kind + field).</summary>
    private static bool SameOperation(string a, string b) => string.Equals(a[(a.IndexOf('#') + 1)..], b[(b.IndexOf('#') + 1)..], StringComparison.Ordinal);
}

/// <summary>
/// Central Test Data for a performance definition: the step mappings (aliases) checked against the catalog for readiness, and resolved — by the
/// backend, at run time only — into a transient data profile for the provider. Values are never persisted with the definition or the run.
/// </summary>
public static class PerformanceCentralTestData
{
    /// <summary>Executed steps' Test Data mappings (other runtime value sources never touch Test Data).</summary>
    public static IEnumerable<(HttpPerformanceStep Step, ScenarioDataMapping Mapping)> Mappings(PerformanceTestDefinition d) =>
        d.Scenario.Steps.Where(s => !s.Disabled).SelectMany(s => (s.DataMappings ?? []).Where(m => m.Source == RuntimeValueSource.TestData).Select(m => (s, m)));

    /// <summary>Test Data scenario variables referenced by an executed step.</summary>
    public static IEnumerable<ScenarioVariable> Variables(PerformanceTestDefinition d)
    {
        var used = d.Scenario.Steps.Where(s => !s.Disabled).SelectMany(s => s.DataMappings ?? []).Where(m => m.Source == RuntimeValueSource.ScenarioVariable)
            .Select(m => m.ScenarioVariable).ToHashSet(StringComparer.Ordinal);
        return (d.Scenario.Variables ?? []).Where(v => v.Source == RuntimeValueSource.TestData && v.AssetAlias is not null && used.Contains(v.Name));
    }

    /// <summary>The data-profile column of a Test Data scenario variable (cannot collide with a placeholder name).</summary>
    public static string VariableColumn(string name) => "var:" + name;

    /// <summary>Problems of the mapped aliases: unknown, not approved (or expired/disabled), wrong data type, or for another environment.</summary>
    public static List<string> Issues(PerformanceTestDefinition d, TestDataCatalog catalog, DateTimeOffset now)
    {
        var assets = catalog.Assets().ToDictionary(a => a.Alias, StringComparer.Ordinal);
        var issues = new List<string>();
        foreach (var (step, mapping) in Mappings(d).Concat(Variables(d).Select(v => (Step: new HttpPerformanceStep { Name = $"scenario variable {v.Name}" },
            Mapping: new ScenarioDataMapping(v.Name, [v.AssetAlias!], TestDataDataType.SimpleValues, "variable")))))
        {
            if (mapping.AssetAliases.Count == 0) { issues.Add($"Test Data missing for {Label(mapping)} (step '{step.Name}')."); continue; }
            foreach (var alias in mapping.AssetAliases)
            {
                if (!assets.TryGetValue(alias, out var asset)) { issues.Add($"Test Data review required: {alias} (for {Label(mapping)}) no longer exists in Test Data."); continue; }
                var approval = TestDataResolver.EffectiveApproval(asset, now);
                if (approval != TestDataApproval.Approved) issues.Add($"Test Data review required: {alias} (for {Label(mapping)}) is {approval.ToString().ToLowerInvariant()}.");
                else if (TestDataDataTypes.Of(asset.Kind, asset.MediaType) != mapping.DataType)
                    issues.Add($"Test Data review required: {alias} is {TestDataDataTypes.Label(TestDataDataTypes.Of(asset.Kind, asset.MediaType))}, but {Label(mapping)} needs {TestDataDataTypes.Label(mapping.DataType)}.");
                else if (asset.Environment is { } env && !string.Equals(env, d.EnvironmentName, StringComparison.OrdinalIgnoreCase) && !string.Equals(env, d.EnvironmentType, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(env, d.EnvironmentId, StringComparison.OrdinalIgnoreCase))
                    issues.Add($"Test Data review required: {alias} is scoped to {env}, not {d.EnvironmentName}.");
                else if (asset.Value is null) issues.Add($"Test Data review required: {alias} has no value available to the backend.");
            }
        }
        return issues;
    }

    private static string Label(ScenarioDataMapping m) => m.In == "body" ? "the request body" : m.In == "variable" ? $"${m.Variable}" : $"{{{m.Variable}}}";

    /// <summary>A transient profile for one run: one column per mapped variable, row i = the i-th value of each (cycled). Never stored.</summary>
    public static PerformanceTestDataProfile? Resolve(PerformanceTestDefinition d, TestDataCatalog catalog)
    {
        var columns = Mappings(d).GroupBy(x => x.Mapping.Variable, StringComparer.Ordinal)
            .Select(g => (Variable: g.Key, Values: g.SelectMany(x => x.Mapping.AssetAliases).Distinct(StringComparer.Ordinal).Select(catalog.Reveal).OfType<string>().ToList()))
            .Concat(Variables(d).Select(v => (Variable: VariableColumn(v.Name), Values: new[] { catalog.Reveal(v.AssetAlias!) }.OfType<string>().ToList())))
            .Where(c => c.Values.Count > 0).ToList();
        if (columns.Count == 0) return null;
        var rows = Enumerable.Range(0, columns.Max(c => c.Values.Count)).Select(i => columns.Select(c => c.Values[i % c.Values.Count]).ToList()).ToList();
        return new PerformanceTestDataProfile
        {
            Id = "central-test-data", Name = "Central Test Data (resolved for this run)", Columns = [.. columns.Select(c => c.Variable)], Rows = rows,
            Selection = d.Scenario.DataSelection ?? TestDataSelection.RoundRobin, ApprovedSynthetic = true,
        };
    }
}
