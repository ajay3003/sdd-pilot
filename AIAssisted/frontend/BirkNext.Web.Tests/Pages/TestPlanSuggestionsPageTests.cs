using BirkNext.TestPlanning;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;
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
        public RequirementCoverageView? Coverage { get; init; }
        public Task<(RequirementCoverageView?, string?)> RequirementCoverageAsync(string scope, CancellationToken ct = default, bool rows = true) =>
            Task.FromResult<(RequirementCoverageView?, string?)>((Coverage, Coverage is null ? "n/a" : null));
        public Task<(TestPlanWorkspace?, string?)> WorkspaceAsync(string? guideKey, CancellationToken ct = default) => Task.FromResult<(TestPlanWorkspace?, string?)>((workspace, null));
        public Task<(TestPlanWorkspace?, string?)> SummaryAsync(string? guideKey, CancellationToken ct = default) => Task.FromResult<(TestPlanWorkspace?, string?)>((workspace, null));
        public Task<(TestPlanDraft?, string?)> DraftAsync(Guid id, CancellationToken ct = default) => Task.FromResult<(TestPlanDraft?, string?)>((draft, null));
        public Task<(TestPlanDraft?, string?)> CreateAsync(CreateTestPlanDraftRequest request, CancellationToken ct = default) { Created = request; return Task.FromResult<(TestPlanDraft?, string?)>((Draft(), null)); }
        /// <summary>When set, a save waits for it (a slow server), so a test can edit while the save is on its way.</summary>
        public TaskCompletionSource? HoldSave { get; set; }
        public List<SaveTestPlanDraftRequest> Saves { get; } = [];
        public async Task<(TestPlanDraft?, string?)> SaveAsync(Guid id, SaveTestPlanDraftRequest request, CancellationToken ct = default)
        {
            Saved = request; Saves.Add(request);
            if (HoldSave is { } hold) { await hold.Task; HoldSave = null; }
            return (draft! with { Revision = request.BaseRevision + 1 }, null);
        }
        public Task<(TestPlanDraft?, string?)> ReviewAsync(Guid id, ReviewTestCaseRequest request, CancellationToken ct = default) { Reviewed = request; return Task.FromResult<(TestPlanDraft?, string?)>((draft, null)); }
        public Task<(TestPlanDraft?, string?)> RefreshAsync(Guid id, int baseRevision, CancellationToken ct = default) => Task.FromResult<(TestPlanDraft?, string?)>((draft, null));
        public Task<(SaveTestPlanSnapshotResult?, string?)> SaveSnapshotAsync(Guid id, SaveTestPlanSnapshotRequest request, CancellationToken ct = default) => Task.FromResult<(SaveTestPlanSnapshotResult?, string?)>((null, "n/a"));
        public Task<(TestPlanSnapshot?, string?)> SnapshotAsync(Guid id, CancellationToken ct = default) => Task.FromResult<(TestPlanSnapshot?, string?)>((null, "n/a"));
        public Task<(DeliveryComparison?, string?)> CompareAsync(Guid from, Guid to, CancellationToken ct = default) => Task.FromResult<(DeliveryComparison?, string?)>((null, "n/a"));
        public Task<(TestPlanExport?, string?)> ExportAsync(Guid snapshotId, CancellationToken ct = default) =>
            Task.FromResult<(TestPlanExport?, string?)>((new TestPlanExport("birknext.test-plan-proposal/v1", "test-plan-proposal-x-1.json", "{}", "## Suite: A", ["Bufdata-fil: Expected result: NEEDS CLARIFICATION"],
                "A reviewable proposal prepared in BirkNext. It is not an Azure DevOps Test Plan, nothing was written to Azure DevOps."), null));

        // Project mode (universal discovery).
        public TestPlanWorkspace? Project { get; init; }
        public ScenarioDiscoveryResult? Discovery { get; init; }
        public ScenarioDiscoveryResult? Last { get; init; }
        public ScenarioDiscoveryRequest? Discovered { get; private set; }
        public CreateProjectTestPlanRequest? CreatedProject { get; private set; }
        public List<string> ProjectReads { get; } = [];
        public Task<(TestPlanWorkspace?, string?)> ProjectAsync(string projectKey, string? environmentId, CancellationToken ct = default)
        { ProjectReads.Add(projectKey); return Task.FromResult<(TestPlanWorkspace?, string?)>((Project, Project is null ? "n/a" : null)); }
        public Task<(ScenarioDiscoveryResult?, string?)> DiscoverAsync(ScenarioDiscoveryRequest request, CancellationToken ct = default)
        { Discovered = request; return Task.FromResult<(ScenarioDiscoveryResult?, string?)>((Discovery, Discovery is null ? "n/a" : null)); }
        public Task<(ScenarioDiscoveryResult?, string?)> LastDiscoveryAsync(string projectKey, Guid? snapshotId, CancellationToken ct = default) =>
            Task.FromResult<(ScenarioDiscoveryResult?, string?)>((Last, null));
        public Task<(TestPlanDraft?, string?)> CreateProjectAsync(CreateProjectTestPlanRequest request, CancellationToken ct = default)
        { CreatedProject = request; return Task.FromResult<(TestPlanDraft?, string?)>((ProjectDraft(request.ProjectKey, request.ManualCaseTitle), null)); }
    }

    // ── Project mode fixtures: an unrelated project (a library app) with no System Understanding guide ─────────────────────

    private static DiscoveredScenario Discovered(string id, string title, ScenarioCandidateType type, string area, string source, string? expected = "Shown as documented", string project = "library") => new()
    {
        CandidateId = id, ProjectKey = project, CapabilityId = source == "API contract" ? "api-contract" : source == "UI source" ? "ui-source" : "specification", SourceCategory = source, Area = area,
        Title = title, Purpose = "Purpose of " + title, Type = type, Confidence = expected is null ? CandidateConfidence.Insufficient : CandidateConfidence.ExplicitSpecification,
        ExpectedBehaviour = expected, Evidence = [new("specification", "spec.md#" + id)], CanonicalKey = "k:" + id, Engine = "test", RuleVersion = "tsd-1",
    };

    private static ScenarioDiscoveryResult Discovery(string project = "library", params DiscoveredScenario[] candidates) => new()
    {
        DiscoveryId = "disc-1", ProjectKey = project, ProjectName = "Library", RuleVersion = "tsd-1", DiscoveredAt = DateTimeOffset.UtcNow,
        Capabilities =
        [
            new() { Id = "specification", Label = "Business scenarios from the specification", State = DiscoveryCapabilityState.Available, Candidates = 2, Detail = "2 acceptance scenarios" },
            new() { Id = "system-guide", Label = "System Understanding guide scenarios", State = DiscoveryCapabilityState.NotApplicable, Detail = "No System Understanding guide describes this project's source." },
            new() { Id = "api-contract", Label = "API / contract candidates", State = DiscoveryCapabilityState.Available, Candidates = 1, Detail = "1 operations in 1 contract(s)." },
        ],
        Candidates = [.. candidates],
    };

    private static readonly DiscoveredScenario[] LibraryCandidates =
    [
        Discovered("tsc-borrow", "Borrow a book", ScenarioCandidateType.BusinessFunctional, "Loans", "Specification"),
        Discovered("tsc-return", "Return a book", ScenarioCandidateType.BusinessFunctional, "Loans", "Specification", expected: null),
        Discovered("tsc-getbook", "GET /books/{id}", ScenarioCandidateType.ApiContract, "Library API", "API contract"),
        Discovered("tsc-search", "Search the catalogue", ScenarioCandidateType.UiFunctional, "Catalogue", "UI source"),
    ];

    private static TestPlanDraft ProjectDraft(string project, string? manualTitle = null)
    {
        var manual = manualTitle is null ? null : new TestCaseDraft
        {
            Source = new ScenarioReference { Kind = ScenarioSourceKind.Manual, Owner = project, ScenarioId = "manual-1" }, SuiteId = "suite:area:written-by-hand",
            Generated = new TestCaseContent { Title = manualTitle, Purpose = "", GroupTitle = "Written by hand", ExpectedBasis = ExpectedResultBasis.Missing },
        };
        var discovered = new TestCaseDraft
        {
            Source = new ScenarioReference { Kind = ScenarioSourceKind.ProjectDiscovery, Owner = project, ScenarioId = "tsc-borrow", Fingerprint = "f" }, SuiteId = "suite:area:loans",
            Generated = new TestCaseContent { Title = "Borrow a book", Purpose = "Borrow", Steps = ["Choose a book", "Confirm the loan"], StepExpected = ["The book is shown", null],
                ExpectedResult = "The loan is registered", ExpectedBasis = ExpectedResultBasis.Specification, Requirements = [new("FR-001", "FR-001", null)], CandidateType = ScenarioCandidateType.BusinessFunctional },
            Testability = new TestCaseTestability { Readiness = CaseReadiness.NotAssessed, ReadinessReason = "Whether Test Data is needed is not assessed yet (no input is described)." },
        };
        return new TestPlanDraft
        {
            Id = Guid.Parse("22222222-2222-2222-2222-222222222222"), GuideKey = "project:" + project, ProjectKey = project, ProjectName = "Library", PlanName = "Library – test plan", Revision = 1,
            Suites = [new("suite:area:loans", "Loans", "area", null), .. manual is null ? Array.Empty<TestSuiteDraft>() : [new TestSuiteDraft("suite:area:written-by-hand", "Written by hand", "area", null)]],
            Cases = manual is null ? [discovered] : [manual],
        };
    }

    private static TestPlanWorkspace ProjectWorkspace(string project = "library", List<TestPlanDraftSummary>? drafts = null) => new()
    {
        ProjectKey = project, GuideKey = "project:" + project, CanWrite = true, RuleVersion = "tps-1", Drafts = drafts ?? [],
    };

    private void SelectProject(string slug = "library", string name = "Library")
    {
        Services.AddSingleton(BirkNext.Web.Tests.Services.WorkspaceSnapshots.Projection(BirkNext.Web.Tests.Services.WorkspaceSnapshots.Loaded("Imported", slug, name)).Object);
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
    public void What_reaches_Azure_DevOps_is_shown_and_BirkNext_only_detail_is_collapsed()
    {
        // 2026-11-19: tests are completed in Azure DevOps; only what the import file carries stays in view.
        var draft = Draft();
        var api = new Api(Workspace(drafts: [new(draft.Id, "skolenaervaer", draft.PlanName, null, 3, 2, 0, 0, 1, 2, 2, "t", DateTimeOffset.UtcNow)]), draft);
        var cut = Render(api, $"?draft={draft.Id}&area=review");
        cut.Find("[data-testid=tps-case-manglende-barn] .tps-case-head").Click();
        var editor = cut.Find("[data-testid=tps-case-manglende-barn]");
        var more = cut.Find("[data-testid=tps-more-fields-manglende-barn]");
        more.HasAttribute("open").Should().BeFalse("BirkNext-only fields start collapsed");
        more.TextContent.Should().Contain("not exported to Azure DevOps").And.Contain("Purpose").And.Contain("Expected result of the whole case")
            .And.Contain("Risk").And.Contain("Test type").And.Contain("Execution method").And.Contain("Suite");
        editor.QuerySelectorAll("[data-testid=tps-step-new-manglende-barn]").Should().ContainSingle("steps stay in view");
        more.QuerySelectorAll("[data-testid=tps-steps-manglende-barn]").Should().BeEmpty("steps are not under More fields");
        editor.TextContent.Should().NotContain("Start review");
        cut.FindAll("[data-testid=tps-advanced-suite]").Should().ContainSingle().Which.HasAttribute("open").Should().BeFalse();
        cut.Find("[data-testid=tps-areas-advanced]").TextContent.Should().Contain("Saved snapshots").And.Contain("Compare deliveries");
        cut.Find("[data-testid=tps-area-export]").Closest("[data-testid=tps-areas-advanced]").Should().BeNull("the export stays in view");

        cut.Find("[data-testid=tps-area-save]").Click();
        cut.Find("[data-testid=tps-go-export]").TextContent.Should().Be("Export to Azure DevOps");
        cut.Find("[data-testid=tps-snapshot-advanced]").HasAttribute("open").Should().BeFalse();
        cut.Find("[data-testid=tps-snapshot-advanced]").QuerySelectorAll("[data-testid=tps-delivery]").Should().ContainSingle("the delivery label belongs to snapshots");
        cut.Find("[data-testid=tps-plan-name-bar]").QuerySelectorAll("[data-testid=tps-rename-plan]").Should().ContainSingle("the plan is renamed without a snapshot");
    }

    [Fact]
    public void Discover_now_in_the_address_runs_discovery_once_and_is_removed_from_the_address()
    {
        SelectProject();
        var api = new Api(Workspace()) { Project = ProjectWorkspace(), Discovery = Discovery("library", LibraryCandidates) };
        var cut = Render(api, "?area=choose&discover=now");
        cut.WaitForAssertion(() => api.Discovered.Should().NotBeNull("the tester asked to discover the newly chosen specification"));
        api.Discovered!.ProjectKey.Should().Be("library");
        Services.GetRequiredService<NavigationManager>().Uri.Should().NotContain("discover=now", "a reload does not run it again");
        cut.WaitForAssertion(() => cut.Find("[data-testid=tps-message]").TextContent.Should().Contain("candidates found"));
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
        cut.Find("[data-testid=tps-prepare]").Click();
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
        var cut = Render(new Api(Workspace(canWrite: false)), "?guide=skolenaervaer");
        cut.Find("[data-testid=tps-read-only]").TextContent.Should().Contain("configuration-write permission");
        cut.FindAll("[data-testid=tps-prepare-bar]").Should().BeEmpty();
        cut.FindAll("[data-testid=tps-manual-bar]").Should().BeEmpty();
        cut.FindAll("li[data-testid^=tps-candidate-]").Should().HaveCount(4);
    }

    [Fact]
    public void An_unselected_baseline_is_warned_about_and_never_combined()
    {
        var cut = Render(new Api(Workspace(roleState: "BASELINE_NOT_SELECTED")), "?guide=skolenaervaer");
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
        // One readiness state per case; "no Test Data described" is never shown as "not required".
        cut.Find("[data-testid=tps-testability-manglende-barn]").TextContent.Should().Be("Not assessed");
        cut.Find("[data-testid=tps-data-need-manglende-barn] option[selected]").TextContent.Should().Be("Not assessed yet", "only a person decides a case needs no Test Data");
        cut.Find("[data-testid=tps-data-manglende-barn]").TextContent.Should().Contain("Not verified here");
        cut.Find("[data-testid=tps-link-asset-manglende-barn]").Change("tda_1");
        cut.Find("[data-testid=tps-unsaved]").TextContent.Should().Contain("Unsaved");
        cut.Find("[data-testid=tps-linked-manglende-barn]").TextContent.Should().Contain("syntetisk-barn").And.Contain("Approved");
    }

    [Fact]
    public void The_export_states_the_Azure_DevOps_boundary_lists_unresolved_questions_and_never_says_ADO_ready()
    {
        var snapshot = new TestPlanSnapshotSummary(Guid.NewGuid(), Guid.NewGuid(), "skolenaervaer", 1, "Plan", "Leveranse 1", 3, new string('a', 64), "tps-1", 1, 2, "t", DateTimeOffset.UtcNow, null);
        var cut = Render(new Api(Workspace(snapshots: [snapshot])), "?guide=skolenaervaer&area=export");
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
    public void Discover_groups_candidates_by_the_guides_own_stages_and_keeps_hidden_selections_visible()
    {
        var api = new Api(Workspace());
        var cut = Render(api, "?guide=skolenaervaer&scenarios=manglende-barn,periodelaas");
        // Grouped by the guide's stages (the real area metadata), in the guide's order; no invented category.
        cut.FindAll("section[data-testid^=tps-group-]").Select(g => g.GetAttribute("data-testid")).Should().Equal("tps-group-skoletjenesten-tar-imot", "tps-group-periodelås");
        cut.Find("[data-testid=tps-group-skoletjenesten-tar-imot]").TextContent.Should().Contain("Skoletjenesten tar imot").And.Contain("Barnet finnes ikke").And.Contain("Duplikat melding");
        cut.Find("[data-testid=tps-group-periodelås]").TextContent.Should().Contain("Periodelås").And.Contain("Bufdata-fil");

        // Filtering away a selected scenario keeps it selected and says so.
        cut.Find("[data-testid=tps-filter-area]").Change("Skoletjenesten tar imot");
        cut.Find("[data-testid=tps-selected-count]").TextContent.Should().Contain("2");
        cut.Find("[data-testid=tps-selected-hidden]").TextContent.Should().Contain("1 hidden by filters");
        cut.FindAll("[data-testid=tps-candidate-periodelaas]").Should().BeEmpty();

        // Select visible adds only what is shown; Clear selection empties it and the primary action explains what to do.
        cut.Find("[data-testid=tps-select-shown]").TextContent.Should().Contain("Select visible (2)");
        cut.Find("[data-testid=tps-select-shown]").Click();
        cut.Find("[data-testid=tps-selected-count]").TextContent.Should().Contain("3");
        cut.Find("[data-testid=tps-clear-selection]").Click();
        cut.Find("[data-testid=tps-selected-count]").TextContent.Should().Contain("0");
        cut.FindAll("[data-testid=tps-selected-hidden]").Should().BeEmpty();
        cut.Find("[data-testid=tps-prepare]").HasAttribute("disabled").Should().BeTrue();
        cut.Find("[data-testid=tps-prepare]").TextContent.Should().Contain("Prepare test cases");
        cut.Find("[data-testid=tps-select-hint]").TextContent.Should().Contain("Select at least one scenario");
        cut.FindAll(".tps-btn:not(.tps-btn-quiet)").Count(b => b.Closest("[data-testid=tps-choose]") is not null).Should().Be(1, "one primary action when choosing scenarios");
        api.Created.Should().BeNull("selecting and filtering create nothing");
    }

    [Fact]
    public void Every_preparation_step_says_what_it_is_for_in_order()
    {
        // Four guided steps; snapshots, comparison and the Azure DevOps preparation stay as secondary areas.
        P.Areas.Select(a => a.Id).Should().Equal("choose", "prepare", "testability", "save");
        P.MoreAreas.Select(a => a.Id).Should().Equal("snapshots", "compare", "export");
        P.Areas.Concat(P.MoreAreas).Should().OnlyContain(a => a.Purpose.Length > 10, "each step explains itself");
        P.StepLine("choose").Should().Be("Step 1 of 4 · Find what to test in this project's evidence and choose the scenarios to prepare.");
        P.StepLine("testability").Should().StartWith("Step 3 of 4 · See which cases still need Test Data");
        P.StepLine("export").Should().Contain("nothing is written to Azure DevOps");
        P.StepLine("unknown").Should().BeEmpty();
        // Earlier links keep working: their area maps to the step that now holds it.
        P.AreaOrDefault("discover").Should().Be("choose");
        P.AreaOrDefault("review").Should().Be("prepare");
        P.AreaOrDefault("data").Should().Be("testability");
        P.AreaOrDefault("compare").Should().Be("compare");
        var cut = Render(new Api(Workspace()), "?guide=skolenaervaer&area=discover");
        cut.Find("[data-testid=tps-area-purpose]").TextContent.Should().Be(P.StepLine("choose"));
    }

    [Fact]
    public void Discover_groups_candidates_without_a_stage_under_Other()
    {
        var groups = P.ByStage([
            new ScenarioCandidate { Source = new ScenarioReference { ScenarioId = "a" }, Title = "A", StageId = "s1" },
            new ScenarioCandidate { Source = new ScenarioReference { ScenarioId = "b" }, Title = "B", StageId = null },
            new ScenarioCandidate { Source = new ScenarioReference { ScenarioId = "c" }, Title = "C", StageId = "unknown-stage" }],
            [new("s0", "Empty stage"), new("s1", "Stage one")]);
        groups.Select(g => (g.Id, g.Items.Count)).Should().Equal(("s1", 1), ("other", 2));
    }

    [Fact]
    public void MT7_a_case_shows_verified_and_suggested_specification_requirements_apart_and_accepting_it_approves_no_association()
    {
        static RequirementScenarioAssociation A(string req, AssociationTrust trust) => new()
        {
            RelationKey = "k-" + req, GuideKey = "skolenaervaer", ScenarioId = "manglende-barn", ScenarioTitle = "Barnet finnes ikke", RequirementKey = $"Requirement|skole/001-skoletjenesten/{req}",
            RequirementId = req, Trust = trust, RuleId = trust == AssociationTrust.SuggestedAssociation ? "CR-S-VIA-TEST" : "CR-S-EXPLICIT", Reviewer = trust == AssociationTrust.ReviewedCanonicalAssociation ? "reviewer@bufdir" : null,
        };
        var coverage = new RequirementCoverageView { Scope = "dev", Known = true, Associations = [A("FR-006", AssociationTrust.ReviewedCanonicalAssociation), A("FR-040", AssociationTrust.SuggestedAssociation), A("FR-041", AssociationTrust.Rejected)] };
        var context = new Moq.Mock<IFrontendAnalysisContextFactory>();
        context.Setup(c => c.GetActiveContextAsync()).ReturnsAsync(new BirkNext.Web.Models.FrontendAnalysisContext
            { ActiveProfile = new BirkNext.Web.Models.FrontendAnalysisProfile { Id = "dev", Name = "Dev" } });
        Services.AddSingleton(context.Object);
        var draft = Draft();
        var api = new Api(Workspace(drafts: [new(draft.Id, "skolenaervaer", draft.PlanName, null, 3, 2, 0, 1, 1, 2, 2, "u", DateTimeOffset.UtcNow)]), draft) { Coverage = coverage };
        var cut = Render(api, $"?draft={draft.Id}&area=review");
        cut.Find("[data-testid=tps-case-manglende-barn] .tps-case-head").Click();
        var spec = cut.Find("[data-testid=tps-case-manglende-barn] [data-testid=tps-spec-requirements]");
        spec.QuerySelector("[data-testid=tps-spec-verified]")!.TextContent.Should().Contain("FR-006").And.Contain("Confirmed in Relationship Review").And.NotContain("FR-040");
        spec.QuerySelector("[data-testid=tps-spec-suggested]")!.TextContent.Should().Contain("FR-040").And.NotContain("FR-006");
        spec.QuerySelector("[data-testid=tps-spec-decide]")!.GetAttribute("href").Should().Be("correlation-review?tab=review");
        spec.QuerySelector("[data-testid=tps-spec-rejected]")!.TextContent.Should().Contain("FR-041");
        spec.TextContent.Should().Contain("does not approve any requirement association");
        cut.Find("[data-testid=tps-case-manglende-barn]").TextContent.Should().Contain("Business rules (guide)", "guide rule ids are never called specification requirements");

        cut.Find("[data-testid=tps-accept-manglende-barn]").Click();
        api.Reviewed!.State.Should().Be(TestCaseReviewState.Accepted);
        // Accepting goes only to Test Plan Suggestions; there is no call that could change an association (the API has none).

        // The other case has no association at all.
        cut.Find("[data-testid=tps-case-bufdata-fil] .tps-case-head").Click();
        cut.Find("[data-testid=tps-case-bufdata-fil] [data-testid=tps-spec-none]").TextContent.Should().Contain("No association");
    }

    [Fact]
    public void MT7_without_a_selected_project_requirement_associations_are_unknown_not_absent()
    {
        var draft = Draft();
        var cut = Render(new Api(Workspace(drafts: [new(draft.Id, "skolenaervaer", draft.PlanName, null, 3, 2, 0, 1, 1, 2, 2, "u", DateTimeOffset.UtcNow)]), draft), $"?draft={draft.Id}&area=review");
        cut.Find("[data-testid=tps-case-manglende-barn] .tps-case-head").Click();
        cut.Find("[data-testid=tps-case-manglende-barn] [data-testid=tps-spec-unknown]").TextContent.Should().StartWith("Unknown");
    }

    [Fact]
    public void An_unsaved_edit_blocks_review_decisions_so_a_late_response_can_never_drop_it()
    {
        var draft = Draft();
        var api = new Api(Workspace(drafts: [new(draft.Id, "skolenaervaer", draft.PlanName, null, 3, 2, 0, 0, 1, 2, 2, "t", DateTimeOffset.UtcNow)]), draft);
        var cut = Render(api, $"?draft={draft.Id}&area=review");
        cut.Find("[data-testid=tps-case-manglende-barn] .tps-case-head").Click();
        // Approve also needs an expected result per step (the export's rule); the review decisions as such are open before the edit.
        cut.Find("[data-testid=tps-clarify-manglende-barn]").HasAttribute("disabled").Should().BeFalse();
        cut.FindAll("[data-testid=tps-case-manglende-barn] textarea")[2].Change("Rapporten holdes til barnet finnes");
        cut.Find("[data-testid=tps-review-needs-save]").TextContent.Should().Contain("Save or discard");
        cut.Find("[data-testid=tps-accept-manglende-barn]").HasAttribute("disabled").Should().BeTrue();
        cut.Find("[data-testid=tps-clarify-manglende-barn]").HasAttribute("disabled").Should().BeTrue();
        cut.Find("[data-testid=tps-save]").Click();
        api.Saved!.Cases.Single(c => c.ScenarioId == "manglende-barn").Edits.ExpectedResult.Should().Be("Rapporten holdes til barnet finnes");
    }

    [Fact]
    public void Approve_follows_the_export_rules_and_says_what_is_missing()
    {
        // M2LB end-to-end (2026-11-19): cases were approved with steps lacking an expected result and then refused by the Azure DevOps export.
        var draft = Draft();
        var api = new Api(Workspace(drafts: [new(draft.Id, "skolenaervaer", draft.PlanName, null, 3, 2, 0, 0, 1, 2, 2, "t", DateTimeOffset.UtcNow)]), draft);
        var cut = Render(api, $"?draft={draft.Id}&area=review");
        var c = draft.Cases.Single(x => x.Source.ScenarioId == "manglende-barn");
        cut.Find("[data-testid=tps-case-manglende-barn] .tps-case-head").Click();
        var blocker = P.ApproveBlocker(c);
        cut.Find("[data-testid=tps-accept-manglende-barn]").HasAttribute("disabled").Should().Be(blocker is not null);
        if (blocker is not null)
            cut.Find("[data-testid=tps-approve-why-manglende-barn]").TextContent.Should().Be($"To approve: {blocker}");
        P.ApproveBlocker(c with { Edits = c.Edits with { ExpectedResult = "Rapporten holdes", Steps = ["Send rapporten"], StepExpected = ["Rapporten er mottatt"] } }).Should().BeNull();
        P.ApproveBlocker(c with { Edits = c.Edits with { ExpectedResult = "Rapporten holdes", Steps = ["Send rapporten", "Åpne loggen"], StepExpected = ["Rapporten er mottatt", null] } })
            .Should().Be("step 2 needs an expected result.");
        P.ApproveBlocker(c with { Edits = c.Edits with { ExpectedResult = "Rapporten holdes", Steps = [], StepExpected = [] } }).Should().Be("add at least one step with its expected result.");
        // Only what the Azure DevOps file holds is required: the case-level expected result is not part of an Azure DevOps test case.
        var noCaseExpected = c with { Generated = c.Generated with { ExpectedResult = null }, Edits = c.Edits with { ExpectedResult = null, Steps = ["Send rapporten"], StepExpected = ["Rapporten er mottatt"] } };
        string.IsNullOrWhiteSpace(noCaseExpected.EffectiveExpected).Should().BeTrue();
        P.ApproveBlocker(noCaseExpected).Should().BeNull("approval needs the steps the file carries, not a case-level expected result");
        P.ApproveBlocker(c with { Edits = c.Edits with { Steps = [" ", "Åpne loggen"], StepExpected = ["x", "y"] } }).Should().Be("step 1 needs an action.");
    }

    // ── Audit 2026-11-15 (§20): unsaved edits are never marked saved early and never discarded silently ──────────────────────────────────

    [Fact]
    public void An_edit_made_while_a_save_is_on_its_way_stays_unsaved_and_is_saved_next_from_the_new_revision()
    {
        var draft = Draft();
        var api = new Api(Workspace(drafts: [new(draft.Id, "skolenaervaer", draft.PlanName, null, 3, 2, 0, 0, 1, 2, 2, "t", DateTimeOffset.UtcNow)]), draft) { HoldSave = new() };
        var cut = Render(api, $"?draft={draft.Id}&area=review");
        cut.Find("[data-testid=tps-case-manglende-barn] .tps-case-head").Click();
        cut.FindAll("[data-testid=tps-case-manglende-barn] textarea")[2].Change("First wording");
        cut.Find("[data-testid=tps-save]").Click();                                   // the save is on its way
        cut.FindAll("[data-testid=tps-case-manglende-barn] textarea")[2].Change("Newer wording");
        cut.InvokeAsync(() => api.HoldSave!.SetResult());                            // the first save completes after the newer edit

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Your newer changes are not saved yet"));
        cut.FindAll("[data-testid=tps-unsaved]").Should().NotBeEmpty("the newer edit is not marked saved");
        api.Saves.Single().Cases.Single(c => c.ScenarioId == "manglende-barn").Edits.ExpectedResult.Should().Be("First wording");

        cut.Find("[data-testid=tps-save]").Click();
        cut.WaitForAssertion(() => api.Saves.Should().HaveCount(2));
        api.Saves[1].Cases.Single(c => c.ScenarioId == "manglende-barn").Edits.ExpectedResult.Should().Be("Newer wording");
        api.Saves[1].BaseRevision.Should().Be(4, "the next save builds on the revision the first save created, so it is not refused as stale");
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=tps-unsaved]").Should().BeEmpty());
    }

    [Fact]
    public void Leaving_with_unsaved_edits_asks_first_and_staying_keeps_them()
    {
        var draft = Draft();
        var api = new Api(Workspace(drafts: [new(draft.Id, "skolenaervaer", draft.PlanName, null, 3, 2, 0, 0, 1, 2, 2, "t", DateTimeOffset.UtcNow)]), draft);
        var cut = Render(api, $"?draft={draft.Id}&area=review");
        var confirm = JSInterop.Setup<bool>("confirm", _ => true);
        confirm.SetResult(false);
        cut.Find("[data-testid=tps-case-manglende-barn] .tps-case-head").Click();
        cut.FindAll("[data-testid=tps-case-manglende-barn] textarea")[2].Change("Not saved yet");

        var nav = Services.GetRequiredService<NavigationManager>();
        cut.InvokeAsync(() => nav.NavigateTo("manual-testing"));
        cut.WaitForAssertion(() => JSInterop.Invocations["confirm"].Should().ContainSingle());
        nav.Uri.Should().Contain("test-plan-suggestions", "declining keeps the tester on the plan");
        cut.FindAll("[data-testid=tps-unsaved]").Should().NotBeEmpty();
        api.Saved.Should().BeNull("nothing was saved or discarded behind the tester's back");
    }

    [Fact]
    public void Switching_areas_of_the_same_plan_does_not_ask()
    {
        var draft = Draft();
        var api = new Api(Workspace(drafts: [new(draft.Id, "skolenaervaer", draft.PlanName, null, 3, 2, 0, 0, 1, 2, 2, "t", DateTimeOffset.UtcNow)]), draft);
        var cut = Render(api, $"?draft={draft.Id}&area=review");
        JSInterop.Setup<bool>("confirm", _ => true).SetResult(false);
        cut.Find("[data-testid=tps-case-manglende-barn] .tps-case-head").Click();
        cut.FindAll("[data-testid=tps-case-manglende-barn] textarea")[2].Change("Not saved yet");
        var nav = Services.GetRequiredService<NavigationManager>();
        cut.InvokeAsync(() => nav.NavigateTo($"test-plan-suggestions?draft={draft.Id}&area=prepare"));
        cut.WaitForAssertion(() => nav.Uri.Should().Contain("area=prepare"));
        JSInterop.Invocations["confirm"].Should().BeEmpty("the edits stay with the same plan");
    }

    [Fact]
    public void A_manual_tester_can_classify_a_case_and_describe_a_performance_test_without_a_script()
    {
        var draft = Draft();
        var api = new Api(Workspace(drafts: [new(draft.Id, "skolenaervaer", draft.PlanName, null, 3, 2, 0, 0, 1, 2, 2, "t", DateTimeOffset.UtcNow)]), draft);
        var cut = Render(api, $"?draft={draft.Id}&area=review");
        cut.Find("[data-testid=tps-case-bufdata-fil] .tps-case-head").Click();

        cut.FindAll("[data-testid=tps-perf-bufdata-fil]").Should().BeEmpty("performance fields appear only for a performance case");
        cut.Find("[data-testid=tps-type-bufdata-fil]").Change(nameof(TestCaseType.Performance));
        cut.Find("[data-testid=tps-perf-bufdata-fil]");
        cut.Find("[data-testid=tps-perf-criteria-missing-bufdata-fil]").TextContent.Should().Be("Acceptance criteria not defined — review required.");
        cut.Find("[data-testid=tps-method-bufdata-fil] option[selected]").TextContent.Should().Be("Not decided", "the type never implies how it is executed");

        cut.Find("[data-testid=tps-perf-workload-bufdata-fil]").Change("All schools report on the last day of the period");
        cut.Find("[data-testid=tps-perf-subtype-bufdata-fil]").Change(nameof(PerformanceTestSubtype.Load));
        cut.Find("[data-testid=tps-perf-criteria-missing-bufdata-fil]");
        cut.Markup.Should().NotContain("k6", "no script is asked for");
    }

    // Project Test Setup was removed (2026-11-19): no test area is needed or offered; an old ?scope= link narrows nothing.
    [Fact]
    public void Without_any_test_area_every_candidate_is_listed_and_an_old_scope_link_narrows_nothing()
    {
        Services.AddSingleton(BirkNext.Web.Tests.Services.WorkspaceSnapshots.Projection(BirkNext.Web.Tests.Services.WorkspaceSnapshots.Loaded("Imported", "import:m2lb", "M2LB")).Object);
        var cut = Render(new Api(Workspace()), $"?guide=skolenaervaer&scope={Guid.NewGuid()}");
        cut.FindAll("input[type=checkbox][data-testid^=tps-select-]").Should().HaveCount(4, "the guide's four scenarios, with nothing to configure first");
        cut.FindAll("[data-testid=tps-scope-summary], [data-testid=tps-boundary], [data-testid=scf]").Should().BeEmpty();
        cut.Markup.Should().NotContain("Project Test Setup").And.NotContain("test area");
    }

    // ── Universal (project) workflow ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Without_a_selected_project_the_page_asks_for_one_and_still_lists_earlier_guide_plans()
    {
        var draftId = Guid.NewGuid();
        var api = new Api(Workspace(drafts: [new(draftId, "skolenaervaer", "Guide plan", null, 2, 3, 0, 0, 0, 3, 3, "t", DateTimeOffset.UtcNow)]));
        var cut = Render(api);
        cut.Find("[data-testid=tps-no-project]").TextContent.Should().Contain("Choose a project first");
        cut.Find("[data-testid=tps-historical-plans] a").GetAttribute("href").Should().Be($"test-plan-suggestions?guide=skolenaervaer&draft={draftId}");
        api.ProjectReads.Should().BeEmpty("no project, no project read");
        api.Discovered.Should().BeNull("discovery never runs on navigation");
    }

    [Fact]
    public void An_unrelated_project_shows_its_own_name_and_evidence_and_never_assumes_a_guide()
    {
        SelectProject();
        var api = new Api(Workspace()) { Project = ProjectWorkspace(), Discovery = Discovery("library", LibraryCandidates) };
        var cut = Render(api);
        cut.Find("[data-testid=tps-project-name]").TextContent.Should().Be("Library");
        cut.Markup.Should().NotContain("Skolenærvær").And.NotContain("skolenaervaer");
        api.ProjectReads.Should().Equal("library");
        api.Discovered.Should().BeNull("nothing runs until the tester asks");
        cut.Find("[data-testid=tps-no-candidates]").TextContent.Should().Contain("Not discovered yet");

        cut.Find("[data-testid=tps-discover-run]").Click();
        api.Discovered!.ProjectKey.Should().Be("library");
        cut.Find("[data-testid=tps-capability-system-guide]").GetAttribute("data-state").Should().Be("NotApplicable");
        cut.Find("[data-testid=tps-capability-api-contract]").TextContent.Should().Contain("1 found");
        // Manual testing is the default view: the API operation stays in the inventory, one click away, never in the manual list.
        cut.FindAll("li[data-testid^=tps-candidate-]").Should().HaveCount(3);
        cut.FindAll("[data-testid=tps-candidate-tsc-getbook]").Should().BeEmpty();
        cut.Find("[data-testid=tps-view-all]").Click();
        cut.FindAll("li[data-testid^=tps-candidate-]").Should().HaveCount(4);
        cut.Find("[data-testid=tps-candidate-tsc-return] [data-testid=tps-attention]").TextContent.Should().Be("Needs clarification");
        cut.FindAll("[data-testid=tps-candidate-tsc-borrow] [data-testid=tps-attention]").Should().BeEmpty("a warning only when attention is needed");
        cut.Markup.Should().NotContain("Skolenærvær");
    }

    [Fact]
    public void Filtering_by_type_keeps_hidden_selections_and_preparing_sends_only_ids_of_this_projects_discovery()
    {
        SelectProject();
        var api = new Api(Workspace()) { Project = ProjectWorkspace(), Last = Discovery("library", LibraryCandidates) };
        var cut = Render(api);
        cut.Find("[data-testid=tps-stat-total]").TextContent.Should().Contain("4", "the last result for this project is reused without re-running analyzers");
        api.Discovered.Should().BeNull();
        cut.Find("[data-testid=tps-view-all]").Click();
        cut.FindAll("li[data-testid^=tps-candidate-]").Should().HaveCount(4);

        cut.Find("[data-testid=tps-select-tsc-borrow]").Change(true);
        cut.Find("[data-testid=tps-select-tsc-getbook]").Change(true);
        cut.Find("[data-testid=tps-filter-type]").Change(nameof(ScenarioCandidateType.ApiContract));
        cut.FindAll("li[data-testid^=tps-candidate-]").Select(e => e.GetAttribute("data-testid")).Should().Equal("tps-candidate-tsc-getbook");
        cut.Find("[data-testid=tps-selected-count]").TextContent.Should().Contain("2");
        cut.Find("[data-testid=tps-selected-hidden]").TextContent.Should().Contain("1 hidden by filters");

        // Two cases, one of them technical: the preparation preview says so before anything is created.
        cut.Find("[data-testid=tps-prepare]").Click();
        api.CreatedProject.Should().BeNull("a selection that includes a technical candidate is confirmed first");
        cut.Find("[data-testid=tps-preview-technical]").TextContent.Should().Contain("1 technical candidate");
        cut.Find("[data-testid=tps-preview-confirm]").Click();
        api.CreatedProject!.ProjectKey.Should().Be("library");
        api.CreatedProject.DiscoveryId.Should().Be("disc-1");
        api.CreatedProject.CandidateIds.Should().BeEquivalentTo(["tsc-borrow", "tsc-getbook"]);
        api.CreatedProject.PlanName.Should().BeNull("the plan is named only at save/export");
        cut.Find("[data-testid=tps-prepare-step]").TextContent.Should().Contain("Borrow a book");
    }

    // ── Clarification accuracy (2026-11-18): classified gaps instead of one "Needs clarification" ─────────────────────────────────────────────

    private static DiscoveredScenario PackScenario(string id, string? expected, params EvidenceGap[] gaps) => Discovered(id, "Intake · " + id, ScenarioCandidateType.Integration, "Intake", "Integration journey", expected) with
    {
        Confidence = CandidateConfidence.CandidateAssociation, ExpectedBasis = expected is null ? null : "Stated by the test pack Intake 1", Gaps = [.. gaps],
        SourcesChecked = [new("Test pack Intake 1", true, "Scenario, steps, stated outcome and open points"), new("Integration journey runs", false, "No run in DEV")],
        Evidence = [new("journey", "intake:" + id, "Intake 1"), new("journey-run", Guid.NewGuid().ToString(), "NotVerified in DEV, 2026-11-18 09:00 UTC")],
    };

    [Fact]
    public void A_stated_outcome_with_a_missing_prerequisite_is_not_called_needs_clarification_and_the_details_say_what_is_open()
    {
        SelectProject();
        var two = PackScenario("two-reporters", "Both reports are kept.",
            new(EvidenceGapKind.TestPrerequisiteMissing, "A second approved synthetic reporter is needed.", "Test pack", "Prepare a second approved synthetic reporter in Test Data."),
            new(EvidenceGapKind.VerificationMechanismMissing, "No approved way to show both reports are kept downstream.", "Test pack"),
            new(EvidenceGapKind.AssociationNeedsReview, "Associated through source roles; not confirmed.", "Source role recognition"));
        var api = new Api(Workspace()) { Project = ProjectWorkspace(), Last = Discovery("library", two) };
        var cut = Render(api);

        cut.Find("[data-testid=tps-candidate-two-reporters] [data-testid=tps-attention]").TextContent.Should().Be("Test prerequisite missing");
        cut.Find("[data-testid=tps-details-two-reporters]").Click();
        cut.Find("[data-testid=tps-expected]").TextContent.Should().Be("Both reports are kept. (Stated by the test pack Intake 1)");
        var gaps = cut.FindAll("[data-testid=tps-gap]");
        gaps.Select(g => g.GetAttribute("data-kind")).Should().Equal("TestPrerequisiteMissing", "VerificationMechanismMissing", "AssociationNeedsReview");
        gaps[0].TextContent.Should().Contain("Next: Prepare a second approved synthetic reporter in Test Data.").And.Contain("Tester (Test Data, access, environment)");
        cut.Find("[data-testid=tps-sources-checked]").TextContent.Should().Contain("Integration journey runs — No run in DEV");
        cut.Find("[data-testid=tps-evidence-run]").TextContent.Should().Be("Integration journey run — NotVerified in DEV, 2026-11-18 09:00 UTC", "a run is named by what it showed, never by its id");
        cut.Markup.Should().NotContain("Needs clarification");
    }

    [Fact]
    public void An_unstated_outcome_still_needs_clarification_and_a_scenario_only_awaiting_a_run_gets_no_warning_chip()
    {
        SelectProject();
        var unclear = PackScenario("duplicate", null, new EvidenceGap(EvidenceGapKind.BusinessExpectationUnclear, "No expected outcome is stated.", "Test pack"));
        var runOnly = PackScenario("valid", "The report is accepted.", new EvidenceGap(EvidenceGapKind.RuntimeEvidenceMissing, "Not run in DEV yet.", "Integration journey runs", "Run it from Integration Testing."),
            new EvidenceGap(EvidenceGapKind.AssociationNeedsReview, "Associated through source roles; not confirmed.", "Source role recognition"));
        var api = new Api(Workspace()) { Project = ProjectWorkspace(), Last = Discovery("library", unclear, runOnly) };
        var cut = Render(api);
        cut.Find("[data-testid=tps-candidate-duplicate] [data-testid=tps-attention]").TextContent.Should().Be("Needs clarification");
        cut.FindAll("[data-testid=tps-candidate-valid] [data-testid=tps-attention]").Should().BeEmpty("not having run yet is shown in the details, not as a warning on every row");
        cut.Find("[data-testid=tps-details-duplicate]").Click();
        cut.Find("[data-testid=tps-candidate-details-duplicate] [data-testid=tps-expected]").TextContent.Should().Be("Needs clarification — the evidence states no expected outcome.");
    }

    [Theory]
    [InlineData(EvidenceGapKind.ContradictoryEvidence, "Contradictory evidence")]
    [InlineData(EvidenceGapKind.ImplementationEvidenceMissing, "Implementation evidence missing")]
    [InlineData(EvidenceGapKind.VerificationMechanismMissing, "Verification mechanism missing")]
    public void The_warning_names_the_most_pressing_gap(EvidenceGapKind kind, string label) =>
        TestPlanSuggestionsPresentation.Warning(PackScenario("x", "Outcome.", new EvidenceGap(EvidenceGapKind.RuntimeEvidenceMissing, "r"), new EvidenceGap(kind, "q"),
            new EvidenceGap(EvidenceGapKind.TestPrerequisiteMissing, "p"))).Should().Be(kind == EvidenceGapKind.VerificationMechanismMissing ? "Test prerequisite missing" : label);

    [Fact]
    public void Many_candidates_open_only_the_first_group_and_each_group_heading_opens_its_list_and_counts_selections()
    {
        SelectProject();
        var many = Enumerable.Range(1, 45).Select(i => Discovered($"tsc-{i:00}", $"Scenario {i:00}", ScenarioCandidateType.UiFunctional, i <= 15 ? "Catalogue" : i <= 30 ? "Loans" : "Members", "UI source")).ToArray();
        var api = new Api(Workspace()) { Project = ProjectWorkspace(), Last = Discovery("library", many) };
        var cut = Render(api);

        var groups = cut.FindAll("section[data-testid^=tps-group-]");
        groups.Select(g => g.GetAttribute("data-open")).Should().Equal(["true", "false", "false"], "with many candidates the headings are the overview; only the first group starts open");
        cut.FindAll("li[data-testid^=tps-candidate-]").Should().HaveCount(15);
        var toggle = cut.Find("[data-testid=tps-group-toggle-loans]");
        toggle.GetAttribute("aria-expanded").Should().Be("false");
        toggle.TextContent.Should().Contain("Loans").And.Contain("15");

        cut.Find("[data-testid=tps-select-shown]").Click();
        cut.Find("[data-testid=tps-group-toggle-loans]").TextContent.Should().Contain("15 selected", "a closed group says how many of its candidates are selected");

        cut.Find("[data-testid=tps-group-toggle-loans]").Click();
        cut.Find("[data-testid=tps-group-toggle-loans]").GetAttribute("aria-expanded").Should().Be("true");
        cut.FindAll("li[data-testid^=tps-candidate-]").Should().HaveCount(30);
        cut.Find("[data-testid=tps-group-toggle-catalogue]").Click();
        cut.FindAll("li[data-testid^=tps-candidate-]").Should().HaveCount(15, "the tester can close the first group too");
    }

    [Fact]
    public void A_discovery_result_of_another_project_is_never_shown_and_another_projects_plan_never_opens()
    {
        SelectProject();
        var foreign = ProjectDraft("other-project");
        var api = new Api(Workspace(), foreign) { Project = ProjectWorkspace(), Last = Discovery("other-project", Discovered("tsc-x", "Other project's scenario", ScenarioCandidateType.UiSmoke, "X", "UI source", project: "other-project")) };
        var cut = Render(api, $"?draft={foreign.Id}");
        cut.FindAll("li[data-testid^=tps-candidate-]").Should().BeEmpty();
        cut.Markup.Should().NotContain("Other project's scenario");
        cut.Find("[data-testid=tps-message]").TextContent.Should().Contain("belongs to another project");
        cut.Find("[data-testid=tps-existing-plan]").TextContent.Should().Contain("None yet");
    }

    [Fact]
    public void A_drill_down_from_coverage_opens_that_saved_case_in_its_own_plan()
    {
        SelectProject();
        var plan = ProjectDraft("library");
        var api = new Api(Workspace(), plan) { Project = ProjectWorkspace() };
        var cut = Render(api, $"?draft={plan.Id}&area=prepare&case=tsc-borrow");
        cut.WaitForAssertion(() => cut.Find("[data-testid=tps-case-tsc-borrow]").ClassList.Should().Contain("is-open", "the case the link names is opened"));
        cut.Find("[data-testid=tps-case-tsc-borrow]").TextContent.Should().Contain("Borrow a book");
    }

    [Fact]
    public void A_disabled_Add_test_case_says_why_until_a_title_is_typed()
    {
        // Regression (2026-11-18): "Add test case" was disabled without any reason shown, so it looked broken.
        SelectProject("notes", "Notes only");
        var api = new Api(Workspace()) { Project = ProjectWorkspace("notes"), Discovery = Discovery("notes") };
        var cut = Render(api);
        cut.Find("[data-testid=tps-discover-run]").Click();

        cut.Find("[data-testid=tps-manual-create]").HasAttribute("disabled").Should().BeTrue();
        cut.Find("[data-testid=tps-manual-hint]").TextContent.Should().Be("Type a title to add a test case.");

        cut.Find("[data-testid=tps-manual-title]").Input("Log in with an expired password");
        cut.Find("[data-testid=tps-manual-create]").HasAttribute("disabled").Should().BeFalse();
        cut.FindAll("[data-testid=tps-manual-hint]").Should().BeEmpty("the reason disappears once it no longer applies");
    }

    [Fact]
    public void Without_write_permission_the_page_says_so_and_offers_no_prepare_or_add_actions()
    {
        // The read-only state the Windows Tester Package showed before its backend granted local configuration writes (2026-11-18).
        SelectProject("notes", "Notes only");
        var api = new Api(Workspace()) { Project = ProjectWorkspace("notes") with { CanWrite = false }, Discovery = Discovery("notes") };
        var cut = Render(api);

        cut.Find("[data-testid=tps-read-only]").TextContent.Should().Contain("needs the configuration-write permission");
        cut.FindAll("[data-testid=tps-prepare]").Should().BeEmpty();
        cut.FindAll("[data-testid=tps-manual-create]").Should().BeEmpty();
    }

    [Fact]
    public void With_no_evidence_a_tester_can_still_write_a_test_case_by_hand()
    {
        SelectProject("notes", "Notes only");
        var api = new Api(Workspace()) { Project = ProjectWorkspace("notes"), Discovery = Discovery("notes") };
        var cut = Render(api);
        cut.Find("[data-testid=tps-discover-run]").Click();
        cut.Find("[data-testid=tps-no-candidates]").TextContent.Should().Contain("No scenario found").And.Contain("write test cases by hand");
        cut.Find("[data-testid=tps-manual-title]").Input("Log in with an expired password");
        cut.Find("[data-testid=tps-manual-create]").Click();
        api.CreatedProject!.ProjectKey.Should().Be("notes");
        api.CreatedProject.ManualCaseTitle.Should().Be("Log in with an expired password");
        api.CreatedProject.CandidateIds.Should().BeEmpty("nothing generated is attached to a hand-written case");
        cut.Find("[data-testid=tps-case-manual-1]").TextContent.Should().Contain("Log in with an expired password");
    }

    [Fact]
    public void A_project_case_edits_steps_with_their_own_expected_results_and_keeps_them_apart_from_the_evidence()
    {
        var c = ProjectDraft("library").Cases.Single();
        c.EffectiveStepExpected.Should().Equal("The book is shown", null);

        var edited = P.SetStep(c, 1, null, "The loan appears under My loans");
        edited.Edits.Steps.Should().Equal("Choose a book", "Confirm the loan");
        edited.Edits.StepExpected.Should().Equal("The book is shown", "The loan appears under My loans");
        edited.Generated.Should().Be(c.Generated, "the evidence is never rewritten");

        var added = P.AddStep(edited, "Open My loans");
        added.EffectiveSteps.Should().Equal("Choose a book", "Confirm the loan", "Open My loans");
        added.EffectiveStepExpected[2].Should().BeNull("a new step's result is Needs clarification until stated");
        var moved = P.MoveStep(added, 2, -1);
        moved.EffectiveSteps.Should().Equal("Choose a book", "Open My loans", "Confirm the loan");
        moved.EffectiveStepExpected.Should().Equal("The book is shown", null, "The loan appears under My loans");
        P.RemoveStep(moved, 1).EffectiveSteps.Should().Equal("Choose a book", "Confirm the loan");

        // Reverting to exactly the evidence clears the edit again.
        P.SetStep(edited, 1, null, "").Edits.Steps.Should().BeNull();

        var request = P.ToSaveRequest(ProjectDraft("library") with { Cases = [moved] });
        request.Cases.Single().Edits.StepExpected.Should().Equal("The book is shown", null, "The loan appears under My loans");
    }

    [Fact]
    public void Readiness_is_one_honest_state_and_only_unready_cases_need_action()
    {
        P.ReadinessLabel(CaseReadiness.NotAssessed).Text.Should().Be("Not assessed");
        P.ReadinessLabel(CaseReadiness.NotRequired).Text.Should().Be("Not required");
        P.ReadinessLabel(CaseReadiness.RequiredButMissing).Tone.Should().Be("bad");
        P.DataNeedLabel(TestDataNeed.Unknown).Should().Be("Not assessed yet", "absence of a description is never 'not required'");
        var c = ProjectDraft("library").Cases.Single();
        P.NeedsAction(c).Should().BeTrue();
        P.NeedsAction(c with { Testability = c.Testability with { Readiness = CaseReadiness.RequiredAndAvailable } }).Should().BeFalse();
        P.NeedsAction(c with { Review = TestCaseReviewState.Excluded }).Should().BeFalse();

        var steps = P.Steps(null, [], 4, true, 2);
        steps.Select(s => s.Id).Should().Equal("choose", "prepare", "testability", "save");
        steps[0].State.Should().Be(GuidedStepState.Current);
        steps[0].StatusLabel.Should().Be("4 candidates · 2 selected");
        steps.Skip(1).Should().OnlyContain(s => s.DisabledReason == "Prepare test cases first.");
    }

    [Fact]
    public void Catalog_filters_and_groups_by_real_metadata()
    {
        P.Apply(LibraryCandidates, new P.CatalogFilter(Query: "book")).Should().HaveCount(3);
        P.Apply(LibraryCandidates, new P.CatalogFilter(Source: "Specification")).Should().HaveCount(2);
        P.Apply(LibraryCandidates, new P.CatalogFilter(AttentionOnly: true)).Single().CandidateId.Should().Be("tsc-return");
        P.Apply(LibraryCandidates, new P.CatalogFilter(Tests: "with")).Should().BeEmpty();
        P.Group(LibraryCandidates, P.CatalogGrouping.Type).Select(g => g.Title).Should().Equal("Business functional", "API / contract", "UI functional");
        P.Group(LibraryCandidates, P.CatalogGrouping.Area).Select(g => g.Id).Should().Equal("loans", "library-api", "catalogue");

    }

    // ── Scenario ownership views (2026-11-18): one inventory, Manual testing by default, technical candidates one click away ──────────────

    private static DiscoveredScenario Operation(string id, string file, string operation = "mutation activateEmergencyAccess") => new()
    {
        CandidateId = id, ProjectKey = "library", CapabilityId = "api-contract", SourceCategory = "API contract", Area = "Authorization", Title = operation,
        Purpose = "Verify " + operation, Type = ScenarioCandidateType.ApiContract, Confidence = CandidateConfidence.ContractDerived, ExpectedBehaviour = "Returns EmergencyAccess, as the schema declares.",
        Template = [new("operation", "Operation", operation), new("input", "Input", null), new("expected-response", "Expected response", "Returns EmergencyAccess, as the schema declares.")],
        Evidence = [new("contract-operation", $"{file}:1#{operation}", "GraphQlSchema Authorization")], CanonicalKey = $"contract-op:{file}#{operation}", Engine = "Contract analyzer", RuleVersion = "tsd-1",
    };

    [Fact]
    public void Manual_testing_is_the_default_view_and_switching_views_keeps_every_selection()
    {
        SelectProject();
        var cut = Render(new Api(Workspace()) { Project = ProjectWorkspace(), Last = Discovery("library", LibraryCandidates) });
        cut.Find("[data-testid=tps-views] [aria-pressed=true]").GetAttribute("data-testid").Should().Be("tps-view-manual");
        cut.Find("[data-testid=tps-stat-total]").TextContent.Should().Contain("4");
        cut.Find("[data-testid=tps-stat-manual]").TextContent.Should().Contain("3");
        cut.Find("[data-testid=tps-stat-technical]").TextContent.Should().Contain("1");
        cut.FindAll("li[data-testid^=tps-candidate-]").Select(e => e.GetAttribute("data-testid")).Should().BeEquivalentTo(["tps-candidate-tsc-borrow", "tps-candidate-tsc-return", "tps-candidate-tsc-search"]);

        cut.Find("[data-testid=tps-select-tsc-borrow]").Change(true);
        cut.Find("[data-testid=tps-view-technical]").Click();
        cut.FindAll("li[data-testid^=tps-candidate-]").Select(e => e.GetAttribute("data-testid")).Should().Equal("tps-candidate-tsc-getbook");
        cut.Find("[data-testid=tps-selected-count]").TextContent.Should().Contain("1");
        cut.Find("[data-testid=tps-selected-hidden]").TextContent.Should().Contain("1 hidden by filters", "a selection in another view stays selected and counted");
        Services.GetRequiredService<NavigationManager>().Uri.Should().Contain("view=technical", "the view is part of the address");

        cut.Find("[data-testid=tps-select-tsc-getbook]").Change(true);
        cut.Find("[data-testid=tps-view-manual]").Click();
        cut.Find("[data-testid=tps-select-tsc-borrow]").HasAttribute("checked").Should().BeTrue();
        cut.Find("[data-testid=tps-view-technical]").TextContent.Should().Contain("1 selected");
        cut.Find("[data-testid=tps-selected-count]").TextContent.Should().Contain("2");

        // Select visible selects only what this view shows.
        cut.Find("[data-testid=tps-clear-selection]").Click();
        cut.Find("[data-testid=tps-select-shown]").Click();
        cut.Find("[data-testid=tps-selected-count]").TextContent.Should().Contain("3");
        cut.Find("[data-testid=tps-view-technical]").TextContent.Should().NotContain("selected");
    }

    [Fact]
    public void A_link_opens_the_technical_view_and_the_all_view_lists_the_complete_inventory()
    {
        SelectProject();
        var api = new Api(Workspace()) { Project = ProjectWorkspace(), Last = Discovery("library", LibraryCandidates) };
        var cut = Render(api, "?view=technical");
        cut.FindAll("li[data-testid^=tps-candidate-]").Select(e => e.GetAttribute("data-testid")).Should().Equal("tps-candidate-tsc-getbook");
        cut.Find("[data-testid=tps-view-all]").Click();
        cut.FindAll("li[data-testid^=tps-candidate-]").Should().HaveCount(4, "no candidate disappears from the complete inventory");
        cut.Find("[data-testid=tps-candidate-tsc-getbook] [data-testid=tps-workspace]").TextContent.Should().Be("Technical Testing");
        cut.Find("[data-testid=tps-view-attention]").Click();
        cut.FindAll("li[data-testid^=tps-candidate-]").Select(e => e.GetAttribute("data-testid")).Should().Equal("tps-candidate-tsc-return");
    }

    [Fact]
    public void An_api_only_project_says_why_the_manual_view_is_empty_and_offers_real_actions()
    {
        SelectProject();
        var cut = Render(new Api(Workspace()) { Project = ProjectWorkspace(), Last = Discovery("library", Operation("op-1", "Svc/schema.graphql")) });
        cut.Find("[data-testid=tps-manual-empty]").TextContent.Should().Contain("No business-focused scenarios identified. You can prepare technical candidates manually or open Technical Testing.");
        cut.Find("[data-testid=tps-manual-empty-open-technical]").GetAttribute("href").Should().Be("technical-testing/assess");
        cut.Find("[data-testid=tps-manual-empty-technical]").Click();
        cut.FindAll("li[data-testid^=tps-candidate-]").Select(e => e.GetAttribute("data-testid")).Should().Equal("tps-candidate-op-1");
        cut.Find("[data-testid=tps-select-op-1]").Change(true);
        cut.Find("[data-testid=tps-prepare]").HasAttribute("disabled").Should().BeFalse("a technical candidate can be prepared as a manual case on purpose");
    }

    [Fact]
    public void Candidate_details_say_where_to_test_and_hand_off_without_invented_parameters()
    {
        SelectProject();
        var copies = new[] { Operation("op-1", "AuthService/autodoc/schema.graphql"), Operation("op-2", "Frontend/GraphQL/Authorization/schema.graphql") };
        var cut = Render(new Api(Workspace()) { Project = ProjectWorkspace(), Last = Discovery("library", [.. LibraryCandidates, .. copies]) }, "?view=technical");
        cut.Find("[data-testid=tps-stat-operations]").TextContent.Should().Contain("2 operation entries = 1 distinct operation");
        cut.Find("[data-testid=tps-details-op-1]").Click();
        var details = cut.Find("[data-testid=tps-candidate-details-op-1]");
        details.QuerySelector("[data-testid=tps-ownership]")!.TextContent.Should().Contain("Technical Testing").And.Contain("API / contract (technical)");
        details.QuerySelector("[data-testid=tps-handoff]")!.GetAttribute("href").Should().Be("api-quality-review/contracts");
        details.QuerySelector("[data-testid=tps-same-operation]")!.TextContent.Should().Contain("Frontend/GraphQL/Authorization/schema.graphql").And.Contain("kept separate");

        // Handoffs only use routes and parameters their destination reads.
        var declared = new DiscoveredScenario { CapabilityId = "integration-journeys", Type = ScenarioCandidateType.Integration, CanonicalKey = "journey:project.import:imp-1:loan-flow", Area = "Loan flow" };
        P.HandoffOf(declared, ScenarioOwnership.Of(declared))!.Href.Should().Be("technical-testing/integrations?stage=prepare&journey=project.import%3Aimp-1%3Aloan-flow", "a project key may contain a colon");
        var pack = new DiscoveredScenario { CapabilityId = "integration-journeys", Type = ScenarioCandidateType.Integration, CanonicalKey = "journey:skolenaervaer:rapportmottak:e2e-09" };
        P.HandoffOf(pack, ScenarioOwnership.Of(pack))!.Href.Should().Be("extensions/skolenaervaer?tab=rapportmottak");
        var otherPack = pack with { CanonicalKey = "journey:orders:intake:two-buyers" };
        P.HandoffOf(otherPack, ScenarioOwnership.Of(otherPack))!.Href.Should().Be("integration-quality-review", "a pack without a page of its own is reviewed in IQR");
        var business = LibraryCandidates[0];
        P.HandoffOf(business, ScenarioOwnership.Of(business)).Should().BeNull("a business scenario is tested here");
    }

    [Fact]
    public void A_candidate_already_in_the_plan_offers_the_existing_case_and_a_separate_case_only_on_request()
    {
        SelectProject();
        var plan = ProjectDraft("library");
        var api = new Api(Workspace(), plan) { Project = ProjectWorkspace(), Last = Discovery("library", LibraryCandidates) };
        var cut = Render(api, $"?draft={plan.Id}&area=choose");
        cut.Find("[data-testid=tps-stat-saved]").TextContent.Should().Contain("1");
        cut.Find("[data-testid=tps-candidate-tsc-borrow] [data-testid=tps-in-draft]").TextContent.Should().Be("In the plan");
        cut.Find("[data-testid=tps-select-tsc-borrow]").Change(true);
        cut.Find("[data-testid=tps-prepare-in-plan]").TextContent.Should().Contain("1 already in the plan");
        cut.Find("[data-testid=tps-prepare]").HasAttribute("disabled").Should().BeTrue("nothing new would be added");

        cut.Find("[data-testid=tps-details-tsc-borrow]").Click();
        cut.Find("[data-testid=tps-add-variant]").Click();
        cut.Find("[data-testid=tps-manual-title]").GetAttribute("value").Should().Be("Borrow a book — variant");
        api.CreatedProject.Should().BeNull("a separate case is created only when the tester adds it");

        cut.Find("[data-testid=tps-open-case]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=tps-body]").GetAttribute("data-area").Should().Be("prepare"));
        cut.Find("[data-testid=tps-case-tsc-borrow]").ClassList.Should().Contain("is-open");
    }

    [Fact]
    public void One_ordinary_case_is_prepared_at_once_without_a_confirmation()
    {
        SelectProject();
        var single = new Api(Workspace()) { Project = ProjectWorkspace(), Last = Discovery("library", LibraryCandidates) };
        var cut = Render(single);
        cut.Find("[data-testid=tps-select-tsc-borrow]").Change(true);
        cut.Find("[data-testid=tps-prepare]").Click();
        single.CreatedProject!.CandidateIds.Should().Equal("tsc-borrow");
    }

    [Fact]
    public void A_bulk_preparation_preview_names_what_is_selected_and_backing_out_creates_nothing()
    {
        SelectProject();
        var many = Enumerable.Range(1, 6).Select(i => Discovered($"tsc-ui-{i}", $"Search {i}", ScenarioCandidateType.UiFunctional, "Catalogue", "UI source", expected: i == 1 ? null : "Shown")).ToArray();
        var api = new Api(Workspace()) { Project = ProjectWorkspace(), Last = Discovery("library", many) };
        var cut = Render(api);
        cut.Find("[data-testid=tps-select-shown]").Click();
        cut.Find("[data-testid=tps-prepare]").TextContent.Should().Be("Prepare 6 test cases");
        cut.Find("[data-testid=tps-prepare]").Click();
        cut.Find("[data-testid=tps-preview]").TextContent.Should().Contain("Prepare 6 test cases?");
        cut.Find("[data-testid=tps-preview-kinds]").TextContent.Should().Be("6 ui behaviour");
        cut.Find("[data-testid=tps-preview-missing-expected]").TextContent.Should().StartWith("1 without an expected result");
        cut.FindAll("[data-testid=tps-preview-technical]").Should().BeEmpty();
        cut.WaitForAssertion(() => JSInterop.Invocations.Should().Contain(i => i.Identifier == "birknextRevealSection" && (string)i.Arguments[0]! == "tps-preview", "the preview takes focus"));

        cut.Find("[data-testid=tps-preview-cancel]").Click();
        api.CreatedProject.Should().BeNull();
        cut.Find("[data-testid=tps-selected-count]").TextContent.Should().Contain("6", "backing out keeps the selection");

        cut.Find("[data-testid=tps-prepare]").Click();
        cut.Find("[data-testid=tps-preview-confirm]").Click();
        api.CreatedProject!.CandidateIds.Should().HaveCount(6).And.OnlyContain(id => id.StartsWith("tsc-ui-"), "only the explicitly selected candidates");
    }

    [Fact]
    public void The_preview_counts_overlaps_with_the_plan_and_between_copies_of_one_operation()
    {
        var a = Operation("op-a", "Auth/schema.graphql");
        var b = Operation("op-b", "Frontend/schema.graphql");
        var borrow = LibraryCandidates[0] with { PossibleDuplicates = ["tsc-in-plan"] };
        var all = new[] { a, b, borrow };
        var inventory = P.Classify(all);
        var preview = P.Preview(["op-a", "op-b", borrow.CandidateId], all, inventory, new HashSet<string> { "tsc-in-plan" });
        preview.Should().Match<P.PreparePreview>(p => p.Adding == 3 && p.Technical == 2 && p.Overlaps == 2 && p.NeedsConfirmation);
        P.OverlapsWithPlan([borrow.CandidateId], all, inventory, new HashSet<string> { "tsc-in-plan" }).Should().Be(1);
        P.Preview([borrow.CandidateId], all, inventory, new HashSet<string>()).NeedsConfirmation.Should().BeFalse();
        P.Preview(["op-a"], all, inventory, new HashSet<string>()).NeedsConfirmation.Should().BeFalse("one case is never confirmed twice");
    }

    [Fact]
    public void Rediscovery_keeps_only_selections_whose_candidate_still_exists()
    {
        SelectProject();
        var api = new Api(Workspace()) { Project = ProjectWorkspace(), Last = Discovery("library", LibraryCandidates), Discovery = Discovery("library", LibraryCandidates[0], LibraryCandidates[2]) };
        var cut = Render(api, "?view=all");
        cut.Find("[data-testid=tps-select-tsc-borrow]").Change(true);
        cut.Find("[data-testid=tps-select-tsc-return]").Change(true);
        cut.Find("[data-testid=tps-discover-run]").Click();
        cut.Find("[data-testid=tps-selected-count]").TextContent.Should().Contain("1", "tsc-return left the evidence; its selection is not carried to another candidate");
        cut.Find("[data-testid=tps-select-tsc-borrow]").HasAttribute("checked").Should().BeTrue();
    }

    [Fact]
    public void The_stepper_is_one_compact_row_whose_steps_all_open_and_say_why_when_they_wait()
    {
        SelectProject();
        var cut = Render(new Api(Workspace()) { Project = ProjectWorkspace(), Last = Discovery("library", LibraryCandidates) });
        cut.FindAll("[data-testid=tps-steps] [data-testid=tps-step]").Select(s => s.GetAttribute("data-step")).Should().Equal("choose", "prepare", "testability", "save");
        cut.Find("[data-testid=tps-area-choose]").GetAttribute("aria-current").Should().Be("step");
        cut.FindAll("[data-testid=tps-step-status]")[1].TextContent.Should().Be("Prepare test cases first.");
        cut.Find("[data-testid=tps-area-prepare]").HasAttribute("disabled").Should().BeFalse("a waiting step opens and explains itself");
        cut.Find("[data-testid=tps-area-prepare]").Click();
        cut.Find("[data-testid=tps-no-draft]").TextContent.Should().Contain("No test cases yet");
    }

    [Fact]
    public void A_selection_survives_a_reload_of_the_tab_for_the_scenarios_discovery_still_has()
    {
        SelectProject();
        JSInterop.Setup<string?>("sessionStorage.getItem", i => (string?)i.Arguments[0] == TabDraftStore.SelectionKey("library")).SetResult("[\"tsc-borrow\",\"tsc-gone\"]");
        var api = new Api(Workspace()) { Project = ProjectWorkspace(), Last = Discovery("library", LibraryCandidates) };
        var cut = Render(api);
        cut.Find("[data-testid=tps-view-all]").Click();
        cut.Find("[data-testid=tps-selected-count]").TextContent.Should().Contain("1", "a scenario the discovery no longer has is not restored");
        cut.Find("[data-testid=tps-select-tsc-borrow]").HasAttribute("checked").Should().BeTrue();
        cut.Find("[data-testid=tps-message]").TextContent.Should().Contain("kept from before the page was reloaded").And.Contain("Nothing is prepared");
        api.CreatedProject.Should().BeNull("restoring a selection prepares nothing");

        cut.Find("[data-testid=tps-select-tsc-getbook]").Change(true);
        cut.WaitForAssertion(() => JSInterop.Invocations["sessionStorage.setItem"].Last().Arguments.Should().Equal(TabDraftStore.SelectionKey("library"), "[\"tsc-borrow\",\"tsc-getbook\"]"));
        cut.Find("[data-testid=tps-clear-selection]").Click();
        cut.WaitForAssertion(() => JSInterop.Invocations["sessionStorage.removeItem"].Should().Contain(i => (string?)i.Arguments[0] == TabDraftStore.SelectionKey("library")));
    }

    [Fact]
    public void A_hand_written_case_opens_at_its_first_step_and_a_step_is_added_with_its_expected_result()
    {
        SelectProject("notes", "Notes only");
        var api = new Api(Workspace()) { Project = ProjectWorkspace("notes"), Discovery = Discovery("notes") };
        var cut = Render(api);
        cut.Find("[data-testid=tps-discover-run]").Click();
        cut.Find("[data-testid=tps-manual-title]").Input("Log in with an expired password");
        cut.Find("[data-testid=tps-manual-create]").Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid=tps-steps-empty-manual-1]").TextContent.Should().Contain("No steps yet").And.Contain("before it can be approved"));
        cut.WaitForAssertion(() => JSInterop.Invocations.Should().Contain(i => i.Identifier == "birknextRevealSection" && (string?)i.Arguments[0] == "tps-step-new-manual-1", "the tester continues at the first step"));
        cut.Find("[data-testid=tps-step-new-manual-1]").Input("Log in with the expired password");
        cut.Find("[data-testid=tps-step-new-expected-manual-1]").Input("The password change page is shown");
        cut.Find("[data-testid=tps-step-add-manual-1]").Click();

        cut.Find("[data-testid=tps-step-action-manual-1-0]").GetAttribute("value").Should().Be("Log in with the expired password");
        cut.Find("[data-testid=tps-step-expected-manual-1-0]").GetAttribute("value").Should().Be("The password change page is shown");
        cut.FindAll("[data-testid=tps-steps-empty-manual-1]").Should().BeEmpty();
    }

    [Fact]
    public void Steps_the_evidence_leaves_open_are_named_before_preparing()
    {
        var complete = Discovered("a", "Complete", ScenarioCandidateType.BusinessFunctional, "Loans", "Specification") with { Steps = [new("Open", "Shown"), new("Lend", "Lent")] };
        var partly = Discovered("b", "Partly", ScenarioCandidateType.BusinessFunctional, "Loans", "Specification") with { Steps = [new("Open", "Shown"), new("Lend", null), new("Close", " ")] };
        var none = Discovered("c", "No steps", ScenarioCandidateType.BusinessFunctional, "Loans", "Specification");
        var silent = Discovered("d", "Silent", ScenarioCandidateType.BusinessFunctional, "Loans", "Specification") with { Steps = [new("Open", null)] };
        P.StepGap(complete).Should().BeNull();
        P.StepGap(partly).Should().Be("2 of 3 steps without an expected result");
        P.StepGap(none).Should().Be("No steps in the source");
        P.StepGap(silent).Should().Be("No step states its expected result");

        var all = new[] { complete, partly, none };
        var preview = P.Preview(["a", "b", "c"], all, P.Classify([.. all]), new HashSet<string>());
        preview.IncompleteSteps.Should().Be(2);
        preview.NeedsConfirmation.Should().BeTrue("the tester sees the work left before the cases are created");
        P.Preview(["a"], all, P.Classify([.. all]), new HashSet<string>()).IncompleteSteps.Should().Be(0);
    }
}
