using System.Text.RegularExpressions;
using BirkNext.Web.Components;
using BirkNext.Web.GraphQL;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Traceability &amp; Coverage inside Specification Explorer: a theme-neutral component (no dark palette, host type scale) that
/// stays compact — gap lists preview a few items — while its numbers come from the one TraceabilityModelBuilder projection.
/// Cross-artifact traceability is a link to the Requirements Traceability page, not embedded.
/// </summary>
public sealed class TraceabilityViewEmbeddedTests : BunitContext
{
    private static ExtractionCandidate Candidate(string title, ScenarioKind kind, string heading = "US1 Search") => new()
    {
        Title = title, Classification = kind, ClassificationSignal = ClassificationSignal.Rfc2119Uppercase,
        SourceBlockType = BlockType.UnorderedListItem, ContextHeading = heading,
    };

    private static IReadOnlyList<ExtractionCandidate> Requirements(int count, string? longText = null) =>
        Enumerable.Range(1, count).Select(i => Candidate(
            i == 1 && longText is not null ? $"FR-{i:000}: {longText}" : $"FR-{i:000}: The system MUST support capability {i}.",
            ScenarioKind.Requirement)).ToList();

    private IRenderedComponent<TraceabilityView> Render(IReadOnlyList<ExtractionCandidate> candidates) =>
        Render<TraceabilityView>(p => p.Add(c => c.Candidates, candidates).Add(c => c.Links, (IReadOnlyList<CandidateLinkEntry>)[]));

    private static void OpenGaps(IRenderedComponent<TraceabilityView> cut) =>
        cut.FindAll(".tv-toggle-btn").Single(b => b.TextContent.Contains("Gaps")).Click();

    [Fact]
    public void LightHost_HasNoPageShell_AndLinksToTheFullRequirementsTraceabilityPage()
    {
        var cut = Render(Requirements(3));

        var root = cut.Find(".tv-root");
        root.ClassName.Should().NotContainAny("dark", "page", "shell");
        cut.FindAll(".requirements-traceability-page, .rt-page, main").Should().BeEmpty("no full page is embedded");
        var link = cut.Find("[data-testid=tv-open-requirements-traceability]");
        link.GetAttribute("href").Should().Be("artifact-traceability");
        link.TextContent.Should().Be("Open Requirements Traceability →");
    }

    [Fact]
    public void GapLists_PreviewAFewItems_AndShowAllOnRequest()
    {
        var cut = Render(Requirements(12));
        OpenGaps(cut);

        var section = cut.Find("[data-testid=gap-missing-tests]");
        section.QuerySelectorAll(".tv-gap-items > li").Should().HaveCount(TraceabilityView.GapPreviewLimit, "not all 12 inline by default");
        var more = section.QuerySelector("[data-testid=tv-gap-more]")!;
        more.TextContent.Trim().Should().Be("Show all 12 requirements without tests");
        more.GetAttribute("aria-expanded").Should().Be("false");

        more.Click();

        section = cut.Find("[data-testid=gap-missing-tests]");
        section.QuerySelectorAll(".tv-gap-items > li").Should().HaveCount(12);
        section.QuerySelector("[data-testid=tv-gap-more]")!.TextContent.Trim().Should().Be($"Show first {TraceabilityView.GapPreviewLimit}");
    }

    [Fact]
    public void ShortGapLists_HaveNoShowAllControl()
    {
        var cut = Render(Requirements(TraceabilityView.GapPreviewLimit));
        OpenGaps(cut);

        cut.Find("[data-testid=gap-missing-tests]").QuerySelectorAll(".tv-gap-items > li").Should().HaveCount(TraceabilityView.GapPreviewLimit);
        cut.FindAll("[data-testid=gap-missing-tests] [data-testid=tv-gap-more]").Should().BeEmpty();
    }

    [Fact]
    public void FocusingAGapFromItsHealthCard_ShowsTheWholeList()
    {
        var cut = Render(Requirements(9));

        cut.Find("[data-testid=health-missing-tests]").Click();

        cut.Find("[data-testid=gap-missing-tests]").QuerySelectorAll(".tv-gap-items > li").Should().HaveCount(9);
    }

    [Fact]
    public void HealthCards_GapCounts_AndTheProjection_Agree()
    {
        var candidates = Requirements(8).Concat([Candidate("Given a caseworker when searching then results appear", ScenarioKind.Test, "Unrelated tests")]).ToList();
        var model = TraceabilityModelBuilder.Build(null, candidates, []);
        var cut = Render(candidates);

        string Card(string id) => cut.Find($"[data-testid={id}] strong").TextContent.Trim();
        Card("health-requirements").Should().Be(model.EligibleCount.ToString());
        Card("health-missing-tests").Should().Be(model.MissingTestsCount.ToString());
        Card("health-missing-us").Should().Be(model.MissingUserStoryCount.ToString());
        Card("health-missing-sc").Should().Be(model.MissingSuccessCriteriaCount.ToString());
        Card("health-orphan-tests").Should().Be(model.OrphanTestCount.ToString());

        OpenGaps(cut);
        cut.Find("[data-testid=gap-missing-tests] .tv-gap-count").TextContent.Trim().Should().Be(model.MissingTestsCount.ToString(),
            "the gap list counts the same projection as its health card");
    }

    [Fact]
    public void LongRequirementText_InTheGapPreview_StaysReadableAndExpandable()
    {
        var longText = "The system MUST " + string.Join(" ", Enumerable.Repeat("record every attendance change with its author and reason", 6)) + " END-MARKER.";
        var cut = Render(Requirements(7, longText));
        OpenGaps(cut);

        var item = cut.Find("[data-testid=gap-missing-tests] .tv-gap-items > li");
        item.TextContent.Should().Contain("END-MARKER", "the full text is in the document, wrapped and clamped, never cut");
        var toggle = item.QuerySelector(".traceability-item__toggle")!;
        toggle.GetAttribute("aria-expanded").Should().Be("false");
        toggle.Click();
        cut.Find("[data-testid=gap-missing-tests] .tv-gap-items > li .traceability-item__toggle").GetAttribute("aria-expanded").Should().Be("true");
    }

    [Fact]
    public void LongTestText_InTheOrphanPreview_StaysReadableAndExpandable()
    {
        var longTest = "Given " + string.Join(" ", Enumerable.Repeat("a caseworker with a pupil in two schools", 6)) + " then TEST-END.";
        var candidates = Requirements(1).Concat(Enumerable.Range(1, 7).Select(i =>
            Candidate(i == 1 ? longTest : $"Given test {i} when run then passes", ScenarioKind.Test, "Unrelated tests"))).ToList();
        var cut = Render(candidates);
        cut.Find("[data-testid=health-orphan-tests]").Click();

        var items = cut.FindAll(".tv-gap-items > li");
        items.Should().NotBeEmpty();
        cut.Markup.Should().Contain("TEST-END");
        cut.FindAll(".traceability-item__toggle").Should().NotBeEmpty();
    }

    [Fact]
    public void RequirementList_IsAPlainList_WithTheSelectedRequirementMarkedCurrent()
    {
        var cut = Render(Requirements(3));

        cut.FindAll(".tv-root [role=listbox], .tv-root [role=option]").Should().BeEmpty("an option cannot contain the item's buttons");
        cut.FindAll(".tv-req-item-btn")[1].Click();

        var buttons = cut.FindAll(".tv-req-item-btn");
        buttons[1].GetAttribute("aria-current").Should().Be("true");
        buttons.Where((_, i) => i != 1).Should().OnlyContain(b => b.GetAttribute("aria-current") == null);
    }

    [Fact]
    public void NoRequirements_IsANeutralHostStyledState()
    {
        var cut = Render([]);

        cut.Find(".tv-empty-msg").TextContent.Should().Contain("No requirements available");
        cut.FindAll(".tv-root").Should().BeEmpty("no empty dashboard is drawn");
    }

    // ── Stylesheet guard: the component carries no page theme ────────────────────────────────────────────────────────

    private static string Stylesheet(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "BirkNext.Web", "Components"))) dir = dir.Parent;
        dir.Should().NotBeNull("the test runs inside the frontend folder");
        return File.ReadAllText(Path.Combine(dir!.FullName, "BirkNext.Web", "Components", name));
    }

    [Theory]
    [InlineData("TraceabilityView.razor.css")]
    [InlineData("TraceabilityItemText.razor.css")]
    public void Stylesheets_AreThemeNeutral_WithReadableType(string name)
    {
        var css = Stylesheet(name);
        // Rules (not the token definitions on .tv-root) must not use undefined dark tokens or navy/pastel literals.
        var rules = Regex.Replace(css, @"--tv-[a-z-]+:\s*[^;]+;", "");
        rules.Should().NotMatchRegex(@"--clr-surface-[012]|--clr-surface-alt|--clr-accent|--clr-text,", "these tokens are undefined and fell back to navy");
        rules.Should().NotMatchRegex(@"#(0f172a|1e293b|263043|334155|1e3a5f|0d1929)\b", "no navy surfaces");
        rules.Should().NotMatchRegex(@"color:\s*#(4ade80|f87171|93c5fd|5eead4|c4b5fd|fbbf24|94a3b8|cbd5e1|e2e8f0)\b", "no text colours made for a dark background");
        css.Should().NotContain("::deep").And.NotContain(":global", "no styles leak out of the component");
        foreach (Match size in Regex.Matches(css, @"font-size:\s*([0-9.]+)rem"))
            double.Parse(size.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture).Should().BeGreaterThanOrEqualTo(0.72, "type follows the host scale");
    }
}
