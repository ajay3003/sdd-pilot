using BirkNext.Api.Data.TaskAlignment;
using BirkNext.Api.Services.TaskAlignment;
using BirkNext.TaskAlignment;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BirkNext.Api.Tests.Services;

/// <summary>
/// Persisted Task Alignment runs: a completed run is stored immutably and read back for exactly its project and full Tasks document path;
/// another project's run is never returned, another document with the same file name is a different document, and a failed, refused or
/// partial run is never stored.
/// </summary>
public sealed class TaskAlignmentRunServiceTests
{
    private const string Project = "import:shop-1";
    private const string TasksPath = "shop/specs/001-cart/tasks.md";
    private static readonly string TasksHash = new('A', 64), SpecHash = new('B', 64);

    private readonly string _database = $"task-alignment-{Guid.NewGuid():N}";
    private readonly MutableClock _clock = new(new DateTimeOffset(2026, 11, 14, 9, 0, 0, TimeSpan.Zero));

    private TaskAlignmentRunService Service() => new(new TaskAlignmentDbContext(new DbContextOptionsBuilder<TaskAlignmentDbContext>()
        .UseInMemoryDatabase(_database).Options), _clock);

    private static SaveTaskAlignmentRunRequest Request(string project = Project, string path = TasksPath, string? tasksHash = null) => new()
    {
        ProjectKey = project,
        Module = "shop",
        FeatureKey = "001-cart",
        TasksPath = path,
        TasksHash = tasksHash ?? TasksHash,
        SpecPath = "shop/specs/001-cart/spec.md",
        SpecHash = SpecHash,
        RulesVersion = "rules-v1",
        DesignInputsKey = "none:none",
        Completeness = TaskAlignmentRunRules.Complete,
        Counts = new TaskAlignmentRunCounts { TotalTasks = 2, LinkedTasks = 1, NeedsReviewTasks = 1 },
        Tasks =
        [
            new TaskAlignmentRunTask { TaskId = "T001", Title = "Store carts", Status = "Linked", Risk = "Low", LinkedReferences = [new("FR-001", "Carts are stored", "Requirement")] },
            new TaskAlignmentRunTask { TaskId = "T002", Title = "Tune cache", Status = "NeedsReview", Risk = "Medium", IsFinding = true, UnresolvedReferences = ["FR-099"] },
        ],
    };

    [Fact]
    public async Task A_completed_run_is_stored_and_read_back_with_its_provenance_and_task_results()
    {
        var (saved, error) = await Service().SaveAsync(Request());

        error.Should().BeNull();
        saved!.AnalyzedAt.Should().Be(_clock.GetUtcNow(), "the server sets the analyzed-at time");
        saved.RecordVersion.Should().Be(TaskAlignmentRunRules.RecordVersion);

        var latest = (await Service().LatestAsync(Project, TasksPath))!;
        latest.Summary.Id.Should().Be(saved.Id);
        latest.Summary.TasksHash.Should().Be(TasksHash);
        latest.Summary.SpecHash.Should().Be(SpecHash);
        latest.Summary.FeatureKey.Should().Be("001-cart");
        latest.Summary.Counts.TotalTasks.Should().Be(2);
        latest.Tasks.Select(t => (t.TaskId, t.Status)).Should().Equal(("T001", "Linked"), ("T002", "NeedsReview"));
        latest.Tasks[0].LinkedReferences.Single().ItemId.Should().Be("FR-001");
        latest.Tasks[1].UnresolvedReferences.Should().Equal("FR-099");
        (await Service().GetAsync(Project, saved.Id))!.Tasks.Should().HaveCount(2);
    }

    [Fact]
    public async Task The_latest_run_is_the_newest_for_the_document_and_older_runs_stay_as_history()
    {
        var (first, _) = await Service().SaveAsync(Request());
        _clock.Advance(TimeSpan.FromMinutes(5));
        var (second, _) = await Service().SaveAsync(Request(tasksHash: new string('C', 64)));

        (await Service().LatestAsync(Project, TasksPath))!.Summary.Id.Should().Be(second!.Id);
        (await Service().ListAsync(Project, TasksPath, 20)).Select(r => r.Id).Should().Equal(second.Id, first!.Id);
        (await Service().GetAsync(Project, first.Id)).Should().NotBeNull("an older run is never overwritten");
    }

    [Fact]
    public async Task A_run_of_another_project_is_never_returned()
    {
        var (saved, _) = await Service().SaveAsync(Request());

        (await Service().LatestAsync("import:other", TasksPath)).Should().BeNull();
        (await Service().GetAsync("import:other", saved!.Id)).Should().BeNull();
        (await Service().ListAsync("import:other", null, 20)).Should().BeEmpty();
        (await Service().GetAsync("", saved.Id)).Should().BeNull("a read without a project key returns nothing");
    }

    [Fact]
    public async Task Another_tasks_document_with_the_same_file_name_is_a_different_document()
    {
        await Service().SaveAsync(Request());

        (await Service().LatestAsync(Project, "shop/specs/002-checkout/tasks.md")).Should().BeNull();
        (await Service().LatestAsync(Project, "tasks.md")).Should().BeNull("a file name alone never matches a full path");
        (await Service().LatestAsync(Project, @".\shop\specs\001-cart\tasks.md")).Should().NotBeNull("separators are normalized; the path is the same");
    }

    [Fact]
    public async Task Failed_refused_or_partial_runs_are_never_stored()
    {
        var service = Service();
        (await service.SaveAsync(Request() with { Completeness = "Failed" })).Error.Should().Contain("Only complete");
        (await service.SaveAsync(Request() with { Counts = new TaskAlignmentRunCounts { TotalTasks = 3 } })).Error.Should().Contain("incomplete");
        (await service.SaveAsync(Request() with { ProjectKey = "" })).Error.Should().Contain("projectKey");
        (await service.SaveAsync(Request() with { TasksPath = " " })).Error.Should().Contain("tasksPath");
        (await service.SaveAsync(Request() with { TasksHash = "not-a-hash" })).Error.Should().Contain("fingerprints");

        (await Service().ListAsync(Project, null, 20)).Should().BeEmpty();
    }

    [Fact]
    public void Classification_separates_current_stale_historical_and_other_documents()
    {
        var run = new TaskAlignmentRunSummary { ProjectKey = Project, TasksPath = TasksPath, TasksHash = TasksHash, SpecHash = SpecHash, RulesVersion = "rules-v1", DesignInputsKey = "none:none", Completeness = TaskAlignmentRunRules.Complete };

        TaskAlignmentRunRules.Classify(run, Project, TasksPath, TasksHash.ToLowerInvariant(), SpecHash, "rules-v1", "none:none").Should().Be(TaskAlignmentRunCurrentness.Current);
        TaskAlignmentRunRules.Classify(run, Project, TasksPath, new string('C', 64)).Should().Be(TaskAlignmentRunCurrentness.Stale);
        TaskAlignmentRunRules.Classify(run, Project, TasksPath, TasksHash, rulesVersion: "rules-v2").Should().Be(TaskAlignmentRunCurrentness.Stale);
        TaskAlignmentRunRules.Classify(run, Project, TasksPath, TasksHash, isLatest: false).Should().Be(TaskAlignmentRunCurrentness.Historical);
        TaskAlignmentRunRules.Classify(run, "import:other", TasksPath, TasksHash).Should().Be(TaskAlignmentRunCurrentness.NoResult);
        TaskAlignmentRunRules.Classify(run, Project, "shop/specs/002/tasks.md", TasksHash).Should().Be(TaskAlignmentRunCurrentness.NoResult);
        TaskAlignmentRunRules.Classify(run with { Completeness = "Partial" }, Project, TasksPath, TasksHash).Should().Be(TaskAlignmentRunCurrentness.Incomplete);
        TaskAlignmentRunRules.Classify(null, Project, TasksPath, TasksHash).Should().Be(TaskAlignmentRunCurrentness.NoResult);
    }

    [Fact]
    public async Task A_later_import_of_the_same_project_sees_the_earlier_run_as_its_lineage_never_another_projects()
    {
        var service = Service();
        await service.SaveAsync(Request() with { ProjectName = "Shop" });
        await service.SaveAsync(Request(project: "import:other-3") with { ProjectName = "Other" });

        var lineage = await service.LatestAsync("import:shop-2", TasksPath, "Shop");
        lineage!.Summary.ProjectKey.Should().Be(Project, "the earlier import of the same project and full path");
        (await service.LatestAsync("import:shop-2", TasksPath, (string?)null)).Should().BeNull("without a project name an import key never reaches another import");
        (await service.LatestAsync("import:shop-2", "shop/specs/002-other/tasks.md", "Shop")).Should().BeNull("another Tasks document is not this one");
        (await service.LatestAsync("import:shop-2", TasksPath, "Unknown")).Should().BeNull();

        await service.SaveAsync(Request(project: "import:shop-2", tasksHash: new string('C', 64)) with { ProjectName = "Shop" });
        (await service.LatestAsync("import:shop-2", TasksPath, "Shop"))!.Summary.ProjectKey.Should().Be("import:shop-2", "an import's own run wins over its lineage");

        TaskAlignmentRunRules.Classify(lineage.Summary, "import:shop-2", TasksPath, new string('C', 64), projectName: "Shop").Should().Be(TaskAlignmentRunCurrentness.Stale,
            "a changed Tasks document in a new source revision");
        TaskAlignmentRunRules.Classify(lineage.Summary, "import:shop-2", TasksPath, TasksHash, projectName: "Shop").Should().Be(TaskAlignmentRunCurrentness.Current,
            "an identical Tasks document");
        TaskAlignmentRunRules.Classify(lineage.Summary, "import:shop-2", TasksPath, TasksHash, projectName: "Other").Should().Be(TaskAlignmentRunCurrentness.NoResult);
        TaskAlignmentRunRules.Classify(lineage.Summary, "import:shop-2", TasksPath, TasksHash).Should().Be(TaskAlignmentRunCurrentness.NoResult);
        TaskAlignmentRunRules.IsEarlierImport(lineage.Summary, "import:shop-2", "Shop").Should().BeTrue();
        TaskAlignmentRunRules.IsEarlierImport(lineage.Summary, Project, "Shop").Should().BeFalse("its own import");
    }

    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}
