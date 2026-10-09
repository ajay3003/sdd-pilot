using System.Text.Json;
using System.Text.RegularExpressions;
using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.SourceImpact;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services;

/// <summary>Runs requirement and source-change analysis into one persisted, snapshot-bound report contract.</summary>
public sealed class ImpactAnalysisRunService(AppDbContext db, SourceChangeImpactService sourceImpact)
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
        if (hasBaseline)
        {
            source = await sourceImpact.AnalyzeAsync(new SourceChangeImpactRequest(string.Empty, request.ProjectId,
                request.BaselineSnapshotId!.Value, request.CurrentSnapshotId!.Value, request.ProjectImportId,
                request.ProjectDisplayName, Math.Clamp(request.MaxImpactDepth, 0, 3)), ct, persist: false);
            if (source is null) throw new InvalidOperationException("The selected snapshots are unavailable, do not belong to the same repository, or do not match this Project Import.");
            source = Sanitize(source);
        }

        var findings = new Dictionary<string, ImpactAnalysisFinding>(StringComparer.OrdinalIgnoreCase);
        var limitations = new List<string>();
        if (source is not null) AddSourceFindings(source, findings);
        var selectedRequirements = await AddRequirementFindingsAsync(request.ProjectId, requirementIds, source?.TargetSnapshotId, findings, ct);
        if (requirementIds.Count > 0 && selectedRequirements < requirementIds.Count)
            limitations.Add($"{requirementIds.Count - selectedRequirements} selected requirement(s) were not found in the current project traceability records. No relationships were inferred for them.");

        var assessments = BuildAssessments(source, requirementIds.Count > 0, selectedRequirements, requirementIds.Count);
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

    private async Task<int> AddRequirementFindingsAsync(string projectId, IReadOnlyCollection<Guid> ids, Guid? snapshotId,
        Dictionary<string, ImpactAnalysisFinding> findings, CancellationToken ct)
    {
        if (ids.Count == 0) return 0;
        var requirements = await db.Scenarios.AsNoTracking().Where(s => s.ProjectId == projectId && s.Kind == ScenarioKind.Requirement && ids.Contains(s.Id)).ToListAsync(ct);
        if (requirements.Count == 0) return 0;
        var requirementIds = requirements.Select(r => r.Id).ToHashSet();
        var links = await db.TraceLinks.AsNoTracking().Where(t => t.ProjectId == projectId && t.SourceKind == TraceLinkArtifactKind.Scenario &&
            t.TargetKind == TraceLinkArtifactKind.Scenario && (requirementIds.Contains(t.SourceId) || requirementIds.Contains(t.TargetId))).ToListAsync(ct);
        var relatedIds = links.SelectMany(l => requirementIds.Contains(l.SourceId) ? new[] { l.TargetId } : new[] { l.SourceId }).Distinct().ToList();
        var relatedScenarios = await db.Scenarios.AsNoTracking().Where(s => s.ProjectId == projectId && relatedIds.Contains(s.Id)).ToListAsync(ct);
        var codeLinks = await (from l in db.CodeLinks.AsNoTracking()
                               join f in db.CodeFiles.AsNoTracking() on l.CodeFileId equals f.Id
                               where l.ProjectId == projectId && f.ProjectId == projectId && requirementIds.Contains(l.ScenarioId)
                               select new { l.ScenarioId, f.FilePath, f.FileName }).ToListAsync(ct);

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
        return requirements.Count;
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
                ImpactAnalysisClassification.NeedsReview, "SourceChangeSuggestion", 1, review, []));
        }
    }

    private static List<ImpactAnalysisDomainAssessment> BuildAssessments(SourceChangeImpactReport? source, bool requirementsRequested, int requirementsFound, int requirementsRequestedCount)
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
            new("Integrations", ImpactAnalysisEvidenceStatus.NotEvaluated, "Producer/consumer integration evidence is not yet connected to the unified run resolver."),
            source is { Journeys.Count: > 0 } ? new("Journeys", ImpactAnalysisEvidenceStatus.PartiallyEvaluated, "Paths are source-derived and bounded; business journey completeness and runtime traffic are not verified.") : new("Journeys", ImpactAnalysisEvidenceStatus.NotEvaluated, "No source journey path was established; this does not prove no journey exists."),
            source is { RecommendedTests.Count: > 0 } || requirementsFound > 0 ? new("Tests", ImpactAnalysisEvidenceStatus.PartiallyEvaluated, "Only explicit links are included. Tests were not executed and behavior coverage is not proven.") : new("Tests", ImpactAnalysisEvidenceStatus.NotEvaluated, "No linked tests were available; absence of links does not mean no tests are affected."),
            source is { SecurityAndConfigurationReviews.Count: > 0 } ? new("Security/authentication", ImpactAnalysisEvidenceStatus.PartiallyEvaluated, "Review suggestions are source-based; no vulnerability or runtime security outcome is asserted.") : new("Security/authentication", ImpactAnalysisEvidenceStatus.NotEvaluated, "No dedicated security relationship provider was evaluated."),
            SourceStatus(source, "Deployment", "CI/CD", "Source CI/CD and infrastructure evidence is partial and does not verify deployment."),
            new("Documentation", ImpactAnalysisEvidenceStatus.NotEvaluated, "Documentation-to-source freshness relationships are not connected to this run.")
        };
        return list;
    }

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
        if (!findings.TryGetValue(finding.Id, out var old)) { findings[finding.Id] = finding; return; }
        findings[finding.Id] = old with { Evidence = old.Evidence.Concat(finding.Evidence).Distinct().ToList(),
            Reason = string.Join("; ", new[] { old.Reason, finding.Reason }.Distinct(StringComparer.Ordinal)), Depth = Math.Min(old.Depth, finding.Depth) };
    }

    private static string StableKey(string value) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)))[..16];

    private static string Safe(string value) => Regex.Replace(value,
        @"(?i)[""']?(password|passwd|client[_-]?secret|secret|token|connection[_-]?string|accountkey|accesskey|sharedaccesskey|sharedaccesssignature|sas[_-]?token|api[_-]?key|authorization)[""']?\s*[:=]\s*(""[^""]*""|'[^']*'|[^,;\s}]+)",
        "$1=[REDACTED]");

    private static ImpactAnalysisFinding Sanitize(ImpactAnalysisFinding finding) => finding with
    {
        Id = Safe(finding.Id), DisplayName = Safe(finding.DisplayName), VerificationState = Safe(finding.VerificationState), Reason = Safe(finding.Reason),
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
        var assessments = BuildAssessments(legacy, false, 0, 0);
        return new ImpactAnalysisRunReport(row.Id, row.CreatedAt, row.ProjectId, row.ProjectImportId, row.ProjectDisplayName,
            new("SourceSnapshotComparison", [], legacy.BaselineSnapshotId == Guid.Empty ? null : legacy.BaselineSnapshotId,
                legacy.TargetSnapshotId == Guid.Empty ? null : legacy.TargetSnapshotId,
                string.IsNullOrEmpty(legacy.BaselineFingerprint) ? null : legacy.BaselineFingerprint,
                string.IsNullOrEmpty(legacy.TargetFingerprint) ? null : legacy.TargetFingerprint),
            findings.Values.ToList(), assessments, legacy, legacy.Limitations);
    }
}
