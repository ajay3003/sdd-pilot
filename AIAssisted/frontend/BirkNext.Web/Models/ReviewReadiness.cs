using System.Diagnostics;

namespace BirkNext.Web.Models;

/// <summary>
/// The one "Can I review now?" answer every review (API, Frontend, Integration) gives before it runs. Assessing is not a verdict: nothing is
/// Blocked until the required evidence was evaluated and a required prerequisite is definitively unsatisfied. Optional evidence that is
/// unavailable is a limitation, never a blocker; an evidence query that failed is "Failed to assess", never "Blocked".
/// </summary>
public enum ReviewDecision { Assessing, Ready, ReadyWithLimitations, NeedsReview, Blocked, FailedToAssess }

public enum ReviewStageState { Pending, Running, Done, Failed, Skipped }

/// <summary>Evidence age: Recent (≤ 24 h), Historical (≤ 30 days), Stale (older), Unknown (no timestamp). Affects confidence, never eligibility by itself.</summary>
public enum EvidenceFreshness { Unknown, Recent, Historical, Stale }

public sealed record ReviewStage(string Key, string Label, ReviewStageState State, TimeSpan? Duration = null, string? Detail = null);

/// <summary>
/// One coherent evaluated readiness result: the decision, its limitations and blockers (never mixed), freshness, the stages that produced it and
/// when. Pages render from one snapshot so asynchronous dependencies can never show a contradictory intermediate state.
/// </summary>
public sealed record ReviewReadinessSnapshot<TEvidence>(
    ReviewDecision Decision,
    string Title,
    TEvidence? Evidence,
    IReadOnlyList<string> Limitations,
    IReadOnlyList<string> Blockers,
    EvidenceFreshness Freshness,
    IReadOnlyList<ReviewStage> Stages,
    DateTimeOffset? EvaluatedAt,
    int Generation,
    string? Error = null)
{
    public bool CanRun => Decision is ReviewDecision.Ready or ReviewDecision.ReadyWithLimitations;
}

public static class ReviewDecisions
{
    public const string AssessingTitle = "Assessing readiness…";
    public const string FailedTitle = "Readiness could not be fully assessed";

    public static string Label(ReviewDecision d) => d switch
    {
        ReviewDecision.Assessing => "Assessing",
        ReviewDecision.Ready => "Ready",
        ReviewDecision.ReadyWithLimitations => "Ready with limitations",
        ReviewDecision.NeedsReview => "Needs review",
        ReviewDecision.Blocked => "Blocked",
        _ => "Failed to assess",
    };

    /// <summary>Colour semantics: green ready, green/neutral limited, amber needs review, grey assessing, red only for a genuine blocker/failure.</summary>
    public static string Tone(ReviewDecision d) => d switch
    {
        ReviewDecision.Ready => "ready",
        ReviewDecision.ReadyWithLimitations => "limited",
        ReviewDecision.NeedsReview => "review",
        ReviewDecision.Blocked or ReviewDecision.FailedToAssess => "blocked",
        _ => "assessing",
    };

    public static readonly TimeSpan RecentWindow = TimeSpan.FromHours(24), HistoricalWindow = TimeSpan.FromDays(30);

    public static EvidenceFreshness FreshnessOf(DateTimeOffset? lastObserved, DateTimeOffset now) => lastObserved is not { } at || at == default ? EvidenceFreshness.Unknown
        : now - at <= RecentWindow ? EvidenceFreshness.Recent : now - at <= HistoricalWindow ? EvidenceFreshness.Historical : EvidenceFreshness.Stale;

    public static string FreshnessLabel(EvidenceFreshness f) => f switch
    {
        EvidenceFreshness.Recent => "Recent",
        EvidenceFreshness.Historical => "Historical",
        EvidenceFreshness.Stale => "Stale",
        _ => "Unknown",
    };
}

/// <summary>
/// One readiness evaluation in progress: a generation number (a newer assessment makes older results irrelevant), a cancellation token
/// (navigation or a target change cancels the old one), and named stages with safe timings (no payloads, only durations) so slow
/// dependencies are visible. Results of an outdated generation are discarded by the caller via <see cref="IsCurrent"/>.
/// </summary>
public sealed class ReviewAssessment : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly List<ReviewStage> _stages;
    private readonly Stopwatch _total = Stopwatch.StartNew();

    public ReviewAssessment(int generation, IEnumerable<(string Key, string Label)> stages)
    {
        Generation = generation;
        _stages = stages.Select(s => new ReviewStage(s.Key, s.Label, ReviewStageState.Pending)).ToList();
    }

    public int Generation { get; }
    public CancellationToken Token => _cts.Token;
    public IReadOnlyList<ReviewStage> Stages => _stages;
    public TimeSpan Elapsed => _total.Elapsed;
    public bool Cancelled => _cts.IsCancellationRequested;

    /// <summary>The stage currently running, for "Still assessing readiness… Waiting for: …".</summary>
    public ReviewStage? Waiting => _stages.FirstOrDefault(s => s.State == ReviewStageState.Running);

    public void Cancel() { if (!_cts.IsCancellationRequested) _cts.Cancel(); }

    public bool IsCurrent(int latestGeneration) => !Cancelled && Generation == latestGeneration;

    /// <summary>Runs one stage, recording Running → Done/Failed with its duration. A failure is recorded and rethrown to the caller's policy.</summary>
    public async Task<T> StageAsync<T>(string key, Func<CancellationToken, Task<T>> work, Action? changed = null, Func<T, string?>? detail = null)
    {
        var watch = Stopwatch.StartNew();
        Set(key, ReviewStageState.Running, null, null);
        changed?.Invoke();
        try
        {
            var result = await work(Token);
            Set(key, ReviewStageState.Done, watch.Elapsed, detail?.Invoke(result));
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !Cancelled)
        {
            Set(key, ReviewStageState.Failed, watch.Elapsed, "could not be assessed");
            throw;
        }
        finally { changed?.Invoke(); }
    }

    public void Skip(string key, string? detail = null) => Set(key, ReviewStageState.Skipped, null, detail);

    private void Set(string key, ReviewStageState state, TimeSpan? duration, string? detail)
    {
        var i = _stages.FindIndex(s => s.Key == key);
        if (i >= 0) _stages[i] = _stages[i] with { State = state, Duration = duration ?? _stages[i].Duration, Detail = detail ?? _stages[i].Detail };
    }

    public void Dispose() { Cancel(); _cts.Dispose(); }
}
