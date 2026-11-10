using System.Globalization;
using System.Text;
using System.Text.Json;
using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.Api.Services.ContractTesting;
using BirkNext.Api.Services.ExecutionEvidence;
using BirkNext.Api.Services.SecurityClassification;
using BirkNext.Api.Services.SourceAnalysis;
using BirkNext.Api.Services.TestData;
using BirkNext.Parquet;
using BirkNext.TestData;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services.Parquet.Quality;

/// <summary>
/// Parquet Quality Review over the existing Parquet engine and stores: dataset contracts come from documents retained with Source Analysis
/// snapshots (an explicit project + snapshot), rules from <see cref="ParquetRuleGenerator"/>, evaluation from <see cref="ParquetInspectionService"/>,
/// evidence into <c>parquet_inspections</c> (row values never stored), runs from Execution Evidence (read-only). Nothing here contacts another system.
/// </summary>
public sealed class ParquetQualityService(AppDbContext db, ParquetInspectionService parquet, ExecutionEvidenceStore executions,
    IReviewSourceEvidenceProvider? sources = null, TestDataCatalog? testData = null, TimeProvider? clock = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private DateTimeOffset Now => (clock ?? TimeProvider.System).GetUtcNow();
    private const string Expired = "The Parquet session has expired; open the file again. Saved evidence and reports stay available in Evidence & History.";

    // ── Contracts ────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Dataset contracts of the newest snapshot of each project (or exactly <paramref name="snapshotId"/>), parsed from retained documents.</summary>
    public async Task<(List<(DatasetContractSource Source, DatasetContract Contract)> Contracts, List<string> Limitations)> ContractsAsync(string? projectKey, Guid? snapshotId, CancellationToken ct)
    {
        var limitations = new List<string>();
        if (sources is not { SourceAnalysisEnabled: true }) return ([], [ReviewSourceEvidenceProvider.Disabled]);
        var snapshots = (await sources.ListAsync(string.Empty, ct)).OrderByDescending(s => s.AnalyzedAt).Take(50).ToList();
        if (snapshotId is { } pinned) snapshots = snapshots.Where(s => s.Id == pinned).ToList();
        if (!string.IsNullOrWhiteSpace(projectKey)) snapshots = snapshots.Where(s => ContractRegistryService.ProjectKeyOf(s) == projectKey).ToList();
        var ids = snapshots.Select(s => s.Id).ToList();
        var documents = await db.SourceContractDocuments.AsNoTracking().Where(d => ids.Contains(d.SnapshotId) && d.Path.EndsWith(".md"))
            .Select(d => new { d.SnapshotId, d.Path, d.Text, d.Sha256 }).ToListAsync(ct);
        var result = new List<(DatasetContractSource, DatasetContract)>();
        var seenProjects = new HashSet<string>(StringComparer.Ordinal);
        foreach (var snapshot in snapshots)
        {
            var key = ContractRegistryService.ProjectKeyOf(snapshot);
            var docs = documents.Where(d => d.SnapshotId == snapshot.Id).ToList();
            if (docs.Count == 0) continue;
            // The newest snapshot of a project that holds dataset contracts, unless a snapshot was pinned.
            if (snapshotId is null && !seenProjects.Add(key)) continue;
            var contractDocs = docs.Where(d => DatasetContractParser.LooksLikeDatasetContract(d.Text)).ToList();
            var related = docs.Except(contractDocs).Select(d => (d.Path, d.Text)).ToList();
            var source = new DatasetContractSource(key, ReviewSourceEvidenceProvider.Identity(snapshot).DisplayName, snapshot.Id, snapshot.AnalyzedAt, snapshot.Archive.FileName);
            foreach (var d in contractDocs)
                foreach (var c in DatasetContractParser.Parse(d.Path, d.Text, d.Sha256, related)) result.Add((source, c));
        }
        if (result.Count == 0)
            limitations.Add("No dataset contract was found in the analysed source snapshots. Snapshots analysed before dataset-contract retention hold no contract text: analyse the source again (Project Import or Source Analysis).");
        return (result, limitations);
    }

    public async Task<(ContractDiscovery? Discovery, string? Error)> DiscoverAsync(Guid sessionId, string? projectKey, CancellationToken ct)
    {
        if (parquet.Session(sessionId) is not { } s) return (null, Expired);
        var (contracts, limitations) = await ContractsAsync(projectKey, null, ct);
        var manifest = parquet.Manifest(sessionId);
        var confirmed = await ConfirmedAssociationsAsync(s.Overview.Sha256, ct);
        var candidates = new List<ContractCandidate>();
        foreach (var (source, contract) in contracts)
        {
            var c = Candidate(s.Overview, source, contract, manifest, confirmed);
            if (c is not null) candidates.Add(c);
        }
        candidates = [.. candidates.OrderByDescending(Score).ThenBy(c => c.Source.ProjectKey, StringComparer.Ordinal).ThenBy(c => c.Contract.Key, StringComparer.Ordinal)];
        var outcome = ContractMatchKind.Unresolved;
        string explanation;
        if (candidates.Count == 0) explanation = contracts.Count == 0 ? "No dataset contract is available from Source Analysis. Generic inspection only." : "No contract matches this file by manifest, approved mapping, schema or storage name. Generic inspection only.";
        else
        {
            var top = candidates[0];
            var rivals = candidates.Skip(1).Where(c => Score(c) == Score(top)).ToList();
            if (top.Match == ContractMatchKind.AuthoritativeContract && rivals.Count == 0) { outcome = ContractMatchKind.AuthoritativeContract; explanation = $"{top.Contract.Key} {top.Contract.Version} from {top.Source.ProjectName}: {top.Reasons[0]}"; }
            else if (rivals.Count > 0)
            {
                outcome = ContractMatchKind.Ambiguous;
                candidates = [.. candidates.Select(c => Score(c) == Score(top) ? c with { Match = ContractMatchKind.Ambiguous } : c)];
                var projects = candidates.Where(c => c.Match == ContractMatchKind.Ambiguous).Select(c => c.Source.ProjectName).Distinct().ToList();
                explanation = projects.Count > 1
                    ? $"Equally good candidates in {projects.Count} projects ({string.Join(", ", projects)}). Choose the project the file belongs to."
                    : "Several contracts match equally well. Choose the contract; a person must confirm the association.";
            }
            else { outcome = ContractMatchKind.CandidateMapping; explanation = $"{top.Contract.Key} {top.Contract.Version} is a candidate (not proven): {string.Join("; ", top.Reasons)}. Confirm it before validating."; }
        }
        return (new ContractDiscovery
        {
            SessionId = sessionId, Outcome = outcome, Candidates = candidates, Explanation = explanation, Limitations = limitations,
            Projects = [.. contracts.Select(c => c.Source.ProjectKey).Distinct().Order(StringComparer.Ordinal)],
        }, null);
    }

    private static int Score(ContractCandidate c) => (c.Match == ContractMatchKind.AuthoritativeContract ? 1000 : 0) + c.MatchedColumns * 10 + c.MappedColumns * 5 + (c.Reasons.Any(r => r.StartsWith("File name", StringComparison.Ordinal)) ? 3 : 0);

    internal static ContractCandidate? Candidate(ParquetOverview file, DatasetContractSource source, DatasetContract contract, ParquetManifestSummary? manifest, IReadOnlyList<ContractAssociation> confirmed)
    {
        var actual = file.Columns.Select(c => c.Path.Split('.')[0]).ToHashSet(StringComparer.Ordinal);
        var expected = contract.Columns.Select(c => c.Name).ToList();
        var matched = expected.Count(actual.Contains);
        var documented = contract.Renames.Where(r => r.Kind == "column" && actual.Contains(r.From) && !actual.Contains(r.To) && expected.Contains(r.To))
            .Select(r => new ColumnMapping(r.From, r.To, "Documented", $"{r.Source}:L{r.Line}")).DistinctBy(m => m.Expected).ToList();
        var unmapped = expected.Where(e => !actual.Contains(e) && documented.All(m => m.Expected != e)).ToList();
        var reasons = new List<string>();
        var match = ContractMatchKind.CandidateMapping;
        if (manifest is { Sha256: { } sha } && sha.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase) && manifest.ContractVersion == contract.Version)
        { match = ContractMatchKind.AuthoritativeContract; reasons.Add($"The attached manifest names this file (SHA-256) and contract version {contract.Version}."); }
        if (confirmed.FirstOrDefault(a => a.ProjectKey == source.ProjectKey && a.ContractKey == contract.Key && a.ContractVersion == contract.Version) is { } known)
        { match = ContractMatchKind.AuthoritativeContract; reasons.Add($"Known approved mapping: this exact file was confirmed against it by {known.ReviewedBy ?? "a reviewer"}."); }
        if (matched == expected.Count && actual.Count == expected.Count) reasons.Add($"Schema matches: all {expected.Count} contract columns, no other column.");
        else if (matched + documented.Count > 0) reasons.Add($"Schema overlap: {matched} of {expected.Count} contract column(s) by name{(documented.Count > 0 ? $", {documented.Count} through documented renames ({string.Join(", ", documented.Select(m => $"{m.Actual}→{m.Expected}"))})" : "")}.");
        var nameHit = contract.Container is { Length: > 0 } container && file.FileName.Contains(container, StringComparison.OrdinalIgnoreCase);
        if (nameHit) reasons.Add($"File name contains the storage container name {contract.Container} (a hint only).");
        if (reasons.Count == 0) return null;
        if (unmapped.Count > 0) reasons.Add($"Not explained by the source: {string.Join(", ", unmapped)} (a person must declare a mapping, or the schema is incompatible).");
        var full = matched == expected.Count && actual.Count == expected.Count;
        return new ContractCandidate
        {
            Source = source, Contract = contract, Match = match, Reasons = reasons, MatchedColumns = matched, MappedColumns = documented.Count, ExpectedColumns = expected.Count,
            DocumentedMappings = documented, UnmappedExpected = unmapped,
            Confidence = match == ContractMatchKind.AuthoritativeContract ? "High" : full ? "Medium" : "Low",
        };
    }

    private async Task<List<ContractAssociation>> ConfirmedAssociationsAsync(string sha256, CancellationToken ct)
    {
        var docs = await db.ParquetInspections.AsNoTracking().Where(r => r.DocumentJson.Contains(sha256)).OrderByDescending(r => r.SavedAt).Take(20).Select(r => r.DocumentJson).ToListAsync(ct);
        return [.. docs.Select(d => JsonSerializer.Deserialize<ParquetInspectionEvidence>(d, Json)).Where(e => e?.Sha256 == sha256 && e.Association is { ReviewStatus: "Confirmed" })
            .Select(e => e!.Association!)];
    }

    /// <summary>The association as the server can vouch for it: the contract still exists in that snapshot with the same document text, documented
    /// mappings recomputed from the source, declared mappings checked (file column exists, contract column exists, one to one).</summary>
    public async Task<(ContractAssociation? Association, DatasetContract? Contract, string? Error)> ResolveAsync(ParquetOverview file, ContractAssociation requested, CancellationToken ct)
    {
        var (contracts, _) = await ContractsAsync(requested.ProjectKey, requested.SnapshotId, ct);
        var hit = contracts.FirstOrDefault(c => c.Contract.Key == requested.ContractKey && c.Contract.DocumentPath == requested.DocumentPath);
        if (hit.Contract is null) return (null, null, "The selected contract is not in that source snapshot (it may have been removed). Choose the contract again.");
        if (!string.IsNullOrEmpty(requested.DocumentSha256) && hit.Contract.DocumentSha256 != requested.DocumentSha256) return (null, null, "The contract document changed; choose the contract again.");
        if (hit.Contract.Version != requested.ContractVersion) return (null, null, $"The snapshot holds contract version {hit.Contract.Version}, not {requested.ContractVersion}; versions are never switched silently.");
        var candidate = Candidate(file, hit.Source, hit.Contract, parquet.Manifest(file.SessionId), await ConfirmedAssociationsAsync(file.Sha256, ct));
        var mappings = new List<ColumnMapping>(candidate?.DocumentedMappings ?? []);
        var actual = file.Columns.Select(c => c.Path.Split('.')[0]).ToHashSet(StringComparer.Ordinal);
        foreach (var m in requested.ColumnMappings.Where(m => m.Provenance == "Declared"))
        {
            if (!actual.Contains(m.Actual)) return (null, null, $"Declared mapping: the file has no column {Clip(m.Actual)}.");
            if (hit.Contract.Columns.All(c => c.Name != m.Expected)) return (null, null, $"Declared mapping: the contract has no column {Clip(m.Expected)}.");
            if (actual.Contains(m.Expected)) return (null, null, $"Declared mapping: the file already has {Clip(m.Expected)}; a mapping would hide it.");
            if (mappings.Any(x => x.Expected == m.Expected || x.Actual == m.Actual)) return (null, null, $"Declared mapping: {Clip(m.Actual)} or {Clip(m.Expected)} is already mapped.");
            mappings.Add(m with { Provenance = "Declared", Source = m.Source is { Length: > 0 } src ? Clip(src) : "declared by the tester" });
        }
        return (requested with
        {
            ContractVersion = hit.Contract.Version, DocumentSha256 = hit.Contract.DocumentSha256, Match = candidate?.Match ?? ContractMatchKind.CandidateMapping,
            Confidence = candidate?.Confidence ?? "Low", Reasons = candidate?.Reasons ?? ["Selected by the tester; no evidence links the file to this contract."],
            ColumnMappings = mappings, ReviewStatus = requested.ReviewStatus == "Confirmed" ? "Confirmed" : "Proposed",
        }, hit.Contract, null);
    }

    // ── Rules ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    public async Task<(RuleGeneration? Generation, string? Error)> GenerateAsync(Guid sessionId, ContractAssociation? requested, CancellationToken ct)
    {
        if (parquet.Session(sessionId) is not { } s) return (null, Expired);
        if (requested is null) return (ParquetRuleGenerator.FromSchema(s.Overview), null);
        var (association, contract, error) = await ResolveAsync(s.Overview, requested, ct);
        if (error is not null) return (null, error);
        return (ParquetRuleGenerator.Generate(contract!, association!, await ApprovedAsync(association!.ProjectKey, contract!.Key, contract.Version, ct)), null);
    }

    /// <summary>Server-side truth for a rule set sent by the browser: a generated rule is taken exactly as generated (its origin and review cannot be
    /// raised by the client); any other rule is the tester's own (USER_AUTHORED, active).</summary>
    internal static List<ParquetRule> Reconcile(IEnumerable<ParquetRule> requested, IReadOnlyList<ParquetRule> generated)
    {
        var byId = generated.ToDictionary(r => r.Id, StringComparer.Ordinal);
        var result = new List<ParquetRule>();
        foreach (var r in requested.Take(200))
        {
            if (byId.TryGetValue(r.Id, out var g)) { result.Add(g); continue; }
            if (r.Origin != RuleOrigin.UserAuthored || r.Id.Length is 0 or > 120) { result.Add(r with { Origin = RuleOrigin.UserAuthored, Review = RuleReviewState.Active, Id = $"user.{result.Count + 1}" }); continue; }
            result.Add(r with { Review = r.Review == RuleReviewState.Rejected ? RuleReviewState.Rejected : RuleReviewState.Active, Provenance = "authored by the tester", Name = r.Name is { } n ? Clip(n) : null });
        }
        // Generated rules the browser left out are still part of the requirement picture (never silently dropped).
        result.AddRange(generated.Where(g => result.All(r => r.Id != g.Id)));
        return result;
    }

    public async Task<ParquetRuleSetVersion?> ApprovedAsync(string projectKey, string contractKey, string version, CancellationToken ct)
    {
        var row = await db.ParquetRuleSets.AsNoTracking().Where(r => r.ProjectKey == projectKey && r.ContractKey == contractKey && r.ContractVersion == version)
            .OrderByDescending(r => r.Version).FirstOrDefaultAsync(ct);
        return row is null ? null : ToVersion(row);
    }

    public async Task<List<ParquetRuleSetVersion>> RuleSetsAsync(string projectKey, string contractKey, string version, CancellationToken ct) =>
        [.. (await db.ParquetRuleSets.AsNoTracking().Where(r => r.ProjectKey == projectKey && r.ContractKey == contractKey && r.ContractVersion == version)
            .OrderByDescending(r => r.Version).Take(50).ToListAsync(ct)).Select(ToVersion)];

    private static ParquetRuleSetVersion ToVersion(ParquetRuleSetRecord r) => new()
    {
        Id = r.Id, ProjectKey = r.ProjectKey, ContractKey = r.ContractKey, ContractVersion = r.ContractVersion, Name = r.Name, Version = r.Version,
        Rules = JsonSerializer.Deserialize<List<ParquetRule>>(r.RulesJson, Json) ?? [], ApprovedBy = r.ApprovedBy, ApprovedAt = r.ApprovedAt, Note = r.Note,
    };

    /// <summary>A test lead approves (or rejects) generated business rules and keeps own rules: a new immutable version. Only rules the generator
    /// produces for that exact contract text, or the tester's own rules, are accepted.</summary>
    public async Task<(ParquetRuleSetVersion? Version, string? Error)> ApproveAsync(Guid sessionId, ParquetRuleSetApproval approval, ContractAssociation requested, string approver, CancellationToken ct)
    {
        if (parquet.Session(sessionId) is not { } s) return (null, Expired);
        if (approval.Rules.Count is 0 or > 200) return (null, "Approve between 1 and 200 rules.");
        var (association, contract, error) = await ResolveAsync(s.Overview, requested, ct);
        if (error is not null) return (null, error);
        var generated = ParquetRuleGenerator.Generate(contract!, association!, null).Rules.ToDictionary(r => r.Id, StringComparer.Ordinal);
        var rules = new List<ParquetRule>();
        foreach (var r in approval.Rules)
        {
            if (generated.TryGetValue(r.Id, out var g))
            {
                if (g.Review == RuleReviewState.NotSupported) continue;
                rules.Add(g with { Review = r.Review == RuleReviewState.Rejected ? RuleReviewState.Rejected : RuleReviewState.Active });
                continue;
            }
            if (ParquetInspectionService.RuleProblem(r) is { } problem) return (null, problem);
            rules.Add(r with { Origin = RuleOrigin.UserAuthored, Review = r.Review == RuleReviewState.Rejected ? RuleReviewState.Rejected : RuleReviewState.Active, Provenance = $"authored by {approver}" });
        }
        var next = (await db.ParquetRuleSets.Where(r => r.ProjectKey == association!.ProjectKey && r.ContractKey == contract!.Key && r.ContractVersion == contract.Version)
            .Select(r => (int?)r.Version).MaxAsync(ct) ?? 0) + 1;
        var record = new ParquetRuleSetRecord
        {
            Id = Guid.NewGuid(), ProjectKey = association!.ProjectKey, ContractKey = contract!.Key, ContractVersion = contract.Version, Version = next,
            Name = Clip(string.IsNullOrWhiteSpace(approval.Name) ? $"{contract.Title} {contract.Version}" : approval.Name), RulesJson = JsonSerializer.Serialize(rules, Json),
            ApprovedBy = Clip(approver), ApprovedAt = Now, Note = approval.Note is { Length: > 0 } n ? n[..Math.Min(1000, n.Length)] : null,
        };
        db.ParquetRuleSets.Add(record);
        await db.SaveChangesAsync(ct);
        return (ToVersion(record), null);
    }

    // ── Review: schema + data + business + coverage + dimensions ────────────────────────────────────────────────────────────

    private async Task<(QualityContext Context, ContractAssociation? Association, DatasetContract? Contract, RuleGeneration? Generation, string? Error)> PrepareAsync(ParquetOverview file, ContractAssociation? requested, CancellationToken ct)
    {
        var canaries = Canaries(out var available);
        if (requested is null)
            return (new QualityContext { Manifest = parquet.Manifest(file.SessionId), Canaries = canaries, CanaryCatalogAvailable = available }, null, null, null, null);
        var (association, contract, error) = await ResolveAsync(file, requested, ct);
        if (error is not null) return (QualityContext.None, null, null, null, error);
        var generation = ParquetRuleGenerator.Generate(contract!, association!, await ApprovedAsync(association!.ProjectKey, contract!.Key, contract.Version, ct));
        var map = association.ColumnMappings.ToDictionary(m => m.Expected, m => m.Actual, StringComparer.Ordinal);
        var values = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var m in association.ColumnMappings.Where(m => m.Actual != m.Expected))
            if (contract.Columns.FirstOrDefault(c => c.Name == m.Expected) is { AllowedValues.Count: > 0 } col)
            {
                var renames = contract.Renames.Where(r => r.Kind == "value" && col.AllowedValues.Contains(r.To)).ToDictionary(r => r.From, r => r.To, StringComparer.Ordinal);
                if (renames.Count > 0) values[m.Expected] = renames;
            }
        return (new QualityContext
        {
            ColumnMap = map, ValueMap = values, Manifest = parquet.Manifest(file.SessionId), Canaries = canaries, CanaryCatalogAvailable = available,
            ContractVersion = contract.Version, RuleSetRef = generation.RuleSetRef,
        }, association, contract, generation, null);
    }

    private List<string> Canaries(out bool available)
    {
        available = testData is not null;
        if (testData is null) return [];
        try
        {
            return [.. testData.Assets().Where(a => a.Approval == TestDataApproval.Approved && a.Kind == TestDataKind.EntityReference
                    && a.Capabilities.Contains(SecurityClassificationConsumerService.ProtectedIdentityCapability, StringComparer.OrdinalIgnoreCase) && a.Value is not null)
                .Select(a => a.Value!.Reveal().Trim()).Where(v => v.Length >= 4).Distinct(StringComparer.Ordinal)];
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { available = false; return []; }
    }

    public async Task<(ParquetQualityResult? Result, string? Error)> ReviewAsync(Guid sessionId, ParquetQualityRequest request, CancellationToken ct)
    {
        if (parquet.Session(sessionId) is not { } s) return (null, Expired);
        var (context, association, contract, generation, error) = await PrepareAsync(s.Overview, request.Association, ct);
        if (error is not null) return (null, error);
        var rules = generation is null ? request.Rules.Rules.Select(r => r with { Origin = r.Origin == RuleOrigin.SchemaDerived ? RuleOrigin.SchemaDerived : RuleOrigin.UserAuthored }).ToList()
            : Reconcile(request.Rules.Rules, generation.Rules);
        ParquetValidation? validation = null;
        var limitations = new List<string>(generation?.Limitations ?? []);
        if (rules.Any(r => r.Review == RuleReviewState.Active))
        {
            (validation, var vError) = parquet.Validate(sessionId, new ParquetRuleSet { Name = generation?.RuleSetName ?? request.Rules.Name, Version = request.Rules.Version, Rules = rules }, context, ct);
            if (vError is not null) return (null, vError);
        }
        var schema = contract is null ? new ParquetSchemaCheck(SchemaCompatibility.NotAssessed, [], "No contract selected: the schema is shown as written; nothing to compare against.")
            : ParquetSchemaChecker.Check(contract, s.Overview, association!.ColumnMappings);
        var (stats, sError) = parquet.Statistics(sessionId, context, Pivot(rules), ct);
        if (sError is not null) limitations.Add($"Statistics not available: {sError}");
        var coverage = Coverage(generation?.Requirements ?? [], rules, validation, contract);
        var dims = Dimensions(s.Overview, schema, association, rules, validation, null);
        return (new ParquetQualityResult
        {
            SessionId = sessionId, Association = association, Schema = schema, Validation = validation, Coverage = coverage, Dimensions = dims, Statistics = stats,
            Manifest = context.Manifest, NextAction = Next(association, rules, validation, coverage), Limitations = limitations,
        }, null);
    }

    private static (string, string, string)? Pivot(IEnumerable<ParquetRule> rules)
    {
        var sum = rules.FirstOrDefault(r => r.Kind == ParquetRuleKind.SumIdentity && r.GroupColumns.Count == 1);
        if (sum is not null) return (sum.GroupColumns[0], sum.Column!, sum.ValueColumn!);
        var roll = rules.FirstOrDefault(r => r.Kind == ParquetRuleKind.RollUp && r.GroupColumns.Count > 0);
        return roll is null ? null : (roll.Column!, roll.GroupColumns.FirstOrDefault(g => g != roll.Column) ?? roll.GroupColumns[0], roll.ValueColumn!);
    }

    internal static List<RequirementCoverage> Coverage(IReadOnlyList<DatasetContractRequirement> requirements, IReadOnlyList<ParquetRule> rules, ParquetValidation? validation, DatasetContract? contract)
    {
        var list = new List<RequirementCoverage>();
        foreach (var req in requirements)
        {
            var linked = rules.Where(r => r.RequirementIds.Contains(req.Id)).ToList();
            var results = linked.Select(r => validation?.Results.FirstOrDefault(x => x.RuleId == r.Id)).Where(x => x is not null).Select(x => x!).ToList();
            var source = contract is null ? "" : $"{contract.DocumentPath}:L{req.Line}";
            RequirementCoverage C(RequirementStatus st, string reason) => new()
            {
                RequirementId = contract is null ? req.Id : $"{contract.Key}@{contract.Version}#{req.Id}", Category = req.Category, Text = req.Text, Source = source,
                RuleIds = [.. linked.Select(r => r.Id)], Status = st, Reason = reason,
                Review = linked.Count == 0 ? "—" : linked.All(r => r.Review == RuleReviewState.Active) ? (linked.Any(r => r.Origin == RuleOrigin.ApprovedBusinessRule) ? "Approved" : "Generated") : linked.Any(r => r.Review == RuleReviewState.NeedsReview) ? "Needs review" : linked.All(r => r.Review == RuleReviewState.Rejected) ? "Rejected" : "Mixed",
            };
            var domain = req.Category == "Confidentiality" && !req.Machine;
            if (results.Any(r => r.Outcome == ParquetRuleOutcome.Fail)) { list.Add(C(RequirementStatus.CoveredFail, string.Join(" ", results.Where(r => r.Outcome == ParquetRuleOutcome.Fail).Select(r => $"{r.RuleName}: {r.Actual}.")))); continue; }
            if (domain) { list.Add(C(RequirementStatus.NotAssessed, "NEEDS DOMAIN DECISION: " + (req.Reason ?? "") + (results.Any(r => r.Outcome == ParquetRuleOutcome.Pass) ? " (No canary was found; that is not proof.)" : ""))); continue; }
            if (linked.Count == 0) { list.Add(C(RequirementStatus.NotSupported, req.Reason ?? "No rule can check this statement.")); continue; }
            if (linked.All(r => r.Review == RuleReviewState.Rejected)) { list.Add(C(RequirementStatus.NotApplicable, "A reviewer rejected the rule for this contract version.")); continue; }
            var pending = linked.Where(r => r.Review == RuleReviewState.NeedsReview).ToList();
            var activeLinked = linked.Where(r => r.Review == RuleReviewState.Active).ToList();
            if (activeLinked.Count == 0) { list.Add(C(RequirementStatus.NotAssessed, $"{pending.Count} generated rule(s) await review; unapproved business rules are not run.")); continue; }
            if (validation is null || results.Count < activeLinked.Count) { list.Add(C(RequirementStatus.NotAssessed, "Not run yet: run validation.")); continue; }
            var inconclusive = results.Where(r => r.Outcome == ParquetRuleOutcome.Inconclusive).ToList();
            if (inconclusive.Any(r => r.Actual.StartsWith("column ", StringComparison.Ordinal) || r.Actual == "not readable")) { list.Add(C(RequirementStatus.Blocked, string.Join(" ", inconclusive.Select(r => r.Explanation)))); continue; }
            if (inconclusive.Count > 0) { list.Add(C(RequirementStatus.NotAssessed, string.Join(" ", inconclusive.Select(r => r.Explanation)))); continue; }
            if (pending.Count > 0) { list.Add(C(RequirementStatus.NotAssessed, $"Checked rules pass, but {pending.Count} rule(s) of this requirement await review.")); continue; }
            list.Add(C(RequirementStatus.CoveredPass, $"{results.Count} rule(s) passed ({string.Join(", ", results.Select(r => r.Mode.ToString().ToLowerInvariant()).Distinct())})."));
        }
        return list;
    }

    private static readonly HashSet<ParquetRuleKind> Business = [ParquetRuleKind.SumIdentity, ParquetRuleKind.RollUp, ParquetRuleKind.OnlyWith, ParquetRuleKind.NullUnless, ParquetRuleKind.RequiredWhen, ParquetRuleKind.SortedBy];
    private static readonly HashSet<ParquetRuleKind> SchemaKinds = [ParquetRuleKind.ColumnExists, ParquetRuleKind.TypeIs];

    internal static List<QualityDimension> Dimensions(ParquetOverview file, ParquetSchemaCheck schema, ContractAssociation? association, IReadOnlyList<ParquetRule> rules, ParquetValidation? validation, ParquetCorrelation? correlation)
    {
        var unreadable = file.Columns.Count(c => !c.Readable);
        var readable = new QualityDimension("File readable", unreadable == 0 ? QualityState.Pass : QualityState.Partial,
            unreadable == 0 ? $"All {file.Columns.Count} columns decode ({file.Rows:N0} rows)." : $"{unreadable} of {file.Columns.Count} column(s) cannot be read by the explorer.");
        var declared = association?.ColumnMappings.Count(m => m.Provenance == "Declared") ?? 0;
        var schemaDim = schema.Compatibility switch
        {
            SchemaCompatibility.NotAssessed => new QualityDimension("Schema compatible", QualityState.NotAssessed, "No contract selected."),
            SchemaCompatibility.Incompatible => new QualityDimension("Schema compatible", QualityState.Fail, schema.Summary),
            _ when declared > 0 => new QualityDimension("Schema compatible", QualityState.Partial, $"Compatible only through {declared} declared column mapping(s); as delivered, the file does not match contract {association!.ContractVersion}."),
            SchemaCompatibility.CompatibleWithWarnings => new QualityDimension("Schema compatible", QualityState.Partial, schema.Summary),
            _ => new QualityDimension("Schema compatible", QualityState.Pass, schema.Summary),
        };
        QualityDimension Of(string name, Func<ParquetRuleResult, bool> pick, IEnumerable<ParquetRule> pool, string noneReason)
        {
            var results = validation?.Results.Where(pick).ToList() ?? [];
            var waiting = pool.Count(r => r.Review == RuleReviewState.NeedsReview);
            if (results.Count == 0) return new(name, QualityState.NotAssessed, waiting > 0 ? $"{waiting} rule(s) await review; nothing was run." : noneReason);
            int fail = results.Count(r => r.Outcome == ParquetRuleOutcome.Fail), inc = results.Count(r => r.Outcome == ParquetRuleOutcome.Inconclusive), pass = results.Count - fail - inc;
            var state = fail > 0 ? QualityState.Fail : inc > 0 || waiting > 0 ? QualityState.Partial : QualityState.Pass;
            return new(name, state, $"{pass} passed · {fail} failed · {inc} inconclusive{(waiting > 0 ? $" · {waiting} awaiting review" : "")}" + (association is { } a && name == "Business rules verified" && state == QualityState.Pass ? $" (contract {a.ContractVersion})" : "") + ".");
        }
        var data = Of("Data valid", r => !Business.Contains(r.Kind) && !SchemaKinds.Contains(r.Kind), rules.Where(r => !Business.Contains(r.Kind) && !SchemaKinds.Contains(r.Kind)), "No data rule was run.");
        var business = Of("Business rules verified", r => Business.Contains(r.Kind), rules.Where(r => Business.Contains(r.Kind)), association is null ? "No contract selected." : "The contract states no business rule BirkNext can check.");
        var e2e = correlation switch
        {
            { Level: CorrelationLevel.Confirmed } => new QualityDimension("E2E evidence verified", QualityState.Pass, correlation.Explanation),
            { Level: CorrelationLevel.Candidate or CorrelationLevel.ManualUnverified } => new QualityDimension("E2E evidence verified", QualityState.NotAssessed, correlation.Explanation),
            _ => new QualityDimension("E2E evidence verified", QualityState.NotAssessed, "No execution run is correlated with this file."),
        };
        return [readable, schemaDim, data, business, e2e];
    }

    private static string Next(ContractAssociation? association, IReadOnlyList<ParquetRule> rules, ParquetValidation? validation, IReadOnlyList<RequirementCoverage> coverage)
    {
        if (association is null) return "Select contract";
        if (association.ReviewStatus != "Confirmed") return "Confirm contract";
        if (rules.Count == 0) return "Generate rules";
        if (validation is null) return "Run validation";
        if (validation.Results.Any(r => r.Outcome == ParquetRuleOutcome.Fail) || rules.Any(r => r.Review == RuleReviewState.NeedsReview)) return "Review findings";
        if (coverage.Any(c => c.Status is RequirementStatus.NotAssessed or RequirementStatus.Blocked)) return "Review findings";
        return "Link evidence";
    }

    // ── Baseline ─────────────────────────────────────────────────────────────────────────────────────────────────────────────

    public async Task<(ParquetBaselineResult? Result, string? Error)> BaselineAsync(ParquetBaselineRequest request, CancellationToken ct)
    {
        if (parquet.Session(request.CurrentSessionId) is not { } current) return (null, Expired);
        if (request.BaselineSessionId is null && request.BaselineEvidenceId is null) return (null, "Choose a baseline: another local file or a saved evidence record.");
        var (review, error) = await ReviewAsync(request.CurrentSessionId, request.Quality ?? new ParquetQualityRequest(), ct);
        if (error is not null) return (null, error);
        var findings = new List<BaselineFinding>();
        var checks = new List<string>();
        var limitations = new List<string>();
        ParquetComparison? records = null;
        string label, kind; Guid? evidenceId = null; string? baselineSha; bool approved = false, comparable = true;
        List<ParquetRuleResult> baselineResults; List<string> baselineColumns; long baselineRows; ParquetStatistics? baselineStats; string? baselineContract, baselineVersion;
        if (request.BaselineSessionId is { } bs)
        {
            if (parquet.Session(bs) is not { } b) return (null, "The baseline file's session has expired; open it again.");
            if (bs == request.CurrentSessionId) return (null, "The baseline and the current file are the same session.");
            var (baselineReview, bError) = await ReviewAsync(bs, request.Quality ?? new ParquetQualityRequest(), ct);
            label = b.Overview.FileName; kind = "Local file"; baselineSha = b.Overview.Sha256;
            baselineResults = baselineReview?.Validation?.Results ?? [];
            if (bError is not null) limitations.Add($"The baseline could not be validated with the same rules: {bError}");
            baselineColumns = [.. b.Overview.Columns.Select(c => $"{c.Path}:{c.PhysicalType}{(c.LogicalType is { } lt ? "/" + lt : "")}")];
            baselineRows = b.Overview.Rows; baselineStats = baselineReview?.Statistics;
            baselineContract = request.Quality?.Association?.ContractKey; baselineVersion = request.Quality?.Association?.ContractVersion;
            limitations.Add("A local baseline file is not an approved baseline: differences are informational unless an approved rule decides them.");
            var (cmp, cError) = parquet.CompareByKeys(bs, request.CurrentSessionId, request.KeyColumns, ct);
            if (cError is not null) limitations.Add(cError);
            else if (cmp is not null)
            {
                var masked = request.KeyColumns.Any(k => ContractTestDataRules.IsIdentity(k));
                records = masked ? cmp with { Records = [.. cmp.Records.Select((r, i) => r with { Key = $"record {i + 1} (identifier key masked)" })] } : cmp;
                if (request.KeyColumns.Count > 0)
                    foreach (var (change, n) in cmp.RecordCounts.Where(kv => kv.Key is "Added" or "Removed" or "Changed" or "Ambiguous" && kv.Value > 0))
                        findings.Add(new("Records", $"{change} records (key {string.Join(" + ", request.KeyColumns)})", "—", n.ToString("N0", CultureInfo.InvariantCulture),
                            change == "Ambiguous" ? BaselineClass.Inconclusive : BaselineClass.Inconclusive, change == "Ambiguous" ? "Duplicate keys: these records cannot be matched." : "Changed records are not a regression by themselves; no approved expectation decides them."));
            }
        }
        else
        {
            var env = request.EnvironmentId ?? "";
            if (await parquet.EvidenceAsync(env, request.BaselineEvidenceId!.Value, ct) is not { } e) return (null, "The baseline evidence was not found for this Target Environment.");
            label = $"{e.FileName} (evidence {e.SavedAt:yyyy-MM-dd HH:mm} UTC)"; kind = "Saved evidence"; evidenceId = e.Id; baselineSha = e.Sha256; approved = e.BaselineApprovedBy is not null;
            baselineResults = e.Validation?.Results ?? []; baselineColumns = e.SchemaColumns; baselineRows = e.Rows; baselineStats = e.Statistics;
            baselineContract = e.Association?.ContractKey; baselineVersion = e.Association?.ContractVersion;
            limitations.Add("Compared at summary level (schema, counts, rule outcomes, contract aggregates): saved evidence holds no rows, so record-level differences are not available.");
            if (!approved) limitations.Add("This evidence is not an approved baseline: differences are informational.");
        }
        var currentContract = review!.Association?.ContractKey; var currentVersion = review.Association?.ContractVersion;
        checks.Add(baselineSha == current.Overview.Sha256 ? "Identical file (same SHA-256)." : "Different file content.");
        if (currentContract is not null && baselineContract is not null && currentContract != baselineContract)
        { comparable = false; checks.Add($"Different datasets: {baselineContract} vs {currentContract}."); }
        else checks.Add(currentContract is null ? "Dataset identity not established (no contract selected)." : $"Same dataset contract {currentContract}.");
        if (currentVersion is not null && baselineVersion is not null && currentVersion != baselineVersion) checks.Add($"Contract version changed: {baselineVersion} → {currentVersion}.");
        checks.Add(approved ? "Baseline approved." : "Baseline not approved.");
        if (!comparable)
            return (new ParquetBaselineResult(new BaselineAssessment { BaselineLabel = label, BaselineKind = kind, BaselineEvidenceId = evidenceId, BaselineSha256 = baselineSha, BaselineApproved = approved,
                Comparable = false, Checks = checks, Overall = BaselineClass.NotComparable, Limitations = [.. limitations, "Unrelated datasets are never compared as a regression."] }, null), null);

        // Schema
        var currentColumns = current.Overview.Columns.Select(c => $"{c.Path}:{c.PhysicalType}{(c.LogicalType is { } lt ? "/" + lt : "")}").ToList();
        var bNames = baselineColumns.Select(c => c.Split(':')[0]).ToHashSet(); var cNames = currentColumns.Select(c => c.Split(':')[0]).ToHashSet();
        var contractCols = review.Schema.Findings.Where(f => f.Kind != SchemaFindingKind.Unexpected).Select(f => f.ActualColumn ?? f.Column).ToHashSet();
        foreach (var removed in bNames.Except(cNames))
            findings.Add(new("Schema", removed, "present", "absent", contractCols.Contains(removed) || review.Schema.Compatibility == SchemaCompatibility.Incompatible ? BaselineClass.Incompatible : BaselineClass.Inconclusive,
                review.Schema.Compatibility == SchemaCompatibility.Incompatible ? "The current file no longer satisfies the contract schema." : "Removed column; the contract does not require it."));
        foreach (var added in cNames.Except(bNames))
            findings.Add(new("Schema", added, "absent", "present", BaselineClass.Inconclusive, contractCols.Contains(added) ? "A contract column appeared." : "Added column; not breaking by itself (the contract does not list it)."));
        foreach (var changed in currentColumns.Where(c => bNames.Contains(c.Split(':')[0]) && !baselineColumns.Contains(c)))
            findings.Add(new("Schema", changed.Split(':')[0], baselineColumns.First(b => b.Split(':')[0] == changed.Split(':')[0]).Split(':', 2)[1], changed.Split(':', 2)[1],
                review.Schema.Compatibility == SchemaCompatibility.Incompatible ? BaselineClass.Incompatible : BaselineClass.Inconclusive, "Type changed."));
        // Rows
        if (baselineRows != current.Overview.Rows)
            findings.Add(new("Rows", "Row count", baselineRows.ToString("N0", CultureInfo.InvariantCulture), current.Overview.Rows.ToString("N0", CultureInfo.InvariantCulture), BaselineClass.Inconclusive, "A changed count is not a regression by itself."));
        // Rule outcomes
        foreach (var r in review.Validation?.Results ?? [])
        {
            if (baselineResults.FirstOrDefault(b => b.RuleId == r.RuleId) is not { } b) continue;
            if (b.Outcome == ParquetRuleOutcome.Pass && r.Outcome == ParquetRuleOutcome.Fail) findings.Add(new("Rule", r.RuleName ?? r.RuleId, "Pass", "Fail", BaselineClass.Regression, $"{r.Actual}."));
            else if (b.Outcome == ParquetRuleOutcome.Fail && r.Outcome == ParquetRuleOutcome.Pass) findings.Add(new("Rule", r.RuleName ?? r.RuleId, "Fail", "Pass", BaselineClass.Improvement, "Now passes."));
            else if (b.Outcome != r.Outcome) findings.Add(new("Rule", r.RuleName ?? r.RuleId, b.Outcome.ToString(), r.Outcome.ToString(), BaselineClass.Inconclusive, "One side was not conclusive."));
        }
        // Contract aggregates
        var periodColumn = review.Association is null ? null : review.Schema.Findings.Select(f => f.Column).FirstOrDefault(c => c is "period" or "periode");
        var periodsDiffer = false;
        if (baselineStats is not null && review.Statistics is { } cs)
        {
            if (periodColumn is not null)
            {
                var bp = baselineStats.Columns.FirstOrDefault(c => c.Column == periodColumn || c.Column == review.Association!.ColumnMappings.FirstOrDefault(m => m.Expected == periodColumn)?.Actual)?.Categories.Select(x => x.Value).ToHashSet();
                var cp = cs.Columns.FirstOrDefault(c => c.Column == periodColumn || c.Column == review.Association!.ColumnMappings.FirstOrDefault(m => m.Expected == periodColumn)?.Actual)?.Categories.Select(x => x.Value).ToHashSet();
                if (bp is { Count: > 0 } && cp is { Count: > 0 } && !bp.SetEquals(cp)) { periodsDiffer = true; findings.Add(new("Period", periodColumn, string.Join(", ", bp), string.Join(", ", cp), BaselineClass.ExpectedChange, "A different period: aggregates are expected to differ.")); }
            }
            foreach (var cell in cs.Pivot)
            {
                var before = baselineStats.Pivot.FirstOrDefault(p => p.Row == cell.Row && p.Column == cell.Column);
                if (before is null || before.Value != cell.Value)
                    findings.Add(new("Aggregate", $"{cell.Row} · {cell.Column}", before?.Value.ToString(CultureInfo.InvariantCulture) ?? "absent", cell.Value.ToString(CultureInfo.InvariantCulture),
                        periodsDiffer ? BaselineClass.ExpectedChange : BaselineClass.Inconclusive, periodsDiffer ? "Different period." : "Changed business aggregate; no approved expectation decides it."));
            }
        }
        var overall = findings.Count == 0 ? BaselineClass.Unchanged
            : findings.Any(f => f.Class == BaselineClass.Incompatible) ? BaselineClass.Incompatible
            : findings.Any(f => f.Class == BaselineClass.Regression) ? BaselineClass.Regression
            : findings.Any(f => f.Class == BaselineClass.Inconclusive) ? BaselineClass.Inconclusive
            : findings.Any(f => f.Class == BaselineClass.Improvement) ? BaselineClass.Improvement : BaselineClass.ExpectedChange;
        return (new ParquetBaselineResult(new BaselineAssessment
        {
            BaselineLabel = label, BaselineKind = kind, BaselineEvidenceId = evidenceId, BaselineSha256 = baselineSha, BaselineApproved = approved, Comparable = true,
            Checks = checks, Findings = findings, Overall = overall, Limitations = limitations,
        }, records), null);
    }

    // ── Evidence ─────────────────────────────────────────────────────────────────────────────────────────────────────────────

    public async Task<(ParquetInspectionEvidence? Evidence, string? Error)> SaveEvidenceAsync(ParquetEvidenceRequest request, string initiator, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.EnvironmentId)) return (null, "Choose the Target Environment the evidence belongs to.");
        if (parquet.Session(request.SessionId) is not { } s) return (null, Expired);
        var association = request.Association is { } a && a.ReviewStatus == "Confirmed" ? a with { ReviewedBy = initiator, ReviewedAt = Now } : request.Association;
        var (review, error) = await ReviewAsync(request.SessionId, new ParquetQualityRequest { Association = association, Rules = request.Rules ?? new ParquetRuleSet() }, ct);
        if (error is not null) return (null, error);
        if (review!.Association is { } resolved && association is { ReviewStatus: "Confirmed" }) review = review with { Association = resolved with { ReviewStatus = "Confirmed", ReviewedBy = initiator, ReviewedAt = Now } };
        var (correlation, cError) = await CorrelateAsync(request, s.Overview, review.Association, ct);
        if (cError is not null) return (null, cError);
        BaselineAssessment? baseline = null;
        ParquetComparison? comparison = null;
        var keys = request.KeyColumns.Count > 0 ? request.KeyColumns : request.KeyColumn is { Length: > 0 } k ? [k] : [];
        if (request.CompareWith is { } other || request.BaselineEvidenceId is not null)
        {
            var (b, bError) = await BaselineAsync(new ParquetBaselineRequest
            {
                CurrentSessionId = request.SessionId, BaselineSessionId = request.CompareWith, BaselineEvidenceId = request.CompareWith is null ? request.BaselineEvidenceId : null,
                EnvironmentId = request.EnvironmentId, KeyColumns = keys, Quality = new ParquetQualityRequest { Association = association, Rules = request.Rules ?? new ParquetRuleSet() },
            }, ct);
            if (bError is not null) return (null, bError);
            baseline = b!.Assessment;
            // Counts and changed-field names only: record keys are row values and are never stored.
            if (b.Records is { } r) comparison = r with { Records = [.. r.Records.Select(x => x with { Key = "" })], Limitations = [.. r.Limitations, "Record keys are not stored in evidence."] };
        }
        var e2e = Dimensions(s.Overview, review.Schema, review.Association, [], review.Validation, correlation)[4];
        List<QualityDimension> dims = [.. review.Dimensions.Take(4), e2e];
        var contract = review.Association is null ? null : (await ResolveAsync(s.Overview, review.Association, ct)).Contract;
        var stats = Sanitize(review.Statistics, contract, review.Association);
        var evidence = new ParquetInspectionEvidence
        {
            EnvironmentId = request.EnvironmentId.Trim(), FileName = s.Overview.FileName, Sha256 = s.Overview.Sha256, SizeBytes = s.Overview.SizeBytes, Rows = s.Overview.Rows,
            Columns = s.Overview.Columns.Count, SchemaFingerprint = s.Overview.SchemaFingerprint, DocumentVersion = 2, FileSource = "Local file (temporary session)",
            Validation = review.Validation is { } v ? v with { Results = [.. v.Results.Select(x => x with { Details = [], DrillFilters = [] })] } : null,
            Comparison = comparison, Baseline = baseline, LinkedRunId = correlation?.RunId, Correlation = correlation, Association = review.Association, Schema = review.Schema,
            Coverage = review.Coverage, Dimensions = dims, Statistics = stats, Manifest = review.Manifest,
            SchemaColumns = [.. s.Overview.Columns.Select(c => $"{c.Path}:{c.PhysicalType}{(c.LogicalType is { } lt ? "/" + lt : "")}")],
            Limitations = [.. review.Limitations, "Row values are not stored: rule results keep counts and row numbers; category counts are kept only for values the contract enumerates."],
            Note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim()[..Math.Min(500, request.Note.Trim().Length)], SavedAt = Now, SavedBy = initiator,
        };
        db.ParquetInspections.Add(new ParquetInspectionRecord { Id = evidence.Id, EnvironmentId = evidence.EnvironmentId, FileName = evidence.FileName, SavedAt = evidence.SavedAt, DocumentJson = JsonSerializer.Serialize(evidence, Json) });
        await db.SaveChangesAsync(ct);
        return (evidence, null);
    }

    /// <summary>Durable statistics: counts always; category values only when the contract enumerates them (directly or through a documented value
    /// rename of a mapped column); aggregates only for contract-enumerated keys. Without a contract no category value is kept.</summary>
    internal static ParquetStatistics? Sanitize(ParquetStatistics? stats, DatasetContract? contract, ContractAssociation? association)
    {
        if (stats is null) return null;
        if (contract is null) return stats with { Columns = [.. stats.Columns.Select(c => c with { Categories = [] })], Pivot = [] };
        var enumerated = contract.Columns.SelectMany(c => c.AllowedValues).ToHashSet(StringComparer.Ordinal);
        var valueRenames = contract.Renames.Where(r => r.Kind == "value").ToDictionary(r => r.From, r => r.To, StringComparer.Ordinal);
        bool Allowed(string actualColumn, string value)
        {
            var expected = association?.ColumnMappings.FirstOrDefault(m => m.Actual == actualColumn)?.Expected ?? actualColumn;
            if (contract.Columns.FirstOrDefault(c => c.Name == expected) is not { AllowedValues.Count: > 0 } col) return false;
            return col.AllowedValues.Contains(value) || (expected != actualColumn && valueRenames.TryGetValue(value, out var to) && col.AllowedValues.Contains(to));
        }
        return stats with
        {
            Columns = [.. stats.Columns.Select(c => c with { Categories = [.. c.Categories.Where(x => Allowed(c.Column, x.Value))] })],
            Pivot = [.. stats.Pivot.Where(p => enumerated.Contains(p.Row) && enumerated.Contains(p.Column))],
        };
    }

    private async Task<(ParquetCorrelation? Correlation, string? Error)> CorrelateAsync(ParquetEvidenceRequest request, ParquetOverview file, ContractAssociation? association, CancellationToken ct)
    {
        if (request.LinkedRunId is not { } runId) return (new ParquetCorrelation { Level = CorrelationLevel.NotCorrelated, Explanation = "No execution run linked." }, null);
        var detail = await executions.GetAsync(runId, new ExecutionCallQuery(Take: 1), ct);
        if (detail is null) return (null, "The run does not exist in Execution Evidence.");
        var run = detail.Run;
        if (!string.Equals(run.EnvironmentId, request.EnvironmentId.Trim(), StringComparison.Ordinal)) return (null, "The run belongs to another Target Environment; evidence can only be linked within one environment.");
        if (association is not null && run.SourceSnapshotId is { } snap && sources is not null && await sources.ResolveAsync(string.Empty, snap, ct) is { } runSnapshot
            && ContractRegistryService.ProjectKeyOf(runSnapshot) != association.ProjectKey)
            return (null, $"The run belongs to project {ContractRegistryService.ProjectKeyOf(runSnapshot)}, not {association.ProjectKey}.");
        var evidence = new List<string>();
        var sha = file.Sha256;
        if (await db.ExecutionCalls.AsNoTracking().AnyAsync(c => c.RunId == runId && c.DocumentJson.Contains(sha), ct))
        {
            evidence.Add("The file's SHA-256 appears in the run's captured evidence.");
            return (new ParquetCorrelation { RunId = runId, RunName = run.Name, Level = CorrelationLevel.Confirmed, Evidence = evidence, Explanation = "Confirmed: the run's own evidence names this exact file (SHA-256)." }, null);
        }
        if (association is not null && (run.SourceSnapshotId == association.SnapshotId || run.ContractRefs.Any(r => r.Contains(association.ContractKey, StringComparison.Ordinal))))
        {
            evidence.Add(run.SourceSnapshotId == association.SnapshotId ? "Same source snapshot as the contract." : "The run references the dataset contract.");
            return (new ParquetCorrelation { RunId = runId, RunName = run.Name, Level = CorrelationLevel.Candidate, Evidence = evidence, Explanation = "Candidate: related by source or contract, but nothing in the run names this file. Not proof." }, null);
        }
        if (!request.ManualLink) return (null, "Nothing in the run relates it to this file. Link it as a manual, unverified association (with a reason), or choose another run.");
        if (string.IsNullOrWhiteSpace(request.ManualReason)) return (null, "Give the reason for the manual association.");
        var reason = request.ManualReason.Trim();
        return (new ParquetCorrelation { RunId = runId, RunName = run.Name, Level = CorrelationLevel.ManualUnverified, Evidence = [$"Tester's reason: {reason[..Math.Min(300, reason.Length)]}"],
            Explanation = "Manual, unverified association: it does not prove the run produced this file." }, null);
    }

    public async Task<(ParquetInspectionEvidence? Evidence, string? Error)> ApproveBaselineAsync(string environmentId, Guid id, string approver, CancellationToken ct)
    {
        var row = await db.ParquetInspections.FirstOrDefaultAsync(r => r.Id == id && r.EnvironmentId == environmentId, ct);
        if (row is null) return (null, "The evidence was not found.");
        var e = JsonSerializer.Deserialize<ParquetInspectionEvidence>(row.DocumentJson, Json)!;
        if (e.BaselineApprovedBy is not null) return (e, null);
        // Only the approval annotation is added; results, statistics and identity stay exactly as saved.
        e = e with { BaselineApprovedBy = Clip(approver), BaselineApprovedAt = Now };
        row.DocumentJson = JsonSerializer.Serialize(e, Json);
        await db.SaveChangesAsync(ct);
        return (e, null);
    }

    // ── Report ───────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Tester-oriented Markdown report from saved evidence only (no rows, no re-run; the original file may be gone).</summary>
    public static string Report(ParquetInspectionEvidence e)
    {
        var sb = new StringBuilder();
        string Cell(string? s) => (s ?? "").Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
        var cov = e.Coverage;
        int pass = cov.Count(c => c.Status == RequirementStatus.CoveredPass), fail = cov.Count(c => c.Status == RequirementStatus.CoveredFail),
            na = cov.Count(c => c.Status == RequirementStatus.NotAssessed), blocked = cov.Count(c => c.Status == RequirementStatus.Blocked),
            ns = cov.Count(c => c.Status == RequirementStatus.NotSupported), napp = cov.Count(c => c.Status == RequirementStatus.NotApplicable);
        sb.AppendLine($"# Parquet quality report — {Cell(e.FileName)}").AppendLine();
        sb.AppendLine($"Saved {e.SavedAt:yyyy-MM-dd HH:mm} UTC by {Cell(e.SavedBy)} · Target Environment `{Cell(e.EnvironmentId)}` · evidence `{e.Id}`").AppendLine();
        sb.AppendLine("## 1. Executive quality summary").AppendLine();
        if (e.Dimensions.Count == 0) sb.AppendLine("This evidence predates the quality review (document version 1): no quality dimensions were recorded.");
        foreach (var d in e.Dimensions) sb.AppendLine($"- **{d.Name}**: {d.State} — {Cell(d.Reason)}");
        sb.AppendLine().AppendLine($"Requirements: {cov.Count} listed · {pass} passed · {fail} failed · {na} not assessed · {blocked} blocked · {ns} not supported · {napp} not applicable.");
        if (cov.Count > 0) sb.AppendLine("Requirements are those BirkNext extracted from the contract document (column tables, rule sentences, manifest, determinism); prose it does not recognise is listed as not supported, not omitted.");
        sb.AppendLine().AppendLine("## 2. Dataset and source").AppendLine();
        sb.AppendLine($"- File: {Cell(e.FileName)} ({e.FileSource}), {e.SizeBytes:N0} bytes, {e.Rows:N0} rows, {e.Columns} columns");
        sb.AppendLine($"- SHA-256: `{e.Sha256}` · schema fingerprint `{e.SchemaFingerprint}`");
        sb.AppendLine("- The temporary file is not part of the evidence; this report cannot re-run validation.");
        sb.AppendLine().AppendLine("## 3. Contract and scope").AppendLine();
        if (e.Association is { } a)
        {
            sb.AppendLine($"- Contract `{Cell(a.ContractKey)}` version `{Cell(a.ContractVersion)}` · project `{Cell(a.ProjectKey)}` · source snapshot `{a.SnapshotId}`");
            sb.AppendLine($"- Document `{Cell(a.DocumentPath)}` (SHA-256 `{a.DocumentSha256[..Math.Min(12, a.DocumentSha256.Length)]}`)");
            sb.AppendLine($"- Association: {a.Match}, confidence {a.Confidence}, {a.ReviewStatus}{(a.ReviewedBy is null ? "" : $" by {Cell(a.ReviewedBy)}")}");
            foreach (var r in a.Reasons) sb.AppendLine($"  - {Cell(r)}");
            foreach (var m in a.ColumnMappings) sb.AppendLine($"- Column mapping ({m.Provenance}): `{Cell(m.Actual)}` → `{Cell(m.Expected)}` {Cell(m.Source)}");
        }
        else sb.AppendLine("No contract selected: generic inspection; requirement coverage is not assessed.");
        if (e.Validation is { } val) sb.AppendLine($"- Rule set: {Cell(val.RuleSetName)} v{val.RuleSetVersion} {(val.RuleSetRef is null ? "" : $"(`{Cell(val.RuleSetRef)}`)")} · scope: {Cell(val.Scope)}");
        sb.AppendLine().AppendLine("## 4. Validation coverage").AppendLine();
        if (cov.Count == 0) sb.AppendLine("Not assessed.");
        else
        {
            sb.AppendLine("| Requirement | Category | Status | Rules | Review |").AppendLine("|---|---|---|---|---|");
            foreach (var c in cov) sb.AppendLine($"| {Cell(c.RequirementId)} | {Cell(c.Category)} | {c.Status} | {c.RuleIds.Count} | {Cell(c.Review)} |");
        }
        void Section(string title, Func<RequirementCoverage, bool> pick)
        {
            sb.AppendLine().AppendLine(title).AppendLine();
            var items = cov.Where(pick).ToList();
            if (items.Count == 0) sb.AppendLine("None.");
            foreach (var c in items) sb.AppendLine($"- **{Cell(c.RequirementId)}** — {Cell(c.Text)}  \n  {c.Status}: {Cell(c.Reason)} ({Cell(c.Source)})");
        }
        Section("## 5. Passed requirements", c => c.Status == RequirementStatus.CoveredPass);
        Section("## 6. Failed requirements", c => c.Status == RequirementStatus.CoveredFail);
        Section("## 7. Not assessed requirements", c => c.Status is RequirementStatus.NotAssessed or RequirementStatus.Blocked or RequirementStatus.NotSupported or RequirementStatus.NotApplicable);
        sb.AppendLine().AppendLine("## 8. Data-quality statistics").AppendLine();
        if (e.Statistics is { } st)
        {
            sb.AppendLine($"Exact ({Cell(st.Scope)}); duplicate rows: {st.DuplicateRows:N0}.").AppendLine();
            sb.AppendLine("| Column | Rows | Nulls | Distinct | Min | Max |").AppendLine("|---|---|---|---|---|---|");
            foreach (var c in st.Columns) sb.AppendLine($"| {Cell(c.Column)} | {c.Rows:N0} | {c.Nulls:N0} | {(c.Distinct?.ToString("N0", CultureInfo.InvariantCulture) ?? "over limit")} | {Cell(c.Min ?? "—")} | {Cell(c.Max ?? "—")} |");
            if (st.Pivot.Count > 0)
            {
                sb.AppendLine().AppendLine($"Contract aggregate: sum of {Cell(st.PivotValueColumn)} by {Cell(st.PivotRowColumn)} × {Cell(st.PivotColumnColumn)}").AppendLine();
                foreach (var p in st.Pivot) sb.AppendLine($"- {Cell(p.Row)} · {Cell(p.Column)}: {p.Value.ToString(CultureInfo.InvariantCulture)}");
            }
        }
        else sb.AppendLine("Not available.");
        sb.AppendLine().AppendLine("## 9. Schema compatibility").AppendLine();
        if (e.Schema is { } sc)
        {
            sb.AppendLine($"{sc.Compatibility}: {Cell(sc.Summary)}").AppendLine();
            if (sc.Findings.Count > 0)
            {
                sb.AppendLine("| Column | Finding | Expected | Actual | Severity |").AppendLine("|---|---|---|---|---|");
                foreach (var f in sc.Findings) sb.AppendLine($"| {Cell(f.Column)} | {f.Kind} | {Cell(f.Expected)} | {Cell(f.Actual)} | {f.Severity} |");
            }
        }
        else sb.AppendLine("Not assessed.");
        sb.AppendLine().AppendLine("## 10. Baseline differences").AppendLine();
        if (e.Baseline is { } bl)
        {
            sb.AppendLine($"Baseline: {Cell(bl.BaselineLabel)} ({bl.BaselineKind}{(bl.BaselineApproved ? ", approved" : ", not approved")}) · overall **{bl.Overall}**");
            foreach (var c in bl.Checks) sb.AppendLine($"- {Cell(c)}");
            foreach (var f in bl.Findings) sb.AppendLine($"- {f.Class}: {Cell(f.Area)} {Cell(f.Subject)} {Cell(f.Baseline)} → {Cell(f.Current)} — {Cell(f.Reason)}");
            foreach (var l in bl.Limitations) sb.AppendLine($"- Limitation: {Cell(l)}");
        }
        else sb.AppendLine("No baseline compared.");
        sb.AppendLine().AppendLine("## 11. Confidentiality findings").AppendLine();
        var conf = cov.Where(c => c.Category == "Confidentiality").ToList();
        if (conf.Count == 0) sb.AppendLine("The contract states no confidentiality requirement for this dataset; none was assessed.");
        foreach (var c in conf) sb.AppendLine($"- {Cell(c.RequirementId)}: {c.Status} — {Cell(c.Reason)}");
        foreach (var r in e.Validation?.Results.Where(r => r.Kind is ParquetRuleKind.NoIdentifierValues or ParquetRuleKind.NoProtectedCanary) ?? [])
            sb.AppendLine($"- {Cell(r.RuleName)}: {r.Outcome} — {Cell(r.Actual)}. {Cell(r.Explanation)}");
        sb.AppendLine().AppendLine("## 12. Execution correlation").AppendLine();
        sb.AppendLine(e.Correlation is { } co ? $"{co.Level}{(co.RunId is { } id ? $" · run `{id}` {Cell(co.RunName)}" : "")} — {Cell(co.Explanation)}" : e.LinkedRunId is { } legacy ? $"Run `{legacy}` declared (version 1 evidence; not correlated)." : "No execution run linked.");
        sb.AppendLine().AppendLine("## 13. Limitations and risks").AppendLine();
        foreach (var l in e.Limitations.Concat(e.Comparison?.Limitations ?? [])) sb.AppendLine($"- {Cell(l)}");
        if (e.Association?.ContractVersion.Contains("draft", StringComparison.OrdinalIgnoreCase) == true) sb.AppendLine("- The contract is a draft; results describe conformance to the draft, not to an agreed delivery contract.");
        sb.AppendLine("- A synthetic or locally opened file is never a verified production delivery.");
        sb.AppendLine().AppendLine("## 14. Evidence references").AppendLine();
        sb.AppendLine($"- Parquet evidence `{e.Id}` (file SHA-256 `{e.Sha256}`)");
        foreach (var r in e.Validation?.Results ?? []) sb.AppendLine($"- {Cell(r.EvidenceRef.Length > 0 ? r.EvidenceRef : r.RuleId)}: {r.Outcome} ({r.Mode}, {r.RowsExamined:N0} rows examined)");
        if (e.Association is { } aa) sb.AppendLine($"- Source snapshot `{aa.SnapshotId}` · `{Cell(aa.DocumentPath)}`");
        return sb.ToString();
    }

    private static string Clip(string? s) => new((s ?? "").Where(c => !char.IsControl(c)).Take(200).ToArray());
}
