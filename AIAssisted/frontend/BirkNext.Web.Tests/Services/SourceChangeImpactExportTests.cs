using BirkNext.SourceImpact;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

public sealed class SourceChangeImpactExportTests
{
    [Fact]
    public void Html_export_escapes_untrusted_report_text_and_includes_snapshot_identity()
    {
        var report = Report() with { ProjectDisplayName = "<script>alert(1)</script>" };

        var html = SourceChangeImpactExport.Html(report);

        html.Should().Contain("&lt;script&gt;").And.NotContain("<script>alert(1)</script>");
        html.Should().Contain(report.BaselineFingerprint).And.Contain(report.TargetFingerprint);
        html.Should().Contain("API test").And.Contain("API → database");
    }

    [Fact]
    public void Markdown_export_contains_impact_reasons_and_limits()
    {
        var markdown = SourceChangeImpactExport.Markdown(Report());

        markdown.Should().Contain("What may be affected").And.Contain("uses a changed API")
            .And.Contain("No runtime evidence was assessed");
    }

    [Fact]
    public void Unified_exports_preserve_project_snapshot_evidence_and_not_evaluated_reasons()
    {
        var snapshot = Guid.NewGuid();
        var report = new ImpactAnalysisRunReport(Guid.NewGuid(), DateTimeOffset.UtcNow, "import:orders-1", "orders-1", "Imported Orders",
            new("RequirementAndSourceSnapshotComparison", [Guid.NewGuid()], Guid.NewGuid(), snapshot, new string('a', 64), new string('b', 64)),
            [new("test:1", ImpactAnalysisFindingKind.Test, "Orders API test", ImpactAnalysisClassification.RelatedOnly, "ExplicitTraceLink", 1,
                "Linked test; execution is not verified.", [new("Requirements Traceability", "Covers link.", "tests/orders.feature", snapshot)])],
            [new("Journeys", ImpactAnalysisEvidenceStatus.NotEvaluated, "No journey provider was evaluated.")], null,
            ["No runtime evidence was assessed."]);

        var html = SourceChangeImpactExport.UnifiedHtml(report);
        var markdown = SourceChangeImpactExport.UnifiedMarkdown(report);

        html.Should().Contain("import:orders-1").And.Contain(snapshot.ToString()).And.Contain("No journey provider was evaluated");
        markdown.Should().Contain("Orders API test").And.Contain("No runtime evidence was assessed").And.Contain("NotEvaluated");
    }

    private static SourceChangeImpactReport Report()
    {
        var baseline = Guid.NewGuid();
        var current = Guid.NewGuid();
        return new SourceChangeImpactReport(baseline, "Orders", "before.zip", new string('a', 64), DateTimeOffset.UtcNow.AddDays(-1),
            current, "Orders", "after.zip", new string('b', 64), DateTimeOffset.UtcNow,
            [], [new TechnicalImpactItem("api", "Component", "orders-api", "Orders API", ImpactChangeDomain.Architecture,
                TechnicalImpactLevel.Indirect, "Confirmed", "Orders adapter uses a changed API.", [], [], [])], [], [],
            [new ImpactTestRecommendation(Guid.NewGuid(), "API test", "Integration", "The test uses a changed API.", [])], [],
            ["No runtime evidence was assessed."], "Not calculated")
        {
            RunId = Guid.NewGuid(),
            ProjectDisplayName = "Orders",
            Journeys = [new ImpactJourneyItem("journey", "Orders path", "API → database", "Suggested", current, [], "Source path only")]
        };
    }
}
