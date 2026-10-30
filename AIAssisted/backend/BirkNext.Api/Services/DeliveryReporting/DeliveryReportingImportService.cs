using System.Collections.Concurrent;
using System.Text.Json;
using BirkNext.Api.Data.DeliveryReporting;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.DeliveryReporting;
using BirkNext.Web.Services.Explorers;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services.DeliveryReporting;

/// <summary>
/// Parsed-but-not-stored imports between preview and confirmation. Only the normalized parse result is held (never the archive bytes), in
/// memory, for 30 minutes, at most <see cref="Capacity"/> at a time.
/// </summary>
public sealed class DeliveryImportStagingStore(TimeProvider clock)
{
    public const int Capacity = 6;
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);
    private readonly ConcurrentDictionary<Guid, (ParsedDeliverySnapshot Snapshot, DateTimeOffset Expires)> _items = new();

    public Guid Add(ParsedDeliverySnapshot snapshot)
    {
        Sweep();
        while (_items.Count >= Capacity && _items.OrderBy(i => i.Value.Expires).FirstOrDefault() is { Key: var oldest } && oldest != Guid.Empty)
            _items.TryRemove(oldest, out _);
        var id = Guid.NewGuid();
        _items[id] = (snapshot, clock.GetUtcNow() + Lifetime);
        return id;
    }

    public ParsedDeliverySnapshot? Get(Guid id)
    {
        Sweep();
        return _items.TryGetValue(id, out var item) ? item.Snapshot : null;
    }

    public void Remove(Guid id) => _items.TryRemove(id, out _);

    private void Sweep()
    {
        var now = clock.GetUtcNow();
        foreach (var expired in _items.Where(i => i.Value.Expires <= now).Select(i => i.Key).ToList()) _items.TryRemove(expired, out _);
    }
}

public sealed record DeliveryPreviewOutcome(DeliveryImportPreview? Preview, SourceArchiveValidationFailure? Failure);

public sealed record DeliveryCommitOutcome(DeliveryImportCommitResult Result, int StatusCode);

/// <summary>
/// Delivery Reporting's own import: ZIP → preview → confirm → immutable snapshot in delivery_reporting. It never creates Import Project records,
/// Source Analysis snapshots or workspace artifacts, and never touches Target Environment, bindings, Test Data or Connected Systems.
/// <para>Archive retention: the uploaded bytes are not stored. A snapshot keeps the normalized stories, tasks and per-file fingerprints, which is
/// all comparison and reporting read; the source row keeps file name, size, fingerprint and Git commit for provenance and duplicate detection.
/// Storing whole repository ZIPs (up to 50 MB each, every week) in PostgreSQL would grow the database without serving any report.</para>
/// </summary>
public sealed class DeliveryReportingImportService(DeliveryReportingDbContext db, DeliveryImportStagingStore staging, TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<DeliveryPreviewOutcome> PreviewAsync(Guid? projectId, string fileName, byte[] bytes, CancellationToken ct)
    {
        var parsed = DeliverySnapshotParser.Parse(fileName, bytes, ct);
        if (parsed.Failure is { } failure) return new(null, failure);
        var snapshot = parsed.Snapshot!;
        var stagingId = staging.Add(snapshot);
        var duplicate = projectId is { } id ? await DuplicateWarningAsync(id, snapshot, ct) : null;
        return new(new DeliveryImportPreview
        {
            StagingId = stagingId,
            FileName = snapshot.FileName,
            Fingerprint = snapshot.Fingerprint,
            SourceType = snapshot.SourceType,
            GitCommit = snapshot.GitCommit,
            SuggestedEffectiveAt = snapshot.SuggestedEffectiveAt,
            SuggestedEffectiveReason = snapshot.SuggestedEffectiveReason,
            Modules = snapshot.Modules,
            Features = snapshot.Features.Select(f => new DeliveryPreviewFeature(f.Key, f.Title, ArtifactModules.Label(f.Module), f.SpecPath is not null,
                f.TasksPath is not null, f.TaskTotal, f.TaskCompleted, DeliveryReportingText.Status(f.Status))
                { PhaseCount = f.Phases.Count, StoryCount = f.Stories.Count, CriteriaCount = f.Criteria.Count }).ToList(),
            PhaseCount = snapshot.Features.Sum(f => f.Phases.Count), StoryCount = snapshot.Features.Sum(f => f.Stories.Count), CriteriaCount = snapshot.Features.Sum(f => f.Criteria.Count),
            TaskTotal = snapshot.Features.Sum(f => f.TaskTotal),
            TaskCompleted = snapshot.Features.Sum(f => f.TaskCompleted),
            Coverage = snapshot.Coverage,
            Warnings = snapshot.Warnings,
            DuplicateWarning = duplicate,
        }, null);
    }

    public void Discard(Guid stagingId) => staging.Remove(stagingId);

    public async Task<DeliveryCommitOutcome> CommitAsync(Guid stagingId, DeliveryImportCommitRequest request, CancellationToken ct)
    {
        var snapshot = staging.Get(stagingId);
        if (snapshot is null)
            return new(new(null, null, "The preview expired. Choose the ZIP again."), StatusCodes.Status404NotFound);
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == request.ProjectId, ct);
        if (project is null) return new(new(null, null, "Choose a reporting project first."), StatusCodes.Status400BadRequest);
        if (request.EffectiveAt == default) return new(new(null, null, "Set the effective snapshot date."), StatusCodes.Status400BadRequest);
        var duplicate = await DuplicateWarningAsync(project.Id, snapshot, ct);
        if (duplicate is not null && !request.ConfirmDuplicate)
            return new(new(null, duplicate, null), StatusCodes.Status409Conflict);

        var now = clock.GetUtcNow();
        var source = new DeliveryReportSourceRecord
        {
            Id = Guid.NewGuid(), ProjectId = project.Id, SourceType = snapshot.SourceType.ToString(), OriginalFileName = snapshot.FileName,
            ImportedAt = now, Fingerprint = snapshot.Fingerprint, GitCommit = snapshot.GitCommit, ArchiveBytes = snapshot.ArchiveBytes,
            EntryCount = snapshot.EntryCount,
            MetadataJson = JsonSerializer.Serialize(new { wrapperFolder = snapshot.WrapperFolder, suggestedEffectiveAt = snapshot.SuggestedEffectiveAt, archiveRetained = false }, Json),
        };
        var label = string.IsNullOrWhiteSpace(request.Label)
            ? $"{DeliveryReportingText.Role(request.Role)} {request.EffectiveAt:yyyy-MM-dd}"
            : request.Label.Trim();
        var record = new DeliverySnapshotRecord
        {
            Id = Guid.NewGuid(), ProjectId = project.Id, ReportSourceId = source.Id, Label = label.Length > 200 ? label[..200] : label,
            EffectiveAt = request.EffectiveAt.ToUniversalTime(), ImportedAt = now, Role = request.Role.ToString(),
            CoverageStatus = snapshot.Coverage.ToString(),
            ParseStatus = snapshot.Warnings.Count == 0 ? "Parsed" : "ParsedWithWarnings",
            WarningsCount = snapshot.Warnings.Count, FeatureCount = snapshot.Features.Count,
            TaskCount = snapshot.Features.Sum(f => f.TaskTotal), CompletedTaskCount = snapshot.Features.Sum(f => f.TaskCompleted),
            ModulesJson = JsonSerializer.Serialize(snapshot.Modules, Json), WarningsJson = JsonSerializer.Serialize(snapshot.Warnings, Json),
        };
        db.ReportSources.Add(source);
        db.Snapshots.Add(record);
        foreach (var feature in snapshot.Features)
        {
            var featureRecord = new DeliveryFeatureSnapshotRecord
            {
                Id = Guid.NewGuid(), SnapshotId = record.Id, FeatureKey = feature.Key, Module = ArtifactModules.Label(feature.Module), Folder = feature.Folder,
                Title = feature.Title, Position = feature.Position, SpecPath = feature.SpecPath, TasksPath = feature.TasksPath, PlanPath = feature.PlanPath,
                SpecFingerprint = feature.SpecFingerprint, AcceptanceFingerprint = feature.AcceptanceFingerprint,
                AcceptanceCriteriaJson = JsonSerializer.Serialize(feature.AcceptanceCriteria, Json), RequirementCount = feature.RequirementCount,
                UserStoryCount = feature.UserStoryCount, OpenClarifications = feature.OpenClarifications, TaskTotal = feature.TaskTotal,
                TaskCompleted = feature.TaskCompleted, UntrackedTaskCount = feature.UntrackedTaskCount, Status = feature.Status.ToString(),
                StatusReason = feature.StatusReason, HierarchyCaptured = true,
            };
            db.FeatureSnapshots.Add(featureRecord);
            db.TaskSnapshots.AddRange(feature.Tasks.Select(t => new DeliveryTaskSnapshotRecord
            {
                Id = Guid.NewGuid(), SnapshotId = record.Id, FeatureSnapshotId = featureRecord.Id, TaskKey = t.TaskKey, TaskId = t.TaskId, Text = t.Text,
                Completed = t.Completed, Phase = t.Phase, UserStory = t.UserStory, PhaseKey = t.PhaseKey, UserStoryKey = t.UserStoryKey, Position = t.Position, SourcePath = t.SourcePath,
            }));
            db.PhaseSnapshots.AddRange(feature.Phases.Select(ph => new DeliveryPhaseSnapshotRecord
            {
                Id = Guid.NewGuid(), SnapshotId = record.Id, FeatureSnapshotId = featureRecord.Id, PhaseKey = ph.Key, Title = ph.Title, Position = ph.Position,
            }));
            db.UserStorySnapshots.AddRange(feature.Stories.Select(st => new DeliveryUserStorySnapshotRecord
            {
                Id = Guid.NewGuid(), SnapshotId = record.Id, FeatureSnapshotId = featureRecord.Id, StoryKey = st.Key, Title = st.Title, Description = st.Description,
                Priority = st.Priority, PhaseKeysJson = JsonSerializer.Serialize(st.PhaseKeys, Json), Position = st.Position, SourceLine = st.Line,
            }));
            db.AcceptanceCriterionSnapshots.AddRange(feature.Criteria.Select(c => new DeliveryAcceptanceCriterionSnapshotRecord
            {
                Id = Guid.NewGuid(), SnapshotId = record.Id, FeatureSnapshotId = featureRecord.Id, StoryKey = c.StoryKey, CriterionKey = c.Key, Text = c.Text,
                Position = c.Position, SourceLine = c.Line,
            }));
            db.EvidenceReferences.AddRange(feature.Evidence.Select(e => new DeliveryEvidenceReferenceRecord
            {
                Id = Guid.NewGuid(), SnapshotId = record.Id, FeatureSnapshotId = featureRecord.Id, Kind = e.Kind, Path = e.Path, Line = e.Line, Text = e.Text,
            }));
        }
        db.ArtifactSnapshots.AddRange(snapshot.Artifacts.Select(a => new DeliveryArtifactSnapshotRecord
        {
            Id = Guid.NewGuid(), SnapshotId = record.Id, Path = a.Path, Module = ArtifactModules.Label(a.Module), FeatureKey = a.FeatureKey, Kind = a.Kind, Fingerprint = a.Fingerprint,
        }));
        project.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        staging.Remove(stagingId);
        return new(new(DeliverySnapshotStore.Summary(record, source, 0), duplicate, null), StatusCodes.Status200OK);
    }

    private async Task<string?> DuplicateWarningAsync(Guid projectId, ParsedDeliverySnapshot snapshot, CancellationToken ct)
    {
        var matches = await (from s in db.Snapshots
                             join src in db.ReportSources on s.ReportSourceId equals src.Id
                             where s.ProjectId == projectId && (src.Fingerprint == snapshot.Fingerprint || (snapshot.GitCommit != null && src.GitCommit == snapshot.GitCommit))
                             orderby s.ImportedAt
                             select new { s.Label, s.EffectiveAt, src.Fingerprint, src.GitCommit }).ToListAsync(ct);
        if (matches.Count == 0) return null;
        var first = matches[0];
        var reason = first.Fingerprint == snapshot.Fingerprint ? "the same archive" : $"the same Git commit ({snapshot.GitCommit![..10]})";
        return $"This source appears to have already been imported: \"{first.Label}\" (effective {first.EffectiveAt:yyyy-MM-dd}) has {reason}. Import again only if you want a second snapshot.";
    }
}
