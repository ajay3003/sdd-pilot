using BirkNext.TestPlanning;
using Bunit;
using FluentAssertions;

namespace BirkNext.Web.Tests.Pages;

/// <summary>Planlegg og klargjør V5 (2026-11-26): the case toolbar is grouped (edit · preview · review) with ONE primary action per state, and
/// a case has no «Start test» here — execution lives in Testplaner.</summary>
public sealed partial class ManualTestingPlanTests
{
    [Fact]
    public void V5_the_toolbar_groups_its_actions_with_one_primary_action_per_state_and_offers_no_execution()
    {
        var c1 = Case("c1", "Submit a valid report", story: Story6) with { Edits = new TestCaseEdits { Title = "Send inn", TestKind = ScenarioTestKind.Manual } };
        Setup(Plan(c1));
        var cut = Open();

        string[] Primary() => [.. cut.FindAll("[data-testid=mtp-actions] .btn-primary").Select(b => b.GetAttribute("data-testid")!)];
        cut.Find("[data-testid=mtp-review-group]").GetAttribute("role").Should().Be("group");
        cut.Find("[data-testid=mtp-review-group]").QuerySelectorAll("button").Select(b => b.GetAttribute("data-testid")).Should().Contain("mtp-approve");
        Primary().Should().Equal(["mtp-approve"], "nothing unsaved: the review step is the one primary action");

        cut.Find("[data-testid=mtp-observation]").Input("Rapporten vises i listen");
        Primary().Should().Equal(["mtp-save"], "unsaved changes: Lagre is the one primary action");
        cut.FindAll("[data-testid=mtp-run-start]").Should().BeEmpty("execution lives in Testplaner");
    }
}
