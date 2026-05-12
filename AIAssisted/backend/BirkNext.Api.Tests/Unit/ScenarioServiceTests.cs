using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.Api.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Tests.Unit;

public class ScenarioServiceTests : IDisposable
{
    private readonly AppDbContext _context;
    private readonly ScenarioService _service;

    public ScenarioServiceTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new AppDbContext(options);
        _service = new ScenarioService(_context);
    }

    [Fact]
    public async Task CreateAsync_EmptyTitle_ReturnsTitleRequiredError()
    {
        var result = await _service.CreateAsync(
            title: "",
            description: null,
            kind: ScenarioKind.Test,
            projectId: "proj-001",
            correlationId: "corr-001");

        result.Errors.Should().ContainSingle(e => e.Code == "TITLE_REQUIRED");
        result.Scenario.Should().BeNull();
    }

    [Fact]
    public async Task CreateAsync_WhiteSpaceTitle_ReturnsTitleRequiredError()
    {
        var result = await _service.CreateAsync(
            title: "   ",
            description: null,
            kind: ScenarioKind.Test,
            projectId: "proj-001",
            correlationId: "corr-001");

        result.Errors.Should().ContainSingle(e => e.Code == "TITLE_REQUIRED");
        result.Scenario.Should().BeNull();
    }

    [Fact]
    public async Task CreateAsync_InvalidKind_ReturnsInvalidKindError()
    {
        var result = await _service.CreateAsync(
            title: "Valid title",
            description: null,
            kind: (ScenarioKind)999,
            projectId: "proj-001",
            correlationId: "corr-001");

        result.Errors.Should().ContainSingle(e => e.Code == "INVALID_KIND");
        result.Scenario.Should().BeNull();
    }

    [Fact]
    public async Task CreateAsync_ValidInput_InsertsRowAndReturnsScenario()
    {
        var result = await _service.CreateAsync(
            title: "My scenario",
            description: "A description",
            kind: ScenarioKind.Requirement,
            projectId: "proj-001",
            correlationId: "corr-001");

        result.Errors.Should().BeEmpty();
        result.Scenario.Should().NotBeNull();
        result.Scenario!.Id.Should().NotBeEmpty();
        result.Scenario.Title.Should().Be("My scenario");
        result.Scenario.Description.Should().Be("A description");
        result.Scenario.Kind.Should().Be(ScenarioKind.Requirement);
        result.Scenario.ProjectId.Should().Be("proj-001");

        var saved = await _context.Scenarios.SingleAsync();
        saved.Id.Should().Be(result.Scenario.Id);
    }

    [Fact]
    public async Task GetAllAsync_ReturnsListOrderedByCreatedAtDesc_ForGivenProjectId()
    {
        var projectId = "proj-001";
        var oldest = new Scenario { Title = "Oldest", Kind = ScenarioKind.Test, ProjectId = projectId, CreatedAt = DateTimeOffset.UtcNow.AddDays(-2) };
        var middle = new Scenario { Title = "Middle", Kind = ScenarioKind.Requirement, ProjectId = projectId, CreatedAt = DateTimeOffset.UtcNow.AddDays(-1) };
        var newest = new Scenario { Title = "Newest", Kind = ScenarioKind.Test, ProjectId = projectId, CreatedAt = DateTimeOffset.UtcNow };
        _context.Scenarios.AddRange(oldest, middle, newest);
        await _context.SaveChangesAsync();

        var result = await _service.GetAllAsync(projectId);

        result.Should().HaveCount(3);
        result[0].Id.Should().Be(newest.Id);
        result[1].Id.Should().Be(middle.Id);
        result[2].Id.Should().Be(oldest.Id);
    }

    [Fact]
    public async Task GetAllAsync_EmptyProject_ReturnsEmptyList()
    {
        var result = await _service.GetAllAsync("proj-no-scenarios");

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetAllAsync_DoesNotReturnScenariosFromOtherProjects()
    {
        _context.Scenarios.Add(new Scenario { Title = "Other", Kind = ScenarioKind.Test, ProjectId = "proj-other" });
        await _context.SaveChangesAsync();

        var result = await _service.GetAllAsync("proj-001");

        result.Should().BeEmpty();
    }

    public void Dispose() => _context.Dispose();
}
