using BirkNext.TestPlanning;

namespace BirkNext.Web.Services;

/// <summary>
/// Reordering and removing in Plan &amp; prepare, as pure changes of the draft (saved with Save draft as a new version). The plan's case list is the
/// one order everything uses — the scenario list, the cases under a scenario and the Azure DevOps export — so a move changes that list only:
/// <list type="bullet">
/// <item>a test case moves among the cases of its own scenario (never into another scenario);</item>
/// <item>a scenario moves with all its cases as one block, among the scenarios of the same group (the same user story, or the same "other"
/// group); user stories keep the specification's order;</item>
/// <item>a step moves within its case, with its expected result.</item>
/// </list>
/// Removing a scenario is allowed only when none of its cases was ever reviewed; otherwise it is excluded with a reason (history kept).
/// </summary>
public static class PlanOrdering
{
    /// <summary>Moves a case to <paramref name="toIndex"/> among its scenario's cases (0-based, clamped). Other scenarios are untouched.</summary>
    public static TestPlanDraft MoveCase(TestPlanDraft plan, string caseId, int toIndex)
    {
        var moving = plan.Cases.FirstOrDefault(c => c.Source.ScenarioId == caseId);
        if (moving is null) return plan;
        var scenario = moving.ParentScenario ?? "";
        var siblings = plan.Cases.Where(c => (c.ParentScenario ?? "") == scenario).ToList();
        var from = siblings.IndexOf(moving);
        toIndex = Math.Clamp(toIndex, 0, siblings.Count - 1);
        if (from == toIndex) return plan;
        siblings.RemoveAt(from);
        siblings.Insert(toIndex, moving);
        // Write the siblings back into the slots the scenario's cases occupy, so nothing else moves.
        var queue = new Queue<TestCaseDraft>(siblings);
        return plan with { Cases = [.. plan.Cases.Select(c => (c.ParentScenario ?? "") == scenario ? queue.Dequeue() : c)] };
    }

    /// <summary>Moves a scenario (all its cases, as a block) to <paramref name="toIndex"/> among <paramref name="siblingKeys"/> — the scenarios shown in
    /// the same group, in their current order.</summary>
    public static TestPlanDraft MoveScenario(TestPlanDraft plan, string scenarioKey, IReadOnlyList<string> siblingKeys, int toIndex)
    {
        var order = siblingKeys.ToList();
        var from = order.IndexOf(scenarioKey);
        if (from < 0) return plan;
        toIndex = Math.Clamp(toIndex, 0, order.Count - 1);
        if (from == toIndex) return plan;
        order.RemoveAt(from);
        order.Insert(toIndex, scenarioKey);
        var inGroup = order.ToHashSet(StringComparer.Ordinal);
        // The group's cases, block by block in the new order, written back into the slots the group occupies.
        var blocks = new Queue<TestCaseDraft>(order.SelectMany(k => plan.Cases.Where(c => (c.ParentScenario ?? "") == k)));
        return plan with { Cases = [.. plan.Cases.Select(c => inGroup.Contains(c.ParentScenario ?? "") ? blocks.Dequeue() : c)] };
    }

    /// <summary>Why a scenario cannot simply be removed (null = it can): a reviewed case is excluded with a reason instead, so its record stays.</summary>
    public static string? RemoveScenarioBlocker(TestPlanDraft plan, string scenarioKey)
    {
        var cases = plan.Cases.Where(c => (c.ParentScenario ?? "") == scenarioKey).ToList();
        if (cases.Count == 0) return "The scenario has no test case in this plan.";
        if (cases.Count == plan.Cases.Count) return "A plan keeps at least one test case.";
        if (cases.Any(c => c.ReviewHistory.Count > 0 || c.Review is TestCaseReviewState.Accepted or TestCaseReviewState.NeedsClarification or TestCaseReviewState.Excluded))
            return "A test case of this scenario has been reviewed: exclude the scenario with a reason instead (its history is kept).";
        if (cases.Any(c => !string.IsNullOrWhiteSpace(c.VerifiedAzureDevOpsTestCaseId)))
            return "A test case of this scenario has a known Azure DevOps id: exclude it instead.";
        return null;
    }

    /// <summary>The plan without the scenario and its cases (only when <see cref="RemoveScenarioBlocker"/> allows it; earlier versions keep them).</summary>
    public static TestPlanDraft RemoveScenario(TestPlanDraft plan, string scenarioKey) =>
        RemoveScenarioBlocker(plan, scenarioKey) is null ? plan with { Cases = [.. plan.Cases.Where(c => (c.ParentScenario ?? "") != scenarioKey)] } : plan;

    /// <summary>Moves a step (with its expected result) to <paramref name="toIndex"/>; the case's steps become the tester's edit (the evidence is kept apart).</summary>
    public static TestCaseDraft MoveStep(TestCaseDraft c, int fromIndex, int toIndex)
    {
        var steps = c.EffectiveSteps.ToList();
        var results = c.EffectiveStepExpected.ToList();
        if (fromIndex < 0 || fromIndex >= steps.Count) return c;
        toIndex = Math.Clamp(toIndex, 0, steps.Count - 1);
        if (fromIndex == toIndex) return c;
        var step = steps[fromIndex];
        var result = results[fromIndex];
        steps.RemoveAt(fromIndex); results.RemoveAt(fromIndex);
        steps.Insert(toIndex, step); results.Insert(toIndex, result);
        return c with { Edits = c.Edits with { Steps = steps, StepExpected = results } };
    }
}
