using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.LocalHttpsProxy;
using BirkNext.PerformanceTests;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services.LocalHttpsProxy;

/// <summary>
/// Persisted Endpoint Discovery history: runs (one per proxy capture session, scoped to Target Environment + target host) and the operations
/// observed across them. History is never overwritten: a later run updates last-seen and counts, and an operation a run did not see stays
/// as "Previously observed" (never "removed"). Identities are normalized and safe — REST method + route template (identifier-like segments
/// templated), GraphQL operation type + name; no header, cookie, token, query string, body or observed identifier is stored.
/// </summary>
public sealed class EndpointObservationHistoryService(AppDbContext db, TimeProvider clock)
{
    public sealed record Normalized(string Identity, string Protocol, string Method, string Path, string? GraphQlType, string? GraphQlName, int Port,
        DateTimeOffset First, DateTimeOffset Last, int Count, int LastStatus, bool Auth);

    /// <summary>The target API traffic of a capture, normalized and merged per identity (other hosts, static assets and telemetry excluded).</summary>
    public static IReadOnlyList<Normalized> Normalize(string host, IEnumerable<ObservedNetworkEndpoint> observed) =>
    [
        .. observed.Where(o => string.Equals(o.Host, host, StringComparison.OrdinalIgnoreCase) && o.Method.Length > 0
                && o.Category is ObservedTrafficCategory.Rest or ObservedTrafficCategory.GraphQl or ObservedTrafficCategory.OtherHttp)
            .Select(o =>
            {
                var path = PerformanceScenarioEngine.Template(o.Path);
                if (o.Category == ObservedTrafficCategory.GraphQl)
                {
                    var type = o.OperationType == GraphQlOperationType.Mutation ? "mutation" : "query";
                    return o.OperationName is { Length: > 0 } name
                        ? (o, Identity: $"graphql {type} {name}", Protocol: "GraphQL", Method: "POST", Path: path, Type: (string?)type, Name: (string?)name)
                        : (o, Identity: $"graphql endpoint {path}", Protocol: "GraphQL", Method: "POST", Path: path, Type: (string?)null, Name: (string?)null);
                }
                return (o, Identity: $"{o.Method.ToUpperInvariant()} {path}", Protocol: "REST", Method: o.Method.ToUpperInvariant(), Path: path, Type: (string?)null, Name: (string?)null);
            })
            .GroupBy(x => x.Identity, StringComparer.Ordinal)
            .Select(g => new Normalized(g.Key, g.First().Protocol, g.First().Method, g.First().Path, g.First().Type, g.First().Name, g.First().o.Port,
                g.Min(x => x.o.FirstObservedAt), g.Max(x => x.o.LastObservedAt), g.Sum(x => Math.Max(1, x.o.Count)),
                g.OrderByDescending(x => x.o.LastObservedAt).First().o.LastStatus, g.Any(x => x.o.AuthObserved))),
    ];

    /// <summary>
    /// Records one completed run (idempotent by run id): new identities are added, seen ones get their last-seen and count updated, and the
    /// run stores how many were new, observed again and not seen. A run with zero operations is still a run ("ran, observed nothing").
    /// </summary>
    public async Task<EndpointDiscoveryRunSummary> RecordRunAsync(string environmentId, string host, string runId, DateTimeOffset startedAt, DateTimeOffset completedAt,
        IEnumerable<ObservedNetworkEndpoint> observed, CancellationToken ct)
    {
        host = host.ToLowerInvariant();
        if (await db.EndpointDiscoveryRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, ct) is { } recorded) return Summary(recorded);
        var batch = Normalize(host, observed);
        var existing = await db.ObservedOperations.Where(o => o.EnvironmentId == environmentId && o.Host == host).ToListAsync(ct);
        var byIdentity = existing.ToDictionary(o => o.Identity, StringComparer.Ordinal);
        int added = 0, again = 0;
        foreach (var item in batch)
        {
            if (byIdentity.TryGetValue(item.Identity, out var record))
            {
                record.LastObservedAt = item.Last > record.LastObservedAt ? item.Last : record.LastObservedAt;
                record.ObservationCount += item.Count;
                record.LastRunId = runId;
                record.LastStatus = item.LastStatus;
                record.AuthObserved |= item.Auth;
                again++;
            }
            else
            {
                db.ObservedOperations.Add(Record(environmentId, host, runId, item));
                added++;
            }
        }
        var seen = batch.Select(b => b.Identity).ToHashSet(StringComparer.Ordinal);
        var run = new EndpointDiscoveryRunRecord
        {
            Id = runId, EnvironmentId = environmentId, Host = host, StartedAt = startedAt, CompletedAt = completedAt, Status = EndpointDiscoveryRunSummary.Completed,
            ObservedCount = batch.Count, NewCount = added, ObservedAgainCount = again, NotSeenCount = existing.Count(o => !seen.Contains(o.Identity)),
        };
        db.EndpointDiscoveryRuns.Add(run);
        await db.SaveChangesAsync(ct);
        return Summary(run);
    }

    /// <summary>
    /// Imports observations this browser held before runs were recorded — once per environment and host, only when no run exists. They become
    /// the baseline: their own first/last-seen times are kept and no delta (new / not seen) is invented.
    /// </summary>
    public async Task<EndpointDiscoveryRunSummary?> ImportBaselineAsync(string environmentId, string host, IEnumerable<ObservedNetworkEndpoint> observed, CancellationToken ct)
    {
        host = host.ToLowerInvariant();
        if (await db.EndpointDiscoveryRuns.AnyAsync(r => r.EnvironmentId == environmentId && r.Host == host, ct)) return null;
        var batch = Normalize(host, observed);
        if (batch.Count == 0) return null;
        var id = "baseline-" + Guid.NewGuid().ToString("N")[..24];
        foreach (var item in batch) db.ObservedOperations.Add(Record(environmentId, host, id, item));
        var run = new EndpointDiscoveryRunRecord
        {
            Id = id, EnvironmentId = environmentId, Host = host, StartedAt = batch.Min(b => b.First), CompletedAt = batch.Max(b => b.Last),
            Status = EndpointDiscoveryRunSummary.Baseline, ObservedCount = batch.Count,
        };
        db.EndpointDiscoveryRuns.Add(run);
        await db.SaveChangesAsync(ct);
        return Summary(run);
    }

    /// <summary>
    /// The history of one environment and host. The latest run is the capture still running (when <paramref name="live"/> is given) or the most
    /// recent completed one; every operation's state is relative to it. Another host's history is never included.
    /// </summary>
    public async Task<EndpointObservationHistoryView> ViewAsync(string environmentId, string host, IReadOnlyList<ObservedNetworkEndpoint>? live, CancellationToken ct)
    {
        host = host.ToLowerInvariant();
        var runs = await db.EndpointDiscoveryRuns.AsNoTracking().Where(r => r.EnvironmentId == environmentId && r.Host == host).OrderByDescending(r => r.StartedAt).ToListAsync(ct);
        var records = await db.ObservedOperations.AsNoTracking().Where(o => o.EnvironmentId == environmentId && o.Host == host).ToListAsync(ct);
        var summaries = runs.Select(Summary).ToList();
        if (live is not null)
        {
            var batch = Normalize(host, live);
            var known = records.ToDictionary(r => r.Identity, StringComparer.Ordinal);
            var seen = batch.Select(b => b.Identity).ToHashSet(StringComparer.Ordinal);
            var inProgress = new EndpointDiscoveryRunSummary("live", environmentId, host, batch.Count == 0 ? clock.GetUtcNow() : batch.Min(b => b.First), null,
                EndpointDiscoveryRunSummary.InProgress, batch.Count, batch.Count(b => !known.ContainsKey(b.Identity)), batch.Count(b => known.ContainsKey(b.Identity)),
                records.Count(r => !seen.Contains(r.Identity)));
            var operations = records.Select(r => seen.Contains(r.Identity)
                    ? View(r) with { State = ObservedOperationState.ObservedAgain, LastObservedAt = Max(r.LastObservedAt, batch.First(b => b.Identity == r.Identity).Last),
                        ObservationCount = r.ObservationCount + batch.First(b => b.Identity == r.Identity).Count }
                    : View(r) with { State = ObservedOperationState.PreviouslyObserved })
                .Concat(batch.Where(b => !known.ContainsKey(b.Identity)).Select(b => View(Record(environmentId, host, "live", b)) with { State = ObservedOperationState.New }))
                .OrderByDescending(o => o.LastObservedAt).ToList();
            return new(environmentId, host, true, inProgress, summaries.FirstOrDefault(), [inProgress, .. summaries], operations);
        }
        if (summaries.Count == 0) return EndpointObservationHistoryView.Empty(environmentId, host);
        var latest = summaries[0];
        var states = records.Select(r => View(r) with
        {
            State = r.LastRunId != latest.RunId ? ObservedOperationState.PreviouslyObserved
                : latest.Status == EndpointDiscoveryRunSummary.Baseline ? ObservedOperationState.Observed
                : r.FirstRunId == latest.RunId ? ObservedOperationState.New : ObservedOperationState.ObservedAgain,
        }).OrderByDescending(o => o.LastObservedAt).ToList();
        return new(environmentId, host, true, latest, summaries.Skip(1).FirstOrDefault(), summaries, states);
    }

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

    private static ObservedOperationRecord Record(string environmentId, string host, string runId, Normalized item) => new()
    {
        Id = Guid.NewGuid(), EnvironmentId = environmentId, Host = host, Port = item.Port, Identity = item.Identity, Protocol = item.Protocol, Method = item.Method,
        Path = item.Path, GraphQlOperationType = item.GraphQlType, GraphQlOperationName = item.GraphQlName, FirstObservedAt = item.First, LastObservedAt = item.Last,
        ObservationCount = item.Count, FirstRunId = runId, LastRunId = runId, LastStatus = item.LastStatus, AuthObserved = item.Auth,
    };

    private static ObservedOperationHistory View(ObservedOperationRecord r) => new()
    {
        Identity = r.Identity, Protocol = r.Protocol, Host = r.Host, Port = r.Port, Method = r.Method, Path = r.Path, GraphQlOperationType = r.GraphQlOperationType,
        GraphQlOperationName = r.GraphQlOperationName, FirstObservedAt = r.FirstObservedAt, LastObservedAt = r.LastObservedAt, ObservationCount = r.ObservationCount,
        FirstRunId = r.FirstRunId, LastRunId = r.LastRunId, LastStatus = r.LastStatus, AuthObserved = r.AuthObserved,
    };

    private static EndpointDiscoveryRunSummary Summary(EndpointDiscoveryRunRecord r) =>
        new(r.Id, r.EnvironmentId, r.Host, r.StartedAt, r.CompletedAt, r.Status, r.ObservedCount, r.NewCount, r.ObservedAgainCount, r.NotSeenCount);

    public static string? HostOf(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Host.Length > 0 ? uri.Host.ToLowerInvariant() : null;
}

/// <summary>Records a finished proxy capture session as an Endpoint Discovery run (the proxy service is a singleton; history uses a scoped DbContext).</summary>
public interface IEndpointObservationRecorder
{
    Task RecordAsync(string environmentId, string targetUrl, string runId, DateTimeOffset startedAt, IReadOnlyList<ObservedNetworkEndpoint> observed);
}

public sealed class EndpointObservationRecorder(IServiceScopeFactory scopes, TimeProvider clock, ILogger<EndpointObservationRecorder> logger) : IEndpointObservationRecorder
{
    public async Task RecordAsync(string environmentId, string targetUrl, string runId, DateTimeOffset startedAt, IReadOnlyList<ObservedNetworkEndpoint> observed)
    {
        if (EndpointObservationHistoryService.HostOf(targetUrl) is not { } host) return;
        try
        {
            using var scope = scopes.CreateScope();
            var history = scope.ServiceProvider.GetRequiredService<EndpointObservationHistoryService>();
            var run = await history.RecordRunAsync(environmentId, host, runId, startedAt, clock.GetUtcNow(), observed, CancellationToken.None);
            logger.LogInformation("EndpointDiscoveryRunRecorded {RunId} {Observed} {New} {NotSeen}", run.RunId, run.Observed, run.New, run.NotSeen);
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException or Npgsql.NpgsqlException)
        {
            // History is evidence: a failed write never breaks stopping the proxy.
            logger.LogWarning("EndpointDiscoveryRunNotRecorded {RunId} {ExceptionType}", runId, ex.GetType().Name);
        }
    }
}
