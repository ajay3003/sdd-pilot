using System.Text.Json;
using BirkNext.Web.Pages;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using BirkNext.Web.Services.Explorers;
using BirkNext.Web.Tests.Pages;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BirkNext.Web.Tests.Pages;

public sealed class SharedArtifactTraceabilityTests : BunitContext
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReviewContextUsesTheSameRoleResolutionForSampleAndImportedDocuments(bool imported)
    {
        using var fixture = new Fixture();
        const string specification = "# Feature Specification\n\n## Requirements\n\n### FR-019 Shared resolver requirement\nThe application MUST use one resolved specification.";
        if (imported)
            await fixture.MutateAndWaitForRebuild(() => fixture.ArtifactContext.Import(new(WorkspaceArtifactType.Specification, specification, "requirements-school.md", "File")));
        else
        {
            fixture.Samples.RegisterProject("person-module");
            fixture.Samples.AddDocument("person-module", WorkspaceArtifactType.Specification, "docs/requirements-school.md", specification);
            await fixture.MutateAndWaitForRebuild(() => fixture.Workspace.CurrentProject = "person-module");
        }

        var resolved = await fixture.ArtifactContext.GetStateAsync(WorkspaceArtifactType.Specification);

        resolved.Status.Should().Be(ExplorerArtifactStatus.Loaded);
        resolved.Content.Should().Be(specification);
        resolved.Selected!.FileName.Should().Be("requirements-school.md");
        fixture.Provider.GetCurrent()!.GetRequirements().Should().Contain(r => r.Id == "FR-019");
        fixture.Graph.Build(fixture.Workspace).Should().Contain(r => r.Requirement.Id == "FR-019");
        AddTraceabilityServices(fixture);
        var traceability = Render<Traceability>();
        traceability.WaitForAssertion(() => traceability.Markup.Should().Contain("FR-019"));
    }

    [Fact]
    public async Task OpeningTraceabilityWithImportedSpecificationDoesNotMutateLifecycleOrSave()
    {
        using var fixture = new Fixture();
        await fixture.MutateAndWaitForRebuild(() => fixture.ArtifactContext.Import(new(WorkspaceArtifactType.Specification,
            "# Feature Specification\n\n## Requirements\n\n### FR-024 Read-only traceability\nThe page MUST not create links when opened.", "requirements.md", "File")));
        var lifecycleBefore = JsonSerializer.Serialize(fixture.Workspace.SddLifecycle);
        var autoSave = new Mock<IWorkspaceAutoSaveService>();
        Services.AddSingleton(fixture.Workspace);
        Services.AddSingleton<IWorkspaceSessionService>(fixture.Workspace);
        Services.AddSingleton<IArtifactExplorerContext>(fixture.ArtifactContext);
        Services.AddSingleton<ICurrentWorkspaceProjection>(fixture.Projection);
        Services.AddSingleton(fixture.Graph);
        Services.AddSingleton(fixture.Provider);
        Services.AddSingleton(autoSave.Object);
        Services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));

        var cut = Render<Traceability>();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("FR-024"));
        JsonSerializer.Serialize(fixture.Workspace.SddLifecycle).Should().Be(lifecycleBefore);
        autoSave.Verify(x => x.SaveNowAsync(), Times.Never);
        cut.Markup.Should().NotContain("No Sample Project selected");
    }

    [Fact]
    public async Task TraceabilityAndQualityReviewUseTheSameAuthoritativeSpecification()
    {
        using var fixture = new Fixture();
        const string specA = "# Feature Specification\n\n## Requirements\n\n### FR-031 Candidate A\nCandidate A is not authoritative.";
        const string specB = "# Feature Specification\n\n## Requirements\n\n### FR-032 Authoritative B\nCandidate B is authoritative.";
        fixture.Workspace.AddArtifactRevision(WorkspaceArtifactType.Specification, specA, "requirements-a.md", null, null, "File", select: false);
        var revisionB = fixture.Workspace.AddArtifactRevision(WorkspaceArtifactType.Specification, specB, "requirements-school.md", null, null, "File", select: false)!;
        await fixture.MutateAndWaitForRebuild(() => fixture.Workspace.SetArtifactAuthority(revisionB.RevisionId, "Approved"));

        var resolved = await fixture.ArtifactContext.GetStateAsync(WorkspaceArtifactType.Specification);
        resolved.Status.Should().Be(ExplorerArtifactStatus.Loaded);
        resolved.Selected!.FileName.Should().Be("requirements-school.md");
        fixture.Graph.Build(fixture.Workspace).Select(row => row.Requirement.Id).Should().ContainSingle().Which.Should().Be("FR-032");

        var recording = new RecordingQualityReviewService();
        AddQualityReviewServices(fixture, recording);
        var qualityPage = Render<QualityReview>();
        qualityPage.Find("button.btn-primary").Click();
        qualityPage.WaitForAssertion(() => recording.Calls.Should().ContainSingle());
        recording.Calls.Single().Specification.Should().Be(resolved.Content);
        recording.Calls.Single().Specification.Should().Contain("FR-032").And.NotContain("FR-031");
    }

    [Fact]
    public async Task MissingSpecificationShowsWorkspaceRoleStateAndResetDoesNotLeaveStaleAvailability()
    {
        using var fixture = new Fixture();
        fixture.ArtifactContext.Import(new(WorkspaceArtifactType.Constitution, "# Principles\nPP-01: Be clear", "principles.md", "File"));
        AddTraceabilityServices(fixture);

        var cut = Render<Traceability>();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Specification unavailable"));
        cut.Markup.Should().NotContain("No Sample Project selected");

        fixture.Workspace.ClearAll();
        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Specification unavailable");
            cut.Markup.Should().NotContain("FR-");
        });
    }

    private void AddQualityReviewServices(Fixture fixture, RecordingQualityReviewService recording)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<ISampleProjectDocumentResolver>(fixture.Samples);
        Services.AddSingleton<IArtifactExplorerContext>(fixture.ArtifactContext);
        Services.AddSingleton<IWorkspaceSessionService>(fixture.Workspace);
        Services.AddSingleton(fixture.Graph);
        Services.AddSingleton<IQualityReviewService>(recording);
        Services.AddSingleton(new QualityReviewSessionService());
        Services.AddSingleton(Mock.Of<IDashboardSnapshotService>());
        Services.AddSingleton(Mock.Of<IDeliveryReadinessAssessmentService>());
        Services.AddSingleton(Mock.Of<IReportExportService>());
    }

    private void AddTraceabilityServices(Fixture fixture)
    {
        Services.AddSingleton<IWorkspaceSessionService>(fixture.Workspace);
        Services.AddSingleton<IArtifactExplorerContext>(fixture.ArtifactContext);
        Services.AddSingleton<ICurrentWorkspaceProjection>(fixture.Projection);
        Services.AddSingleton(fixture.Graph);
        Services.AddSingleton<IWorkspaceAutoSaveService>(Mock.Of<IWorkspaceAutoSaveService>());
        Services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
    }

    private sealed class RecordingQualityReviewService : IQualityReviewService
    {
        public IReadOnlyList<QualityReviewPackDescriptor> AvailablePacks { get; } =
        [new("qa-auditor", "Quality", "QA Auditor", "Review specification inputs.", true)];
        public List<(string? Specification, IEnumerable<string> Packs)> Calls { get; } = [];
        public Task InitializeAsync() => Task.CompletedTask;
        public Task<QualityReviewReport> RunAsync(string? constitutionText, string? specText, string? planText, string? taskText,
            string? dataModelText, IEnumerable<string> selectedPackIds)
        {
            Calls.Add((specText, selectedPackIds.ToArray()));
            return Task.FromResult(new QualityReviewReport { PackResults = [], RunAt = DateTimeOffset.UtcNow });
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly Mock<IWorkspaceSessionRestoreService> _restore = new();
        private readonly Mock<IWorkspacePersistenceApiService> _persistence = new();
        public WorkspaceArtifactRepository Workspace { get; } = new();
        public MockSampleProjectDocumentResolver Samples { get; } = new();
        public ArtifactExplorerContext ArtifactContext { get; }
        public CurrentWorkspaceProjection Projection { get; }
        public ReviewContextProvider Provider { get; }
        public SddEvidenceGraphService Graph { get; }

        public Fixture()
        {
            Samples.Repository = Workspace;
            ArtifactContext = new(Workspace, Samples, Samples);
            _restore.Setup(x => x.GetCurrentWorkspaceMetadataAsync()).ReturnsAsync((CurrentWorkspaceMetadata?)null);
            _persistence.Setup(x => x.GetCurrentStateAsync()).ReturnsAsync((CurrentWorkspaceStateDto?)null);
            Projection = new(ArtifactContext, _restore.Object, _persistence.Object, NullLogger<CurrentWorkspaceProjection>.Instance);
            Provider = new(Workspace, new WorkspaceUpdateCoordinator(), new ConstitutionAnalysisService(), new PlanAnalysisService(),
                new DataModelAnalysisService(), NullLogger<ReviewContextProvider>.Instance, ArtifactContext);
            Graph = new(Provider, new PlanAnalysisService());
        }

        public async Task MutateAndWaitForRebuild(Action mutation)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler? handler = null;
            handler = (_, _) =>
            {
                Provider.ReviewContextChanged -= handler;
                completion.TrySetResult();
            };
            Provider.ReviewContextChanged += handler;
            mutation();
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        public void Dispose()
        {
            Projection.Dispose();
            Provider.Dispose();
            ArtifactContext.Dispose();
        }
    }
}
