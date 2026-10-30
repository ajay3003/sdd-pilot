using BirkNext.Applicability;
using BirkNext.BrowserAuthentication;
using BirkNext.Technology;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using BirkNext.Web.Services.ProjectEvidence;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// Review paths: evidence profile, runtime sequencing (environment → Target Application → Project evidence → active → browser login only
/// for automated execution), and input applicability per path. Pure and deterministic.
/// </summary>
public sealed class WorkflowPathsTests
{
    private static FrontendAnalysisProfile Env(string id = "dev", string name = "Development", string? url = "https://m2lb.dev.example.test/", EnvironmentProject? project = null) =>
        new() { Id = id, Name = name, TargetUrl = url, Project = project };

    private static EnvironmentProject M2lb() => new() { ProjectName = "M2LB", ProjectImportId = "import-a" };

    private static ProjectEvidenceView? NoEvidence(string _) => null;

    [Fact]
    public void InfrastructureOnly_IsClassifiedFromCapabilities_NotFileNames()
    {
        var workspace = CurrentWorkspaceSnapshot.None();
        var inputs = ProjectInputPresentation.Build(workspace, null, Coverage(Capability.InfrastructureAsCode, Capability.Pipeline));
        WorkflowPaths.Classify(workspace, inputs, Coverage(Capability.InfrastructureAsCode, Capability.Pipeline)).Should().Be(WorkflowEvidenceProfile.InfrastructureOnly);

        var withApplication = Coverage(Capability.InfrastructureAsCode, Capability.BackendApplication);
        WorkflowPaths.Classify(workspace, ProjectInputPresentation.Build(workspace, null, withApplication), withApplication).Should().Be(WorkflowEvidenceProfile.SourceOnly);
        WorkflowPaths.Classify(workspace, ProjectInputPresentation.Build(workspace, null, null), null).Should().Be(WorkflowEvidenceProfile.NoEvidence);
    }

    [Fact]
    public void Runtime_NoEnvironment_ChooseTargetEnvironmentIsTheFirstStep()
    {
        var setup = WorkflowPaths.RuntimeSetup([], null, NoEvidence);

        setup.Should().ContainSingle();
        setup[0].Key.Should().Be(WorkflowPaths.EnvironmentStep);
        setup[0].ActionLabel.Should().Be("Choose Target Environment");
        setup[0].Route.Should().Be(NavigationCatalog.TargetEnvironmentsRoute);
    }

    [Fact]
    public void Runtime_EnvironmentWithoutTargetApplication_ConfigureTargetApplication_BeforeProject()
    {
        var setup = WorkflowPaths.RuntimeSetup([Env(url: "")], "dev", NoEvidence);

        setup.First(s => !s.Met).Key.Should().Be(WorkflowPaths.TargetApplicationStep);
        setup.First(s => !s.Met).ActionLabel.Should().Be("Configure Target Application");
        setup.First(s => !s.Met).Route.Should().Contain("tab=target").And.Contain("profile=dev");
    }

    [Fact]
    public void Runtime_TargetApplicationWithoutProject_ConfigureProjectEvidence()
    {
        var setup = WorkflowPaths.RuntimeSetup([Env()], "dev", NoEvidence);

        setup.First(s => !s.Met).ActionLabel.Should().Be("Configure Project evidence");
        setup.First(s => !s.Met).Route.Should().Contain("tab=project").And.Contain("profile=dev");
    }

    [Fact]
    public void Runtime_ConfiguredButNotActive_SetActiveIsTheLastStep_OnlyOnTheRuntimePath()
    {
        var setup = WorkflowPaths.RuntimeSetup([Env(project: M2lb()), Env("qa", "QA")], activeProfileId: null, NoEvidence);
        setup.Should().ContainSingle("two environments, none active: the user chooses which one to test").Which.ActionLabel.Should().Be("Choose a Target Environment");

        var single = WorkflowPaths.RuntimeSetup([Env(project: M2lb())], activeProfileId: null, NoEvidence);
        single.Select(s => (s.Key, s.Met)).Should().Equal((WorkflowPaths.EnvironmentStep, true), (WorkflowPaths.TargetApplicationStep, true), (WorkflowPaths.ProjectStep, true), (WorkflowPaths.ActiveStep, false));
        single.First(s => !s.Met).ActionLabel.Should().Be("Set Development active");

        var active = WorkflowPaths.RuntimeSetup([Env(project: M2lb())], "dev", NoEvidence);
        active.Should().OnlyContain(s => s.Met);
    }

    [Fact]
    public void Runtime_AutomatedExecution_AddsUnattendedLogin_ManualDoesNot()
    {
        var ready = WorkflowPaths.RuntimeSetup([Env(project: M2lb())], "dev", NoEvidence);
        var path = WorkflowPaths.Build(WorkflowEvidenceProfile.SourceOnly, CurrentWorkspaceSnapshot.None(), ProjectInputPresentation.Build(CurrentWorkspaceSnapshot.None(), null, null), [], ready, null)
            .Single(p => p.Kind == WorkflowPathKind.Runtime);
        var notConfigured = new AutomatedBrowserExecutionReadiness(false, "Automated browser execution unavailable", "Unattended browser authentication not configured", UnattendedLoginState.NotConfigured, []);

        var automated = WorkflowPaths.ForExecution(path, automated: true, notConfigured);
        automated.PrimaryLabel.Should().Be("Configure unattended browser login");
        automated.PrimaryRoute.Should().Be("admin/system-settings?section=authentication");
        automated.Setup.Last().Detail.Should().Be("Unattended browser authentication not configured");

        WorkflowPaths.ForExecution(path, automated: false, notConfigured).Setup.Should().NotContain(s => s.Key == WorkflowPaths.UnattendedLoginStep, "a manual run uses attended login");
        var source = path with { Kind = WorkflowPathKind.Source };
        WorkflowPaths.ForExecution(source, automated: true, notConfigured).Should().Be(source, "static reviews never get a login step");
    }

    [Fact]
    public void InputApplicability_TargetIsOptional_UnlessTheRuntimePathIsChosen()
    {
        var target = ProjectInputPresentation.Target(null);
        WorkflowPaths.InputApplicability(target, null).Should().Be((SetupApplicability.Optional, "Optional"));
        WorkflowPaths.InputApplicability(target, WorkflowPathKind.Source).Applicability.Should().Be(SetupApplicability.Optional);
        WorkflowPaths.InputApplicability(target, WorkflowPathKind.Runtime).Applicability.Should().Be(SetupApplicability.RequiredNow);

        var documents = ProjectInputPresentation.Documents(CurrentWorkspaceSnapshot.None());
        WorkflowPaths.InputApplicability(documents, WorkflowPathKind.Source).Should().Be((SetupApplicability.Unavailable, "Unavailable"), "missing documents block document reviews only");
    }

    [Fact]
    public void ScreenshotState_SourceReadyNoDocumentsNoEnvironment_ThreePaths_NoMandatoryEnvironment()
    {
        var workspace = CurrentWorkspaceSnapshot.None();
        var coverage = Coverage(Capability.InfrastructureAsCode, Capability.Pipeline);
        var inputs = ProjectInputPresentation.Build(workspace, null, coverage);
        ReviewCapability Cap(string id, string name, ReviewSection section, bool ready) => new(id, name, section, ReviewApplicabilityState.Applicable,
            ready ? ReviewReadiness.Ready : ReviewReadiness.NeedsSetup, ReviewRequiredness.Optional, ReviewDecisionState.NoDecisionTracked, id, [], "");
        var capabilities = new[] { Cap("source-analysis", "Source Analysis", ReviewSection.Source, true), Cap("pipeline-review", "Pipeline Review", ReviewSection.Source, true),
            Cap("frontend-quality-review", "Frontend Quality Review", ReviewSection.Runtime, false) };

        var paths = WorkflowPaths.Build(WorkflowPaths.Classify(workspace, inputs, coverage), workspace, inputs, capabilities, WorkflowPaths.RuntimeSetup([], null, NoEvidence), null);

        paths.Select(p => (p.Kind, p.State)).Should().Equal((WorkflowPathKind.Source, WorkflowPathState.Ready), (WorkflowPathKind.Documents, WorkflowPathState.Unavailable), (WorkflowPathKind.Runtime, WorkflowPathState.NeedsSetup));
        paths[0].Title.Should().Be("Source / infrastructure review");
        paths[0].Links.Select(l => l.Label).Should().Equal("Source Analysis", "Pipeline Review");
        paths[1].PrimaryLabel.Should().Be("Import project documents");
        paths[2].StateLabel.Should().Be("Requires Target Environment");
        paths[2].Unlocks.Should().NotContain("document review").And.Contain("does not affect document or source reviews");
    }

    private static ProjectTechnologyCoverage Coverage(params Capability[] capabilities) => new()
    {
        EnvironmentId = "", SourceSnapshotId = Guid.NewGuid(), SourceArchive = "Terraform-BasicOpsInfra_M2LBM01.zip", AnalyzedAt = DateTimeOffset.UtcNow,
        Source = new SourceTechnologyCoverage { Capabilities = capabilities.Select(c => new CapabilityEvidence(c, BirkNext.Applicability.DetectionConfidence.Confirmed, "test")).ToList() },
    };
}
