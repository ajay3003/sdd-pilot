using BirkNext.Web.Components;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Web.Tests.Components;

/// <summary>"Source content not structured": one shared, reviewer-first panel for every Explorer. Summary and potential mapping gaps
/// are visible; grouped categories and the technical raw source are collapsed; every block keeps its exact provenance.</summary>
public sealed class MarkdownSourceNotesPanelTests : BunitContext
{
    public MarkdownSourceNotesPanelTests()
    {
        Services.AddSingleton<IConstitutionAnalysisService, ConstitutionAnalysisService>();
        Services.AddSingleton<IPlanAnalysisService, PlanAnalysisService>();
        Services.AddSingleton<IDataModelAnalysisService, DataModelAnalysisService>();
    }

    private static MarkdownSourceNote Note(int line, string text, string type = "Text", string? section = null) =>
        new($"Doc:F:{line}:x", line, line, type, "F" + line, text, section);

    [Fact]
    public void Summary_is_neutral_and_the_raw_source_keeps_exact_provenance_on_request()
    {
        var blocks = new[]
        {
            Note(12, "Trailing prose BEGIN-VISIBLE", section: "Summary"),
            Note(13, "More prose in the same section", section: "Summary"),
            Note(30, "```", "FencedCodeStart"),
            Note(31, "END-VISIBLE complete text at EOF", "FencedCodeLine"),
        };

        var cut = Render<MarkdownSourceNotesPanel>(p => p.Add(x => x.Blocks, blocks));

        var summary = cut.Find("[data-testid=markdown-source-notes-summary]").TextContent;
        summary.Should().Contain("Source content not structured · 4 blocks")
            .And.Contain("These blocks were preserved from the source document but were not deterministically mapped to structured Explorer fields.")
            .And.Contain("No likely mapping gaps detected").And.NotContain("raw notes").And.NotContain("Error").And.NotContain("Failed");
        cut.FindAll("[data-testid=markdown-source-gap]").Should().BeEmpty();
        cut.Markup.Should().NotContain("Lines 12", "line ranges and parser block types are technical detail");
        cut.Markup.Should().NotContain("BEGIN-VISIBLE", "groups are collapsed by default");
        cut.FindAll("[data-testid=markdown-source-notes-detail]").Should().BeEmpty("the technical raw source is collapsed by default");

        cut.Find("[data-testid=markdown-source-notes-toggle]").Click();
        cut.FindAll("article[data-source-block-id]").Select(x => x.GetAttribute("data-source-block-id")).Should().Equal(blocks.Select(b => b.BlockId));
        cut.Find("[data-testid=markdown-source-notes-detail]").TextContent.Should().Contain("Lines 30–30 · FencedCodeStart · Structural")
            .And.Contain("Trailing prose BEGIN-VISIBLE").And.Contain("END-VISIBLE complete text at EOF");
    }

    [Fact]
    public void Counts_add_up_and_structural_scaffolding_is_not_grouped_in_the_reviewer_view()
    {
        var blocks = new[]
        {
            Note(1, "<!--"), Note(2, "Sync Impact Report"), Note(3, "Version change: 1.0.0 → 1.0.1"), Note(4, "-->"),
            Note(6, "Build the Person Module — the platform's single source of truth.", section: "Summary"),
            Note(8, "|"), Note(9, "Source: docs/person-domain-model-no.md"),
        };
        var cut = Render<MarkdownSourceNotesPanel>(p => p.Add(x => x.Blocks, blocks));

        var counts = cut.FindAll("[data-testid=markdown-source-notes-counts] li").ToDictionary(li => li.GetAttribute("data-category")!, li => int.Parse(li.QuerySelector("strong")!.TextContent));
        counts.Values.Sum().Should().Be(blocks.Length);
        counts.Should().Contain(new Dictionary<string, int> { ["Structural"] = 3, ["ChangeHistory"] = 2, ["Informational"] = 1, ["Reference"] = 1 });
        cut.FindAll("[data-testid=markdown-source-category-structural]").Should().BeEmpty("scaffolding stays in the raw source only");

        cut.Find("[data-testid=markdown-source-category-toggle-history]").Click();
        var group = cut.Find("[data-testid=markdown-source-group]");
        group.GetAttribute("data-source-block-ids").Should().Be($"{blocks[1].BlockId} {blocks[2].BlockId}");
        group.TextContent.Should().Contain("Version change: 1.0.0 → 1.0.1").And.Contain("lines 2–3");
        group.QuerySelector("button")!.GetAttribute("aria-expanded").Should().Be("false");
        group.QuerySelector("button")!.Click();
        cut.Find("[data-testid=markdown-source-exact]").TextContent.Should().Contain("line 2 · Text").And.Contain("Sync Impact Report");
    }

    [Fact]
    public void Potential_mapping_gaps_are_listed_first_with_reason_and_source()
    {
        const string plan = "# Plan\n\n## Phase Dependencies\n\nPhase 2 (Foundational) ← BLOCKS all user stories\n";
        var document = new PlanAnalysisService().Parse(plan);
        var cut = Render<MarkdownSourceNotesPanel>(p => p.Add(x => x.Blocks, document.UnmappedSourceBlocks).Add(x => x.Context, UnmappedSourceContext.For(document)));

        cut.Find("[data-testid=markdown-source-notes-verdict]").TextContent.Should().Contain("1 potential mapping gap");
        var gap = cut.Find("[data-testid=markdown-source-gap]");
        gap.GetAttribute("data-potential-kind").Should().Be("Dependency");
        gap.TextContent.Should().Contain("Potential mapping gap · Dependency").And.Contain("Phase 2 (Foundational) ← BLOCKS all user stories")
            .And.Contain("Reason:").And.Contain("Source: line 5").And.Contain("Phase Dependencies");
        gap.QuerySelector("[data-testid=markdown-source-show]")!.GetAttribute("aria-label").Should().Be("Show source for this potential mapping gap");
        document.Dependencies.Should().BeEmpty("a gap is a review observation; nothing is promoted into the structured model");
    }

    [Fact]
    public void AllFiveProductionExplorerPanelsUseTheSharedPanelAndKeepTheTextOnRequest()
    {
        const string tail = "VISIBLE-EXPLORER-TAIL-PROSE";
        var spec = Render<SpecExplorerPanel>(p => p.Add(x => x.InitialSpecMarkdown, $"# Requirements\n\n{tail}"));
        var constitution = Render<ConstitutionExplorerPanel>(p => p.Add(x => x.ConstitutionText, $"# Constitution\n\n{tail}"));
        var plan = Render<PlanExplorerPanel>(p => p.Add(x => x.PlanText, $"# Plan\n\n{tail}"));
        var tasks = Render<TaskExplorerPanel>(p => p.Add(x => x.TasksText, $"# Tasks\n\n{tail}"));
        var dataModel = Render<DataModelExplorerPanel>(p => p.Add(x => x.DataModelText, $"# Data Model\n\n{tail}"));

        static void Check<T>(IRenderedComponent<T> panel, string tail) where T : Microsoft.AspNetCore.Components.IComponent
        {
            panel.Markup.Should().NotContain(tail, "source content renders only on request");
            panel.Markup.Should().NotContain("Lines ", "no line ranges in the normal view");
            panel.Find("[data-testid=markdown-source-notes-summary]").TextContent.Should().Contain("Source content not structured");
            panel.Find("[data-testid=markdown-source-notes-toggle]").Click();
            panel.Markup.Should().Contain(tail, "the authored text is preserved verbatim");
        }
        Check(spec, tail); Check(constitution, tail); Check(plan, tail); Check(tasks, tail); Check(dataModel, tail);
        spec.Find("article[data-source-block-id]").GetAttribute("data-source-block-id").Should().NotBeNullOrWhiteSpace();
    }
}
