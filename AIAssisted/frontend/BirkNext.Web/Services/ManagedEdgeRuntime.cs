using System.Net.Http.Json;
using BirkNext.ManagedEdge;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

public interface IManagedEdgeCdpApiService
{
    Task<ManagedEdgeStatus> ConnectAsync(ManagedEdgeConnectRequest request);
    Task<ManagedEdgeStatus> StatusAsync(ManagedEdgeSessionRequest request, bool verify);
    Task DisconnectAsync(ManagedEdgeSessionRequest request);
    Task<ManagedEdgePreflightResult> PreflightAsync(ManagedEdgePreflightRequest request);
    Task<ManagedEdgePreflightResult> LaunchAsync(ManagedEdgeLaunchRequest request);
}

public sealed class ManagedEdgeCdpApiService(HttpClient http) : IManagedEdgeCdpApiService
{
    public async Task<ManagedEdgeStatus> ConnectAsync(ManagedEdgeConnectRequest request)
    {
        using var response = await http.PostAsJsonAsync("api/managed-edge/connect", request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ManagedEdgeStatus>())!;
    }
    public async Task<ManagedEdgeStatus> StatusAsync(ManagedEdgeSessionRequest request, bool verify)
    {
        using var response = await http.PostAsJsonAsync(verify ? "api/managed-edge/verify" : "api/managed-edge/status", request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ManagedEdgeStatus>())!;
    }
    public async Task DisconnectAsync(ManagedEdgeSessionRequest request)
    {
        using var response = await http.PostAsJsonAsync("api/managed-edge/disconnect", request);
    }
    public async Task<ManagedEdgePreflightResult> PreflightAsync(ManagedEdgePreflightRequest request)
    {
        using var response = await http.PostAsJsonAsync("api/managed-edge/preflight", request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ManagedEdgePreflightResult>())!;
    }
    public async Task<ManagedEdgePreflightResult> LaunchAsync(ManagedEdgeLaunchRequest request)
    {
        using var response = await http.PostAsJsonAsync("api/managed-edge/launch", request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ManagedEdgePreflightResult>())!;
    }
}

/// <summary>In-memory runtime state, independent of saved profiles and public detection.</summary>
public sealed class ManagedEdgeRuntime(IManagedEdgeCdpApiService api) : IAsyncDisposable
{
    private ManagedEdgeSessionRequest? _owner;
    private string? _identity;
    private long _generation;
    private long _operation;
    private CancellationTokenSource? _poll;
    public ManagedEdgeStatus Status { get; private set; } = new();
    /// <summary>Latest transient Edge compatibility result. Never written to a profile.</summary>
    public ManagedEdgePreflightResult? Preflight { get; private set; }
    public event Action? Changed;
    public bool Busy { get; private set; }
    public bool PreflightBusy { get; private set; }

    public ManagedEdgeStatus For(FrontendAnalysisProfile? profile) => _identity is null ||
        (profile is not null && _identity == ManualAuthenticationVerificationEvidence.Fingerprint(profile))
        ? Status : new() { State = ManagedEdgeState.Stale, Evidence = "Environment settings changed. Reconnect and verify again." };

    /// <summary>A compatibility result only describes the target origin it was checked for.</summary>
    public ManagedEdgePreflightResult? PreflightFor(FrontendAnalysisProfile? profile) =>
        Preflight is not null && string.Equals(Preflight.TargetOrigin, OriginOf(profile?.TargetUrl), StringComparison.Ordinal) ? Preflight : null;

    public static string? OriginOf(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" ? uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant() : null;

    public async Task SynchronizeAsync(FrontendAnalysisProfile? profile)
    {
        if (_identity is not null && (profile is null || _identity != ManualAuthenticationVerificationEvidence.Fingerprint(profile)))
            await DisconnectAsync(ManagedEdgeState.Stale);
    }

    public Task CheckCompatibilityAsync(FrontendAnalysisProfile profile) =>
        RunPreflightAsync(profile, () => api.PreflightAsync(new(profile.TargetUrl)),
            "The compatibility check did not complete. Check that the local BirkNext backend is running, then retry.");

    public Task LaunchEdgeAsync(FrontendAnalysisProfile profile) =>
        RunPreflightAsync(profile, () => api.LaunchAsync(new(profile.TargetUrl)),
            "Starting Edge did not complete. Check the local BirkNext backend and the Edge window, then run the compatibility check again.");

    private async Task RunPreflightAsync(FrontendAnalysisProfile profile, Func<Task<ManagedEdgePreflightResult>> operation, string failure)
    {
        if (PreflightBusy) return;
        var origin = OriginOf(profile.TargetUrl);
        PreflightBusy = true;
        Changed?.Invoke();
        try { Preflight = await operation(); }
        catch { Preflight = new() { TargetOrigin = origin, FailureReason = failure }; }
        finally { PreflightBusy = false; Changed?.Invoke(); }
    }

    public async Task ConnectAsync(FrontendAnalysisProfile profile)
    {
        await DisconnectAsync();
        var generation = ++_generation;
        _identity = ManualAuthenticationVerificationEvidence.Fingerprint(profile);
        // The trust policy is the environment's SAVED Browser delivery trust (Authentication configuration). There is no runtime opt-in:
        // exact origin is always preferred, and approved MCAS proxy fallback applies only when the saved policy permits it.
        var request = new ManagedEdgeConnectRequest(profile.Id, profile.TargetUrl ?? "", _identity, profile.Authentication.BrowserDeliveryTrust);
        Busy = true;
        Status = new() { State = ManagedEdgeState.Connecting, Evidence = "Connecting to the local Edge instance…" };
        Changed?.Invoke();
        try
        {
            var result = await api.ConnectAsync(request);
            if (generation != _generation)
            {
                if (result.SessionId is { } abandoned) await api.DisconnectAsync(new(abandoned, request.ProfileId, request.ContextFingerprint));
                return;
            }
            Status = result;
            if (result.SessionId is { } session)
            {
                _owner = new(session, request.ProfileId, request.ContextFingerprint);
                _poll = new();
                _ = PollAsync(_poll.Token);
            }
        }
        catch { if (generation == _generation) Status = new() { State = ManagedEdgeState.Failed, Evidence = "Cannot connect to managed Edge. Check the local backend, Edge and port 9222." }; }
        finally { if (generation == _generation) { Busy = false; Changed?.Invoke(); } }
    }

    public async Task VerifyAsync()
    {
        if (_owner is not { } owner || Busy) return;
        var generation = _generation;
        var operation = ++_operation;
        Busy = true;
        Changed?.Invoke();
        try { var result = await api.StatusAsync(owner, true); if (generation == _generation && operation == _operation) Status = result; }
        catch { if (generation == _generation) Status = new() { State = ManagedEdgeState.Stale, Evidence = "Runtime connection lost. Reconnect and verify again." }; }
        finally { if (generation == _generation) { Busy = false; Changed?.Invoke(); } }
    }

    public async Task RefreshAsync()
    {
        if (_owner is not { } owner || Busy) return;
        var generation = _generation;
        var operation = ++_operation;
        try { var result = await api.StatusAsync(owner, false); if (generation == _generation && operation == _operation) Status = result; }
        catch { if (generation == _generation && operation == _operation) Status = new() { State = ManagedEdgeState.Stale, Evidence = "Runtime connection lost. Reconnect and verify again." }; }
        if (generation == _generation) Changed?.Invoke();
    }

    private async Task PollAsync(CancellationToken ct)
    {
        try { while (!ct.IsCancellationRequested) { await Task.Delay(3000, ct); await RefreshAsync(); } }
        catch (OperationCanceledException) { }
    }

    public async Task DisconnectAsync(ManagedEdgeState state = ManagedEdgeState.NotConnected)
    {
        ++_generation;
        _poll?.Cancel(); _poll?.Dispose(); _poll = null;
        var owner = _owner;
        _owner = null; _identity = null; Busy = false;
        Status = new() { State = state, Evidence = state == ManagedEdgeState.Stale ? "Environment settings changed. Reconnect and verify again." : "Not connected. The user's Edge browser remains open." };
        Changed?.Invoke();
        if (owner is not null) try { await api.DisconnectAsync(owner); } catch { }
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync();
}
