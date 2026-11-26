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
        new("chart.moduleCoverage", Charts, "Testdekning per modul (graf)", "Samme tall som «Oversikt per modul», som 0–100 %-graf. Av som standard (v3): tabellen viser dekningen.", false),
        new("chart.priority", Charts, "Scenarioer etter prioritet", "Scenarioer per prioritet fra spesifikasjonen (P1, P2, P3).", true),
        new("chart.coverageGaps", Charts, "Manglende testdekning", "Scenarioer uten lagret testtilfelle, etter prioritet.", true),
        new("chart.blockers", Charts, "Testbarhet og blokkeringer", "Hva som mangler før testtilfellene kan gjennomføres.", true),
        new("chart.sourceChanges", Charts, "Kildeendringer", "Testtilfeller der kilden er endret, fjernet eller ikke kunne sammenlignes.", true),
        new("chart.manualTechnical", Charts, "Manuelle og tekniske testtilfeller", "Testtilfeller etter valgt testtype.", false),
        new("chart.history", Charts, "Utvikling i testforberedelse", "Utvikling over tid — bare når historikken finnes.", false),
        new("chart.moduleApproval", Charts, "Godkjenning per modul", "Lagrede og godkjente testtilfeller per modul.", false),
        new("chart.usImplementation", Charts, "Implementeringsframdrift for brukerhistorier", "Brukerhistorier etter oppgavestatus i tasks.md (hver historie telles én gang).", false),
        new("chart.scenarioReadiness", Charts, "Klargjøring av scenarioer", "Akseptansescenarioer og dokumenterte tester: klar, trenger klargjøring eller må vurderes (ikke testresultater).", true),
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

    /// <summary>The main KPI row's fixed cards (customisable KPI tiles sit among them, see <see cref="KpiSequence"/>).</summary>
    public static readonly IReadOnlyList<string> CoreKpis = ["metric.scenarios", "metric.testCases", "metric.approved", "metric.ready"];

    /// <summary>The display order of the main KPI row (2026-11-26, customisation v5): the saved order of the ids that still exist, then every other
    /// card in the default order (scenarios, cases, approved, the custom tiles, ready). Unknown or repeated saved ids are ignored, never an error.</summary>
    public static IReadOnlyList<string> KpiSequence(ManualTestingDashboardSettings s)
    {
        var defaults = new List<string> { "metric.scenarios", "metric.testCases", "metric.approved" };
        defaults.AddRange(s.EffectiveTiles.OrderBy(t => t.Order).Select(t => t.Id));
        defaults.Add("metric.ready");
        var known = defaults.ToHashSet(StringComparer.Ordinal);
        var saved = (s.KpiOrder ?? []).Where(known.Contains).Distinct(StringComparer.Ordinal).ToList();
        return [.. saved, .. defaults.Where(d => !saved.Contains(d, StringComparer.Ordinal))];
    }
}

// ── Custom KPI tiles (2026-11-26) ─────────────────────────────────────────────────────────────────────────────────────────────────
// A tile counts the unique scenarios (acceptance scenarios and standalone documented tests — never user stories, cases or plan occurrences)
// whose value on ONE classification dimension equals the tile's metric. The title is only a label: it never changes classification or counts.

/// <summary>The classification dimension a tile counts on. Independent axes: test level is not a scenario type.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
public enum KpiTileDimension { TestLevel, ScenarioType }

/// <summary>One custom KPI tile. <see cref="Id"/> is stable and never derived from the title; <see cref="Metric"/> is the canonical enum value
/// on <see cref="Dimension"/> («EndToEnd», «NonFunctional» …).</summary>
public sealed record DashboardKpiTile(string Id, string? Title, KpiTileDimension Dimension, string Metric, bool Visible = true, int Order = 0);

public static class DashboardKpiTiles
{
    public const string DefaultId = "tile.e2e";
    public const int MaxTiles = 12, MaxTitle = 60;

    /// <summary>The application's default: «Ende-til-ende-tester», Testnivå = Ende til ende.</summary>
    public static readonly IReadOnlyList<DashboardKpiTile> Defaults = [new(DefaultId, "Ende-til-ende-tester", KpiTileDimension.TestLevel, nameof(ScenarioTestLevel.EndToEnd))];

    /// <summary>Grouped metric choices (canonical value, Norwegian label) per dimension.</summary>
    public static readonly IReadOnlyList<(KpiTileDimension Dimension, string Group, IReadOnlyList<(string Value, string Label)> Options)> Choices =
    [
        (KpiTileDimension.TestLevel, "Testnivå", [(nameof(ScenarioTestLevel.EndToEnd), "Ende til ende"), (nameof(ScenarioTestLevel.Integration), "Integrasjon"),
            (nameof(ScenarioTestLevel.Component), "Komponent"), (nameof(ScenarioTestLevel.Unclassified), "Uavklart testnivå")]),
        (KpiTileDimension.ScenarioType, "Scenariotype", [(nameof(ScenarioRequirementType.Functional), "Funksjonell"), (nameof(ScenarioRequirementType.NonFunctional), "Ikke-funksjonell"),
            (nameof(ScenarioRequirementType.Mixed), "Blandet"), (nameof(ScenarioRequirementType.Unclassified), "Uavklart scenariotype")]),
    ];

    public static string DimensionLabel(KpiTileDimension d) => d == KpiTileDimension.TestLevel ? "Testnivå" : "Scenariotype";

    public static string? MetricLabel(KpiTileDimension d, string metric) =>
        Choices.FirstOrDefault(c => c.Dimension == d).Options?.FirstOrDefault(o => o.Value == metric).Label;

    public static bool IsValidMetric(KpiTileDimension d, string? metric) => metric is not null && MetricLabel(d, metric) is not null;

    /// <summary>The title shown: the custom one (trimmed), else the metric's own default name.</summary>
    public static string DisplayTitle(DashboardKpiTile t) => Clean(t.Title) ?? DefaultTitle(t.Dimension, t.Metric);

    public static string DefaultTitle(KpiTileDimension d, string metric) => (d, metric) switch
    {
        (KpiTileDimension.TestLevel, nameof(ScenarioTestLevel.EndToEnd)) => "Ende-til-ende-tester",
        (KpiTileDimension.TestLevel, nameof(ScenarioTestLevel.Integration)) => "Integrasjonstester",
        (KpiTileDimension.TestLevel, nameof(ScenarioTestLevel.Component)) => "Komponenttester",
        (KpiTileDimension.ScenarioType, nameof(ScenarioRequirementType.Functional)) => "Funksjonelle scenarioer",
        (KpiTileDimension.ScenarioType, nameof(ScenarioRequirementType.NonFunctional)) => "Ikke-funksjonelle scenarioer",
        (KpiTileDimension.ScenarioType, nameof(ScenarioRequirementType.Mixed)) => "Blandede scenarioer",
        _ => MetricLabel(d, metric) ?? metric,
    };

    /// <summary>Trimmed, whitespace-collapsed, at most <see cref="MaxTitle"/> characters; null when empty (then the default title is used).</summary>
    public static string? Clean(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        var t = System.Text.RegularExpressions.Regex.Replace(title.Trim(), @"\s+", " ");
        return t.Length > MaxTitle ? t[..MaxTitle].TrimEnd() : t;
    }

    /// <summary>The tiles to store: validated, titles cleaned, ids unique, order renumbered in the given order. Throws on an invalid tile.</summary>
    public static List<DashboardKpiTile> Normalize(IEnumerable<DashboardKpiTile> tiles)
    {
        var list = tiles.ToList();
        if (list.Count > MaxTiles) throw new ArgumentException($"Høyst {MaxTiles} fliser.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<DashboardKpiTile>();
        foreach (var t in list.OrderBy(t => t.Order))
        {
            if (string.IsNullOrWhiteSpace(t.Id) || t.Id.Length > 64 || !System.Text.RegularExpressions.Regex.IsMatch(t.Id, @"^[A-Za-z0-9][A-Za-z0-9._\-]*$"))
                throw new ArgumentException("Ugyldig id for flis.");
            if (!ids.Add(t.Id)) throw new ArgumentException($"Flisen {t.Id} finnes to ganger.");
            if (!Enum.IsDefined(t.Dimension) || !IsValidMetric(t.Dimension, t.Metric)) throw new ArgumentException($"Ukjent metrikk for flisen {t.Id}.");
            result.Add(t with { Title = Clean(t.Title), Order = result.Count });
        }
        return result;
    }
}

/// <summary>The saved Testoversikt setup: an explicit on/off per widget id; ids not present use their default.</summary>
public sealed record ManualTestingDashboardSettings
{
    /// <summary>Custom KPI tiles (2026-11-26); null = never customised → <see cref="DashboardKpiTiles.Defaults"/>. An empty list is an explicit choice.</summary>
    public List<DashboardKpiTile>? Tiles { get; init; }
    public IReadOnlyList<DashboardKpiTile> EffectiveTiles => Tiles ?? DashboardKpiTiles.Defaults;
    /// <summary>The main KPI row's order (core card ids and tile ids); null = the default order. See <see cref="ManualTestingDashboard.KpiSequence"/>.</summary>
    public List<string>? KpiOrder { get; init; }

    public Dictionary<string, bool> Visible { get; init; } = new(StringComparer.Ordinal);
    public DateTimeOffset? UpdatedAt { get; init; }
    public string? UpdatedBy { get; init; }

    public bool IsOn(string id) => Visible.TryGetValue(id, out var on) ? on : ManualTestingDashboard.Widgets.FirstOrDefault(w => w.Id == id)?.DefaultOn ?? false;
    /// <summary>System Settings → Manuell testing → Testplanstruktur (2026-11-26): the grouping template proposed for NEW plans; null = the default.
    /// Existing plans keep their saved structure.</summary>
    public string? PlanStructure { get; init; }
    public string EffectivePlanStructure => PlanStructureTemplates.Known(PlanStructure) ? PlanStructure! : PlanStructureTemplates.Default;
}

/// <summary>A change to the setup: the switches to store (unknown ids are refused), or <see cref="ResetToDefaults"/>.</summary>
public sealed record SaveManualTestingDashboardSettingsRequest(Dictionary<string, bool>? Visible, bool ResetToDefaults = false, string? PlanStructure = null)
{
    /// <summary>The complete tile list to store (null = keep the stored tiles).</summary>
    public List<DashboardKpiTile>? Tiles { get; init; }
    /// <summary>«Tilbakestill standard» for the tiles only: restores <see cref="DashboardKpiTiles.Defaults"/>; other widgets are kept.</summary>
    public bool ResetTiles { get; init; }
    /// <summary>The KPI row's order to store (null = keep).</summary>
    public List<string>? KpiOrder { get; init; }
    /// <summary>«Tilbakestill standardoppsett» from the customisation mode: visibility, tiles and order go back to the defaults BEFORE the request's own
    /// changes are applied; the plan-structure setting is kept. Presentation only — never scenarios, cases, plans or results.</summary>
    public bool ResetLayout { get; init; }
}

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
    /// <summary>The scenario's test level (2026-11-26): what its source states (an acceptance scenario states none → Unclassified).</summary>
    public ScenarioTestLevel Level { get; init; } = ScenarioTestLevel.Unclassified;
    /// <summary>True when the level is only DECLARED by a document (e.g. a documented TEST-E): «Dokumentert E2E», not a verified boundary.</summary>
    public bool LevelDeclared { get; init; }
    /// <summary>A standalone documented test (no user story; never one invented).</summary>
    public bool Standalone { get; init; }
    /// <summary>Adaptive readiness of this scenario (2026-11-26; the same engine as «Klargjør AC»); null for a requirement without a scenario.</summary>
    public PreparationStatus? Preparation { get; init; }
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

public sealed record OverviewPlan(Guid Id, string Name, int Cases, int Approved, int Waiting, int SourceAttention, List<string> Modules, DateTimeOffset UpdatedAt)
{
    /// <summary>The plan's sprint label (its delivery label), when set.</summary>
    public string? SprintLabel { get; init; }
    /// <summary>Scenario-level execution in THIS plan (2026-11-26): its scenario occurrences by current status. Never a global scenario result;
    /// never a case or step count.</summary>
    public PlanExecutionCounts? Execution { get; init; }
    /// <summary>Suites in the plan and its scenario-only items (planned without a case), 2026-11-26. Null from a server that predates them.</summary>
    public int? Suites { get; init; }
    public int? PlannedOnly { get; init; }
}

/// <summary>A plan's scenario occurrences by their current execution status in that plan.</summary>
public sealed record PlanExecutionCounts(int Scenarios, int NotRun, int Passed, int Failed, int Blocked);

public sealed record OverviewRevision(Guid PlanId, string PlanName, int Revision, string Reason, string SavedBy, DateTimeOffset SavedAt);
