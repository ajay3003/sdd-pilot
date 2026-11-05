using BirkNext.Integrations;
using BirkNext.Snapshots;
using BirkNext.Technology;
using BirkNext.Web.Services;
using BirkNext.Web.Services.ProjectImport;
using BirkNext.Web.Models;
using BirkNext.Web.Services.ProjectEvidence;
using FluentAssertions;
using Moq;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// Hardening (NEXT41, IMP-02/WF-02/DASH-06): a stored source snapshot is not an analyzed one. Import, Recommended Workflow and the Project Quality
/// Dashboard read one outcome — the shared <see cref="SourceSetupOutcome.Overall"/> rule the Snapshot Registry uses — over the areas the snapshot
/// contains. Infrastructure-only sources are not failed for lacking application code; a source nothing could be read from is never Partial.
/// </summary>
public sealed class SourceOutcomeSemanticsTests
{
    static List<SourceSetupArea> Areas(params (string Area, string Status)[] areas) => [.. areas.Select(a => new SourceSetupArea(a.Area, a.Status))];

    static ProjectTechnologyCoverage Coverage(SnapshotProcessingStatus outcome, List<SourceSetupArea> areas) => new()
    {
        EnvironmentId = "dev", SourceSnapshotId = Guid.NewGuid(), SourceArchive = "M2LB.zip", AnalyzedAt = DateTimeOffset.UnixEpoch,
        Source = new SourceTechnologyCoverage(), SourceOutcome = outcome, SourceAreas = areas,
    };

    [Fact]
    public void The_source_input_says_partially_analyzed_or_failed_never_analyzed_for_an_incomplete_snapshot()
    {
        var complete = ProjectInputPresentation.Source(Coverage(SnapshotProcessingStatus.Completed, Areas(("Application code", "Complete"), ("Architecture", "Complete"))));
        complete.Should().Match<ProjectInput>(i => i.Status == ProjectInputStatus.Ready && i.StatusLabel == "Analyzed");

        var partial = ProjectInputPresentation.Source(Coverage(SnapshotProcessingStatus.Partial, Areas(("Application code", "Complete"), ("Architecture", "Partial"), ("CI/CD", "Partial"))));
        partial.Should().Match<ProjectInput>(i => i.Status == ProjectInputStatus.Partial && i.StatusLabel == "Partially analyzed");
        partial.Detail.Should().Contain("Architecture: Partial · CI/CD: Partial");

        var failed = ProjectInputPresentation.Source(Coverage(SnapshotProcessingStatus.Failed, Areas(("Application code", "Failed"))));
        failed.Should().Match<ProjectInput>(i => i.Status == ProjectInputStatus.Failed && i.StatusLabel == "Analysis failed" && i.IsProvided && !i.IsUsable);
        partial.IsUsable.Should().BeTrue("a partially analyzed source is usable; its limitation is shown");
        WorkflowPaths.InputApplicability(partial, null).Label.Should().Be("Partially analyzed", "never \"Unavailable\" for an analyzed source");
        WorkflowPaths.InputApplicability(failed, null).Label.Should().Be("Analysis failed");
        failed.Detail.Should().Contain("The import itself is kept", "a failed analyzer never invalidates the imported source");

        // An older backend without the outcome keeps the previous behaviour (a snapshot with inventory reads Analyzed).
        ProjectInputPresentation.Source(Coverage(SnapshotProcessingStatus.Completed, []) with { SourceOutcome = null }).StatusLabel.Should().Be("Analyzed");
    }

    [Fact]
    public void Import_agrees_with_the_Snapshot_Registry_when_the_code_analyzer_says_Ready_but_another_area_is_partial()
    {
        var snapshot = new SourceSetupSnapshot(Guid.NewGuid(), "M2LB.zip", "f", DateTimeOffset.UnixEpoch, "Ready")
        {
            Areas = Areas(("Application code", "Complete"), ("Architecture", "Partial"), ("Configuration", "Complete")),
        };

        var analysis = ImportProjectOverview.SourceAnalysis(true, snapshot.SnapshotId, snapshot);

        analysis.Should().Match<ImportSourceAnalysis>(a => a.State == ImportAnalysisState.Partial && a.Status == "Partial");
        SourceSetupOutcome.Overall(snapshot.Areas).Should().Be(SnapshotProcessingStatus.Partial, "the registry's outcome for the same snapshot");
    }

    [Fact]
    public void Infrastructure_only_sources_are_complete_or_partial_never_failed_for_missing_application_code()
    {
        var iacComplete = Areas(("Application code", "NotDetected"), ("Infrastructure as Code", "Complete"), ("Configuration", "Complete"));
        SourceSetupOutcome.Overall(iacComplete).Should().Be(SnapshotProcessingStatus.Completed);
        ImportProjectOverview.SourceAnalysis(true, Guid.NewGuid(), new(Guid.NewGuid(), "tf.zip", "f", DateTimeOffset.UnixEpoch, "Failed") { Areas = iacComplete })
            .Status.Should().Be("Available · no application code");

        // Terraform-BasicOpsInfra_Meldingsutveksler's shape: IaC and configuration complete, CI/CD partial, no application code.
        var basicOps = Areas(("Application code", "NotDetected"), ("Architecture", "Unsupported"), ("Infrastructure as Code", "Complete"), ("Configuration", "Complete"), ("CI/CD", "Partial"));
        SourceSetupOutcome.Overall(basicOps).Should().Be(SnapshotProcessingStatus.Partial);
        ImportProjectOverview.SourceAnalysis(true, Guid.NewGuid(), new(Guid.NewGuid(), "tf.zip", "f", DateTimeOffset.UnixEpoch, "Failed") { Areas = basicOps })
            .Status.Should().Be("Partial · no application code");
    }

    [Fact]
    public async Task The_current_source_is_the_Target_Applications_main_package_not_the_package_imported_last()
    {
        var main = Guid.NewGuid();
        var package = new ProjectEvidencePackage("import-m2lb", "M2LB", "M2LB.zip", "sha", DateTimeOffset.UnixEpoch, main, [], new Dictionary<WorkspaceArtifactType, int>(), 0, [], [], [], null);
        var evidence = new Mock<IProjectEvidenceService>();
        evidence.Setup(e => e.Peek("dev")).Returns(new ProjectEvidenceView("dev", "Development", null, package, [], [], [package], []));
        var contexts = new Mock<IFrontendAnalysisContextFactory>();
        contexts.Setup(c => c.GetActiveContextAsync()).ReturnsAsync(new FrontendAnalysisContext { ActiveProfile = new FrontendAnalysisProfile { Id = "dev", Name = "Development" } });
        var api = new Mock<ITechnologyCoverageApiService>();
        api.Setup(a => a.GetAsync("dev", main, It.IsAny<CancellationToken>())).ReturnsAsync(Coverage(SnapshotProcessingStatus.Completed, []) with { SourceSnapshotId = main });
        var state = new ProjectApplicabilityState(api.Object, contexts.Object, WorkspaceSnapshots.Projection(WorkspaceSnapshots.AllRoles()).Object,
            Mock.Of<IFrontendAnalysisSettingsService>(), evidence.Object);

        await state.RefreshAsync();

        state.Coverage!.SourceSnapshotId.Should().Be(main);
        api.Verify(a => a.GetAsync("dev", It.IsAny<CancellationToken>()), Times.Never(), "never the workspace's newest snapshot when the application's own source is known");
    }
}
