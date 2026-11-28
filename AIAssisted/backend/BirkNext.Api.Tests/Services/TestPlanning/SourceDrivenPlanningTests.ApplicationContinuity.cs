using System.Text.Json;
using BirkNext.Api.Models;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Api.Services.Specifications;
using BirkNext.Api.Services.TestPlanning;
using BirkNext.Integrations;
using BirkNext.SourceEvidence;
using BirkNext.TestPlanning;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BirkNext.Api.Tests.Services.TestPlanning;

/// <summary>
/// DEF-V12-01 (2026-11-28): test plans and prepared cases follow an application through its newer imports. Found by the isolated browser journey
/// STALE-01: after a «Ny versjon» import was opened, Testoversikt and Planlegg og klargjør showed no plan and 0 cases although the older import had
/// a plan with cases. Root cause: a project (one import) learned its application only from its own latest discovery, so a newer import that nobody
/// had run «Finn og forstå» on did not know it was the same application and never read the application's plans (stored with the earlier import's
/// project key). The import records the snapshot it produced with its specification catalog, and that snapshot carries the application identity
/// chosen at import — the mapping is the import's own record, never a name, title or number.
/// </summary>
public sealed partial class SourceDrivenPlanningTests
{
    private const string AppA = "repo:def-v12-01-a";
    private const string AppB = "repo:def-v12-01-b";

    /// <summary>One import of an application: a Source Analysis snapshot with the application identity and the specification documents captured
    /// with it (as Project Import does).</summary>
    private async Task<Guid> ImportVersionAsync(string importId, string repository, char content, DateTimeOffset at, params (string Path, string Text)[] documents)
    {
        var id = Guid.NewGuid();
        await using var db = Context();
        db.IqrSourceSnapshots.Add(new IqrSourceSnapshotRecord
        {
            Id = id, EnvironmentId = "", IntegrationId = IqrSourceStore.SourceAnalysisOwner, AnalyzedAt = at,
            EvidenceJson = JsonSerializer.Serialize(new IqrSourceSnapshot
            {
                Id = id, Archive = new SourceArchive($"{importId}.zip", new string('c', 63) + content, 10, $"{importId}.zip"), AnalyzedAt = at,
                Repository = new SourceRepositoryIdentity(repository, repository, "Test"),
                Projects = [new SourceProject("App.Api", "App/src/App.Api", false, "Api", SourceConfidence.Confirmed)],
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
        });
        await db.SaveChangesAsync();
        await new SpecificationCatalogService(db).CaptureAsync(importId, $"{importId}-sha", id, documents.Select(d => new SpecificationCatalogService.Document(d.Path, d.Text)), default);
        return id;
    }

    private const string SecondSpec = "specs/002-followup/spec.md";
    private const string F = "**Given** a follow-up letter is due, **When** the day starts, **Then** the letter is sent.";

    [Fact]
    public async Task DEF_V12_01_plans_and_cases_follow_the_application_through_new_versions_and_stay_isolated_from_other_applications()
    {
        var day = DateTimeOffset.UtcNow.AddDays(-10);

        // ── V1 of application A: plan P1 with cases for A, B and C; A and B approved; one result recorded ─────────────────────────
        var s1 = await ImportVersionAsync("v1", AppA, '1', day, (SpecPath, Spec(A, B, C)));
        Guid planId; int planRevision; HashSet<string> caseIds; string aId, bId, cId;
        await using (var db = Context())
        {
            var x = Services(db);
            var d1 = await x.Discovery.DiscoverAsync(new ScenarioDiscoveryRequest { ProjectKey = "import:v1", ProjectName = "A", SnapshotId = s1, MemberSnapshotIds = [s1] }, default);
            d1.Basis!.ApplicationKey.Should().Be(AppA);
            var plan = await x.Plans.CreateProjectAsync(new CreateProjectTestPlanRequest
            {
                ProjectKey = "import:v1", ProjectName = "A", DiscoveryId = d1.DiscoveryId, CandidateIds = [.. Scenarios(d1).Select(c => c.CandidateId)], PlanName = "P1",
            }, "tester", default);
            plan.ApplicationKey.Should().Be(AppA);
            (aId, bId, cId) = (Case(plan, "valid attendance").Source.ScenarioId, Case(plan, "closed period").Source.ScenarioId, Case(plan, "duplicate").Source.ScenarioId);
            plan = await x.Plans.ReviewAsync(plan.Id, new ReviewTestCaseRequest(plan.Revision, aId, TestCaseReviewState.Accepted, null), "lead", default);
            plan = await x.Plans.ReviewAsync(plan.Id, new ReviewTestCaseRequest(plan.Revision, bId, TestCaseReviewState.Accepted, null), "lead", default);
            var occurrence = (await x.Plans.ExecutionAsync(plan.Id, default)).Occurrences.First(o => o.ScenarioId == aId);
            await x.Plans.RecordResultAsync(plan.Id, new RecordScenarioResultRequest(occurrence.Key, ScenarioExecutionStatus.Passed, "ok", null, null), "tester", default);
            (planId, planRevision) = (plan.Id, plan.Revision);
            caseIds = [.. plan.Cases.Select(c => c.Source.ScenarioId)];
            caseIds.Should().HaveCount(3);
        }

        // ── Application B: its own plan (never visible in A) ──────────────────────────────────────────────────────────────────
        var sb = await ImportVersionAsync("b1", AppB, 'b', day, (SpecPath, Spec(D)));
        Guid otherPlan;
        await using (var db = Context())
        {
            var x = Services(db);
            var db1 = await x.Discovery.DiscoverAsync(new ScenarioDiscoveryRequest { ProjectKey = "import:b1", ProjectName = "B", SnapshotId = sb, MemberSnapshotIds = [sb] }, default);
            otherPlan = (await x.Plans.CreateProjectAsync(new CreateProjectTestPlanRequest
            {
                ProjectKey = "import:b1", ProjectName = "B", DiscoveryId = db1.DiscoveryId, CandidateIds = [.. Scenarios(db1).Select(c => c.CandidateId)], PlanName = "PB",
            }, "tester", default)).Id;
        }

        // ── V2 of A, imported as a new version and made current: A unchanged, B changed (400 → 422), C removed. Nobody has discovered it yet. ──
        var s2 = await ImportVersionAsync("v2", AppA, '2', day.AddDays(1), (SpecPath, Spec(A, BChanged)));
        await using (var db = Context())
        {
            var x = Services(db);
            await x.Baselines.SelectBaselineAsync(AppA, s2, "tester", "Ny versjon ved import", default);

            var overview = await x.Plans.OverviewAsync("import:v2", default);
            overview.Plans.Should().ContainSingle("DEF-V12-01-A: the application's plan is available in its newer import").Which.Id.Should().Be(planId);
            overview.Cases.Select(c => c.CaseId).Should().BeEquivalentTo(caseIds, "DEF-V12-01-B/M: exactly the prepared cases, by identity");
            overview.Cases.Single(c => c.CaseId == aId).Review.Should().Be(TestCaseReviewState.Accepted, "DEF-V12-01-F: approval is preserved");
            overview.Cases.Single(c => c.CaseId == bId).Review.Should().Be(TestCaseReviewState.Accepted);
            overview.Plans.Single().Execution!.Passed.Should().Be(1, "DEF-V12-01-G: execution history is preserved");
            overview.Plans.Should().NotContain(p => p.Id == otherPlan, "DEF-V12-01-I: another application's plan never appears");
            // Not compared with V2 yet: the cases are never claimed to be in line with a version nobody compared them with (UNRESOLVED).
            overview.Cases.Should().OnlyContain(c => c.SourceState == CaseSourceState.NotCompared,
                "before V2 is discovered, the cases are shown as not compared with it — never «current», never «removed»");

            var workspace = await x.Plans.ProjectWorkspaceAsync("import:v2", null, true, default);
            workspace.Drafts.Should().ContainSingle(d => d.Id == planId, "Planlegg og klargjør reads the same application plans");
            workspace.PlanStatus![planId].State.Should().Be("Ny kildeversjon");

            // The tester compares (Finn og forstå on V2): the cases are classified, never rewritten.
            await x.Discovery.DiscoverAsync(new ScenarioDiscoveryRequest { ProjectKey = "import:v2", ProjectName = "A", SnapshotId = s2, MemberSnapshotIds = [s2] }, default);
            overview = await x.Plans.OverviewAsync("import:v2", default);
            overview.Cases.Single(c => c.CaseId == aId).SourceState.Should().Be(CaseSourceState.Current, "DEF-V12-01-C");
            overview.Cases.Single(c => c.CaseId == bId).SourceState.Should().Be(CaseSourceState.Changed, "DEF-V12-01-D: the changed scenario keeps its case, flagged");
            overview.Cases.Single(c => c.CaseId == bId).Review.Should().Be(TestCaseReviewState.Accepted, "a changed case is never re-approved or un-approved silently");
            overview.Cases.Single(c => c.CaseId == cId).SourceState.Should().Be(CaseSourceState.Removed, "DEF-V12-01-E: the removed scenario's case stays discoverable");
            // Coverage: the V2 scenarios A and B are the ones the cases carry (same identities).
            var v2Identities = overview.Specifications.SelectMany(s => s.Scenarios).Select(s => s.Identity).ToHashSet();
            overview.Cases.Where(c => c.CaseId != cId).Should().OnlyContain(c => v2Identities.Contains(c.ScenarioIdentity!), "DEF-V12-01-C: covered scenarios stay covered");
            overview.Cases.Single(c => c.CaseId == cId).ScenarioIdentity.Should().NotBeNull().And.Match(i => !v2Identities.Contains(i));

            // DEF-V12-01-P: the historical import still shows its plan as it was; nothing was rewritten.
            (await x.Plans.OverviewAsync("import:v1", default)).Plans.Should().ContainSingle(p => p.Id == planId);
            (await x.Plans.DraftAsync(planId, default)).Revision.Should().Be(planRevision, "a new import never rewrites a plan");
        }

        // ── V3 (B removed too) and V4 (a new specification with a new scenario): still one plan, the same three cases ───────────
        var s3 = await ImportVersionAsync("v3", AppA, '3', day.AddDays(2), (SpecPath, Spec(A)));
        var s4 = await ImportVersionAsync("v4", AppA, '4', day.AddDays(3), (SpecPath, Spec(A)), (SecondSpec, Spec(F)));
        await using (var db = Context())
        {
            var x = Services(db);
            await x.Baselines.SelectBaselineAsync(AppA, s3, "tester", "V3", default);
            await x.Discovery.DiscoverAsync(new ScenarioDiscoveryRequest { ProjectKey = "import:v3", ProjectName = "A", SnapshotId = s3, MemberSnapshotIds = [s3] }, default);
            var v3 = await x.Plans.OverviewAsync("import:v3", default);
            v3.Plans.Select(p => p.Id).Should().Equal([planId], "DEF-V12-01-H: one plan, never a copy per import");
            v3.Cases.Select(c => c.CaseId).Should().BeEquivalentTo(caseIds);
            v3.Cases.Single(c => c.CaseId == bId).SourceState.Should().Be(CaseSourceState.Removed);

            await x.Baselines.SelectBaselineAsync(AppA, s4, "tester", "V4", default);
            await x.Discovery.DiscoverAsync(new ScenarioDiscoveryRequest { ProjectKey = "import:v4", ProjectName = "A", SnapshotId = s4, MemberSnapshotIds = [s4] }, default);
        }
        // A later request (the resolver reads the baselines once per request, as the API scopes it).
        await using (var db = Context())
        {
            var x = Services(db);
            var v4 = await x.Plans.OverviewAsync("import:v4", default);
            v4.Plans.Select(p => p.Id).Should().Equal([planId], "DEF-V12-01-H");
            v4.Cases.Select(c => c.CaseId).Should().BeEquivalentTo(caseIds, "no duplicate cases after repeated imports");
            v4.Specifications.Should().Contain(s => s.Path.Contains("002-followup"), "the new specification is part of the source");
            var followUp = v4.Specifications.Single(s => s.Path.Contains("002-followup")).Scenarios.Single(s => s.IsAcceptanceScenario).Identity;
            v4.Cases.Should().NotContain(c => c.ScenarioIdentity == followUp, "a new scenario is uncovered — no case is invented for it");
            v4.Cases.Single(c => c.CaseId == aId).SourceState.Should().Be(CaseSourceState.Current, "DEF-V12-01-C over a further version");
            v4.Plans.Should().NotContain(p => p.Id == otherPlan, "DEF-V12-01-I");

            // Application B still sees only its own plan.
            (await x.Plans.OverviewAsync("import:b1", default)).Plans.Select(p => p.Id).Should().Equal([otherPlan], "DEF-V12-01-I");

            // Persisted records: one plan row for A, its result rows untouched.
            (await db.TestPlanDrafts.CountAsync(d => d.GuideKey.StartsWith("project:"))).Should().Be(2, "P1 and PB only");
            (await db.ManualTestRuns.CountAsync(r => r.DraftId == planId)).Should().Be(1);
        }
    }
}
