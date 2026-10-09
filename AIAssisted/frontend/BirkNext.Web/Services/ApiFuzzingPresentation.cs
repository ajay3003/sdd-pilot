using BirkNext.ApiReview;

namespace BirkNext.Web.Services;

/// <summary>One labelled number of a safe-fuzzing result summary.</summary>
public sealed record ApiFuzzingCount(string Key, string Label, int Value, string Tone);

/// <summary>
/// Pure presentation of API Quality Review → Safe fuzzing. The backend owns every decision (environment safety, eligibility, cases,
/// outcomes); this only labels them. The client-side environment check exists so a Production target shows "Blocked by safety policy"
/// before any request — the backend refuses it independently.
/// </summary>
public static class ApiFuzzingPresentation
{
    public const string Intro = ApiFuzzingWording.NotAPenetrationTest;
    public const string ScopeNote = ApiFuzzingWording.Scope;
    public const string AuthorizationLimitation = "Authentication enforcement is checked by the review; general authorization (what each role may access) is not tested and remains future work.";
    public const string BlockedTitle = "Blocked by safety policy";

    public static IReadOnlyList<ApiFuzzingLevel> Levels { get; } = [ApiFuzzingLevel.Off, ApiFuzzingLevel.ContractFuzzing, ApiFuzzingLevel.SafeSecurityFuzzing];

    public static string LevelLabel(ApiFuzzingLevel level) => level switch
    {
        ApiFuzzingLevel.ContractFuzzing => "Contract fuzzing",
        ApiFuzzingLevel.SafeSecurityFuzzing => "Safe security fuzzing",
        _ => "Off",
    };

    public static string LevelDescription(ApiFuzzingLevel level) => level switch
    {
        ApiFuzzingLevel.ContractFuzzing => "Inputs that violate the published contract: missing required values, invalid enum, UUID or date, wrong type, out-of-range numbers, GraphQL validation errors.",
        ApiFuzzingLevel.SafeSecurityFuzzing => "Contract fuzzing plus bounded robustness cases: empty and oversized-but-bounded strings, an unexpected query parameter, malformed header values and one malformed GraphQL document. No attack dictionaries.",
        _ => "No fuzz requests are sent.",
    };

    /// <summary>Client mirror of the backend allow-list. Null = may be analysed; otherwise why it is blocked. The backend decides.</summary>
    public static string? ClientBlockedReason(string? environmentType) =>
        string.Equals(environmentType, "Production", StringComparison.OrdinalIgnoreCase) ? "Production environments are never fuzzed."
        : ApiActiveTestingEnvironments.IsAllowed(environmentType) ? null
        : string.IsNullOrWhiteSpace(environmentType) || string.Equals(environmentType, "Custom", StringComparison.OrdinalIgnoreCase)
            ? "Environment safety could not be established. Classify the Target Environment as Local, Development, QA or Test."
            : $"Safe fuzzing runs only against Local, Development, QA or Test environments, not {environmentType}.";

    public static string SafetyLabel(ApiEnvironmentSafetyState state) => state switch
    {
        ApiEnvironmentSafetyState.Allowed => "Allowed",
        ApiEnvironmentSafetyState.ProductionBlocked or ApiEnvironmentSafetyState.ProductionMarkerBlocked => "Blocked — production",
        ApiEnvironmentSafetyState.UnknownBlocked or ApiEnvironmentSafetyState.ConflictBlocked => "Blocked — environment safety not established",
        ApiEnvironmentSafetyState.NotPermittedBlocked => "Blocked — environment not permitted",
        ApiEnvironmentSafetyState.Disabled => "Disabled on this BirkNext instance",
        _ => state.ToString(),
    };

    public static string SafetyTone(ApiEnvironmentSafetyState state) => state == ApiEnvironmentSafetyState.Allowed ? "ready" : "attention";

    public static string ClassificationLabel(ApiFuzzSafetyClassification classification) => classification switch
    {
        ApiFuzzSafetyClassification.ReadOnlyEligible => "Eligible (read-only)",
        ApiFuzzSafetyClassification.UnsafeMethod => "Skipped — write method",
        ApiFuzzSafetyClassification.Mutation => "Skipped — GraphQL mutation",
        ApiFuzzSafetyClassification.MissingContract => "Skipped — no contract input",
        ApiFuzzSafetyClassification.UnknownSafety => "Skipped — safety unknown",
        ApiFuzzSafetyClassification.ProductionBlocked => "Blocked — production",
        ApiFuzzSafetyClassification.AccessUnavailable => "Skipped — access unavailable",
        ApiFuzzSafetyClassification.BudgetExhausted => "Not in this run — budget",
        _ => classification.ToString(),
    };

    public static string ClassificationTone(ApiFuzzSafetyClassification classification) => classification switch
    {
        ApiFuzzSafetyClassification.ReadOnlyEligible => "ready",
        ApiFuzzSafetyClassification.ProductionBlocked => "attention",
        _ => "muted",
    };

    public static string MutationLabel(ApiFuzzMutationType mutation) => mutation switch
    {
        ApiFuzzMutationType.MissingRequired => "Required parameter omitted",
        ApiFuzzMutationType.InvalidEnum => "Invalid enum value",
        ApiFuzzMutationType.InvalidUuid => "Invalid UUID",
        ApiFuzzMutationType.InvalidDate => "Invalid date",
        ApiFuzzMutationType.WrongType => "Wrong type",
        ApiFuzzMutationType.NumericBelowMinimum => "Below minimum",
        ApiFuzzMutationType.NumericAboveMaximum => "Above maximum",
        ApiFuzzMutationType.NegativeNotAllowed => "Negative where not allowed",
        ApiFuzzMutationType.ZeroNotAllowed => "Zero where not allowed",
        ApiFuzzMutationType.StringTooLong => "Longer than maxLength",
        ApiFuzzMutationType.EmptyString => "Empty string",
        ApiFuzzMutationType.OversizedString => "Oversized string (bounded)",
        ApiFuzzMutationType.UnexpectedParameter => "Unexpected query parameter",
        ApiFuzzMutationType.MissingRequiredHeader => "Required header omitted",
        ApiFuzzMutationType.InvalidHeaderValue => "Invalid header value",
        ApiFuzzMutationType.MalformedHeaderValue => "Malformed header value",
        ApiFuzzMutationType.GraphQlUnknownField => "Unknown field",
        ApiFuzzMutationType.GraphQlWrongScalarType => "Wrong scalar type",
        ApiFuzzMutationType.GraphQlMissingRequiredArgument => "Required argument omitted",
        ApiFuzzMutationType.GraphQlNullForNonNull => "Null for non-null argument",
        ApiFuzzMutationType.GraphQlInvalidEnum => "Invalid enum value",
        ApiFuzzMutationType.GraphQlMalformedSyntax => "Malformed syntax",
        _ => mutation.ToString(),
    };

    public static string ExpectedLabel(ApiFuzzExpectedBehavior behavior) => behavior switch
    {
        ApiFuzzExpectedBehavior.RejectWithClientError => "Rejected with a client error (4xx)",
        ApiFuzzExpectedBehavior.RejectWithGraphQlError => "GraphQL validation error, no data",
        _ => "Accepted or rejected (both fine)",
    };

    public static string OutcomeLabel(ApiFuzzOutcome outcome) => outcome switch
    {
        ApiFuzzOutcome.HandledValidation => "Handled validation",
        ApiFuzzOutcome.Unexpected5xx => "Unexpected 5xx",
        ApiFuzzOutcome.UnexpectedAcceptance => "Unexpected acceptance",
        ApiFuzzOutcome.Timeout => "Timeout",
        ApiFuzzOutcome.ConnectionFailure => "Connection failure",
        ApiFuzzOutcome.ContractViolation => "Contract violation",
        ApiFuzzOutcome.PotentialInformationLeak => "Potential information leak",
        ApiFuzzOutcome.AuthenticationBlocked => "Blocked by authentication",
        ApiFuzzOutcome.AuthorizationBlocked => "Blocked by access control",
        ApiFuzzOutcome.NotExecuted => "Not executed",
        ApiFuzzOutcome.SafetyBlocked => "Blocked by safety guard",
        _ => outcome.ToString(),
    };

    public static string OutcomeTone(ApiFuzzOutcome outcome) => outcome switch
    {
        ApiFuzzOutcome.HandledValidation => "pass",
        ApiFuzzOutcome.Unexpected5xx or ApiFuzzOutcome.PotentialInformationLeak => "fail",
        ApiFuzzOutcome.UnexpectedAcceptance or ApiFuzzOutcome.ContractViolation => "warning",
        _ => "muted",
    };

    public static string CompletenessLabel(ApiFuzzCompleteness completeness, bool running) => running ? "Running" : completeness switch
    {
        ApiFuzzCompleteness.Full => "Full — every planned case executed",
        ApiFuzzCompleteness.Partial => "Partial",
        ApiFuzzCompleteness.Failed => "Failed — the run could not execute meaningfully",
        _ => "Not run",
    };

    public static int Count(ApiFuzzingReport report, ApiFuzzOutcome outcome) => report.Outcomes.TryGetValue(outcome, out var n) ? n : 0;

    /// <summary>The summary row the panel and the export both show. Findings found never make the run "Failed".</summary>
    public static IReadOnlyList<ApiFuzzingCount> Summary(ApiFuzzingReport report) =>
    [
        new("planned", "Cases planned", report.CasesPlanned, "muted"),
        new("executed", "Cases executed", report.CasesExecuted, "muted"),
        new("handled", "Handled validation", Count(report, ApiFuzzOutcome.HandledValidation), "pass"),
        new("5xx", "Unexpected 5xx", Count(report, ApiFuzzOutcome.Unexpected5xx), Count(report, ApiFuzzOutcome.Unexpected5xx) > 0 ? "fail" : "muted"),
        new("leaks", "Potential leaks", report.Results.Count(r => r.LeakIndicators.Count > 0), report.Results.Any(r => r.LeakIndicators.Count > 0) ? "fail" : "muted"),
        new("acceptance", "Unexpected acceptance", Count(report, ApiFuzzOutcome.UnexpectedAcceptance), Count(report, ApiFuzzOutcome.UnexpectedAcceptance) > 0 ? "warning" : "muted"),
        new("contract", "Contract violations", Count(report, ApiFuzzOutcome.ContractViolation), Count(report, ApiFuzzOutcome.ContractViolation) > 0 ? "warning" : "muted"),
        new("blocked", "Blocked cases", report.CasesBlocked + Count(report, ApiFuzzOutcome.NotExecuted), report.CasesBlocked > 0 ? "attention" : "muted"),
    ];

    public static string LimitsText(ApiFuzzingSettings settings)
    {
        var s = settings.Clamped();
        return $"At most {s.MaxTotalRequests} requests · {s.MaxCasesPerOperation} per operation · one at a time · {s.RequestDelayMs} ms apart · {s.RequestTimeoutSeconds} s timeout · parameters ≤ {s.MaxParameterLength} characters · GraphQL documents ≤ {s.MaxPayloadBytes} bytes{(s.StopOnUnexpected5xx ? " · stops at the first unexpected 5xx" : "")}.";
    }

    public static string EnforcementLabel(ApiAuthenticationEnforcementStatus status) => status switch
    {
        ApiAuthenticationEnforcementStatus.Verified => "Verified",
        ApiAuthenticationEnforcementStatus.UnexpectedlyPublic => "Unexpectedly public",
        ApiAuthenticationEnforcementStatus.NotVerified => "Not verified",
        _ => "Not applicable",
    };

    public static string EnforcementTone(ApiAuthenticationEnforcementStatus status) => status switch
    {
        ApiAuthenticationEnforcementStatus.Verified => "pass",
        ApiAuthenticationEnforcementStatus.UnexpectedlyPublic => "warning",
        _ => "muted",
    };

    public static string HeaderResultLabel(SecurityHeaderOutcome result) => result switch
    {
        SecurityHeaderOutcome.Pass => "Present (expected)",
        SecurityHeaderOutcome.Missing => "Missing (expected)",
        SecurityHeaderOutcome.Observed => "Observed (not required)",
        SecurityHeaderOutcome.NotAssessed => "Not required",
        _ => "Not applicable",
    };

    public static string HeaderResultTone(SecurityHeaderOutcome result) => result switch
    {
        SecurityHeaderOutcome.Pass => "pass",
        SecurityHeaderOutcome.Missing => "warning",
        _ => "muted",
    };
}
