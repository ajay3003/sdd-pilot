using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.Api.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BirkNext.Api.Tests.Unit;

/// <summary>
/// Relationship integrity (2026-11-25, RI-01 and the readiness evidence gate): a saved Covers link is test DESIGN linkage — not a prepared, approved,
/// executed or passed test — and an audit that reads no test results never reports a project Ready.
/// </summary>
public sealed class RelationshipIntegrityTests : IDisposable
{
    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    public void Dispose() => _db.Dispose();
    private const string Project = "proj-ri";

    private async Task<Scenario> Seed(ScenarioKind kind)
    {
        var s = new Scenario { Title = $"{kind} {Guid.NewGuid():N}", Kind = kind, ProjectId = Project };
        _db.Scenarios.Add(s);
        await _db.SaveChangesAsync();
        return s;
    }

    private async Task Link(Scenario test, Scenario requirement)
    {
        _db.TraceLinks.Add(new TraceLink { ProjectId = Project, SourceId = test.Id, TargetId = requirement.Id, SourceKind = TraceLinkArtifactKind.Scenario,
            TargetKind = TraceLinkArtifactKind.Scenario, LinkType = TraceLinkType.Covers });
        await _db.SaveChangesAsync();
    }

    [Fact]
    public async Task RI01_a_linked_test_scenario_is_design_linkage_with_no_execution_evidence()
    {
        var requirement = await Seed(ScenarioKind.Requirement);
        await Seed(ScenarioKind.Requirement);
        await Link(await Seed(ScenarioKind.Test), requirement);

        var summary = await new TraceLinkService(_db).GetCoverageSummaryAsync(Project);

        (summary.EvidenceBasis, summary.ExecutionEvidence).Should().Be((CoverageSummary.TestDesignLinkage, CoverageSummary.NotEvaluated));
        (summary.TotalRequirements, summary.CoveredRequirements, summary.CoveragePercent).Should().Be((2, 1, 50.0));
    }

    [Fact]
    public async Task RI01_no_link_is_no_design_coverage_and_several_links_count_one_requirement_once()
    {
        var requirement = await Seed(ScenarioKind.Requirement);
        (await new TraceLinkService(_db).GetCoverageSummaryAsync(Project)).CoveredRequirements.Should().Be(0);
        await Link(await Seed(ScenarioKind.Test), requirement);
        await Link(await Seed(ScenarioKind.Test), requirement);
        var summary = await new TraceLinkService(_db).GetCoverageSummaryAsync(Project);
        (summary.TotalRequirements, summary.CoveredRequirements).Should().Be((1, 1), "unique requirements, not links, are counted");
        (await new TraceLinkService(_db).GetTraceabilityMatrixAsync(Project)).Single().LinkedTests.Should().HaveCount(2);
    }

    [Fact]
    public void RI01_graphql_describes_the_legacy_metric_as_design_linkage_and_keeps_its_fields()
    {
        var fields = typeof(CoverageSummary).GetProperties().Select(p => p.Name).ToList();
        fields.Should().Contain(["TotalRequirements", "CoveredRequirements", "NotCoveredRequirements", "CoveragePercent", "OrphanTests", "EvidenceBasis", "ExecutionEvidence"],
            "existing GraphQL fields stay (compatibility); the evidence basis is added");
        var source = File.ReadAllText(Path.Combine(Api(), "GraphQL", "CoverageSummaryObjectType.cs"));
        source.Should().Contain("TEST DESIGN LINKAGE").And.Contain("Not evidence of prepared, approved, executed or passed tests");
    }

    [Theory]
    [InlineData(100, 100.0)]
    [InlineData(95, 85.0)]
    public void Readiness_never_reports_ready_from_linkage_alone(int score, double linkedCoverage)
    {
        var drift = new SpecDriftReport { CoveragePercent = linkedCoverage, TotalRequirements = 10 };
        AIQaAuditorService.DetermineReadiness(score, linkedCoverage, drift).Should().Be(QaReadinessStatus.ReviewNeeded,
            "requirements with linked tests are not approved, executed or passed tests; this audit reads no test results");
    }

    [Fact]
    public void Readiness_still_reports_high_risk_when_the_signals_are_poor()
    {
        AIQaAuditorService.DetermineReadiness(40, 30.0, new SpecDriftReport { CoveragePercent = 30, TotalRequirements = 10 }).Should().Be(QaReadinessStatus.HighRisk);
    }

    private static string Api([System.Runtime.CompilerServices.CallerFilePath] string here = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(here)!);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "BirkNext.Api"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "BirkNext.Api");
    }
}
