using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BirkNext.Api.Services.ContractAnalysis;
using BirkNext.Api.Services.LocalHttpsProxy;
using BirkNext.ApiReview;
using BirkNext.LocalHttpsProxy;

namespace BirkNext.Api.Services.ApiQuality;

public interface IApiReviewEngine
{
    Task<ApiReviewReport> RunAsync(ApiReviewRunRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// API Quality Review engine. API-centric (one REST service or GraphQL endpoint at a time), read-only (GET/HEAD/OPTIONS, GraphQL
/// queries), fail-fast on missing authentication context, and structural about response evidence (JSON paths/types, never values).
/// Public targets use the anonymous HTTP client; authenticated targets go through <see cref="IAuthenticatedReviewGateway"/> so the
/// engine never sees a token. Endpoint paths come from the request (Endpoint Discovery / configuration / contract) — nothing is guessed.
/// </summary>
public sealed class ApiReviewEngine(HttpClient publicClient, IAuthenticatedReviewGateway gateway, IOpenApiExtractor openApi, IGraphQlExtractor graphQl, ILogger<ApiReviewEngine> logger,
    IGraphQlSchemaArtifactStore? schemaArtifacts = null) : IApiReviewEngine
{
    public const string TypenameProbe = "query { __typename }";
    public const string InvalidFieldProbe = "query { __birkNextUnknownFieldProbe }";
    private const string UnknownRouteSegment = "birknext-unknown-route-probe-7f3a";
    private const long MaxPublicBodyBytes = 1024 * 1024;
    private const int MaxOperationsPerTarget = 40;

    private sealed record Exec(bool Executed, ApiReviewAccessMode Mode, int StatusCode, string? ContentType, double? ElapsedMs, long? ContentLength,
        IReadOnlyDictionary<string, string> Headers, IReadOnlyList<JsonShapeEntry> Shape, IReadOnlyList<string> Leaks, bool ProblemDetails, bool? JsonValid,
        int? GraphQlErrors, bool? GraphQlHasData, string Message, bool Timeout = false);

    public async Task<ApiReviewReport> RunAsync(ApiReviewRunRequest request, CancellationToken cancellationToken = default)
    {
        if (!publicClient.DefaultRequestHeaders.Contains(NetworkEvidencePolicy.ProvenanceHeader)) publicClient.DefaultRequestHeaders.TryAddWithoutValidation(NetworkEvidencePolicy.ProvenanceHeader, "BirkNextDiagnostic");
        var startedAt = DateTimeOffset.UtcNow;
        var capabilities = gateway.Resolve(request.Identity);
        var results = new List<ApiReviewTargetResult>();
        var findings = new List<ApiReviewFinding>();
        var limitations = new List<string>
        {
            "Automated API review is read-only: REST GET/HEAD/OPTIONS and GraphQL queries only. Write operations, role-based authorization and business correctness need manual review.",
            "Response bodies are parsed transiently for structural contract validation; only JSON paths and types are recorded, never values.",
            "GraphQL operation names are client-defined; mapping to schema root fields is a normalized heuristic and unmatched operations are marked Manual Review.",
        };
        if (request.Environment.IsProduction) limitations.Add("Production policy: passive read-only review; unknown-route/invalid-query error probes are disabled.");
        var manual = new List<string>
        {
            "Write operations (POST/PUT/PATCH/DELETE, GraphQL mutations): behaviour, idempotency and side effects.",
            "Access control between roles/tenants.",
            "Business correctness of returned data.",
        };

        foreach (var target in request.Targets.Where(t => t.Selected).Take(25))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var baseline = request.Baselines.FirstOrDefault(b => b.TargetId == target.TargetId);
            var (mode, reason, action) = ResolveAccess(target, request, capabilities);
            try
            {
                var result = target.ApiType == ApiReviewTargetType.GraphQl
                    ? await ReviewGraphQlAsync(target, request, mode, reason, action, baseline, findings, cancellationToken)
                    : await ReviewRestAsync(target, request, mode, reason, action, baseline, findings, cancellationToken);
                results.Add(result);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                results.Add(new ApiReviewTargetResult { Target = target, AccessMode = mode, AccessReason = reason, Status = ApiReviewTargetStatus.NotTested, RequiredAction = "The target did not answer within the review timeout.", FindingCount = null });
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or JsonException)
            {
                logger.LogWarning("API review of {Target} failed with {ExceptionType}.", target.Url, ex.GetType().Name);
                results.Add(new ApiReviewTargetResult { Target = target, AccessMode = mode, AccessReason = reason, Status = ApiReviewTargetStatus.NotTested, RequiredAction = $"Review could not complete: {ex.GetType().Name}.", FindingCount = null });
            }
        }
        if (request.Targets.Any(t => t.Selected && t.Operations.Any(o => !o.IsSafe)))
            manual.Add($"{request.Targets.Where(t => t.Selected).SelectMany(t => t.Operations).Count(o => !o.IsSafe)} observed write operation(s) were listed but not executed.");

        return new ApiReviewReport
        {
            Environment = request.Environment, Policy = request.Policy, StartedAt = startedAt, GeneratedAt = DateTimeOffset.UtcNow, Access = capabilities,
            Targets = results, Findings = findings.OrderBy(f => f.Severity).ThenBy(f => f.Type).ThenBy(f => f.Endpoint).ToList(),
            Coverage = Coverage(results, request), ManualReviewItems = manual, Limitations = limitations,
        };
    }

    // ── Access resolution (fail fast) ───────────────────────────────────────────

    private static (ApiReviewAccessMode Mode, string Reason, string? Action) ResolveAccess(ApiReviewTarget target, ApiReviewRunRequest request, AuthenticatedReviewCapabilities capabilities)
    {
        if (!string.Equals(target.Scheme, "https", StringComparison.OrdinalIgnoreCase) && target.AuthRequired)
            return (ApiReviewAccessMode.Blocked, "Authenticated review requires HTTPS; the target is not served over TLS.", "Serve the API over HTTPS.");
        if (!target.AuthRequired)
            return (ApiReviewAccessMode.PublicHttp, "Public HTTP: no authentication observed or configured for this API.", null);
        return request.Environment.AuthenticatedTestingMethod switch
        {
            AuthenticatedTestingMethod.LocalHttpsProxy when capabilities.AuthenticatedApi =>
                (ApiReviewAccessMode.AuthenticatedHttp, "Authenticated HTTP via the Local HTTPS proxy context (read-only, executed by the backend gateway).", null),
            AuthenticatedTestingMethod.LocalHttpsProxy =>
                (ApiReviewAccessMode.Unavailable, capabilities.ContextStatus == AuthenticatedApiContextStatus.Expired ? "Authenticated API context expired." : "Authenticated API context not available.",
                    "Start the Local HTTPS Proxy and sign in through your normal managed Edge, then run the review again."),
            AuthenticatedTestingMethod.ManualOnly =>
                (ApiReviewAccessMode.ManualOnly, "Manual verification only: automated authenticated API review is unavailable for this environment.", "Verify the authenticated API manually or switch the Target Environment to the Local HTTPS proxy method."),
            _ => (ApiReviewAccessMode.Unavailable, "The Managed Edge (CDP) method provides no authenticated API execution.", "Switch the Target Environment's authenticated testing method to Local HTTPS proxy."),
        };
    }

    // ── REST ────────────────────────────────────────────────────────────────────

    private async Task<ApiReviewTargetResult> ReviewRestAsync(ApiReviewTarget target, ApiReviewRunRequest request, ApiReviewAccessMode mode, string reason, string? action,
        ApiReviewBaseline? baseline, List<ApiReviewFinding> findings, CancellationToken ct)
    {
        var targetFindings = new List<ApiReviewFinding>();
        var checks = new List<ApiReviewCheck>();
        var operations = new List<ApiReviewOperationResult>();
        var shapes = new Dictionary<string, List<JsonShapeEntry>>();
        var statuses = new Dictionary<string, int>();

        // Contract (OpenAPI) is public metadata; fetched anonymously so the review knows the documented operations before probing.
        OpenApiReviewResult? contractReview = null;
        NormalizedContract? contract = null;
        ApiReviewContractSummary? contractSummary = null;
        if (!string.IsNullOrWhiteSpace(target.ContractSource))
        {
            (contractReview, contract, contractSummary) = await ReviewOpenApiAsync(target, ct);
            if (contractReview is not null) { checks.AddRange(contractReview.Checks); targetFindings.AddRange(contractReview.Findings); }
        }

        if (mode is ApiReviewAccessMode.Unavailable or ApiReviewAccessMode.ManualOnly or ApiReviewAccessMode.Blocked)
        {
            // Fail fast: nothing is executed, the contract review (if any) stands on its own, and the target is Blocked — not "no findings".
            findings.AddRange(targetFindings);
            return new ApiReviewTargetResult
            {
                Target = target, AccessMode = mode, AccessReason = reason, RequiredAction = action, Status = ApiReviewTargetStatus.Blocked, Contract = contractSummary, Checks = checks,
                Operations = target.Operations.Select(o => new ApiReviewOperationResult { Display = o.Display, Method = o.Method, Path = o.Path, AccessMode = mode, Executed = false, Result = ApiReviewCheckResult.Blocked, Note = reason }).ToList(),
                FindingCount = contractReview is null ? null : targetFindings.Count, Baseline = null,
            };
        }

        var safeOps = target.Operations.Where(o => o.IsSafe && o.OperationType == GraphQlOperationType.None).GroupBy(o => $"{o.Method} {o.Path}").Select(g => g.First()).Take(MaxOperationsPerTarget).ToList();
        var unsafeOps = target.Operations.Where(o => !o.IsSafe).ToList();
        if (safeOps.Count == 0) safeOps.Add(new ApiReviewOperation { Method = "GET", Path = target.BasePath, Source = target.Source, Confidence = target.Confidence });
        Exec? primary = null;
        foreach (var op in safeOps)
        {
            var url = $"{target.Origin}{op.Path}";
            var exec = await ExecuteRestAsync(request, mode, op.Method == "HEAD" || op.Method == "OPTIONS" ? "GET" : op.Method, url, ct);
            primary ??= exec.Executed ? exec : null;
            var opChecks = new List<ApiReviewCheck>();
            var key = $"{op.Method} {op.Path}";
            var display = key;
            if (!exec.Executed)
            {
                operations.Add(new ApiReviewOperationResult { Display = display, Method = op.Method, Path = op.Path, AccessMode = mode, Executed = false, Result = exec.Timeout ? ApiReviewCheckResult.NotTested : ApiReviewCheckResult.Blocked, Note = exec.Message });
                if (!exec.Timeout) findings.Add(Finding(target, "rest-not-executed", ApiReviewSeverity.Medium, ApiReviewFindingType.Rest, display, "Reachability", "Operation could not be executed", exec.Message, "Check reachability and the authenticated context.", [], ApiReviewCheckResult.Blocked));
                continue;
            }
            statuses[key] = exec.StatusCode;
            if (exec.Shape.Count > 0) shapes[key] = exec.Shape.ToList();

            // Status semantics
            var statusResult = exec.StatusCode switch
            {
                >= 200 and < 300 => ApiReviewCheckResult.Pass,
                401 or 403 when mode == ApiReviewAccessMode.AuthenticatedHttp => ApiReviewCheckResult.Fail,
                401 or 403 => ApiReviewCheckResult.Warning,
                404 => ApiReviewCheckResult.Warning,
                >= 500 => ApiReviewCheckResult.Fail,
                _ => ApiReviewCheckResult.Warning,
            };
            opChecks.Add(Check("rest-status", ApiReviewFindingType.Rest, "Status code", statusResult, $"HTTP {exec.StatusCode} via {Label(mode)}."));
            if (exec.StatusCode >= 500)
                Add(targetFindings, findings, Finding(target, "rest-5xx", ApiReviewSeverity.High, ApiReviewFindingType.Rest, display, "Status code", $"Server error HTTP {exec.StatusCode}", "A safe GET returned a server error.", "Investigate server logs for this operation.", [$"HTTP {exec.StatusCode}"]));
            else if (exec.StatusCode is 401 or 403 && mode == ApiReviewAccessMode.AuthenticatedHttp)
                Add(targetFindings, findings, Finding(target, "rest-auth-rejected", ApiReviewSeverity.Medium, ApiReviewFindingType.AccessControl, display, "Authentication", $"Authenticated request rejected (HTTP {exec.StatusCode})", "The in-memory credential was rejected; the API audience or scopes may differ from the observed traffic.", "Verify the API audience/scopes; the review cannot assess this operation's contract.", [$"HTTP {exec.StatusCode}"], ApiReviewCheckResult.Warning));
            else if (exec.StatusCode is 401 or 403 && mode == ApiReviewAccessMode.PublicHttp && op.AuthObserved)
                opChecks.Add(Check("rest-auth-enforced", ApiReviewFindingType.AccessControl, "Authentication enforced without credential", ApiReviewCheckResult.Pass, $"HTTP {exec.StatusCode} without credential, as expected for an authenticated endpoint."));
            else if (exec.StatusCode is >= 200 and < 300 && mode == ApiReviewAccessMode.PublicHttp && op.AuthObserved)
                Add(targetFindings, findings, Finding(target, "rest-unexpectedly-public", ApiReviewSeverity.High, ApiReviewFindingType.AccessControl, display, "Authentication", "Endpoint answers without authentication although traffic carried a bearer", "The observed traffic used a bearer token, yet an anonymous request succeeded.", "Confirm whether the endpoint is intentionally public; otherwise enforce authentication.", [$"HTTP {exec.StatusCode} without credential"]));

            // Content type & JSON validity
            var json = JsonBodyInspector.IsJsonMediaType(exec.ContentType);
            opChecks.Add(Check("rest-content-type", ApiReviewFindingType.Rest, "JSON content type", exec.StatusCode == 204 || op.Method == "HEAD" ? ApiReviewCheckResult.NotApplicable : json ? ApiReviewCheckResult.Pass : ApiReviewCheckResult.Warning, exec.ContentType ?? "no Content-Type"));
            if (exec.StatusCode is >= 200 and < 300 && !json && exec.StatusCode != 204 && exec.ContentLength is > 0)
                Add(targetFindings, findings, Finding(target, "rest-non-json", ApiReviewSeverity.Medium, ApiReviewFindingType.Rest, display, "Content type", "Success response is not JSON", $"Content-Type: {exec.ContentType ?? "absent"}. An HTML response usually means the API host serves the SPA shell for unknown API routes.", "Return application/json for API operations (or application/problem+json for errors).", [$"Content-Type: {exec.ContentType ?? "absent"}"], ApiReviewCheckResult.Warning));
            if (json && exec.JsonValid == false)
                Add(targetFindings, findings, Finding(target, "rest-invalid-json", ApiReviewSeverity.High, ApiReviewFindingType.Rest, display, "JSON validity", "Response claims JSON but does not parse", "The body could not be parsed as JSON.", "Fix serialization; clients will fail to parse the response.", [$"Content-Type: {exec.ContentType}"]));
            opChecks.Add(Check("rest-json-valid", ApiReviewFindingType.Rest, "JSON parses", exec.JsonValid is null ? ApiReviewCheckResult.NotApplicable : exec.JsonValid.Value ? ApiReviewCheckResult.Pass : ApiReviewCheckResult.Fail, exec.JsonValid is null ? "No JSON body." : exec.JsonValid.Value ? $"{exec.Shape.Count} structural path(s)." : "Invalid JSON."));

            // Latency & payload
            var latency = exec.ElapsedMs ?? 0;
            var latencyResult = request.Policy.LatencyResult(latency);
            opChecks.Add(Check("rest-latency", ApiReviewFindingType.Performance, "Response time", latencyResult, $"{latency:0} ms ({request.Policy.LatencyPolicyText})."));
            if (latencyResult != ApiReviewCheckResult.Pass)
                Add(targetFindings, findings, Finding(target, "rest-slow", latencyResult == ApiReviewCheckResult.Fail ? ApiReviewSeverity.Medium : ApiReviewSeverity.Low, ApiReviewFindingType.Performance, display, "Response time", $"Slow response: {latency:0} ms", $"Single-sample latency of the review request ({request.Policy.LatencyPolicyText}). Page-level API impact is reviewed by Performance Quality.", "Profile the endpoint server-side.", [$"Observed: {latency:0} ms", $"Policy: {request.Policy.LatencyPolicyText}"], latencyResult == ApiReviewCheckResult.Fail ? ApiReviewCheckResult.Fail : ApiReviewCheckResult.Warning));
            if (request.Policy.RestPayloadResult(exec.ContentLength) == ApiReviewCheckResult.Warning && exec.ContentLength is { } size)
                Add(targetFindings, findings, Finding(target, "rest-large-payload", ApiReviewSeverity.Low, ApiReviewFindingType.Performance, display, "Payload size", $"Large response payload ({size / 1024} KB)", "The response exceeds the large-payload threshold; check pagination.", "Paginate or filter the collection.", [$"Bytes: {size}", $"Threshold: {ApiReviewPolicy.Bytes(request.Policy.RestPayloadThreshold)}"], ApiReviewCheckResult.Warning));
            opChecks.Add(Check("rest-payload", ApiReviewFindingType.Performance, "Payload size", request.Policy.RestPayloadResult(exec.ContentLength),
                $"{(exec.ContentLength ?? 0).ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} bytes (REST payload warning > {ApiReviewPolicy.Bytes(request.Policy.RestPayloadThreshold)})."));

            // Pagination hints on collection responses (structural: items/totalCount or top-level array)
            var paths = exec.Shape.Select(s => s.Path).ToHashSet(StringComparer.Ordinal);
            var isCollection = paths.Contains("$[*]") || paths.Contains("$.items[*]") || paths.Contains("$.data[*]") || paths.Contains("$.results[*]") || paths.Contains("$.value[*]");
            var pagedShape = paths.Any(p => p is "$.totalCount" or "$.total" or "$.pageSize" or "$.page" or "$.nextCursor" or "$.continuationToken" or "$.hasMore" or "$.pageInfo" or "$.count" or "$.@odata.nextLink" or "$.@odata.count");
            opChecks.Add(Check("rest-pagination", ApiReviewFindingType.Rest, "Pagination pattern", !isCollection ? ApiReviewCheckResult.NotApplicable : pagedShape || !paths.Contains("$[*]") ? ApiReviewCheckResult.Pass : ApiReviewCheckResult.Warning,
                !isCollection ? "Not a collection response." : pagedShape ? "Paging metadata present." : paths.Contains("$[*]") ? "Top-level array without paging metadata." : "Wrapped collection."));
            if (isCollection && paths.Contains("$[*]") && !pagedShape && contract is null && exec.ContentLength is > 64 * 1024)
                Add(targetFindings, findings, Finding(target, "rest-unbounded-collection", ApiReviewSeverity.Low, ApiReviewFindingType.Rest, display, "Pagination", "Large top-level array without pagination metadata", "A large bare-array response suggests an unbounded collection.", "Introduce paging (page/pageSize, limit/offset or cursor) and expose totals.", [$"Bytes: {exec.ContentLength}"], ApiReviewCheckResult.Warning));

            // Contract validation
            bool? contractMatched = null;
            if (contract is not null)
            {
                var operation = contract.Operations.FirstOrDefault(c => string.Equals(c.Method, op.Method, StringComparison.OrdinalIgnoreCase) && (OpenApiDocumentReview.TemplateMatches(c.Path, op.Path) || OpenApiDocumentReview.TemplateMatches(c.Path, TrimBase(op.Path, target.BasePath)) || OpenApiDocumentReview.TemplateMatches(target.BasePath.TrimEnd('/') + c.Path, op.Path)));
                if (operation is null)
                {
                    contractMatched = false;
                    Add(targetFindings, findings, Finding(target, "contract-undocumented-operation", ApiReviewSeverity.Medium, ApiReviewFindingType.Contract, display, "Operation documented", "Observed operation is not in the OpenAPI contract", "The endpoint was observed in traffic but no path template in the contract matches it.", "Document the operation or remove the undocumented endpoint.", [$"Observed: {display}", $"Contract: {contractReview?.Operations.Count ?? 0} operations"], ApiReviewCheckResult.Warning, ApiReviewDriftClassification.Informational));
                    opChecks.Add(Check("contract-operation", ApiReviewFindingType.Contract, "Operation in contract", ApiReviewCheckResult.Warning, "Not documented."));
                }
                else
                {
                    contractMatched = true;
                    opChecks.Add(Check("contract-operation", ApiReviewFindingType.Contract, "Operation in contract", ApiReviewCheckResult.Pass, $"{operation.Method} {operation.Path}"));
                    if (exec.StatusCode is >= 200 and < 300 && exec.Shape.Count > 0)
                    {
                        var contractFindings = ContractValidation.ValidateAgainstSchema(operation, exec.StatusCode.ToString(), exec.Shape, target.TargetId, display);
                        foreach (var f in contractFindings) Add(targetFindings, findings, f);
                        opChecks.Add(Check("contract-shape", ApiReviewFindingType.Contract, "Response matches contract schema", contractFindings.Any(f => f.Severity <= ApiReviewSeverity.High) ? ApiReviewCheckResult.Fail : contractFindings.Count > 0 ? ApiReviewCheckResult.Warning : ApiReviewCheckResult.Pass, $"{contractFindings.Count} structural difference(s)."));
                    }
                    else opChecks.Add(Check("contract-shape", ApiReviewFindingType.Contract, "Response matches contract schema", ApiReviewCheckResult.NotTested, "No JSON success body to compare."));
                }
            }

            // Drift versus previous run
            if (baseline is not null && baseline.OperationShapes.TryGetValue(key, out var previousShape) && exec.Shape.Count > 0 && exec.StatusCode is >= 200 and < 300)
            {
                var drift = ContractValidation.Drift(key, previousShape, exec.Shape, target.TargetId, display);
                foreach (var f in drift) Add(targetFindings, findings, f);
                opChecks.Add(Check("drift-shape", ApiReviewFindingType.Drift, "No drift since previous review", drift.Any(f => f.Drift == ApiReviewDriftClassification.Breaking) ? ApiReviewCheckResult.Fail : drift.Count > 0 ? ApiReviewCheckResult.Warning : ApiReviewCheckResult.Pass, $"{drift.Count} change(s) vs {baseline.RecordedAt:yyyy-MM-dd}."));
                if (baseline.OperationStatuses.TryGetValue(key, out var previousStatus) && previousStatus != exec.StatusCode)
                    Add(targetFindings, findings, Finding(target, "drift-status-change", ApiReviewSeverity.Medium, ApiReviewFindingType.Drift, display, "Drift", $"Response status changed {previousStatus} → {exec.StatusCode}", "The operation answered a different status than in the previous review.", "Verify the change is intentional.", [$"Previous: {previousStatus}", $"Current: {exec.StatusCode}"], ApiReviewCheckResult.Warning, ApiReviewDriftClassification.PotentiallyBreaking));
            }

            // Cache headers on GET
            var cache = exec.Headers.TryGetValue("cache-control", out var cc) ? cc : null;
            opChecks.Add(Check("rest-cache-control", ApiReviewFindingType.Rest, "Cache-Control present", op.Method != "GET" ? ApiReviewCheckResult.NotApplicable : cache is null ? ApiReviewCheckResult.Warning : ApiReviewCheckResult.Pass, cache ?? "absent"));

            // Error leakage on any response
            if (exec.Leaks.Count > 0)
                Add(targetFindings, findings, Finding(target, "errors-leak", ApiReviewSeverity.High, ApiReviewFindingType.Errors, display, "Error leakage", "Response body contains internal error details", "Stack traces, exception types, file paths or connection details were detected in the response body (content redacted).", "Return ProblemDetails without internal details; log the exception server-side.", exec.Leaks.Select(l => $"Indicator: {l}").ToList()));

            var overall = Worst(opChecks.Select(c => c.Result).Where(r => r is not (ApiReviewCheckResult.NotApplicable or ApiReviewCheckResult.NotTested)));
            operations.Add(new ApiReviewOperationResult
            {
                Display = display, Method = op.Method, Path = op.Path, AccessMode = mode, Executed = true, StatusCode = exec.StatusCode, ContentType = exec.ContentType, ElapsedMs = exec.ElapsedMs,
                ContentLength = exec.ContentLength, Result = overall, Checks = opChecks, ContractMatched = contractMatched, ShapeEntryCount = exec.Shape.Count,
            });
        }
        foreach (var op in unsafeOps.Take(20))
            operations.Add(new ApiReviewOperationResult { Display = op.Display, Method = op.Method, Path = op.Path, AccessMode = mode, Executed = false, Result = ApiReviewCheckResult.ManualReview, Note = "Write operation observed; not executed by the automated review (read-only policy)." });

        // Service-level: security headers, CORS, error handling probes
        if (primary is not null)
        {
            checks.AddRange(SecurityChecks(target, primary, targetFindings, findings));
            checks.AddRange(await CorsChecksAsync(target, request, mode, primary, targetFindings, findings, ct));
            if (request.Policy.ErrorHandlingProbes && !request.Environment.IsProduction)
                checks.AddRange(await RestErrorHandlingProbesAsync(target, request, mode, targetFindings, findings, ct));
            else checks.Add(Check("errors-unknown-route", ApiReviewFindingType.Errors, "Unknown route handling", ApiReviewCheckResult.NotTested, "Error probes disabled by policy."));
            var rateLimit = primary.Headers.Keys.Any(k => k.StartsWith("x-ratelimit", StringComparison.OrdinalIgnoreCase) || k.StartsWith("ratelimit", StringComparison.OrdinalIgnoreCase) || k == "retry-after");
            checks.Add(Check("rest-rate-limit-headers", ApiReviewFindingType.Security, "Rate-limit headers", rateLimit ? ApiReviewCheckResult.Pass : ApiReviewCheckResult.ManualReview, rateLimit ? "Rate-limit headers exposed." : "No rate-limit headers observed; rate limiting is not load-tested here (informational)."));
            // Rule-based, no threshold: compression is expected when the request advertised it. Only the public request advertises
            // gzip/br; the authenticated gateway request does not (it reads the body for structure), so an uncompressed answer there
            // says nothing about the server and is not assessed. The rule does not consider payload size (no minimum-size rule exists).
            checks.Add(primary.Mode == ApiReviewAccessMode.AuthenticatedHttp && !primary.Headers.ContainsKey("content-encoding")
                ? Check("rest-compression", ApiReviewFindingType.Performance, "Compression", ApiReviewCheckResult.NotTested, "Not assessed: the authenticated gateway request does not advertise Accept-Encoding, so an uncompressed response is expected.")
                : Check("rest-compression", ApiReviewFindingType.Performance, "Compression", primary.Headers.ContainsKey("content-encoding") ? ApiReviewCheckResult.Pass : ApiReviewCheckResult.Warning,
                    primary.Headers.TryGetValue("content-encoding", out var enc) ? $"Content-Encoding: {enc}" : "Compression not observed: the request advertised gzip/br, but the response was not encoded."));
        }
        else checks.Add(Check("rest-reachability", ApiReviewFindingType.Rest, "Reachability", ApiReviewCheckResult.Fail, "No operation could be executed."));

        // GraphQL-independent drift for the contract hash
        if (baseline?.ContractHash is { } previousHash && contractReview is not null && previousHash != contractReview.Hash)
            Add(targetFindings, findings, Finding(target, "drift-contract-changed", ApiReviewSeverity.Info, ApiReviewFindingType.Drift, target.ContractSource ?? target.Url, "Contract version", "OpenAPI contract changed since the previous review", $"Operation count {baseline.ContractOperationCount} → {contractReview.Operations.Count}.", "Review the contract diff and re-validate consumers.", [$"Previous hash: {previousHash}", $"Current hash: {contractReview.Hash}"], ApiReviewCheckResult.Warning, ApiReviewDriftClassification.Informational));

        var executed = operations.Count(o => o.Executed);
        var status = executed == 0 ? ApiReviewTargetStatus.NotTested : executed < safeOps.Count ? ApiReviewTargetStatus.PartiallyCompleted : ApiReviewTargetStatus.Completed;
        return new ApiReviewTargetResult
        {
            Target = target, AccessMode = mode, AccessReason = reason, Status = status, Operations = operations, Contract = contractSummary, Checks = checks, FindingCount = executed == 0 && contractReview is null ? null : targetFindings.Count,
            Baseline = new ApiReviewBaseline { TargetId = target.TargetId, RecordedAt = DateTimeOffset.UtcNow, ContractHash = contractReview?.Hash, ContractOperationCount = contractReview?.Operations.Count, OperationShapes = shapes, OperationStatuses = statuses },
        };
    }

    private async Task<(OpenApiReviewResult? Review, NormalizedContract? Contract, ApiReviewContractSummary Summary)> ReviewOpenApiAsync(ApiReviewTarget target, CancellationToken ct)
    {
        var source = target.ContractSource!;
        try
        {
            using var response = await publicClient.GetAsync(source, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
                return (null, null, new ApiReviewContractSummary { Kind = "OpenAPI", Source = source, Available = false, Status = response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden ? ApiReviewCheckResult.Blocked : ApiReviewCheckResult.Fail, Note = $"Contract fetch returned HTTP {(int)response.StatusCode}." });
            var json = await ReadBoundedTextAsync(response, 10 * 1024 * 1024, ct);
            var review = OpenApiDocumentReview.Review(json, source, target.TargetId);
            var extraction = review.Valid && review.Version is { } v && v.StartsWith("3.", StringComparison.Ordinal) ? openApi.Extract(json) : null;
            var summary = new ApiReviewContractSummary
            {
                Kind = "OpenAPI", Source = source, Available = review.Valid, Status = review.Valid ? ApiReviewCheckResult.Pass : ApiReviewCheckResult.Fail, Version = review.Version, Title = review.Title,
                OperationCount = review.Operations.Count, TypeCount = extraction?.Contract?.Schemas.Count ?? 0, Hash = review.Hash,
                Note = review.Valid ? (extraction is { Success: true } ? $"{review.Operations.Count} operations, {extraction.Contract!.Schemas.Count} schemas parsed for live validation." : $"{review.Operations.Count} operations; response schemas not parsed ({extraction?.ErrorMessage ?? "Swagger 2.0 documents are reviewed for documentation quality only"}).") : review.Error ?? "Invalid document.",
            };
            return (review, extraction is { Success: true } ? extraction.Contract : null, summary);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return (null, null, new ApiReviewContractSummary { Kind = "OpenAPI", Source = source, Available = false, Status = ApiReviewCheckResult.NotTested, Note = $"Contract not reachable ({ex.GetType().Name})." });
        }
    }

    private static string TrimBase(string path, string basePath) => basePath.Length > 1 && path.StartsWith(basePath, StringComparison.OrdinalIgnoreCase) ? path[basePath.Length..] : path;

    private List<ApiReviewCheck> SecurityChecks(ApiReviewTarget target, Exec primary, List<ApiReviewFinding> targetFindings, List<ApiReviewFinding> findings)
    {
        var checks = new List<ApiReviewCheck>();
        var h = primary.Headers;
        var https = string.Equals(target.Scheme, "https", StringComparison.OrdinalIgnoreCase);
        checks.Add(Check("sec-tls", ApiReviewFindingType.Security, "TLS", https ? ApiReviewCheckResult.Pass : ApiReviewCheckResult.Fail, https ? "HTTPS" : "Plain HTTP"));
        if (!https)
            Add(targetFindings, findings, Finding(target, "sec-no-tls", ApiReviewSeverity.High, ApiReviewFindingType.Security, target.Origin, "TLS", "API served over plain HTTP", "Unencrypted API traffic exposes tokens and data.", "Serve the API over HTTPS only.", [target.Origin]));
        var hsts = h.ContainsKey("strict-transport-security");
        checks.Add(Check("sec-hsts", ApiReviewFindingType.Security, "Strict-Transport-Security", !https ? ApiReviewCheckResult.NotApplicable : hsts ? ApiReviewCheckResult.Pass : ApiReviewCheckResult.Warning, hsts ? h["strict-transport-security"] : "absent"));
        if (https && !hsts)
            Add(targetFindings, findings, Finding(target, "sec-no-hsts", ApiReviewSeverity.Low, ApiReviewFindingType.Security, target.Origin, "HSTS", "HSTS header missing on API responses", "Strict-Transport-Security is absent; browsers will not pin HTTPS for this host.", "Add Strict-Transport-Security with a long max-age.", [], ApiReviewCheckResult.Warning));
        var xcto = h.TryGetValue("x-content-type-options", out var x) && x.Contains("nosniff", StringComparison.OrdinalIgnoreCase);
        checks.Add(Check("sec-xcto", ApiReviewFindingType.Security, "X-Content-Type-Options: nosniff", xcto ? ApiReviewCheckResult.Pass : ApiReviewCheckResult.Warning, xcto ? "nosniff" : "absent"));
        if (!xcto)
            Add(targetFindings, findings, Finding(target, "sec-no-xcto", ApiReviewSeverity.Low, ApiReviewFindingType.Security, target.Origin, "Content type sniffing", "X-Content-Type-Options missing", "Without nosniff, browsers may MIME-sniff API responses.", "Add X-Content-Type-Options: nosniff.", [], ApiReviewCheckResult.Warning));
        var cache = h.TryGetValue("cache-control", out var cc) ? cc : null;
        checks.Add(Check("sec-cache-control", ApiReviewFindingType.Security, "Cache-Control on API responses", cache is null ? ApiReviewCheckResult.Warning : ApiReviewCheckResult.Pass, cache ?? "absent"));
        if (cache is null)
            Add(targetFindings, findings, Finding(target, "sec-no-cache-control", ApiReviewSeverity.Low, ApiReviewFindingType.Security, target.Origin, "Cache-Control", "No Cache-Control on API responses", "Authenticated API data without Cache-Control may be cached by intermediaries.", "Send Cache-Control: no-store (or explicit private caching) on API responses.", [], ApiReviewCheckResult.Warning));
        var server = h.TryGetValue("server", out var sv) ? sv : h.TryGetValue("x-powered-by", out var xp) ? xp : null;
        checks.Add(Check("sec-server-disclosure", ApiReviewFindingType.Security, "Server technology disclosure", server is null ? ApiReviewCheckResult.Pass : ApiReviewCheckResult.Warning, server is null ? "No Server/X-Powered-By header." : $"Server header present: {server}"));
        if (server is not null)
            Add(targetFindings, findings, Finding(target, "sec-server-disclosure", ApiReviewSeverity.Info, ApiReviewFindingType.Security, target.Origin, "Server header", "Server technology disclosed", "The Server / X-Powered-By header reveals implementation details (informational).", "Remove or neutralize the header.", [$"Header value: {server}"], ApiReviewCheckResult.Warning));
        return checks;
    }

    private async Task<List<ApiReviewCheck>> CorsChecksAsync(ApiReviewTarget target, ApiReviewRunRequest request, ApiReviewAccessMode mode, Exec primary, List<ApiReviewFinding> targetFindings, List<ApiReviewFinding> findings, CancellationToken ct)
    {
        var checks = new List<ApiReviewCheck>();
        var h = new Dictionary<string, string>(primary.Headers, StringComparer.OrdinalIgnoreCase);
        // Preflight probe (OPTIONS with Origin) is always anonymous: browsers never send credentials on preflight, so this is safe and representative.
        var origin = request.FrontendOrigin ?? request.Environment.TargetUrl;
        if (Uri.TryCreate(origin, UriKind.Absolute, out var originUri))
        {
            try
            {
                using var preflight = new HttpRequestMessage(HttpMethod.Options, $"{target.Origin}{target.BasePath}");
                preflight.Headers.TryAddWithoutValidation("Origin", originUri.GetLeftPart(UriPartial.Authority));
                preflight.Headers.TryAddWithoutValidation("Access-Control-Request-Method", "GET");
                preflight.Headers.TryAddWithoutValidation("Access-Control-Request-Headers", "authorization, content-type");
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct); cts.CancelAfter(TimeSpan.FromSeconds(10));
                using var response = await publicClient.SendAsync(preflight, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                foreach (var name in new[] { "access-control-allow-origin", "access-control-allow-credentials", "access-control-allow-methods", "access-control-allow-headers" })
                    if (response.Headers.TryGetValues(name, out var values)) h[name] = string.Join(", ", values);
                checks.Add(Check("cors-preflight", ApiReviewFindingType.Security, "CORS preflight", (int)response.StatusCode < 400 ? ApiReviewCheckResult.Pass : ApiReviewCheckResult.Warning, $"OPTIONS → HTTP {(int)response.StatusCode} for Origin {originUri.GetLeftPart(UriPartial.Authority)}."));
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                checks.Add(Check("cors-preflight", ApiReviewFindingType.Security, "CORS preflight", ApiReviewCheckResult.NotTested, $"Preflight probe failed ({ex.GetType().Name})."));
            }
        }
        var allowOrigin = h.TryGetValue("access-control-allow-origin", out var ao) ? ao.Trim() : null;
        var credentials = h.TryGetValue("access-control-allow-credentials", out var acc) && acc.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
        var methods = h.TryGetValue("access-control-allow-methods", out var am) ? am : null;
        var headers = h.TryGetValue("access-control-allow-headers", out var ah) ? ah : null;
        var evidence = new List<string> { $"Access-Control-Allow-Origin: {allowOrigin ?? "absent"}", $"Access-Control-Allow-Credentials: {(credentials ? "true" : "absent/false")}" };
        if (methods is not null) evidence.Add($"Access-Control-Allow-Methods: {methods}");
        if (headers is not null) evidence.Add($"Access-Control-Allow-Headers: {headers}");
        if (allowOrigin == "*" && credentials)
        {
            checks.Add(Check("cors-policy", ApiReviewFindingType.Security, "CORS policy", ApiReviewCheckResult.Fail, "Wildcard origin with credentials.", evidence));
            Add(targetFindings, findings, Finding(target, "cors-wildcard-credentials", ApiReviewSeverity.High, ApiReviewFindingType.Security, target.Origin, "CORS", "Wildcard CORS origin combined with credentials", "Access-Control-Allow-Origin: * together with Allow-Credentials: true is an invalid and dangerous combination.", "Restrict origins to an explicit allowlist when credentials are allowed.", evidence));
        }
        else if (allowOrigin == "*" && target.AuthRequired)
        {
            checks.Add(Check("cors-policy", ApiReviewFindingType.Security, "CORS policy", ApiReviewCheckResult.Warning, "Wildcard origin on an authenticated API.", evidence));
            Add(targetFindings, findings, Finding(target, "cors-wildcard-authenticated", ApiReviewSeverity.Medium, ApiReviewFindingType.Security, target.Origin, "CORS", "Permissive CORS (any origin) on an authenticated API", "Any web origin may call the API from a browser; exploitability is not asserted automatically.", "Restrict Access-Control-Allow-Origin to the known frontend origins.", evidence, ApiReviewCheckResult.Warning));
        }
        else if (allowOrigin is not null && originUri is not null && string.Equals(allowOrigin, originUri.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase))
            checks.Add(Check("cors-policy", ApiReviewFindingType.Security, "CORS policy", ApiReviewCheckResult.Pass, "Origin allowlisted for the frontend origin.", evidence));
        else if (allowOrigin is null)
            checks.Add(Check("cors-policy", ApiReviewFindingType.Security, "CORS policy", ApiReviewCheckResult.ManualReview, "No CORS headers observed; if the frontend calls this API from a different origin, verify the policy manually.", evidence));
        else
            checks.Add(Check("cors-policy", ApiReviewFindingType.Security, "CORS policy", ApiReviewCheckResult.Pass, "Explicit origin policy.", evidence));
        if (methods is not null && methods.Contains('*'))
            Add(targetFindings, findings, Finding(target, "cors-any-method", ApiReviewSeverity.Low, ApiReviewFindingType.Security, target.Origin, "CORS", "CORS allows any method", "Access-Control-Allow-Methods: * is broader than the API needs.", "List the methods the frontend actually uses.", evidence, ApiReviewCheckResult.Warning));
        return checks;
    }

    private async Task<List<ApiReviewCheck>> RestErrorHandlingProbesAsync(ApiReviewTarget target, ApiReviewRunRequest request, ApiReviewAccessMode mode, List<ApiReviewFinding> targetFindings, List<ApiReviewFinding> findings, CancellationToken ct)
    {
        var checks = new List<ApiReviewCheck>();
        var url = $"{target.Origin}{target.BasePath.TrimEnd('/')}/{UnknownRouteSegment}";
        var exec = await ExecuteRestAsync(request, mode, "GET", url, ct);
        if (!exec.Executed) { checks.Add(Check("errors-unknown-route", ApiReviewFindingType.Errors, "Unknown route handling", ApiReviewCheckResult.NotTested, exec.Message)); return checks; }
        var display = $"GET {target.BasePath.TrimEnd('/')}/<unknown>";
        var status = exec.StatusCode;
        var correct = status is 404 or 400 or 405 or 401 or 403;
        checks.Add(Check("errors-unknown-route", ApiReviewFindingType.Errors, "Unknown route handling", correct ? ApiReviewCheckResult.Pass : status >= 500 ? ApiReviewCheckResult.Fail : ApiReviewCheckResult.Warning, $"HTTP {status}{(exec.ProblemDetails ? " (ProblemDetails)" : "")}."));
        if (status is >= 200 and < 300)
            Add(targetFindings, findings, Finding(target, "errors-unknown-route-200", ApiReviewSeverity.Medium, ApiReviewFindingType.Errors, display, "Unknown route", "Unknown route answers 2xx", $"An unknown API route returned HTTP {status}{(JsonBodyInspector.IsJsonMediaType(exec.ContentType) ? "" : " with a non-JSON body (SPA fallback?)")}.", "Return 404 with a problem document for unknown API routes; exclude /api from SPA fallback.", [$"HTTP {status}", $"Content-Type: {exec.ContentType ?? "absent"}"], ApiReviewCheckResult.Warning));
        else if (status >= 500)
            Add(targetFindings, findings, Finding(target, "errors-unknown-route-5xx", ApiReviewSeverity.High, ApiReviewFindingType.Errors, display, "Unknown route", $"Unknown route causes HTTP {status}", "Routing errors surface as server errors.", "Handle unknown routes with 404.", [$"HTTP {status}"]));
        var structured = exec.ProblemDetails || (exec.JsonValid == true && exec.Shape.Any(s => s.Path is "$.title" or "$.message" or "$.error" or "$.errors" or "$.code"));
        checks.Add(Check("errors-format", ApiReviewFindingType.Errors, "Consistent error format (ProblemDetails)", status >= 400 ? (exec.ProblemDetails ? ApiReviewCheckResult.Pass : structured ? ApiReviewCheckResult.Warning : ApiReviewCheckResult.Warning) : ApiReviewCheckResult.NotApplicable,
            exec.ProblemDetails ? "RFC 7807 ProblemDetails." : structured ? "Structured JSON error without ProblemDetails." : "Unstructured error body."));
        if (status >= 400 && !exec.ProblemDetails)
            Add(targetFindings, findings, Finding(target, "errors-format", ApiReviewSeverity.Low, ApiReviewFindingType.Errors, display, "Error format", "Error responses do not use ProblemDetails", structured ? "Errors are structured JSON but not RFC 7807." : "Error body is not a structured JSON document.", "Use application/problem+json with type/title/status/detail/traceId.", [$"Content-Type: {exec.ContentType ?? "absent"}"], ApiReviewCheckResult.Warning));
        checks.Add(Check("errors-leak", ApiReviewFindingType.Errors, "No internal details in error responses", exec.Leaks.Count == 0 ? ApiReviewCheckResult.Pass : ApiReviewCheckResult.Fail, exec.Leaks.Count == 0 ? "No indicators." : string.Join(", ", exec.Leaks)));
        if (exec.Leaks.Count > 0)
            Add(targetFindings, findings, Finding(target, "errors-leak", ApiReviewSeverity.High, ApiReviewFindingType.Errors, display, "Error leakage", "Error response leaks internal details", "Stack trace / exception / path indicators detected in the error body (content redacted).", "Disable developer exception pages and return ProblemDetails.", exec.Leaks.Select(l => $"Indicator: {l}").ToList()));
        return checks;
    }

    // ── GraphQL ─────────────────────────────────────────────────────────────────

    private async Task<ApiReviewTargetResult> ReviewGraphQlAsync(ApiReviewTarget target, ApiReviewRunRequest request, ApiReviewAccessMode mode, string reason, string? action,
        ApiReviewBaseline? baseline, List<ApiReviewFinding> findings, CancellationToken ct)
    {
        var targetFindings = new List<ApiReviewFinding>();
        var checks = new List<ApiReviewCheck>();
        var endpoint = target.Url;
        var observed = target.Operations.Where(o => o.OperationType != GraphQlOperationType.None).ToList();
        if (mode is ApiReviewAccessMode.Unavailable or ApiReviewAccessMode.ManualOnly or ApiReviewAccessMode.Blocked)
        {
            // Compatibility needs a schema and the documents, not access: a configured artifact still lets it run.
            var blockedArtifact = await ResolveSchemaArtifactAsync(target, request, ct);
            var blockedCompatibility = AssessCompatibility(target, null, blockedArtifact, $"Not attempted — {reason}", observed, endpoint);
            foreach (var f in GraphQlOperationCompatibility.Findings(blockedCompatibility, target.TargetId)) Add(targetFindings, findings, f);
            return new ApiReviewTargetResult
            {
                Target = target, AccessMode = mode, AccessReason = reason, RequiredAction = action, Status = ApiReviewTargetStatus.Blocked, FindingCount = null,
                Operations = CompatibilityRows(blockedCompatibility, target, mode, ApiReviewCheckResult.Blocked, reason),
                GraphQlCompatibility = blockedCompatibility, GraphQlOperationMatches = Matches(blockedCompatibility),
            };
        }

        // 1. Safe query: __typename against the discovered/configured endpoint (never an assumed path).
        var probe = await ExecuteGraphQlAsync(request, mode, endpoint, TypenameProbe, ct);
        var operations = new List<ApiReviewOperationResult>();
        if (!probe.Executed)
        {
            checks.Add(Check("gql-reachability", ApiReviewFindingType.GraphQl, "Endpoint answers a safe query", probe.Timeout ? ApiReviewCheckResult.NotTested : ApiReviewCheckResult.Blocked, probe.Message));
            var unreachedArtifact = await ResolveSchemaArtifactAsync(target, request, ct);
            var unreachedCompatibility = AssessCompatibility(target, null, unreachedArtifact, $"Not attempted — {probe.Message}", observed, endpoint);
            foreach (var f in GraphQlOperationCompatibility.Findings(unreachedCompatibility, target.TargetId)) Add(targetFindings, findings, f);
            return new ApiReviewTargetResult
            {
                Target = target, AccessMode = mode, AccessReason = reason, Status = ApiReviewTargetStatus.NotTested, Checks = checks, FindingCount = null, RequiredAction = probe.Message,
                Operations = CompatibilityRows(unreachedCompatibility, target, mode, ApiReviewCheckResult.NotTested, "Observed operation; not executed."),
                GraphQlCompatibility = unreachedCompatibility, GraphQlOperationMatches = Matches(unreachedCompatibility),
            };
        }
        var json = JsonBodyInspector.IsJsonMediaType(probe.ContentType);
        var ok = probe.StatusCode is >= 200 and < 300 && probe.GraphQlHasData == true;
        checks.Add(Check("gql-reachability", ApiReviewFindingType.GraphQl, "Endpoint answers query { __typename }", ok ? ApiReviewCheckResult.Pass : probe.StatusCode is 401 or 403 ? ApiReviewCheckResult.Fail : ApiReviewCheckResult.Fail, $"HTTP {probe.StatusCode} via {Label(mode)}; data {(probe.GraphQlHasData == true ? "present" : "absent")}, {probe.GraphQlErrors ?? 0} error(s)."));
        checks.Add(Check("gql-content-type", ApiReviewFindingType.GraphQl, "JSON content type", json ? ApiReviewCheckResult.Pass : ApiReviewCheckResult.Warning, probe.ContentType ?? "absent"));
        if (!json)
            Add(targetFindings, findings, Finding(target, "gql-non-json", ApiReviewSeverity.Medium, ApiReviewFindingType.GraphQl, endpoint, "Content type", "GraphQL response is not JSON", $"Content-Type: {probe.ContentType ?? "absent"}.", "Return application/json or application/graphql-response+json.", [$"Content-Type: {probe.ContentType ?? "absent"}"], ApiReviewCheckResult.Warning));
        if (probe.StatusCode is 401 or 403 && mode == ApiReviewAccessMode.AuthenticatedHttp)
            Add(targetFindings, findings, Finding(target, "gql-auth-rejected", ApiReviewSeverity.Medium, ApiReviewFindingType.AccessControl, endpoint, "Authentication", $"Authenticated GraphQL query rejected (HTTP {probe.StatusCode})", "The in-memory credential was rejected by the GraphQL endpoint.", "Verify the API audience/scopes.", [$"HTTP {probe.StatusCode}"], ApiReviewCheckResult.Warning));
        else if (probe.StatusCode is >= 200 and < 300 && probe.GraphQlHasData == true && mode == ApiReviewAccessMode.PublicHttp && target.AuthRequired)
            Add(targetFindings, findings, Finding(target, "gql-unexpectedly-public", ApiReviewSeverity.Medium, ApiReviewFindingType.AccessControl, endpoint, "Authentication", "GraphQL endpoint answers queries without authentication", "Observed traffic carried a bearer, yet an anonymous __typename query succeeded (the endpoint may enforce auth per field).", "Confirm field-level authorization or require authentication at the endpoint.", [$"HTTP {probe.StatusCode}"], ApiReviewCheckResult.Warning));
        else if (probe.StatusCode >= 500)
            Add(targetFindings, findings, Finding(target, "gql-5xx", ApiReviewSeverity.High, ApiReviewFindingType.GraphQl, endpoint, "Status code", $"GraphQL endpoint returned HTTP {probe.StatusCode}", "A trivial query caused a server error.", "Investigate the GraphQL server.", [$"HTTP {probe.StatusCode}"]));
        var latency = probe.ElapsedMs ?? 0;
        var gqlLatency = request.Policy.LatencyResult(latency);
        checks.Add(Check("gql-latency", ApiReviewFindingType.Performance, "Response time (safe query)", gqlLatency, $"{latency:0} ms ({request.Policy.LatencyPolicyText})."));
        if (gqlLatency != ApiReviewCheckResult.Pass)
            Add(targetFindings, findings, Finding(target, "gql-slow", gqlLatency == ApiReviewCheckResult.Fail ? ApiReviewSeverity.Medium : ApiReviewSeverity.Low, ApiReviewFindingType.Performance, endpoint, "Response time", $"Slow GraphQL endpoint: {latency:0} ms for __typename", "Even a trivial query is slow; the endpoint or its middleware is the bottleneck. Per-operation latency from real traffic is reviewed by Performance Quality.", "Profile the GraphQL pipeline.", [$"Observed: {latency:0} ms"], ApiReviewCheckResult.Warning));
        if (probe.Leaks.Count > 0)
            Add(targetFindings, findings, Finding(target, "gql-leak", ApiReviewSeverity.High, ApiReviewFindingType.Errors, endpoint, "Error leakage", "GraphQL response leaks internal details", "Indicators of stack traces/exceptions were found in the response (content redacted).", "Mask exceptions in the GraphQL error filter.", probe.Leaks.Select(l => $"Indicator: {l}").ToList()));
        operations.Add(new ApiReviewOperationResult { Display = "query { __typename }", Method = "POST", Path = target.BasePath, AccessMode = mode, Executed = true, StatusCode = probe.StatusCode, ContentType = probe.ContentType, ElapsedMs = probe.ElapsedMs, ContentLength = probe.ContentLength, Result = ok ? ApiReviewCheckResult.Pass : ApiReviewCheckResult.Fail, ShapeEntryCount = probe.Shape.Count });

        // 2. Security headers / CORS on the endpoint
        checks.AddRange(SecurityChecks(target, probe, targetFindings, findings));
        checks.AddRange(await CorsChecksAsync(target, request, mode, probe, targetFindings, findings, ct));

        // 3. Schema via introspection (query-only). Disabled introspection is a policy observation, not a failure.
        GraphQlSchemaReviewResult? schemaReview = null;
        ApiReviewContractSummary contractSummary;
        GraphQlNormalizedContract? runtimeSchema = null;
        var (schemaJson, introspectionDisabled, introspectionMessage) = await FetchSchemaAsync(request, mode, endpoint, ct);
        if (schemaJson is not null)
        {
            var extraction = graphQl.Extract(schemaJson);
            if (extraction.Success && extraction.Contract is not null)
            {
                runtimeSchema = extraction.Contract;
                schemaReview = GraphQlSchemaReview.Review(extraction.Contract, observed, endpoint, target.TargetId);
                checks.AddRange(schemaReview.Checks);
                foreach (var f in schemaReview.Findings) Add(targetFindings, findings, f);
                contractSummary = new ApiReviewContractSummary
                {
                    Kind = "GraphQL schema", Source = endpoint, Available = true, Status = ApiReviewCheckResult.Pass, OperationCount = schemaReview.RootQueryFields.Count, TypeCount = schemaReview.TypeCount,
                    MutationCount = schemaReview.MutationFields.Count, SubscriptionCount = schemaReview.SubscriptionFields.Count, DeprecatedCount = schemaReview.DeprecatedFields.Count, IntrospectionEnabled = true, Hash = schemaReview.Hash,
                    Note = $"{schemaReview.RootQueryFields.Count} root query fields, {schemaReview.MutationFields.Count} mutations (not executed), {schemaReview.TypeCount} types.",
                };
                if (request.Policy.IntrospectionExpectedDisabled)
                    Add(targetFindings, findings, Finding(target, "gql-introspection-enabled", ApiReviewSeverity.Medium, ApiReviewFindingType.Security, endpoint, "Introspection policy", "Introspection is enabled", "The environment policy expects introspection to be disabled (production-like).", "Disable introspection for this environment.", [endpoint], ApiReviewCheckResult.Warning));
                checks.Add(Check("gql-introspection", ApiReviewFindingType.Security, "Introspection policy", request.Policy.IntrospectionExpectedDisabled ? ApiReviewCheckResult.Warning : ApiReviewCheckResult.Pass, request.Policy.IntrospectionExpectedDisabled ? "Enabled although the policy expects it disabled." : "Enabled (acceptable for this environment type)."));
                if (baseline is not null)
                {
                    var drift = ContractValidation.GraphQlDrift(baseline, schemaReview, target.TargetId, endpoint);
                    foreach (var f in drift) Add(targetFindings, findings, f);
                    checks.Add(Check("gql-drift", ApiReviewFindingType.Drift, "No schema drift since previous review", drift.Any(f => f.Drift == ApiReviewDriftClassification.Breaking) ? ApiReviewCheckResult.Fail : drift.Count > 0 ? ApiReviewCheckResult.Warning : ApiReviewCheckResult.Pass, $"{drift.Count} change(s) vs {baseline.RecordedAt:yyyy-MM-dd}."));
                }
            }
            else
            {
                contractSummary = new ApiReviewContractSummary { Kind = "GraphQL schema", Source = endpoint, Available = false, Status = ApiReviewCheckResult.Fail, IntrospectionEnabled = true, Note = extraction.FailureMessage ?? "Introspection document could not be normalized." };
                checks.Add(Check("gql-schema", ApiReviewFindingType.GraphQl, "Schema parsed", ApiReviewCheckResult.Fail, contractSummary.Note));
            }
        }
        else
        {
            contractSummary = new ApiReviewContractSummary { Kind = "GraphQL schema", Source = endpoint, Available = false, Status = introspectionDisabled ? ApiReviewCheckResult.NotApplicable : ApiReviewCheckResult.NotTested, IntrospectionEnabled = introspectionDisabled ? false : null, Note = introspectionMessage };
            checks.Add(Check("gql-introspection", ApiReviewFindingType.Security, "Introspection policy", introspectionDisabled ? ApiReviewCheckResult.Pass : ApiReviewCheckResult.NotTested, introspectionDisabled ? "Disabled (policy-based; schema checks need an SDL/introspection source)." : introspectionMessage));
            checks.Add(Check("gql-schema", ApiReviewFindingType.GraphQl, "Schema available", ApiReviewCheckResult.NotTested, "No schema: schema, nullability, deprecation and drift checks were not performed."));
        }

        // Client/server compatibility: the observed documents against the best trusted schema of THIS endpoint. Runtime introspection
        // first; a configured schema artifact when introspection is unavailable; otherwise Not assessed — never a fabricated schema.
        // The artifact is resolved even when runtime introspection wins, so the result records that a fallback existed.
        var runtimeOutcome = runtimeSchema is not null ? "Retrieved"
            : schemaJson is not null ? "Retrieved but could not be normalized"
            : introspectionDisabled ? $"Rejected — {introspectionMessage}" : $"Unavailable — {introspectionMessage}";
        var compatibility = AssessCompatibility(target, runtimeSchema, await ResolveSchemaArtifactAsync(target, request, ct), runtimeOutcome, observed, endpoint);
        foreach (var f in GraphQlOperationCompatibility.Findings(compatibility, target.TargetId)) Add(targetFindings, findings, f);
        compatibility = compatibility with { SchemaChangeImpact = SchemaChangeImpact(targetFindings, compatibility) };
        checks.Add(Check("gql-compatibility", ApiReviewFindingType.Contract, "Observed operations compatible with the schema",
            compatibility.Observed == 0 ? ApiReviewCheckResult.NotApplicable
            : compatibility.Assessed == 0 ? ApiReviewCheckResult.NotTested
            : compatibility.Incompatible > 0 ? ApiReviewCheckResult.Fail : ApiReviewCheckResult.Pass,
            compatibility.Observed == 0 ? "No observed business operation."
            : compatibility.Assessed == 0 ? $"{compatibility.Observed} observed operation(s) not assessed: {compatibility.NotAssessedReason ?? compatibility.Operations[0].NotAssessedReason}"
            : $"{compatibility.Compatible} compatible, {compatibility.Incompatible} incompatible, {compatibility.NotAssessed} not assessed of {compatibility.Observed} observed ({GraphQlOperationCompatibility.SourceLabel(compatibility.SchemaSource)}).",
            compatibility.Operations.Where(o => o.Status == GraphQlCompatibilityStatus.Incompatible).Select(o => o.Display).Take(20).ToList()));
        operations.AddRange(CompatibilityRows(compatibility, target, mode, ApiReviewCheckResult.NotTested, "Observed operation; not executed by the review."));
        foreach (var o in observed.Where(o => o.OperationType == GraphQlOperationType.Mutation).GroupBy(o => o.OperationName).Select(g => g.First()))
            Add(targetFindings, findings, Finding(target, "gql-observed-mutation", ApiReviewSeverity.Info, ApiReviewFindingType.GraphQl, endpoint, "Mutations", $"Observed mutation {o.OperationName ?? "(anonymous)"} requires manual review", "Mutations are never executed by the automated review.", "Verify mutation behaviour and authorization manually.", [o.Display], ApiReviewCheckResult.ManualReview));

        // 4. Error handling: invalid field (query-only, safe). Disabled in production policy.
        if (request.Policy.ErrorHandlingProbes && !request.Environment.IsProduction)
        {
            var invalid = await ExecuteGraphQlAsync(request, mode, endpoint, InvalidFieldProbe, ct);
            if (invalid.Executed)
            {
                var errorsShape = invalid.GraphQlErrors is > 0;
                var statusOk = invalid.StatusCode is 200 or 400;
                checks.Add(Check("gql-error-shape", ApiReviewFindingType.Errors, "Invalid query yields GraphQL errors", errorsShape && statusOk ? ApiReviewCheckResult.Pass : ApiReviewCheckResult.Warning, $"HTTP {invalid.StatusCode}; {invalid.GraphQlErrors ?? 0} error(s); extensions {(invalid.Shape.Any(s => s.Path.StartsWith("$.errors[*].extensions", StringComparison.Ordinal)) ? "present" : "absent")}."));
                if (!errorsShape)
                    Add(targetFindings, findings, Finding(target, "gql-error-shape", ApiReviewSeverity.Low, ApiReviewFindingType.Errors, endpoint, "Error shape", "Invalid query did not return a GraphQL errors array", $"HTTP {invalid.StatusCode} without a spec-compliant errors array.", "Return { errors: [...] } for validation failures.", [$"HTTP {invalid.StatusCode}"], ApiReviewCheckResult.Warning));
                if (invalid.StatusCode >= 500)
                    Add(targetFindings, findings, Finding(target, "gql-error-5xx", ApiReviewSeverity.Medium, ApiReviewFindingType.Errors, endpoint, "Error handling", $"Invalid query causes HTTP {invalid.StatusCode}", "Validation errors should not surface as server errors.", "Handle validation errors in the GraphQL pipeline.", [$"HTTP {invalid.StatusCode}"]));
                checks.Add(Check("gql-error-leak", ApiReviewFindingType.Errors, "No internal details in errors", invalid.Leaks.Count == 0 ? ApiReviewCheckResult.Pass : ApiReviewCheckResult.Fail, invalid.Leaks.Count == 0 ? "No indicators." : string.Join(", ", invalid.Leaks)));
                if (invalid.Leaks.Count > 0)
                    Add(targetFindings, findings, Finding(target, "gql-error-leak", ApiReviewSeverity.High, ApiReviewFindingType.Errors, endpoint, "Error leakage", "GraphQL error response leaks internal details", "Stack trace/exception indicators in the error response (content redacted).", "Mask exception details in the error filter.", invalid.Leaks.Select(l => $"Indicator: {l}").ToList()));
            }
            else checks.Add(Check("gql-error-shape", ApiReviewFindingType.Errors, "Invalid query yields GraphQL errors", ApiReviewCheckResult.NotTested, invalid.Message));
        }
        else checks.Add(Check("gql-error-shape", ApiReviewFindingType.Errors, "Invalid query yields GraphQL errors", ApiReviewCheckResult.NotTested, "Error probes disabled by policy."));
        checks.Add(Check("gql-depth-complexity", ApiReviewFindingType.Security, "Query depth / complexity controls", ApiReviewCheckResult.ManualReview, "Not probed automatically (no deep or expensive queries are sent); verify server-side limits manually."));

        return new ApiReviewTargetResult
        {
            Target = target, AccessMode = mode, AccessReason = reason, Status = ApiReviewTargetStatus.Completed, Operations = operations, Contract = contractSummary, Checks = checks,
            GraphQlOperationMatches = Matches(compatibility), GraphQlCompatibility = compatibility, FindingCount = targetFindings.Count,
            Baseline = new ApiReviewBaseline { TargetId = target.TargetId, RecordedAt = DateTimeOffset.UtcNow, GraphQlSchemaHash = schemaReview?.Hash, GraphQlRootFields = schemaReview?.RootQueryFields ?? [], GraphQlDeprecatedFields = schemaReview?.DeprecatedFields ?? [] },
        };
    }

    /// <summary>A configured schema for one target: the parsed schema (or why it is unusable) and, for a stored artifact, its metadata.</summary>
    private sealed record SchemaArtifactResolution(GraphQlNormalizedContract? Schema, string? Detail, GraphQlSchemaArtifact? Stored, string? Problem);

    /// <summary>
    /// The trusted schema artifact configured for THIS target in THIS environment (the run's environment + stable target id — never a
    /// display name, never another environment's artifact). Falls back to a schema URL in <see cref="ApiReviewTarget.ContractSource"/>.
    /// Null when nothing is configured — never a schema inferred from observed operations.
    /// </summary>
    private async Task<SchemaArtifactResolution?> ResolveSchemaArtifactAsync(ApiReviewTarget target, ApiReviewRunRequest request, CancellationToken ct)
    {
        if (schemaArtifacts is not null && await schemaArtifacts.ResolveAsync(request.Environment.EnvironmentId, target.TargetId, ct) is { } stored)
            return new SchemaArtifactResolution(stored.Schema, $"Configured SDL {stored.Artifact.FileName} ({stored.Artifact.ShortHash})", stored.Artifact, stored.Problem);
        var (schema, detail) = await FetchSchemaArtifactAsync(target, ct);
        return schema is null ? null : new SchemaArtifactResolution(schema, detail, null, null);
    }

    /// <summary>
    /// Source selection and assessment in one place: runtime introspection first; the configured artifact only when the runtime schema is
    /// unavailable; otherwise Not assessed (with the artifact's problem as the reason when it exists but is invalid). The result records
    /// which source was used, what the runtime attempt returned and the artifact snapshot — so a historical run never reads the current one.
    /// </summary>
    private ApiReviewGraphQlCompatibility AssessCompatibility(ApiReviewTarget target, GraphQlNormalizedContract? runtimeSchema, SchemaArtifactResolution? artifact, string runtimeOutcome,
        IReadOnlyList<ApiReviewOperation> observed, string endpoint)
    {
        var useArtifact = runtimeSchema is null && artifact?.Schema is not null;
        var (schema, source, detail) = runtimeSchema is not null ? (runtimeSchema, GraphQlSchemaSource.RuntimeIntrospection, $"Introspection of {endpoint}")
            : useArtifact ? (artifact!.Schema, GraphQlSchemaSource.ConfiguredArtifact, artifact.Detail)
            : ((GraphQlNormalizedContract?)null, GraphQlSchemaSource.None, (string?)null);
        var invalidArtifact = runtimeSchema is null && artifact?.Problem is { } problem ? $"Configured schema artifact invalid: {problem}" : null;
        var compatibility = GraphQlOperationCompatibility.Assess(schema, source, detail, schema is null ? null : DateTimeOffset.UtcNow, observed, endpoint, logger, invalidArtifact) with
        {
            RuntimeSchemaOutcome = runtimeOutcome,
            ConfiguredArtifact = artifact?.Stored is { } stored
                ? new GraphQlSchemaArtifactSnapshot { ArtifactId = stored.Id, FileName = stored.FileName, ContentHash = stored.ContentHash, UpdatedAt = stored.UpdatedAt, UsedForCompatibility = useArtifact }
                : null,
            ConfiguredArtifactProblem = artifact?.Problem,
        };
        logger.LogInformation(
            "GraphQL schema source for target {TargetId}: {SchemaSource}; runtime introspection {RuntimeOutcome}; artifact {ArtifactId} {ArtifactHash} {ArtifactStatus}; {Observed} observed, {Compatible} compatible, {Incompatible} incompatible, {NotAssessed} not assessed in {DurationMs:0} ms.",
            target.TargetId, compatibility.SchemaSource, runtimeOutcome, artifact?.Stored?.Id ?? "none", artifact?.Stored?.ShortHash ?? "",
            artifact is null ? "not configured" : artifact.Problem is not null ? "invalid" : useArtifact ? "used" : "available as fallback",
            compatibility.Observed, compatibility.Compatible, compatibility.Incompatible, compatibility.NotAssessed, compatibility.DurationMs);
        return compatibility;
    }

    /// <summary>
    /// A schema URL in <see cref="ApiReviewTarget.ContractSource"/>: SDL or an introspection result, fetched read-only. Null when none is
    /// configured or it cannot be read.
    /// </summary>
    private async Task<(GraphQlNormalizedContract? Schema, string? Detail)> FetchSchemaArtifactAsync(ApiReviewTarget target, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(target.ContractSource)) return (null, null);
        try
        {
            using var response = await publicClient.GetAsync(target.ContractSource, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogInformation("GraphQL schema artifact {Source} returned HTTP {Status}; compatibility falls back to Not assessed.", target.ContractSource, (int)response.StatusCode);
                return (null, null);
            }
            var text = await ReadBoundedTextAsync(response, 10 * 1024 * 1024, ct);
            var schema = text.TrimStart().StartsWith('{')
                ? graphQl.Extract(text) is { Success: true, Contract: { } contract } ? contract : null
                : GraphQlSdlSchema.FromSdl(text, out _);
            logger.LogInformation("GraphQL schema artifact {Source}: {Outcome}.", target.ContractSource, schema is null ? "unreadable" : "parsed");
            return schema is null ? (null, null) : (schema, $"Configured schema artifact {target.ContractSource}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogInformation("GraphQL schema artifact {Source} not reachable ({Error}).", target.ContractSource, ex.GetType().Name);
            return (null, null);
        }
    }

    /// <summary>One row per observed operation variant: execution (never, for observed operations) and compatibility, side by side.</summary>
    private static List<ApiReviewOperationResult> CompatibilityRows(ApiReviewGraphQlCompatibility compatibility, ApiReviewTarget target, ApiReviewAccessMode mode, ApiReviewCheckResult execution, string executionNote) =>
        compatibility.Operations.Select(o => new ApiReviewOperationResult
        {
            Display = o.Display, Method = "POST", Path = target.BasePath, AccessMode = mode, Executed = false, Result = execution,
            OperationType = o.OperationType, ObservationCount = o.ObservationCount, Historical = o.Historical, Compatibility = o.Status,
            ContractMatched = o.Status switch { GraphQlCompatibilityStatus.Compatible => true, GraphQlCompatibilityStatus.Incompatible => false, _ => null },
            Note = (o.OperationType == GraphQlOperationType.Mutation ? "Contract validation only — mutation was not executed. " : executionNote + " ")
                + o.Status switch
                {
                    GraphQlCompatibilityStatus.Compatible => $"Compatible with the {GraphQlOperationCompatibility.SourceLabel(compatibility.SchemaSource)}.",
                    GraphQlCompatibilityStatus.Incompatible => $"Incompatible: {o.Issues[0].Message}",
                    _ => $"Compatibility not assessed: {o.NotAssessedReason}",
                },
        }).ToList();

    /// <summary>The existing per-operation match list, now from document validation rather than an operation-name heuristic.</summary>
    private static List<ApiReviewGraphQlOperationMatch> Matches(ApiReviewGraphQlCompatibility compatibility) =>
        compatibility.Operations.Select(o => new ApiReviewGraphQlOperationMatch(o.Display, o.RootFields.FirstOrDefault(),
            o.Status switch { GraphQlCompatibilityStatus.Compatible => ApiReviewCheckResult.Pass, GraphQlCompatibilityStatus.Incompatible => ApiReviewCheckResult.Fail, _ => ApiReviewCheckResult.NotTested },
            o.Status switch
            {
                GraphQlCompatibilityStatus.Compatible => "Compatible with the schema.",
                GraphQlCompatibilityStatus.Incompatible => string.Join(" ", o.Issues.Take(3).Select(i => i.Message)),
                _ => o.NotAssessedReason ?? "Not assessed.",
            })).ToList();

    /// <summary>Removed root fields (the drift engine's stable field identity) that observed operations still select.</summary>
    private static List<string> SchemaChangeImpact(IEnumerable<ApiReviewFinding> targetFindings, ApiReviewGraphQlCompatibility compatibility)
    {
        const string prefix = "Removed root field: ";
        return targetFindings.Where(f => f.RuleId == "drift-gql-root-field-removed")
            .SelectMany(f => f.Evidence).Where(e => e.StartsWith(prefix, StringComparison.Ordinal)).Select(e => e[prefix.Length..])
            .Select(field => (field, ops: compatibility.Operations.Where(o => o.RootFields.Contains(field, StringComparer.Ordinal)).Select(o => o.Display).Distinct().ToList()))
            .Where(x => x.ops.Count > 0)
            .Select(x => $"Removed root field `{x.field}` — used by {string.Join(", ", x.ops)}").ToList();
    }

    private async Task<(string? SchemaJson, bool Disabled, string Message)> FetchSchemaAsync(ApiReviewRunRequest request, ApiReviewAccessMode mode, string endpoint, CancellationToken ct)
    {
        if (mode == ApiReviewAccessMode.AuthenticatedHttp)
        {
            var outcome = await gateway.FetchGraphQlSchemaAsync(request.Identity, endpoint, ct);
            return (outcome.SchemaJson, outcome.IntrospectionDisabled, outcome.Message);
        }
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new StringContent(JsonSerializer.Serialize(new { query = AuthenticatedApiExecutionService.IntrospectionQuery }), Encoding.UTF8, "application/json") };
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await publicClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
            var text = await ReadBoundedTextAsync(response, 4 * 1024 * 1024, ct);
            try
            {
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object && data.TryGetProperty("__schema", out _)) return (text, false, "Introspection schema returned.");
                return (null, true, $"HTTP {(int)response.StatusCode}; introspection disabled or rejected.");
            }
            catch (JsonException) { return (null, false, $"HTTP {(int)response.StatusCode}; response was not JSON."); }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return (null, false, $"Introspection request failed ({ex.GetType().Name})."); }
    }

    // ── Execution (public client or gateway) ────────────────────────────────────

    private async Task<Exec> ExecuteRestAsync(ApiReviewRunRequest request, ApiReviewAccessMode mode, string method, string url, CancellationToken ct)
    {
        if (mode == ApiReviewAccessMode.AuthenticatedHttp)
        {
            var outcome = await gateway.ExecuteRestAsync(request.Identity, method, url, ct);
            return FromGateway(outcome, mode);
        }
        try
        {
            using var message = new HttpRequestMessage(new HttpMethod(method), url);
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            message.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip, br");
            var stopwatch = Stopwatch.StartNew();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct); cts.CancelAfter(TimeSpan.FromSeconds(20));
            using var response = await publicClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            var text = await ReadBoundedTextAsync(response, MaxPublicBodyBytes, cts.Token);
            stopwatch.Stop();
            return Inspect(response, text, stopwatch.Elapsed.TotalMilliseconds, mode, graphQl: false);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return new Exec(false, mode, 0, null, null, null, new Dictionary<string, string>(), [], [], false, null, null, null, "The request did not complete within 20 s.", Timeout: true); }
        catch (HttpRequestException ex) { return new Exec(false, mode, 0, null, null, null, new Dictionary<string, string>(), [], [], false, null, null, null, $"Connection failed ({ex.HttpRequestError})."); }
    }

    private async Task<Exec> ExecuteGraphQlAsync(ApiReviewRunRequest request, ApiReviewAccessMode mode, string endpoint, string query, CancellationToken ct)
    {
        if (mode == ApiReviewAccessMode.AuthenticatedHttp)
            return FromGateway(await gateway.ExecuteGraphQlQueryAsync(request.Identity, endpoint, query, ct), mode);
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new StringContent(JsonSerializer.Serialize(new { query }), Encoding.UTF8, "application/json") };
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/graphql-response+json"));
            var stopwatch = Stopwatch.StartNew();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct); cts.CancelAfter(TimeSpan.FromSeconds(20));
            using var response = await publicClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            var text = await ReadBoundedTextAsync(response, MaxPublicBodyBytes, cts.Token);
            stopwatch.Stop();
            return Inspect(response, text, stopwatch.Elapsed.TotalMilliseconds, mode, graphQl: true);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return new Exec(false, mode, 0, null, null, null, new Dictionary<string, string>(), [], [], false, null, null, null, "The request did not complete within 20 s.", Timeout: true); }
        catch (HttpRequestException ex) { return new Exec(false, mode, 0, null, null, null, new Dictionary<string, string>(), [], [], false, null, null, null, $"Connection failed ({ex.HttpRequestError})."); }
    }

    private static Exec FromGateway(AuthenticatedReviewExecutionOutcome outcome, ApiReviewAccessMode mode)
    {
        if (!outcome.Executed || outcome.Result is null)
            return new Exec(false, mode, 0, null, null, null, new Dictionary<string, string>(), [], [], false, null, null, null, outcome.Message);
        var r = outcome.Result;
        return new Exec(true, mode, r.StatusCode, r.ContentType, r.ElapsedMs, r.ContentLength, new Dictionary<string, string>(r.SecurityHeaders, StringComparer.OrdinalIgnoreCase), r.BodyShape, r.LeakIndicators, r.ProblemDetails, r.JsonValid, r.GraphQlErrorCount, r.GraphQlHasData, r.Outcome);
    }

    /// <summary>Public response → structural evidence. The text is scanned for leak indicators and parsed for its shape, then discarded.</summary>
    private static Exec Inspect(HttpResponseMessage response, string text, double elapsedMs, ApiReviewAccessMode mode, bool graphQl)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in AuthenticatedApiExecutionResult.SecurityHeaderAllowList)
            if (response.Headers.TryGetValues(name, out var values) || response.Content.Headers.TryGetValues(name, out values))
                headers[name] = string.Join(", ", values).Length > 512 ? string.Join(", ", values)[..512] : string.Join(", ", values);
        var contentType = response.Content.Headers.ContentType?.MediaType;
        var leaks = JsonBodyInspector.LeakIndicators(text);
        IReadOnlyList<JsonShapeEntry> shape = [];
        bool? jsonValid = null;
        int? errors = null; bool? hasData = null;
        if (text.Length > 0 && (JsonBodyInspector.IsJsonMediaType(contentType) || graphQl || text.TrimStart().StartsWith('{') || text.TrimStart().StartsWith('[')))
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                jsonValid = true;
                shape = JsonBodyInspector.Shape(doc.RootElement);
                if (graphQl && doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    errors = doc.RootElement.TryGetProperty("errors", out var e) && e.ValueKind == JsonValueKind.Array ? e.GetArrayLength() : 0;
                    hasData = doc.RootElement.TryGetProperty("data", out var d) && d.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);
                }
            }
            catch (JsonException) { jsonValid = false; }
        }
        var length = response.Content.Headers.ContentLength ?? Encoding.UTF8.GetByteCount(text);
        return new Exec(true, mode, (int)response.StatusCode, contentType, Math.Round(elapsedMs, 1), length, headers, shape, leaks, JsonBodyInspector.IsProblemDetails(contentType, shape), jsonValid, errors, hasData, $"HTTP {(int)response.StatusCode}");
    }

    private static async Task<string> ReadBoundedTextAsync(HttpResponseMessage response, long max, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[16384];
        while (buffer.Length < max)
        {
            var read = await stream.ReadAsync(chunk, ct);
            if (read <= 0) break;
            buffer.Write(chunk, 0, read);
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    // ── helpers ─────────────────────────────────────────────────────────────────

    private static ApiReviewCoverage Coverage(List<ApiReviewTargetResult> results, ApiReviewRunRequest request)
    {
        var rest = results.Where(r => r.Target.ApiType == ApiReviewTargetType.Rest).ToList();
        var gql = results.Where(r => r.Target.ApiType == ApiReviewTargetType.GraphQl).ToList();
        var restOps = rest.SelectMany(r => r.Operations).ToList();
        var observedGql = gql.SelectMany(r => r.Target.Operations.Where(o => o.OperationType != GraphQlOperationType.None)).Count();
        var auth = results.Where(r => r.AccessMode is ApiReviewAccessMode.AuthenticatedHttp or ApiReviewAccessMode.Unavailable or ApiReviewAccessMode.ManualOnly).ToList();
        var pub = results.Where(r => r.AccessMode == ApiReviewAccessMode.PublicHttp).ToList();
        return new ApiReviewCoverage
        {
            RestOperationsTotal = restOps.Count(o => o.Result != ApiReviewCheckResult.ManualReview), RestOperationsReviewed = restOps.Count(o => o.Executed),
            GraphQlOperationsObserved = observedGql, GraphQlOperationsMatched = gql.SelectMany(r => r.GraphQlOperationMatches).Count(m => m.Result == ApiReviewCheckResult.Pass),
            ContractChecks = results.Sum(r => r.Checks.Count(c => c.Area == ApiReviewFindingType.Contract) + r.Operations.Sum(o => o.Checks.Count(c => c.Area is ApiReviewFindingType.Contract or ApiReviewFindingType.Drift))),
            SecurityChecks = results.Sum(r => r.Checks.Count(c => c.Area == ApiReviewFindingType.Security)),
            AuthenticatedPlanned = auth.Count, AuthenticatedExecuted = auth.Count(r => r.Status is ApiReviewTargetStatus.Completed or ApiReviewTargetStatus.PartiallyCompleted),
            PublicPlanned = pub.Count, PublicExecuted = pub.Count(r => r.Status is ApiReviewTargetStatus.Completed or ApiReviewTargetStatus.PartiallyCompleted),
            TargetsBlocked = results.Count(r => r.Status == ApiReviewTargetStatus.Blocked), TargetsCompleted = results.Count(r => r.Status == ApiReviewTargetStatus.Completed),
            UnsafeOperationsNotExecuted = request.Targets.Where(t => t.Selected).SelectMany(t => t.Operations).Count(o => !o.IsSafe),
        };
    }

    private static string Label(ApiReviewAccessMode mode) => mode == ApiReviewAccessMode.AuthenticatedHttp ? "authenticated HTTP (gateway)" : "public HTTP";

    private static ApiReviewCheckResult Worst(IEnumerable<ApiReviewCheckResult> results)
    {
        var list = results.ToList();
        if (list.Contains(ApiReviewCheckResult.Fail)) return ApiReviewCheckResult.Fail;
        if (list.Contains(ApiReviewCheckResult.Blocked)) return ApiReviewCheckResult.Blocked;
        if (list.Contains(ApiReviewCheckResult.Warning)) return ApiReviewCheckResult.Warning;
        if (list.Contains(ApiReviewCheckResult.ManualReview)) return ApiReviewCheckResult.ManualReview;
        return list.Count == 0 ? ApiReviewCheckResult.NotTested : ApiReviewCheckResult.Pass;
    }

    private static void Add(List<ApiReviewFinding> targetFindings, List<ApiReviewFinding> all, ApiReviewFinding finding) { targetFindings.Add(finding); all.Add(finding); }

    private static ApiReviewFinding Finding(ApiReviewTarget target, string id, ApiReviewSeverity severity, ApiReviewFindingType type, string endpoint, string check, string title, string description,
        string recommendation, List<string> evidence, ApiReviewCheckResult result = ApiReviewCheckResult.Fail, ApiReviewDriftClassification? drift = null) =>
        OpenApiDocumentReview.Finding(target.TargetId, id, severity, type, endpoint, check, title, description, recommendation, evidence, result, drift);

    private static ApiReviewCheck Check(string id, ApiReviewFindingType area, string title, ApiReviewCheckResult result, string detail, List<string>? evidence = null) =>
        OpenApiDocumentReview.Check(id, area, title, result, detail, evidence);
}
