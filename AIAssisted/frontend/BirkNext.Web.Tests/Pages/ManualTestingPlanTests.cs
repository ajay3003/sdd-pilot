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
public sealed class ManualTestingPlanTests : BunitContext
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

    private void Setup(TestPlanDraft? plan = null, string[]? selection = null, bool canWrite = true)
    {
        plan ??= Standard();
        Services.AddSingleton(BirkNext.Web.Tests.Services.WorkspaceSnapshots.Projection(BirkNext.Web.Tests.Services.WorkspaceSnapshots.Loaded("Imported", "library", "Library")).Object);
        _api.Setup(x => x.ProjectAsync("library", It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync((new TestPlanWorkspace
        {
            ProjectKey = "library", GuideKey = "project:library", CanWrite = canWrite,
            Drafts = [new TestPlanDraftSummary(PlanId, "project:library", plan.PlanName, null, plan.Revision, plan.Cases.Count, 0, 0, 0, 0, 0, "anna", DateTimeOffset.UtcNow)],
            PlanStatus = new() { [PlanId] = new PlanSourceStatus { State = "Må gjennomgås", Message = "1 scenario er endret i kilden.", Changed = 1 } },
        }, (string?)null));
        _api.Setup(x => x.LastDiscoveryAsync("library", It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((new ScenarioDiscoveryResult { DiscoveryId = "d1", ProjectKey = "library", Candidates = [new DiscoveredScenario { CandidateId = "c7", ProjectKey = "library", Title = "Scenario 7" }] }, (string?)null));
        _api.Setup(x => x.DraftAsync(PlanId, It.IsAny<CancellationToken>())).ReturnsAsync((plan, (string?)null));
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
            .ReturnsAsync(((TestPlanDraft?)null, (string?)"409 draft-changed"));
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

        cut.Find("[data-testid=mtp-export]").GetAttribute("href").Should().Be($"test-plan-suggestions?draft={PlanId}&area=export");
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

        _api.Verify(x => x.AddManualCaseAsync(PlanId, It.Is<AddManualTestCaseRequest>(r => r.ScenarioKey == "c1" && r.BaseRevision == 3), It.IsAny<CancellationToken>()));
        cut.Find("[data-testid=mtp-title]").GetAttribute("value").Should().StartWith("Nytt testtilfelle");
        cut.Find("[data-testid=mtp-steps-none]").TextContent.Should().Contain("Ingen teststeg");
        cut.Find("[data-testid=mtp-scenario]").QuerySelectorAll("[data-testid=mtp-case]").Should().HaveCount(3, "several cases under one scenario");
    }
}
