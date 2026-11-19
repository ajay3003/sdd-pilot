using BirkNext.Api.Services.TestPlanning;
using BirkNext.TestPlanning;
using FluentAssertions;
using Xunit;

namespace BirkNext.Api.Tests.Services.TestPlanning;

/// <summary>
/// Approving a case means it is ready for the Azure DevOps file (2026-11-19): the same content rules as the export — at least one step, an
/// action and an expected result on every step. The case-level expected result is not part of an Azure DevOps test case and is not required.
/// </summary>
public sealed class ApproveRuleTests
{
    private static TestCaseDraft Case(string? caseExpected, List<string> steps, List<string?> stepExpected) => new()
    {
        Source = new ScenarioReference { Kind = ScenarioSourceKind.Manual, Owner = "p", ScenarioId = "c1" },
        Generated = new TestCaseContent { Title = "Return a book", ExpectedResult = caseExpected, Steps = steps, StepExpected = stepExpected },
    };

    [Fact]
    public void The_export_rules_decide_and_the_case_level_expected_result_is_not_required()
    {
        TestPlanSuggestionService.ApproveBlocker(Case(null, ["Return the book"], ["It is registered"])).Should().BeNull();
        TestPlanSuggestionService.ApproveBlocker(Case("Done", [], [])).Should().Be("add at least one step with its expected result.");
        TestPlanSuggestionService.ApproveBlocker(Case("Done", ["Open", "Return"], ["Shown", null])).Should().Be("step 2 needs an expected result.");
        TestPlanSuggestionService.ApproveBlocker(Case("Done", ["Open", "Return", "Close"], ["Shown"])).Should().Be("step 2, 3 need an expected result.");
        TestPlanSuggestionService.ApproveBlocker(Case("Done", [" ", "Return"], ["x", "y"])).Should().Be("step 1 needs an action.");
    }
}
