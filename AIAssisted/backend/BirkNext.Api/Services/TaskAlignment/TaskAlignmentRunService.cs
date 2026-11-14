using System.Text.Json;
using BirkNext.Api.Data.TaskAlignment;
using BirkNext.TaskAlignment;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services.TaskAlignment;

/// <summary>
/// Stores and reads immutable Task Alignment runs. Every read is scoped by project key and a run of another project is never returned; the
/// latest run is per (project, full Tasks document path) — another document with the same file name is a different document. Only complete
/// runs are stored; a refused save stores nothing.
/// </summary>
public sealed class TaskAlignmentRunService(TaskAlignmentDbContext db, TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<(TaskAlignmentRunSummary? Run, string? Error)> SaveAsync(SaveTaskAlignmentRunRequest request, CancellationToken ct = default)
    {
        if (TaskAlignmentRunRules.Validate(request) is { } error) return (null, error);
        var record = new TaskAlignmentRunRecord
        {
            Id = Guid.NewGuid(),
            ProjectKey = request.ProjectKey.Trim(),
            ProjectName = Blank(request.ProjectName),
            Module = Blank(request.Module),
            FeatureKey = Blank(request.FeatureKey),
            TasksPath = TaskAlignmentRunRules.NormalizePath(request.TasksPath),
            TasksHash = request.TasksHash.ToUpperInvariant(),
            SpecPath = Blank(TaskAlignmentRunRules.NormalizePath(request.SpecPath)),
            SpecHash = request.SpecHash.ToUpperInvariant(),
            SourceSnapshotId = request.SourceSnapshotId,
            RulesVersion = request.RulesVersion.Trim(),
            DesignInputsKey = request.DesignInputsKey ?? "",
            RecordVersion = TaskAlignmentRunRules.RecordVersion,
            Completeness = TaskAlignmentRunRules.Complete,
            AnalyzedAt = clock.GetUtcNow(),
            TotalTasks = request.Tasks.Count,
            FindingCount = request.Tasks.Count(t => t.IsFinding),
            CountsJson = JsonSerializer.Serialize(request.Counts, Json),
            TasksJson = JsonSerializer.Serialize(request.Tasks, Json),
        };
        db.Runs.Add(record);
        await db.SaveChangesAsync(ct);
        return (ToSummary(record), null);
    }

    /// <summary>
    /// The newest run for exactly this project and Tasks document path. When this project key has none and <paramref name="projectName"/> is
    /// given: the newest run of an earlier import with the same project name and the same full path (a previous source revision), which the
    /// caller classifies (stale unless the fingerprints are identical). Null otherwise.
    /// </summary>
    public async Task<TaskAlignmentRun?> LatestAsync(string projectKey, string tasksPath, string? projectName, CancellationToken ct = default)
    {
        if (await LatestAsync(projectKey, tasksPath, ct) is { } own) return own;
        var path = TaskAlignmentRunRules.NormalizePath(tasksPath);
        var name = Blank(projectName);
        if (name is null || path.Length == 0 || string.IsNullOrWhiteSpace(projectKey)) return null;
        var key = projectKey.Trim();
        var record = await db.Runs.AsNoTracking()
            .Where(r => r.ProjectName == name && r.TasksPath == path && r.ProjectKey != key)
            .OrderByDescending(r => r.AnalyzedAt).ThenByDescending(r => r.Id)
            .FirstOrDefaultAsync(ct);
        return record is null ? null : ToRun(record);
    }

    /// <summary>The newest run for exactly this project and Tasks document path, or null.</summary>
    public async Task<TaskAlignmentRun?> LatestAsync(string projectKey, string tasksPath, CancellationToken ct = default)
    {
        var path = TaskAlignmentRunRules.NormalizePath(tasksPath);
        if (string.IsNullOrWhiteSpace(projectKey) || path.Length == 0) return null;
        var key = projectKey.Trim();
        var record = await db.Runs.AsNoTracking()
            .Where(r => r.ProjectKey == key && r.TasksPath == path)
            .OrderByDescending(r => r.AnalyzedAt).ThenByDescending(r => r.Id)
            .FirstOrDefaultAsync(ct);
        return record is null ? null : ToRun(record);
    }

    /// <summary>Run summaries of a project, newest first; filtered to one Tasks document path when given.</summary>
    public async Task<IReadOnlyList<TaskAlignmentRunSummary>> ListAsync(string projectKey, string? tasksPath, int limit, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(projectKey)) return [];
        var key = projectKey.Trim();
        var path = TaskAlignmentRunRules.NormalizePath(tasksPath);
        var query = db.Runs.AsNoTracking().Where(r => r.ProjectKey == key);
        if (path.Length > 0) query = query.Where(r => r.TasksPath == path);
        var records = await query.OrderByDescending(r => r.AnalyzedAt).ThenByDescending(r => r.Id)
            .Take(Math.Clamp(limit, 1, TaskAlignmentRunRules.MaxHistory)).ToListAsync(ct);
        return [.. records.Select(ToSummary)];
    }

    /// <summary>One run, only when it belongs to <paramref name="projectKey"/>.</summary>
    public async Task<TaskAlignmentRun?> GetAsync(string projectKey, Guid id, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(projectKey)) return null;
        var key = projectKey.Trim();
        var record = await db.Runs.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id && r.ProjectKey == key, ct);
        return record is null ? null : ToRun(record);
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static TaskAlignmentRun ToRun(TaskAlignmentRunRecord record) => new()
    {
        Summary = ToSummary(record),
        Tasks = JsonSerializer.Deserialize<List<TaskAlignmentRunTask>>(record.TasksJson, Json) ?? [],
    };

    private static TaskAlignmentRunSummary ToSummary(TaskAlignmentRunRecord r) => new()
    {
        Id = r.Id,
        ProjectKey = r.ProjectKey,
        ProjectName = r.ProjectName,
        Module = r.Module,
        FeatureKey = r.FeatureKey,
        TasksPath = r.TasksPath,
        TasksHash = r.TasksHash,
        SpecPath = r.SpecPath,
        SpecHash = r.SpecHash,
        SourceSnapshotId = r.SourceSnapshotId,
        RulesVersion = r.RulesVersion,
        DesignInputsKey = r.DesignInputsKey,
        RecordVersion = r.RecordVersion,
        Completeness = r.Completeness,
        AnalyzedAt = r.AnalyzedAt,
        Counts = JsonSerializer.Deserialize<TaskAlignmentRunCounts>(r.CountsJson, Json) ?? new(),
    };
}
