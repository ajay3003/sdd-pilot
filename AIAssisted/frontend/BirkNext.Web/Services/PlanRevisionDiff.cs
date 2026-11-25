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
