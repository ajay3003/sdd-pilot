using BirkNext.ContractSchemaEvidence;
using BirkNext.Integrations;
using BirkNext.SourceDomains;

namespace BirkNext.Api.Services.Integrations;

/// <summary>
/// Reviews one integration's contract relationship: the producer artifact against the consumer artifact, each resolved EXACTLY — a stored contract
/// selected from source (snapshot id + contract id), or a path in the integration's own bound snapshot. A logical contract id is matched only by an
/// exact contract name or file stem, and only to correlate identity; nothing is compared that is not an exact artifact, and nothing is confirmed
/// (confirmation stays a person's decision). Read-only: nothing is fetched or stored.
/// </summary>
public sealed class ContractRelationshipReviewService(IIntegrationCatalogService catalog, IIntegrationContractStore contracts,
    SourceEvidence.IIntegrationEvidenceBindingStore bindings, SourceAnalysis.IReviewSourceEvidenceProvider sources)
{
    private sealed record Side(ContractArtifactRef? Artifact, ContractFact? Fact, string Note);

    public async Task<ContractRelationshipReview?> ReviewAsync(string environmentId, string integrationId, CancellationToken ct)
    {
        var configured = await catalog.GetAsync(environmentId, null, null, ct);
        if (configured.Integrations.FirstOrDefault(item => item.Id == integrationId) is not { } integration) return null;
        var binding = (await bindings.ListAsync(environmentId, ct)).FirstOrDefault(item => item.IntegrationId == integrationId);
        var bound = binding is null || !sources.SourceAnalysisEnabled ? null : await sources.ResolveAsync(environmentId, binding.SnapshotId, ct);
        var artifacts = (await contracts.ListAsync(environmentId, ct)).Where(item => item.IntegrationId == integrationId).ToList();

        var producer = await ResolveAsync(environmentId, integration.ProducerContractReference, artifacts.FirstOrDefault(a => a.Role == IntegrationContractRole.Producer), bound, "producer", ct);
        var consumer = await ResolveAsync(environmentId, integration.ConsumerContractReference, artifacts.FirstOrDefault(a => a.Role == IntegrationContractRole.Consumer), bound, "consumer", ct);
        var review = new ContractRelationshipReview
        {
            IntegrationId = integrationId, Producer = producer.Artifact, Consumer = consumer.Artifact,
            ProducerReference = integration.ProducerContractReference, ConsumerReference = integration.ConsumerContractReference,
            Limitations = [.. new[] { producer.Note, consumer.Note }.Where(note => note.Length > 0),
                "A comparison is structural (fields, types, occurrence, allowed values); runtime compatibility and the active validation relationship are not established by it."],
        };
        if (producer.Fact is { } p && consumer.Fact is { } c)
        {
            if (producer.Artifact!.Key == consumer.Artifact!.Key)
                return review with { State = ContractRelationshipResultState.NotAssessable,
                    Summary = "Producer and consumer point to the same artifact: comparing it with itself proves nothing. Select the producer's own artifact." };
            var (state, differences) = ContractRelationshipComparer.Compare(p, c);
            return review with
            {
                State = state, Differences = differences,
                Summary = $"{ContractRelationshipComparer.Label(state)} ({differences.Count} difference{(differences.Count == 1 ? "" : "s")}) · relationship not confirmed until a person confirms it.",
            };
        }
        // Identity correlation only: the consumer's exact schema is found and carries the producer's logical contract id.
        var correlated = consumer.Fact is { } found && integration.ProducerContractReference is { Length: > 0 } logical && !ContractArtifactPaths.IsPath(logical)
            && ContractArtifactPaths.NamedExactly(found.Contract, logical);
        return review with
        {
            State = ContractRelationshipResultState.NotAssessable,
            Summary = correlated ? "Matching source schema found · relationship not yet confirmed (the producer artifact is not available for a structural comparison)."
                : consumer.Fact is not null ? "Consumer schema found · producer artifact not available, so the relationship cannot be compared."
                : producer.Fact is not null ? "Producer artifact found · consumer artifact not available, so the relationship cannot be compared."
                : "Insufficient evidence: neither side resolves to an exact contract artifact.",
        };
    }

    private async Task<Side> ResolveAsync(string environmentId, string? reference, IntegrationContractArtifact? stored, IqrSourceSnapshot? bound, string side, CancellationToken ct)
    {
        // 1. A contract a person selected from source: exact snapshot + exact contract id.
        if (stored is { SourceSnapshotId: { } snapshotId, SourceContractId: { } contractId })
        {
            var snapshot = await sources.ResolveAsync(environmentId, snapshotId, ct);
            var contract = snapshot?.EvidenceDomains?.Contracts.Contracts.FirstOrDefault(item => item.Id == contractId);
            return contract is null
                ? new(null, null, $"The stored {side} contract ({stored.FileName}) no longer resolves in snapshot {stored.SourceArchive}; nothing is substituted.")
                : new(Ref(snapshot!, contract), Fact(snapshot!, contract), "");
        }
        if (stored is not null) return new(null, null, $"The {side} contract {stored.FileName} was uploaded manually; structural comparison needs a source-analyzed artifact.");
        if (string.IsNullOrWhiteSpace(reference)) return new(null, null, $"No {side} contract is configured.");
        if (bound?.EvidenceDomains?.Contracts.Contracts is not { } inBound)
            return new(null, null, $"The {side} reference {reference} cannot be resolved: the integration has no bound source evidence.");
        // 2. An exact path in the integration's own bound snapshot. 3. A logical id only by exact name/file stem — and only when exactly one matches.
        var matches = ContractArtifactPaths.IsPath(reference)
            ? inBound.Where(item => ContractArtifactPaths.PathMatches(item, reference)).ToList()
            : side == "consumer" ? inBound.Where(item => ContractArtifactPaths.NamedExactly(item, reference)).ToList() : [];
        return matches.Count switch
        {
            1 => new(Ref(bound, matches[0]), Fact(bound, matches[0]), ""),
            0 => new(null, null, ContractArtifactPaths.IsPath(reference) || side == "consumer"
                ? $"The {side} reference {reference} is not an artifact of the bound snapshot {bound.Archive.FileName} (artifact not found; nothing else is opened)."
                : $"The {side} reference {reference} is a logical contract id (Test Pack reference); no {side} artifact is stored, so it cannot be compared."),
            _ => new(null, null, $"The {side} reference {reference} matches {matches.Count} artifacts in {bound.Archive.FileName}; select the exact one (requires manual selection)."),
        };
    }

    private static ContractArtifactRef Ref(IqrSourceSnapshot snapshot, SourceContract contract) =>
        new(snapshot.Id, snapshot.Archive.FileName, ContractArtifactPaths.Normalize(contract.File), contract.Name, contract.Version);

    private static ContractFact Fact(IqrSourceSnapshot snapshot, SourceContract contract) =>
        ContractEvidenceQueries.Fact(new ContractEvidenceSource(snapshot.Id, snapshot.Archive.FileName, snapshot.AnalyzedAt, snapshot.EvidenceDomains!.Contracts), contract);
}
