using BirkNext.Api.Services.SourceAnalysis;
using BirkNext.ContractSchemaEvidence;
using BirkNext.DataModelEvidence;
using BirkNext.TestData;

namespace BirkNext.Api.Services.TestData;

/// <summary>Candidates for one exact Source Analysis snapshot from shared contract and data model evidence: contract structures (XSD, JSON
/// Schema, OpenAPI schemas), operation requirements (OpenAPI request bodies and parameters, GraphQL inputs, variables and result types), enum
/// reference data and data model entities — each with the reason it was proposed. Read-only: a candidate is never approved, stored or used until
/// a person registers and approves it in the Test Data catalog; one already registered is marked with its existing asset.</summary>
public sealed class TestDataCandidateService(IReviewSourceEvidenceProvider sources, TestDataRegistryStore? registry = null)
{
    public const int MaxCandidates = 300;

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
            foreach (var contract in contracts.Contracts.Where(c => c.Type is BirkNext.SourceDomains.SourceContractType.OpenApi or BirkNext.SourceDomains.SourceContractType.GraphQlSchema))
            {
                result.AddRange(TestDataLifecycle.FromOperations(contract, snapshot.Id));
                result.AddRange(TestDataLifecycle.FromEnums(contract, snapshot.Id));
            }
        }
        result.AddRange(TestDataLifecycle.FromDataModel(DataModelQueries.GetEntities(snapshot.DatabaseArchitecture)));
        var repository = ReviewSourceEvidenceProvider.Identity(snapshot).Key;
        var registered = (registry?.Assets() ?? []).Where(a => a.RepositoryKey == repository && a.CandidateId is not null).ToDictionary(a => a.CandidateId!, a => a.Id, StringComparer.Ordinal);
        return [.. result.DistinctBy(c => c.Id).Take(MaxCandidates).Select(c => registered.TryGetValue(c.Id, out var existing) ? c with { ExistingAssetId = existing } : c)];
    }
}
