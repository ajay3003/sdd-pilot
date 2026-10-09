using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BirkNext.Integrations;

namespace BirkNext.Web.Services;

/// <summary>
/// Client for IQR → Integration journeys (every journey pack). Readiness, journeys, architecture rules and the journey catalog are open
/// metadata; runs and history need the same ActiveEventExecute permission as Active Event Testing. A run request carries only ids and the
/// confirmation — never an endpoint, namespace, topic, payload, credential or environment type.
/// </summary>
public interface IIntegrationJourneysApiService
{
    Task<ActiveEventApiResult<IReadOnlyList<IntegrationJourneyPackView>>> PacksAsync(string environmentId, CancellationToken ct = default);
    Task<ActiveEventApiResult<ArchitectureRuleReport>> RulesAsync(string environmentId, string packId, CancellationToken ct = default);
    Task<ActiveEventApiResult<IntegrationJourneyRun>> StartAsync(IntegrationJourneyRunRequest request, CancellationToken ct = default);
    Task<ActiveEventApiResult<IntegrationJourneyRun>> GetAsync(Guid runId, CancellationToken ct = default);
    Task<ActiveEventApiResult<IReadOnlyList<IntegrationJourneyRunSummary>>> HistoryAsync(string environmentId, string packId, string? journeyId, JourneyRunState? state, CancellationToken ct = default);
}

public sealed class IntegrationJourneysApiService(HttpClient http) : IIntegrationJourneysApiService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static string E(string value) => Uri.EscapeDataString(value);

    public async Task<ActiveEventApiResult<IReadOnlyList<IntegrationJourneyPackView>>> PacksAsync(string environmentId, CancellationToken ct = default) =>
        List(await GetAsync<List<IntegrationJourneyPackView>>($"api/integration-journeys/packs?environmentId={E(environmentId)}", ct));

    public Task<ActiveEventApiResult<ArchitectureRuleReport>> RulesAsync(string environmentId, string packId, CancellationToken ct = default) =>
        GetAsync<ArchitectureRuleReport>($"api/integration-journeys/packs/{E(packId)}/architecture-rules?environmentId={E(environmentId)}", ct);

    public async Task<ActiveEventApiResult<IntegrationJourneyRun>> StartAsync(IntegrationJourneyRunRequest request, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync("api/integration-journeys/runs", request, Json, ct);
        return await ReadAsync<IntegrationJourneyRun>(response, ct);
    }

    public Task<ActiveEventApiResult<IntegrationJourneyRun>> GetAsync(Guid runId, CancellationToken ct = default) =>
        GetAsync<IntegrationJourneyRun>($"api/integration-journeys/runs/{runId}", ct);

    public async Task<ActiveEventApiResult<IReadOnlyList<IntegrationJourneyRunSummary>>> HistoryAsync(string environmentId, string packId, string? journeyId, JourneyRunState? state,
        CancellationToken ct = default) =>
        List(await GetAsync<List<IntegrationJourneyRunSummary>>($"api/integration-journeys/runs?environmentId={E(environmentId)}&packId={E(packId)}" +
            (journeyId is { Length: > 0 } journey ? $"&journeyId={E(journey)}" : "") + (state is { } s ? $"&state={s}" : ""), ct));

    private async Task<ActiveEventApiResult<T>> GetAsync<T>(string url, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, ct);
        return await ReadAsync<T>(response, ct);
    }

    private static ActiveEventApiResult<IReadOnlyList<T>> List<T>(ActiveEventApiResult<List<T>> result) => new(result.Value, result.Error, result.Status);

    private static async Task<ActiveEventApiResult<T>> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return new(await response.Content.ReadFromJsonAsync<T>(Json, ct), null, response.StatusCode);
        var message = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "Sign-in required: journey runs and history need an authenticated BirkNext API token.",
            HttpStatusCode.Forbidden => "Not authorized: your account does not hold the ActiveEventExecute permission.",
            HttpStatusCode.NotFound => "Not found.",
            _ => null,
        };
        if (message is null)
        {
            try { message = (await response.Content.ReadFromJsonAsync<JsonElement>(Json, ct)).GetProperty("message").GetString(); }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or NotSupportedException) { }
        }
        return new(default, message ?? $"The backend refused the request (HTTP {(int)response.StatusCode}).", response.StatusCode);
    }
}

/// <summary>Plain-language labels and tones for journey states. Accepted/observed are never shown as passed.</summary>
public static class IntegrationJourneyPresentation
{
    public static string Readiness(JourneyReadinessState state) => state switch
    {
        JourneyReadinessState.Ready => "Ready",
        JourneyReadinessState.Partial => "Partial",
        JourneyReadinessState.NotReady => "Not ready",
        JourneyReadinessState.NotConfigured => "Not configured",
        _ => "Not available",
    };

    public static string ReadinessTone(JourneyReadinessState state) => state switch
    {
        JourneyReadinessState.Ready => "ready",
        JourneyReadinessState.Partial => "attention",
        _ => "neutral",
    };

    public static string Step(JourneyStepState state) => state switch
    {
        JourneyStepState.NotVerified => "Not verified",
        JourneyStepState.UnexpectedResult => "Unexpected result",
        JourneyStepState.NotAssessed => "Not assessed",
        _ => state.ToString(),
    };

    public static string StepTone(JourneyStepState state) => state switch
    {
        JourneyStepState.Verified => "ready",
        JourneyStepState.UnexpectedResult => "danger",
        JourneyStepState.Accepted or JourneyStepState.Observed or JourneyStepState.Sent => "attention",
        _ => "neutral",
    };

    public static string Run(JourneyRunState state) => state switch
    {
        JourneyRunState.Completed => "Completed (all mandatory steps verified)",
        JourneyRunState.Partial => "Partial",
        JourneyRunState.NotVerified => "Not verified",
        _ => state.ToString(),
    };

    public static string Rule(ArchitectureRuleOutcome outcome) => outcome switch
    {
        ArchitectureRuleOutcome.Conforms => "Conforms",
        ArchitectureRuleOutcome.PotentialDeviation => "Potential deviation",
        _ => "Not assessed",
    };

    public static string RuleTone(ArchitectureRuleOutcome outcome) => outcome switch
    {
        ArchitectureRuleOutcome.Conforms => "ready",
        ArchitectureRuleOutcome.PotentialDeviation => "attention",
        _ => "neutral",
    };

    public static string Expectation(ArchitectureRuleExpectation expectation) =>
        expectation == ArchitectureRuleExpectation.MustDependOn ? "Must use" : "Must not use";

    public static string Category(JourneyPrerequisiteCategory category) => category switch
    {
        JourneyPrerequisiteCategory.TrustedEnvironment => "Trusted environment",
        JourneyPrerequisiteCategory.SourceEvidence => "Source evidence",
        JourneyPrerequisiteCategory.SyntheticData => "Synthetic test data",
        JourneyPrerequisiteCategory.DownstreamVerification => "Downstream verification",
        JourneyPrerequisiteCategory.Executor => "Execution",
        _ => category.ToString(),
    };

    public static string Kind(JourneyStepKind kind) => kind switch
    {
        JourneyStepKind.SourceChange => "Source change",
        JourneyStepKind.ExternalSubmission => "External submission",
        JourneyStepKind.ExternalAcceptance => "External acceptance",
        JourneyStepKind.MessageTransport => "Message transport",
        JourneyStepKind.PayloadRetrieval => "Payload retrieval",
        JourneyStepKind.StructuralValidation => "Structural validation",
        JourneyStepKind.DomainIntake => "Domain intake",
        JourneyStepKind.IdentityLookup => "Identity lookup",
        JourneyStepKind.DomainState => "Domain state",
        JourneyStepKind.BrokerDelivery => "Broker delivery",
        JourneyStepKind.ConsumerProcessing => "Consumer processing",
        JourneyStepKind.DownstreamVerification => "Downstream verification",
        _ => kind.ToString(),
    };

    public static string Source(IntegrationJourneyView view)
    {
        var items = view.Prerequisites.Where(item => item.Category is JourneyPrerequisiteCategory.SourceEvidence or JourneyPrerequisiteCategory.Contract).ToArray();
        if (items.Length == 0) return "Not evaluated";
        var ready = items.Count(item => item.State == JourneyReadinessState.Ready);
        return ready == items.Length ? "Source verified" : ready == 0 ? "Not source verified" : $"{ready} of {items.Length} source checks verified";
    }

    public static string Configuration(IntegrationJourneyView view)
    {
        var items = view.Prerequisites.Where(item => item.Category == JourneyPrerequisiteCategory.Integration && item.Key.StartsWith("integration:", StringComparison.Ordinal)).ToArray();
        return items.Length == 0 ? "No integration required" : items.All(item => item.State == JourneyReadinessState.Ready) ? "Configured" :
            items.Any(item => item.State == JourneyReadinessState.NotConfigured) ? "Not configured" : "Configured, not enrolled";
    }

    public static string Verification(IntegrationJourneyView view) => view.LastRun is { } last
        ? $"{Run(last.State)} · {last.VerifiedSteps} of {last.TotalSteps} steps verified"
        : view.Maturity.RuntimeVerified ? "Runtime verified" : "Not verified (no run)";
}
