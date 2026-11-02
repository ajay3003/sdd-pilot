using System.Text.Json.Serialization;

namespace BirkNext.LocalHttpsProxy;

/// <summary>
/// An operation's place in Endpoint Discovery history, relative to the latest run of the same Target Environment and host:
/// New (first seen in the latest run), ObservedAgain (seen before and in the latest run), PreviouslyObserved (in history, not seen in the latest
/// run — never "removed"), Observed (the baseline imported from earlier browser history: no delta is invented for it).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ObservedOperationState { New, ObservedAgain, PreviouslyObserved, Observed }

/// <summary>One Endpoint Discovery run: a proxy capture session (start → stop) for one Target Environment and target host, or the baseline
/// imported from history this browser held before runs were recorded. Safe metadata only.</summary>
public sealed record EndpointDiscoveryRunSummary(string RunId, string EnvironmentId, string Host, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt,
    string Status, int Observed, int New, int ObservedAgain, int NotSeen)
{
    public const string Completed = "Completed", InProgress = "In progress", Baseline = "Baseline (earlier browser history)";
    [JsonIgnore] public bool IsInProgress => Status == InProgress;
}

/// <summary>
/// One observed API operation's history: its normalized identity (REST method + route template, or GraphQL operation type + name), first and
/// last observation, how often, and its state relative to the latest run. Never a header, cookie, token, query string, body or observed
/// identifier (identifier-like path segments are templated before they are stored).
/// </summary>
public sealed record ObservedOperationHistory
{
    public string Identity { get; init; } = "";
    /// <summary>"REST" or "GraphQL".</summary>
    public string Protocol { get; init; } = "REST";
    public string Host { get; init; } = "";
    public int Port { get; init; } = 443;
    public string Method { get; init; } = "";
    /// <summary>The route template ("/api/person/{id}") — REST; the GraphQL endpoint path for GraphQL.</summary>
    public string Path { get; init; } = "";
    public string? GraphQlOperationType { get; init; }
    public string? GraphQlOperationName { get; init; }
    public DateTimeOffset FirstObservedAt { get; init; }
    public DateTimeOffset LastObservedAt { get; init; }
    public int ObservationCount { get; init; }
    public string FirstRunId { get; init; } = "";
    public string LastRunId { get; init; } = "";
    public int LastStatus { get; init; }
    public bool AuthObserved { get; init; }
    public ObservedEndpointConfidence Confidence { get; init; } = ObservedEndpointConfidence.Candidate;
    public RequestProvenance Provenance { get; init; }
    public ObservedOperationState State { get; init; }
    /// <summary>Seen in the latest run (current observations); false = historical only.</summary>
    public bool Current => State is ObservedOperationState.New or ObservedOperationState.ObservedAgain or ObservedOperationState.Observed;
}

/// <summary>Endpoint Discovery history of one Target Environment and host: whether discovery ever ran, the latest and previous runs, and every
/// operation ever observed (current and historical). Another host's history (e.g. before a base URL change) stays with that host.</summary>
public sealed record EndpointObservationHistoryView(string EnvironmentId, string Host, bool EverRun, EndpointDiscoveryRunSummary? LatestRun,
    EndpointDiscoveryRunSummary? PreviousRun, IReadOnlyList<EndpointDiscoveryRunSummary> Runs, IReadOnlyList<ObservedOperationHistory> Operations)
{
    public static EndpointObservationHistoryView Empty(string environmentId, string host) => new(environmentId, host, false, null, null, [], []);
}

/// <summary>Import of the observations this browser held before runs were recorded (once per environment and host; never invents deltas).</summary>
public sealed record EndpointObservationBaselineRequest(string Host, IReadOnlyList<ObservedNetworkEndpoint> Observations);
