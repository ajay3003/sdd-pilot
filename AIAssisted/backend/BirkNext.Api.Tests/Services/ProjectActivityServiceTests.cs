using BirkNext.Api.Data;
using BirkNext.Api.Services.ProjectActivity;
using BirkNext.ProjectActivity;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Tests.Services;

/// <summary>
/// Persistent project activity: scoped by the logical project id (never another project's events), newest first by server time, one event per
/// (project, idempotency key), bounded take, never a secret, and still there for a new context on the same store (a restarted API).
/// </summary>
public sealed class ProjectActivityServiceTests
{
    private readonly string _store = Guid.NewGuid().ToString();
    private readonly Clock _time = new(new DateTimeOffset(2026, 11, 2, 7, 0, 0, TimeSpan.Zero));

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private AppDbContext Db() => new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(_store).Options);

    private static ProjectActivityRecordRequest Import(string context, string package, string key) => new()
    {
        ProjectContextId = context, ProjectName = "M2LB", Type = ProjectActivityType.ImportCompleted, Status = ProjectActivityStatus.Imported,
        SourceModule = "Import Project", Title = $"Imported {package}", Summary = "Supporting/shared source · 4 documents", PackageId = $"import-{package}",
        PackageName = package, RelatedEntityType = "ImportedPackage", RelatedEntityId = $"import-{package}", Route = "project-import", IdempotencyKey = key,
    };

    [Fact]
    public async Task BothImportsOfOneApplication_AreListedNewestFirst_UnderItsContext_Only()
    {
        await using (var db = Db())
        {
            var service = new ProjectActivityService(db, _time);
            await service.RecordAsync(Import("app-m2lb", "M2LB", "import:m2lb:1"), default);
            _time.Advance(TimeSpan.FromMinutes(6));
            await service.RecordAsync(Import("app-m2lb", "M2LB.Common", "import:common:1"), default);
            await service.RecordAsync(Import("app-orders", "Orders", "import:orders:1"), default);
        }

        // A new context on the same store: what a restarted API reads.
        await using var reopened = Db();
        var list = await new ProjectActivityService(reopened, _time).ListAsync("app-m2lb", 10, default);
        list.Select(a => a.Title).Should().Equal(["Imported M2LB.Common", "Imported M2LB"], "newest first, by server time");
        list.Should().OnlyContain(a => a.ProjectContextId == "app-m2lb", "another application's activity never appears");
        list[0].PackageName.Should().Be("M2LB.Common", "the package is a reference; the scope is the application");
        list[0].Category.Should().Be(ProjectActivityCategory.Import);
        list[0].Status.Should().Be(ProjectActivityStatus.Imported, "an import is never reviewed, passed or ready");
        list[1].OccurredAt.Should().Be(new DateTimeOffset(2026, 11, 2, 7, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task TheSameCompletionReportedTwice_IsOneEvent_DistinctEventsCloseTogetherBothSurvive()
    {
        await using var db = Db();
        var service = new ProjectActivityService(db, _time);
        var first = await service.RecordAsync(Import("app-m2lb", "M2LB", "import:m2lb:1"), default);
        var again = await service.RecordAsync(Import("app-m2lb", "M2LB", "import:m2lb:1") with { Title = "Imported M2LB (retry)" }, default);
        again.Id.Should().Be(first.Id);
        again.Title.Should().Be("Imported M2LB", "a stored event is never rewritten");

        await Task.WhenAll(
            new ProjectActivityService(Db(), _time).RecordAsync(Import("app-m2lb", "A", "a"), default),
            new ProjectActivityService(Db(), _time).RecordAsync(Import("app-m2lb", "B", "b"), default));
        (await service.ListAsync("app-m2lb", 10, default)).Should().HaveCount(3);
    }

    [Fact]
    public async Task SecretsAreNeverStored_AndRoutesStayInsideTheApp()
    {
        await using var db = Db();
        var service = new ProjectActivityService(db, _time);
        var item = await service.RecordAsync(new()
        {
            ProjectContextId = "app-m2lb", Type = ProjectActivityType.PerformanceRunFailed, Status = ProjectActivityStatus.Failed, SourceModule = "Performance Test Review",
            Title = "Performance Test failed", Summary = "Authorization: Bearer abcdefghijklmnopqrstuvwxyz0123456789",
            Outcome = "token=eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.sig", EnvironmentName = "Development", IdempotencyKey = "run-1",
        }, default);
        item.Summary.Should().BeNull();
        item.Outcome.Should().BeNull();
        item.Title.Should().Be("Performance Test failed", "the event keeps its other facts");
        (await db.ProjectActivity.SingleAsync()).Summary.Should().BeNull();

        await service.Invoking(s => s.RecordAsync(Import("app-m2lb", "M2LB", "x") with { Route = "https://evil.example/x" }, default))
            .Should().ThrowAsync<ProjectActivityRequestException>();
        await service.Invoking(s => s.RecordAsync(Import("", "M2LB", "x"), default)).Should().ThrowAsync<ProjectActivityRequestException>();
        await service.Invoking(s => s.RecordAsync(Import("app-m2lb", "M2LB", ""), default)).Should().ThrowAsync<ProjectActivityRequestException>();
    }

    [Fact]
    public async Task ListIsBounded_AndAnUnknownOrBlankContextHasNoActivity()
    {
        await using var db = Db();
        var service = new ProjectActivityService(db, _time);
        for (var i = 0; i < 60; i++) { await service.RecordAsync(Import("app-m2lb", $"P{i}", $"k{i}"), default); _time.Advance(TimeSpan.FromSeconds(1)); }
        (await service.ListAsync("app-m2lb", 10, default)).Select(a => a.PackageName).Should().Equal(Enumerable.Range(50, 10).Reverse().Select(i => $"P{i}"));
        (await service.ListAsync("app-m2lb", 500, default)).Should().HaveCount(ProjectActivityService.MaxTake);
        (await service.ListAsync("", 10, default)).Should().BeEmpty();
        (await service.ListAsync("M2LB", 10, default)).Should().BeEmpty("a display name is not a project id");
    }
}
