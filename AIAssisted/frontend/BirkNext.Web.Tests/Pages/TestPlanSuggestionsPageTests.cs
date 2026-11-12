using BirkNext.TestPlanning;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using P = BirkNext.Web.Services.TestPlanSuggestionsPresentation;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// Test Plan Suggestions page and its presentation rules: contextual selection from What to test (never an approval), filters, a save request
/// that carries only human-owned parts, honest labels (NEEDS CLARIFICATION, unreviewed guide, not verified here), the source-baseline warning,
/// read-only users, review rules in the UI, snapshots, comparison and an export that never claims Azure DevOps readiness; and the Manual Testing
/// focus facts shared by Dashboard and Recommended Workflow.
/// </summary>
public sealed class TestPlanSuggestionsPageTests : BunitContext
{
    private static ScenarioCandidate Candidate(string id, string title, string stage, string state = "Suggested", bool expected = true) => new()
    {
        Source = new ScenarioReference { Kind = ScenarioSourceKind.SystemGuide, Owner = "skolenaervaer", ScenarioId = id, Revision = 1, Fingerprint = "fp-" + id },
        Title = title, Objective = "Objective of " + title, GuideState = state, StageId = stage, StageTitle = stage == "s-inntak" ? "Skoletjenesten tar imot" : "Periodelås",
        HasExpected = expected, HasTestData = false, HasObservation = false,
        Requirements = stage == "s-inntak" ? [new TestCaseRequirement("r-ukjent-barn", "Ukjent barn", "Holdes")] : [],
    };

    private static TestPlanWorkspace Workspace(bool canWrite = true, string roleState = "NOT_IMPORTED", List<TestPlanDraftSummary>? drafts = null, List<TestPlanSnapshotSummary>? snapshots = null) => new()
    {
        Guides = [new("skolenaervaer", "Skolenærvær", "Draft only — not reviewed")], GuideKey = "skolenaervaer", CanWrite = canWrite, RuleVersion = "tps-1",
        Basis = new TestPlanSourceBasis { GuideKey = "skolenaervaer", GuideRevision = 1, GuideFingerprint = "g", GuideReviewState = "Draft only — not reviewed", Roles = [new("m2lb", "M2LB", null, null, roleState)] },
        Stages = [new("s-inntak", "Skoletjenesten tar imot"), new("s-laas", "Periodelås")],
        Candidates = [Candidate("manglende-barn", "Barnet finnes ikke", "s-inntak"), Candidate("duplikat-melding", "Duplikat melding", "s-inntak"), Candidate("periodelaas", "Periodelås", "s-laas"),
            Candidate("bufdata-fil", "Bufdata-fil", "s-laas", "NeedsClarification", expected: false)],
        Drafts = drafts ?? [], Snapshots = snapshots ?? [],
        TestData = [new("tda_1", "syntetisk-barn", null, "Scalar", "Approved", "Valid", "abcdef0123456789", null, null, null)],
    };

    private static TestCaseDraft Case(string id, string title, string? expected, ExpectedResultBasis basis = ExpectedResultBasis.UnreviewedGuide) => new()
    {
        Source = new ScenarioReference { Kind = ScenarioSourceKind.SystemGuide, Owner = "skolenaervaer", ScenarioId = id, Revision = 1, Fingerprint = "fp-" + id },
        SuiteId = "suite:s-inntak", Generated = new TestCaseContent { Title = title, Purpose = "p", ExpectedResult = expected, ExpectedBasis = basis, Steps = ["Step one"] },
        Missing = expected is null ? ["Expected result: NEEDS CLARIFICATION (none in the source)"] : [],
        Testability = new TestCaseTestability { Blockers = ["No approved Test Data linked"], Summary = "Not ready" },
    };

    private static TestPlanDraft Draft() => new()
    {
        Id = Guid.Parse("11111111-1111-1111-1111-111111111111"), GuideKey = "skolenaervaer", PlanName = "Skolenærvær – Funksjonell testing", Revision = 3, RuleVersion = "tps-1",
        Suites = [new("suite:s-inntak", "Skoletjenesten tar imot", "stage", "s-inntak")],
        Cases = [Case("manglende-barn", "Barnet finnes ikke", "Rapporten holdes"), Case("bufdata-fil", "Bufdata-fil", null, ExpectedResultBasis.NeedsClarification)],
    };

    private sealed class Api(TestPlanWorkspace workspace, TestPlanDraft? draft = null) : ITestPlanSuggestionsApiService
    {
        public CreateTestPlanDraftRequest? Created { get; private set; }
        public SaveTestPlanDraftRequest? Saved { get; private set; }
        public ReviewTestCaseRequest? Reviewed { get; private set; }
        public Task<(TestPlanWorkspace?, string?)> WorkspaceAsync(string? guideKey, CancellationToken ct = default) => Task.FromResult<(TestPlanWorkspace?, string?)>((workspace, null));
        public Task<(TestPlanWorkspace?, string?)> SummaryAsync(string? guideKey, CancellationToken ct = default) => Task.FromResult<(TestPlanWorkspace?, string?)>((workspace, null));
        public Task<(TestPlanDraft?, string?)> DraftAsync(Guid id, CancellationToken ct = default) => Task.FromResult<(TestPlanDraft?, string?)>((draft, null));
        public Task<(TestPlanDraft?, string?)> CreateAsync(CreateTestPlanDraftRequest request, CancellationToken ct = default) { Created = request; return Task.FromResult<(TestPlanDraft?, string?)>((Draft(), null)); }
        public Task<(TestPlanDraft?, string?)> SaveAsync(Guid id, SaveTestPlanDraftRequest request, CancellationToken ct = default) { Saved = request; return Task.FromResult<(TestPlanDraft?, string?)>((draft! with { Revision = draft!.Revision + 1 }, null)); }
        public Task<(TestPlanDraft?, string?)> ReviewAsync(Guid id, ReviewTestCaseRequest request, CancellationToken ct = default) { Reviewed = request; return Task.FromResult<(TestPlanDraft?, string?)>((draft, null)); }
        public Task<(TestPlanDraft?, string?)> RefreshAsync(Guid id, int baseRevision, CancellationToken ct = default) => Task.FromResult<(TestPlanDraft?, string?)>((draft, null));
        public Task<(SaveTestPlanSnapshotResult?, string?)> SaveSnapshotAsync(Guid id, SaveTestPlanSnapshotRequest request, CancellationToken ct = default) => Task.FromResult<(SaveTestPlanSnapshotResult?, string?)>((null, "n/a"));
        public Task<(TestPlanSnapshot?, string?)> SnapshotAsync(Guid id, CancellationToken ct = default) => Task.FromResult<(TestPlanSnapshot?, string?)>((null, "n/a"));
        public Task<(DeliveryComparison?, string?)> CompareAsync(Guid from, Guid to, CancellationToken ct = default) => Task.FromResult<(DeliveryComparison?, string?)>((null, "n/a"));
        public Task<(TestPlanExport?, string?)> ExportAsync(Guid snapshotId, CancellationToken ct = default) =>
            Task.FromResult<(TestPlanExport?, string?)>((new TestPlanExport("birknext.test-plan-proposal/v1", "test-plan-proposal-x-1.json", "{}", "## Suite: A", ["Bufdata-fil: Expected result: NEEDS CLARIFICATION"],
                "A reviewable proposal prepared in BirkNext. It is not an Azure DevOps Test Plan, nothing was written to Azure DevOps."), null));
    }

    private IRenderedComponent<TestPlanSuggestions> Render(Api api, string query = "")
    {
        Services.AddSingleton<ITestPlanSuggestionsApiService>(api);
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.GetRequiredService<NavigationManager>().NavigateTo("test-plan-suggestions" + query);
        var cut = Render<TestPlanSuggestions>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=tps]").GetAttribute("aria-busy").Should().Be("false"));
        return cut;
    }

    [Fact]
    public void Scenarios_selected_in_What_to_test_arrive_selected_never_approved_and_generate_a_proposal_from_exactly_those()
    {
        var api = new Api(Workspace());
        var cut = Render(api, "?guide=skolenaervaer&scenarios=manglende-barn,periodelaas,not-a-scenario");
        cut.Find("[data-testid=tps-selected-count]").TextContent.Should().Contain("2");
        cut.Find("[data-testid=tps-select-manglende-barn]").HasAttribute("checked").Should().BeTrue();
        cut.Find("[data-testid=tps-message]").TextContent.Should().Contain("Nothing is approved by selecting");
        cut.Find("[data-testid=tps-guide-basis]").TextContent.Should().Contain("Draft only — not reviewed");
        cut.Find("[data-testid=tps-generate]").Click();
        api.Created!.ScenarioIds.Should().BeEquivalentTo(["manglende-barn", "periodelaas"], "an unknown id is never sent");
        api.Created.GuideKey.Should().Be("skolenaervaer");
    }

    [Fact]
    public void Filters_narrow_candidates_by_business_area_requirement_state_and_missing_information()
    {
        var all = Workspace().Candidates;
        P.Apply(all, new P.Filter(Stage: "s-laas")).Select(c => c.Source.ScenarioId).Should().Equal("periodelaas", "bufdata-fil");
        P.Apply(all, new P.Filter(Requirement: "r-ukjent-barn")).Should().HaveCount(2);
        P.Apply(all, new P.Filter(GuideState: "NeedsClarification")).Single().Source.ScenarioId.Should().Be("bufdata-fil");
        P.Apply(all, new P.Filter(Gap: "expected")).Single().Source.ScenarioId.Should().Be("bufdata-fil");
        P.Apply(all, new P.Filter(Query: "duplikat")).Single().Title.Should().Be("Duplikat melding");
        P.ParseScenarios("a, b,,a").Should().Equal("a", "b");
        P.PrepareLink("skolenaervaer", ["a", "b"]).Should().Be("test-plan-suggestions?guide=skolenaervaer&scenarios=a%2Cb");
    }

    [Fact]
    public void A_read_only_user_sees_candidates_but_no_create_or_review_controls()
    {
        var cut = Render(new Api(Workspace(canWrite: false)));
        cut.Find("[data-testid=tps-read-only]").TextContent.Should().Contain("configuration-write permission");
        cut.FindAll("[data-testid=tps-create-bar]").Should().BeEmpty();
        cut.FindAll("[data-testid^=tps-candidate-]").Should().HaveCount(4);
    }

    [Fact]
    public void An_unselected_baseline_is_warned_about_and_never_combined()
    {
        var cut = Render(new Api(Workspace(roleState: "BASELINE_NOT_SELECTED")));
        cut.Find("[data-testid=tps-baseline-warning]").TextContent.Should().Contain("versions are never combined");
        cut.Find("[data-testid=tps-sources]").TextContent.Should().Contain("BASELINE_NOT_SELECTED");
    }

    [Fact]
    public void Review_shows_honest_expected_result_labels_blocks_accepting_without_one_and_requires_nothing_from_the_client_but_human_parts()
    {
        var draft = Draft();
        var api = new Api(Workspace(drafts: [new(draft.Id, "skolenaervaer", draft.PlanName, null, 3, 2, 0, 0, 1, 2, 2, "t", DateTimeOffset.UtcNow)]), draft);
        var cut = Render(api, $"?draft={draft.Id}&area=review");
        cut.Find("[data-testid=tps-case-bufdata-fil] .tps-case-head").Click();
        cut.Find("[data-testid=tps-case-bufdata-fil]").TextContent.Should().Contain("NEEDS CLARIFICATION");
        cut.Find("[data-testid=tps-accept-bufdata-fil]").HasAttribute("disabled").Should().BeTrue("a case without an expected result cannot be accepted");
        cut.Find("[data-testid=tps-case-manglende-barn] .tps-case-head").Click();
        cut.Find("[data-testid=tps-case-manglende-barn]").TextContent.Should().Contain("From an unreviewed guide draft");

        cut.Find("[data-testid=tps-rationale-bufdata-fil]").Input("Avklar filformat med Bufdata");
        cut.Find("[data-testid=tps-clarify-bufdata-fil]").Click();
        api.Reviewed.Should().Be(new ReviewTestCaseRequest(3, "bufdata-fil", TestCaseReviewState.NeedsClarification, "Avklar filformat med Bufdata"));

        var request = P.ToSaveRequest(draft with { Cases = [.. draft.Cases.Select(c => c.Source.ScenarioId == "manglende-barn" ? c with { Risk = TestCaseRisk.High } : c)] });
        request.BaseRevision.Should().Be(3);
        request.Cases.Single(c => c.ScenarioId == "manglende-barn").Risk.Should().Be(TestCaseRisk.High);
        typeof(TestCaseHumanPart).GetProperties().Select(p => p.Name).Should().NotContain(["Generated", "Review", "Testability", "Missing"], "generated content and review state never travel in a save");
    }

    [Fact]
    public void Test_Data_and_testability_never_claim_the_environment_and_show_what_is_missing()
    {
        var draft = Draft();
        var cut = Render(new Api(Workspace(drafts: [new(draft.Id, "skolenaervaer", draft.PlanName, null, 3, 2, 0, 0, 1, 2, 2, "t", DateTimeOffset.UtcNow)]), draft), $"?draft={draft.Id}&area=data");
        cut.Find("[data-testid=tps-no-data-manglende-barn]").TextContent.Should().Contain("None linked");
        cut.Find("[data-testid=tps-testability-manglende-barn]").TextContent.Should().Contain("Not ready");
        cut.Find("[data-testid=tps-data-manglende-barn]").TextContent.Should().Contain("Not verified here");
        cut.Find("[data-testid=tps-link-asset-manglende-barn]").Change("tda_1");
        cut.Find("[data-testid=tps-unsaved]").TextContent.Should().Contain("Unsaved");
        cut.Find("[data-testid=tps-linked-manglende-barn]").TextContent.Should().Contain("syntetisk-barn").And.Contain("Approved");
    }

    [Fact]
    public void The_export_states_the_Azure_DevOps_boundary_lists_unresolved_questions_and_never_says_ADO_ready()
    {
        var snapshot = new TestPlanSnapshotSummary(Guid.NewGuid(), Guid.NewGuid(), "skolenaervaer", 1, "Plan", "Leveranse 1", 3, new string('a', 64), "tps-1", 1, 2, "t", DateTimeOffset.UtcNow, null);
        var cut = Render(new Api(Workspace(snapshots: [snapshot])), "?area=export");
        cut.Find("[data-testid=tps-export-snapshot]").Change(snapshot.Id.ToString());
        cut.Find("[data-testid=tps-export-run]").Click();
        cut.Find("[data-testid=tps-export-boundary]").TextContent.Should().Contain("not an Azure DevOps Test Plan");
        cut.Find("[data-testid=tps-export-unresolved]").TextContent.Should().Contain("NEEDS CLARIFICATION");
        cut.Find("[data-testid=tps-export-download]").GetAttribute("download").Should().Be("test-plan-proposal-x-1.json");
        cut.Markup.Should().NotContainEquivalentOf("ADO-ready");
    }

    [Fact]
    public void Dashboard_and_Recommended_Workflow_focus_comes_only_from_persisted_proposals()
    {
        P.Focus(null).Actions.Should().BeEmpty();
        var none = P.Focus(Workspace());
        none.Actions.Single().Id.Should().Be("tps-review");
        none.Facts.Single().Count.Should().Be(4);

        var draftId = Guid.NewGuid();
        var summary = new TestPlanDraftSummary(draftId, "skolenaervaer", "Plan", null, 5, 7, 2, 1, 1, 7, 7, "t", DateTimeOffset.UtcNow);
        var snap = new TestPlanSnapshotSummary(Guid.NewGuid(), draftId, "skolenaervaer", 1, "Plan", "L1", 4, new string('a', 64), "tps-1", 1, 7, "t", DateTimeOffset.UtcNow, null);
        var focus = P.Focus(Workspace(drafts: [summary], snapshots: [snap]));
        focus.Facts.Select(f => (f.Id, f.Count)).Should().Contain([("cases", 7), ("accepted", 2), ("expected", 1), ("data", 7), ("blocked", 7)]);
        focus.Actions.Select(a => a.Id).Should().Equal("tps-review", "tps-expected", "tps-data", "tps-snapshot", "tps-export");
        focus.Actions.Single(a => a.Id == "tps-snapshot").Why.Should().Contain("Changed since snapshot 1");
        focus.Actions.Should().OnlyContain(a => a.Route.StartsWith("test-plan-suggestions?guide=skolenaervaer&draft="));
        focus.Latest!.Sequence.Should().Be(1);

        var two = P.Focus(Workspace(drafts: [summary with { Revision = 4 }], snapshots: [snap, snap with { Id = Guid.NewGuid(), Sequence = 2 }]));
        two.Actions.Select(a => a.Id).Should().Contain("tps-compare").And.NotContain("tps-snapshot");
    }

    // Found in browser acceptance: a review response that arrived after an edit replaced the working copy, silently dropping the edit.
    // Reviews now start only from a saved state, and edit controls are locked while a request is in flight.
    [Fact]
    public void An_unsaved_edit_blocks_review_decisions_so_a_late_response_can_never_drop_it()
    {
        var draft = Draft();
        var api = new Api(Workspace(drafts: [new(draft.Id, "skolenaervaer", draft.PlanName, null, 3, 2, 0, 0, 1, 2, 2, "t", DateTimeOffset.UtcNow)]), draft);
        var cut = Render(api, $"?draft={draft.Id}&area=review");
        cut.Find("[data-testid=tps-case-manglende-barn] .tps-case-head").Click();
        cut.Find("[data-testid=tps-accept-manglende-barn]").HasAttribute("disabled").Should().BeFalse();
        cut.FindAll("[data-testid=tps-case-manglende-barn] textarea")[2].Change("Rapporten holdes til barnet finnes");
        cut.Find("[data-testid=tps-review-needs-save]").TextContent.Should().Contain("Save or discard");
        cut.Find("[data-testid=tps-accept-manglende-barn]").HasAttribute("disabled").Should().BeTrue();
        cut.Find("[data-testid=tps-clarify-manglende-barn]").HasAttribute("disabled").Should().BeTrue();
        cut.Find("[data-testid=tps-save]").Click();
        api.Saved!.Cases.Single(c => c.ScenarioId == "manglende-barn").Edits.ExpectedResult.Should().Be("Rapporten holdes til barnet finnes");
    }
}
