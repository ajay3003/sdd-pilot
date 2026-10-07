using BirkNext.Applicability;
using BirkNext.Technology;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;
using DetectionConfidence = BirkNext.Applicability.DetectionConfidence;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// Recommended Workflow over the three project inputs — documents, source, target — with the real applicability evaluator.
/// Any input, or any combination, can progress; none is required for every project, and input completeness is never a score.
/// </summary>
public sealed class ProjectInputWorkflowTests
{
    private static readonly WorkspaceArtifactType[] AllRoles = [.. CurrentWorkspaceSnapshot.WorkflowRoles];

    // ── Fixture ──────────────────────────────────────────────────────────────────────────────────────────────────────────

    private CurrentWorkspaceSnapshot _workspace = CurrentWorkspaceSnapshot.None();
    private FrontendAnalysisProfile? _environment;
    private ProjectTechnologyCoverage? _coverage;
    private List<WorkflowStepViewModel> _steps = [];
    private readonly Mock<IRecommendedWorkflowApiService> _workflowApi = new();

    private void Documents(params WorkspaceArtifactType[] roles) => _workspace = WorkspaceSnapshots.Loaded("Person Module", "person-module", "Person Module", roles);

    private void Environment(string? url) => _environment = new FrontendAnalysisProfile { Id = "env-dev", Name = "DEV", TargetUrl = url ?? "" };

    private void Source(bool outdatedCiCd = false, bool predatesInventory = false, params (string Technology, Capability? Capability)[] detected)
    {
        _environment ??= new FrontendAnalysisProfile { Id = "env-dev", Name = "DEV", TargetUrl = "" };
        _coverage = new ProjectTechnologyCoverage
        {
            EnvironmentId = "env-dev", SourceSnapshotId = Guid.NewGuid(), SourceArchive = "M2LB_2_.zip", AnalyzedAt = DateTimeOffset.UtcNow,
            CiCdEvidenceOutdated = outdatedCiCd,
            Source = predatesInventory ? null : new SourceTechnologyCoverage
            {
                Technologies = detected.Select(d => new DetectedTechnology(d.Technology, d.Technology, TechnologySupportRegistry.Find(d.Technology)?.Area ?? TechnologyArea.Language, DetectionConfidence.Confirmed, 3, [])).ToList(),
                Capabilities = detected.Where(d => d.Capability is not null)
                    .Select(d => new CapabilityEvidence(d.Capability!.Value, DetectionConfidence.Confirmed, "test", d.Technology)).ToList(),
            },
        };
    }

    private void NoSourceYet() => _coverage = new ProjectTechnologyCoverage { EnvironmentId = _environment!.Id };

    private async Task<WorkflowReadiness> ReadinessAsync()
    {
        var projection = WorkspaceSnapshots.Projection(_workspace);
        var contexts = new Mock<IFrontendAnalysisContextFactory>();
        contexts.Setup(c => c.GetActiveContextAsync()).ReturnsAsync(() => _environment is null
            ? new FrontendAnalysisContext { ActiveTargetError = "No active Target Environment" }
            : new FrontendAnalysisContext { ActiveProfile = _environment, TargetUrl = _environment.TargetUrl });
        var api = new Mock<ITechnologyCoverageApiService>();
        api.Setup(a => a.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(() => _coverage);
        var applicability = new ProjectApplicabilityState(api.Object, contexts.Object, projection.Object);
        _workflowApi.Setup(w => w.BuildWorkflowStepsAsync(It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<bool>()))
            .ReturnsAsync(() => _steps);
        var service = new WorkflowReadinessService(projection.Object, applicability, _workflowApi.Object, NullLogger<WorkflowReadinessService>.Instance);
        return await service.GetReadinessAsync();
    }

    private static WorkflowStepViewModel Step(string key, WorkflowStepStatus status, bool current = false) => new()
    {
        Number = 1, Key = key, Title = key, Description = key, Route = key, ActionLabel = key, Color = "#2563eb",
        Status = status, CanOpen = status != WorkflowStepStatus.Locked, IsCurrent = current,
        RequiresApproval = true, RequiresManualReview = true,
        ApprovalState = status == WorkflowStepStatus.Approved ? ApprovalState.Approved : ApprovalState.Pending,
        ReviewState = status == WorkflowStepStatus.Approved ? ReviewState.Reviewed : ReviewState.NotStarted,
        Prerequisites = PrerequisiteState.Available,
    };

    private void DocumentStepsOpen() => _steps = [Step("SpecificationExplorer", WorkflowStepStatus.Available, current: true)];
    private void DocumentStepsApproved() => _steps = [Step("SpecificationExplorer", WorkflowStepStatus.Approved)];

    private static (string, Capability?) Nuget => ("dependency.nuget", Capability.PackageInventory);
    private static (string, Capability?) AzurePipelines => ("pipeline.azuredevops", Capability.Pipeline);

    private void VerifyNoDocumentSteps() =>
        _workflowApi.Verify(w => w.BuildWorkflowStepsAsync(It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<bool>()), Times.Never);

    // ── Scenarios ────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EmptyStart_RecommendsASampleProject_AndOffersSourceAndTargetAsAlternatives()
    {
        var r = await ReadinessAsync();

        r.IsOnboarding.Should().BeTrue();
        r.Inputs.All.Should().OnlyContain(i => i.Status == ProjectInputStatus.Absent);
        r.NextRecommendedAction!.Title.Should().Be("Give BirkNext project context");
        r.NextRecommendedAction.Route.Should().Be("sample-projects");
        r.NextRecommendedAction.ActionLabel.Should().Be("Choose Sample Project");
        r.NextRecommendedAction.Description.Should().Contain("quickest").And.Contain("No input is required for every project");
        r.AlternativeActions.Select(a => a.Route).Should().Equal("source-analysis", NavigationCatalog.TargetEnvironmentsRoute);
        r.Inputs.Documents.Requirement.Should().Be("Recommended start");
        r.Inputs.Source.Requirement.Should().StartWith("Optional");
        r.Inputs.Target.Requirement.Should().Be("Required for runtime reviews");
        r.ReleaseReadinessPercent.Should().BeNull("nothing is assessed: no 0%");
        VerifyNoDocumentSteps();
    }

    [Fact]
    public async Task SampleOnly_DocumentsAvailable_ProgressWithoutSourceOrTarget()
    {
        Documents(AllRoles);
        DocumentStepsOpen();

        var r = await ReadinessAsync();

        r.IsOnboarding.Should().BeFalse();
        r.Inputs.Documents.Status.Should().Be(ProjectInputStatus.Ready);
        r.Inputs.Documents.Facts.Should().Equal("Constitution", "Specification", "Plan", "Task", "Data Model");
        r.Inputs.Source.Status.Should().Be(ProjectInputStatus.Absent);
        r.Inputs.Target.Status.Should().Be(ProjectInputStatus.Absent);
        r.NextRecommendedAction!.Key.Should().Be("SpecificationExplorer");
        r.NextRecommendedAction.Title.Should().NotBe("Load project artifacts").And.NotBe("Give BirkNext project context");
        r.AlternativeActions.Should().BeEmpty("source and target are optional, not blockers");
    }

    [Fact]
    public async Task DocumentOnlyProject_AfterDocumentSteps_RecommendsQualityReview_NeverSourceOrTarget()
    {
        Documents(AllRoles);
        DocumentStepsApproved();

        var r = await ReadinessAsync();

        r.NextRecommendedAction!.Key.Should().Be(WorkflowReadinessService.OpenReviewKeyPrefix + "quality-review");
        r.ApplicableReviews.Select(x => x.ReviewId).Should().Contain("quality-review");
        r.ApplicableReviews.Should().NotContain(x => x.Lane == "Source Review", "no source: source reviews do not apply yet");
        r.NextRecommendedAction.Route.Should().NotBe("source-analysis").And.NotBe(NavigationCatalog.TargetEnvironmentsRoute);
    }

    [Fact]
    public async Task SourceOnly_RecommendsASourceReview_AndSampleProjectsIsOnlyAnOption()
    {
        Source(detected: [Nuget, AzurePipelines]);

        var r = await ReadinessAsync();

        r.IsOnboarding.Should().BeFalse();
        r.Inputs.Source.Status.Should().Be(ProjectInputStatus.Ready);
        r.Inputs.Source.Facts.Should().Contain("M2LB_2_.zip");
        r.Inputs.Documents.Status.Should().Be(ProjectInputStatus.Absent);
        r.Inputs.Target.Status.Should().Be(ProjectInputStatus.Partial, "an environment without an application URL holds the source");
        r.NextRecommendedAction!.Key.Should().Be(WorkflowReadinessService.OpenReviewKeyPrefix + "dependency-review");
        r.ApplicableReviews.Select(x => (x.ReviewId, x.Lane)).Should().Contain([("dependency-review", "Source Review"), ("pipeline-review", "Source Review")]);
        r.AlternativeActions.Should().BeEmpty();
        VerifyNoDocumentSteps();
    }

    [Fact]
    public async Task TargetOnly_RuntimeReviewsBecomeAvailable_WithoutDocumentsOrSource()
    {
        Environment("https://m2lbdev.bufetat.no");
        NoSourceYet();

        var r = await ReadinessAsync();

        r.Inputs.Target.Status.Should().Be(ProjectInputStatus.Ready);
        r.Inputs.Target.Facts.Should().Equal("DEV", "m2lbdev.bufetat.no");
        r.Inputs.Source.Status.Should().Be(ProjectInputStatus.Absent);
        r.Inputs.Documents.Status.Should().Be(ProjectInputStatus.Absent);
        r.NextRecommendedAction!.Key.Should().StartWith(WorkflowReadinessService.OpenReviewKeyPrefix);
        r.ApplicableReviews.Should().NotBeEmpty().And.OnlyContain(x => x.Lane == "Quality & Testing");
        r.ApplicableReviews.Select(x => x.ReviewId).Should().Contain("api-quality-review");
        VerifyNoDocumentSteps();
    }

    [Fact]
    public async Task DocumentsAndSource_DocumentStepFirst_SourceReviewsListed_TargetOptional()
    {
        Documents(AllRoles);
        DocumentStepsOpen();
        Source(detected: [Nuget]);

        var r = await ReadinessAsync();

        r.NextRecommendedAction!.Key.Should().Be("SpecificationExplorer");
        r.ApplicableReviews.Select(x => x.ReviewId).Should().Contain(["quality-review", "dependency-review"]);
        r.Inputs.Target.Status.Should().Be(ProjectInputStatus.Partial);
    }

    [Fact]
    public async Task DocumentsAndTarget_DocumentAndRuntimeReviews_WithoutSource()
    {
        Documents(AllRoles);
        DocumentStepsApproved();
        Environment("https://m2lbdev.bufetat.no");
        NoSourceYet();

        var r = await ReadinessAsync();

        r.ApplicableReviews.Select(x => x.Lane).Distinct().Should().BeEquivalentTo(["Quality & Testing"]);
        r.ApplicableReviews.Select(x => x.ReviewId).Should().Contain(["quality-review", "api-quality-review"]);
        r.NextRecommendedAction!.Key.Should().Be(WorkflowReadinessService.OpenReviewKeyPrefix + "quality-review", "sidebar order: Quality Review first");
    }

    [Fact]
    public async Task SourceAndTarget_SourceAndRuntimeReviews_WithoutDocuments()
    {
        Environment("https://m2lbdev.bufetat.no");
        Source(detected: [Nuget, AzurePipelines]);

        var r = await ReadinessAsync();

        r.ApplicableReviews.Select(x => x.Lane).Distinct().Should().BeEquivalentTo(["Source Review", "Quality & Testing"]);
        r.ApplicableReviews.Should().NotContain(x => x.ReviewId == "quality-review", "no documents: no requirements to review");
        r.NextRecommendedAction!.Key.Should().Be(WorkflowReadinessService.OpenReviewKeyPrefix + "dependency-review");
    }

    [Fact]
    public async Task AllThree_NextActionComesFromOpenReviewWork_NotFromInputSetup()
    {
        Documents(AllRoles);
        DocumentStepsOpen();
        Environment("https://m2lbdev.bufetat.no");
        Source(detected: [Nuget]);

        var r = await ReadinessAsync();
        r.NextRecommendedAction!.Key.Should().Be("SpecificationExplorer");

        DocumentStepsApproved();
        r = await ReadinessAsync();
        r.NextRecommendedAction!.Key.Should().Be(WorkflowReadinessService.OpenReviewKeyPrefix + "dependency-review",
            "documents done: the first applicable review in sidebar order (source review before runtime testing)");
        r.Inputs.All.Should().OnlyContain(i => i.Status == ProjectInputStatus.Ready);
    }

    [Fact]
    public async Task MultipleSpecifications_DocumentsNeedASelection_NotFiveCanonicalFiles()
    {
        var workspace = WorkspaceSnapshots.Loaded("Docs", "docs", "Docs", WorkspaceArtifactType.Specification);
        _workspace = workspace with { Roles = workspace.Roles.Select(r => r.Role == WorkspaceArtifactType.Specification ? WorkspaceSnapshots.Available(r.Role, 3) : r).ToList() };

        var r = await ReadinessAsync();

        r.Inputs.Documents.Status.Should().Be(ProjectInputStatus.NeedsAttention);
        r.Inputs.Documents.StatusLabel.Should().Be("Selection required");
        r.Inputs.Documents.Facts.Should().Equal("Specification (3)");
        r.NextRecommendedAction!.Key.Should().Be(WorkflowReadinessService.ChooseArtifactsKey);
    }

    [Fact]
    public async Task ManualImportWithoutSampleProject_DocumentsAreAvailable_InTheManualWorkspace()
    {
        _workspace = WorkspaceSnapshots.Loaded("Unsaved workspace", null, null, WorkspaceArtifactType.Specification);
        DocumentStepsOpen();

        var r = await ReadinessAsync();

        r.Inputs.Documents.Status.Should().Be(ProjectInputStatus.Ready);
        r.Inputs.Documents.Detail.Should().Contain("manual workspace");
        r.Inputs.Documents.StatusLabel.Should().NotContain("Sample");
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task OutdatedSource_IsNeedsRefresh_NotAbsent_AndIsRecommended(bool outdatedCiCd, bool predatesInventory)
    {
        Documents(AllRoles);
        DocumentStepsOpen();
        Source(outdatedCiCd, predatesInventory, Nuget);

        var r = await ReadinessAsync();

        r.Inputs.Source.Status.Should().Be(ProjectInputStatus.NeedsAttention);
        r.Inputs.Source.StatusLabel.Should().Be("Needs refresh");
        r.NextRecommendedAction!.Key.Should().Be(WorkflowReadinessService.RefreshSourceKey);
        r.NextRecommendedAction.Route.Should().Be("source-analysis");
    }

    [Theory]
    [InlineData(null, "No application URL")]
    [InlineData("https://example-qa.local", "Placeholder URL")]
    public async Task PartialTarget_IsDistinctFromAbsent_AndIsFinishedBeforeRuntimeReviews(string? url, string label)
    {
        Environment(url);
        NoSourceYet();

        var r = await ReadinessAsync();

        r.Inputs.Target.Status.Should().Be(ProjectInputStatus.Partial);
        r.Inputs.Target.StatusLabel.Should().Be(label);
        r.NextRecommendedAction!.Key.Should().Be(WorkflowReadinessService.CompleteTargetKey);
        r.NextRecommendedAction.Route.Should().Be(NavigationCatalog.TargetEnvironmentsRoute);
    }

    [Fact]
    public async Task PartialTarget_DoesNotBlockADocumentOnlyPath()
    {
        Documents(AllRoles);
        DocumentStepsApproved();
        Environment("https://example-qa.local");
        NoSourceYet();

        var r = await ReadinessAsync();

        r.NextRecommendedAction!.Key.Should().Be(WorkflowReadinessService.OpenReviewKeyPrefix + "quality-review",
            "the target is only finished first when the next review needs it");
    }

    [Fact]
    public async Task UnsupportedSourceTechnology_SourceIsStillProvided()
    {
        Source(detected: [("lang.java", Capability.SourceCode)]);

        var r = await ReadinessAsync();

        r.Inputs.Source.Status.Should().Be(ProjectInputStatus.Ready, "an unsupported technology is a tool limitation, not a missing source");
        r.IsOnboarding.Should().BeFalse();
    }

    [Fact]
    public async Task ConfiguredTarget_DoesNotForceReviewsThatDoNotApply()
    {
        Environment("https://m2lbdev.bufetat.no");
        Source(detected: [Nuget]);

        var r = await ReadinessAsync();

        r.ApplicableReviews.Should().NotContain(x => x.ReviewId == "integration-quality-review", "no integrations are configured");
        r.ApplicableReviews.Should().NotContain(x => x.ReviewId == "security-classification-review", "the extension is not enabled");
    }

    [Fact]
    public void InputCompleteness_IsNeverAScore()
    {
        typeof(ProjectInputs).GetProperties().Select(p => p.PropertyType).Should().NotContain([typeof(int), typeof(double), typeof(int?), typeof(double?)]);
    }

    [Fact]
    public async Task ApplicabilityChange_ThatRereadsReadiness_DoesNotLoop()
    {
        var projection = WorkspaceSnapshots.Projection(_workspace);
        var contexts = new Mock<IFrontendAnalysisContextFactory>();
        contexts.Setup(c => c.GetActiveContextAsync()).ReturnsAsync(new FrontendAnalysisContext { ActiveTargetError = "No active Target Environment" });
        var applicability = new ProjectApplicabilityState(Mock.Of<ITechnologyCoverageApiService>(), contexts.Object, projection.Object);
        var service = new WorkflowReadinessService(projection.Object, applicability, _workflowApi.Object, NullLogger<WorkflowReadinessService>.Instance);
        var rereads = 0;
        // A page re-reads readiness on every change; the refresh it triggers completes synchronously here.
        service.ReadinessChanged += () => { if (++rereads < 50) service.GetReadinessAsync().GetAwaiter().GetResult(); };

        await service.GetReadinessAsync();

        rereads.Should().Be(1, "the first load publishes once; re-reading during that publish does not refresh again");
    }
}
