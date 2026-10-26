using BirkNext.Web.GraphQL;
using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using StrawberryShake;
using System.Net;
using System.Text;
using System.Text.Json;

namespace BirkNext.Web.Tests.Pages;

public class DashboardPageTests : BunitContext
{
    private readonly Mock<IWorkflowReadinessService> _workflowReadiness = new();

    public DashboardPageTests()
    {
        Services.AddSingleton<IDashboardMetricsService, DashboardMetricsService>();
        Services.AddSingleton(new Mock<IReportExportService>().Object);
        Services.AddSingleton(BirkNext.Web.Tests.Services.WorkspaceSnapshots.Projection(CurrentWorkspaceSnapshot.None()).Object);
        Services.AddSingleton(new Mock<IWorkspaceSessionService>().Object);
        Services.AddSingleton(new Mock<IDashboardSnapshotService>().Object);
        Services.AddSingleton(new RuntimeReviewSessionService());
        Services.AddSingleton(new QualityReviewSessionService());
        _workflowReadiness
            .Setup(service => service.GetReadinessAsync())
            .ReturnsAsync(EmptyWorkflowReadiness());
        Services.AddSingleton(_workflowReadiness.Object);

        var handler = new SampleProjectsHttpHandler();
        var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost/")
        };
        Services.AddSingleton(new SampleProjectsApiService(client));
        Services.AddSingleton<BirkNext.Web.Services.SampleProjects.ISampleProjectArtifactDiscovery>(sp => new BirkNext.Web.Services.SampleProjects.SampleProjectArtifactDiscoveryService(sp.GetRequiredService<SampleProjectsApiService>()));
    }

    [Fact]
    public void DashboardRoute_RendersMetrics()
    {
        var candidate = MakeReviewedCandidate("req-1", ScenarioKind.Requirement, CandidateReviewStatus.Accepted);
        RegisterClient([candidate], []);

        var cut = Render<Dashboard>();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("SDD Governance Dashboard");
            cut.Markup.Should().Contain("Project Health");
            cut.Markup.Should().Contain("Workflow");
            cut.Markup.Should().Contain("Needs setup", "without a project every area needs a project first");
            cut.Markup.Should().NotContain(">—<", "no area shows a bare dash as its state");
        }, timeout: TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void DashboardMetrics_RenderWithEmptyData()
    {
        RegisterClient([], []);

        var cut = Render<Dashboard>();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().NotContain(">0%<", "nothing is assessed without a workspace");
            cut.Markup.Should().Contain("Needs setup");
            cut.Markup.Should().Contain("No workspace loaded");
            cut.Markup.Should().Contain("Project Health");
            cut.Markup.Should().Contain("Traceability");
            cut.Markup.Should().Contain("Top Risks");
            cut.Find("[data-testid=db-risks-message]").TextContent.Should().Contain("Risks have not been assessed yet").And.NotContain("No risks detected");
        }, timeout: TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void DashboardWorkflowMetric_UsesSharedReadinessForLoadedWorkspace()
    {
        var readiness = LoadedWorkflowReadiness();
        _workflowReadiness
            .Setup(service => service.GetReadinessAsync())
            .ReturnsAsync(readiness);
        Services.AddSingleton(BirkNext.Web.Tests.Services.WorkspaceSnapshots.Projection(readiness.Workspace).Object);
        RegisterClient([], []);

        var cut = Render<Dashboard>();

        cut.WaitForAssertion(() =>
        {
            var workflow = cut.Find("[data-testid=db-health-area][data-key=workflow]");
            workflow.GetAttribute("data-status").Should().Be(nameof(DashboardStatus.InProgress));
            workflow.TextContent.Should().Contain("required review decisions recorded (30%)").And.Contain("In progress");
        }, timeout: TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void DashboardMetrics_RenderHealthProgressRisksAndActivitySummary()
    {
        var candidates = new List<IGetReviewedCandidates_ReviewedCandidates>
        {
            MakeReviewedCandidate("req-1", ScenarioKind.Requirement, CandidateReviewStatus.Accepted),
            MakeReviewedCandidate("req-2", ScenarioKind.Requirement, CandidateReviewStatus.New),
            MakeReviewedCandidate("test-1", ScenarioKind.Test, CandidateReviewStatus.Accepted),
            MakeReviewedCandidate("test-2", ScenarioKind.Test, CandidateReviewStatus.Rejected),
            MakeReviewedCandidate("clr-1", ScenarioKind.NeedsClarification, CandidateReviewStatus.NeedsReview),
        };
        var links = new List<IGetCandidateLinks_CandidateLinks>
        {
            MakeCandidateLink("req-1", "test-1", CandidateLinkType.RequirementTest),
            MakeCandidateLink("req-2", "clr-1", CandidateLinkType.RequirementClarification),
        };
        RegisterClient(candidates, links);

        var cut = Render<Dashboard>();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("SDD Governance Dashboard");
            cut.Markup.Should().Contain("Project Artifacts");
            cut.Markup.Should().NotContain("Governance Status");
            cut.Markup.Should().Contain("Test and release evidence", "release evidence is separate from review readiness");
            cut.Markup.Should().Contain("Analysis Summary", "per-analysis results stay available under Analysis details");
            cut.Markup.Should().Contain("Quick Actions");
        }, timeout: TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void Dashboard_ApiFailure_ShowsInlineError()
    {
        var reviewedQuery = new Mock<IGetReviewedCandidatesQuery>();
        reviewedQuery
            .Setup(q => q.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Backend unavailable"));

        var linksQuery = new Mock<IGetCandidateLinksQuery>();
        var client = new Mock<IBirkNextClient>();
        client.Setup(c => c.GetReviewedCandidates).Returns(reviewedQuery.Object);
        client.Setup(c => c.GetCandidateLinks).Returns(linksQuery.Object);
        Services.AddSingleton(client.Object);

        var cut = Render<Dashboard>();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("SDD Governance Dashboard");
            cut.Markup.Should().Contain("No workspace loaded");
            cut.Markup.Should().Contain("Needs setup");
        }, timeout: TimeSpan.FromSeconds(1));
    }

    private void RegisterClient(
        IReadOnlyList<IGetReviewedCandidates_ReviewedCandidates> candidates,
        IReadOnlyList<IGetCandidateLinks_CandidateLinks> links)
    {
        var reviewedQuery = new Mock<IGetReviewedCandidatesQuery>();
        reviewedQuery
            .Setup(q => q.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeReviewedResult(candidates));

        var linksQuery = new Mock<IGetCandidateLinksQuery>();
        linksQuery
            .Setup(q => q.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeLinksResult(links));

        var client = new Mock<IBirkNextClient>();
        client.Setup(c => c.GetReviewedCandidates).Returns(reviewedQuery.Object);
        client.Setup(c => c.GetCandidateLinks).Returns(linksQuery.Object);
        Services.AddSingleton(client.Object);
    }

    private static IOperationResult<IGetReviewedCandidatesResult> MakeReviewedResult(
        IReadOnlyList<IGetReviewedCandidates_ReviewedCandidates> candidates)
    {
        var data = new Mock<IGetReviewedCandidatesResult>();
        data.Setup(d => d.ReviewedCandidates).Returns(candidates);

        var result = new Mock<IOperationResult<IGetReviewedCandidatesResult>>();
        result.Setup(r => r.Data).Returns(data.Object);
        result.Setup(r => r.Errors).Returns([]);
        return result.Object;
    }

    private static IOperationResult<IGetCandidateLinksResult> MakeLinksResult(
        IReadOnlyList<IGetCandidateLinks_CandidateLinks> links)
    {
        var data = new Mock<IGetCandidateLinksResult>();
        data.Setup(d => d.CandidateLinks).Returns(links);

        var result = new Mock<IOperationResult<IGetCandidateLinksResult>>();
        result.Setup(r => r.Data).Returns(data.Object);
        result.Setup(r => r.Errors).Returns([]);
        return result.Object;
    }

    private static IGetReviewedCandidates_ReviewedCandidates MakeReviewedCandidate(
        string id,
        ScenarioKind classification,
        CandidateReviewStatus reviewStatus,
        string title = "")
    {
        var candidate = new Mock<IGetReviewedCandidates_ReviewedCandidates>();
        candidate.Setup(c => c.Id).Returns(id);
        candidate.Setup(c => c.Title).Returns(title);
        candidate.Setup(c => c.Classification).Returns(classification);
        candidate.Setup(c => c.ReviewStatus).Returns(reviewStatus);
        return candidate.Object;
    }

    private static IGetCandidateLinks_CandidateLinks MakeCandidateLink(
        string sourceCandidateRef,
        string targetCandidateRef,
        CandidateLinkType linkType)
    {
        var link = new Mock<IGetCandidateLinks_CandidateLinks>();
        link.Setup(l => l.SourceCandidateRef).Returns(sourceCandidateRef);
        link.Setup(l => l.TargetCandidateRef).Returns(targetCandidateRef);
        link.Setup(l => l.LinkType).Returns(linkType);
        return link.Object;
    }

    private static WorkflowReadiness EmptyWorkflowReadiness() =>
        new(
            Workspace: CurrentWorkspaceSnapshot.None(),
            SpecificationExplorerState: null,
            TraceabilityState: null,
            ImplementationReviewState: null,
            QualityGateState: null,
            NextRecommendedAction: null,
            OverallReadiness: new WorkflowReadinessBreakdown(),
            RequiredReviewCount: 0,
            ApprovedReviewCount: 0,
            Steps: [],
            CanRelease: false,
            ReleaseReason: "Load a workspace before release readiness can be evaluated.");

    private static WorkflowReadiness LoadedWorkflowReadiness() =>
        EmptyWorkflowReadiness() with
        {
            Workspace = BirkNext.Web.Tests.Services.WorkspaceSnapshots.Loaded("Saved workspace", "sample-project", "Sample Project",
                WorkspaceArtifactType.Constitution, WorkspaceArtifactType.Specification, WorkspaceArtifactType.Plan),
            RequiredReviewCount = 3,
            ApprovedReviewCount = 1,
            ReleaseReadinessPercent = 30,
            OverallReadiness = new WorkflowReadinessBreakdown
            {
                ArtifactReadiness = 60,
                ReviewReadiness = 30,
                ApprovalReadiness = 30,
                OverallReadiness = 30
            }
        };


    private IRenderedComponent<Dashboard> RenderDashboardWithWorkspace(
        string? currentProject,
        params SampleProjectDto[] projects)
        => RenderDashboardWithWorkspace(currentProject, null, null, projects);

    private IRenderedComponent<Dashboard> RenderDashboardWithWorkspace(
        string? currentProject,
        Action<SampleProjectsHttpHandler>? configureHandler,
        Action<WorkspaceArtifactRepository>? configureWorkspace,
        params SampleProjectDto[] projects)
    {
        var handler = new SampleProjectsHttpHandler();
        handler.SetProjects(projects);
        configureHandler?.Invoke(handler);

        var workspace = new WorkspaceArtifactRepository();
        configureWorkspace?.Invoke(workspace);
        workspace.CurrentProject = currentProject;

        var ctx = new BunitContext();
        ctx.Services.AddSingleton<IDashboardMetricsService, DashboardMetricsService>();
        ctx.Services.AddSingleton(new Mock<IReportExportService>().Object);
        ctx.Services.AddSingleton<IWorkspaceArtifactRepository>(workspace);
        ctx.Services.AddSingleton<IWorkspaceSessionService>(workspace);
        ctx.Services.AddSingleton(new Mock<IDashboardSnapshotService>().Object);
        ctx.Services.AddSingleton(new RuntimeReviewSessionService());
        ctx.Services.AddSingleton(new QualityReviewSessionService());
        ctx.Services.AddSingleton(_workflowReadiness.Object);
        AddCurrentWorkspace(ctx.Services, workspace, handler);

        return ctx.Render<Dashboard>();
    }

    /// <summary>The production current-workspace stack: Sample Project discovery + resolver + explorer context + projection.</summary>
    internal static void AddCurrentWorkspace(IServiceCollection services, WorkspaceArtifactRepository workspace, HttpMessageHandler handler)
    {
        var api = new SampleProjectsApiService(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") });
        var discovery = new BirkNext.Web.Services.SampleProjects.SampleProjectArtifactDiscoveryService(api);
        var resolver = new SampleProjectDocumentResolver(api, workspace, discovery);
        var explorers = new BirkNext.Web.Services.Explorers.ArtifactExplorerContext(workspace, resolver, discovery);
        services.AddSingleton(api);
        services.AddSingleton<BirkNext.Web.Services.SampleProjects.ISampleProjectArtifactDiscovery>(discovery);
        services.AddSingleton<ISampleProjectDocumentResolver>(resolver);
        services.AddSingleton<BirkNext.Web.Services.Explorers.IArtifactExplorerContext>(explorers);
        services.AddSingleton<ICurrentWorkspaceProjection>(new CurrentWorkspaceProjection(
            explorers, Mock.Of<IWorkspaceSessionRestoreService>(), Mock.Of<IWorkspacePersistenceApiService>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CurrentWorkspaceProjection>.Instance));
    }

    private static string Text(IRenderedComponent<Dashboard> cut, string testId) => cut.Find($"[data-testid={testId}]").TextContent.Trim();

    [Fact]
    public void SelectedSampleProjectShowsAvailabilityWithoutWorkspaceCopies()
    {
        var cut = RenderDashboardWithWorkspace("autorisasjon", CreateSampleProject("autorisasjon", "Autorisasjon"));

        cut.WaitForAssertion(() =>
        {
            Text(cut, "db-workspace-name").Should().Contain("Autorisasjon");
            Text(cut, "db-workspace-roles").Should().Be("5 document types");
            cut.Markup.Should().NotContain("artifacts loaded");
        });
    }

    [Fact]
    public void SelectedSampleProject_ArtifactCardsSayAvailable_NeverNotLoaded()
    {
        var cut = RenderDashboardWithWorkspace("autorisasjon", CreateSampleProject("autorisasjon", "Autorisasjon"));

        cut.WaitForAssertion(() => Text(cut, "db-workspace-roles").Should().Be("5 document types"));
        var cards = cut.FindAll("[data-testid=db-artifact-card]");
        cards.Should().HaveCount(5);
        foreach (var card in cards)
        {
            // One primary status; availability and "review not run" are secondary metadata, not three equal badges.
            card.QuerySelector("[data-testid=db-artifact-status]")!.TextContent.Should().EndWith("Ready to review");
            card.QuerySelector("[data-testid=db-artifact-meta]")!.TextContent.Should().StartWith("Available").And.Contain("Review not run");
            card.TextContent.Should().NotContain("Not loaded");
            card.TextContent.Should().NotContain("Not found");
            card.TextContent.Should().NotContain("healthy", "import metadata is not a review result");
        }
        var governance = cut.Find("[data-testid=db-health-area][data-key=governance]");
        governance.GetAttribute("data-status").Should().Be(nameof(DashboardStatus.NotAssessed), "five available roles are not a governance result");
        governance.TextContent.Should().NotContain("Roles available").And.NotContain("—");
    }

    [Fact]
    public void ArtifactCards_AndGovernance_ReadTheSameReviewStateAsRecommendedWorkflow()
    {
        var steps = ReviewDecisionSemanticsTests.MixedSteps();
        _workflowReadiness.Setup(w => w.GetReadinessAsync())
            .ReturnsAsync(ReviewDecisionSemanticsTests.Readiness(BirkNext.Web.Tests.Services.WorkspaceSnapshots.AllRoles("Person Module", "autorisasjon"), steps));

        var cut = RenderDashboardWithWorkspace("autorisasjon", CreateSampleProject("autorisasjon", "Autorisasjon"));

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=db-artifact-review]").Should().HaveCount(5));
        foreach (var (role, key) in new[] { ("Constitution", "ConstitutionExplorer"), ("Specification", "SpecificationExplorer"),
                     ("Plan", "PlanExplorer"), ("Tasks", "TaskExplorer"), ("DataModel", "DataModelExplorer") })
        {
            var expected = ArtifactReviewPresentation.Of(steps.Single(s => s.Key == key));
            var badge = cut.Find($"[data-testid=db-artifact-card][data-role={role}] [data-testid=db-artifact-review]");
            badge.GetAttribute("data-state").Should().Be(expected.State.ToString(), $"{role}: the Dashboard and the workflow read one review state");
            badge.TextContent.Should().EndWith(expected.Label);
        }
        // Approved: Constitution only, of the five document reviews that apply (Data Model's optional one included).
        var governance = cut.Find("[data-testid=db-health-area][data-key=governance]");
        governance.GetAttribute("data-status").Should().Be(nameof(DashboardStatus.AttentionNeeded));
        governance.TextContent.Should().Contain("1 of 5 document reviews approved").And.Contain("1 document review needs changes");
        var plan = cut.Find("[data-testid=db-artifact-card][data-role=Plan]");
        plan.QuerySelector("[data-testid=db-artifact-status]")!.TextContent.Should().EndWith("Review out of date");
        plan.QuerySelector("[data-testid=db-artifact-meta]")!.TextContent.Should().StartWith("Available").And.Contain("Review: review stale");
    }

    [Fact]
    public void PartialSampleProjectShowsCorrectAvailableArtifactCount()
    {
        var partialProject = new SampleProjectDto(
            "partial-project",
            "Partial Project",
            "PARTIAL",
            "A project with some artifacts",
            "C:\\SampleData\\partial",
            true,
            new[]
            {
                new SampleFileDto("spec.md", true, "Specification", "", "", true, false),
                new SampleFileDto("plan.md", true, "Plan", "", "", true, false),
                new SampleFileDto("tasks.md", true, "Tasks", "", "", true, false),
                new SampleFileDto("constitution.md", false, "Constitution", "", "", true, false),
                new SampleFileDto("data-model.md", false, "DataModel", "", "", true, false),
            });

        var cut = RenderDashboardWithWorkspace("partial-project", partialProject);

        cut.WaitForAssertion(() =>
        {
            Text(cut, "db-workspace-roles").Should().Be("3 document types");
            var constitution = cut.Find("[data-testid=db-artifact-card][data-role=Constitution]");
            constitution.TextContent.Should().Contain("Not found");
            constitution.GetAttribute("data-status").Should().Be(nameof(DashboardStatus.NotApplicable));
            cut.Find("[data-testid=db-health-area][data-key=compliance]").GetAttribute("data-status")
                .Should().Be(nameof(DashboardStatus.NotApplicable), "no constitution means nothing to check — not the same as not assessed");
        });
    }

    [Fact]
    public void ResolvedZeroArtifactSampleProjectShowsZeroAvailable()
    {
        var emptyProject = new SampleProjectDto(
            "empty-project",
            "Empty Project",
            "EMPTY",
            "A project with no artifacts",
            "C:\\SampleData\\empty",
            true,
            new[]
            {
                new SampleFileDto("constitution.md", false, "Constitution", "", "", true, false),
                new SampleFileDto("spec.md", false, "Specification", "", "", true, false),
                new SampleFileDto("plan.md", false, "Plan", "", "", true, false),
                new SampleFileDto("tasks.md", false, "Tasks", "", "", true, false),
                new SampleFileDto("data-model.md", false, "DataModel", "", "", true, false),
            });

        var cut = RenderDashboardWithWorkspace("empty-project", emptyProject);

        cut.WaitForAssertion(() =>
        {
            Text(cut, "db-workspace-roles").Should().Be("0 document types");
            cut.Markup.Should().NotContain("No workspace loaded", "a project without documents is still a loaded workspace");
            cut.Markup.Should().NotContain("artifacts loaded");
        });
    }


    [Fact]
    public void CatalogAPIFailureShowsUnavailableState()
    {
        var cut = RenderDashboardWithWorkspace(
            "autorisasjon",
            h => h.FailGetProjects(),
            null,
            CreateSampleProject("autorisasjon", "Autorisasjon"));

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Documents unavailable");
            cut.Markup.Should().NotContain("No workspace loaded");
            cut.Markup.Should().NotContain("document types");
        });
    }

    [Fact]
    public void GenericWorkspaceWithoutSampleProjectShowsAvailableRoles()
    {
        var cut = RenderDashboardWithWorkspace(null, null, workspace =>
        {
            workspace.AddArtifactRevision(WorkspaceArtifactType.Constitution, "# Rules", "rules.md", null, null, "File", select: true);
            workspace.AddArtifactRevision(WorkspaceArtifactType.Specification, "# Requirements", "requirements.md", null, null, "File", select: true);
        });

        cut.WaitForAssertion(() =>
        {
            Text(cut, "db-workspace-roles").Should().Be("2 document types");
            Text(cut, "db-workspace-name").Should().Contain("Unsaved workspace");
            var spec = cut.Find("[data-testid=db-artifact-card][data-role=Specification]");
            spec.QuerySelector("[data-testid=db-artifact-summary]")!.TextContent.Should().NotContain("requirements.md", "file paths are details, not the headline");
            spec.QuerySelector("[data-testid=db-artifact-details]")!.TextContent.Should().Contain("requirements.md");
        });
    }

    [Fact]
    public void EmptyWorkspaceDashboardDoesNotShowLoadedArtifacts()
    {
        var cut = RenderDashboardWithWorkspace(null);

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("No workspace loaded");
            cut.Find("[data-testid=db-health-area][data-key=governance]").GetAttribute("data-status")
                .Should().Be(nameof(DashboardStatus.NeedsSetup), "no workspace is not a 0% governance score");
            cut.FindAll("[data-status=ReadyToReview]").Should().BeEmpty("nothing can be reviewed without a workspace");
            cut.FindAll("[data-testid=db-artifact-card]").Should().OnlyContain(card => card.TextContent.Contains("Not found"));
        });
    }

    [Fact]
    public void LegacySampleProjectWorkspaceRestoresSlugOnlyAndSkipsPersistedArtifactCopies()
    {
        var cut = RenderDashboardWithWorkspace("autorisasjon", null, workspace =>
        {
            // Legacy unscoped session copies left behind by an old restore; the selected project's documents are what count.
            workspace.Set(WorkspaceArtifactType.Constitution, "old constitution content");
            workspace.Set(WorkspaceArtifactType.Specification, "old spec content");
        }, CreateSampleProject("autorisasjon", "Autorisasjon"));

        cut.WaitForAssertion(() =>
        {
            Text(cut, "db-workspace-name").Should().Contain("Autorisasjon");
            Text(cut, "db-workspace-roles").Should().Be("5 document types");
            cut.Markup.Should().NotContain("artifacts loaded");
        });
    }

    [Fact]
    public void SpecificationsInSeparatePacks_AreNotAnAmbiguity()
    {
        var project = CreateSampleProject("multi", "Multi") with
        {
            Files = [new SampleFileDto("specs/001-a/spec.md", true, "Specification", "", "", true, false),
                     new SampleFileDto("specs/002-b/spec.md", true, "Specification", "", "", true, false),
                     new SampleFileDto("plan.md", true, "Plan", "", "", true, false)]
        };

        var cut = RenderDashboardWithWorkspace("multi", project);

        cut.WaitForAssertion(() =>
        {
            Text(cut, "db-workspace-roles").Should().Be("2 feature packs · 2 document types");
            var spec = cut.Find("[data-testid=db-artifact-card][data-role=Specification]");
            spec.QuerySelector("[data-testid=db-artifact-summary]")!.TextContent.Should().Be("2 specifications");
            spec.QuerySelector("[data-testid=db-artifact-meta]")!.TextContent.Should().Contain("In 2 of 2 feature packs");
            spec.TextContent.Should().NotContain("Needs selection").And.NotContain("candidates");
            spec.GetAttribute("data-status").Should().Be(nameof(DashboardStatus.ReadyToReview), "two packs with one specification each are valid");
            cut.FindAll("[data-testid=db-attention-item][data-key^=ambiguous]").Should().BeEmpty();
            // The root plan is shared by both packs: no pack is missing a plan.
            cut.FindAll("[data-testid=db-attention-item][data-key=missing-Plan]").Should().BeEmpty();
        });
    }

    private SampleProjectDto CreateSampleProject(string slug, string name)
    {
        return new SampleProjectDto(
            slug,
            name,
            slug.ToUpper(),
            $"{name} description",
            $"C:\\SampleData\\{slug}",
            true,
            new[]
            {
                new SampleFileDto("constitution.md", true, "Constitution", "", "", true, false),
                new SampleFileDto("spec.md", true, "Specification", "", "", true, false),
                new SampleFileDto("plan.md", true, "Plan", "", "", true, false),
                new SampleFileDto("tasks.md", true, "Tasks", "", "", true, false),
                new SampleFileDto("data-model.md", true, "DataModel", "", "", true, false),
            });
    }

    public sealed class SampleProjectsHttpHandler : HttpMessageHandler
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
        private readonly Dictionary<string, SampleProjectDto> _projects = new(StringComparer.OrdinalIgnoreCase);
        private bool _throwOnGetProjects = false;

        public void SetProjects(params SampleProjectDto[] projects)
        {
            foreach (var project in projects)
                _projects[project.Slug] = project;
        }

        public void FailGetProjects()
        {
            _throwOnGetProjects = true;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath.Trim('/');

            if (request.Method == HttpMethod.Get && path == "api/sample-projects")
            {
                if (_throwOnGetProjects)
                    return Task.FromException<HttpResponseMessage>(new HttpRequestException("Catalog unavailable"));
                return Json(_projects.Values.ToList());
            }

            if (request.Method == HttpMethod.Get && path == "api/sample-projects/meta")
                return Json(new SampleProjectsMetaDto("C:\\SampleData", "test", true));

            // Document text for generic discovery; the bulk endpoint is absent, so discovery reads each file.
            if (request.Method == HttpMethod.Get && path.EndsWith("/file", StringComparison.Ordinal))
            {
                var filename = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.RequestUri.Query)["filename"].ToString();
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"# {filename}", Encoding.UTF8, "text/plain") });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static Task<HttpResponseMessage> Json<T>(T value)
        {
            var json = JsonSerializer.Serialize(value, JsonOptions);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }
}

