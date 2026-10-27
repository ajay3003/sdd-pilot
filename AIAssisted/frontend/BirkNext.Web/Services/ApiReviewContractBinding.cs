using BirkNext.ApiReview;
using BirkNext.ContractSchemaEvidence;
using BirkNext.Integrations;
using BirkNext.SourceDomains;

namespace BirkNext.Web.Services;

/// <summary>How a REST target's contract evidence was resolved. Association, never trust: Bound means deterministic evidence matched, not
/// approved, deployed or runtime-verified.</summary>
public enum ApiReviewContractBindingState
{
    /// <summary>No OpenAPI contract evidence exists for this environment.</summary>
    NotAvailable,
    /// <summary>An OpenAPI contract URL is configured on the Target Environment (explicit configuration wins).</summary>
    Configured,
    /// <summary>Exactly one Source Analysis OpenAPI contract declares operations the target was observed to serve.</summary>
    SourceBound,
    /// <summary>Several OpenAPI contracts match equally; none is chosen.</summary>
    MultipleCandidates,
    /// <summary>OpenAPI contracts exist in several Source Analysis snapshots; none is chosen.</summary>
    MultipleSnapshots,
    /// <summary>OpenAPI contracts exist, but none declares an operation this target was observed to serve.</summary>
    IdentityNotEstablished,
}

/// <summary>The resolved contract for one REST target, with its provenance. Never a copy of the contract content.</summary>
public sealed record ApiReviewContractBinding(
    ApiReviewContractBindingState State,
    string? ContractName = null,
    string? File = null,
    Guid? SnapshotId = null,
    string? SnapshotSource = null,
    int MatchedOperations = 0,
    int CandidateCount = 0)
{
    /// <summary>"OpenAPI · Source Analysis snapshot", "Not available", "Review required — multiple contract candidates".</summary>
    public string Label => State switch
    {
        ApiReviewContractBindingState.Configured => "OpenAPI · configured on the Target Environment",
        ApiReviewContractBindingState.SourceBound => "OpenAPI · Source Analysis snapshot",
        ApiReviewContractBindingState.MultipleCandidates => "Review required — multiple contract candidates",
        ApiReviewContractBindingState.MultipleSnapshots => "Review required — multiple source snapshots may apply",
        ApiReviewContractBindingState.IdentityNotEstablished => "Not assessed — contract identity not established",
        _ => "Not available",
    };

    /// <summary>What this means for the review, neutrally. A missing or ambiguous contract never blocks the runtime review.</summary>
    public string Meaning => State switch
    {
        ApiReviewContractBindingState.Configured => "Live responses are validated against the configured contract.",
        ApiReviewContractBindingState.SourceBound =>
            $"{File} declares {MatchedOperations} operation{(MatchedOperations == 1 ? "" : "s")} this API was observed to serve. Deterministic evidence association — not approval, deployment or runtime verification. Runtime contract validation needs a published contract URL; the source contract is shown as evidence.",
        ApiReviewContractBindingState.MultipleCandidates => $"{CandidateCount} OpenAPI contracts match equally; none is chosen. Runtime API review can still run; contract compatibility is not assessed until one is selected.",
        ApiReviewContractBindingState.MultipleSnapshots => "OpenAPI evidence exists in several Source Analysis snapshots; the newest is never chosen automatically. Runtime API review can still run.",
        ApiReviewContractBindingState.IdentityNotEstablished => "OpenAPI contracts exist in source, but none declares an operation this API was observed to serve. Runtime API review can still run.",
        _ => "Runtime API review can still run. Contract compatibility will not be assessed.",
    };

    /// <summary>Contract compatibility before the run: assessable only from a configured contract (the one the engine validates against).</summary>
    public string CompatibilityLabel => State == ApiReviewContractBindingState.Configured ? "Available to assess" : "Not assessed";
}

/// <summary>
/// Resolves REST contract evidence before asking anyone to configure it: explicit configuration &gt; one deterministically matching Source
/// Analysis OpenAPI contract &gt; none. Matching is by declared operation paths against the paths the target was observed to serve (never by
/// file name), within one Source Analysis snapshot. Reads the stored contract evidence (ContractEvidenceQueries); never re-parses.
/// </summary>
public static class ApiReviewContractBindings
{
    public static ApiReviewContractBinding Resolve(ApiReviewTarget target, IReadOnlyList<IqrSourceSnapshot> snapshots)
    {
        if (!string.IsNullOrWhiteSpace(target.ContractSource))
            return new(ApiReviewContractBindingState.Configured, ContractName: target.ContractSource);

        var bySnapshot = snapshots
            .Where(s => s.EvidenceDomains?.Contracts is not null)
            .Select(s => (Snapshot: s, Facts: ContractEvidenceQueries.GetContracts(new ContractEvidenceSource(s.Id, s.Archive.FileName, s.AnalyzedAt, s.EvidenceDomains!.Contracts!))
                .Where(f => f.Contract.Type == SourceContractType.OpenApi).ToList()))
            .Where(x => x.Facts.Count > 0)
            .ToList();
        if (bySnapshot.Count == 0) return new(ApiReviewContractBindingState.NotAvailable);

        var matching = bySnapshot
            .Select(x => (x.Snapshot, Matches: x.Facts.Select(f => (Fact: f, Count: MatchedOperations(target, f.Contract))).Where(m => m.Count > 0).ToList()))
            .Where(x => x.Matches.Count > 0)
            .ToList();
        if (matching.Count == 0) return new(ApiReviewContractBindingState.IdentityNotEstablished);
        if (matching.Count > 1) return new(ApiReviewContractBindingState.MultipleSnapshots, CandidateCount: matching.Sum(m => m.Matches.Count));

        var (snapshot, matches) = matching[0];
        var best = matches.Max(m => m.Count);
        var top = matches.Where(m => m.Count == best).ToList();
        if (top.Count > 1) return new(ApiReviewContractBindingState.MultipleCandidates, CandidateCount: top.Count, SnapshotId: snapshot.Id, SnapshotSource: snapshot.Archive.FileName);
        var chosen = top[0];
        return new(ApiReviewContractBindingState.SourceBound, chosen.Fact.Name, chosen.Fact.Contract.File, snapshot.Id, snapshot.Archive.FileName, chosen.Count);
    }

    /// <summary>Observed operations whose path matches a declared path template (with or without the target's base path).</summary>
    public static int MatchedOperations(ApiReviewTarget target, SourceContract contract)
    {
        var declared = contract.Operations.Where(o => !string.IsNullOrWhiteSpace(o.Path)).Select(o => Segments(o.Path!)).ToList();
        if (declared.Count == 0) return 0;
        var basePath = Segments(target.BasePath);
        return target.Operations.Count(op =>
        {
            var observed = Segments(op.Path);
            var relative = basePath.Length > 0 && observed.Take(basePath.Length).SequenceEqual(basePath, StringComparer.OrdinalIgnoreCase) ? observed[basePath.Length..] : observed;
            return declared.Any(d => Matches(d, observed) || Matches(d, relative));
        });
    }

    private static string[] Segments(string path) => (path.Split('?')[0]).Split('/', StringSplitOptions.RemoveEmptyEntries);

    private static bool Matches(string[] template, string[] path) =>
        template.Length == path.Length && path.Length > 0
        && template.Zip(path).All(p => p.First.StartsWith('{') && p.First.EndsWith('}') || string.Equals(p.First, p.Second, StringComparison.OrdinalIgnoreCase));
}
