using BirkNext.Api.Services.DeliveryReporting;
using BirkNext.DeliveryReporting;
using FluentAssertions;
using Xunit;
using static BirkNext.Api.Tests.DeliveryReporting.DeliveryReportingFixtures;

namespace BirkNext.Api.Tests.DeliveryReporting;

public sealed class DeliveryReportExportTests
{
    private static readonly TimeZoneInfo Oslo = TimeZoneInfo.FindSystemTimeZoneById("Europe/Oslo");

    private static WeeklyDeliveryReport Report((string, string)[] baseline, (string, string)[] current, string project = "M2LB",
        DateTimeOffset? baselineAt = null)
    {
        var b = Content(Parse(baseline), baselineAt ?? Monday, "Monday baseline");
        var c = Content(Parse(current), Friday, "Friday current");
        return WeeklyReportBuilder.Build(Guid.NewGuid(), Guid.NewGuid(), project, Friday.AddHours(1), b, c, DeliverySnapshotComparison.Compare(b, c), Oslo);
    }

    private static (string, string)[] TwoFeatures(int childDone, int caseDone) =>
    [
        .. ChildSearch(8, childDone),
        ("Frontend/specs/002-case-overview/spec.md", Spec("Case overview", DefaultAcceptance)),
        ("Frontend/specs/002-case-overview/tasks.md", Tasks(4, caseDone)),
    ];

    [Fact]
    public void Report_HasWeekCoverageMetricsAndFocus()
    {
        var report = Report(TwoFeatures(3, 1), TwoFeatures(7, 1));

        report.Title.Should().Be("M2LB Weekly Delivery Status");
        report.WeekLabel.Should().Be("2026-W44");
        report.Coverage.Should().Be(DeliveryCoverage.Complete);
        report.HasExplicitStatusEvidence.Should().BeFalse();
        report.Metrics.Select(m => m.Key).Should().NotContain(["ready-for-test", "testing", "blocked"], "only statuses the engine can support are shown");
        report.Metrics.Single(m => m.Key == "features-changed").Value.Should().Be(1);
        report.Metrics.Single(m => m.Key == "tasks-completed").Value.Should().Be(4);
        report.NextWeekFocus.Should().Contain(f => f.Text.StartsWith("2 features remain in progress"));
    }

    [Fact]
    public void MarkdownExport_IsManagementFirst_AndHidesUnchangedStoriesByDefault()
    {
        var report = Report(TwoFeatures(3, 1), TwoFeatures(7, 1));

        var export = DeliveryReportExporter.Export(report, DeliveryExportFormat.Markdown, new());

        export.FileName.Should().Be("M2LB-Weekly-Delivery-Report-2026-W44.md");
        export.Content.Should().StartWith("# M2LB Weekly Delivery Status — 2026-W44");
        export.Content.Should().Contain("| Child search | Frontend | In progress | In progress | Progressed | 3/8 → 7/8 | 1 story · 1 phase | – |");
        export.Content.Should().NotContain("Case overview |", "unchanged stories are off by default");
        export.Content.Should().Contain("1 unchanged feature is not listed");
        export.Content.Should().NotContain("specs/001-child-search", "no file paths without the evidence appendix");
        export.Content.Should().NotContain(report.Current.Fingerprint).And.NotContain(report.ReportId.ToString());
        export.Content.Should().Contain("Nothing needs attention.");
    }

    [Fact]
    public void Options_IncludeUnchangedStories_AndEvidenceAppendix()
    {
        var report = Report(TwoFeatures(3, 1), TwoFeatures(7, 1));

        var md = DeliveryReportExporter.Export(report, DeliveryExportFormat.Markdown, new(IncludeEvidenceAppendix: true, IncludeUnchangedStories: true)).Content;

        md.Should().Contain("## All features").And.Contain("| Case overview |");
        md.Should().Contain("## Appendix: evidence").And.Contain("Frontend/specs/001-child-search/tasks.md").And.Contain(report.Current.Fingerprint);
    }

    [Fact]
    public void HtmlExport_IsStandalone_AndStatusIsText()
    {
        var report = Report(TwoFeatures(3, 1), TwoFeatures(7, 1));

        var export = DeliveryReportExporter.Export(report, DeliveryExportFormat.Html, new());

        export.FileName.Should().Be("M2LB-Weekly-Delivery-Report-2026-W44.html");
        export.ContentType.Should().Be("text/html");
        export.Content.Should().StartWith("<!doctype html>").And.Contain("<style>").And.Contain("@media print");
        export.Content.Should().NotContain("<script").And.NotContain("<link");
        export.Content.Should().Contain("<span class=\"badge progressed\">Progressed</span>");
        export.Content.Should().Contain("3/8 → 7/8");
    }

    [Fact]
    public void SpecialCharacters_AreEscaped_AndTheFileNameIsSafe()
    {
        var spec = Spec("Search <b>kids</b> | fast & safe", DefaultAcceptance);
        (string, string)[] baseline = [("Frontend/specs/001-child-search/spec.md", spec), ("Frontend/specs/001-child-search/tasks.md", Tasks(4, 1))];
        (string, string)[] current = [("Frontend/specs/001-child-search/spec.md", spec), ("Frontend/specs/001-child-search/tasks.md", Tasks(4, 2))];
        var report = Report(baseline, current, project: "M2LB / Barnevern & Co");

        WeeklyReportBuilder.FileName(report, "md").Should().Be("M2LB-Barnevern-Co-Weekly-Delivery-Report-2026-W44.md");
        DeliveryReportExporter.Html(report, new()).Should().Contain("Search &lt;b&gt;kids&lt;/b&gt; | fast &amp; safe").And.NotContain("<b>kids</b>");
        DeliveryReportExporter.Markdown(report, new()).Should().Contain(@"Search \<b\>kids\</b\> \| fast & safe");
    }

    [Fact]
    public void BaselineBeforeTheWeek_IsPartialCoverage_WithAWarning()
    {
        var report = Report(ChildSearch(8, 3), ChildSearch(8, 7), baselineAt: new DateTimeOffset(2026, 10, 23, 10, 0, 0, TimeSpan.FromHours(2)));

        report.Coverage.Should().Be(DeliveryCoverage.Partial);
        report.CoverageNotes.Should().ContainSingle().Which.Should().Be("Baseline is from 23 Oct 2026. The report may include changes from 23–26 Oct, before this week started.");
        DeliveryReportExporter.Markdown(report, new()).Should().Contain("**Coverage:** Partial").And.Contain("> **Coverage note:** Baseline is from 23 Oct 2026");
    }

    [Fact]
    public void BaselineLaterInTheWeek_SaysWhatIsNotIncluded()
    {
        var report = Report(ChildSearch(8, 3), ChildSearch(8, 7), baselineAt: new DateTimeOffset(2026, 10, 28, 10, 0, 0, TimeSpan.FromHours(1)));

        report.Coverage.Should().Be(DeliveryCoverage.Partial);
        report.CoverageNotes.Single().Should().Contain("Changes from 26–28 Oct are not included");
    }

    [Fact]
    public void FrozenReport_ExportsIdentically_AfterJsonRoundTrip()
    {
        var report = Report(TwoFeatures(3, 1), TwoFeatures(7, 1));
        var json = System.Text.Json.JsonSerializer.Serialize(report, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        var restored = System.Text.Json.JsonSerializer.Deserialize<WeeklyDeliveryReport>(json, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;

        DeliveryReportExporter.Markdown(restored, new(true, true)).Should().Be(DeliveryReportExporter.Markdown(report, new(true, true)));
        DeliveryReportExporter.Html(restored, new(true, true)).Should().Be(DeliveryReportExporter.Html(report, new(true, true)));
    }
}
