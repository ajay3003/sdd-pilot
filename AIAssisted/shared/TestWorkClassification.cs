using System.Text.Json.Serialization;

namespace BirkNext.TestPlanning;

// ── Test work classification and the E2E rules (v1 2026-11-28, v2 2026-11-28) ───────────────────────────────────────────────────────────────
// Manual Testing and Technical Testing read ONE candidate inventory (TestScenarioDiscoveryEngine) and ONE plan/case store. Three INDEPENDENT
// assessments per candidate, each from what the producers already recorded (capability, candidate type, steps, expected behaviour, evidence kind,
// contract type) — never from a title, a file name or the source technology alone:
//   1. WHAT is tested (TestSubject): business or technical end-to-end, REST, GraphQL, integration, business, other technical.
//   2. HOW it can be tested (method): the rules PROPOSE Manual / Technical / Both; a person's explicit choice (Finn og forstå) DECIDES. A choice that
//      differs from the proposal is kept and shown as a deviation — never silently undone, never an error.
//   3. HOW CERTAIN (E2EStatus): Confirmed only with evidence of a start trigger, a cross-boundary chain, an observable outcome and a source
//      relationship. A person's method choice never promotes a candidate to Confirmed.
// Precedence: source evidence → deterministic rules → the person's method choice → audit (who, when) → renewed review when the source changes.
// Pure and offline; nothing is persisted, moved or hidden — the workspaces filter, the records stay where they are. The generic Spec Kit parser is
// not involved.

/// <summary>The kind of test work, as the two workspaces sort it (WHAT combined with HOW).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TestWorkCategory
{
    ManualBusiness, ManualE2E, TechnicalApi, TechnicalGraphQl, TechnicalIntegration, TechnicalE2E, Hybrid,
    /// <summary>Technical work outside API/GraphQL/integration/E2E (frontend quality, performance): Technical Testing, its specialist module.</summary>
    TechnicalOther,
    NeedsClassification,
}

/// <summary>WHAT is tested, independent of how and of how certain.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TestSubject { BusinessE2E, TechnicalE2E, RestApi, GraphQl, Integration, Business, OtherTechnical, Unknown }

/// <summary>Whether a candidate is an end-to-end flow, by the evidence behind it.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum E2EStatus { Confirmed, Candidate, NotE2E, NeedsClarification }

/// <summary>How the work is executed: by a person, by a technical tester/automation, or both (one flow, two test variants).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TestExecutionStyle { Manual, Technical, Hybrid }

/// <summary>The technical surface the evidence names (never inferred from a word in a title).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TechnicalSurface { None, Rest, GraphQl, Messaging, Ui }

/// <summary>The four facts an end-to-end flow needs. Confirmed requires all four, with a PROVEN cross-boundary chain.</summary>
/// <param name="CrossBoundaryProven">The evidence itself is a chain through two or more systems (not only declared or suggested).</param>
public sealed record E2EEvidence(bool Trigger, bool CrossBoundary, bool CrossBoundaryProven, bool Outcome, bool Source)
{
    public IEnumerable<string> Missing()
    {
        if (!Trigger) yield return "startpunkt (handling eller utløser)";
        if (!CrossBoundary) yield return "behandling på tvers av systemgrenser";
        if (!Outcome) yield return "observerbart sluttresultat";
        if (!Source) yield return "kildekobling";
    }
}

/// <param name="Status">End-to-end status (certainty).</param>
/// <param name="Execution">How it is executed: the person's choice when made, otherwise the proposal.</param>
/// <param name="Proposed">What the rules propose, independent of any choice.</param>
/// <param name="Decided">The person's explicit choice, or null.</param>
/// <param name="Reasons">Why, in plain words: each recorded fact the result rests on (explainable, testable).</param>
public sealed record E2EAssessment(E2EStatus Status, TestExecutionStyle Execution, IReadOnlyList<string> Reasons, string RuleVersion)
{
    public TestExecutionStyle Proposed { get; init; } = Execution;
    public ScenarioTestKind? Decided { get; init; }
    public E2EEvidence? Evidence { get; init; }
    public bool IsE2E => Status is E2EStatus.Confirmed or E2EStatus.Candidate;
    /// <summary>The person chose another method than the rules propose (kept, shown; never undone).</summary>
    public bool Deviates => Decided is not null && Execution != Proposed;
}

public sealed record TestWorkClassification(TestWorkCategory Category, E2EAssessment E2E, TechnicalSurface Surface, ScenarioOwnershipInfo Ownership, string RuleVersion)
{
    public TestSubject Subject { get; init; } = TestSubject.Unknown;
    /// <summary>Shown in Manual Testing: manual business, manual E2E and hybrid work (and what still needs classification — see <see cref="TechnicalDefault"/>).</summary>
    public bool ManualDefault => Category is TestWorkCategory.ManualBusiness or TestWorkCategory.ManualE2E or TestWorkCategory.Hybrid;
    /// <summary>Shown in Technical Testing: every technical category, hybrid, and what still needs classification (never hidden from both).</summary>
    public bool TechnicalDefault => Category is not (TestWorkCategory.ManualBusiness or TestWorkCategory.ManualE2E);
}

/// <summary>
/// E2E rules, version <see cref="RuleVersion"/>. Status:
/// <list type="bullet">
/// <item><b>Confirmed</b>: the evidence itself is a chain through two or more systems — a declared integration journey or a System Understanding
/// guide scenario whose steps run through more than one system — AND it has a start trigger, an observable outcome and a source reference.</item>
/// <item><b>Candidate</b>: an end-to-end flow is declared or suggested (a test specification's TEST-E, a UI user flow, an existing or developer test
/// through messaging/several components), or a proven chain lacks a trigger or an outcome.</item>
/// <item><b>NotE2E</b>: one operation, one page, one acceptance scenario that states no system boundary. A contract operation (REST or GraphQL) alone
/// is never end-to-end; a code test reference alone is never an executed test.</item>
/// <item><b>NeedsClarification</b>: a producer the rules do not know.</item>
/// </list>
/// </summary>
public static class E2ERules
{
    public const string RuleVersion = "e2e-rules/2";
    public const string TestSpecification = "test-specification";

    private static bool HasTrigger(DiscoveredScenario c) =>
        c.Steps.Any(s => !string.IsNullOrWhiteSpace(s.Action)) || c.Template.Any(t => t.Key is "when" or "operation" or "trigger" && !string.IsNullOrWhiteSpace(t.Value));

    public static E2EEvidence EvidenceOf(DiscoveredScenario c)
    {
        var proven = c.CapabilityId == ScenarioOwnership.IntegrationJourneys
            || c.CapabilityId == ScenarioOwnership.SystemGuide && c.Type == ScenarioCandidateType.BusinessE2E;
        var declared = c.CapabilityId == TestSpecification && c.Type == ScenarioCandidateType.BusinessE2E
            || c.CapabilityId == ScenarioOwnership.UiSource && c.Type == ScenarioCandidateType.BusinessE2E
            || c.CapabilityId == ScenarioOwnership.DeveloperTests && c.Type == ScenarioCandidateType.Integration
            || c.CapabilityId == ScenarioOwnership.ExistingTests && c.Type is ScenarioCandidateType.BusinessE2E or ScenarioCandidateType.Integration;
        return new(HasTrigger(c), proven || declared, proven, !string.IsNullOrWhiteSpace(c.ExpectedBehaviour), c.Evidence.Count > 0);
    }

    private static bool Known(string capability) => capability is ScenarioOwnership.Specification or ScenarioOwnership.SystemGuide or ScenarioOwnership.UiSource
        or ScenarioOwnership.ApiContract or ScenarioOwnership.IntegrationJourneys or ScenarioOwnership.DeveloperTests or ScenarioOwnership.ExistingTests or TestSpecification;

    public static E2EAssessment Assess(DiscoveredScenario c, ScenarioTestKind? reviewed = null)
    {
        var reasons = new List<string>();
        var evidence = EvidenceOf(c);
        E2EStatus status;
        if (!Known(c.CapabilityId))
        {
            status = E2EStatus.NeedsClarification;
            reasons.Add($"Kilden «{c.CapabilityId}» er ukjent for E2E-reglene.");
        }
        else if (evidence.CrossBoundaryProven && evidence.Trigger && evidence.Outcome && evidence.Source)
        {
            status = E2EStatus.Confirmed;
            reasons.Add(c.CapabilityId == ScenarioOwnership.IntegrationJourneys
                ? "Deklarert integrasjonsreise: en komponentkjede gjennom to eller flere systemer, med startpunkt, sluttresultat og kilde."
                : "Guidescenario der stegene går gjennom mer enn ett system, med startpunkt, sluttresultat og kilde.");
        }
        else if (evidence.CrossBoundary)
        {
            status = E2EStatus.Candidate;
            reasons.Add(evidence.CrossBoundaryProven ? "Kjede gjennom flere systemer, men bevis mangler for: " + string.Join(", ", evidence.Missing()) + "."
                : c.CapabilityId switch
                {
                    TestSpecification => "Erklært ende-til-ende i et testspesifikasjonsdokument; grensen er erklært, ikke verifisert.",
                    ScenarioOwnership.UiSource => "Brukerflyt funnet i UI-kilden; systemene bak den vises ikke av dette beviset.",
                    _ => "En eksisterende automatisert test gjennom meldinger eller flere komponenter; grensen er antydet, ikke bevist.",
                });
        }
        else
        {
            status = E2EStatus.NotE2E;
            reasons.Add(c.CapabilityId switch
            {
                ScenarioOwnership.ApiContract => "Én operasjon i én tjenestes kontrakt (REST eller GraphQL) — en kontrakttest, ikke E2E.",
                ScenarioOwnership.UiSource => "Én side eller funksjon i én applikasjon.",
                ScenarioOwnership.Specification => "Et akseptansescenario som ikke oppgir noen systemgrense.",
                TestSpecification => "En dokumentert test som ikke erklærer ende-til-ende-nivå.",
                _ => "Beviset nevner én komponent.",
            });
        }

        var proposed = Propose(c, status);
        var execution = reviewed switch
        {
            ScenarioTestKind.Manual => TestExecutionStyle.Manual,
            ScenarioTestKind.Technical => TestExecutionStyle.Technical,
            ScenarioTestKind.Both => TestExecutionStyle.Hybrid,
            _ => proposed,
        };
        if (reviewed is { } decision)
            reasons.Add(execution == proposed ? $"Personens valg: {StoryGroupingEngine.TestKindLabel(decision)} (samme som forslaget)."
                : $"Personens valg: {StoryGroupingEngine.TestKindLabel(decision)}. Reglene foreslår {Label(proposed).ToLowerInvariant()}; valget gjelder.");
        return new(status, execution, reasons, RuleVersion) { Proposed = proposed, Decided = reviewed, Evidence = evidence };
    }

    /// <summary>The proposed method: an end-to-end flow with a manual and a technical workspace is both; otherwise the primary workspace. A module's
    /// test specification is technical (both when it declares end-to-end).</summary>
    private static TestExecutionStyle Propose(DiscoveredScenario c, E2EStatus status)
    {
        var endToEnd = status is E2EStatus.Confirmed or E2EStatus.Candidate;
        if (c.CapabilityId == TestSpecification) return endToEnd ? TestExecutionStyle.Hybrid : TestExecutionStyle.Technical;
        var ownership = ScenarioOwnership.Of(c);
        var both = ownership.Primary is not null && ownership.Secondary is not null && ownership.Primary != ownership.Secondary;
        return both && endToEnd ? TestExecutionStyle.Hybrid
            : ownership.Primary == TestingWorkspace.TechnicalTesting ? TestExecutionStyle.Technical : TestExecutionStyle.Manual;
    }

    public static string Label(TestExecutionStyle style) => style switch
    {
        TestExecutionStyle.Manual => "Manuell",
        TestExecutionStyle.Technical => "Teknisk",
        _ => "Manuell og teknisk",
    };
}

/// <summary>Work classification, version <see cref="RuleVersion"/>, on top of <see cref="E2ERules"/> and <see cref="ScenarioOwnership"/>.</summary>
public static class TestWorkClassifier
{
    public const string RuleVersion = "test-work/2";

    /// <summary>The surface the evidence names: a contract operation's recorded contract type (OpenAPI → REST, GraphQL schema/operations →
    /// GraphQL), a messaging integration, or a UI source finding.</summary>
    public static TechnicalSurface SurfaceOf(DiscoveredScenario c)
    {
        if (c.CapabilityId == ScenarioOwnership.ApiContract)
        {
            foreach (var e in c.Evidence.Where(e => e.Kind == "contract-operation"))
            {
                if (e.Detail?.StartsWith("GraphQl", StringComparison.Ordinal) == true) return TechnicalSurface.GraphQl;
                if (e.Detail?.StartsWith("OpenApi", StringComparison.Ordinal) == true) return TechnicalSurface.Rest;
            }
            return TechnicalSurface.Rest; // a contract operation without a recorded type: REST is the analyzer's default
        }
        if (c.Type == ScenarioCandidateType.Integration || c.CapabilityId == ScenarioOwnership.IntegrationJourneys) return TechnicalSurface.Messaging;
        if (c.CapabilityId == ScenarioOwnership.UiSource) return TechnicalSurface.Ui;
        return TechnicalSurface.None;
    }

    /// <summary>WHAT is tested, from the evidence alone (not from the method and not from certainty beyond E2E eligibility).</summary>
    public static TestSubject SubjectOf(DiscoveredScenario c, E2EStatus status, TechnicalSurface surface)
    {
        if (status == E2EStatus.NeedsClarification) return TestSubject.Unknown;
        if (status is E2EStatus.Confirmed or E2EStatus.Candidate)
            // A guide or UI user flow is a business process; a journey, a module test specification or a messaging test is a technical chain.
            return c.CapabilityId is ScenarioOwnership.SystemGuide or ScenarioOwnership.UiSource ? TestSubject.BusinessE2E : TestSubject.TechnicalE2E;
        return surface switch
        {
            TechnicalSurface.GraphQl => TestSubject.GraphQl,
            TechnicalSurface.Rest => TestSubject.RestApi,
            TechnicalSurface.Messaging => TestSubject.Integration,
            _ => ScenarioOwnership.Of(c).Purpose switch
            {
                ScenarioPurpose.BusinessFunctional or ScenarioPurpose.UiFunctional or ScenarioPurpose.BusinessEndToEnd => TestSubject.Business,
                ScenarioPurpose.IntegrationFunctional => TestSubject.Integration,
                ScenarioPurpose.Unclassified => c.CapabilityId == E2ERules.TestSpecification ? TestSubject.OtherTechnical : TestSubject.Unknown,
                _ => TestSubject.OtherTechnical,
            },
        };
    }

    public static TestWorkClassification Classify(DiscoveredScenario c, ScenarioTestKind? reviewed = null)
    {
        var e2e = E2ERules.Assess(c, reviewed);
        var ownership = ScenarioOwnership.Of(c);
        var surface = SurfaceOf(c);
        var subject = SubjectOf(c, e2e.Status, surface);
        var category = subject == TestSubject.Unknown && reviewed is null ? TestWorkCategory.NeedsClassification
            : e2e.Execution == TestExecutionStyle.Hybrid ? TestWorkCategory.Hybrid
            : e2e.Execution == TestExecutionStyle.Manual ? e2e.IsE2E ? TestWorkCategory.ManualE2E : TestWorkCategory.ManualBusiness
            : e2e.IsE2E ? TestWorkCategory.TechnicalE2E
            : subject switch
            {
                TestSubject.GraphQl => TestWorkCategory.TechnicalGraphQl,
                TestSubject.RestApi => TestWorkCategory.TechnicalApi,
                TestSubject.Integration => TestWorkCategory.TechnicalIntegration,
                _ => TestWorkCategory.TechnicalOther,
            };
        return new(category, e2e, surface, ownership, RuleVersion) { Subject = subject };
    }

    public static string Label(TestWorkCategory category) => category switch
    {
        TestWorkCategory.ManualBusiness => "Manuell forretningstest",
        TestWorkCategory.ManualE2E => "Manuell E2E",
        TestWorkCategory.TechnicalApi => "REST/API",
        TestWorkCategory.TechnicalGraphQl => "GraphQL",
        TestWorkCategory.TechnicalIntegration => "Integrasjon",
        TestWorkCategory.TechnicalE2E => "Teknisk E2E",
        TestWorkCategory.Hybrid => "Manuell og teknisk",
        TestWorkCategory.TechnicalOther => "Annen teknisk",
        _ => "Må klassifiseres",
    };

    public static string Label(TestSubject subject) => subject switch
    {
        TestSubject.BusinessE2E => "Forretnings-E2E",
        TestSubject.TechnicalE2E => "Teknisk E2E",
        TestSubject.RestApi => "REST/API",
        TestSubject.GraphQl => "GraphQL",
        TestSubject.Integration => "Integrasjon",
        TestSubject.Business => "Forretningsfunksjon",
        TestSubject.OtherTechnical => "Annen teknisk",
        _ => "Ukjent",
    };

    public static string Label(E2EStatus status) => status switch
    {
        E2EStatus.Confirmed => "Bekreftet E2E",
        E2EStatus.Candidate => "E2E-kandidat",
        E2EStatus.NotE2E => "Ikke E2E",
        _ => "Må avklares",
    };
}
