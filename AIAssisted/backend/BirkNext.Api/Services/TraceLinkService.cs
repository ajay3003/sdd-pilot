using BirkNext.Api.Data;
using BirkNext.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services;

public class TraceLinkResult
{
    public TraceLink? TraceLink { get; init; }
    public IReadOnlyList<UserError> Errors { get; init; } = [];
}

public class DeleteTraceLinkResult
{
    public string? DeletedId { get; init; }
    public IReadOnlyList<UserError> Errors { get; init; } = [];
    public bool IsSuccess => DeletedId is not null;
}

public record TraceLinkWithTest(TraceLink Link, Scenario Test);

public class TraceabilityMatrixRow
{
    public Scenario Requirement { get; init; } = null!;
    public IReadOnlyList<TraceLinkWithTest> LinkedTests { get; init; } = [];
    public CoverageStatus CoverageStatus { get; init; }
}

public class CoverageSummary
{
    public int TotalRequirements { get; init; }
    public int CoveredRequirements { get; init; }
    public int NotCoveredRequirements { get; init; }
    public double CoveragePercent { get; init; }
    public int OrphanTests { get; init; }

    /// <summary>Requirements with pending suggestions but no confirmed coverage.</summary>
    public int SuggestedCoverageRequirements { get; init; }

    /// <summary>Total pending (non-rejected) suggestions for this project.</summary>
    public int PendingSuggestionCount { get; init; }

    /// <summary>High-confidence pending suggestions.</summary>
    public int HighConfidenceSuggestionCount { get; init; }
}

public enum CoverageStatus { Covered, NotCovered }

public sealed class TraceLinkService
{
    private readonly AppDbContext _db;
    private readonly ILogger<TraceLinkService>? _logger;

    public TraceLinkService(AppDbContext db, ILogger<TraceLinkService>? logger = null)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>Creates a trace link after validating both endpoints exist in the project.</summary>
    public async Task<TraceLinkResult> CreateAsync(
        string projectId,
        Guid sourceId,
        string sourceKind,
        Guid targetId,
        string targetKind,
        TraceLinkType linkType,
        string? createdBy,
        string? notes,
        string correlationId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            throw new ArgumentException("Project id is required.", nameof(projectId));

        if (sourceId == targetId)
        {
            _logger?.LogWarning(
                "TraceLinkValidationFailed {CorrelationId} {ProjectId} {ErrorCodes}",
                correlationId, projectId, "SELF_LINK");
            return new TraceLinkResult
            {
                Errors = [new UserError("SELF_LINK", "Source and target must be different artifacts.")]
            };
        }

        var errors = new List<UserError>();

        if (sourceKind == TraceLinkArtifactKind.Scenario)
        {
            var exists = await _db.Scenarios
                .AnyAsync(s => s.Id == sourceId && s.ProjectId == projectId, ct);
            if (!exists)
                errors.Add(new UserError("SOURCE_NOT_FOUND", "Source scenario not found.", "sourceId"));
        }

        if (targetKind == TraceLinkArtifactKind.Scenario)
        {
            var exists = await _db.Scenarios
                .AnyAsync(s => s.Id == targetId && s.ProjectId == projectId, ct);
            if (!exists)
                errors.Add(new UserError("TARGET_NOT_FOUND", "Target scenario not found.", "targetId"));
        }

        if (errors.Count > 0)
        {
            _logger?.LogWarning(
                "TraceLinkValidationFailed {CorrelationId} {ProjectId} {ErrorCodes}",
                correlationId, projectId, string.Join(",", errors.Select(e => e.Code)));
            return new TraceLinkResult { Errors = errors };
        }

        var duplicate = await _db.TraceLinks.AnyAsync(
            t => t.ProjectId == projectId
              && t.SourceId == sourceId && t.SourceKind == sourceKind
              && t.TargetId == targetId && t.TargetKind == targetKind
              && t.LinkType == linkType,
            ct);

        if (duplicate)
        {
            _logger?.LogWarning(
                "TraceLinkDuplicateRejected {CorrelationId} {ProjectId}",
                correlationId, projectId);
            return new TraceLinkResult
            {
                Errors = [new UserError("DUPLICATE_LINK", "This trace link already exists.")]
            };
        }

        var link = new TraceLink
        {
            ProjectId = projectId,
            SourceId = sourceId,
            SourceKind = sourceKind,
            TargetId = targetId,
            TargetKind = targetKind,
            LinkType = linkType,
            CreatedBy = createdBy,
            Notes = notes,
        };

        _db.TraceLinks.Add(link);
        await _db.SaveChangesAsync(ct);

        _logger?.LogInformation(
            "TraceLinkCreated {CorrelationId} {ProjectId} {TraceLinkId}",
            correlationId, projectId, link.Id);

        return new TraceLinkResult { TraceLink = link };
    }

    /// <summary>Deletes a trace link by ID. ProjectId is required for safety scoping.</summary>
    public async Task<DeleteTraceLinkResult> DeleteAsync(
        Guid id,
        string projectId,
        string correlationId,
        CancellationToken ct = default)
    {
        var link = await _db.TraceLinks
            .FirstOrDefaultAsync(t => t.Id == id && t.ProjectId == projectId, ct);

        if (link is null)
        {
            _logger?.LogWarning(
                "TraceLinkDeleteNotFound {CorrelationId} {TraceLinkId} {ProjectId}",
                correlationId, id, projectId);
            return new DeleteTraceLinkResult
            {
                Errors = [new UserError("NOT_FOUND", "Trace link not found.")]
            };
        }

        _db.TraceLinks.Remove(link);
        await _db.SaveChangesAsync(ct);

        _logger?.LogInformation(
            "TraceLinkDeleted {CorrelationId} {ProjectId} {TraceLinkId}",
            correlationId, projectId, id);

        return new DeleteTraceLinkResult { DeletedId = id.ToString() };
    }

    /// <summary>
    /// Returns one matrix row per Requirement scenario in the project,
    /// each with its linked Test scenarios and computed coverage status.
    /// </summary>
    public async Task<IReadOnlyList<TraceabilityMatrixRow>> GetTraceabilityMatrixAsync(
        string projectId,
        CancellationToken ct = default)
    {
        var (requirements, tests, links) = await LoadCoverageDataAsync(projectId, ct);

        var testById = tests.ToDictionary(t => t.Id);
        var linksByTarget = links
            .Where(l => l.LinkType == TraceLinkType.Covers)
            .GroupBy(l => l.TargetId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var rows = requirements.Select(req =>
        {
            var reqLinks = linksByTarget.TryGetValue(req.Id, out var ls) ? ls : [];
            var linked = reqLinks
                .Where(l => testById.ContainsKey(l.SourceId))
                .Select(l => new TraceLinkWithTest(l, testById[l.SourceId]))
                .ToList();

            return new TraceabilityMatrixRow
            {
                Requirement = req,
                LinkedTests = linked,
                CoverageStatus = linked.Count > 0 ? CoverageStatus.Covered : CoverageStatus.NotCovered,
            };
        }).ToList();

        return rows;
    }

    /// <summary>
    /// Returns aggregate coverage statistics for the project.
    /// </summary>
    public async Task<CoverageSummary> GetCoverageSummaryAsync(
        string projectId,
        CancellationToken ct = default)
    {
        var (requirements, tests, links) = await LoadCoverageDataAsync(projectId, ct);

        var testIds = tests.Select(t => t.Id).ToHashSet();
        var coveredTargets = links
            .Where(l => l.LinkType == TraceLinkType.Covers && testIds.Contains(l.SourceId))
            .Select(l => l.TargetId)
            .ToHashSet();

        var coveredSources = links
            .Where(l => l.LinkType == TraceLinkType.Covers)
            .Select(l => l.SourceId)
            .ToHashSet();

        int total = requirements.Count;
        int covered = requirements.Count(r => coveredTargets.Contains(r.Id));
        int notCovered = total - covered;
        double percent = total > 0 ? Math.Round((double)covered / total * 100, 1) : 0.0;
        int orphans = tests.Count(t => !coveredSources.Contains(t.Id));

        return new CoverageSummary
        {
            TotalRequirements = total,
            CoveredRequirements = covered,
            NotCoveredRequirements = notCovered,
            CoveragePercent = percent,
            OrphanTests = orphans,
        };
    }

    private async Task<(List<Scenario> Requirements, List<Scenario> Tests, List<TraceLink> Links)>
        LoadCoverageDataAsync(string projectId, CancellationToken ct)
    {
        var scenarios = await _db.Scenarios
            .Where(s => s.ProjectId == projectId
                     && (s.Kind == ScenarioKind.Requirement || s.Kind == ScenarioKind.Test))
            .ToListAsync(ct);

        var requirements = scenarios.Where(s => s.Kind == ScenarioKind.Requirement).ToList();
        var tests = scenarios.Where(s => s.Kind == ScenarioKind.Test).ToList();

        var links = await _db.TraceLinks
            .Where(t => t.ProjectId == projectId
                     && t.SourceKind == TraceLinkArtifactKind.Scenario
                     && t.TargetKind == TraceLinkArtifactKind.Scenario)
            .ToListAsync(ct);

        return (requirements, tests, links);
    }
}
