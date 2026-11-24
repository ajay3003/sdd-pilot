using System.Text.RegularExpressions;
using BirkNext.TestPlanning;

namespace BirkNext.Api.Services.TestPlanning;

/// <summary>
/// Finn og forstå's work queues (<see cref="ScenarioWorkQueue"/>), from facts the discovery and the saved plans already hold — no new engine, no
/// score. Eligibility and ranking are separate: a scenario is recommended only when it is ready for test-case preparation (relevant facts known,
/// expected result stated and supported, nothing to clarify, current source, no current test case); among those, the order follows the user story's
/// declared priority, then how the expected result is supported, then described preconditions, then the specification's own order. Every
/// membership carries plain Norwegian reasons. Ready to prepare is never presented as ready to execute: test data, access and observation needs
/// are listed separately (<see cref="ScenarioWorkItem.ExecutionNeeds"/>).
/// </summary>
public static partial class ScenarioWorkQueues
{
    [GeneratedRegex(@"^\s*P\s*(\d+)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex PriorityPattern();

    /// <summary>The declared priority as a number (P1 → 1); null when none is declared or it is not in the P&lt;n&gt; form.</summary>
    public static int? PriorityNumber(string? priority) =>
        priority is not null && PriorityPattern().Match(priority) is { Success: true } m && int.TryParse(m.Groups[1].Value, out var n) ? n : null;

    /// <summary>An expected result a tester can prepare from: stated in the specification or a contract, by an existing test, or confirmed by a person.</summary>
    public static bool ExpectedSupported(DiscoveredScenario c) => c.ExpectedBehaviour is { Length: > 0 }
        && c.Confidence is CandidateConfidence.ExplicitSpecification or CandidateConfidence.ContractDerived or CandidateConfidence.ExistingTest or CandidateConfidence.HumanConfirmed;

    /// <summary>What must be clarified before a test case can be prepared. Empty = nothing.</summary>
    public static List<string> Clarifications(DiscoveredScenario c, ScenarioCaseCoverage? coverage)
    {
        var list = new List<string>();
        if (c.ExpectedBehaviour is not { Length: > 0 }) list.Add("Forventet resultat mangler i kilden");
        else if (!ExpectedSupported(c))
            list.Add(c.Confidence switch
            {
                CandidateConfidence.SourceDerived => "Forventet resultat er bare utledet fra koden — må bekreftes",
                CandidateConfidence.CandidateAssociation => "Kilden er ikke gjennomgått — forventet resultat må bekreftes",
                _ => "Utilstrekkelig grunnlag for forventet resultat",
            });
        if (c.Conflicts.Count > 0) list.Add("Kildene beskriver ulike forventede resultater");
        foreach (var gap in c.Gaps.Select(g => g.Kind).Distinct())
        {
            var text = gap switch
            {
                EvidenceGapKind.BusinessExpectationUnclear => "Forretningsforventningen er uklar",
                EvidenceGapKind.ContradictoryEvidence => "Kildene er motstridende",
                EvidenceGapKind.AssociationNeedsReview => "Koblingen til kilden må bekreftes",
                _ => null,
            };
            if (text is not null && !list.Contains(text)) list.Add(text);
        }
        if (c.OpenQuestions.Count > 0) list.Add(c.OpenQuestions.Count == 1 ? "1 åpent spørsmål" : $"{c.OpenQuestions.Count} åpne spørsmål");
        if (coverage is { Conflicts: > 0 } k) list.Add(k.Conflicts == 1 ? "Et testtilfelle har en uløst kildekonflikt" : $"{k.Conflicts} testtilfeller har en uløst kildekonflikt");
        return list;
    }

    /// <summary>What the execution still needs (does not block preparation).</summary>
    public static List<string> ExecutionNeeds(DiscoveredScenario c)
    {
        var list = new List<string>();
        if (c.TestDataNeeds.Count > 0) list.Add("Trenger testdata: " + string.Join(", ", c.TestDataNeeds.Take(3)) + (c.TestDataNeeds.Count > 3 ? " …" : ""));
        if (c.Gaps.Any(g => g.Kind == EvidenceGapKind.TestPrerequisiteMissing)) list.Add("Trenger testdata, tilgang eller oppsett før kjøring");
        if (c.Gaps.Any(g => g.Kind == EvidenceGapKind.VerificationMechanismMissing)) list.Add("Mangler en måte å observere resultatet på");
        return list;
    }

    private static string Supported(DiscoveredScenario c) => c.Confidence switch
    {
        CandidateConfidence.ExplicitSpecification => "Forventet resultat dokumentert i spesifikasjonen",
        CandidateConfidence.ContractDerived => "Forventet resultat dokumentert i API-kontrakten",
        CandidateConfidence.ExistingTest => "Forventet resultat dokumentert i en eksisterende test",
        _ => "Forventet resultat bekreftet av en person",
    };

    private static int SupportOrder(DiscoveredScenario c) => c.Confidence switch
    {
        CandidateConfidence.ExplicitSpecification or CandidateConfidence.HumanConfirmed => 0,
        CandidateConfidence.ContractDerived => 1,
        _ => 2,
    };

    /// <summary>Classifies every candidate. <paramref name="coverage"/> holds the scenarios that have saved cases; <paramref name="coverageComplete"/>
    /// false means a plan could not be read, so "no saved case" is unknown and nothing is recommended on it.</summary>
    public static List<ScenarioWorkItem> Classify(IReadOnlyList<DiscoveredScenario> candidates, IReadOnlyDictionary<string, ScenarioCaseCoverage> coverage,
        bool coverageComplete, bool discoveryCurrent)
    {
        var items = new List<(DiscoveredScenario C, ScenarioWorkItem Item)>(candidates.Count);
        foreach (var c in candidates)
        {
            coverage.TryGetValue(c.CandidateId, out var cov);
            var clarify = Clarifications(c, cov);
            var queues = new List<ScenarioWorkQueue>();
            var reasons = new List<string>();
            var current = cov?.Current ?? 0;
            var changed = cov is { SourceChanged: > 0 } || (current > 0 && !discoveryCurrent);
            if (changed)
            {
                queues.Add(ScenarioWorkQueue.SourceChanged);
                reasons.Add(cov is { SourceChanged: > 0 } s
                    ? (s.SourceChanged == 1 ? "Kildegrunnlaget er endret for 1 testtilfelle" : $"Kildegrunnlaget er endret for {s.SourceChanged} testtilfeller")
                    : "Funnet i en eldre kildeversjon enn den gjeldende");
            }
            if (clarify.Count > 0) { queues.Add(ScenarioWorkQueue.NeedsClarification); reasons.Add("Trenger avklaring før klargjøring"); }
            if (current > 0)
            {
                queues.Add(ScenarioWorkQueue.Prepared);
                reasons.Add(cov!.Approved > 0 && cov.Approved == current ? (current == 1 ? "Har 1 godkjent testtilfelle" : $"Har {current} godkjente testtilfeller")
                    : current == 1 ? "Har 1 testtilfelle" : $"Har {current} testtilfeller");
            }
            var recommended = clarify.Count == 0 && current == 0 && coverageComplete && discoveryCurrent && !changed && (cov?.Excluded ?? 0) == 0;
            if (recommended)
            {
                queues.Add(ScenarioWorkQueue.Recommended);
                var priority = PriorityNumber(c.Story?.Priority);
                reasons.Add("Mangler testtilfelle");
                reasons.Add(Supported(c));
                reasons.Add(priority == 1 ? "Høy prioritet (P1)" : priority is { } p ? $"Prioritet P{p}" : "Prioritet ikke oppgitt i kilden");
                reasons.Add(c.Preconditions.Count > 0 ? "Forutsetninger beskrevet" : "Ingen forutsetninger beskrevet");
                reasons.Add("Kildegrunnlaget er oppdatert");
                if (cov is { Archived: > 0 }) reasons.Add("Finnes også i en arkivert plan");
            }
            else if (current == 0 && clarify.Count == 0 && !changed)
            {
                // Not recommended, and no other queue explains why: say it.
                if (!coverageComplete) reasons.Add("Ukjent om det finnes testtilfeller (en plan kunne ikke leses)");
                else if (!discoveryCurrent) reasons.Add("Funnet i en eldre kildeversjon enn den gjeldende");
                else if (cov is { Excluded: > 0 }) reasons.Add("Bevisst utelatt i en plan");
            }
            items.Add((c, new ScenarioWorkItem
            {
                CandidateId = c.CandidateId, Queues = queues, Priority = c.Story?.Priority, Coverage = cov, Reasons = reasons, Clarifications = clarify,
                ExecutionNeeds = ExecutionNeeds(c),
            }));
        }
        // Ranking among the recommended only: declared priority, then how the expected result is supported, then described preconditions, then the
        // specification's own order (story, number in story), then title and id — deterministic for the same input.
        var rank = 0;
        var ranked = items.Where(x => x.Item.Queues.Contains(ScenarioWorkQueue.Recommended))
            .OrderBy(x => PriorityNumber(x.C.Story?.Priority) ?? int.MaxValue)
            .ThenBy(x => SupportOrder(x.C))
            .ThenBy(x => x.C.Preconditions.Count > 0 ? 0 : 1)
            .ThenBy(x => x.C.Story?.Ordinal ?? int.MaxValue).ThenBy(x => x.C.NumberInStory ?? int.MaxValue)
            .ThenBy(x => x.C.Title, StringComparer.Ordinal).ThenBy(x => x.C.CandidateId, StringComparer.Ordinal)
            .Select(x => x.C.CandidateId).ToDictionary(id => id, _ => ++rank, StringComparer.Ordinal);
        return [.. items.Select(x => ranked.TryGetValue(x.C.CandidateId, out var r) ? x.Item with { Rank = r } : x.Item)];
    }
}
