using BirkNext.Api.Services.SourceAnalysis;
using BirkNext.ContractSchemaEvidence;
using BirkNext.DataModelEvidence;
using BirkNext.TestData;

namespace BirkNext.Api.Services.TestData;

/// <summary>Candidates for one exact Source Analysis snapshot from shared contract and data model evidence. Read-only: a candidate is never
/// approved, stored or used until a person approves it in the Test Data catalog.</summary>
public sealed class TestDataCandidateService(IReviewSourceEvidenceProvider sources)
{
    public const int MaxCandidates = 200;

    public async Task<IReadOnlyList<TestDataCandidate>?> CandidatesAsync(string environmentId, Guid snapshotId, CancellationToken ct)
    {
        if (await sources.ResolveAsync(environmentId, snapshotId, ct) is not { } snapshot) return null;
        var result = new List<TestDataCandidate>();
        if (snapshot.EvidenceDomains?.Contracts is { } contracts)
        {
            var source = new ContractEvidenceSource(snapshot.Id, snapshot.Archive.FileName, snapshot.AnalyzedAt, contracts);
            foreach (var fact in ContractEvidenceQueries.GetContracts(source).Where(f => f.Contract.Type is BirkNext.SourceDomains.SourceContractType.XmlSchema
                or BirkNext.SourceDomains.SourceContractType.JsonSchema or BirkNext.SourceDomains.SourceContractType.OpenApi))
                result.AddRange(TestDataLifecycle.FromContract(fact));
        }
        result.AddRange(TestDataLifecycle.FromDataModel(DataModelQueries.GetEntities(snapshot.DatabaseArchitecture)));
        return [.. result.Take(MaxCandidates)];
    }
}
