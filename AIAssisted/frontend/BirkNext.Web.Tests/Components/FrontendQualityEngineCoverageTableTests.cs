using BirkNext.Web.Components;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Xunit;

namespace BirkNext.Web.Tests.Components;

/// <summary>Engine coverage table: search, release-impact filter, blocking-first sort, honest counts and an explicit empty filtered state.</summary>
public sealed class FrontendQualityEngineCoverageTableTests : BunitContext
{
    private static FrontendQualityEngineCoverageRow Row(string engine, FrontendQualityReleaseImpact impact, string result = "Assessed") =>
        new(engine, "Recommended", "Rendered pages", "Browser evidence", null, result, null, impact);

    private static readonly FrontendQualityEngineCoverageRow[] Rows =
    [
        Row("Accessibility", FrontendQualityReleaseImpact.Satisfied),
        Row("Security headers", FrontendQualityReleaseImpact.Blocking, "Not assessed"),
        Row("Performance", FrontendQualityReleaseImpact.NonBlocking),
        Row("Content", FrontendQualityReleaseImpact.NotApplicable, "Not applicable"),
    ];

    [Fact]
    public void Search_and_impact_filter_narrow_rows_and_report_counts()
    {
        var cut = Render<FrontendQualityEngineCoverageTable>(p => p.Add(c => c.Rows, Rows));
        cut.Find("[data-testid='fqr-coverage-count']").TextContent.Should().Be("Showing 4 of 4");

        cut.Find("[data-testid='fqr-coverage-search']").Input("header");
        cut.FindAll("[data-testid='fqr-engine-coverage-row']").Should().HaveCount(1);
        cut.Find("[data-testid='fqr-coverage-count']").TextContent.Should().Be("Showing 1 of 4");

        cut.Find("[data-testid='fqr-coverage-clear']").Click();
        cut.Find("[data-testid='fqr-coverage-impact']").Change(nameof(FrontendQualityReleaseImpact.NonBlocking));
        cut.FindAll("[data-testid='fqr-engine-coverage-row']").Should().ContainSingle().Which.TextContent.Should().Contain("Performance");
    }

    [Fact]
    public void A_filter_that_matches_nothing_says_so_instead_of_an_empty_table()
    {
        var cut = Render<FrontendQualityEngineCoverageTable>(p => p.Add(c => c.Rows, Rows));
        cut.Find("[data-testid='fqr-coverage-search']").Input("zzz-nothing");
        cut.FindAll("[data-testid='fqr-engine-coverage-row']").Should().BeEmpty();
        cut.Find("[data-testid='fqr-coverage-none']").TextContent.Should().Contain("No engine matches");
        cut.Find("[data-testid='fqr-coverage-count']").TextContent.Should().Be("Showing 0 of 4");
    }

    [Fact]
    public void Blocking_first_sort_puts_blocking_engines_on_top_and_small_tables_have_no_controls()
    {
        var cut = Render<FrontendQualityEngineCoverageTable>(p => p.Add(c => c.Rows, Rows));
        cut.Find("[data-testid='fqr-coverage-sort']").Change("impact");
        cut.FindAll("[data-testid='fqr-engine-coverage-row']")[0].TextContent.Should().Contain("Security headers");

        var small = Render<FrontendQualityEngineCoverageTable>(p => p.Add(c => c.Rows, Rows.Take(2).ToArray()));
        small.FindAll("[data-testid='fqr-coverage-filters']").Should().BeEmpty();
    }
}
