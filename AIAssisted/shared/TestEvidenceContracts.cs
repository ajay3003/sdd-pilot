using System.Text.Json.Serialization;

namespace BirkNext.TestEvidence;

// Test evidence has two separate questions, answered by two separate provider kinds:
//   source test discovery  — "what tests exist in source?"       (Source Analysis owns it; result lives on the source snapshot)
//   execution result import — "what actually ran and what happened?" (a result artifact such as TRX; never parses source)
// Test source exists ≠ executed; executed ≠ passed; a passed linked test ≠ requirement verified.

public static class TestEvidenceProviderIds
{
    /// <summary>Source test discovery for .NET projects using xUnit (v2/v3) — [Fact]/[Theory] from C# syntax.</summary>
    public const string DotNetXunitDiscovery = "test.discovery.dotnet.xunit";
    /// <summary>Execution results from Visual Studio TRX files (VSTest logger or Microsoft.Testing.Platform TrxReport), whatever framework produced them.</summary>
    public const string Trx = "test.execution.trx";
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TestEvidenceConfidence { Confirmed, StronglySupported, Inferred, Unresolved }

/// <summary>Conservative classification. Project metadata or a declared category makes it explicit; a project-name token makes it StronglySupported.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TestKind { Unit, Integration, Contract, FrontendComponent, Unknown }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TestDefinitionKind { Fact, Theory }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TestReferenceKind { Requirement, AcceptanceCriterion }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SourceTestDiscoveryStatus { Complete, Partial, NoTestProjects, Unsupported }

// ── Source test discovery (Source Analysis) ─────────────────────────────────────────────────────────────────────────────

/// <summary>Tests found in one source snapshot. Discovery only: nothing here says a test ran.</summary>
public sealed record SourceTestInventory
{
    public string ProviderId { get; init; } = TestEvidenceProviderIds.DotNetXunitDiscovery;
    public int ProviderVersion { get; init; } = 1;
    public Guid SnapshotId { get; init; }
    public string SnapshotFingerprint { get; init; } = "";
    /// <summary>Repository the snapshot came from (multi-repository workspaces keep tests apart by it).</summary>
    public string? RepositoryName { get; init; }
    public SourceTestDiscoveryStatus Status { get; init; }
    public List<SourceTestProject> Projects { get; init; } = [];
    public List<SourceTestDefinition> Definitions { get; init; } = [];
    /// <summary>Where the source declares TRX generation or publication (package, test command, publish task). Configuration evidence: a pipeline
    /// that publishes TRX is not an imported execution.</summary>
    public List<TrxConfigurationEvidence> TrxConfiguration { get; init; } = [];
    public List<string> Limitations { get; init; } = [];
}

public sealed record SourceTestProject
{
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
    /// <summary>xUnit, NUnit, MSTest or null when only the test SDK is referenced.</summary>
    public string? Framework { get; init; }
    public string? FrameworkPackage { get; init; }
    /// <summary>Test-relevant packages (test SDK, runner, TRX/coverage extensions, bUnit, Testcontainers, Mvc.Testing). Names only.</summary>
    public List<string> Packages { get; init; } = [];
    public bool TrxReportConfigured { get; init; }
    public TestKind Kind { get; init; }
    public TestEvidenceConfidence KindConfidence { get; init; }
    public string KindBasis { get; init; } = "";
    /// <summary>False when the framework has no source discovery provider (NUnit, MSTest): its results still import from TRX, uncorrelated.</summary>
    public bool DiscoverySupported { get; init; }
    public int DefinitionCount { get; init; }
}

public sealed record SourceTestTrait(string Name, string Value, string Scope);

/// <summary>An explicit identifier in test source. Confirmed/StronglySupported references link; Inferred ones are candidates needing review.</summary>
public sealed record SourceTestReference(string Id, TestReferenceKind Kind, TestEvidenceConfidence Confidence, string Basis, int Line);

public sealed record SourceTestDefinition
{
    /// <summary>Stable across snapshots: repository + project + fully-qualified name. Used to re-resolve a test in a later snapshot.</summary>
    public string TestDefinitionId { get; init; } = "";
    public string StableIdentity { get; init; } = "";
    public string Project { get; init; } = "";
    public string FilePath { get; init; } = "";
    public int Line { get; init; }
    public string? Namespace { get; init; }
    /// <summary>Containing class as runners report it: Namespace.Outer+Inner.</summary>
    public string ClassName { get; init; } = "";
    public string MethodName { get; init; } = "";
    public string FullyQualifiedName { get; init; } = "";
    public string? DisplayName { get; init; }
    public string Framework { get; init; } = "xUnit";
    public TestDefinitionKind DefinitionKind { get; init; }
    public TestKind Kind { get; init; }
    public TestEvidenceConfidence KindConfidence { get; init; }
    public string KindBasis { get; init; } = "";
    /// <summary>Skip declared in source ([Fact(Skip = …)]). A source skip is not a result.</summary>
    public bool SkipDeclared { get; init; }
    /// <summary>[InlineData] rows declared on a theory (MemberData/ClassData rows are only known at execution).</summary>
    public int InlineDataRows { get; init; }
    public bool HasDynamicData { get; init; }
    public List<SourceTestTrait> Traits { get; init; } = [];
    public List<string> Categories { get; init; } = [];
    public List<SourceTestReference> References { get; init; } = [];
    /// <summary>SHA-256 of the method's source text: a changed fingerprint for the same identity means the test itself changed.</summary>
    public string SourceFingerprint { get; init; } = "";
}

public sealed record TrxConfigurationEvidence(string File, int Line, string Kind, string Detail);

// ── Provider registry ──────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>Capability dimensions of a test-evidence provider. "Supported" is never one word: each dimension has its own level.</summary>
public sealed record TestEvidenceProviderCapability(string Dimension, string Level, string Detail);

public sealed record TestEvidenceProviderDescriptor(string ProviderId, string DisplayName, string Kind, List<string> Formats, List<TestEvidenceProviderCapability> Capabilities, string Limitations);

public static class TestEvidenceProviderRegistry
{
    public static IReadOnlyList<TestEvidenceProviderDescriptor> Providers { get; } =
    [
        new(TestEvidenceProviderIds.Trx, "TRX test results", "ExecutionResultImport", [".trx"],
        [
            new("ArtifactImport", "Full", "Manual import of .trx files written by the VSTest TRX logger or the Microsoft.Testing.Platform TrxReport extension."),
            new("SourceCorrelation", "Partial", "Deterministic match to source tests found by a discovery provider (today: xUnit). Results of other frameworks import uncorrelated."),
            new("RequirementCorrelation", "Partial", "Only through explicit references on the correlated source test; never from test names or wording."),
            new("LiveRetrieval", "Unsupported", "BirkNext does not run tests."),
            new("PipelineRetrieval", "Planned", "Automatic retrieval from Azure DevOps test runs is not implemented; import the published .trx files."),
        ], "Raw TRX is not retained (machine paths, user names, unbounded output); normalized evidence and the artifact fingerprint are. Code coverage is not imported."),
        new(TestEvidenceProviderIds.DotNetXunitDiscovery, "xUnit source test discovery (.NET)", "SourceTestDiscovery", [".csproj", ".cs"],
        [
            new("TestProjectDetection", "Full", "Test projects from project metadata (IsTestProject, test SDK and framework packages), not folder names."),
            new("FactTheoryDiscovery", "Partial", "[Fact]/[Theory] from C# syntax; custom attributes derived from FactAttribute and inherited test methods are not discovered."),
            new("TraitDiscovery", "Full", "[Trait] name/value pairs on methods and classes."),
            new("RequirementReferences", "Partial", "Explicit identifiers in traits, method comments and method bodies, by the shared requirement grammar."),
        ], "Syntax only (no compilation): theory data from MemberData/ClassData and runtime display names are unknown until results are imported."),
    ];

    public static TestEvidenceProviderDescriptor? Find(string providerId) => Providers.FirstOrDefault(p => p.ProviderId == providerId);
}

// ── Execution result import (TRX) ──────────────────────────────────────────────────────────────────────────────────────

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TestResultImportStatus { Valid, InvalidArtifact, UnsupportedFormat, TooLarge }

/// <summary>Counts parsed from the individual results, never derived (Total − Failed is not Passed). Providers' own counters are informational.</summary>
public sealed record TestResultCounts
{
    public int Total { get; init; }
    public int Passed { get; init; }
    public int Failed { get; init; }
    public int Skipped { get; init; }
    public int NotExecuted { get; init; }
    public int Inconclusive { get; init; }
    public int TimedOut { get; init; }
    public int Aborted { get; init; }
    public int ExecutionFailed { get; init; }
    public int Unknown { get; init; }

    public static TestResultCounts From(IEnumerable<NormalizedTestExecution> executions)
    {
        var list = executions.ToList();
        return new TestResultCounts
        {
            Total = list.Count,
            Passed = list.Count(e => e.Result == "Passed"),
            Failed = list.Count(e => e.Result == "Failed"),
            Skipped = list.Count(e => e.Result == "Skipped"),
            NotExecuted = list.Count(e => e.ExecutionState == "NotExecuted"),
            Inconclusive = list.Count(e => e.Result == "Inconclusive"),
            TimedOut = list.Count(e => e.ExecutionState == "TimedOut"),
            Aborted = list.Count(e => e.ExecutionState == "Aborted"),
            ExecutionFailed = list.Count(e => e.ExecutionState == "ExecutionFailed"),
            Unknown = list.Count(e => e.Result == "Unknown" && e.ExecutionState is not ("NotExecuted" or "TimedOut" or "Aborted" or "ExecutionFailed")),
        };
    }
}

public sealed record TestRunPreview
{
    public string ProviderRunId { get; init; } = "";
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
    public double? DurationMs { get; init; }
    /// <summary>Completed, Aborted, ExecutionFailed or Unknown — the run, not the tests. An aborted run does not make unreported tests Failed.</summary>
    public string RunState { get; init; } = "Unknown";
    /// <summary>The provider's own summary outcome as written (e.g. TRX ResultSummary outcome "Failed").</summary>
    public string? ProviderOutcome { get; init; }
    public TestResultCounts Counts { get; init; } = new();
    /// <summary>The provider's declared counters, kept to show where they disagree with the parsed results.</summary>
    public TestResultCounts? DeclaredCounts { get; init; }
    public List<string> Messages { get; init; } = [];
}

/// <summary>One execution of one test case. A theory produces one per data row. Immutable once imported.</summary>
public sealed record NormalizedTestExecution
{
    public string ProviderExecutionId { get; init; } = "";
    public string ProviderTestId { get; init; } = "";
    public string TestName { get; init; } = "";
    public string? ClassName { get; init; }
    /// <summary>Method as the provider identified it (bare name, fully-qualified name, or null for display-name-only results).</summary>
    public string? MethodName { get; init; }
    public string? FullyQualifiedName { get; init; }
    /// <summary>Theory data-row label, e.g. "value: 1, expected: 1".</summary>
    public string? DataRowLabel { get; init; }
    /// <summary>Assembly file name only (no path) — distinguishes identical names in different test projects.</summary>
    public string? AssemblyName { get; init; }
    public string ExecutionState { get; init; } = "Unknown";
    public string Result { get; init; } = "Unknown";
    public string ProviderOutcome { get; init; } = "";
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
    public double? DurationMs { get; init; }
    public string? ErrorMessage { get; init; }
    public string? StackTrace { get; init; }
    public string? Output { get; init; }
    public TestCorrelation Correlation { get; init; } = TestCorrelation.NotAssessed;
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TestCorrelationState { Confirmed, Ambiguous, Unresolved, NotAssessed }

public sealed record TestCorrelation(TestCorrelationState State, string Basis, string? TestDefinitionId = null, List<string>? CandidateDefinitionIds = null)
{
    public static TestCorrelation NotAssessed { get; } = new(TestCorrelationState.NotAssessed, "No source test inventory was selected for correlation.");
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TestSourceBinding { Provided, Derived, Unknown }

public sealed record TestResultArtifactPreview
{
    public TestResultImportStatus Status { get; init; }
    public string ProviderId { get; init; } = TestEvidenceProviderIds.Trx;
    public int ProviderVersion { get; init; } = 1;
    public string Format { get; init; } = "TRX";
    public string FileName { get; init; } = "";
    public long SizeBytes { get; init; }
    /// <summary>SHA-256 of the uploaded bytes: deduplication, provenance and re-import detection.</summary>
    public string Fingerprint { get; init; } = "";
    public string? Error { get; init; }
    public TestRunPreview? Run { get; init; }
    public List<NormalizedTestExecution> Executions { get; init; } = [];
    /// <summary>Snapshot whose test inventory was used to correlate identities (null when none was selected).</summary>
    public Guid? CorrelationSnapshotId { get; init; }
    public string? CorrelationSnapshotFingerprint { get; init; }
    public string? CorrelationRepositoryName { get; init; }
    /// <summary>Provided only when the user stated the results came from that source; identity correlation alone never binds a version.</summary>
    public TestSourceBinding SourceBinding { get; init; } = TestSourceBinding.Unknown;
    public string? BuildReference { get; init; }
    public TestSourceBinding BuildBinding { get; init; } = TestSourceBinding.Unknown;
    public string? CommitReference { get; init; }
    public TestSourceBinding CommitBinding { get; init; } = TestSourceBinding.Unknown;
    public string? EnvironmentReference { get; init; }
    /// <summary>The correlated source definitions (only those an execution matched), so the workspace can record their references.</summary>
    public List<SourceTestDefinition> MatchedDefinitions { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
    public List<string> Limitations { get; init; } = [];
}

// ── Correlation ────────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Deterministic execution → source-test matching, strongest identity first: fully-qualified name (with assembly/project to break ties), class +
/// method, then a display name declared in that class. No fuzzy matching: anything else is Unresolved, more than one candidate is Ambiguous.
/// </summary>
public static class TestEvidenceCorrelation
{
    public static TestCorrelation Correlate(NormalizedTestExecution execution, IReadOnlyList<SourceTestDefinition> definitions)
    {
        if (definitions.Count == 0) return TestCorrelation.NotAssessed;
        var fqn = execution.FullyQualifiedName;
        if (!string.IsNullOrEmpty(fqn))
        {
            var byName = definitions.Where(d => d.FullyQualifiedName.Equals(fqn, StringComparison.Ordinal)).ToList();
            var result = Pick(byName, execution, "Fully-qualified test name");
            if (result is not null) return result;
        }
        if (!string.IsNullOrEmpty(execution.ClassName) && !string.IsNullOrEmpty(execution.MethodName) && !execution.MethodName.Contains('.'))
        {
            var byMethod = definitions.Where(d => d.ClassName.Equals(execution.ClassName, StringComparison.Ordinal) && d.MethodName.Equals(execution.MethodName, StringComparison.Ordinal)).ToList();
            var result = Pick(byMethod, execution, "Class and method name");
            if (result is not null) return result;
        }
        if (!string.IsNullOrEmpty(execution.ClassName))
        {
            var byDisplay = definitions.Where(d => d.ClassName.Equals(execution.ClassName, StringComparison.Ordinal) && d.DisplayName is not null
                && d.DisplayName.Equals(execution.TestName, StringComparison.Ordinal)).ToList();
            var result = Pick(byDisplay, execution, "Display name declared in source for this class");
            if (result is not null) return result;
        }
        return new TestCorrelation(TestCorrelationState.Unresolved, "No source test has this identity in the selected snapshot (a name match is required; similar names are not linked).");
    }

    private static TestCorrelation? Pick(List<SourceTestDefinition> candidates, NormalizedTestExecution execution, string basis)
    {
        if (candidates.Count == 0) return null;
        if (candidates.Count == 1) return new TestCorrelation(TestCorrelationState.Confirmed, basis, candidates[0].TestDefinitionId);
        // Same name in several test projects/repositories: the assembly that ran it decides, when it names exactly one project.
        if (execution.AssemblyName is { Length: > 0 } assembly)
        {
            var project = System.IO.Path.GetFileNameWithoutExtension(assembly);
            var inProject = candidates.Where(c => c.Project.Equals(project, StringComparison.OrdinalIgnoreCase)).ToList();
            if (inProject.Count == 1) return new TestCorrelation(TestCorrelationState.Confirmed, basis + " and test assembly", inProject[0].TestDefinitionId);
        }
        return new TestCorrelation(TestCorrelationState.Ambiguous, $"{basis} matches {candidates.Count} source tests; none is linked automatically.", null,
            candidates.Select(c => c.TestDefinitionId).ToList());
    }

    /// <summary>Aggregate of one logical definition over its executions in one run (a theory's data rows). Individual executions stay separate.</summary>
    public static string Aggregate(IEnumerable<string> results)
    {
        var list = results.ToList();
        if (list.Count == 0) return "No execution evidence";
        if (list.All(r => r == "Passed")) return list.Count == 1 ? "Passed" : $"All {list.Count} passed";
        var failed = list.Count(r => r == "Failed");
        if (failed > 0) return list.Count == 1 ? "Failed" : $"{failed} of {list.Count} failed";
        return list.Count == 1 ? list[0] : $"{list.Count(r => r == "Passed")} of {list.Count} passed; others not executed or unknown";
    }
}
