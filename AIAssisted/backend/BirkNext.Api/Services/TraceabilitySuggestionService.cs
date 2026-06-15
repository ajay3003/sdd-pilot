using System.Text.Json;
using System.Text.RegularExpressions;
using BirkNext.Api.Data;
using BirkNext.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services;

public sealed class SuggestionSummary
{
    public int TotalGenerated { get; init; }
    public int HighConfidenceCount { get; init; }
    public int NeedsReviewCount { get; init; }
    public int SkippedAlreadyExists { get; init; }
}

public sealed class SuggestionItem
{
    public TraceabilitySuggestion Suggestion { get; init; } = null!;
    public string SourceTitle { get; init; } = string.Empty;
    public string TargetTitle { get; init; } = string.Empty;
}

public sealed class ConfirmSuggestionResult
{
    public TraceLink? TraceLink { get; init; }
    public IReadOnlyList<UserError> Errors { get; init; } = [];
    public bool IsSuccess => TraceLink is not null;
}

public sealed class RejectSuggestionResult
{
    public bool IsSuccess { get; init; }
    public IReadOnlyList<UserError> Errors { get; init; } = [];
}

public sealed class TraceabilitySuggestionService
{
    private const double HighConfidenceThreshold = 0.75;
    private const double MinimumConfidenceThreshold = 0.25;

    private static readonly Regex IdPattern = new(
        @"\b(FR|SC|US|TC|TEST|TASK|T)[-]?\s*(\d+)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "is", "are", "was", "were", "be", "been", "being",
        "have", "has", "had", "do", "does", "did", "will", "would", "could",
        "should", "may", "might", "shall", "can", "need", "must", "that",
        "this", "these", "those", "it", "its", "if", "when", "where", "who",
        "which", "and", "or", "not", "no", "in", "on", "at", "to", "for",
        "of", "from", "with", "by", "as", "so", "then", "than", "but", "also",
        "given", "when", "then", "and", "scenario", "test", "requirement",
        "should", "must", "will", "shall", "without", "user", "users",
    };

    private readonly AppDbContext _db;
    private readonly ILogger<TraceabilitySuggestionService>? _logger;

    public TraceabilitySuggestionService(AppDbContext db, ILogger<TraceabilitySuggestionService>? logger = null)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Generates traceability suggestions for all (requirement, test) pairs in the project.
    /// Skips pairs that already have confirmed trace links or existing suggestions (unless rejected).
    /// </summary>
    public async Task<SuggestionSummary> GenerateSuggestionsAsync(
        string projectId,
        CancellationToken ct = default)
    {
        var scenarios = await _db.Scenarios
            .Where(s => s.ProjectId == projectId
                     && (s.Kind == ScenarioKind.Requirement || s.Kind == ScenarioKind.Test))
            .ToListAsync(ct);

        var requirements = scenarios.Where(s => s.Kind == ScenarioKind.Requirement).ToList();
        var tests = scenarios.Where(s => s.Kind == ScenarioKind.Test).ToList();

        if (requirements.Count == 0 || tests.Count == 0)
            return new SuggestionSummary();

        var confirmedLinks = await _db.TraceLinks
            .Where(t => t.ProjectId == projectId
                     && t.SourceKind == TraceLinkArtifactKind.Scenario
                     && t.TargetKind == TraceLinkArtifactKind.Scenario
                     && t.LinkType == TraceLinkType.Covers)
            .Select(t => new { t.SourceId, t.TargetId })
            .ToListAsync(ct);

        var confirmedPairs = confirmedLinks
            .Select(l => (l.SourceId, l.TargetId))
            .ToHashSet();

        var existingSuggestions = await _db.TraceabilitySuggestions
            .Where(s => s.ProjectId == projectId)
            .Select(s => new { s.SourceId, s.TargetId, s.LinkType, s.Status })
            .ToListAsync(ct);

        var rejectedPairs = existingSuggestions
            .Where(s => s.Status == TraceabilitySuggestionStatus.Rejected)
            .Select(s => (s.SourceId, s.TargetId))
            .ToHashSet();

        var activePairs = existingSuggestions
            .Where(s => s.Status != TraceabilitySuggestionStatus.Rejected)
            .Select(s => (s.SourceId, s.TargetId))
            .ToHashSet();

        var newSuggestions = new List<TraceabilitySuggestion>();
        int skipped = 0;

        foreach (var req in requirements)
        {
            foreach (var test in tests)
            {
                var pair = (SourceId: test.Id, TargetId: req.Id);

                if (confirmedPairs.Contains(pair) || activePairs.Contains(pair))
                {
                    skipped++;
                    continue;
                }

                if (rejectedPairs.Contains(pair))
                    continue;

                var (confidence, reason, signals) = ScorePair(req.Title, test.Title);

                if (confidence < MinimumConfidenceThreshold)
                    continue;

                var status = confidence < 0.50
                    ? TraceabilitySuggestionStatus.NeedsReview
                    : TraceabilitySuggestionStatus.Suggested;

                newSuggestions.Add(new TraceabilitySuggestion
                {
                    ProjectId = projectId,
                    SourceId = test.Id,
                    SourceKind = TraceLinkArtifactKind.Scenario,
                    TargetId = req.Id,
                    TargetKind = TraceLinkArtifactKind.Scenario,
                    LinkType = TraceLinkType.Covers,
                    Status = status,
                    Confidence = confidence,
                    Reason = reason,
                    SignalsJson = JsonSerializer.Serialize(signals),
                });
            }
        }

        if (newSuggestions.Count > 0)
        {
            _db.TraceabilitySuggestions.AddRange(newSuggestions);
            await _db.SaveChangesAsync(ct);
        }

        int highConfidence = newSuggestions.Count(s => s.Confidence >= HighConfidenceThreshold);
        int needsReview = newSuggestions.Count(s => s.Status == TraceabilitySuggestionStatus.NeedsReview);

        _logger?.LogInformation(
            "TraceabilitySuggestionsGenerated ProjectId={ProjectId} New={New} HighConf={High} NeedsReview={NeedsReview} Skipped={Skipped}",
            projectId, newSuggestions.Count, highConfidence, needsReview, skipped);

        return new SuggestionSummary
        {
            TotalGenerated = newSuggestions.Count,
            HighConfidenceCount = highConfidence,
            NeedsReviewCount = needsReview,
            SkippedAlreadyExists = skipped,
        };
    }

    /// <summary>Returns all non-rejected suggestions for the project, enriched with scenario titles.</summary>
    public async Task<IReadOnlyList<SuggestionItem>> GetSuggestionsAsync(
        string projectId,
        CancellationToken ct = default)
    {
        var suggestions = await _db.TraceabilitySuggestions
            .Where(s => s.ProjectId == projectId && s.Status != TraceabilitySuggestionStatus.Rejected)
            .OrderByDescending(s => s.Confidence)
            .ThenBy(s => s.CreatedAt)
            .ToListAsync(ct);

        if (suggestions.Count == 0)
            return [];

        var ids = suggestions
            .SelectMany(s => new[] { s.SourceId, s.TargetId })
            .Distinct()
            .ToList();

        var scenarioTitles = await _db.Scenarios
            .Where(s => ids.Contains(s.Id) && s.ProjectId == projectId)
            .Select(s => new { s.Id, s.Title })
            .ToDictionaryAsync(s => s.Id, s => s.Title, ct);

        return suggestions.Select(s => new SuggestionItem
        {
            Suggestion = s,
            SourceTitle = scenarioTitles.TryGetValue(s.SourceId, out var st) ? st : s.SourceId.ToString(),
            TargetTitle = scenarioTitles.TryGetValue(s.TargetId, out var tt) ? tt : s.TargetId.ToString(),
        }).ToList();
    }

    /// <summary>Returns pending suggestion counts for the project (for dashboard/library banners).</summary>
    public async Task<(int Total, int HighConfidence)> GetPendingCountsAsync(
        string projectId,
        CancellationToken ct = default)
    {
        var counts = await _db.TraceabilitySuggestions
            .Where(s => s.ProjectId == projectId && s.Status == TraceabilitySuggestionStatus.Suggested)
            .GroupBy(s => s.Confidence >= HighConfidenceThreshold)
            .Select(g => new { IsHigh = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        int high = counts.FirstOrDefault(c => c.IsHigh)?.Count ?? 0;
        int total = counts.Sum(c => c.Count);
        return (total, high);
    }

    /// <summary>Confirms a suggestion by creating a TraceLink and marking it confirmed.</summary>
    public async Task<ConfirmSuggestionResult> ConfirmAsync(
        Guid suggestionId,
        string projectId,
        string correlationId,
        CancellationToken ct = default)
    {
        var suggestion = await _db.TraceabilitySuggestions
            .FirstOrDefaultAsync(s => s.Id == suggestionId && s.ProjectId == projectId, ct);

        if (suggestion is null)
            return new ConfirmSuggestionResult
            {
                Errors = [new UserError("NOT_FOUND", "Suggestion not found.")]
            };

        if (suggestion.Status == TraceabilitySuggestionStatus.Confirmed)
        {
            var existing = await _db.TraceLinks
                .FirstOrDefaultAsync(t => t.SourceId == suggestion.SourceId
                                       && t.TargetId == suggestion.TargetId
                                       && t.ProjectId == projectId, ct);
            if (existing is not null)
                return new ConfirmSuggestionResult { TraceLink = existing };
        }

        var duplicate = await _db.TraceLinks.AnyAsync(
            t => t.ProjectId == projectId
              && t.SourceId == suggestion.SourceId
              && t.TargetId == suggestion.TargetId
              && t.LinkType == suggestion.LinkType, ct);

        if (duplicate)
        {
            suggestion.Status = TraceabilitySuggestionStatus.Confirmed;
            suggestion.ConfirmedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
            return new ConfirmSuggestionResult
            {
                Errors = [new UserError("DUPLICATE_LINK", "A trace link for this pair already exists.")]
            };
        }

        var link = new TraceLink
        {
            ProjectId = projectId,
            SourceId = suggestion.SourceId,
            SourceKind = suggestion.SourceKind,
            TargetId = suggestion.TargetId,
            TargetKind = suggestion.TargetKind,
            LinkType = suggestion.LinkType,
            CreatedBy = "suggestion",
            Notes = $"Confirmed from suggestion (confidence: {suggestion.Confidence:P0})",
        };

        _db.TraceLinks.Add(link);
        suggestion.Status = TraceabilitySuggestionStatus.Confirmed;
        suggestion.ConfirmedAt = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync(ct);

        _logger?.LogInformation(
            "SuggestionConfirmed CorrelationId={CorrelationId} SuggestionId={SuggestionId} TraceLinkId={TraceLinkId}",
            correlationId, suggestionId, link.Id);

        return new ConfirmSuggestionResult { TraceLink = link };
    }

    /// <summary>Rejects a suggestion. Rejected suggestions are not regenerated.</summary>
    public async Task<RejectSuggestionResult> RejectAsync(
        Guid suggestionId,
        string projectId,
        string correlationId,
        CancellationToken ct = default)
    {
        var suggestion = await _db.TraceabilitySuggestions
            .FirstOrDefaultAsync(s => s.Id == suggestionId && s.ProjectId == projectId, ct);

        if (suggestion is null)
            return new RejectSuggestionResult
            {
                Errors = [new UserError("NOT_FOUND", "Suggestion not found.")]
            };

        suggestion.Status = TraceabilitySuggestionStatus.Rejected;
        suggestion.RejectedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        _logger?.LogInformation(
            "SuggestionRejected CorrelationId={CorrelationId} SuggestionId={SuggestionId}",
            correlationId, suggestionId);

        return new RejectSuggestionResult { IsSuccess = true };
    }

    /// <summary>Confirms all high-confidence pending suggestions in bulk.</summary>
    public async Task<int> ConfirmHighConfidenceAsync(
        string projectId,
        string correlationId,
        CancellationToken ct = default)
    {
        var highConf = await _db.TraceabilitySuggestions
            .Where(s => s.ProjectId == projectId
                     && s.Status == TraceabilitySuggestionStatus.Suggested
                     && s.Confidence >= HighConfidenceThreshold)
            .ToListAsync(ct);

        int confirmed = 0;
        foreach (var s in highConf)
        {
            var result = await ConfirmAsync(s.Id, projectId, correlationId, ct);
            if (result.IsSuccess) confirmed++;
        }

        _logger?.LogInformation(
            "BulkHighConfidenceConfirmed CorrelationId={CorrelationId} ProjectId={ProjectId} Confirmed={Confirmed}",
            correlationId, projectId, confirmed);

        return confirmed;
    }

    /// <summary>Returns how many requirements are suggested-covered but not confirmed-covered.</summary>
    public async Task<int> GetSuggestedCoverageCountAsync(string projectId, CancellationToken ct = default)
    {
        var confirmedTargets = await _db.TraceLinks
            .Where(t => t.ProjectId == projectId
                     && t.TargetKind == TraceLinkArtifactKind.Scenario
                     && t.LinkType == TraceLinkType.Covers)
            .Select(t => t.TargetId)
            .ToListAsync(ct);

        var confirmedSet = confirmedTargets.ToHashSet();

        var suggestedTargets = await _db.TraceabilitySuggestions
            .Where(s => s.ProjectId == projectId
                     && s.TargetKind == TraceLinkArtifactKind.Scenario
                     && s.Status == TraceabilitySuggestionStatus.Suggested
                     && s.LinkType == TraceLinkType.Covers)
            .Select(s => s.TargetId)
            .Distinct()
            .ToListAsync(ct);

        return suggestedTargets.Count(id => !confirmedSet.Contains(id));
    }

    // ── Scoring engine ────────────────────────────────────────────────────────

    private static (double Confidence, string Reason, List<string> Signals) ScorePair(
        string requirementTitle,
        string testTitle)
    {
        var signals = new List<string>();
        double score = 0.0;

        var reqIds = ExtractIds(requirementTitle);
        var testIds = ExtractIds(testTitle);

        // FR/SC matches are the strongest signal
        var reqFr = reqIds.Where(x => x.Prefix is "FR").Select(x => x.Normalized).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var testFr = testIds.Where(x => x.Prefix is "FR").Select(x => x.Normalized).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sharedFr = reqFr.Intersect(testFr).ToList();
        if (sharedFr.Count > 0)
        {
            score += 0.50 + Math.Min(sharedFr.Count * 0.05, 0.15);
            signals.Add($"Shared requirement IDs: {string.Join(", ", sharedFr)}");
        }

        var reqSc = reqIds.Where(x => x.Prefix is "SC").Select(x => x.Normalized).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var testSc = testIds.Where(x => x.Prefix is "SC").Select(x => x.Normalized).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sharedSc = reqSc.Intersect(testSc).ToList();
        if (sharedSc.Count > 0)
        {
            score += 0.40 + Math.Min(sharedSc.Count * 0.05, 0.10);
            signals.Add($"Shared success criterion IDs: {string.Join(", ", sharedSc)}");
        }

        // User story match
        var reqUs = reqIds.Where(x => x.Prefix is "US").Select(x => x.Normalized).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var testUs = testIds.Where(x => x.Prefix is "US").Select(x => x.Normalized).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sharedUs = reqUs.Intersect(testUs).ToList();
        if (sharedUs.Count > 0)
        {
            score += 0.25;
            signals.Add($"Same user story: {string.Join(", ", sharedUs)}");
        }

        // Task/test ID cross-reference
        var reqTasks = reqIds.Where(x => x.Prefix is "T" or "TC" or "TASK" or "TEST").Select(x => x.Normalized).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var testTasks = testIds.Where(x => x.Prefix is "T" or "TC" or "TASK" or "TEST").Select(x => x.Normalized).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sharedTasks = reqTasks.Intersect(testTasks).ToList();
        if (sharedTasks.Count > 0)
        {
            score += 0.35;
            signals.Add($"Shared task/test IDs: {string.Join(", ", sharedTasks)}");
        }

        // Meaningful word overlap
        var reqWords = ExtractMeaningfulWords(requirementTitle);
        var testWords = ExtractMeaningfulWords(testTitle);
        var sharedWords = reqWords.Intersect(testWords).ToList();
        if (sharedWords.Count >= 2)
        {
            double wordScore = Math.Min(sharedWords.Count * 0.04, 0.20);
            score += wordScore;
            var shown = sharedWords.Take(5).ToList();
            signals.Add($"Shared terms: {string.Join(", ", shown)}");
        }

        score = Math.Round(Math.Min(score, 1.0), 2);

        string reason = signals.Count > 0
            ? string.Join(". ", signals) + "."
            : "No strong matching signals found.";

        return (score, reason, signals);
    }

    private static List<(string Prefix, string Number, string Normalized)> ExtractIds(string text)
    {
        var results = new List<(string, string, string)>();
        foreach (Match m in IdPattern.Matches(text))
        {
            var prefix = m.Groups[1].Value.ToUpperInvariant();
            var number = m.Groups[2].Value.TrimStart('0').PadLeft(1, '0');
            results.Add((prefix, number, $"{prefix}-{number}"));
        }
        return results;
    }

    private static HashSet<string> ExtractMeaningfulWords(string text)
    {
        var words = Regex.Split(text.ToLowerInvariant(), @"[^a-z0-9]+")
            .Where(w => w.Length >= 4 && !StopWords.Contains(w))
            .ToHashSet();
        return words;
    }
}
