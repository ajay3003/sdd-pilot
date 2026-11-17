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
    /// <summary>What a retest must match to be comparable: the original target's contract version (API Quality Review) or the stored test revisions
    /// of the stage's steps (integration suite stage). A retest against another definition is "Not comparable" and cannot verify a fix.</summary>
    public string? ComparisonBasis { get; init; }
    /// <summary>Integration suite stage findings: the suite and stage (null for API Quality Review findings).</summary>
    public Guid? SuiteId { get; init; }
    public string? StageId { get; init; }
    public int Version { get; init; }
    public List<TechnicalFindingEvent> History { get; init; } = [];
}

public sealed record TrackTechnicalFindingRequest(string ProjectKey, Guid RunId, string FindingId, string? By);

/// <summary>Follow up a failed stage of a composite integration suite run.</summary>
public sealed record TrackStageFindingRequest(string ProjectKey, Guid SuiteId, Guid RunId, string StageId, string? By);

public sealed record UpdateTechnicalFindingRequest(int Version, TechnicalFindingStatus Status, string? Note, string? ExternalReference, Guid? RetestRunId, string? By);

public static class TechnicalFindingRules
{
    public const string ApiQualityReview = "api-quality-review";
    public const string IntegrationSuiteStage = "integration-suite-stage";

    /// <summary>Retest outcomes, recorded with the event and never re-evaluated. Only <see cref="Absent"/> can verify a fix.</summary>
    public const string Absent = "Absent";
    public const string StillPresent = "Still present";
    public const string NotReviewed = "Not reviewed";
    public const string NotComparable = "Not comparable";
    public const string Inconclusive = "Inconclusive";

    /// <summary>The same failing stage in any run of the suite: suite key and stage.</summary>
    public static string StageKey(string suiteKey, string stageId) => $"{suiteKey}|{stageId}";

    /// <summary>Why a retest outcome cannot verify a fix (null for <see cref="Absent"/>).</summary>
    public static string? WhyNotVerified(string? outcome) => outcome switch
    {
        Absent => null,
        null => "Link the retest run that no longer shows the finding.",
        NotReviewed => "The retest run did not review the finding's target, so it cannot verify the fix.",
        NotComparable => "Review needed: the retest ran another definition (contract or test revisions changed), so it is not comparable with the original run.",
        Inconclusive => "Review needed: the retest did not reach a conclusive result for this finding.",
        _ => "The retest run still shows the finding; reopen it instead.",
    };

    /// <summary>The same issue in any run: rule (or finding id for older reports), target and endpoint.</summary>
    public static string Key(BirkNext.ApiReview.ApiReviewFinding f) =>
        $"{(string.IsNullOrWhiteSpace(f.RuleId) ? f.Id : f.RuleId)}|{f.TargetId}|{f.Endpoint}";

    /// <summary>Allowed status changes. Staying in the same status is allowed only to record a retest observation (see the service).</summary>
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

/// <summary>Specialist technical reviews stored on the server (previously only in the browser): Frontend Quality Review, safe fuzzing and
/// authorization checks. The report is kept as produced (opaque JSON); the summary fields are what lists show.</summary>
public static class TechnicalReviewKinds
{
    public const string FrontendQuality = "frontend-quality-review";
    public const string SafeFuzzing = "api-safe-fuzzing";
    public const string Authorization = "api-authorization-checks";
    public static readonly IReadOnlyList<string> All = [FrontendQuality, SafeFuzzing, Authorization];

    public static string Label(string kind) => kind switch
    {
        FrontendQuality => "Frontend Quality Review", SafeFuzzing => "Safe fuzzing", Authorization => "Authorization checks", _ => kind,
    };
}

public sealed record StoreTechnicalReviewRunRequest(string Kind, string? ProjectKey, string EnvironmentId, string? EnvironmentName, string? TargetUrl, Guid? SourceSnapshotId,
    DateTimeOffset GeneratedAt, string Outcome, int Checks, int Problems, string? Summary, System.Text.Json.JsonElement Report);

public sealed record StoredTechnicalReviewRun(Guid Id, string Kind, string? ProjectKey, string EnvironmentId, string EnvironmentName, string? TargetUrl, Guid? SourceSnapshotId,
    DateTimeOffset GeneratedAt, string Outcome, int Checks, int Problems, string Summary, string ReportSha256, DateTimeOffset RecordedAt);
