using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace BirkNext.TestEvidence;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// Shared Test Evidence: one canonical test identity (repository + project + fully-qualified name, the discovery provider's StableIdentity)
// that every consumer — Test Coverage & Overlap, Source Analysis, Pipeline Review, traceability, Impact Analysis, IQR and the quality
// reviews — uses to talk about the same test. States never collapse: Discovered ≠ Mapped ≠ Configured in CI ≠ Selected ≠ Executed ≠
// Passed ≠ verified ≠ accepted. A name similarity never confirms a mapping or a duplicate; an execution is always tied to its context.
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TestMappingConfidence { Explicit, Strong, Candidate, Inferred, Ambiguous, Unmapped }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TestLayer { Unit, Integration, Contract, Component, EndToEnd, Unknown }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TestOverlapClass { Complementary, PotentialRedundancy, NotComparable }

/// <summary>A requirement (or acceptance criterion) link with its confidence and basis. Candidate/Inferred links never count as coverage.</summary>
public sealed record TestRequirementLink(string RequirementId, TestMappingConfidence Confidence, string Basis);

/// <summary>One canonical test: identity, where it is declared, its layer, its explicit links. Source existence only — never a result.</summary>
public sealed record CanonicalTest(string Id, string DefinitionId, string Project, string Framework, string FullyQualifiedName, string ClassName, string MethodName,
    TestLayer Layer, string File, int Line, bool SkipDeclared, string Parameters, IReadOnlyList<TestRequirementLink> Requirements, Guid SnapshotId, string State = "Discovered in source");

/// <summary>One execution of a test in an explicit context. Immutable; an execution from another environment never answers for this one.</summary>
public sealed record TestExecutionRecord(string TestKey, string TestName, string Result, DateTimeOffset? ExecutedAt, string Context, string? Environment, Guid? SourceSnapshotId,
    string? Commit, string Provider)
{
    /// <summary>An expected-rejection (negative) test passes when the rejection was observed: its "Passed" means the system said no.</summary>
    public bool Negative { get; init; }
}

public sealed record ExecutionMatch(CanonicalTest? Test, TestExecutionRecord Execution, string Basis);

public sealed record TestPairOverlap(CanonicalTest A, CanonicalTest B, TestOverlapClass Class, string Reason);

public sealed record ImpactedTest(CanonicalTest Test, string Confidence, string Reason);

/// <summary>Coverage dimensions kept apart (never one percentage).</summary>
public sealed record TestCoverageDimensions(int Discovered, int UnsupportedProjects, int RequirementLinkedExplicit, int RequirementCandidateOnly, int ConfiguredInCi,
    int ExecutedInContext, int PassedInContext, int ExecutionOnly, string Context);

public static class TestEvidenceQueries
{
    public const string NotExecuted = "Not executed in this context";

    public static TestMappingConfidence Confidence(TestEvidenceConfidence c) => c switch
    {
        TestEvidenceConfidence.Confirmed => TestMappingConfidence.Explicit,
        TestEvidenceConfidence.StronglySupported => TestMappingConfidence.Strong,
        TestEvidenceConfidence.Inferred => TestMappingConfidence.Candidate,
        _ => TestMappingConfidence.Unmapped,
    };

    public static TestLayer Layer(TestKind kind) => kind switch
    {
        TestKind.Unit => TestLayer.Unit,
        TestKind.Integration => TestLayer.Integration,
        TestKind.Contract => TestLayer.Contract,
        TestKind.FrontendComponent => TestLayer.Component,
        _ => TestLayer.Unknown,
    };

    /// <summary>Canonical tests from a snapshot's discovered inventory (parsed once at Source Analysis upload; never re-read here).</summary>
    public static IReadOnlyList<CanonicalTest> FromInventory(SourceTestInventory? inventory) =>
        inventory is null ? [] : [.. inventory.Definitions.Select(d => new CanonicalTest(d.StableIdentity, d.TestDefinitionId, d.Project, d.Framework, d.FullyQualifiedName, d.ClassName,
            d.MethodName, Layer(d.Kind), d.FilePath, d.Line, d.SkipDeclared,
            d.HasDynamicData ? "Parameterized (rows known only at execution)" : d.InlineDataRows > 0 ? $"{d.InlineDataRows} declared data row(s)" : "Not parameterized",
            [.. d.References.Select(r => new TestRequirementLink(r.Id, Confidence(r.Confidence), r.Basis))], inventory.SnapshotId))];

    /// <summary>Unsupported frameworks are a coverage limitation, never "zero tests".</summary>
    public static IReadOnlyList<string> Limitations(SourceTestInventory? inventory) =>
        inventory is null ? ["This snapshot predates source test discovery: tests are not assessed (not zero)."]
        : [.. inventory.Projects.Where(p => !p.DiscoverySupported).Select(p => $"{p.Name}: {p.Framework ?? "unknown"} tests are not discovered from source — not zero tests."), .. inventory.Limitations];

    /// <summary>Matches executions to canonical tests by identity (stable identity, definition id, fully-qualified name, class + method). An
    /// execution that matches nothing is kept as execution-only — never discarded.</summary>
    public static IReadOnlyList<ExecutionMatch> Match(IEnumerable<TestExecutionRecord> executions, IReadOnlyList<CanonicalTest> tests)
    {
        var byKey = new Dictionary<string, CanonicalTest>(StringComparer.Ordinal);
        foreach (var t in tests) { byKey.TryAdd(t.Id, t); byKey.TryAdd(t.DefinitionId, t); byKey.TryAdd($"fqn:{t.FullyQualifiedName}", t); }
        var byClassMethod = tests.GroupBy(t => $"{t.ClassName}.{t.MethodName}", StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        return [.. executions.Select(e =>
        {
            if (byKey.TryGetValue(e.TestKey, out var t)) return new ExecutionMatch(t, e, "Same test identity");
            if (byKey.TryGetValue($"fqn:{e.TestName}", out var f)) return new ExecutionMatch(f, e, "Same fully-qualified name");
            var tail = Regex.Replace(e.TestName, @"\(.*\)$", "");
            return byClassMethod.TryGetValue(tail, out var list) && list.Count == 1 ? new ExecutionMatch(list[0], e, "Same class and method")
                : new ExecutionMatch(null, e, list is { Count: > 1 } ? "Ambiguous: several tests share the name" : "Execution-only: no discovered test matches");
        })];
    }

    /// <summary>The most recent execution of one test in exactly this context (environment or run kind). Local runs never answer for QA.</summary>
    public static TestExecutionRecord? LatestRelevant(IEnumerable<ExecutionMatch> matches, string testId, string context) =>
        matches.Where(m => m.Test?.Id == testId && string.Equals(m.Execution.Environment ?? m.Execution.Context, context, StringComparison.OrdinalIgnoreCase))
            .Select(m => m.Execution).OrderByDescending(e => e.ExecutedAt ?? DateTimeOffset.MinValue).FirstOrDefault();

    public static string ResultText(TestExecutionRecord? e) => e is null ? NotExecuted : e.Negative && e.Result == "Passed" ? "Passed (expected rejection observed)" : e.Result;

    public static TestCoverageDimensions Coverage(SourceTestInventory? inventory, IReadOnlyList<CanonicalTest> tests, IReadOnlySet<string> configuredProjects,
        IReadOnlyList<ExecutionMatch> matches, string context)
    {
        var inContext = tests.Select(t => LatestRelevant(matches, t.Id, context)).OfType<TestExecutionRecord>().ToList();
        return new(tests.Count, inventory?.Projects.Count(p => !p.DiscoverySupported) ?? 0,
            tests.Count(t => t.Requirements.Any(r => r.Confidence is TestMappingConfidence.Explicit or TestMappingConfidence.Strong)),
            tests.Count(t => t.Requirements.Count > 0 && t.Requirements.All(r => r.Confidence is TestMappingConfidence.Candidate or TestMappingConfidence.Inferred)),
            tests.Count(t => configuredProjects.Contains(t.Project)), inContext.Count, inContext.Count(e => e.Result == "Passed"),
            matches.Count(m => m.Test is null), context);
    }

    /// <summary>Layered tests of the same requirement are complementary; potential redundancy needs the same layer AND the same explicit
    /// requirement AND near-identical names. A name similarity alone is never a duplicate.</summary>
    public static TestPairOverlap Overlap(CanonicalTest a, CanonicalTest b)
    {
        var shared = a.Requirements.Where(r => r.Confidence is TestMappingConfidence.Explicit or TestMappingConfidence.Strong).Select(r => r.RequirementId)
            .Intersect(b.Requirements.Where(r => r.Confidence is TestMappingConfidence.Explicit or TestMappingConfidence.Strong).Select(r => r.RequirementId), StringComparer.OrdinalIgnoreCase).ToList();
        if (shared.Count == 0) return new(a, b, TestOverlapClass.NotComparable, "No shared explicit requirement — names alone are not compared.");
        if (a.Layer != b.Layer) return new(a, b, TestOverlapClass.Complementary, $"Both verify {string.Join(", ", shared)} at different layers ({a.Layer}, {b.Layer}) — layered coverage, not redundancy.");
        static HashSet<string> Tokens(string s) => [.. Regex.Split(Regex.Replace(s, "([a-z])([A-Z])", "$1 $2"), @"[^A-Za-z0-9]+").Where(t => t.Length > 2).Select(t => t.ToLowerInvariant())];
        var ta = Tokens(a.MethodName); var tb = Tokens(b.MethodName);
        var similarity = ta.Count == 0 || tb.Count == 0 ? 0 : ta.Intersect(tb).Count() / (double)Math.Min(ta.Count, tb.Count);
        return similarity >= 0.75
            ? new(a, b, TestOverlapClass.PotentialRedundancy, $"Same layer ({a.Layer}), same explicit requirement ({string.Join(", ", shared)}) and near-identical names — review for redundancy.")
            : new(a, b, TestOverlapClass.Complementary, $"Same requirement and layer, different behaviour names — likely complementary cases.");
    }

    /// <summary>Tests a change may affect: by component (test project naming the component — candidate), by contract (test code referencing the
    /// contract file — direct) and by dependency (test project declaring the package — strong). Never a failed test.</summary>
    public static IReadOnlyList<ImpactedTest> Impacted(IReadOnlyList<CanonicalTest> tests, IEnumerable<string> changedComponents, IEnumerable<string> contractTestFiles,
        IEnumerable<string> projectsDeclaringChangedPackages)
    {
        var result = new List<ImpactedTest>();
        var files = contractTestFiles.ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Project names, not paths: "Billing.UnitTests" has no extension to strip; only a ".csproj" path does.
        static string Name(string p) { var file = System.IO.Path.GetFileName(p.Replace('\\', '/')); return file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ? file[..^7] : file; }
        var packageProjects = projectsDeclaringChangedPackages.Select(Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var components = changedComponents.Where(c => c.Length > 2).ToList();
        foreach (var t in tests)
        {
            if (files.Contains(t.File)) result.Add(new(t, "Direct", "Test code references the changed contract."));
            else if (packageProjects.Contains(Name(t.Project))) result.Add(new(t, "Strong", "Its test project declares the changed package."));
            else if (components.FirstOrDefault(c => Name(t.Project).StartsWith(c + ".", StringComparison.OrdinalIgnoreCase)) is { } c)
                result.Add(new(t, "Candidate", $"Its test project is named after the changed component {c} — candidate, confirm the mapping."));
        }
        return result;
    }

    /// <summary>Tests still in source whose project is no longer run by any pipeline (configured before, not after).</summary>
    public static IReadOnlyList<CanonicalTest> NoLongerConfigured(IReadOnlyList<CanonicalTest> tests, IReadOnlySet<string> configuredBefore, IReadOnlySet<string> configuredAfter) =>
        [.. tests.Where(t => configuredBefore.Contains(t.Project) && !configuredAfter.Contains(t.Project))];

    /// <summary>Tests whose requirement link is explicit/strong for a requirement — the canonical relationship traceability reads.</summary>
    public static IReadOnlyList<CanonicalTest> ForRequirement(IReadOnlyList<CanonicalTest> tests, string requirementId, bool includeCandidates = false) =>
        [.. tests.Where(t => t.Requirements.Any(r => string.Equals(r.RequirementId, requirementId, StringComparison.OrdinalIgnoreCase)
            && (includeCandidates || r.Confidence is TestMappingConfidence.Explicit or TestMappingConfidence.Strong)))];
}
