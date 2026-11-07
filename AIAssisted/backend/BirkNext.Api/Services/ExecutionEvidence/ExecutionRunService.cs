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
/// </summary>
public sealed class ExecutionRunService(IServiceScopeFactory scopes, IHttpClientFactory http, IApiEnvironmentSafetyPolicy safety, TimeProvider? clock = null,
    ILogger<ExecutionRunService>? logger = null)
{
    public const string ClientName = "execution-evidence";
    public const string CorrelationHeader = "X-BirkNext-Execution-Id";
    public const int MaxCalls = 500;
    private static readonly ConcurrentDictionary<Guid, CancellationTokenSource> Running = new();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

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
        if (request.UseTargetAuthentication)
            return "Running with the Target Environment's captured credential is not available for execution evidence runs yet; the call was not sent without it.";
        return null;
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
        if (request.Calls.Count is 0 or > MaxCalls || string.IsNullOrWhiteSpace(request.EnvironmentId)) return new(null, ExecutionState.Blocked, string.Join(" ", blockers), blockers);

        var now = _clock.GetUtcNow();
        var runId = Guid.NewGuid();
        var calls = request.Calls.Select((d, i) =>
        {
            var why = blockers.Count > 0 ? string.Join(" ", blockers) : CallBlock(d, request);
            return new ExecutionCallRecord { CallId = Guid.NewGuid(), Sequence = i + 1, Definition = Sanitize(d), State = why is null ? ExecutionState.Planned : ExecutionState.Blocked, Reason = why };
        }).ToList();
        var summary = new ExecutionRunSummary
        {
            RunId = runId, Name = string.IsNullOrWhiteSpace(request.Name) ? $"Run {now:yyyy-MM-dd HH:mm}" : request.Name.Trim(), EnvironmentId = request.EnvironmentId,
            EnvironmentType = decision.EnvironmentType, TargetOrigin = origin?.ToString() ?? "", State = calls.All(c => c.State == ExecutionState.Blocked) ? ExecutionState.Blocked : ExecutionState.Running,
            StartedAt = now, CapturePolicy = request.CapturePolicy, RedactionVersion = ExecutionEvidenceRedaction.Version, Initiator = initiator,
            AuthenticationMode = request.UseTargetAuthentication ? "Target Environment credential (requested; not available)" : "None",
            SourceSnapshotId = request.SourceSnapshotId, ContractRefs = request.ContractRefs,
        };
        using (var scope = scopes.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<ExecutionEvidenceStore>();
            await store.CreateAsync(summary, calls, ct);
            if (summary.State == ExecutionState.Blocked) { await store.CompleteAsync(runId, ExecutionState.Blocked, ct); return new(runId, ExecutionState.Blocked, string.Join(" ", blockers.DefaultIfEmpty("Every call was blocked before sending.")), blockers); }
        }
        var cts = new CancellationTokenSource();
        Running[runId] = cts;
        // The originals are sent; only the redacted definitions are persisted.
        var originals = request.Calls.ToList();
        _ = Task.Run(() => ExecuteAsync(runId, origin!, calls, originals, request.CapturePolicy, cts.Token));
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

    private async Task ExecuteAsync(Guid runId, Uri origin, List<ExecutionCallRecord> calls, List<ExecutionCallDefinition> originals, CapturePolicy policy, CancellationToken runToken)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<ExecutionEvidenceStore>();
            var client = http.CreateClient(ClientName);
            foreach (var planned in calls)
            {
                if (planned.State != ExecutionState.Planned) continue;
                if (runToken.IsCancellationRequested)
                {
                    await store.SaveCallAsync(runId, planned with { State = ExecutionState.Cancelled, Reason = "The run was cancelled before this call was sent." }, CancellationToken.None);
                    continue;
                }
                var record = planned with { State = ExecutionState.Running, StartedAt = _clock.GetUtcNow() };
                await store.SaveCallAsync(runId, record, CancellationToken.None);
                record = await RunCallAsync(client, runId, origin, record, originals[record.Sequence - 1], policy, runToken);
                await store.SaveCallAsync(runId, record, CancellationToken.None);
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

    private async Task<ExecutionCallRecord> RunCallAsync(HttpClient client, Guid runId, Uri origin, ExecutionCallRecord record, ExecutionCallDefinition d, CapturePolicy policy, CancellationToken runToken)
    {
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
            message.Headers.TryAddWithoutValidation(CorrelationHeader, $"{runId:N}.{record.Sequence}.{n}");
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
            attempts.Add(new ExecutionAttempt
            {
                AttemptNumber = n, StartedAt = started, CompletedAt = _clock.GetUtcNow(), Transport = request is null && transport != TransportResult.ResponseReceived ? TransportResult.NotSent : transport,
                Request = request, Response = response2, Error = error, RetryReason = retryReason,
            });
            if (n == max || transport is TransportResult.Cancelled) break;
            retryReason = transport is TransportResult.ConnectionFailed or TransportResult.TimedOut && d.Retry.RetryOnTransportError ? $"Retry after {transport}"
                : response2 is not null && d.Retry.RetryOnStatus?.Contains(response2.StatusCode) == true ? $"Retry after HTTP {response2.StatusCode}" : null;
            if (retryReason is null) break;
            if (d.Retry.DelayMs > 0) { try { await Task.Delay(Math.Clamp(d.Retry.DelayMs, 0, 10000), runToken); } catch (OperationCanceledException) { break; } }
        }
        var last = attempts[^1];
        var results = ExecutionAssertions.Evaluate(d.Assertions, last.Response, lastRaw, d.Protocol);
        var state = ExecutionAssertions.Verdict(last, results);
        return record with
        {
            Attempts = attempts, Assertions = results, State = state, CompletedAt = _clock.GetUtcNow(),
            Reason = state switch
            {
                ExecutionState.NotAssessed when d.Assertions.Count == 0 => "No assertions are configured, so the result is not assessed (never a pass by default).",
                ExecutionState.NotAssessed => "Some assertions could not be evaluated.",
                ExecutionState.Failed => $"{results.Count(x => x.Outcome == AssertionOutcome.Failed)} of {results.Count} assertion(s) failed.",
                ExecutionState.Passed => $"All {results.Count} assertion(s) passed.",
                _ => last.Error,
            },
        };
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
