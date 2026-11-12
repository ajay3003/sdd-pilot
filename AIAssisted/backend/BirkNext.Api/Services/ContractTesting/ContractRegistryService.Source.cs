using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BirkNext.Api.Models;
using BirkNext.Api.Services.ExecutionEvidence;
using BirkNext.Api.Services.SourceAnalysis;
using BirkNext.ContractTesting;
using BirkNext.Integrations;
using BirkNext.SourceDomains;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services.ContractTesting;

/// <summary>
/// Contracts from Source Analysis with project-scoped identity. A project is a Source Analysis repository identity
/// (<see cref="ReviewSourceEvidenceProvider.Identity"/>: the root solution, else the archive name), joined with the repository identities a person
/// explicitly declared to be the same repository (<c>source_repository_associations</c>) — never inferred from names. A source contract's path is
/// provenance and a matching signal inside a project, never an identity across projects: the same path in two projects is two contracts.
/// </summary>
public sealed partial class ContractRegistryService
{
    public const string IdentityUnscoped = "Unscoped", IdentityNeedsReview = "NeedsReview";
    public const string DecisionConfirm = "identity-confirm", DecisionReject = "identity-reject", DecisionLocation = "location", DecisionProject = "project-assignment";
    private const double StrongOverlap = 0.6, PossibleOverlap = 0.3;

    /// <summary>The project key of a snapshot (its repository identity).</summary>
    public static string ProjectKeyOf(IqrSourceSnapshot snapshot) => ReviewSourceEvidenceProvider.Identity(snapshot).Key;

    /// <summary>A project key plus every key linked to it by active "same repository" declarations (transitively).</summary>
    public async Task<HashSet<string>> ProjectGroupAsync(string projectKey, CancellationToken ct)
    {
        var group = new HashSet<string>(StringComparer.Ordinal) { projectKey };
        var links = await db.SourceRepositoryAssociations.AsNoTracking().Where(r => r.RevokedAt == null).Select(r => new { r.RepositoryKeyA, r.RepositoryKeyB }).ToListAsync(ct);
        for (var grew = true; grew;)
        {
            grew = false;
            foreach (var l in links)
            {
                if (group.Contains(l.RepositoryKeyA) && group.Add(l.RepositoryKeyB)) grew = true;
                if (group.Contains(l.RepositoryKeyB) && group.Add(l.RepositoryKeyA)) grew = true;
            }
        }
        return group;
    }

    /// <summary>The projects Contract Testing knows: from the newest snapshots and from the contracts already in the registry.</summary>
    public async Task<IReadOnlyList<ContractProject>> ProjectsAsync(CancellationToken ct)
    {
        var snapshots = sources is { SourceAnalysisEnabled: true } ? (await sources.ListAsync(string.Empty, ct)).OrderByDescending(s => s.AnalyzedAt).Take(50).ToList() : [];
        var contracts = await db.ApiContracts.AsNoTracking().Where(c => c.ProjectKey != null).Select(c => c.ProjectKey!).ToListAsync(ct);
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var s in snapshots) names.TryAdd(ProjectKeyOf(s), ReviewSourceEvidenceProvider.Identity(s).DisplayName);
        foreach (var k in contracts) names.TryAdd(k, k);
        var result = new List<ContractProject>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in names.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            if (seen.Contains(key)) continue;
            var group = await ProjectGroupAsync(key, ct);
            var keys = group.Where(names.ContainsKey).OrderBy(k => k, StringComparer.Ordinal).ToList();
            seen.UnionWith(group);
            var groupSnapshots = snapshots.Where(s => group.Contains(ProjectKeyOf(s))).ToList();
            result.Add(new(keys[0], string.Join(" + ", keys.Select(k => names[k])), keys, groupSnapshots.Count, contracts.Count(group.Contains),
                groupSnapshots.FirstOrDefault()?.Id, groupSnapshots.FirstOrDefault()?.AnalyzedAt));
        }
        return result;
    }

    /// <summary>
    /// REST (OpenAPI) and GraphQL schema contracts that Source Analysis snapshots found — one row per source contract per project (from the newest
    /// snapshot of that project that holds it) — with its identity classification against the project's existing contracts. Reading only.
    /// </summary>
    public async Task<IReadOnlyList<SourceContractCandidate>> SourceCandidatesAsync(CancellationToken ct) => await SourceCandidatesAsync(null, ct);

    public async Task<IReadOnlyList<SourceContractCandidate>> SourceCandidatesAsync(string? projectKey, CancellationToken ct)
    {
        if (sources is null || !sources.SourceAnalysisEnabled) return [];
        var filter = projectKey is null ? null : await ProjectGroupAsync(projectKey, ct);
        var imported = await db.ApiContractVersions.AsNoTracking().Where(v => v.SourceSnapshotId != null)
            .Join(db.ApiContracts.AsNoTracking(), v => v.ContractId, c => c.Id, (v, c) => new { v.SourceSnapshotId, v.SourcePath, c.Key, v.Version }).ToListAsync(ct);
        // At most one snapshot per logical source (the analysis baseline, else the only content; none while BASELINE_NOT_SELECTED): contracts of alternative branch snapshots are never
        // combined, and a contract that only an unselected alternative holds is not offered.
        var snapshots = BirkNext.Api.Services.SourceAnalysis.SourceBaselineService.Resolve(
            (await sources.ListAsync(string.Empty, ct)).OrderByDescending(s => s.AnalyzedAt).Take(100),
            await BirkNext.Api.Services.SourceAnalysis.SourceBaselineService.CurrentBaselinesAsync(db, ct)).OrderByDescending(s => s.AnalyzedAt).Take(20).ToList();
        var groups = new Dictionary<string, (string Id, HashSet<string> Keys)>(StringComparer.Ordinal);
        var newest = new Dictionary<string, (IqrSourceSnapshot Snapshot, SourceContract Contract, int Count, HashSet<string> Group)>(StringComparer.Ordinal);
        foreach (var snapshot in snapshots)
        {
            var key = ProjectKeyOf(snapshot);
            if (filter is not null && !filter.Contains(key)) continue;
            if (!groups.TryGetValue(key, out var g)) { var keys = await ProjectGroupAsync(key, ct); g = (keys.Min(StringComparer.Ordinal)!, keys); groups[key] = g; }
            foreach (var c in snapshot.EvidenceDomains?.Contracts.Contracts.Where(c => c.Type is SourceContractType.OpenApi or SourceContractType.GraphQlSchema) ?? [])
            {
                var id = g.Id + "|" + c.Id;
                newest[id] = newest.TryGetValue(id, out var seen) ? (seen.Snapshot, seen.Contract, seen.Count + 1, seen.Group) : (snapshot, c, 1, g.Keys);
            }
        }
        var snapshotIds = newest.Values.Select(n => n.Snapshot.Id).Distinct().ToList();
        var documents = (await db.SourceContractDocuments.AsNoTracking().Where(d => snapshotIds.Contains(d.SnapshotId)).Select(d => new { d.SnapshotId, d.Path, d.Sha256 }).ToListAsync(ct))
            .GroupBy(d => (d.SnapshotId, d.Path)).ToDictionary(g => g.Key, g => g.First().Sha256);
        var result = new List<SourceContractCandidate>();
        foreach (var (snapshot, c, count, group) in newest.Values)
        {
            var hit = imported.FirstOrDefault(i => i.SourceSnapshotId == snapshot.Id && i.SourcePath == c.Id);
            var docSha = documents.GetValueOrDefault((snapshot.Id, c.File.Replace('\\', '/')));
            var protocol = c.Type == SourceContractType.OpenApi ? ApiContractProtocol.Rest : ApiContractProtocol.GraphQl;
            var match = await ClassifyAsync(snapshot, c, docSha, group, ct);
            var state = match.Classification switch
            {
                _ when match.Archived => SourceContractCandidateState.Archived,
                ContractIdentityClassification.SameContract => SourceContractCandidateState.UpToDate,
                ContractIdentityClassification.NewVersion => SourceContractCandidateState.Changed,
                ContractIdentityClassification.MovedUnchanged => SourceContractCandidateState.Moved,
                ContractIdentityClassification.NewContract => SourceContractCandidateState.New,
                _ => SourceContractCandidateState.NeedsReview,
            };
            var identity = ReviewSourceEvidenceProvider.Identity(snapshot);
            result.Add(new(snapshot.Id, snapshot.AnalyzedAt, snapshot.Archive.FileName, snapshot.Commit, c.Id, c.Name, c.Type.ToString(), c.File, c.Operations.Count,
                hit is not null ? ContractIdentity.ContractRef(hit.Key, hit.Version)
                    : state == SourceContractCandidateState.UpToDate && match.Contract is { } same ? ContractIdentity.ContractRef(same.Key, match.LatestVersion ?? 0) : null)
            {
                Module = SourceModule(c.File), Protocol = protocol, State = state, ContractKey = match.Contract?.Key, ContractVersion = match.LatestVersion, Snapshots = count,
                HasDocument = docSha is not null, ProjectKey = identity.Key, ProjectName = identity.DisplayName, Identity = match.Classification, Candidates = match.Candidates,
            });
        }
        return [.. result.OrderBy(r => r.ProjectName, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Module, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.File, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The top-level folder of a source contract's file ("Autorisasjon/autodoc/openapi.yaml" → "Autorisasjon"); "(root)" for a top-level file.</summary>
    public static string SourceModule(string file)
    {
        var parts = file.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 1 ? parts[0] : "(root)";
    }

    /// <summary>The module of a stored source path ("openapi:Autorisasjon/autodoc/openapi.yaml" → "Autorisasjon").</summary>
    public static string? ModuleOfSourcePath(string? sourcePath) =>
        sourcePath is null ? null : SourceModule(sourcePath[(sourcePath.IndexOf(':') + 1)..]) is var m && m != "(root)" ? m : null;

    /// <summary>
    /// The fingerprint a source-backed version is stored with. With the retained document text it is the document itself, so the same OpenAPI/GraphQL
    /// document in another snapshot or archive is the same version even when the analyser's surroundings (code references, line numbers) differ.
    /// Without text it is the parsed evidence (the only thing known).
    /// </summary>
    private static string SourceFingerprint(SourceContract source, string? documentSha) =>
        documentSha is null ? LegacySourceFingerprint(source, null) : Sha("source-document\n" + documentSha);

    /// <summary>The earlier fingerprint (parsed evidence plus the document hash): versions stored with it are still recognised as identical.</summary>
    private static string LegacySourceFingerprint(SourceContract source, string? documentSha)
    {
        var evidenceJson = JsonSerializer.Serialize(source, ExecutionEvidenceStore.Json);
        return Sha(documentSha is null ? evidenceJson : evidenceJson + "\n" + documentSha);
    }

    private static string Sha(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static bool SameSource(string? stored, SourceContract source, string? documentSha) =>
        stored is not null && (stored == SourceFingerprint(source, documentSha) || stored == LegacySourceFingerprint(source, documentSha));

    private sealed record IdentityMatch(ContractIdentityClassification Classification, ApiContractRow? Contract, int? LatestVersion, bool Archived, List<ContractIdentityCandidate> Candidates);

    /// <summary>Contracts of the project group that may be continued automatically (resolved identity, same protocol).</summary>
    private IQueryable<ApiContractRow> ProjectContracts(HashSet<string> group, ApiContractProtocol protocol)
    {
        var p = protocol.ToString();
        var keys = group.ToList();
        return db.ApiContracts.AsNoTracking().Where(c => c.ProjectKey != null && keys.Contains(c.ProjectKey) && c.Protocol == p && c.IdentityState != IdentityNeedsReview);
    }

    /// <summary>
    /// The contract of the project that already continues a source contract: a version imported from the same source contract id (same path and
    /// type) in the same project group, or a recorded location of a moved contract. Never a contract of another project.
    /// </summary>
    private async Task<ApiContractRow?> ContinuedContractAsync(string sourceContractId, ApiContractProtocol protocol, HashSet<string> group, CancellationToken ct)
    {
        var keys = group.ToList();
        var ids = await db.ApiContractVersions.AsNoTracking().Where(v => v.SourcePath == sourceContractId && v.SourceProjectKey != null && keys.Contains(v.SourceProjectKey))
            .Select(v => v.ContractId).Distinct().ToListAsync(ct);
        ids.AddRange(await db.ContractReviewDecisions.AsNoTracking()
            .Where(d => d.Kind == DecisionLocation && d.SubjectKey == sourceContractId && d.ProjectKey != null && keys.Contains(d.ProjectKey) && d.ContractId != null)
            .Select(d => d.ContractId!.Value).ToListAsync(ct));
        if (ids.Count == 0) return null;
        return await ProjectContracts(group, protocol).Where(c => ids.Contains(c.Id)).OrderByDescending(c => c.UpdatedAt).FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Classifies a source contract against the project's existing contracts. Same location → SameContract/NewVersion. Otherwise relocation
    /// candidates (same project group, same protocol, whose own source locations are no longer in this snapshot — a contract still present at its
    /// old path is a copy, not a move): the identical document → MovedUnchanged (one) or Ambiguous (several); ≥ 60 % of operations shared →
    /// MovedChanged (one) or Ambiguous; ≥ 30 % shared, or the same title with any shared operation → PossibleMatch (one) or Ambiguous; else
    /// NewContract. A title alone never matches. Candidates a person rejected for this source contract are left out.
    /// </summary>
    private async Task<IdentityMatch> ClassifyAsync(IqrSourceSnapshot snapshot, SourceContract source, string? docSha, HashSet<string> group, CancellationToken ct)
    {
        await EnsureLegacyResolvedAsync(ct);
        var protocol = source.Type == SourceContractType.OpenApi ? ApiContractProtocol.Rest : ApiContractProtocol.GraphQl;
        if (await ContinuedContractAsync(source.Id, protocol, group, ct) is { } continued)
        {
            var latest = await db.ApiContractVersions.AsNoTracking().Where(v => v.ContractId == continued.Id).OrderByDescending(v => v.Version).Select(v => new { v.Version, v.Sha256 }).FirstOrDefaultAsync(ct);
            return new(SameSource(latest?.Sha256, source, docSha) ? ContractIdentityClassification.SameContract : ContractIdentityClassification.NewVersion, continued, latest?.Version,
                continued.Lifecycle == nameof(ContractLifecycle.Archived), []);
        }
        var keys = group.ToList();
        // A legacy contract with this source location whose project is not resolved: never bypassed by a second contract — a person assigns it first.
        var legacyIds = await db.ApiContractVersions.AsNoTracking().Where(v => v.SourcePath == source.Id && (v.SourceProjectKey == null || keys.Contains(v.SourceProjectKey)))
            .Select(v => v.ContractId).Distinct().ToListAsync(ct);
        if (legacyIds.Count > 0 && await db.ApiContracts.AsNoTracking().Where(c => legacyIds.Contains(c.Id) && c.IdentityState == IdentityNeedsReview).FirstOrDefaultAsync(ct) is { } legacy)
            return new(ContractIdentityClassification.Ambiguous, null, null, false,
                [new(legacy.Key, legacy.Name, 0, source.Id, false, false, 0, 0, source.Operations.Count, 0,
                    [$"legacy contract {legacy.Key} holds this source location but its project is not resolved: assign its project first"])]);
        var rejected =(await db.ContractReviewDecisions.AsNoTracking().Where(d => d.Kind == DecisionReject && d.SubjectKey == source.Id && d.ProjectKey != null && keys.Contains(d.ProjectKey))
            .Select(d => d.ContractId).ToListAsync(ct)).ToHashSet();
        if (rejected.Contains(null)) return new(ContractIdentityClassification.NewContract, null, null, false, []); // "import as a new contract" was decided
        var present = (snapshot.EvidenceDomains?.Contracts.Contracts ?? []).Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        var sourceOps = source.Operations.Select(o => o.Name).ToHashSet(StringComparer.Ordinal);
        var candidates = new List<(ContractIdentityCandidate Candidate, ApiContractRow Row)>();
        foreach (var row in await ProjectContracts(group, protocol).Where(c => c.Lifecycle != nameof(ContractLifecycle.Archived)).ToListAsync(ct))
        {
            if (rejected.Contains(row.Id)) continue;
            var versions = await db.ApiContractVersions.AsNoTracking().Where(v => v.ContractId == row.Id).OrderByDescending(v => v.Version)
                .Select(v => new { v.Version, v.Sha256, v.SourcePath, v.ContractJson }).ToListAsync(ct);
            if (versions.Count == 0) continue;
            var locations = versions.Select(v => v.SourcePath).Where(p => p is not null).Concat(await db.ContractReviewDecisions.AsNoTracking()
                .Where(d => d.Kind == DecisionLocation && d.ContractId == row.Id).Select(d => (string?)d.SubjectKey).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
            if (locations.Count == 0 || locations.Any(l => l is not null && present.Contains(l))) continue; // still at its old location (or not source-backed)
            var latest = versions[0];
            var doc = JsonSerializer.Deserialize<SourceContract>(latest.ContractJson, ExecutionEvidenceStore.Json);
            var ops = (doc?.Operations ?? []).Select(o => o.Name).ToHashSet(StringComparer.Ordinal);
            var shared = ops.Intersect(sourceOps, StringComparer.Ordinal).Count();
            var union = ops.Union(sourceOps, StringComparer.Ordinal).Count();
            var overlap = union == 0 ? 0 : (double)shared / union;
            var sameDocument = docSha is not null && SameSource(latest.Sha256, source, docSha);
            var sameTitle = string.Equals(row.Name, source.Name, StringComparison.OrdinalIgnoreCase) || string.Equals(doc?.Name, source.Name, StringComparison.OrdinalIgnoreCase);
            if (!sameDocument && overlap < PossibleOverlap && !(sameTitle && shared > 0)) continue;
            var evidence = new List<string> { $"same project ({string.Join(", ", group.OrderBy(k => k, StringComparer.Ordinal))}) and protocol ({protocol})" };
            if (sameDocument) evidence.Add("identical document (SHA-256 of the retained text)");
            evidence.Add($"{shared} of {sourceOps.Count} operations also in {row.Key}@v{latest.Version} ({ops.Count} operations, {overlap:P0} overlap)");
            if (sameTitle) evidence.Add($"same title \"{source.Name}\"");
            evidence.Add($"its last location {latest.SourcePath ?? "(upload)"} is not in this snapshot");
            candidates.Add((new(row.Key, row.Name, latest.Version, latest.SourcePath, sameDocument, sameTitle, shared, ops.Count, sourceOps.Count, Math.Round(overlap, 3), evidence), row));
        }
        candidates = [.. candidates.OrderByDescending(c => c.Candidate.SameDocument).ThenByDescending(c => c.Candidate.OperationOverlap).ThenBy(c => c.Candidate.Key, StringComparer.Ordinal)];
        var list = candidates.Select(c => c.Candidate).ToList();
        IdentityMatch One(ContractIdentityClassification single, IReadOnlyList<(ContractIdentityCandidate Candidate, ApiContractRow Row)> pool) =>
            pool.Count == 1 ? new(single, pool[0].Row, pool[0].Candidate.LatestVersion, false, list) : new(ContractIdentityClassification.Ambiguous, null, null, false, list);
        var exact = candidates.Where(c => c.Candidate.SameDocument).ToList();
        if (exact.Count > 0) return One(ContractIdentityClassification.MovedUnchanged, exact);
        var strong = candidates.Where(c => c.Candidate.OperationOverlap >= StrongOverlap && c.Candidate.SharedOperations > 0).ToList();
        if (strong.Count > 0) return One(ContractIdentityClassification.MovedChanged, strong);
        if (candidates.Count > 0) return One(ContractIdentityClassification.PossibleMatch, candidates);
        return new(ContractIdentityClassification.NewContract, null, null, false, []);
    }

    /// <summary>
    /// Imports a contract a Source Analysis snapshot already holds (no re-upload) into the snapshot's project: the same source location continues the
    /// project's contract (next version, or nothing when identical); the identical document at a new location is recorded as a location of the
    /// existing contract; a possible relocation is not imported until a person decides (<see cref="DecideIdentityAsync"/>); otherwise a new contract
    /// of the project. An explicit target must belong to the same project. The snapshot is never changed.
    /// </summary>
    public async Task<ContractImportResult> ImportFromSourceAsync(SourceContractImportRequest request, CancellationToken ct) => await ImportFromSourceAsync(request, false, ct);

    private async Task<ContractImportResult> ImportFromSourceAsync(SourceContractImportRequest request, bool decided, CancellationToken ct)
    {
        ContractImportResult Fail(string message) => new(false, false, null, null, message, [message]);
        if (sources is null || !sources.SourceAnalysisEnabled) return Fail("Source Analysis is not available.");
        if (await sources.ResolveAsync(string.Empty, request.SnapshotId, ct) is not { } snapshot) return Fail("The source snapshot does not exist.");
        if (snapshot.EvidenceDomains?.Contracts.Contracts.FirstOrDefault(c => c.Id == request.SourceContractId) is not { } source) return Fail("The snapshot has no such contract.");
        if (source.Type is not (SourceContractType.OpenApi or SourceContractType.GraphQlSchema)) return Fail("Only OpenAPI and GraphQL schema contracts can be imported for contract testing.");
        var protocol = source.Type == SourceContractType.OpenApi ? ApiContractProtocol.Rest : ApiContractProtocol.GraphQl;
        var projectKey = ProjectKeyOf(snapshot);
        var group = await ProjectGroupAsync(projectKey, ct);
        // The snapshot's retained document text (snapshots analysed before retention have none): with it the version is validated against its
        // full schema and fingerprinted by the document.
        var path = source.File.Replace('\\', '/');
        var document = await db.SourceContractDocuments.AsNoTracking().Where(d => d.SnapshotId == snapshot.Id && d.Path == path).Select(d => new { d.Text, d.Sha256 }).FirstOrDefaultAsync(ct);
        var sha = SourceFingerprint(source, document?.Sha256);
        if (await db.ApiContractVersions.AsNoTracking().Where(v => v.SourceSnapshotId == snapshot.Id && v.SourcePath == source.Id)
                .Join(db.ApiContracts.AsNoTracking(), v => v.ContractId, c => c.Id, (v, c) => new { c.Key, v.Version }).FirstOrDefaultAsync(ct) is { } already)
            return new(false, true, already.Key, already.Version, $"Already imported from this snapshot as {ContractIdentity.ContractRef(already.Key, already.Version)}; nothing was stored.", []);
        var now = _clock.GetUtcNow();
        ApiContractRow contract;
        if (!string.IsNullOrWhiteSpace(request.ContractKey))
        {
            if (await db.ApiContracts.FirstOrDefaultAsync(c => c.Key == request.ContractKey, ct) is not { } existing) return Fail($"Contract {request.ContractKey} does not exist.");
            if (existing.Lifecycle == nameof(ContractLifecycle.Archived)) return Fail($"{existing.Name} is archived: it is read-only.");
            if (existing.Protocol != protocol.ToString()) return Fail($"{existing.Name} is a {existing.Protocol} contract; this source contract is {protocol}.");
            if (existing.IdentityState == IdentityNeedsReview) return Fail($"{existing.Name} needs an identity review (its project is not resolved): assign its project first.");
            if (existing.ProjectKey is { } owner && !group.Contains(owner))
                return Fail($"{existing.Name} belongs to project {owner}; this source contract belongs to project {projectKey}. Contracts are never shared across projects.");
            if (existing.ProjectKey is null) { existing.ProjectKey = projectKey; existing.IdentityState = null; } // an uploaded contract adopted by its project
            contract = existing;
        }
        else if (!string.IsNullOrWhiteSpace(request.Name))
            contract = await NewContractAsync(request.Name.Trim()); // the tester's explicit choice: a new contract of this project
        else
        {
            var match = await ClassifyAsync(snapshot, source, document?.Sha256, group, ct);
            switch (match.Classification)
            {
                case ContractIdentityClassification.SameContract or ContractIdentityClassification.NewVersion when match.Contract is { } same:
                    if (match.Archived) return Fail($"{same.Name} is archived: it is read-only.");
                    contract = await db.ApiContracts.FirstAsync(c => c.Id == same.Id, ct);
                    break;
                case ContractIdentityClassification.MovedUnchanged when match.Contract is { } moved:
                    await RecordAsync(DecisionLocation, moved.Id, projectKey, source.Id, snapshot.Id,
                        $"Identical document now at {source.File} (was {match.Candidates[0].LatestSourcePath}); recorded as a location of {moved.Key}.", "BirkNext (identical document)", ct);
                    return new(false, true, moved.Key, match.LatestVersion, $"Moved: the identical document of {ContractIdentity.ContractRef(moved.Key, match.LatestVersion ?? 0)} is now at {source.File}; recorded as its new location, no new version.", []) { Moved = true };
                case ContractIdentityClassification.NewContract:
                    contract = await NewContractAsync(source.Name);
                    break;
                default:
                    var names = string.Join(", ", match.Candidates.Select(c => $"{c.Key} ({c.SharedOperations}/{c.SourceOperations} operations shared)"));
                    var why = $"{source.File} may be a moved or renamed contract of this project ({match.Classification}: {names}). Review it: continue an existing contract or import it as a new one.";
                    return new(false, false, null, null, why, [why]) { NeedsReview = true };
            }
        }
        var last = await db.ApiContractVersions.AsNoTracking().Where(v => v.ContractId == contract.Id).OrderByDescending(v => v.Version).FirstOrDefaultAsync(ct);
        if (last is not null && SameSource(last.Sha256, source, document?.Sha256))
        {
            if (last.SourcePath != source.Id) // a confirmed relocation of the identical document: keep the new location in the history
                await RecordAsync(DecisionLocation, contract.Id, projectKey, source.Id, snapshot.Id, $"Identical document now at {source.File}.", decided ? "confirmed relocation" : "BirkNext", ct);
            return new(false, true, contract.Key, last.Version, $"Unchanged: the source evidence is identical to version {last.Version}; nothing was stored.", []);
        }
        var version = (last?.Version ?? 0) + 1;
        var stored = source with
        {
            Id = contract.Key,
            Limitations = [.. source.Limitations, document is null
                ? "Imported from a Source Analysis snapshot: the snapshot keeps the parsed contract, not the document text, so responses get structural (type-based) validation, not full schema validation."
                : "Imported from a Source Analysis snapshot with its retained document text: requests and responses are validated against the full schema."],
        };
        db.ApiContractVersions.Add(new ApiContractVersionRow
        {
            Id = Guid.NewGuid(), ContractId = contract.Id, Version = version, Sha256 = sha, Source = nameof(ContractVersionSource.SourceSnapshot),
            SourceLabel = Trim($"{snapshot.Archive.FileName} · {source.File} · snapshot {snapshot.Id:N}" + (snapshot.Commit is { Length: > 0 } commit && commit != "Unknown" ? $" · {commit}" : ""), 500),
            Format = source.Format, DocumentText = document?.Text ?? "", ContractJson = JsonSerializer.Serialize(stored, ExecutionEvidenceStore.Json), OperationCount = source.Operations.Count, ImportedAt = now,
            SourceSnapshotId = snapshot.Id, SourcePath = Trim(source.Id, 500), SourceProjectKey = projectKey,
        });
        contract.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        return new(true, false, contract.Key, version, $"Imported {source.Name} from source snapshot {snapshot.AnalyzedAt:yyyy-MM-dd HH:mm} as version {version} ({source.Operations.Count} operations).", []);

        async Task<ApiContractRow> NewContractAsync(string requested)
        {
            var name = Trim(requested, 200);
            var row = new ApiContractRow { Id = Guid.NewGuid(), Key = await UniqueKeyAsync(name, ct), Name = name, Protocol = protocol.ToString(), CreatedAt = now, UpdatedAt = now, ProjectKey = projectKey };
            db.ApiContracts.Add(row);
            return row;
        }
    }

    private async Task RecordAsync(string kind, Guid? contractId, string? projectKey, string subject, Guid? snapshotId, string detail, string by, CancellationToken ct)
    {
        db.ContractReviewDecisions.Add(new ContractReviewDecisionRow
        {
            Id = Guid.NewGuid(), Kind = kind, ContractId = contractId, ProjectKey = projectKey, SubjectKey = Trim(subject, 500), SnapshotId = snapshotId, Detail = detail,
            DecidedBy = Trim(string.IsNullOrWhiteSpace(by) ? "local user" : by, 200), DecidedAt = _clock.GetUtcNow(),
        });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// A person's decision on a possible relocation: confirm → the source contract continues the chosen contract of the same project (next version,
    /// or a recorded location when the document is identical); reject a candidate → it is no longer offered; reject without a target → imported as a
    /// new contract of the project. The decision, the evidence and who/when are recorded; nothing else is rewritten.
    /// </summary>
    public async Task<ContractImportResult> DecideIdentityAsync(ContractIdentityDecisionRequest request, string decidedBy, CancellationToken ct)
    {
        ContractImportResult Fail(string message) => new(false, false, null, null, message, [message]);
        if (sources is null || !sources.SourceAnalysisEnabled) return Fail("Source Analysis is not available.");
        if (await sources.ResolveAsync(string.Empty, request.SnapshotId, ct) is not { } snapshot) return Fail("The source snapshot does not exist.");
        if (snapshot.EvidenceDomains?.Contracts.Contracts.FirstOrDefault(c => c.Id == request.SourceContractId) is not { } source) return Fail("The snapshot has no such contract.");
        var projectKey = ProjectKeyOf(snapshot);
        var group = await ProjectGroupAsync(projectKey, ct);
        var docSha = await db.SourceContractDocuments.AsNoTracking().Where(d => d.SnapshotId == snapshot.Id && d.Path == source.File.Replace('\\', '/')).Select(d => d.Sha256).FirstOrDefaultAsync(ct);
        var match = await ClassifyAsync(snapshot, source, docSha, group, ct);
        var candidate = request.TargetKey is null ? null : match.Candidates.FirstOrDefault(c => c.Key == request.TargetKey);
        if (request.TargetKey is not null && candidate is null) return Fail($"{request.TargetKey} is not a relocation candidate of {source.File} in this project.");
        var target = candidate is null ? null : await db.ApiContracts.AsNoTracking().FirstAsync(c => c.Key == candidate.Key, ct);
        var detail = JsonSerializer.Serialize(new { match.Classification, file = source.File, candidate }, ExecutionEvidenceStore.Json);
        if (request.Confirm)
        {
            if (target is null) return Fail("Choose the contract this source contract continues.");
            await RecordAsync(DecisionConfirm, target.Id, projectKey, source.Id, snapshot.Id, detail, decidedBy, ct);
            return await ImportFromSourceAsync(new SourceContractImportRequest(snapshot.Id, source.Id, target.Key, null), true, ct);
        }
        await RecordAsync(DecisionReject, target?.Id, projectKey, source.Id, snapshot.Id, detail, decidedBy, ct);
        return target is null
            ? await ImportFromSourceAsync(new SourceContractImportRequest(snapshot.Id, source.Id, null, source.Name), true, ct)
            : new(false, false, null, null, $"{target.Key} is no longer offered for {source.File}.", []);
    }

    /// <summary>The recorded identity decisions and locations of a contract (history: who, when, what).</summary>
    public async Task<IReadOnlyList<ContractReviewDecisionRow>> DecisionsAsync(string key, CancellationToken ct) =>
        await db.ApiContracts.AsNoTracking().FirstOrDefaultAsync(c => c.Key == key, ct) is { } c
            ? await db.ContractReviewDecisions.AsNoTracking().Where(d => d.ContractId == c.Id).OrderBy(d => d.DecidedAt).ToListAsync(ct) : [];

    /// <summary>
    /// Legacy identity resolution (contracts stored before project scope): each source-backed version gets the project of its snapshot; a contract
    /// whose versions all resolve to one project group gets that project; a contract without any source-backed version is Unscoped; a contract
    /// whose versions point to different projects, or to snapshots that no longer exist, is marked NeedsReview and is never continued automatically
    /// until a person assigns its project. Nothing is merged, split or deleted. Idempotent.
    /// </summary>
    public async Task<(int Resolved, int Unscoped, int NeedsReview)> ResolveLegacyIdentitiesAsync(CancellationToken ct)
    {
        var pending = await db.ApiContracts.Where(c => c.ProjectKey == null && c.IdentityState == null).ToListAsync(ct);
        int resolved = 0, unscoped = 0, review = 0;
        var cache = new Dictionary<Guid, string?>();
        foreach (var contract in pending)
        {
            var versions = await db.ApiContractVersions.Where(v => v.ContractId == contract.Id && v.SourceSnapshotId != null).ToListAsync(ct);
            if (versions.Count == 0) { contract.IdentityState = IdentityUnscoped; unscoped++; continue; }
            var missing = false;
            foreach (var v in versions)
            {
                if (!cache.TryGetValue(v.SourceSnapshotId!.Value, out var key))
                    cache[v.SourceSnapshotId!.Value] = key = sources is null ? null : await sources.ResolveAsync(string.Empty, v.SourceSnapshotId!.Value, ct) is { } s ? ProjectKeyOf(s) : null;
                if (key is null) missing = true; else v.SourceProjectKey ??= key;
            }
            var keys = versions.Where(v => v.SourceProjectKey is not null).Select(v => v.SourceProjectKey!).Distinct(StringComparer.Ordinal).ToList();
            var oneGroup = keys.Count > 0 && (await ProjectGroupAsync(keys[0], ct)).IsSupersetOf(keys);
            if (!missing && oneGroup) { contract.ProjectKey = versions.OrderBy(v => v.Version).First().SourceProjectKey; resolved++; }
            else { contract.IdentityState = IdentityNeedsReview; review++; }
        }
        await db.SaveChangesAsync(ct);
        return (resolved, unscoped, review);
    }

    private bool _legacyChecked;

    /// <summary>Resolves legacy identities before anything is classified, so a contract stored before project scope is never duplicated by an import.</summary>
    private async Task EnsureLegacyResolvedAsync(CancellationToken ct)
    {
        if (_legacyChecked) return;
        if (await db.ApiContracts.AnyAsync(c => c.ProjectKey == null && c.IdentityState == null, ct)) await ResolveLegacyIdentitiesAsync(ct);
        _legacyChecked = true;
    }

    /// <summary>A person assigns the project of a contract whose identity needs review (one of the projects its versions came from).</summary>
    public async Task<string?> AssignProjectAsync(string key, string projectKey, string by, CancellationToken ct)
    {
        if (await db.ApiContracts.FirstOrDefaultAsync(c => c.Key == key, ct) is not { } contract) return "Not found.";
        if (contract.IdentityState != IdentityNeedsReview) return "Only a contract whose identity needs review can be assigned a project.";
        var known = await db.ApiContractVersions.AsNoTracking().Where(v => v.ContractId == contract.Id && v.SourceProjectKey != null).Select(v => v.SourceProjectKey!).Distinct().ToListAsync(ct);
        if (!known.Contains(projectKey)) return $"{projectKey} is not a project any version of {key} was imported from ({string.Join(", ", known)}).";
        contract.ProjectKey = projectKey;
        contract.IdentityState = null;
        await db.SaveChangesAsync(ct);
        await RecordAsync(DecisionProject, contract.Id, projectKey, key, null, $"Project {projectKey} assigned (versions came from {string.Join(", ", known)}).", by, ct);
        return null;
    }
}
