using BirkNext.TestPlanning;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>«Se endringer» (2026-11-25): what changed between two stored revisions, per case and field.</summary>
public sealed class PlanRevisionDiffTests
{
    private static TestCaseDraft Case(string id, string title, TestCaseReviewState review = TestCaseReviewState.Generated) => new()
    {
        Source = new ScenarioReference { Kind = ScenarioSourceKind.ProjectDiscovery, Owner = "p", ScenarioId = id }, Review = review,
        Generated = new TestCaseContent { Title = title, Steps = ["Open"], StepExpected = ["Opens"], ExpectedResult = "Stored" },
    };

    [Fact]
    public void Added_removed_changed_fields_and_approval_are_named()
    {
        var old = new TestPlanDraft { PlanName = "Plan", Cases = [Case("a", "Valid report"), Case("b", "Old case")] };
        var a2 = Case("a", "Valid report", TestCaseReviewState.Accepted) with { Edits = new TestCaseEdits { Steps = ["Open", "Send"], StepExpected = ["Opens", "Sent"], Preconditions = "QA" } };
        var @new = new TestPlanDraft { PlanName = "Plan 2", Cases = [a2, Case("c", "New case")] };

        var changes = PlanRevisionDiff.Compare(old, @new).Select(c => (c.CaseTitle, c.Text)).ToList();

        changes.Should().Contain((null, "Navnet er endret fra «Plan» til «Plan 2»."));
        changes.Should().Contain(("New case", "Lagt til.")).And.Contain(("Old case", "Fjernet."));
        changes.Should().Contain(("Valid report", "Endret: forutsetninger, teststeg, forventet resultat per steg."));
        changes.Should().Contain(c => c.CaseTitle == "Valid report" && c.Text.StartsWith("Status: "));
        PlanRevisionDiff.Compare(old, old).Should().BeEmpty();
    }
}
