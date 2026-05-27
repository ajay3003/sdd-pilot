using BirkNext.Api.Data;
using BirkNext.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services;

public sealed class CandidateLinkService
{
    private readonly AppDbContext _db;
    private readonly ILogger<CandidateLinkService> _logger;

    public CandidateLinkService(AppDbContext db, ILogger<CandidateLinkService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<int> SaveBatchAsync(
        string projectId,
        string sessionId,
        IEnumerable<CandidateLinkItem> links,
        string correlationId,
        CancellationToken ct = default)
    {
        var existing = await _db.CandidateLinks
            .Where(l => l.ProjectId == projectId && l.SessionId == sessionId)
            .ToListAsync(ct);

        if (existing.Count > 0)
            _db.CandidateLinks.RemoveRange(existing);

        var entities = links.Select(link => new CandidateLink
        {
            ProjectId          = projectId,
            SessionId          = sessionId,
            SourceCandidateRef = link.SourceCandidateRef,
            TargetCandidateRef = link.TargetCandidateRef,
            LinkType           = link.LinkType,
        }).ToList();

        _db.CandidateLinks.AddRange(entities);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "CandidateLinksSaved: correlationId={CorrelationId}, projectId={ProjectId}, sessionId={SessionId}, savedCount={SavedCount}",
            correlationId, projectId, sessionId, entities.Count);

        return entities.Count;
    }

    public async Task<IReadOnlyList<CandidateLink>> GetByProjectAsync(
        string projectId,
        string? sessionId,
        CancellationToken ct = default)
    {
        var query = _db.CandidateLinks.Where(l => l.ProjectId == projectId);
        if (sessionId is not null)
            query = query.Where(l => l.SessionId == sessionId);
        return await query.OrderByDescending(l => l.CreatedAt).ToListAsync(ct);
    }
}

public record CandidateLinkItem(
    string SourceCandidateRef,
    string TargetCandidateRef,
    CandidateLinkType LinkType);
