using BirkNext.Api.Data;
using BirkNext.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services;

public class ProjectDocumentService(AppDbContext db, ILogger<ProjectDocumentService>? logger = null)
{
    public async Task<string?> GetContentAsync(ProjectDocumentKind kind, CancellationToken ct = default)
    {
        var doc = await db.ProjectDocuments
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.DocumentKind == kind, ct);
        return doc?.Content;
    }

    public async Task UpsertAsync(ProjectDocumentKind kind, string content, CancellationToken ct = default)
    {
        var existing = await db.ProjectDocuments
            .FirstOrDefaultAsync(d => d.DocumentKind == kind, ct);

        if (existing is null)
        {
            db.ProjectDocuments.Add(new ProjectDocument
            {
                DocumentKind = kind,
                Content = content
            });
        }
        else
        {
            existing.Content = content;
            existing.UpdatedUtc = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(ct);
        logger?.LogInformation("ProjectDocument upserted {Kind} ({Length} chars)", kind, content.Length);
    }

    public async Task<IReadOnlyList<ProjectDocumentSummary>> GetSummaryAsync(CancellationToken ct = default)
    {
        var docs = await db.ProjectDocuments
            .AsNoTracking()
            .OrderBy(d => d.DocumentKind)
            .Select(d => new { d.DocumentKind, Length = d.Content.Length, d.UpdatedUtc })
            .ToListAsync(ct);
        return docs.Select(d => new ProjectDocumentSummary(d.DocumentKind.ToString(), d.Length, d.UpdatedUtc))
            .ToList();
    }
}

public record ProjectDocumentSummary(string DocumentKind, int ContentLengthChars, DateTimeOffset UpdatedUtc);
