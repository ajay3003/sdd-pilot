using System.Text.Json;
using BirkNext.TestEvidence;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Moq;

namespace BirkNext.Web.Tests.Services;

/// <summary>Test evidence in the shared SDD lifecycle: definitions vs executions vs results, linking, dedupe, currentness and findings.</summary>
public sealed class SddTestEvidenceServiceTests
{
    internal static readonly Guid SnapshotA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    internal static readonly Guid SnapshotB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly string[] Requirements = ["FR-023", "FR-026", "FR-031", "JIRA-123"];
    private static readonly string[] Criteria = ["AC-007"];

    internal static SourceTestDefinition Definition(string method, string fingerprint = "f1", string repository = "Contoso", string project = "Contoso.Unit.Tests",
        TestDefinitionKind kind = TestDefinitionKind.Fact, params SourceTestReference[] references) => new()
    {
        TestDefinitionId = $"{repository}:{project}:{method}", StableIdentity = $"{repository}::{project}::Contoso.Tests.{method}", Project = project,
        ClassName = "Contoso.Tests.OrderTests", MethodName = method, FullyQualifiedName = $"Contoso.Tests.OrderTests.{method}", FilePath = "tests/OrderTests.cs", Line = 10,
        DefinitionKind = kind, Kind = TestKind.Unit, KindConfidence = TestEvidenceConfidence.StronglySupported, KindBasis = "Project name token 'Unit'",
        SourceFingerprint = fingerprint, References = references.ToList(),
    };

    internal static SourceTestReference Ref(string id, TestEvidenceConfidence confidence, TestReferenceKind kind = TestReferenceKind.Requirement) =>
        new(id, kind, confidence, confidence == TestEvidenceConfidence.Confirmed ? "[Trait(\"Requirement\")] on the test method" : confidence == TestEvidenceConfidence.StronglySupported ? "Structured comment on the test method" : "Mentioned in a comment in the test body", 9);

    internal static SourceTestInventory Inventory(Guid snapshot, string fingerprint, params SourceTestDefinition[] definitions) =>
        new() { SnapshotId = snapshot, SnapshotFingerprint = fingerprint, RepositoryName = definitions.FirstOrDefault()?.StableIdentity.Split("::")[0] ?? "Contoso", Definitions = definitions.ToList() };

    internal static readonly SourceTestDefinition Discount = Definition("Discount", references: Ref("FR-023", TestEvidenceConfidence.StronglySupported));
    internal static readonly SourceTestDefinition Rounding = Definition("Rounding", references: [Ref("FR-026", TestEvidenceConfidence.Confirmed), Ref("FR-031", TestEvidenceConfidence.Inferred)]);
    internal static readonly SourceTestDefinition Square = Definition("Square", kind: TestDefinitionKind.Theory, references: [Ref("JIRA-123", TestEvidenceConfidence.Confirmed), Ref("AC-007", TestEvidenceConfidence.Confirmed, TestReferenceKind.AcceptanceCriterion)]);
    internal static readonly SourceTestDefinition Plain = Definition("Plain");

    internal static NormalizedTestExecution Exec(string id, SourceTestDefinition? definition, string result, string? row = null, string state = "Completed") => new()
    {
        ProviderExecutionId = id, ProviderTestId = "t" + id, TestName = definition?.FullyQualifiedName ?? "Other.Test", ClassName = definition?.ClassName, MethodName = definition?.MethodName,
        FullyQualifiedName = definition?.FullyQualifiedName, DataRowLabel = row, ExecutionState = state, Result = result, ProviderOutcome = result, DurationMs = 2,
        StartedAt = DateTimeOffset.Parse("2026-09-30T08:00:00Z"), FinishedAt = DateTimeOffset.Parse("2026-09-30T08:00:01Z"),
        Correlation = definition is null ? new TestCorrelation(TestCorrelationState.Unresolved, "No source test has this identity") : new TestCorrelation(TestCorrelationState.Confirmed, "Fully-qualified test name", definition.TestDefinitionId),
    };

    internal static TestResultArtifactPreview Preview(string fingerprint, string runId, DateTimeOffset finished, bool bound, Guid snapshot, string snapshotFingerprint, params (NormalizedTestExecution Execution, SourceTestDefinition? Definition)[] rows)
    {
        var executions = rows.Select(r => r.Execution with { FinishedAt = finished, StartedAt = finished.AddSeconds(-1) }).ToList();
        return new TestResultArtifactPreview
        {
            Status = TestResultImportStatus.Valid, FileName = $"{runId}.trx", Fingerprint = fingerprint, SizeBytes = 1000,
            Run = new TestRunPreview { ProviderRunId = runId, StartedAt = finished.AddSeconds(-5), FinishedAt = finished, RunState = "Completed", Counts = TestResultCounts.From(executions) },
            Executions = executions, CorrelationSnapshotId = snapshot, CorrelationSnapshotFingerprint = snapshotFingerprint, CorrelationRepositoryName = "Contoso",
            SourceBinding = bound ? TestSourceBinding.Provided : TestSourceBinding.Unknown, BuildReference = "20260930.1", BuildBinding = TestSourceBinding.Provided,
            MatchedDefinitions = rows.Select(r => r.Definition).OfType<SourceTestDefinition>().DistinctBy(d => d.TestDefinitionId).ToList(),
        };
    }

    internal static TestResultArtifactPreview FirstRun(bool bound = true) => Preview("fp-1", "run-1", DateTimeOffset.Parse("2026-09-30T08:00:00Z"), bound, SnapshotA, "sha-a",
        (Exec("1", Discount, "Passed"), Discount), (Exec("2", Rounding, "Passed"), Rounding),
        (Exec("3", Square, "Passed", "value: 1"), Square), (Exec("4", Square, "Passed", "value: 2"), Square), (Exec("5", Square, "Failed", "value: 3"), Square),
        (Exec("6", Plain, "Unknown", state: "NotExecuted"), Plain), (Exec("7", null, "Passed"), null));

    internal static (SddLifecycleState State, SddEvidenceGraphService Graph, WorkspaceArtifactRepository Repository) Workspace()
    {
        var requirements = Requirements.Select(id => new SemanticRequirement
        {
            Id = id, Text = $"Requirement {id}",
            LinkedAcceptanceScenarios = id == "JIRA-123" ? [new SemanticAcceptanceScenario { Id = "AC-007", Title = "Squares" }] : [],
        }).ToList();
        var context = new ReviewContext { Specification = new SpecificationSemanticModel { Requirements = requirements } };
        var provider = new Mock<IReviewContextProvider>();
        provider.Setup(x => x.GetCurrent()).Returns(context);
        var repository = new WorkspaceArtifactRepository();
        repository.SddLifecycle.SourceSnapshots.Add(new SddSourceSnapshotReference { SnapshotId = SnapshotA.ToString(), Fingerprint = "sha-a", EnvironmentReference = "dev", Currentness = "Current", AnalysisStatus = "Ready" });
        return (repository.SddLifecycle, new SddEvidenceGraphService(provider.Object), repository);
    }

    // ── Definitions ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Definitions_LinkOnlyStrongReferencesToCurrentRequirements()
    {
        var (state, graph, repository) = Workspace();
        var added = SddTestEvidenceService.RecordDefinitions(state, Inventory(SnapshotA, "sha-a", Discount, Rounding, Square, Plain), Requirements, Criteria);
        Assert.Equal(4, added);
        Assert.Equal(0, SddTestEvidenceService.RecordDefinitions(state, Inventory(SnapshotA, "sha-a", Discount), Requirements, Criteria));
        var links = state.Links.Where(l => l.Relationship == SddTestEvidenceService.RequirementTestedBySource).ToList();
        Assert.Contains(links, l => l.FromId == "FR-023" && l.Confidence == "StronglySupported");
        Assert.Contains(links, l => l.FromId == "FR-026" && l.Confidence == "Confirmed");
        Assert.DoesNotContain(links, l => l.FromId == "FR-031");
        var rows = SddTestEvidenceService.RequirementRows(state, graph.Build(repository));
        var fr031 = rows.Single(r => r.RequirementId == "FR-031");
        Assert.Empty(fr031.SourceTests);
        Assert.Single(fr031.CandidateSourceTests);
        Assert.Single(rows.Single(r => r.RequirementId == "JIRA-123").SourceTests);
        Assert.Empty(rows.Single(r => r.RequirementId == "FR-023").Executions);
        Assert.Equal("No execution evidence", rows.Single(r => r.RequirementId == "FR-023").Currentness);
    }

    // ── Import ────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Import_RecordsArtifactRunAndEveryExecution_WithRequirementLinksFromTheSourceTest()
    {
        var (state, graph, repository) = Workspace();
        var summary = SddTestEvidenceService.Import(state, FirstRun(), Requirements, Criteria);

        Assert.False(summary.AlreadyImported);
        Assert.Equal((7, 5, 1, 1), (summary.TestsFound, summary.Passed, summary.Failed, summary.SkippedOrNotExecuted));
        Assert.Equal(1, summary.UnresolvedDefinitions);
        Assert.Equal(5, summary.RequirementLinked);
        Assert.Equal(3, summary.AcceptanceCriterionLinked);
        var artifact = Assert.Single(state.TestResultArtifacts);
        Assert.Equal(("fp-1", "Provided", SnapshotA.ToString(), "20260930.1"), (artifact.Fingerprint, artifact.SourceBinding, artifact.SourceSnapshotId, artifact.BuildReference));
        var run = Assert.Single(state.TestRuns);
        Assert.Equal(7, run.Counts.Total);
        Assert.Equal(7, state.TestExecutions.Count);

        var square = state.TestExecutions.Where(e => e.TestDefinitionId == Square.TestDefinitionId).ToList();
        Assert.Equal(3, square.Count);
        Assert.All(square, e => Assert.Equal(["JIRA-123"], e.RequirementReferences));
        Assert.All(square, e => Assert.Equal(["AC-007"], e.AcceptanceCriterionReferences));
        var rounding = state.TestExecutions.Single(e => e.TestDefinitionId == Rounding.TestDefinitionId);
        Assert.Equal(["FR-026"], rounding.RequirementReferences);
        Assert.Equal(["FR-031"], rounding.CandidateRequirementReferences);

        var unmatched = state.TestExecutions.Single(e => e.CorrelationState == "Unresolved");
        Assert.Empty(unmatched.RequirementReferences);
        Assert.Null(unmatched.TestDefinitionId);
        Assert.Equal("Passed", unmatched.Result);

        Assert.Contains(state.Links, l => l.FromId == "FR-026" && l.Relationship == SddTestEvidenceService.RequirementVerifiedBy && l.Confidence == "Confirmed");
        Assert.Contains(state.Links, l => l.FromId == "AC-007" && l.Relationship == SddTestEvidenceService.AcceptanceCriterionVerifiedBy);
        Assert.DoesNotContain(state.Links, l => l.FromId == "FR-031" && l.Relationship == SddTestEvidenceService.RequirementVerifiedBy);

        var rows = graph.Build(repository);
        Assert.Equal(3, rows.Single(r => r.Requirement.Id == "JIRA-123").Executions.Count);
        Assert.Empty(rows.Single(r => r.Requirement.Id == "FR-031").Executions);
        Assert.Contains(state.TestDefinitions, d => d.Definition.TestDefinitionId == Square.TestDefinitionId);
    }

    [Fact]
    public void Import_ExecutionStateAndResultStayDistinct()
    {
        var (state, _, _) = Workspace();
        SddTestEvidenceService.Import(state, FirstRun(), Requirements, Criteria);
        var notExecuted = state.TestExecutions.Single(e => e.TestDefinitionId == Plain.TestDefinitionId);
        Assert.Equal(("NotExecuted", "Unknown"), (notExecuted.ExecutionState, notExecuted.Result));
        Assert.Equal("NotExecuted", SddTestEvidenceService.CurrentResult(state, Plain.TestDefinitionId).Result);
    }

    [Fact]
    public void Import_SameFileOrSameRunIsNeverImportedTwice()
    {
        var (state, _, _) = Workspace();
        SddTestEvidenceService.Import(state, FirstRun(), Requirements, Criteria);
        var again = SddTestEvidenceService.Import(state, FirstRun(), Requirements, Criteria);
        Assert.True(again.AlreadyImported);
        Assert.Contains(again.Warnings, w => w.Contains("this file was imported before"));
        var reserialized = FirstRun() with { Fingerprint = "fp-1-reformatted" };
        var sameRun = SddTestEvidenceService.Import(state, reserialized, Requirements, Criteria);
        Assert.True(sameRun.AlreadyImported);
        Assert.Contains(sameRun.Warnings, w => w.Contains("test run id"));
        Assert.Single(state.TestResultArtifacts);
        Assert.Equal(7, state.TestExecutions.Count);
    }

    [Fact]
    public void Import_IsImmutable_ALaterRunAddsEvidenceAndNeverRewritesAnOldResult()
    {
        var (state, _, _) = Workspace();
        SddTestEvidenceService.Import(state, FirstRun(), Requirements, Criteria);
        var second = Preview("fp-2", "run-2", DateTimeOffset.Parse("2026-10-01T08:00:00Z"), true, SnapshotA, "sha-a", (Exec("1", Discount, "Failed"), Discount));
        SddTestEvidenceService.Import(state, second, Requirements, Criteria);
        var discount = state.TestExecutions.Where(e => e.TestDefinitionId == Discount.TestDefinitionId).OrderBy(e => e.ExecutedAt).ToList();
        Assert.Equal(["Passed", "Failed"], discount.Select(e => e.Result));
        var current = SddTestEvidenceService.CurrentResult(state, Discount.TestDefinitionId);
        Assert.Equal("Failed", current.Result);
        Assert.Contains("bound to the currently selected source snapshot", current.Reason);
        Assert.Contains("1 earlier execution(s) retained", current.Reason);
    }

    // ── Current result ────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void CurrentResult_PrefersTheCurrentSource_AndExplainsUnknownSource()
    {
        var (state, _, _) = Workspace();
        SddTestEvidenceService.Import(state, FirstRun(bound: true), Requirements, Criteria);
        // A newer run whose source version is unknown does not displace the result bound to the selected source.
        var newer = Preview("fp-3", "run-3", DateTimeOffset.Parse("2026-10-02T08:00:00Z"), false, SnapshotA, "sha-a", (Exec("9", Discount, "Failed"), Discount));
        SddTestEvidenceService.Import(state, newer, Requirements, Criteria);
        var current = SddTestEvidenceService.CurrentResult(state, Discount.TestDefinitionId);
        Assert.Equal("Passed", current.Result);
        Assert.Equal("NotAssessed", state.TestExecutions.Single(e => e.ProviderResultId!.StartsWith("run-3")).SourceCurrentness);

        var (unbound, _, _) = Workspace();
        SddTestEvidenceService.Import(unbound, FirstRun(bound: false), Requirements, Criteria);
        var reason = SddTestEvidenceService.CurrentResult(unbound, Discount.TestDefinitionId).Reason;
        Assert.Contains("source version unknown", reason);
        Assert.All(unbound.TestExecutions, e => Assert.Null(e.SourceSnapshotId));
    }

    [Fact]
    public void CurrentResult_TheoryAggregatesItsRowsWithoutDroppingThem()
    {
        var (state, _, _) = Workspace();
        SddTestEvidenceService.Import(state, FirstRun(), Requirements, Criteria);
        var current = SddTestEvidenceService.CurrentResult(state, Square.TestDefinitionId);
        Assert.Equal("1 of 3 failed", current.Result);
        Assert.Equal(3, current.RunExecutions.Count);
        var none = SddTestEvidenceService.CurrentResult(state, "never-ran");
        Assert.Equal("No execution evidence", none.Result);
        Assert.Contains("not NotExecuted", none.Reason);
    }

    // ── Source and requirement change ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SnapshotChange_ReResolvesDefinitions_AndRetainsHistoricalResults()
    {
        var (state, _, _) = Workspace();
        SddTestEvidenceService.RecordDefinitions(state, Inventory(SnapshotA, "sha-a", Discount, Rounding, Square), Requirements, Criteria);
        SddTestEvidenceService.Import(state, FirstRun(), Requirements, Criteria);

        var changedRounding = Rounding with { SourceFingerprint = "f2" };
        SddTestEvidenceService.RecordDefinitions(state, Inventory(SnapshotB, "sha-b", Discount, changedRounding), Requirements, Criteria);

        var old = state.TestDefinitions.Where(d => d.SourceSnapshotId == SnapshotA.ToString()).ToDictionary(d => d.Definition.MethodName);
        Assert.Equal("Historical", old["Discount"].Currentness);
        Assert.Equal("PotentiallyStale", old["Rounding"].Currentness);
        Assert.Contains("changed", old["Rounding"].CurrentnessReason);
        Assert.Equal("PotentiallyStale", old["Square"].Currentness);
        Assert.Contains("no longer resolves", old["Square"].CurrentnessReason);

        var squareRuns = state.TestExecutions.Where(e => e.TestDefinitionId == Square.TestDefinitionId).ToList();
        Assert.All(squareRuns, e => Assert.Equal("PotentiallyStale", e.Currentness));
        Assert.Contains(squareRuns, e => e.Result == "Passed");
        Assert.Equal("Current", state.TestExecutions.Single(e => e.TestDefinitionId == Discount.TestDefinitionId).Currentness);
        Assert.Equal("No current result", SddTestEvidenceService.CurrentResult(state, Square.TestDefinitionId).Result);
        Assert.Equal(2, state.TestDefinitions.Count(d => d.SourceSnapshotId == SnapshotB.ToString()));
    }

    [Fact]
    public void SnapshotChange_InAnotherRepositoryDoesNotTouchThisOne()
    {
        var (state, _, _) = Workspace();
        var common = Definition("Discount", repository: "Common", project: "Common.Tests");
        SddTestEvidenceService.RecordDefinitions(state, Inventory(SnapshotA, "sha-a", Discount), Requirements, Criteria);
        SddTestEvidenceService.RecordDefinitions(state, Inventory(SnapshotB, "sha-b", common), Requirements, Criteria);
        Assert.All(state.TestDefinitions, d => Assert.Equal("Current", d.Currentness));
        Assert.Equal(2, state.TestDefinitions.Select(d => d.Definition.TestDefinitionId).Distinct().Count());
    }

    [Fact]
    public void RequirementChange_MarksLinkedImportedExecutionsPotentiallyStale()
    {
        var (state, _, _) = Workspace();
        SddTestEvidenceService.Import(state, FirstRun(), Requirements, Criteria);
        SddLifecycleReviewService.ReconcileRequirements(state, [new SemanticRequirement { Id = "FR-026", Text = "Rounding" }]);
        SddLifecycleReviewService.ReconcileRequirements(state, [new SemanticRequirement { Id = "FR-026", Text = "Rounding is bankers" }]);
        var rounding = state.TestExecutions.Single(e => e.TestDefinitionId == Rounding.TestDefinitionId);
        Assert.Equal("PotentiallyStale", rounding.Currentness);
        Assert.Equal("Passed", rounding.Result);
    }

    // ── Quality findings ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void QualityFindings_OnlyABoundCurrentFailureIsAFailure()
    {
        var (state, graph, repository) = Workspace();
        SddTestEvidenceService.RecordDefinitions(state, Inventory(SnapshotA, "sha-a", Discount, Rounding, Square, Definition("Unrun", references: Ref("FR-023", TestEvidenceConfidence.Confirmed))), Requirements, Criteria);
        SddTestEvidenceService.Import(state, FirstRun(bound: true), Requirements, Criteria);
        var findings = graph.QualityFindings(repository);

        var failed = Assert.Single(findings, f => f.Code == "CurrentLinkedTestFailed");
        Assert.Equal(("Failed", "JIRA-123"), (failed.Severity, failed.RequirementId));
        Assert.StartsWith("test-execution:", failed.EvidenceReference);
        Assert.Contains(findings, f => f.Code == "LinkedSourceTestWithoutExecutionEvidence" && f.Severity == "NotAssessed" && f.RequirementId == "FR-023");
        Assert.Contains(findings, f => f.Code == "RequirementTestLinkUnresolved" && f.Severity == "NeedsReview" && f.RequirementId == "FR-031");
        Assert.DoesNotContain(findings, f => f.Code == "CurrentLinkedTestFailed" && f.RequirementId == "FR-026");

        var (unbound, unboundGraph, unboundRepository) = Workspace();
        SddTestEvidenceService.Import(unbound, FirstRun(bound: false), Requirements, Criteria);
        var unboundFindings = unboundGraph.QualityFindings(unboundRepository);
        Assert.DoesNotContain(unboundFindings, f => f.Severity == "Failed");
        Assert.Contains(unboundFindings, f => f.Code == "LinkedTestFailedSourceUnknown" && f.Severity == "NeedsReview");
        Assert.Contains(unboundFindings, f => f.Code == "TestResultSourceUnknown" && f.Severity == "Information");
    }

    [Fact]
    public void QualityFindings_AreUnchangedForWorkspacesWithoutProviderEvidence()
    {
        var (state, graph, repository) = Workspace();
        SddEvidenceGraphService.ImportExecutions(state, [new SddTestExecutionImportRecord { TestId = "T-1", RequirementReferences = ["FR-023"], ExecutionState = "Completed", Result = "Failed", ResultSource = "json" }]);
        Assert.Empty(SddTestEvidenceService.QualityFindings(state, graph.Build(repository)));
    }

    // ── Persistence ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Persistence_RoundTripsThroughTheWorkspaceLifecycleJson_AndOldJsonLoads()
    {
        var (state, _, repository) = Workspace();
        SddTestEvidenceService.RecordDefinitions(state, Inventory(SnapshotA, "sha-a", Discount, Square), Requirements, Criteria);
        SddTestEvidenceService.Import(state, FirstRun(), Requirements, Criteria);
        var json = JsonSerializer.Serialize(repository.SddLifecycle);

        var restored = new WorkspaceArtifactRepository();
        restored.RestoreSddLifecycle(json);
        Assert.Single(restored.SddLifecycle.TestResultArtifacts);
        Assert.Single(restored.SddLifecycle.TestRuns);
        Assert.Equal(7, restored.SddLifecycle.TestExecutions.Count);
        Assert.Equal(state.TestDefinitions.Count, restored.SddLifecycle.TestDefinitions.Count);
        Assert.Equal(TestReferenceKind.AcceptanceCriterion, restored.SddLifecycle.TestDefinitions.Single(d => d.Definition.MethodName == "Square").Definition.References.Single(r => r.Id == "AC-007").Kind);
        Assert.Equal("1 of 3 failed", SddTestEvidenceService.CurrentResult(restored.SddLifecycle, Square.TestDefinitionId).Result);

        var legacy = new WorkspaceArtifactRepository();
        legacy.RestoreSddLifecycle("""{"SchemaVersion":1,"TestExecutions":[{"TestId":"T-1","ExecutionState":"Completed","Result":"Passed","ResultSource":"json"}]}""");
        Assert.Empty(legacy.SddLifecycle.TestRuns);
        Assert.Empty(legacy.SddLifecycle.TestDefinitions);
        Assert.Equal("NotAssessed", Assert.Single(legacy.SddLifecycle.TestExecutions).CorrelationState);
        Assert.Null(legacy.SddLifecycle.TestExecutions[0].RunId);
    }
}
