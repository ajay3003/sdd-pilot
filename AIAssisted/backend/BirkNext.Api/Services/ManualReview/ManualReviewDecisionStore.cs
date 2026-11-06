using System.Text.Json;
using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.ManualReview;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services.ManualReview;

/// <summary>
/// Durable manual review decisions (API Quality Review obligations, M01 security controls). Append-only: recording a decision adds a row; the
/// latest row per (module, scope, subject) is current and earlier rows are its audit history. Nothing here derives or changes an automated result.
/// </summary>
public sealed class ManualReviewDecisionStore(AppDbContext db, TimeProvider? clock = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<(ManualReviewDecisionView? Decision, string? Error)> RecordAsync(ManualReviewDecisionRequest request, CancellationToken ct = default)
    {
        var normalized = request with
        {
            ScopeKey = request.ScopeKey.Trim(), SubjectKey = request.SubjectKey.Trim(), Reviewer = request.Reviewer.Trim(), Note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim(),
            AppliesTo = string.IsNullOrWhiteSpace(request.AppliesTo) ? null : request.AppliesTo.Trim(), EvidenceReferences = [.. request.EvidenceReferences.Select(e => e.Trim()).Where(e => e.Length > 0).Distinct()],
        };
        if (ManualReviewRules.Validate(normalized) is { } error) return (null, error);
        var record = new ManualReviewDecisionRecord
        {
            Id = Guid.NewGuid(), Module = normalized.Module, ScopeKey = normalized.ScopeKey, SubjectKey = normalized.SubjectKey, Reviewer = normalized.Reviewer,
            Decision = normalized.Decision.ToString(), Note = normalized.Note, EvidenceJson = JsonSerializer.Serialize(normalized.EvidenceReferences, Json),
            AppliesTo = normalized.AppliesTo, DecidedAt = (clock ?? TimeProvider.System).GetUtcNow(),
        };
        db.ManualReviewDecisions.Add(record);
        await db.SaveChangesAsync(ct);
        return (View(record, current: true), null);
    }

    /// <summary>Every decision of a module + scope (optionally one subject), newest first; <see cref="ManualReviewDecisionView.Current"/> marks the latest per subject.</summary>
    public async Task<IReadOnlyList<ManualReviewDecisionView>> ListAsync(string module, string scopeKey, string? subjectKey = null, CancellationToken ct = default)
    {
        var query = db.ManualReviewDecisions.AsNoTracking().Where(r => r.Module == module && r.ScopeKey == scopeKey);
        if (!string.IsNullOrWhiteSpace(subjectKey)) query = query.Where(r => r.SubjectKey == subjectKey);
        var rows = (await query.ToListAsync(ct)).OrderByDescending(r => r.DecidedAt).ThenByDescending(r => r.Id).ToList();
        var latest = rows.GroupBy(r => r.SubjectKey).Select(g => g.First().Id).ToHashSet();
        return [.. rows.Select(r => View(r, latest.Contains(r.Id)))];
    }

    private static ManualReviewDecisionView View(ManualReviewDecisionRecord r, bool current) => new()
    {
        Id = r.Id, Module = r.Module, ScopeKey = r.ScopeKey, SubjectKey = r.SubjectKey, Reviewer = r.Reviewer,
        Decision = Enum.TryParse<ManualReviewDecisionValue>(r.Decision, out var d) ? d : ManualReviewDecisionValue.NeedsEvidence,
        Note = r.Note, EvidenceReferences = JsonSerializer.Deserialize<List<string>>(r.EvidenceJson, Json) ?? [], AppliesTo = r.AppliesTo, DecidedAt = r.DecidedAt, Current = current,
    };
}
