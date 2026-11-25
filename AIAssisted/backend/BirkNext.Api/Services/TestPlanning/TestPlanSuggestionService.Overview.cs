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

    public async Task<ManualTestingOverview> OverviewAsync(string projectKey, CancellationToken ct)
    {
        var key = TestScenarioDiscoveryService.Key(projectKey);
        var workspace = await ProjectWorkspaceAsync(key, null, false, ct);

        // Scenarios per specification, from the stored evidence of the project's newest specification catalog.
        var specifications = new List<OverviewSpecification>();
        var stored = specificationCatalogs is null ? null : await specificationCatalogs.EvidenceAsync(null, key, ct);
        foreach (var spec in stored?.Evidence ?? [])
        {
            var scenarios = D.FromSpecification(key, spec.Evidence).Candidates
                .Where(c => c.CapabilityId == D.Specification && c.SourceIdentity.Length > 0)
                .DistinctBy(c => c.SourceIdentity, StringComparer.Ordinal)
                .Select(c => new OverviewScenario(c.SourceIdentity, c.Story?.Key, c.Story?.StoryId, c.Story?.Title, c.NumberInStory, NormalPriority(c.Story?.Priority), c.Title))
                .ToList();
            var folder = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(spec.Path.Replace('\\', '/')) ?? "");
            specifications.Add(new OverviewSpecification(spec.Path, ModuleOf(spec.Path), folder.Length > 0 ? folder : spec.Path, scenarios));
        }

        // Every case of the active plans, flagged against its own source (the same per-case resolution as Planlegg og klargjør).
        var cases = new List<OverviewCase>();
        var plans = new List<OverviewPlan>();
        var revisions = new List<OverviewRevision>();
        foreach (var summary in workspace.Drafts)
        {
            TestPlanDraft plan;
            try { plan = await DraftAsync(summary.Id, ct); }
            catch (TestPlanException) { continue; }
            var active = plan.Cases.Where(c => c.Review is not (TestCaseReviewState.Excluded or TestCaseReviewState.Superseded)).ToList();
            var parents = plan.Cases.Where(c => c.Source.Kind != ScenarioSourceKind.Manual).ToDictionary(c => c.Source.ScenarioId, StringComparer.Ordinal);
            foreach (var c in active)
            {
                var own = c.Source.Kind == ScenarioSourceKind.Manual && c.ScenarioKey is { } parentKey && parents.TryGetValue(parentKey, out var parent) ? parent : c;
                var path = SpecificationPathOf(own);
                cases.Add(new OverviewCase
                {
                    PlanId = plan.Id, PlanName = plan.PlanName, CaseId = c.Source.ScenarioId, Title = c.EffectiveTitle,
                    SpecificationPath = path, Module = path is null ? NoModule : ModuleOf(path),
                    ScenarioIdentity = own.Generated.SourceIdentity, StoryKey = own.Generated.Story?.Key, StoryId = own.Generated.Story?.StoryId,
                    Priority = NormalPriority(c.Edits.Priority ?? own.Generated.Story?.Priority),
                    Review = c.Review, Ready = c.Testability.Ready,
                    MissingExpected = c.Missing.Any(m => m.StartsWith("Expected result: NEEDS", StringComparison.Ordinal)),
                    NoTestData = c.TestData.Count == 0, Hindrance = !string.IsNullOrWhiteSpace(c.Edits.BlockedBy), MissingSteps = c.EffectiveSteps.Count == 0,
                    SourceState = c.SourceState, TestKind = c.Edits.TestKind,
                    Translation = ScenarioPlanning.StateOf(c), TranslationOrigin = c.Translation(ScenarioPlanning.Norwegian)?.Origin,
                });
            }
            var mine = cases.Where(x => x.PlanId == plan.Id).ToList();
            plans.Add(new OverviewPlan(plan.Id, plan.PlanName, mine.Count, mine.Count(x => x.Review == TestCaseReviewState.Accepted),
                mine.Count(x => x.Review is TestCaseReviewState.Generated or TestCaseReviewState.UnderReview or TestCaseReviewState.NeedsClarification),
                mine.Count(x => x.SourceState is CaseSourceState.Changed or CaseSourceState.Removed or CaseSourceState.SourceUnavailable),
                [.. mine.Select(x => x.Module).Distinct(StringComparer.Ordinal).Order(StringComparer.CurrentCultureIgnoreCase)], plan.UpdatedAt));
            foreach (var r in (await RevisionsAsync(plan.Id, ct)).Take(5))
                revisions.Add(new OverviewRevision(plan.Id, plan.PlanName, r.Revision, r.Reason, r.SavedBy, r.SavedAt));
        }

        return new ManualTestingOverview
        {
            ProjectKey = key, ComputedAt = Now, ScenariosAvailable = specifications.Count > 0, Specifications = specifications, Cases = cases, Plans = plans,
            RecentRevisions = [.. revisions.OrderByDescending(r => r.SavedAt).Take(10)],
        };
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
