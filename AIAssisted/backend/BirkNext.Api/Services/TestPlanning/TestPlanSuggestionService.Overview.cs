using Microsoft.EntityFrameworkCore;
using BirkNext.TestPlanning;
using D = BirkNext.Api.Services.TestPlanning.TestScenarioDiscoveryEngine;

namespace BirkNext.Api.Services.TestPlanning;

/// <summary>
/// Testoversikt (2026-11-25): the project's saved test work and its specifications' scenarios, read-only. Scenarios come from the specification
/// evidence stored at import (every specification of the project, not only the one selected in Finn og forstå) through the same mapping discovery
/// uses (<see cref="D.FromSpecification"/>), so their identities are the ones saved cases carry. Each case is attributed by its own source; the
/// page aggregates per project, module or specification. No discovery is run, nothing is written, no revision is created.
/// </summary>
public sealed partial class TestPlanSuggestionService
{
    public const string NoModule = "Uten modultilknytning";

    /// <summary>Reviewers' scenario-type decisions per specification (normalized path → scenario source identity → newest decision); read-only,
    /// one query. Each decision belongs to ONE acceptance scenario — nothing here is a story-level type.</summary>
    internal async Task<Dictionary<string, Dictionary<string, ScenarioTypeDecision>>> TypeDecisionsAsync(string key, CancellationToken ct) =>
        (await GroupingDecisionsAsync(key, ct)).Types;

    /// <summary>Both kinds of per-scenario decisions of every grouping document of the project, from one query (read-only).</summary>
    internal async Task<(Dictionary<string, Dictionary<string, ScenarioTypeDecision>> Types, Dictionary<string, List<ScenarioPreparationDecision>> Preparation)> GroupingDecisionsAsync(string key, CancellationToken ct)
    {
        var preparation = new Dictionary<string, List<ScenarioPreparationDecision>>(StringComparer.OrdinalIgnoreCase);
        var typeDecisions = new Dictionary<string, Dictionary<string, ScenarioTypeDecision>>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in await db.StoryGroupings.AsNoTracking().Where(r => r.ProjectKey == key).ToListAsync(ct))
        {
            try
            {
                if (System.Text.Json.JsonSerializer.Deserialize<StoryGroupingDocument>(row.ContentJson, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)) is { } doc)
                {
                    typeDecisions[StoryGroupingService.NormalizePath(row.SpecificationPath)] = StoryGroupingEngine.ScenarioTypeDecisions(doc);
                    preparation[StoryGroupingService.NormalizePath(row.SpecificationPath)] = doc.Preparation ?? [];
                }
            }
            catch (System.Text.Json.JsonException) { }
        }
        return (typeDecisions, preparation);
    }

    /// <summary>The scenario's readiness from its own facts and the testers' decisions in its document (cases are evidence only, so this is the
    /// same status every tab shows).</summary>
    private static PreparationStatus Readiness(DiscoveredScenario c, Dictionary<string, List<ScenarioPreparationDecision>> preparation) =>
        ScenarioPreparationEngine.Evaluate(ScenarioPreparationEngine.FactsOf(c, []),
            ScenarioPreparationEngine.DocumentKey(c) is { } doc ? preparation.GetValueOrDefault(StoryGroupingService.NormalizePath(doc)) : null,
            c.SourceIdentity, c.Fingerprint).Status;

    /// <summary>The newest reviewer decision for this acceptance scenario, if any.</summary>
    internal static ScenarioTypeDecision? DecisionFor(Dictionary<string, Dictionary<string, ScenarioTypeDecision>> all, DiscoveredScenario c) =>
        c.Story?.SpecificationPath is { Length: > 0 } spec && c.SourceIdentity.Length > 0 && all.TryGetValue(StoryGroupingService.NormalizePath(spec), out var bySpec)
            ? bySpec.GetValueOrDefault(c.SourceIdentity) : null;

    /// <summary>Standalone documented tests (2026-11-26) of the project's latest discovery, one entry per test document (its module from the path).
    /// Read-only; nothing is discovered here. Each test is one unique scenario with its own type and its DECLARED level.</summary>
    private async Task<List<OverviewSpecification>> DocumentedTestSpecificationsAsync(string key, Dictionary<string, Dictionary<string, ScenarioTypeDecision>> typeDecisions,
        Dictionary<string, List<ScenarioPreparationDecision>> preparation, CancellationToken ct)
    {
        if (discoveries is null) return [];
        if (discoveryStore is not null)
            try { await discoveryStore.RestoreAsync(key, null, ct); } catch (Exception e) when (e is not OperationCanceledException) { }
        if (discoveries.Latest(key) is not { } latest) return [];
        return [.. latest.Candidates.Where(c => c.CapabilityId == D.TestSpecification && c.SourceIdentity.Length > 0)
            .DistinctBy(c => c.SourceIdentity, StringComparer.Ordinal)
            .GroupBy(c => c.CanonicalKey["testspec:".Length..].Split('#')[0], StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new OverviewSpecification(g.Key, "", System.IO.Path.GetFileNameWithoutExtension(g.Key), [.. g.Select(c =>
            {
                var type = ScenarioTypeClassifier.Classify(c, DecisionFor(typeDecisions, c));
                var level = TestLevelClassifier.Of(c.Type);
                return new OverviewScenario(c.SourceIdentity, null, null, null, null, null, c.Title, type.Type, [.. type.Qualities])
                    { Level = level.Level, LevelDeclared = level.Candidate, Standalone = true, Preparation = Readiness(c, preparation) };
            })]))];
    }

    public async Task<ManualTestingOverview> OverviewAsync(string projectKey, CancellationToken ct)
    {
        var key = TestScenarioDiscoveryService.Key(projectKey);
        var workspace = await ProjectWorkspaceAsync(key, null, false, ct);

        // Scenarios per specification, from the stored evidence of the project's newest specification catalog.
        var specifications = new List<OverviewSpecification>();
        var stored = specificationCatalogs is null ? null : await specificationCatalogs.EvidenceAsync(null, key, ct);
        // Reviewers' scenario-type decisions of every specification of the project (one query; 2026-11-26).
        var (typeDecisions, preparationDecisions) = await GroupingDecisionsAsync(key, ct);
        foreach (var spec in stored?.Evidence ?? [])
        {
            var decisions = typeDecisions.GetValueOrDefault(StoryGroupingService.NormalizePath(spec.Path));
            var requirementTexts = spec.Evidence.Requirements.GroupBy(r => r.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Text, StringComparer.OrdinalIgnoreCase);
            // Twins (same Given/When, different Then) are separated by the same rule as discovery before de-duplication (Data Quality finding
            // 2026-11-27: one scenario of each twin pair was missing here, and a case for a twin carries the separated identity).
            var scenarios = D.SeparateTwins(D.FromSpecification(key, spec.Evidence).Candidates
                    .Where(c => c.CapabilityId == D.Specification && c.SourceIdentity.Length > 0))
                .DistinctBy(c => c.SourceIdentity, StringComparer.Ordinal)
                .Select(c =>
                {
                    var requirementOnly = c.CanonicalKey.StartsWith("requirement:", StringComparison.Ordinal);
                    var type = requirementOnly ? null : ScenarioTypeClassifier.Classify(c, decisions?.GetValueOrDefault(c.SourceIdentity), requirementTexts);
                    return new OverviewScenario(c.SourceIdentity, c.Story?.Key, c.Story?.StoryId, c.Story?.Title, c.NumberInStory, NormalPriority(c.Story?.Priority), c.Title,
                        type?.Type ?? ScenarioRequirementType.Unclassified, type is null ? null : [.. type.Qualities], requirementOnly)
                        { Level = TestLevelClassifier.Of(c.Type).Level, Preparation = requirementOnly ? null : Readiness(c, preparationDecisions) };
                })
                .ToList();
            var folder = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(spec.Path.Replace('\\', '/')) ?? "");
            specifications.Add(new OverviewSpecification(spec.Path, "", folder.Length > 0 ? folder : spec.Path, scenarios));
        }
        specifications.AddRange(await DocumentedTestSpecificationsAsync(key, typeDecisions, preparationDecisions, ct));
        // Project-level audit (2026-11-27): the canonical module rule (ModuleStructure, the explorers' rule) over every document of the project.
        // Before, a simpler rule («second folder is specs») put a module's own documents (HendelseService/docs/…Testspesifikasjon.md, a module's
        // requirements documents) under «Prosjektnivå», together with the project's own specs/ and loose project documents.
        var roots = new HashSet<string>(ModuleStructure.RootsOf(specifications.Select(s => (string?)s.Path)
            .Concat(discoveries?.Latest(key)?.Candidates.Select(c => c.Story?.SpecificationPath) ?? [])), StringComparer.Ordinal);
        // A document's own Spec-Kit structure is module evidence too (a case's specification the catalog does not hold).
        string ModuleFor(string path) { if (ModuleStructure.StrongRoot(path) is { } own) roots.Add(own); return ModuleStructure.OverviewModule(path, roots); }
        specifications = [.. specifications.Select(s => s with { Module = ModuleFor(s.Path) })];

        // Every case of the active plans, flagged against its own source (the same per-case resolution as Planlegg og klargjør).
        var cases = new List<OverviewCase>();
        var plans = new List<OverviewPlan>();
        var revisions = new List<OverviewRevision>();
        // Scenario-level execution per plan (one query for every active plan; read-only).
        var draftIds = workspace.Drafts.Select(d => d.Id).ToList();
        var runRows = await db.ManualTestRuns.AsNoTracking().Where(r => draftIds.Contains(r.DraftId) && r.CaseId.StartsWith("occ:")).ToListAsync(ct);
        var latestByPlan = runRows
            .GroupBy(r => (r.DraftId, Key: r.CaseId[4..]))
            .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.CompletedAt ?? r.StartedAt).ThenByDescending(r => r.Id).First().Outcome);
        // Technical Testing V2 (2026-11-28): the recorded events per occurrence, to give each case its own latest result (a result for the case, or
        // for the whole occurrence). Unreadable rows are skipped.
        var eventsByOccurrence = runRows
            .Select(r => { try { return (r.DraftId, Key: r.CaseId[4..], Event: System.Text.Json.JsonSerializer.Deserialize<ScenarioExecutionEvent>(r.ContentJson, BirkNext.Api.Services.TestPlanning.TestPlanSuggestionEngine.Json)); } catch (System.Text.Json.JsonException) { return (r.DraftId, Key: r.CaseId[4..], Event: (ScenarioExecutionEvent?)null); } })
            .Where(x => x.Event is not null)
            .GroupBy(x => (x.DraftId, x.Key))
            .ToDictionary(g => g.Key, g => g.Select(x => x.Event!).OrderByDescending(x => x.At).ThenByDescending(x => x.Id).ToList());
        foreach (var summary in workspace.Drafts)
        {
            TestPlanDraft plan;
            try { plan = await DraftAsync(summary.Id, ct); }
            catch (TestPlanException) { continue; }
            var active = plan.Cases.Where(c => c.Review is not (TestCaseReviewState.Excluded or TestCaseReviewState.Superseded)).ToList();
            // DEF-V12-01 (2026-11-28): a newer source version of the application that no discovery has compared the plan with yet. Its cases are
            // then «Ikke sammenlignet» — never «i samsvar» with a version nobody compared them with (a change or removal already found is kept).
            var status = workspace.PlanStatus?.GetValueOrDefault(summary.Id) ?? await StatusAsync(plan, ct);
            var uncompared = status.NewerSources.Count > 0;
            var parents = plan.Cases.Where(c => c.Source.Kind != ScenarioSourceKind.Manual).ToDictionary(c => c.Source.ScenarioId, StringComparer.Ordinal);
            var occurrenceOf = Occurrences(plan).SelectMany(o => o.CaseIds.Select(id => (Case: id, o.Key))).GroupBy(x => x.Case, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().Key, StringComparer.Ordinal);
            ScenarioExecutionEvent? LatestFor(string caseId) => occurrenceOf.TryGetValue(caseId, out var occ) && eventsByOccurrence.TryGetValue((plan.Id, occ), out var list)
                ? list.FirstOrDefault(x => x.CaseId is null || string.Equals(x.CaseId, caseId, StringComparison.Ordinal)) : null;
            foreach (var c in active)
            {
                var own = c.Source.Kind == ScenarioSourceKind.Manual && c.ScenarioKey is { } parentKey && parents.TryGetValue(parentKey, out var parent) ? parent : c;
                // A scenario with no specification file of its own (2026-11-26): the specification folder its cited files lie in, never its title.
                var path = SpecificationPathOf(own) ?? SpecificationFolderEvidence.SpecificationOf(own.Generated.Evidence.Select(e => e.Reference));
                cases.Add(new OverviewCase
                {
                    PlanId = plan.Id, PlanName = plan.PlanName, CaseId = c.Source.ScenarioId, Title = c.EffectiveTitle,
                    SpecificationPath = path, Module = path is null ? NoModule : ModuleFor(path),
                    ScenarioIdentity = own.Generated.SourceIdentity, StoryKey = own.Generated.Story?.Key, StoryId = own.Generated.Story?.StoryId,
                    Priority = NormalPriority(c.Edits.Priority ?? own.Generated.Story?.Priority),
                    Review = c.Review, Ready = c.Testability.Ready,
                    MissingExpected = c.Missing.Any(m => m.StartsWith("Expected result: NEEDS", StringComparison.Ordinal)),
                    NoTestData = c.TestData.Count == 0, Hindrance = !string.IsNullOrWhiteSpace(c.Edits.BlockedBy), MissingSteps = c.EffectiveSteps.Count == 0,
                    SourceState = uncompared && c.Source.Kind == ScenarioSourceKind.ProjectDiscovery && c.SourceState is null or CaseSourceState.Current ? CaseSourceState.NotCompared : c.SourceState,
                    TestKind = c.Edits.TestKind,
                    Execution = LatestFor(c.Source.ScenarioId)?.Status, ExecutedAt = LatestFor(c.Source.ScenarioId)?.At,
                    Translation = ScenarioPlanning.StateOf(c), TranslationOrigin = c.Translation(ScenarioPlanning.Norwegian)?.Origin,
                });
            }
            var mine = cases.Where(x => x.PlanId == plan.Id).ToList();
            plans.Add(new OverviewPlan(plan.Id, plan.PlanName, mine.Count, mine.Count(x => x.Review == TestCaseReviewState.Accepted),
                mine.Count(x => x.Review is TestCaseReviewState.Generated or TestCaseReviewState.UnderReview or TestCaseReviewState.NeedsClarification),
                mine.Count(x => x.SourceState is CaseSourceState.Changed or CaseSourceState.Removed or CaseSourceState.SourceUnavailable),
                [.. mine.Select(x => x.Module).Distinct(StringComparer.Ordinal).Order(StringComparer.CurrentCultureIgnoreCase)], plan.UpdatedAt)
            {
                SprintLabel = plan.DeliveryLabel,
                Execution = ExecutionCounts(plan, latestByPlan),
                Suites = plan.Suites.Count,
                PlannedOnly = Occurrences(plan).Count(o => o.ScenarioOnly),
            });
            foreach (var r in (await RevisionsAsync(plan.Id, ct)).Take(5))
                revisions.Add(new OverviewRevision(plan.Id, plan.PlanName, r.Revision, r.Reason, r.SavedBy, r.SavedAt));
        }

        return new ManualTestingOverview
        {
            MethodDecisions = await MethodDecisionsAsync(key, ct),
            ProjectKey = key, ComputedAt = Now, ScenariosAvailable = specifications.Count > 0, Specifications = specifications, Cases = cases, Plans = plans,
            RecentRevisions = [.. revisions.OrderByDescending(r => r.SavedAt).Take(10)],
        };
    }

    /// <summary>
    /// Technical Testing V2 (2026-11-28): every explicit Manuell/Teknisk/Begge choice of the project's specification documents (Finn og forstå), newest
    /// per scenario, with the scenario's source identity from the project's current discovery. A choice whose candidate is not in that discovery is
    /// returned without an identity (kept and shown, never mapped by title). Read-only.
    /// </summary>
    internal async Task<List<OverviewMethodDecision>> MethodDecisionsAsync(string key, CancellationToken ct)
    {
        var identities = (discoveries?.Latest(key)?.Candidates ?? []).GroupBy(c => c.CandidateId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().SourceIdentity, StringComparer.Ordinal);
        var list = new List<OverviewMethodDecision>();
        foreach (var row in await db.StoryGroupings.AsNoTracking().Where(r => r.ProjectKey == key).ToListAsync(ct))
        {
            StoryGroupingDocument? doc;
            try { doc = System.Text.Json.JsonSerializer.Deserialize<StoryGroupingDocument>(row.ContentJson, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)); }
            catch (System.Text.Json.JsonException) { continue; }
            foreach (var d in (doc?.TestKinds ?? []).GroupBy(k => k.ScenarioId, StringComparer.Ordinal).Select(g => g.Last()).Where(d => d.Kind is not null))
                list.Add(new(row.SpecificationPath, d.ScenarioId, identities.TryGetValue(d.ScenarioId, out var id) && id.Length > 0 ? id : null, d.Kind!.Value, d.By, d.At));
        }
        return list;
    }

    private static PlanExecutionCounts ExecutionCounts(TestPlanDraft plan, IReadOnlyDictionary<(Guid, string), string> latest)
    {
        var statuses = Occurrences(plan).Select(o => latest.TryGetValue((plan.Id, o.Key), out var s) ? s : nameof(ScenarioExecutionStatus.NotRun)).ToList();
        int N(ScenarioExecutionStatus s) => statuses.Count(x => x == s.ToString());
        return new(statuses.Count, N(ScenarioExecutionStatus.NotRun), N(ScenarioExecutionStatus.Passed), N(ScenarioExecutionStatus.Failed), N(ScenarioExecutionStatus.Blocked));
    }

    /// <summary>The module a specification belongs to: «SkoleService» for «SkoleService/specs/001-…/spec.md»; «Prosjektnivå» for a specification at the root.</summary>
    internal static string ModuleOf(string specificationPath)
    {
        var parts = specificationPath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 3 && parts[1].Equals("specs", StringComparison.OrdinalIgnoreCase) ? parts[0] : "Prosjektnivå";
    }

    /// <summary>«P1», «P2», «P3» as the source states it; anything else is not a priority.</summary>
    private static string? NormalPriority(string? priority) =>
        priority?.Trim().ToUpperInvariant() is { Length: 2 } p && p[0] == 'P' && char.IsDigit(p[1]) ? p : null;
}
