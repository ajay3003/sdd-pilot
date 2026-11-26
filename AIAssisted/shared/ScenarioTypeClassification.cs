using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace BirkNext.TestPlanning;

// ── Acceptance scenario type (2026-11-26) ────────────────────────────────────────────────────────────────────────────────────────────────────
// Functional / non-functional / mixed / unclassified, per ACCEPTANCE SCENARIO (the classification unit), from what the scenario itself asserts.
// One shared, deterministic rule set (rule version below) used by Finn og forstå, Testoversikt and the server; a reviewer's decision overrides
// the proposal. Independent of the test method (Manual/Technical), the test level, the story's task status and test preparation.

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ScenarioRequirementType { Functional, NonFunctional, Mixed, Unclassified }

/// <summary>Quality attributes a non-functional or mixed scenario verifies. Accessibility (universell utforming) and Availability (oppetid) are distinct.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum QualityAttribute { Performance, Security, Reliability, Availability, Accessibility, Usability, Scalability, Maintainability, Observability, Interoperability, Compatibility, Other }

/// <summary>Where the classification comes from (strongest first).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ScenarioTypeBasis
{
    /// <summary>A reviewer confirmed or changed it.</summary>
    Reviewed,
    /// <summary>The scenario names its own type ("(NFR)", "Non-functional", "Ikke-funksjonell").</summary>
    Explicit,
    /// <summary>Every requirement the scenario links is a non-functional requirement (NFR-…), or quality wording in the linked NFR text.</summary>
    RequirementLink,
    /// <summary>The scenario's own outcome (Then) and action (When), clause by clause.</summary>
    ScenarioContent,
    /// <summary>Nothing reliable: not guessed.</summary>
    Unresolved,
}

public sealed record ScenarioTypeResult(ScenarioRequirementType Type, IReadOnlyList<QualityAttribute> Qualities, ScenarioTypeBasis Basis, string Explanation, IReadOnlyList<string> Evidence)
{
    public string RuleVersion => ScenarioTypeClassifier.RuleVersion;
    public bool Reviewed => Basis == ScenarioTypeBasis.Reviewed;
}

/// <summary>A reviewer's decision on one scenario's type (append-only in the story grouping document, newest wins).</summary>
public sealed record ScenarioTypeDecision(string ScenarioId, string? StoryKey, ScenarioRequirementType? Type, List<QualityAttribute> Qualities, string? Reason, string By, DateTimeOffset At,
    string? ScenarioFingerprint = null);

public static partial class ScenarioTypeClassifier
{
    public const string RuleVersion = "scenario-type/1";

    public static string Label(ScenarioRequirementType t) => t switch
    {
        ScenarioRequirementType.Functional => "Funksjonell",
        ScenarioRequirementType.NonFunctional => "Ikke-funksjonell",
        ScenarioRequirementType.Mixed => "Blandet",
        _ => "Uavklart",
    };

    /// <summary>Group headers when grouping by scenario type.</summary>
    public static string GroupLabel(ScenarioRequirementType t) => t switch
    {
        ScenarioRequirementType.Functional => "Funksjonelle",
        ScenarioRequirementType.NonFunctional => "Ikke-funksjonelle",
        ScenarioRequirementType.Mixed => "Blandede",
        _ => "Uavklarte",
    };

    public static string Label(QualityAttribute q) => q switch
    {
        QualityAttribute.Performance => "Ytelse",
        QualityAttribute.Security => "Sikkerhet",
        QualityAttribute.Reliability => "Pålitelighet",
        QualityAttribute.Availability => "Tilgjengelighet / oppetid",
        QualityAttribute.Accessibility => "Universell utforming",
        QualityAttribute.Usability => "Brukervennlighet",
        QualityAttribute.Scalability => "Skalerbarhet",
        QualityAttribute.Maintainability => "Vedlikeholdbarhet",
        QualityAttribute.Observability => "Observabilitet",
        QualityAttribute.Interoperability => "Samhandling",
        QualityAttribute.Compatibility => "Kompatibilitet",
        _ => "Annet",
    };

    public static string BasisLabel(ScenarioTypeBasis b) => b switch
    {
        ScenarioTypeBasis.Reviewed => "Bekreftet av gjennomgang",
        ScenarioTypeBasis.Unresolved => "Trenger avklaring",
        _ => "Foreslått fra kildegrunnlag",
    };

    /// <summary>The short row label: «Ikke-funksjonell · Ytelse»; functional scenarios need no label (the common case stays quiet).</summary>
    public static string? RowLabel(ScenarioTypeResult r) => r.Type switch
    {
        ScenarioRequirementType.Functional => null,
        ScenarioRequirementType.Unclassified => "Type uavklart",
        _ => r.Qualities.Count == 0 ? Label(r.Type) : $"{Label(r.Type)} · {string.Join(", ", r.Qualities.Take(2).Select(Label))}",
    };

    public static readonly ScenarioRequirementType[] Order = [ScenarioRequirementType.Functional, ScenarioRequirementType.NonFunctional, ScenarioRequirementType.Mixed, ScenarioRequirementType.Unclassified];

    // ── Rules ───────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [GeneratedRegex(@"\((?:NFR|non[- ]functional|ikke[- ]funksjonell)\)|\[(?:NFR|non[- ]functional|ikke[- ]funksjonell)\]|^\s*(?:NFR|Non[- ]functional|Ikke[- ]funksjonell)\b\s*[:—-]", RegexOptions.IgnoreCase)]
    private static partial Regex ExplicitNfrRe();

    // Quality assertions: the clause asserts a quality property, not a business outcome. Terms are quality-specific; a mere system action
    // ("the API returns …") is never one, and "authorised"/"code 6/7" business rules are functional behaviour (security-relevant, not NFR).
    private static readonly (QualityAttribute Quality, Regex Pattern)[] QualityRules =
    [
        (QualityAttribute.Performance, new(@"\b(?:within|under|below|less than|innen|under|på under|maks(?:imalt)?)\s+\d+(?:[.,]\d+)?\s*(?:ms|millisecond\w*|millisekund\w*|s|sec\w*|second\w*|sekund\w*|minut\w*)\b|\b(?:p9[059]|response time|responstid|svartid|latency|throughput|ytelse|performance)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)),
        (QualityAttribute.Accessibility, new(@"\b(?:WCAG|screen[- ]reader\w*|skjermleser\w*|keyboard|tastatur\w*|tab(?:-| )?(?:key|order|rekkefølge)|focus(?:ed|es|able)?|fokus\w*|contrast|kontrast\w*|aria-[a-z]+|alt[- ]text|skip(?:[- ]to| navigation| link)|press(?:es|ing)?\s+(?:the\s+)?Tab\b|hopp til hovedinnhold|universell utforming|accessib\w*|tilgjengelig for (?:alle|skjermleser))\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)),
        (QualityAttribute.Availability, new(@"\b(?:readiness|liveness|health[- ]?checks?|helsesjekk\w*|/health\w*|uptime|oppetid|failover|high availability|høy tilgjengelighet|degraded|Unhealthy|Healthy)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)),
        (QualityAttribute.Reliability, new(@"\b(?:retr(?:y|ies|ied)|backoff|back-off|dead[- ]letter\w*|resilien\w*|survives?|overlever|not (?:be )?lost|ikke (?:går )?tapt|recover(?:s|ed|y)?|gjenopprett\w*|self[- ]heal\w*|selvhel\w*|at[- ]least[- ]once|exactly[- ]once)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)),
        (QualityAttribute.Security, new(@"\b(?:encrypt\w*|kryptert\w*|krypter\w*|TLS|HTTPS only|never (?:logged|exposed|stored in plain\w*)|aldri (?:logget|logges|eksponert)|not (?:appear|written) in (?:the )?logs?|secrets?|hemmelighet\w*|PII|injection|XSS|CSRF)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)),
        (QualityAttribute.Usability, new(@"\b(?:colou?r|farge\w*|visual\w*|visuell\w*|styling|stil(?:en)?|font\w*|typograf\w*|spacing|avstand\w*|consistent (?:look|style|appearance)|konsistent\w*|responsive|mobile (?:layout|view)|look and feel|design tokens?|hover|ikon(?:et)? vises tydelig)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)),
        (QualityAttribute.Observability, new(@"\b(?:telemetry|telemetri|metric\w*|metrikk\w*|trace\w*|tracing|sporing|correlation[- ]?id|korrelasjons[- ]?id\w*|structured log\w*|Application Insights|log(?:ged|s)? (?:with|an?|the) (?:entry|event|warning|error)|logges med)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)),
        (QualityAttribute.Scalability, new(@"\b(?:\d+\s+(?:concurrent|simultaneous|samtidige)\s+\w+|concurrent users|samtidige brukere|scales?|skaler\w*|under load|under last)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)),
        (QualityAttribute.Compatibility, new(@"\b(?:backward[s]?[- ]compatib\w*|bakoverkompatib\w*|all supported browsers|alle støttede nettlesere)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)),
    ];

    /// <summary>An observable business outcome: something is shown, returned, stored, sent, refused, navigated to or changed.</summary>
    [GeneratedRegex(@"\b(?:shows?|shown|displays?|displayed|lists?|listed|renders?|rendered|returns?|returned|responds?|stores?|stored|saves?|saved|persist\w*|creat\w+|updat\w+|delet\w+|remov\w+|sends?|sent|publish\w*|receiv\w+|reject\w*|refus\w+|accept\w*|navigat\w+|redirect\w*|filters?|filtered|sorts?|sorted|marks?|marked|sets?|calculat\w+|contains?|includes?|is (?:visible|hidden|disabled|enabled)|hidden|disabled|enabled|appears?|opens?|closes?|records?|recorded|generat\w+|produces?|forward\w*|logged|logs|clear(?:s|ed)?|reset|restor\w*|chang(?:es|ed)|becom\w*|moves?|moved|tømme[st]|nullstill\w*|trigger\w*|alert(?:s|ed)?|advanc\w*|remains?|counted|answers?|(?:is|are) (?:not )?required|requires|does not appear|status (?:code )?\d{3}|\b(?:200|201|202|204|400|401|403|404|409|422)\b|vises|visning|vis(?:er|t)|returner\w*|lagre\w*|opprett\w*|oppdater\w*|slett\w*|sende?s|sendt|mottar|mottatt|avvis\w*|godta\w*|navigerer|filtrer\w*|sorter\w*|merke\w*|beregne\w*|inneholder|skjul\w*|åpne\w*|registrer\w*|får|blir|videresend\w*|logge[st]|varsl\w*|forblir|telles|svarer|kreves|krever)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FunctionalOutcomeRe();

    [GeneratedRegex(@"\s*(?:;|,\s*and\s+|\s+and\s+(?=(?:the|a|an|it|its|no|each|every|all|this|that|[A-Z])\b)|,\s*og\s+|\s+og\s+(?=(?:den|det|de|en|et|ingen|hver|alle|[A-ZÆØÅ])\b)|\.\s+)", RegexOptions.CultureInvariant)]
    private static partial Regex ClauseSplitRe();

    /// <summary>A concrete, checkable outcome inside a quality clause (an HTTP status, «returns», a quoted message): the clause then also asserts
    /// functional behaviour («returns HTTP 503 … not ready» = availability AND API behaviour).</summary>
    [GeneratedRegex(@"\bHTTP\s*\d{3}\b|\bstatus(?:\s+code)?\s*\d{3}\b|\b(?:200|201|202|204|400|401|403|404|409|422|500|503)\b|\breturns?\b|\breturnerer\b|""[^""]{2,}""|«[^»]{2,}»|“[^”]{2,}”", RegexOptions.IgnoreCase)]
    private static partial Regex ConcreteOutcomeRe();

    [GeneratedRegex(@"^NFR\b|^NF-", RegexOptions.IgnoreCase)]
    private static partial Regex NfrIdRe();

    private static string? Field(DiscoveredScenario c, string key) => c.Template.FirstOrDefault(f => f.Key == key)?.Value is { Length: > 0 } v ? v : null;

    /// <summary>The proposal for one scenario from its own content, then a reviewer's decision (when given, and made for this scenario text) wins.</summary>
    public static ScenarioTypeResult Classify(DiscoveredScenario c, ScenarioTypeDecision? decision = null, IReadOnlyDictionary<string, string>? requirementTexts = null)
    {
        var proposed = Propose(c, requirementTexts);
        if (decision is not { Type: { } reviewedType }) return proposed;
        var stale = decision.ScenarioFingerprint is { Length: > 0 } fp && c.Fingerprint.Length > 0 && fp != c.Fingerprint;
        if (stale)
            return proposed with { Explanation = $"{proposed.Explanation} En tidligere gjennomgang («{Label(reviewedType)}», {decision.By}) gjaldt en annen versjon av scenarioet og må gjøres på nytt." };
        return new(reviewedType, decision.Qualities, ScenarioTypeBasis.Reviewed,
            $"Bekreftet av {decision.By} {decision.At.ToLocalTime():dd.MM.yyyy}{(string.IsNullOrWhiteSpace(decision.Reason) ? "" : $": {decision.Reason}")}. Forslaget fra kilden var «{Label(proposed.Type)}».",
            proposed.Evidence);
    }

    /// <summary>The automatic proposal (deterministic for one scenario text and <see cref="RuleVersion"/>).</summary>
    public static ScenarioTypeResult Propose(DiscoveredScenario c, IReadOnlyDictionary<string, string>? requirementTexts = null)
    {
        var then = Field(c, "then") ?? c.ExpectedBehaviour;
        var when = Field(c, "when") ?? c.Steps.FirstOrDefault()?.Action;
        // Level 1: the scenario names its own type.
        if (ExplicitNfrRe().IsMatch(c.Title) || then is not null && ExplicitNfrRe().IsMatch(then))
        {
            var q = Qualities($"{c.Title} {when} {then}");
            return new(ScenarioRequirementType.NonFunctional, q, ScenarioTypeBasis.Explicit, "Scenarioet oppgir selv at det er ikke-funksjonelt.", [$"Tittel: «{c.Title}»"]);
        }
        // Level 2: every linked requirement is an NFR (explicit in the source); its wording names the quality.
        if (c.RequirementIds.Count > 0 && c.RequirementIds.All(r => NfrIdRe().IsMatch(r)))
        {
            var text = string.Join(" ", c.RequirementIds.Select(r => requirementTexts?.GetValueOrDefault(r) ?? ""));
            var q = Qualities($"{text} {then}");
            var functional = then is not null && Clauses(then).Any(cl => FunctionalOutcomeRe().IsMatch(cl) && !QualityRules.Any(r => r.Pattern.IsMatch(cl)));
            return new(functional ? ScenarioRequirementType.Mixed : ScenarioRequirementType.NonFunctional, q, ScenarioTypeBasis.RequirementLink,
                functional ? $"Knyttet til ikke-funksjonelle krav ({string.Join(", ", c.RequirementIds)}), og utfallet beskriver også funksjonell oppførsel."
                    : $"Knyttet bare til ikke-funksjonelle krav ({string.Join(", ", c.RequirementIds)}).",
                [.. c.RequirementIds.Select(r => $"Krav {r}")]);
        }
        // Level 3: the scenario's own outcome, clause by clause; the action can carry the quality ("presses Tab").
        if (then is null)
            return new(ScenarioRequirementType.Unclassified, [], ScenarioTypeBasis.Unresolved, "Scenarioet oppgir ikke noe forventet utfall (Then), så typen kan ikke avgjøres.", []);
        var clauses = Clauses(then).ToList();
        var qualityClauses = clauses.Where(cl => QualityRules.Any(r => r.Pattern.IsMatch(cl))).ToList();
        // A quality clause with a concrete outcome asserts both — except universal design, where the observable result is the quality itself.
        var dual = qualityClauses.Where(cl => ConcreteOutcomeRe().IsMatch(cl) && !Qualities(cl).Contains(QualityAttribute.Accessibility)).ToList();
        var functionalClauses = clauses.Where(cl => (!qualityClauses.Contains(cl) && FunctionalOutcomeRe().IsMatch(cl)) || dual.Contains(cl)).ToList();
        var actionQuality = when is not null && QualityRules.Any(r => r.Pattern.IsMatch(when)) ? Qualities(when) : [];
        var qualities = Qualities(string.Join(" ", qualityClauses)).Concat(actionQuality).Distinct().ToList();
        var evidence = new List<string>();
        if (qualityClauses.Count > 0) evidence.Add($"Kvalitetskrav i utfallet: «{Clip(string.Join("; ", qualityClauses))}»");
        if (actionQuality.Count > 0) evidence.Add($"Handlingen: «{Clip(when!)}»");
        if (functionalClauses.Count > 0) evidence.Add($"Funksjonelt utfall: «{Clip(string.Join("; ", functionalClauses))}»");
        // An accessibility action ("using only the keyboard") makes the scenario about that quality even when its outcome is a normal result.
        if (qualityClauses.Count > 0 && functionalClauses.Count > 0)
            return new(ScenarioRequirementType.Mixed, qualities, ScenarioTypeBasis.ScenarioContent,
                $"Utfallet krever både funksjonell oppførsel og en egen kvalitetsegenskap ({string.Join(", ", qualities.Select(Label))}).", evidence);
        // An action that exercises a quality («the readiness endpoint receives a request») with a functional outcome («returns 503») asserts both.
        if (actionQuality.Count > 0 && functionalClauses.Count > 0 && !actionQuality.Contains(QualityAttribute.Accessibility))
            return new(ScenarioRequirementType.Mixed, qualities, ScenarioTypeBasis.ScenarioContent,
                $"Handlingen gjelder en kvalitetsegenskap ({string.Join(", ", actionQuality.Select(Label))}), og utfallet er funksjonell oppførsel.", evidence);
        if (qualityClauses.Count > 0 || actionQuality.Count > 0)
            return new(ScenarioRequirementType.NonFunctional, qualities, ScenarioTypeBasis.ScenarioContent,
                $"Utfallet beskriver en kvalitetsegenskap ({string.Join(", ", qualities.Select(Label))}), ikke et forretningsresultat.", evidence);
        if (functionalClauses.Count > 0)
            return new(ScenarioRequirementType.Functional, [], ScenarioTypeBasis.ScenarioContent, "Utfallet beskriver oppførsel eller et resultat som kan observeres.", evidence);
        return new(ScenarioRequirementType.Unclassified, [], ScenarioTypeBasis.Unresolved, "Utfallet er ikke tydelig nok til å avgjøre om scenarioet er funksjonelt eller ikke-funksjonelt.", [$"Utfall: «{Clip(then)}»"]);
    }

    private static IEnumerable<string> Clauses(string text) => ClauseSplitRe().Split(text).Select(s => s.Trim()).Where(s => s.Length > 0);

    private static List<QualityAttribute> Qualities(string text) => [.. QualityRules.Where(r => r.Pattern.IsMatch(text)).Select(r => r.Quality)];

    private static string Clip(string s) => s.Length > 160 ? s[..160] + "…" : s;
}
