using System.Text.Json;
using BirkNext.Api.Data.DeliveryReporting;
using BirkNext.DeliveryReporting;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services.DeliveryReporting;

/// <summary>
/// Reporting projects and their snapshots, read and written in delivery_reporting only. Snapshot contents are immutable: only the label and
/// role (metadata) can change, and deletion removes that snapshot's rows only — reports that used it keep their frozen content.
/// </summary>
public sealed class DeliverySnapshotStore(DeliveryReportingDbContext db, TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // ── Projects ──────────────────────────────────────────────────────────────────────────────────────────────────────

    public async Task<List<DeliveryProject>> ProjectsAsync(CancellationToken ct)
    {
        var projects = await db.Projects.AsNoTracking().OrderBy(p => p.Name).ToListAsync(ct);
        var snapshots = await db.Snapshots.AsNoTracking().GroupBy(s => s.ProjectId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, ct);
        var reports = await db.WeeklyReports.AsNoTracking().GroupBy(r => r.ProjectId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, ct);
        return projects.Select(p => new DeliveryProject(p.Id, p.Name, p.Description, p.CreatedAt, p.UpdatedAt,
            snapshots.GetValueOrDefault(p.Id), reports.GetValueOrDefault(p.Id))).ToList();
    }

    public async Task<(DeliveryProject? Project, string? Error)> CreateProjectAsync(CreateDeliveryProjectRequest request, CancellationToken ct)
    {
        var name = (request.Name ?? "").Trim();
        if (name.Length == 0) return (null, "Enter a project name.");
        if (name.Length > 200) return (null, "The project name can be at most 200 characters.");
        if (await db.Projects.AnyAsync(p => p.Name.ToLower() == name.ToLower(), ct)) return (null, $"A reporting project named \"{name}\" already exists.");
        var now = clock.GetUtcNow();
        var description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        var record = new DeliveryProjectRecord { Id = Guid.NewGuid(), Name = name, Description = description?.Length > 2000 ? description[..2000] : description, CreatedAt = now, UpdatedAt = now };
        db.Projects.Add(record);
        await db.SaveChangesAsync(ct);
        return (new(record.Id, record.Name, record.Description, now, now, 0, 0), null);
    }

    // ── Snapshots ─────────────────────────────────────────────────────────────────────────────────────────────────────

    public async Task<List<DeliverySnapshotSummary>> SnapshotsAsync(Guid projectId, CancellationToken ct)
    {
        var rows = await (from s in db.Snapshots.AsNoTracking()
                          join src in db.ReportSources.AsNoTracking() on s.ReportSourceId equals src.Id
                          where s.ProjectId == projectId
                          orderby s.EffectiveAt descending, s.ImportedAt descending
                          select new { s, src }).ToListAsync(ct);
        var ids = rows.Select(r => r.s.Id).ToList();
        var reportCounts = await db.WeeklyReports.AsNoTracking()
            .Where(r => (r.BaselineSnapshotId != null && ids.Contains(r.BaselineSnapshotId.Value)) || (r.CurrentSnapshotId != null && ids.Contains(r.CurrentSnapshotId.Value)))
            .Select(r => new { r.BaselineSnapshotId, r.CurrentSnapshotId }).ToListAsync(ct);
        return rows.Select(r => Summary(r.s, r.src, reportCounts.Count(c => c.BaselineSnapshotId == r.s.Id || c.CurrentSnapshotId == r.s.Id))).ToList();
    }

    public async Task<DeliverySnapshotDetail?> SnapshotAsync(Guid projectId, Guid snapshotId, CancellationToken ct)
    {
        var row = await (from s in db.Snapshots.AsNoTracking()
                         join src in db.ReportSources.AsNoTracking() on s.ReportSourceId equals src.Id
                         where s.ProjectId == projectId && s.Id == snapshotId
                         select new { s, src }).FirstOrDefaultAsync(ct);
        if (row is null) return null;
        var features = await db.FeatureSnapshots.AsNoTracking().Where(f => f.SnapshotId == snapshotId).OrderBy(f => f.Position).ToListAsync(ct);
        return new(Summary(row.s, row.src, 0), JsonSerializer.Deserialize<List<string>>(row.s.ModulesJson, Json) ?? [],
            features.Select(f => new DeliverySnapshotFeature(f.FeatureKey, f.Title, f.Module, DeliveryReportingText.Status(Enum.Parse<DeliveryStatus>(f.Status)),
                f.StatusReason, f.TaskTotal, f.TaskCompleted, f.SpecPath, f.TasksPath)).ToList(),
            JsonSerializer.Deserialize<List<DeliveryImportWarning>>(row.s.WarningsJson, Json) ?? []);
    }

    /// <summary>Changes the label and/or role only. Snapshot contents never change after import.</summary>
    public async Task<DeliverySnapshotSummary?> UpdateSnapshotAsync(Guid projectId, Guid snapshotId, UpdateDeliverySnapshotRequest request, CancellationToken ct)
    {
        var snapshot = await db.Snapshots.FirstOrDefaultAsync(s => s.ProjectId == projectId && s.Id == snapshotId, ct);
        if (snapshot is null) return null;
        if (request.Label is { } label && label.Trim().Length > 0) snapshot.Label = label.Trim().Length > 200 ? label.Trim()[..200] : label.Trim();
        if (request.Role is { } role) snapshot.Role = role.ToString();
        await db.SaveChangesAsync(ct);
        var source = await db.ReportSources.AsNoTracking().FirstAsync(s => s.Id == snapshot.ReportSourceId, ct);
        return Summary(snapshot, source, 0);
    }

    /// <summary>Deletes one snapshot (its features, tasks, files and evidence cascade) and its source row when no other snapshot uses it. Reports
    /// that used the snapshot keep their frozen content; their snapshot reference becomes empty.</summary>
    public async Task<bool> DeleteSnapshotAsync(Guid projectId, Guid snapshotId, CancellationToken ct)
    {
        var snapshot = await db.Snapshots.FirstOrDefaultAsync(s => s.ProjectId == projectId && s.Id == snapshotId, ct);
        if (snapshot is null) return false;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        db.Snapshots.Remove(snapshot);
        await db.SaveChangesAsync(ct);
        if (!await db.Snapshots.AnyAsync(s => s.ReportSourceId == snapshot.ReportSourceId, ct)
            && await db.ReportSources.FirstOrDefaultAsync(s => s.Id == snapshot.ReportSourceId, ct) is { } source)
        {
            db.ReportSources.Remove(source);
            await db.SaveChangesAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return true;
    }

    /// <summary>The nearest valid earlier snapshot for <paramref name="currentId"/>, within this project only. Prefers the earliest snapshot in the
    /// current snapshot's week (covers the most of it), else the latest before the week. Never chooses between two candidates on the same day.</summary>
    public async Task<DeliveryBaselineSuggestion?> SuggestBaselineAsync(Guid projectId, Guid currentId, TimeZoneInfo zone, CancellationToken ct)
    {
        var current = await db.Snapshots.AsNoTracking().FirstOrDefaultAsync(s => s.ProjectId == projectId && s.Id == currentId, ct);
        if (current is null) return null;
        var earlier = await db.Snapshots.AsNoTracking().Where(s => s.ProjectId == projectId && s.Id != currentId && s.EffectiveAt < current.EffectiveAt)
            .OrderBy(s => s.EffectiveAt).ToListAsync(ct);
        if (earlier.Count == 0) return new(null, "No earlier snapshot in this project. Import a baseline from earlier in the week.", false, []);
        var currentLocal = TimeZoneInfo.ConvertTime(current.EffectiveAt, zone);
        var weekStart = DateOnly.FromDateTime(currentLocal.Date).AddDays(-(((int)currentLocal.DayOfWeek + 6) % 7));
        DateOnly Day(DeliverySnapshotRecord s) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(s.EffectiveAt, zone).Date);

        var inWeek = earlier.Where(s => Day(s) >= weekStart).ToList();
        var pool = inWeek.Count > 0 ? inWeek : earlier;
        var targetDay = inWeek.Count > 0 ? inWeek.Min(Day) : earlier.Max(Day);
        var sameDay = pool.Where(s => Day(s) == targetDay).ToList();
        if (sameDay.Count > 1)
        {
            var baselines = sameDay.Where(s => s.Role == nameof(DeliverySnapshotRole.Baseline)).ToList();
            if (baselines.Count != 1)
                return new(null, $"{sameDay.Count} snapshots are dated {targetDay:yyyy-MM-dd}. Choose the baseline yourself.", true, sameDay.Select(s => s.Id).ToList());
            sameDay = baselines;
        }
        var chosen = sameDay[0];
        var reason = inWeek.Count > 0
            ? $"Earliest snapshot in the week of the current snapshot ({targetDay:yyyy-MM-dd})."
            : $"Latest snapshot before this week ({targetDay:yyyy-MM-dd}); the report may include changes from before the week started.";
        return new(chosen.Id, reason, false, [chosen.Id]);
    }

    /// <summary>The normalized content of a stored snapshot, as the comparison reads it.</summary>
    public async Task<DeliverySnapshotContent?> ContentAsync(Guid projectId, Guid snapshotId, CancellationToken ct)
    {
        var row = await (from s in db.Snapshots.AsNoTracking()
                         join src in db.ReportSources.AsNoTracking() on s.ReportSourceId equals src.Id
                         where s.ProjectId == projectId && s.Id == snapshotId
                         select new { s, src }).FirstOrDefaultAsync(ct);
        if (row is null) return null;
        var features = await db.FeatureSnapshots.AsNoTracking().Where(f => f.SnapshotId == snapshotId).OrderBy(f => f.Position).ToListAsync(ct);
        var tasks = (await db.TaskSnapshots.AsNoTracking().Where(t => t.SnapshotId == snapshotId).OrderBy(t => t.Position).ToListAsync(ct))
            .GroupBy(t => t.FeatureSnapshotId).ToDictionary(g => g.Key, g => g.ToList());
        var sources = await db.ArtifactSnapshots.AsNoTracking().Where(a => a.SnapshotId == snapshotId && a.Kind == "Source")
            .Select(a => new { a.Path, a.Module, a.Fingerprint }).ToListAsync(ct);
        var phases = (await db.PhaseSnapshots.AsNoTracking().Where(p => p.SnapshotId == snapshotId).OrderBy(p => p.Position).ToListAsync(ct))
            .GroupBy(p => p.FeatureSnapshotId).ToDictionary(g => g.Key, g => g.Select(p => new DeliveryContentPhase(p.PhaseKey, p.Title, p.Position)).ToList());
        var stories = (await db.UserStorySnapshots.AsNoTracking().Where(s => s.SnapshotId == snapshotId).OrderBy(s => s.Position).ToListAsync(ct))
            .GroupBy(s => s.FeatureSnapshotId).ToDictionary(g => g.Key, g => g.Select(s => new DeliveryContentStory(s.StoryKey, s.Title, s.Description, s.Priority,
                JsonSerializer.Deserialize<List<string>>(s.PhaseKeysJson, Json) ?? [], s.Position, s.SourceLine)).ToList());
        var criteria = (await db.AcceptanceCriterionSnapshots.AsNoTracking().Where(a => a.SnapshotId == snapshotId).OrderBy(a => a.Position).ToListAsync(ct))
            .GroupBy(a => a.FeatureSnapshotId).ToDictionary(g => g.Key, g => g.Select(a => new DeliveryContentCriterion(a.CriterionKey, a.Text, a.StoryKey, a.Position)).ToList());
        return new DeliverySnapshotContent
        {
            Ref = Ref(row.s, row.src),
            Coverage = Enum.Parse<DeliveryCoverage>(row.s.CoverageStatus),
            Warnings = JsonSerializer.Deserialize<List<DeliveryImportWarning>>(row.s.WarningsJson, Json) ?? [],
            Features = features.Select(f => new DeliveryContentFeature
            {
                Key = f.FeatureKey, Folder = f.Folder, Module = f.Module, Title = f.Title, Position = f.Position, SpecPath = f.SpecPath, TasksPath = f.TasksPath,
                PlanPath = f.PlanPath, SpecFingerprint = f.SpecFingerprint, AcceptanceFingerprint = f.AcceptanceFingerprint,
                AcceptanceCriteria = JsonSerializer.Deserialize<List<string>>(f.AcceptanceCriteriaJson, Json) ?? [],
                Status = Enum.Parse<DeliveryStatus>(f.Status), StatusReason = f.StatusReason, TaskTotal = f.TaskTotal, TaskCompleted = f.TaskCompleted,
                Tasks = tasks.TryGetValue(f.Id, out var list) ? list.Select(t => new DeliveryContentTask(t.TaskKey, t.Text, t.Completed)
                    { TaskId = t.TaskId, PhaseKey = t.PhaseKey, UserStoryKey = t.UserStoryKey, SourcePath = t.SourcePath }).ToList() : [],
                HierarchyCaptured = f.HierarchyCaptured, Phases = phases.GetValueOrDefault(f.Id) ?? [], Stories = stories.GetValueOrDefault(f.Id) ?? [],
                Criteria = criteria.GetValueOrDefault(f.Id) ?? [],
            }).ToList(),
            SourceFiles = sources.GroupBy(s => s.Path, StringComparer.Ordinal).ToDictionary(g => g.Key, g => (g.First().Module, g.First().Fingerprint), StringComparer.Ordinal),
        };
    }

    internal static DeliveryReportSnapshotRef Ref(DeliverySnapshotRecord s, DeliveryReportSourceRecord src) => new()
    {
        SnapshotId = s.Id, Label = s.Label, EffectiveAt = s.EffectiveAt, ImportedAt = s.ImportedAt, SourceType = Enum.Parse<DeliverySourceType>(src.SourceType),
        SourceFileName = src.OriginalFileName, GitCommit = src.GitCommit, Fingerprint = src.Fingerprint, FeatureCount = s.FeatureCount, TaskCount = s.TaskCount,
        CompletedTaskCount = s.CompletedTaskCount,
    };

    internal static DeliverySnapshotSummary Summary(DeliverySnapshotRecord s, DeliveryReportSourceRecord src, int reportCount) => new()
    {
        Id = s.Id, ProjectId = s.ProjectId, Label = s.Label, EffectiveAt = s.EffectiveAt, ImportedAt = s.ImportedAt, Role = Enum.Parse<DeliverySnapshotRole>(s.Role),
        SourceType = Enum.Parse<DeliverySourceType>(src.SourceType), SourceFileName = src.OriginalFileName, GitCommit = src.GitCommit, Fingerprint = src.Fingerprint,
        Coverage = Enum.Parse<DeliveryCoverage>(s.CoverageStatus), FeatureCount = s.FeatureCount, TaskCount = s.TaskCount, CompletedTaskCount = s.CompletedTaskCount,
        WarningsCount = s.WarningsCount, ReportCount = reportCount,
    };
}
