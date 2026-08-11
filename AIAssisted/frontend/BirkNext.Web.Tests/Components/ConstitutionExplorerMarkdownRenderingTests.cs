using System.Text.RegularExpressions;
using BirkNext.Web.Components;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Web.Tests.Components;

public sealed class ConstitutionExplorerMarkdownRenderingTests : BunitContext
{
    private readonly ConstitutionAnalysisService _analysisService = new();

    public ConstitutionExplorerMarkdownRenderingTests()
    {
        Services.AddSingleton<IConstitutionAnalysisService>(_analysisService);
        Services.AddSingleton<MarkdownRenderingService>();
    }

    [Fact]
    public void StandardRawText_PreservesTableRowsBeforeRendering()
    {
        var standard = ParseSingleStandard(SourceCodeLanguageConstitution());

        var lines = standard.RawText.Split('\n');
        var headerIndex = Array.FindIndex(lines, line => line.Contains("| Character | Replacement |", StringComparison.Ordinal));
        var separatorIndex = Array.FindIndex(lines, line => line.Contains("|-----------|-------------|", StringComparison.Ordinal));

        headerIndex.Should().BeGreaterThanOrEqualTo(0);
        separatorIndex.Should().Be(headerIndex + 1);
        lines[separatorIndex + 1].Should().Contain("\u00e6");
        lines[separatorIndex + 2].Should().Contain("\u00f8");
        lines[separatorIndex + 3].Should().Contain("\u00e5");
    }

    [Fact]
    public void SourceCodeLanguageStandard_RendersTableThroughMarkdownContent()
    {
        var cut = RenderExpandedStandards(SourceCodeLanguageConstitution());

        cut.Markup.Should().Contain("<table>");
        cut.Markup.Should().Contain("<th>Character</th>");
        cut.Markup.Should().Contain("<th>Replacement</th>");
        cut.Markup.Should().Contain("<td>\u00e6</td>");
        cut.Markup.Should().Contain("<td>ae</td>");
    }

    [Fact]
    public void GenericStandardMarkdownTable_RendersAsHtmlTable()
    {
        const string markdown = """
            # Test Constitution

            ## Platform Standards

            ### PS-01 Generic Table
            This standard contains a generic table.

            | Column A | Column B |
            |----------|----------|
            | Alpha    | Beta     |
            """;

        var cut = RenderExpandedStandards(markdown);

        cut.Markup.Should().Contain("<table>");
        cut.Markup.Should().Contain("<th>Column A</th>");
        cut.Markup.Should().Contain("<td>Alpha</td>");
        cut.Markup.Should().NotContain("| Column A | Column B | |----------|----------|");
    }

    [Fact]
    public void StandardsWithBulletsAndInlineCode_StillRenderMarkdown()
    {
        var cut = RenderExpandedStandards(SourceCodeLanguageConstitution());

        cut.Markup.Should().Contain("<ul>");
        cut.Markup.Should().Contain("<li>Entity/concept names: <code>Barn</code></li>");
        cut.Markup.Should().Contain("<code>HttpClient</code>");
    }

    [Fact]
    public void StandardContent_IsNotRenderedTwice()
    {
        var cut = RenderExpandedStandards(SourceCodeLanguageConstitution());

        Regex.Matches(cut.Markup, "Entity/concept names").Should().HaveCount(1);
        Regex.Matches(cut.Markup, "Character substitution").Should().HaveCount(1);
        Regex.Matches(cut.Markup, "<ul class=\"ce-rule-list\">").Should().BeEmpty();
    }

    private Bunit.IRenderedComponent<ConstitutionExplorerPanel> RenderExpandedStandards(string markdown)
    {
        var document = _analysisService.Parse(markdown);
        var cut = Render<ConstitutionExplorerPanel>(parameters => parameters
            .Add(component => component.ParsedDocument, document)
            .Add(component => component.InitialView, "standards"));

        cut.Find(".ce-standard-header").Click();

        return cut;
    }

    private BirkNext.Web.Models.ConstitutionStandard ParseSingleStandard(string markdown)
    {
        var document = _analysisService.Parse(markdown);
        document.Standards.Should().ContainSingle();
        return document.Standards[0];
    }

    private static string SourceCodeLanguageConstitution() =>
        "# Test Constitution\n\n" +
        "## Platform Standards\n\n" +
        "### PS-01 Source Code Language\n" +
        "All source code MUST use `HttpClient` for published contracts.\n\n" +
        "- Entity/concept names: `Barn`\n\n" +
        "**Character substitution**: When a retained Norwegian domain term contains the characters\n" +
        "`\u00e6`, `\u00f8`, or `\u00e5`, they MUST be replaced as follows in source code identifiers:\n\n" +
        "| Character | Replacement |\n" +
        "|-----------|-------------|\n" +
        "| \u00e6         | ae          |\n" +
        "| \u00f8         | oe          |\n" +
        "| \u00e5         | aa          |\n";
}
