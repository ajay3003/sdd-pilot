using BirkNext.ApiReview;
using BirkNext.LocalHttpsProxy;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>The canonical post-run states every API Quality Review check is shown in (Task 10 §49).</summary>
public enum ApiReviewCanonicalStatus
{
    /// <summary>A check ran and its expectation was satisfied.</summary>
    Passed,
    /// <summary>A check ran and its expectation was not satisfied.</summary>
    IssueDetected,
    /// <summary>Evidence exists, but the check is not a pass/fail assertion (CORS headers, server header, a measurement without threshold).</summary>
    Observed,
    /// <summary>Evidence, capability or sample was insufficient to decide. Always carries a reason.</summary>
    NotAssessed,
    /// <summary>The check does not apply to this API or surface.</summary>
    NotApplicable,
    /// <summary>Automation intentionally cannot decide.</summary>
    ManualReview,
}

/// <summary>
/// Maps the engine's recorded check results to the canonical states without changing any result: a Warning is still the issue it
/// recorded, a Not tested is still not tested — only the words are unified. "Verified" is reserved for an explicit expectation that was
/// checked (authentication enforcement); "Passed" for a check whose expectation was satisfied; "Observed" for evidence that asserts nothing.
/// </summary>
public static class ApiReviewStatusTaxonomy
{
    /// <summary>Checks whose result is evidence, not a pass/fail assertion, when nothing was detected.</summary>
    private static readonly HashSet<string> ObservationalWhenPassing = new(StringComparer.Ordinal)
    {
        "cors-preflight", "cors-policy", "gql-introspection", "errors-leak", "gql-error-leak",
    };

    public static ApiReviewCanonicalStatus Of(ApiReviewCheck check, ApiReviewPolicy policy) => check.Result switch
    {
        ApiReviewCheckResult.Pass when ObservationalWhenPassing.Contains(check.CheckId) => ApiReviewCanonicalStatus.Observed,
        ApiReviewCheckResult.Pass when check.CheckId == "rest-payload" && policy.RestPayloadThreshold <= 0 => ApiReviewCanonicalStatus.Observed,
        ApiReviewCheckResult.Pass => ApiReviewCanonicalStatus.Passed,
        // A server/technology header is an observation (Info finding) unless a policy raises it; it is never a pass/fail.
        ApiReviewCheckResult.Warning when check.CheckId == "sec-server-disclosure" => ApiReviewCanonicalStatus.Observed,
        ApiReviewCheckResult.Warning or ApiReviewCheckResult.Fail => ApiReviewCanonicalStatus.IssueDetected,
        ApiReviewCheckResult.ManualReview => ApiReviewCanonicalStatus.ManualReview,
        ApiReviewCheckResult.NotApplicable => ApiReviewCanonicalStatus.NotApplicable,
        _ => ApiReviewCanonicalStatus.NotAssessed,
    };

    public static string Label(ApiReviewCanonicalStatus status) => status switch
    {
        ApiReviewCanonicalStatus.Passed => "Passed",
        ApiReviewCanonicalStatus.IssueDetected => "Issue detected",
        ApiReviewCanonicalStatus.Observed => "Observed",
        ApiReviewCanonicalStatus.NotApplicable => "Not applicable",
        ApiReviewCanonicalStatus.ManualReview => "Manual review",
        _ => "Not assessed",
    };

    /// <summary>The label a check shows. Timing and payload checks say "review threshold" — never an SLA "Pass".</summary>
    public static string Label(ApiReviewCheck check, ApiReviewPolicy policy)
    {
        var status = Of(check, policy);
        // A drift comparison that ran and found nothing is said as exactly that — never "Passed", which reads as contract coverage.
        if (check.Area == ApiReviewFindingType.Drift && status == ApiReviewCanonicalStatus.Passed) return "No changes detected";
        if (check.CheckId is "rest-latency" or "gql-latency" or "rest-payload" or "gql-payload" && check.Area == ApiReviewFindingType.Performance)
        {
            if (status == ApiReviewCanonicalStatus.Passed) return "Within configured review threshold";
            if (status == ApiReviewCanonicalStatus.IssueDetected) return "Exceeded configured review threshold";
        }
        return Label(status);
    }

    public static string Tone(ApiReviewCanonicalStatus status) => status switch
    {
        ApiReviewCanonicalStatus.Passed => "pass",
        ApiReviewCanonicalStatus.IssueDetected => "warning",
        _ => "muted",
    };
}

/// <summary>One review domain's coverage after the run, with the exact reason when it is not complete.</summary>
public sealed record ApiReviewDomainCoverageRow(string Key, string Domain, string State, string? Reason, bool Limited, string Tab)
{
    /// <summary>The domain's recorded coverage fact ("2 / 2 operations reviewed", "17 passive, read-only checks executed").</summary>
    public string? Detail { get; init; }
}

/// <summary>A causal limitation: what could not be fully assessed, and why.</summary>
public sealed record ApiReviewLimitationRow(string Key, string Title, string Reason);

/// <summary>Execution, assessment, manual review and coverage of one run — four facts, never one badge.</summary>
public sealed record ApiReviewOutcomeView(
    string Execution,
    string Assessment,
    string ManualReview,
    string Coverage,
    bool CoverageLimited,
    int LogicalIssues,
    int SourceFindings,
    int ServicesReviewed,
    int Services,
    int RequestsExecuted,
    string AuthenticatedAccess);

/// <summary>One service row of the Overview summary.</summary>
public sealed record ApiReviewServiceSummaryRow(ApiReviewTargetResult Result, string Name, string Type, string Access, string Operations, string ContractOrSchema, string ReviewState, bool Limited);

/// <summary>A protocol tab's summary: what was reviewed, how, and the one state that matters.</summary>
public sealed record ApiReviewProtocolSummary(IReadOnlyList<(string Label, string Value)> Facts, string ReviewState, string? Note);

/// <summary>A contract fact with its own cause: REST contract, GraphQL schema, compatibility, drift comparison.</summary>
public sealed record ApiReviewContractCause(string Key, string Label, string State, string? Reason);

/// <summary>
/// The decision-first post-run of API Quality Review (Task 10). Derived from the stored report only; every number reconciles with the
/// logical-issue model (<see cref="ApiReviewPresentation.LogicalIssues"/>) and the source findings it groups.
/// </summary>
public static class ApiReviewPostRunPresentation
{
    public static ApiReviewOutcomeView Outcome(ApiReviewReport report)
    {
        var result = ApiReviewPresentation.Result(report);
        var coverage = Coverage(report);
        var authRequired = report.Coverage.AuthenticatedPlanned;
        var executed = result.Metadata.OperationsReviewed;
        return new(
            result.State switch
            {
                ApiReviewResultState.FailedToRun => "Failed",
                ApiReviewResultState.PartialCoverage => "Completed — partial coverage",
                _ => "Completed",
            },
            result.State == ApiReviewResultState.FailedToRun ? "Incomplete"
                : result.IssueCount > 0 ? "Issues identified" : "No automated issues identified",
            report.ManualReviewItems.Count > 0 ? $"Manual review required — {report.ManualReviewItems.Count} area{(report.ManualReviewItems.Count == 1 ? "" : "s")}" : "Not required",
            coverage.Any(c => c.Limited) ? "Limited" : "Complete",
            coverage.Any(c => c.Limited),
            result.IssueCount,
            result.FindingCount,
            result.TargetsAssessed,
            report.Targets.Count,
            executed,
            // Reports without access counters keep the recorded wording; nothing is inferred from the access mode.
            authRequired == 0 ? result.Metadata.AccessUsed
                : $"Used for {report.Coverage.AuthenticatedExecuted}/{authRequired} authentication-required service{(authRequired == 1 ? "" : "s")}"
                  + (report.Coverage.PublicPlanned > 0 ? $" · public access for {report.Coverage.PublicExecuted}/{report.Coverage.PublicPlanned}" : ""));
    }

    // ── Coverage & limitations ──────────────────────────────────────────────────────────────────────────────────────────────────

    public static IReadOnlyList<ApiReviewDomainCoverageRow> Coverage(ApiReviewReport report)
    {
        var rows = new List<ApiReviewDomainCoverageRow>();
        var rest = report.Targets.Where(t => t.Target.ApiType == ApiReviewTargetType.Rest).ToList();
        var gql = report.Targets.Where(t => t.Target.ApiType == ApiReviewTargetType.GraphQl).ToList();
        var checks = ApiReviewEvidencePresentation.Checks(report).ToList();

        if (rest.Count > 0)
        {
            var executed = rest.Any(t => t.Operations.Any(o => o.Executed));
            var contract = rest.All(t => t.Contract is { Available: true });
            rows.Add(!executed ? new("rest", "REST", "Not assessed", NotExecutedReason(rest), true, "REST")
                : contract ? new("rest", "REST", "Reviewed", null, false, "REST")
                : new("rest", "REST", "Reviewed — contract validation unavailable", "No OpenAPI contract was configured.", true, "REST"));
        }
        if (gql.Count > 0)
        {
            var executed = gql.Any(t => t.Operations.Any(o => o.Executed));
            var schema = gql.All(t => t.Contract is { Available: true } || t.GraphQlCompatibility?.SchemaSource is not null and not GraphQlSchemaSource.None);
            rows.Add(!executed ? new("graphql", "GraphQL", "Not assessed", NotExecutedReason(gql), true, "GraphQL")
                : schema ? new("graphql", "GraphQL", "Reviewed", null, false, "GraphQL")
                : new("graphql", "GraphQL", "Reviewed — runtime schema unavailable", SchemaReason(gql), true, "GraphQL"));
        }

        var contractsComplete = report.Targets.Count > 0 && report.Targets.All(t => t.Contract is { Available: true }) && DriftCompared(report);
        rows.Add(contractsComplete ? new("contracts", "Contracts", "Assessed", null, false, "Contracts")
            : new("contracts", "Contracts", "Incomplete", ContractsReason(report), true, "Contracts"));

        var security = checks.Where(c => c.Area is ApiReviewFindingType.Security or ApiReviewFindingType.AccessControl).ToList();
        rows.Add(security.Any(Decided) ? new("security", "Security", "Reviewed", null, false, "Security")
            : new("security", "Security", "Not assessed", "No security check could run on the reviewed responses.", true, "Security"));

        var errors = checks.Where(c => c.Area == ApiReviewFindingType.Errors).ToList();
        rows.Add(errors.Any(Decided) ? new("errors", "Error handling", "Reviewed", null, false, "Errors")
            : new("errors", "Error handling", "Not assessed", "No error response could be requested with a safe request.", true, "Errors"));

        var measured = Measured(report);
        var legacy = ApiReviewPresentation.Summary(report).Coverage.ToDictionary(r => r.Label, r => string.Join(" · ", new[] { r.Value, r.Detail }.Where(x => !string.IsNullOrWhiteSpace(x))));
        for (var i = 0; i < rows.Count; i++)
            if (legacy.TryGetValue(rows[i].Domain, out var fact)) rows[i] = rows[i] with { Detail = fact };
        if (report.Coverage.UnsafeOperationsNotExecuted > 0)
            rows.Add(new("writes", "Write operations", "Observed — not executed", $"{report.Coverage.UnsafeOperationsNotExecuted} write operation{(report.Coverage.UnsafeOperationsNotExecuted == 1 ? "" : "s")} observed; the read-only review policy never executes them.", false, "REST"));
        rows.Add(measured == 0
            ? new("performance", "Performance", "Not assessed", "No review request was measured.", true, "Performance")
            : new("performance", "Performance", "Observed — limited request sample",
                $"Only the review's own {measured} read-only request{(measured == 1 ? "" : "s")} {(measured == 1 ? "was" : "were")} measured; this is not load testing.", true, "Performance"));
        return rows;
    }

    /// <summary>The causal limitations, each named exactly once.</summary>
    public static IReadOnlyList<ApiReviewLimitationRow> Limitations(ApiReviewReport report)
    {
        var rows = new List<ApiReviewLimitationRow>();
        var restWithout = report.Targets.Where(t => t.Target.ApiType == ApiReviewTargetType.Rest && t.Contract is not { Available: true }).ToList();
        if (restWithout.Count > 0)
            rows.Add(new("rest-contract", "REST contract validation unavailable",
                $"No OpenAPI contract was configured for {string.Join(", ", restWithout.Select(t => ApiReviewPresentation.DisplayName(t.Target)))}."));
        var gqlWithout = report.Targets.Where(t => t.Target.ApiType == ApiReviewTargetType.GraphQl && t.Contract is not { Available: true }
            && t.GraphQlCompatibility?.SchemaSource is null or GraphQlSchemaSource.None).ToList();
        if (gqlWithout.Count > 0)
            rows.Add(new("graphql-schema", "GraphQL compatibility not assessed", SchemaReason(gqlWithout)));
        if (Measured(report) > 0)
            rows.Add(new("performance", "Performance evidence limited", "Only review traffic was measured; this is not load, stress or end-user performance testing."));
        if (!DriftCompared(report) && report.Targets.Count > 0)
            rows.Add(new("baseline", "No baseline comparison", "No previous comparable review exists for these services; this review records the first baseline."));
        return rows;
    }

    // ── Services ────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    public static IReadOnlyList<ApiReviewServiceSummaryRow> Services(ApiReviewReport report) => report.Targets.Select(t =>
    {
        var graphQl = t.Target.ApiType == ApiReviewTargetType.GraphQl;
        var executed = t.Operations.Count(o => o.Executed);
        var operations = graphQl
            ? $"{executed} request{(executed == 1 ? "" : "s")} / {ApiReviewPresentation.GraphQlCounts(t).Observed} observed operations"
            : $"{executed}";
        var status = ApiReviewStatusLabels.Of(t);
        var reviewed = status is ApiReviewTargetPresentationStatus.Assessed or ApiReviewTargetPresentationStatus.PartiallyAssessed;
        var contractLimited = t.Contract is not { Available: true };
        var state = !reviewed ? ApiReviewStatusLabels.Label(status)
            : contractLimited || status == ApiReviewTargetPresentationStatus.PartiallyAssessed ? "Reviewed with limitation" : "Reviewed";
        return new ApiReviewServiceSummaryRow(t, ApiReviewPresentation.DisplayName(t.Target), graphQl ? "GraphQL" : "REST",
            ApiReviewStatusLabels.AccessLabel(t.AccessMode), operations,
            graphQl ? (t.Contract is { Available: true } ? "Runtime schema" : "Runtime schema unavailable")
                : t.Contract is { Available: true } ? "OpenAPI contract" : "No OpenAPI contract",
            state, state != "Reviewed");
    }).ToList();

    // ── Protocol tab summaries ──────────────────────────────────────────────────────────────────────────────────────────────────

    public static ApiReviewProtocolSummary? Rest(ApiReviewReport report)
    {
        var rest = report.Targets.Where(t => t.Target.ApiType == ApiReviewTargetType.Rest).ToList();
        if (rest.Count == 0) return null;
        var ops = rest.SelectMany(t => t.Operations).ToList();
        var executed = ops.Count(o => o.Executed);
        var contract = rest.All(t => t.Contract is { Available: true });
        return new([
            ("Operations reviewed", $"{executed} of {ops.Count}"),
            ("Read-only requests executed", $"{executed}"),
            ("Access", AccessUsed(rest)),
            ("Contract", contract ? "Available" : "Unavailable — no OpenAPI contract configured"),
        ], !rest.Any(t => t.Operations.Any(o => o.Executed)) ? "Not reviewed" : contract ? "Reviewed" : "Reviewed with contract limitation",
            ops.Count(o => !o.Executed && !IsSafe(o)) is var writes and > 0
                ? $"{writes} write operation{(writes == 1 ? " was" : "s were")} observed and not executed (read-only review policy)." : null);
    }

    public static ApiReviewProtocolSummary? GraphQl(ApiReviewReport report)
    {
        var gql = report.Targets.Where(t => t.Target.ApiType == ApiReviewTargetType.GraphQl).ToList();
        if (gql.Count == 0) return null;
        var observed = gql.Sum(t => ApiReviewPresentation.GraphQlCounts(t).Observed);
        var executed = gql.Sum(t => t.Operations.Count(o => o.Executed));
        var schema = gql.All(t => t.Contract is { Available: true } || t.GraphQlCompatibility?.SchemaSource is not null and not GraphQlSchemaSource.None);
        var compatibility = gql.Any(t => ApiReviewPresentation.GraphQlCounts(t).CompatibilityAssessed);
        var systemOnly = gql.SelectMany(t => t.Operations.Where(o => o.Executed)).All(o => IsSystemQuery(o));
        return new([
            ("Operations observed", $"{observed}"),
            ("Read-only queries executed", $"{executed}"),
            ("Runtime schema", schema ? "Available" : "Unavailable"),
            ("Contract compatibility", compatibility ? "Assessed" : "Not assessed"),
            ("Reason", compatibility ? "—" : schema ? "No observed operation document was available to validate." : SchemaReason(gql)),
        ], executed == 0 ? "Not reviewed" : schema ? "Reviewed" : "Reviewed with schema limitation",
            executed > 0 && systemOnly
                ? "Only a lightweight system query (__typename) was executed. It establishes reachability and error behaviour, not business response performance, business correctness or operation compatibility."
                : null);
    }

    // ── Contracts ───────────────────────────────────────────────────────────────────────────────────────────────────────────────

    public static IReadOnlyList<ApiReviewContractCause> ContractCauses(ApiReviewReport report)
    {
        var rows = new List<ApiReviewContractCause>();
        var rest = report.Targets.Where(t => t.Target.ApiType == ApiReviewTargetType.Rest).ToList();
        var gql = report.Targets.Where(t => t.Target.ApiType == ApiReviewTargetType.GraphQl).ToList();
        if (rest.Count > 0)
            rows.Add(rest.All(t => t.Contract is { Available: true })
                ? new("rest-contract", "REST contract", "Available", null)
                : new("rest-contract", "REST contract", "Unavailable", rest.Any(t => t.Target.ContractSource is not null)
                    ? "The configured OpenAPI contract could not be retrieved." : "No OpenAPI contract configured."));
        if (gql.Count > 0)
        {
            var schema = gql.All(t => t.Contract is { Available: true } || t.GraphQlCompatibility?.SchemaSource is not null and not GraphQlSchemaSource.None);
            rows.Add(schema ? new("graphql-schema", "GraphQL schema", "Available", null) : new("graphql-schema", "GraphQL schema", "Unavailable", SchemaReason(gql)));
            var assessed = gql.Any(t => ApiReviewPresentation.GraphQlCounts(t).CompatibilityAssessed);
            var incompatible = gql.Sum(t => ApiReviewPresentation.GraphQlCounts(t).Incompatible);
            rows.Add(!assessed ? new("compatibility", "Compatibility", "Not assessed", schema ? "No observed operation document was available to validate." : "No usable schema available.")
                : incompatible > 0 ? new("compatibility", "Compatibility", "Incompatible", $"{incompatible} observed operation{(incompatible == 1 ? "" : "s")} do not validate against the schema.")
                : new("compatibility", "Compatibility", "Compatible", null));
        }
        rows.Add(DriftCompared(report)
            ? new("drift", "Drift comparison", report.Findings.Any(f => f.Type == ApiReviewFindingType.Drift) ? "Drift detected" : "Compared — no drift in the compared evidence", null)
            : new("drift", "Drift comparison", "Not available", "No previous comparable baseline."));
        return rows;
    }

    // ── Performance ─────────────────────────────────────────────────────────────────────────────────────────────────────────────

    public static IReadOnlyList<(string Label, string Value)> Performance(ApiReviewReport report)
    {
        var rest = report.Targets.Where(t => t.Target.ApiType == ApiReviewTargetType.Rest).Sum(t => t.Operations.Count(o => o.Executed && o.ElapsedMs is not null));
        var gql = report.Targets.Where(t => t.Target.ApiType == ApiReviewTargetType.GraphQl).Sum(t => t.Operations.Count(o => o.Executed && o.ElapsedMs is not null));
        var facts = new List<(string, string)>
        {
            ("REST requests measured", $"{rest}"),
            ("GraphQL requests measured", $"{gql}"),
            ("Response-time threshold", report.Policy.LatencyPolicyText),
            ("REST payload threshold", report.Policy.RestPayloadThreshold > 0 ? ApiReviewPolicy.Bytes(report.Policy.RestPayloadThreshold) : "Not set (sizes are observational)"),
            ("Coverage limitation", "Sample is too small for representative performance conclusions."),
        };
        if (GraphQlRepresentative(report) is { } representative) facts.Add(("GraphQL representative response timing", representative));
        return facts;
    }

    /// <summary>
    /// "Not assessed — reason" when no representative business query was executed (only the __typename probe); null without GraphQL.
    /// A lightweight query's timing is never presented as business response performance.
    /// </summary>
    public static string? GraphQlRepresentative(ApiReviewReport report)
    {
        var gql = report.Targets.Where(t => t.Target.ApiType == ApiReviewTargetType.GraphQl).ToList();
        if (gql.Count == 0) return null;
        var perf = gql.SelectMany(t => t.Checks).Where(c => c.Area == ApiReviewFindingType.Performance).ToList();
        if (perf.Count > 0 && perf.All(c => c.Result is ApiReviewCheckResult.NotTested or ApiReviewCheckResult.Blocked))
            return $"Not assessed — {perf.First(c => c.Detail.Length > 0).Detail.Replace("Not tested: ", "", StringComparison.Ordinal)}";
        var executed = gql.SelectMany(t => t.Operations.Where(o => o.Executed)).ToList();
        if (executed.Count == 0 || executed.All(IsSystemQuery))
            return "Not assessed — no representative business query was executed.";
        return "Assessed from representative queries";
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private static bool Decided(ApiReviewCheck c) => c.Result is ApiReviewCheckResult.Pass or ApiReviewCheckResult.Warning or ApiReviewCheckResult.Fail;

    private static int Measured(ApiReviewReport report) => report.Targets.Sum(t => t.Operations.Count(o => o.Executed && o.ElapsedMs is not null));

    private static bool DriftCompared(ApiReviewReport report) =>
        ApiReviewEvidencePresentation.Checks(report).Any(c => c.Area == ApiReviewFindingType.Drift && Decided(c));

    private static bool IsSafe(ApiReviewOperationResult o) => o.OperationType != GraphQlOperationType.None
        ? o.OperationType == GraphQlOperationType.Query : o.Method is "GET" or "HEAD" or "OPTIONS";

    public static bool IsSystemQuery(ApiReviewOperationResult o) => o.Display.Contains("__typename", StringComparison.Ordinal);

    private static string NotExecutedReason(IReadOnlyList<ApiReviewTargetResult> targets) =>
        targets.Select(t => t.RequiredAction ?? t.AccessReason).FirstOrDefault(r => !string.IsNullOrWhiteSpace(r)) ?? "No request could be executed.";

    private static string SchemaReason(IReadOnlyList<ApiReviewTargetResult> gql) =>
        gql.Select(t => t.Contract?.Note).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) is { } note
            ? $"Runtime schema could not be retrieved ({note})."
            : "Runtime schema could not be retrieved.";

    private static string ContractsReason(ApiReviewReport report) =>
        string.Join(" ", Limitations(report).Where(l => l.Key is "rest-contract" or "graphql-schema" or "baseline").Select(l => l.Title + "."));

    private static string AccessUsed(IReadOnlyList<ApiReviewTargetResult> targets) =>
        targets.Any(t => t.AccessMode == ApiReviewAccessMode.AuthenticatedHttp) ? "Authenticated access used"
        : targets.Any(t => t.AccessMode == ApiReviewAccessMode.PublicHttp) ? "Public access" : "Not executed";
}
