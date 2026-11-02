namespace BirkNext.Api.Models;

/// <summary>One Endpoint Discovery run (table <c>endpoint_discovery_runs</c>): a proxy capture session of one Target Environment and target host,
/// or the baseline imported from earlier browser history. Counts only; never traffic content.</summary>
public sealed class EndpointDiscoveryRunRecord
{
    public string Id { get; set; } = "";
    public string EnvironmentId { get; set; } = "";
    public string Host { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string Status { get; set; } = "";
    public int ObservedCount { get; set; }
    public int NewCount { get; set; }
    public int ObservedAgainCount { get; set; }
    public int NotSeenCount { get; set; }
}

/// <summary>
/// One observed API operation's history (table <c>observed_operations</c>), unique per environment + host + normalized identity. Safe metadata
/// only: method, route template (identifier-like segments templated), GraphQL operation type/name — never a header, token, cookie, query
/// string, body or observed identifier. A later run updates last-seen and counts; nothing is deleted because a run did not see it.
/// </summary>
public sealed class ObservedOperationRecord
{
    public Guid Id { get; set; }
    public string EnvironmentId { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public string Identity { get; set; } = "";
    public string Protocol { get; set; } = "";
    public string Method { get; set; } = "";
    public string Path { get; set; } = "";
    public string? GraphQlOperationType { get; set; }
    public string? GraphQlOperationName { get; set; }
    public DateTimeOffset FirstObservedAt { get; set; }
    public DateTimeOffset LastObservedAt { get; set; }
    public int ObservationCount { get; set; }
    public string FirstRunId { get; set; } = "";
    public string LastRunId { get; set; } = "";
    public int LastStatus { get; set; }
    public bool AuthObserved { get; set; }
}
