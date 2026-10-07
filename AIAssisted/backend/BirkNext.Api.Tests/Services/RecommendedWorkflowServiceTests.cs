using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BirkNext.Api.Tests.Services;

public class RecommendedWorkflowServiceTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly IRecommendedWorkflowService _service;
    private readonly ILogger<RecommendedWorkflowService> _logger;
    private readonly Guid _workspaceId = Guid.NewGuid();

    public RecommendedWorkflowServiceTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _db = new AppDbContext(options);
        var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
        _logger = loggerFactory.CreateLogger<RecommendedWorkflowService>();
        _service = new RecommendedWorkflowService(_db, _logger);

        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db?.Dispose();
    }

    // ── Artifact revisions the client reads ─────────────────────────────────────────────────────────────────────────────

    private static ArtifactRevisionRef Ref(string role, string fingerprint = "A1A1A1A1A1", string? id = null) => new()
    {
        Role = role, ArtifactId = id ?? $"sample:{role.ToLowerInvariant()}.md", Fingerprint = fingerprint, FileName = $"{role.ToLowerInvariant()}.md",
    };

    /// <summary>The selected revision of every role, with one role's revision replaced where given.</summary>
    private static List<ArtifactRevisionRef> Revisions(params ArtifactRevisionRef[] overrides)
    {
        var all = new[] { "Constitution", "Specification", "Plan", "Tasks", "DataModel" }.Select(r => Ref(r)).ToList();
        foreach (var o in overrides) all[all.FindIndex(r => r.Role == o.Role)] = o;
        return all;
    }

    private static readonly List<ArtifactRevisionRef> Current = Revisions();

    private Task<List<WorkflowStepViewModel>> BuildAsync(
        bool constitution = true, bool specification = true, bool plan = true, bool tasks = true, bool dataModel = false,
        IReadOnlyCollection<ArtifactRevisionRef>? artifacts = null) =>
        _service.BuildWorkflowStepsAsync(_workspaceId, constitution, specification, plan, tasks, dataModel, artifacts ?? Current);

    private static WorkflowStepViewModel Step(IEnumerable<WorkflowStepViewModel> steps, string key) => steps.Single(s => s.Key == key);

    // Test 1: Loaded artifact creates Available step, not Approved
    [Fact]
    public async Task BuildWorkflowSteps_WithLoadedArtifacts_CreatesAvailableNotApproved()
    {
        var steps = await BuildAsync(plan: false, tasks: false);

        var specReview = Step(steps, "SpecificationExplorer");
        Assert.Equal(WorkflowStepStatus.Available, specReview.Status);
        Assert.NotEqual(WorkflowStepStatus.Approved, specReview.Status);
        Assert.Contains("specification.md @ A1A1A1A1", specReview.ArtifactReferences);
    }

    // Test 2: Step becomes Reviewed only after Mark Reviewed, and reviewed is not approved
    [Fact]
    public async Task MarkStepReviewed_ChangesReviewState_WithoutApproving()
    {
        await _service.ApproveStepAsync(_workspaceId, "ConstitutionExplorer", Current);
        await _service.MarkStepInProgressAsync(_workspaceId, "SpecificationExplorer");

        await _service.MarkStepReviewedAsync(_workspaceId, "SpecificationExplorer", Current);

        var progress = await _service.GetReviewProgressAsync(_workspaceId, "SpecificationExplorer");
        Assert.NotNull(progress);
        Assert.Equal(ReviewState.Reviewed, progress.ReviewState);
        Assert.Equal(ApprovalState.Pending, progress.ApprovalState);
        var step = Step(await BuildAsync(), "SpecificationExplorer");
        Assert.Equal(WorkflowStepStatus.Reviewed, step.Status);
        Assert.True(step.IsCurrent, "a reviewed step still needs its approval");
    }

    // Test 3: Step becomes Approved only after Approve
    [Fact]
    public async Task ApproveStep_SetsApprovedState()
    {
        await _service.MarkStepReviewedAsync(_workspaceId, "SpecificationExplorer", Current);

        await _service.ApproveStepAsync(_workspaceId, "SpecificationExplorer", Current);

        var progress = await _service.GetReviewProgressAsync(_workspaceId, "SpecificationExplorer");
        Assert.NotNull(progress);
        Assert.Equal(ApprovalState.Approved, progress.ApprovalState);
    }

    // Test 4: Approved step persists after workspace reload (same revision: still current)
    [Fact]
    public async Task ApprovedStep_PersistedInDatabase_StaysCurrentForTheSameRevision()
    {
        await _service.ApproveStepAsync(_workspaceId, "SpecificationExplorer", Current, comment: "Test approval");

        var steps = await BuildAsync(plan: false, tasks: false);

        var specReview = Step(steps, "SpecificationExplorer");
        Assert.Equal(WorkflowStepStatus.Approved, specReview.Status);
        Assert.NotNull(specReview.DecidedAt);
        Assert.Null(specReview.PreviousDecision);
    }

    // Test 5: Artifact content change invalidates dependent approval (explicit invalidation API)
    [Fact]
    public async Task InvalidateApprovalsAsync_InvalidatesDependentSteps()
    {
        await _service.ApproveStepAsync(_workspaceId, "SpecificationExplorer", Current);

        await _service.InvalidateArtifactDependentApprovalsAsync(_workspaceId, new List<string> { "Specification" }, "hash_xyz789");

        var progress = await _service.GetReviewProgressAsync(_workspaceId, "SpecificationExplorer");
        Assert.NotNull(progress);
        Assert.Equal(ApprovalState.InvalidatedByArtifactChange, progress.ApprovalState);
        Assert.Equal(WorkflowStepStatus.Stale, Step(await BuildAsync(), "SpecificationExplorer").Status);
    }

    // Test 6: Artifact Traceability is available once required artifacts are loaded
    [Fact]
    public async Task BuildWorkflowSteps_TraceabilityAvailableWhenArtifactsLoaded()
    {
        var traceability = Step(await BuildAsync(), "ArtifactTraceability");
        Assert.Equal(WorkflowStepStatus.Available, traceability.Status);
    }

    // Test 7: Implementation Review locked until Artifact Traceability approved on its current revisions
    [Fact]
    public async Task BuildWorkflowSteps_LocksImplementationReviewUntilTraceabilityApproved()
    {
        var implReview = Step(await BuildAsync(), "ImplementationReview");
        Assert.Equal(WorkflowStepStatus.Locked, implReview.Status);
        Assert.Equal("Approve Artifact Traceability first", implReview.DisabledReason);

        await _service.ApproveStepAsync(_workspaceId, "ArtifactTraceability", Current);

        Assert.Equal(WorkflowStepStatus.Available, Step(await BuildAsync(), "ImplementationReview").Status);

        // A new Plan revision makes the traceability approval stale, so Implementation Review is blocked again.
        var changedPlan = Revisions(Ref("Plan", "B2B2B2B2B2"));
        var steps = await BuildAsync(artifacts: changedPlan);
        Assert.Equal(WorkflowStepStatus.Stale, Step(steps, "ArtifactTraceability").Status);
        Assert.Equal(WorkflowStepStatus.Locked, Step(steps, "ImplementationReview").Status);
    }

    // Test 8: Reject marks step as NeedsChanges, and it stays the current step
    [Fact]
    public async Task RejectStep_SetsNeedsChangesState_AndStaysCurrent()
    {
        await _service.RejectStepAsync(_workspaceId, "ConstitutionExplorer", Current, comment: "Needs revision");

        var progress = await _service.GetReviewProgressAsync(_workspaceId, "ConstitutionExplorer");
        Assert.NotNull(progress);
        Assert.Equal(ApprovalState.NeedsChanges, progress.ApprovalState);
        var steps = await BuildAsync();
        Assert.Equal(WorkflowStepStatus.NeedsAttention, Step(steps, "ConstitutionExplorer").Status);
        Assert.Equal("ConstitutionExplorer", _service.GetCurrentRecommendedStep(steps)?.Key);
    }

    // Test 9: GetCurrentRecommendedStep returns the first open review step, and moves on after an approval
    [Fact]
    public async Task GetCurrentRecommendedStep_MovesToTheNextApplicableStepAfterApproval()
    {
        var steps = await BuildAsync();
        Assert.Equal("ConstitutionExplorer", _service.GetCurrentRecommendedStep(steps)?.Key);

        await _service.ApproveStepAsync(_workspaceId, "ConstitutionExplorer", Current);

        steps = await BuildAsync();
        var current = _service.GetCurrentRecommendedStep(steps);
        Assert.Equal("SpecificationExplorer", current?.Key);
        Assert.True(current!.IsCurrent);
    }

    // Test 10: Mark InProgress updates LastOpenedAt and is not a decision
    [Fact]
    public async Task MarkStepInProgress_UpdatesLastOpenedAt_WithoutReviewing()
    {
        await _service.MarkStepInProgressAsync(_workspaceId, "SpecificationExplorer");

        var progress = await _service.GetReviewProgressAsync(_workspaceId, "SpecificationExplorer");
        Assert.NotNull(progress);
        Assert.NotNull(progress.LastOpenedAt);
        Assert.True(progress.LastOpenedAt > DateTimeOffset.UtcNow.AddSeconds(-5));
        Assert.Equal(WorkflowStepStatus.Available, Step(await BuildAsync(), "SpecificationExplorer").Status);
    }

    // Test 11: Approvals not invalidated if hash matches
    [Fact]
    public async Task InvalidateApprovalsAsync_DoesNotInvalidateIfHashMatches()
    {
        await _service.ApproveStepAsync(_workspaceId, "SpecificationExplorer", Current);
        var hash = (await _service.GetReviewProgressAsync(_workspaceId, "SpecificationExplorer"))!.ArtifactSetHashAtApproval!;

        await _service.InvalidateArtifactDependentApprovalsAsync(_workspaceId, new List<string> { "Specification" }, hash);

        var progress = await _service.GetReviewProgressAsync(_workspaceId, "SpecificationExplorer");
        Assert.Equal(ApprovalState.Approved, progress!.ApprovalState);
    }

    // Test 12: Multiple workspaces (projects) have independent state
    [Fact]
    public async Task MultipleWorkspaces_HaveIndependentState()
    {
        var workspace2 = Guid.NewGuid();

        await _service.ApproveStepAsync(_workspaceId, "SpecificationExplorer", Current);
        await _service.MarkStepInProgressAsync(workspace2, "SpecificationExplorer");

        var progress1 = await _service.GetReviewProgressAsync(_workspaceId, "SpecificationExplorer");
        var progress2 = await _service.GetReviewProgressAsync(workspace2, "SpecificationExplorer");

        Assert.Equal(ApprovalState.Approved, progress1!.ApprovalState);
        Assert.Equal(ReviewState.InProgress, progress2!.ReviewState);
        Assert.Equal(ApprovalState.Pending, progress2.ApprovalState);

        var other = await _service.BuildWorkflowStepsAsync(workspace2, true, true, true, true, false, Current);
        Assert.Equal(WorkflowStepStatus.Available, Step(other, "SpecificationExplorer").Status);
    }

    // Test 13: Loaded artifact changes don't affect other artifacts' approvals
    [Fact]
    public async Task InvalidateApprovalsAsync_OnlyInvalidatesDependentSteps()
    {
        await _service.ApproveStepAsync(_workspaceId, "SpecificationExplorer", Current);
        await _service.ApproveStepAsync(_workspaceId, "PlanExplorer", Current);

        await _service.InvalidateArtifactDependentApprovalsAsync(_workspaceId, new List<string> { "Specification" }, "new_hash");

        var specProgress = await _service.GetReviewProgressAsync(_workspaceId, "SpecificationExplorer");
        var planProgress = await _service.GetReviewProgressAsync(_workspaceId, "PlanExplorer");

        Assert.Equal(ApprovalState.InvalidatedByArtifactChange, specProgress!.ApprovalState);
        Assert.Equal(ApprovalState.Approved, planProgress!.ApprovalState);
    }

    // Test 14: Data Model step appears only when the data-model artifact exists, as an optional review
    [Fact]
    public async Task BuildWorkflowSteps_DataModelStepOnlyAppearsWhenArtifactExists()
    {
        Assert.DoesNotContain(await BuildAsync(dataModel: false), s => s.Key == "DataModelExplorer");

        var dataModel = Step(await BuildAsync(dataModel: true), "DataModelExplorer");
        Assert.True(dataModel.IsOptional);
        Assert.Equal(new List<string> { "DataModel" }, dataModel.ArtifactRoles);
        Assert.Equal(WorkflowStepStatus.Available, dataModel.Status);
    }

    // Test 15: Readiness calculation reflects approval progress
    [Fact]
    public async Task CalculateWorkflowReadiness_IncreaseWithApprovals()
    {
        var readinessInitial = _service.CalculateWorkflowReadiness(await BuildAsync());

        await _service.ApproveStepAsync(_workspaceId, "SpecificationExplorer", Current);
        var readinessAfterApproval = _service.CalculateWorkflowReadiness(await BuildAsync());

        Assert.True(readinessAfterApproval > readinessInitial);
    }

    // Test 16: Readiness breakdown shows detailed metrics
    [Fact]
    public async Task GetReadinessBreakdown_ReturnsDetailedMetrics()
    {
        var breakdown = _service.GetReadinessBreakdown(await BuildAsync());

        Assert.True(breakdown.OverallReadiness >= 0 && breakdown.OverallReadiness <= 100);
        Assert.True(breakdown.ArtifactReadiness >= 0 && breakdown.ArtifactReadiness <= 100);
        Assert.True(breakdown.ReviewReadiness >= 0 && breakdown.ReviewReadiness <= 100);
        Assert.True(breakdown.ApprovalReadiness >= 0 && breakdown.ApprovalReadiness <= 100);
    }

    // Test 17: Ready for release when every applicable required step is approved on its current revisions
    [Fact]
    public async Task GetReadinessBreakdown_ReadyForReleaseWhenAllApproved()
    {
        foreach (var stepKey in new[] { "ConstitutionExplorer", "SpecificationExplorer", "PlanExplorer", "TaskExplorer", "ArtifactTraceability", "ImplementationReview" })
            await _service.ApproveStepAsync(_workspaceId, stepKey, Current);

        var breakdown = _service.GetReadinessBreakdown(await BuildAsync(dataModel: true));

        Assert.Equal(6, breakdown.StepsRequiringApproval);
        Assert.Equal(6, breakdown.StepsApproved);
        Assert.True(breakdown.ReadyForRelease, "the optional Data Model review does not block release");
    }

    // Test 18: Non-approval steps are skipped in readiness calculation
    [Fact]
    public async Task GetReadinessBreakdown_IgnoresNonApprovalSteps()
    {
        var steps = await BuildAsync(plan: false, tasks: false);

        var dashboard = Step(steps, "Dashboard");
        Assert.False(dashboard.RequiresApproval);
        Assert.False(dashboard.RequiresManualReview);
        Assert.DoesNotContain(steps, s => s.Key == "ReviewContextValidation");

        var breakdown = _service.GetReadinessBreakdown(steps);
        Assert.Equal(0, breakdown.StepsApproved);
        Assert.DoesNotContain(steps.Where(s => s.RequiresApproval), s => s.Key == "Dashboard");
    }

    // Test 19: Five Explorers present in workflow
    [Fact]
    public async Task BuildWorkflowSteps_ContainsFiveExplorerSteps()
    {
        var explorers = (await BuildAsync(dataModel: true)).Where(s => s.Key.Contains("Explorer")).ToList();

        Assert.Equal(5, explorers.Count);
        Assert.All(explorers, e => Assert.Equal("Explorer", e.StepType));
        Assert.Equal(
            new[] { "ConstitutionExplorer", "SpecificationExplorer", "PlanExplorer", "TaskExplorer", "DataModelExplorer" },
            explorers.Select(e => e.Key));
    }

    // Test 20: Specification Review is retired from the workflow
    [Fact]
    public async Task BuildWorkflowSteps_DoesNotContainSpecificationReview()
    {
        var steps = await BuildAsync(plan: false, tasks: false);

        Assert.Contains(steps, s => s.Key == "SpecificationExplorer" && s.Route == "specification-explorer");
        Assert.DoesNotContain(steps, s => s.Key == "SpecificationReview");
    }

    // Test 21: Artifact Traceability no longer depends on retired Specification Review approval
    [Fact]
    public async Task BuildWorkflowSteps_ArtifactTraceabilityAvailableWithoutSpecificationReviewApproval()
    {
        Assert.NotEqual(WorkflowStepStatus.Locked, Step(await BuildAsync(), "ArtifactTraceability").Status);
    }

    // Test 22: Workflow order is sequential with correct numbering
    [Fact]
    public async Task BuildWorkflowSteps_SequentialNumberingWithNoGaps()
    {
        var steps = await BuildAsync(dataModel: true);

        var expectedOrder = new[]
        {
            "LoadSampleProject", "ConstitutionExplorer", "SpecificationExplorer", "PlanExplorer", "TaskExplorer",
            "DataModelExplorer", "ArtifactTraceability", "ImplementationReview", "Dashboard"
        };
        Assert.Equal(expectedOrder, steps.Select(s => s.Key));
        for (int i = 0; i < steps.Count; i++)
            Assert.Equal(i + 1, steps[i].Number);
    }

    // Test 23: SpecificationExplorer appears before Artifact Traceability
    [Fact]
    public async Task BuildWorkflowSteps_SpecificationExplorerBeforeArtifactTraceability()
    {
        var steps = await BuildAsync(dataModel: true);

        Assert.True(steps.FindIndex(s => s.Key == "SpecificationExplorer") < steps.FindIndex(s => s.Key == "ArtifactTraceability"));
    }

    // Test 24: A missing required artifact makes the step not applicable (not counted, not recommended), not a failure
    [Fact]
    public async Task BuildWorkflowSteps_StepIsNotApplicableWhenItsRequiredArtifactIsAbsent()
    {
        var steps = await BuildAsync(specification: false, dataModel: true);

        var specExplorer = Step(steps, "SpecificationExplorer");
        Assert.Equal(WorkflowStepStatus.NotApplicable, specExplorer.Status);
        Assert.Equal("Requires Specification", specExplorer.DisabledReason);
        Assert.False(specExplorer.IsCurrent);
        Assert.Equal(WorkflowStepStatus.NotApplicable, Step(steps, "ArtifactTraceability").Status);
        Assert.Equal(WorkflowStepStatus.NotApplicable, Step(steps, "ImplementationReview").Status);

        var breakdown = _service.GetReadinessBreakdown(steps);
        Assert.Equal(3, breakdown.StepsRequiringApproval); // Constitution, Plan, Tasks — not the three that need a Specification
    }

    // ── Decisions are bound to exact artifact revisions ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Approval_IsStoredAgainstTheExactArtifactRevision()
    {
        await _service.ApproveStepAsync(_workspaceId, "ConstitutionExplorer", Current);

        var progress = await _service.GetReviewProgressAsync(_workspaceId, "ConstitutionExplorer");
        Assert.NotNull(progress!.ArtifactSetHash);
        Assert.NotNull(progress.ArtifactIdentityHash);
        Assert.Equal(progress.ArtifactSetHash, progress.ArtifactSetHashAtApproval);
        Assert.Equal("Constitution: constitution.md @ A1A1A1A1", progress.ArtifactReferences);
        Assert.Equal("Local Developer", progress.ApprovedBy);
        Assert.NotNull(progress.ApprovedAt);
    }

    [Fact]
    public async Task NewRevision_DoesNotInheritTheApproval_WhichStaysAsHistory()
    {
        await _service.ApproveStepAsync(_workspaceId, "ConstitutionExplorer", Current);

        var revisionB = Revisions(Ref("Constitution", "B2B2B2B2B2"));
        var steps = await BuildAsync(artifacts: revisionB);

        var constitution = Step(steps, "ConstitutionExplorer");
        Assert.Equal(WorkflowStepStatus.Stale, constitution.Status);
        Assert.Equal(ApprovalState.Pending, constitution.ApprovalState);
        Assert.Equal("Approved", constitution.PreviousDecision);
        Assert.Contains("@ A1A1A1A1", constitution.PreviousArtifactReferences);
        Assert.Contains("@ B2B2B2B2", constitution.ArtifactReferences);
        Assert.True(constitution.IsCurrent, "a stale review is recommended again");

        // Revision A's approval is preserved; approving B adds a decision rather than rewriting A's.
        await _service.ApproveStepAsync(_workspaceId, "ConstitutionExplorer", revisionB);
        var rows = await _service.GetWorkspaceReviewProgressAsync(_workspaceId);
        Assert.Equal(2, rows.Count(r => r.StepKey == "ConstitutionExplorer" && r.ApprovalState == ApprovalState.Approved));
        Assert.Equal(WorkflowStepStatus.Approved, Step(await BuildAsync(artifacts: revisionB), "ConstitutionExplorer").Status);
        Assert.Equal(WorkflowStepStatus.Approved, Step(await BuildAsync(), "ConstitutionExplorer").Status);
    }

    [Fact]
    public async Task AnotherArtifactOfTheRole_DoesNotInheritTheDecision_AndIsNotStale()
    {
        await _service.ApproveStepAsync(_workspaceId, "SpecificationExplorer", Current);

        var otherSpecification = Revisions(Ref("Specification", "C3C3C3C3C3", id: "sample:specs/other/spec.md"));
        var specification = Step(await BuildAsync(artifacts: otherSpecification), "SpecificationExplorer");

        Assert.Equal(WorkflowStepStatus.Available, specification.Status);
        Assert.Null(specification.PreviousDecision);
    }

    [Fact]
    public async Task UnselectedRole_BlocksItsReviewUntilAnArtifactIsChosen()
    {
        var noSpecificationChosen = Current.Where(r => r.Role != "Specification").ToList();

        var steps = await BuildAsync(artifacts: noSpecificationChosen);

        var specification = Step(steps, "SpecificationExplorer");
        Assert.Equal(WorkflowStepStatus.Locked, specification.Status);
        Assert.Equal("Choose the Specification artifact to review first", specification.DisabledReason);
        Assert.Equal(WorkflowStepStatus.Locked, Step(steps, "ArtifactTraceability").Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.ApproveStepAsync(_workspaceId, "SpecificationExplorer", noSpecificationChosen));
    }

    [Fact]
    public async Task Decision_WithoutAnArtifactRevision_IsRefused()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.ApproveStepAsync(_workspaceId, "ConstitutionExplorer", Array.Empty<ArtifactRevisionRef>()));

        Assert.Contains("must name the exact revision of its Constitution artifact", error.Message);
        Assert.Empty(await _service.GetWorkspaceReviewProgressAsync(_workspaceId));
    }

    [Fact]
    public async Task LegacyRoleLevelDecision_IsNeverCurrent()
    {
        _db.WorkspaceReviewProgress.Add(new WorkspaceReviewProgress
        {
            Id = Guid.NewGuid(), WorkspaceId = _workspaceId, StepKey = "PlanExplorer",
            ReviewState = ReviewState.Reviewed, ApprovalState = ApprovalState.Approved, ApprovedAt = DateTimeOffset.UtcNow,
        });
        await _db.SaveChangesAsync();

        var plan = Step(await BuildAsync(), "PlanExplorer");

        Assert.Equal(WorkflowStepStatus.Stale, plan.Status);
        Assert.Equal("Approved", plan.PreviousDecision);
    }

    [Fact]
    public async Task MultiArtifactStep_IsBoundToEveryArtifactItReads()
    {
        await _service.ApproveStepAsync(_workspaceId, "ArtifactTraceability", Current);

        var progress = await _service.GetReviewProgressAsync(_workspaceId, "ArtifactTraceability");
        Assert.Contains("Constitution: constitution.md", progress!.ArtifactReferences);
        Assert.Contains("Tasks: tasks.md", progress.ArtifactReferences);

        // A Data Model change does not touch traceability: it does not read the Data Model.
        var otherDataModel = Revisions(Ref("DataModel", "D4D4D4D4D4"));
        Assert.Equal(WorkflowStepStatus.Approved, Step(await BuildAsync(dataModel: true, artifacts: otherDataModel), "ArtifactTraceability").Status);
    }
}
