using System.IO.Compression;
using System.Text;

namespace BirkNext.Api.Tests.BrowserVerification;

/// <summary>
/// Deterministic Testoversikt fixture (V12, 2026-11-27) and its INDEPENDENT oracle. The expected populations are computed here from this model only —
/// never through ManualTestingOverviewPresentation, TestPlanSuggestionService or any other production aggregation. A scenario's oracle identity is
/// (specification path, fixture token); the token (e.g. <c>AUR-BKG1-US1-S2</c>) is written into the scenario's action, so the browser shows it in
/// every row and drill-down. The same token in two specifications is the deliberate «same scenario title across specifications» case.
/// </summary>
public static class TestOverviewFixture
{
    public sealed record Scenario(string Token, string Given, string When, string Then, IReadOnlyList<string> Requirements)
    {
        public string Title => When;
    }

    public sealed record Story(string Id, string Title, string? Priority, IReadOnlyList<Scenario> Scenarios);

    /// <summary>One specification document. <paramref name="Module"/> is the fixture's own expectation of its module («» = project level).</summary>
    public sealed record Spec(string Path, string Module, string Pack, string Title, IReadOnlyList<Story> Stories, IReadOnlyList<string> Requirements)
    {
        public IEnumerable<(Story Story, Scenario Scenario)> AcceptanceScenarios => Stories.SelectMany(s => s.Scenarios.Select(x => (s, x)));
        /// <summary>Requirements no acceptance scenario of this document names (the fixture's rule, written independently).</summary>
        public IReadOnlyList<string> RequirementsWithoutScenario =>
            [.. Requirements.Where(r => !AcceptanceScenarios.Any(a => a.Scenario.Requirements.Contains(r, StringComparer.OrdinalIgnoreCase)))];
    }

    /// <summary>Fixture module marker for a document outside every module root that is not under the project's own specs/ folder.</summary>
    public const string UnassignedModule = "?";

    /// <param name="Wrapped">The ZIP holds one top folder named after the application (as most downloaded archives do). The canonical module rule
    /// then sees that folder as a module root: modules are «Aurora/Booking» and the project's own specs/ is the module «Aurora».</param>
    public sealed record Application(string Name, bool Wrapped, IReadOnlyList<Spec> Specs)
    {
        public IEnumerable<string> Modules => Specs.Select(s => s.Module).Where(m => m.Length > 0 && m != UnassignedModule).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);

        /// <summary>The module key the explorers and the scope dropdown use (value of the ssp-module option).</summary>
        public string ModuleKey(string module) => Wrapped ? module.Length == 0 ? Name : $"{Name}/{module}" : module == UnassignedModule ? "" : module;

        /// <summary>The module name Testoversikt shows for a fixture module (rows, follow-up groups).</summary>
        public string OverviewModule(string module) => Wrapped ? ModuleKey(module)
            : module switch { "" => "Prosjektnivå", UnassignedModule => "Uten modultilknytning", _ => module };

        public string ArchivePath(Spec spec) => Wrapped ? $"{Name}/{spec.Path}" : spec.Path;
    }

    /// <summary>Oracle identity of one acceptance scenario.</summary>
    public readonly record struct Key(string SpecPath, string Token);

    private static Scenario S(string token, string given, string requirement) => S(token, given, requirement, then: null);

    private static Scenario S(string token, string given, string requirement, string? then) =>
        new(token, given, $"the tester runs check {token}", then ?? $"the result of {token} is recorded ({requirement})", [requirement]);

    private static Spec Feature(string app, string module, string pack, string title, IReadOnlyList<Story> stories, params string[] requirements) =>
        new(module.Length == 0 ? $"specs/{pack}/spec.md" : $"{module}/specs/{pack}/spec.md", module, pack, title, stories, requirements);

    /// <summary>Application A (wrapped in the folder «Aurora»): three modules, a project-level specification, duplicate US/FR ids in every document, a shared scenario title and
    /// enough P1 gaps (22 Catalog specifications, one with 7 P1 scenarios) to page the P1 follow-up list (20 groups per page, 5 rows per group).</summary>
    public static Application Aurora { get; } = BuildAurora();

    /// <summary>Application B: one module, no overlap with A (switching applications must replace every figure).</summary>
    /// <summary>Application B: NOT wrapped in a folder: one module, the project's own specs/ («Prosjektnivå») and a document outside every
    /// module root («Uten modultilknytning»). No overlap with A (switching applications must replace every figure).</summary>
    public static Application Borealis { get; } = new("Borealis", Wrapped: false,
    [
        Feature("Borealis", "Inventory", "001-stock", "Stock levels",
        [
            new("US1", "See stock", "P1", [S("BOR-INV1-US1-S1", "a warehouse with stock", "FR-001"), S("BOR-INV1-US1-S2", "an empty warehouse", "FR-002")]),
            new("US2", "Reorder", "P3", [S("BOR-INV1-US2-S1", "stock below the limit", "FR-003")]),
        ], "FR-001", "FR-002", "FR-003", "FR-004"),
        Feature("Borealis", "", "001-overview", "Overview",
        [
            new("US1", "See the overview", "P2", [S("BOR-PRJ1-US1-S1", "a signed-in manager", "FR-001")]),
        ], "FR-001"),
        new("docs/analysis/spec.md", UnassignedModule, "", "Analysis",
        [
            new("US1", "Read the analysis", "P1", [S("BOR-UNA1-US1-S1", "an analysis document", "FR-001")]),
        ], ["FR-001", "FR-002"]),
    ]);

    private static Application BuildAurora()
    {
        const string a = "Aurora";
        // The same Given/When (token AUR-SHARED-S1) in Booking/001 and Billing/001: one title, two scenarios (identity includes the file).
        Scenario Shared(string requirement) => new("AUR-SHARED-S1", "a registered customer", "the tester runs check AUR-SHARED-S1", $"the customer overview is shown ({requirement})", [requirement]);
        var specs = new List<Spec>
        {
            Feature(a, "Booking", "001-reservations", "Reservations",
            [
                new("US1", "Reserve a room", "P1", [S("AUR-BKG1-US1-S1", "a free room", "FR-001"), S("AUR-BKG1-US1-S2", "an occupied room", "FR-001"), Shared("FR-002")]),
                new("US2", "Change a reservation", "P2", [S("AUR-BKG1-US2-S1", "an existing reservation", "FR-003")]),
            ], "FR-001", "FR-002", "FR-003", "FR-004"),
            Feature(a, "Booking", "002-cancellations", "Cancellations",
            [
                new("US1", "Cancel", "P1", [S("AUR-BKG2-US1-S1", "a reservation tomorrow", "FR-001")]),
                new("US2", "Refund", "P3", [S("AUR-BKG2-US2-S1", "a cancelled reservation", "FR-002")]),
            ], "FR-001", "FR-002"),
            Feature(a, "Billing", "001-invoices", "Invoices",
            [
                new("US1", "Invoice a stay", "P1", [S("AUR-BIL1-US1-S1", "a finished stay", "FR-001"), Shared("FR-001")]),
                new("US2", "Send reminders", "P2", [S("AUR-BIL1-US2-S1", "an unpaid invoice", "FR-002")]),
            ], "FR-001", "FR-002", "FR-003"),
            Feature(a, "", "001-platform", "Platform",
            [
                new("US1", "Sign in", "P1", [S("AUR-PLT1-US1-S1", "a known user", "FR-001")]),
            ], "FR-001", "FR-002"),
            // Twelve P1 scenarios in one document: the follow-up group shows 10 and offers «vis flere».
            Feature(a, "Catalog", "000-catalog-search", "Catalog search",
            [
                new("US1", "Search", "P1", [.. Enumerable.Range(1, 12).Select(i => S($"AUR-CAT0-US1-S{i}", $"catalog state {i}", "FR-001"))]),
            ], "FR-001"),
        };
        // 22 more Catalog documents with one uncovered P1 scenario each: more than one page of follow-up groups.
        for (var i = 1; i <= 22; i++)
            specs.Add(Feature(a, "Catalog", $"{i:000}-catalog-item-{i:00}", $"Catalog item {i:00}",
                [new("US1", $"Item {i:00}", "P1", [S($"AUR-CAT{i:00}-US1-S1", $"catalog item {i:00}", "FR-001")])], "FR-001"));
        return new(a, Wrapped: true, specs);
    }

    public static string Markdown(Spec spec)
    {
        var b = new StringBuilder();
        b.AppendLine($"# Feature Specification: {spec.Title}").AppendLine();
        b.AppendLine("## User Scenarios & Testing *(mandatory)*").AppendLine();
        var n = 0;
        foreach (var story in spec.Stories)
        {
            n++;
            b.AppendLine($"### User Story {story.Id[2..]} - {story.Title}{(story.Priority is null ? "" : $" (Priority: {story.Priority})")}").AppendLine();
            b.AppendLine($"As a tester I want {story.Title.ToLowerInvariant()}.").AppendLine();
            b.AppendLine("**Acceptance Scenarios**:").AppendLine();
            var i = 0;
            foreach (var s in story.Scenarios) b.AppendLine($"{++i}. **Given** {s.Given}, **When** {s.When}, **Then** {s.Then}.");
            b.AppendLine();
        }
        b.AppendLine("## Requirements *(mandatory)*").AppendLine().AppendLine("### Functional Requirements").AppendLine();
        foreach (var r in spec.Requirements) b.AppendLine($"- **{r}**: The system MUST support {spec.Title.ToLowerInvariant()} rule {r}.");
        return b.ToString();
    }

    /// <summary>The ZIP the browser imports (Spec Kit documents only: no source, no secrets).</summary>
    public static byte[] Archive(Application app)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var spec in app.Specs)
            {
                Write(zip, app.ArchivePath(spec), Markdown(spec));
                if (spec.Pack.Length == 0) continue; // a loose document has no Spec-Kit feature folder
                var path = app.ArchivePath(spec);
                Write(zip, $"{path[..path.LastIndexOf('/')]}/plan.md", $"# Implementation Plan: {spec.Title}\n\n**Language/Version**: C# / .NET 8\n");
            }
        }
        return buffer.ToArray();
    }

    private static void Write(ZipArchive zip, string path, string text)
    {
        var entry = zip.CreateEntry(path);
        entry.LastWriteTime = new DateTimeOffset(2026, 11, 27, 12, 0, 0, TimeSpan.Zero); // deterministic archive bytes
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(text);
    }

    // ---------------------------------------------------------------- test plans and cases (seeded through the plan API)

    /// <summary>How a fixture case is left after seeding (each through the normal plan API: create, review, edit).</summary>
    public enum CaseState
    {
        /// <summary>Generated, never reviewed: waits for approval, not ready.</summary>
        Awaiting,
        /// <summary>Accepted, but no verification method: approved and NOT ready.</summary>
        Approved,
        /// <summary>Accepted + «needs no Test Data» + a verification method: approved and ready.</summary>
        ApprovedReady,
        /// <summary>A reviewer asked for clarification: waits, not ready.</summary>
        NeedsClarification,
        /// <summary>Generated with a blocking prerequisite (a registered hindrance): waits, not ready.</summary>
        Blocked,
    }

    /// <summary>One seeded case: its plan, the scenario it is prepared from (oracle key) and the state it is left in.</summary>
    public sealed record CaseFixture(string Plan, Key Scenario, CaseState State);

    public const string PlanA = "Fixture A – Booking og Billing", PlanB = "Fixture B – tvers av moduler";

    /// <summary>Aurora's cases: two plans, cases across specifications and modules, one per state, and the shared title in both documents.</summary>
    public static IReadOnlyList<CaseFixture> AuroraCases { get; } =
    [
        new(PlanA, new("Booking/specs/001-reservations/spec.md", "AUR-BKG1-US1-S1"), CaseState.ApprovedReady),
        new(PlanA, new("Booking/specs/001-reservations/spec.md", "AUR-BKG1-US1-S2"), CaseState.Awaiting),
        new(PlanA, new("Booking/specs/001-reservations/spec.md", "AUR-SHARED-S1"), CaseState.Awaiting),
        new(PlanA, new("Billing/specs/001-invoices/spec.md", "AUR-SHARED-S1"), CaseState.Approved),
        new(PlanA, new("Billing/specs/001-invoices/spec.md", "AUR-BIL1-US2-S1"), CaseState.NeedsClarification),
        new(PlanB, new("Booking/specs/002-cancellations/spec.md", "AUR-BKG2-US1-S1"), CaseState.Blocked),
        new(PlanB, new("specs/001-platform/spec.md", "AUR-PLT1-US1-S1"), CaseState.Awaiting),
        new(PlanB, new("Booking/specs/001-reservations/spec.md", "AUR-BKG1-US2-S1"), CaseState.ApprovedReady),
    ];

    /// <summary>
    /// Application C (stale source, S13/S14), used by one journey only: v1 has Core/001-a (two P1 scenarios, both with a case) and Core/002-b; v2 —
    /// imported as a NEW VERSION and made current — changes the expected result of S1 (same Given/When: same scenario, changed source), removes S2
    /// and removes 002-b entirely (a saved selection of it is then a stale reference).
    /// </summary>
    public static Application CirrusV1 { get; } = new("Cirrus", Wrapped: true,
    [
        Feature("Cirrus", "Core", "001-a", "Accounts",
            [new("US1", "Open an account", "P1", [S("CIR-A-US1-S1", "a new customer", "FR-001"), S("CIR-A-US1-S2", "a returning customer", "FR-001")])], "FR-001"),
        Feature("Cirrus", "Core", "002-b", "Statements",
            [new("US1", "Read a statement", "P2", [S("CIR-B-US1-S1", "a monthly statement", "FR-001")])], "FR-001"),
    ]);

    public static Application CirrusV2 { get; } = new("Cirrus", Wrapped: true,
    [
        Feature("Cirrus", "Core", "001-a", "Accounts",
            [new("US1", "Open an account", "P1", [S("CIR-A-US1-S1", "a new customer", "FR-001", then: "the account is opened and a welcome letter is sent (FR-001)")])], "FR-001"),
    ]);

    public const string PlanC = "Fixture C – kildeendring";

    public static IReadOnlyList<CaseFixture> CirrusCases { get; } =
    [
        new(PlanC, new("Core/specs/001-a/spec.md", "CIR-A-US1-S1"), CaseState.Approved),
        new(PlanC, new("Core/specs/001-a/spec.md", "CIR-A-US1-S2"), CaseState.Awaiting),
    ];

    public static IReadOnlyList<CaseFixture> CasesOf(Application app) => app == Aurora ? AuroraCases : app == CirrusV1 ? CirrusCases : [];

    public static IReadOnlyList<CaseFixture> CasesIn(Application app, Scope scope)
    {
        var specs = SpecsIn(app, scope).Select(s => s.Path).ToHashSet(StringComparer.Ordinal);
        return [.. CasesOf(app).Where(c => specs.Contains(c.Scenario.SpecPath))];
    }

    public static string? PriorityOf(Application app, Key k) =>
        app.Specs.Single(s => s.Path == k.SpecPath).Stories.Single(st => st.Scenarios.Any(x => x.Token == k.Token)).Priority;

    public static bool IsApproved(CaseState s) => s is CaseState.Approved or CaseState.ApprovedReady;
    public static bool IsReady(CaseState s) => s == CaseState.ApprovedReady;

    // ---------------------------------------------------------------- oracle (independent of production aggregation)

    /// <summary>A scope of the fixture: null = all at that level. <paramref name="Module"/> «» = project-level documents.</summary>
    public sealed record Scope(string? Module = null, string? Pack = null, string? SpecPath = null);

    public static IReadOnlyList<Spec> SpecsIn(Application app, Scope scope) => [.. app.Specs.Where(s =>
        (scope.Module is null || s.Module == scope.Module) && (scope.Pack is null || s.Pack == scope.Pack) && (scope.SpecPath is null || s.Path == scope.SpecPath))];

    public static HashSet<Key> Scenarios(Application app, Scope scope, Func<Story, bool>? story = null) =>
        [.. SpecsIn(app, scope).SelectMany(s => s.AcceptanceScenarios.Where(a => story?.Invoke(a.Story) ?? true).Select(a => new Key(s.Path, a.Scenario.Token)))];

    public static int RequirementsWithoutScenario(Application app, Scope scope) => SpecsIn(app, scope).Sum(s => s.RequirementsWithoutScenario.Count);

    /// <summary>«Scenarioforslag»: acceptance scenarios plus requirement-only proposals (no test cases exist in this fixture).</summary>
    public static int ScenarioProposals(Application app, Scope scope) => Scenarios(app, scope).Count + RequirementsWithoutScenario(app, scope);

    /// <summary>Scenarios with at least one seeded case (covered).</summary>
    public static HashSet<Key> Covered(Application app) => [.. CasesOf(app).Select(c => c.Scenario)];

    /// <summary>P1 scenarios without a test case: every P1 acceptance scenario of the scope minus those the seeded cases cover.</summary>
    public static HashSet<Key> P1WithoutCases(Application app, Scope scope)
    {
        var covered = Covered(app);
        return [.. Scenarios(app, scope, s => s.Priority == "P1").Where(k => !covered.Contains(k))];
    }

    /// <summary>Scenarios of one priority without a test case.</summary>
    public static int GapOf(Application app, Scope scope, string priority)
    {
        var covered = Covered(app);
        return Scenarios(app, scope, s => s.Priority == priority).Count(k => !covered.Contains(k));
    }

    public static int Priority(Application app, Scope scope, string priority) => Scenarios(app, scope, s => s.Priority == priority).Count;

    /// <summary>Per-module acceptance-scenario counts (project-level documents under «»).</summary>
    public static IReadOnlyDictionary<string, int> ScenariosPerModule(Application app) =>
        app.Specs.GroupBy(s => s.Module).ToDictionary(g => g.Key, g => g.Sum(s => s.AcceptanceScenarios.Count()));

    public static IReadOnlyDictionary<string, int> RequirementsPerModule(Application app) =>
        app.Specs.GroupBy(s => s.Module).ToDictionary(g => g.Key, g => g.Sum(s => s.RequirementsWithoutScenario.Count));
}
