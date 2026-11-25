using BirkNext.TestCoverage;
using BirkNext.TestPlanning;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using P = BirkNext.Web.Services.PlanPreparePresentation;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// Manuell testing → Planlegg og klargjør (2026-11-23): the master-detail case editor.
/// A — the editor's working copy produces only human-owned edits (unchanged = no edit) and edits steps (add, edit, remove, reorder).
/// B — "Vis teknisk innhold": technical scenarios and stories are hidden by default, never what needs a person.
/// C — scenario first, several cases per scenario, Norwegian labels, user story / requirements / evidence as context.
/// D — saving sends the plan's revision and only this case's edits; a stale save is refused and the edits stay in the form.
/// E — unsaved edits are never lost by opening another case.
/// F — review: "Klar for gjennomgang" and "Godkjenn" only for a complete case; a source conflict blocks approval.
/// G — the selection from Finn og forstå becomes cases (new plan or this plan); Oversett and Eksporter are the existing ones.
/// </summary>
public sealed partial class ManualTestingPlanTests : BunitContext
{
    private static readonly Guid PlanId = Guid.Parse("66666666-6666-6666-6666-666666666666");
    private readonly Mock<ITestPlanSuggestionsApiService> _api = new();

    private static readonly CoverageUserStory Story6 = new() { Key = "s6", StoryId = "US6", Title = "User Story 6 - Submit an attendance report (Priority: P1)" };
    private static readonly CoverageUserStory TechStory = new() { Key = "s9", StoryId = "US2", Title = "User Story 2 - Developers read the code without Norwegian knowledge (Priority: P1)" };

    private static TestCaseDraft Case(string id, string title, string? expected = "The report is stored", string capability = ScenarioOwnership.Specification,
        ScenarioCandidateType type = ScenarioCandidateType.BusinessFunctional, CoverageUserStory? story = null, TestCaseReviewState review = TestCaseReviewState.Generated,
        ScenarioSourceKind kind = ScenarioSourceKind.ProjectDiscovery, string? scenarioKey = null, List<string>? steps = null, CaseSourceState? source = null) => new()
    {
        Source = new ScenarioReference { Kind = kind, Owner = "library", ScenarioId = id },
        SuiteId = "suite-1",
        ScenarioKey = scenarioKey,
        Review = review,
        SourceState = source,
        Generated = new TestCaseContent
        {
            Title = title, Purpose = "Check " + title, Preconditions = "A valid school", Steps = steps ?? ["Open the form", "Submit the report"], StepExpected = ["The form opens", null],
            ExpectedResult = expected, Capability = capability, CandidateType = type, Story = story,
            Evidence = [new CandidateEvidenceRef("requirement", "FR-001"), new CandidateEvidenceRef("specification", "specs/001/spec.md")],
        },
    };

    private static TestPlanDraft Plan(params TestCaseDraft[] cases) => new()
    {
        Id = PlanId, GuideKey = "project:library", ProjectKey = "library", ProjectName = "Library", PlanName = "Library – test plan", Revision = 3,
        Suites = [new TestSuiteDraft("suite-1", "Intake", "project", null)], Cases = [.. cases],
    };

    private static TestPlanDraft Standard() => Plan(
        Case("c1", "Submit a valid report", story: Story6),
        Case("m1", "Submit a report twice", kind: ScenarioSourceKind.Manual, scenarioKey: "c1"),
        Case("api1", "GET /reports", capability: ScenarioOwnership.ApiContract, type: ScenarioCandidateType.ApiContract),
        Case("t1", "Identifiers are English", story: TechStory));

    private void Setup(TestPlanDraft? plan = null, string[]? selection = null, bool canWrite = true, bool archived = false)
    {
        plan ??= Standard();
        var summary = new TestPlanDraftSummary(PlanId, "project:library", plan.PlanName, null, plan.Revision, plan.Cases.Count, 0, 0, 0, 0, 0, "anna", DateTimeOffset.UtcNow)
            { Lifecycle = archived ? TestPlanLifecycle.Archived : TestPlanLifecycle.Active };
        Services.AddSingleton(BirkNext.Web.Tests.Services.WorkspaceSnapshots.Projection(BirkNext.Web.Tests.Services.WorkspaceSnapshots.Loaded("Imported", "library", "Library")).Object);
        _api.Setup(x => x.ProjectAsync("library", It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync((new TestPlanWorkspace
        {
            ProjectKey = "library", GuideKey = "project:library", CanWrite = canWrite,
            Drafts = archived ? [] : [summary], ArchivedDrafts = archived ? [summary] : [],
            TestData = [new TestDataOption("asset-1", "gyldig-rapport", "Gyldig rapport", "Json", "Approved", "Valid", null, null, null, null)],
            PlanStatus = new() { [PlanId] = new PlanSourceStatus { State = "Må gjennomgås", Message = "1 scenario er endret i kilden.", Changed = 1 } },
        }, (string?)null));
        _api.Setup(x => x.LastDiscoveryAsync("library", It.IsAny<Guid?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((new ScenarioDiscoveryResult { DiscoveryId = "d1", ProjectKey = "library", Candidates = [new DiscoveredScenario { CandidateId = "c7", ProjectKey = "library", Title = "Scenario 7" }] }, (string?)null));
        _api.Setup(x => x.DraftAsync(PlanId, It.IsAny<CancellationToken>())).ReturnsAsync((plan, (string?)null));
        _api.Setup(x => x.TranslationAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new TranslationAvailability(true, "offline", ""));
        Services.AddSingleton(_api.Object);
        JSInterop.Mode = JSRuntimeMode.Loose;
        if (selection is not null)
            JSInterop.Setup<string?>("sessionStorage.getItem", TabDraftStore.SelectionKey("library")).SetResult(System.Text.Json.JsonSerializer.Serialize(selection));
    }

    private IRenderedComponent<ManualTestingPlan> Open(string url = "manual-testing/plan")
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo(url);
        var cut = Render<ManualTestingPlan>();
        cut.WaitForAssertion(() => cut.Find("[aria-busy=false]"));
        return cut;
    }

    // ── A: editor working copy ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_an_unchanged_form_produces_no_edits_and_a_changed_one_only_the_changed_fields()
    {
        var c = Case("c1", "Submit a valid report") with { Edits = new TestCaseEdits { TestType = TestCaseType.Functional } };
        var form = P.CaseForm.From(c);

        form.IsDirty(c).Should().BeFalse();
        form.ToEdits(c).Should().BeEquivalentTo(new TestCaseEdits { TestType = TestCaseType.Functional }, "fields the form does not show are kept; nothing else becomes an edit");

        form.Title = "Send inn en gyldig rapport";
        form.Steps[1].Expected = "Rapporten lagres";
        form.IsDirty(c).Should().BeTrue();
        var edits = form.ToEdits(c);
        edits.Title.Should().Be("Send inn en gyldig rapport");
        edits.Steps.Should().Equal("Open the form", "Submit the report");
        edits.StepExpected.Should().Equal("The form opens", "Rapporten lagres");
        edits.Preconditions.Should().BeNull("unchanged = the generated value");
        edits.TestType.Should().Be(TestCaseType.Functional);
    }

    [Fact]
    public void A_steps_are_added_edited_removed_and_reordered_and_empty_rows_are_never_saved()
    {
        var c = Case("c1", "Submit a valid report");
        var form = P.CaseForm.From(c);
        form.AddStep();
        form.Steps[2].Action = "Check the receipt";
        form.Steps[2].Expected = "A receipt is shown";
        form.AddStep(); // left empty
        form.MoveStep(2, -1);
        form.MoveStep(0, -1); // past the top: nothing
        form.RemoveStep(0);

        var edits = form.ToEdits(c);
        edits.Steps.Should().Equal("Check the receipt", "Submit the report");
        edits.StepExpected.Should().Equal("A receipt is shown", null);

        var back = P.CaseForm.From(c with { Edits = edits });
        back.Steps.Select(s => s.Action).Should().Equal("Check the receipt", "Submit the report");
        back.IsDirty(c with { Edits = edits }).Should().BeFalse("the saved edit reads back as the form");
    }

    // ── B: technical content ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void B_technical_scenarios_and_stories_are_hidden_by_default_but_never_what_needs_a_person()
    {
        P.IsTechnicalStory(TechStory).Should().BeTrue();
        P.IsTechnicalStory(Story6).Should().BeFalse();
        P.IsTechnicalText("User Story 1 – Translate Non-Domain Identifiers to English").Should().BeTrue();
        P.IsTechnicalText("All Tests Pass After Translation").Should().BeTrue();
        P.IsTechnicalText("Lærer registrerer fravær for en elev").Should().BeFalse();

        var plan = Standard();
        var (rows, hidden) = P.Rows(plan, showTechnical: false);
        rows.SelectMany(r => r.Cases).Select(P.IdOf).Should().Equal("c1", "m1");
        hidden.Should().Be(2);
        P.Rows(plan, showTechnical: true).Rows.SelectMany(r => r.Cases).Should().HaveCount(4);

        var approved = Case("api2", "GET /reports/{id}", capability: ScenarioOwnership.ApiContract, type: ScenarioCandidateType.ApiContract, review: TestCaseReviewState.Accepted);
        var conflict = Case("t2", "Identifiers are English", story: TechStory) with
        {
            Generated = Case("t2", "Identifiers are English", story: TechStory).Generated with { Conflicts = [new ScenarioConflict()] },
        };
        var edited = Case("api3", "DELETE /reports", capability: ScenarioOwnership.ApiContract, type: ScenarioCandidateType.ApiContract) with { Edits = new TestCaseEdits { Title = "Slett rapport" } };
        P.TechnicalReason(approved).Should().BeNull("an approved case is never hidden");
        P.TechnicalReason(conflict).Should().BeNull("a source conflict needs a person");
        P.TechnicalReason(edited).Should().BeNull("a person already worked on it");
    }

    [Fact]
    public void B_search_finds_scenarios_by_title_story_and_requirement()
    {
        var plan = Standard();
        P.Rows(plan, false, "US6").Rows.Select(r => r.Key).Should().Equal("c1");
        P.Rows(plan, false, "fr-001").Rows.Should().HaveCount(1);
        P.Rows(plan, false, "nothing like this").Rows.Should().BeEmpty();
    }

    // ── C: master-detail page ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void C_the_page_lists_scenarios_with_several_cases_and_shows_the_first_case_with_its_context_in_Norwegian()
    {
        Setup();
        var cut = Open();

        cut.FindAll("[data-testid=twh-stage]").Select(a => a.TextContent.Trim()).Should().Contain(t => t.Contains("Finn og forstå")).And.Contain(t => t.Contains("Planlegg og klargjør"));
        cut.Find("[data-testid=twh-stage][data-stage=plan]").GetAttribute("aria-current").Should().Be("page");
        var scenario = cut.Find("[data-testid=mtp-scenario]");
        scenario.QuerySelectorAll("[data-testid=mtp-case]").Should().HaveCount(2, "the scenario's own case and one written under it");
        scenario.TextContent.Should().Contain("US6");
        cut.Find("[data-testid=mtp-hidden]").TextContent.Should().Contain("2 tekniske testtilfeller skjult");
        cut.Find("[data-testid=mtp-title]").GetAttribute("value").Should().Be("Submit a valid report");
        cut.FindAll("[data-testid=mtp-step]").Should().HaveCount(2);
        cut.Find("[data-testid=mtp-detail-state]").TextContent.Should().Be("Utkast");
        cut.Find("[data-testid=mtp-ctx-story]").TextContent.Should().Contain("US6");
        cut.Find("[data-testid=mtp-ctx-requirements]").TextContent.Should().Contain("FR-001");
        cut.Find("[data-testid=mtp-ctx-evidence]").TextContent.Should().Contain("Spesifikasjon");
        cut.Find("[data-testid=mtp-source-changed]").TextContent.Should().Contain("Kildegrunnlaget er endret");
        cut.Markup.Should().Contain("Teststeg").And.Contain("Forventet resultat").And.Contain("Forutsetninger");

        cut.Find("[data-testid=mtp-technical]").Change(true);
        cut.FindAll("[data-testid=mtp-case]").Should().HaveCount(4, "Vis teknisk innhold shows everything");
    }

    // ── D: save ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void D_saving_sends_the_revision_and_only_this_cases_edits_and_a_stale_save_keeps_the_form()
    {
        Setup();
        SaveTestPlanDraftRequest? sent = null;
        _api.Setup(x => x.SaveAsync(PlanId, It.IsAny<SaveTestPlanDraftRequest>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, SaveTestPlanDraftRequest, CancellationToken>((_, r, _) => sent = r)
            .ReturnsAsync(((TestPlanDraft?)null, (string?)"The proposal was changed by someone else. Reload it; your change was not saved."));
        var cut = Open();

        cut.Find("[data-testid=mtp-save]").HasAttribute("disabled").Should().BeTrue("nothing to save");
        cut.Find("[data-testid=mtp-title]").Input("Send inn en gyldig rapport");
        cut.Find("[data-testid=mtp-step-add]").Click();
        cut.FindAll("[data-testid=mtp-step-action]")[2].Input("Kontroller kvitteringen");
        cut.FindAll("[data-testid=mtp-step-expected]")[2].Input("Kvittering vises");
        cut.Find("[data-testid=mtp-save]").Click();

        sent.Should().NotBeNull();
        sent!.BaseRevision.Should().Be(3);
        var mine = sent.Cases.Single(c => c.ScenarioId == "c1").Edits;
        mine.Title.Should().Be("Send inn en gyldig rapport");
        mine.Steps.Should().Equal("Open the form", "Submit the report", "Kontroller kvitteringen");
        mine.StepExpected.Should().Equal("The form opens", null, "Kvittering vises");
        sent.Cases.Where(c => c.ScenarioId != "c1").Should().OnlyContain(c => c.Edits.IsEmpty, "other cases are sent as stored");
        cut.Find("[data-testid=mtp-error]").TextContent.Should().Contain("endret et annet sted");
        cut.Find("[data-testid=mtp-title]").GetAttribute("value").Should().Be("Send inn en gyldig rapport", "a refused save never throws the tester's work away");
    }

    [Fact]
    public void D_a_saved_case_reads_back_from_the_server()
    {
        Setup();
        _api.Setup(x => x.SaveAsync(PlanId, It.IsAny<SaveTestPlanDraftRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, SaveTestPlanDraftRequest r, CancellationToken _) =>
                (Standard() with { Revision = 4, Cases = [.. Standard().Cases.Select(c => c.Source.ScenarioId == "c1" ? c with { Edits = r.Cases.Single(x => x.ScenarioId == "c1").Edits } : c)] }, (string?)null));
        var cut = Open();
        cut.Find("[data-testid=mtp-expected]").Input("Rapporten er lagret");
        cut.Find("[data-testid=mtp-save]").Click();

        cut.Find("[data-testid=mtp-notice]").TextContent.Should().Be("Lagret.");
        cut.Find("[data-testid=mtp-save]").HasAttribute("disabled").Should().BeTrue("the form equals the saved case");
        cut.Find("[data-testid=mtp-plan-counts]").TextContent.Should().Contain("revisjon 4");
    }

    // ── E: unsaved protection ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void E_opening_another_case_with_unsaved_edits_asks_first_and_discarding_continues()
    {
        Setup();
        var cut = Open();
        cut.Find("[data-testid=mtp-title]").Input("Endret tittel");
        cut.Find("[data-testid=mtp-case][data-case=m1]").Click();

        cut.Find("[data-testid=mtp-unsaved]").TextContent.Should().Contain("ulagrede endringer");
        cut.Find("[data-testid=mtp-title]").GetAttribute("value").Should().Be("Endret tittel", "still on the edited case");
        cut.Find("[data-testid=mtp-unsaved-cancel]").Click();
        cut.FindAll("[data-testid=mtp-unsaved]").Should().BeEmpty();
        cut.Find("[data-testid=mtp-title]").GetAttribute("value").Should().Be("Endret tittel");

        cut.Find("[data-testid=mtp-case][data-case=m1]").Click();
        cut.Find("[data-testid=mtp-unsaved-discard]").Click();
        cut.Find("[data-testid=mtp-title]").GetAttribute("value").Should().Be("Submit a report twice");
        _api.Verify(x => x.SaveAsync(It.IsAny<Guid>(), It.IsAny<SaveTestPlanDraftRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void E_navigating_away_with_unsaved_edits_is_held_until_the_tester_decides()
    {
        Setup();
        var cut = Open();
        var nav = Services.GetRequiredService<NavigationManager>();
        cut.Find("[data-testid=mtp-title]").Input("Endret tittel");

        cut.InvokeAsync(() => nav.NavigateTo("manual-testing"));

        cut.WaitForAssertion(() => cut.Find("[data-testid=mtp-unsaved]"));
        nav.Uri.Should().EndWith("manual-testing/plan");
        cut.Find("[data-testid=mtp-unsaved-discard]").Click();
        nav.Uri.Should().EndWith("/manual-testing");
    }

    // ── F: review ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void F_ready_for_review_and_approve_change_the_state_and_an_incomplete_or_conflicting_case_cannot_be_approved()
    {
        var incomplete = Case("c2", "Lock the period", expected: null) with { Generated = Case("c2", "Lock the period", expected: null).Generated with { StepExpected = [] } };
        var conflicting = Case("c3", "Reject a closed period") with { Generated = Case("c3", "Reject a closed period").Generated with { Conflicts = [new ScenarioConflict()] } };
        Setup(Plan(Case("c1", "Submit a valid report", story: Story6), incomplete, conflicting));
        _api.Setup(x => x.ReviewAsync(PlanId, It.IsAny<ReviewTestCaseRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, ReviewTestCaseRequest r, CancellationToken _) => (Plan(Case("c1", "Submit a valid report", story: Story6, review: r.State), incomplete, conflicting) with { Revision = 4 }, (string?)null));
        var cut = Open();

        cut.Find("[data-testid=mtp-ready]").Click();
        _api.Verify(x => x.ReviewAsync(PlanId, new ReviewTestCaseRequest(3, "c1", TestCaseReviewState.UnderReview, null), It.IsAny<CancellationToken>()));
        cut.Find("[data-testid=mtp-detail-state]").TextContent.Should().Be("Klar for gjennomgang");
        cut.Find("[data-testid=mtp-approve]").Click();
        cut.Find("[data-testid=mtp-detail-state]").TextContent.Should().Be("Godkjent");

        cut.Find("[data-testid=mtp-case][data-case=c2]").Click();
        cut.Find("[data-testid=mtp-approve]").HasAttribute("disabled").Should().BeTrue();
        cut.Find("[data-testid=mtp-problems]").TextContent.Should().Contain("Skriv forventet resultat");

        cut.Find("[data-testid=mtp-case][data-case=c3]").Click();
        cut.Find("[data-testid=mtp-conflict]").TextContent.Should().Contain("Kildene er uenige");
        cut.Find("[data-testid=mtp-approve]").HasAttribute("disabled").Should().BeTrue("an unresolved conflict blocks approval");
    }

    [Fact]
    public void F_a_reader_without_write_access_sees_the_case_but_cannot_change_it()
    {
        Setup(canWrite: false);
        var cut = Open();
        cut.Find("[data-testid=mtp-title]").HasAttribute("disabled").Should().BeTrue();
        cut.FindAll("[data-testid=mtp-actions]").Should().BeEmpty();
        cut.FindAll("[data-testid=mtp-add-case]").Should().BeEmpty();
    }

    // ── G: Discover → plan; translate; export ────────────────────────────────────────────────────────────────

    [Fact]
    public void G_the_selection_from_Finn_og_forsta_is_added_to_this_plan_and_then_cleared()
    {
        Setup(selection: ["c1", "c7"]);
        CreateProjectTestPlanRequest? sent = null;
        _api.Setup(x => x.CreateProjectAsync(It.IsAny<CreateProjectTestPlanRequest>(), It.IsAny<CancellationToken>()))
            .Callback<CreateProjectTestPlanRequest, CancellationToken>((r, _) => sent = r)
            .ReturnsAsync((Plan([.. Standard().Cases, Case("c7", "Scenario 7")]) with { Revision = 4 }, (string?)null));
        var cut = Open();

        cut.Find("[data-testid=mtp-selection]").TextContent.Should().Contain("1 scenario valgt", "c1 is already in the plan");
        cut.Find("[data-testid=mtp-selection-add]").Click();

        sent.Should().NotBeNull();
        sent!.CandidateIds.Should().Equal("c7");
        sent.DraftId.Should().Be(PlanId);
        sent.BaseRevision.Should().Be(3);
        sent.DiscoveryId.Should().Be("d1");
        JSInterop.Invocations.Should().Contain(i => i.Identifier == "sessionStorage.removeItem" && i.Arguments[0]!.ToString() == TabDraftStore.SelectionKey("library"));
        cut.FindAll("[data-testid=mtp-selection]").Should().BeEmpty();
        cut.Find("[data-testid=mtp-notice]").TextContent.Should().Contain("lagt til");
    }

    [Fact]
    public void G_translate_uses_the_existing_translation_and_export_opens_the_existing_export()
    {
        Setup();
        _api.Setup(x => x.TranslateAsync(PlanId, It.IsAny<TranslateScenarioRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((new TranslateScenarioResult(Standard() with { Revision = 4 }, ["c1"], [], [], "Oversatt 1 testtilfelle."), (string?)null));
        var cut = Open();

        cut.Find("[data-testid=mtp-advanced]").GetAttribute("href").Should().Be($"test-plan-suggestions?draft={PlanId}&advanced=1");
        cut.FindAll("[data-testid=mtp-tool][data-tool=export]").Should().BeEmpty("the transfer lives on the Testplaner tab");
        cut.Find("[data-testid=mtp-manage-plans]").GetAttribute("href").Should().Be("manual-testing/plans");
        cut.Find("[data-testid=mtp-transfer]").Click();
        Services.GetRequiredService<NavigationManager>().Uri.Should().EndWith($"manual-testing/plans?transfer={PlanId}");
        cut.Find("[data-testid=mtp-translate]").Click();

        _api.Verify(x => x.TranslateAsync(PlanId, new TranslateScenarioRequest(3, "c1", false, ScenarioPlanning.Norwegian), It.IsAny<CancellationToken>()));
        cut.Find("[data-testid=mtp-notice]").TextContent.Should().Be("Oversatt 1 testtilfelle.");

        cut.Find("[data-testid=mtp-title]").Input("Endret");
        cut.Find("[data-testid=mtp-translate]").HasAttribute("disabled").Should().BeTrue("unsaved edits are saved before translating");
    }

    [Fact]
    public void G_adding_a_case_under_a_scenario_opens_it_in_the_editor()
    {
        Setup();
        _api.Setup(x => x.AddManualCaseAsync(PlanId, It.IsAny<AddManualTestCaseRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Plan([.. Standard().Cases, Case("m2", "Nytt testtilfelle: Submit valid report", kind: ScenarioSourceKind.Manual, scenarioKey: "c1", steps: [])]) with { Revision = 4 }, (string?)null));
        var cut = Open();

        cut.Find("[data-testid=mtp-add-case]").Click();
        // 2026-11-25: a short form first — the type is chosen before the case exists.
        _api.Verify(x => x.AddManualCaseAsync(It.IsAny<Guid>(), It.IsAny<AddManualTestCaseRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        cut.Find("[data-testid=mtp-new-kind-option][data-kind=Manual]").Click();
        cut.Find("[data-testid=mtp-new-create]").Click();

        _api.Verify(x => x.AddManualCaseAsync(PlanId, It.Is<AddManualTestCaseRequest>(r => r.ScenarioKey == "c1" && r.BaseRevision == 3 && r.TestKind == ScenarioTestKind.Manual), It.IsAny<CancellationToken>()));
        cut.Find("[data-testid=mtp-title]").GetAttribute("value").Should().StartWith("Nytt testtilfelle");
        cut.Find("[data-testid=mtp-steps-none]").TextContent.Should().Contain("Ingen teststeg");
        cut.Find("[data-testid=mtp-scenario]").QuerySelectorAll("[data-testid=mtp-case]").Should().HaveCount(3, "several cases under one scenario");
    }

    // ── H–N: plan lifecycle, tools, translation review, Test Data, conflicts, exclusion (2026-11-23) ─────────────────────────────────

    [Fact]
    public void H_archiving_asks_first_and_the_archived_plan_becomes_read_only_and_can_be_restored()
    {
        Setup();
        _api.Setup(x => x.ArchiveAsync(PlanId, It.IsAny<TestPlanLifecycleRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((new TestPlanDraftSummary(PlanId, "project:library", "Library – test plan", null, 4, 4, 0, 0, 0, 0, 0, "anna", DateTimeOffset.UtcNow) { Lifecycle = TestPlanLifecycle.Archived }, (string?)null));
        _api.Setup(x => x.UnarchiveAsync(PlanId, It.IsAny<TestPlanLifecycleRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((new TestPlanDraftSummary(PlanId, "project:library", "Library – test plan", null, 5, 4, 0, 0, 0, 0, 0, "anna", DateTimeOffset.UtcNow), (string?)null));
        var cut = Open();

        cut.Find("[data-testid=mtp-archive]").Click();
        _api.Verify(x => x.ArchiveAsync(It.IsAny<Guid>(), It.IsAny<TestPlanLifecycleRequest>(), It.IsAny<CancellationToken>()), Times.Never, "archiving asks first");
        cut.Find("[data-testid=mtp-archive-confirm]").TextContent.Should().Contain("Ingenting slettes");
        cut.Find("[data-testid=mtp-archive-yes]").Click();

        _api.Verify(x => x.ArchiveAsync(PlanId, new TestPlanLifecycleRequest(3, null), It.IsAny<CancellationToken>()));
        cut.Find("[data-testid=mtp-archived]").TextContent.Should().Contain("arkivert");
        cut.Find("[data-testid=mtp-title]").HasAttribute("disabled").Should().BeTrue("an archived plan is read-only");
        cut.FindAll("[data-testid=mtp-actions]").Should().BeEmpty();
        cut.Find("[data-testid=mtp-unarchive]").Click();
        _api.Verify(x => x.UnarchiveAsync(PlanId, new TestPlanLifecycleRequest(4, null), It.IsAny<CancellationToken>()));
    }

    [Fact]
    public void H_an_archived_plan_opened_by_link_is_shown_read_only()
    {
        Setup(archived: true);
        var cut = Open($"manual-testing/plan?draft={PlanId}");
        cut.Find("[data-testid=mtp-archived]").Should().NotBeNull();
        cut.Find("[data-testid=mtp-title]").HasAttribute("disabled").Should().BeTrue();
        cut.FindAll("[data-testid=mtp-add-case]").Should().BeEmpty();
        cut.FindAll("[data-testid=mtp-translate-all]").Should().BeEmpty();
    }

    [Fact]
    public void I_the_versions_tool_lists_revisions_and_restores_one_at_the_plan_revision()
    {
        Setup();
        _api.Setup(x => x.RevisionsAsync(PlanId, It.IsAny<CancellationToken>())).ReturnsAsync((new List<TestPlanRevisionSummary>
        {
            new(3, "Saved", "anna", DateTimeOffset.UtcNow, "Library – test plan", 4, true), new(1, "Created", "anna", DateTimeOffset.UtcNow.AddDays(-1), "Library – test plan", 2, false),
        }, (string?)null));
        _api.Setup(x => x.RestoreRevisionAsync(PlanId, 3, 1, It.IsAny<CancellationToken>())).ReturnsAsync((Standard() with { Revision = 4 }, (string?)null));
        var cut = Open();

        cut.Find("[data-testid=mtp-tool][data-tool=versions]").Click();
        cut.FindAll("[data-testid=mtp-version]").Should().HaveCount(2);
        cut.Find("[data-testid=mtp-version][data-revision='1'] [data-testid=mtp-version-restore]").Click();
        _api.Verify(x => x.RestoreRevisionAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never, "restoring asks first");
        cut.Find("[data-testid=mtp-version-confirm]").TextContent.Should().Contain("ny gjeldende versjon").And.Contain("Ingen versjoner slettes");
        cut.Find("[data-testid=mtp-version-restore-yes]").Click();

        _api.Verify(x => x.RestoreRevisionAsync(PlanId, 3, 1, It.IsAny<CancellationToken>()));
        cut.Find("[data-testid=mtp-notice]").TextContent.Should().Contain("revisjon 4");
    }

    [Fact]
    public void I_the_source_changes_tool_shows_the_impact_and_refreshes_without_touching_edits()
    {
        Setup();
        _api.Setup(x => x.ImpactAsync(PlanId, It.IsAny<CancellationToken>())).ReturnsAsync((new PlanImpactAnalysis
        {
            DraftId = PlanId, PlanRevision = 3, Summary = "1 testtilfelle er endret i kilden.",
            Cases = [new CaseImpact { CaseId = "c1", Title = "Submit a valid report", Kinds = [CaseImpactKind.ExpectedResultChanged], ReviewedBefore = true,
                Changes = [new CaseImpactChange("Forventet resultat", "200", "201")] }],
        }, (string?)null));
        _api.Setup(x => x.RefreshAsync(PlanId, 3, It.IsAny<CancellationToken>())).ReturnsAsync((Standard() with { Revision = 4 }, (string?)null));
        var cut = Open();

        cut.Find("[data-testid=mtp-source-changed] button").Click();
        cut.Find("[data-testid=mtp-changes-summary]").TextContent.Should().Contain("endret i kilden");
        cut.Find("[data-testid=mtp-change]").TextContent.Should().Contain("forventet resultat endret").And.Contain("var godkjent");
        cut.Find("[data-testid=mtp-refresh]").Click();
        _api.Verify(x => x.RefreshAsync(PlanId, 3, It.IsAny<CancellationToken>()));
    }

    [Fact]
    public void J_a_translation_is_reviewed_in_the_editor_its_state_is_shown_and_edits_are_saved_with_the_case()
    {
        var original = Case("c1", "Submit a valid report", story: Story6);
        var translated = original.WithTranslation(ScenarioPlanning.Norwegian, new TestCaseTranslation
        {
            Title = "Send inn en gyldig rapport", Steps = ["Åpne skjemaet", "Send inn rapporten"], StepExpected = ["Skjemaet vises", null], ExpectedResult = "Rapporten lagres",
            SourceFingerprint = ScenarioPlanning.SourceFingerprint(original), Origin = TranslationOrigin.Machine, Provider = "offline",
        });
        Setup(Plan(translated));
        SaveTestPlanDraftRequest? sent = null;
        _api.Setup(x => x.SaveAsync(PlanId, It.IsAny<SaveTestPlanDraftRequest>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, SaveTestPlanDraftRequest, CancellationToken>((_, r, _) => sent = r).ReturnsAsync((Plan(translated) with { Revision = 4 }, (string?)null));
        var cut = Open();

        cut.Find("[data-testid=mtp-translation-state]").TextContent.Should().Be("Maskinoversatt — ikke gjennomgått");
        cut.Find("[data-testid=mtp-tr-title]").GetAttribute("value").Should().Be("Send inn en gyldig rapport");
        cut.FindAll("[data-testid=mtp-tr-step-action]")[1].GetAttribute("value").Should().Be("Send inn rapporten");
        cut.Find("[data-testid=mtp-save]").HasAttribute("disabled").Should().BeTrue();
        cut.Find("[data-testid=mtp-tr-expected]").Input("Rapporten er lagret og kvittering vises");
        cut.Find("[data-testid=mtp-save]").Click();

        var nb = sent!.Cases.Single(c => c.ScenarioId == "c1").Translations![ScenarioPlanning.Norwegian];
        nb.ExpectedResult.Should().Be("Rapporten er lagret og kvittering vises");
        nb.Title.Should().Be("Send inn en gyldig rapport", "unchanged fields keep the reviewed text");
        sent.Cases.Single(c => c.ScenarioId == "c1").Edits.IsEmpty.Should().BeTrue("the original stays as it was");
    }

    [Fact]
    public void J_a_translation_made_from_an_older_original_says_so()
    {
        var original = Case("c1", "Submit a valid report");
        var stale = original.WithTranslation(ScenarioPlanning.Norwegian, new TestCaseTranslation { Title = "Gammel tittel", SourceFingerprint = "older", Origin = TranslationOrigin.MachineEdited });
        Setup(Plan(stale));
        var cut = Open();
        cut.Find("[data-testid=mtp-translation-state]").TextContent.Should().StartWith("Må gjennomgås");
    }

    [Fact]
    public void K_test_data_is_linked_from_the_catalogue_and_saved_by_asset_id()
    {
        Setup();
        SaveTestPlanDraftRequest? sent = null;
        _api.Setup(x => x.SaveAsync(PlanId, It.IsAny<SaveTestPlanDraftRequest>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, SaveTestPlanDraftRequest, CancellationToken>((_, r, _) => sent = r).ReturnsAsync((Standard() with { Revision = 4 }, (string?)null));
        var cut = Open();

        cut.Find("[data-testid=mtp-testdata-add]").Change("asset-1");
        cut.Find("[data-testid=mtp-testdata-link]").TextContent.Should().Contain("gyldig-rapport");
        cut.Find("[data-testid=mtp-save]").Click();

        sent!.Cases.Single(c => c.ScenarioId == "c1").TestDataAssetIds.Should().Equal("asset-1");
    }

    [Fact]
    public void L_a_source_conflict_is_resolved_in_the_editor_with_a_chosen_claim_and_a_reason()
    {
        var conflict = new ScenarioConflict { Claims = [new ScenarioClaim("cand-spec", "Spesifikasjon", "Status 200", "spec.md#3"), new ScenarioClaim("cand-guide", "Systemforståelse", "Status 503", "guide#2")] };
        var c3 = Case("c3", "Reject a closed period") with { Generated = Case("c3", "Reject a closed period").Generated with { Conflicts = [conflict] } };
        Setup(Plan(c3));
        _api.Setup(x => x.ResolveConflictAsync(PlanId, It.IsAny<ResolveConflictRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync((Plan(c3) with { Revision = 4 }, (string?)null));
        var cut = Open();

        cut.Find("[data-testid=mtp-case-conflict]").TextContent.Should().Be("Kildekonflikt");
        cut.Find("[data-testid=mtp-ctx-why]").TextContent.Should().Contain("uenige");
        cut.FindAll("[data-testid=mtp-claim]")[1].Change(true);
        cut.Find("[data-testid=mtp-conflict-resolve]").HasAttribute("disabled").Should().BeTrue("a reason is required");
        cut.Find("[data-testid=mtp-conflict-why]").Input("Tjenesten er nede i vedlikeholdsvinduet");
        cut.Find("[data-testid=mtp-conflict-resolve]").Click();

        _api.Verify(x => x.ResolveConflictAsync(PlanId, new ResolveConflictRequest(3, "c3", "Status 503", "cand-guide", "Tjenesten er nede i vedlikeholdsvinduet"), It.IsAny<CancellationToken>()));
    }

    [Fact]
    public void M_excluding_a_case_needs_a_reason_and_uses_the_review_state()
    {
        Setup();
        _api.Setup(x => x.ReviewAsync(PlanId, It.IsAny<ReviewTestCaseRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync((Standard() with { Revision = 4 }, (string?)null));
        var cut = Open();
        cut.Find("[data-testid=mtp-exclude]").Click();
        cut.Find("[data-testid=mtp-exclude-yes]").HasAttribute("disabled").Should().BeTrue();
        cut.Find("[data-testid=mtp-exclude-why]").Input("Dekket av en annen test");
        cut.Find("[data-testid=mtp-exclude-yes]").Click();
        _api.Verify(x => x.ReviewAsync(PlanId, new ReviewTestCaseRequest(3, "c1", TestCaseReviewState.Excluded, "Dekket av en annen test"), It.IsAny<CancellationToken>()));
    }

    [Fact]
    public void N_a_case_deep_link_opens_that_case_even_when_it_is_technical()
    {
        Setup();
        var cut = Open($"manual-testing/plan?draft={PlanId}&case=api1");
        cut.Find("[data-testid=mtp-title]").GetAttribute("value").Should().Be("GET /reports");
        cut.Find("[data-testid=mtp-technical]").HasAttribute("checked").Should().BeTrue();
    }

    [Theory]
    [InlineData(null, null, null, null, "manual-testing/plan")]
    [InlineData("66666666-6666-6666-6666-666666666666", "prepare", "c1", null, "manual-testing/plan?draft=66666666-6666-6666-6666-666666666666&case=c1")]
    [InlineData("66666666-6666-6666-6666-666666666666", "export", null, null, "manual-testing/plans?transfer=66666666-6666-6666-6666-666666666666")]
    [InlineData(null, "choose", null, null, "manual-testing/discover")]
    [InlineData("66666666-6666-6666-6666-666666666666", "prepare", null, "1", null)]
    public void N_old_plan_links_land_on_the_canonical_page_unless_the_advanced_tools_are_asked_for(string? draft, string? area, string? caseId, string? advanced, string? expected)
    {
        ManualTestingPresentation.CanonicalRoute(null, draft, area, caseId, advanced).Should().Be(expected);
        ManualTestingPresentation.CanonicalRoute("skolenaervaer", draft, area, caseId, null).Should().BeNull("guide-mode plans stay on the full page");
    }

    // ── O: US + AC badges in the left list (2026-11-25) ─────────────────────────────────────────────────────────

    private static readonly CoverageUserStory Story1 = new() { Key = "s1", StoryId = "US1", Title = "User Story 1 - Accept a report (Priority: P1)" };
    private static readonly CoverageUserStory Story2 = new() { Key = "s2", StoryId = "US2", Title = "User Story 2 - Protect a Kode 6/7 child (Priority: P1)" };

    /// <summary>The saved acceptance scenario number of a case (as parsed into the case when it was prepared).</summary>
    private static TestCaseDraft Numbered(TestCaseDraft c, int? n) => c with { Generated = c.Generated with { NumberInStory = n } };

    [Fact]
    public void O01_O05_each_scenario_row_shows_its_own_story_and_acceptance_scenario_from_the_saved_identity()
    {
        var plan = Plan(
            Numbered(Case("a1", "Valid report is accepted", story: Story1), 1),
            Numbered(Case("a2", "Stored data for a Kode 6/7 child is protected", story: Story2), 2),
            Numbered(Case("b2", "Kode 6/7 address is never shown", story: Story1), 2),   // AC 2 under US1: another scenario than US2/AC 2
            Numbered(Case("a3", "Report for a closed period is rejected", story: Story1), 3),
            Case("m1", "Accepted report twice", kind: ScenarioSourceKind.Manual, scenarioKey: "a1"));
        var rows = P.Rows(plan, true).Rows.ToDictionary(r => r.Key);
        (rows["a1"].StoryId, rows["a1"].Ac).Should().Be(("US1", "AC 1"));
        (rows["a2"].StoryId, rows["a2"].Ac).Should().Be(("US2", "AC 2"));
        (rows["b2"].StoryId, rows["b2"].Ac).Should().Be(("US1", "AC 2"), "the same AC number under another story is its own scenario");
        rows["a3"].Ac.Should().Be("AC 3", "different scenarios of one story keep their own numbers");
        rows["a1"].Cases.Should().HaveCount(2, "a case written under the scenario shares the scenario's US/AC row");
        rows.Values.Should().OnlyContain(r => !r.AcAmbiguous);
    }

    [Fact]
    public void O06_O09_no_number_is_ever_guessed_for_handwritten_missing_or_ambiguous_scenarios()
    {
        var plan = Plan(
            Case("h1", "Hand-written check", kind: ScenarioSourceKind.Manual),
            Case("n1", "Scenario without a number", story: Story1),
            Numbered(Case("x1", "Number under another story", story: Story1), 4) with { },
            Numbered(Case("p1", "Scenario with a number", story: Story2), 1));
        var x1 = plan.Cases.Single(c => c.Source.ScenarioId == "x1");
        plan = plan with { Cases = [.. plan.Cases.Where(c => c.Source.ScenarioId != "x1"), x1 with { Generated = x1.Generated with { Story = Story2 } }] };
        var rows = P.Rows(plan, true).Rows;
        rows.Single(r => r.Title == "Uten scenario" || r.Cases.Any(c => c.Source.ScenarioId == "h1")).Ac.Should().BeNull("a hand-written case has no acceptance scenario");
        rows.Single(r => r.Key == "n1").Should().Match<P.ScenarioRow>(r => r.Ac == null && !r.AcAmbiguous, "no number saved → no badge");
        rows.Single(r => r.Key == "p1").Ac.Should().Be("AC 1");
        // A row is one scenario (grouping by the source scenario id), so two numbers can only meet through inconsistent saved data: never chosen.
        var p1 = Numbered(Case("p1", "p1", story: Story2), 1);
        P.AcOf(p1, [p1, Numbered(Case("p1", "same scenario, other saved number", story: Story2), 3)], Story2).Should().Be(((string?)null, true),
            "two acceptance scenarios for one row: none is chosen");
        P.AcOf(Numbered(Case("q", "q", story: Story1), 2), [], Story2).Should().Be(((string?)null, true), "a number of another story is never shown under this one");
        P.AcOf(null, [Case("m", "m", kind: ScenarioSourceKind.Manual)], Story1).Should().Be(((string?)null, false));
    }

    [Fact]
    public void O10_O11_O18_the_badge_follows_the_saved_identity_through_reimport_translation_and_edits()
    {
        var original = Numbered(Case("a2", "Stored data for a Kode 6/7 child is protected", story: Story2), 2);
        var reimported = original with { Generated = original.Generated with { Title = "Stored data for a Kode 6/7 child stays protected" } };
        var edited = reimported with { Edits = reimported.Edits with { Title = "Eget navn på testtilfellet" } };
        foreach (var c in new[] { original, reimported, edited })
            P.Rows(Plan(c), true).Rows.Single().Ac.Should().Be("AC 2", "a new title, a translation or a manual edit never changes the acceptance scenario");
        P.Rows(Plan(original, Numbered(Case("new", "Inserted scenario", story: Story2), 1)), true).Rows.Single(r => r.Key == "a2").Ac
            .Should().Be("AC 2", "a scenario inserted before it does not shift its number");
        P.Rows(Plan(original, edited with { Source = original.Source with { ScenarioId = "copy" } }), true).Rows.Should().HaveCount(2, "rows are never merged or created");
    }

    [Fact]
    public void O12_O16_the_page_shows_US_and_AC_together_static_and_compact_and_search_still_works()
    {
        Setup(Plan(
            Numbered(Case("a1", "Valid report is accepted", story: Story1), 1),
            Numbered(Case("a2", "Stored data for a Kode 6/7 child is protected from everyone who has no need to know, also after a correction and a period lock", story: Story2), 2)));
        var cut = Open();
        var rows = cut.FindAll("[data-testid=mtp-scenario]");
        rows.Select(r => (r.QuerySelector("[data-testid=mtp-row-story]")!.TextContent, r.QuerySelector("[data-testid=mtp-row-ac]")!.TextContent))
            .Should().Equal(("US1", "AC 1"), ("US2", "AC 2"));
        var ac = cut.FindAll("[data-testid=mtp-row-ac]")[1];
        ac.TagName.Should().Be("SPAN", "a static label: «Vis scenarioet i Finn og forstå» in the detail opens the scenario");
        ac.GetAttribute("title").Should().Be("Akseptansescenario AC 2 i US2");
        ac.PreviousElementSibling!.GetAttribute("data-testid").Should().Be("mtp-row-story", "AC sits right after US");
        ac.NextElementSibling!.ClassList.Should().Contain("mtp-scenario-title", "no extra row for the badges");
        cut.Find("[data-testid=mtp-case][aria-current=true]").ClassList.Should().Contain("is-selected", "the selected case keeps its styling");
        cut.Find("[data-testid=mtp-scenario-heading]").TextContent.Should().StartWith("AC 1 —", "the list and the detail name the same acceptance scenario");

        P.Rows(Plan(Numbered(Case("a2", "x", story: Story2), 2)), true, "ac2").Rows.Should().ContainSingle("the badge text is searchable");
        P.Rows(Plan(Numbered(Case("a2", "x", story: Story2), 2)), true, "US2").Rows.Should().ContainSingle();
    }

    // ── P: «Kildegrunnlag» collapsed by default (2026-11-25) ─────────────────────────────────────────────────────

    [Fact]
    public void P_kildegrunnlag_starts_collapsed_expands_and_collapses_and_is_collapsed_again_for_another_case()
    {
        Setup();
        var cut = Open();
        var toggle = cut.Find("[data-testid=mtp-context-toggle]");
        toggle.GetAttribute("aria-expanded").Should().Be("false");
        toggle.GetAttribute("aria-controls").Should().Be("mtp-context-body");
        toggle.TextContent.Should().Be("Kildegrunnlag");
        cut.Find("[data-testid=mtp-context-body]").HasAttribute("hidden").Should().BeTrue("collapsed by default");
        cut.Find("[data-testid=mtp-ctx-requirements]").TextContent.Should().Contain("FR-001", "the content is unchanged, only hidden");

        toggle.Click();
        cut.Find("[data-testid=mtp-context-toggle]").GetAttribute("aria-expanded").Should().Be("true");
        cut.Find("[data-testid=mtp-context-body]").HasAttribute("hidden").Should().BeFalse();
        cut.Find("[data-testid=mtp-context-toggle]").Click();
        cut.Find("[data-testid=mtp-context-body]").HasAttribute("hidden").Should().BeTrue("a second click collapses it");

        cut.Find("[data-testid=mtp-context-toggle]").Click();
        cut.Find("[data-testid=mtp-technical]").Change(true);
        cut.FindAll("[data-testid=mtp-case]").Single(b => b.GetAttribute("data-case") == "t1").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=mtp-title]").GetAttribute("value").Should().Be("Identifiers are English"));
        cut.Find("[data-testid=mtp-context-toggle]").GetAttribute("aria-expanded").Should().Be("false", "another case or scenario starts collapsed");
        cut.Find("[data-testid=mtp-context-body]").HasAttribute("hidden").Should().BeTrue();
    }
}
