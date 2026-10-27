using BirkNext.Web.Components;
using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using BirkNext.Web.Services.Explorers;
using BirkNext.Web.Tests.Pages;
using BirkNext.Web.Tests.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// The canonical Artifact Pack inventory as the pages show it: Document Quality Review lists the supporting documents it will read (and the
/// excluded ones) before the run and exactly what it read after; Plan Explorer states which documents its architecture and testing answers
/// come from; Task Explorer compares recorded revisions with exact provenance and keeps manual input as a fallback; design-system controls.
/// </summary>
public sealed class ArtifactScopeUiTests : BunitContext
{
    private readonly WorkspaceArtifactRepository _repository = new();
    private readonly MockSampleProjectDocumentResolver _samples = new();
    private readonly ArtifactExplorerContext _explorers;

    public ArtifactScopeUiTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        _samples.Repository = _repository;
        _explorers = new ArtifactExplorerContext(_repository, _samples, _samples);
    }

    private async Task<ArtifactPackInventory> ImportPersonAsync()
    {
        ArtifactScopeEvidenceTests.ImportFixture(_repository);
        await _explorers.SelectPackAsync("001-person");
        return await _explorers.GetPackInventoryAsync();
    }

    private IRenderedComponent<QualityReview> RenderQualityReview()
    {
        Services.AddSingleton<ISampleProjectDocumentResolver>(_samples);
        Services.AddSingleton<IWorkspaceSessionService>(_repository);
        Services.AddSingleton<IArtifactExplorerContext>(_explorers);
        Services.AddSingleton<IQualityReviewService>(ArtifactScopeEvidenceTests.ReviewService());
        Services.AddSingleton(new QualityReviewSessionService());
        Services.AddSingleton(Mock.Of<IDashboardSnapshotService>());
        Services.AddSingleton(Mock.Of<IDeliveryReadinessAssessmentService>());
        Services.AddSingleton(Mock.Of<IReportExportService>());
        return Render<QualityReview>();
    }

    [Fact]
    public async Task Quality_review_shows_the_document_scope_before_the_run()
    {
        await ImportPersonAsync();
        var cut = RenderQualityReview();

        cut.WaitForAssertion(() =>
        {
            cut.Find("[data-testid=qr-primary-note]").TextContent.Should().NotContain("supporting pack documentation is not read");
            var supporting = cut.FindAll("[data-testid=qr-supporting-doc]").Select(e => e.GetAttribute("data-path")).ToList();
            supporting.Should().Contain(["specs/001-person/research.md", "specs/001-person/security.md", "docs/privacy.md"]);
            cut.FindAll("[data-testid=qr-supporting-doc]").First(e => e.GetAttribute("data-path") == "docs/privacy.md").TextContent
                .Should().Contain("Explicitly referenced by Specification");
            cut.Find("[data-testid=qr-excluded-docs]").TextContent.Should().Contain("Sibling Artifact Pack 002-billing");
            cut.Find("[data-testid=qr-generated-docs]").TextContent.Should().Contain("autodoc/data-model.md").And.Contain("used only by Review Packs that request it");
        });
    }

    [Fact]
    public async Task Quality_review_provenance_lists_the_supporting_documents_it_read()
    {
        await ImportPersonAsync();
        var cut = RenderQualityReview();

        cut.WaitForAssertion(() => cut.Find("button.btn-primary").HasAttribute("disabled").Should().BeFalse());
        cut.Find("button.btn-primary").Click();

        cut.WaitForAssertion(() =>
        {
            var provenance = cut.Find("[data-testid=qr-provenance]");
            provenance.TextContent.Should().NotContain("Additional documentation read: none");
            cut.FindAll("[data-testid=qr-provenance-group]").Select(g => g.GetAttribute("data-category")).Should().Contain("Supporting authored documentation");
            var research = cut.FindAll("[data-testid=qr-provenance-document]").Single(d => d.GetAttribute("data-path") == "specs/001-person/research.md");
            research.TextContent.Should().Contain("fingerprint").And.Contain("read by");
        });
    }

    [Fact]
    public async Task Plan_explorer_names_the_documents_behind_architecture_and_testing()
    {
        var inventory = await ImportPersonAsync();
        var parser = new PlanAnalysisService();
        var plan = parser.Parse(ArtifactScopeEvidenceTests.PlanText);
        var set = PlanDocumentSet.From(plan, inventory, parser, ArtifactScopeEvidenceTests.PlanText);
        Services.AddSingleton<IPlanAnalysisService>(parser);

        var architecture = Render<PlanExplorerPanel>(p => p.Add(x => x.ParsedPlan, plan).Add(x => x.DocumentSet, set).Add(x => x.InitialView, "architecture"));
        architecture.Find("[data-testid=pe-architecture-scope]").TextContent.Should().Contain("The primary Plan states no architecture decisions");
        architecture.FindAll("[data-testid=pe-adr-source]").Select(e => e.TextContent).Should().Contain(["research.md", "001-drop-uniqueness.md"]);

        var testing = Render<PlanExplorerPanel>(p => p.Add(x => x.ParsedPlan, plan).Add(x => x.DocumentSet, set).Add(x => x.InitialView, "testing"));
        testing.Find("[data-testid=pe-testing-documents]").TextContent.Should().StartWith(set.Documents.Count.ToString());
        testing.Find("[data-testid=pe-testing-supporting]").TextContent.Should().Contain("Testing Stack").And.Contain("research.md");
        testing.FindAll("[data-testid=pe-testing-no-statements]").Should().BeEmpty("a supporting plan document states the testing approach");
    }

    [Fact]
    public void Task_changes_compare_recorded_revisions_with_exact_provenance_and_keep_manual_input_as_fallback()
    {
        var earlier = new TaskRevisionOption("Revision 1", "# Tasks\n\n- [ ] T001 Old\n", "Person/specs/001-person/tasks.md", "aaaaaaaaaaaaaaaa", 1, DateTimeOffset.Parse("2026-10-20T10:00:00Z"));
        var current = new TaskRevisionOption("Current", "# Tasks\n\n- [ ] T001 New\n", "Person/specs/001-person/tasks.md", "bbbbbbbbbbbbbbbb", 2, DateTimeOffset.Parse("2026-10-25T10:00:00Z"));

        var cut = Render<TaskChangesPanel>(p => p.Add(x => x.CurrentTasksText, current.Content).Add(x => x.PreviousRevisions, [earlier]).Add(x => x.CurrentVersion, current));
        cut.Find("[data-testid=tc-left-version]").TextContent.Should().Contain("rev 1").And.Contain("fingerprint aaaaaaaaaaaa").And.Contain("Person/specs/001-person/tasks.md");
        cut.Find("[data-testid=tc-right-version]").TextContent.Should().Contain("rev 2").And.Contain("fingerprint bbbbbbbbbbbb");
        cut.Find("[data-testid=tc-manual-compare]").HasAttribute("open").Should().BeFalse("a recorded revision exists: manual input is the fallback");
        cut.Find("[data-testid=tc-revision-compare-btn]").Click();
        cut.Find("[data-testid=tc-compared-versions]").TextContent.Should().Contain("rev 1").And.Contain("rev 2");

        var manual = Render<TaskChangesPanel>(p => p.Add(x => x.CurrentTasksText, current.Content));
        manual.Find("[data-testid=tc-manual-compare]").HasAttribute("open").Should().BeTrue();
        manual.Find("[data-testid=tc-no-recorded-revision]").TextContent.Should().Contain("No earlier recorded revision");
    }

    [Fact]
    public void Task_explorer_uses_design_system_buttons_and_keeps_declared_completion_declared()
    {
        const string text = "# Tasks\n\n## Phase 1\n\n- [x] T001 Write integration tests for search\n- [ ] T002 Build search\n";
        var cut = Render<TaskExplorerPanel>(p => p.Add(x => x.TasksText, text).Add(x => x.ParsedTasks, TaskExplorerService.Parse(text)));

        cut.Find("[data-testid=te-show-testing-work]").ClassList.Should().Contain("btn-secondary");
        cut.Find("[data-testid=te-summary-status]").TextContent.Should().Contain("Marked complete in task file");
    }

    [Fact]
    public void Unmapped_source_blocks_are_collapsed_technical_details_that_dqr_still_reads()
    {
        var cut = Render<MarkdownSourceNotesPanel>(p => p.Add(x => x.Blocks, [new MarkdownSourceNote("b1", 3, 3, "Paragraph", "f", "Loose paragraph")]));

        cut.Find("[data-testid=markdown-source-notes-summary]").TextContent.Should().Contain("Source content not structured · 1 block").And.NotContain("raw source notes");
        cut.Find("[data-testid=markdown-source-notes-scope]").TextContent.Should().Contain("Document Quality Review reads the full text");
        cut.FindAll("[data-testid=markdown-source-notes-detail]").Should().BeEmpty("collapsed by default");
        cut.Find("[data-testid=markdown-source-notes-toggle]").Click();
        cut.Find("[data-testid=markdown-source-notes-detail]").TextContent.Should().Contain("Loose paragraph", "no content is lost");
    }
}
