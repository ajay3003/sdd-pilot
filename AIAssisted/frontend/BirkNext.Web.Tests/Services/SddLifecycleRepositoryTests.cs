using System.Text.Json;
using BirkNext.Web.Services;
using BirkNext.Web.Models;
using Moq;

namespace BirkNext.Web.Tests.Services;

public sealed class SddLifecycleRepositoryTests
{
    [Fact]
    public void Set_CapturesImmutableRevisions_WithoutPromotingNewestToAuthority()
    {
        var repository = new WorkspaceArtifactRepository();
        repository.Set(WorkspaceArtifactType.Specification, "REQ-1: first", "requirements.md");
        var first = Assert.Single(repository.SddLifecycle.Revisions);
        repository.SetArtifactAuthority(first.RevisionId, "Baseline");

        repository.Set(WorkspaceArtifactType.Specification, "REQ-1: revised", "requirements.md");

        Assert.Equal(2, repository.SddLifecycle.Revisions.Count);
        Assert.Equal("Baseline", first.Authority);
        Assert.False(first.IsCurrentSelection);
        Assert.Equal("Unknown", repository.SddLifecycle.Revisions[1].Authority);
        Assert.True(repository.SddLifecycle.Revisions[1].IsCurrentSelection);
        Assert.NotEqual(first.Fingerprint, repository.SddLifecycle.Revisions[1].Fingerprint);
    }

    [Fact]
    public void RestoreSddLifecycle_RestoresHistoryAndCurrentRevisionWithoutDuplicate()
    {
        var source = new WorkspaceArtifactRepository();
        source.Set(WorkspaceArtifactType.Specification, "REQ-1", "spec.md");
        source.SetArtifactAuthority(source.SddLifecycle.Revisions[0].RevisionId, "Approved");
        var json = JsonSerializer.Serialize(source.SddLifecycle);

        var restored = new WorkspaceArtifactRepository();
        restored.Set(WorkspaceArtifactType.Specification, "REQ-1", "spec.md");
        restored.RestoreSddLifecycle(json);

        var revision = Assert.Single(restored.SddLifecycle.Revisions);
        Assert.Equal("Approved", revision.Authority);
        Assert.True(revision.IsCurrentSelection);
    }

    [Fact]
    public void Baseline_IsExplicitAndUniqueWithinArtifactRole()
    {
        var repository = new WorkspaceArtifactRepository();
        repository.Set(WorkspaceArtifactType.Plan, "Plan A");
        var first = repository.SddLifecycle.Revisions[0];
        repository.Set(WorkspaceArtifactType.Plan, "Plan B");
        var second = repository.SddLifecycle.Revisions[1];

        repository.SetArtifactAuthority(first.RevisionId, "Baseline");
        repository.SetArtifactAuthority(second.RevisionId, "Baseline");

        Assert.Equal("Historical", first.Authority);
        Assert.Equal("Baseline", second.Authority);
        Assert.True(second.IsCurrentSelection);
    }

    [Fact]
    public void QuestionRequiresExplicitResolutionAndReference()
    {
        var lifecycle = new SddLifecycleState();
        Assert.Throws<ArgumentException>(() => SddLifecycleReviewService.ResolveQuestion(lifecycle, "OQ-1", "Which policy?", "Choice A", ""));
        Assert.Empty(lifecycle.Questions);

        SddLifecycleReviewService.ResolveQuestion(lifecycle, "OQ-1", "Which policy?", "Choice A", "DEC-7", ["JIRA-123"]);

        Assert.Equal("Resolved", Assert.Single(lifecycle.Questions).Status);
        var decision = Assert.Single(lifecycle.Decisions);
        Assert.Equal("Accepted", decision.Status);
        Assert.Equal("DEC-7", decision.SourceReference);
    }

    [Fact]
    public void RequirementRevisionChangeMarksOnlyRelatedEvidencePotentiallyStale()
    {
        var lifecycle = new SddLifecycleState();
        var original = new SemanticRequirement { Id = "REQ-1", Text = "Keep the record" };
        var unchanged = new SemanticRequirement { Id = "REQ-2", Text = "Keep the report" };
        SddLifecycleReviewService.ReconcileRequirements(lifecycle, [original, unchanged]);
        SddLifecycleReviewService.AddOrRefreshLink(lifecycle, "REQ-1", "TASK-1", "RequirementDecomposedInto", "StronglySupported");
        SddLifecycleReviewService.AddOrRefreshLink(lifecycle, "REQ-2", "TASK-2", "RequirementDecomposedInto", "StronglySupported");
        lifecycle.ImplementationEvidence.Add(new SddImplementationEvidence { RequirementId = "REQ-1", Reference = "snapshot:file" });
        lifecycle.TestEvidence.Add(new SddTestEvidence { RequirementId = "REQ-1", TestReference = "TEST-1", State = "Executed" });

        SddLifecycleReviewService.ReconcileRequirements(lifecycle, [new SemanticRequirement { Id = "REQ-1", Text = "Keep the record encrypted" }, unchanged]);

        Assert.Equal("PotentiallyStale", lifecycle.Links.Single(x => x.FromId == "REQ-1").Currentness);
        Assert.Equal("Current", lifecycle.Links.Single(x => x.FromId == "REQ-2").Currentness);
        Assert.Equal("PotentiallyStale", Assert.Single(lifecycle.ImplementationEvidence).Currentness);
        Assert.Equal("PotentiallyStale", Assert.Single(lifecycle.TestEvidence).Currentness);
        Assert.Contains(lifecycle.RequirementChanges, x => x.RequirementId == "REQ-1" && x.Change == "Modified");

        SddLifecycleReviewService.AddOrRefreshLink(lifecycle, "REQ-1", "TASK-1", "RequirementDecomposedInto", "StronglySupported");
        Assert.Equal("PotentiallyStale", lifecycle.Links.Single(x => x.FromId == "REQ-1").Currentness);
    }

    [Fact]
    public void AcceptanceCriteriaChangeIsDistinctAndTestEvidenceBecomesStale()
    {
        var lifecycle = new SddLifecycleState();
        var withAc = new SemanticRequirement
        {
            Id = "REQ-1", Text = "Submit report",
            LinkedAcceptanceScenarios = [new SemanticAcceptanceScenario { Id = "AC-1", Title = "accepted" }]
        };
        SddLifecycleReviewService.ReconcileRequirements(lifecycle, [withAc]);
        lifecycle.TestEvidence.Add(new SddTestEvidence { RequirementId = "REQ-1", AcceptanceCriterionId = "AC-1", State = "Passed" });

        SddLifecycleReviewService.ReconcileRequirements(lifecycle, [new SemanticRequirement
        {
            Id = "REQ-1", Text = "Submit report",
            LinkedAcceptanceScenarios = [new SemanticAcceptanceScenario { Id = "AC-1", Title = "accepted after review" }]
        }]);

        Assert.Equal("PotentiallyStale", Assert.Single(lifecycle.TestEvidence).Currentness);
        Assert.Contains(lifecycle.RequirementChanges, x => x.ChangeDetail.Contains("acceptance criteria"));
    }

    [Fact]
    public void ExplicitCodeLinkBindsToExactSourceSnapshotAndDeduplicates()
    {
        var state = new SddLifecycleState();
        var snapshot = new BirkNext.Integrations.IqrSourceSnapshot
        {
            Id = Guid.NewGuid(), Archive = new BirkNext.Integrations.SourceArchive("src.zip", "SHA256-A", 12),
            AnalyzerVersion = 4, AnalyzedAt = DateTimeOffset.UtcNow, Status = BirkNext.Integrations.SourceAnalysisStatus.Partial,
            Limitations = ["Architecture analyzer unsupported"]
        };
        var codeLink = new SddCodeLinkSourceReference("link-1", "REQ-1 - Registration", "src/Registration.cs", "Requirement", "PersistedCodeLink", DateTimeOffset.UtcNow);

        Assert.Equal(1, SddEvidenceGraphService.BindCodeLinks(state, snapshot, [codeLink], ["REQ-1"]));
        Assert.Equal(0, SddEvidenceGraphService.BindCodeLinks(state, snapshot, [codeLink], ["REQ-1"]));

        var evidence = Assert.Single(state.ImplementationEvidence);
        Assert.Equal(snapshot.Id.ToString(), evidence.SourceSnapshotId);
        Assert.Equal("SHA256-A", evidence.SourceFingerprint);
        Assert.Equal("StronglySupported", evidence.Confidence);
        Assert.Equal("NotAssessed", evidence.SourceValidation);
        Assert.Single(state.Links.Where(x => x.Relationship == "RequirementImplementedBy"));
    }

    [Fact]
    public void CodeLinkWithoutExplicitRequirementIdDoesNotCreateImplementationClaim()
    {
        var state = new SddLifecycleState();
        var snapshot = new BirkNext.Integrations.IqrSourceSnapshot { Archive = new("src.zip", "HASH", 1) };
        var codeLink = new SddCodeLinkSourceReference("link", "Registration service", "Registration.cs", "Requirement", "PersistedCodeLink", DateTimeOffset.UtcNow);

        var count = SddEvidenceGraphService.BindCodeLinks(state, snapshot, [codeLink], ["REQ-1"]);

        Assert.Equal(0, count);
        Assert.Empty(state.ImplementationEvidence);
    }

    [Fact]
    public void NewDifferentSourceFingerprintRetainsAndMarksOldCodeLinkEvidenceStale()
    {
        var state = new SddLifecycleState();
        var link = new SddCodeLinkSourceReference("link-1", "REQ-1", "src/Registration.cs", "Requirement", "PersistedCodeLink", DateTimeOffset.UtcNow);
        var first = new BirkNext.Integrations.IqrSourceSnapshot { Id = Guid.NewGuid(), Archive = new("a.zip", "HASH-A", 1) };
        var second = new BirkNext.Integrations.IqrSourceSnapshot { Id = Guid.NewGuid(), Archive = new("b.zip", "HASH-B", 1) };
        SddEvidenceGraphService.BindCodeLinks(state, first, [link], ["REQ-1"]);

        SddEvidenceGraphService.BindCodeLinks(state, second, [link], ["REQ-1"]);

        Assert.Equal(2, state.ImplementationEvidence.Count);
        Assert.Equal("PotentiallyStale", state.ImplementationEvidence.Single(x => x.SourceFingerprint == "HASH-A").Currentness);
        Assert.Contains("different Source Analysis archive fingerprint", state.ImplementationEvidence.Single(x => x.SourceFingerprint == "HASH-A").CurrentnessReason!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Current", state.ImplementationEvidence.Single(x => x.SourceFingerprint == "HASH-B").Currentness);
    }

    [Fact]
    public void SourceSnapshotChangeMarksOnlySourceBoundTestExecutionsPotentiallyStale()
    {
        var state = new SddLifecycleState();
        state.TestExecutions.Add(new SddTestExecutionEvidence
        {
            TestId = "TEST-BOUND", SourceSnapshotId = "snapshot-a", SourceFingerprint = "HASH-A",
            ExecutionState = "Completed", Result = "Passed"
        });
        state.TestExecutions.Add(new SddTestExecutionEvidence
        {
            TestId = "TEST-UNBOUND", ExecutionState = "Completed", Result = "Passed"
        });

        SddEvidenceGraphService.SourceSnapshotChanged(state, Guid.NewGuid(), "HASH-B");

        Assert.Equal("PotentiallyStale", state.TestExecutions.Single(x => x.TestId == "TEST-BOUND").Currentness);
        Assert.Contains("different source snapshot fingerprint", state.TestExecutions.Single(x => x.TestId == "TEST-BOUND").CurrentnessReason!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Current", state.TestExecutions.Single(x => x.TestId == "TEST-UNBOUND").Currentness);
    }

    [Fact]
    public void GraphRebuildDoesNotRevalidateStaleLinksOrChangeCurrentnessWithoutNewEvidence()
    {
        var context = new Mock<IReviewContextProvider>();
        var requirement = new SemanticRequirement { Id = "REQ-1", Text = "Keep a record" };
        var review = new ReviewContext();
        review.Specification.Requirements.Add(requirement);
        review.SpecToPlan["REQ-1"] = ["DES-1"];
        context.Setup(x => x.GetCurrent()).Returns(review);
        var graph = new SddEvidenceGraphService(context.Object);
        var workspace = new WorkspaceArtifactRepository();
        var stale = new SddTraceabilityLink
        {
            FromId = "REQ-1", ToId = "DES-1", Relationship = "RequirementPlansTo",
            Currentness = "PotentiallyStale", Reason = "Requirement changed"
        };
        workspace.SddLifecycle.Links.Add(stale);

        graph.SynchronizeCurrentArtifacts(workspace);

        Assert.Equal("PotentiallyStale", stale.Currentness);
        Assert.Equal("Requirement changed", stale.Reason);
    }

    [Fact]
    public void TestExecutionSeparatesStateFromResultLinksRequirementsAndDeduplicates()
    {
        var state = new SddLifecycleState();
        var record = new SddTestExecutionImportRecord
        {
            TestId = "TEST-1", TestName = "registration works", RequirementReferences = ["REQ-1"], AcceptanceCriterionReferences = ["AC-1"],
            ExecutionState = "Completed", Result = "Failed", ProviderId = "JUnit", ProviderResultId = "run-8", ResultSource = "junit.xml",
            SourceSnapshotId = "snapshot-1", SourceFingerprint = "source-hash", ExecutedAt = DateTimeOffset.UtcNow
        };

        Assert.Equal(1, SddEvidenceGraphService.ImportExecutions(state, [record]));
        Assert.Equal(0, SddEvidenceGraphService.ImportExecutions(state, [record]));

        var execution = Assert.Single(state.TestExecutions);
        Assert.Equal("Completed", execution.ExecutionState);
        Assert.Equal("Failed", execution.Result);
        Assert.Equal("source-hash", execution.SourceFingerprint);
        Assert.Contains(state.Links, x => x.Relationship == "RequirementVerifiedBy" && x.FromId == "REQ-1");
        Assert.Contains(state.Links, x => x.Relationship == "AcceptanceCriterionVerifiedBy" && x.FromId == "AC-1");
    }

    [Fact]
    public void ProviderExecutionFailureCannotBeReportedAsTestFailure()
    {
        var state = new SddLifecycleState();
        var record = new SddTestExecutionImportRecord
        {
            TestId = "TEST-1", ResultSource = "provider", ExecutionState = "ExecutionFailed", Result = "Failed"
        };
        Assert.Throws<ArgumentException>(() => SddEvidenceGraphService.ImportExecutions(state, [record]));
        Assert.Empty(state.TestExecutions);
    }

    [Fact]
    public void RequirementChangeMakesLinkedExecutionPotentiallyStaleWithoutDeletingResult()
    {
        var state = new SddLifecycleState();
        var original = new SemanticRequirement { Id = "REQ-1", Text = "Submit" };
        SddLifecycleReviewService.ReconcileRequirements(state, [original]);
        SddEvidenceGraphService.ImportExecutions(state, [new SddTestExecutionImportRecord
        {
            TestId = "T1", TestName = "submit", RequirementReferences = ["REQ-1"], ExecutionState = "Completed", Result = "Passed", ResultSource = "ci"
        }]);

        SddLifecycleReviewService.ReconcileRequirements(state, [new SemanticRequirement { Id = "REQ-1", Text = "Submit only once" }]);

        var result = Assert.Single(state.TestExecutions);
        Assert.Equal("Passed", result.Result);
        Assert.Equal("PotentiallyStale", result.Currentness);
        Assert.Contains("historical result is retained", result.CurrentnessReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TraceabilityAndQualityProjectionConsumeTheSamePersistedGraph()
    {
        var requirement = new SemanticRequirement
        {
            Id = "REQ-1", Text = "Register item",
            LinkedAcceptanceScenarios = [new SemanticAcceptanceScenario { Id = "AC-1", Title = "Accepted" }]
        };
        var context = new ReviewContext
        {
            Specification = new SpecificationSemanticModel { Requirements = [requirement] },
            SpecToPlan = new() { ["REQ-1"] = ["PLAN-1"] },
            SpecToTasks = new() { ["REQ-1"] = ["TASK-1"] }
        };
        var contextProvider = new Mock<IReviewContextProvider>();
        contextProvider.Setup(x => x.GetCurrent()).Returns(context);
        var graph = new SddEvidenceGraphService(contextProvider.Object);
        var repository = new WorkspaceArtifactRepository();
        graph.SynchronizeCurrentArtifacts(repository);
        SddEvidenceGraphService.ImportExecutions(repository.SddLifecycle, [new SddTestExecutionImportRecord
        {
            TestId = "TEST-1", RequirementReferences = ["REQ-1"], AcceptanceCriterionReferences = ["AC-1"],
            ExecutionState = "Completed", Result = "Passed", ResultSource = "ci"
        }]);

        var traceability = Assert.Single(graph.Build(repository));
        var findings = graph.QualityFindings(repository);
        Assert.Equal("PLAN-1", Assert.Single(traceability.PlanReferences));
        Assert.Equal("TASK-1", Assert.Single(traceability.TaskReferences));
        Assert.Equal("AC-1", Assert.Single(traceability.DesignedTests).AcceptanceCriterionId);
        Assert.Equal("Passed", Assert.Single(traceability.Executions).Result);
        Assert.DoesNotContain(findings, x => x.Code == "RequirementWithoutCurrentExecutionEvidence");
        Assert.Contains(repository.SddLifecycle.Links, x => x.Relationship == "RequirementVerifiedBy");
        Assert.Contains(repository.SddLifecycle.Links, x => x.Relationship == "AcceptanceCriterionVerifiedBy");
    }
}
