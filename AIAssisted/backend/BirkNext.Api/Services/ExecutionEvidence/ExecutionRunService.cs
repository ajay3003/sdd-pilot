using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BirkNext.Api.Services.ApiQuality;
using BirkNext.ApiReview;
using BirkNext.ExecutionEvidence;
using BirkNext.LocalHttpsProxy;

namespace BirkNext.Api.Services.ExecutionEvidence;

/// <summary>
/// Runs a user-initiated set of REST/GraphQL calls against the selected Target Environment and records the evidence. Gates (never relaxed):
/// the API Quality Review environment safety policy (Local/Development/QA/Test only; production claims, production host markers and blocked
/// hosts refused; loopback allowed), paths relative to the target only, no credentials in call definitions, mutating calls only with an
/// explicit confirmation, subscriptions refused (no streaming transport). Nothing runs on page load, import or when a result is opened.
/// Authentication (attended session or the environment's unattended API credential) is resolved by <see cref="ExecutionCredentialResolver"/>
/// before anything is sent and applied per attempt inside the backend; a run whose credential cannot be used is Blocked, never sent anonymously.
/// </summary>
public sealed class ExecutionRunService(IServiceScopeFactory scopes, IHttpClientFactory http, IApiEnvironmentSafetyPolicy safety, TimeProvider? clock = null,
    ILogger<ExecutionRunService>? logger = null, ExecutionCredentialResolver? credentials = null)
{
    public const string ClientName = "execution-evidence";
    public const string CorrelationHeader = "X-BirkNext-Execution-Id";
    public const int MaxCalls = 500;
    public const int MaxReauthenticationWaitSeconds = 900;

    /// <summary>
    /// Authentication state shared by the calls of one run: the attended identity the run started with (a renewed session must be the same
    /// account) and the one-time budget for waiting on a renewed session.
    /// </summary>
    private sealed class RunAuthentication(int waitSeconds)
    {
        public string? Identity;
        public int WaitSecondsLeft = waitSeconds;
    }
    private static readonly ConcurrentDictionary<Guid, CancellationTokenSource> Running = new();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>
    /// The socket handler of the execution-evidence client: no redirects, no cookies. Its plaintext stream (after TLS) counts every request
    /// head actually written (<see cref="TransmissionCountingStream"/>), because the HTTP stack resends a body-less request when the connection
    /// closes before a response — one recorded attempt may be several wire transmissions, and the evidence says so.
    /// </summary>
    public static SocketsHttpHandler CreatePrimaryHandler() => new()
    {
        AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = System.Net.DecompressionMethods.All,
        PlaintextStreamFilter = (context, _) => ValueTask.FromResult<Stream>(new TransmissionCountingStream(context.PlaintextStream)),
    };

    /// <summary>Calls that may be retried automatically: GET, HEAD, OPTIONS, PUT, DELETE (idempotent by HTTP) and GraphQL queries. POST, PATCH and
    /// GraphQL mutations are never resent by BirkNext, whatever the retry policy says.</summary>
    public static bool Idempotent(ExecutionCallDefinition d) => d.Protocol == ExecutionProtocol.GraphQl
        ? OperationType(d.GraphQlQuery) == "query"
        : d.Method.ToUpperInvariant() is "GET" or "HEAD" or "OPTIONS" or "PUT" or "DELETE";

    public static bool Mutating(ExecutionCallDefinition d) => d.Protocol == ExecutionProtocol.GraphQl
        ? OperationType(d.GraphQlQuery) == "mutation"
        : d.Method.ToUpperInvariant() is "POST" or "PUT" or "PATCH" or "DELETE";

    /// <summary>The operation type of a GraphQL document (first operation keyword; a bare selection set is a query).</summary>
    public static string OperationType(string? document)
    {
        var text = string.Join('\n', (document ?? "").Split('\n').Select(l => l.Split('#')[0])).TrimStart();
        return text.StartsWith("mutation", StringComparison.Ordinal) ? "mutation" : text.StartsWith("subscription", StringComparison.Ordinal) ? "subscription" : "query";
    }

    /// <summary>Why one call may not be sent (null = it may).</summary>
    public static string? CallBlock(ExecutionCallDefinition d, ExecutionRunRequest request)
    {
        if (string.IsNullOrWhiteSpace(d.Path) || !d.Path.StartsWith('/') || d.Path.StartsWith("//", StringComparison.Ordinal) || d.Path.Contains("://", StringComparison.Ordinal) || d.Path.Contains('\\'))
            return "The path must be relative to the Target Environment (start with a single '/', no scheme or host).";
        if (d.Headers.Keys.FirstOrDefault(ExecutionEvidenceRedaction.IsSensitiveHeader) is { } header)
            return $"The call definition carries a credential header ({header}). Credentials are never part of a test definition; use the Target Environment's authentication.";
        if (d.Protocol == ExecutionProtocol.Rest && d.Method.ToUpperInvariant() is not ("GET" or "HEAD" or "OPTIONS" or "POST" or "PUT" or "PATCH" or "DELETE"))
            return $"HTTP method {d.Method} is not supported.";
        if (d.Protocol == ExecutionProtocol.GraphQl && string.IsNullOrWhiteSpace(d.GraphQlQuery)) return "A GraphQL call needs a query document.";
        if (d.Protocol == ExecutionProtocol.GraphQl && OperationType(d.GraphQlQuery) == "subscription")
            return "GraphQL subscriptions need a streaming transport (WebSocket/SSE), which execution evidence does not support; not sent.";
        if (d.Protocol == ExecutionProtocol.GraphQl && d.GraphQlVariablesJson is { Length: > 0 } vars && TryJson(vars) is not JsonObject)
            return "GraphQL variables must be a JSON object.";
        if (Mutating(d) && !request.ConfirmMutatingCalls)
            return "This call can change data (mutating method or GraphQL mutation). It runs only when mutating calls are explicitly confirmed for the run.";
        if (d.Extract.Count > ExecutionOutputs.MaxExtractions) return $"A call provides at most {ExecutionOutputs.MaxExtractions} outputs.";
        if (d.Extract.Select(ExecutionOutputs.DefinitionProblem).FirstOrDefault(p => p is not null) is { } outputProblem) return outputProblem;
        if (d.Extract.GroupBy(e => e.Name, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1) is { } dup) return $"Output {dup.Key} is defined twice.";
        if (d.StepId is { } step && !ExecutionOutputs.StepId().IsMatch(step)) return "A step id is letters, digits, '_' or '-' (max 64).";
        return null;
    }

    /// <summary>
    /// Why a chained call may not run in this order (null = it may): every step it depends on — explicitly or through an output placeholder —
    /// must be an earlier call of the run with a unique step id. A later or unknown step (which includes every cycle) is refused.
    /// </summary>
    private static string? OrderBlock(ExecutionCallDefinition d, IReadOnlyList<ExecutionCallDefinition> earlier)
    {
        var known = earlier.Select(e => e.StepId).OfType<string>().ToHashSet(StringComparer.Ordinal);
        if (d.StepId is { } id && known.Contains(id)) return $"Step id {id} is used twice in the run.";
        var unknown = d.DependsOn.Concat(ExecutionOutputs.ReferencedSteps(d)).Where(s => !known.Contains(s)).Distinct(StringComparer.Ordinal).ToList();
        return unknown.Count == 0 ? null
            : $"It depends on step(s) {string.Join(", ", unknown)} that do not run before it (unknown, later, or a cycle); it is never sent with a guessed value.";
    }

    private static JsonNode? TryJson(string text) { try { return JsonNode.Parse(text); } catch (JsonException) { return null; } }

    public async Task<ExecutionRunStartResult> StartAsync(ExecutionRunRequest request, string initiator, CancellationToken ct)
    {
        var blockers = new List<string>();
        if (request.Calls.Count == 0) blockers.Add("A run needs at least one call.");
        if (request.Calls.Count > MaxCalls) blockers.Add($"A run has at most {MaxCalls} calls.");
        if (string.IsNullOrWhiteSpace(request.EnvironmentId)) blockers.Add("Select a Target Environment.");
        Uri? origin = Uri.TryCreate(request.TargetUrl, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" && string.IsNullOrEmpty(u.UserInfo)
            ? new Uri(u.GetLeftPart(UriPartial.Authority)) : null;
        if (origin is null) blockers.Add("The Target Environment URL is not an absolute http(s) URL without credentials.");
        var decision = safety.Evaluate(new ApiReviewRunRequest
        {
            Environment = new() { EnvironmentId = request.EnvironmentId, EnvironmentType = request.EnvironmentType, TargetUrl = request.TargetUrl, IsProduction = request.IsProduction },
            Identity = new(AuthenticatedTestingMethod.ManagedEdgeCdp, request.ProfileId, request.ContextFingerprint),
        });
        if (!decision.ActiveTestingAllowed) blockers.Add(decision.Reason);
        var authKind = ExecutionCredentialResolver.Effective(request);
        var auth = origin is null ? new ExecutionCredentialResolver.Prepared(ExecutionCredentialResolver.Label(authKind), "", null)
            : credentials?.Prepare(request, origin) ?? (authKind == ExecutionAuthenticationKind.None ? new("None", "none", null)
                : new(ExecutionCredentialResolver.Label(authKind), "", "Authenticated execution is not available in this BirkNext instance."));
        if (auth.Problem is { } authProblem) blockers.Add(authProblem);
        if (request.Calls.Count is 0 or > MaxCalls || string.IsNullOrWhiteSpace(request.EnvironmentId)) return new(null, ExecutionState.Blocked, string.Join(" ", blockers), blockers);

        var now = _clock.GetUtcNow();
        var runId = Guid.NewGuid();
        var calls = request.Calls.Select((d, i) =>
        {
            var why = blockers.Count > 0 ? string.Join(" ", blockers) : CallBlock(d, request) ?? OrderBlock(d, request.Calls.Take(i).ToList());
            return new ExecutionCallRecord { CallId = Guid.NewGuid(), Sequence = i + 1, Definition = Sanitize(d), State = why is null ? ExecutionState.Planned : ExecutionState.Blocked, Reason = why };
        }).ToList();
        var summary = new ExecutionRunSummary
        {
            RunId = runId, Name = string.IsNullOrWhiteSpace(request.Name) ? $"Run {now:yyyy-MM-dd HH:mm}" : request.Name.Trim(), EnvironmentId = request.EnvironmentId,
            EnvironmentType = decision.EnvironmentType, TargetOrigin = origin?.ToString() ?? "", State = calls.All(c => c.State == ExecutionState.Blocked) ? ExecutionState.Blocked : ExecutionState.Running,
            StartedAt = now, CapturePolicy = request.CapturePolicy, RedactionVersion = ExecutionEvidenceRedaction.Version, Initiator = initiator,
            AuthenticationMode = auth.Mode, AuthenticationReference = auth.Reference,
            SourceSnapshotId = request.SourceSnapshotId, ContractRefs = request.ContractRefs, ChainRef = request.ChainRef,
            ReauthenticationWaitSeconds = authKind == ExecutionAuthenticationKind.AttendedSession ? Math.Clamp(request.ReauthenticationWaitSeconds, 0, MaxReauthenticationWaitSeconds) : 0,
            StopOnFirstFailure = request.StopOnFirstFailure,
        };
        using (var scope = scopes.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<ExecutionEvidenceStore>();
            await store.CreateAsync(summary, calls, ct);
            // Without a run-level blocker the calls' own reasons say why (for example the missing data-changing confirmation), never a bare "blocked".
            if (summary.State == ExecutionState.Blocked)
            {
                await store.CompleteAsync(runId, ExecutionState.Blocked, ct);
                var why = blockers.Count > 0 ? string.Join(" ", blockers)
                    : "Every call was blocked before sending: " + string.Join(" ", calls.Select(c => c.Reason).Where(r => r is { Length: > 0 }).Distinct(StringComparer.Ordinal).Take(5));
                return new(runId, ExecutionState.Blocked, why, blockers);
            }
        }
        var cts = new CancellationTokenSource();
        Running[runId] = cts;
        // The originals are sent; only the redacted definitions are persisted.
        var originals = request.Calls.ToList();
        _ = Task.Run(() => ExecuteAsync(runId, origin!, calls, originals, request, cts.Token));
        return new(runId, ExecutionState.Running, $"Run started: {calls.Count(c => c.State == ExecutionState.Planned)} call(s) will be sent, {calls.Count(c => c.State == ExecutionState.Blocked)} blocked.", blockers);
    }

    public static bool Cancel(Guid runId) { if (!Running.TryGetValue(runId, out var cts)) return false; cts.Cancel(); return true; }
    public static bool IsRunning(Guid runId) => Running.ContainsKey(runId);

    /// <summary>Removes values a definition must never persist: credential headers (also refused by <see cref="CallBlock"/>).</summary>
    private static ExecutionCallDefinition Sanitize(ExecutionCallDefinition d) => d with
    {
        Headers = d.Headers.Where(h => !ExecutionEvidenceRedaction.IsSensitiveHeader(h.Key)).ToDictionary(h => h.Key, h => ExecutionEvidenceRedaction.Text(h.Value).Text, StringComparer.OrdinalIgnoreCase),
        Body = d.Body is null ? null : ExecutionEvidenceRedaction.Body(Encoding.UTF8.GetBytes(d.Body), Encoding.UTF8.GetByteCount(d.Body), d.ContentType ?? "application/json", CapturePolicy.FullPermittedBody, 64 * 1024, false).Text,
        GraphQlVariablesJson = d.GraphQlVariablesJson is null ? null : ExecutionEvidenceRedaction.Body(Encoding.UTF8.GetBytes(d.GraphQlVariablesJson), d.GraphQlVariablesJson.Length, "application/json", CapturePolicy.FullPermittedBody, 64 * 1024, false).Text,
    };

    private async Task ExecuteAsync(Guid runId, Uri origin, List<ExecutionCallRecord> calls, List<ExecutionCallDefinition> originals, ExecutionRunRequest request, CancellationToken runToken)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<ExecutionEvidenceStore>();
            var client = http.CreateClient(ClientName);
            var authentication = new RunAuthentication(ExecutionCredentialResolver.Effective(request) == ExecutionAuthenticationKind.AttendedSession
                ? Math.Clamp(request.ReauthenticationWaitSeconds, 0, MaxReauthenticationWaitSeconds) : 0);
            string? stopped = null;
            // Chained runs: the final state and the extracted values (memory only) of every step that ran, for its dependents.
            var stepStates = new Dictionary<string, ExecutionState>(StringComparer.Ordinal);
            var stepOutputs = new Dictionary<string, Dictionary<string, JsonNode?>>(StringComparer.Ordinal);
            foreach (var planned in calls.Where(c => c.State != ExecutionState.Planned && c.Definition.StepId is not null)) stepStates[planned.Definition.StepId!] = planned.State;
            foreach (var planned in calls)
            {
                if (planned.State != ExecutionState.Planned) continue;
                if (runToken.IsCancellationRequested)
                {
                    await store.SaveCallAsync(runId, planned with { State = ExecutionState.Cancelled, Reason = "The run was cancelled before this call was sent." }, CancellationToken.None);
                    continue;
                }
                if (stopped is not null)
                {
                    await store.SaveCallAsync(runId, planned with { State = ExecutionState.Skipped, Reason = stopped }, CancellationToken.None);
                    continue;
                }
                var original = originals[planned.Sequence - 1];
                var step = planned.Definition.StepId;
                // A dependent step runs only when every step it depends on passed; its step outputs are resolved now, never guessed.
                var dependencies = planned.Definition.DependsOn.Concat(ExecutionOutputs.ReferencedSteps(original)).Distinct(StringComparer.Ordinal).ToList();
                if (dependencies.FirstOrDefault(s => !stepStates.TryGetValue(s, out var st) || st != ExecutionState.Passed) is { } upstream)
                {
                    var upstreamState = stepStates.TryGetValue(upstream, out var us) ? us.ToString() : "without a result";
                    await store.SaveCallAsync(runId, planned with { State = ExecutionState.Blocked, CompletedAt = _clock.GetUtcNow(),
                        Reason = $"Not sent: it depends on step {upstream}, which ended {upstreamState}." }, CancellationToken.None);
                    if (step is not null) stepStates[step] = ExecutionState.Blocked;
                    if (request.StopOnFirstFailure) stopped = $"Not sent: the run stops at the first failure, and call {planned.Sequence} ({planned.Definition.Name}) was blocked by its dependency.";
                    continue;
                }
                var (resolved, missingOutputs, used) = ExecutionOutputs.Resolve(original, stepOutputs);
                if (missingOutputs.Count > 0)
                {
                    await store.SaveCallAsync(runId, planned with { State = ExecutionState.Blocked, CompletedAt = _clock.GetUtcNow(),
                        Reason = $"Not sent: output(s) {string.Join(", ", missingOutputs)} of an earlier step are not available; nothing is substituted for them." }, CancellationToken.None);
                    if (step is not null) stepStates[step] = ExecutionState.Blocked;
                    continue;
                }
                var record = planned with
                {
                    State = ExecutionState.Running, StartedAt = _clock.GetUtcNow(),
                    Definition = used.Count == 0 ? planned.Definition : planned.Definition with { ResolvedValues = [.. planned.Definition.ResolvedValues.Where(v => used.All(u => u.Name != v.Name)), .. used] },
                };
                await store.SaveCallAsync(runId, record, CancellationToken.None);
                var outputs = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
                record = await RunCallAsync(client, store, authentication, runId, origin, record, resolved, request, runToken, outputs);
                await store.SaveCallAsync(runId, record, CancellationToken.None);
                if (step is not null) { stepStates[step] = record.State; if (record.State == ExecutionState.Passed) stepOutputs[step] = outputs; }
                if (request.StopOnFirstFailure && record.State is ExecutionState.Failed or ExecutionState.Error or ExecutionState.TimedOut or ExecutionState.Blocked)
                    stopped = $"Not sent: the run stops at the first failure, and call {record.Sequence} ({record.Definition.Name}) ended {record.State}.";
            }
            await store.CompleteAsync(runId, null, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger?.LogError("Execution run {RunId} stopped unexpectedly: {Error}", runId, ex.GetType().Name);
            try { using var scope = scopes.CreateScope(); await scope.ServiceProvider.GetRequiredService<ExecutionEvidenceStore>().RecoverInterruptedAsync(CancellationToken.None); }
            catch (Exception) { /* the startup recovery will mark it Interrupted */ }
        }
        finally { if (Running.TryRemove(runId, out var cts)) cts.Dispose(); }
    }

    private async Task<ExecutionCallRecord> RunCallAsync(HttpClient client, ExecutionEvidenceStore store, RunAuthentication authentication, Guid runId, Uri origin, ExecutionCallRecord record,
        ExecutionCallDefinition d, ExecutionRunRequest run, CancellationToken runToken, Dictionary<string, JsonNode?>? outputValues = null)
    {
        var authenticated = ExecutionCredentialResolver.Effective(run) != ExecutionAuthenticationKind.None && !d.WithoutAuthentication;
        var authBlocked = false;
        var policy = run.CapturePolicy;
        var reports = new List<ContractValidationReport>();
        using var validationScope = d.ContractCheck == ContractCheckMode.None ? null : scopes.CreateScope();
        var validator = validationScope?.ServiceProvider.GetService<IExecutionContractValidator>();
        if (validator is not null && await validator.RequestAsync(d, runToken) is { } requestReport)
        {
            reports.Add(requestReport);
            if (d.ContractCheck == ContractCheckMode.Enforce && requestReport.State == ContractValidationState.Invalid)
                return record with
                {
                    State = ExecutionState.Blocked, CompletedAt = _clock.GetUtcNow(), ContractValidation = reports,
                    Reason = $"Not sent: the request violates the contract. {requestReport.Summary} {string.Join(" ", requestReport.Findings.Take(3).Select(f => $"{f.InstancePath}: {f.Message}"))}".Trim(),
                };
        }
        var attempts = new List<ExecutionAttempt>();
        byte[]? lastRaw = null;
        string? retryReason = null;
        var max = Math.Clamp(d.Retry.MaxAttempts, 1, 5);
        for (var n = 1; n <= max; n++)
        {
            var started = _clock.GetUtcNow();
            var slot = new EvidenceCaptureSlot { Policy = policy };
            using var message = Build(origin, d);
            message.Options.Set(EvidenceCaptureSlot.Key, slot);
            var correlation = $"{runId:N}.{record.Sequence}.{n}";
            message.Headers.TryAddWithoutValidation(CorrelationHeader, correlation);
            string? session = null;
            if (authenticated)
            {
                // Re-validated per attempt: the attended session may have expired, an unattended token is refreshed when due.
                var applied = await ApplyCredentialAsync(message, store, authentication, runId, record, run, runToken);
                if (applied.Problem is not null)
                {
                    authBlocked = !runToken.IsCancellationRequested;
                    attempts.Add(new ExecutionAttempt { AttemptNumber = n, StartedAt = started, CompletedAt = _clock.GetUtcNow(), Transport = runToken.IsCancellationRequested ? TransportResult.Cancelled : TransportResult.NotSent,
                        RetryReason = retryReason, Error = runToken.IsCancellationRequested ? "The run was cancelled while waiting for authentication; not sent." : $"Not sent: {applied.Problem}" });
                    break;
                }
                slot.CredentialHeaders = applied.Headers;
                session = applied.Session;
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(runToken);
            timeout.CancelAfter(Math.Clamp(d.TimeoutMs, 500, 120000));
            TransportResult transport; string? error = null;
            try
            {
                using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                transport = TransportResult.ResponseReceived;
            }
            catch (OperationCanceledException) when (runToken.IsCancellationRequested) { transport = TransportResult.Cancelled; error = "The run was cancelled during this attempt."; }
            catch (OperationCanceledException) { transport = TransportResult.TimedOut; error = $"No complete response within {d.TimeoutMs} ms."; }
            catch (HttpRequestException ex) { transport = TransportResult.ConnectionFailed; error = $"Connection failed ({ex.HttpRequestError})."; }
            var response2 = slot.Response;
            if (transport == TransportResult.ResponseReceived && response2 is not null)
            {
                var json = ExecutionAssertions.ParseJson(slot.RawResponse, out var invalid);
                var expectsJson = (response2.Headers.FirstOrDefault(h => h.Name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))?.Value ?? "").Contains("json", StringComparison.OrdinalIgnoreCase);
                response2 = response2 with { InvalidJson = invalid && (expectsJson || d.Protocol == ExecutionProtocol.GraphQl) };
                if (d.Protocol == ExecutionProtocol.GraphQl)
                {
                    var env = ExecutionAssertions.GraphQl(json);
                    response2 = response2 with { GraphQlHasData = env.Parsed ? env.HasData : null, GraphQlErrorCount = env.Parsed ? env.ErrorCount : null,
                        GraphQlErrorMessages = env.Messages, GraphQlErrorPaths = env.Paths, GraphQlErrorCodes = env.Codes };
                }
                lastRaw = slot.RawResponse;
            }
            else { response2 = null; lastRaw = null; }
            var request = slot.Request is { } r && d.Protocol == ExecutionProtocol.GraphQl
                ? r with { GraphQlOperationName = d.GraphQlOperationName, GraphQlOperationType = OperationType(d.GraphQlQuery) } : slot.Request;
            var transmissions = TransmissionCountingStream.Take(correlation);
            if (transmissions > 1) error = $"{error} The HTTP stack transmitted this request {transmissions} times (it resends a request without a body when the connection closes before a response).".Trim();
            // A data-changing request that reached the network without an answer may or may not have been applied: say so, never guess.
            if (!Idempotent(d) && transmissions > 0 && transport is TransportResult.TimedOut or TransportResult.ConnectionFailed)
                error = $"{error} Outcome unknown: the request was sent but no response arrived, so it may or may not have changed data; it is not sent again.".Trim();
            var wanted = n < max && transport is not TransportResult.Cancelled
                && (transport is TransportResult.ConnectionFailed or TransportResult.TimedOut && d.Retry.RetryOnTransportError || response2 is not null && d.Retry.RetryOnStatus?.Contains(response2.StatusCode) == true);
            if (wanted && !Idempotent(d)) error = $"{error} Not retried: {(d.Protocol == ExecutionProtocol.GraphQl ? "a GraphQL mutation" : d.Method.ToUpperInvariant())} can change data and is never resent automatically.".Trim();
            attempts.Add(new ExecutionAttempt
            {
                AttemptNumber = n, StartedAt = started, CompletedAt = _clock.GetUtcNow(), Transport = request is null && transport != TransportResult.ResponseReceived ? TransportResult.NotSent : transport,
                Request = request, Response = response2, RetryReason = retryReason, Transmissions = transmissions, AuthenticationSession = session, Error = error,
            });
            if (!wanted || !Idempotent(d)) break;
            retryReason = transport is TransportResult.ConnectionFailed or TransportResult.TimedOut ? $"Retry after {transport}" : $"Retry after HTTP {response2!.StatusCode}";
            if (d.Retry.DelayMs > 0) { try { await Task.Delay(Math.Clamp(d.Retry.DelayMs, 0, 10000), runToken); } catch (OperationCanceledException) { break; } }
        }
        var last = attempts[^1];
        ContractValidationReport? responseReport = null;
        if (validator is not null && last.Response is { } received) { responseReport = await validator.ResponseAsync(d, received, lastRaw, runToken); if (responseReport is not null) reports.Add(responseReport); }
        var results = ExecutionAssertions.Evaluate([.. d.Assertions.Where(a => a.Kind != AssertionKind.ContractSchema)], last.Response, lastRaw, d.Protocol);
        foreach (var a in d.Assertions.Where(a => a.Kind == AssertionKind.ContractSchema))
            results.Add(responseReport is null
                ? new(a.Id, a.Kind, a.Operator, a.Target, a.Expected, null, AssertionOutcome.NotAssessed, last.Response is null ? "No response was received." : "The call has no contract reference to validate against.")
                : new(a.Id, a.Kind, a.Operator, a.Target, a.Expected ?? "valid", responseReport.State == ContractValidationState.Valid && responseReport.Unsupported.Count > 0 ? "valid (partially evaluated)" : responseReport.State.ToString().ToLowerInvariant(),
                    responseReport.State == ContractValidationState.Invalid ? AssertionOutcome.Failed
                    : responseReport.State == ContractValidationState.Valid && responseReport.Unsupported.Count == 0 ? AssertionOutcome.Passed : AssertionOutcome.NotAssessed,
                    responseReport.Summary));
        var state = ExecutionAssertions.Verdict(last, results);
        // Outputs are extracted only from a call that passed: a failed step's values are never handed to its dependents.
        List<ExtractedOutput> extracted = [];
        string? outputProblem = null;
        if (d.Extract.Count > 0 && state == ExecutionState.Passed)
        {
            var (found, values) = ExecutionOutputs.Extract(d.Extract, last.Response, lastRaw);
            extracted = found;
            foreach (var (name, value) in values) outputValues?.TryAdd(name, value);
            if (found.FirstOrDefault(o => !o.Found) is { } miss) { state = ExecutionState.Failed; outputProblem = $"Output {miss.Name} could not be extracted: {miss.Problem}."; }
        }
        return record with
        {
            Attempts = attempts, Assertions = results, State = state, CompletedAt = _clock.GetUtcNow(), ContractValidation = reports, Outputs = extracted,
            Authentication = AuthenticationOutcome(run, d, attempts, authBlocked),
            Reason = outputProblem ?? state switch
            {
                ExecutionState.NotAssessed when d.Assertions.Count == 0 => "No assertions are configured, so the result is not assessed (never a pass by default).",
                ExecutionState.NotAssessed => "Some assertions could not be evaluated.",
                ExecutionState.Failed => $"{results.Count(x => x.Outcome == AssertionOutcome.Failed)} of {results.Count} assertion(s) failed.",
                ExecutionState.Passed => $"All {results.Count} assertion(s) passed.",
                _ => last.Error,
            },
        };
    }

    /// <summary>
    /// Applies the run's credential to one attempt. An attended session that expired mid-run is waited for (once per run, up to the run's
    /// budget) while the call shows that it waits; the renewed credential must belong to the identity the run started with. Never falls back
    /// to sending without the credential.
    /// </summary>
    private async Task<ExecutionCredentialResolver.Applied> ApplyCredentialAsync(HttpRequestMessage message, ExecutionEvidenceStore store, RunAuthentication authentication, Guid runId,
        ExecutionCallRecord record, ExecutionRunRequest run, CancellationToken runToken)
    {
        if (credentials is null) return new([], "Authenticated execution is not available in this BirkNext instance.");
        var applied = await credentials.ApplyAsync(run, message, runToken);
        if (applied.Problem is not null && applied.SessionUnavailable && authentication.WaitSecondsLeft > 0)
        {
            var until = _clock.GetUtcNow().AddSeconds(authentication.WaitSecondsLeft);
            await store.SaveCallAsync(runId, record with { Reason = $"Waiting for the attended session to be renewed: sign in to the application again (until {until:HH:mm:ss} UTC). Nothing is sent without it." }, CancellationToken.None);
            logger?.LogInformation("Execution run {RunId} waits up to {Seconds} s for a renewed attended session.", runId, authentication.WaitSecondsLeft);
            while (applied.Problem is not null && applied.SessionUnavailable && _clock.GetUtcNow() < until && !runToken.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromMilliseconds(500), runToken); } catch (OperationCanceledException) { break; }
                applied = await credentials.ApplyAsync(run, message, runToken);
            }
            authentication.WaitSecondsLeft = Math.Max(0, (int)(until - _clock.GetUtcNow()).TotalSeconds);
            if (applied.Problem is not null && applied.SessionUnavailable && !runToken.IsCancellationRequested)
            {
                authentication.WaitSecondsLeft = 0;
                applied = applied with { Problem = "The attended session expired and was not renewed in time; the remaining authenticated calls are not sent." };
            }
        }
        if (applied.Problem is null && applied.Identity is { } identity)
        {
            if (authentication.Identity is null) authentication.Identity = identity;
            else if (!string.Equals(authentication.Identity, identity, StringComparison.Ordinal))
            {
                message.Headers.Authorization = null;
                return new([], "The attended session now belongs to a different account than the one this run started with; not sent with that account's credential.");
            }
        }
        return applied;
    }

    /// <summary>The per-call authentication outcome (see <see cref="CallAuthenticationOutcome"/>). GraphQL reports authentication in errors with HTTP 200.</summary>
    internal static CallAuthenticationOutcome AuthenticationOutcome(ExecutionRunRequest run, ExecutionCallDefinition d, IReadOnlyList<ExecutionAttempt> attempts, bool authBlocked)
    {
        if (ExecutionCredentialResolver.Effective(run) == ExecutionAuthenticationKind.None) return CallAuthenticationOutcome.NotApplicable;
        if (d.WithoutAuthentication) return CallAuthenticationOutcome.AnonymousByDesign;
        if (authBlocked) return CallAuthenticationOutcome.BlockedForAuthentication;
        var last = attempts.LastOrDefault(a => a.AuthenticationSession is not null);
        if (last is null) return CallAuthenticationOutcome.NotApplicable;
        var codes = last.Response?.GraphQlErrorCodes ?? [];
        return last.Response?.StatusCode == 401 || codes.Contains("UNAUTHENTICATED", StringComparer.OrdinalIgnoreCase) ? CallAuthenticationOutcome.FailedAuthentication
            : last.Response?.StatusCode == 403 || codes.Contains("FORBIDDEN", StringComparer.OrdinalIgnoreCase) ? CallAuthenticationOutcome.AuthorizationDenied
            : CallAuthenticationOutcome.Authenticated;
    }

    private static HttpRequestMessage Build(Uri origin, ExecutionCallDefinition d)
    {
        var query = d.Query.Count == 0 ? "" : "?" + string.Join('&', d.Query.Select(q => $"{Uri.EscapeDataString(q.Key)}={Uri.EscapeDataString(q.Value)}"));
        var uri = new Uri(origin, d.Path + query);
        HttpRequestMessage message;
        if (d.Protocol == ExecutionProtocol.GraphQl)
        {
            var payload = new JsonObject { ["query"] = d.GraphQlQuery, ["variables"] = d.GraphQlVariablesJson is { Length: > 0 } v ? TryJson(v) : null };
            if (!string.IsNullOrWhiteSpace(d.GraphQlOperationName)) payload["operationName"] = d.GraphQlOperationName;
            message = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json") };
        }
        else
        {
            message = new HttpRequestMessage(new HttpMethod(d.Method.ToUpperInvariant()), uri);
            if (d.Body is not null) message.Content = new StringContent(d.Body, Encoding.UTF8, MediaTypeHeaderValue.Parse(d.ContentType ?? "application/json").MediaType!);
        }
        foreach (var (k, v) in d.Headers) if (!ExecutionEvidenceRedaction.IsSensitiveHeader(k)) message.Headers.TryAddWithoutValidation(k, v);
        return message;
    }
}

/// <summary>
/// Contract checks of a call (implemented by Contract Testing). Request: before sending; with <see cref="ContractCheckMode.Enforce"/> an invalid
/// request is not sent. Response: after the last attempt. Null = the call has no checkable contract reference.
/// </summary>
public interface IExecutionContractValidator
{
    Task<ContractValidationReport?> RequestAsync(ExecutionCallDefinition definition, CancellationToken ct);
    Task<ContractValidationReport?> ResponseAsync(ExecutionCallDefinition definition, CapturedResponse response, byte[]? raw, CancellationToken ct);
}

/// <summary>On start: runs left Running by a previous process become Interrupted. Never resends anything.</summary>
public sealed class ExecutionEvidenceRecoveryService(IServiceScopeFactory scopes, ILogger<ExecutionEvidenceRecoveryService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var recovered = await scope.ServiceProvider.GetRequiredService<ExecutionEvidenceStore>().RecoverInterruptedAsync(ct);
            if (recovered > 0) logger.LogInformation("Execution evidence: {Count} interrupted run(s) marked Interrupted after restart.", recovered);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning("Execution evidence recovery skipped: {Error}", ex.GetType().Name); }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
