using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace BirkNext.TestPlanning;

// =====================================================================================================================================
// Adaptive AC readiness (2026-11-26). Readiness belongs to ONE acceptance scenario or standalone documented test — never to a user story, never
// to a test case, and never an execution result. Each criterion has three independent parts: whether it APPLIES to this scenario
// (Påkrevd / Valgfritt / Ikke relevant / Ikke vurdert), whether it is MET (Oppfylt / Mangler / Ikke vurdert), and the EVIDENCE for it.
// Only required criteria form the denominator; optional and not-applicable ones never block, and «not applicable» is never «satisfied».
// Proposals (from the source and the scenario's cases) are shown as «Foreslått» and only become decisions when a tester confirms them; the
// decisions are stored append-only in the specification's grouping document (who, when, previous value via history, optimistic revision).
// Readiness is advisory: it never blocks execution in Testplaner and never changes a result, a case or an approval.
// =====================================================================================================================================

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PreparationCriterion { ExpectedBehaviour, TestData, Trigger, Verification, Environment, CaseReview }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CriterionApplicability { Required, Optional, NotApplicable, Undecided }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CriterionEvaluation { Satisfied, Unsatisfied, NotEvaluated }

/// <summary>Where a value comes from: a standard rule of the product, a proposal from the source/cases, evidence in a case, or a tester's decision.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CriterionBasis { Standard, Proposed, Source, Case, Confirmed }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PreparationStatus { Ready, NeedsPreparation, NeedsAssessment }

/// <summary>A tester's decision on one criterion of one scenario (append-only; the newest per scenario + criterion wins). Null parts keep the
/// earlier decision for that part. <see cref="ScenarioFingerprint"/> is the scenario text it was made on: a changed scenario asks for a new look.</summary>
public sealed record ScenarioPreparationDecision
{
    public string ScenarioId { get; init; } = "";
    public PreparationCriterion Criterion { get; init; }
    public CriterionApplicability? Applicability { get; init; }
    public CriterionEvaluation? Evaluation { get; init; }
    public string? Evidence { get; init; }
    public string? Reason { get; init; }
    public string? ScenarioFingerprint { get; init; }
    public string By { get; init; } = "";
    public DateTimeOffset At { get; init; }
}

/// <summary>What the product knows about the scenario without a tester's decision (computed, never stored).</summary>
public sealed record PreparationFacts
{
    public bool HasExpectedOutcome { get; init; }
    /// <summary>The source names a starting situation / test data need (Given, test data needs).</summary>
    public bool SourceNamesData { get; init; }
    /// <summary>The scenario is triggered by a message, API call, event or job (integration / E2E level, or an action that is not a UI step).</summary>
    public bool NeedsTrigger { get; init; }
    /// <summary>The scenario names special access, roles or infrastructure.</summary>
    public bool NamesAccess { get; init; }
    public int Cases { get; init; }
    public int ApprovedCases { get; init; }
    /// <summary>At least one case states how the result is observed (an observation method, or steps with expected results).</summary>
    public bool CaseStatesVerification { get; init; }
    /// <summary>At least one case links or describes test data.</summary>
    public bool CaseHasTestData { get; init; }
    public bool Standalone { get; init; }
}

public sealed record CriterionState(
    PreparationCriterion Criterion,
    CriterionApplicability Applicability, CriterionBasis ApplicabilityBasis,
    CriterionEvaluation Evaluation, CriterionBasis EvaluationBasis,
    string? Evidence, string? Reason,
    /// <summary>The proposal shown next to an undecided applicability («Foreslått: Påkrevd»); never counted.</summary>
    CriterionApplicability? Suggested, string SuggestionWhy,
    /// <summary>A tester's decision made on an earlier text of the scenario: shown «Må gjennomgås på nytt», kept, and not trusted for «Klar».</summary>
    bool Stale, ScenarioPreparationDecision? Decision);

public sealed record PreparationSummary(PreparationStatus Status, int Required, int SatisfiedRequired, int MissingRequired, int UnevaluatedRequired,
    int Undecided, int Stale, int OptionalOpen, IReadOnlyList<CriterionState> Criteria)
{
    public IEnumerable<CriterionState> InGroup(CriterionApplicability a) => Criteria.Where(c => c.Applicability == a);
}

public static partial class ScenarioPreparationEngine
{
    public const string RuleVersion = "preparation/1";
    public const int MaxEvidence = 2000, MaxReason = 500;

    public static readonly IReadOnlyList<PreparationCriterion> Order =
        [PreparationCriterion.ExpectedBehaviour, PreparationCriterion.TestData, PreparationCriterion.Trigger, PreparationCriterion.Verification, PreparationCriterion.Environment, PreparationCriterion.CaseReview];

    public static string Label(PreparationCriterion c) => c switch
    {
        PreparationCriterion.ExpectedBehaviour => "Forventet resultat",
        PreparationCriterion.TestData => "Testdata",
        PreparationCriterion.Trigger => "Kjøremåte / utløser",
        PreparationCriterion.Verification => "Verifiseringsmetode",
        PreparationCriterion.Environment => "Miljø og tilgang",
        _ => "Godkjent testtilfelle",
    };

    /// <summary>The direct action for an incomplete criterion.</summary>
    public static string Action(PreparationCriterion c) => c switch
    {
        PreparationCriterion.ExpectedBehaviour => "Bekreft forventet resultat",
        PreparationCriterion.TestData => "Angi testdata",
        PreparationCriterion.Trigger => "Angi kjøremåte",
        PreparationCriterion.Verification => "Beskriv verifisering",
        PreparationCriterion.Environment => "Angi miljø og tilgang",
        _ => "Vurder testtilfelle",
    };

    /// <summary>The question the evidence field answers.</summary>
    public static string EvidencePrompt(PreparationCriterion c) => c switch
    {
        PreparationCriterion.ExpectedBehaviour => "Hva er forventet resultat, og hvor er det bekreftet?",
        PreparationCriterion.TestData => "Hvilke testdata trengs, og hvordan skaffes de? (Ikke lim inn produksjonsdata.)",
        PreparationCriterion.Trigger => "Hvordan startes testen? (handling, inngang, forutsetninger, avhengigheter)",
        PreparationCriterion.Verification => "Hvordan kontrolleres resultatet?",
        PreparationCriterion.Environment => "Miljø, rolle/tilgang og avhengigheter (aldri passord eller tokens)",
        _ => "Merknad om testtilfellet",
    };

    public static string Label(CriterionApplicability a) => a switch
    {
        CriterionApplicability.Required => "Påkrevd",
        CriterionApplicability.Optional => "Valgfritt",
        CriterionApplicability.NotApplicable => "Ikke relevant",
        _ => "Ikke vurdert",
    };

    public static string Label(CriterionEvaluation e) => e switch
    {
        CriterionEvaluation.Satisfied => "Oppfylt",
        CriterionEvaluation.Unsatisfied => "Mangler",
        _ => "Ikke vurdert",
    };

    public static string Label(CriterionBasis b) => b switch
    {
        CriterionBasis.Standard => "Standard",
        CriterionBasis.Proposed => "Foreslått",
        CriterionBasis.Source => "Basert på kilde",
        CriterionBasis.Case => "Fra testtilfelle",
        _ => "Bekreftet av tester",
    };

    public static string Label(PreparationStatus s) => s switch
    {
        PreparationStatus.Ready => "Klar til kjøring",
        PreparationStatus.NeedsPreparation => "Trenger klargjøring",
        _ => "Må vurderes",
    };

    /// <summary>«Påkrevd · Oppfylt», «Valgfritt · Ikke vurdert», «Ikke relevant», «Relevans ikke vurdert».</summary>
    public static string StateText(CriterionState c) => c.Applicability switch
    {
        CriterionApplicability.NotApplicable => "Ikke relevant",
        CriterionApplicability.Undecided => "Relevans ikke vurdert",
        _ => $"{Label(c.Applicability)} · {Label(c.Evaluation)}",
    };

    /// <summary>«2 av 4 påkrevde punkter oppfylt», «Alle påkrevde punkter oppfylt», «Ingen påkrevde punkter» (never «0 av 0»).</summary>
    public static string CountText(PreparationSummary s) => s.Required == 0 ? "Ingen påkrevde punkter"
        : s.SatisfiedRequired == s.Required ? $"Alle påkrevde punkter oppfylt ({s.Required})"
        : $"{s.SatisfiedRequired} av {s.Required} påkrevde punkter oppfylt";

    /// <summary>The newest decision per criterion for one scenario (each part keeps its own newest value).</summary>
    public static IReadOnlyDictionary<PreparationCriterion, ScenarioPreparationDecision> Current(IEnumerable<ScenarioPreparationDecision>? decisions, string scenarioId)
    {
        var result = new Dictionary<PreparationCriterion, ScenarioPreparationDecision>();
        foreach (var d in (decisions ?? []).Where(d => d.ScenarioId == scenarioId))
            result[d.Criterion] = result.TryGetValue(d.Criterion, out var before)
                ? d with { Applicability = d.Applicability ?? before.Applicability, Evaluation = d.Applicability == CriterionApplicability.NotApplicable ? null : d.Evaluation ?? before.Evaluation,
                    Evidence = d.Evidence ?? before.Evidence, Reason = d.Reason ?? before.Reason }
                : d;
        return result;
    }

    /// <summary>The readiness of one scenario: the standard rules and the proposals from <paramref name="facts"/>, overridden per part by the
    /// tester's current decisions. Deterministic; reads nothing and writes nothing.</summary>
    public static PreparationSummary Evaluate(PreparationFacts facts, IEnumerable<ScenarioPreparationDecision>? decisions, string scenarioId, string? fingerprint)
    {
        var current = Current(decisions, scenarioId);
        var states = Order.Select(c => State(c, facts, current.GetValueOrDefault(c), fingerprint)).ToList();
        var required = states.Where(s => s.Applicability == CriterionApplicability.Required).ToList();
        var missing = required.Count(s => s.Evaluation == CriterionEvaluation.Unsatisfied);
        var unevaluated = required.Count(s => s.Evaluation == CriterionEvaluation.NotEvaluated);
        var undecided = states.Count(s => s.Applicability == CriterionApplicability.Undecided);
        var stale = states.Count(s => s.Stale);
        var optionalOpen = states.Count(s => s.Applicability == CriterionApplicability.Optional && s.Evaluation != CriterionEvaluation.Satisfied);
        var status = missing > 0 ? PreparationStatus.NeedsPreparation
            : undecided > 0 || unevaluated > 0 || stale > 0 ? PreparationStatus.NeedsAssessment
            : PreparationStatus.Ready;
        return new(status, required.Count, required.Count(s => s.Evaluation == CriterionEvaluation.Satisfied), missing, unevaluated, undecided, stale, optionalOpen, states);
    }

    private static CriterionState State(PreparationCriterion c, PreparationFacts f, ScenarioPreparationDecision? d, string? fingerprint)
    {
        var (standard, standardBasis, suggested, why) = Proposal(c, f);
        var (evaluation, evaluationBasis, evidence) = Derived(c, f);
        var stale = d is not null && d.ScenarioFingerprint is { Length: > 0 } fp && fingerprint is { Length: > 0 } && fp != fingerprint;
        var applicability = d?.Applicability ?? standard;
        var applicabilityBasis = d?.Applicability is not null ? CriterionBasis.Confirmed : standardBasis;
        if (d?.Evaluation is { } e) (evaluation, evaluationBasis) = (e, CriterionBasis.Confirmed);
        if (applicability == CriterionApplicability.NotApplicable) (evaluation, evaluationBasis) = (CriterionEvaluation.NotEvaluated, applicabilityBasis);
        return new(c, applicability, applicabilityBasis, evaluation, evaluationBasis, d?.Evidence ?? evidence, d?.Reason,
            applicability == CriterionApplicability.Undecided ? suggested : null, why, stale, d);
    }

    /// <summary>Applicability without a decision: expected result and verification are normally required (product standard); a test case
    /// review is optional (cases are optional); test data, trigger and environment depend on the scenario and stay undecided with a proposal.</summary>
    private static (CriterionApplicability Value, CriterionBasis Basis, CriterionApplicability? Suggested, string Why) Proposal(PreparationCriterion c, PreparationFacts f) => c switch
    {
        PreparationCriterion.ExpectedBehaviour => (CriterionApplicability.Required, CriterionBasis.Standard, null, "Et scenario trenger et pålitelig forventet resultat."),
        PreparationCriterion.Verification => (CriterionApplicability.Required, CriterionBasis.Standard, null, "Resultatet må kunne kontrolleres."),
        PreparationCriterion.CaseReview => (CriterionApplicability.Optional, CriterionBasis.Standard, null, "Testtilfeller er valgfrie; scenarioet kan testes uten."),
        PreparationCriterion.TestData => (CriterionApplicability.Undecided, CriterionBasis.Proposed,
            f.SourceNamesData ? CriterionApplicability.Required : CriterionApplicability.Optional,
            f.SourceNamesData ? "Kilden beskriver en utgangssituasjon som krever bestemte data." : "Kilden nevner ingen bestemte data."),
        PreparationCriterion.Trigger => (CriterionApplicability.Undecided, CriterionBasis.Proposed,
            f.NeedsTrigger ? CriterionApplicability.Required : CriterionApplicability.Optional,
            f.NeedsTrigger ? "Scenarioet startes av en melding, et kall, en hendelse eller en jobb." : "Ser ut til å kunne startes som en vanlig manuell handling."),
        _ => (CriterionApplicability.Undecided, CriterionBasis.Proposed,
            f.NamesAccess ? CriterionApplicability.Required : CriterionApplicability.Optional,
            f.NamesAccess ? "Kilden nevner bestemt tilgang, rolle eller infrastruktur." : "Vanlig testtilgang ser ut til å holde."),
    };

    /// <summary>Evaluation from existing content: never «Oppfylt» without a tester (source text and case content are evidence to confirm). Only the
    /// optional case review reflects the cases' approval, and it never decides the status.</summary>
    private static (CriterionEvaluation, CriterionBasis, string?) Derived(PreparationCriterion c, PreparationFacts f) => c switch
    {
        // Case content is shown as evidence the tester confirms with one click — never counted by itself, so every tab (with or without the
        // plan's cases loaded) computes the same status.
        PreparationCriterion.ExpectedBehaviour when f.ApprovedCases > 0 && f.HasExpectedOutcome => (CriterionEvaluation.NotEvaluated, CriterionBasis.Case, "Et godkjent testtilfelle har forventet resultat — bekreft at det gjelder scenarioet."),
        PreparationCriterion.ExpectedBehaviour when f.HasExpectedOutcome => (CriterionEvaluation.NotEvaluated, CriterionBasis.Source, "Kilden oppgir et forventet resultat — bekreft at det er tydelig nok til å teste."),
        PreparationCriterion.Verification when f.CaseStatesVerification => (CriterionEvaluation.NotEvaluated, CriterionBasis.Case, "Et testtilfelle beskriver hvordan resultatet kontrolleres — bekreft at det holder."),
        // Linked test data is evidence, not proof the data exists in the environment: the tester confirms availability.
        PreparationCriterion.TestData when f.CaseHasTestData => (CriterionEvaluation.NotEvaluated, CriterionBasis.Case, "Et testtilfelle har koblede testdata — bekreft at de finnes i testmiljøet."),
        PreparationCriterion.CaseReview when f.Cases == 0 => (CriterionEvaluation.NotEvaluated, CriterionBasis.Standard, "Ingen testtilfelle — scenarioet kan testes uten."),
        PreparationCriterion.CaseReview when f.ApprovedCases > 0 => (CriterionEvaluation.Satisfied, CriterionBasis.Case, $"{f.ApprovedCases} av {f.Cases} testtilfeller er godkjent."),
        PreparationCriterion.CaseReview => (CriterionEvaluation.Unsatisfied, CriterionBasis.Case, $"Ingen av {f.Cases} testtilfeller er godkjent ennå."),
        _ => (CriterionEvaluation.NotEvaluated, CriterionBasis.Standard, null),
    };

    [GeneratedRegex(@"\b(passord|password|pwd|secret|hemmelighet|token|api[_\- ]?key|client[_\- ]?secret|connection\s*string)\b\s*[:=]\s*\S+", RegexOptions.IgnoreCase)]
    private static partial Regex SecretRe();

    /// <summary>Why a decision is refused (null = valid): «Ikke relevant» needs a reason, texts are bounded, and no secret is ever stored.</summary>
    public static string? Validate(CriterionApplicability? applicability, CriterionEvaluation? evaluation, string? evidence, string? reason)
    {
        if (applicability == CriterionApplicability.NotApplicable && string.IsNullOrWhiteSpace(reason)) return "Skriv kort hvorfor punktet ikke er relevant.";
        if (applicability == CriterionApplicability.NotApplicable && evaluation is not null) return "Et punkt som ikke er relevant, vurderes ikke som oppfylt eller manglende.";
        if ((evidence?.Length ?? 0) > MaxEvidence || (reason?.Length ?? 0) > MaxReason) return "Teksten er for lang.";
        if (SecretRe().IsMatch(evidence ?? "") || SecretRe().IsMatch(reason ?? "")) return "Ikke lagre passord, tokens eller andre hemmeligheter her.";
        return null;
    }

    /// <summary>Where a scenario's decisions are stored: its specification (an AC) or its own test document (a standalone documented test).</summary>
    public static string? DocumentKey(DiscoveredScenario s) =>
        s.Story?.SpecificationPath is { Length: > 0 } spec ? spec
        : s.CanonicalKey.StartsWith("testspec:", StringComparison.Ordinal) ? s.CanonicalKey["testspec:".Length..].Split('#')[0]
        : s.Evidence.Select(e => e.Reference).FirstOrDefault(r => r.Contains("/specs/", StringComparison.OrdinalIgnoreCase))?.Split('#')[0];

    // ── Facts from a discovered scenario and its cases (shared by every tab) ──

    [GeneratedRegex(@"\b(rolle|role|tilgang|access|tilgangsstyring|autoris|authori|sertifikat|certificate|VPN|brannmur|firewall|miljø(et)? med|service ?bus|event ?hub|kø|queue|database)\b", RegexOptions.IgnoreCase)]
    private static partial Regex AccessRe();

    [GeneratedRegex(@"\b(PUT|POST|GET|DELETE|PATCH)\s+/|\b(melding|message|hendelse|event|mottas|received|publiser|publish|kall(er)?|calls?|API|jobb|job|batch|endepunkt|endpoint|webhook|kø|queue)\b", RegexOptions.IgnoreCase)]
    private static partial Regex TriggerRe();

    public static PreparationFacts FactsOf(DiscoveredScenario s, IReadOnlyList<TestCaseDraft> cases)
    {
        var given = string.Join(" ", s.Preconditions.Concat(s.Template.Where(t => t.Key == "given").Select(t => t.Value ?? "")));
        var when = string.Join(" ", s.Steps.Select(x => x.Action).Concat(s.Template.Where(t => t.Key == "when").Select(t => t.Value ?? "")));
        var text = $"{s.Title} {given} {when} {s.ExpectedBehaviour}";
        var level = TestLevelClassifier.Of(s.Type).Level;
        var active = cases.Where(c => c.Review is not (TestCaseReviewState.Excluded or TestCaseReviewState.Superseded)).ToList();
        return new PreparationFacts
        {
            HasExpectedOutcome = !string.IsNullOrWhiteSpace(s.ExpectedBehaviour),
            SourceNamesData = !string.IsNullOrWhiteSpace(given) || s.TestDataNeeds.Count > 0,
            NeedsTrigger = level is ScenarioTestLevel.Integration or ScenarioTestLevel.EndToEnd || TriggerRe().IsMatch(when),
            NamesAccess = AccessRe().IsMatch(text),
            Cases = active.Count,
            ApprovedCases = active.Count(c => c.Review == TestCaseReviewState.Accepted),
            CaseStatesVerification = active.Any(c => !string.IsNullOrWhiteSpace(c.Edits.ObservationMethod) || !string.IsNullOrWhiteSpace(c.Generated.ObservationMethod)),
            CaseHasTestData = active.Any(c => c.TestData.Count > 0),
            Standalone = s.Story is null,
        };
    }
}
