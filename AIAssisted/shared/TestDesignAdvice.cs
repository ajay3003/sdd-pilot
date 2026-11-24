using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace BirkNext.TestPlanning;

// ── Intelligent test design & execution readiness (2026-11-24) ──────────────────────────────────────────────────────────────────────────────
// One deterministic, bounded advisor over the scenario text BirkNext already holds (the interpreted Given/When/Then of a case or a discovered
// scenario, its open questions and the requirement rule facts of ScenarioPlanning.RuleFactsOf). It recommends HOW to test, what to verify and
// what is missing. It never creates, approves or rewrites a case, never invents an endpoint, query, queue, path, permission or tool, and never
// states an expected result the source does not state. Additional test designs are TestCaseVariantProposal (ScenarioPlanning.Variants), so
// accepting one uses the existing "add case from variant" path (re-checked by the server, duplicates suppressed by title and key).

/// <summary>How a scenario can be executed. Recommendations, not exclusive labels: a scenario can be manual-suitable and automation-recommended.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ExecutionApproach { Manual, ManualTechnical, IntegrationE2E, AutomatedRecommended, Technical, NeedsAssessment }

/// <summary>Yes / no / not known. An unknown is never counted as satisfied.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum Tri { Yes, No, Unknown }

public sealed record ApproachRecommendation(ExecutionApproach Approach, string Reason);

/// <summary>One of the manual-suitability questions (A–H) with its answer and why.</summary>
public sealed record SuitabilityAnswer(string Letter, string Question, Tri Answer, string Reason);

public sealed record ExecutionSuitability(IReadOnlyList<ApproachRecommendation> Approaches, IReadOnlyList<SuitabilityAnswer> Manual)
{
    public bool Has(ExecutionApproach a) => Approaches.Any(x => x.Approach == a);
}

/// <summary>Where an assertion can be observed, in categories (never a concrete query, queue or path).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VerificationSurface { Screen, ProcessingOutcome, PersistedState, DeliveredOutput, FlagOrReason, AbsenceAcrossSurfaces, Timing, Unknown }

/// <summary>One assertion of the expected result: what must be verified, where (category), and whether a person has confirmed how.</summary>
public sealed record VerificationPoint(string Assertion, VerificationSurface Surface, string Where, bool Negative, bool Critical, bool MethodConfirmed);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DataAvailability { Confirmed, MustBeCreated, Unknown, Blocked }

/// <summary>A test-data requirement (from the source) and its availability (from linked Test Data assets) — kept apart on purpose.</summary>
public sealed record TestDataItem(string Requirement, DataAvailability Availability, string Note);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ReadinessLevel { ReadyToPrepare, NeedsPreparation, ReadyForReview, ReadyToRun, Blocked }

public sealed record ReadinessCheck(string Name, Tri Satisfied, string Detail);

/// <summary>Execution readiness: not execution status ("Klar til kjøring" is not "bestått"). Saving a draft is never blocked by it.</summary>
public sealed record ExecutionReadiness(ReadinessLevel Level, IReadOnlyList<ReadinessCheck> Checks)
{
    public IReadOnlyList<ReadinessCheck> Missing => [.. Checks.Where(c => c.Satisfied != Tri.Yes)];
}

/// <summary>One row of a decision table: the conditions that hold, the outcome the source states (null = not established), and the row's kind.</summary>
public sealed record DecisionRow(IReadOnlyList<string> Conditions, string? Outcome, string Kind, string Basis);

public sealed record DecisionTable(IReadOnlyList<string> Conditions, IReadOnlyList<DecisionRow> Rows, IReadOnlyList<string> Precedence);

/// <summary>A cross-service end-to-end proposal: only services and checkpoints the text names; every checkpoint without a confirmed way to verify
/// it is listed as a gap.</summary>
public sealed record EndToEndProposal(string Objective, IReadOnlyList<string> Services, IReadOnlyList<string> Checkpoints, IReadOnlyList<string> Gaps);

/// <summary>The text of one scenario the advisor reads (from a prepared case or a discovered scenario).</summary>
public sealed record ScenarioFacts(string Title, string? Given, string? When, string? Then, IReadOnlyList<string> Steps, IReadOnlyList<string> OpenQuestions,
    ScenarioCandidateType? Type, string? Module, string? Capability)
{
    public string All => string.Join("\n", new[] { Title, Given, When, Then }.Concat(Steps).Where(s => !string.IsNullOrWhiteSpace(s)));
    /// <summary>The source's own words only (Given / When / Then), without BirkNext's composed steps.</summary>
    public string SourceText => string.Join("\n", new[] { Given, When, Then }.Where(s => !string.IsNullOrWhiteSpace(s)));
}

public static partial class TestDesignAdvisor
{
    public const int MaxProposals = 8, MaxMatrixRows = 12, MaxTableConditions = 6;

    // ── Facts ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    public static ScenarioFacts FactsOf(TestCaseDraft c)
    {
        string? T(string key) => c.Generated.Template.FirstOrDefault(t => t.Key == key)?.Value is { Length: > 0 } v ? v : null;
        return new(c.EffectiveTitle, T("given") ?? c.EffectivePreconditions, T("when") ?? c.EffectiveSteps.FirstOrDefault(),
            T("then") ?? c.EffectiveExpected ?? string.Join("\n", c.EffectiveStepExpected.Where(e => !string.IsNullOrWhiteSpace(e))),
            c.EffectiveSteps, c.Generated.OpenQuestions, c.Generated.CandidateType, c.Generated.GroupTitle, c.Generated.Capability);
    }

    public static ScenarioFacts FactsOf(DiscoveredScenario s)
    {
        string? T(string key) => s.Template.FirstOrDefault(t => t.Key == key)?.Value is { Length: > 0 } v ? v : null;
        return new(s.Title, T("given") ?? (s.Preconditions.Count > 0 ? string.Join("; ", s.Preconditions) : null), T("when") ?? s.Steps.FirstOrDefault()?.Action,
            T("then") ?? s.ExpectedBehaviour, [.. s.Steps.Select(x => x.Action)], s.OpenQuestions, s.Type, s.Area, s.CapabilityId);
    }

    /// <summary>A discovered scenario seen as an (unsaved) case, so the same proposals are shown before and after it is prepared.</summary>
    public static TestCaseDraft AsDraft(DiscoveredScenario s) => new()
    {
        Source = new ScenarioReference { Kind = ScenarioSourceKind.ProjectDiscovery, Owner = s.ProjectKey, ScenarioId = s.CandidateId },
        Generated = new TestCaseContent
        {
            Title = s.Title, Purpose = s.Purpose, Preconditions = s.Preconditions.Count > 0 ? string.Join("\n", s.Preconditions) : null,
            Steps = [.. s.Steps.Select(x => x.Action)], StepExpected = [.. s.Steps.Select(x => x.Expected)], ExpectedResult = s.ExpectedBehaviour,
            Template = s.Template, OpenQuestions = s.OpenQuestions, CandidateType = s.Type, Capability = s.CapabilityId, GroupTitle = s.Area, Story = s.Story,
        },
    };

    [GeneratedRegex(@"\b(click|clicks|press|presses|type|types|enter|enters|fill|fills|select|selects|open|opens|navigate|navigates|search|searches|choose|chooses|log(?:s)? in|sign(?:s)? in|upload|uploads|klikk\w*|trykk\w*|fyll\w*|velg\w*|åpne\w*|søk\w*|skriv\w*|logg\w* inn)\b", RegexOptions.IgnoreCase)]
    private static partial Regex UiAction();

    [GeneratedRegex(@"\b(page|screen|form|button|field|list|dialog|menu|tab|link|appears|shown|displayed|visible|message|side|siden|skjerm\w*|skjema\w*|knapp\w*|felt\w*|liste\w*|vises|melding\w*)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Observable();

    [GeneratedRegex(@"\b(case ?worker|saksbehandler\w*|administrator|admin|user|tester|operator|reviewer|guardian|teacher|bruker\w*|lærer\w*)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Person();

    [GeneratedRegex(@"\b(stored|persisted|saved|recorded|written|logged|published|delivered|queued|sent to|emitted|exported|retained|deleted|discarded|lagre\w*|logge\w*|publiser\w*|levert\w*|slett\w*)\b", RegexOptions.IgnoreCase)]
    private static partial Regex TechnicalOutcome();

    [GeneratedRegex(@"\b(arrives?|received|is delivered|processes|processed|executes|is triggered|runs|locks?|is locked|publishes|consumes|translat\w+|maps|cdc|event|message|webhook|job|batch|schedul\w+)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SystemTrigger();

    [GeneratedRegex(@"\b(twice|again|duplicate\w*|same message|same change|repeated|retr(?:y|ies|ied)|re-?sent|idempot\w*|concurrent\w*|simultaneous\w*|at the same time|out of order|before the original|before its original)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Repetition();

    [GeneratedRegex(@"\b(within \d+|seconds?|millisecond\w*|ms\b|response time|timeout|latency|under load|per second|throughput|large volume|\d{3,} (?:reports|messages|rows|records))\b", RegexOptions.IgnoreCase)]
    private static partial Regex TimingOrVolume();

    [GeneratedRegex(@"\b(identical|byte[- ]for[- ]byte|exactly|precisely|no transformation|same content|checksum|hash)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Precision();

    [GeneratedRegex(@"\b(Kode ?[67]|classified|classification|confidential|security level|sensitive|unauthori[sz]ed|authori[sz]\w+|permission|access|grant\w*|role\w*|tilgang\w*|`[A-Za-zÆØÅæøå]+:[A-Za-zÆØÅæøå]+`)", RegexOptions.IgnoreCase)]
    private static partial Regex Security();

    [GeneratedRegex(@"\b(not|no|never|without|none|nothing|ikke|aldri|ingen|uten)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Negation();

    // A system or service name the text names (PascalCase words and a few known system names). Only names that appear in the text are used.
    [GeneratedRegex(@"\b(?:M2LB\.)?([A-Z][a-zæøå]+(?:[A-Z][a-zæøå]+)+|Person|Altinn|BiRK|Bufdata|Utdanning|Entra|Tjeneste|Autorisasjon|Revisjon)\b")]
    private static partial Regex ServiceName();

    private static readonly HashSet<string> NotServices = new(StringComparer.Ordinal)
    {
        "BirkID", "AntallTimerFaktiskNaervaer", "AntallTimerEtterPlanen", "LeseloggHendelse", "TvangsProtokoll", "JavaScript", "TypeScript", "PostgreSQL",
    };

    /// <summary>The services/systems the scenario text names (in order of appearance, no duplicates), never guessed from a path.</summary>
    public static IReadOnlyList<string> ServicesNamed(string? text, IReadOnlyCollection<string>? known = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var found = new List<string>();
        foreach (Match m in ServiceName().Matches(text))
        {
            var name = m.Groups[1].Value;
            if (NotServices.Contains(name) || found.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            var system = Regex.IsMatch(name, "^(Person|Altinn|BiRK|Bufdata|Utdanning|Entra|Tjeneste|Autorisasjon|Revisjon)$");
            // Without the project's module list, only names that read as a system ("…Service", "…Adapter", "…Receiver" …) count: an identifier
            // such as a field or class name is not a service.
            if (known is { Count: > 0 } ? !known.Contains(name, StringComparer.OrdinalIgnoreCase) && !system
                : !system && !Regex.IsMatch(name, "(Service|Adapter|Receiver|Gateway|Module|Api)$")) continue;
            found.Add(name);
        }
        return found;
    }

    private static bool TriggerUnknown(ScenarioFacts f) =>
        f.OpenQuestions.Any(q => Regex.IsMatch(q, @"(is sent|is triggered|is brought about|refers to|Who acts).*not stated", RegexOptions.IgnoreCase))
        || string.IsNullOrWhiteSpace(f.When) && f.Steps.Count == 0;

    // ── Execution suitability ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Execution approaches with reasons, from what the scenario says (actor, trigger, observable result, precision, timing, systems) —
    /// never from a service name, path or test name alone. <paramref name="knownServices"/> = the project's module names, when known.</summary>
    public static ExecutionSuitability Suitability(ScenarioFacts f, IReadOnlyCollection<string>? knownServices = null)
    {
        var all = f.All;
        var trigger = f.When ?? f.Steps.FirstOrDefault() ?? "";
        var then = f.Then ?? "";
        var approaches = new List<ApproachRecommendation>();
        var uiAction = UiAction().IsMatch(trigger) || f.Steps.Any(s => UiAction().IsMatch(s));
        var person = Person().IsMatch(f.Given ?? "") || Person().IsMatch(trigger) || Regex.IsMatch(trigger, @"^\s*(as the|they)\b", RegexOptions.IgnoreCase);
        var observable = Observable().IsMatch(then);
        var technicalOutcome = TechnicalOutcome().IsMatch(then);
        var systemTrigger = SystemTrigger().IsMatch(trigger) && !uiAction;
        var services = ServicesNamed(all, knownServices).Where(s => !string.Equals(s, f.Module, StringComparison.OrdinalIgnoreCase)).ToList();
        var repeated = Repetition().IsMatch(all);
        var timing = TimingOrVolume().IsMatch(all);
        var precise = Precision().IsMatch(then);
        var unknownTrigger = TriggerUnknown(f);
        var noExpected = string.IsNullOrWhiteSpace(then);

        if ((uiAction || person) && observable && !technicalOutcome)
            approaches.Add(new(ExecutionApproach.Manual, "En person utfører handlingen og ser resultatet direkte."));
        if (technicalOutcome && (uiAction || person || Regex.IsMatch(trigger, @"\b(arrives?|submit\w*|send\w*)\b", RegexOptions.IgnoreCase)))
            approaches.Add(new(ExecutionApproach.ManualTechnical,
                "En tester kan starte forløpet, men " + (Negation().IsMatch(then) ? "at noe ikke skjer/ikke lagres" : "lagret eller levert resultat") + " må kontrolleres med teknisk tilgang."));
        if (services.Count >= 1 && (systemTrigger || technicalOutcome || f.Type is ScenarioCandidateType.Integration or ScenarioCandidateType.BusinessE2E))
            approaches.Add(new(ExecutionApproach.IntegrationE2E, $"Atferden går på tvers av {string.Join(", ", services.Take(4))}."));
        if (repeated || timing || precise)
            approaches.Add(new(ExecutionApproach.AutomatedRecommended,
                repeated ? "Gjentakelse/rekkefølge må styres presist og gjentas likt — automatisering gir pålitelig resultat."
                : timing ? "Tid eller volum måles best automatisk."
                : "Presis sammenligning (identisk innhold) gjøres sikrest automatisk."));
        if (systemTrigger && !person && !uiAction && !observable && technicalOutcome && !approaches.Any(a => a.Approach == ExecutionApproach.ManualTechnical))
            approaches.Add(new(ExecutionApproach.Technical, "Ingen person handler og resultatet er kun synlig i tekniske flater."));
        var arrival = Regex.IsMatch(trigger, @"\b(arrives?|submit\w*|send\w*)\b", RegexOptions.IgnoreCase);
        var processed = Regex.IsMatch(then, @"\b(accepted|rejected|flagged|returned|forwarded|receives?)\b", RegexOptions.IgnoreCase);
        if (!noExpected && arrival && processed && !approaches.Any(a => a.Approach == ExecutionApproach.ManualTechnical))
            approaches.Add(new(ExecutionApproach.ManualTechnical, "En tester kan sende inn og se svaret, men flagg, årsak og lagret resultat må kontrolleres med teknisk innsyn."));
        if (!noExpected && approaches.Count == 0 && (person || uiAction))
            approaches.Add(new(ExecutionApproach.Manual, "En person utfører handlingen; hvor resultatet vises er ikke oppgitt i kilden."));
        if (!noExpected && approaches.Count == 0 && !person && !uiAction)
            approaches.Add(new(ExecutionApproach.Technical, "Utløses av systemet (ingen person handler); resultatet kontrolleres med teknisk innsyn."));
        // An unknown trigger is a readiness gap (question A), not a reason to call the approach undecidable.
        if (approaches.Count == 0 || noExpected)
            approaches.Add(new(ExecutionApproach.NeedsAssessment, noExpected ? "Forventet resultat er ikke oppgitt i kilden." : "Kilden gir for lite til å avgjøre testmåten."));

        var manual = new List<SuitabilityAnswer>
        {
            new("A", "Kan testeren utløse atferden?", uiAction || person ? Tri.Yes : Tri.Unknown,
                uiAction || person ? "En person-handling er beskrevet." : "Utløseren er en systemhendelse eller ikke beskrevet."),
            new("B", "Kan testeren lage nødvendige testdata?", Tri.Unknown,
                string.IsNullOrWhiteSpace(f.Given) ? "Utgangssituasjonen er ikke beskrevet." : "Kravet til data er kjent; om det finnes i testmiljøet er ikke bekreftet."),
            new("C", "Kan testeren se det forventede resultatet?", noExpected ? Tri.No : observable && !technicalOutcome ? Tri.Yes : technicalOutcome ? Tri.No : Tri.Unknown,
                noExpected ? "Forventet resultat mangler." : technicalOutcome ? "Resultatet er lagret/levert/logget — krever teknisk innsyn." : observable ? "Synlig på skjerm." : "Hvor resultatet vises er ikke oppgitt."),
            new("D", "Er verktøy og tilganger kjent?", Tri.Unknown, "Ikke registrert for testmiljøet."),
            new("E", "Er forventet resultat konkret nok?", noExpected ? Tri.No : Regex.IsMatch(then, @"\b(works|correctly|as expected|appropriate|properly|fungerer|riktig)\b", RegexOptions.IgnoreCase) ? Tri.No : Tri.Yes,
                noExpected ? "Mangler." : "Vurdert ut fra ordlyden."),
            new("F", "Er kilden gjeldende og konsistent?", Tri.Unknown, "Avgjøres av kildestatus for testtilfellet."),
            new("G", "Krever testen koordinering med andre team?", services.Count > 0 ? Tri.Yes : Tri.No, services.Count > 0 ? $"Involverer {string.Join(", ", services.Take(4))}." : "Kun egen tjeneste nevnt."),
            new("H", "Gir automatisering vesentlig bedre pålitelighet?", repeated || timing || precise ? Tri.Yes : Tri.No, repeated || timing || precise ? "Gjentakelse, tid eller presisjon." : "Ingen slike krav."),
        };
        return new([.. approaches.DistinctBy(a => a.Approach)], manual);
    }

    public static string ApproachLabel(ExecutionApproach a) => a switch
    {
        ExecutionApproach.Manual => "Manuell",
        ExecutionApproach.ManualTechnical => "Manuell + teknisk",
        ExecutionApproach.IntegrationE2E => "Integrasjon / E2E",
        ExecutionApproach.AutomatedRecommended => "Automatisering anbefalt",
        ExecutionApproach.Technical => "Teknisk",
        _ => "Trenger vurdering",
    };

    // ── Verification (test oracle) ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The expected result as verification points: what to verify and where it can be observed (a category). <paramref name="methodConfirmed"/>
    /// is true only when a person has written how it is observed (the case's observation method) — "verification required" is not "method confirmed".</summary>
    public static IReadOnlyList<VerificationPoint> Verification(ScenarioFacts f, bool methodConfirmed = false)
    {
        if (string.IsNullOrWhiteSpace(f.Then)) return [];
        var points = new List<VerificationPoint>();
        // Split at "; " and at ", and " only when a clause follows (it has a verb): "name, age, birth date, and current case status" is one list.
        foreach (var raw in Regex.Split(f.Then, @";\s+|\n+|,\s+and\s+(?=[^,;]*\b(?:is|are|was|were|has|have|does|do|appears?|exists?|returns?|\w+ed)\b)"))
        {
            var a = raw.Trim().TrimEnd('.', ',');
            if (a.Length < 3) continue;
            var negative = Negation().IsMatch(a);
            var (surface, where) =
                negative && Regex.IsMatch(a, @"\b(stored|published|logged|retained|exists?|contain\w*|shown|counted|reveal\w*|lagre\w*|logge\w*)\b", RegexOptions.IgnoreCase)
                    ? (VerificationSurface.AbsenceAcrossSurfaces, "Alle flater der det kunne finnes (lagring, hendelser, logger, uttrekk)")
                : Regex.IsMatch(a, @"\b(flag\w*|reason|code|status)\b|""[^""]+""", RegexOptions.IgnoreCase) && Regex.IsMatch(a, @"\b(flag\w*|reject\w*|reason)\b", RegexOptions.IgnoreCase)
                    ? (VerificationSurface.FlagOrReason, "Flagg/årsak der resultatet lagres eller leveres")
                : Regex.IsMatch(a, @"\b(delivered|exported|row|file|published|sent to|levert\w*)\b", RegexOptions.IgnoreCase) ? (VerificationSurface.DeliveredOutput, "Det leverte resultatet (uttrekk, melding eller fil)")
                : Regex.IsMatch(a, @"\b(stored|persisted|saved|recorded|lagre\w*)\b", RegexOptions.IgnoreCase) ? (VerificationSurface.PersistedState, "Lagret forretningstilstand")
                : Regex.IsMatch(a, @"\b(accepted|rejected|returned|receives?|response|receipt|outcome|kvittering)\b", RegexOptions.IgnoreCase) ? (VerificationSurface.ProcessingOutcome, "Svaret/kvitteringen fra behandlingen")
                : Observable().IsMatch(a) ? (VerificationSurface.Screen, "Skjermbildet")
                : TimingOrVolume().IsMatch(a) ? (VerificationSurface.Timing, "Måling av tid/volum")
                : (VerificationSurface.Unknown, "Ikke oppgitt i kilden");
            var critical = negative || Security().IsMatch(a) || surface is VerificationSurface.AbsenceAcrossSurfaces or VerificationSurface.PersistedState
                or VerificationSurface.DeliveredOutput or VerificationSurface.FlagOrReason;
            points.Add(new(a, surface, where, negative, critical, methodConfirmed && surface != VerificationSurface.Unknown));
        }
        return points;
    }

    public static IReadOnlyList<VerificationPoint> Verification(TestCaseDraft c) =>
        Verification(FactsOf(c), methodConfirmed: !string.IsNullOrWhiteSpace(c.Edits.ObservationMethod));

    // ── Test data: requirement vs availability ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>The data the source requires (the Given, split at its own clause boundaries) and what is known about availability: a linked,
    /// approved Test Data asset is Confirmed; a linked asset that no longer exists is Blocked; nothing linked is Unknown. The specification
    /// describing data never makes it available.</summary>
    public static IReadOnlyList<TestDataItem> TestData(TestCaseDraft c)
    {
        var given = FactsOf(c).Given;
        if (string.IsNullOrWhiteSpace(given) || c.EffectiveTestDataNeed == TestDataNeed.NotRequired) return [];
        var parts = Regex.Split(given, @";\s+|\s+and\s+(?=(?:a|an|the|no|one|two)\b)", RegexOptions.IgnoreCase).Select(p => p.Trim().TrimEnd('.', ',')).Where(p => p.Length > 2).ToList();
        var links = c.TestData;
        DataAvailability availability; string note;
        if (links.Any(l => !l.Resolved)) { availability = DataAvailability.Blocked; note = "Koblet testdata finnes ikke lenger i katalogen."; }
        else if (links.Count > 0 && links.All(l => string.Equals(l.Approval, "Approved", StringComparison.OrdinalIgnoreCase)))
        { availability = DataAvailability.Confirmed; note = $"Koblet og godkjent: {string.Join(", ", links.Select(l => l.Alias))}."; }
        else if (links.Count > 0) { availability = DataAvailability.MustBeCreated; note = $"Koblet, men ikke godkjent: {string.Join(", ", links.Select(l => l.Alias))}."; }
        else { availability = DataAvailability.Unknown; note = "Ingen testdata er koblet — om dataene finnes i testmiljøet er ukjent."; }
        return [.. parts.Select(p => new TestDataItem(p, availability, note))];
    }

    // ── Execution readiness ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Explainable readiness from the evidence the case holds. Mapped to the existing review states (no new lifecycle): Generated → Klar til
    /// forberedelse; NeedsClarification or anything missing → Trenger klargjøring; UnderReview → Klar til gjennomgang; Accepted with every check
    /// satisfied → Klar til kjøring; removed source or unresolved conflict → Blokkert. Unknown is never satisfied. Never an execution result.
    /// </summary>
    public static ExecutionReadiness Readiness(TestCaseDraft c)
    {
        var f = FactsOf(c);
        var checks = new List<ReadinessCheck>();
        var sourceOk = c.SourceState is null or CaseSourceState.Current;
        checks.Add(new("Kildegrunnlag", c.SourceState is CaseSourceState.Removed ? Tri.No : sourceOk ? Tri.Yes : Tri.Unknown,
            c.SourceState switch { null or CaseSourceState.Current => "Gjeldende.", CaseSourceState.Removed => "Scenarioet er fjernet i kilden.", _ => c.SourceStateReason ?? "Kildestatus er ikke avklart." }));
        checks.Add(new("Forventet resultat", c.HasUnresolvedConflict ? Tri.No : string.IsNullOrWhiteSpace(c.EffectiveExpected) && c.EffectiveStepExpected.All(string.IsNullOrWhiteSpace) ? Tri.No : Tri.Yes,
            c.HasUnresolvedConflict ? "Kildene er uenige." : "Oppgitt."));
        var data = TestData(c);
        checks.Add(new("Testdata", data.Count == 0 ? (c.EffectiveTestDataNeed == TestDataNeed.Required ? Tri.Unknown : Tri.Yes) : data.All(d => d.Availability == DataAvailability.Confirmed) ? Tri.Yes
            : data.Any(d => d.Availability == DataAvailability.Blocked) ? Tri.No : Tri.Unknown, data.FirstOrDefault()?.Note ?? "Ikke nødvendig."));
        var triggerUnknown = TriggerUnknown(f);
        checks.Add(new("Utløser", triggerUnknown ? Tri.Unknown : Tri.Yes, triggerUnknown ? "Hvordan handlingen utløses i testmiljøet er ikke oppgitt." : "Beskrevet i stegene."));
        var points = Verification(c);
        var critical = points.Where(p => p.Critical).ToList();
        // Confirmed only when a person has written how every assertion is observed; an unconfirmed critical one is named first.
        checks.Add(new("Verifiseringsmetode", points.Count == 0 ? Tri.No : points.All(p => p.MethodConfirmed) ? Tri.Yes : Tri.Unknown,
            points.Count == 0 ? "Ingen påstander å verifisere."
            : points.All(p => p.MethodConfirmed) ? "Bekreftet av en person."
            : critical.Any(p => !p.MethodConfirmed) ? $"{critical.Count(p => !p.MethodConfirmed)} kritiske påstander uten bekreftet metode (skriv observasjonsmetode)."
            : $"{points.Count(p => !p.MethodConfirmed)} påstander uten bekreftet metode (skriv observasjonsmetode)."));
        checks.Add(new("Tilgang og miljø", string.IsNullOrWhiteSpace(c.Edits.EnvironmentPrerequisites) ? Tri.Unknown : Tri.Yes,
            string.IsNullOrWhiteSpace(c.Edits.EnvironmentPrerequisites) ? "Ikke registrert." : "Registrert av en person."));
        checks.Add(new("Gjennomgang", c.Review == TestCaseReviewState.Accepted ? Tri.Yes : Tri.No, c.Review == TestCaseReviewState.Accepted ? "Godkjent." : "Ikke godkjent ennå."));

        var level = c.SourceState is CaseSourceState.Removed || c.HasUnresolvedConflict || checks.Any(x => x.Name == "Testdata" && x.Satisfied == Tri.No) ? ReadinessLevel.Blocked
            : c.Review == TestCaseReviewState.Accepted ? (checks.All(x => x.Satisfied == Tri.Yes) ? ReadinessLevel.ReadyToRun : ReadinessLevel.NeedsPreparation)
            : c.Review == TestCaseReviewState.UnderReview ? ReadinessLevel.ReadyForReview
            : c.Review == TestCaseReviewState.NeedsClarification ? ReadinessLevel.NeedsPreparation
            : ReadinessLevel.ReadyToPrepare;
        return new(level, checks);
    }

    public static string ReadinessLabel(ReadinessLevel l) => l switch
    {
        ReadinessLevel.ReadyToPrepare => "Klar til forberedelse",
        ReadinessLevel.NeedsPreparation => "Trenger klargjøring",
        ReadinessLevel.ReadyForReview => "Klar til gjennomgang",
        ReadinessLevel.ReadyToRun => "Klar til kjøring",
        _ => "Blokkert",
    };

    // ── Test-design techniques → variant proposals ───────────────────────────────────────────────────────────────────────────────

    [GeneratedRegex(@"(?<op>more than|greater than|exceeds?|above|over|at least|no (?:less|fewer) than|minimum(?: of)?|less than|fewer than|below|under|at most|no more than|up to|maximum(?: of)?|within)\s+(?<val>\d+(?:[.,]\d+)?(?:\s*[×x*]\s*[^.,;]+?)?)(?=\s*(?:hours?|days?|minutes?|seconds?|characters?|%|percent|times|kr|NOK|$|[.,;)]|\s+(?:of|in|the|from|after|before|per)\b))", RegexOptions.IgnoreCase)]
    private static partial Regex Threshold();

    /// <summary>Boundary-value proposals for each threshold the scenario states. Inclusive/exclusive is read from the comparison words; concrete
    /// input values are not chosen here (precision and allowed range must be checked against the contract), and only the side the source
    /// states gets an expected result — the other side is left to be clarified.</summary>
    public static IEnumerable<TestCaseVariantProposal> Boundaries(TestCaseDraft scenario)
    {
        var f = FactsOf(scenario);
        var text = $"{f.Given}\n{f.When}\n{f.Then}";
        foreach (Match m in Threshold().Matches(text))
        {
            var op = m.Groups["op"].Value.ToLowerInvariant();
            var value = Regex.Replace(m.Groups["val"].Value.Trim(), @"\s+", " ");
            // An expression ("24 × the number of days in its date range") is kept to the end of its clause, so its range is not cut off.
            if (Regex.IsMatch(value, @"\d\s*[×*x]\s", RegexOptions.IgnoreCase))
                value = Regex.Replace(Regex.Match(text[m.Groups["val"].Index..], @"^[^.,;\n]+").Value.Trim(), @"\s+", " ");
            var exclusiveUpper = op is "more than" or "greater than" or "exceeds" or "exceed" or "above" or "over";
            var inclusiveLower = op.StartsWith("at least", StringComparison.Ordinal) || op.StartsWith("no less", StringComparison.Ordinal) || op.StartsWith("no fewer", StringComparison.Ordinal) || op.StartsWith("minimum", StringComparison.Ordinal);
            var exclusiveLower = op is "less than" or "fewer than" or "below" or "under";
            var inclusiveUpper = op is "at most" or "no more than" or "up to" or "within" || op.StartsWith("maximum", StringComparison.Ordinal);
            var meaning = exclusiveUpper ? $"strengt større enn {value} (lik {value} er utenfor regelen)"
                : inclusiveLower ? $"minst {value} ({value} er innenfor)"
                : exclusiveLower ? $"strengt mindre enn {value} (lik {value} er utenfor regelen)"
                : $"høyst {value} ({value} er innenfor)";
            var expression = value.Contains('×') || value.Contains('*') || Regex.IsMatch(value, @"\d\s*x\s", RegexOptions.IgnoreCase);
            var basis = $"Kilden sier «{m.Groups["op"].Value} {value}»: {meaning}." + (expression ? " Grensen er et uttrykk — regn ut verdien for testdataene, med datoperioden slik kilden avgrenser den." : "")
                + " Kontroller i kontrakten at verdiene er lovlige (presisjon, tillatt område) før de brukes.";
            var inside = Outcome(f.Then);
            string Key(string side) => $"boundary:{Sha(m.Value + side)}";
            yield return new(Key("at"), Clip($"{scenario.Generated.Title} — grenseverdi: nøyaktig {value}", 200), "Grenseverdi", basis,
                $"Verdi nøyaktig lik {value}.", null, inclusiveLower || inclusiveUpper ? inside : null);
            yield return new(Key("in"), Clip($"{scenario.Generated.Title} — grenseverdi: like innenfor {value}", 200), "Grenseverdi", basis,
                $"Verdi like {(exclusiveUpper || inclusiveLower ? "over" : "under")} {value}.", null, inside);
            yield return new(Key("out"), Clip($"{scenario.Generated.Title} — grenseverdi: like utenfor {value}", 200), "Grenseverdi", basis,
                $"Verdi like {(exclusiveUpper || inclusiveLower ? "under" : "over")} {value}.", null, null);
            if (expression || Regex.IsMatch(f.Given ?? "", @"\bhours?\b", RegexOptions.IgnoreCase) && inclusiveUpper)
                yield return new(Key("neg"), Clip($"{scenario.Generated.Title} — ugyldig verdi: negativ", 200), "Ugyldig inndata", basis + " En negativ verdi er ugyldig inndata; hva som skal skje er ikke oppgitt.",
                    "Negativ verdi.", null, null);
        }
    }

    private static string? Outcome(string? then) =>
        string.IsNullOrWhiteSpace(then) ? null : Regex.Split(then, @";\s+")[0].Trim().TrimEnd('.') is { Length: > 0 } o ? Upper(o) + "." : null;

    /// <summary>Idempotency, retry and ordering proposals for scenarios that state a repetition. Message identity, business key, version, send
    /// time and arrival order are kept apart; the persisted state is part of what is verified. No exact-once internals are assumed.</summary>
    public static IEnumerable<TestCaseVariantProposal> Repetitions(TestCaseDraft scenario)
    {
        var f = FactsOf(scenario);
        var text = f.All;
        if (!Repetition().IsMatch(text) && !Regex.IsMatch(text, @"\b(correction|withdraw\w*)\b", RegexOptions.IgnoreCase)) yield break;
        var keys = new[] { ("message id", "meldingsidentitet"), ("message-chain", "meldingskjede"), ("version", "versjon"), ("send time", "sendetid"), ("record identity", "postidentitet"), ("business key", "forretningsnøkkel") }
            .Where(k => text.Contains(k.Item1, StringComparison.OrdinalIgnoreCase)).Select(k => k.Item2).ToList();
        var distinct = keys.Count > 0 ? $" Hold {string.Join(", ", keys)} og ankomstrekkefølge fra hverandre." : " Kilden skiller ikke identitet fra ankomstrekkefølge — avklar hva som gjør to like.";
        if (Regex.IsMatch(text, @"\b(twice|again|duplicate\w*|same message|same change|repeated|re-?sent)\b", RegexOptions.IgnoreCase))
            yield return new($"repeat:{Sha(f.Title + "repeat")}", Clip($"{scenario.Generated.Title} — samme hendelse tre ganger og etter omstart", 200), "Idempotens",
                "Kilden beskriver gjentatt mottak. Gjenta mer enn to ganger og etter en omstart, og kontroller lagret tilstand — ikke bare svaret." + distinct, null, null, Outcome(f.Then));
        if (Regex.IsMatch(text, @"\b(out of order|before the original|before its original|correction|withdraw\w*)\b", RegexOptions.IgnoreCase))
            yield return new($"order:{Sha(f.Title + "order")}", Clip($"{scenario.Generated.Title} — motsatt ankomstrekkefølge", 200), "Rekkefølge",
                "Kilden nevner korrigering/tilbaketrekking eller rekkefølge. Send senere versjon før den opprinnelige og kontroller sluttilstanden." + distinct, null, null, null);
        if (Regex.IsMatch(text, @"\b(concurrent\w*|simultaneous\w*|at the same time)\b", RegexOptions.IgnoreCase))
            yield return new($"concurrent:{Sha(f.Title + "concurrent")}", Clip($"{scenario.Generated.Title} — samtidige endringer", 200), "Samtidighet",
                "Kilden nevner samtidighet. Kjør endringene samtidig (automatisert) og kontroller lagret tilstand." + distinct, null, null, null);
    }

    /// <summary>Security proposals from a scenario that states an access rule: the opposite access situation (without the stated permission or
    /// grant) and the negative confidentiality assertions the source states. Permissions are only the ones the text names.</summary>
    public static IEnumerable<TestCaseVariantProposal> SecurityVariants(TestCaseDraft scenario)
    {
        var f = FactsOf(scenario);
        if (!Security().IsMatch(f.All)) yield break;
        var permissions = Regex.Matches(f.All, @"`([A-Za-zÆØÅæøå]+:[A-Za-zÆØÅæøå]+)`").Select(m => m.Groups[1].Value).Distinct().ToList();
        var classified = Regex.IsMatch(f.All, @"Kode ?[67]|classified|security level", RegexOptions.IgnoreCase);
        if (permissions.Count > 0)
            yield return new($"noperm:{Sha(string.Join(",", permissions))}", Clip($"{scenario.Generated.Title} — uten {string.Join(", ", permissions)}", 200), "Tilgang (negativ)",
                $"Kilden krever {string.Join(", ", permissions)}. Test samme handling uten tillatelsen; kontroller at ingenting avsløres (heller ikke at barnet finnes). Forventet svar er ikke oppgitt i scenarioet.",
                $"En bruker uten {string.Join(", ", permissions)}.", null, null);
        if (classified)
            yield return new($"classified:{Sha(f.Title + "classified")}", Clip($"{scenario.Generated.Title} — skjermet barn (Kode 6/7) uten særskilt tilgang", 200), "Konfidensialitet",
                "Kilden omtaler skjerming. Kontroller oppslag, liste og telling hver for seg: skjermet barn verken vises, telles eller avsløres. Fravær i én liste er ikke nok.",
                "Et skjermet barn og en bruker uten tilgang til det.", null, null);
    }

    /// <summary>All test-design proposals of the scenario (existing evidence variants come first, from <see cref="ScenarioPlanning.Variants"/>);
    /// deduplicated by key and title and bounded.</summary>
    public static IEnumerable<TestCaseVariantProposal> DesignProposals(TestCaseDraft scenario) =>
        Boundaries(scenario).Concat(Repetitions(scenario)).Concat(SecurityVariants(scenario));

    // ── Decision table (rule interaction, precedence) ────────────────────────────────────────────────────────────────────────────

    [GeneratedRegex(@"^\s*(?:When|If|Where)\s+(?<cond>.+?),\s*(?<rest>.+)$", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ConditionalRule();

    /// <summary>
    /// A reviewable decision table over requirements of one specification that state a condition and an outcome ("When …, the report MUST be
    /// accepted and flagged …"). Each condition alone (the outcome the requirement states), and each pair: excluded when the source makes them
    /// contradictory (present / without the same thing), resolved by precedence when a requirement says it is not applied when another applies,
    /// and otherwise "needs review" (no outcome invented). A rejection is never turned into acceptance by a quality flag. Bounded to
    /// <see cref="MaxTableConditions"/> conditions.
    /// </summary>
    public static DecisionTable Decisions(IEnumerable<ProjectRequirementOption> requirements)
    {
        var rules = new List<(string Id, string Condition, string Outcome, string Text)>();
        foreach (var r in requirements)
        {
            var sentence = Regex.Replace(r.Title, @"\[[^\]]*\]", "").Trim();
            var m = ConditionalRule().Match(sentence.Split(". ")[0]);
            if (!m.Success) continue;
            var outcome = ScenarioPlanning.RuleFactsOf(r.Title).Outcomes.FirstOrDefault();
            var verb = Regex.Match(m.Groups["rest"].Value, @"MUST(?: NOT)? be (\w+)").Groups[1].Value;
            if (outcome is null && verb.Length == 0) continue;
            rules.Add((r.RequirementId, m.Groups["cond"].Value.Trim(), outcome is null ? verb : $"{verb} «{outcome}»".Trim(), r.Title));
            if (rules.Count == MaxTableConditions) break;
        }
        var rows = new List<DecisionRow>();
        var precedence = new List<string>();
        foreach (var r in rules) rows.Add(new([$"{r.Id}: {r.Condition}"], r.Outcome, "Relevant", $"{r.Id} sier det."));
        for (var i = 0; i < rules.Count; i++)
            for (var j = i + 1; j < rules.Count; j++)
            {
                var (a, b) = (rules[i], rules[j]);
                var conds = new[] { $"{a.Id}: {a.Condition}", $"{b.Id}: {b.Condition}" };
                if (Contradict(a.Condition, b.Condition)) { rows.Add(new(conds, null, "Motstridende", "Betingelsene kan ikke gjelde samtidig.")); continue; }
                var aSkips = Regex.IsMatch(a.Text, $@"MUST NOT be applied when {Regex.Escape(b.Id)}\b", RegexOptions.IgnoreCase);
                var bSkips = Regex.IsMatch(b.Text, $@"MUST NOT be applied when {Regex.Escape(a.Id)}\b", RegexOptions.IgnoreCase);
                if (aSkips || bSkips)
                {
                    var winner = aSkips ? b : a; var loser = aSkips ? a : b;
                    precedence.Add($"{winner.Id} går foran {loser.Id} ({loser.Id} sier det selv).");
                    rows.Add(new(conds, winner.Outcome, "Relevant — forrang", $"{loser.Id} gjelder ikke når {winner.Id} gjelder."));
                    continue;
                }
                var aRejects = a.Outcome.StartsWith("rejected", StringComparison.OrdinalIgnoreCase);
                var bRejects = b.Outcome.StartsWith("rejected", StringComparison.OrdinalIgnoreCase);
                rows.Add(aRejects != bRejects
                    ? new(conds, null, "Må vurderes", "Den ene avviser, den andre aksepterer med flagg: en avvisning skal ikke bli akseptert på grunn av et flagg, men kilden sier ikke hvilken som gjelder.")
                    : new(conds, null, "Må vurderes", "Begge kan gjelde; kilden sier ikke hva som skjer samlet."));
            }
        return new([.. rules.Select(r => $"{r.Id}: {r.Condition}")], rows, precedence);
    }

    private static bool Contradict(string a, string b)
    {
        static string? Subject(string s, string pattern) => Regex.Match(s, pattern, RegexOptions.IgnoreCase) is { Success: true } m ? m.Groups[1].Value.ToLowerInvariant() : null;
        var present = Subject(a, @"\b(?:an?\s+)?([\w ]+?) is present") ?? Subject(b, @"\b(?:an?\s+)?([\w ]+?) is present");
        const string without = @"\bwithout (?:an?\s+)?([\w ]+?)(?=,|;|$|\s+(?:and|or|when|where|if|is|are)\b)";
        var absent = Subject(a, without) ?? Subject(b, without);
        return present is not null && absent is not null && (present.EndsWith(absent, StringComparison.Ordinal) || absent.EndsWith(present, StringComparison.Ordinal));
    }

    // ── States, security matrix, cross-service E2E ───────────────────────────────────────────────────────────────────────────────

    private static readonly string[] KnownStates =
        ["received", "validated", "accepted", "rejected", "held", "resolved", "corrected", "withdrawn", "deleted", "open", "lock pending", "locked", "delivered", "flagged"];

    /// <summary>The states the text actually names, in order of first appearance (never a persisted enum, never states the text does not name).</summary>
    public static IReadOnlyList<string> StatesNamed(string? text) =>
        string.IsNullOrWhiteSpace(text) ? [] : [.. KnownStates.Select(s => (s, i: Regex.Match(text, $@"\b{s}\b", RegexOptions.IgnoreCase).Index))
            .Where(x => Regex.IsMatch(text, $@"\b{x.s}\b", RegexOptions.IgnoreCase)).OrderBy(x => x.i).Select(x => x.s)];

    /// <summary>A sequence (state-transition) proposal when one scenario names three or more states, e.g. held → resolved → accepted.</summary>
    public static IEnumerable<TestCaseVariantProposal> StateSequences(TestCaseDraft scenario)
    {
        var f = FactsOf(scenario);
        var states = StatesNamed(f.All);
        if (states.Count < 3) yield break;
        yield return new($"states:{Sha(string.Join(">", states))}", Clip($"{scenario.Generated.Title} — forløp {string.Join(" → ", states)}", 200), "Tilstandsforløp",
            $"Scenarioet nevner tilstandene {string.Join(", ", states)}. Test forløpet i rekkefølge, og at et forsøk på å hoppe over eller gjenta et steg ikke gir en annen sluttilstand. Overganger kilden ikke beskriver må avklares.",
            null, null, null);
    }

    /// <summary>Representative access combinations from the dimensions the text names (bounded): classification levels, operations and permissions.
    /// Each row's expected outcome is left to the source; the negative assertions are listed as checks.</summary>
    public static (IReadOnlyList<string> Rows, IReadOnlyList<string> NegativeChecks) SecurityMatrix(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || !Security().IsMatch(text)) return ([], []);
        var levels = new[] { ("ugradert", @"\b(unclassified|ugradert|security level 0)\b"), ("Kode 6", @"Kode ?6"), ("Kode 7", @"Kode ?7"),
            ("gradering ukjent", @"classification (?:is )?(?:unavailable|unknown)|Person (?:is )?unavailable"), ("gradering endret", @"classification (?:changes|changed)") }
            .Where(l => Regex.IsMatch(text, l.Item2, RegexOptions.IgnoreCase)).Select(l => l.Item1).ToList();
        var ops = new[] { ("oppslag", @"\b(look ?up|lookup|by id|direct)\b"), ("liste", @"\b(list|search|søk)\w*"), ("telling", @"\b(count\w*|telling)\b"), ("statistikk", @"\bstatisti\w*") }
            .Where(o => Regex.IsMatch(text, o.Item2, RegexOptions.IgnoreCase)).Select(o => o.Item1).ToList();
        var perms = Regex.Matches(text, @"`([A-Za-zÆØÅæøå]+:[A-Za-zÆØÅæøå]+)`").Select(m => m.Groups[1].Value).Distinct().ToList();
        var access = perms.Count > 0 ? new[] { $"med {string.Join("/", perms)}", $"uten {string.Join("/", perms)}" } : ["med tilgang", "uten tilgang"];
        var rows = (levels.Count == 0 ? ["(gradering ikke nevnt)"] : levels).SelectMany(l => (ops.Count == 0 ? ["(operasjon ikke nevnt)"] : ops).SelectMany(o => access.Select(a => $"{l} · {o} · {a}")))
            .Take(MaxMatrixRows).ToList();
        var negatives = Regex.Split(text, @"[.;\n]").Select(s => s.Trim()).Where(s => s.Length > 10 && Negation().IsMatch(s) && Security().IsMatch(s) || Regex.IsMatch(s, @"\bnot (?:shown|counted|stored|published|logged|revealed)\b", RegexOptions.IgnoreCase))
            .Distinct().Take(6).ToList();
        return (rows, negatives);
    }

    /// <summary>A cross-service E2E proposal when the scenario names other services. Checkpoints are the sentences that name a service; each lacks a
    /// confirmed way to observe it until a person records one (listed as a gap). No route, queue or tool is named.</summary>
    public static EndToEndProposal? EndToEnd(ScenarioFacts f, IReadOnlyCollection<string>? knownServices = null)
    {
        var services = ServicesNamed(f.SourceText, knownServices).ToList();
        if (f.Module is { Length: > 0 } own && !services.Contains(own, StringComparer.OrdinalIgnoreCase) && services.Count > 0) services.Insert(0, own);
        if (services.Count < 2) return null;
        var checkpoints = Regex.Split(f.SourceText, @"(?<=[.;])\s+|\n").Select(s => s.Trim()).Where(s => s.Length > 8 && services.Any(sv => s.Contains(sv, StringComparison.OrdinalIgnoreCase)))
            .Distinct().Take(8).ToList();
        var gaps = checkpoints.Select(c => $"Hvordan «{Clip(c, 80)}» observeres er ikke bekreftet.").ToList();
        return new(f.Title, services, checkpoints, gaps);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private static string Sha(string s) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(s)))[..10].ToLowerInvariant();
    private static string Clip(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
    private static string Upper(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
