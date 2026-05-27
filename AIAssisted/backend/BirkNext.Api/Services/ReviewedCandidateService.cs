using BirkNext.Api.Data;
using BirkNext.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services;

public sealed class ReviewedCandidateService
{
    private readonly AppDbContext _db;
    private readonly ILogger<ReviewedCandidateService> _logger;

    public ReviewedCandidateService(AppDbContext db, ILogger<ReviewedCandidateService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<int> SaveBatchAsync(
        string projectId,
        string sessionId,
        IEnumerable<ReviewedCandidateItem> items,
        string correlationId,
        CancellationToken ct = default)
    {
        var existing = await _db.ReviewedCandidates
            .Where(c => c.ProjectId == projectId && c.SessionId == sessionId)
            .ToListAsync(ct);

        if (existing.Count > 0)
            _db.ReviewedCandidates.RemoveRange(existing);

        var entities = items.Select(item => new ReviewedCandidate
        {
            Title        = item.Title,
            Classification = item.Classification,
            ReviewStatus  = item.ReviewStatus,
            SourceDocument = item.SourceDocument,
            SourceSection  = item.SourceSection,
            ProjectId     = projectId,
            SessionId     = sessionId,
            ReviewedBy    = item.ReviewedBy ?? "placeholder",
            ReviewedAt    = item.ReviewedAt,
        }).ToList();

        _db.ReviewedCandidates.AddRange(entities);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "ReviewBatchSaved: correlationId={CorrelationId}, projectId={ProjectId}, sessionId={SessionId}, savedCount={SavedCount}",
            correlationId, projectId, sessionId, entities.Count);

        return entities.Count;
    }

    public async Task<IReadOnlyList<ReviewedCandidate>> GetByProjectAsync(
        string projectId,
        string? sessionId,
        CancellationToken ct = default)
    {
        var query = _db.ReviewedCandidates.Where(c => c.ProjectId == projectId);
        if (sessionId is not null)
            query = query.Where(c => c.SessionId == sessionId);
        return await query.OrderByDescending(c => c.CreatedAt).ToListAsync(ct);
    }
}

public record ReviewedCandidateItem(
    string Title,
    ScenarioKind Classification,
    CandidateReviewStatus ReviewStatus,
    string? SourceDocument,
    string? SourceSection,
    string? ReviewedBy,
    DateTimeOffset? ReviewedAt);
