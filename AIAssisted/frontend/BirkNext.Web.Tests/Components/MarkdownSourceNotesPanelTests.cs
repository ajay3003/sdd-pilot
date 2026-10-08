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

        rendered.Find("details > summary").TextContent.Should().Contain("3 blocks");
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

        spec.Markup.Should().Contain(tail, spec.Markup);
        constitution.Markup.Should().Contain(tail, constitution.Markup);
        plan.Markup.Should().Contain(tail, plan.Markup);
        tasks.Markup.Should().Contain(tail, tasks.Markup);
        dataModel.Markup.Should().Contain(tail, dataModel.Markup);
        spec.Find("article[data-source-block-id]").TextContent.Should().Contain(tail);
        spec.Find("article[data-source-block-id]").GetAttribute("data-source-block-id").Should().NotBeNullOrWhiteSpace();
    }
}
