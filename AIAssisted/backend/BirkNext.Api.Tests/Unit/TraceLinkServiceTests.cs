using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.Api.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Tests.Unit;

public class TraceLinkServiceTests : IDisposable
{
    private readonly AppDbContext _context;
    private readonly TraceLinkService _service;
    private const string ProjectId = "proj-trace-001";

    public TraceLinkServiceTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new AppDbContext(options);
        _service = new TraceLinkService(_context);
    }

    public void Dispose() => _context.Dispose();

    // ─── CreateAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateAsync_ValidLink_ReturnsTraceLink()
    {
        var req = await SeedScenarioAsync(ScenarioKind.Requirement);
        var test = await SeedScenarioAsync(ScenarioKind.Test);

        var result = await _service.CreateAsync(
            ProjectId, test.Id, "Scenario", req.Id, "Scenario",
            TraceLinkType.Covers, null, null, "corr-001");

        result.Errors.Should().BeEmpty();
        result.TraceLink.Should().NotBeNull();
        result.TraceLink!.SourceId.Should().Be(test.Id);
        result.TraceLink.TargetId.Should().Be(req.Id);
        result.TraceLink.LinkType.Should().Be(TraceLinkType.Covers);
        result.TraceLink.ProjectId.Should().Be(ProjectId);
    }

    [Fact]
    public async Task CreateAsync_SelfLink_ReturnsSelfLinkError()
    {
        var scenario = await SeedScenarioAsync(ScenarioKind.Requirement);

        var result = await _service.CreateAsync(
            ProjectId, scenario.Id, "Scenario", scenario.Id, "Scenario",
            TraceLinkType.Covers, null, null, "corr-001");

        result.TraceLink.Should().BeNull();
        result.Errors.Should().ContainSingle(e => e.Code == "SELF_LINK");
    }

    [Fact]
    public async Task CreateAsync_SourceNotFound_ReturnsSourceNotFoundError()
    {
        var req = await SeedScenarioAsync(ScenarioKind.Requirement);
        var missingId = Guid.NewGuid();

        var result = await _service.CreateAsync(
            ProjectId, missingId, "Scenario", req.Id, "Scenario",
            TraceLinkType.Covers, null, null, "corr-001");

        result.TraceLink.Should().BeNull();
        result.Errors.Should().ContainSingle(e => e.Code == "SOURCE_NOT_FOUND");
    }

    [Fact]
    public async Task CreateAsync_TargetNotFound_ReturnsTargetNotFoundError()
    {
        var test = await SeedScenarioAsync(ScenarioKind.Test);
        var missingId = Guid.NewGuid();

        var result = await _service.CreateAsync(
            ProjectId, test.Id, "Scenario", missingId, "Scenario",
            TraceLinkType.Covers, null, null, "corr-001");

        result.TraceLink.Should().BeNull();
        result.Errors.Should().ContainSingle(e => e.Code == "TARGET_NOT_FOUND");
    }

    [Fact]
    public async Task CreateAsync_SourceFromDifferentProject_ReturnsSourceNotFoundError()
    {
        var req = await SeedScenarioAsync(ScenarioKind.Requirement);
        var test = await SeedScenarioAsync(ScenarioKind.Test, projectId: "other-project");

        var result = await _service.CreateAsync(
            ProjectId, test.Id, "Scenario", req.Id, "Scenario",
            TraceLinkType.Covers, null, null, "corr-001");

        result.TraceLink.Should().BeNull();
        result.Errors.Should().ContainSingle(e => e.Code == "SOURCE_NOT_FOUND");
    }

    [Fact]
    public async Task CreateAsync_DuplicateLink_ReturnsDuplicateLinkError()
    {
        var req = await SeedScenarioAsync(ScenarioKind.Requirement);
        var test = await SeedScenarioAsync(ScenarioKind.Test);

        await _service.CreateAsync(
            ProjectId, test.Id, "Scenario", req.Id, "Scenario",
            TraceLinkType.Covers, null, null, "corr-001");

        var result = await _service.CreateAsync(
            ProjectId, test.Id, "Scenario", req.Id, "Scenario",
            TraceLinkType.Covers, null, null, "corr-002");

        result.TraceLink.Should().BeNull();
        result.Errors.Should().ContainSingle(e => e.Code == "DUPLICATE_LINK");
    }

    // ─── DeleteAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteAsync_ExistingLink_ReturnsDeletedId()
    {
        var req = await SeedScenarioAsync(ScenarioKind.Requirement);
        var test = await SeedScenarioAsync(ScenarioKind.Test);
        var created = await _service.CreateAsync(
            ProjectId, test.Id, "Scenario", req.Id, "Scenario",
            TraceLinkType.Covers, null, null, "corr-001");

        var result = await _service.DeleteAsync(
            created.TraceLink!.Id, ProjectId, "corr-002");

        result.IsSuccess.Should().BeTrue();
        result.DeletedId.Should().Be(created.TraceLink.Id.ToString());
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteAsync_WrongProject_ReturnsNotFoundError()
    {
        var req = await SeedScenarioAsync(ScenarioKind.Requirement);
        var test = await SeedScenarioAsync(ScenarioKind.Test);
        var created = await _service.CreateAsync(
            ProjectId, test.Id, "Scenario", req.Id, "Scenario",
            TraceLinkType.Covers, null, null, "corr-001");

        var result = await _service.DeleteAsync(
            created.TraceLink!.Id, "wrong-project", "corr-002");

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.Code == "NOT_FOUND");
    }

    // ─── GetTraceabilityMatrixAsync ──────────────────────────────────────────

    [Fact]
    public async Task GetTraceabilityMatrixAsync_CoveredRequirement_ReturnsCoveredStatus()
    {
        var req = await SeedScenarioAsync(ScenarioKind.Requirement);
        var test = await SeedScenarioAsync(ScenarioKind.Test);
        await _service.CreateAsync(
            ProjectId, test.Id, "Scenario", req.Id, "Scenario",
            TraceLinkType.Covers, null, null, "corr-001");

        var matrix = await _service.GetTraceabilityMatrixAsync(ProjectId);

        matrix.Should().ContainSingle(r => r.Requirement.Id == req.Id);
        var row = matrix.Single(r => r.Requirement.Id == req.Id);
        row.CoverageStatus.Should().Be(CoverageStatus.Covered);
        row.LinkedTests.Should().ContainSingle(lt => lt.Test.Id == test.Id);
    }

    [Fact]
    public async Task GetTraceabilityMatrixAsync_UncoveredRequirement_ReturnsNotCoveredStatus()
    {
        var req = await SeedScenarioAsync(ScenarioKind.Requirement);

        var matrix = await _service.GetTraceabilityMatrixAsync(ProjectId);

        var row = matrix.Should().ContainSingle().Subject;
        row.Requirement.Id.Should().Be(req.Id);
        row.CoverageStatus.Should().Be(CoverageStatus.NotCovered);
        row.LinkedTests.Should().BeEmpty();
    }

    [Fact]
    public async Task GetTraceabilityMatrixAsync_NeedsClarificationScenario_ExcludedFromMatrix()
    {
        await SeedScenarioAsync(ScenarioKind.NeedsClarification);

        var matrix = await _service.GetTraceabilityMatrixAsync(ProjectId);

        matrix.Should().BeEmpty();
    }

    [Fact]
    public async Task GetTraceabilityMatrixAsync_RelatedToLink_DoesNotCoverRequirement()
    {
        var req = await SeedScenarioAsync(ScenarioKind.Requirement);
        var test = await SeedScenarioAsync(ScenarioKind.Test);
        await _service.CreateAsync(
            ProjectId, test.Id, "Scenario", req.Id, "Scenario",
            TraceLinkType.RelatedTo, null, null, "corr-001");

        var matrix = await _service.GetTraceabilityMatrixAsync(ProjectId);

        var row = matrix.Single(r => r.Requirement.Id == req.Id);
        row.CoverageStatus.Should().Be(CoverageStatus.NotCovered);
        row.LinkedTests.Should().BeEmpty();
    }

    // ─── GetCoverageSummaryAsync ──────────────────────────────────────────────

    [Fact]
    public async Task GetCoverageSummaryAsync_MixedRequirements_ReturnsCorrectCounts()
    {
        var req1 = await SeedScenarioAsync(ScenarioKind.Requirement);
        var req2 = await SeedScenarioAsync(ScenarioKind.Requirement);
        var test = await SeedScenarioAsync(ScenarioKind.Test);
        await _service.CreateAsync(
            ProjectId, test.Id, "Scenario", req1.Id, "Scenario",
            TraceLinkType.Covers, null, null, "corr-001");

        var summary = await _service.GetCoverageSummaryAsync(ProjectId);

        summary.TotalRequirements.Should().Be(2);
        summary.CoveredRequirements.Should().Be(1);
        summary.NotCoveredRequirements.Should().Be(1);
        summary.CoveragePercent.Should().Be(50.0);
        summary.OrphanTests.Should().Be(0);
    }

    [Fact]
    public async Task GetCoverageSummaryAsync_OrphanTest_CountedInOrphanTests()
    {
        await SeedScenarioAsync(ScenarioKind.Requirement);
        await SeedScenarioAsync(ScenarioKind.Test);

        var summary = await _service.GetCoverageSummaryAsync(ProjectId);

        summary.OrphanTests.Should().Be(1);
        summary.CoveredRequirements.Should().Be(0);
    }

    [Fact]
    public async Task GetCoverageSummaryAsync_NoRequirements_ReturnZeroCoveragePercent()
    {
        var summary = await _service.GetCoverageSummaryAsync(ProjectId);

        summary.TotalRequirements.Should().Be(0);
        summary.CoveragePercent.Should().Be(0.0);
        summary.OrphanTests.Should().Be(0);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private async Task<Scenario> SeedScenarioAsync(
        ScenarioKind kind,
        string? projectId = null)
    {
        var scenario = new Scenario
        {
            Title = $"{kind} scenario {Guid.NewGuid():N}",
            Kind = kind,
            ProjectId = projectId ?? ProjectId,
        };
        _context.Scenarios.Add(scenario);
        await _context.SaveChangesAsync();
        return scenario;
    }
}
