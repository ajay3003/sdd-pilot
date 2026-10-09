using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BirkNext.Api.Services.ApiQuality.Fuzzing;
using BirkNext.Api.Services.LocalHttpsProxy;
using BirkNext.ApiReview;
using BirkNext.LocalHttpsProxy;
using BirkNext.RuntimeSecurity;

namespace BirkNext.Api.Services.ApiQuality.Security;

/// <summary>
/// A server-side test identity for authorization scenarios (e.g. a future Entra test-identity token broker). It applies its own credential
/// to the outgoing request; the credential never leaves the provider, is never returned, logged or stored. None is registered by default.
/// </summary>
public interface IAuthorizationTestIdentityProvider
{
    string Alias { get; }
    /// <summary>Applies the credential, or returns why it cannot (expired, not configured). Null = applied.</summary>
    Task<string?> ApplyAsync(HttpRequestMessage request, CancellationToken ct);
}

public interface IAuthorizationScenarioService
{
    Task<AuthorizationRunReport> RunAsync(AuthorizationRunRequest request, CancellationToken ct = default);
}

/// <summary>
/// Executes explicit authorization scenarios: the same safe request (REST GET/HEAD or one GraphQL query without variables) once per
/// configured identity, each compared with its OWN explicit allow/deny expectation. Identities are never compared with each other and
/// result counts are never evidence. Runs only for server-registered trusted non-production targets, sequentially, within a small budget.
/// </summary>
public sealed class AuthorizationScenarioService(HttpClient publicClient, IAuthenticatedReviewGateway gateway, IApiEnvironmentSafetyPolicy safetyPolicy,
    ITrustedSecurityTargetRegistry trust, IEnumerable<IAuthorizationTestIdentityProvider> identityProviders, ILogger<AuthorizationScenarioService> logger) : IAuthorizationScenarioService
{
    public const int MaxRequests = 40;
    public const int RequestDelayMs = 250;
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private const int MaxBodyBytes = 256 * 1024;

    public async Task<AuthorizationRunReport> RunAsync(AuthorizationRunRequest request, CancellationToken ct = default)
    {
        var started = DateTimeOffset.UtcNow;
        var scenarios = request.Scenarios.Take(AuthorizationScenarioRules.MaxScenarios).ToList();
        var safety = safetyPolicy.Evaluate(request.Review);
        var decision = trust.Resolve(request.Review.Environment.EnvironmentId, scenarios.Select(s => s.Url));
        var expectations = new RuntimeSecurityExpectations { AuthorizationScenarios = scenarios };
        AuthorizationRunReport Report(List<AuthorizationObservation> observations, string? blocked) => new()
        {
            RunId = Guid.NewGuid().ToString("N")[..12], EnvironmentId = request.Review.Environment.EnvironmentId, EnvironmentName = request.Review.Environment.Name,
            Trust = decision, Safety = safety, Scenarios = scenarios, Observations = observations, ExpectationFingerprint = expectations.Fingerprint(),
            StartedAt = started, CompletedAt = DateTimeOffset.UtcNow, BlockedReason = blocked, Limitations = Limitations(),
        };
        if (scenarios.Count == 0) return Report([], "No authorization scenario is configured for this Target Environment (Security Expectations → Authorization scenarios).");
        if (!safety.ActiveTestingAllowed) return Report(Unavailable(scenarios, $"Blocked by safety policy: {safety.Reason}"), $"Blocked by safety policy: {safety.Reason}");
        if (!decision.Allowed) return Report(Unavailable(scenarios, decision.Reason), decision.Reason);

        var selectedOrigins = request.Review.Targets.Where(t => t.Selected).Select(t => t.Origin).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var capabilities = gateway.Resolve(request.Review.Identity);
        var observations = new List<AuthorizationObservation>();
        var sent = 0;
        foreach (var scenario in scenarios)
        {
            if (AuthorizationScenarioRules.Validate(scenario) is { } invalid)
            {
                observations.AddRange(scenario.Identities.Select(i => Observation(scenario, i, AuthorizationOutcome.ExecutionUnavailable, $"Invalid scenario: {invalid}")));
                continue;
            }
            var origin = CorsProbeRules.NormalizeOrigin(scenario.Url)!;
            if (!selectedOrigins.Any(o => string.Equals(CorsProbeRules.NormalizeOrigin(o), origin, StringComparison.OrdinalIgnoreCase)))
            {
                observations.AddRange(scenario.Identities.Select(i => Observation(scenario, i, AuthorizationOutcome.ExecutionUnavailable, "The scenario URL does not belong to a selected API target of this review.")));
                continue;
            }
            if (scenario.ApiType == AuthorizationScenarioApiType.GraphQl && ApiSafeRequestGuard.GraphQlQueryRejection(scenario.GraphQlQuery!, false, AuthorizationScenarioRules.MaxGraphQlBytes) is { } rejection)
            {
                observations.AddRange(scenario.Identities.Select(i => Observation(scenario, i, AuthorizationOutcome.ExecutionUnavailable, $"GraphQL safety: {rejection}")));
                continue;
            }
            foreach (var identity in scenario.Identities.Take(AuthorizationScenarioRules.MaxIdentitiesPerScenario))
            {
                if (ct.IsCancellationRequested) { observations.Add(Observation(scenario, identity, AuthorizationOutcome.NotVerified, "Cancelled before this request was sent.")); continue; }
                if (sent >= MaxRequests) { observations.Add(Observation(scenario, identity, AuthorizationOutcome.NotVerified, $"The request budget ({MaxRequests}) is spent.")); continue; }
                if (sent > 0)
                {
                    try { await Task.Delay(RequestDelayMs, ct); }
                    catch (OperationCanceledException) { observations.Add(Observation(scenario, identity, AuthorizationOutcome.NotVerified, "Cancelled before this request was sent.")); continue; }
                }
                var response = await ExecuteAsync(scenario, identity, request.Review.Identity, capabilities, ct);
                if (response.Unavailable is { } unavailable) { observations.Add(Observation(scenario, identity, AuthorizationOutcome.ExecutionUnavailable, unavailable)); continue; }
                sent++;
                var authError = response.ErrorCodes.Any(AuthorizationScenarioRules.IsAuthorizationErrorCode);
                var (outcome, reason) = AuthorizationScenarioRules.Classify(scenario, identity, response.Executed, response.Status,
                    scenario.ApiType == AuthorizationScenarioApiType.GraphQl ? authError : null, response.HasData, response.Errors, response.Failure);
                observations.Add(Observation(scenario, identity, outcome, reason) with
                {
                    StatusCode = response.Executed ? response.Status : null,
                    GraphQlAuthorizationError = scenario.ApiType == AuthorizationScenarioApiType.GraphQl && response.Executed ? authError : null,
                    GraphQlHasData = scenario.ApiType == AuthorizationScenarioApiType.GraphQl ? response.HasData : null,
                });
                logger.LogInformation("Authorization scenario {ScenarioId} identity {Alias} ({Role}): expected {Expected}, HTTP {Status}, outcome {Outcome}.",
                    scenario.ScenarioId, identity.Alias, identity.Role, identity.Expected, response.Executed ? response.Status : null, outcome);
            }
        }
        return Report(observations, null);
    }

    private sealed record Response(bool Executed, int? Status, bool? HasData, int? Errors, List<string> ErrorCodes, string? Failure = null, string? Unavailable = null);

    private async Task<Response> ExecuteAsync(AuthorizationScenario scenario, AuthorizationScenarioIdentity identity, AuthenticatedReviewIdentity reviewIdentity,
        AuthenticatedReviewCapabilities capabilities, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeout);
        var graphQl = scenario.ApiType == AuthorizationScenarioApiType.GraphQl;
        if (identity.Alias == AuthorizationIdentityAliases.ProxySession)
        {
            if (reviewIdentity.Method != AuthenticatedTestingMethod.LocalHttpsProxy || !capabilities.AuthenticatedApi)
                return new(false, null, null, null, [], Unavailable: "The proxy-session identity needs an active Local HTTPS proxy authenticated context for this Target Environment.");
            try
            {
                var outcome = graphQl
                    ? await gateway.ExecuteGraphQlQueryAsync(reviewIdentity, scenario.Url, scenario.GraphQlQuery!, timeout.Token)
                    : await gateway.ExecuteRestAsync(reviewIdentity, scenario.Method.Trim().ToUpperInvariant(), scenario.Url, timeout.Token);
                if (!outcome.Executed || outcome.Result is null)
                    return outcome.Status is AuthenticatedExecutionStatus.NoContext or AuthenticatedExecutionStatus.Expired or AuthenticatedExecutionStatus.OutOfScope
                        ? new(false, null, null, null, [], Unavailable: outcome.Message)
                        : new(false, null, null, null, [], Failure: outcome.Message);
                var r = outcome.Result;
                return new(true, r.StatusCode, r.GraphQlHasData, r.GraphQlErrorCount, r.GraphQlErrorCodes);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(false, null, null, null, [], Failure: $"No response within {RequestTimeout.TotalSeconds:0} s."); }
        }

        IAuthorizationTestIdentityProvider? provider = null;
        if (identity.Alias != AuthorizationIdentityAliases.Anonymous)
        {
            provider = identityProviders.FirstOrDefault(p => string.Equals(p.Alias, identity.Alias, StringComparison.Ordinal));
            if (provider is null)
                return new(false, null, null, null, [], Unavailable: $"No server-side identity provider is registered for '{identity.Alias}'. BirkNext never stores passwords or tokens; register a test-identity provider or use anonymous/proxy-session.");
        }
        try
        {
            using var message = graphQl
                ? new HttpRequestMessage(HttpMethod.Post, scenario.Url) { Content = new StringContent(JsonSerializer.Serialize(new { query = scenario.GraphQlQuery }), Encoding.UTF8, "application/json") }
                : new HttpRequestMessage(new HttpMethod(scenario.Method.Trim().ToUpperInvariant()), scenario.Url);
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (provider is not null && await provider.ApplyAsync(message, timeout.Token) is { } notApplied)
                return new(false, null, null, null, [], Unavailable: notApplied);
            using var response = await publicClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var status = (int)response.StatusCode;
            if (!graphQl) return new(true, status, null, null, []);
            var body = await ResponseBodyReader.ReadAsync(response, timeout.Token, maxInspected: MaxBodyBytes);
            try
            {
                using var document = JsonDocument.Parse(body.Text ?? "");
                var root = document.RootElement;
                var errors = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("errors", out var e) && e.ValueKind == JsonValueKind.Array ? e.GetArrayLength() : 0;
                var hasData = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object && d.EnumerateObject().Any(p => p.Value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined));
                return new(true, status, hasData, errors, GraphQlErrorCodeReader.Codes(root));
            }
            catch (JsonException) { return new(true, status, null, null, []); }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(false, null, null, null, [], Failure: $"No response within {RequestTimeout.TotalSeconds:0} s."); }
        catch (OperationCanceledException) { return new(false, null, null, null, [], Failure: "Cancelled."); }
        catch (HttpRequestException ex) { return new(false, null, null, null, [], Failure: $"Connection failed ({ex.HttpRequestError})."); }
    }

    private static AuthorizationObservation Observation(AuthorizationScenario s, AuthorizationScenarioIdentity i, AuthorizationOutcome outcome, string reason) => new()
    {
        ScenarioId = s.ScenarioId, Scenario = string.IsNullOrWhiteSpace(s.DisplayName) ? s.ScenarioId : s.DisplayName, IdentityAlias = i.Alias, Role = i.Role,
        Expected = i.Expected, Outcome = outcome, Reason = reason,
    };

    private static List<AuthorizationObservation> Unavailable(IEnumerable<AuthorizationScenario> scenarios, string reason) =>
        scenarios.SelectMany(s => s.Identities.Select(i => Observation(s, i, AuthorizationOutcome.ExecutionUnavailable, reason))).ToList();

    private static List<string> Limitations() =>
    [
        RuntimeSecurityWording.NotAPenetrationTest,
        "Each identity is compared with its own explicit allow/deny expectation. Result counts and differences between identities are never used as authorization evidence.",
        "Only safe requests are sent: REST GET/HEAD and GraphQL queries without variables. No mutation, no write method, never Production.",
        $"Bounded: at most {AuthorizationScenarioRules.MaxScenarios} scenarios, {AuthorizationScenarioRules.MaxIdentitiesPerScenario} identities each, {MaxRequests} requests, one at a time, {RequestDelayMs} ms apart, {RequestTimeout.TotalSeconds:0} s timeout.",
        "Identities: anonymous (no credential), proxy-session (the memory-only Local HTTPS proxy credential) and server-registered test-identity providers. Credentials are never stored, logged, returned or exported.",
        "HTTP 404 counts as denial only for identities where anti-disclosure is explicitly declared.",
    ];
}
