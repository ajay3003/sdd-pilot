using System.Net.Http.Json;
using BirkNext.LocalHttpsProxy;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

public interface ILocalHttpsProxyApiService
{
    Task<LocalHttpsProxyStatus> GetRuntimeAsync();
    Task<LocalHttpsProxyStatus> LaunchEdgeAsync(LocalHttpsProxyEdgeLaunchRequest request);
    /// <summary>Closes only the browser this runtime launched, then opens it again on the current proxy port.</summary>
    Task<LocalHttpsProxyStatus> RestartEdgeAsync(LocalHttpsProxyEdgeLaunchRequest request);
    Task<LocalHttpsProxyStatus> CheckCompatibilityAsync(LocalHttpsProxyScopeRequest request);
    Task<LocalHttpsProxyStatus> StartAsync(LocalHttpsProxyScopeRequest request);
    Task<LocalHttpsProxyStatus> StatusAsync(LocalHttpsProxySessionRequest request);
    Task<LocalHttpsProxyStatus> StopAsync(LocalHttpsProxySessionRequest request);
    Task<ProxyCertificateStatus> InstallCertificateAsync();
    Task<ProxyCertificateStatus> RemoveCertificateAsync();
    Task<AuthenticatedApiExecutionResult> ExecuteRestAsync(AuthenticatedRestRequest request);
    Task<AuthenticatedApiExecutionResult> ExecuteGraphQlAsync(AuthenticatedGraphQlRequest request);
}

public sealed class LocalHttpsProxyApiService(HttpClient http) : ILocalHttpsProxyApiService
{
    public async Task<LocalHttpsProxyStatus> GetRuntimeAsync() => (await http.GetFromJsonAsync<LocalHttpsProxyStatus>("api/local-https-proxy/runtime"))!;
    public Task<LocalHttpsProxyStatus> LaunchEdgeAsync(LocalHttpsProxyEdgeLaunchRequest request) => PostAsync<LocalHttpsProxyStatus>("api/local-https-proxy/launch-edge", request);
    public Task<LocalHttpsProxyStatus> RestartEdgeAsync(LocalHttpsProxyEdgeLaunchRequest request) => PostAsync<LocalHttpsProxyStatus>("api/local-https-proxy/edge/restart", request);
    private async Task<T> PostAsync<T>(string path, object body)
    {
        using var response = await http.PostAsJsonAsync(path, body);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    public Task<LocalHttpsProxyStatus> CheckCompatibilityAsync(LocalHttpsProxyScopeRequest request) => PostAsync<LocalHttpsProxyStatus>("api/local-https-proxy/compatibility", request);
    public Task<LocalHttpsProxyStatus> StartAsync(LocalHttpsProxyScopeRequest request) => PostAsync<LocalHttpsProxyStatus>("api/local-https-proxy/start", request);
    public Task<LocalHttpsProxyStatus> StatusAsync(LocalHttpsProxySessionRequest request) => PostAsync<LocalHttpsProxyStatus>("api/local-https-proxy/status", request);
    public Task<LocalHttpsProxyStatus> StopAsync(LocalHttpsProxySessionRequest request) => PostAsync<LocalHttpsProxyStatus>("api/local-https-proxy/stop", request);
    public Task<ProxyCertificateStatus> InstallCertificateAsync() => PostAsync<ProxyCertificateStatus>("api/local-https-proxy/certificate/install", new LocalHttpsProxyCertificateRequest(true));
    public Task<ProxyCertificateStatus> RemoveCertificateAsync() => PostAsync<ProxyCertificateStatus>("api/local-https-proxy/certificate/remove", new LocalHttpsProxyCertificateRequest(true));
    public Task<AuthenticatedApiExecutionResult> ExecuteRestAsync(AuthenticatedRestRequest request) => PostAsync<AuthenticatedApiExecutionResult>("api/local-https-proxy/execute/rest", request);
    public Task<AuthenticatedApiExecutionResult> ExecuteGraphQlAsync(AuthenticatedGraphQlRequest request) => PostAsync<AuthenticatedApiExecutionResult>("api/local-https-proxy/execute/graphql", request);
}

/// <summary>
/// In-memory runtime state of the Local HTTPS proxy for the selected Target Environment. Independent of saved profiles: starting,
/// stopping, certificate actions and authenticated checks never touch a profile, never enter edit mode and are never persisted.
/// The status never contains a credential; the backend keeps it memory-only and exposes availability only.
/// </summary>
public sealed class LocalHttpsProxyRuntime(ILocalHttpsProxyApiService api) : IAsyncDisposable
{
    private LocalHttpsProxySessionRequest? _owner;
    private string? _identity;
    private long _generation;
    private long _operation;
    private CancellationTokenSource? _poll;
    private Task? _pollTask;
    private string? _selectedIdentity;
    private bool _disposed;

    public LocalHttpsProxyStatus Status { get; private set; } = new();

    /// <summary>
    /// Whether a browser has actually been routed through this proxy since it was started. BirkNext does not
    /// and cannot read Edge proxy settings, so observed traffic is the only proof that a browser was pointed
    /// at this port — and that fact has to outlive the proxy. The status is replaced when the proxy stops, but
    /// the browser is still pointed at the port, which is now closed. Runtime-only, never persisted.
    /// </summary>
    public bool BrowserWasRouted { get; private set; }

    /// <summary>Traffic reaching the proxy proves a browser was configured for it; nothing else does.</summary>
    private static bool Routed(LocalHttpsProxyStatus status) =>
        status.AuthenticatedRequestsObserved > 0 || status.AuthenticatedCredentialAvailable || status.ObservedNetworkEndpoints.Count > 0;
    public AuthenticatedApiExecutionResult? LastRestResult { get; private set; }
    public AuthenticatedApiExecutionResult? LastGraphQlResult { get; private set; }
    public string? LastExecutionError { get; private set; }
    public bool Busy { get; private set; }
    /// <summary>
    /// The backend has answered at least once for the selected environment. Until then the page says "Checking…"
    /// rather than rendering a default status as though it were a measurement.
    /// </summary>
    public bool Loaded { get; private set; }
    public event Action? Changed;

    /// <summary>Status is only valid for the environment configuration it was produced for; any relevant change reads as Stale.</summary>
    public LocalHttpsProxyStatus For(FrontendAnalysisProfile? profile) =>
        ForFingerprint(profile is null ? null : LocalHttpsProxyScope.Fingerprint(profile));

    /// <summary>Status for a precomputed proxy context fingerprint (e.g. <see cref="FrontendAnalysisContext.ReviewIdentity"/>); Stale when it does not match the running session.</summary>
    public LocalHttpsProxyStatus ForFingerprint(string? fingerprint) => _identity is null ||
        (fingerprint is not null && _identity == fingerprint)
        ? Status : new() { State = LocalHttpsProxyState.Stale, Evidence = "This proxy belongs to another environment configuration. Its runtime is unchanged; stop it explicitly before starting another." };

    public bool SessionActive => _owner is not null;

    /// <summary>Marks the first backend answer for this environment. Called by every path that sets Status from the backend.</summary>
    private void MarkLoaded() => Loaded = true;

    public async Task SynchronizeAsync(FrontendAnalysisProfile? profile)
    {
        if (_disposed || profile is null || Busy) return;
        var identity = LocalHttpsProxyScope.Fingerprint(profile);
        if (_selectedIdentity == identity) return;
        _selectedIdentity = identity;
        await CheckCompatibilityAsync(profile);
    }

    public async Task CheckCompatibilityAsync(FrontendAnalysisProfile profile)
    {
        if (Busy || _disposed) return;
        var generation = _generation;
        var identity = LocalHttpsProxyScope.Fingerprint(profile);
        Busy = true;
        Changed?.Invoke();
        try
        {
            var current = await api.GetRuntimeAsync();
            var result = current is { RuntimeId: not null } ? current : await api.CheckCompatibilityAsync(LocalHttpsProxyScope.Request(profile));
            if (generation != _generation || _disposed) return;
            Apply(result, profile.Id, identity);
            EnsurePolling();
        }
        catch { if (generation == _generation) { MarkLoaded(); Status = Status with { AuthenticatedCredentialAvailable = false, FailureReason = "Proxy status unavailable. Check that the local BirkNext backend is running, then retry." }; EnsurePolling(); } }
        finally { if (generation == _generation) { Busy = false; Changed?.Invoke(); } }
    }

    public async Task StartAsync(FrontendAnalysisProfile profile)
    {
        if (Busy || _disposed) return;
        var generation = ++_generation;
        _identity = LocalHttpsProxyScope.Fingerprint(profile);
        var request = LocalHttpsProxyScope.Request(profile);
        Busy = true;
        Status = new() { State = LocalHttpsProxyState.Starting, Evidence = "Starting the loopback HTTPS proxy…" };
        Changed?.Invoke();
        try
        {
            var result = await api.StartAsync(request);
            if (generation != _generation || _disposed) return;
            Apply(result, request.ProfileId, request.ContextFingerprint);
            // A new session starts with no browser routed through it; the flag is not carried over from the last one.
            BrowserWasRouted = Routed(result);
            EnsurePolling();
        }
        catch { if (generation == _generation) Status = new() { State = LocalHttpsProxyState.Failed, FailureReason = "The local HTTPS proxy could not be started. Check that the local BirkNext backend is running in the local workstation mode." }; }
        finally { if (generation == _generation) { Busy = false; Changed?.Invoke(); } }
    }

    public async Task RefreshAsync()
    {
        if (_disposed || Busy) return;
        var generation = _generation;
        var operation = ++_operation;
        try
        {
            var result = await api.GetRuntimeAsync();
            if (result is { RuntimeId: null, SessionId: null })
                result = result with { LocalIntegrationAvailable = Status.LocalIntegrationAvailable, EnvironmentAllowed = Status.EnvironmentAllowed,
                    PortAvailable = Status.PortAvailable, Certificate = Status.Certificate, ApprovedHosts = Status.ApprovedHosts,
                    CanStart = Status.LocalIntegrationAvailable && Status.EnvironmentAllowed && Status.PortAvailable };
            if (generation == _generation && operation == _operation)
            {
                Apply(result, _owner?.ProfileId, _identity);
                if (Routed(result)) BrowserWasRouted = true;
            }
        }
        catch { if (generation == _generation && operation == _operation) Status = Status with { AuthenticatedCredentialAvailable = false, FailureReason = "Proxy status unavailable. Reconnecting to the backend; no stop was requested." }; }
        if (generation == _generation) Changed?.Invoke();
    }

    private async Task PollAsync(CancellationToken ct)
    {
        try { while (!ct.IsCancellationRequested) { await Task.Delay(3000, ct); await RefreshAsync(); } }
        catch (OperationCanceledException) { }
    }

    private void Apply(LocalHttpsProxyStatus result, string? profileId, string? fingerprint)
    {
        MarkLoaded();
        ArgumentNullException.ThrowIfNull(result);
        Status = result;
        _identity = result.ContextFingerprint ?? fingerprint;
        _owner = result.SessionId is { } id ? new(id, result.ProfileId ?? profileId!, _identity!) : null;
        BrowserWasRouted |= Routed(result);
    }

    private void EnsurePolling()
    {
        if (_poll is not null || _disposed) return;
        _poll = new();
        _pollTask = PollAsync(_poll.Token);
    }

    /// <summary>
    /// Closes the dedicated browser this runtime launched and opens it again on the current proxy port. For a browser
    /// left over from an earlier runtime: it is running, and pointed at a port that no longer exists.
    /// </summary>
    public Task RestartBrowserAsync() => OpenBrowserAsync(restart: true);

    public async Task OpenBrowserAsync(bool restart = false)
    {
        if (_owner is not { } owner || Busy || _disposed) return;
        Busy = true;
        var generation = _generation;
        try
        {
            var request = new LocalHttpsProxyEdgeLaunchRequest(owner.SessionId, owner.ProfileId, owner.ContextFingerprint);
            var result = restart ? await api.RestartEdgeAsync(request) : await api.LaunchEdgeAsync(request);
            if (generation == _generation) Apply(result, owner.ProfileId, owner.ContextFingerprint);
        }
        catch { if (generation == _generation) Status = Status with { FailureReason = "The dedicated proxy browser could not be opened." }; }
        finally { if (generation == _generation) { Busy = false; Changed?.Invoke(); } }
    }

    public async Task StopAsync(LocalHttpsProxyState state = LocalHttpsProxyState.Stopped)
    {
        if (Busy || _disposed) return;
        var generation = ++_generation;
        var owner = _owner;
        if (owner is null) return;
        Busy = true;
        LastRestResult = null; LastGraphQlResult = null; LastExecutionError = null;
        Status = Status with { RuntimeStatus = LocalHttpsProxyRuntimePhase.Stopping };
        Changed?.Invoke();
        try
        {
            var result = await api.StopAsync(owner);
            if (generation == _generation) Apply(result, owner.ProfileId, owner.ContextFingerprint);
        }
        catch { if (generation == _generation) Status = Status with { FailureReason = "Stop could not be confirmed. Refresh status and retry." }; }
        finally { if (generation == _generation) { Busy = false; Changed?.Invoke(); } }
    }

    public Task InstallCertificateAsync() => CertificateAsync(api.InstallCertificateAsync);
    public Task RemoveCertificateAsync() => CertificateAsync(api.RemoveCertificateAsync);

    private async Task CertificateAsync(Func<Task<ProxyCertificateStatus>> action)
    {
        if (Busy) return;
        var generation = _generation;
        Busy = true;
        Changed?.Invoke();
        try { var certificate = await action(); if (generation == _generation) Status = Status with { Certificate = certificate }; }
        catch { if (generation == _generation) Status = Status with { FailureReason = "The certificate action did not complete. Trust state is unchanged." }; }
        finally { if (generation == _generation) { Busy = false; Changed?.Invoke(); } }
    }

    public async Task ExecuteRestCheckAsync(string url, string method = "GET")
    {
        if (_owner is not { } owner || Busy) return;
        var generation = _generation;
        Busy = true; LastExecutionError = null;
        Changed?.Invoke();
        try { var result = await api.ExecuteRestAsync(new(owner.SessionId, owner.ProfileId, owner.ContextFingerprint, method, url)); if (generation == _generation) LastRestResult = result; }
        catch { if (generation == _generation) LastExecutionError = "The authenticated REST check was rejected or did not complete. Only HTTPS GET/HEAD/OPTIONS on approved hosts with an available authenticated API context are allowed."; }
        finally { if (generation == _generation) { Busy = false; Changed?.Invoke(); } }
    }

    public async Task ExecuteGraphQlCheckAsync(string endpointUrl, string query)
    {
        if (_owner is not { } owner || Busy) return;
        var generation = _generation;
        Busy = true; LastExecutionError = null;
        Changed?.Invoke();
        try { var result = await api.ExecuteGraphQlAsync(new(owner.SessionId, owner.ProfileId, owner.ContextFingerprint, endpointUrl, query)); if (generation == _generation) LastGraphQlResult = result; }
        catch { if (generation == _generation) LastExecutionError = "The authenticated GraphQL check was rejected or did not complete. Only a single query operation (no mutations or subscriptions) on approved HTTPS hosts is allowed."; }
        finally { if (generation == _generation) { Busy = false; Changed?.Invoke(); } }
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        ++_generation;
        _poll?.Cancel();
        if (_pollTask is not null) await _pollTask;
        _poll?.Dispose();
        Changed = null;
        // This is a frontend observer. Only explicit user commands may stop the backend runtime.
    }
}
