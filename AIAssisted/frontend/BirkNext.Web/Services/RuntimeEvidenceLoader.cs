using System.Net.Http.Json;
using BirkNext.LocalHttpsProxy;
using BirkNext.Web.Models;
using Microsoft.JSInterop;

namespace BirkNext.Web.Services;

/// <summary>Persisted Endpoint Discovery history (runs + observed operations) of one Target Environment and target host.</summary>
public interface IEndpointObservationHistoryApiService
{
    Task<EndpointObservationHistoryView?> HistoryAsync(string environmentId, string targetUrl, CancellationToken ct = default);
}

public sealed class EndpointObservationHistoryApiService(HttpClient http) : IEndpointObservationHistoryApiService
{
    public Task<EndpointObservationHistoryView?> HistoryAsync(string environmentId, string targetUrl, CancellationToken ct = default) =>
        http.GetFromJsonAsync<EndpointObservationHistoryView>(
            $"api/endpoint-discovery/history?environmentId={Uri.EscapeDataString(environmentId)}&targetUrl={Uri.EscapeDataString(targetUrl)}", ct);
}

public enum RuntimeEvidenceSource { None, BrowserHistory, LiveCapture, PersistedHistory }

/// <summary>
/// The runtime evidence a review uses, loaded by the review itself: the browser's Endpoint Discovery snapshot, the live proxy capture of this
/// environment folded into it (the same merge Endpoint Discovery performs while it is open), and — when this browser holds no API traffic for the
/// target host — the persisted server-side history. <see cref="Available"/> is evidence, never configuration; Bearer observed in it means
/// authenticated traffic was seen, not that BirkNext holds a reusable credential.
/// </summary>
public sealed record RuntimeEvidence(
    EndpointDiscoverySnapshot Snapshot,
    RuntimeEvidenceSource Source,
    EvidenceFreshness Freshness,
    DateTimeOffset? LastObservedAt,
    int ApiObservations,
    EndpointObservationHistoryView? History,
    bool LiveCaptureMerged,
    /// <summary>Safe, non-fatal notes ("Persisted Endpoint Discovery history could not be read").</summary>
    IReadOnlyList<string> Notes)
{
    public bool Available => ApiObservations > 0;

    public string SourceLabel => Source switch
    {
        RuntimeEvidenceSource.LiveCapture => "Endpoint Discovery · live capture",
        RuntimeEvidenceSource.BrowserHistory => "Endpoint Discovery",
        RuntimeEvidenceSource.PersistedHistory => "Endpoint Discovery history (persisted)",
        _ => "None",
    };
}

/// <summary>
/// Canonical runtime-evidence loading shared by API Quality Review and Performance Test Review (Endpoint Discovery keeps its own live merge while
/// open; both paths merge the same proxy observations with the same rules, so the result does not depend on which page was opened first).
/// Optional sources (live proxy runtime, persisted history) degrade to a note; only the browser snapshot itself failing to load is an error.
/// </summary>
public static class RuntimeEvidenceLoader
{
    public static async Task<RuntimeEvidence> LoadAsync(IEndpointDiscoveryService discovery, IJSRuntime js, ILocalHttpsProxyApiService? proxy,
        IEndpointObservationHistoryApiService? history, string profileId, string? contextFingerprint, string? targetUrl, CancellationToken ct = default)
    {
        await discovery.LoadAsync(js);
        ct.ThrowIfCancellationRequested();
        var notes = new List<string>();
        var host = Uri.TryCreate(targetUrl, UriKind.Absolute, out var target) ? target.Host : null;

        var liveMerged = false;
        if (proxy is not null)
        {
            try
            {
                var runtime = await proxy.GetRuntimeAsync();
                ct.ThrowIfCancellationRequested();
                // Only this environment's session, and only for the configuration it was started with (a stale session's traffic is not evidence here).
                if (runtime.ProfileId == profileId && (contextFingerprint is null || runtime.ContextFingerprint is null || runtime.ContextFingerprint == contextFingerprint)
                    && runtime.ObservedNetworkEndpoints.Count > 0)
                {
                    await discovery.MergeObservedAsync(js, profileId, runtime.ObservedNetworkEndpoints);
                    liveMerged = true;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException && !ct.IsCancellationRequested)
            {
                notes.Add("The live Endpoint Discovery capture status could not be read; recorded evidence is used.");
            }
        }

        var snapshot = discovery.GetSnapshot(profileId);
        EndpointObservationHistoryView? persisted = null;
        if (history is not null && targetUrl is not null && host is not null)
        {
            try { persisted = await history.HistoryAsync(profileId, targetUrl, ct); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException && !ct.IsCancellationRequested)
            {
                notes.Add("Persisted Endpoint Discovery history could not be read.");
            }
        }
        ct.ThrowIfCancellationRequested();

        var browserApi = ApiObservations(snapshot, host);
        var source = browserApi.Count > 0 ? (liveMerged ? RuntimeEvidenceSource.LiveCapture : RuntimeEvidenceSource.BrowserHistory) : RuntimeEvidenceSource.None;
        var used = snapshot;
        if (browserApi.Count == 0 && persisted is { Operations.Count: > 0 } && target is not null)
        {
            // This browser holds no API traffic for the target (new browser, cleared site data): the persisted history is the evidence. A transient
            // snapshot — never written to this browser's store, so Endpoint Discovery's own page-oriented history is untouched.
            used = new EndpointDiscoverySnapshot { Shared = [.. persisted.Operations.Select(o => Endpoint(o, target.Scheme))], UpdatedAt = persisted.Operations.Max(o => o.LastObservedAt) };
            browserApi = ApiObservations(used, host);
            source = browserApi.Count > 0 ? RuntimeEvidenceSource.PersistedHistory : RuntimeEvidenceSource.None;
        }
        DateTimeOffset? last = browserApi.Count == 0 ? null : browserApi.Max(e => e.LastObservedAt);
        var freshness = persisted?.LatestRun is { IsInProgress: true } || liveMerged ? EvidenceFreshness.Recent : ReviewDecisions.FreshnessOf(last, DateTimeOffset.UtcNow);
        return new RuntimeEvidence(used, source, browserApi.Count == 0 ? EvidenceFreshness.Unknown : freshness, last, browserApi.Count, persisted, liveMerged, notes);
    }

    /// <summary>Application API observations (REST/GraphQL; probes, diagnostics, static/technical resources and background traffic excluded) on the target host.</summary>
    public static List<ObservedNetworkEndpoint> ApiObservations(EndpointDiscoverySnapshot snapshot, string? host) =>
        [.. snapshot.Pages.SelectMany(p => p.Endpoints).Concat(snapshot.Shared)
            .Where(NetworkEvidencePolicy.IsApiCandidate)
            .Where(e => host is null || string.Equals(e.Host, host, StringComparison.OrdinalIgnoreCase))];

    /// <summary>A persisted operation as the observation it summarises (route template, never an observed identifier).</summary>
    public static ObservedNetworkEndpoint Endpoint(ObservedOperationHistory o, string scheme) => new()
    {
        Category = o.Protocol == "GraphQL" ? ObservedTrafficCategory.GraphQl : ObservedTrafficCategory.Rest, Scheme = scheme, Host = o.Host, Port = o.Port, Method = o.Method,
        Path = o.Path, OperationName = o.GraphQlOperationName,
        OperationType = o.Protocol != "GraphQL" ? GraphQlOperationType.None : o.GraphQlOperationType == "mutation" ? GraphQlOperationType.Mutation : GraphQlOperationType.Query,
        Count = o.ObservationCount, FirstObservedAt = o.FirstObservedAt, LastObservedAt = o.LastObservedAt, LastStatus = o.LastStatus, AuthObserved = o.AuthObserved,
        Confidence = o.Confidence, Provenance = o.Provenance,
    };
}
