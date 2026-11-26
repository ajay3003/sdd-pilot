using BirkNext.TestPlanning;

namespace BirkNext.Web.Services;

/// <summary>
/// Test suite management in Planlegg og klargjør (2026-11-26): pure edits of a plan's suite tree, saved through the normal plan save (each save is
/// one revision; expanding, collapsing and selecting are never saved). Moving a case changes only its suite — never its scenario, steps, expected
/// results, translation, approval, test data or identity. Removing a suite never deletes a case: its cases go to a suite the tester chooses and its
/// child suites move up to its parent.
/// </summary>
public static class PlanSuiteEditing
{
    public sealed record Edit(TestPlanDraft Plan, string? Error);

    private static Edit Ok(TestPlanDraft plan) => new(plan, null);
    private static Edit Refuse(TestPlanDraft plan, string why) => new(plan, why);

    /// <summary>A plan whose suites form a tree (any suite has a parent).</summary>
    public static bool Hierarchical(TestPlanDraft plan) => plan.Suites.Any(s => s.ParentId is not null);

    /// <summary>Every case in the suite or one of its descendants (excluded and superseded cases are not counted).</summary>
    public static int CasesIn(TestPlanDraft plan, string suiteId) =>
        plan.Cases.Count(c => c.Review is not (TestCaseReviewState.Superseded or TestCaseReviewState.Excluded) && (c.SuiteId == suiteId || IsDescendant(plan.Suites, c.SuiteId, suiteId)));

    public static bool IsDescendant(IReadOnlyList<TestSuiteDraft> suites, string? suiteId, string ancestorId)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var s = suites.FirstOrDefault(x => x.Id == suiteId); s?.ParentId is { } parent && seen.Add(s.Id); s = suites.FirstOrDefault(x => x.Id == parent))
            if (parent == ancestorId) return true;
        return false;
    }

    public static Edit Add(TestPlanDraft plan, string name, string? parentId, Func<string>? newId = null)
    {
        var clean = name.Trim();
        if (clean.Length == 0) return Refuse(plan, "Gi testgruppen et navn.");
        if (parentId is not null && plan.Suites.All(s => s.Id != parentId)) return Refuse(plan, "Den overordnede testgruppen finnes ikke.");
        if (plan.Suites.Any(s => s.ParentId == parentId && string.Equals(s.Name.Trim(), clean, StringComparison.OrdinalIgnoreCase)))
            return Refuse(plan, $"Det finnes allerede en testgruppe «{clean}» her.");
        var id = newId?.Invoke() ?? "suite:r:" + Guid.NewGuid().ToString("N")[..10];
        var order = plan.Suites.Where(s => s.ParentId == parentId).Select(s => s.Order).DefaultIfEmpty(-1).Max() + 1;
        return Ok(plan with { Suites = [.. plan.Suites, new TestSuiteDraft(id, clean, "reviewer", null) { ParentId = parentId, Order = order }] });
    }

    public static Edit Rename(TestPlanDraft plan, string suiteId, string name)
    {
        var clean = name.Trim();
        if (clean.Length == 0) return Refuse(plan, "Gi testgruppen et navn.");
        var suite = plan.Suites.FirstOrDefault(s => s.Id == suiteId);
        if (suite is null) return Refuse(plan, "Testgruppen finnes ikke.");
        if (plan.Suites.Any(s => s.Id != suiteId && s.ParentId == suite.ParentId && string.Equals(s.Name.Trim(), clean, StringComparison.OrdinalIgnoreCase)))
            return Refuse(plan, $"Det finnes allerede en testgruppe «{clean}» her.");
        return Ok(plan with { Suites = [.. plan.Suites.Select(s => s.Id == suiteId ? s with { Name = clean } : s)] });
    }

    public static Edit Move(TestPlanDraft plan, string suiteId, string? newParentId)
    {
        if (newParentId == suiteId || newParentId is not null && IsDescendant(plan.Suites, newParentId, suiteId)) return Refuse(plan, "En testgruppe kan ikke flyttes inn i seg selv.");
        if (newParentId is not null && plan.Suites.All(s => s.Id != newParentId)) return Refuse(plan, "Testgruppen du flytter til finnes ikke.");
        var order = plan.Suites.Where(s => s.ParentId == newParentId && s.Id != suiteId).Select(s => s.Order).DefaultIfEmpty(-1).Max() + 1;
        return Ok(plan with { Suites = [.. plan.Suites.Select(s => s.Id == suiteId ? s with { ParentId = newParentId, Order = order } : s)] });
    }

    public static Edit MoveCase(TestPlanDraft plan, string caseId, string suiteId)
    {
        if (plan.Suites.All(s => s.Id != suiteId)) return Refuse(plan, "Testgruppen finnes ikke.");
        return Ok(plan with { Cases = [.. plan.Cases.Select(c => c.Source.ScenarioId == caseId ? c with { SuiteId = suiteId } : c)] });
    }

    /// <summary>Removes a suite. Its cases need a target suite (refused without one); its child suites move up to its parent. Never deletes a case.</summary>
    public static Edit Remove(TestPlanDraft plan, string suiteId, string? casesTo)
    {
        var suite = plan.Suites.FirstOrDefault(s => s.Id == suiteId);
        if (suite is null) return Refuse(plan, "Testgruppen finnes ikke.");
        var own = plan.Cases.Where(c => c.SuiteId == suiteId).ToList();
        if (own.Count > 0 && (casesTo is null || casesTo == suiteId || plan.Suites.All(s => s.Id != casesTo)))
            return Refuse(plan, $"Testgruppen har {own.Count} testtilfelle{(own.Count == 1 ? "" : "r")}. Velg hvor de skal flyttes først; ingen testtilfeller slettes.");
        if (plan.Suites.Count == 1 && plan.Cases.Count > 0) return Refuse(plan, "Testplanen må ha minst én testgruppe så lenge den har testtilfeller.");
        return Ok(plan with
        {
            Suites = [.. plan.Suites.Where(s => s.Id != suiteId).Select(s => s.ParentId == suiteId ? s with { ParentId = suite.ParentId } : s)],
            Cases = [.. plan.Cases.Select(c => c.SuiteId == suiteId ? c with { SuiteId = casesTo! } : c)],
        });
    }

    /// <summary>Every suite as «Ende til ende / Rapportering», in tree order (for move and target choices).</summary>
    public static List<(string Id, string Path)> Choices(TestPlanDraft plan) =>
        [.. PlanStructure.Flatten(plan.Suites).Select(x => (x.Suite.Id, PlanStructure.PathOf(plan.Suites, x.Suite.Id)))];
}
