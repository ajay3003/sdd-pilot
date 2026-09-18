using BirkNext.ApiReview;

namespace BirkNext.Web.Models;

/// <summary>
/// Presentation records for the API Quality Review page. Projections of authoritative state only (<see cref="ApiReviewTarget"/>,
/// the run-eligibility rule, capabilities, <see cref="ApiReviewReport"/>), built by <c>ApiReviewPresentation</c>.
/// No second status taxonomy: every label maps 1:1 from a typed backend/contract state.
/// </summary>
public enum ApiReviewAccessAvailability
{
    Loading,
    /// <summary>Authenticated API context is available (Local HTTPS proxy, memory-only credential used by the backend gateway).</summary>
    Available,
    /// <summary>Proxy method selected, but no authenticated API context has been captured yet.</summary>
    NotConnected,
    /// <summary>A context existed and expired.</summary>
    Expired,
    /// <summary>The environment is manual-verification only; no automated authenticated API review exists.</summary>
    ManualOnly,
    /// <summary>The selected authenticated testing method (Managed Edge CDP) provides no authenticated API execution.</summary>
    NotSupportedByMethod,
}

public static class ApiReviewAccessAvailabilities
{
    public static string Label(ApiReviewAccessAvailability availability) => availability switch
    {
        ApiReviewAccessAvailability.Loading => "Resolving…",
        ApiReviewAccessAvailability.Available => "Available",
        ApiReviewAccessAvailability.NotConnected => "Not connected",
        ApiReviewAccessAvailability.Expired => "Session expired",
        ApiReviewAccessAvailability.ManualOnly => "Manual verification only",
        ApiReviewAccessAvailability.NotSupportedByMethod => "Not supported by the selected method",
        _ => availability.ToString(),
    };

    public static string Tone(ApiReviewAccessAvailability availability) => availability switch
    {
        ApiReviewAccessAvailability.Available => "ready",
        ApiReviewAccessAvailability.Loading => "pending",
        _ => "attention",
    };
}

public sealed record ApiReviewTechnicalField(string Label, string Value);

public sealed record ApiReviewTargetSummaryModel(
    string Environment,
    string EnvironmentType,
    string FrontendUrl,
    string Authentication,
    /// <summary>"Public only" / "Authenticated available" / "Authenticated unavailable" — what the review can reach right now.</summary>
    string ApiAccess,
    ApiReviewAccessAvailability AccessAvailability,
    IReadOnlyList<ApiReviewTechnicalField> Technical);

public sealed record ApiReviewAccessPanelModel(
    ApiReviewAccessAvailability Availability,
    /// <summary>Selected targets include at least one that requires authentication.</summary>
    bool AuthenticationNeeded,
    string Summary,
    IReadOnlyList<string> Steps,
    string? ActionText,
    string ActionHref,
    IReadOnlyList<ApiReviewTechnicalField> Technical);

public enum ApiReviewReadinessLevel { Loading, Blocked, Limited, Ready }

public enum ApiReviewReadinessItemState { Ok, Warning, Missing }

public sealed record ApiReviewReadinessItem(string Label, ApiReviewReadinessItemState State);

public sealed record ApiReviewReadinessModel(
    ApiReviewReadinessLevel Level,
    string Title,
    string Message,
    int SelectedCount,
    IReadOnlyList<ApiReviewReadinessItem> Items,
    string? ActionText,
    string? ActionHref)
{
    public bool CanRun => Level is ApiReviewReadinessLevel.Ready or ApiReviewReadinessLevel.Limited;
}

public static class ApiReviewReadinessItemStates
{
    public static string Glyph(ApiReviewReadinessItemState state) => state switch
    {
        ApiReviewReadinessItemState.Ok => "✓",
        ApiReviewReadinessItemState.Warning => "⚠",
        _ => "○",
    };
}

public enum ApiReviewContractState
{
    /// <summary>A published contract (OpenAPI URL) is configured.</summary>
    Available,
    /// <summary>No published contract is configured; live responses are reviewed structurally.</summary>
    NotConfigured,
    /// <summary>GraphQL schema retrieved (or to be retrieved) by runtime introspection.</summary>
    RuntimeSchema,
    /// <summary>Introspection was refused by the server (a policy observation, not a failure).</summary>
    IntrospectionUnavailable,
    /// <summary>No target of this protocol is selected.</summary>
    NotApplicable,
}

public static class ApiReviewContractStates
{
    public static string Label(ApiReviewContractState state) => state switch
    {
        ApiReviewContractState.Available => "Contract available",
        ApiReviewContractState.NotConfigured => "No contract configured",
        ApiReviewContractState.RuntimeSchema => "Runtime schema",
        ApiReviewContractState.IntrospectionUnavailable => "Introspection unavailable",
        ApiReviewContractState.NotApplicable => "No target",
        _ => state.ToString(),
    };

    public static string Glyph(ApiReviewContractState state) => state switch
    {
        ApiReviewContractState.Available or ApiReviewContractState.RuntimeSchema => "✓",
        ApiReviewContractState.IntrospectionUnavailable => "⚠",
        _ => "○",
    };

    public static string Tone(ApiReviewContractState state) => state switch
    {
        ApiReviewContractState.Available or ApiReviewContractState.RuntimeSchema => "ready",
        ApiReviewContractState.IntrospectionUnavailable => "attention",
        _ => "muted",
    };
}

public sealed record ApiReviewContractRow(string Label, ApiReviewContractState State, string Detail);

public sealed record ApiReviewContractPanelModel(
    IReadOnlyList<ApiReviewContractRow> Rows,
    int BaselineCount,
    string HistoryLabel,
    string LatestComparison,
    IReadOnlyList<string> Details);

public sealed record ApiReviewOperationRowModel(string Primary, string Secondary, string Access, string Source, bool IsSafe);

public sealed record ApiReviewTargetCardModel(
    ApiReviewTarget Target,
    string DisplayName,
    string PathLabel,
    string SourceLabel,
    string AccessLabel,
    string ConfidenceLabel,
    int OperationCount,
    int WriteCount,
    bool HasContract,
    string? SchemaLabel,
    IReadOnlyList<ApiReviewOperationRowModel> Operations);

/// <summary>Result-view status of one target, mapped 1:1 from <see cref="ApiReviewTargetStatus"/> + <see cref="ApiReviewAccessMode"/>.</summary>
public enum ApiReviewTargetPresentationStatus
{
    Assessed,
    PartiallyAssessed,
    NotAssessed,
    AuthenticationRequired,
    ManualVerificationOnly,
    Unavailable,
}

public static class ApiReviewStatusLabels
{
    public static ApiReviewTargetPresentationStatus Of(ApiReviewTargetResult result) => result.Status switch
    {
        ApiReviewTargetStatus.Completed => ApiReviewTargetPresentationStatus.Assessed,
        ApiReviewTargetStatus.PartiallyCompleted => ApiReviewTargetPresentationStatus.PartiallyAssessed,
        ApiReviewTargetStatus.Blocked => result.AccessMode switch
        {
            ApiReviewAccessMode.Unavailable => ApiReviewTargetPresentationStatus.AuthenticationRequired,
            ApiReviewAccessMode.ManualOnly => ApiReviewTargetPresentationStatus.ManualVerificationOnly,
            _ => ApiReviewTargetPresentationStatus.Unavailable,
        },
        _ => ApiReviewTargetPresentationStatus.NotAssessed,
    };

    public static string Label(ApiReviewTargetPresentationStatus status) => status switch
    {
        ApiReviewTargetPresentationStatus.Assessed => "Assessed",
        ApiReviewTargetPresentationStatus.PartiallyAssessed => "Partially assessed",
        ApiReviewTargetPresentationStatus.NotAssessed => "Not assessed",
        ApiReviewTargetPresentationStatus.AuthenticationRequired => "Authentication required",
        ApiReviewTargetPresentationStatus.ManualVerificationOnly => "Manual verification only",
        ApiReviewTargetPresentationStatus.Unavailable => "Unavailable",
        _ => status.ToString(),
    };

    public static string Tone(ApiReviewTargetPresentationStatus status) => status switch
    {
        ApiReviewTargetPresentationStatus.Assessed => "ready",
        ApiReviewTargetPresentationStatus.PartiallyAssessed => "warning",
        ApiReviewTargetPresentationStatus.NotAssessed => "muted",
        _ => "attention",
    };

    /// <summary>Access column of the result views: what was actually used, or why nothing was executed.</summary>
    public static string AccessLabel(ApiReviewAccessMode mode) => mode switch
    {
        ApiReviewAccessMode.PublicHttp => "Public",
        ApiReviewAccessMode.AuthenticatedHttp => "Authenticated",
        ApiReviewAccessMode.Unavailable => "Authentication required · not executed",
        ApiReviewAccessMode.ManualOnly => "Manual verification only",
        ApiReviewAccessMode.Blocked => "Blocked",
        _ => mode.ToString(),
    };

    public static string ResultLabel(ApiReviewCheckResult result) => result switch
    {
        ApiReviewCheckResult.Pass => "Pass",
        ApiReviewCheckResult.Fail => "Fail",
        ApiReviewCheckResult.Warning => "Warning",
        ApiReviewCheckResult.ManualReview => "Manual review",
        ApiReviewCheckResult.NotApplicable => "Not applicable",
        ApiReviewCheckResult.Blocked => "Blocked",
        _ => "Not tested",
    };

    public static string ContractLabel(ApiReviewContractSummary? contract)
    {
        if (contract is null) return "No contract";
        if (contract.Kind.StartsWith("GraphQL", StringComparison.OrdinalIgnoreCase))
            return contract.IntrospectionEnabled == false ? "Introspection unavailable" : contract.Available ? "Runtime schema" : "Schema unavailable";
        return contract.Available ? "OpenAPI" : "OpenAPI unavailable";
    }

    public static string DriftLabel(ApiReviewDriftClassification drift) => drift switch
    {
        ApiReviewDriftClassification.Breaking => "Breaking",
        ApiReviewDriftClassification.PotentiallyBreaking => "Potentially breaking",
        ApiReviewDriftClassification.NonBreaking => "Non-breaking",
        _ => "Informational",
    };
}

public sealed record ApiReviewSeverityCount(ApiReviewSeverity Severity, int Count);

public sealed record ApiReviewCoverageRowModel(string Label, string Value, string? Detail);

public sealed record ApiReviewSummaryModel(
    string Environment,
    string EnvironmentType,
    string GeneratedAt,
    int RestServices,
    int GraphQlServices,
    /// <summary>Access actually used: "Authenticated" / "Public only" / "Mixed" / "None executed".</summary>
    string AccessUsed,
    string AccessUsedDetail,
    int OperationsReviewed,
    IReadOnlyList<ApiReviewCoverageRowModel> Coverage,
    IReadOnlyList<ApiReviewSeverityCount> Severities,
    int TargetsBlocked);

/// <summary>
/// What the next run will cover, counted from the SELECTED targets. Configuration counts only — nothing here says
/// anything about what a review found, because none has run yet.
/// </summary>
public sealed record ApiReviewScopeSummary(int Selected, int Rest, int GraphQl, int AuthRequired, int Operations)
{
    public string Headline => Selected == 0
        ? "No API targets selected"
        : $"{Selected} API target{(Selected == 1 ? "" : "s")} selected";

    /// <summary>"3 REST · 1 GraphQL" — only the protocols actually present.</summary>
    public string Protocols => string.Join(" · ", new[]
    {
        Rest > 0 ? $"{Rest} REST" : null,
        GraphQl > 0 ? $"{GraphQl} GraphQL" : null,
    }.Where(p => p is not null));

    /// <summary>Stated only when it is true; an all-public scope says nothing about authentication.</summary>
    public string? AuthNote => AuthRequired == 0
        ? null
        : $"{AuthRequired} require{(AuthRequired == 1 ? "s" : "")} authentication";
}

/// <summary>
/// Scope state of one API review DOMAIN. Deliberately the same vocabulary the Frontend Quality Review uses for its
/// domains, and deliberately NOT access or configuration vocabulary: "Unavailable", "Not connected" and "Not configured"
/// describe an access path or a setting, never whether a domain is part of the review.
/// </summary>
public enum ApiReviewDomainState { Included, Limited, PartialEvidence, NotIncluded }

public static class ApiReviewDomainStates
{
    public static string Label(ApiReviewDomainState state) => state switch
    {
        ApiReviewDomainState.Included => "Included",
        ApiReviewDomainState.Limited => "Limited",
        ApiReviewDomainState.PartialEvidence => "Partial evidence",
        _ => "Not included",
    };

    public static string Tone(ApiReviewDomainState state) => state switch
    {
        ApiReviewDomainState.Included => "ready",
        ApiReviewDomainState.NotIncluded => "muted",
        _ => "attention",
    };
}

public sealed record ApiReviewDomainCard(
    string Key,
    string Title,
    string Purpose,
    ApiReviewDomainState State,
    string? Limitation);

/// <summary>
/// How a completed API review ended. No "Passed", "Secure" or "Compliant": the report carries no such claim, and a
/// read-only review that found nothing has not established that an API is sound.
/// </summary>
public enum ApiReviewResultState
{
    /// <summary>Nothing executed: every selected target was blocked or errored.</summary>
    FailedToRun,
    /// <summary>Some targets executed and some did not, so coverage is incomplete.</summary>
    PartialCoverage,
    /// <summary>Everything executed, and the review leaves obligations only a person can discharge.</summary>
    CompletedWithManualReview,
    /// <summary>Everything selected executed, but under reduced access or evidence.</summary>
    CompletedWithLimitations,
    Completed,
}

public static class ApiReviewResultStates
{
    public static string Label(ApiReviewResultState state) => state switch
    {
        ApiReviewResultState.FailedToRun => "No target could be reviewed",
        ApiReviewResultState.PartialCoverage => "Partial coverage",
        ApiReviewResultState.CompletedWithManualReview => "Completed — manual review required",
        ApiReviewResultState.CompletedWithLimitations => "Completed with limitations",
        _ => "Completed",
    };

    public static string Tone(ApiReviewResultState state) => state switch
    {
        ApiReviewResultState.Completed => "ready",
        ApiReviewResultState.FailedToRun => "attention",
        _ => "warning",
    };

    /// <summary>The review produced judgeable output. False means this is an execution report, not a quality one.</summary>
    public static bool IsCompleted(ApiReviewResultState state) => state is not ApiReviewResultState.FailedToRun;
}

/// <param name="ManualReviewCount">
/// Review obligations, counted separately from findings and never added to them: they are work outstanding, not defects found.
/// </param>
public sealed record ApiReviewResultView(
    ApiReviewResultState State,
    string Summary,
    int FindingCount,
    int ManualReviewCount,
    int TargetsAssessed,
    int TargetsBlocked,
    ApiReviewSummaryModel Metadata)
{
    public string StateLabel => ApiReviewResultStates.Label(State);
}
