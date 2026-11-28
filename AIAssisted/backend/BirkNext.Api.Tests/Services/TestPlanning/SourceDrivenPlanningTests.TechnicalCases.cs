using BirkNext.Api.Services.TestPlanning;
using BirkNext.TestPlanning;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BirkNext.Api.Tests.Services.TestPlanning;

/// <summary>
/// Technical Testing V2 (2026-11-28) on a fresh migrated PostgreSQL database: a technical case is an ordinary case of the shared plan whose details
/// live in its template values (same save, revision and history — T17/T18); a credential is refused; a result can be recorded for ONE case of a
/// scenario occurrence with environment, revision, expected/actual and evidence (T19); the occurrence and the plan summary still derive from the
/// occurrence's latest event (T20); and the overview gives each case its own latest result.
/// </summary>
public sealed partial class SourceDrivenPlanningTests
{
    [Fact]
    public async Task V2_technical_case_details_are_saved_as_a_revision_credentials_are_refused_and_results_are_tracked_per_case()
    {
        var s1 = await ImportVersionAsync("tc1", "repo:tc", 't', DateTimeOffset.UtcNow.AddDays(-1), (SpecPath, Spec(A, B)));
        await using var db = Context();
        var x = Services(db);
        var d1 = await x.Discovery.DiscoverAsync(new ScenarioDiscoveryRequest { ProjectKey = "import:tc1", ProjectName = "TC", SnapshotId = s1, MemberSnapshotIds = [s1] }, default);
        var plan = await x.Plans.CreateProjectAsync(new CreateProjectTestPlanRequest
        {
            ProjectKey = "import:tc1", ProjectName = "TC", DiscoveryId = d1.DiscoveryId, CandidateIds = [.. Scenarios(d1).Select(c => c.CandidateId)], PlanName = "Teknisk",
        }, "tester", default);
        var aId = Case(plan, "valid attendance").Source.ScenarioId;
        var bId = Case(plan, "closed period").Source.ScenarioId;
        var before = plan.Revision;

        // T17/T18: technical details on the existing case model → one more revision, values kept, history names the field.
        var details = new Dictionary<string, string>
        {
            ["tech.type"] = nameof(TestWorkCategory.TechnicalApi), ["tech.objective"] = "Rapporten lagres", ["tech.operation"] = "POST /api/skole/v1/skolenaervaer/reports",
            ["tech.auth"] = "Rolle: rapportinnsender (Entra-app)", ["tech.response"] = "201 Created",
        };
        plan = await x.Plans.SaveAsync(plan.Id, Human(plan, (c, part) => c.Source.ScenarioId == aId
            ? part with { Edits = part.Edits with { TestKind = ScenarioTestKind.Technical, TemplateValues = details } } : part), "tech-tester", default);
        plan.Revision.Should().Be(before + 1);
        var saved = plan.Cases.Single(c => c.Source.ScenarioId == aId).Edits;
        TechnicalCaseTemplates.Value(saved, "operation").Should().Be("POST /api/skole/v1/skolenaervaer/reports");
        TechnicalCaseTemplates.TypeOf(saved).Should().Be(TestWorkCategory.TechnicalApi);
        TechnicalCaseTemplates.Described(saved).Should().BeTrue();
        saved.TestKind.Should().Be(ScenarioTestKind.Technical);
        (await x.Plans.RevisionsAsync(plan.Id, default)).Should().Contain(r => r.Revision == plan.Revision);

        // A credential is refused (nothing saved).
        var refused = () => x.Plans.SaveAsync(plan.Id, Human(plan, (c, part) => c.Source.ScenarioId == aId
            ? part with { Edits = part.Edits with { TemplateValues = new(details) { ["tech.auth"] = "Authorization: Bearer abcdefghijklmnopqrstuvwxyz0123456789" } } } : part), "tech-tester", default);
        (await refused.Should().ThrowAsync<TestPlanException>()).Which.Code.Should().Be("secret-value");
        (await x.Plans.DraftAsync(plan.Id, default)).Revision.Should().Be(plan.Revision, "a refused save writes nothing");

        // A second, hand-written case for scenario B: one occurrence with two cases.
        plan = await x.Plans.AddManualCaseAsync(plan.Id, new AddManualTestCaseRequest(plan.Revision, "Teknisk variant av stengt periode", null, null, ScenarioKey: bId), "tech-tester", default);
        var manual = plan.Cases.Single(c => c.Source.Kind == ScenarioSourceKind.Manual).Source.ScenarioId;
        var execution = await x.Plans.ExecutionAsync(plan.Id, default);
        var occB = execution.Occurrences.Single(o => o.CaseIds.Contains(bId));
        occB.CaseIds.Should().Contain(manual);

        // T19: a result for the occurrence as a whole, then a result for ONE of its cases with technical evidence.
        execution = await x.Plans.RecordResultAsync(plan.Id, new RecordScenarioResultRequest(occB.Key, ScenarioExecutionStatus.Passed, null, null, null), "tech-tester", default);
        var latest = execution.Occurrences.Single(o => o.Key == occB.Key).Latest!.Id;
        execution = await x.Plans.RecordResultAsync(plan.Id, new RecordScenarioResultRequest(occB.Key, ScenarioExecutionStatus.Failed, "422 i stedet for 400", "BUG-7", latest,
            CaseId: manual, Environment: "QA", Expected: "400", Actual: "422", Evidence: ["correlation-id 9f1c"]), "tech-tester", default);
        var recorded = execution.Occurrences.Single(o => o.Key == occB.Key).Latest!;
        recorded.CaseId.Should().Be(manual);
        recorded.Environment.Should().Be("QA");
        recorded.PlanRevision.Should().Be(plan.Revision);
        recorded.Actual.Should().Be("422");
        recorded.Evidence.Should().Equal("correlation-id 9f1c");

        // A case of another scenario, and a credential as evidence, are refused.
        var wrongCase = () => x.Plans.RecordResultAsync(plan.Id, new RecordScenarioResultRequest(occB.Key, ScenarioExecutionStatus.Passed, null, null, recorded.Id, CaseId: aId), "t", default);
        (await wrongCase.Should().ThrowAsync<TestPlanException>()).Which.Code.Should().Be("case-unknown");
        var secret = () => x.Plans.RecordResultAsync(plan.Id, new RecordScenarioResultRequest(occB.Key, ScenarioExecutionStatus.Passed, null, null, recorded.Id,
            Evidence: ["token=abcdefghijklmnopqrstu"]), "t", default);
        (await secret.Should().ThrowAsync<TestPlanException>()).Which.Code.Should().Be("secret-value");

        // T20 + overview: the occurrence is Failed (its latest event); each case has its own latest result; nothing run → no result.
        var overview = await x.Plans.OverviewAsync("import:tc1", default);
        overview.Plans.Single().Execution!.Failed.Should().Be(1, "the plan summary derives from the occurrences, as before");
        overview.Plans.Single().Execution!.NotRun.Should().Be(1, "scenario A was never run");
        overview.Cases.Single(c => c.CaseId == manual).Execution.Should().Be(ScenarioExecutionStatus.Failed);
        overview.Cases.Single(c => c.CaseId == bId).Execution.Should().Be(ScenarioExecutionStatus.Passed, "the case-scoped failure is the other case's");
        overview.Cases.Single(c => c.CaseId == aId).Execution.Should().BeNull("never inferred: A was not run");
        overview.Cases.Single(c => c.CaseId == aId).TestKind.Should().Be(ScenarioTestKind.Technical);
        (await db.ManualTestRuns.CountAsync(r => r.DraftId == plan.Id)).Should().Be(2, "two results recorded, two refused");
    }
}
