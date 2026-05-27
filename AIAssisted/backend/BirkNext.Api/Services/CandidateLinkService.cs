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
        if (string.IsNullOrWhiteSpace(projectId))
            throw new ArgumentException("Project id is required.", nameof(projectId));
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new ArgumentException("Session id is required.", nameof(sessionId));

        var normalizedLinks = NormalizeAndValidate(links);

        var existing = await _db.CandidateLinks
            .Where(l => l.ProjectId == projectId && l.SessionId == sessionId)
            .ToListAsync(ct);

        if (existing.Count > 0)
            _db.CandidateLinks.RemoveRange(existing);

        var entities = normalizedLinks.Select(link => new CandidateLink
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

    private static IReadOnlyList<CandidateLinkItem> NormalizeAndValidate(IEnumerable<CandidateLinkItem> links)
    {
        var result = new List<CandidateLinkItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var link in links)
        {
            if (string.IsNullOrWhiteSpace(link.SourceCandidateRef))
                throw new ArgumentException("Source candidate reference is required.", nameof(links));
            if (string.IsNullOrWhiteSpace(link.TargetCandidateRef))
                throw new ArgumentException("Target candidate reference is required.", nameof(links));
            if (string.Equals(link.SourceCandidateRef, link.TargetCandidateRef, StringComparison.Ordinal))
                throw new ArgumentException("A candidate cannot be linked to itself.", nameof(links));

            var key = LinkKey(link.SourceCandidateRef, link.TargetCandidateRef, link.LinkType);
            if (!seen.Add(key))
                continue;

            result.Add(link);
        }

        return result;
    }

    private static string LinkKey(string source, string target, CandidateLinkType type)
    {
        var first = string.CompareOrdinal(source, target) <= 0 ? source : target;
        var second = string.CompareOrdinal(source, target) <= 0 ? target : source;
        return $"{type}:{first}:{second}";
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
