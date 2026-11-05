using BirkNext.Governance;
using BirkNext.Interpretation;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services.CanonicalInterpretation;

/// <summary>
/// Release readiness (release/1) for one project and, optionally, some of its modules: dimensions from the latest canonical interpretation and the
/// shared governance gate, never from a task checklist. Execution-based dimensions BirkNext does not yet read into this view say Not assessed and link
/// to the review that owns them. Nothing is computed from another module because it shares the repository.
/// </summary>
public sealed partial class CanonicalInterpretationService
{
    public async Task<ReleaseReadinessAssessment> AssessReleaseAsync(string scope, IReadOnlyCollection<string>? modules, string? name, CancellationToken ct)
    {
        scope = ValidScope(scope);
        var moduleSet = modules is { Count: > 0 } ? new HashSet<string>(modules.Where(m => !string.IsNullOrWhiteSpace(m)).Select(m => m.Trim()), StringComparer.OrdinalIgnoreCase) : null;
        bool InScope(string? module) => moduleSet is null || (module is not null && moduleSet.Contains(module));
        var gate = await GovernanceAsync(scope, GateAction.ReleaseReadiness, moduleSet, ct);
        var scopeRef = new ReleaseScopeReference(scope, string.IsNullOrWhiteSpace(name) ? (moduleSet is null ? "Whole project" : string.Join(", ", moduleSet.Order())) : name.Trim(),
            moduleSet is null ? [] : [.. moduleSet.Order(StringComparer.OrdinalIgnoreCase)], gate.InterpretationId);

        var dims = new List<ReadinessDimension>();
        if (gate.InterpretationId is not { } latest)
        {
            dims.Add(new(ReadinessDimensionKey.ScopeClarity, "Scope", DimensionApplicability.Required, GateDecision.NotAssessed,
                "Nothing is interpreted for this project yet, so the release scope is unknown.", [], "Interpret the project's Delivery and source snapshots in Relationship Review.", "correlation-review"));
            return Assessment(scopeRef, dims, gate, []);
        }

        var members = await (from m in db.SnapshotEntities.AsNoTracking()
                             join e in db.Entities.AsNoTracking() on m.EntityId equals e.Id
                             where m.SnapshotId == latest && (e.EntityType == "Feature" || e.EntityType == "Requirement" || e.EntityType == "Task")
                             select new { e.Id, e.EntityType, e.IdentityKey, e.DisplayName, e.AttributesJson }).ToListAsync(ct);
        string? ModuleOf(string identity, string json) => Attr(json, "module") ?? (identity.Contains('/') ? identity[..identity.IndexOf('/')] : null);
        var features = members.Where(e => e.EntityType == "Feature" && InScope(ModuleOf(e.IdentityKey, e.AttributesJson))).ToList();
        // Feature identities carry the normalized module ("skoleservice/…"); the declared Module entity carries its name ("SkoleService").
        var withFeatures = members.Where(e => e.EntityType == "Feature").Select(e => ModuleOf(e.IdentityKey, e.AttributesJson)).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var available = (await db.Entities.AsNoTracking().Where(e => e.Scope == scope && e.EntityType == "Module").Select(e => e.DisplayName).ToListAsync(ct))
            .Where(withFeatures.Contains).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
        var requirements = members.Where(e => e.EntityType == "Requirement" && Attr(e.AttributesJson, "module") is { } m && InScope(m)).ToList();
        var tasks = members.Where(e => e.EntityType == "Task" && InScope(Attr(e.AttributesJson, "module"))).ToList();
        var ids = requirements.Select(r => r.Id).Concat(tasks.Select(t => t.Id)).ToList();
        var links = await db.Relations.AsNoTracking()
            .Where(r => r.Scope == scope && r.Active && r.ReviewState != "Rejected" && (r.RelationType == "Verifies" || (r.RelationType == "References" && r.RuleId == CorrelationRules.TaskSymbolRule))
                && (ids.Contains(r.TargetEntityId) || ids.Contains(r.SourceEntityId)))
            .Select(r => new { r.SourceEntityId, r.TargetEntityId, r.RelationType, r.Resolution, r.ReviewState }).ToListAsync(ct);
        static bool Established(string resolution, string review) => review == "Confirmed" || resolution is "Explicit" or "Derived" or "Confirmed";
        var tested = links.Where(l => l.RelationType == "Verifies" && Established(l.Resolution, l.ReviewState)).Select(l => l.TargetEntityId).ToHashSet();
        var suggested = links.Where(l => l.RelationType == "Verifies").Select(l => l.TargetEntityId).ToHashSet();
        var implemented = links.Where(l => l.RelationType == "References" && Established(l.Resolution, l.ReviewState)).Select(l => l.SourceEntityId).ToHashSet();
        var findings = gate.Findings.Select(f => f.Finding).ToList();
        int Count(params GovernancePolicy[] policies) => findings.Where(f => policies.Any(p => p.Id == f.PolicyId)).Sum(f => f.Count);
        IReadOnlyList<string> Examples(params GovernancePolicy[] policies) =>
            [.. findings.Where(f => policies.Any(p => p.Id == f.PolicyId)).Select(f => $"{f.Title}{(f.Module is null ? "" : $" — {f.Module}")}: {string.Join(" · ", f.Evidence)}")];

        dims.Add(features.Count == 0
            ? new(ReadinessDimensionKey.ScopeClarity, "Scope", DimensionApplicability.Required, GateDecision.NotAssessed,
                moduleSet is null ? "No feature pack is interpreted for this project." : $"No feature pack of {string.Join(", ", moduleSet.Order())} is in the latest interpretation.",
                [], "Choose modules that have feature packs, or interpret their Delivery snapshot.", "correlation-review")
            : new(ReadinessDimensionKey.ScopeClarity, "Scope", DimensionApplicability.Required, GateDecision.Ready,
                $"{features.Count} feature pack{(features.Count == 1 ? "" : "s")} in scope{(moduleSet is null ? "" : $" (modules {string.Join(", ", moduleSet.Order())})")}.",
                [.. features.Select(f => f.DisplayName).Order(StringComparer.Ordinal).Take(8)], null, "delivery-reporting?tab=scope"));

        dims.Add(new(ReadinessDimensionKey.Requirements, "Requirements", DimensionApplicability.Required,
            requirements.Count > 0 ? GateDecision.Ready : GateDecision.ReadyWithLimitations,
            requirements.Count > 0 ? $"{requirements.Count} requirement{(requirements.Count == 1 ? "" : "s")} written for the scope." : "No requirements are written for this scope.",
            [], requirements.Count > 0 ? null : "Write the scope's functional requirements in its specification.", "specification-explorer"));

        var storyIssues = Count(GovernancePolicies.RemovedStory, GovernancePolicies.UnresolvedStory, GovernancePolicies.Ambiguity);
        dims.Add(new(ReadinessDimensionKey.Traceability, "Traceability", DimensionApplicability.Required,
            storyIssues == 0 ? GateDecision.Ready : GateDecision.ReadyWithLimitations,
            storyIssues == 0 ? "Every task traces to a current story, group or shared work; no reference is ambiguous."
                : $"{storyIssues} reference{(storyIssues == 1 ? "" : "s")} to fix or decide (removed or undefined stories, ambiguous ids).",
            Examples(GovernancePolicies.RemovedStory, GovernancePolicies.UnresolvedStory, GovernancePolicies.Ambiguity),
            storyIssues == 0 ? null : "Fix story references in the tasks files; decide ambiguous references in Relationship Review.", "traceability"));

        var checkedTasks = tasks.Count(t => Attr(t.AttributesJson, "completed") == "true");
        var withCode = tasks.Count(t => implemented.Contains(t.Id));
        dims.Add(new(ReadinessDimensionKey.Implementation, "Implementation evidence", DimensionApplicability.Optional,
            tasks.Count == 0 ? GateDecision.NotAssessed : withCode == tasks.Count ? GateDecision.Ready : GateDecision.ReadyWithLimitations,
            tasks.Count == 0 ? "No tasks are recorded for this scope."
                : $"{withCode} of {tasks.Count} tasks name source code that exists. {checkedTasks} task{(checkedTasks == 1 ? " is" : "s are")} checked in the tasks file — a checked task is not implementation, test or release evidence.",
            [], withCode == tasks.Count ? null : "Re-analyze the source so tasks can be linked to the types they name.", "traceability"));

        var untested = requirements.Count(r => !suggested.Contains(r.Id));
        var suggestedOnly = requirements.Count(r => suggested.Contains(r.Id) && !tested.Contains(r.Id));
        dims.Add(new(ReadinessDimensionKey.TestDesign, "Test design", DimensionApplicability.Required,
            requirements.Count == 0 ? GateDecision.NotAssessed : untested + suggestedOnly == 0 ? GateDecision.Ready : GateDecision.ReadyWithLimitations,
            requirements.Count == 0 ? "No requirements to design tests for."
                : $"{requirements.Count - untested - suggestedOnly} of {requirements.Count} requirements have a confirmed test · {suggestedOnly} only a suggested test (not counted) · {untested} none.",
            Examples(GovernancePolicies.CandidateOnly, GovernancePolicies.NoTest),
            untested + suggestedOnly == 0 ? null : "Confirm suggested test links, or write tests; Functional Quality Scope lists draft scenarios for uncovered criteria.", "delivery-reporting?tab=scope"));

        dims.Add(new(ReadinessDimensionKey.Governance, "Governance", DimensionApplicability.Required, gate.Decision,
            $"{GovernancePolicies.DecisionLabel(gate.Decision)}: {gate.Reason}",
            [.. gate.Findings.Where(f => f.Level is FindingLevel.Blocking or FindingLevel.Warning).Select(f => $"{GovernancePolicies.LevelLabel(f.Level)} · {f.Finding.Title}{(f.Finding.Module is null ? "" : $" — {f.Finding.Module}")}")],
            gate.Decision == GateDecision.Ready ? null : "Open the governance findings in Relationship Review.", "correlation-review"));

        var decisions = findings.Where(f => f.Owner == "Human review").Sum(f => f.Count);
        dims.Add(new(ReadinessDimensionKey.OpenDecisions, "Open review decisions", DimensionApplicability.Required,
            decisions == 0 ? GateDecision.Ready : GateDecision.ReadyWithLimitations,
            decisions == 0 ? "No review decision is open for this scope." : $"{decisions} decision{(decisions == 1 ? "" : "s")} waiting in the shared review queue.",
            Examples(GovernancePolicies.IdentityConflict, GovernancePolicies.StaleAssociation), decisions == 0 ? null : "Answer them in Relationship Review.", "correlation-review"));

        // Execution evidence is owned by each review; this view does not read their runs yet and never invents a result.
        foreach (var (key, title, href) in new[]
        {
            (ReadinessDimensionKey.FunctionalExecution, "Functional test execution", (string?)null),
            (ReadinessDimensionKey.ApiReview, "API review", "api-quality-review"),
            (ReadinessDimensionKey.IntegrationReview, "Integration review", "integration-quality-review"),
            (ReadinessDimensionKey.FrontendReview, "Frontend review", "frontend-quality-review"),
            (ReadinessDimensionKey.Performance, "Performance", "performance-test-review"),
            (ReadinessDimensionKey.CriticalE2E, "Critical E2E", "critical-e2e-regression"),
        })
            dims.Add(new(key, title, DimensionApplicability.Optional, GateDecision.NotAssessed,
                href is null ? "BirkNext records no functional test execution results for this scope." : $"Not read into release readiness yet: open {title} for its own readiness and results.",
                [], null, href));
        return Assessment(scopeRef, dims, gate, available);
    }

    // ---------------------------------------------------------------- frozen reports

    static readonly System.Text.Json.JsonSerializerOptions ReportJson = new(System.Text.Json.JsonSerializerDefaults.Web);

    /// <summary>
    /// Freezes the current assessment of a scope as an immutable report (exact interpretation, rule and policy versions, every dimension and its
    /// evidence). Later interpretations, decisions or policy changes never change it; a new freeze is a new report.
    /// </summary>
    public async Task<ReleaseReadinessReport> FreezeReleaseAsync(string scope, FreezeReleaseReadinessRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name)) throw new CanonicalRequestException("Name the release readiness report (for example the release or sprint).");
        var assessment = await AssessReleaseAsync(scope, request.Modules, request.Name, ct);
        var record = new Data.CanonicalInterpretation.ReleaseReadinessReportRecord
        {
            Id = Guid.NewGuid(), Scope = assessment.ScopeReference.Scope, Name = request.Name.Trim(), FrozenAt = assessment.EvaluatedAt, Decision = assessment.Decision.ToString(),
            ModulesJson = System.Text.Json.JsonSerializer.Serialize(assessment.ScopeReference.Modules, ReportJson), InterpretationId = assessment.ScopeReference.InterpretationId,
            RuleSetVersion = assessment.RuleSetVersion, PolicySetVersion = assessment.PolicySetVersion,
            AssessmentJson = System.Text.Json.JsonSerializer.Serialize(assessment, ReportJson),
        };
        db.ReleaseReports.Add(record);
        await db.SaveChangesAsync(ct);
        return new(record.Id, record.Name, record.FrozenAt, assessment, []);
    }

    public async Task<IReadOnlyList<ReleaseReadinessReportSummary>> ReleaseReportsAsync(string scope, int take, CancellationToken ct)
    {
        scope = ValidScope(scope);
        var reports = await db.ReleaseReports.AsNoTracking().Where(r => r.Scope == scope).OrderByDescending(r => r.FrozenAt).Take(Math.Clamp(take, 1, 100))
            .Select(r => new { r.Id, r.Name, r.FrozenAt, r.Decision, r.ModulesJson, r.InterpretationId }).ToListAsync(ct);
        var ids = reports.Select(r => r.Id).ToList();
        var latestSignOff = (await db.ReleaseSignOffs.AsNoTracking().Where(s => ids.Contains(s.ReportId)).Select(s => new { s.ReportId, s.Outcome, s.At }).ToListAsync(ct))
            .GroupBy(s => s.ReportId).ToDictionary(g => g.Key, g => Enum.Parse<SignOffOutcome>(g.OrderByDescending(s => s.At).First().Outcome));
        return [.. reports.Select(r => new ReleaseReadinessReportSummary(r.Id, r.Name, r.FrozenAt, Enum.Parse<GateDecision>(r.Decision),
            System.Text.Json.JsonSerializer.Deserialize<List<string>>(r.ModulesJson, ReportJson) ?? [], r.InterpretationId,
            latestSignOff.TryGetValue(r.Id, out var o) ? o : null))];
    }

    /// <summary>Exactly that report, as frozen, with its sign-offs. Null when it does not exist in this scope — never the latest instead.</summary>
    public async Task<ReleaseReadinessReport?> ReleaseReportAsync(string scope, Guid id, CancellationToken ct)
    {
        scope = ValidScope(scope);
        var r = await db.ReleaseReports.AsNoTracking().FirstOrDefaultAsync(x => x.Scope == scope && x.Id == id, ct);
        if (r is null) return null;
        var signOffs = await db.ReleaseSignOffs.AsNoTracking().Where(s => s.ReportId == id).OrderBy(s => s.At)
            .Select(s => new { s.Outcome, s.By, s.At, s.Note }).ToListAsync(ct);
        return new(r.Id, r.Name, r.FrozenAt, System.Text.Json.JsonSerializer.Deserialize<ReleaseReadinessAssessment>(r.AssessmentJson, ReportJson)!,
            [.. signOffs.Select(s => new ReleaseSignOff(Enum.Parse<SignOffOutcome>(s.Outcome), s.By, s.At, s.Note))]);
    }

    /// <summary>
    /// Records a sign-off next to a frozen report. It never changes the report's evidence or computed decision; accepting a Blocked report needs a
    /// note (the accepted risk), and so does a rejection.
    /// </summary>
    public async Task<ReleaseReadinessReport?> SignOffReleaseAsync(string scope, Guid id, ReleaseSignOffRequest request, string? authenticatedReviewer, CancellationToken ct)
    {
        scope = ValidScope(scope);
        var report = await db.ReleaseReports.AsNoTracking().FirstOrDefaultAsync(x => x.Scope == scope && x.Id == id, ct);
        if (report is null) return null;
        var note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        if (note is null && (request.Outcome != SignOffOutcome.Accepted || report.Decision != nameof(GateDecision.Ready)))
            throw new CanonicalRequestException(request.Outcome == SignOffOutcome.Rejected
                ? "Say why the release is rejected."
                : "This report is not plain Ready: record the accepted risk or limitation in a note.");
        db.ReleaseSignOffs.Add(new() { Id = Guid.NewGuid(), ReportId = id, Scope = scope, Outcome = request.Outcome.ToString(),
            By = string.IsNullOrWhiteSpace(authenticatedReviewer) ? "local user" : authenticatedReviewer, At = clock.GetUtcNow(), Note = note });
        await db.SaveChangesAsync(ct);
        return await ReleaseReportAsync(scope, id, ct);
    }

    ReleaseReadinessAssessment Assessment(ReleaseScopeReference scopeRef, List<ReadinessDimension> dims, GateResult gate, IReadOnlyList<string> available)
    {
        var (decision, reason) = ReleaseReadinessRules.Decide(dims);
        var blockers = gate.Findings.Where(f => f.Level == FindingLevel.Blocking && f.Waiver is null).Select(f => $"{f.Finding.Title}: {f.Finding.Resolution}")
            .Concat(dims.Where(d => d.Applicability == DimensionApplicability.Required && d.State == GateDecision.Blocked && d.Key != ReadinessDimensionKey.Governance).Select(d => $"{d.Title}: {d.Summary}"))
            .ToList();
        var next = dims.Where(d => d.Applicability == DimensionApplicability.Required && d.NextAction is not null && d.State != GateDecision.Ready)
            .OrderBy(d => d.State == GateDecision.Blocked ? 0 : d.State == GateDecision.NotAssessed ? 1 : 2).Select(d => $"{d.Title}: {d.NextAction}").Distinct().Take(8).ToList();
        return new(scopeRef, decision, reason, dims, blockers, next, ReleaseReadinessRules.RuleSetVersion, gate.PolicySetVersion, clock.GetUtcNow()) { AvailableModules = available };
    }
}
