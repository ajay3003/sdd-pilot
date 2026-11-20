using BirkNext.Web.Models.Analysis;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// Code Traceability shows the project's saved code links (read-only) instead of placeholder text: grouped by requirement or by file, filtered,
/// with an honest empty state and the boundary that a link is not proof of a correct implementation.
/// </summary>
public sealed class CodeTraceabilityPageTests : BunitContext
{
    private IRenderedComponent<CodeTraceability> Render(IReadOnlyList<SddCodeLinkSourceReference> links, string project = "skole")
    {
        var model = new Mock<IAnalysisPageModelService>();
        model.Setup(m => m.GetImplementationReviewModelAsync()).ReturnsAsync(new AnalysisPageModel { Title = "Code Traceability", Summary = new AnalysisSummary { CanRun = true } });
        Services.AddSingleton(model.Object);
        var workspace = new WorkspaceArtifactRepository();
        workspace.ProjectName = project;
        Services.AddSingleton<IWorkspaceSessionService>(workspace);
        var api = new Mock<ISddEvidenceApiService>();
        api.Setup(a => a.CodeLinksAsync(project, It.IsAny<CancellationToken>())).ReturnsAsync(links);
        Services.AddSingleton(api.Object);
        JSInterop.Mode = JSRuntimeMode.Loose;
        ComponentFactories.AddStub<BirkNext.Web.Shared.Components.Layout.PageHeader>();
        ComponentFactories.AddStub<BirkNext.Web.Components.AnalysisReadinessPanel>();
        return Render<CodeTraceability>();
    }

    private static SddCodeLinkSourceReference Link(string id, string requirement, string file) =>
        new(id, requirement, file, "Implementation", "Manual", new DateTimeOffset(2026, 11, 20, 10, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Saved_code_links_are_listed_by_requirement_or_by_file_and_can_be_filtered()
    {
        var cut = Render([Link("l1", "FR-001", "src/Reports.cs"), Link("l2", "FR-002", "src/Reports.cs"), Link("l3", "FR-001", "src/Lock.cs")]);

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=ct-group]").Select(g => g.GetAttribute("data-key")).Should().Equal("FR-001", "FR-002"));
        cut.Find("[data-testid=ct-count]").TextContent.Should().Contain("3 of 3 links").And.Contain("2 requirements").And.Contain("2 files");
        cut.Markup.Should().NotContain("will be displayed here");

        cut.Find("[data-testid=ct-by-file]").Click();
        cut.FindAll("[data-testid=ct-group]").Select(g => g.GetAttribute("data-key")).Should().Equal("src/Lock.cs", "src/Reports.cs");

        cut.Find("[data-testid=ct-filter]").Input("Lock");
        cut.FindAll("[data-testid=ct-link]").Select(l => l.GetAttribute("data-link")).Should().Equal("l3");
        cut.Find("[data-testid=ct-boundary]").TextContent.Should().Contain("not proof");
    }

    [Fact]
    public void Without_saved_links_the_page_says_where_links_come_from()
    {
        var cut = Render([]);
        cut.WaitForAssertion(() => cut.Find("[data-testid=ct-empty]").TextContent.Should().Contain("Implementation Review").And.Contain("never infers"));
    }
}
