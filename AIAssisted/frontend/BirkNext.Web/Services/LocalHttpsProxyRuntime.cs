using System.Net.Http.Json;
using BirkNext.LocalHttpsProxy;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

public interface ILocalHttpsProxyApiService
{
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

    public LocalHttpsProxyStatus Status { get; private set; } = new();
    public AuthenticatedApiExecutionResult? LastRestResult { get; private set; }
    public AuthenticatedApiExecutionResult? LastGraphQlResult { get; private set; }
    public string? LastExecutionError { get; private set; }
    public bool Busy { get; private set; }
    public event Action? Changed;

    /// <summary>Status is only valid for the environment configuration it was produced for; any relevant change reads as Stale.</summary>
    public LocalHttpsProxyStatus For(FrontendAnalysisProfile? profile) => _identity is null ||
        (profile is not null && _identity == LocalHttpsProxyScope.Fingerprint(profile))
        ? Status : new() { State = LocalHttpsProxyState.Stale, Evidence = "Environment settings changed. The proxy session and its in-memory credential are no longer valid; start the proxy again after saving." };

    public bool SessionActive => _owner is not null;

    public async Task SynchronizeAsync(FrontendAnalysisProfile? profile)
    {
        if (_identity is not null && (profile is null || _identity != LocalHttpsProxyScope.Fingerprint(profile)))
            await StopAsync(LocalHttpsProxyState.Stale);
    }

    public async Task CheckCompatibilityAsync(FrontendAnalysisProfile profile)
    {
        if (Busy) return;
        var generation = _generation;
        var identity = LocalHttpsProxyScope.Fingerprint(profile);
        Busy = true;
        Changed?.Invoke();
        try
        {
            var result = await api.CheckCompatibilityAsync(LocalHttpsProxyScope.Request(profile));
            if (generation != _generation) return;
            _identity = identity;
            Status = _owner is not null ? Status with { Certificate = result.Certificate, PortAvailable = result.PortAvailable, ApprovedHosts = result.ApprovedHosts } : result;
        }
        catch { if (generation == _generation) { _identity = identity; Status = new() { State = LocalHttpsProxyState.NotStarted, FailureReason = "The compatibility check did not complete. Check that the local BirkNext backend is running, then retry." }; } }
        finally { if (generation == _generation) { Busy = false; Changed?.Invoke(); } }
    }

    public async Task StartAsync(FrontendAnalysisProfile profile)
    {
        await StopAsync();
        var generation = ++_generation;
        _identity = LocalHttpsProxyScope.Fingerprint(profile);
        var request = LocalHttpsProxyScope.Request(profile);
        Busy = true;
        Status = new() { State = LocalHttpsProxyState.Starting, Evidence = "Starting the loopback HTTPS proxy…" };
        Changed?.Invoke();
        try
        {
            var result = await api.StartAsync(request);
            if (generation != _generation)
            {
                if (result.SessionId is { } abandoned) { try { await api.StopAsync(new(abandoned, request.ProfileId, request.ContextFingerprint)); } catch { } }
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
        catch { if (generation == _generation) Status = new() { State = LocalHttpsProxyState.Failed, FailureReason = "The local HTTPS proxy could not be started. Check that the local BirkNext backend is running in the local workstation mode." }; }
        finally { if (generation == _generation) { Busy = false; Changed?.Invoke(); } }
    }

    public async Task RefreshAsync()
    {
        if (_owner is not { } owner || Busy) return;
        var generation = _generation;
        var operation = ++_operation;
        try
        {
            var result = await api.StatusAsync(owner);
            if (generation == _generation && operation == _operation)
            {
                Status = result;
                if (result.State is LocalHttpsProxyState.Stopped or LocalHttpsProxyState.Stale) { _poll?.Cancel(); _owner = null; }
            }
        }
        catch { if (generation == _generation && operation == _operation) { Status = new() { State = LocalHttpsProxyState.Stale, Evidence = "Proxy session lost. Start the proxy again." }; _poll?.Cancel(); _owner = null; } }
        if (generation == _generation) Changed?.Invoke();
    }

    private async Task PollAsync(CancellationToken ct)
    {
        try { while (!ct.IsCancellationRequested) { await Task.Delay(3000, ct); await RefreshAsync(); } }
        catch (OperationCanceledException) { }
    }

    public async Task StopAsync(LocalHttpsProxyState state = LocalHttpsProxyState.Stopped)
    {
        ++_generation;
        _poll?.Cancel(); _poll?.Dispose(); _poll = null;
        var owner = _owner;
        _owner = null; _identity = null; Busy = false;
        LastRestResult = null; LastGraphQlResult = null; LastExecutionError = null;
        Status = new()
        {
            State = state,
            Evidence = state == LocalHttpsProxyState.Stale ? "Environment settings changed. The proxy session and its in-memory credential were invalidated."
                : owner is null ? "Proxy not started." : "Proxy stopped. The in-memory authenticated API context was wiped."
        };
        Changed?.Invoke();
        if (owner is not null) { try { await api.StopAsync(owner); } catch { } }
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

    public async ValueTask DisposeAsync() => await StopAsync();
}
