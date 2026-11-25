using BirkNext.TestCoverage;
using BirkNext.TestPlanning;
using Bunit;
using FluentAssertions;
using Moq;
using P = BirkNext.Web.Services.PlanPreparePresentation;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// Testtype on a saved test case in Planlegg og klargjør (2026-11-25): Manuell or Teknisk is chosen when a case is created — under a scenario or
/// directly, without one — and changed in the editor with the normal explicit save. The scenario's approach from Finn og forstå is a suggestion
/// only (a reviewed decision may be preselected, an unreviewed recommendation never), an older case stays «Ikke angitt», and the case's own type
/// is what the list's technical filter reads. Never "automated", "passed" or a review state.
/// </summary>
public sealed partial class ManualTestingPlanTests
{
    private static readonly CoverageUserStory SpecStory = new() { Key = "s1", StoryId = "US1", Title = "User Story 1 - Accept a report", SpecificationPath = "specs/001/spec.md" };

    private static StoryGroupingDocument Decided(string scenarioId, ScenarioTestKind kind) =>
        StoryGroupingEngine.Apply(new StoryGroupingDocument { ProjectKey = "library", SpecificationPath = "specs/001/spec.md" },
            [new StoryGroupingOperation { Kind = StoryGroupingOperationKind.SetScenarioTestKind, ScenarioId = scenarioId, TestKind = kind }], "anna", DateTimeOffset.UtcNow, _ => true);

    private void AddReturns(TestPlanDraft after) =>
        _api.Setup(x => x.AddManualCaseAsync(PlanId, It.IsAny<AddManualTestCaseRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync((after, (string?)null));

    [Fact]
    public void K01_K03_K20_a_direct_case_offers_Manuell_and_Teknisk_requires_a_choice_and_nothing_is_created_before_Legg_til()
    {
        Setup();
        AddReturns(Plan([.. Standard().Cases, Case("m9", "Kontroller at rapporten kan sendes", kind: ScenarioSourceKind.Manual, steps: [])
            with { Edits = new TestCaseEdits { TestKind = ScenarioTestKind.Technical } }]) with { Revision = 4 });
        var cut = Open();

        cut.Find("[data-testid=mtp-new-case]").Click();
        cut.Find("[data-testid=mtp-new-context]").TextContent.Should().Contain("uten scenario");
        cut.FindAll("[data-testid=mtp-new-kind-option]").Select(b => b.TextContent).Should().Equal("Manuell", "Teknisk");
        cut.FindAll("[data-testid=mtp-new-kind-option]").Should().OnlyContain(b => b.GetAttribute("aria-checked") == "false", "no type is chosen for the tester");
        cut.Find("[data-testid=mtp-new-create]").HasAttribute("disabled").Should().BeTrue("a title and a type are needed");

        cut.Find("[data-testid=mtp-new-title-input]").Input("Kontroller at rapporten kan sendes");
        cut.Find("[data-testid=mtp-new-create]").HasAttribute("disabled").Should().BeTrue("still no type");
        cut.Find("[data-testid=mtp-new-kind-option][data-kind=Technical]").Click();
        _api.Verify(x => x.AddManualCaseAsync(It.IsAny<Guid>(), It.IsAny<AddManualTestCaseRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        cut.Find("[data-testid=mtp-new-create]").Click();

        _api.Verify(x => x.AddManualCaseAsync(PlanId, It.Is<AddManualTestCaseRequest>(r => r.ScenarioKey == null && r.Title == "Kontroller at rapporten kan sendes"
            && r.TestKind == ScenarioTestKind.Technical), It.IsAny<CancellationToken>()), Times.Once);
        cut.FindAll("[data-testid=mtp-new]").Should().BeEmpty();
        cut.Find("[data-testid=mtp-kind-option][data-kind=Technical]").GetAttribute("aria-checked").Should().Be("true", "the new case opens with its type");
    }

    [Fact]
    public void K04_K05_K07_an_unreviewed_scenario_only_recommends_and_a_reviewed_one_is_preselected_but_can_be_overridden()
    {
        Setup(Plan(Case("c1", "Submit a valid report", story: SpecStory), Case("c2", "Reject an invalid report", story: SpecStory)));
        _api.Setup(x => x.StoryGroupingAsync("library", "specs/001/spec.md", It.IsAny<CancellationToken>())).ReturnsAsync((Decided("c2", ScenarioTestKind.Technical), (string?)null));
        AddReturns(Plan(Case("c1", "Submit a valid report", story: SpecStory), Case("c2", "Reject an invalid report", story: SpecStory)) with { Revision = 4 });
        var cut = Open();

        cut.FindAll("[data-testid=mtp-add-case]")[0].Click(); // c1: no reviewed decision
        cut.FindAll("[data-testid=mtp-new-kind-option]").Should().OnlyContain(b => b.GetAttribute("aria-checked") == "false", "an unreviewed recommendation is never a choice");
        cut.Find("[data-testid=mtp-new-kind-hint]").TextContent.Should().StartWith("Anbefalt for scenarioet:");
        cut.Find("[data-testid=mtp-new-cancel]").Click();

        cut.FindAll("[data-testid=mtp-add-case]")[1].Click(); // c2: reviewed Teknisk in Finn og forstå
        cut.Find("[data-testid=mtp-new-kind-option][data-kind=Technical]").GetAttribute("aria-checked").Should().Be("true");
        cut.Find("[data-testid=mtp-new-kind-hint]").TextContent.Should().Contain("vurdert som teknisk").And.Contain("anna").And.Contain("du velger");
        cut.Find("[data-testid=mtp-new-kind-option][data-kind=Manual]").Click();
        cut.Find("[data-testid=mtp-new-create]").Click();

        _api.Verify(x => x.AddManualCaseAsync(PlanId, It.Is<AddManualTestCaseRequest>(r => r.ScenarioKey == "c2" && r.TestKind == ScenarioTestKind.Manual), It.IsAny<CancellationToken>()));
        _api.Verify(x => x.ApplyStoryGroupingAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<StoryGroupingChangeRequest>(), It.IsAny<CancellationToken>()), Times.Never,
            "choosing a case type never changes the scenario's decision");
    }

    [Fact]
    public void K08_K11_K12_changing_only_the_type_is_an_explicit_save_of_that_field_and_every_other_field_is_sent_as_it_was()
    {
        var c1 = Case("c1", "Submit a valid report", story: Story6) with { Edits = new TestCaseEdits { Title = "Send inn en gyldig rapport", Priority = "P1" } };
        Setup(Plan(c1));
        SaveTestPlanDraftRequest? sent = null;
        _api.Setup(x => x.SaveAsync(PlanId, It.IsAny<SaveTestPlanDraftRequest>(), It.IsAny<CancellationToken>()))
            .Callback((Guid _, SaveTestPlanDraftRequest r, CancellationToken _) => sent = r)
            .ReturnsAsync((Guid _, SaveTestPlanDraftRequest r, CancellationToken _) => (Plan(c1 with { Edits = r.Cases.Single().Edits }) with { Revision = 4 }, (string?)null));
        var cut = Open();

        cut.Find("[data-testid=mtp-kind-unset]").TextContent.Should().Be("Ikke angitt");
        cut.Find("[data-testid=mtp-save]").HasAttribute("disabled").Should().BeTrue();
        cut.Find("[data-testid=mtp-kind-option][data-kind=Technical]").Click();
        _api.Verify(x => x.SaveAsync(It.IsAny<Guid>(), It.IsAny<SaveTestPlanDraftRequest>(), It.IsAny<CancellationToken>()), Times.Never, "no silent auto-save");
        cut.Find("[data-testid=mtp-save]").Click();

        sent!.Cases.Single().Edits.Should().BeEquivalentTo(c1.Edits with { TestKind = ScenarioTestKind.Technical }, "only the type changed");
        cut.Find("[data-testid=mtp-kind-option][data-kind=Technical]").GetAttribute("aria-checked").Should().Be("true", "it reads back as saved");
        cut.Find("[data-testid=mtp-save]").HasAttribute("disabled").Should().BeTrue();
        // The list reads the case's own type: with «Vis teknisk innhold» off the case is counted as hidden, while the editor keeps it open.
        cut.Find("[data-testid=mtp-hidden]").TextContent.Should().StartWith("1 teknisk");
        cut.Find("[data-testid=mtp-technical]").Change(true);
        cut.Find("[data-testid=mtp-case-kind]").TextContent.Should().Be("Teknisk");
    }

    [Fact]
    public void K10_K13_K14_a_saved_type_reopens_as_saved_an_older_case_stays_unset_and_a_scenario_decision_never_overrides_the_case()
    {
        var manual = Case("c1", "Submit a valid report", story: SpecStory) with { Edits = new TestCaseEdits { TestKind = ScenarioTestKind.Manual } };
        var legacy = Case("c2", "Reject an invalid report", story: SpecStory);
        Setup(Plan(manual, legacy));
        _api.Setup(x => x.StoryGroupingAsync("library", "specs/001/spec.md", It.IsAny<CancellationToken>())).ReturnsAsync((Decided("c1", ScenarioTestKind.Technical), (string?)null));
        var cut = Open();

        cut.Find("[data-testid=mtp-kind-option][data-kind=Manual]").GetAttribute("aria-checked").Should().Be("true", "the case keeps its own type although its scenario was reviewed as technical");
        cut.Find("[data-testid=mtp-kind-hint]").TextContent.Should().Contain("vurdert som teknisk");

        var form = P.CaseForm.From(legacy);
        form.TestKind.Should().BeNull("an older case is never given a type it was not given");
        form.ToEdits(legacy).TestKind.Should().BeNull();
        form.IsDirty(legacy).Should().BeFalse();
    }

    [Fact]
    public void K15_K16_the_lists_technical_filter_reads_the_cases_own_type_and_the_type_is_never_automation_or_a_result()
    {
        // api1 is technical by the heuristic; a person chose Manuell for it, so it is listed with the manual cases. t1 was chosen Teknisk.
        var api = Case("api1", "GET /reports", capability: ScenarioOwnership.ApiContract, type: ScenarioCandidateType.ApiContract) with { Edits = new TestCaseEdits { TestKind = ScenarioTestKind.Manual } };
        var tech = Case("c3", "Submit a valid report", story: Story6) with { Edits = new TestCaseEdits { TestKind = ScenarioTestKind.Technical } };
        P.TechnicalReason(api).Should().BeNull();
        P.TechnicalReason(tech).Should().Be("Testtypen er Teknisk");

        Setup(Plan(api, tech));
        var cut = Open();
        cut.FindAll("[data-testid=mtp-case]").Select(c => c.GetAttribute("data-case")).Should().Equal("api1");
        cut.Find("[data-testid=mtp-hidden]").TextContent.Should().StartWith("1 teknisk");
        cut.Find("[data-testid=mtp-case-kind]").TextContent.Should().Be("Manuell");
        cut.Markup.Should().NotContain("Automatisert").And.NotContain("Bestått");
    }

    [Fact]
    public void K06_suggestions_say_where_they_come_from_and_a_direct_case_has_none()
    {
        var c = Case("c1", "Submit a valid report", story: SpecStory);
        P.SuggestKind(null, null).Should().BeNull();
        P.SuggestionText(null).Should().Be("Velg testtype for testtilfellet.");
        var open = P.SuggestKind(c, null)!;
        open.Reviewed.Should().BeNull("an unreviewed scenario has no decision");
        P.SuggestionText(open).Should().StartWith("Anbefalt for scenarioet:", "the execution suitability Finn og forstå shows; «Manuell + teknisk» leaves both choices open");
        P.CaseKindLabel(null).Should().Be("Ikke angitt");
        P.CaseKindLabel(ScenarioTestKind.Technical).Should().Be("Teknisk");
    }

    [Fact]
    public void K05b_a_scenario_reviewed_as_Begge_preselects_nothing_for_one_case()
    {
        Setup(Plan(Case("c1", "Submit a valid report", story: SpecStory)));
        _api.Setup(x => x.StoryGroupingAsync("library", "specs/001/spec.md", It.IsAny<CancellationToken>())).ReturnsAsync((Decided("c1", ScenarioTestKind.Both), (string?)null));
        var cut = Open();

        cut.Find("[data-testid=mtp-add-case]").Click();
        cut.FindAll("[data-testid=mtp-new-kind-option]").Should().OnlyContain(b => b.GetAttribute("aria-checked") == "false");
        cut.FindAll("[data-testid=mtp-new-kind-option]").Select(b => b.TextContent).Should().Equal("Manuell", "Teknisk");
        cut.Find("[data-testid=mtp-new-kind-hint]").TextContent.Should().Contain("både manuelt og teknisk").And.Contain("Velg testtype");
    }
}
