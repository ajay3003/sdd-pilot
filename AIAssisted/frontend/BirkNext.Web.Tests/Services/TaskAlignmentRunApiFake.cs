using BirkNext.TaskAlignment;
using BirkNext.Web.Services;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// In-memory stand-in for the backend Task Alignment run store: same validation, same per-project and exact-path reads. It outlives a page
/// render, so a new session service plus this fake is a browser reload.
/// </summary>
public sealed class TaskAlignmentRunApiFake : ITaskAlignmentRunApiService
{
    public List<TaskAlignmentRun> Runs { get; } = [];
    public DateTimeOffset Now { get; set; } = new(2026, 1, 2, 3, 4, 0, TimeSpan.Zero);
    /// <summary>When set, every save fails with this message and nothing is stored.</summary>
    public string? FailSaveWith { get; set; }
    public int SaveAttempts { get; private set; }

    public Task<(TaskAlignmentRunSummary? Run, string? Error)> SaveAsync(SaveTaskAlignmentRunRequest request, CancellationToken ct = default)
    {
        SaveAttempts++;
        if (FailSaveWith is { } failure) return Task.FromResult<(TaskAlignmentRunSummary?, string?)>((null, failure));
        if (TaskAlignmentRunRules.Validate(request) is { } error) return Task.FromResult<(TaskAlignmentRunSummary?, string?)>((null, error));
        var summary = new TaskAlignmentRunSummary
        {
            Id = Guid.NewGuid(), ProjectKey = request.ProjectKey, ProjectName = request.ProjectName, Module = request.Module, FeatureKey = request.FeatureKey,
            TasksPath = TaskAlignmentRunRules.NormalizePath(request.TasksPath), TasksHash = request.TasksHash, SpecPath = request.SpecPath, SpecHash = request.SpecHash,
            RulesVersion = request.RulesVersion, DesignInputsKey = request.DesignInputsKey, RecordVersion = TaskAlignmentRunRules.RecordVersion,
            Completeness = TaskAlignmentRunRules.Complete, AnalyzedAt = Now, Counts = request.Counts,
        };
        Runs.Add(new TaskAlignmentRun { Summary = summary, Tasks = [.. request.Tasks] });
        return Task.FromResult<(TaskAlignmentRunSummary?, string?)>((summary, null));
    }

    public Task<(TaskAlignmentRun? Run, string? Error)> LatestAsync(string projectKey, string tasksPath, CancellationToken ct = default) =>
        Task.FromResult<(TaskAlignmentRun?, string?)>((Of(projectKey, tasksPath).FirstOrDefault(), null));

    public Task<(TaskAlignmentRun? Run, string? Error)> LatestOfProjectAsync(string projectKey, string tasksPath, string? projectName, CancellationToken ct = default) =>
        Task.FromResult<(TaskAlignmentRun?, string?)>((Of(projectKey, tasksPath).FirstOrDefault()
            ?? (string.IsNullOrWhiteSpace(projectName) ? null
                : Runs.Where(r => r.Summary.ProjectKey != projectKey && r.Summary.ProjectName == projectName && r.Summary.TasksPath == TaskAlignmentRunRules.NormalizePath(tasksPath))
                    .OrderByDescending(r => r.Summary.AnalyzedAt).FirstOrDefault()), null));

    public Task<(List<TaskAlignmentRunSummary>? Runs, string? Error)> ListAsync(string projectKey, string? tasksPath, CancellationToken ct = default) =>
        Task.FromResult<(List<TaskAlignmentRunSummary>?, string?)>(([.. (tasksPath is null ? Runs.Where(r => r.Summary.ProjectKey == projectKey) : Of(projectKey, tasksPath))
            .OrderByDescending(r => r.Summary.AnalyzedAt).Select(r => r.Summary)], null));

    public Task<(TaskAlignmentRun? Run, string? Error)> GetAsync(string projectKey, Guid id, CancellationToken ct = default) =>
        Task.FromResult<(TaskAlignmentRun?, string?)>((Runs.FirstOrDefault(r => r.Summary.Id == id && r.Summary.ProjectKey == projectKey), null));

    /// <summary>Stores a run directly (another browser session, an earlier day).</summary>
    public TaskAlignmentRun Seed(string projectKey, string tasksPath, string tasksHash, DateTimeOffset analyzedAt, params (string TaskId, string Status)[] tasks) =>
        Seed(projectKey, null, tasksPath, tasksHash, analyzedAt, tasks);

    public TaskAlignmentRun Seed(string projectKey, string? projectName, string tasksPath, string tasksHash, DateTimeOffset analyzedAt, params (string TaskId, string Status)[] tasks)
    {
        var run = new TaskAlignmentRun
        {
            Summary = new TaskAlignmentRunSummary
            {
                Id = Guid.NewGuid(), ProjectKey = projectKey, ProjectName = projectName, TasksPath = TaskAlignmentRunRules.NormalizePath(tasksPath), TasksHash = tasksHash, SpecHash = new string('B', 64),
                RulesVersion = TaskSpecAlignmentService.RulesVersion, DesignInputsKey = "none:none", RecordVersion = TaskAlignmentRunRules.RecordVersion,
                Completeness = TaskAlignmentRunRules.Complete, AnalyzedAt = analyzedAt,
                Counts = new TaskAlignmentRunCounts { TotalTasks = tasks.Length, LinkedTasks = tasks.Count(t => t.Status == "Linked") },
            },
            Tasks = [.. tasks.Select(t => new TaskAlignmentRunTask { TaskId = t.TaskId, Title = t.TaskId, Status = t.Status, Risk = "Low", Purpose = "Unknown", ImpactLevel = "Unknown" })],
        };
        Runs.Add(run);
        return run;
    }

    private IEnumerable<TaskAlignmentRun> Of(string projectKey, string tasksPath) =>
        Runs.Where(r => r.Summary.ProjectKey == projectKey && r.Summary.TasksPath == TaskAlignmentRunRules.NormalizePath(tasksPath))
            .OrderByDescending(r => r.Summary.AnalyzedAt);
}
