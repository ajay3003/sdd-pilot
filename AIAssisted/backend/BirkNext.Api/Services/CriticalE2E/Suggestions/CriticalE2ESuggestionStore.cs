using System.Text.Json;
using System.Text.Json.Serialization;
using BirkNext.CriticalE2E;

namespace BirkNext.Api.Services.CriticalE2E.Suggestions;

/// <summary>What a person decided and what the attended page showed for one suggestion. Suggestions themselves are regenerated (deterministic
/// ids); only this review state persists. Verification evidence is identity only (match counts, roles, names) — never a DOM snapshot.</summary>
public sealed record SuggestionReviewRecord
{
    public string SuggestionId { get; init; } = "";
    public Guid SnapshotId { get; init; }
    public List<StepVerification> Verifications { get; init; } = [];
    /// <summary>Every decision ever made (history kept); the latest per step applies.</summary>
    public List<StepDecision> Decisions { get; init; } = [];
    public string? ApprovedFlowId { get; init; }
    public int? ApprovedFlowVersion { get; init; }
    public DateTimeOffset? ApprovedAt { get; init; }
    public string? ApprovedBy { get; init; }
    public string? ReviewerExpected { get; init; }
    /// <summary>The manual tester's review (expected result, Test Data, notes, decision). Human content: kept across snapshots, unlike locator verification.</summary>
    public SuggestionManualReview? ManualReview { get; init; }
}

public interface ICriticalE2ESuggestionStore
{
    SuggestionReviewRecord? Get(string suggestionId);
    IReadOnlyList<SuggestionReviewRecord> All();
    SuggestionReviewRecord Update(string suggestionId, Guid snapshotId, Func<SuggestionReviewRecord, SuggestionReviewRecord> change);
}

/// <summary>File-backed like the Critical E2E store (<c>App_Data/critical-e2e/suggestion-reviews.json</c>); bounded to the newest 2000 records.</summary>
public sealed class CriticalE2ESuggestionStore : ICriticalE2ESuggestionStore
{
    public const int MaxRecords = 2000;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    private readonly object _gate = new();
    private readonly string _path;
    private readonly Dictionary<string, SuggestionReviewRecord> _records = new(StringComparer.Ordinal);

    public CriticalE2ESuggestionStore(IHostEnvironment environment) : this(System.IO.Path.Combine(BirkNext.Api.Services.AppDataLocation.Root(environment), "critical-e2e")) { }

    public CriticalE2ESuggestionStore(string directory)
    {
        Directory.CreateDirectory(directory);
        _path = System.IO.Path.Combine(directory, "suggestion-reviews.json");
        if (File.Exists(_path))
            try { foreach (var r in JsonSerializer.Deserialize<List<SuggestionReviewRecord>>(File.ReadAllText(_path), Json) ?? []) _records[r.SuggestionId] = r; }
            catch (JsonException) { /* a corrupt file is never trusted; it is replaced on the next write */ }
    }

    public SuggestionReviewRecord? Get(string suggestionId) { lock (_gate) return _records.GetValueOrDefault(suggestionId); }

    public IReadOnlyList<SuggestionReviewRecord> All() { lock (_gate) return [.. _records.Values]; }

    public SuggestionReviewRecord Update(string suggestionId, Guid snapshotId, Func<SuggestionReviewRecord, SuggestionReviewRecord> change)
    {
        lock (_gate)
        {
            var current = _records.GetValueOrDefault(suggestionId) ?? new SuggestionReviewRecord { SuggestionId = suggestionId, SnapshotId = snapshotId };
            var next = change(current) with { SuggestionId = suggestionId };
            _records[suggestionId] = next;
            if (_records.Count > MaxRecords)
                foreach (var stale in _records.Values.Where(r => r.ApprovedFlowId is null).OrderBy(r => r.Decisions.LastOrDefault()?.DecidedAt ?? DateTimeOffset.MinValue).Take(_records.Count - MaxRecords).ToList())
                    _records.Remove(stale.SuggestionId);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_records.Values.ToList(), Json));
            File.Move(temp, _path, overwrite: true);
            return next;
        }
    }
}
