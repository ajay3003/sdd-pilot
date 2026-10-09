using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BirkNext.Api.Services.ContractAnalysis;
using BirkNext.Api.Services.LocalHttpsProxy;
using BirkNext.ApiReview;
using BirkNext.LocalHttpsProxy;

namespace BirkNext.Api.Services.ApiQuality.Fuzzing;

public interface IApiFuzzingService
{
    /// <summary>Eligibility and case preview. Sends no fuzz request; only the contract/schema retrieval the review itself performs.</summary>
    Task<ApiFuzzingPlan> PlanAsync(ApiFuzzingRunRequest request, CancellationToken ct = default);

    /// <summary>Re-derives the plan server-side and executes it within the budget. <paramref name="progress"/> receives partial reports.</summary>
    Task<ApiFuzzingReport> RunAsync(ApiFuzzingRunRequest request, string runId, Action<ApiFuzzingReport>? progress, CancellationToken ct = default);
}

/// <summary>
/// Safe fuzzing inside API Quality Review. The client chooses the level, lower limits and an operation subset; everything that is sent is
/// derived here from the target's published contract (OpenAPI parameters, GraphQL schema arguments) — never from client-supplied URLs or
/// payloads. Every request passes <see cref="ApiSafeRequestGuard"/>, the backend environment decision and the request budget. Public targets
/// use the anonymous client; authenticated targets go through the review gateway, which re-validates and applies the memory-only credential.
/// </summary>
public sealed class ApiFuzzingService(HttpClient publicClient, IAuthenticatedReviewGateway gateway, IOpenApiExtractor openApi, IGraphQlExtractor graphQl,
    IApiEnvironmentSafetyPolicy safetyPolicy, ILogger<ApiFuzzingService> logger, IGraphQlSchemaArtifactStore? schemaArtifacts = null,
    Security.ITrustedSecurityTargetRegistry? trust = null, IEnumerable<IRequestCleanupStrategy>? cleanupStrategies = null) : IApiFuzzingService
{
    private sealed record TargetContext(ApiReviewTarget Target, ApiReviewAccessMode Mode, Dictionary<string, List<string>> DeclaredCodes, AuthenticatedReviewIdentity Identity)
    {
        /// <summary>The server resolved this target as trusted for request-body cases.</summary>
        public bool BodyTrusted { get; set; }
    }

    public async Task<ApiFuzzingPlan> PlanAsync(ApiFuzzingRunRequest request, CancellationToken ct = default) => (await PlanInternalAsync(request, ct)).Plan;

    private async Task<(ApiFuzzingPlan Plan, Dictionary<string, TargetContext> Targets)> PlanInternalAsync(ApiFuzzingRunRequest request, CancellationToken ct)
    {
        var settings = request.Settings.Clamped();
        var safety = safetyPolicy.EvaluateForFuzzing(request.Review);
        var selected = request.Review.Targets.Where(t => t.Selected).Take(ApiFuzzingLimits.MaxTargets).ToList();
        var contexts = new Dictionary<string, TargetContext>(StringComparer.Ordinal);
        ApiFuzzingPlan Empty(string reason, List<ApiFuzzOperationEligibility>? operations = null) => new()
        {
            Safety = safety, Level = settings.Level, Settings = settings, Operations = operations ?? [], NotAvailableReason = reason, GeneratedAt = DateTimeOffset.UtcNow,
        };

        if (settings.Level == ApiFuzzingLevel.Off || !settings.Enabled) return (Empty("Safe fuzzing is off. Choose Contract fuzzing or Safe security fuzzing to analyse eligibility."), contexts);
        if (!safety.ActiveTestingAllowed)
        {
            var classification = safety.State is ApiEnvironmentSafetyState.ProductionBlocked or ApiEnvironmentSafetyState.ProductionMarkerBlocked
                ? ApiFuzzSafetyClassification.ProductionBlocked : ApiFuzzSafetyClassification.UnknownSafety;
            return (Empty($"Blocked by safety policy: {safety.Reason}", selected.Select(t => new ApiFuzzOperationEligibility
            {
                TargetId = t.TargetId, OperationId = "(target)", Display = ApiTargetName(t), Protocol = t.ApiType == ApiReviewTargetType.GraphQl ? ApiFuzzProtocol.GraphQl : ApiFuzzProtocol.Rest,
                Classification = classification, Reason = safety.Reason,
            }).ToList()), contexts);
        }

        var capabilities = gateway.Resolve(request.Review.Identity);
        var operations = new List<ApiFuzzOperationEligibility>();
        var cases = new List<ApiFuzzCase>();
        var fingerprints = new List<ApiFuzzContractFingerprint>();
        var bodyTrust = new List<BirkNext.RuntimeSecurity.TrustedTargetDecision>();
        var cleanupIds = (cleanupStrategies ?? []).Select(c => c.Id).ToList();
        foreach (var target in selected)
        {
            var (mode, accessReason, _) = ApiReviewEngine.ResolveAccess(target, request.Review, capabilities);
            var protocol = target.ApiType == ApiReviewTargetType.GraphQl ? ApiFuzzProtocol.GraphQl : ApiFuzzProtocol.Rest;
            if (mode is not (ApiReviewAccessMode.PublicHttp or ApiReviewAccessMode.AuthenticatedHttp))
            {
                operations.Add(new() { TargetId = target.TargetId, OperationId = "(target)", Display = ApiTargetName(target), Protocol = protocol, Classification = ApiFuzzSafetyClassification.AccessUnavailable, Reason = accessReason });
                continue;
            }
            var context = new TargetContext(target, mode, new Dictionary<string, List<string>>(StringComparer.Ordinal), request.Review.Identity);
            contexts[target.TargetId] = context;
            ApiFuzzCaseGenerator.GenerationResult? generated;
            if (target.ApiType == ApiReviewTargetType.Rest)
            {
                var (contract, hash, reason) = await RestContractAsync(target, ct);
                fingerprints.Add(new(target.TargetId, "OpenAPI", target.ContractSource, hash));
                if (contract is null)
                {
                    operations.Add(new() { TargetId = target.TargetId, OperationId = "(target)", Display = ApiTargetName(target), Protocol = protocol, Classification = ApiFuzzSafetyClassification.MissingContract, Reason = reason });
                    continue;
                }
                foreach (var op in contract.Operations) context.DeclaredCodes[$"{op.Method.ToUpperInvariant()} {op.Path}"] = op.ResponseCodes;
                foreach (var op in contract.Operations) context.DeclaredCodes[$"{op.Method.ToUpperInvariant()} {op.Path} (body)"] = op.ResponseCodes;
                generated = ApiFuzzCaseGenerator.Rest(target, contract, settings);
                if (settings.BodyFuzzing)
                {
                    // Request bodies cross the read-only boundary: the server registry, not the client, decides the target is trusted.
                    var decision = trust?.Resolve(request.Review.Environment.EnvironmentId, [target.Origin])
                        ?? new BirkNext.RuntimeSecurity.TrustedTargetDecision { Reason = "No trusted target registry is available on this server." };
                    bodyTrust.Add(decision);
                    if (decision.Allowed)
                    {
                        context.BodyTrusted = true;
                        var body = ApiBodyFuzzCaseGenerator.Rest(target, contract, settings, request.BodyOperations, cleanupIds);
                        generated = new(generated.Operations.Concat(body.Operations).ToList(), generated.Cases.Concat(body.Cases).ToList());
                    }
                    else
                    {
                        var untrusted = request.BodyOperations.Where(o => o.Policy is BirkNext.RuntimeSecurity.BodyFuzzingPolicy.ReadOnlyBodySafe or BirkNext.RuntimeSecurity.BodyFuzzingPolicy.StateChangingWithCleanup)
                            .Select(o => new ApiFuzzOperationEligibility
                            {
                                TargetId = target.TargetId, OperationId = o.Key + " (body)", Display = o.Key + " · request body", Protocol = protocol, Method = o.Method.ToUpperInvariant(),
                                Classification = ApiFuzzSafetyClassification.UntrustedTarget, Reason = decision.Reason,
                            });
                        generated = new(generated.Operations.Concat(untrusted).ToList(), generated.Cases);
                    }
                }
            }
            else
            {
                var (schema, source, hash, reason) = await GraphQlSchemaAsync(target, request.Review, mode, ct);
                fingerprints.Add(new(target.TargetId, "GraphQL schema", source, hash));
                if (schema is null)
                {
                    operations.Add(new() { TargetId = target.TargetId, OperationId = "(target)", Display = ApiTargetName(target), Protocol = protocol, Classification = ApiFuzzSafetyClassification.MissingContract, Reason = reason });
                    continue;
                }
                generated = ApiFuzzCaseGenerator.GraphQl(target, schema, settings, ObservedRootFieldNames(target));
            }
            operations.AddRange(generated.Operations);
            cases.AddRange(generated.Cases);
        }

        if (request.OperationIds is { Count: > 0 } subset)
        {
            var keep = subset.ToHashSet(StringComparer.Ordinal);
            cases = cases.Where(c => keep.Contains($"{c.TargetId}|{c.OperationId}") || keep.Contains(c.OperationId)).ToList();
        }
        var planned = cases.Take(settings.MaxTotalRequests).ToList();
        var overBudget = cases.Count - planned.Count;
        var plannedOps = planned.Select(c => $"{c.TargetId}|{c.OperationId}").ToHashSet(StringComparer.Ordinal);
        operations = operations.Select(o => o.Classification == ApiFuzzSafetyClassification.ReadOnlyEligible && o.CaseCount > 0 && !plannedOps.Contains($"{o.TargetId}|{o.OperationId}")
            ? o with { Classification = ApiFuzzSafetyClassification.BudgetExhausted, Reason = request.OperationIds is { Count: > 0 } ? "Not selected for this run." : $"Eligible, but the request budget ({settings.MaxTotalRequests}) is spent on earlier operations." }
            : o).ToList();
        var plan = new ApiFuzzingPlan
        {
            Safety = safety, Level = settings.Level, Settings = settings, Operations = operations, Cases = planned, Contracts = fingerprints, CasesOverBudget = overBudget, GeneratedAt = DateTimeOffset.UtcNow,
            BodyTrust = bodyTrust,
            NotAvailableReason = planned.Count > 0 ? null
                : operations.Count == 0 ? "No API target is selected."
                : operations.All(o => o.Classification == ApiFuzzSafetyClassification.MissingContract) ? "Contract fuzzing is not available: no OpenAPI contract or GraphQL schema could be retrieved. Unknown endpoints are never fuzzed."
                : "No eligible read-only operation with a contract-derived case.",
        };
        logger.LogInformation("Safe fuzzing plan: level {Level}, safety {Safety}, {Eligible} eligible operation(s), {Planned} case(s), {OverBudget} over budget.",
            plan.Level, safety.State, plan.EligibleOperations, planned.Count, overBudget);
        return (plan, contexts);
    }

    public async Task<ApiFuzzingReport> RunAsync(ApiFuzzingRunRequest request, string runId, Action<ApiFuzzingReport>? progress, CancellationToken ct = default)
    {
        var started = DateTimeOffset.UtcNow;
        var (plan, contexts) = await PlanInternalAsync(request, ct);
        var settings = plan.Settings;
        var report = new ApiFuzzingReport
        {
            RunId = runId, EnvironmentId = request.Review.Environment.EnvironmentId, EnvironmentName = request.Review.Environment.Name, Safety = plan.Safety, Level = plan.Level, Settings = settings,
            Operations = plan.Operations, Cases = plan.Cases, Contracts = plan.Contracts, CasesPlanned = plan.Cases.Count, CasesOverBudget = plan.CasesOverBudget, StartedAt = started,
            BodyTrust = plan.BodyTrust, BodyOperations = settings.BodyFuzzing ? [.. request.BodyOperations] : [],
            Limitations = Limitations(settings),
        };
        if (!plan.CanRun)
            return report with { CompletedAt = DateTimeOffset.UtcNow, Completeness = ApiFuzzCompleteness.NotRun, CompletenessReason = plan.NotAvailableReason ?? "Nothing to run." };

        var results = new List<ApiFuzzCaseResult>();
        var stopReason = (string?)null;
        using var budget = new ApiFuzzRequestBudget(settings);
        progress?.Invoke(Snapshot(report, results, running: true));
        foreach (var fuzzCase in plan.Cases)
        {
            if (ct.IsCancellationRequested) { stopReason ??= "Cancelled by the user: no further cases were scheduled."; }
            if (stopReason is not null) { results.Add(NotExecuted(fuzzCase, stopReason)); continue; }
            if (!contexts.TryGetValue(fuzzCase.TargetId, out var context)) { results.Add(Blocked(fuzzCase, "The target is no longer part of the plan.")); continue; }
            var safe = BuildRequest(fuzzCase, context.Target, context.BodyTrusted);
            if (ApiSafeRequestGuard.Validate(safe, context.Target.Origin, settings, plan.Safety) is { } rejection)
            {
                logger.LogWarning("Safe fuzzing case {CaseId} ({OperationId}, {Mutation}) blocked by the safety guard: {Reason}", fuzzCase.CaseId, fuzzCase.OperationId, fuzzCase.MutationType, rejection);
                results.Add(Blocked(fuzzCase, rejection));
                continue;
            }
            bool acquired;
            try { acquired = await budget.TryAcquireAsync($"{fuzzCase.TargetId}|{fuzzCase.OperationId}", ct); }
            catch (OperationCanceledException) { stopReason = "Cancelled by the user: no further cases were scheduled."; results.Add(NotExecuted(fuzzCase, stopReason)); continue; }
            if (!acquired) { results.Add(NotExecuted(fuzzCase, "The request budget is exhausted.")); continue; }
            ApiFuzzResponse response;
            try { response = await SendAsync(safe, context, settings, ct); }
            finally { budget.Release(); }
            if (!response.Executed && ct.IsCancellationRequested)
            {
                stopReason = "Cancelled by the user while a request was in flight.";
                results.Add(NotExecuted(fuzzCase, stopReason));
                continue;
            }
            var declared = fuzzCase.Protocol == ApiFuzzProtocol.Rest && context.DeclaredCodes.TryGetValue(fuzzCase.OperationId, out var codes) ? codes : null;
            var result = ApiFuzzOutcomeClassifier.Classify(fuzzCase, response, declared);
            results.Add(result);
            logger.LogInformation("Safe fuzzing run {RunId} case {CaseId}: {OperationId} {Mutation} → {Status} in {ElapsedMs} ms, outcome {Outcome}.",
                runId, fuzzCase.CaseId, fuzzCase.OperationId, fuzzCase.MutationType, result.StatusCode?.ToString() ?? "no response", result.ElapsedMs?.ToString("0") ?? "-", result.Outcome);
            if (result.Outcome == ApiFuzzOutcome.Unexpected5xx && settings.StopOnUnexpected5xx)
                stopReason = $"Stopped after an unexpected server error ({fuzzCase.CaseId}) because StopOnUnexpected5xx is on.";
            progress?.Invoke(Snapshot(report, results, running: true));
        }

        var executed = results.Count(r => r.Executed);
        var blocked = results.Count(r => r.Outcome == ApiFuzzOutcome.SafetyBlocked);
        var notExecuted = results.Count(r => r.Outcome == ApiFuzzOutcome.NotExecuted);
        var timeouts = results.Count(r => r.Outcome == ApiFuzzOutcome.Timeout);
        var connectionFailures = results.Count(r => r.Outcome == ApiFuzzOutcome.ConnectionFailure);
        var completeness = executed == 0 && (connectionFailures + timeouts) > 0 ? ApiFuzzCompleteness.Failed
            : executed == 0 ? ApiFuzzCompleteness.Partial
            : blocked + notExecuted + timeouts + connectionFailures > 0 || plan.CasesOverBudget > 0 ? ApiFuzzCompleteness.Partial
            : ApiFuzzCompleteness.Full;
        var reason = completeness switch
        {
            ApiFuzzCompleteness.Full => $"All {plan.Cases.Count} planned case(s) executed.",
            ApiFuzzCompleteness.Failed => "No case produced a response: the target was unreachable or every request timed out.",
            _ => string.Join(" ", new[]
            {
                stopReason, blocked > 0 ? $"{blocked} case(s) blocked by the safety guard." : null, notExecuted > 0 && stopReason is null ? $"{notExecuted} case(s) not executed." : null,
                timeouts > 0 ? $"{timeouts} case(s) timed out." : null, connectionFailures > 0 ? $"{connectionFailures} connection failure(s)." : null,
                plan.CasesOverBudget > 0 ? $"{plan.CasesOverBudget} eligible case(s) did not fit the request budget." : null,
            }.Where(x => x is not null)),
        };
        var final = Snapshot(report, results, running: false) with
        {
            CompletedAt = DateTimeOffset.UtcNow, Completeness = completeness, CompletenessReason = reason, Cancelled = ct.IsCancellationRequested || (stopReason?.StartsWith("Cancelled", StringComparison.Ordinal) ?? false),
        };
        logger.LogInformation("Safe fuzzing run {RunId} finished: {Completeness}, {Executed}/{Planned} executed, {Findings} logical finding(s).", runId, completeness, executed, plan.Cases.Count, final.Findings.Count);
        return final;
    }

    private static ApiFuzzingReport Snapshot(ApiFuzzingReport report, List<ApiFuzzCaseResult> results, bool running) => report with
    {
        Results = [.. results], Running = running,
        CasesExecuted = results.Count(r => r.Executed), CasesBlocked = results.Count(r => r.Outcome == ApiFuzzOutcome.SafetyBlocked),
        Outcomes = results.GroupBy(r => r.Outcome).OrderBy(g => g.Key).ToDictionary(g => g.Key, g => g.Count()),
        Findings = ApiFuzzFindingAggregator.Aggregate(report.Cases, results),
    };

    private static List<string> Limitations(ApiFuzzingSettings settings) =>
    [
        ApiFuzzingWording.NotAPenetrationTest,
        ApiFuzzingWording.Scope,
        settings.BodyFuzzing ? $"{ApiFuzzingWording.BodyScope} Bodies ≤ {settings.MaxBodyBytes} bytes, flat (no nesting is generated)." : "Request-body fuzzing was off for this run.",
        $"Bounded: at most {settings.MaxTotalRequests} requests, {settings.MaxCasesPerOperation} per operation, one at a time, {settings.RequestDelayMs} ms apart, {settings.RequestTimeoutSeconds} s timeout, parameters ≤ {settings.MaxParameterLength} characters, GraphQL documents ≤ {settings.MaxPayloadBytes} bytes.",
        "Values are synthetic. Responses are scanned transiently for internal-detail indicators; no body, header value or credential is stored.",
        "A rejected request (4xx) is the expected behaviour for invalid input and is not a finding. Findings are robustness and contract evidence, not proof of exploitability.",
        "Not covered: write methods without an explicit read-only-body opt-in, state-changing operations, DELETE, GraphQL mutations, authorization between roles (see Authorization scenarios), rate limiting, dependency vulnerabilities (see Dependency Health).",
    ];

    private static ApiFuzzCaseResult NotExecuted(ApiFuzzCase c, string note) => new() { CaseId = c.CaseId, Outcome = ApiFuzzOutcome.NotExecuted, Note = note };
    private static ApiFuzzCaseResult Blocked(ApiFuzzCase c, string note) => new() { CaseId = c.CaseId, Outcome = ApiFuzzOutcome.SafetyBlocked, Note = note };

    private static string ApiTargetName(ApiReviewTarget t) => string.IsNullOrWhiteSpace(t.ServiceName) ? t.Url : t.ServiceName;

    /// <summary>Destination from the target's own origin + the contract path (prefixed with the target base path when the contract path is relative to it).</summary>
    internal static ApiSafeRequest BuildRequest(ApiFuzzCase c, ApiReviewTarget target, bool bodyTrusted = false)
    {
        if (c.Protocol == ApiFuzzProtocol.GraphQl)
            return new ApiSafeRequest("POST", target.Url, [], c.GraphQlQuery, c.ExpectSyntaxError);
        if (c.Location == ApiFuzzParameterLocation.Body)
        {
            var bodyBase = target.BasePath.TrimEnd('/');
            var bodyPath = bodyBase.Length > 0 && !c.Path.StartsWith(bodyBase + "/", StringComparison.OrdinalIgnoreCase) && !string.Equals(c.Path, bodyBase, StringComparison.OrdinalIgnoreCase) ? bodyBase + c.Path : c.Path;
            return new ApiSafeRequest(c.Method, $"{target.Origin}{bodyPath}", [], Body: c.Body, ContentType: c.OmitContentType ? null : c.ContentType,
                BodyApproval: new BodyFuzzApproval(bodyTrusted, c.BodyPolicy ?? BirkNext.RuntimeSecurity.BodyFuzzingPolicy.Disabled, c.OperationId));
        }
        var basePath = target.BasePath.TrimEnd('/');
        var path = basePath.Length > 0 && !c.Path.StartsWith(basePath + "/", StringComparison.OrdinalIgnoreCase) && !string.Equals(c.Path, basePath, StringComparison.OrdinalIgnoreCase)
            ? basePath + c.Path : c.Path;
        var query = c.Query.Count == 0 ? "" : "?" + string.Join("&", c.Query.Select(q => $"{Uri.EscapeDataString(q.Key)}={Uri.EscapeDataString(q.Value)}"));
        return new ApiSafeRequest(c.Method, $"{target.Origin}{path}{query}", c.Headers);
    }

    private async Task<ApiFuzzResponse> SendAsync(ApiSafeRequest request, TargetContext context, ApiFuzzingSettings settings, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.RequestTimeoutSeconds));
        if (context.Mode == ApiReviewAccessMode.AuthenticatedHttp)
        {
            try
            {
                var outcome = await gateway.ExecuteSafeRequestAsync(context.Identity, request, timeout.Token);
                if (!outcome.Executed || outcome.Result is null) return new(false, 0, null, null, null, null, null, [], false, outcome.Message);
                var r = outcome.Result;
                return new(true, r.StatusCode, r.ContentType, r.ElapsedMs, r.JsonValid, r.GraphQlErrorCount, r.GraphQlHasData, r.LeakIndicators, false, r.Outcome);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(false, 0, null, null, null, null, null, [], true, $"No response within {settings.RequestTimeoutSeconds} s."); }
        }
        try
        {
            using var message = new HttpRequestMessage(new HttpMethod(request.Method), request.Url);
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (request.GraphQlQuery is not null)
            {
                message.Content = new StringContent(JsonSerializer.Serialize(new { query = request.GraphQlQuery }), Encoding.UTF8, "application/json");
                message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/graphql-response+json"));
            }
            else if (request.Body is not null)
            {
                message.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(request.Body));
                if (request.ContentType is { } bodyType) message.Content.Headers.ContentType = new MediaTypeHeaderValue(bodyType) { CharSet = "utf-8" };
            }
            foreach (var (name, value) in request.Headers) message.Headers.TryAddWithoutValidation(name, value);
            var stopwatch = Stopwatch.StartNew();
            using var response = await publicClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var body = await ResponseBodyReader.ReadAsync(response, timeout.Token, maxInspected: 256 * 1024);
            stopwatch.Stop();
            var text = body.Text ?? "";
            bool? jsonValid = null; int? errors = null; bool? hasData = null;
            if (text.Length > 0 && (JsonBodyInspector.IsJsonMediaType(response.Content.Headers.ContentType?.MediaType) || request.GraphQlQuery is not null))
            {
                try
                {
                    using var doc = JsonDocument.Parse(text);
                    jsonValid = true;
                    if (request.GraphQlQuery is not null && doc.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        errors = doc.RootElement.TryGetProperty("errors", out var e) && e.ValueKind == JsonValueKind.Array ? e.GetArrayLength() : 0;
                        hasData = doc.RootElement.TryGetProperty("data", out var d) && d.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);
                    }
                }
                catch (JsonException) { jsonValid = false; }
            }
            return new(true, (int)response.StatusCode, response.Content.Headers.ContentType?.MediaType, Math.Round(stopwatch.Elapsed.TotalMilliseconds, 1), jsonValid, errors, hasData,
                JsonBodyInspector.LeakIndicators(text), false, $"HTTP {(int)response.StatusCode}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(false, 0, null, null, null, null, null, [], true, $"No response within {settings.RequestTimeoutSeconds} s."); }
        catch (OperationCanceledException) { return new(false, 0, null, null, null, null, null, [], false, "Cancelled."); }
        catch (HttpRequestException ex) { return new(false, 0, null, null, null, null, null, [], false, $"Connection failed ({ex.HttpRequestError})."); }
    }

    private async Task<(NormalizedContract? Contract, string? Hash, string Reason)> RestContractAsync(ApiReviewTarget target, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(target.ContractSource))
            return (null, null, "No OpenAPI contract is configured for this target (Swagger / OpenAPI URL). Contract fuzzing is not available; unknown endpoints are never fuzzed.");
        try
        {
            using var response = await publicClient.GetAsync(target.ContractSource, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) return (null, null, $"The OpenAPI contract returned HTTP {(int)response.StatusCode}; contract fuzzing is not available.");
            var body = await ResponseBodyReader.ReadAsync(response, ct, maxInspected: 10 * 1024 * 1024);
            if (body.Text is not { } json) return (null, null, $"The OpenAPI contract could not be read ({body.Evidence.Reason}).");
            var extraction = openApi.Extract(json);
            if (!extraction.Success || extraction.Contract is null) return (null, JsonBodyInspector.Hash(json), $"The contract could not be normalized ({extraction.ErrorMessage ?? "unknown error"}); only OpenAPI 3.x JSON is supported.");
            return (extraction.Contract, JsonBodyInspector.Hash(json), "");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return (null, null, $"The OpenAPI contract was not reachable ({ex.GetType().Name})."); }
    }

    private async Task<(GraphQlNormalizedContract? Schema, string? Source, string? Hash, string Reason)> GraphQlSchemaAsync(ApiReviewTarget target, ApiReviewRunRequest review, ApiReviewAccessMode mode, CancellationToken ct)
    {
        string? schemaJson = null;
        if (mode == ApiReviewAccessMode.AuthenticatedHttp)
        {
            var outcome = await gateway.FetchGraphQlSchemaAsync(review.Identity, target.Url, ct);
            schemaJson = outcome.SchemaJson;
        }
        else
        {
            try
            {
                using var message = new HttpRequestMessage(HttpMethod.Post, target.Url) { Content = new StringContent(JsonSerializer.Serialize(new { query = AuthenticatedApiExecutionService.IntrospectionQuery }), Encoding.UTF8, "application/json") };
                message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                using var response = await publicClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
                var body = await ResponseBodyReader.ReadAsync(response, ct, maxInspected: 4 * 1024 * 1024);
                if (body.Text is { } text && text.Contains("\"__schema\"", StringComparison.Ordinal)) schemaJson = text;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { logger.LogInformation("Safe fuzzing: introspection of {Target} failed ({Error}).", target.TargetId, ex.GetType().Name); }
        }
        if (schemaJson is not null && graphQl.Extract(schemaJson) is { Success: true, Contract: { } contract })
            return (contract, $"Introspection of {target.Url}", JsonBodyInspector.Hash(schemaJson), "");
        if (schemaArtifacts is not null && await schemaArtifacts.ResolveAsync(review.Environment.EnvironmentId, target.TargetId, ct) is { Schema: { } artifactSchema } stored)
            return (artifactSchema, $"Configured SDL {stored.Artifact.FileName}", stored.Artifact.ContentHash, "");
        return (null, null, null, "No GraphQL schema is available (introspection disabled or rejected and no configured schema artifact). Contract fuzzing is not available.");
    }

    /// <summary>Root fields the frontend was observed selecting (from the redacted observed documents), so they are fuzzed first.</summary>
    private static List<string> ObservedRootFieldNames(ApiReviewTarget target)
    {
        var fields = new List<string>();
        foreach (var document in target.Operations.Where(o => o.OperationType == GraphQlOperationType.Query && o.Document is not null).Select(o => o.Document!))
        {
            try
            {
                var parsed = HotChocolate.Language.Utf8GraphQLParser.Parse(document);
                foreach (var op in parsed.Definitions.OfType<HotChocolate.Language.OperationDefinitionNode>())
                    fields.AddRange(op.SelectionSet.Selections.OfType<HotChocolate.Language.FieldNode>().Select(f => f.Name.Value));
            }
            catch (HotChocolate.Language.SyntaxException) { }
        }
        return fields.Distinct(StringComparer.Ordinal).ToList();
    }
}
