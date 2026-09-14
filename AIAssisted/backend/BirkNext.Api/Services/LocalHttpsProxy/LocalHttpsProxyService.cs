using System.Security.Cryptography;
using BirkNext.Api.Services.AuthenticatedReview;
using BirkNext.Api.Services.ManagedEdge;
using BirkNext.LocalHttpsProxy;
using Microsoft.Extensions.Options;

namespace BirkNext.Api.Services.LocalHttpsProxy;

public interface ILocalHttpsProxyService
{
    /// <summary>Transient compatibility check: deployment mode, environment gate, port, certificate trust, approved hosts. Nothing is persisted.</summary>
    Task<LocalHttpsProxyStatus> CheckCompatibilityAsync(LocalHttpsProxyScopeRequest scope, CancellationToken cancellationToken = default);
    Task<LocalHttpsProxyStatus> StartAsync(LocalHttpsProxyScopeRequest scope, CancellationToken cancellationToken = default);
    Task<LocalHttpsProxyStatus> StatusAsync(LocalHttpsProxySessionRequest session);
    Task<LocalHttpsProxyStatus> StopAsync(LocalHttpsProxySessionRequest session);
    Task<ProxyCertificateStatus> InstallCertificateAsync(LocalHttpsProxyCertificateRequest request);
    Task<ProxyCertificateStatus> RemoveCertificateAsync(LocalHttpsProxyCertificateRequest request);
    /// <summary>Starts a separate Edge instance configured to use the running proxy. Never changes the Windows or default-profile proxy settings.</summary>
    Task<LocalHttpsProxyStatus> LaunchEdgeAsync(LocalHttpsProxyEdgeLaunchRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Lets the execution service resolve the approved host scope of a live session. Throws <see cref="System.Collections.Generic.KeyNotFoundException"/> for a stale or foreign session.</summary>
public interface ILocalHttpsProxySessionAccess
{
    ApprovedHostSet GetScope(LocalHttpsProxySessionRequest session);
}

/// <summary>
/// Read-only, profile-keyed view of the current proxy/credential status for reviews that do not hold a runtime session id. Returns the
/// live <see cref="LocalHttpsProxyStatus"/> only when the current session matches the profile+fingerprint; otherwise null. No credential.
/// </summary>
public interface ILocalHttpsProxyStatusQuery
{
    LocalHttpsProxyStatus? StatusForProfile(string profileId, string contextFingerprint);
}

/// <summary>
/// Owns the single loopback proxy session. Enforces the DEV/non-production gate, the LocalWorkstation deployment gate, loopback-only
/// binding and the approved-host allowlist; promotes an observed Bearer credential to the memory-only authenticated API context only
/// when it was sent over HTTPS to an approved host on a successful request, is not expired and matches the expected tenant (when
/// both are known); wipes the credential on stop, replacement, expiry, session lifetime and shutdown.
/// </summary>
public sealed class LocalHttpsProxyService(IOptions<LocalHttpsProxyOptions> options, IOptions<AuthenticatedReviewOptions> runtime, IProxyCertificateAuthority authority,
    TransientAuthenticatedApiContextStore store, IUpstreamConnector upstream, IEdgeInstallationLocator edgeLocator, IManagedEdgeLauncher edgeLauncher,
    ILogger<LocalHttpsProxyService>? logger = null, Func<DateTimeOffset>? clock = null) : BackgroundService, ILocalHttpsProxyService, ILocalHttpsProxySessionAccess, ILocalHttpsProxyStatusQuery
{
    public const string PortsOccupiedReason = "The configured loopback proxy ports are all occupied. BirkNext never stops the occupying process; free a port or configure LocalHttpsProxy:Port.";
    public const string EdgeMissingReason = "Microsoft Edge was not found in the standard installation locations. Configure the proxy manually in Windows proxy settings instead.";

    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly SemaphoreSlim _gate = new(1);
    private Session? _session;

    public async Task<LocalHttpsProxyStatus> CheckCompatibilityAsync(LocalHttpsProxyScopeRequest scope, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var current = _session is { } session && Owns(session, scope) ? session : null;
            return Describe(scope, current);
        }
        finally { _gate.Release(); }
    }

    public async Task<LocalHttpsProxyStatus> StartAsync(LocalHttpsProxyScopeRequest scope, CancellationToken cancellationToken = default)
    {
        ValidateIdentity(scope.ProfileId, scope.ContextFingerprint);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_session is { } existing)
            {
                if (Owns(existing, scope)) return Describe(scope, existing);
                // A different environment (or changed target configuration) replaces the session: the old credential is wiped first.
                await StopSessionAsync(existing, "replaced by another environment");
            }
            var status = Describe(scope, null);
            if (!status.CanStart) return status with { State = LocalHttpsProxyState.Failed, FailureReason = status.FailureReason ?? PortsOccupiedReason };
            var hosts = ApprovedHostSet.Create(scope.TargetUrl, scope.ApprovedHosts);
            try { authority.EnsureAuthority(); }
            catch (Exception ex) when (ex is CryptographicException or System.Security.SecurityException or PlatformNotSupportedException)
            {
                logger?.LogWarning("Inspection root generation failed with {ExceptionType}.", ex.GetType().Name);
                return status with { State = LocalHttpsProxyState.Failed, CanStart = false, FailureReason = "The BirkNext DEV inspection certificate could not be generated on this workstation." };
            }

            var session = new Session(scope, hosts, store, options.Value, _clock, logger);
            var server = new LocalHttpsProxyServer(hosts, authority, upstream, session, logger);
            var port = TryStart(server);
            if (port is null)
            {
                await server.DisposeAsync();
                return status with { State = LocalHttpsProxyState.Failed, CanStart = false, PortAvailable = false, FailureReason = PortsOccupiedReason };
            }
            session.Attach(server, port.Value);
            _session = session;
            logger?.LogInformation("Local HTTPS proxy listening on 127.0.0.1:{Port} for {HostCount} approved host(s) of a {EnvironmentType} environment.", port, hosts.Authorities.Count, scope.EnvironmentType);
            return Describe(scope, session);
        }
        finally { _gate.Release(); }
    }

    public async Task<LocalHttpsProxyStatus> StatusAsync(LocalHttpsProxySessionRequest request)
    {
        await _gate.WaitAsync();
        try
        {
            var session = Get(request);
            if (_clock() - session.CreatedAt > options.Value.SessionLifetime)
            {
                await StopSessionAsync(session, "session lifetime reached");
                return Describe(session.Scope, null) with { State = LocalHttpsProxyState.Stale, Evidence = "The proxy session reached its lifetime and was stopped; the in-memory authenticated API context was wiped. Start the proxy again." };
            }
            return Describe(session.Scope, session);
        }
        finally { _gate.Release(); }
    }

    public async Task<LocalHttpsProxyStatus> StopAsync(LocalHttpsProxySessionRequest request)
    {
        await _gate.WaitAsync();
        try
        {
            var session = Get(request);
            await StopSessionAsync(session, "stopped by the user");
            return Describe(session.Scope, null) with { State = LocalHttpsProxyState.Stopped, Evidence = "Proxy stopped. The in-memory authenticated API context was wiped immediately." };
        }
        finally { _gate.Release(); }
    }

    public Task<ProxyCertificateStatus> InstallCertificateAsync(LocalHttpsProxyCertificateRequest request) => CertificateActionAsync(request, () => authority.Install());

    public Task<ProxyCertificateStatus> RemoveCertificateAsync(LocalHttpsProxyCertificateRequest request) => CertificateActionAsync(request, () => authority.Remove());

    private async Task<ProxyCertificateStatus> CertificateActionAsync(LocalHttpsProxyCertificateRequest request, Func<ProxyCertificateStatus> action)
    {
        if (!request.Confirmed) throw new ArgumentException("Certificate trust changes require explicit confirmation.");
        if (!runtime.Value.IsLocalWorkstation) throw new InvalidOperationException(LocalHttpsProxyEnvironmentPolicy.RemoteDeploymentReason);
        await _gate.WaitAsync();
        try
        {
            try { return action(); }
            catch (Exception ex) when (ex is CryptographicException or System.Security.SecurityException or PlatformNotSupportedException)
            {
                // Typical cause: the user declined the Windows trust dialog. Report the unchanged state.
                logger?.LogInformation("Certificate trust change did not complete ({ExceptionType}).", ex.GetType().Name);
                return authority.Status() with { Guidance = "The certificate store change was not completed (it may have been declined). Trust state is unchanged." };
            }
        }
        finally { _gate.Release(); }
    }

    public async Task<LocalHttpsProxyStatus> LaunchEdgeAsync(LocalHttpsProxyEdgeLaunchRequest request, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var session = Get(new(request.SessionId, request.ProfileId, request.ContextFingerprint));
            var status = Describe(session.Scope, session);
            EdgeInstallation? edge;
            try { edge = edgeLocator.Locate(); } catch (Exception) { edge = null; }
            if (edge is null) return status with { FailureReason = EdgeMissingReason };
            var profileDirectory = string.IsNullOrWhiteSpace(options.Value.EdgeProfileDirectory) ? DefaultEdgeProfileDirectory() : options.Value.EdgeProfileDirectory;
            IReadOnlyList<string> arguments;
            try
            {
                arguments = BuildEdgeArguments(session.Port, profileDirectory, session.Scope.TargetUrl);
                Directory.CreateDirectory(profileDirectory);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            {
                return status with { FailureReason = "The dedicated BirkNext Edge profile directory is not usable. The normal Edge profile is never reused." };
            }
            bool started;
            try { started = edgeLauncher.Launch(edge.ExecutablePath, arguments); }
            catch (Exception) { started = false; }
            return started
                ? status with { Evidence = $"A separate Microsoft Edge instance was started with --proxy-server=127.0.0.1:{session.Port} and a dedicated BirkNext profile. Sign in manually there. If organization policy forces proxy settings, configure the proxy manually in Windows instead." }
                : status with { FailureReason = "Microsoft Edge did not start. No existing browser or proxy setting was changed." };
        }
        finally { _gate.Release(); }
    }

    public static string DefaultEdgeProfileDirectory() => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BirkNext", "LocalHttpsProxyEdgeProfile");

    /// <summary>Only the loopback proxy, a loopback bypass, a dedicated profile, first-run suppression and the validated target URL.</summary>
    public static IReadOnlyList<string> BuildEdgeArguments(int port, string profileDirectory, string targetUrl)
    {
        if (port is < 1 or > 65535) throw new ArgumentException("A bound loopback proxy port is required.");
        if (!System.IO.Path.IsPathFullyQualified(profileDirectory) || ManagedEdgePreflightService.IsNormalEdgeProfile(profileDirectory))
            throw new ArgumentException("A dedicated BirkNext profile directory is required; the normal Edge profile is never reused.");
        ManagedEdgePolicy.Origin(targetUrl);
        return [$"--proxy-server=127.0.0.1:{port}", "--proxy-bypass-list=<-loopback>", $"--user-data-dir={profileDirectory}", "--no-first-run", "--no-default-browser-check", new Uri(targetUrl, UriKind.Absolute).AbsoluteUri];
    }

    ApprovedHostSet ILocalHttpsProxySessionAccess.GetScope(LocalHttpsProxySessionRequest session) => Get(session).Hosts;

    LocalHttpsProxyStatus? ILocalHttpsProxyStatusQuery.StatusForProfile(string profileId, string contextFingerprint)
    {
        _gate.Wait();
        try
        {
            var session = _session;
            if (session is null ||
                !string.Equals(session.Scope.ProfileId, profileId, StringComparison.Ordinal) ||
                !string.Equals(session.Scope.ContextFingerprint, contextFingerprint, StringComparison.Ordinal))
                return null;
            return Describe(session.Scope, session);
        }
        finally { _gate.Release(); }
    }

    // ── internals ────────────────────────────────────────────────────────────

    private static void ValidateIdentity(string profileId, string fingerprint)
    {
        if (string.IsNullOrWhiteSpace(profileId) || profileId.Length > 128 || fingerprint is null || fingerprint.Length != 64 || !fingerprint.All(Uri.IsHexDigit))
            throw new ArgumentException("Profile identity and context fingerprint are required.");
    }

    private static bool Owns(Session session, LocalHttpsProxyScopeRequest scope) =>
        string.Equals(session.Scope.ProfileId, scope.ProfileId, StringComparison.Ordinal) && string.Equals(session.Scope.ContextFingerprint, scope.ContextFingerprint, StringComparison.Ordinal);

    private Session Get(LocalHttpsProxySessionRequest request)
    {
        var session = _session;
        if (session is null || !string.Equals(session.Id, request.SessionId, StringComparison.Ordinal) ||
            !string.Equals(session.Scope.ProfileId, request.ProfileId, StringComparison.Ordinal) ||
            !string.Equals(session.Scope.ContextFingerprint, request.ContextFingerprint, StringComparison.Ordinal))
            throw new System.Collections.Generic.KeyNotFoundException("Proxy session is no longer current.");
        return session;
    }

    private int? TryStart(LocalHttpsProxyServer server)
    {
        var preferred = options.Value.Port;
        if (preferred == 0) { try { server.Start(0); return server.Endpoint!.Port; } catch (System.Net.Sockets.SocketException) { return null; } }
        var limit = Math.Clamp(options.Value.PortSearchLimit, 0, 100);
        for (var port = preferred; port <= Math.Min(65535, preferred + limit); port++)
        {
            try { server.Start(port); return server.Endpoint!.Port; }
            catch (System.Net.Sockets.SocketException) { /* occupied: try the next loopback port, never touch the occupying process */ }
        }
        return null;
    }

    private bool AnyPortAvailable()
    {
        var preferred = options.Value.Port;
        if (preferred == 0) return true;
        var limit = Math.Clamp(options.Value.PortSearchLimit, 0, 100);
        for (var port = preferred; port <= Math.Min(65535, preferred + limit); port++) if (LocalHttpsProxyServer.IsLoopbackPortFree(port)) return true;
        return false;
    }

    private LocalHttpsProxyStatus Describe(LocalHttpsProxyScopeRequest scope, Session? session)
    {
        var local = runtime.Value.IsLocalWorkstation;
        var environmentAllowed = LocalHttpsProxyEnvironmentPolicy.IsAllowed(scope.EnvironmentType);
        ApprovedHostSet? hosts = null;
        string? failure = null;
        try { hosts = ApprovedHostSet.Create(scope.TargetUrl, scope.ApprovedHosts); }
        catch (ArgumentException ex) { failure = ex.Message; }
        if (!local) failure = LocalHttpsProxyEnvironmentPolicy.RemoteDeploymentReason;
        else if (!environmentAllowed) failure = LocalHttpsProxyEnvironmentPolicy.EnvironmentBlockedReason;
        var certificate = local ? authority.Status() : new ProxyCertificateStatus { State = ProxyCertificateTrustState.Unknown, Guidance = "Unavailable in this deployment mode." };
        var portAvailable = session is not null || (local && failure is null && AnyPortAvailable());
        if (failure is null && !portAvailable) failure = PortsOccupiedReason;
        var targetOrigin = hosts is null ? null : hosts.TargetPort == 443 ? $"https://{hosts.TargetHost}" : $"https://{hosts.TargetHost}:{hosts.TargetPort}";
        var status = new LocalHttpsProxyStatus
        {
            LocalIntegrationAvailable = local, EnvironmentAllowed = environmentAllowed, EnvironmentType = scope.EnvironmentType, PortAvailable = portAvailable,
            Certificate = certificate, ApprovedHosts = hosts?.Authorities ?? [], TargetOrigin = targetOrigin,
            CanStart = session is null && failure is null, FailureReason = failure,
            Evidence = failure ?? (certificate.State == ProxyCertificateTrustState.Trusted
                ? "Ready to start. Edge must use the proxy 127.0.0.1:<port> after start; sign in manually there."
                : "Ready to start. The BirkNext DEV inspection certificate must be trusted before Edge accepts intercepted connections.")
        };
        return session is null ? status : Merge(status, session);
    }

    private LocalHttpsProxyStatus Merge(LocalHttpsProxyStatus status, Session session)
    {
        var now = _clock();
        var descriptor = store.Describe(session.Scope.ProfileId, session.Scope.ContextFingerprint);
        var available = descriptor is not null;
        var expired = !available && session.CredentialExpiresAt is { } expiresAt && expiresAt <= now;
        var trusted = status.Certificate.State == ProxyCertificateTrustState.Trusted;
        var state = session.Stopped ? LocalHttpsProxyState.Stopped
            : session.Server?.Faulted == true ? LocalHttpsProxyState.Failed
            : available ? LocalHttpsProxyState.Ready
            : expired ? LocalHttpsProxyState.WaitingForAuthenticatedTraffic
            : session.BearerObserved > 0 ? LocalHttpsProxyState.AuthenticatedTrafficDetected
            : session.Intercepted > 0 ? LocalHttpsProxyState.WaitingForAuthenticatedTraffic
            : !trusted ? LocalHttpsProxyState.WaitingForCertificateTrust
            : LocalHttpsProxyState.Listening;
        var evidence = state switch
        {
            LocalHttpsProxyState.Ready => $"Authenticated API context available (memory only) from approved host {descriptor!.ObservedHost}. Approved REST GET/HEAD/OPTIONS and GraphQL query checks can run. The credential is never displayed, logged or saved.",
            LocalHttpsProxyState.WaitingForAuthenticatedTraffic when expired => "Expired - perform an authenticated action in the browser again. The previous in-memory credential was wiped.",
            LocalHttpsProxyState.AuthenticatedTrafficDetected => $"Authenticated traffic detected, but no credential was accepted yet: {session.LastRejection ?? "waiting for a successful application request"}.",
            LocalHttpsProxyState.WaitingForAuthenticatedTraffic => "Listening and intercepting approved hosts. Sign in manually in Edge and open the target application; BirkNext waits for authenticated API traffic.",
            LocalHttpsProxyState.WaitingForCertificateTrust => session.TlsFailures > 0
                ? "Edge rejected the BirkNext DEV inspection certificate. Install the test certificate (explicit confirmation) or import it manually, then reload the target application."
                : "Listening. Install or import the BirkNext DEV inspection certificate before Edge can use the intercepted connection.",
            LocalHttpsProxyState.Failed => "The proxy listener stopped unexpectedly. Stop and start the proxy again.",
            LocalHttpsProxyState.Stopped => "Proxy stopped. The in-memory authenticated API context was wiped.",
            _ => $"Listening on 127.0.0.1:{session.Port}. Configure Edge to use this proxy, sign in manually, then open the target application."
        };
        return status with
        {
            SessionId = session.Id, State = state, Port = session.Port, CanStart = false,
            InterceptedRequests = session.Intercepted, PassThroughConnections = session.PassThrough, TlsHandshakeFailures = session.TlsFailures,
            AuthenticatedRequestsObserved = session.BearerObserved, LastInterceptedHost = session.LastHost, ObservedEndpoints = session.ObservedEndpoints,
            AuthenticatedCredentialAvailable = available, CredentialExpired = expired,
            CredentialObservedHost = descriptor?.ObservedHost, CredentialObservedAt = descriptor?.ObservedAt, CredentialExpiresAt = descriptor?.ExpiresAt, CredentialFormat = descriptor?.Format,
            Evidence = evidence, FailureReason = state == LocalHttpsProxyState.Failed ? "Proxy listener faulted." : status.FailureReason
        };
    }

    private async Task StopSessionAsync(Session session, string reason)
    {
        session.Stopped = true;
        if (session.Server is { } server) await server.DisposeAsync();
        store.Invalidate(session.Scope.ProfileId);
        if (ReferenceEquals(_session, session)) _session = null;
        logger?.LogInformation("Local HTTPS proxy session ended ({Reason}); the in-memory credential was wiped.", reason);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                store.PurgeExpired();
                if (_session is { } session && _clock() - session.CreatedAt > options.Value.SessionLifetime)
                {
                    await _gate.WaitAsync(stoppingToken);
                    try { if (ReferenceEquals(_session, session)) await StopSessionAsync(session, "session lifetime reached"); }
                    finally { _gate.Release(); }
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    public override void Dispose()
    {
        if (_session is { } session)
        {
            session.Stopped = true;
            session.Server?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _session = null;
        }
        store.InvalidateAll();
        base.Dispose();
    }

    /// <summary>Per-session counters and the traffic observer that decides whether an observed credential is promoted. Holds no credential itself.</summary>
    private sealed class Session(LocalHttpsProxyScopeRequest scope, ApprovedHostSet hosts, TransientAuthenticatedApiContextStore store, LocalHttpsProxyOptions options, Func<DateTimeOffset> clock, ILogger? logger) : IProxyTrafficObserver
    {
        private int _intercepted, _passThrough, _tlsFailures, _bearerObserved;
        private volatile string? _lastHost;
        private volatile string? _lastRejection;
        private long _credentialExpiresAtTicks;
        private readonly ObservedEndpointRegistry _endpoints = new();

        public string Id { get; } = Guid.NewGuid().ToString("N");
        public LocalHttpsProxyScopeRequest Scope { get; } = scope;
        public ApprovedHostSet Hosts { get; } = hosts;
        public LocalHttpsProxyServer? Server { get; private set; }
        public int Port { get; private set; }
        public DateTimeOffset CreatedAt { get; } = clock();
        public volatile bool Stopped;
        public int Intercepted => _intercepted;
        public int PassThrough => _passThrough;
        public int TlsFailures => _tlsFailures;
        public int BearerObserved => _bearerObserved;
        public string? LastHost => _lastHost;
        public string? LastRejection => _lastRejection;
        /// <summary>Authenticated API endpoints discovered from this session's observed traffic. Runtime-only, no credential.</summary>
        public IReadOnlyList<ObservedAuthenticatedEndpoint> ObservedEndpoints => _endpoints.Snapshot();
        public DateTimeOffset? CredentialExpiresAt { get { var ticks = Interlocked.Read(ref _credentialExpiresAtTicks); return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero); } }

        public void Attach(LocalHttpsProxyServer server, int port) { Server = server; Port = port; }

        public void OnPassThrough(string host, int port) => Interlocked.Increment(ref _passThrough);
        public void OnInterceptedConnection(string host) => _lastHost = host;
        public void OnTlsHandshakeFailed(string host) => Interlocked.Increment(ref _tlsFailures);

        public void OnExchange(ProxyExchange exchange)
        {
            Interlocked.Increment(ref _intercepted);
            _lastHost = exchange.Host;
            if (exchange.BearerToken is not { } token) return;
            Interlocked.Increment(ref _bearerObserved);
            if (Stopped) return;
            // Authenticated endpoint discovery is independent of credential promotion: a request that is rejected for promotion (wrong
            // status, tenant, expiry) is still real observed evidence of an authenticated endpoint. Only approved host:port are recorded.
            RecordObservedEndpoint(exchange);
            if (!Hosts.Contains(exchange.Host, exchange.Port)) { _lastRejection = "the credential was observed on a host outside the approved allowlist"; return; }
            if (exchange.StatusCode is < 200 or >= 300) { _lastRejection = $"the last authenticated request returned HTTP {exchange.StatusCode}"; return; }
            var metadata = BearerTokenInspector.Inspect(token);
            var now = clock();
            if (metadata.ExpiresAt is { } tokenExpiry && tokenExpiry <= now) { _lastRejection = "the observed token had already expired"; return; }
            if (Scope.ExpectedTenant is { } expected && Guid.TryParse(expected, out var expectedTenant) && metadata.TenantId is { } tenant && Guid.Parse(tenant) != expectedTenant)
            { _lastRejection = "the observed token belongs to a different tenant than this environment expects"; return; }
            var expiresAt = now + options.CredentialLifetime;
            if (metadata.ExpiresAt is { } bound && bound < expiresAt) expiresAt = bound;
            ((ITransientCredentialSink)store).Store(Scope.ProfileId, Scope.ContextFingerprint, exchange.Host, Hosts, token, expiresAt, metadata.Format);
            Interlocked.Exchange(ref _credentialExpiresAtTicks, expiresAt.UtcTicks);
            _lastRejection = null;
            logger?.LogInformation("Authenticated API context established for environment {ProfileId} from approved host {Host} ({Format}); expires {ExpiresAt:u}. The credential is held in memory only.",
                Scope.ProfileId, exchange.Host, metadata.Format, expiresAt);
        }

        /// <summary>Classifies an observed authenticated request into a safe endpoint record. No token, header value or body is retained.</summary>
        private void RecordObservedEndpoint(ProxyExchange exchange)
        {
            if (!Hosts.Contains(exchange.Host, exchange.Port)) return;
            var endpoint = ObservedTrafficClassifier.Classify(new ObservedRequestMetadata
            {
                Host = exchange.Host,
                Port = exchange.Port,
                Method = exchange.Method,
                Target = exchange.Path ?? "/",
                RequestContentType = exchange.RequestContentType,
                ResponseStatus = exchange.StatusCode,
                ResponseContentType = exchange.ResponseContentType,
                BearerObserved = true,
                GraphQlOperationType = exchange.GraphQlOperationType,
                GraphQlOperationName = exchange.GraphQlOperationName
            }, clock());
            if (endpoint is not null) _endpoints.Record(endpoint);
        }
    }
}
