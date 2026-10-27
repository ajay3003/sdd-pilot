using System.Net;
using System.Text;
using BirkNext.Applicability;
using BirkNext.Technology;
using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// Recommended Workflow keeps the evidence dimensions apart: source analyzed ≠ Artifact Pack bound to source ≠ implementation assessed;
/// Azure DevOps change provenance is optional and never blocks source-backed review; a target is needed for runtime reviews only; a review
/// decision with known gaps completes a step; sibling packs never satisfy the viewed pack; generated documentation is a neutral, optional
/// analysis; the Dashboard is never a counted step.
/// </summary>
public sealed class RecommendedWorkflowEvidenceTests : BunitContext
{
    private const string Scope = "import:abc|Person|001-person-module";
    private readonly WorkspaceArtifactRepository _repository = new();
    private CurrentWorkspaceSnapshot _workspace = WorkspaceSnapshots.Loaded("Person Module", "person-module", "Person Module", [.. CurrentWorkspaceSnapshot.WorkflowRoles]);
    private ProjectTechnologyCoverage? _coverage;
    private FrontendAnalysisProfile? _environment;
    private List<WorkflowStepViewModel> _steps = [];
    private string _adoStatus = """{"configured":false,"usingMock":false,"message":"Azure DevOps is not configured for this project."}""";

    public RecommendedWorkflowEvidenceTests() => _repository.SddLifecycle.ActiveRequirementScope = Scope;

    private void SourceAnalyzed()
    {
        _environment ??= new FrontendAnalysisProfile { Id = "env-dev", Name = "DEV", TargetUrl = "" };
        _coverage = new ProjectTechnologyCoverage
        {
            EnvironmentId = "env-dev", SourceSnapshotId = Guid.NewGuid(), SourceArchive = "M2LB.zip", AnalyzedAt = DateTimeOffset.UtcNow,
            Source = new SourceTechnologyCoverage { Technologies = [], Capabilities = [] },
        };
    }

    private void BindPack(string scope) => _repository.SddLifecycle.ImplementationEvidence.Add(new SddImplementationEvidence
    {
        RequirementId = "FR-001", RequirementScope = scope, FilePath = "src/Person.cs", ProviderId = "CodeTraceability",
    });

    private static WorkflowStepViewModel Step(string key, string title, WorkflowStepStatus status, bool optional = false) => new()
    {
        Number = 1, Key = key, Title = title, Description = title, Route = key, ActionLabel = "Open " + title, Color = "#2563eb",
        Status = status, CanOpen = status != WorkflowStepStatus.Locked, IsOptional = optional, StepType = key.EndsWith("Explorer") ? "Explorer" : "Analysis",
        RequiresApproval = false, RequiresManualReview = true, Prerequisites = PrerequisiteState.Available,
        ApprovalState = status switch { WorkflowStepStatus.Approved => ApprovalState.Approved, WorkflowStepStatus.NeedsAttention => ApprovalState.NeedsChanges, _ => ApprovalState.Pending },
        ReviewState = status is WorkflowStepStatus.Reviewed or WorkflowStepStatus.Approved or WorkflowStepStatus.NeedsAttention ? ReviewState.Reviewed : ReviewState.NotStarted,
        ArtifactReferences = "Specification: spec.md @ A1A1A1A1",
    };

    private void CoreDecided() => _steps =
    [
        Step("SpecificationExplorer", "Specification Explorer", WorkflowStepStatus.Reviewed),
        Step("ArtifactTraceability", "Requirements Traceability", WorkflowStepStatus.NeedsAttention),
        Step("ImplementationReview", "Task Alignment Review", WorkflowStepStatus.Reviewed),
    ];

    private Task<WorkflowReadiness> ReadinessAsync()
    {
        var projection = WorkspaceSnapshots.Projection(_workspace);
        var contexts = new Mock<IFrontendAnalysisContextFactory>();
        contexts.Setup(c => c.GetActiveContextAsync()).ReturnsAsync(() => _environment is null
            ? new FrontendAnalysisContext { ActiveTargetError = "No active Target Environment" }
            : new FrontendAnalysisContext { ActiveProfile = _environment, TargetUrl = _environment.TargetUrl });
        var coverageApi = new Mock<ITechnologyCoverageApiService>();
        coverageApi.Setup(a => a.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(() => _coverage);
        var applicability = new ProjectApplicabilityState(coverageApi.Object, contexts.Object, projection.Object, Mock.Of<IFrontendAnalysisSettingsService>());
        var workflowApi = new Mock<IRecommendedWorkflowApiService>();
        workflowApi.Setup(w => w.BuildWorkflowStepsAsync(It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<IReadOnlyList<ArtifactRevisionRef>>()))
            .ReturnsAsync(() => _steps);
        var ado = new ImplementationTraceabilityApiService(new HttpClient(new StatusHandler(() => _adoStatus)) { BaseAddress = new Uri("http://localhost/") });
        return new WorkflowReadinessService(projection.Object, applicability, workflowApi.Object, NullLogger<WorkflowReadinessService>.Instance, _repository, ado)
            .GetReadinessAsync();
    }

    [Fact]
    public async Task Source_analyzed_without_pack_binding_recommends_the_binding_and_assesses_nothing()
    {
        SourceAnalyzed();
        CoreDecided();

        var r = await ReadinessAsync();

        r.Inputs.Source.Status.Should().Be(ProjectInputStatus.Ready, "the snapshot is analyzed");
        r.SourceBinding.Should().Be(SourceBindingState.NotEstablished, "an analyzed snapshot never binds a pack by itself");
        r.NextRecommendedAction!.Key.Should().Be(WorkflowReadinessService.EstablishSourceBindingKey);
        r.NextRecommendedAction.Description.Should().StartWith("Source has already been analyzed, but the selected Artifact Pack is not yet mapped to source evidence.");
        r.Recommendation!.ReasonCode.Should().Be("SOURCE_BINDING_MISSING");
        var source = r.EvidenceBranches.Single(b => b.Key == "source");
        source.State.Should().Be("Source analyzed · Artifact Pack source binding not established");
        source.Detail.Should().Contain("Not assessed").And.NotContain("Source unavailable").And.NotContain("failed");
        source.Ready.Should().BeFalse();
    }

    [Fact]
    public async Task A_sibling_pack_binding_does_not_satisfy_this_pack_but_its_own_binding_does()
    {
        SourceAnalyzed();
        CoreDecided();
        BindPack("import:abc|Person|002-barn-parent-feilkode");

        (await ReadinessAsync()).SourceBinding.Should().Be(SourceBindingState.NotEstablished, "evidence of a sibling pack is never this pack's");

        BindPack(Scope);
        var r = await ReadinessAsync();
        r.SourceBinding.Should().Be(SourceBindingState.Established);
        r.NextRecommendedAction!.Title.Should().Be("Open Implementation Evidence Review");
        r.EvidenceBranches.Single(b => b.Key == "source").State.Should().Be("Ready to review");
    }

    [Fact]
    public async Task Missing_Azure_DevOps_never_blocks_source_backed_review_and_configured_Azure_DevOps_is_optional()
    {
        SourceAnalyzed();
        CoreDecided();
        BindPack(Scope);

        var r = await ReadinessAsync();
        r.ChangeProvenance.Should().Be(ChangeProvenanceState.NotConfigured);
        r.NextRecommendedAction!.Route.Should().Be(WorkflowReadinessService.ImplementationEvidenceRoute, "Azure DevOps is not a prerequisite of source evidence");
        r.EvidenceBranches.Single(b => b.Key == "change").State.Should().Be("Not configured");
        r.ApplicableReviews.Should().NotContain(a => a.ReviewId == "change-traceability");

        _adoStatus = """{"configured":true,"usingMock":false,"message":"Azure DevOps integration is configured."}""";
        r = await ReadinessAsync();
        r.ApplicableReviews.Should().Contain(a => a.ReviewId == "change-traceability" && a.Label == "Change Traceability");
        r.NextRecommendedAction!.Route.Should().Be(WorkflowReadinessService.ImplementationEvidenceRoute, "Change Traceability is never the unconditional next step");

        _adoStatus = """{"configured":false,"usingMock":true,"message":"Developer demo evidence"}""";
        r = await ReadinessAsync();
        r.ChangeProvenance.Should().Be(ChangeProvenanceState.DemoEvidence);
        r.ApplicableReviews.Should().NotContain(a => a.ReviewId == "change-traceability", "demo evidence is never project evidence");
    }

    [Fact]
    public async Task Without_a_target_document_reviews_still_progress_and_runtime_reviews_say_not_configured()
    {
        _steps = [Step("SpecificationExplorer", "Specification Explorer", WorkflowStepStatus.Available)];

        var r = await ReadinessAsync();

        r.Inputs.Target.Status.Should().Be(ProjectInputStatus.Absent);
        r.NextRecommendedAction!.Key.Should().Be("SpecificationExplorer");
        r.EvidenceBranches.Single(b => b.Key == "runtime").State.Should().Be("Not configured");
        r.EvidenceBranches.Single(b => b.Key == "source").State.Should().Be("Source unavailable");
    }

    [Fact]
    public async Task Reviewed_with_gaps_is_a_recorded_decision_and_viewing_is_not()
    {
        _steps =
        [
            Step("SpecificationExplorer", "Specification Explorer", WorkflowStepStatus.InProgress),
            Step("ArtifactTraceability", "Requirements Traceability", WorkflowStepStatus.NeedsAttention),
            Step("ImplementationReview", "Task Alignment Review", WorkflowStepStatus.Available),
            Step("DataModelExplorer", "Data Model Explorer", WorkflowStepStatus.Reviewed, optional: true),
        ];

        var r = await ReadinessAsync();

        r.RequiredReviewGates.Select(g => g.Key).Should().Equal("SpecificationExplorer", "ArtifactTraceability", "ImplementationReview");
        r.DecidedReviewCount.Should().Be(1, "only the traceability review recorded a decision (with gaps); opening the Specification Explorer records none");
        r.NextRecommendedAction!.Key.Should().Be("SpecificationExplorer");
        ArtifactReviewPresentation.Of(r.Steps.Single(s => s.Key == "ArtifactTraceability")).Label.Should().Be("Reviewed with gaps");
        r.CanRelease.Should().BeFalse("the workflow never claims release readiness without a policy");
        r.Steps.Should().NotContain(s => s.Key == "Dashboard");
    }

    [Fact]
    public async Task All_required_decisions_recorded_reports_completion_without_inventing_a_pass()
    {
        CoreDecided();

        var r = await ReadinessAsync();

        r.RequiredDecisionsComplete.Should().BeTrue();
        r.ReleaseReadinessPercent.Should().Be(100);
        r.ReleaseReason.Should().Be(WorkflowReadinessService.NoReleasePolicy);
    }

    [Fact]
    public async Task Generated_documentation_health_is_applicable_only_when_the_imported_project_has_generated_docs()
    {
        _repository.SddLifecycle.CurrentProjectImportId = "abc";
        _repository.SddLifecycle.GeneratedDocumentationSets.Add(new SddGeneratedDocumentationSet { SetId = "genset-abc", ProjectImportId = "abc", State = "NotPresent" });

        (await ReadinessAsync()).ApplicableReviews.Should().NotContain(a => a.ReviewId == "generated-documentation-health", "no autodoc is neutral, not a missing review");

        _repository.SddLifecycle.GeneratedDocumentationSets[0].State = "Present";
        (await ReadinessAsync()).ApplicableReviews.Should().ContainSingle(a => a.ReviewId == "generated-documentation-health")
            .Which.Reason.Should().Be("Generated documentation detected in the current target project.");
    }

    [Fact]
    public void Change_traceability_page_without_Azure_DevOps_shows_not_configured_and_no_evidence()
    {
        Services.AddSingleton(new ImplementationTraceabilityApiService(new HttpClient(new StatusHandler(() => _adoStatus)) { BaseAddress = new Uri("http://localhost/") }));
        JSInterop.Mode = JSRuntimeMode.Loose;

        var cut = Render<ImplementationTraceability>();

        cut.WaitForAssertion(() => cut.Find("[data-testid=ct-status]").GetAttribute("data-state").Should().Be("NotConfigured"));
        cut.Find("h1").TextContent.Should().Be("Change Traceability");
        cut.Find("[data-testid=ct-not-configured]").TextContent.Should().Contain("Azure DevOps is not configured for this project");
        cut.FindAll("#it-workitem-ids").Should().BeEmpty("nothing to load without Azure DevOps");
        cut.Find("[data-testid=ct-boundary]").TextContent.Should().Contain("does not prove that a requirement is correctly implemented");
        cut.Markup.Should().NotContain("local/demo evidence");
    }

    [Fact]
    public void Navigation_and_catalog_use_the_canonical_names()
    {
        var labels = NavigationCatalog.Sections.SelectMany(s => s.Items).Select(i => i.Label).ToList();
        labels.Should().Contain(["Requirements Traceability", "Task Alignment Review", "Change Traceability"]);
        labels.Should().NotContain(["Artifact Traceability", "Implementation Review", "Implementation Traceability"]);
        DocumentationCatalog.Features.Select(f => f.DisplayName).Should().NotContain(["Implementation Review", "Implementation Traceability", "Artifact Traceability"]);
        typeof(BirkNext.Web.Pages.TaskToSpecAlignment).GetCustomAttributes(typeof(Microsoft.AspNetCore.Components.RouteAttribute), false)
            .Cast<Microsoft.AspNetCore.Components.RouteAttribute>().Select(r => r.Template).Should().Contain(["/task-alignment", "/task-alignment-review"]);
        typeof(ImplementationTraceability).GetCustomAttributes(typeof(Microsoft.AspNetCore.Components.RouteAttribute), false)
            .Cast<Microsoft.AspNetCore.Components.RouteAttribute>().Select(r => r.Template).Should().Contain(["/implementation-traceability", "/change-traceability"]);
    }

    private sealed class StatusHandler(Func<string> json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json(), Encoding.UTF8, "application/json") });
    }
}
