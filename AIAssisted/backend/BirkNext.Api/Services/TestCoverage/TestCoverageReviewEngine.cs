using System.Diagnostics;
using System.Text.RegularExpressions;
using Path = System.IO.Path;
using BirkNext.Api.Services.SourceAnalysis;
using BirkNext.CriticalE2E;
using BirkNext.Integrations;
using BirkNext.SourceArchitecture;
using BirkNext.TestCoverage;
using BirkNext.TestEvidence;

namespace BirkNext.Api.Services.TestCoverage;

/// <summary>A QA automated end-to-end flow BirkNext knows about (Critical E2E definition) with its most recent recorded outcome, if any.</summary>
public sealed record E2EFlowEvidence(string Id, string Name, string Module, string Description, CriticalE2EStatus? LatestStatus, DateTimeOffset? LatestRunAt);

/// <summary>
/// Test Coverage &amp; Overlap Review engine. Pure function over one Source Analysis snapshot (optionally a baseline of the same repository), the
/// browser's workspace evidence (requirements, acceptance scenarios, imported executions), Critical E2E flow definitions and reviewer decisions.
/// Everything is index-based (project → components, requirement/token postings → tests); no pairwise comparison over all tests.
/// It never certifies coverage, never computes a coverage percentage and never turns discovery into execution or mocks into real integrations.
/// </summary>
public static partial class TestCoverageReviewEngine
{
    private static readonly HashSet<ArchitectureComponentType> Runnable =
    [
        ArchitectureComponentType.Frontend, ArchitectureComponentType.Api, ArchitectureComponentType.Worker, ArchitectureComponentType.Adapter, ArchitectureComponentType.BackgroundService,
        ArchitectureComponentType.Gateway, ArchitectureComponentType.Proxy, ArchitectureComponentType.ScheduledJob, ArchitectureComponentType.Function, ArchitectureComponentType.Cli,
    ];
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "the", "and", "for", "with", "that", "this", "when", "then", "given", "should", "user", "system", "from", "into", "have", "has", "are", "was", "will", "not", "can", "its",
        "their", "they", "them", "there", "which", "what", "been", "being", "also", "only", "each", "other", "than", "such", "where", "while", "after", "before",
    };
    [GeneratedRegex(@"[A-Za-zÆØÅæøå0-9]+")]
    private static partial Regex Word();

    private sealed record Dev(CoverageTest Test, TestBehaviorFact? Fact, HashSet<string> Tokens);

    public static TestCoverageReviewResult Review(IqrSourceSnapshot current, IqrSourceSnapshot? baseline, TestCoverageReviewRequest request,
        IReadOnlyList<CoverageDecision> decisions, IReadOnlyList<E2EFlowEvidence> e2eFlows, DateTimeOffset now, CancellationToken ct = default)
    {
        var phases = new List<ReviewPhase>();
        var limitations = new List<string>();
        var watch = Stopwatch.StartNew();
        void Phase(string name, int items) { phases.Add(new(name, items, watch.ElapsedMilliseconds)); watch.Restart(); }

        var workspace = request.Workspace ?? new CoverageWorkspaceEvidence();
        var inventory = current.TestInventory;
        var facts = current.TestBehaviorEvidence;
        var architecture = current.Architecture;
        if (facts is null) limitations.Add("This snapshot was analyzed before per-test facts existed: tests are listed from discovery only and are not analyzed. Analyze the source again.");
        else limitations.AddRange(facts.Limitations);
        if (inventory is null) limitations.Add("No source test inventory exists on this snapshot.");
        if (architecture is null) limitations.Add("No Source Architecture model exists on this snapshot: components and journeys are not available.");

        // ── 1. Discovering test projects / analyzing tests ─────────────────────────────────────────────────────────
        var ownership = decisions.Where(d => d.Kind == CoverageDecisionKind.ProjectOwnership).GroupBy(d => d.SubjectKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(d => d.DecidedAt).First().Value, StringComparer.Ordinal);
        var components = (architecture?.Components ?? []).Where(c => Runnable.Contains(c.ComponentType)).ToList();
        var projectToComponents = new Dictionary<string, List<ArchitectureComponent>>(StringComparer.OrdinalIgnoreCase);
        foreach (var component in components)
            foreach (var project in new[] { ProjectName(component.SourceProject), component.Name }.Concat(component.IncludedLibraries).Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
                (projectToComponents.TryGetValue(project, out var list) ? list : projectToComponents[project] = []).Add(component);

        var executions = Executions(workspace.Executions);
        var factById = (facts?.Tests ?? []).ToDictionary(f => f.TestId, StringComparer.Ordinal);
        var devs = new List<Dev>();
        foreach (var fact in facts?.Tests ?? [])
        {
            ct.ThrowIfCancellationRequested();
            devs.Add(new Dev(BuildTest(fact, null, projectToComponents, ownership, executions), fact, Tokens(fact)));
        }
        // Inventory tests without per-test facts (old snapshot, or a discovery-only framework): listed, not analyzed.
        foreach (var definition in (inventory?.Definitions ?? []).Where(d => !factById.ContainsKey(d.TestDefinitionId)))
            devs.Add(new Dev(BuildTest(null, definition, projectToComponents, ownership, executions), null, Tokens(definition.MethodName)));
        Phase("Discovering test projects and analyzing tests", devs.Count);

        // ── 2. Journeys ──────────────────────────────────────────────────────────────────────────────────────────────
        var journeyDecisions = decisions.Where(d => d.Kind == CoverageDecisionKind.JourneyConnection).GroupBy(d => d.SubjectKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(d => d.DecidedAt).First().Value, StringComparer.Ordinal);
        var testsByComponent = devs.SelectMany(d => d.Test.Components.Select(c => (c, d))).GroupBy(x => x.c, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(x => x.d).ToList(), StringComparer.Ordinal);
        var journeys = architecture is null ? [] : Journeys(current, architecture, components, testsByComponent, journeyDecisions, e2eFlows);
        Phase("Finding system journeys and mapping tests to journey steps", journeys.Count);

        // ── 3. Components ────────────────────────────────────────────────────────────────────────────────────────────
        var componentCoverage = components.Select(c => ComponentView(c, architecture!, testsByComponent.GetValueOrDefault(c.Id) ?? [], e2eFlows)).ToList();

        // ── 4. QA candidates and overlaps ────────────────────────────────────────────────────────────────────────────
        var qaTests = new List<QaTestCandidate>();
        qaTests.AddRange(workspace.AcceptanceScenarios.Select(s => s with { IntendedLevel = IntendedLevel(s).Level, IntendedLevelBasis = IntendedLevel(s).Basis }));
        qaTests.AddRange(devs.Where(d => d.Test.Ownership == TestOwnership.QaAutomated).Select(d => new QaTestCandidate
        {
            Id = d.Test.TestId, Title = d.Test.Name, Source = $"QA automated test ({d.Test.Project})", Ownership = TestOwnership.QaAutomated, RequirementIds = d.Test.RequirementIds,
            IntendedLevel = d.Test.Level, IntendedLevelBasis = d.Test.LevelBasis, Execution = d.Test.Execution,
        }));
        qaTests.AddRange(e2eFlows.Select(f => new QaTestCandidate
        {
            Id = "e2e:" + f.Id, Title = f.Name, Source = "Critical E2E flow", Ownership = TestOwnership.QaAutomated, When = f.Description, IntendedLevel = TestLevel.E2E,
            IntendedLevelBasis = "Critical E2E flow (runs against a deployed target)",
            Execution = f.LatestStatus switch { CriticalE2EStatus.Passed => TestExecutionState.Passed, CriticalE2EStatus.NotRun or null => TestExecutionState.NotVerified, _ => TestExecutionState.Failed },
        }));
        var developerSide = devs.Where(d => d.Test.Ownership != TestOwnership.QaAutomated).ToList();
        // Requirement ids are only unique within a module in many repositories (every module may have its own FR-001). An id referenced by tests
        // in more than one top-level module is ambiguous and is not used as a link; it is reported instead.
        var ambiguous = devs.SelectMany(d => d.Test.RequirementIds.Select(r => (Id: r, Module: Module(d.Test.FilePath)))).GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Select(x => x.Module).Distinct(StringComparer.Ordinal).Count() > 1).ToDictionary(g => g.Key, g => g.Select(x => x.Module).Distinct(StringComparer.Ordinal).Count(), StringComparer.OrdinalIgnoreCase);
        if (ambiguous.Count > 0) limitations.Add($"{ambiguous.Count} requirement id(s) are referenced by tests in several modules (for example {string.Join(", ", ambiguous.Keys.Order(StringComparer.Ordinal).Take(3))}); those links are ambiguous and were not used.");
        var overlapDecisions = decisions.Where(d => d.Kind == CoverageDecisionKind.Overlap).GroupBy(d => d.SubjectKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(d => d.DecidedAt).First().Value, StringComparer.Ordinal);
        var overlaps = Overlaps(qaTests, developerSide, journeys, overlapDecisions, ambiguous, ct);
        Phase("Checking overlaps", overlaps.Count);

        // ── 5. Behaviours, gaps, QA scope ───────────────────────────────────────────────────────────────────────────
        var changed = ChangedProjects(current, baseline, components, devs, out var changeNote);
        if (changeNote is not null) limitations.Add(changeNote);
        var behaviors = Behaviors(workspace, developerSide, qaTests, journeys, changed, ambiguous);
        var gaps = Gaps(current, baseline, workspace, components, componentCoverage, testsByComponent, journeys, devs, changed, facts);
        Phase("Finding remaining gaps", gaps.Count);
        var scope = Scope(journeys, overlaps, behaviors, gaps, devs);
        Phase("Preparing documentation", behaviors.Count);

        var tests = devs.Select(d => d.Test).ToList();
        return new TestCoverageReviewResult
        {
            CompletedAt = now, ProjectName = workspace.ProjectName, Current = Ref(current), Baseline = baseline is null ? null : Ref(baseline),
            Inventory = InventoryOf(inventory, facts, tests, current),
            Tests = tests, QaTests = qaTests, Components = componentCoverage, Journeys = journeys, Behaviors = behaviors, Overlaps = overlaps, Gaps = gaps, QaScope = scope,
            Phases = phases, Limitations = limitations.Distinct(StringComparer.Ordinal).ToList(),
            Partial = facts is null || facts.Truncated || inventory?.Status == SourceTestDiscoveryStatus.Partial || architecture is null,
        };
    }

    // ── tests ─────────────────────────────────────────────────────────────────────────────────────────────────────

    private static Dictionary<string, string> Executions(IEnumerable<CoverageExecution> executions)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var e in executions)
        {
            if (!string.IsNullOrWhiteSpace(e.TestId)) map.TryAdd(e.TestId, e.Result);
            if (!string.IsNullOrWhiteSpace(e.TestName)) map.TryAdd("name:" + e.TestName, e.Result);
        }
        return map;
    }

    private static CoverageTest BuildTest(TestBehaviorFact? fact, SourceTestDefinition? definition, Dictionary<string, List<ArchitectureComponent>> projectToComponents,
        IReadOnlyDictionary<string, string> ownership, IReadOnlyDictionary<string, string> executions)
    {
        var id = fact?.TestId ?? definition!.TestDefinitionId;
        var project = fact?.Project ?? definition!.Project;
        var fqn = fact?.FullyQualifiedName ?? definition!.FullyQualifiedName;
        var name = fact is null ? $"{definition!.ClassName}.{definition.MethodName}" : $"{fact.ClassName}.{fact.MethodName}";
        var mapped = (fact?.TargetProjects.Count > 0 ? fact.TargetProjects : fact?.ProductionProjects ?? [])
            .SelectMany(p => projectToComponents.GetValueOrDefault(p) ?? []).Select(c => c.Id).Distinct(StringComparer.Ordinal).ToList();
        var (level, strength, basis) = fact is null ? LevelFromInventory(definition!) : Level(fact);
        var result = executions.GetValueOrDefault(id) ?? executions.GetValueOrDefault("name:" + fqn) ?? executions.GetValueOrDefault("name:" + name);
        var execution = result switch
        {
            null => TestExecutionState.NotVerified,
            _ when result.Equals("Passed", StringComparison.OrdinalIgnoreCase) => TestExecutionState.Passed,
            _ when result.Equals("Failed", StringComparison.OrdinalIgnoreCase) => TestExecutionState.Failed,
            _ when result.Equals("Skipped", StringComparison.OrdinalIgnoreCase) => TestExecutionState.Skipped,
            _ => TestExecutionState.NotExecuted,
        };
        var (owner, ownerBasis) = ownership.TryGetValue(project, out var declared) && Enum.TryParse<TestOwnership>(declared, out var o)
            ? (o, "Declared by a reviewer for this test project")
            : (TestOwnership.Unknown, "Automated test in the source repository; ownership not declared (developer or QA)");
        var assertions = fact?.Assertions ?? [];
        var meaningful = assertions.Any(a => a.Kind is not (TestAssertionKind.NotNullOnly or TestAssertionKind.Other));
        var testStrength = fact is null ? EvidenceStrength.Weak
            : assertions.Count == 0 ? EvidenceStrength.NotEnough
            : meaningful && mapped.Count > 0 && execution == TestExecutionState.Passed ? EvidenceStrength.Strong
            : EvidenceStrength.Some;
        return new CoverageTest
        {
            TestId = id, Name = name, Project = project, FilePath = fact?.FilePath ?? definition!.FilePath, Line = fact?.Line ?? definition!.Line,
            Framework = fact?.Framework ?? definition!.Framework, Level = level, LevelEvidence = strength, LevelBasis = basis, Ownership = owner, OwnershipBasis = ownerBasis,
            Analyzed = fact is not null, Skipped = fact?.Skipped ?? definition!.SkipDeclared, Execution = execution, Components = mapped,
            Boundaries = fact?.Signals ?? [], Assertions = assertions, RequirementIds = fact?.RequirementReferences ?? definition!.References.Select(r => r.Id).ToList(),
            Purpose = fact is null ? "Test found by discovery; its content was not analyzed." : Purpose(fact), Strength = testStrength,
        };
    }

    /// <summary>Test level from setup evidence first; names and categories are weak evidence and never make a test "Integration" on their own.</summary>
    internal static (TestLevel Level, EvidenceStrength Strength, string Basis) Level(TestBehaviorFact fact)
    {
        var s = fact.Signals;
        bool Has(BoundaryKind kind, params BoundaryMode[] modes) => s.Any(x => x.Kind == kind && (modes.Length == 0 || modes.Contains(x.Mode)));
        var category = string.Join(" ", fact.Categories).ToLowerInvariant();
        if (Has(BoundaryKind.Browser, BoundaryMode.Real)) return (TestLevel.E2E, EvidenceStrength.Strong, "Drives a real browser (" + s.First(x => x.Kind == BoundaryKind.Browser).Detail + ")");
        if (Has(BoundaryKind.Browser, BoundaryMode.InMemory)) return (TestLevel.Ui, EvidenceStrength.Strong, "Renders a UI component without a browser (bUnit)");
        if (category.Contains("performance") || category.Contains("load")) return (TestLevel.Performance, EvidenceStrength.Some, "Declared performance/load category");
        if (category.Contains("security")) return (TestLevel.Security, EvidenceStrength.Some, "Declared security category");
        if (category.Contains("contract") || fact.Project.Contains("Contract", StringComparison.OrdinalIgnoreCase) && fact.Assertions.Any(a => a.Kind == TestAssertionKind.Snapshot))
            return (TestLevel.Contract, EvidenceStrength.Some, "Contract test project/category with snapshot assertions");
        var realTransport = Has(BoundaryKind.EventHub, BoundaryMode.Real, BoundaryMode.RealContainer) || Has(BoundaryKind.ServiceBus, BoundaryMode.Real, BoundaryMode.RealContainer);
        if (realTransport) return (TestLevel.Integration, EvidenceStrength.Strong, "Connects to a real messaging client");
        if (Has(BoundaryKind.Http, BoundaryMode.InProcess)) return (TestLevel.Api, EvidenceStrength.Strong, "Hosts the API in-process (" + s.First(x => x.Kind == BoundaryKind.Http && x.Mode == BoundaryMode.InProcess).Detail + ")");
        if (Has(BoundaryKind.Database, BoundaryMode.RealContainer, BoundaryMode.Real)) return (TestLevel.Repository, EvidenceStrength.Strong, "Uses a real database engine (" + s.First(x => x.Kind == BoundaryKind.Database && x.Mode is BoundaryMode.RealContainer or BoundaryMode.Real).Detail + ")");
        if (Has(BoundaryKind.Messaging, BoundaryMode.InMemory)) return (TestLevel.Integration, EvidenceStrength.Some, "In-process message handling (no real broker)");
        if (Has(BoundaryKind.Database, BoundaryMode.InMemory)) return (TestLevel.Repository, EvidenceStrength.Some, "In-memory database substitute (not the deployed database engine)");
        if (s.Any(x => x.Mode is BoundaryMode.Mocked or BoundaryMode.Fake) || fact.Targets.Count > 0)
            return (TestLevel.Unit, EvidenceStrength.Some, s.Any(x => x.Mode is BoundaryMode.Mocked or BoundaryMode.Fake) ? "Dependencies replaced by mocks/fakes; no host or real dependency" : "Constructs production types directly; no host or real dependency");
        if (category.Contains("integration") || fact.Project.Contains("Integration", StringComparison.OrdinalIgnoreCase))
            return (TestLevel.NeedsReview, EvidenceStrength.Weak, "Name/category suggests integration, but no setup evidence was found");
        return (TestLevel.Unknown, EvidenceStrength.NotEnough, "No setup evidence that shows the test level");
    }

    private static (TestLevel, EvidenceStrength, string) LevelFromInventory(SourceTestDefinition definition) => definition.Kind switch
    {
        TestKind.Unit => (TestLevel.Unit, EvidenceStrength.Weak, "Project classification from discovery only (" + definition.KindBasis + ")"),
        TestKind.FrontendComponent => (TestLevel.Ui, EvidenceStrength.Weak, "Project classification from discovery only (" + definition.KindBasis + ")"),
        TestKind.Contract => (TestLevel.Contract, EvidenceStrength.Weak, "Project classification from discovery only (" + definition.KindBasis + ")"),
        TestKind.Integration => (TestLevel.NeedsReview, EvidenceStrength.Weak, "Discovery suggests integration (" + definition.KindBasis + "); content not analyzed"),
        _ => (TestLevel.Unknown, EvidenceStrength.NotEnough, "Not analyzed"),
    };

    /// <summary>A short plain-language description built only from recorded facts. Hedged: it says what the test appears to check.</summary>
    internal static string Purpose(TestBehaviorFact fact)
    {
        var parts = new List<string>();
        var subject = string.Join(" ", fact.BehaviorTokens.Take(6));
        var setup = fact.Signals.Where(s => s.Mode is not BoundaryMode.Unknown).Select(s => s.Mode switch
        {
            BoundaryMode.InProcess => "hosts the API in-process",
            BoundaryMode.RealContainer => $"uses a real {Plain(s.Kind)} in a container",
            BoundaryMode.Real => $"uses a real {Plain(s.Kind)}",
            BoundaryMode.InMemory => $"uses an in-memory {Plain(s.Kind)}",
            BoundaryMode.Mocked => $"replaces the {Plain(s.Kind)} with a mock",
            BoundaryMode.Fake => $"uses a fake {Plain(s.Kind)}",
            _ => "",
        }).Where(t => t.Length > 0).Distinct().Take(3).ToList();
        if (setup.Count > 0) parts.Add(string.Join(", ", setup));
        var checks = fact.Assertions.Select(a => a.Kind switch
        {
            TestAssertionKind.HttpStatus => $"expects HTTP {a.Detail}",
            TestAssertionKind.ExceptionThrown => $"expects {a.Detail}",
            TestAssertionKind.RecordCount => "checks how many records are stored",
            TestAssertionKind.RecordPersisted => "checks that a record is stored",
            TestAssertionKind.MessagePublished => "checks that a message is sent",
            TestAssertionKind.MockInvocationVerified => "checks that a dependency was called",
            TestAssertionKind.AuthorizationResult => "checks the authorization result",
            TestAssertionKind.Snapshot => "compares output with an approved snapshot",
            TestAssertionKind.Rendered => "checks rendered output",
            TestAssertionKind.ValueEquality => "checks returned values",
            TestAssertionKind.CollectionShape => "checks a returned collection",
            TestAssertionKind.BooleanCondition => "checks a condition",
            TestAssertionKind.NotNullOnly => "only checks that a result exists",
            _ => "",
        }).Where(t => t.Length > 0).Distinct().Take(3).ToList();
        if (checks.Count > 0) parts.Add(string.Join(", ", checks));
        var body = parts.Count == 0 ? "no assertion was recognised" : string.Join("; ", parts);
        return $"Test appears to check {(subject.Length > 0 ? $"\"{subject}\"" : "behaviour")}: {body}." + (fact.Assertions.Count == 0 ? " Review recommended." : "");
    }

    internal static string Plain(BoundaryKind kind) => kind switch
    {
        BoundaryKind.Http => "HTTP dependency", BoundaryKind.GraphQl => "GraphQL dependency", BoundaryKind.Database => "database", BoundaryKind.EventHub => "Event Hub",
        BoundaryKind.ServiceBus => "Service Bus", BoundaryKind.Messaging => "message bus", BoundaryKind.Browser => "browser", BoundaryKind.FileSystem => "file system",
        BoundaryKind.Storage => "storage", BoundaryKind.Cdc => "change capture", BoundaryKind.ExternalService => "external service", _ => "dependency",
    };

    private static string Stem(string token) => token.Length > 4 && token.EndsWith('s') ? token[..^1] : token;
    private static HashSet<string> Tokens(TestBehaviorFact fact) =>
        fact.BehaviorTokens.Concat(SourceAnalysis.TestBehavior.TestBehaviorSourceAnalyzer.Tokens(fact.ClassName.Replace("Tests", "").Replace("Test", "")))
            .Where(t => t.Length > 2 && !StopWords.Contains(t)).Select(Stem).ToHashSet(StringComparer.Ordinal);
    private static HashSet<string> Tokens(string name) => SourceAnalysis.TestBehavior.TestBehaviorSourceAnalyzer.Tokens(name).Where(t => t.Length > 2 && !StopWords.Contains(t)).Select(Stem).ToHashSet(StringComparer.Ordinal);
    private static HashSet<string> TextTokens(params string?[] text) => text.Where(t => !string.IsNullOrWhiteSpace(t))
        .SelectMany(t => Word().Matches(t!).Select(m => m.Value.ToLowerInvariant())).Where(w => w.Length > 3 && !StopWords.Contains(w)).Select(Stem).ToHashSet(StringComparer.Ordinal);

    // ── journeys ──────────────────────────────────────────────────────────────────────────────────────────────────

    private sealed record Edge(string From, string To, BoundaryKind Boundary, ConnectionEvidence Evidence, List<string> Provenance);

    private static ConnectionEvidence Evidence(ArchitectureEvidenceState state) => state switch
    {
        ArchitectureEvidenceState.Confirmed => ConnectionEvidence.Confirmed,
        ArchitectureEvidenceState.StronglySupported => ConnectionEvidence.StronglySupported,
        ArchitectureEvidenceState.Inferred => ConnectionEvidence.Suggested,
        _ => ConnectionEvidence.NeedsConfirmation,
    };

    private static List<string> Provenance(IEnumerable<ArchitectureEvidence> evidence, params string?[] extra) =>
        evidence.Take(3).Select(e => $"{e.File}:{e.Line} — {e.Explanation}").Concat(extra.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!)).ToList();

    private static List<Journey> Journeys(IqrSourceSnapshot snapshot, ArchitectureSnapshot architecture, List<ArchitectureComponent> components,
        Dictionary<string, List<Dev>> testsByComponent, IReadOnlyDictionary<string, string> journeyDecisions, IReadOnlyList<E2EFlowEvidence> e2eFlows)
    {
        var nodes = new Dictionary<string, JourneyNode>(StringComparer.Ordinal);
        foreach (var c in components) nodes[c.Id] = new(c.Id, Display(c), c.ComponentType.ToString());
        foreach (var ch in architecture.MessagingChannels) nodes[ch.Id] = new(ch.Id, ch.Name, ch.Type.ToString());
        foreach (var ds in architecture.DataStores) nodes[ds.Id] = new(ds.Id, ds.LogicalName, "Database");
        foreach (var ex in architecture.ExternalSystems) nodes[ex.Id] = new(ex.Id, ex.Name, "External system");

        var edges = new List<Edge>();
        foreach (var dep in architecture.Dependencies.Where(d => d.ToId is null && d.DependencyType is ArchitectureDependencyType.Http or ArchitectureDependencyType.GraphQl && nodes.ContainsKey(d.FromComponentId)))
        {
            // Unresolved call target: a component whose name matches the configuration reference is only a suggestion; otherwise the
            // target stays an explicit "needs confirmation" node. Names never become confirmed connections.
            var reference = dep.TargetReference ?? dep.ConfigurationReference ?? "unknown target";
            var key = Normal(reference.Split(':')[0]);
            var match = key.Length >= 4 ? components.Where(c => c.Id != dep.FromComponentId && (Normal(Display(c)) == key || Normal(c.Name) == key || Normal(Display(c)).Length >= 6 && key.StartsWith(Normal(Display(c)), StringComparison.Ordinal))).ToList() : [];
            var boundary = dep.DependencyType == ArchitectureDependencyType.GraphQl ? BoundaryKind.GraphQl : BoundaryKind.Http;
            if (match.Count == 1)
                edges.Add(new(dep.FromComponentId, match[0].Id, boundary, ConnectionEvidence.Suggested, Provenance(dep.Evidence, $"configuration {reference}", $"target suggested by name similarity only ({reference} ~ {Display(match[0])}); needs confirmation")));
            else
            {
                var unresolved = "unresolved:" + dep.Id;
                nodes[unresolved] = new(unresolved, $"Unresolved target ({reference})", "Unresolved");
                edges.Add(new(dep.FromComponentId, unresolved, boundary, ConnectionEvidence.NeedsConfirmation, Provenance(dep.Evidence, $"configuration {reference}", "Source Analysis could not resolve the call target")));
            }
        }
        foreach (var dep in architecture.Dependencies)
        {
            if (!nodes.ContainsKey(dep.FromComponentId) || dep.ToId is null || !nodes.ContainsKey(dep.ToId) || dep.FromComponentId == dep.ToId) continue;
            var boundary = dep.DependencyType switch
            {
                ArchitectureDependencyType.Http => architecture.ExternalSystems.Any(e => e.Id == dep.ToId) ? BoundaryKind.ExternalService : BoundaryKind.Http,
                ArchitectureDependencyType.GraphQl => BoundaryKind.GraphQl,
                ArchitectureDependencyType.DatabaseReadWrite => BoundaryKind.Database,
                ArchitectureDependencyType.BlobReadWrite or ArchitectureDependencyType.CheckpointStore => BoundaryKind.Storage,
                _ => (BoundaryKind?)null,
            };
            if (boundary is { } b) edges.Add(new(dep.FromComponentId, dep.ToId, b, Evidence(dep.EvidenceState), Provenance(dep.Evidence, dep.ConfigurationReference is null ? null : $"configuration {dep.ConfigurationReference}", dep.ContractReference is null ? null : $"contract {dep.ContractReference}")));
        }
        foreach (var ds in architecture.DataStores)
            foreach (var componentId in ds.ReferencedByComponents.Where(nodes.ContainsKey))
                if (!edges.Any(e => e.From == componentId && e.To == ds.Id))
                    edges.Add(new(componentId, ds.Id, BoundaryKind.Database, Evidence(ds.Confidence), Provenance(ds.Evidence, ds.DbContext is null ? null : $"DbContext {ds.DbContext}")));
        var signals = snapshot.IntegrationSignals.Where(s => s.Kind.Contains("capture", StringComparison.OrdinalIgnoreCase) || s.Value.Contains("cdc", StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var ch in architecture.MessagingChannels)
        {
            var boundary = ch.Type switch
            {
                MessagingChannelType.EventHub => BoundaryKind.EventHub,
                MessagingChannelType.ServiceBusQueue or MessagingChannelType.ServiceBusTopic or MessagingChannelType.ServiceBusEntity => BoundaryKind.ServiceBus,
                _ => BoundaryKind.Messaging,
            };
            foreach (var p in ch.Producers.Where(p => nodes.ContainsKey(p.ComponentId)))
                edges.Add(new(p.ComponentId, ch.Id, boundary, Evidence(p.State), Provenance(p.Evidence, ch.ConfigurationReferences.FirstOrDefault())));
            foreach (var c in ch.Consumers.Where(c => nodes.ContainsKey(c.ComponentId)))
                edges.Add(new(ch.Id, c.ComponentId, boundary, Evidence(c.State), Provenance(c.Evidence, ch.ConfigurationReferences.FirstOrDefault())));
            if (ch.Producers.Count == 0 && ch.Consumers.Count > 0)
            {
                // The producer is outside the analyzed source. A change-capture signal names it as a suggestion only, and only for event-streaming
                // channels (change capture streams events); a signal of a consuming component is preferred over any other.
                var upstream = "upstream:" + ch.Id;
                var streaming = ch.Type is MessagingChannelType.EventHub or MessagingChannelType.KafkaTopic;
                var consumers = ch.Consumers.Select(c => c.ComponentId).ToHashSet(StringComparer.Ordinal);
                var capture = !streaming ? null : signals.FirstOrDefault(sg => sg.ComponentId is { } id && consumers.Contains(id)) ?? signals.FirstOrDefault();
                nodes[upstream] = new(upstream, capture is null ? "Upstream producer (outside this source)" : $"Upstream change capture ({capture.Value})", "External source");
                edges.Add(new(upstream, ch.Id, capture is null ? boundary : BoundaryKind.Cdc, capture is null ? ConnectionEvidence.NeedsConfirmation : ConnectionEvidence.Suggested,
                    capture is null ? ["No producer of this channel was found in the analyzed source."] : [$"{capture.Evidence.File}:{capture.Evidence.Line} — {capture.Kind} signal; the producing system is not in this source"]));
            }
        }
        edges = edges.DistinctBy(e => (e.From, e.To, e.Boundary)).ToList();

        var outgoing = edges.GroupBy(e => e.From, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var incoming = edges.Select(e => e.To).ToHashSet(StringComparer.Ordinal);
        var sources = outgoing.Keys.Where(n => !incoming.Contains(n) && nodes.TryGetValue(n, out var node) && node.Kind != "Database").OrderBy(n => nodes[n].Name, StringComparer.Ordinal).ToList();
        var paths = new List<List<Edge>>();
        foreach (var source in sources)
        {
            var perSource = 0;
            void Walk(string at, List<Edge> path, HashSet<string> seen)
            {
                if (paths.Count >= 60 || perSource >= 12) return;
                var next = outgoing.GetValueOrDefault(at)?.Where(e => !seen.Contains(e.To)).ToList() ?? [];
                if (next.Count == 0 || path.Count >= 8) { if (path.Count >= 1) { paths.Add([.. path]); perSource++; } return; }
                foreach (var edge in next)
                {
                    seen.Add(edge.To); path.Add(edge);
                    Walk(edge.To, path, seen);
                    path.RemoveAt(path.Count - 1); seen.Remove(edge.To);
                }
            }
            Walk(source, [], [source]);
        }

        var componentById = components.ToDictionary(c => c.Id, StringComparer.Ordinal);
        var journeys = new List<Journey>();
        foreach (var path in paths.Where(p => p.Count >= 2 || p.Any(e => e.Boundary is not BoundaryKind.Database)))
        {
            var steps = path.Select(e => Step(e, nodes, componentById, testsByComponent, journeyDecisions, e2eFlows)).ToList();
            var nodeIds = new[] { path[0].From }.Concat(path.Select(e => e.To)).ToList();
            var title = string.Join(" → ", nodeIds.Select(n => nodes[n].Name));
            journeys.Add(new Journey
            {
                Id = "journey:" + string.Join(">", nodeIds), Title = title, Nodes = nodeIds.Select(n => nodes[n]).ToList(), Steps = steps,
                Weakest = steps.Max(s => s.ReviewerDecision == "Confirmed" ? ConnectionEvidence.Confirmed : s.Evidence),
                FocusForTesters = steps.Where(s => s.Developer is not StepCoverage.Covered).Select(Focus).Distinct(StringComparer.Ordinal).Take(6).ToList(),
            });
        }
        return journeys.OrderBy(j => j.Steps.Count(s => s.Developer == StepCoverage.Covered) * 1.0 / Math.Max(1, j.Steps.Count)).ThenByDescending(j => j.Steps.Count).ThenBy(j => j.Title, StringComparer.Ordinal).Take(40).ToList();
    }

    private static JourneyStep Step(Edge edge, Dictionary<string, JourneyNode> nodes, Dictionary<string, ArchitectureComponent> components,
        Dictionary<string, List<Dev>> testsByComponent, IReadOnlyDictionary<string, string> decisions, IReadOnlyList<E2EFlowEvidence> e2eFlows)
    {
        var id = $"{edge.From}|{edge.To}|{edge.Boundary}";
        var fromTests = testsByComponent.GetValueOrDefault(edge.From) ?? [];
        var toTests = testsByComponent.GetValueOrDefault(edge.To) ?? [];
        var kinds = edge.Boundary switch
        {
            BoundaryKind.EventHub or BoundaryKind.ServiceBus or BoundaryKind.Messaging or BoundaryKind.Cdc => new[] { BoundaryKind.EventHub, BoundaryKind.ServiceBus, BoundaryKind.Messaging },
            BoundaryKind.Http or BoundaryKind.GraphQl or BoundaryKind.ExternalService => new[] { BoundaryKind.Http, BoundaryKind.GraphQl },
            BoundaryKind.Database => new[] { BoundaryKind.Database },
            BoundaryKind.Storage => new[] { BoundaryKind.Storage },
            _ => new[] { edge.Boundary },
        };
        // The component side the step belongs to: producers/callers own outbound steps; consumers own inbound channel steps.
        var owner = components.ContainsKey(edge.From) ? fromTests : toTests;
        var other = components.ContainsKey(edge.From) ? toTests : fromTests;
        var touching = owner.Select(d => (d, Modes: d.Test.Boundaries.Where(b => kinds.Contains(b.Kind)).Select(b => b.Mode).ToList())).Where(x => x.Modes.Count > 0).ToList();
        var bothSides = owner.Where(d => other.Contains(d)).Where(d => d.Test.Boundaries.Any(b => kinds.Contains(b.Kind) && b.Mode is BoundaryMode.Real or BoundaryMode.RealContainer or BoundaryMode.InProcess)).ToList();
        StepCoverage coverage; string explanation; List<Dev> evidence;
        if (edge.From.StartsWith("upstream:", StringComparison.Ordinal))
        { coverage = StepCoverage.NotVerified; explanation = "The producing system is outside this source; no test in this source can prove this step."; evidence = []; }
        else if (bothSides.Count > 0)
        { coverage = StepCoverage.Covered; explanation = $"{bothSides.Count} test(s) exercise both sides of this step with a real or in-process {Plain(edge.Boundary)}."; evidence = bothSides; }
        else if (touching.Any(x => x.Modes.Any(m => m is BoundaryMode.Real or BoundaryMode.RealContainer)))
        { evidence = touching.Where(x => x.Modes.Any(m => m is BoundaryMode.Real or BoundaryMode.RealContainer)).Select(x => x.d).ToList(); coverage = StepCoverage.PartlyCovered; explanation = $"{evidence.Count} test(s) use a real {Plain(edge.Boundary)} for one side only; the deployed connection is not verified."; }
        else if (touching.Any(x => x.Modes.Any(m => m is BoundaryMode.InProcess or BoundaryMode.InMemory)))
        { evidence = touching.Where(x => x.Modes.Any(m => m is BoundaryMode.InProcess or BoundaryMode.InMemory)).Select(x => x.d).ToList(); coverage = StepCoverage.PartlyCovered; explanation = $"{evidence.Count} test(s) exercise this in-process or with an in-memory {Plain(edge.Boundary)}, not the real one."; }
        else if (touching.Any(x => x.Modes.Any(m => m is BoundaryMode.Mocked or BoundaryMode.Fake)))
        { evidence = touching.Select(x => x.d).ToList(); coverage = StepCoverage.MockOnly; explanation = $"{evidence.Count} test(s) cover this only with a mocked or fake {Plain(edge.Boundary)}."; }
        else if (owner.Count > 0)
        { evidence = owner.Take(20).ToList(); coverage = StepCoverage.LowerLevelOnly; explanation = $"{owner.Count} test(s) cover this component, but none reaches this {Plain(edge.Boundary)}."; }
        else
        { coverage = StepCoverage.NotVerified; explanation = "No test evidence was found for this step."; evidence = []; }

        var names = new[] { nodes[edge.From].Name, nodes[edge.To].Name };
        var flows = e2eFlows.Where(f => names.Any(n => f.Module.Length > 0 && n.Contains(f.Module, StringComparison.OrdinalIgnoreCase))).ToList();
        var passed = flows.FirstOrDefault(f => f.LatestStatus == CriticalE2EStatus.Passed);
        return new JourneyStep
        {
            Id = id, From = edge.From, To = edge.To, FromName = nodes[edge.From].Name, ToName = nodes[edge.To].Name, Boundary = edge.Boundary, Evidence = edge.Evidence,
            Provenance = edge.Provenance, ReviewerDecision = decisions.GetValueOrDefault(id),
            Developer = coverage, DeveloperExplanation = explanation, DeveloperTestIds = evidence.Select(d => d.Test.TestId).Distinct(StringComparer.Ordinal).Take(25).ToList(),
            Qa = flows.Count > 0 ? StepCoverage.PartlyCovered : StepCoverage.NotVerified,
            QaExplanation = flows.Count > 0 ? $"Critical E2E flow(s) for this module: {string.Join(", ", flows.Select(f => f.Name).Take(3))}. The flow is not proven to cross this exact step." : "No QA test mapped to this step.",
            Runtime = passed is null ? "Not verified" : $"Some — a Critical E2E run of \"{passed.Name}\" passed; this exact step is not proven by it.",
        };
    }

    private static string Focus(JourneyStep step) => step.Boundary switch
    {
        BoundaryKind.Cdc => $"Real change capture into {step.ToName}",
        BoundaryKind.EventHub or BoundaryKind.ServiceBus or BoundaryKind.Messaging when step.From.StartsWith("upstream:", StringComparison.Ordinal) => $"A real message arriving on {step.ToName}",
        BoundaryKind.EventHub or BoundaryKind.ServiceBus or BoundaryKind.Messaging => $"Real {Plain(step.Boundary)} message from {step.FromName} to {step.ToName}",
        BoundaryKind.Http or BoundaryKind.GraphQl => $"Deployed call from {step.FromName} to {step.ToName} (including authentication between them)",
        BoundaryKind.Database => $"Final result stored by {step.FromName} in {step.ToName}",
        BoundaryKind.ExternalService => $"Call from {step.FromName} to the external system {step.ToName}",
        _ => $"{step.FromName} → {step.ToName}",
    };

    // ── components ────────────────────────────────────────────────────────────────────────────────────────────────

    private static ComponentCoverage ComponentView(ArchitectureComponent c, ArchitectureSnapshot a, List<Dev> tests, IReadOnlyList<E2EFlowEvidence> flows)
    {
        var dims = new List<ComponentDimension>();
        var hasHttp = a.Interfaces.Any(i => i.ComponentId == c.Id && (i.Protocol.Contains("HTTP", StringComparison.OrdinalIgnoreCase) || i.Protocol.Contains("GraphQL", StringComparison.OrdinalIgnoreCase)));
        var hasDb = a.DataStores.Any(d => d.ReferencedByComponents.Contains(c.Id)) || a.Dependencies.Any(d => d.FromComponentId == c.Id && d.DependencyType == ArchitectureDependencyType.DatabaseReadWrite);
        var hasMessaging = a.MessagingChannels.Any(ch => ch.Producers.Any(p => p.ComponentId == c.Id) || ch.Consumers.Any(p => p.ComponentId == c.Id));
        var hasAuth = a.Interfaces.Any(i => i.ComponentId == c.Id && !string.IsNullOrWhiteSpace(i.AuthRequirement));
        var hasRetry = c.Technologies.Any(t => t.Contains("Polly", StringComparison.OrdinalIgnoreCase) || t.Contains("Resilience", StringComparison.OrdinalIgnoreCase) || t.Contains("Retry", StringComparison.OrdinalIgnoreCase));
        List<Dev> Where(Func<Dev, bool> f) => tests.Where(f).ToList();
        void Dim(string name, bool applicable, List<Dev> relevant, Func<Dev, bool>? realOnly, string notApplicable, string missing)
        {
            if (!applicable && relevant.Count == 0) { dims.Add(new(name, DimensionStatus.NotAssessed, notApplicable, [])); return; }
            if (relevant.Count == 0) { dims.Add(new(name, DimensionStatus.MissingEvidence, missing, [])); return; }
            var ids = relevant.Select(d => d.Test.TestId).Take(25).ToList();
            var real = realOnly is null ? relevant : relevant.Where(realOnly).ToList();
            if (real.Count == 0) { dims.Add(new(name, DimensionStatus.Partial, $"{relevant.Count} test(s), but only with mocks, fakes or in-memory substitutes.", ids)); return; }
            var strong = real.Count(d => d.Test.Strength == EvidenceStrength.Strong);
            dims.Add(new(name, strong > 0 ? DimensionStatus.Strong : DimensionStatus.Some,
                strong > 0 ? $"{strong} analyzed test(s) with recognised assertions and a passed execution." : $"{real.Count} test(s) found and analyzed; {TestCoverageText.NoExecution}", ids));
        }
        bool RealOr(Dev d, params BoundaryKind[] kinds) => d.Test.Boundaries.Any(b => kinds.Contains(b.Kind) && b.Mode is BoundaryMode.Real or BoundaryMode.RealContainer or BoundaryMode.InProcess);
        Dim("Unit", true, Where(d => d.Test.Level == TestLevel.Unit), null, "", "No unit-level test evidence found.");
        Dim("Integration", hasDb || hasMessaging || hasHttp, Where(d => d.Test.Level is TestLevel.Integration or TestLevel.Repository or TestLevel.Api), d => d.Test.Boundaries.Any(b => b.Mode is BoundaryMode.Real or BoundaryMode.RealContainer or BoundaryMode.InProcess),
            "No integration boundary detected for this component.", "No integration-level test evidence found.");
        Dim("Contract", false, Where(d => d.Test.Level == TestLevel.Contract), null, "No contract tests found; not assessed.", "");
        Dim("API", hasHttp, Where(d => d.Test.Boundaries.Any(b => b.Kind == BoundaryKind.Http && b.Mode == BoundaryMode.InProcess) || d.Test.Assertions.Any(x => x.Kind == TestAssertionKind.HttpStatus)), null,
            "No HTTP/GraphQL interface detected for this component.", "No API-level test evidence found.");
        Dim("Database", hasDb, Where(d => d.Test.Boundaries.Any(b => b.Kind == BoundaryKind.Database)), d => RealOr(d, BoundaryKind.Database), "No database use detected for this component.", "No database test evidence found.");
        Dim("Messaging", hasMessaging, Where(d => d.Test.Boundaries.Any(b => b.Kind is BoundaryKind.EventHub or BoundaryKind.ServiceBus or BoundaryKind.Messaging)),
            d => RealOr(d, BoundaryKind.EventHub, BoundaryKind.ServiceBus), "No messaging channel detected for this component.", "No messaging test evidence found.");
        Dim("Authorization", hasAuth, Where(d => d.Test.Assertions.Any(x => x.Kind == TestAssertionKind.AuthorizationResult) || d.Fact?.BehaviorTokens.Any(t => t.StartsWith("authori", StringComparison.Ordinal) || t is "unauthorized" or "forbidden") == true),
            null, "No authorization requirement detected on this component's interfaces.", "No authorization test evidence found (allow/deny).");
        Dim("Error handling", true, Where(d => d.Test.Assertions.Any(x => x.Kind == TestAssertionKind.ExceptionThrown || x.Kind == TestAssertionKind.HttpStatus && x.Detail.StartsWith('4') || x.Detail.StartsWith('5'))),
            null, "", "No test asserting an error result was found.");
        Dim("Retry", hasRetry, Where(d => d.Fact?.BehaviorTokens.Any(t => t.StartsWith("retr", StringComparison.Ordinal)) == true), null, "No retry mechanism detected by Source Analysis; not assessed.", "Retry code exists, but no retry test was found.");
        Dim("Idempotency", false, Where(d => d.Fact?.BehaviorTokens.Any(t => t.StartsWith("idempot", StringComparison.Ordinal) || t.StartsWith("duplicat", StringComparison.Ordinal)) == true), null,
            "No idempotency/duplicate-handling test found; not assessed.", "");
        var e2e = Where(d => d.Test.Level == TestLevel.E2E);
        var flowCount = flows.Count(f => f.Module.Length > 0 && (c.Name.Contains(f.Module, StringComparison.OrdinalIgnoreCase) || Display(c).Contains(f.Module, StringComparison.OrdinalIgnoreCase)));
        dims.Add(e2e.Count + flowCount > 0
            ? new("E2E", DimensionStatus.Some, $"{e2e.Count} browser test(s) and {flowCount} Critical E2E flow(s) relate to this component.", e2e.Select(d => d.Test.TestId).Take(25).ToList())
            : new("E2E", DimensionStatus.MissingEvidence, "No automated end-to-end evidence found.", []));
        Dim("Performance", false, Where(d => d.Test.Level == TestLevel.Performance), null, "No performance tests found; not assessed.", "");
        Dim("Security", false, Where(d => d.Test.Level == TestLevel.Security), null, "No security tests found; not assessed.", "");
        return new ComponentCoverage
        {
            ComponentId = c.Id, Name = Display(c), Type = c.ComponentType.ToString(), SourceProject = c.SourceProject, Tests = tests.Count, Skipped = tests.Count(d => d.Test.Skipped), Dimensions = dims,
        };
    }

    // ── overlaps ─────────────────────────────────────────────────────────────────────────────────────────────────

    internal static (TestLevel Level, string Basis) IntendedLevel(QaTestCandidate s)
    {
        if (s.IntendedLevel is not TestLevel.Unknown) return (s.IntendedLevel, s.IntendedLevelBasis);
        var text = $"{s.Title} {s.Given} {s.When} {s.Then}".ToLowerInvariant();
        if (Regex.IsMatch(text, @"\b(message|event|queue|topic|event hub|service bus|cdc|kafka)\b")) return (TestLevel.E2E, "Scenario describes a message/event flow (system journey)");
        if (Regex.IsMatch(text, @"\b(click|page|screen|button|form|navigat|sees|displayed|shown)")) return (TestLevel.Ui, "Scenario describes user-interface steps");
        if (Regex.IsMatch(text, @"\b(api|endpoint|request|response|http|status code|[1-5]\d\d)\b")) return (TestLevel.Api, "Scenario describes an API request and response");
        return (TestLevel.Unknown, "The scenario does not say at which level it is tested");
    }

    private static int Rank(TestLevel level) => level switch
    {
        TestLevel.Unit => 1, TestLevel.Component or TestLevel.Repository or TestLevel.Contract => 2, TestLevel.Api or TestLevel.Integration or TestLevel.Ui => 3, TestLevel.E2E or TestLevel.Runtime => 4, _ => 0,
    };

    private static bool OutcomeMatches(QaTestCandidate qa, Dev dev)
    {
        var then = $"{qa.Then} {qa.Title}".ToLowerInvariant();
        var outcomes = dev.Test.Assertions;
        var codes = Regex.Matches(then, @"\b[1-5]\d\d\b").Select(m => m.Value).ToList();
        if (codes.Count > 0) return outcomes.Any(a => a.Kind == TestAssertionKind.HttpStatus && codes.Contains(a.Detail));
        var failure = Regex.IsMatch(then, @"\b(error|reject|denied|deny|invalid|fail|not (be )?(saved|stored|created)|duplicate|unauthori|forbidden|prevent)");
        var success = Regex.IsMatch(then, @"\b(saved|stored|created|returned|returns|succeed|success|accepted|updated|published|sent)\b");
        var devFailure = outcomes.Any(a => a.Kind is TestAssertionKind.ExceptionThrown or TestAssertionKind.AuthorizationResult || a.Kind == TestAssertionKind.HttpStatus && a.Detail is ['4', ..] or ['5', ..]);
        var devSuccess = outcomes.Any(a => a.Kind is TestAssertionKind.RecordPersisted or TestAssertionKind.MessagePublished || a.Kind == TestAssertionKind.HttpStatus && a.Detail is ['2', ..] || a.Detail == "2xx");
        return failure && devFailure || success && devSuccess && !failure;
    }

    private static List<TestOverlap> Overlaps(List<QaTestCandidate> qaTests, List<Dev> devs, List<Journey> journeys, IReadOnlyDictionary<string, string> decisions,
        IReadOnlyDictionary<string, int> ambiguous, CancellationToken ct)
    {
        // Inverted indexes: requirement id → tests, token → tests. Candidate pairs come only from shared postings. Ambiguous ids never link.
        var byRequirement = devs.SelectMany(d => d.Test.RequirementIds.Where(r => !ambiguous.ContainsKey(r)).Select(r => (r, d))).GroupBy(x => x.r, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(x => x.d).ToList(), StringComparer.OrdinalIgnoreCase);
        var byToken = devs.SelectMany(d => d.Tokens.Select(t => (t, d))).GroupBy(x => x.t, StringComparer.Ordinal)
            .Where(g => g.Count() <= 200).ToDictionary(g => g.Key, g => g.Select(x => x.d).ToList(), StringComparer.Ordinal);
        var overlaps = new List<TestOverlap>();
        foreach (var qa in qaTests)
        {
            ct.ThrowIfCancellationRequested();
            var qaTokens = TextTokens(qa.Title, qa.When, qa.Then);
            var qaLevel = IntendedLevel(qa);
            var candidates = qa.RequirementIds.SelectMany(r => byRequirement.GetValueOrDefault(r) ?? [])
                .Concat(qaTokens.SelectMany(t => byToken.GetValueOrDefault(t) ?? [])).Where(d => d.Test.TestId != qa.Id).Distinct().ToList();
            var scored = candidates.Select(d =>
            {
                var sameRequirement = qa.RequirementIds.Where(r => !ambiguous.ContainsKey(r)).Intersect(d.Test.RequirementIds, StringComparer.OrdinalIgnoreCase).Any();
                var shared = qaTokens.Intersect(d.Tokens).Count();
                // Overlap coefficient over normalized tokens: shared / smaller set. At least two shared tokens; a single name match is never enough.
                var jaccard = shared == 0 ? 0 : shared * 1.0 / Math.Max(1, Math.Min(qaTokens.Count, d.Tokens.Count));
                var sameBehavior = shared >= 2 && jaccard >= 0.4 && (sameRequirement || shared >= 3 && jaccard >= 0.5);
                return (d, sameRequirement, sameBehavior, jaccard, outcome: sameBehavior && d.Fact is not null && OutcomeMatches(qa, d));
            }).Where(x => x.sameRequirement || x.sameBehavior).OrderByDescending(x => x.outcome).ThenByDescending(x => x.sameRequirement).ThenByDescending(x => x.jaccard).Take(5).ToList();
            if (scored.Count == 0) continue;
            var best = scored[0];
            var dev = best.d;
            var analyzed = dev.Fact is not null && dev.Test.Assertions.Count > 0;
            var executed = dev.Test.Execution == TestExecutionState.Passed;
            var devRank = Rank(dev.Test.Level);
            var qaRank = Rank(qaLevel.Level);
            var sameLevel = devRank > 0 && qaRank > 0 && devRank >= qaRank;
            var sameBoundary = sameLevel && (qaLevel.Level != TestLevel.Api || dev.Test.Boundaries.Any(b => b.Kind == BoundaryKind.Http && b.Mode is BoundaryMode.InProcess or BoundaryMode.Real));
            OverlapKind kind;
            if (!analyzed) kind = OverlapKind.InsufficientEvidence;
            else if (!best.sameBehavior) kind = OverlapKind.ComplementaryCoverage;
            else if (!best.outcome) kind = OverlapKind.PartialOverlap;
            else if (qaRank == 0) kind = OverlapKind.NeedsTesterReview;
            else if (sameLevel && sameBoundary && (best.sameRequirement || best.jaccard >= 0.5)) kind = OverlapKind.HighConfidenceOverlap;
            else if (devRank < qaRank) kind = OverlapKind.DifferentBoundary;
            else kind = OverlapKind.PartialOverlap;
            var components = dev.Test.Components.ToHashSet(StringComparer.Ordinal);
            var remaining = journeys.SelectMany(j => j.Steps).Where(s => (components.Contains(s.From) || components.Contains(s.To)) && s.Developer != StepCoverage.Covered)
                .Select(s => $"{s.FromName} → {s.ToName} ({Plain(s.Boundary)})").Distinct(StringComparer.Ordinal).Take(4).ToList();
            var same = new List<string>();
            if (best.sameRequirement) same.Add("requirement");
            if (best.sameBehavior) same.Add("behaviour");
            if (best.outcome) same.Add("expected result");
            if (sameLevel) same.Add("test level");
            var different = new List<string>();
            if (!sameLevel) different.Add($"test level ({Readable(dev.Test.Level)} vs {Readable(qaLevel.Level)})");
            if (dev.Test.Boundaries.Any(b => b.Mode is BoundaryMode.Mocked or BoundaryMode.Fake or BoundaryMode.InMemory)) different.Add("environment (developer test uses mocks/in-memory substitutes)");
            if (!executed) different.Add("execution (no proof the developer test ran)");
            var id = $"overlap:{qa.Id}|{dev.Test.TestId}";
            overlaps.Add(new TestOverlap
            {
                Id = id, QaTestId = qa.Id, QaTitle = qa.Title, DeveloperTestIds = scored.Where(x => x.sameBehavior || x.sameRequirement).Select(x => x.d.Test.TestId).ToList(),
                Kind = kind, Dimensions = new(best.sameRequirement, best.sameBehavior, best.outcome, components.Count > 0, sameLevel, sameBoundary, analyzed, executed),
                Strength = kind == OverlapKind.HighConfidenceOverlap ? (executed ? EvidenceStrength.Strong : EvidenceStrength.Some) : analyzed ? EvidenceStrength.Some : EvidenceStrength.Weak,
                Same = same, Different = different,
                RemainingRisk = remaining.Count == 0 ? "No unverified journey step was found for the components this test covers; runtime behaviour is still not verified." : "Not covered by the developer test: " + string.Join("; ", remaining) + ".",
                Recommendation = kind switch
                {
                    OverlapKind.HighConfidenceOverlap => "Probably does not need to be repeated at the same test level. Keep QA effort for the parts of the journey that are not verified.",
                    OverlapKind.DifferentBoundary => "Not a duplicate: the developer test checks this at a lower level. Do not repeat the isolated logic check; keep the QA test for the real system journey.",
                    OverlapKind.PartialOverlap => "Partly overlaps: similar behaviour, but a different expected result or level. Review whether both cases are needed.",
                    OverlapKind.ComplementaryCoverage => "Same requirement, different behaviour. Both tests add coverage.",
                    OverlapKind.NeedsTesterReview => "Possible overlap. BirkNext cannot tell at which level the QA test runs — tester review needed.",
                    _ => "Not enough evidence to compare (name similarity only, or the developer test was not analyzed).",
                },
                Reasons = [.. same.Select(s => "same " + s), $"developer test: {dev.Test.Name} ({Readable(dev.Test.Level)})", analyzed ? "developer test source analyzed" : "developer test not analyzed",
                    executed ? "developer test execution passed" : "no execution evidence for the developer test", $"QA test level: {Readable(qaLevel.Level)} ({qaLevel.Basis})"],
                ReviewerDecision = decisions.GetValueOrDefault(id),
            });
        }
        return overlaps;
    }

    // ── behaviours, gaps, scope ──────────────────────────────────────────────────────────────────────────────────

    private static List<BehaviorCoverage> Behaviors(CoverageWorkspaceEvidence workspace, List<Dev> devs, List<QaTestCandidate> qa, List<Journey> journeys, HashSet<string> changedComponents,
        IReadOnlyDictionary<string, int> ambiguous)
    {
        var behaviors = new List<BehaviorCoverage>();
        var unverifiedSteps = journeys.SelectMany(j => j.Steps).Where(s => s.Developer != StepCoverage.Covered).ToList();
        BehaviorCoverage Build(string id, string title, List<string> requirements, List<Dev> tests, int qaTests)
        {
            var components = tests.SelectMany(t => t.Test.Components).Distinct(StringComparer.Ordinal).ToList();
            var notVerified = unverifiedSteps.Where(s => components.Contains(s.From) || components.Contains(s.To)).Select(Focus).Distinct(StringComparer.Ordinal).Take(4).ToList();
            if (tests.Count > 0) notVerified.Add("Behaviour in a deployed environment (no runtime evidence)");
            var analyzed = tests.Where(t => t.Fact is not null).ToList();
            var mockOnly = analyzed.Count > 0 && analyzed.All(t => t.Test.Boundaries.Count > 0 && t.Test.Boundaries.All(b => b.Mode is BoundaryMode.Mocked or BoundaryMode.Fake));
            var maxRank = tests.Count == 0 ? 0 : tests.Max(t => Rank(t.Test.Level));
            var strong = tests.Any(t => t.Test.Strength == EvidenceStrength.Strong && Rank(t.Test.Level) >= 3);
            var kind = tests.Count == 0 ? CoverageRecommendationKind.NoTestEvidence
                : components.Any(changedComponents.Contains) ? CoverageRecommendationKind.ChangedSinceTest
                : analyzed.Count == 0 ? CoverageRecommendationKind.NeedsHumanReview
                : tests.Any(t => t.Test.Level == TestLevel.E2E) ? CoverageRecommendationKind.CoveredByAutomatedE2E
                : mockOnly ? CoverageRecommendationKind.CoveredByMocksOnly
                : strong && notVerified.Count <= 1 ? CoverageRecommendationKind.StronglyCoveredSameLevel
                : maxRank <= 2 && notVerified.Count > 1 ? CoverageRecommendationKind.CoveredAtLowerLevelOnly
                : notVerified.Count > 1 ? CoverageRecommendationKind.RemainingIntegrationRisk
                : CoverageRecommendationKind.PartlyCovered;
            return new BehaviorCoverage
            {
                Id = id, Title = title, RequirementIds = requirements, Components = components, Recommendation = kind, Status = StatusText(kind),
                AlreadyTested = analyzed.Select(t => t.Test.Purpose).Distinct(StringComparer.Ordinal).Take(5).ToList(),
                WhereTested = tests.Select(t => $"{Readable(t.Test.Level)} tests in {t.Test.Project}").Distinct(StringComparer.Ordinal).Take(5).ToList(),
                NotVerifiedYet = notVerified, ForTesters = ForTesters(kind, notVerified), DeveloperTestIds = tests.Select(t => t.Test.TestId).Take(30).ToList(), QaTests = qaTests,
                Strength = tests.Count == 0 ? EvidenceStrength.NotEnough : strong ? EvidenceStrength.Strong : analyzed.Count > 0 ? EvidenceStrength.Some : EvidenceStrength.Weak,
                Why = [$"{tests.Count} test(s) linked ({analyzed.Count} analyzed, {tests.Count(t => t.Test.Execution == TestExecutionState.Passed)} with a passed execution)",
                    requirements.Count > 0 ? "linked through explicit requirement identifiers in test source" : "grouped by test class (behaviour under test)"],
            };
        }
        foreach (var req in workspace.Requirements.Take(500))
        {
            var qaCount = qa.Count(q => q.RequirementIds.Contains(req.Id, StringComparer.OrdinalIgnoreCase));
            var title = $"{req.Id} — {Shorten(CleanRequirement(req.Id, req.Text), 90)}";
            if (ambiguous.TryGetValue(req.Id, out var modules))
            {
                behaviors.Add(new BehaviorCoverage
                {
                    Id = "req:" + req.Id, Title = title, RequirementIds = [req.Id], Recommendation = CoverageRecommendationKind.NeedsHumanReview,
                    Status = StatusText(CoverageRecommendationKind.NeedsHumanReview), QaTests = qaCount, Strength = EvidenceStrength.NotEnough,
                    ForTesters = $"Tests in {modules} different modules name {req.Id}. BirkNext cannot tell which of them belong to this requirement; review the link before relying on it.",
                    Why = [$"{req.Id} is referenced by tests in {modules} separate modules; the link is ambiguous and was not used"],
                });
                continue;
            }
            var tests = devs.Where(d => d.Test.RequirementIds.Contains(req.Id, StringComparer.OrdinalIgnoreCase)).ToList();
            behaviors.Add(Build("req:" + req.Id, title, [req.Id], tests, qaCount));
        }
        foreach (var group in devs.Where(d => d.Fact is not null).GroupBy(d => (d.Test.Project, d.Fact!.ClassName)).OrderBy(g => g.Key.Project, StringComparer.Ordinal).ThenBy(g => g.Key.ClassName, StringComparer.Ordinal).Take(400))
        {
            var title = Humanize(group.Key.ClassName);
            behaviors.Add(Build($"class:{group.Key.Project}:{group.Key.ClassName}", $"{title} ({group.Key.Project})", group.SelectMany(d => d.Test.RequirementIds).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), group.ToList(), 0));
        }
        return behaviors;
    }

    internal static string StatusText(CoverageRecommendationKind kind) => kind switch
    {
        CoverageRecommendationKind.StronglyCoveredSameLevel => "Covered",
        CoverageRecommendationKind.CoveredByAutomatedE2E => "Covered",
        CoverageRecommendationKind.CoveredAtLowerLevelOnly or CoverageRecommendationKind.CoveredByMocksOnly or CoverageRecommendationKind.PartlyCovered or CoverageRecommendationKind.RemainingIntegrationRisk => "Partly covered",
        CoverageRecommendationKind.NoTestEvidence => "Not verified",
        _ => "Needs tester review",
    };

    private static string ForTesters(CoverageRecommendationKind kind, List<string> notVerified) => kind switch
    {
        CoverageRecommendationKind.StronglyCoveredSameLevel => "No additional same-level QA test appears necessary based on current evidence. Check the behaviour once as part of a system journey.",
        CoverageRecommendationKind.CoveredByAutomatedE2E => "Automated end-to-end tests exist. Review them before adding a manual test for the same flow.",
        CoverageRecommendationKind.CoveredAtLowerLevelOnly => "Do not repeat the developer's isolated checks. Keep one system test through the real flow" + (notVerified.Count > 0 ? $": {notVerified[0]}." : "."),
        CoverageRecommendationKind.CoveredByMocksOnly => "The developer tests replace the real dependencies with mocks. Test the real integration once.",
        CoverageRecommendationKind.RemainingIntegrationRisk => "The logic is tested, but parts of the journey are not. Focus on: " + string.Join("; ", notVerified.Take(3)) + ".",
        CoverageRecommendationKind.PartlyCovered => "Some test evidence exists. Review which cases are covered before testing.",
        CoverageRecommendationKind.ChangedSinceTest => "The implementation changed, but no related test change was found. Review and re-test the changed behaviour.",
        CoverageRecommendationKind.NoTestEvidence => "No test evidence was found. This needs a test (developer or QA).",
        _ => "BirkNext could not analyze these tests. Review them manually.",
    };

    private static HashSet<string> ChangedProjects(IqrSourceSnapshot current, IqrSourceSnapshot? baseline, List<ArchitectureComponent> components, List<Dev> devs, out string? note)
    {
        note = null;
        var changed = new HashSet<string>(StringComparer.Ordinal);
        if (baseline is null) return changed;
        if (current.TargetIndex is null || baseline.TargetIndex is null) { note = "One of the snapshots has no file index: change-aware test review was not assessed."; return changed; }
        var before = baseline.TargetIndex.Files.ToDictionary(f => f.RelativePath, f => f.ContentFingerprint, StringComparer.Ordinal);
        var changedFiles = current.TargetIndex.Files.Where(f => !before.TryGetValue(f.RelativePath, out var fp) || fp != f.ContentFingerprint || fp is null).Select(f => f.RelativePath).ToList();
        foreach (var component in components)
        {
            var dir = Dir(component.SourceProject);
            if (dir.Length == 0) continue;
            var production = changedFiles.Any(f => f.StartsWith(dir + "/", StringComparison.Ordinal) && f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase));
            if (!production) continue;
            var relatedTestDirs = devs.Where(d => d.Test.Components.Contains(component.Id)).Select(d => Dir(d.Test.FilePath)).Distinct(StringComparer.Ordinal).ToList();
            var testsChanged = changedFiles.Any(f => relatedTestDirs.Any(t => f.StartsWith(t + "/", StringComparison.Ordinal) || Dir(f) == t));
            if (!testsChanged) changed.Add(component.Id);
        }
        return changed;
    }

    private static List<CoverageGap> Gaps(IqrSourceSnapshot current, IqrSourceSnapshot? baseline, CoverageWorkspaceEvidence workspace, List<ArchitectureComponent> components,
        List<ComponentCoverage> coverage, Dictionary<string, List<Dev>> testsByComponent, List<Journey> journeys, List<Dev> devs, HashSet<string> changed, TestBehaviorSourceEvidence? facts)
    {
        var gaps = new List<CoverageGap>();
        void Add(GapKind kind, string key, string title, string explanation, string attention, List<string> subjects, List<string> evidence) =>
            gaps.Add(new CoverageGap { Id = $"gap:{kind}:{key}", Kind = kind, Title = title, Explanation = explanation, Attention = attention, Subjects = subjects, Evidence = evidence });
        foreach (var c in coverage)
        {
            var tests = testsByComponent.GetValueOrDefault(c.ComponentId) ?? [];
            if (tests.Count == 0) { Add(GapKind.NoTestEvidence, c.ComponentId, $"No test evidence found for {c.Name}", "No test in the analyzed source maps to this component (by project reference or tested type). This does not prove that no test exists.", "Needs attention", [c.ComponentId], [c.SourceProject]); continue; }
            var integrationDims = c.Dimensions.Where(d => d.Dimension is "Integration" or "Database" or "Messaging" or "API").ToList();
            if (tests.All(t => t.Test.Level is TestLevel.Unit) && integrationDims.Any(d => d.Status == DimensionStatus.MissingEvidence))
                Add(GapKind.UnitOnly, c.ComponentId, $"Only unit-level tests found for {c.Name}", "The component has integration boundaries (API, database or messaging), but the tests found replace them or do not reach them.", "Needs attention", [c.ComponentId], tests.Take(5).Select(t => t.Test.Name).ToList());
            if (c.Dimensions.FirstOrDefault(d => d.Dimension == "Messaging") is { Status: DimensionStatus.Partial } messaging)
                Add(GapKind.MockOnly, c.ComponentId, $"Messaging in {c.Name} is tested with mocks or in-memory substitutes only", "Tests touch the message broker only through mocks, fakes or in-process tracking. The real broker path is not verified.", "Needs attention", [c.ComponentId], messaging.TestIds.Take(5).ToList());
            if (c.Skipped > 0) Add(GapKind.SkippedTests, c.ComponentId, $"{c.Skipped} skipped test(s) for {c.Name}", "These tests are declared skipped in source and do not run.", "Review recommended", [c.ComponentId], tests.Where(t => t.Test.Skipped).Take(5).Select(t => t.Test.Name).ToList());
            if (c.Dimensions.FirstOrDefault(d => d.Dimension == "Authorization") is { Status: DimensionStatus.MissingEvidence } && c.Dimensions.Any(d => d.Dimension == "API" && d.Status is DimensionStatus.Strong or DimensionStatus.Some))
                Add(GapKind.AuthorizationHappyPathOnly, c.ComponentId, $"Authorization for {c.Name} has no allow/deny test evidence", "API tests exist, but none asserts a 401/403 or another authorization result. Only the happy path appears to be tested.", "Needs attention", [c.ComponentId], []);
            if (c.Dimensions.FirstOrDefault(d => d.Dimension == "Retry") is { Status: DimensionStatus.MissingEvidence })
                Add(GapKind.RetryNotTested, c.ComponentId, $"Retry behaviour in {c.Name} is not tested", "Source Analysis found a retry/resilience mechanism, but no test named for retry behaviour was found.", "Review recommended", [c.ComponentId], []);
            if (changed.Contains(c.ComponentId))
                Add(GapKind.ChangedWithoutTestChange, c.ComponentId, $"Implementation changed in {c.Name}, but no related test change was found", "Source files of this component differ from the baseline snapshot while the test files mapped to it did not change. This does not mean the change lacks test coverage.", "Needs attention", [c.ComponentId], []);
        }
        if (baseline?.Architecture is { } before)
            foreach (var c in components.Where(c => before.Components.All(b => b.SourceProject != c.SourceProject && b.Name != c.Name) && (testsByComponent.GetValueOrDefault(c.Id)?.Count ?? 0) == 0))
                Add(GapKind.NewComponentWithoutTests, c.Id, $"New component {Display(c)} has no test evidence", "The component is new since the baseline snapshot and no test maps to it.", "Needs attention", [c.Id], []);
        foreach (var step in journeys.SelectMany(j => j.Steps).Where(s => s.Developer is StepCoverage.NotVerified or StepCoverage.MockOnly).DistinctBy(s => s.Id).Take(60))
            Add(GapKind.JourneyStepNotVerified, step.Id, $"{step.FromName} → {step.ToName}: {(step.Developer == StepCoverage.MockOnly ? "mocked only" : "not verified")}", step.DeveloperExplanation,
                step.Evidence is ConnectionEvidence.Confirmed or ConnectionEvidence.StronglySupported ? "Needs attention" : "Review recommended", [step.From, step.To], step.Provenance.Take(2).ToList());
        var referenced = devs.SelectMany(d => d.Test.RequirementIds).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var req in workspace.Requirements.Where(r => !referenced.Contains(r.Id)).Take(100))
            Add(GapKind.RequirementWithoutTests, req.Id, $"No test evidence found for {req.Id}", "No automated test in the analyzed source names this requirement. Tests may still cover it without naming it — review recommended.", "Review recommended", [req.Id], []);
        foreach (var u in facts?.Unsupported ?? [])
            Add(GapKind.UnsupportedTests, u.Language + u.Framework, $"{u.Files} {u.Language} test file(s) ({u.Framework}) were not analyzed", u.Reason, "Review recommended", [], []);
        return gaps;
    }

    private static RemainingQaScope Scope(List<Journey> journeys, List<TestOverlap> overlaps, List<BehaviorCoverage> behaviors, List<CoverageGap> gaps, List<Dev> devs)
    {
        var keep = journeys.Where(j => j.Steps.Any(s => s.Developer != StepCoverage.Covered)).Take(8)
            .Select(j => $"One system test through {j.Title}: " + string.Join("; ", j.FocusForTesters.Take(3)))
            .Concat(gaps.Where(g => g.Kind is GapKind.AuthorizationHappyPathOnly or GapKind.ChangedWithoutTestChange or GapKind.MockOnly).Take(8).Select(g => g.Title))
            .Distinct(StringComparer.Ordinal).ToList();
        var unnecessary = overlaps.Where(o => o.Kind == OverlapKind.HighConfidenceOverlap && o.ReviewerDecision != "Rejected")
            .Select(o => $"Repeating \"{o.QaTitle}\" at the same level — developer automation already checks it")
            // Requirement-id links alone never justify "do not repeat": only behaviours grouped from analyzed test source qualify, and the
            // wording says when there is no proof the tests ran.
            .Concat(behaviors.Where(b => !b.Id.StartsWith("req:", StringComparison.Ordinal) && b.Recommendation == CoverageRecommendationKind.StronglyCoveredSameLevel).Take(10)
                .Select(b => $"Isolated re-testing of {b.Title}"))
            .Concat(behaviors.Where(b => !b.Id.StartsWith("req:", StringComparison.Ordinal) && b.Recommendation == CoverageRecommendationKind.CoveredAtLowerLevelOnly
                    && b.Strength is EvidenceStrength.Strong or EvidenceStrength.Some && b.AlreadyTested.Count > 0)
                .Take(10).Select(b => $"Repeating the isolated logic of {b.Title} (keep one journey test instead){(b.Strength == EvidenceStrength.Strong ? "" : " — based on test source; execution not verified")}"))
            .Distinct(StringComparer.Ordinal).Take(25).ToList();
        var review = overlaps.Where(o => o.Kind is OverlapKind.NeedsTesterReview or OverlapKind.InsufficientEvidence).Select(o => $"Possible overlap for \"{o.QaTitle}\" — {o.Recommendation}")
            .Concat(journeys.SelectMany(j => j.Steps).Where(s => s.Evidence is ConnectionEvidence.Suggested or ConnectionEvidence.NeedsConfirmation && s.ReviewerDecision is null)
                .DistinctBy(s => s.Id).Take(10).Select(s => $"Confirm the connection {s.FromName} → {s.ToName} ({s.Evidence})"))
            .Concat(devs.Count(d => d.Test.Level is TestLevel.NeedsReview or TestLevel.Unknown) is var n and > 0 ? [$"{n} test(s) whose level could not be determined from setup evidence"] : [])
            .Distinct(StringComparer.Ordinal).Take(25).ToList();
        return new RemainingQaScope(keep, unnecessary, review);
    }

    private static CoverageInventory InventoryOf(SourceTestInventory? inventory, TestBehaviorSourceEvidence? facts, List<CoverageTest> tests, IqrSourceSnapshot snapshot)
    {
        var coverageFiles = (snapshot.TargetIndex?.Files ?? []).Select(f => f.RelativePath).Where(p => Regex.IsMatch(p, @"(cobertura|coverage)\.xml$|lcov\.info$|\.coverage$", RegexOptions.IgnoreCase)).Take(3).ToList();
        var executed = tests.Count(t => t.Execution is not TestExecutionState.NotVerified);
        return new CoverageInventory
        {
            TestProjects = inventory?.Projects.Count ?? facts?.ProjectReferences.Count ?? 0,
            TestsDiscovered = tests.Count, TestsAnalyzed = tests.Count(t => t.Analyzed), TestsWithExecutionEvidence = executed,
            Passed = tests.Count(t => t.Execution == TestExecutionState.Passed), Failed = tests.Count(t => t.Execution == TestExecutionState.Failed),
            SkippedDeclared = tests.Count(t => t.Skipped),
            Frameworks = tests.Select(t => t.Framework).Concat(inventory?.Projects.Select(p => p.Framework ?? "") ?? []).Where(f => f.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
            ByLevel = tests.GroupBy(t => t.Level).ToDictionary(g => g.Key.ToString(), g => g.Count()),
            Unsupported = facts?.Unsupported ?? [],
            ExecutionEvidence = executed == 0 ? TestCoverageText.NoExecution : $"{executed} test(s) have imported execution results; the rest are not verified as executed.",
            CodeCoverage = coverageFiles.Count == 0 ? TestCoverageText.NoPercentage
                : $"Coverage file(s) found in the source ({string.Join(", ", coverageFiles)}), but BirkNext does not import coverage reports; no coverage percentage is shown.",
        };
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────────────────────

    internal static string Readable(TestLevel level) => level switch
    {
        TestLevel.Unit => "unit", TestLevel.Component => "component", TestLevel.Repository => "database/repository", TestLevel.Contract => "contract", TestLevel.Integration => "integration",
        TestLevel.Api => "API", TestLevel.Ui => "UI component", TestLevel.E2E => "end-to-end", TestLevel.Security => "security", TestLevel.Performance => "performance",
        TestLevel.Runtime => "runtime", TestLevel.NeedsReview => "needs review", _ => "unknown",
    };

    /// <summary>The project name when known (unique and recognisable), otherwise the architecture's logical name.</summary>
    internal static string Display(ArchitectureComponent c) => ProjectName(c.SourceProject) is { Length: > 0 } p ? p : c.Name;
    private static string Normal(string text) => new(text.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    private static CoverageSnapshotRef Ref(IqrSourceSnapshot s) =>
        new(s.Id, ReviewSourceEvidenceProvider.Identity(s).DisplayName, s.Archive.FileName, s.Archive.Sha256, s.AnalyzedAt);
    private static string ProjectName(string path) => path.Length == 0 ? "" : Path.GetFileNameWithoutExtension(path.Replace('\\', '/').Split('/').Last());
    private static string Dir(string path) { var p = path.Replace('\\', '/'); var i = p.LastIndexOf('/'); return i < 0 ? "" : p[..i]; }
    private static string Module(string path) { var p = path.Replace('\\', '/').TrimStart('/'); var i = p.IndexOf('/'); return i < 0 ? "" : p[..i]; }
    /// <summary>Requirement text without Markdown list/emphasis markers or a repeated leading id.</summary>
    internal static string CleanRequirement(string id, string text)
    {
        var t = Regex.Replace(text, @"^\s*[-*+]\s*", "").Replace("**", "").Replace("__", "").Trim();
        if (t.StartsWith(id, StringComparison.OrdinalIgnoreCase)) t = t[id.Length..].TrimStart(':', ' ', '-', '—', '.').Trim();
        return t.Length == 0 ? text : t;
    }
    private static string Shorten(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
    private static string Humanize(string className) =>
        string.Join(" ", SourceAnalysis.TestBehavior.TestBehaviorSourceAnalyzer.Tokens(className.EndsWith("Tests", StringComparison.Ordinal) ? className[..^5] : className.EndsWith("Test", StringComparison.Ordinal) ? className[..^4] : className)) is { Length: > 0 } h
            ? char.ToUpperInvariant(h[0]) + h[1..] : className;
}
