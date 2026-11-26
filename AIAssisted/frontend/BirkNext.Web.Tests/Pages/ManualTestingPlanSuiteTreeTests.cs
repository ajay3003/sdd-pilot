using BirkNext.TestPlanning;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Moq;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// Hierarchical test plans in Planlegg og klargjør (2026-11-26): the saved suite tree is the left list (collapsible, counts per suite, the open
/// case's suite always open), suite changes and case moves are saved as one revision through the normal save, moving a case never changes anything
/// but its suite, and a flat plan looks as before. The pure suite edits refuse what would lose or orphan anything.
/// </summary>
public sealed partial class ManualTestingPlanTests
{
    private static TestPlanDraft Tree() => Plan(
        Case("e1", "Gyldig rapport", type: ScenarioCandidateType.BusinessE2E) with { SuiteId = "s-rapp" },
        Case("e2", "Korrigering", type: ScenarioCandidateType.BusinessE2E) with { SuiteId = "s-rapp" },
        Case("i1", "Gyldig XML", type: ScenarioCandidateType.Integration) with { SuiteId = "s-mott" }) with
    {
        Suites =
        [
            new("s-e2e", "Ende til ende", "structure", null) { TestLevel = ScenarioTestLevel.EndToEnd }, new("s-rapp", "Rapportering", "structure", null) { ParentId = "s-e2e" },
            new("s-cdc", "CDC", "structure", null) { ParentId = "s-e2e", Order = 1 },
            new("s-int", "Integrasjon", "structure", null) { Order = 1 }, new("s-mott", "Meldingsmottak", "structure", null) { ParentId = "s-int" },
        ],
    };

    [Fact]
    public void A_plan_with_a_suite_tree_lists_its_suites_with_counts_and_empty_suites_and_folds_without_saving()
    {
        Setup(Tree());
        var cut = Open();
        cut.FindAll("[data-testid=mtp-suite]").Select(s => (s.GetAttribute("data-suite"), s.GetAttribute("data-depth"))).Should().Equal(
            ("s-e2e", "0"), ("s-rapp", "1"), ("s-cdc", "1"), ("s-int", "0"), ("s-mott", "1"));
        cut.Find("[data-testid=mtp-suite][data-suite=s-e2e] > [data-testid=mtp-suite-toggle] [data-testid=mtp-suite-count]").TextContent.Should().Be("2");
        cut.Find("[data-testid=mtp-suite][data-suite=s-cdc] [data-testid=mtp-suite-empty]").TextContent.Should().Be("0 testtilfeller", "an empty suite stays visible");

        cut.Find("[data-testid=mtp-suite][data-suite=s-int] > [data-testid=mtp-suite-toggle]").Click();
        cut.FindAll("[data-testid=mtp-suite][data-suite=s-mott]").Should().BeEmpty("collapsed");
        cut.Find("[data-testid=mtp-suites-expand]").Click();
        cut.FindAll("[data-testid=mtp-suite][data-suite=s-mott]").Should().ContainSingle();
        _api.Verify(x => x.SaveAsync(It.IsAny<Guid>(), It.IsAny<SaveTestPlanDraftRequest>(), It.IsAny<CancellationToken>()), Times.Never, "folding is never saved");
    }

    [Fact]
    public void Moving_a_case_saves_one_revision_with_only_its_suite_changed()
    {
        var plan = Tree();
        Setup(plan);
        SaveAsyncReturns(plan with { Revision = 4 });
        var cut = Open();
        cut.Find("[data-testid=mtp-case][data-case=i1]").Click();
        cut.Find("[data-testid=mtp-case-suite]").Change("s-cdc");
        _api.Verify(x => x.SaveAsync(PlanId, It.Is<SaveTestPlanDraftRequest>(r =>
            r.Cases.Single(c => c.ScenarioId == "i1").SuiteId == "s-cdc" && r.Cases.Where(c => c.ScenarioId != "i1").All(c => c.SuiteId == "s-rapp")
            && r.Suites.Count == 5 && r.Cases.Single(c => c.ScenarioId == "i1").Edits == plan.Cases.Single(c => c.Source.ScenarioId == "i1").Edits), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void A_flat_plan_keeps_its_list_without_suite_headers()
    {
        Setup(Standard());
        var cut = Open();
        cut.FindAll("[data-testid=mtp-suite]").Should().BeEmpty();
        cut.FindAll("[data-testid=mtp-scenario]").Should().NotBeEmpty();
    }

    private void SaveAsyncReturns(TestPlanDraft after) =>
        _api.Setup(x => x.SaveAsync(PlanId, It.IsAny<SaveTestPlanDraftRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync((after, (string?)null));

    [Fact]
    public void Suite_edits_refuse_what_would_lose_or_orphan_anything_and_never_touch_a_case_beyond_its_suite()
    {
        var plan = Tree();
        PlanSuiteEditing.Add(plan, "Rapportering", "s-e2e").Error.Should().Contain("finnes allerede", "siblings have unique names");
        var added = PlanSuiteEditing.Add(plan, "Rapportering", "s-int", () => "s-new");
        added.Error.Should().BeNull("the same name under another parent is another suite");
        added.Plan.Suites.Single(s => s.Id == "s-new").ParentId.Should().Be("s-int");
        PlanSuiteEditing.Move(plan, "s-e2e", "s-rapp").Error.Should().Contain("inn i seg selv");
        PlanSuiteEditing.Rename(plan, "s-rapp", " ").Error.Should().NotBeNull();

        PlanSuiteEditing.Remove(plan, "s-rapp", null).Error.Should().Contain("ingen testtilfeller slettes");
        var removed = PlanSuiteEditing.Remove(plan, "s-e2e", null);
        removed.Error.Should().BeNull("a suite without own cases can go");
        removed.Plan.Suites.Where(s => s.Id is "s-rapp" or "s-cdc").Should().OnlyContain(s => s.ParentId == null, "its children move up");
        removed.Plan.Cases.Should().HaveCount(3);
        var moved = PlanSuiteEditing.Remove(plan, "s-rapp", "s-mott").Plan;
        moved.Cases.Where(c => c.Source.ScenarioId.StartsWith('e')).Should().OnlyContain(c => c.SuiteId == "s-mott");

        var caseMoved = PlanSuiteEditing.MoveCase(plan, "e1", "s-cdc").Plan.Cases.Single(c => c.Source.ScenarioId == "e1");
        caseMoved.Should().BeEquivalentTo(plan.Cases.Single(c => c.Source.ScenarioId == "e1") with { SuiteId = "s-cdc" });
        PlanSuiteEditing.CasesIn(plan, "s-e2e").Should().Be(2);
    }

    [Fact]
    public void Se_endringer_names_suite_changes_and_moved_cases()
    {
        var before = Tree();
        var after = PlanSuiteEditing.MoveCase(PlanSuiteEditing.Rename(PlanSuiteEditing.Add(before, "Duplikater", "s-int", () => "s-dup").Plan, "s-cdc", "CDC-flyt").Plan, "i1", "s-dup").Plan;
        PlanRevisionDiff.Compare(before, after).Select(c => c.Text).Should().Contain([
            "Testgruppen «Integrasjon / Duplikater» er lagt til.", "Testgruppen «CDC» heter nå «CDC-flyt».",
            "Flyttet fra testgruppen «Integrasjon / Meldingsmottak» til «Integrasjon / Duplikater»."]);
    }
}
