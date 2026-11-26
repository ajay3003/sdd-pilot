using BirkNext.TestPlanning;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Moq;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// Templates, empty plans and manual cases in Planlegg og klargjør (2026-11-26): an empty plan says so and offers «Opprett testgruppe» and «Legg til
/// testtilfelle»; an empty suite (from a template) is shown with «+ Nytt testtilfelle», which proposes the suite and its level; the editor carries the
/// case's own Testnivå and Scenariotype; «Lagre som mal» saves the plan's structure only.
/// </summary>
public sealed partial class ManualTestingPlanTests
{
    private static TestPlanDraft EmptyPlan() => Plan() with { Suites = [] };

    private static TestPlanDraft TemplatePlan() => Plan() with
    {
        Suites =
        [
            new("t-e2e", "Ende til ende", "template", null) { TestLevel = ScenarioTestLevel.EndToEnd },
            new("t-int", "Integrasjon", "template", null) { Order = 1, TestLevel = ScenarioTestLevel.Integration },
        ],
    };

    [Fact]
    public void T01_an_empty_plan_says_it_is_empty_and_offers_a_suite_and_a_case()
    {
        Setup(EmptyPlan());
        var cut = Open();

        cut.Find("[data-testid=mtp-plan-empty]").TextContent.Should().Contain("Testplanen er tom.");
        cut.FindAll("[data-testid=mtp-rows-none]").Should().BeEmpty("an empty plan is not «Ingen treff»");
        cut.Find("[data-testid=mtp-plan-empty-suite]").Click();
        cut.Find("[data-testid=mtp-suite-panel]").Should().NotBeNull("«Opprett testgruppe» opens the suite panel");
        cut.Find("[data-testid=mtp-plan-empty-case]").Click();
        cut.Find("[data-testid=mtp-new]").Should().NotBeNull();
    }

    [Fact]
    public void T02_empty_template_suites_are_shown_and_a_new_case_in_a_suite_proposes_its_level_and_sends_suite_level_and_type()
    {
        Setup(TemplatePlan());
        AddReturns(TemplatePlan() with { Revision = 4 });
        var cut = Open();

        cut.FindAll("[data-testid=mtp-suite]").Select(s => s.GetAttribute("data-suite")).Should().Equal("t-e2e", "t-int");
        cut.FindAll("[data-testid=mtp-suite-empty]").Should().HaveCount(2);
        cut.FindAll("[data-testid=mtp-suite][data-suite=t-int] [data-testid=mtp-suite-new-case]").Single().Click();

        cut.Find("[data-testid=mtp-new-suite]").QuerySelector("option[selected]")!.GetAttribute("value").Should().Be("t-int");
        cut.Find("[data-testid=mtp-new-level]").QuerySelector("option[selected]")!.GetAttribute("value").Should().Be("Integration", "the suite's level is proposed");
        cut.Find("[data-testid=mtp-new-level]").Change("EndToEnd");
        cut.Find("[data-testid=mtp-new-level-differs]").TextContent.Should().Contain("Det er lov", "a different level is allowed, only explained");
        cut.Find("[data-testid=mtp-new-type]").Change("Functional");
        cut.Find("[data-testid=mtp-new-title-input]").Input("Send meldingen til mottaket");
        cut.Find("[data-testid=mtp-new-kind-option][data-kind=Manual]").Click();
        cut.Find("[data-testid=mtp-new-create]").Click();

        _api.Verify(x => x.AddManualCaseAsync(PlanId, It.Is<AddManualTestCaseRequest>(r => r.SuiteId == "t-int" && r.TestLevel == ScenarioTestLevel.EndToEnd
            && r.ScenarioType == ScenarioRequirementType.Functional && r.TestKind == ScenarioTestKind.Manual && r.ScenarioKey == null), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void T03_the_editor_saves_the_case_own_test_level_and_scenario_type()
    {
        var plan = TemplatePlan() with { Cases = [Case("m1", "Skrevet for hånd", kind: ScenarioSourceKind.Manual, steps: []) with { SuiteId = "t-e2e", Edits = new TestCaseEdits { TestKind = ScenarioTestKind.Manual } }] };
        Setup(plan);
        SaveAsyncReturns(plan with { Revision = 4 });
        var cut = Open();
        cut.Find("[data-testid=mtp-case][data-case=m1]").Click();

        cut.Find("[data-testid=mtp-case-level]").Change("Integration");
        cut.Find("[data-testid=mtp-case-type]").Change("NonFunctional");
        cut.Find("[data-testid=mtp-save]").Click();

        _api.Verify(x => x.SaveAsync(PlanId, It.Is<SaveTestPlanDraftRequest>(r => r.Cases.Single().Edits.TestLevel == ScenarioTestLevel.Integration
            && r.Cases.Single().Edits.ScenarioType == ScenarioRequirementType.NonFunctional && r.Cases.Single().SuiteId == "t-e2e"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void T04_lagre_som_mal_saves_the_stored_structure_from_the_plan()
    {
        Setup(TemplatePlan());
        _api.Setup(x => x.CreateTemplateAsync(It.IsAny<SaveTestPlanTemplateRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((new TestPlanTemplate { Id = "custom:1", Name = "Regresjon" }, (string?)null));
        var cut = Open();

        cut.Find("[data-testid=mtp-suites-manage]").Click();
        cut.Find("[data-testid=mtp-save-template-button]").HasAttribute("disabled").Should().BeTrue("a name is needed");
        cut.Find("[data-testid=mtp-save-template-name]").Input("Regresjon");
        cut.Find("[data-testid=mtp-save-template-button]").Click();

        _api.Verify(x => x.CreateTemplateAsync(It.Is<SaveTestPlanTemplateRequest>(r => r.Name == "Regresjon" && r.FromPlan == PlanId && r.Suites == null), It.IsAny<CancellationToken>()), Times.Once);
        cut.Find("[data-testid=mtp-notice]").TextContent.Should().Contain("Malen «Regresjon» er lagret");
    }
}
