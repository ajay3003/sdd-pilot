using BirkNext.ContractTesting;

namespace BirkNext.Api.Services.ContractTesting;

/// <summary>
/// Bulk import of Source Analysis contracts: each item goes through the one registry import (<see cref="ContractRegistryService.ImportFromSourceAsync"/>,
/// which continues an earlier import of the same source contract as its next version, never a second contract), then — when asked — the one test
/// generator (<see cref="ContractTestService.GenerateAsync"/>) and the one test-data generator (<see cref="ContractTestDataService"/>). No logic of its
/// own beyond orchestration and counting; nothing is ever sent to an API.
/// </summary>
public sealed class ContractBulkImportService(ContractRegistryService registry, ContractTestService tests, ContractTestDataService data)
{
    public const int MaxItems = 200;

    public async Task<SourceContractBulkImportResult> ImportAsync(SourceContractBulkImportRequest request, CancellationToken ct)
    {
        var items = new List<SourceContractBulkItem>();
        var candidates = (await registry.SourceCandidatesAsync(ct)).ToDictionary(c => (c.SnapshotId, c.ContractId));
        foreach (var item in request.Items.DistinctBy(i => (i.SnapshotId, i.SourceContractId)).Take(MaxItems))
        {
            candidates.TryGetValue((item.SnapshotId, item.SourceContractId), out var candidate);
            var file = candidate?.File ?? item.SourceContractId;
            var name = candidate?.Name ?? item.SourceContractId;
            var before = candidate?.ContractVersion;
            var import = await registry.ImportFromSourceAsync(new SourceContractImportRequest(item.SnapshotId, item.SourceContractId, null, null), ct);
            if (import.NeedsReview)
            {
                // A possible move/rename of an existing contract of the project: never imported as a second contract without a person's decision.
                items.Add(new(item.SourceContractId, file, name, BulkImportOutcome.NeedsReview, null, null, import.Message));
                continue;
            }
            if (import.Key is not { } key || import.Version is not { } version || !(import.Imported || import.Unchanged))
            {
                items.Add(new(item.SourceContractId, file, name, BulkImportOutcome.Failed, import.Key, import.Version, import.Message));
                continue;
            }
            var outcome = import.Moved ? BulkImportOutcome.Moved : import.Unchanged ? BulkImportOutcome.Unchanged
                : before is not null && version > 1 ? BulkImportOutcome.NewVersion : BulkImportOutcome.Imported;
            var result = new SourceContractBulkItem(item.SourceContractId, file, name, outcome, key, version, import.Message);
            if (request.GenerateTests)
            {
                var (created, skipped) = await tests.GenerateAsync(key, version, null, ct);
                result = result with
                {
                    TestsCreated = created.Count,
                    TestsExisting = skipped.Count(s => s.EndsWith(": already exists.", StringComparison.Ordinal)),
                    Unsupported = skipped.Count(s => !s.EndsWith(": already exists.", StringComparison.Ordinal)),
                };
            }
            if (request.GenerateTestData && await data.GenerateAsync(key, new ContractTestDataRequest(version, null, ContractTestDataDefaults.Seed, request.EnvironmentId), ct) is { } report)
            {
                int Count(params ContractTestDataState[] states) => report.Operations.Count(o => states.Contains(o.State));
                result = result with
                {
                    DataBound = Count(ContractTestDataState.Bound, ContractTestDataState.AlreadyBound), DataNeedsReview = Count(ContractTestDataState.NeedsReview),
                    DataUnresolved = Count(ContractTestDataState.Unresolved, ContractTestDataState.NotSupported), DataNoInputs = Count(ContractTestDataState.NoInputs),
                    DataFailed = Count(ContractTestDataState.Failed),
                };
            }
            items.Add(result);
        }
        return new(items);
    }
}
