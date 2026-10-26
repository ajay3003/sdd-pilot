using BirkNext.Web.Components;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Web.Tests.Components;

public sealed class MarkdownSourceNotesPanelTests : BunitContext
{
    public MarkdownSourceNotesPanelTests()
    {
        Services.AddSingleton<IConstitutionAnalysisService, ConstitutionAnalysisService>();
        Services.AddSingleton<IPlanAnalysisService, PlanAnalysisService>();
        Services.AddSingleton<IDataModelAnalysisService, DataModelAnalysisService>();
    }

    [Fact]
    public void RendersFullSourceBlocksWithStableProvenanceAndNativeKeyboardExpandableDetails()
    {
        var blocks = new[]
        {
            new MarkdownSourceNote("Spec:ABC:12:one", 12, 12, "Text", "ABC", "Trailing prose BEGIN-VISIBLE"),
            new MarkdownSourceNote("Spec:ABC:19:two", 19, 19, "Text", "ABC", "Trailing prose BEGIN-VISIBLE"),
            new MarkdownSourceNote("Spec:ABC:30:end", 30, 30, "Text", "DEF", "END-VISIBLE complete text at EOF")
        };

        var rendered = Render<MarkdownSourceNotesPanel>(parameters => parameters.Add(p => p.Blocks, blocks));

        rendered.Find("[data-testid=markdown-source-notes-summary]").TextContent.Should().Contain("3 source blocks were not mapped to structured fields and are kept as raw source notes.");
        rendered.Markup.Should().NotContain("Lines 12", "line ranges and parser block types are technical detail, rendered on request");
        rendered.Find("[data-testid=markdown-source-notes-toggle]").Click();
        rendered.FindAll("article[data-source-block-id]").Select(x => x.GetAttribute("data-source-block-id"))
            .Should().Equal(blocks.Select(x => x.BlockId));
        rendered.Markup.Should().Contain("Trailing prose BEGIN-VISIBLE").And.Contain("END-VISIBLE complete text at EOF");
        rendered.FindAll("pre").Should().HaveCount(3);
    }

    [Fact]
    public void AllFiveProductionExplorerPanelsRenderTrailingUnmappedSourceTextWithBlockHooks()
    {
        const string tail = "VISIBLE-EXPLORER-TAIL-PROSE";
        var spec = Render<SpecExplorerPanel>(p => p.Add(x => x.InitialSpecMarkdown, $"# Requirements\n\n{tail}"));
        var constitution = Render<ConstitutionExplorerPanel>(p => p.Add(x => x.ConstitutionText, $"# Constitution\n\n{tail}"));
        var plan = Render<PlanExplorerPanel>(p => p.Add(x => x.PlanText, $"# Plan\n\n{tail}"));
        var tasks = Render<TaskExplorerPanel>(p => p.Add(x => x.TasksText, $"# Tasks\n\n{tail}"));
        var dataModel = Render<DataModelExplorerPanel>(p => p.Add(x => x.DataModelText, $"# Data Model\n\n{tail}"));

        // Every explorer keeps parser structure out of the primary view: the verbatim blocks render only on "Review source notes".
        static void Check<T>(IRenderedComponent<T> panel, string tail) where T : Microsoft.AspNetCore.Components.IComponent
        {
            panel.Markup.Should().NotContain(tail, "technical details render only on request");
            panel.Markup.Should().NotContain("Lines ", "no line ranges in the normal view");
            panel.Find("[data-testid=markdown-source-notes-summary]").TextContent.Should().Contain("not mapped to structured fields");
            panel.Find("[data-testid=markdown-source-notes-toggle]").Click();
            panel.Markup.Should().Contain(tail, "the authored text is preserved verbatim");
        }
        Check(spec, tail); Check(constitution, tail); Check(plan, tail); Check(tasks, tail); Check(dataModel, tail);
        spec.Find("article[data-source-block-id]").TextContent.Should().Contain(tail);
        spec.Find("article[data-source-block-id]").GetAttribute("data-source-block-id").Should().NotBeNullOrWhiteSpace();
    }
}
