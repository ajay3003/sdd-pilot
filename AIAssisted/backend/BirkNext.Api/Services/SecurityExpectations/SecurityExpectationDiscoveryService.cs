using System.Data;
using System.Text.Json;
using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.Integrations;
using BirkNext.SecurityExpectations;
using BirkNext.SourceArchitecture;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services.SecurityExpectations;

public interface ISecurityExpectationDiscoveryService
{
    Task<IqrSourceSnapshot?> CurrentSourceAsync(string environmentId, CancellationToken ct = default);
    Task<SecurityExpectationDiscoveryResult> DiscoverAsync(string environmentId, SecurityDiscoveryRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<SecurityExpectationDiscoveryResult>> ListAsync(string environmentId, CancellationToken ct = default);
    Task<SecurityCandidateReviewResponse> ReviewAsync(string environmentId, SecurityCandidateReviewRequest request, bool accept, CancellationToken ct = default);
}
public sealed class SecurityDiscoveryReviewException(string message) : Exception(message);
public sealed class SecurityExpectationDiscoveryService(AppDbContext db) : ISecurityExpectationDiscoveryService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public async Task<IqrSourceSnapshot?> CurrentSourceAsync(string env, CancellationToken ct = default)
    {
        var row = await db.IqrSourceSnapshots.AsNoTracking().Where(s => s.EnvironmentId == env && s.IntegrationId == "source-analysis")
            .OrderByDescending(s => s.AnalyzedAt).ThenByDescending(s => s.Id).FirstOrDefaultAsync(ct);
        return row is null ? null : JsonSerializer.Deserialize<IqrSourceSnapshot>(row.EvidenceJson, Json);
    }
    public async Task<SecurityExpectationDiscoveryResult> DiscoverAsync(string environmentId, SecurityDiscoveryRequest request, CancellationToken ct = default)
    {
        var row = await db.IqrSourceSnapshots.AsNoTracking().FirstOrDefaultAsync(s => s.EnvironmentId == environmentId && s.Id == request.SourceSnapshotId && s.IntegrationId == "source-analysis", ct)
            ?? throw new SecurityDiscoveryReviewException("Select an existing Source Analysis snapshot for this Target Environment.");
        var snapshot = JsonSerializer.Deserialize<IqrSourceSnapshot>(row.EvidenceJson, Json)!;
        var current = await CurrentSourceAsync(environmentId, ct);
        var result = Project(snapshot, environmentId, request.Approved) with { IsCurrent = current?.Id == snapshot.Id && current.Archive.Sha256 == snapshot.Archive.Sha256 };
        result = CurrentView(result, result.IsCurrent, []);
        db.SecurityExpectationDiscoveries.Add(new() { Id = result.Id, EnvironmentId = environmentId, SourceSnapshotId = snapshot.Id,
            CreatedAt = result.ExtractedAt, EvidenceJson = JsonSerializer.Serialize(result, Json) });
        await db.SaveChangesAsync(ct);
        return result;
    }
    public static SecurityExpectationDiscoveryResult Project(IqrSourceSnapshot snapshot, string environmentId, ApprovedSecurityExpectations approved)
    {
        var source = snapshot.SecurityExpectationsEvidence;
        if (source is not null && (source.SourceSnapshotId != snapshot.Id || source.SourceFingerprint != snapshot.Archive.Sha256 ||
            source.Candidates.Any(c => c.SourceSnapshotId != snapshot.Id)))
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
        var candidates = (source?.Candidates ?? []).Select(c => {
            var different = SecurityExpectationValues.Singleton(c.FieldType) && SecurityExpectationValues.Values(approved, c.FieldType).Any() && !SecurityExpectationValues.Matches(approved,c);
            var ambiguous = SecurityExpectationValues.Singleton(c.FieldType) && source!.Candidates.Where(x=>x.FieldType==c.FieldType).Select(x=>x.NormalizedValue).Distinct().Count()>1;
            return different || ambiguous ? c with { CandidateState = SecurityCandidateState.Conflict, ConflictGroupId = c.FieldType.ToString(), SuggestedAction = "Keep current or explicitly choose replacement" } : c;
        }).ToList();
        return new() {
            TargetEnvironmentId = environmentId, SourceSnapshotId = snapshot.Id, SourceFingerprint = snapshot.Archive.Sha256,
            AnalyzerVersion = source?.AnalyzerVersion ?? SecurityExpectationSourceAnalyzer.Version, ExtractedAt = DateTimeOffset.UtcNow, Candidates = candidates,
            Conflicts = candidates.Where(c => c.CandidateState == SecurityCandidateState.Conflict).Select(c => c.FieldType.ToString()).Distinct().ToList(),
            Diagnostics = source?.Diagnostics ?? ["This historical snapshot has no security value projection. Re-analyze source to discover identifiers and explicit endpoints; historical evidence remains unchanged."],
            UnsupportedEvidence = source?.UnsupportedEvidence ?? [],
            Status = source is null || legacy && source.Candidates.Count == 0 ? ArchitectureStatus.Unsupported : legacy || snapshot.Status != SourceAnalysisStatus.Ready || source.UnsupportedEvidence.Count > 0
                ? ArchitectureStatus.Partial : candidates.Any(c => c.CandidateState == SecurityCandidateState.Conflict) ? ArchitectureStatus.NeedsReview : ArchitectureStatus.Complete
        };
    }
    private static SecurityExpectationDiscoveryResult CurrentView(SecurityExpectationDiscoveryResult result, bool current, List<SecurityCandidateDecision> decisions) =>
        result with { IsCurrent = current, Candidates = result.Candidates.Select(c => c with { IsCurrent = current,
            CandidateState = !current ? SecurityCandidateState.Stale : decisions.LastOrDefault(d=>d.CandidateId==c.Id)?.State ?? c.CandidateState }).ToList() };

    public async Task<IReadOnlyList<SecurityExpectationDiscoveryResult>> ListAsync(string environmentId, CancellationToken ct = default)
    {
        var current = await CurrentSourceAsync(environmentId, ct);
        var rows = await db.SecurityExpectationDiscoveries.AsNoTracking().Where(r=>r.EnvironmentId==environmentId)
            .OrderByDescending(r=>r.CreatedAt).Take(30).ToListAsync(ct);
        return rows.Select(row => {
            var result = JsonSerializer.Deserialize<SecurityExpectationDiscoveryResult>(row.EvidenceJson,Json)! with { Revision = row.Revision };
            return CurrentView(result, current?.Id==result.SourceSnapshotId && current.Archive.Sha256==result.SourceFingerprint,
                JsonSerializer.Deserialize<List<SecurityCandidateDecision>>(row.DecisionsJson,Json) ?? []);
        }).ToList();
    }
    public async Task<SecurityCandidateReviewResponse> ReviewAsync(string environmentId, SecurityCandidateReviewRequest request, bool accept, CancellationToken ct = default)
    {
        // Serializable snapshot check + review update prevents accepting an obsolete source silently.
        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable,ct) : null;
        var row = await db.SecurityExpectationDiscoveries.FirstOrDefaultAsync(r=>r.EnvironmentId==environmentId && r.Id==request.DiscoveryId,ct)
            ?? throw new SecurityDiscoveryReviewException("Discovery not found for this Target Environment.");
        if(row.Revision != request.Revision) throw new SecurityDiscoveryReviewException("Review changed; refresh discovery.");
        var result = JsonSerializer.Deserialize<SecurityExpectationDiscoveryResult>(row.EvidenceJson,Json)!;
        var current = await CurrentSourceAsync(environmentId,ct);
        if(accept && (current?.Id!=result.SourceSnapshotId || current.Archive.Sha256!=result.SourceFingerprint))
            throw new SecurityDiscoveryReviewException("Source evidence changed; refresh discovery.");
        var candidate = result.Candidates.FirstOrDefault(c=>c.Id==request.CandidateId)
            ?? throw new SecurityDiscoveryReviewException("Candidate not found.");
        if (candidate.SourceSnapshotId != result.SourceSnapshotId)
            throw new SecurityDiscoveryReviewException("Candidate source binding is inconsistent; refresh discovery.");
        var approved = SecurityExpectationValues.Copy(request.Approved);
        if(accept) {
            if(candidate.CandidateState==SecurityCandidateState.Conflict && !request.Replace)
                throw new SecurityDiscoveryReviewException("Conflicting candidates require an explicit selection or replacement.");
            try { approved = SecurityExpectationValues.Accept(request.Approved,candidate with { IsCurrent=true },result.SourceFingerprint,request.Replace,DateTimeOffset.UtcNow); }
            catch(InvalidOperationException e) { throw new SecurityDiscoveryReviewException(e.Message); }
        }
        var decisions = JsonSerializer.Deserialize<List<SecurityCandidateDecision>>(row.DecisionsJson,Json) ?? [];
        decisions.Add(new(candidate.Id,accept ? SecurityCandidateState.Accepted : SecurityCandidateState.Rejected,DateTimeOffset.UtcNow));
        row.DecisionsJson = JsonSerializer.Serialize(decisions,Json); row.Revision++;
        try {
            await db.SaveChangesAsync(ct);
            if(transaction is not null) await transaction.CommitAsync(ct);
        } catch(DbUpdateConcurrencyException) { throw new SecurityDiscoveryReviewException("Review changed; refresh discovery."); }
        return new(CurrentView(result with { Revision=row.Revision },current?.Id==result.SourceSnapshotId && current.Archive.Sha256==result.SourceFingerprint,decisions),approved);
    }
}
