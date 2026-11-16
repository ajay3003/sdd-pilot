namespace BirkNext.TechnicalHistory;

// ── Stored API Quality Review runs (server-side history; the browser keeps only its working copy) ─────────────────────────────────────────

/// <summary>One finished API Quality Review run as recorded: who/where and the counts; the full report is fetched by id. Append-only.</summary>
public sealed record StoredApiReviewRun(Guid Id, string? ProjectKey, string EnvironmentId, string EnvironmentName, DateTimeOffset StartedAt, DateTimeOffset GeneratedAt,
    int Targets, int Completed, int Blocked, int High, int Medium, int Low, int Info, DateTimeOffset RecordedAt);

public sealed record StoreApiReviewRunRequest(string? ProjectKey, string EnvironmentId, BirkNext.ApiReview.ApiReviewReport Report);

// ── Technical finding follow-up (defect lifecycle for findings of stored technical runs) ────────────────────────────────────────────────

/// <summary>
/// New = recorded, not looked at; UnderReview = being assessed; Confirmed = a real problem (a defect); Deferred = accepted for now;
/// ResolvedPendingRetest = reported fixed, waiting for a retest; VerifiedResolved = a linked later run no longer shows it; Reopened = it came back.
/// </summary>
public enum TechnicalFindingStatus { New, UnderReview, Confirmed, Deferred, ResolvedPendingRetest, VerifiedResolved, Reopened }

/// <param name="RetestRunId">The later stored run used as the retest (its outcome is recorded with the event, never re-evaluated).</param>
/// <param name="RetestOutcome">"Absent" (not found in the retest run) or "Still present".</param>
public sealed record TechnicalFindingEvent(DateTimeOffset At, string By, TechnicalFindingStatus From, TechnicalFindingStatus To, string? Note,
    Guid? RetestRunId = null, string? RetestOutcome = null, string? ExternalReference = null);

/// <summary>
/// A finding of a stored technical run that a person follows up. Identity = project + source + finding key (rule, target and endpoint), so
/// the same issue in a later run is the same follow-up. The original run and its report are never changed; retests are separate runs.
/// </summary>
public sealed record TechnicalFinding
{
    public Guid Id { get; init; }
    public string ProjectKey { get; init; } = "";
    /// <summary>The module whose run produced it ("api-quality-review").</summary>
    public string Source { get; init; } = "";
    public string FindingKey { get; init; } = "";
    public string Title { get; init; } = "";
    public string Severity { get; init; } = "";
    public string Endpoint { get; init; } = "";
    public string EnvironmentId { get; init; } = "";
    public Guid OriginalRunId { get; init; }
    public DateTimeOffset FirstSeenAt { get; init; }
    public TechnicalFindingStatus Status { get; init; }
    public string? ExternalReference { get; init; }
    public Guid? LastRetestRunId { get; init; }
    public int Version { get; init; }
    public List<TechnicalFindingEvent> History { get; init; } = [];
}

public sealed record TrackTechnicalFindingRequest(string ProjectKey, Guid RunId, string FindingId, string? By);

public sealed record UpdateTechnicalFindingRequest(int Version, TechnicalFindingStatus Status, string? Note, string? ExternalReference, Guid? RetestRunId, string? By);

public static class TechnicalFindingRules
{
    public const string ApiQualityReview = "api-quality-review";

    /// <summary>The same issue in any run: rule (or finding id for older reports), target and endpoint.</summary>
    public static string Key(BirkNext.ApiReview.ApiReviewFinding f) =>
        $"{(string.IsNullOrWhiteSpace(f.RuleId) ? f.Id : f.RuleId)}|{f.TargetId}|{f.Endpoint}";

    public static bool CanMove(TechnicalFindingStatus from, TechnicalFindingStatus to) => (from, to) switch
    {
        (TechnicalFindingStatus.New, TechnicalFindingStatus.UnderReview or TechnicalFindingStatus.Confirmed or TechnicalFindingStatus.Deferred) => true,
        (TechnicalFindingStatus.UnderReview, TechnicalFindingStatus.Confirmed or TechnicalFindingStatus.Deferred) => true,
        (TechnicalFindingStatus.Confirmed, TechnicalFindingStatus.ResolvedPendingRetest or TechnicalFindingStatus.Deferred) => true,
        (TechnicalFindingStatus.Deferred, TechnicalFindingStatus.Confirmed or TechnicalFindingStatus.UnderReview) => true,
        (TechnicalFindingStatus.ResolvedPendingRetest, TechnicalFindingStatus.VerifiedResolved or TechnicalFindingStatus.Reopened) => true,
        (TechnicalFindingStatus.VerifiedResolved, TechnicalFindingStatus.Reopened) => true,
        (TechnicalFindingStatus.Reopened, TechnicalFindingStatus.Confirmed or TechnicalFindingStatus.ResolvedPendingRetest) => true,
        _ => false,
    };

    public static string Label(TechnicalFindingStatus s) => s switch
    {
        TechnicalFindingStatus.New => "New", TechnicalFindingStatus.UnderReview => "Under review", TechnicalFindingStatus.Confirmed => "Confirmed",
        TechnicalFindingStatus.Deferred => "Deferred", TechnicalFindingStatus.ResolvedPendingRetest => "Resolved — retest pending",
        TechnicalFindingStatus.VerifiedResolved => "Verified resolved", _ => "Reopened",
    };
}
