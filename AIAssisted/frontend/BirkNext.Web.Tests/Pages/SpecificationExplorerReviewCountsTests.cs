using BirkNext.Web.GraphQL;
using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Pages;

/// <summary>Specification Review Analysis counts unique logical entities, keeps review candidates separate and shows
/// deterministic relationships, against the real person-adapter Specification and the real deterministic analyzer.</summary>
public sealed class SpecificationExplorerReviewCountsTests : BunitContext
{
    private readonly WorkspaceArtifactRepository _workspace = new();
    private readonly MockSampleProjectDocumentResolver _documentResolver = new();
    private readonly ScenarioExtractionService _analyzer = new(new ExtractionConfiguration());
    private int _analysisRuns;

    public SpecificationExplorerReviewCountsTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<IWorkspaceSessionService>(_workspace);
        Services.AddSingleton<MarkdownRenderingService>();
        Services.AddSingleton<ISampleProjectDocumentResolver>(_documentResolver);
        Services.AddSingleton<BirkNext.Web.Services.SampleProjects.ISampleProjectArtifactDiscovery>(_documentResolver);
        Services.AddSingleton<BirkNext.Web.Services.Explorers.IArtifactExplorerContext>(
            new BirkNext.Web.Services.Explorers.ArtifactExplorerContext(_workspace, _documentResolver, _documentResolver));
        var extraction = new Mock<IScenarioExtractionService>();
        extraction.Setup(s => s.ExtractAsync(It.IsAny<string>(), It.IsAny<ExtractionProfile>(), It.IsAny<CancellationToken>()))
            .Returns<string, ExtractionProfile, CancellationToken>((text, profile, token) =>
            {
                _analysisRuns++;
                return _analyzer.ExtractAsync(text, profile, token);
            });
        Services.AddSingleton(extraction.Object);
        Services.AddSingleton(new FeatureVisibilityService());
        var session = new Mock<IExtractionSessionService>();
        session.Setup(s => s.LoadAsync()).ReturnsAsync((ExtractionSessionSnapshot?)null);
        Services.AddSingleton(session.Object);
        Services.AddSingleton(Mock.Of<IWorkspaceStateManager>());
    }

    [Fact]
    public void PersonAdapterAnalysisCountsUniqueEntitiesAndKeepsCandidateDistributionSeparate()
    {
        _documentResolver.SetProjectSpecification("person-adapter", PersonAdapterSpec());
        _documentResolver.SetSelectedProject("person-adapter");

        var cut = Render<SpecificationExplorer>();
        Analyze(cut);

        var markdown = PersonAdapterSpec();
        var projection = SpecificationReviewProjection.Build(markdown);
        var pipeline = _analyzer.ExtractAsync(markdown, ExtractionProfile.Speckit).Result;
        var candidates = SpecificationReviewProjection.BuildCandidates(projection, pipeline.Candidates);
        var summary = SpecificationReviewProjection.Summarize(projection, candidates);
        cut.WaitForAssertion(() =>
        {
            Metric(cut, "requirements-metric").Should().Contain($"{summary.Requirements}Requirements analyzed");
            Metric(cut, "tests-metric").Should().Contain($"{summary.Tests}Tests analyzed");
            Metric(cut, "clarifications-metric").Should().Contain($"{summary.Clarifications}Clarifications analyzed");
            cut.Find("[data-testid='candidates-metric'] span").TextContent.Should().Be(summary.Candidates.ToString());
            cut.Find("[data-testid='candidates-by-type']").TextContent.Should().Contain($"{summary.RequirementCandidates} requirement");
            cut.Find("[data-testid='src-filter-requirement']").TextContent.Should().Contain(summary.RequirementCandidates.ToString());
            cut.FindAll("[data-testid='spec-review-candidates'] > li").Should().HaveCount(Math.Min(summary.Candidates, SpecificationReviewProjection.CandidatePreviewSize));
            if (summary.Candidates > SpecificationReviewProjection.CandidatePreviewSize)
                cut.Find("[data-testid='src-show-all']").TextContent.Trim().Should().Be($"Show all {summary.Candidates}");
            else
                cut.FindAll("[data-testid='src-show-all']").Should().BeEmpty();
        });

        // Relationships come from document structure and explicit references; coverage stays unassessed.
        cut.Find("[data-testid='srp-tests-story']").TextContent.Should().Contain("/");
        cut.Find("[data-testid='srp-tests-linked']").TextContent.Should().Contain("/");
        cut.Find("[data-testid='srp-clar-linked']").TextContent.Should().Contain("/");
        cut.Find("[data-testid='se-traceability-state']").TextContent.Should().Contain("Not assessed for this Specification");
        cut.Markup.Should().NotContain("78 findings").And.NotContain("Coverage Analysis");
        _analysisRuns.Should().Be(1);
    }

    [Fact]
    public void ArtifactSwitchReplacesRelationshipsAndCountsWithoutLeakingThePreviousSpecification()
    {
        _documentResolver.SetProjectSpecification("project-a", "# A\n\n## Requirements\n\n- **FR-001**: A MUST hold.\n- **FR-002**: B MUST hold.\n");
        _documentResolver.SetProjectSpecification("project-b", "# B\n\n## Requirements\n\n- **FR-101**: C MUST hold.\n");
        _documentResolver.SetSelectedProject("project-a");

        var cut = Render<SpecificationExplorer>();
        Analyze(cut);
        cut.WaitForAssertion(() => Metric(cut, "requirements-metric").Should().StartWith("2"));
        cut.FindAll("[data-testid='srp-item'][data-entity='FR-001']").Should().ContainSingle();

        _documentResolver.SetSelectedProject("project-b");
        cut.Render();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll("[data-testid='srp-item'][data-entity='FR-001']").Should().BeEmpty();
            cut.FindAll("[data-testid='srp-item'][data-entity='FR-101']").Should().ContainSingle();
        });
        Analyze(cut);
        cut.WaitForAssertion(() =>
        {
            Metric(cut, "requirements-metric").Should().Be("1Requirements analyzed");
            cut.Markup.Should().NotContain("A MUST hold");
        });
    }

    [Fact]
    public void CustomFileNameSpecificationGetsRelationshipsAndCounts()
    {
        const string spec = "# School attendance\n\n## Requirements\n\n- **FR-001**: The system MUST record absence.\n\n## Clarifications\n\n### Session 2026-04-20\n\n- Q: Does FR-001 include late arrivals? → A: Yes.\n";
        _workspace.AddArtifactRevision(WorkspaceArtifactType.Specification, spec, "requirements-person.md", null, null, "File", select: true);

        var cut = Render<SpecificationExplorer>();
        cut.WaitForAssertion(() => cut.Find("[data-testid='artifact-explorer-file']").TextContent.Should().Be("requirements-person.md"));
        cut.Find("[data-testid='srp-clar-linked']").TextContent.Should().Be("1 / 1");
        Analyze(cut);
        cut.WaitForAssertion(() =>
        {
            Metric(cut, "requirements-metric").Should().Be("1Requirements analyzed");
            Metric(cut, "clarifications-metric").Should().Be("1Clarifications analyzed");
        });
    }

    [Theory]
    [InlineData("Dashboard.razor")]
    [InlineData("QualityReview.razor")]
    [InlineData("RecommendedWorkflow.razor")]
    public void DashboardQualityReviewAndWorkflowDoNotConsumeReviewCandidates(string page)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "BirkNext.Web", "Pages"))) dir = dir.Parent;
        var source = File.ReadAllText(Path.Combine(dir!.FullName, "BirkNext.Web", "Pages", page));

        // Review candidates are suggestions for human review; they must not become findings, failures or gates there.
        source.Should().NotContainAny("ExtractionPipelineResult", "ExtractionCandidate", "SpecReviewCandidate",
            "SpecificationReviewSummary", "IScenarioExtractionService", "SpecificationAnalys");
    }

    private void Analyze(IRenderedComponent<SpecificationExplorer> cut)
    {
        cut.WaitForAssertion(() => cut.Find("[data-testid='spec-explorer-analyze']").Should().NotBeNull());
        if (cut.Find("[data-testid='spec-review-state']").TextContent.Trim() != "Analyzed")
            cut.Find("[data-testid='spec-explorer-analyze']").Click();
    }

    private static string Metric(IRenderedComponent<SpecificationExplorer> cut, string testId) =>
        string.Concat(cut.Find($"[data-testid='{testId}']").Children.Select(c => c.TextContent.Trim()));

    private static string PersonAdapterSpec() => File.ReadAllText(TestDataHelper.ResolveFixturePath("person-adapter", "spec.md"));
}
