using System.Text.Json;
using System.Text.RegularExpressions;
using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.Api.Services.SourceAnalysis;
using BirkNext.Integrations;
using BirkNext.SourceImpact;
using BirkNext.GeneratedDocumentation;
using BirkNext.SecurityExpectations;
using BirkNext.SourceArchitecture;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services;

/// <summary>Runs requirement and source-change analysis into one persisted, snapshot-bound report contract.</summary>
public sealed class ImpactAnalysisRunService(AppDbContext db, SourceChangeImpactService sourceImpact, IReviewSourceEvidenceProvider sourceEvidence)
{
    public async Task<IReadOnlyList<ImpactRequirementOption>> RequirementsAsync(string projectId, CancellationToken ct)
    {
        var rows = await db.Scenarios.AsNoTracking()
            .Where(s => s.ProjectId == projectId && s.Kind == ScenarioKind.Requirement)
            .OrderBy(s => s.DisplayOrder).ThenBy(s => s.Title)
            .Select(s => new ImpactRequirementOption(s.Id, s.Title, s.Description)).ToListAsync(ct);
        return rows;
    }

    public async Task<ImpactAnalysisRunReport> RunAsync(ImpactAnalysisRunRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ProjectId)) throw new ArgumentException("A current project or workspace identity is required.");
        if (request.ProjectImportId is { Length: > 0 } importId && request.ProjectId != $"import:{importId}")
            throw new ArgumentException("The project identity does not match the selected Project Import.");
        var requirementIds = request.RequirementIds.Where(x => x != Guid.Empty).Distinct().ToList();
        var hasBaseline = request.BaselineSnapshotId is { } baseline && baseline != Guid.Empty;
        var hasCurrent = request.CurrentSnapshotId is { } current && current != Guid.Empty;
        if (hasBaseline != hasCurrent) throw new ArgumentException("Choose both baseline and current snapshots, or neither.");
        if (hasBaseline && request.BaselineSnapshotId == request.CurrentSnapshotId)
            throw new ArgumentException("Baseline and current snapshots must be different.");
        if (!hasBaseline && requirementIds.Count == 0)
            throw new ArgumentException("Select a requirement or a source snapshot comparison.");

        SourceChangeImpactReport? source = null;
        IqrSourceSnapshot? currentSourceEvidence = null;
        if (hasBaseline)
        {
            source = await sourceImpact.AnalyzeAsync(new SourceChangeImpactRequest(string.Empty, request.ProjectId,
                request.BaselineSnapshotId!.Value, request.CurrentSnapshotId!.Value, request.ProjectImportId,
                request.ProjectDisplayName, Math.Clamp(request.MaxImpactDepth, 0, 3)), ct, persist: false);
            if (source is null) throw new InvalidOperationException("The selected snapshots are unavailable, do not belong to the same repository, or do not match this Project Import.");
            source = Sanitize(source);
            currentSourceEvidence = await sourceEvidence.ResolveAsync(string.Empty, source.TargetSnapshotId, ct);
            if (currentSourceEvidence is null || currentSourceEvidence.Id != source.TargetSnapshotId ||
                !string.Equals(currentSourceEvidence.Archive.Sha256, source.TargetFingerprint, StringComparison.OrdinalIgnoreCase) ||
                (request.ProjectImportId is { Length: > 0 } boundImport && currentSourceEvidence.ProjectImport?.ImportId != boundImport))
                throw new InvalidOperationException("The current source evidence no longer matches the selected snapshot and imported project identity.");
        }

        var findings = new Dictionary<string, ImpactAnalysisFinding>(StringComparer.OrdinalIgnoreCase);
        var limitations = new List<string>();
        if (source is not null) AddSourceFindings(source, findings);
        if (source is not null && currentSourceEvidence is not null)
            AddSnapshotEvidenceFindings(currentSourceEvidence, source, findings);
        var (selectedRequirements, linkedRequirementTests) = await AddRequirementFindingsAsync(request.ProjectId, requirementIds, source?.TargetSnapshotId, findings, ct);
        if (requirementIds.Count > 0 && selectedRequirements < requirementIds.Count)
            limitations.Add($"{requirementIds.Count - selectedRequirements} selected requirement(s) were not found in the current project traceability records. No relationships were inferred for them.");

        var assessments = BuildAssessments(source, currentSourceEvidence, requirementIds.Count > 0, selectedRequirements, requirementIds.Count, linkedRequirementTests);
        if (source is not null) limitations.AddRange(source.Limitations.Concat(source.CoverageGaps));
        if (requirementIds.Count > 0 && selectedRequirements == 0)
            limitations.Add("Requirement analysis was not evaluated because this project has no matching persisted requirement records.");

        var runId = Guid.NewGuid();
        var createdAt = DateTimeOffset.UtcNow;
        var report = new ImpactAnalysisRunReport(runId, createdAt, request.ProjectId, request.ProjectImportId,
            Safe(request.ProjectDisplayName ?? source?.ProjectDisplayName ?? source?.TargetRepository ?? request.ProjectId),
            new ImpactAnalysisChangeSet(source is null ? "RequirementSelection" : requirementIds.Count == 0 ? "SourceSnapshotComparison" : "RequirementAndSourceSnapshotComparison",
                requirementIds, source?.BaselineSnapshotId, source?.TargetSnapshotId, source?.BaselineFingerprint, source?.TargetFingerprint),
            findings.Values.Select(Sanitize).OrderBy(f => f.Kind).ThenBy(f => f.DisplayName, StringComparer.OrdinalIgnoreCase).ToList(),
            assessments.Select(a => a with { Domain = Safe(a.Domain), Reason = Safe(a.Reason) }).ToList(), source, limitations.Select(Safe).Distinct(StringComparer.Ordinal).ToList());

        db.ImpactAnalysisRuns.Add(new ImpactAnalysisRunRecord
        {
            Id = report.RunId,
            ProjectId = report.ProjectId,
            ProjectImportId = report.ProjectImportId,
            ProjectDisplayName = report.ProjectDisplayName,
            BaselineSnapshotId = report.ChangeSet.BaselineSnapshotId ?? Guid.Empty,
            CurrentSnapshotId = report.ChangeSet.CurrentSnapshotId ?? Guid.Empty,
            BaselineFingerprint = report.ChangeSet.BaselineFingerprint ?? string.Empty,
            CurrentFingerprint = report.ChangeSet.CurrentFingerprint ?? string.Empty,
            CreatedAt = report.CreatedAt,
            ResultJson = JsonSerializer.Serialize(report)
        });
        await db.SaveChangesAsync(ct);
        return report;
    }

    public async Task<IReadOnlyList<ImpactAnalysisRunHistoryItem>> HistoryAsync(string projectId, string? projectImportId, CancellationToken ct)
    {
        var query = db.ImpactAnalysisRuns.AsNoTracking().Where(r => r.ProjectId == projectId);
        query = string.IsNullOrEmpty(projectImportId) ? query.Where(r => r.ProjectImportId == null) : query.Where(r => r.ProjectImportId == projectImportId);
        var records = await query.OrderByDescending(r => r.CreatedAt).Take(100).ToListAsync(ct);
        return records.Select(r => ToUnified(r.ResultJson, r)).Select(r => new ImpactAnalysisRunHistoryItem(r.RunId,
            r.ProjectDisplayName, r.ProjectImportId, r.CreatedAt, r.Findings.Count,
            r.DomainAssessments.Count(a => a.Status == ImpactAnalysisEvidenceStatus.Evaluated),
            r.DomainAssessments.Count(a => a.Status == ImpactAnalysisEvidenceStatus.NotEvaluated),
            r.ChangeSet.BaselineSnapshotId, r.ChangeSet.CurrentSnapshotId, r.ChangeSet.CurrentFingerprint)).ToList();
    }

    public async Task<ImpactAnalysisRunReport?> HistoryItemAsync(Guid runId, CancellationToken ct)
    {
        var row = await db.ImpactAnalysisRuns.AsNoTracking().SingleOrDefaultAsync(r => r.Id == runId, ct);
        return row is null ? null : ToUnified(row.ResultJson, row);
    }

    private async Task<(int RequirementsFound, int LinkedTestsFound)> AddRequirementFindingsAsync(string projectId, IReadOnlyCollection<Guid> ids, Guid? snapshotId,
        Dictionary<string, ImpactAnalysisFinding> findings, CancellationToken ct)
    {
        if (ids.Count == 0) return (0, 0);
        var requirements = await db.Scenarios.AsNoTracking().Where(s => s.ProjectId == projectId && s.Kind == ScenarioKind.Requirement && ids.Contains(s.Id)).ToListAsync(ct);
        if (requirements.Count == 0) return (0, 0);
        var requirementIds = requirements.Select(r => r.Id).ToHashSet();
        var links = await db.TraceLinks.AsNoTracking().Where(t => t.ProjectId == projectId && t.SourceKind == TraceLinkArtifactKind.Scenario &&
            t.TargetKind == TraceLinkArtifactKind.Scenario && (requirementIds.Contains(t.SourceId) || requirementIds.Contains(t.TargetId))).ToListAsync(ct);
        var relatedIds = links.SelectMany(l => requirementIds.Contains(l.SourceId) ? new[] { l.TargetId } : new[] { l.SourceId }).Distinct().ToList();
        var relatedScenarios = await db.Scenarios.AsNoTracking().Where(s => s.ProjectId == projectId && relatedIds.Contains(s.Id)).ToListAsync(ct);
        var codeLinks = await (from l in db.CodeLinks.AsNoTracking()
                               join f in db.CodeFiles.AsNoTracking() on l.CodeFileId equals f.Id
                               where l.ProjectId == projectId && f.ProjectId == projectId && requirementIds.Contains(l.ScenarioId)
                               select new { l.ScenarioId, f.FilePath, f.FileName }).ToListAsync(ct);

        var linkedTestIds = new HashSet<Guid>();
        foreach (var requirement in requirements)
        {
            AddFinding(findings, new($"requirement:{requirement.Id}", ImpactAnalysisFindingKind.Requirement, requirement.Title,
                ImpactAnalysisClassification.SelectedForReview, "SelectedProjectRequirement", 0, "This requirement was selected for impact review; selection does not assert that it changed.",
                [new("Persisted requirement record", "The requirement exists in the current project traceability store.", SourceSnapshotId: snapshotId)]));
            foreach (var file in codeLinks.Where(x => x.ScenarioId == requirement.Id))
                AddFinding(findings, new($"source:{file.FilePath}", ImpactAnalysisFindingKind.Source, file.FileName,
                    ImpactAnalysisClassification.RelatedOnly, "ExplicitCodeTraceLink", 1,
                    $"The requirement is explicitly linked to {file.FilePath}; the link does not prove this source implements the requirement.",
                    [new("Code Traceability", "Explicit requirement-to-source link.", file.FilePath, snapshotId)]));

            foreach (var link in links.Where(l => l.SourceId == requirement.Id || l.TargetId == requirement.Id))
            {
                var otherId = link.SourceId == requirement.Id ? link.TargetId : link.SourceId;
                if (relatedScenarios.FirstOrDefault(s => s.Id == otherId) is not { } scenario) continue;
                var isTest = scenario.Kind == ScenarioKind.Test;
                if (isTest) linkedTestIds.Add(scenario.Id);
                var kind = isTest ? ImpactAnalysisFindingKind.Test : ImpactAnalysisFindingKind.Requirement;
                var relationship = link.LinkType == TraceLinkType.Covers ? "Covers" : "Related to";
                var reason = isTest && link.LinkType == TraceLinkType.Covers
                    ? "This test is linked to the selected requirement. Execution and behavioral coverage are not verified."
                    : $"A persisted {relationship} traceability link connects this item to the selected requirement.";
                AddFinding(findings, new($"{kind}:{scenario.Id}", kind, scenario.Title,
                    ImpactAnalysisClassification.RelatedOnly, "ExplicitTraceLink", 1, reason,
                    [new("Requirements Traceability", $"{relationship} link {link.Id}.", SourceSnapshotId: snapshotId)]));
            }
        }
        return (requirements.Count, linkedTestIds.Count);
    }

    private static void AddSourceFindings(SourceChangeImpactReport source, Dictionary<string, ImpactAnalysisFinding> findings)
    {
        var snapshotId = source.TargetSnapshotId;
        foreach (var change in source.Changes)
            AddFinding(findings, new($"change:{change.Id}", MapKind(change.Domain), change.Name,
                ImpactAnalysisClassification.SelectedChange, "ObservedSourceSnapshotDifference", 0,
                $"A structured source-evidence difference was observed: {change.Detail}",
                change.SourceFiles.Select(f => new ImpactAnalysisEvidence("Source snapshot", "File included in the source comparison.", f, snapshotId)).ToList()));
        foreach (var item in source.TechnicalImpacts)
            AddFinding(findings, new($"{item.EntityType}:{item.EntityId}", ImpactAnalysisFindingKind.Component, item.DisplayName,
                item.Level switch { TechnicalImpactLevel.Direct => ImpactAnalysisClassification.DirectRelation, TechnicalImpactLevel.Indirect => ImpactAnalysisClassification.IndirectRelation, TechnicalImpactLevel.NeedsReview => ImpactAnalysisClassification.NeedsReview, TechnicalImpactLevel.Potential => ImpactAnalysisClassification.PossibleRelation, _ => ImpactAnalysisClassification.RelatedOnly },
                item.EvidenceState, Math.Max(1, item.Depth), item.Reason,
                item.Path.Select(p => new ImpactAnalysisEvidence(p.Relationship, $"{p.Label}: {p.Basis}", p.Basis, snapshotId)).ToList()));
        foreach (var req in source.Requirements)
            AddFinding(findings, new($"requirement:{req.ScenarioId}", ImpactAnalysisFindingKind.Requirement, req.Title,
                ImpactAnalysisClassification.RelatedOnly, "ExplicitCodeTraceLink", 1, req.Reason,
                req.Paths.Select(p => new ImpactAnalysisEvidence("Code Traceability", "Requirement linked to an impacted source location.", p, snapshotId)).ToList()));
        foreach (var test in source.RecommendedTests)
            AddFinding(findings, new($"test:{test.TestId}", ImpactAnalysisFindingKind.Test, test.Title,
                ImpactAnalysisClassification.RelatedOnly, "ExplicitTraceabilityOrCodeLink", 1,
                $"Review this linked test; the test has not been executed and behavioral coverage is not proven. {test.Reason}",
                test.Paths.Select(p => new ImpactAnalysisEvidence("Test link", test.Reason, p, snapshotId)).ToList()));
        foreach (var task in source.Tasks)
            AddFinding(findings, new($"task:{task.ScenarioId}", ImpactAnalysisFindingKind.Task, task.Title,
                ImpactAnalysisClassification.RelatedOnly, "ExplicitCodeTraceLink", 1, task.Reason,
                task.Paths.Select(p => new ImpactAnalysisEvidence("Task traceability", "Task linked to an impacted source location.", p, snapshotId)).ToList()));
        foreach (var journey in source.Journeys)
            AddFinding(findings, new($"journey:{journey.Id}", ImpactAnalysisFindingKind.Journey, journey.DisplayName,
                ImpactAnalysisClassification.PossibleRelation, journey.EvidenceState, 1, journey.ImpactedSection,
                journey.Steps.Select(s => new ImpactAnalysisEvidence(s.Relationship, s.Label, s.SourceFiles.FirstOrDefault(), snapshotId)).ToList()));
        foreach (var review in source.SecurityAndConfigurationReviews)
        {
            var isConfiguration = review.Contains("configuration change", StringComparison.OrdinalIgnoreCase);
            AddFinding(findings, new($"review:{StableKey(review)}", isConfiguration ? ImpactAnalysisFindingKind.Configuration : ImpactAnalysisFindingKind.Security, isConfiguration ? "Configuration review" : "Security review",
                ImpactAnalysisClassification.NeedsReview, "SourceChangeSuggestion", 1, review,
                source.Changes.Where(c => isConfiguration ? c.Domain == ImpactChangeDomain.Configuration : c.Domain is ImpactChangeDomain.Configuration or ImpactChangeDomain.Architecture)
                    .SelectMany(c => c.SourceFiles.Select(path => new ImpactAnalysisEvidence(isConfiguration ? "Source configuration comparison" : "Security-related source change",
                        c.Detail, path, source.TargetSnapshotId))).ToList()));
        }
    }

    private static void AddSnapshotEvidenceFindings(IqrSourceSnapshot snapshot, SourceChangeImpactReport source,
        Dictionary<string, ImpactAnalysisFinding> findings)
    {
        var changedPaths = source.Changes.SelectMany(c => c.SourceFiles).Concat(source.TechnicalImpacts.SelectMany(i => i.SourceFiles))
            .Select(NormalizePath).Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var changedEntityIds = source.Changes.SelectMany(c => new[] { c.Id, c.EntityKey }).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Source Analysis already resolved cross-domain producer/consumer links. Reuse the resulting paths and preserve their
        // provider confidence; no separate integration parser or project-specific matching is introduced here.
        foreach (var item in source.TechnicalImpacts.Where(i => i.Path.Any(p =>
                     p.Relationship.Contains("contract", StringComparison.OrdinalIgnoreCase) ||
                     p.Relationship.Contains("messaging", StringComparison.OrdinalIgnoreCase) ||
                     p.Relationship.Contains("producer", StringComparison.OrdinalIgnoreCase) ||
                     p.Relationship.Contains("consumer", StringComparison.OrdinalIgnoreCase) ||
                     p.Relationship.Contains("infrastructure", StringComparison.OrdinalIgnoreCase))))
        {
            AddFinding(findings, new($"integration:{item.Id}", ImpactAnalysisFindingKind.Integration, item.DisplayName,
                item.Level switch { TechnicalImpactLevel.Direct => ImpactAnalysisClassification.DirectRelation, TechnicalImpactLevel.Indirect => ImpactAnalysisClassification.IndirectRelation,
                    TechnicalImpactLevel.Potential => ImpactAnalysisClassification.PossibleRelation, _ => ImpactAnalysisClassification.NeedsReview },
                item.EvidenceState, item.Depth, item.Reason,
                item.Path.Select(p => new ImpactAnalysisEvidence(p.Relationship, $"{p.Label}: {p.Basis}", p.Basis, snapshot.Id)).ToList()));
        }

        // The existing generated-documentation snapshot links documents to modules and source evidence. A changed file inside
        // the documented module is a review suggestion only; it does not establish that the generated document is stale.
        if (snapshot.GeneratedDocumentation is { } generated && generated.SourceSnapshotId == snapshot.Id &&
            string.Equals(generated.SourceFingerprint, snapshot.Archive.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            var modules = generated.Modules.GroupBy(m => m.ModuleId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            foreach (var document in generated.Documents.Where(d => d.SourceSnapshotId == snapshot.Id && d.Origin == DocumentationOrigin.Generated))
            {
                var moduleRoot = modules.GetValueOrDefault(document.ModuleId)?.RootPath;
                var relatedPaths = changedPaths.Where(path => IsWithin(path, moduleRoot)).ToList();
                var relatedByEvidence = document.RelatedSourceEvidenceIds.Any(changedEntityIds.Contains) ||
                                        document.RelatedContractEvidenceIds.Any(changedEntityIds.Contains);
                if (relatedPaths.Count == 0 && !relatedByEvidence) continue;
                var evidence = new List<ImpactAnalysisEvidence>
                {
                    new("Generated Documentation Analysis", $"Generated {document.DocumentKind} document in module {document.ModuleId}; freshness remains governed by the documentation review.", document.SafeRelativePath, snapshot.Id)
                };
                evidence.AddRange(relatedPaths.Select(path => new ImpactAnalysisEvidence("Source snapshot comparison", "Changed source file is within the documented module scope.", path, snapshot.Id)));
                AddFinding(findings, new($"documentation:{document.EvidenceId}", ImpactAnalysisFindingKind.Documentation, document.SafeRelativePath,
                    ImpactAnalysisClassification.PossibleRelation, "GeneratedDocumentationModuleScope", 1,
                    "This generated document covers a module containing changed source. Review whether it needs regeneration; no stale state is asserted.", evidence));
            }
        }

        // Security expectation evidence stores public field names and paths. Values are deliberately excluded from findings and history.
        if (snapshot.SecurityExpectationsEvidence is { } security && security.SourceSnapshotId == snapshot.Id &&
            string.Equals(security.SourceFingerprint, snapshot.Archive.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            foreach (var candidate in security.Candidates.Where(c => c.IsCurrent && c.SourceSnapshotId == snapshot.Id))
            {
                var matchingChanges = changedPaths.Where(p => string.Equals(p, NormalizePath(candidate.SourceFile), StringComparison.OrdinalIgnoreCase)).ToList();
                if (matchingChanges.Count == 0) continue;
                var state = candidate.EvidenceState.ToString();
                AddFinding(findings, new($"security:{candidate.Id}", ImpactAnalysisFindingKind.Security, $"Security configuration: {candidate.FieldType}",
                    ImpactAnalysisClassification.NeedsReview, state, 1,
                    "Changed source file also contains security/authentication expectation evidence. Review the policy and related tests; this is source evidence, not a runtime security result.",
                    matchingChanges.Select(path => new ImpactAnalysisEvidence("Security Expectations source evidence",
                        $"{candidate.FieldType} candidate at line {candidate.SourceLine}; candidate state {candidate.CandidateState}, evidence {state}.", path, snapshot.Id)).ToList()));
            }
        }

        static string NormalizePath(string? path) => (path ?? string.Empty).Replace('\\', '/').TrimStart('/');
        static bool IsWithin(string path, string? root)
        {
            var normalizedRoot = NormalizePath(root);
            return normalizedRoot.Length == 0 || path.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
                   path.StartsWith(normalizedRoot.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase);
        }
    }

    private static List<ImpactAnalysisDomainAssessment> BuildAssessments(SourceChangeImpactReport? source, IqrSourceSnapshot? snapshot,
        bool requirementsRequested, int requirementsFound, int requirementsRequestedCount, int linkedRequirementTests)
    {
        var list = new List<ImpactAnalysisDomainAssessment>
        {
            new("Requirements", !requirementsRequested ? ImpactAnalysisEvidenceStatus.NotEvaluated : requirementsFound == requirementsRequestedCount ? ImpactAnalysisEvidenceStatus.Evaluated : ImpactAnalysisEvidenceStatus.PartiallyEvaluated,
                !requirementsRequested ? "No requirement was selected." : "Persisted requirement and traceability records were checked; links do not prove execution or behavior coverage."),
            new("Source", source is null ? ImpactAnalysisEvidenceStatus.NotEvaluated : ImpactAnalysisEvidenceStatus.Evaluated,
                source is null ? "No source snapshot comparison was selected." : "The selected immutable source snapshots were compared."),
            SourceStatus(source, "Architecture", "Architecture comparison unavailable", "The source comparison includes architecture evidence; source relationships do not prove deployed topology."),
            SourceStatus(source, "Contracts", "contract comparisons unavailable", "Contract evidence is source-derived. A changed contract is not automatically a breaking change."),
            SourceStatus(source, "Data model", "Database comparison unavailable", "Source data-model evidence does not establish runtime database impact."),
            SourceStatus(source, "Configuration", "configuration comparisons unavailable", "Only source configuration evidence is assessed; runtime values and deployment behavior are not verified."),
            PartialOrNot(source, "Dependencies", "The source comparison does not establish runtime dependency usage."),
            snapshot?.Architecture is not null || snapshot?.EvidenceDomains is not null
                ? new("Integrations and cross-service contracts", ImpactAnalysisEvidenceStatus.PartiallyEvaluated, "Source Analysis architecture, contract-consumer and cross-domain relationships were reused for the exact current snapshot. Unresolved links and deployed/runtime topology are not assessed.")
                : new("Integrations and cross-service contracts", ImpactAnalysisEvidenceStatus.NotEvaluated, "The selected source snapshot has no architecture or cross-domain integration evidence."),
            source is { Journeys.Count: > 0 } ? new("Journeys", ImpactAnalysisEvidenceStatus.PartiallyEvaluated, "Paths are source-derived and bounded; business journey completeness and runtime traffic are not verified.") : new("Journeys", ImpactAnalysisEvidenceStatus.NotEvaluated, "No source journey path was established; this does not prove no journey exists."),
            source is { RecommendedTests.Count: > 0 } || linkedRequirementTests > 0 ? new("Tests", ImpactAnalysisEvidenceStatus.PartiallyEvaluated, "Only explicit links are included. Tests were not executed and behavior coverage is not proven.") : new("Tests", ImpactAnalysisEvidenceStatus.NotEvaluated, "No linked tests were available; absence of links does not mean no tests are affected."),
            snapshot is { SecurityExpectationsEvidence: { } securityEvidence } && source is not null &&
                securityEvidence.SourceSnapshotId == snapshot.Id && string.Equals(securityEvidence.SourceFingerprint, snapshot.Archive.Sha256, StringComparison.OrdinalIgnoreCase)
                ? new("Security/authentication", ImpactAnalysisEvidenceStatus.PartiallyEvaluated, "Security Expectations source candidates were checked against changed source paths for this exact snapshot. Runtime policy behavior and vulnerability status are not evaluated.")
                : new("Security/authentication", ImpactAnalysisEvidenceStatus.NotEvaluated, snapshot is null ? "No source snapshot comparison was selected." : "Security Expectations source evidence is unavailable for this snapshot."),
            SourceStatus(source, "Deployment", "CI/CD", "Source CI/CD and infrastructure evidence is partial and does not verify deployment."),
            snapshot is { GeneratedDocumentation: { } documentationEvidence } && documentationEvidence.SourceSnapshotId == snapshot.Id &&
                string.Equals(documentationEvidence.SourceFingerprint, snapshot.Archive.Sha256, StringComparison.OrdinalIgnoreCase)
                ? new("Documentation", ImpactAnalysisEvidenceStatus.PartiallyEvaluated, "Generated Documentation Analysis was reused for the exact current snapshot. Module-scope matches suggest documents to review; freshness is not recalculated here and authored documentation is not evaluated.")
                : new("Documentation", ImpactAnalysisEvidenceStatus.NotEvaluated, snapshot is null ? "No source snapshot comparison was selected." : "Generated Documentation Analysis is unavailable for this snapshot.")
        };

        list[list.FindIndex(a => a.Domain == "Tests")] = source is { RecommendedTests.Count: > 0 } || linkedRequirementTests > 0
            ? new("Tests", ImpactAnalysisEvidenceStatus.PartiallyEvaluated, "Source-discovered tests and explicit traceability links are used where available. Discovery is not execution evidence, and E2E classification is only shown when a provider establishes it.")
            : new("Tests", ImpactAnalysisEvidenceStatus.NotEvaluated, "No test inventory or linked tests were available; absence of evidence does not mean no tests are affected.");
        return list;
    }

    private static string NormalizePath(string? path) => (path ?? string.Empty).Replace('\\', '/').TrimStart('/');

    private static ImpactAnalysisDomainAssessment SourceStatus(SourceChangeImpactReport? source, string domain, string unavailableToken, string assessedReason)
    {
        if (source is null) return new(domain, ImpactAnalysisEvidenceStatus.NotEvaluated, "No source snapshot comparison was selected.");
        var matching = source.Limitations.FirstOrDefault(x => x.Contains(unavailableToken, StringComparison.OrdinalIgnoreCase));
        return matching is null ? new(domain, ImpactAnalysisEvidenceStatus.PartiallyEvaluated, assessedReason)
            : new(domain, ImpactAnalysisEvidenceStatus.NotEvaluated, matching);
    }

    private static ImpactAnalysisDomainAssessment PartialOrNot(SourceChangeImpactReport? source, string domain, string reason) => source is null
        ? new(domain, ImpactAnalysisEvidenceStatus.NotEvaluated, "No source snapshot comparison was selected.")
        : new(domain, ImpactAnalysisEvidenceStatus.PartiallyEvaluated, reason);

    private static ImpactAnalysisFindingKind MapKind(ImpactChangeDomain domain) => domain switch
    {
        ImpactChangeDomain.Contracts => ImpactAnalysisFindingKind.Contract,
        ImpactChangeDomain.Database => ImpactAnalysisFindingKind.Data,
        ImpactChangeDomain.Configuration => ImpactAnalysisFindingKind.Configuration,
        ImpactChangeDomain.Dependencies => ImpactAnalysisFindingKind.Dependency,
        ImpactChangeDomain.Infrastructure or ImpactChangeDomain.CiCd => ImpactAnalysisFindingKind.Deployment,
        ImpactChangeDomain.Observability => ImpactAnalysisFindingKind.Integration,
        _ => ImpactAnalysisFindingKind.Source
    };

    private static void AddFinding(Dictionary<string, ImpactAnalysisFinding> findings, ImpactAnalysisFinding finding)
    {
        finding = finding with { SuggestedQaVerification = SuggestedVerification(finding.Kind) };
        if (!findings.TryGetValue(finding.Id, out var old)) { findings[finding.Id] = finding; return; }
        findings[finding.Id] = old with { Evidence = old.Evidence.Concat(finding.Evidence).Distinct().ToList(),
            Reason = string.Join("; ", new[] { old.Reason, finding.Reason }.Distinct(StringComparer.Ordinal)), Depth = Math.Min(old.Depth, finding.Depth) };
    }

    private static string SuggestedVerification(ImpactAnalysisFindingKind kind) => kind switch
    {
        ImpactAnalysisFindingKind.Test => "Review this linked test and run it when appropriate; discovery or traceability does not prove it ran or covers the changed behavior.",
        ImpactAnalysisFindingKind.Journey => "Review the affected journey section and select an appropriate end-to-end or manual check; no runtime journey was executed.",
        ImpactAnalysisFindingKind.Security => "Review the related authentication or security policy and its focused tests; this finding does not assert a vulnerability.",
        ImpactAnalysisFindingKind.Documentation => "Check whether this document should be regenerated or updated from the changed source; staleness is not asserted.",
        ImpactAnalysisFindingKind.Integration or ImpactAnalysisFindingKind.Contract => "Review the linked contract boundary and relevant producer/consumer or contract tests; no live integration was exercised.",
        ImpactAnalysisFindingKind.Configuration or ImpactAnalysisFindingKind.Deployment => "Review the named configuration or deployment evidence and its validation checks; no deployment was performed.",
        ImpactAnalysisFindingKind.Requirement => "Review linked implementation, tasks, and tests for this requirement; traceability does not prove implementation or test execution.",
        ImpactAnalysisFindingKind.Data => "Review the affected data model and migration or persistence tests; source evidence does not verify a live database.",
        ImpactAnalysisFindingKind.Dependency => "Review projects using this dependency and run their relevant build or tests; declared use does not prove runtime impact.",
        _ => "Review the source evidence and identify a focused verification for the changed area."
    };

    private static string StableKey(string value) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)))[..16];

    private static string Safe(string value) => Regex.Replace(value,
        @"(?i)[""']?(password|passwd|client[_-]?secret|secret|token|connection[_-]?string|accountkey|accesskey|sharedaccesskey|sharedaccesssignature|sas[_-]?token|api[_-]?key|authorization)[""']?\s*[:=]\s*(""[^""]*""|'[^']*'|[^,;\s}]+)",
        "$1=[REDACTED]");

    private static ImpactAnalysisFinding Sanitize(ImpactAnalysisFinding finding) => finding with
    {
        Id = Safe(finding.Id), DisplayName = Safe(finding.DisplayName), VerificationState = Safe(finding.VerificationState), Reason = Safe(finding.Reason),
        SuggestedQaVerification = Safe(finding.SuggestedQaVerification),
        Evidence = finding.Evidence.Select(e => e with { Kind = Safe(e.Kind), Description = Safe(e.Description), SourcePath = e.SourcePath is null ? null : Safe(e.SourcePath) }).ToList()
    };

    private static SourceChangeImpactReport Sanitize(SourceChangeImpactReport report) => report with
    {
        BaselineRepository = Safe(report.BaselineRepository), BaselineArchiveName = Safe(report.BaselineArchiveName),
        TargetRepository = Safe(report.TargetRepository), TargetArchiveName = Safe(report.TargetArchiveName),
        Changes = report.Changes.Select(c => c with { EntityType = Safe(c.EntityType), EntityKey = Safe(c.EntityKey), Name = Safe(c.Name), Detail = Safe(c.Detail), SourceFiles = c.SourceFiles.Select(Safe).ToList() }).ToList(),
        TechnicalImpacts = report.TechnicalImpacts.Select(i => i with { EntityType = Safe(i.EntityType), EntityId = Safe(i.EntityId), DisplayName = Safe(i.DisplayName), EvidenceState = Safe(i.EvidenceState), Reason = Safe(i.Reason), SourceFiles = i.SourceFiles.Select(Safe).ToList(), Path = i.Path.Select(p => p with { EntityType = Safe(p.EntityType), EntityId = Safe(p.EntityId), Label = Safe(p.Label), Relationship = Safe(p.Relationship), EvidenceState = Safe(p.EvidenceState), Basis = Safe(p.Basis) }).ToList() }).ToList(),
        Requirements = report.Requirements.Select(i => i with { Kind = Safe(i.Kind), Title = Safe(i.Title), Reason = Safe(i.Reason), Paths = i.Paths.Select(Safe).ToList() }).ToList(),
        Tasks = report.Tasks.Select(i => i with { Kind = Safe(i.Kind), Title = Safe(i.Title), Reason = Safe(i.Reason), Paths = i.Paths.Select(Safe).ToList() }).ToList(),
        RecommendedTests = report.RecommendedTests.Select(i => i with { Title = Safe(i.Title), TestKind = Safe(i.TestKind), Reason = Safe(i.Reason), Paths = i.Paths.Select(Safe).ToList() }).ToList(),
        Journeys = report.Journeys.Select(j => j with { DisplayName = Safe(j.DisplayName), ImpactedSection = Safe(j.ImpactedSection), EvidenceState = Safe(j.EvidenceState), Limitation = Safe(j.Limitation), Steps = j.Steps.Select(s => s with { EntityType = Safe(s.EntityType), EntityId = Safe(s.EntityId), Label = Safe(s.Label), Relationship = Safe(s.Relationship), EvidenceState = Safe(s.EvidenceState), SourceFiles = s.SourceFiles.Select(Safe).ToList() }).ToList() }).ToList(),
        SecurityAndConfigurationReviews = report.SecurityAndConfigurationReviews.Select(Safe).ToList(), Limitations = report.Limitations.Select(Safe).ToList(), RiskStatus = Safe(report.RiskStatus),
        ProjectId = report.ProjectId is null ? null : Safe(report.ProjectId), ProjectImportId = report.ProjectImportId is null ? null : Safe(report.ProjectImportId),
        ProjectDisplayName = report.ProjectDisplayName is null ? null : Safe(report.ProjectDisplayName)
    };

    private static ImpactAnalysisRunReport ToUnified(string json, ImpactAnalysisRunRecord row)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.TryGetProperty("ChangeSet", out _))
            return JsonSerializer.Deserialize<ImpactAnalysisRunReport>(json) ?? throw new JsonException("Invalid unified impact report.");
        var legacy = JsonSerializer.Deserialize<SourceChangeImpactReport>(json) ?? throw new JsonException("Invalid historical source-impact report.");
        var findings = new Dictionary<string, ImpactAnalysisFinding>(StringComparer.OrdinalIgnoreCase);
        AddSourceFindings(legacy, findings);
        var assessments = BuildAssessments(legacy, null, false, 0, 0, 0);
        return new ImpactAnalysisRunReport(row.Id, row.CreatedAt, row.ProjectId, row.ProjectImportId, row.ProjectDisplayName,
            new("SourceSnapshotComparison", [], legacy.BaselineSnapshotId == Guid.Empty ? null : legacy.BaselineSnapshotId,
                legacy.TargetSnapshotId == Guid.Empty ? null : legacy.TargetSnapshotId,
                string.IsNullOrEmpty(legacy.BaselineFingerprint) ? null : legacy.BaselineFingerprint,
                string.IsNullOrEmpty(legacy.TargetFingerprint) ? null : legacy.TargetFingerprint),
            findings.Values.ToList(), assessments, legacy, legacy.Limitations);
    }
}
