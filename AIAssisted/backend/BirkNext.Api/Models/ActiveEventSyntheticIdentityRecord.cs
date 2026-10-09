namespace BirkNext.Api.Models;

/// <summary>
/// Generic ledger of synthetic identity values handed out to active event runs: one row per (environment, scope, value). The primary key
/// makes a value impossible to reserve twice. Scopes are provider-owned names (e.g. an adapter table's synthetic primary key); no domain
/// column exists here and no payload is stored.
/// </summary>
public sealed class ActiveEventSyntheticIdentityRecord
{
    public string EnvironmentId { get; init; } = "";
    public string Scope { get; init; } = "";
    public long Value { get; init; }
    public Guid RunId { get; init; }
    public string ExtensionId { get; init; } = "";
    public DateTimeOffset ReservedAt { get; init; }
}
