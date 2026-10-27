using System.Text.Json;
using System.Text.Json.Serialization;
using BirkNext.Web.Models;
using Microsoft.JSInterop;

namespace BirkNext.Web.Services;

/// <summary>One completed Frontend Quality Review run, kept with its immutable report.</summary>
public sealed record FrontendQualityReviewHistoryEntry
{
    [JsonPropertyName("id")] public string Id { get; init; } = Guid.NewGuid().ToString("N");
    [JsonPropertyName("profileId")] public string ProfileId { get; init; } = "";
    [JsonPropertyName("completedAt")] public DateTimeOffset CompletedAt { get; init; }
    [JsonPropertyName("scope")] public FrontendReviewAccessScope? Scope { get; init; }
    [JsonPropertyName("disposition")] public FrontendQualityReleaseDisposition? Disposition { get; init; }
    [JsonPropertyName("issueCount")] public int IssueCount { get; init; }
    [JsonPropertyName("requiredAssessed")] public int RequiredAssessed { get; init; }
    [JsonPropertyName("requiredTotal")] public int RequiredTotal { get; init; }
    [JsonPropertyName("report")] public FrontendQualityReviewReport Report { get; init; } = new();
}

/// <summary>
/// Previous Frontend Quality Review runs per Target Environment, newest first and bounded. A run is stored exactly as it completed;
/// opening it shows that immutable result, never a recomputation against current settings. Persisted in this browser only (the same
/// local storage as other review state), never on the backend; reports carry sanitized evidence only.
/// </summary>
public sealed class FrontendQualityReviewHistory(IJSRuntime js)
{
    public const int MaxRuns = 10;
    private const string KeyPrefix = "birknext:fqr-history:";
    private const int MaxStoredCharacters = 1_500_000;
    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private readonly Dictionary<string, List<FrontendQualityReviewHistoryEntry>> _runs = new(StringComparer.Ordinal);

    public IReadOnlyList<FrontendQualityReviewHistoryEntry> For(string profileId) =>
        _runs.TryGetValue(profileId, out var runs) ? runs : [];

    public async Task LoadAsync(string profileId)
    {
        if (_runs.ContainsKey(profileId)) return;
        try
        {
            var text = await js.InvokeAsync<string?>("birkNextStorage.getItem", KeyPrefix + profileId);
            _runs[profileId] = string.IsNullOrWhiteSpace(text) ? [] : JsonSerializer.Deserialize<List<FrontendQualityReviewHistoryEntry>>(text, Json) ?? [];
        }
        catch (Exception e) when (e is JSException or JsonException or InvalidOperationException) { _runs[profileId] = []; }
    }

    /// <summary>Records a completed run (newest first, at most <see cref="MaxRuns"/>). Returns the stored entry.</summary>
    public async Task<FrontendQualityReviewHistoryEntry> AddAsync(string profileId, FrontendQualityReviewReport report)
    {
        await LoadAsync(profileId);
        var entry = Entry(profileId, report);
        var runs = _runs[profileId];
        runs.Insert(0, entry);
        if (runs.Count > MaxRuns) runs.RemoveRange(MaxRuns, runs.Count - MaxRuns);
        await PersistAsync(profileId, runs);
        return entry;
    }

    public static FrontendQualityReviewHistoryEntry Entry(string profileId, FrontendQualityReviewReport report) => new()
    {
        ProfileId = profileId,
        CompletedAt = report.CompletedAt is { } completed ? new DateTimeOffset(DateTime.SpecifyKind(completed, DateTimeKind.Utc)) : DateTimeOffset.UtcNow,
        // The same scope order and logical-issue count as the post-run projection (FrontendQualityRunSemantics), so a history row and the
        // result page never disagree. The full report is stored, so assessed surfaces and authenticated evidence are re-projected on open.
        Scope = report.ExecutionPlan?.Scope ?? report.TargetAccess?.ReviewScope,
        Disposition = report.ReleaseDisposition,
        IssueCount = FrontendQualityResultPresentation.Build(report).ActionableIssues.Count,
        RequiredAssessed = report.Coverage?.RequiredAssessed ?? 0,
        RequiredTotal = report.Coverage?.RequiredTotal ?? 0,
        Report = report,
    };

    private async Task PersistAsync(string profileId, List<FrontendQualityReviewHistoryEntry> runs)
    {
        try
        {
            // Oldest runs are dropped until the history fits the browser's storage budget; the newest run is always kept in memory.
            var kept = runs.ToList();
            var text = JsonSerializer.Serialize(kept, Json);
            while (text.Length > MaxStoredCharacters && kept.Count > 1) { kept.RemoveAt(kept.Count - 1); text = JsonSerializer.Serialize(kept, Json); }
            await js.InvokeVoidAsync("birkNextStorage.setItem", KeyPrefix + profileId, text);
        }
        catch (Exception e) when (e is JSException or InvalidOperationException) { }
    }

    /// <summary>Forgets the in-memory runs; Local Data Reset removes the stored keys by prefix.</summary>
    public void ResetForLocalDataReset() => _runs.Clear();

    public static string DispositionLabel(FrontendQualityReleaseDisposition? disposition) => disposition switch
    {
        FrontendQualityReleaseDisposition.Blocked => "Blocked",
        FrontendQualityReleaseDisposition.NoAutomatedBlockDetected => "No automated block detected",
        FrontendQualityReleaseDisposition.ReviewRequired => "Review required",
        _ => "Not recorded",
    };
}
