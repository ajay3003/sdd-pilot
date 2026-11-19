using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using BirkNext.Web.Tests.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// Review decisions (on Project Dashboard since Recommended Workflow was retired): availability, review, approval and staleness are separate states read from one
/// presentation; a decision is explicit and names the exact artifact revisions; Manual Review counts the required gates
/// that apply, and says which they are.
/// </summary>
public sealed class ReviewDecisionSemanticsTests : BunitContext
{
    private static readonly DateTimeOffset Oct7 = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Oct6 = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    internal static WorkflowStepViewModel Gate(string key, string title, string role, WorkflowStepStatus status,
        bool optional = false, string stepType = "Explorer", string[]? requires = null, DateTimeOffset? decidedAt = null) => new()
    {
        Key = key, Title = title, Description = title, Route = key.ToLowerInvariant(), ActionLabel = $"Open {title}", Color = "#2563eb",
        Status = status, IsOptional = optional, RequiresApproval = true, RequiresManualReview = true, StepType = stepType,
        RequiredArtifacts = optional ? [] : [.. requires ?? [role]], ArtifactRoles = [.. requires ?? [role]],
        CanOpen = status is not (WorkflowStepStatus.Locked or WorkflowStepStatus.NotApplicable),
        ReviewState = status is WorkflowStepStatus.Reviewed or WorkflowStepStatus.Approved or WorkflowStepStatus.NeedsAttention ? ReviewState.Reviewed : ReviewState.NotStarted,
        ApprovalState = status switch { WorkflowStepStatus.Approved => ApprovalState.Approved, WorkflowStepStatus.NeedsAttention => ApprovalState.NeedsChanges, _ => ApprovalState.Pending },
        ArtifactReferences = status == WorkflowStepStatus.NotApplicable ? null
            : string.Join("; ", (requires ?? [role]).Select(r => $"{r}: {r}.md @ A1A1A1A1")),
        DecidedAt = status is WorkflowStepStatus.Reviewed or WorkflowStepStatus.Approved or WorkflowStepStatus.NeedsAttention ? decidedAt ?? Oct7 : null,
        PreviousDecision = status == WorkflowStepStatus.Stale ? "Approved" : null,
        PreviousDecisionAt = status == WorkflowStepStatus.Stale ? Oct6 : null,
        PreviousArtifactReferences = status == WorkflowStepStatus.Stale ? $"{role}: {role}.md @ 0B0B0B0B" : null,
        DisabledReason = status switch
        {
            WorkflowStepStatus.NotApplicable => $"Requires {role} artifact",
            WorkflowStepStatus.Locked => "Approve Artifact Traceability first",
            _ => "",
        },
    };

    /// <summary>Person Module with every review state present once.</summary>
    internal static List<WorkflowStepViewModel> MixedSteps() =>
    [
        new() { Key = "LoadSampleProject", Title = "Load project artifacts", Description = "5 project artifact roles are available.", Route = "sample-projects",
                Status = WorkflowStepStatus.Approved, StepType = "ArtifactLoad", RequiresApproval = false, RequiresManualReview = false },
        Gate("ConstitutionExplorer", "Constitution Explorer", "Constitution", WorkflowStepStatus.Approved),
        Gate("SpecificationExplorer", "Specification Explorer", "Specification", WorkflowStepStatus.NeedsAttention),
        Gate("PlanExplorer", "Plan Explorer", "Plan", WorkflowStepStatus.Stale),
        Gate("TaskExplorer", "Task Explorer", "Tasks", WorkflowStepStatus.Reviewed),
        Gate("DataModelExplorer", "Data Model Explorer", "DataModel", WorkflowStepStatus.Available, optional: true),
        Gate("ArtifactTraceability", "Artifact Traceability", "Constitution", WorkflowStepStatus.Available, stepType: "Analysis",
            requires: ["Constitution", "Specification", "Plan", "Tasks"]),
        Gate("ImplementationReview", "Implementation Review", "Specification", WorkflowStepStatus.Locked, stepType: "Analysis",
            requires: ["Specification", "Tasks"]),
    ];

    internal static WorkflowReadiness Readiness(CurrentWorkspaceSnapshot workspace, List<WorkflowStepViewModel> steps)
    {
        var required = steps.Where(s => WorkflowReadinessService.IsReviewGate(s) && !s.IsOptional && s.Status != WorkflowStepStatus.NotApplicable).ToList();
        return new WorkflowReadiness(workspace, null, null, null, null, steps.FirstOrDefault(s => s.IsCurrent), new WorkflowReadinessBreakdown(),
            required.Count, required.Count(s => s.Status == WorkflowStepStatus.Approved), steps, false, "Release is available only after all required review steps are approved.")
        {
            ArtifactLoad = ArtifactLoadState.Done,
        };
    }

    private readonly Mock<IRecommendedWorkflowApiService> _api = new();

    /// <summary>The review decisions as Project Dashboard shows them (moved there from the retired Recommended Workflow page).</summary>
    private IRenderedComponent<BirkNext.Web.Components.ReviewDecisionPanel> RenderPanel(WorkflowReadiness readiness)
    {
        Services.AddSingleton(_api.Object);
        Services.AddSingleton(NullLogger<BirkNext.Web.Components.ReviewDecisionPanel>.Instance);
        return Render<BirkNext.Web.Components.ReviewDecisionPanel>(p => p.Add(x => x.Readiness, readiness));
    }

    private static CurrentWorkspaceSnapshot PersonModule() => WorkspaceSnapshots.AllRoles("Person Module", "person-module");

    // ── One presentation, distinct states ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Presentation_KeepsAvailabilityReviewApprovalAndStalenessApart()
    {
        var labels = MixedSteps().Where(WorkflowReadinessService.IsReviewGate).ToDictionary(s => s.Key, s => ArtifactReviewPresentation.Of(s));

        labels["ConstitutionExplorer"].Label.Should().Be("Approved");
        labels["ConstitutionExplorer"].Summary.Should().Be("Approved on 7 Oct 2026 · current revision");
        labels["SpecificationExplorer"].Label.Should().Be("Reviewed with gaps");
        labels["SpecificationExplorer"].IsDecided.Should().BeTrue("a review with known gaps is a recorded decision");
        labels["PlanExplorer"].Label.Should().Be("Review stale");
        labels["PlanExplorer"].Summary.Should().Be("Review stale · the artifact changed since it was approved on 6 Oct 2026");
        labels["TaskExplorer"].Label.Should().Be("Reviewed");
        labels["TaskExplorer"].Summary.Should().Be("Reviewed on 7 Oct 2026 · awaiting approval", "reviewed is not approved");
        labels["DataModelExplorer"].Label.Should().Be("Ready to review");
        labels["DataModelExplorer"].Summary.Should().Be("Available · Not reviewed", "available is not reviewed");
        labels["ImplementationReview"].Label.Should().Be("Blocked");
        labels.Values.Select(v => v.Label).Should().NotContain("Ready");

        var notApplicable = ArtifactReviewPresentation.Of(Gate("PlanExplorer", "Plan Explorer", "Plan", WorkflowStepStatus.NotApplicable));
        notApplicable.Label.Should().Be("N/A");
        notApplicable.Applies.Should().BeFalse();
        notApplicable.CanDecide.Should().BeFalse();
    }

    [Fact]
    public void LegacyDecision_WithoutARevision_IsShownAsStale_NotCurrent()
    {
        var legacy = Gate("PlanExplorer", "Plan Explorer", "Plan", WorkflowStepStatus.Stale);
        legacy.PreviousArtifactReferences = null;

        ArtifactReviewPresentation.Of(legacy).Summary.Should().Be("Review stale · the earlier decision (approved on 6 Oct 2026) names no artifact revision");
    }

    // ── Review decisions on Project Dashboard ───────────────────────────────────────────────────────────────────────────

    private static string Item(string key) => $"[data-testid=review-decision][data-step={key}]";

    [Fact]
    public void States_AreSaidInText_NeverReadyAsCompletion()
    {
        var cut = RenderPanel(Readiness(PersonModule(), MixedSteps()));

        string State(string key) => cut.Find($"{Item(key)} [data-testid=review-decision-state]").TextContent;
        string Line(string key) => cut.Find($"{Item(key)} [data-testid=review-decision-summary]").TextContent;
        State("ConstitutionExplorer").Should().Be("Approved");
        Line("ConstitutionExplorer").Should().EndWith("Approved on 7 Oct 2026 · current revision");
        State("PlanExplorer").Should().Be("Review stale");
        Line("PlanExplorer").Should().StartWith("⟳", "stale has an icon and text, not colour alone");
        State("DataModelExplorer").Should().Be("Ready to review");
        State("ImplementationReview").Should().Be("Blocked");
        Line("ImplementationReview").Should().EndWith("Approve Artifact Traceability first");
        cut.FindAll("[data-testid=review-decision-state]").Select(b => b.TextContent).Should().NotContain("Ready");
        cut.Markup.Should().NotContain("Approval Invalidated");
    }

    [Fact]
    public void Decision_IsExplicit_AndNamesTheExactRevision()
    {
        var cut = RenderPanel(Readiness(PersonModule(), MixedSteps()));

        var decision = cut.Find($"{Item("DataModelExplorer")} [data-testid=review-decision-record]");
        decision.TagName.Should().Be("DETAILS", "the decision is behind an explicit disclosure");
        decision.HasAttribute("open").Should().BeFalse();
        decision.QuerySelector("summary")!.TextContent.Should().Be("Record review decision");
        decision.QuerySelector("[data-testid=review-decision-target]")!.TextContent.Should().Be("Applies to DataModel: DataModel.md @ A1A1A1A1");
        decision.QuerySelector("[role=group]")!.GetAttribute("aria-labelledby").Should().Be("rdp-target-DataModelExplorer");
        decision.QuerySelectorAll("button").Select(b => b.TextContent.Trim()).Should().Equal("Mark reviewed", "Reviewed with gaps", "Approve");

        // Approved: no second approval; reviewed with gaps: no second one; blocked: no decision at all.
        cut.Find($"{Item("ConstitutionExplorer")} [data-testid=review-decision-record]").QuerySelectorAll("button")
            .Select(b => b.TextContent.Trim()).Should().Equal("Reviewed with gaps");
        cut.Find($"{Item("SpecificationExplorer")} [data-testid=review-decision-record]").QuerySelectorAll("button")
            .Select(b => b.TextContent.Trim()).Should().Equal("Mark reviewed", "Approve");
        cut.Find(Item("ImplementationReview")).QuerySelectorAll("button, details").Should().BeEmpty();
    }

    [Fact]
    public void Approve_SendsTheRevisionsShown_AndOpeningAReviewRecordsNothing()
    {
        var workspace = PersonModule();
        var cut = RenderPanel(Readiness(workspace, MixedSteps()));

        cut.Find($"{Item("DataModelExplorer")} a").GetAttribute("href").Should().Be("datamodelexplorer");
        _api.VerifyNoOtherCalls();

        cut.Find($"{Item("DataModelExplorer")} .rdp-approve").Click();

        _api.Verify(a => a.ApproveStepAsync(workspace.WorkspaceId!.Value, "DataModelExplorer",
            It.Is<IReadOnlyList<ArtifactRevisionRef>>(refs => refs.Count == 5
                && refs.Any(r => r.Role == "DataModel" && r.ArtifactId == "person-module/sample:DataModel.md" && r.Fingerprint == WorkspaceSnapshots.Fingerprint(WorkspaceArtifactType.DataModel))),
            null), Times.Once);
        _api.Verify(a => a.MarkStepReviewedAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<ArtifactRevisionRef>>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public void RefusedDecision_IsShown_NeverSilentlyDropped()
    {
        _api.Setup(a => a.ApproveStepAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<ArtifactRevisionRef>>(), It.IsAny<string?>()))
            .ThrowsAsync(new InvalidOperationException("A decision on Data Model Explorer must name the exact revision of its DataModel artifact."));
        var cut = RenderPanel(Readiness(PersonModule(), MixedSteps()));

        cut.Find($"{Item("DataModelExplorer")} .rdp-approve").Click();

        cut.WaitForElement("[data-testid=review-decisions-error][role=alert]").TextContent.Should().Contain("must name the exact revision");
    }

    // ── Count of required decisions ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Count_IsApplicableRequiredGates_AndEachGateSaysWhetherItIsCounted()
    {
        var steps = MixedSteps();
        steps.Single(s => s.Key == "ArtifactTraceability").Status = WorkflowStepStatus.NotApplicable;
        var cut = RenderPanel(Readiness(PersonModule(), steps));

        // Required and applicable: Constitution, Specification, Plan, Tasks, Implementation Review — not the optional Data Model review, not the
        // not-applicable traceability step. Decided: Constitution (approved), Specification (reviewed with gaps), Tasks (reviewed).
        cut.Find("[data-testid=review-decisions-count]").TextContent.Should().Be("3 / 5 required review decisions recorded · 1 optional not counted");
        var gates = cut.FindAll("[data-testid=review-decision]");
        gates.Select(g => g.GetAttribute("data-step")).Should().Equal(
            "ConstitutionExplorer", "SpecificationExplorer", "PlanExplorer", "TaskExplorer", "DataModelExplorer", "ArtifactTraceability", "ImplementationReview");
        gates.Count(g => g.GetAttribute("data-counted") == "required").Should().Be(5);
        gates.Single(g => g.GetAttribute("data-counted") == "optional").GetAttribute("data-step").Should().Be("DataModelExplorer");
        gates.Single(g => g.GetAttribute("data-counted") == "not-counted").GetAttribute("data-step").Should().Be("ArtifactTraceability");
    }

    [Fact]
    public void Count_FollowsApplicability()
    {
        var four = MixedSteps().Where(s => s.Key != "DataModelExplorer").ToList();
        four.Single(s => s.Key == "PlanExplorer").Status = WorkflowStepStatus.NotApplicable;

        var cut = RenderPanel(Readiness(PersonModule(), four));

        cut.Find("[data-testid=review-decisions-count]").TextContent.Should().Be("3 / 5 required review decisions recorded", "no optional gate applies");
    }

    // ── Revision identity in the current workspace ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void ArtifactIdentity_IsScopedByProject_SoTheSamePathInAnotherProjectIsAnotherArtifact()
    {
        var personModule = WorkspaceSnapshots.AllRoles("Auto", "person-module").ArtifactRevisions.Single(r => r.Role == "Constitution");
        var autorisasjon = WorkspaceSnapshots.AllRoles("Auto", "autorisasjon").ArtifactRevisions.Single(r => r.Role == "Constitution");

        personModule.ArtifactId.Should().Be("person-module/sample:Constitution.md");
        autorisasjon.ArtifactId.Should().Be("autorisasjon/sample:Constitution.md");
        personModule.FileName.Should().Be(autorisasjon.FileName, "the same file name, shown alike");
    }

    [Fact]
    public async Task Projection_GivesEachSelectedArtifactItsContentFingerprint_AndANewRevisionANewOne()
    {
        var workspace = new WorkspaceArtifactRepository();
        workspace.AddArtifactRevision(WorkspaceArtifactType.Constitution, "# Rules v1", "rules.md", null, null, "File", select: true);
        var services = new ServiceCollection();
        DashboardPageTests.AddCurrentWorkspace(services, workspace, new DashboardPageTests.SampleProjectsHttpHandler());
        var projection = services.BuildServiceProvider().GetRequiredService<ICurrentWorkspaceProjection>();

        var first = (await projection.GetAsync()).ArtifactRevisions.Single();
        first.Role.Should().Be("Constitution");
        first.ArtifactId.Should().Be("manual-workspace/workspace:rules.md", "scoped by project: no project selected here");
        first.Fingerprint.Should().Be(ArtifactFingerprint.Compute("# Rules v1"));

        workspace.AddArtifactRevision(WorkspaceArtifactType.Constitution, "# Rules v2", "rules.md", null, null, "File", select: true);
        projection.Invalidate();
        var second = (await projection.GetAsync()).ArtifactRevisions.Single();

        second.ArtifactId.Should().Be(first.ArtifactId, "the same document");
        second.Fingerprint.Should().Be(ArtifactFingerprint.Compute("# Rules v2")).And.NotBe(first.Fingerprint, "a new revision is a new review subject");
    }
}
