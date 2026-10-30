using System.Text.Json;
using BirkNext.Api.Data.DeliveryReporting;
using BirkNext.DeliveryReporting;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services.DeliveryReporting;

public sealed record WeeklyReportOutcome(WeeklyDeliveryReport? Report, string? Error, int StatusCode);

/// <summary>
/// Generates, stores, lists, deletes and exports weekly reports. Generation reads two Delivery Reporting snapshots, compares them, freezes the
/// result as one document plus its comparison rows (feature deltas, status events, report items), all in delivery_reporting. Opening and
/// exporting a report read only the frozen document, never the snapshots again.
/// </summary>
public sealed class WeeklyReportService(DeliveryReportingDbContext db, DeliverySnapshotStore snapshots, TimeProvider clock, IConfiguration configuration)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Reporting time zone (DeliveryReporting:TimeZone, default Europe/Oslo): decides the report week and how dates read.</summary>
    public TimeZoneInfo Zone
    {
        get
        {
            var id = configuration["DeliveryReporting:TimeZone"] is { Length: > 0 } configured ? configured : "Europe/Oslo";
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { return TimeZoneInfo.Utc; }
        }
    }

    public async Task<WeeklyReportOutcome> GenerateAsync(GenerateWeeklyReportRequest request, CancellationToken ct)
    {
        var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == request.ProjectId, ct);
        if (project is null) return new(null, "The reporting project does not exist.", StatusCodes.Status404NotFound);
        if (request.BaselineSnapshotId == request.CurrentSnapshotId)
            return new(null, "Choose two different snapshots: a baseline and a current snapshot.", StatusCodes.Status400BadRequest);
        var baseline = await snapshots.ContentAsync(project.Id, request.BaselineSnapshotId, ct);
        var current = await snapshots.ContentAsync(project.Id, request.CurrentSnapshotId, ct);
        if (baseline is null || current is null)
            return new(null, "Both snapshots must belong to this reporting project.", StatusCodes.Status400BadRequest);
        if (baseline.Ref.EffectiveAt > current.Ref.EffectiveAt)
            return new(null, "The baseline must not be later than the current snapshot. Swap them or choose another baseline.", StatusCodes.Status400BadRequest);

        var comparison = DeliverySnapshotComparison.Compare(baseline, current);
        var reportId = Guid.NewGuid();
        var now = clock.GetUtcNow();
        var report = WeeklyReportBuilder.Build(reportId, project.Id, project.Name, now, baseline, current, comparison, Zone);

        var comparisonRecord = new DeliverySnapshotComparisonRecord
        {
            Id = Guid.NewGuid(), ProjectId = project.Id, BaselineSnapshotId = baseline.Ref.SnapshotId, CurrentSnapshotId = current.Ref.SnapshotId, CreatedAt = now,
            FeaturesAdded = comparison.FeaturesAdded, FeaturesRemoved = comparison.FeaturesRemoved, FeaturesChanged = comparison.FeaturesChanged,
        };
        db.Comparisons.Add(comparisonRecord);
        db.FeatureDeltas.AddRange(comparison.Stories.Select(s => new DeliveryFeatureDeltaRecord
        {
            Id = Guid.NewGuid(), ComparisonId = comparisonRecord.Id, FeatureKey = s.FeatureKey, Title = Cap(s.Title, 400), Module = s.Module,
            BaselineStatus = s.BaselineStatus?.ToString(), CurrentStatus = s.CurrentStatus?.ToString(), BaselineTaskCompleted = s.BaselineTaskCompleted,
            BaselineTaskTotal = s.BaselineTaskTotal, CurrentTaskCompleted = s.CurrentTaskCompleted, CurrentTaskTotal = s.CurrentTaskTotal, TaskDelta = s.TaskDelta,
            SpecificationChanged = s.SpecificationChanged, AcceptanceCriteriaChanged = s.AcceptanceCriteriaChanged, NewFeature = s.NewFeature,
            RemovedFeature = s.RemovedFeature, DeliveryMovement = s.Movement.ToString(), ReasonsJson = JsonSerializer.Serialize(s.Reasons, Json),
        }));
        db.StatusEvents.AddRange(comparison.Events.Select(e => new DeliveryStatusEventRecord
        {
            Id = Guid.NewGuid(), ComparisonId = comparisonRecord.Id, FeatureKey = e.FeatureKey, Kind = e.Kind, FromValue = e.From, ToValue = e.To, Detail = Cap(e.Detail, 2000),
        }));
        db.WeeklyReports.Add(new DeliveryWeeklyReportRecord
        {
            Id = reportId, ProjectId = project.Id, ComparisonId = comparisonRecord.Id, BaselineSnapshotId = baseline.Ref.SnapshotId,
            CurrentSnapshotId = current.Ref.SnapshotId, Title = Cap(report.Title, 400), WeekLabel = report.WeekLabel, PeriodStart = report.PeriodStart,
            PeriodEnd = report.PeriodEnd, GeneratedAt = now, CoverageStatus = report.Coverage.ToString(), ContentVersion = report.Version,
            ContentJson = JsonSerializer.Serialize(report, Json),
        });
        var position = 0;
        db.WeeklyReportItems.AddRange(report.Stories.Where(DeliveryReportStories.IsDefaultVisible).Select(s => new DeliveryWeeklyReportItemRecord
        {
            Id = Guid.NewGuid(), ReportId = reportId, Section = "Story", Position = position++, FeatureKey = s.FeatureKey, Title = Cap(s.Title, 400),
            Movement = s.Movement.ToString(), Text = Cap(string.Join("; ", s.Reasons), 2000),
        }).ToList());
        db.WeeklyReportItems.AddRange(report.Attention.Select(a => new DeliveryWeeklyReportItemRecord
        {
            Id = Guid.NewGuid(), ReportId = reportId, Section = "Attention", Position = position++, FeatureKey = a.FeatureKey, Title = Cap(a.Title, 400),
            Kind = a.Kind.ToString(), Text = Cap(a.Detail, 2000),
        }).ToList());
        db.WeeklyReportItems.AddRange(report.NextWeekFocus.Select(f => new DeliveryWeeklyReportItemRecord
        {
            Id = Guid.NewGuid(), ReportId = reportId, Section = "Focus", Position = position++, Title = Cap(f.Text, 400), Text = Cap(string.Join("; ", f.Features), 2000),
        }).ToList());
        await db.SaveChangesAsync(ct);
        return new(report, null, StatusCodes.Status200OK);
    }

    public async Task<List<DeliveryReportSummaryRow>> ListAsync(Guid projectId, CancellationToken ct)
    {
        var records = await db.WeeklyReports.AsNoTracking().Where(r => r.ProjectId == projectId).OrderByDescending(r => r.GeneratedAt).ToListAsync(ct);
        return records.Select(r =>
        {
            var report = Deserialize(r);
            return new DeliveryReportSummaryRow(r.Id, r.Title, r.WeekLabel, r.PeriodStart, r.PeriodEnd, r.GeneratedAt, Enum.Parse<DeliveryCoverage>(r.CoverageStatus),
                report?.Baseline.Label ?? "", report?.Current.Label ?? "", report?.Stories.Count(s => s.Changed) ?? 0, report?.Attention.Count ?? 0);
        }).ToList();
    }

    /// <summary>The frozen report exactly as generated.</summary>
    public async Task<WeeklyDeliveryReport?> GetAsync(Guid projectId, Guid reportId, CancellationToken ct)
    {
        var record = await db.WeeklyReports.AsNoTracking().FirstOrDefaultAsync(r => r.ProjectId == projectId && r.Id == reportId, ct);
        return record is null ? null : Deserialize(record);
    }

    public async Task<DeliveryReportExport?> ExportAsync(Guid projectId, Guid reportId, DeliveryExportFormat format, DeliveryExportOptions options, CancellationToken ct) =>
        await GetAsync(projectId, reportId, ct) is { } report ? DeliveryReportExporter.Export(report, format, options) : null;

    /// <summary>Deletes the report with its comparison, deltas, events and items. Snapshots are untouched.</summary>
    public async Task<bool> DeleteAsync(Guid projectId, Guid reportId, CancellationToken ct)
    {
        var record = await db.WeeklyReports.FirstOrDefaultAsync(r => r.ProjectId == projectId && r.Id == reportId, ct);
        if (record is null) return false;
        var comparison = await db.Comparisons.FirstOrDefaultAsync(c => c.Id == record.ComparisonId, ct);
        db.WeeklyReports.Remove(record);
        if (comparison is not null) db.Comparisons.Remove(comparison);
        await db.SaveChangesAsync(ct);
        return true;
    }

    private static WeeklyDeliveryReport? Deserialize(DeliveryWeeklyReportRecord record) => JsonSerializer.Deserialize<WeeklyDeliveryReport>(record.ContentJson, Json);

    private static string Cap(string value, int length) => value.Length > length ? value[..length] : value;
}
