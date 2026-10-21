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

    /// <summary>Execution is its own fact: an operation that was not sent is "Not executed", never a review result.</summary>
    public static string Execution(ApiReviewOperationResult operation) => !operation.Executed
        ? operation.Result is ApiReviewCheckResult.Blocked ? "Not executed — blocked" : "Not executed"
        : operation.StatusCode is >= 200 and < 300 ? "Successful" : "Executed";

    public static IEnumerable<ApiReviewCheck> Checks(ApiReviewReport report) =>
        report.Targets.SelectMany(t => t.Checks.Concat(t.Operations.SelectMany(o => o.Checks)));

    /// <summary>
    /// Legacy reports (no body evidence) with no recorded size show the payload check as Not tested. Newer reports carry their own
    /// Not tested reason (decode failure, unsupported encoding…), which is kept.
    /// </summary>
    public static IEnumerable<ApiReviewCheck> OperationChecks(ApiReviewOperationResult operation) =>
        operation.Checks.Select(c => c.CheckId == "rest-payload" && operation.ContentLength is null && operation.Body is null
            ? c with { Result = ApiReviewCheckResult.NotTested, Detail = "Payload size was not recorded." }
            : c);

    /// <summary>
    /// The payload column: the decoded payload — the REST payload threshold's basis — with transfer size and Content-Encoding as
    /// secondary text when the response was encoded. Reports recorded before body evidence existed show their own value unchanged.
    /// </summary>
    public static (string Primary, string? Secondary) PayloadSize(ApiReviewOperationResult operation)
    {
        if (!operation.Executed) return ("—", null);
        if (operation.Body is not { } body) return (operation.ContentLength?.ToString() ?? "—", null);
        var transfer = body.ContentEncoding is { } coding ? $"Transfer: {(body.TransferBytes is { } t ? Size(t) : "unknown")} · {coding}" : null;
        return body.Decoding switch
        {
            ApiResponseBodyDecoding.NoBody => ("No body", null),
            ApiResponseBodyDecoding.NotEncoded or ApiResponseBodyDecoding.Decoded when operation.ContentLength is { } exact =>
                (body.ContentEncoding is null ? Size(exact) : $"{Size(exact)} decoded", transfer),
            ApiResponseBodyDecoding.NotEncoded or ApiResponseBodyDecoding.Decoded when body.DecodedBytesIsLowerBound && body.DecodedBytes is { } atLeast =>
                ($"At least {Size(atLeast)} decoded", transfer),
            _ => ("Not tested", body.Reason ?? transfer),
        };
    }

    /// <summary>Payload evaluation basis in words, for exports and technical detail.</summary>
    public static string PayloadBasis(ApiReviewResponseBody body) => body.Decoding switch
    {
        ApiResponseBodyDecoding.Decoded => body.DecodedBytesIsLowerBound ? "Decoded payload (lower bound)" : "Decoded payload",
        ApiResponseBodyDecoding.NotEncoded => "Payload (not encoded: transfer = decoded)",
        ApiResponseBodyDecoding.NoBody => "No body",
        _ => "Not evaluated",
    };

    public static string DecodingLabel(ApiResponseBodyDecoding decoding) => decoding switch
    {
        ApiResponseBodyDecoding.NotEncoded => "Not encoded",
        ApiResponseBodyDecoding.Decoded => "Decoded",
        ApiResponseBodyDecoding.UnsupportedEncoding => "Unsupported encoding",
        ApiResponseBodyDecoding.DecodeFailed => "Decoding failed",
        ApiResponseBodyDecoding.Incomplete => "Incomplete body",
        ApiResponseBodyDecoding.NoBody => "No body",
        _ => "Not decoded (gateway)",
    };

    /// <summary>"48 KB" — KB = 1024 bytes, as the thresholds are stored.</summary>
    public static string Size(long bytes) =>
        bytes < 1024 ? $"{bytes.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} bytes"
        : bytes < 1024 * 1024 ? $"{(bytes / 1024.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)} KB"
        : $"{(bytes / (1024.0 * 1024)).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)} MB";

    public static string ContractCheckCoverage(ApiReviewReport report)
    {
        var checks = Checks(report).ToList();
        var drift = checks.Count(c => c.Area == ApiReviewFindingType.Drift && c.Result is ApiReviewCheckResult.Pass or ApiReviewCheckResult.Warning or ApiReviewCheckResult.Fail);
        var contract = checks.Count(c => c.Area == ApiReviewFindingType.Contract && c.Result is ApiReviewCheckResult.Pass or ApiReviewCheckResult.Warning or ApiReviewCheckResult.Fail);
        return $"{contract} contract checks executed · {drift} drift/history checks executed";
    }

    /// <summary>The canonical label (Task 10 §49): Passed, Issue detected, Observed, Not assessed, Not applicable, Manual review — timing and
    /// payload checks say "within/exceeded configured review threshold". The recorded result is never changed, only worded.</summary>
    public static string CheckLabel(ApiReviewCheck check, ApiReviewPolicy policy) => ApiReviewStatusTaxonomy.Label(check, policy);

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
        "errors-leak" or "gql-error-leak" when check.Result == ApiReviewCheckResult.Pass => "No internal-error indicators found in the sampled error response.",
        _ when check.Result is ApiReviewCheckResult.NotTested or ApiReviewCheckResult.Blocked && string.IsNullOrWhiteSpace(check.Detail) => "Not assessed: no reason was recorded by this review.",
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
