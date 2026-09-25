using BirkNext.ApiReview;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>Bounded UI wording for existing evidence. Does not change engine results or finding counts.</summary>
public static class ApiReviewEvidencePresentation
{
    public const string ReviewedHelp = "Reviewed — available automated scope completed. This does not mean the service is fully validated.";
    public const string MemoryOnlyHelp = "Memory only means credentials are held only in the backend runtime for the current testing context and are not exposed to the review result.";

    public static string ContractState(ApiReviewContractSummary contract) => contract.Available
        ? "Available"
        : contract.Status == ApiReviewCheckResult.Blocked ? "Blocked" : "Unavailable";

    public static string Execution(ApiReviewOperationResult operation) => !operation.Executed
        ? ApiReviewStatusLabels.ResultLabel(operation.Result is ApiReviewCheckResult.Blocked ? ApiReviewCheckResult.Blocked : ApiReviewCheckResult.NotTested)
        : operation.StatusCode is >= 200 and < 300 ? "Successful" : "Executed";

    public static IEnumerable<ApiReviewCheck> Checks(ApiReviewReport report) =>
        report.Targets.SelectMany(t => t.Checks.Concat(t.Operations.SelectMany(o => o.Checks)));

    public static IEnumerable<ApiReviewCheck> OperationChecks(ApiReviewOperationResult operation) =>
        operation.Checks.Select(c => c.CheckId == "rest-payload" && operation.ContentLength is null
            ? c with { Result = ApiReviewCheckResult.NotTested, Detail = "Payload size was not recorded." }
            : c);

    public static string ContractCheckCoverage(ApiReviewReport report)
    {
        var checks = Checks(report).ToList();
        var drift = checks.Count(c => c.Area == ApiReviewFindingType.Drift && c.Result is ApiReviewCheckResult.Pass or ApiReviewCheckResult.Warning or ApiReviewCheckResult.Fail);
        var contract = checks.Count(c => c.Area == ApiReviewFindingType.Contract && c.Result is ApiReviewCheckResult.Pass or ApiReviewCheckResult.Warning or ApiReviewCheckResult.Fail);
        return $"{contract} contract checks executed · {drift} drift/history checks executed";
    }

    public static string CheckLabel(ApiReviewCheck check, ApiReviewPolicy policy)
    {
        if (check.CheckId == "rest-payload" && check.Result == ApiReviewCheckResult.Pass && policy.RestPayloadThreshold <= 0)
            return "Observed";
        // Header checks that detected something produce a finding with its own severity; the check says what it saw,
        // not a second severity-like word. Other results (threshold warnings, failures) keep their real meaning.
        if (check.Result == ApiReviewCheckResult.Warning)
        {
            var observedLabel = check.CheckId switch
            {
                "sec-hsts" or "sec-xcto" or "sec-cache-control" => "Issue detected",
                "sec-server-disclosure" => "Disclosure observed",
                _ => null,
            };
            if (observedLabel is not null) return observedLabel;
        }
        if (check.Result != ApiReviewCheckResult.Pass) return ApiReviewStatusLabels.ResultLabel(check.Result);
        if (check.Area == ApiReviewFindingType.Drift) return "No changes detected";
        return check.CheckId switch
        {
            "sec-tls" => "HTTPS observed",
            "sec-hsts" or "sec-cache-control" or "rest-rate-limit-headers" or "rest-compression" => "Header observed",
            "sec-server-disclosure" => "No issue detected",
            "cors-preflight" => "Response observed",
            "cors-policy" or "gql-introspection" => "Observed",
            "errors-leak" or "gql-error-leak" => "No indicators observed",
            _ => "Pass",
        };
    }

    public static string CheckSummary(IReadOnlyList<ApiReviewCheck> checks, ApiReviewPolicy policy)
    {
        if (checks.All(c => c.Area == ApiReviewFindingType.Drift))
        {
            var completed = checks.Count(c => c.Result is ApiReviewCheckResult.Pass or ApiReviewCheckResult.Warning or ApiReviewCheckResult.Fail);
            return checks.All(c => c.Result == ApiReviewCheckResult.Pass)
                ? $"Drift checks · {completed} completed · 0 changes detected"
                : $"Drift checks · {completed} completed · " + string.Join(" · ", checks.GroupBy(c => CheckLabel(c, policy)).Select(g => $"{g.Key}: {g.Count()}"));
        }
        var noun = checks.Count > 0 && checks.All(c => c.Area == ApiReviewFindingType.Errors) ? "Error-handling checks" : "Checks";
        return $"{noun} ({checks.Count}) · " + string.Join(" · ", checks.GroupBy(c => CheckLabel(c, policy)).Select(g => $"{g.Key}: {g.Count()}"));
    }

    public static string CheckTitle(ApiReviewCheck check) => check.CheckId is "errors-leak" or "gql-error-leak"
        ? check.Title.Replace("No internal details in error responses", "Internal-detail indicators in error responses")
            .Replace("No internal details in errors", "Internal-detail indicators in errors")
        : check.CheckId == "sec-tls" ? check.Title.Replace("TLS", "Transport scheme") : check.Title;

    public static string CheckDetail(ApiReviewCheck check, ApiReviewPolicy policy) => check.CheckId switch
    {
        "sec-tls" => $"{check.Detail}. This does not assess full TLS protocol/cipher configuration.",
        "cors-preflight" => $"{check.Detail} HTTP response only; browser CORS enforcement was not tested.",
        "cors-policy" => $"{check.Detail} Observed response headers only; this is not a complete CORS assessment.",
        "gql-introspection" => $"{check.Detail} Introspection response observed; this does not validate schema coverage or authorization.",
        "errors-leak" or "gql-error-leak" when check.Result == ApiReviewCheckResult.Pass => "No internal-detail indicators observed in the sampled error response.",
        // Newer results carry the policy in their own detail (the values the engine evaluated); older ones get it from the report's
        // policy snapshot. Either way it is the policy of THAT run, never today's settings.
        "rest-payload" when check.Detail.Contains("warning >", StringComparison.Ordinal) => check.Detail,
        "rest-payload" when policy.RestPayloadThreshold > 0 => $"{check.Detail} Review policy: warning > {ApiReviewPolicy.Bytes(policy.RestPayloadThreshold)}.",
        "rest-payload" => $"{check.Detail} No positive payload threshold recorded; size is observational only.",
        "rest-latency" or "gql-latency" when check.Detail.Contains("warning >", StringComparison.Ordinal) => check.Detail,
        "rest-latency" or "gql-latency" => $"{check.Detail} Review policy: {policy.LatencyPolicyText}.",
        _ => check.Detail,
    };
}
