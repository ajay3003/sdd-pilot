namespace BirkNext.TestPlanning;

// ── Testoversikt (Manual Testing dashboard, 2026-11-25) ──────────────────────────────────────────────────────────────────────────
// Preparation, coverage, readiness, approval and source status of saved test work — never execution results (no «Gjennomført»,
// «Bestått» or «Feilet» on the dashboard, by decision). One read-only overview from stored records; the page aggregates it per
// project, module or specification with ManualTestingOverviewPresentation, so every card and chart uses the same definitions.

/// <summary>One configurable part of Testoversikt. <see cref="Id"/> is the stable key stored in the settings (never the title).</summary>
public sealed record DashboardWidget(string Id, string Group, string Title, string Description, bool DefaultOn);

public static class ManualTestingDashboard
{
    public const string Metrics = "Nøkkeltall", Charts = "Grafer", FollowUp = "Oppfølging", Display = "Visning";

    /// <summary>Every widget with its Norwegian title, a one-line explanation and its default. Defaults apply only when nothing is saved for an id.</summary>
    public static readonly IReadOnlyList<DashboardWidget> Widgets =
    [
        new("metric.scenarios", Metrics, "Scenarioforslag", "Unike akseptansescenarioer og kravscenarioer fra spesifikasjonene i utvalget.", true),
        new("metric.testCases", Metrics, "Testtilfeller", "Lagrede testtilfeller i aktive testplaner (utelatte og utgåtte telles ikke).", true),
        new("metric.approved", Metrics, "Godkjente", "Testtilfeller med status Godkjent.", true),
        new("metric.ready", Metrics, "Klar til testing", "Testtilfeller som er klare til kjøring etter testbarhetskontrollen.", true),
        new("metric.testPlans", Metrics, "Testplaner", "Aktive testplaner med testtilfeller i utvalget.", false),
        // 2026-11-26: user story implementation status (tasks.md) and acceptance scenario type — optional, off by default.
        new("metric.usInProgress", Metrics, "US under utvikling", "Brukerhistorier der noen, men ikke alle, oppgavene i tasks.md er fullført.", false),
        new("metric.usOpenTasks", Metrics, "US med uferdige oppgaver", "Brukerhistorier med minst én oppgave som ikke er fullført.", false),
        new("metric.usTasksCompleted", Metrics, "US-oppgaver fullført", "Brukerhistorier der alle direkte oppgaver er avkrysset (ikke det samme som implementert).", false),
        new("metric.usNeedsClarification", Metrics, "US-status må avklares", "Brukerhistorier med motstridende eller uforklarte oppgavemarkeringer.", false),
        new("metric.functionalScenarios", Metrics, "Funksjonelle scenarioer", "Akseptansescenarioer som beskriver oppførsel eller resultat.", false),
        new("metric.nonFunctionalScenarios", Metrics, "Ikke-funksjonelle scenarioer", "Akseptansescenarioer som beskriver en kvalitetsegenskap (blandede telles ikke her).", false),

        new("chart.preparation", Charts, "Testforberedelse", "Opprettet, godkjent og klar til testing — trinn som overlapper, ikke summeres.", true),
        new("chart.moduleCoverage", Charts, "Testdekning per modul", "Hvor mange scenarioer som har minst ett lagret testtilfelle.", true),
        new("chart.priority", Charts, "Scenarioer etter prioritet", "Scenarioer per prioritet fra spesifikasjonen (P1, P2, P3).", true),
        new("chart.coverageGaps", Charts, "Manglende testdekning", "Scenarioer uten lagret testtilfelle, etter prioritet.", true),
        new("chart.blockers", Charts, "Testbarhet og blokkeringer", "Hva som mangler før testtilfellene kan gjennomføres.", true),
        new("chart.sourceChanges", Charts, "Kildeendringer", "Testtilfeller der kilden er endret, fjernet eller ikke kunne sammenlignes.", true),
        new("chart.manualTechnical", Charts, "Manuelle og tekniske testtilfeller", "Testtilfeller etter valgt testtype.", false),
        new("chart.history", Charts, "Utvikling i testforberedelse", "Utvikling over tid — bare når historikken finnes.", false),
        new("chart.moduleApproval", Charts, "Godkjenning per modul", "Lagrede og godkjente testtilfeller per modul.", false),
        new("chart.usImplementation", Charts, "Implementeringsframdrift for brukerhistorier", "Brukerhistorier etter oppgavestatus i tasks.md (hver historie telles én gang).", false),
        new("chart.scenarioTypes", Charts, "Scenarioer etter type", "Funksjonelle, ikke-funksjonelle, blandede og uavklarte akseptansescenarioer.", false),
        new("chart.nfrCategories", Charts, "Ikke-funksjonelle scenarioer per kategori", "Kvalitetsegenskaper (ytelse, universell utforming …); et scenario kan ha flere.", false),
        new("chart.gapsByType", Charts, "Manglende dekning etter type", "Akseptansescenarioer uten lagret testtilfelle, etter scenariotype.", false),

        new("panel.p1Attention", FollowUp, "Viktig å følge opp", "P1-scenarioer uten testtilfeller og P1-testtilfeller som venter.", true),
        new("panel.testPlans", FollowUp, "Testplaner", "Kort status for prosjektets testplaner.", true),
        new("panel.recentChanges", FollowUp, "Siste endringer", "De siste lagrede versjonene av testplanene.", false),
        new("panel.translationReview", FollowUp, "Oversettelser til gjennomgang", "Manglende, maskinoversatte og utdaterte norske tekster.", false),

        new("display.moduleOverview", Display, "Oversikt per modul", "Tabell med tall per modul (eller per spesifikasjon/brukerhistorie).", true),
        new("display.explanations", Display, "Vis forklaring til nøkkeltall", "En kort forklaring under hvert nøkkeltall.", true),
        new("display.hideEmpty", Display, "Skjul tomme grafer", "Grafer uten data vises ikke.", true),
    ];

    public static bool IsKnown(string id) => Widgets.Any(w => w.Id == id);
}

/// <summary>The saved Testoversikt setup: an explicit on/off per widget id; ids not present use their default.</summary>
public sealed record ManualTestingDashboardSettings
{
    public Dictionary<string, bool> Visible { get; init; } = new(StringComparer.Ordinal);
    public DateTimeOffset? UpdatedAt { get; init; }
    public string? UpdatedBy { get; init; }

    public bool IsOn(string id) => Visible.TryGetValue(id, out var on) ? on : ManualTestingDashboard.Widgets.FirstOrDefault(w => w.Id == id)?.DefaultOn ?? false;
}

/// <summary>A change to the setup: the switches to store (unknown ids are refused), or <see cref="ResetToDefaults"/>.</summary>
public sealed record SaveManualTestingDashboardSettingsRequest(Dictionary<string, bool>? Visible, bool ResetToDefaults = false);

/// <summary>The project's saved test work and its specifications' scenarios, for Testoversikt (read-only; no discovery is run, nothing is written).</summary>
public sealed record ManualTestingOverview
{
    public string ProjectKey { get; init; } = "";
    public DateTimeOffset ComputedAt { get; init; }
    /// <summary>False when no parsed specification is stored for the project (older imports): scenario figures are then not available — never zero.</summary>
    public bool ScenariosAvailable { get; init; }
    public List<OverviewSpecification> Specifications { get; init; } = [];
    public List<OverviewCase> Cases { get; init; } = [];
    public List<OverviewPlan> Plans { get; init; } = [];
    public List<OverviewRevision> RecentRevisions { get; init; } = [];
    /// <summary>User story implementation status of the project's specifications (2026-11-26, the shared status engine); empty when not computed.</summary>
    public List<OverviewStoryStatus> Stories { get; init; } = [];
    /// <summary>False when the project's import holds no task lines at all (story status is then unknown, never zero).</summary>
    public bool StoryStatusAvailable { get; init; }
}

/// <summary>One user story's task status, compact (the details are read on demand from the story-status endpoint).</summary>
public sealed record OverviewStoryStatus(string StoryKey, string StoryId, string Title, string SpecificationPath, string Module, UserStoryTaskStatus Status, int Completed, int Eligible,
    int Remaining);

/// <summary>One specification file and its scenarios (unique by source identity).</summary>
public sealed record OverviewSpecification(string Path, string Module, string Title, List<OverviewScenario> Scenarios);

/// <summary>One scenario of a specification: its identity (the one saved cases carry), story, number and the priority the source states.</summary>
public sealed record OverviewScenario(string Identity, string? StoryKey, string? StoryId, string? StoryTitle, int? Number, string? Priority, string Title,
    ScenarioRequirementType Type = ScenarioRequirementType.Unclassified, List<QualityAttribute>? Qualities = null, bool RequirementOnly = false)
{
    /// <summary>An acceptance scenario (not a requirement without one): only these are classified by type.</summary>
    public bool IsAcceptanceScenario => !RequirementOnly;
}

/// <summary>One saved test case (active plans; excluded and superseded are left out), attributed by its OWN source: the specification it was prepared
/// from (or its parent scenario's), never the current selection. <see cref="Module"/> is «Uten modultilknytning» when that is not recorded.</summary>
public sealed record OverviewCase
{
    public Guid PlanId { get; init; }
    public string PlanName { get; init; } = "";
    public string CaseId { get; init; } = "";
    public string Title { get; init; } = "";
    public string? SpecificationPath { get; init; }
    public string Module { get; init; } = "";
    /// <summary>The source scenario identity the case covers (its own, or its parent scenario's for a hand-written case); null when none.</summary>
    public string? ScenarioIdentity { get; init; }
    public string? StoryKey { get; init; }
    public string? StoryId { get; init; }
    public string? Priority { get; init; }
    public TestCaseReviewState Review { get; init; }
    public bool Ready { get; init; }
    public bool MissingExpected { get; init; }
    public bool NoTestData { get; init; }
    public bool Hindrance { get; init; }
    public bool MissingSteps { get; init; }
    public CaseSourceState? SourceState { get; init; }
    public ScenarioTestKind? TestKind { get; init; }
    public TranslationState Translation { get; init; }
    public TranslationOrigin? TranslationOrigin { get; init; }
}

public sealed record OverviewPlan(Guid Id, string Name, int Cases, int Approved, int Waiting, int SourceAttention, List<string> Modules, DateTimeOffset UpdatedAt);

public sealed record OverviewRevision(Guid PlanId, string PlanName, int Revision, string Reason, string SavedBy, DateTimeOffset SavedAt);
