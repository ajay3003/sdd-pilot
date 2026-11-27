using BirkNext.TestPlanning;

namespace BirkNext.Web.Services;

/// <summary>
/// «Se endringer» in Versjoner (2026-11-25): what changed between two stored revisions of one plan, in the tester's words. Only from the two stored
/// revisions (both are read from the plan's history, which is never rewritten); nothing is reconstructed or guessed.
/// FUN-016 (2026-11-27): the plan's scenario occurrences too — added, removed and moved between testgrupper, with or without a test case, by their
/// canonical scenario id (<see cref="PlanOccurrences"/>) — not only its test cases. Execution results are not part of a revision and never appear here.
/// </summary>
public static class PlanRevisionDiff
{
    public const string PlanKind = "plan", SuiteKind = "suite", ScenarioKind = "scenario", CaseKind = "case";

    /// <summary>One change; <see cref="Kind"/> groups it (plan, testgruppe, scenario, testtilfelle).</summary>
    public sealed record Change(string? CaseTitle, string Text, string Kind = CaseKind);

    /// <summary>A scenario as the tester knows it: «US1 · AC 4 — title», or the title of a standalone test (its own id, e.g. TEST-E-01, is in it).</summary>
    public static string ScenarioLabel(ScenarioOccurrence o) =>
        (o.StoryId is { } st ? $"{st}{(o.NumberInStory is { } n ? $" · AC {n}" : "")} — " : "") + (string.IsNullOrWhiteSpace(o.Title) ? o.ScenarioId ?? o.Key : o.Title);

    private static string SuiteName(TestPlanDraft plan, string? suiteId) =>
        suiteId is not null && plan.Suites.Any(s => s.Id == suiteId) ? PlanStructure.PathOf(plan.Suites, suiteId) : "ingen testgruppe";

    public static IReadOnlyList<Change> Compare(TestPlanDraft older, TestPlanDraft newer)
    {
        var list = new List<Change>();
        if (!string.Equals(older.PlanName, newer.PlanName, StringComparison.Ordinal)) list.Add(new(null, $"Navnet er endret fra «{older.PlanName}» til «{newer.PlanName}».", PlanKind));
        // Suite structure (2026-11-26): added, removed, renamed and moved suites, by suite id.
        var oldSuites = older.Suites.ToDictionary(x => x.Id, StringComparer.Ordinal);
        foreach (var x in newer.Suites)
        {
            if (!oldSuites.TryGetValue(x.Id, out var was)) { list.Add(new(null, $"Testgruppen «{PlanStructure.PathOf(newer.Suites, x.Id)}» er lagt til.", SuiteKind)); continue; }
            if (was.Name != x.Name) list.Add(new(null, $"Testgruppen «{was.Name}» heter nå «{x.Name}».", SuiteKind));
            if (was.ParentId != x.ParentId) list.Add(new(null, $"Testgruppen «{x.Name}» er flyttet fra «{(was.ParentId is null ? "øverst" : PlanStructure.PathOf(older.Suites, was.ParentId))}» til «{(x.ParentId is null ? "øverst" : PlanStructure.PathOf(newer.Suites, x.ParentId))}».", SuiteKind));
        }
        foreach (var x in older.Suites.Where(o => newer.Suites.All(n => n.Id != o.Id))) list.Add(new(null, $"Testgruppen «{PlanStructure.PathOf(older.Suites, x.Id)}» er fjernet.", SuiteKind));

        // Scenario occurrences (FUN-016): by key (the canonical scenario id; a hand-written case without a scenario is its own occurrence).
        var oldOcc = PlanOccurrences.Of(older).ToDictionary(o => o.Key, StringComparer.Ordinal);
        var newOcc = PlanOccurrences.Of(newer).ToDictionary(o => o.Key, StringComparer.Ordinal);
        foreach (var o in newOcc.Values.Where(o => !oldOcc.ContainsKey(o.Key)))
            list.Add(new(ScenarioLabel(o), $"Scenario lagt til i «{SuiteName(newer, o.SuiteId)}» {(o.CaseIds.Count == 0 ? "uten testtilfelle" : "med testtilfelle")}.", ScenarioKind));
        foreach (var o in oldOcc.Values.Where(o => !newOcc.ContainsKey(o.Key)))
            list.Add(new(ScenarioLabel(o), $"Scenario fjernet fra «{SuiteName(older, o.SuiteId)}».", ScenarioKind));
        foreach (var n in newOcc.Values)
        {
            if (!oldOcc.TryGetValue(n.Key, out var o)) continue;
            if (!string.Equals(o.SuiteId, n.SuiteId, StringComparison.Ordinal))
                list.Add(new(ScenarioLabel(n), $"Scenario flyttet fra «{SuiteName(older, o.SuiteId)}» til «{SuiteName(newer, n.SuiteId)}».", ScenarioKind));
            if (o.CaseIds.Count == 0 && n.CaseIds.Count > 0) list.Add(new(ScenarioLabel(n), "Testtilfelle forberedt for scenarioet (scenarioet står i samme testgruppe).", ScenarioKind));
        }
        var before = older.Cases.ToDictionary(c => c.Source.ScenarioId, StringComparer.Ordinal);
        var after = newer.Cases.ToDictionary(c => c.Source.ScenarioId, StringComparer.Ordinal);
        foreach (var c in newer.Cases.Where(c => !before.ContainsKey(c.Source.ScenarioId))) list.Add(new(c.EffectiveTitle, "Testtilfelle lagt til."));
        foreach (var c in older.Cases.Where(c => !after.ContainsKey(c.Source.ScenarioId))) list.Add(new(c.EffectiveTitle, "Testtilfelle fjernet."));
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
            if (o.SuiteId != n.SuiteId) list.Add(new(n.EffectiveTitle, $"Testtilfellet er flyttet fra testgruppen «{(older.Suites.Any(x => x.Id == o.SuiteId) ? PlanStructure.PathOf(older.Suites, o.SuiteId) : "ingen")}» til «{(newer.Suites.Any(x => x.Id == n.SuiteId) ? PlanStructure.PathOf(newer.Suites, n.SuiteId) : "ingen")}»."));
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
