using System.Data;
using System.Text.Json;
using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.Api.Services.SourceAnalysis;
using BirkNext.Integrations;
using BirkNext.SecurityExpectations;
using BirkNext.SourceArchitecture;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services.SecurityExpectations;

public interface ISecurityExpectationDiscoveryService
{
    Task<IReadOnlyList<IqrSourceSnapshot>> SourcesAsync(string environmentId, CancellationToken ct = default);
    Task<IqrSourceSnapshot?> CurrentSourceAsync(string environmentId, CancellationToken ct = default);
    /// <summary>Source Analysis snapshots for expectation discovery and, for a chosen scope, related-source suggestions and problems. Read-only.</summary>
    Task<ReviewSourceOptions> SourceScopeAsync(string environmentId, ReviewSourceScopeRequest? scope, CancellationToken ct = default);
    Task<SecurityExpectationDiscoveryResult> DiscoverAsync(string environmentId, SecurityDiscoveryRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<SecurityExpectationDiscoveryResult>> ListAsync(string environmentId, CancellationToken ct = default);
    Task<SecurityCandidateReviewResponse> ReviewAsync(string environmentId, SecurityCandidateReviewRequest request, bool accept, CancellationToken ct = default);
}
public sealed class SecurityDiscoveryReviewException(string message) : Exception(message);
/// <summary>
/// Security Expectations' source discovery. Snapshots come from Source Analysis through the shared <see cref="IReviewSourceEvidenceProvider"/>
/// (exact resolution, no substitution); this service owns candidate extraction, de-duplication, the approved-vs-detected comparison and the
/// human review. Approved expectations never depend on a snapshot being available.
/// </summary>
public sealed class SecurityExpectationDiscoveryService(AppDbContext db, IReviewSourceEvidenceProvider? sources = null) : ISecurityExpectationDiscoveryService
{
    private readonly IReviewSourceEvidenceProvider _sources = sources ?? new ReviewSourceEvidenceProvider(new Integrations.SourceEvidence.IqrSourceStore(db));

    /// <summary>Snapshot metadata only (captured evidence stays on the backend).</summary>
    public async Task<IReadOnlyList<IqrSourceSnapshot>> SourcesAsync(string environmentId, CancellationToken ct = default) =>
        !_sources.SourceAnalysisEnabled ? [] : (await _sources.ListAsync(environmentId, ct)).Select(snapshot => new IqrSourceSnapshot
        {
            Id = snapshot.Id, IntegrationId = snapshot.IntegrationId, Archive = snapshot.Archive, AnalyzedAt = snapshot.AnalyzedAt, Status = snapshot.Status,
            AnalyzerVersion = snapshot.AnalyzerVersion, Commit = snapshot.Commit, Branch = snapshot.Branch, Repository = snapshot.Repository,
        }).ToList();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public async Task<IqrSourceSnapshot?> CurrentSourceAsync(string env, CancellationToken ct = default) =>
        !_sources.SourceAnalysisEnabled ? null : (await _sources.ListAsync(env, ct)).FirstOrDefault();

    public const string NoEvidence = "Analyzed before security expectation evidence was captured. Analyze the archive again in Source Analysis to create a new snapshot.";

    /// <summary>Whether a snapshot can yield expectation candidates (captured evidence, or an architecture snapshot of the same source).</summary>
    public static ConsumerSourceEvidence Evidence(IqrSourceSnapshot s) => s.SecurityExpectationsEvidence is { } e
        ? new(true, e.Candidates.Count == 0 ? "No security expectation candidate was found in this snapshot." : null, $"{e.Candidates.Count} candidate(s)")
        : s.Architecture is not null ? new(true, "Candidates are derived from this snapshot's architecture evidence.") : new(false, NoEvidence);

    /// <summary>Related sources for expectations: exact package/project references to sources whose own snapshot yields expectation candidates
    /// (shared identity/auth configuration). Other references are not suggested.</summary>
    public static List<RelatedSourceCandidate> Candidates(IqrSourceSnapshot primary, IReadOnlyList<IqrSourceSnapshot> all) =>
        ReviewSourceEvidenceProvider.ReferenceCandidates(primary, all).Select(c => c with
        {
            MatchingSnapshotIds = c.MatchingSnapshotIds.Where(id => all.FirstOrDefault(s => s.Id == id)?.SecurityExpectationsEvidence?.Candidates.Count > 0).ToList(),
        }).Where(c => c.MatchingSnapshotIds.Count > 0).ToList();

    public async Task<ReviewSourceOptions> SourceScopeAsync(string environmentId, ReviewSourceScopeRequest? scope, CancellationToken ct = default)
    {
        var snapshots = _sources.SourceAnalysisEnabled ? await _sources.ListAsync(environmentId, ct) : [];
        return ReviewSourceEvidenceProvider.Options(_sources.SourceAnalysisEnabled, snapshots, Evidence, scope, p => Candidates(p, snapshots));
    }
    public async Task<SecurityExpectationDiscoveryResult> DiscoverAsync(string environmentId, SecurityDiscoveryRequest request, CancellationToken ct = default)
    {
        if (!_sources.SourceAnalysisEnabled) throw new SecurityDiscoveryReviewException(ReviewSourceEvidenceProvider.Disabled);
        var snapshot = await _sources.ResolveAsync(environmentId, request.SourceSnapshotId, ct)
            ?? throw new SecurityDiscoveryReviewException("Select an existing Source Analysis snapshot for this Target Environment.");
        var result = Project(snapshot, environmentId, request.Approved) with { IsCurrent = true };
        var related = new List<SecurityExpectationDiscoveryResult>();
        foreach (var id in (request.RelatedSourceSnapshotIds ?? []).Where(id => id != snapshot.Id).Distinct())
        {
            var relatedSnapshot = await _sources.ResolveAsync(environmentId, id, ct)
                ?? throw new SecurityDiscoveryReviewException("Related source must be an existing Source Analysis snapshot.");
            if (ReviewSourceEvidenceProvider.Identity(relatedSnapshot).Key == ReviewSourceEvidenceProvider.Identity(snapshot).Key)
                throw new SecurityDiscoveryReviewException("A related source must be another repository; a scope holds one snapshot per repository.");
            related.Add(Project(relatedSnapshot, environmentId, request.Approved));
        }
        if (related.Count > 0)
        {
            var grouped = SecurityExpectationValues.Group(result.Candidates.Concat(related.SelectMany(r => r.Candidates)));
            var combined = grouped.Select(c => c with { CandidateState = SecurityExpectationValues.DeriveState(c, request.Approved, grouped.Count(x => x.FieldType == c.FieldType)) }).ToList();
            result = result with { SourceScope = new(snapshot.Id, related.Select(r => r.SourceSnapshotId).ToList()),
                SourceFingerprints = result.SourceFingerprints.Concat(related.SelectMany(r => r.SourceFingerprints)).ToDictionary(p => p.Key, p => p.Value),
                Candidates = combined,
                Conflicts = combined.Where(c => c.CandidateState == SecurityCandidateState.Conflict).Select(c => c.FieldType.ToString()).Distinct().ToList(),
                Status = combined.Any(c => c.CandidateState is SecurityCandidateState.NeedsReview or SecurityCandidateState.Conflict) ? ArchitectureStatus.NeedsReview : result.Status,
                Diagnostics = result.Diagnostics.Concat(related.SelectMany(r => r.Diagnostics)).Distinct().ToList(),
                UnsupportedEvidence = result.UnsupportedEvidence.Concat(related.SelectMany(r => r.UnsupportedEvidence)).Distinct().ToList() };
        }
        var requestedRelated = result.SourceScope?.RelatedSourceSnapshotIds.Order().ToList() ?? [];
        var previousRows = await db.SecurityExpectationDiscoveries.AsNoTracking().Where(r => r.EnvironmentId == environmentId && r.SourceSnapshotId == snapshot.Id)
            .OrderByDescending(r => r.CreatedAt).Take(30).Select(r => new { r.EvidenceJson, r.DecisionsJson }).ToListAsync(ct);
        var decisions = new List<SecurityCandidateDecision>();
        foreach (var previous in previousRows)
        {
            var prior = JsonSerializer.Deserialize<SecurityExpectationDiscoveryResult>(previous.EvidenceJson, Json);
            if (prior?.SourceScope?.RelatedSourceSnapshotIds.Order().SequenceEqual(requestedRelated) == true)
                decisions = RemapDecisions(prior, result.Candidates, JsonSerializer.Deserialize<List<SecurityCandidateDecision>>(previous.DecisionsJson, Json) ?? []);
            if (prior?.SourceScope?.RelatedSourceSnapshotIds.Order().SequenceEqual(requestedRelated) == true) break;
        }
        result = CurrentView(result, result.IsCurrent, decisions);
        db.SecurityExpectationDiscoveries.Add(new() { Id = result.Id, EnvironmentId = environmentId, SourceSnapshotId = snapshot.Id,
            CreatedAt = result.ExtractedAt, EvidenceJson = JsonSerializer.Serialize(result, Json), DecisionsJson = JsonSerializer.Serialize(decisions, Json) });
        await db.SaveChangesAsync(ct);
        return result;
    }
    public static SecurityExpectationDiscoveryResult Project(IqrSourceSnapshot snapshot, string environmentId, ApprovedSecurityExpectations approved)
    {
        var source = snapshot.SecurityExpectationsEvidence;
        if (source is not null && (source.SourceSnapshotId != snapshot.Id || source.SourceFingerprint != snapshot.Archive.Sha256 ||
            source.Candidates.Any(c => c.SourceSnapshotId != snapshot.Id || c.SupportingEvidence.Any(e => e.SourceSnapshotId != snapshot.Id ||
                e.SourceFingerprint.Length > 0 && e.SourceFingerprint != snapshot.Archive.Sha256))))
            return new() { TargetEnvironmentId = environmentId, SourceSnapshotId = snapshot.Id, SourceFingerprint = snapshot.Archive.Sha256,
                AnalyzerVersion = source.AnalyzerVersion, ExtractedAt = DateTimeOffset.UtcNow, Status = ArchitectureStatus.Unsupported,
                Diagnostics = ["Source evidence binding is inconsistent; refresh source analysis. No expectation candidates were promoted."] };
        var legacy = source is null;
        if (source is null && snapshot.Architecture is { } architecture && architecture.SourceSnapshotId == snapshot.Id && architecture.SourceFingerprint == snapshot.Archive.Sha256)
        {
            var fallback = new List<SecurityExpectationCandidate>();
            foreach (var reference in architecture.ConfigurationReferences)
            foreach (var value in reference.SafeValues)
            {
                var dependencies = architecture.Dependencies.Where(d => d.FromComponentId == reference.ComponentId &&
                    d.ConfigurationReference?.Equals(reference.Key, StringComparison.OrdinalIgnoreCase) == true).ToList();
                SecurityExpectationField? field = reference.Key.EndsWith(":Authority",StringComparison.OrdinalIgnoreCase) && reference.Purpose == "Authentication"
                    ? SecurityExpectationField.Authority : dependencies.Any(d => d.DependencyType == ArchitectureDependencyType.GraphQl) ? SecurityExpectationField.GraphQlHost
                    : dependencies.Any(d => d.DependencyType == ArchitectureDependencyType.Http) ? SecurityExpectationField.RestHost : null;
                if (field is null || SecurityExpectationValues.Normalize(field.Value,value) is not { } normalized) continue;
                var component = architecture.Components.FirstOrDefault(c => c.Id == reference.ComponentId)?.Name ?? reference.ComponentId;
                var file = reference.Files.FirstOrDefault() ?? "";
                fallback.Add(new() { Id = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{field}|{component}|{file}|{normalized}"))),
                    FieldType = field.Value, Value = SecurityExpectationValues.Singleton(field.Value) ? value : normalized, NormalizedValue = normalized,
                    CandidateState = SecurityCandidateState.Detected, EvidenceState = ArchitectureEvidenceState.StronglySupported, Confidence = "Existing source architecture evidence",
                    SourceSnapshotId = snapshot.Id, SourceComponent = component, SourceFile = file, SourceSymbol = reference.Key, EvidenceType = "Configuration",
                    Explanation = "Allow-listed configuration value from the exact historical architecture snapshot; no runtime observation." });
            }
            source = new() { SourceSnapshotId=snapshot.Id, SourceFingerprint=snapshot.Archive.Sha256, Candidates=fallback,
                Diagnostics=["Historical source retained endpoint evidence only. Re-analyze source for public identity identifiers, redirects, CDN roles and source-declared headers."] };
        }
        var grouped = SecurityExpectationValues.Group(source?.Candidates ?? []).Select(c => c with { SupportingEvidence = c.SupportingEvidence.Select(e => e with {
            SourceFingerprint = snapshot.Archive.Sha256,
            Repository = e.Repository.StartsWith(snapshot.Archive.FileName + " / ", StringComparison.Ordinal) ? e.Repository : snapshot.Archive.FileName + " / " + e.Repository
        }).ToList() }).ToList();
        var candidates = grouped.Select(c => c with { CandidateState = SecurityExpectationValues.DeriveState(c, approved,
            grouped.Count(x => x.FieldType == c.FieldType)) }).ToList();
        return new() {
            TargetEnvironmentId = environmentId, SourceSnapshotId = snapshot.Id, SourceFingerprint = snapshot.Archive.Sha256,
            SourceScope = new(snapshot.Id, []), SourceDisplayName = snapshot.Archive.FileName, SourceAnalyzedAt = snapshot.AnalyzedAt,
            SourceFingerprints = new() { [snapshot.Id] = snapshot.Archive.Sha256 },
            AnalyzerVersion = source?.AnalyzerVersion ?? SecurityExpectationSourceAnalyzer.Version, ExtractedAt = DateTimeOffset.UtcNow, Candidates = candidates,
            Conflicts = candidates.Where(c => c.CandidateState == SecurityCandidateState.Conflict).Select(c => c.FieldType.ToString()).Distinct().ToList(),
            Diagnostics = source?.Diagnostics ?? ["This historical snapshot has no security value projection. Re-analyze source to discover identifiers and explicit endpoints; historical evidence remains unchanged."],
            UnsupportedEvidence = source?.UnsupportedEvidence ?? [],
            Status = source is null || legacy && source.Candidates.Count == 0 ? ArchitectureStatus.Unsupported : candidates.Any(c => c.CandidateState is SecurityCandidateState.Conflict or SecurityCandidateState.NeedsReview)
                ? ArchitectureStatus.NeedsReview : legacy || source.UnsupportedEvidence.Count > 0 ? ArchitectureStatus.Partial : ArchitectureStatus.Complete
        };
    }
    private static SecurityExpectationDiscoveryResult CurrentView(SecurityExpectationDiscoveryResult result, bool current, List<SecurityCandidateDecision> decisions) =>
        result with { IsCurrent = current, ReviewDecisions = decisions, Candidates = result.Candidates.Select(c => c with { IsCurrent = current && c.IsCurrent,
            CandidateState = !current || !c.IsCurrent ? SecurityCandidateState.Stale : DecisionState(c, decisions) ?? c.CandidateState }).ToList() };
    private static SecurityCandidateState? DecisionState(SecurityExpectationCandidate candidate, List<SecurityCandidateDecision> decisions)
    {
        var states = decisions.Where(d => d.CandidateId == candidate.Id).Select(d => d.State).Distinct().ToList();
        if (states.Count > 1) return SecurityCandidateState.Conflict;
        return decisions.LastOrDefault(d => d.CandidateId == candidate.Id)?.State;
    }

    public async Task<IReadOnlyList<SecurityExpectationDiscoveryResult>> ListAsync(string environmentId, CancellationToken ct = default)
    {
        var rows = await db.SecurityExpectationDiscoveries.AsNoTracking().Where(r=>r.EnvironmentId==environmentId)
            .OrderByDescending(r=>r.CreatedAt).Take(30).ToListAsync(ct);
        return rows.Select(row => {
            var result = JsonSerializer.Deserialize<SecurityExpectationDiscoveryResult>(row.EvidenceJson,Json)! with { Revision = row.Revision };
            var candidates = SecurityExpectationValues.Group(result.Candidates.Select(c =>
            {
                var normalized = c.FormatState == "Valid" ? SecurityExpectationValues.Normalize(c.FieldType, c.Value) : null;
                return normalized is null ? c : c with { NormalizedValue = normalized, Value = normalized };
            }));
            var decisions = RemapDecisions(result, candidates, JsonSerializer.Deserialize<List<SecurityCandidateDecision>>(row.DecisionsJson,Json) ?? []);
            result = result with { Candidates = candidates };
            if (result.SourceScope is null) result = result with { Candidates = result.Candidates.Select(c => c with { IsCurrent = false, CandidateState = SecurityCandidateState.Stale }).ToList() };
            return CurrentView(result, true, decisions);
        }).ToList();
    }
    private static List<SecurityCandidateDecision> RemapDecisions(SecurityExpectationDiscoveryResult prior,
        List<SecurityExpectationCandidate> currentCandidates, List<SecurityCandidateDecision> priorDecisions)
    {
        var priorCandidates = prior.Candidates.ToDictionary(c => c.Id, StringComparer.Ordinal);
        return priorDecisions.Where(d => priorCandidates.ContainsKey(d.CandidateId))
            .Select(d => (Decision: d, Candidate: priorCandidates[d.CandidateId]))
            .Select(x => (x.Decision, Candidate: currentCandidates.FirstOrDefault(c => c.FieldType == x.Candidate.FieldType &&
                SemanticValue(c) == SemanticValue(x.Candidate) && c.EnvironmentScope == x.Candidate.EnvironmentScope)))
            .Where(x => x.Candidate is not null)
            .Select(x => x.Decision with { CandidateId = x.Candidate!.Id })
            .GroupBy(d => d.CandidateId, StringComparer.Ordinal)
            .Select(g => g.Select(d => d.State).Distinct().Count() > 1
                ? new SecurityCandidateDecision(g.Key, SecurityCandidateState.Conflict, g.Max(d => d.At))
                : g.OrderBy(d => d.At).Last())
            .ToList();
    }
    private static string SemanticValue(SecurityExpectationCandidate candidate) =>
        candidate.FormatState == "Valid" ? SecurityExpectationValues.Normalize(candidate.FieldType, candidate.Value) ?? candidate.NormalizedValue : candidate.NormalizedValue;
    public async Task<SecurityCandidateReviewResponse> ReviewAsync(string environmentId, SecurityCandidateReviewRequest request, bool accept, CancellationToken ct = default)
    {
        // Serializable binding check + review update preserves the explicitly selected immutable scope.
        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable,ct) : null;
        var row = await db.SecurityExpectationDiscoveries.FirstOrDefaultAsync(r=>r.EnvironmentId==environmentId && r.Id==request.DiscoveryId,ct)
            ?? throw new SecurityDiscoveryReviewException("Discovery not found for this Target Environment.");
        if(row.Revision != request.Revision) throw new SecurityDiscoveryReviewException("Review changed; refresh discovery.");
        var result = JsonSerializer.Deserialize<SecurityExpectationDiscoveryResult>(row.EvidenceJson,Json)!;
        if (accept && result.SourceScope is null) throw new SecurityDiscoveryReviewException("Legacy source discovery is read-only. Refresh candidates from its exact Source Analysis snapshot.");
        if(accept && !await db.IqrSourceSnapshots.AnyAsync(s => s.EnvironmentId == environmentId && s.Id == result.SourceSnapshotId, ct))
            throw new SecurityDiscoveryReviewException("Source evidence changed; refresh discovery.");
        if (accept)
        foreach (var binding in result.SourceFingerprints)
        {
            var sourceRow = await db.IqrSourceSnapshots.AsNoTracking().FirstOrDefaultAsync(s => s.EnvironmentId == environmentId && s.Id == binding.Key && s.IntegrationId == "source-analysis", ct);
            if (sourceRow is null || JsonSerializer.Deserialize<IqrSourceSnapshot>(sourceRow.EvidenceJson, Json)?.Archive.Sha256 != binding.Value)
                throw new SecurityDiscoveryReviewException("Selected source scope binding changed; refresh candidates.");
        }
        var candidate = result.Candidates.FirstOrDefault(c=>c.Id==request.CandidateId)
            ?? throw new SecurityDiscoveryReviewException("Candidate not found.");
        if (!new[] { result.SourceSnapshotId }.Concat(result.SourceScope?.RelatedSourceSnapshotIds ?? []).Contains(candidate.SourceSnapshotId))
            throw new SecurityDiscoveryReviewException("Candidate source binding is inconsistent; refresh discovery.");
        var approved = SecurityExpectationValues.Copy(request.Approved);
        if(accept && request.Scope is { Length: > 0 } scope) {
            // Component-scoped approval (Client/Application IDs): other components' values are not a conflict.
            try { approved = SecurityExpectationValues.AcceptScoped(request.Approved,candidate with { IsCurrent=true },scope,result.SourceFingerprints.GetValueOrDefault(candidate.SourceSnapshotId,result.SourceFingerprint),DateTimeOffset.UtcNow); }
            catch(InvalidOperationException e) { throw new SecurityDiscoveryReviewException(e.Message); }
        }
        else if(accept) {
            if(SecurityExpectationValues.DeriveState(candidate,request.Approved,result.Candidates.Count(c => c.FieldType == candidate.FieldType))==SecurityCandidateState.Conflict && !request.Replace)
                throw new SecurityDiscoveryReviewException("Conflicting candidates require an explicit selection or replacement.");
            try { approved = SecurityExpectationValues.Accept(request.Approved,candidate with { IsCurrent=true },result.SourceFingerprints.GetValueOrDefault(candidate.SourceSnapshotId,result.SourceFingerprint),request.Replace,DateTimeOffset.UtcNow); }
            catch(InvalidOperationException e) { throw new SecurityDiscoveryReviewException(e.Message); }
        }
        var decisions = JsonSerializer.Deserialize<List<SecurityCandidateDecision>>(row.DecisionsJson,Json) ?? [];
        decisions.RemoveAll(d => d.CandidateId == candidate.Id);
        decisions.Add(new(candidate.Id,accept ? SecurityCandidateState.Accepted : SecurityCandidateState.Rejected,DateTimeOffset.UtcNow));
        row.DecisionsJson = JsonSerializer.Serialize(decisions,Json); row.Revision++;
        try {
            await db.SaveChangesAsync(ct);
            if(transaction is not null) await transaction.CommitAsync(ct);
        } catch(DbUpdateConcurrencyException) { throw new SecurityDiscoveryReviewException("Review changed; refresh discovery."); }
        return new(CurrentView(result with { Revision=row.Revision },true,decisions),approved);
    }
}
