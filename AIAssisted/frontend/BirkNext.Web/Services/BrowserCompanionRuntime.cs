using System.Net.Http.Json;
using Microsoft.JSInterop;
using BirkNext.BrowserCompanion;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

public interface IBrowserCompanionApiService
{
    Task<BrowserCompanionPairingChallenge> StartPairingAsync(BrowserCompanionPairingStartRequest request, CancellationToken cancellationToken = default);
    Task<BrowserCompanionStatus> StatusAsync(string profileId, CancellationToken cancellationToken = default);
    Task<BrowserCompanionStatus> UnpairAsync(string profileId, CancellationToken cancellationToken = default);
}

public sealed class BrowserCompanionApiService(HttpClient http) : IBrowserCompanionApiService
{
    private async Task<T> PostAsync<T>(string path, object body, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync(path, body, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct))!;
    }

    public Task<BrowserCompanionPairingChallenge> StartPairingAsync(BrowserCompanionPairingStartRequest request, CancellationToken ct = default) =>
        PostAsync<BrowserCompanionPairingChallenge>("api/browser-companion/pairing/start", request, ct);
    public Task<BrowserCompanionStatus> StatusAsync(string profileId, CancellationToken ct = default) =>
        PostAsync<BrowserCompanionStatus>("api/browser-companion/status", new BrowserCompanionStatusRequest(profileId), ct);
    public Task<BrowserCompanionStatus> UnpairAsync(string profileId, CancellationToken ct = default) =>
        PostAsync<BrowserCompanionStatus>("api/browser-companion/unpair", new BrowserCompanionStatusRequest(profileId), ct);
}

/// <summary>
/// Approved origins the Browser Companion may collect evidence on for a Target Environment: the frontend (target URL) origin plus any
/// configured allowed redirect URLs on http(s). API hosts are deliberately not page origins: the companion analyses application pages,
/// the proxy analyses API traffic.
/// </summary>
public static class BrowserCompanionScope
{
    public static IReadOnlyList<string> ApprovedOrigins(FrontendAnalysisProfile? profile)
    {
        if (profile is null) return [];
        var origins = new List<string>();
        void Add(string? url)
        {
            if (Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" && !ApplicationPagePolicy.IsInfrastructureHost(uri.Host))
            {
                var origin = uri.IsDefaultPort ? $"{uri.Scheme}://{uri.Host}" : $"{uri.Scheme}://{uri.Host}:{uri.Port}";
                if (!origins.Contains(origin, StringComparer.OrdinalIgnoreCase)) origins.Add(origin);
            }
        }
        Add(profile.TargetUrl);
        // Redirect permission is not application-page ownership.
        return origins;
    }

    public static BrowserCompanionPairingStartRequest PairingRequest(FrontendAnalysisProfile profile) =>
        new(profile.Id, profile.Name, profile.EnvironmentType.ToString(), ApprovedOrigins(profile));
}

/// <summary>
/// In-memory runtime state of the Browser Companion for the active Target Environment. Mirrors <see cref="LocalHttpsProxyRuntime"/>:
/// polling, never persisted, never a credential. Pairing and evidence are bound to one profile id; switching the active environment
/// re-evaluates scope because status is always read for the environment being viewed.
/// </summary>
public sealed class BrowserCompanionRuntime(IBrowserCompanionApiService api, IEndpointDiscoveryService? discovery = null, IJSRuntime? js = null) : IAsyncDisposable
{
    private string? _profileId;
    private long _generation;
    private CancellationTokenSource? _poll;
    private bool _backendUnavailable;

    public BrowserCompanionStatus Status { get; private set; } = new();
    public bool Busy { get; private set; }
    public string? LastError { get; private set; }
    public event Action? Changed;

    /// <summary>Status is valid for one environment only; another environment reads as NotPaired until its own status is fetched.</summary>
    public BrowserCompanionStatus For(string? profileId) =>
        profileId is not null && _profileId == profileId ? Status : new BrowserCompanionStatus { ProfileId = profileId, Message = "Browser Companion not paired." };

    public bool BackendUnavailable => _backendUnavailable;

    /// <summary>Follow the environment being viewed: (re)start polling its companion status. Cheap when already following it.</summary>
    public async Task FollowAsync(FrontendAnalysisProfile? profile)
    {
        if (profile is null) { Stop(); return; }
        if (discovery is not null && js is not null) await discovery.LoadAsync(js);
        discovery?.ConfigureTarget(profile);
        if (_profileId == profile.Id && _poll is not null) return;
        Stop();
        // Mark the environment as followed BEFORE the first await: the Changed event raised by RefreshAsync re-renders the
        // panel, whose OnParametersSet calls FollowAsync again; that re-entrant call must return early instead of restarting.
        _profileId = profile.Id;
        var generation = ++_generation;
        var poll = new CancellationTokenSource();
        _poll = poll;
        await RefreshAsync();
        if (generation != _generation) return;
        _ = PollAsync(poll.Token);
    }

    public async Task StartPairingAsync(FrontendAnalysisProfile profile)
    {
        if (Busy) return;
        await FollowAsync(profile);
        var generation = _generation;
        Busy = true; LastError = null;
        Changed?.Invoke();
        try
        {
            var request = BrowserCompanionScope.PairingRequest(profile);
            if (request.ApprovedOrigins.Count == 0) { LastError = "The Target Environment has no http(s) Target URL; nothing can be approved for the companion."; return; }
            await api.StartPairingAsync(request);
            if (generation == _generation) await RefreshAsync();
        }
        catch { if (generation == _generation) LastError = "Pairing could not be started. Check that the local BirkNext backend is running."; }
        finally { if (generation == _generation) { Busy = false; Changed?.Invoke(); } }
    }

    public async Task UnpairAsync()
    {
        if (_profileId is not { } profileId || Busy) return;
        var generation = _generation;
        Busy = true; LastError = null;
        Changed?.Invoke();
        try { Status = await api.UnpairAsync(profileId); }
        catch { if (generation == _generation) LastError = "Unpair request failed."; }
        finally { if (generation == _generation) { Busy = false; Changed?.Invoke(); } }
    }

    public async Task RefreshAsync()
    {
        if (_profileId is not { } profileId) return;
        var generation = _generation;
        try
        {
            var status = await api.StatusAsync(profileId);
            if (generation != _generation) return;
            if (discovery is not null && js is not null && status.ProfileId == profileId)
                await discovery.MergeBrowserEvidenceAsync(js, profileId, status.Pages);
            if (generation != _generation) return;
            Status = status;
            _backendUnavailable = false;
        }
        catch
        {
            if (generation != _generation) return;
            _backendUnavailable = true;
            Status = new BrowserCompanionStatus { ProfileId = profileId, State = BrowserCompanionState.NotPaired, Message = "Browser Companion status unavailable: the local BirkNext backend is not reachable." };
        }
        Changed?.Invoke();
    }

    private async Task PollAsync(CancellationToken ct)
    {
        try { while (!ct.IsCancellationRequested) { await Task.Delay(3000, ct); await RefreshAsync(); } }
        catch (OperationCanceledException) { }
    }

    private void Stop()
    {
        ++_generation;
        _poll?.Cancel(); _poll?.Dispose(); _poll = null;
        _profileId = null;
        Status = new();
        Busy = false;
    }

    public ValueTask DisposeAsync() { Stop(); return ValueTask.CompletedTask; }
}
