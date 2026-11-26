using BirkNext.TestPlanning;

namespace BirkNext.Web.Services;

/// <summary>
/// «Se endringer» in Versjoner (2026-11-25): what changed between two stored revisions of one plan, per case and field, in the tester's words.
/// Only from the two stored revisions (both are read from the plan's history); nothing is reconstructed or guessed.
/// </summary>
public static class PlanRevisionDiff
{
    public sealed record Change(string? CaseTitle, string Text);

    public static IReadOnlyList<Change> Compare(TestPlanDraft older, TestPlanDraft newer)
    {
        var list = new List<Change>();
        if (!string.Equals(older.PlanName, newer.PlanName, StringComparison.Ordinal)) list.Add(new(null, $"Navnet er endret fra «{older.PlanName}» til «{newer.PlanName}»."));
        // Suite structure (2026-11-26): added, removed, renamed and moved suites, by suite id.
        var oldSuites = older.Suites.ToDictionary(x => x.Id, StringComparer.Ordinal);
        foreach (var x in newer.Suites)
        {
            if (!oldSuites.TryGetValue(x.Id, out var was)) { list.Add(new(null, $"Testgruppen «{PlanStructure.PathOf(newer.Suites, x.Id)}» er lagt til.")); continue; }
            if (was.Name != x.Name) list.Add(new(null, $"Testgruppen «{was.Name}» heter nå «{x.Name}»."));
            if (was.ParentId != x.ParentId) list.Add(new(null, $"Testgruppen «{x.Name}» er flyttet fra «{(was.ParentId is null ? "øverst" : PlanStructure.PathOf(older.Suites, was.ParentId))}» til «{(x.ParentId is null ? "øverst" : PlanStructure.PathOf(newer.Suites, x.ParentId))}»."));
        }
        foreach (var x in older.Suites.Where(o => newer.Suites.All(n => n.Id != o.Id))) list.Add(new(null, $"Testgruppen «{PlanStructure.PathOf(older.Suites, x.Id)}» er fjernet."));
        var before = older.Cases.ToDictionary(c => c.Source.ScenarioId, StringComparer.Ordinal);
        var after = newer.Cases.ToDictionary(c => c.Source.ScenarioId, StringComparer.Ordinal);
        foreach (var c in newer.Cases.Where(c => !before.ContainsKey(c.Source.ScenarioId))) list.Add(new(c.EffectiveTitle, "Lagt til."));
        foreach (var c in older.Cases.Where(c => !after.ContainsKey(c.Source.ScenarioId))) list.Add(new(c.EffectiveTitle, "Fjernet."));
        foreach (var n in newer.Cases)
        {
            if (!before.TryGetValue(n.Source.ScenarioId, out var o)) continue;
            var fields = new List<string>();
            if (o.EffectiveTitle != n.EffectiveTitle) fields.Add("tittel");
            if ((o.EffectivePreconditions ?? "") != (n.EffectivePreconditions ?? "")) fields.Add("forutsetninger");
            if (!o.EffectiveSteps.SequenceEqual(n.EffectiveSteps)) fields.Add("teststeg");
            if (!o.EffectiveStepExpected.Select(x => x ?? "").SequenceEqual(n.EffectiveStepExpected.Select(x => x ?? ""))) fields.Add("forventet resultat per steg");
            if ((o.EffectiveExpected ?? "") != (n.EffectiveExpected ?? "")) fields.Add("samlet forventet resultat");
            if (!o.TestData.Select(d => d.AssetId).Order(StringComparer.Ordinal).SequenceEqual(n.TestData.Select(d => d.AssetId).Order(StringComparer.Ordinal))) fields.Add("testdata");
            if ((o.EffectivePurpose ?? "") != (n.EffectivePurpose ?? "")) fields.Add("formål");
            if ((o.EffectiveObservation ?? "") != (n.EffectiveObservation ?? "")) fields.Add("slik ser du resultatet");
            if ((o.EffectiveEnvironment ?? "") != (n.EffectiveEnvironment ?? "")) fields.Add("tilgang og miljø");
            if ((o.Edits.BlockedBy ?? "") != (n.Edits.BlockedBy ?? "")) fields.Add("hindring");
            if (o.Edits.TestKind != n.Edits.TestKind) fields.Add("testtype");
            if (o.SuiteId != n.SuiteId) list.Add(new(n.EffectiveTitle, $"Flyttet fra testgruppen «{(older.Suites.Any(x => x.Id == o.SuiteId) ? PlanStructure.PathOf(older.Suites, o.SuiteId) : "ingen")}» til «{(newer.Suites.Any(x => x.Id == n.SuiteId) ? PlanStructure.PathOf(newer.Suites, n.SuiteId) : "ingen")}»."));
            if (!SameTranslation(o.Translation(ScenarioPlanning.Norwegian), n.Translation(ScenarioPlanning.Norwegian))) fields.Add("norsk tekst");
            if (fields.Count > 0) list.Add(new(n.EffectiveTitle, "Endret: " + string.Join(", ", fields) + "."));
            if (o.Review != n.Review) list.Add(new(n.EffectiveTitle, $"Status: {PlanPreparePresentation.ReviewLabel(o.Review)} → {PlanPreparePresentation.ReviewLabel(n.Review)}."));
        }
        return list;
    }

    private static bool SameTranslation(TestCaseTranslation? a, TestCaseTranslation? b) =>
        a is null && b is null || a is not null && b is not null && (a.Title ?? "") == (b.Title ?? "") && (a.Purpose ?? "") == (b.Purpose ?? "")
        && (a.Preconditions ?? "") == (b.Preconditions ?? "") && (a.ExpectedResult ?? "") == (b.ExpectedResult ?? "")
        && a.Steps.SequenceEqual(b.Steps) && a.StepExpected.Select(x => x ?? "").SequenceEqual(b.StepExpected.Select(x => x ?? "")) && a.Origin == b.Origin;
}
