using System.Text.Json;
using BirkNext.Web.Services;
using BirkNext.Web.Models;

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
}
