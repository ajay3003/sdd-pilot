using System.Net;
using System.Security.Cryptography;
using BirkNext.Api.Services.AuthenticatedReview;
using BirkNext.Api.Services.ManagedEdge;
using BirkNext.LocalHttpsProxy;
using Microsoft.Extensions.Options;

namespace BirkNext.Api.Services.LocalHttpsProxy;

public interface ILocalHttpsProxyService
{
    Task<LocalHttpsProxyStatus> GetRuntimeAsync();
    /// <summary>Transient compatibility check: deployment mode, environment gate, port, certificate trust, approved hosts. Nothing is persisted.</summary>
    Task<LocalHttpsProxyStatus> CheckCompatibilityAsync(LocalHttpsProxyScopeRequest scope, CancellationToken cancellationToken = default);
    Task<LocalHttpsProxyStatus> StartAsync(LocalHttpsProxyScopeRequest scope, CancellationToken cancellationToken = default);
    Task<LocalHttpsProxyStatus> StatusAsync(LocalHttpsProxySessionRequest session);
    Task<LocalHttpsProxyStatus> StopAsync(LocalHttpsProxySessionRequest session);
    Task<ProxyCertificateStatus> InstallCertificateAsync(LocalHttpsProxyCertificateRequest request);
    Task<ProxyCertificateStatus> RemoveCertificateAsync(LocalHttpsProxyCertificateRequest request);
    /// <summary>Starts a separate Edge instance configured to use the running proxy. Never changes the Windows or default-profile proxy settings.</summary>
    Task<LocalHttpsProxyStatus> LaunchEdgeAsync(LocalHttpsProxyEdgeLaunchRequest request, CancellationToken cancellationToken = default);
    /// <summary>
    /// Closes the dedicated browser this runtime launched — only that one — and starts it again on the current proxy
    /// port. For a browser left over from an earlier runtime, which is running but pointed at a port that is gone.
    /// </summary>
    Task<LocalHttpsProxyStatus> RestartEdgeAsync(LocalHttpsProxyEdgeLaunchRequest request, CancellationToken cancellationToken = default);
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
    TransientAuthenticatedApiContextStore store, IUpstreamConnector upstream, IEdgeInstallationLocator edgeLocator, IProxyEdgeLauncher edgeLauncher,
    ILogger<LocalHttpsProxyService>? logger = null, Func<DateTimeOffset>? clock = null, IOwnedEdgeProcessInspector? edgeInspector = null,
    DedicatedCompanionProvisioner? companion = null) : BackgroundService, ILocalHttpsProxyService, ILocalHttpsProxySessionAccess, ILocalHttpsProxyStatusQuery
{
    public const string PortsOccupiedReason = "The configured loopback proxy ports are all occupied. BirkNext never stops the occupying process; free a port or configure LocalHttpsProxy:Port.";
    public const string EdgeMissingReason = "Microsoft Edge was not found in the standard installation locations. Install Edge to open the dedicated proxy browser.";

    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);
    /// <summary>Reads runtime evidence from the owned Edge process only. Absent means every runtime answer is "unknown".</summary>
    private readonly IOwnedEdgeProcessInspector _inspector = edgeInspector ?? new UnavailableOwnedEdgeProcessInspector();
    private readonly SemaphoreSlim _gate = new(1);
    private Session? _session;
    private volatile LocalHttpsProxyStatus _last = new() { State = LocalHttpsProxyState.Stopped };
    private volatile bool _shuttingDown;

    public async Task<LocalHttpsProxyStatus> GetRuntimeAsync()
    {
        if (!await _gate.WaitAsync(0)) return _last;
        try
        {
            var status = _session is { } session ? Describe(session.Scope, session) : _last;
            logger?.LogDebug("ProxyStateQueried {RuntimeId} {ProfileId} {Port}", status.RuntimeId, status.ProfileId, status.Port);
            return status;
        }
        finally { _gate.Release(); }
    }

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
            if (_shuttingDown) throw new InvalidOperationException("Backend is shutting down.");
            logger?.LogInformation("ProxyStartRequested {ProfileId}", scope.ProfileId);
            if (_session is { } existing)
            {
                if (Owns(existing, scope))
                {
                    logger?.LogInformation("ProxyAlreadyRunning {RuntimeId} {ProfileId} {Port}", existing.Id, scope.ProfileId, existing.Port);
                    return Describe(scope, existing);
                }
                return Describe(existing.Scope, existing) with { FailureReason = "A proxy is already running for another environment configuration. Stop that proxy explicitly before starting this one." };
            }
            var status = Describe(scope, null);
            if (!status.CanStart) return status with { State = LocalHttpsProxyState.Failed, FailureReason = status.FailureReason ?? PortsOccupiedReason };
            var hosts = ApprovedHostSet.Create(scope.TargetUrl, scope.ApprovedHosts);
            var session = new Session(scope, hosts, store, options.Value, _clock, logger, _inspector);
            _last = status with { RuntimeId = session.Id, ProfileId = scope.ProfileId, ContextFingerprint = scope.ContextFingerprint, RuntimeStatus = LocalHttpsProxyRuntimePhase.Starting, State = LocalHttpsProxyState.Starting, CanStart = false };
            try { authority.EnsureAuthority(); }
            catch (Exception ex) when (ex is CryptographicException or System.Security.SecurityException or PlatformNotSupportedException)
            {
                logger?.LogWarning("ProxyFailed {RuntimeId} {ProfileId} {Port} certificate generation {ExceptionType}", session.Id, scope.ProfileId, 0, ex.GetType().Name);
                return _last = _last with { RuntimeStatus = LocalHttpsProxyRuntimePhase.Failed, State = LocalHttpsProxyState.Failed, CanStart = true, FailureReason = "The BirkNext DEV inspection certificate could not be generated on this workstation." };
            }

            var server = new LocalHttpsProxyServer(hosts, authority, upstream, session, logger);
            var port = TryStart(server);
            if (port is null)
            {
                await server.DisposeAsync();
                logger?.LogWarning("ProxyFailed {RuntimeId} {ProfileId} {Port} ports occupied", session.Id, scope.ProfileId, 0);
                return _last = _last with { RuntimeStatus = LocalHttpsProxyRuntimePhase.Failed, State = LocalHttpsProxyState.Failed, CanStart = true, PortAvailable = false, FailureReason = PortsOccupiedReason };
            }
            session.Attach(server, port.Value);
            _session = session;
            logger?.LogInformation("ProxyStarted {RuntimeId} {ProfileId} {Port}", session.Id, scope.ProfileId, port);
            return _last = Describe(scope, session);
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
            if (_session is null && _last.RuntimeId == request.SessionId && _last.ProfileId == request.ProfileId && _last.ContextFingerprint == request.ContextFingerprint) return _last;
            var session = Get(request);
            await StopSessionAsync(session, "stopped by the user");
            return _last;
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

    public async Task<LocalHttpsProxyStatus> RestartEdgeAsync(LocalHttpsProxyEdgeLaunchRequest request, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var session = Get(new(request.SessionId, request.ProfileId, request.ContextFingerprint));
            // Only the handle this runtime owns is closed. BirkNext never goes looking for msedge.exe processes, so a
            // browser window the user opened themselves is not something this can reach.
            if (session.Edge is { } owned)
            {
                try { await owned.StopAsync().ConfigureAwait(false); }
                catch (Exception) { /* Check the owned process before allowing another launch. */ }
                if (owned.Running) return Describe(session.Scope, session) with { FailureReason = "Dedicated Edge could not be closed. Close it before restarting; a second process will not be launched." };
                owned.Dispose(); session.Edge = null; session.EdgeProxyPort = null; session.EdgeProxyArgument = null; session.ResetEdgeEvidence();
                companion?.Retire();
            }
        }
        finally { _gate.Release(); }
        return await LaunchEdgeAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<LocalHttpsProxyStatus> LaunchEdgeAsync(LocalHttpsProxyEdgeLaunchRequest request, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var session = Get(new(request.SessionId, request.ProfileId, request.ContextFingerprint));
            var status = Describe(session.Scope, session);
            if (session.Edge is { Running: true }) return status;
            if (!status.ProxyListening) return status with { FailureReason = "The proxy listener is not running." };
            EdgeInstallation? edge;
            try { edge = edgeLocator.Locate(); } catch (Exception) { edge = null; }
            if (edge is null) return status with { FailureReason = EdgeMissingReason };
            var profileDirectory = string.IsNullOrWhiteSpace(options.Value.EdgeProfileDirectory) ? DefaultEdgeProfileDirectory() : options.Value.EdgeProfileDirectory;
            IReadOnlyList<string> arguments;
            try
            {
                arguments = BuildEdgeArguments(session.Port, profileDirectory, session.Scope.TargetUrl, companion?.Prepare(session.Scope));
                Directory.CreateDirectory(profileDirectory);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            {
                return status with { FailureReason = "The dedicated BirkNext Edge profile directory is not usable. The normal Edge profile is never reused." };
            }
            try
            {
                session.Edge?.Dispose();
                session.Edge = edgeLauncher.Launch(edge.ExecutablePath, arguments);
                session.EdgeProfileDirectory = profileDirectory;
                // Recorded at launch, from the arguments actually passed — the evidence the UI later reports.
                session.EdgeProxyPort = session.Edge is null ? null : session.Port;
                session.EdgeProxyArgument = session.Edge is null ? null : arguments[0];
                session.EdgeExitLogged = false;
                // Evidence belongs to one launched process. A new launch starts with none, so nothing carries over.
                session.ResetEdgeEvidence();
            }
            catch (Exception) { session.Edge = null; session.EdgeProxyPort = null; session.EdgeProxyArgument = null; }
            if (session.Edge is { } launched) logger?.LogInformation("ProxyEdgeStarted {RuntimeId} {ProfileId} {Port} {EdgeProcessId}", session.Id, session.Scope.ProfileId, session.Port, launched.Id);
            return session.Edge is not null
                ? Describe(session.Scope, session) with { Evidence = $"Dedicated Microsoft Edge launched with --proxy-server=127.0.0.1:{session.Port}. Whether its traffic reaches the proxy is shown once a connection from it is observed. Sign in manually there." }
                : status with { FailureReason = "Microsoft Edge did not start. No existing browser or proxy setting was changed." };
        }
        finally { _gate.Release(); }
    }

    /// <summary>What the running dedicated browser's configuration is, as far as BirkNext can prove it.</summary>
    private sealed record EdgeConfigurationEvidence(
        DedicatedBrowserVerification Verification, DedicatedBrowserProxyArgument Argument, string? ObservedEndpoint, bool? ProfileVerified, DateTimeOffset? ReadAt);

    /// <summary>
    /// What BirkNext can prove about the dedicated browser's configuration, and nothing more.
    ///
    /// The evidence is the owned process's OWN arguments, read back from its process id: the launch record says what
    /// BirkNext intended, the process says what it is running with. When the process cannot be read, the launch record
    /// is reported as exactly that — configured, not verified. A browser BirkNext did not start is invisible here, and
    /// Edge settings, Edge policy and the Windows proxy are never read.
    /// </summary>
    private EdgeConfigurationEvidence VerifyEdge(Session session)
    {
        if (session.Edge is not { Running: true } edge)
            return new(DedicatedBrowserVerification.NotRunning, DedicatedBrowserProxyArgument.Unknown, null, null, null);

        var (launch, readAt) = session.LaunchEvidence(edge.Id, _inspector);
        if (launch is null)
        {
            session.LogArgumentOnce(edge.Id, () => logger?.LogInformation("DedicatedEdgeProxyArgumentUnverifiable {RuntimeId} {EdgeProcessId}", session.Id, edge.Id));
            return new(DedicatedBrowserVerification.NotConfirmed, DedicatedBrowserProxyArgument.Unknown, null, null, null);
        }

        var profileVerified = launch.UserDataDirectory is { Length: > 0 } actual && session.EdgeProfileDirectory is { Length: > 0 } expected
            && SamePath(actual, expected);
        var observed = ProxyEndpoint(launch.ProxyServer);
        var argument = launch.ProxyServer is null ? DedicatedBrowserProxyArgument.Missing
            : observed == $"127.0.0.1:{session.Port}" ? DedicatedBrowserProxyArgument.Verified
            : DedicatedBrowserProxyArgument.Mismatch;
        var verification = argument switch
        {
            DedicatedBrowserProxyArgument.Missing => DedicatedBrowserVerification.Missing,
            DedicatedBrowserProxyArgument.Verified when profileVerified => DedicatedBrowserVerification.Confirmed,
            _ => DedicatedBrowserVerification.Mismatch,
        };
        session.LogArgumentOnce(edge.Id, () =>
        {
            if (verification == DedicatedBrowserVerification.Confirmed)
                logger?.LogInformation("DedicatedEdgeProxyArgumentVerified {RuntimeId} {EdgeProcessId} {Endpoint}", session.Id, edge.Id, observed);
            else
                logger?.LogWarning("DedicatedEdgeProxyMismatch {RuntimeId} {EdgeProcessId} {Argument} {ObservedEndpoint} {ExpectedPort} {ProfileVerified}",
                    session.Id, edge.Id, argument, observed ?? "none", session.Port, profileVerified);
        });
        return new(verification, argument, observed, profileVerified, readAt);
    }

    /// <summary>
    /// The host:port of a Chromium <c>--proxy-server</c> value, for comparison and display only. Accepts the plain form
    /// BirkNext passes and an explicit <c>http://</c> scheme; anything else (a per-scheme list, another host) is returned
    /// as-is, bounded, so it compares unequal and is shown as what it is.
    /// </summary>
    internal static string? ProxyEndpoint(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) trimmed = trimmed[7..];
        trimmed = trimmed.TrimEnd('/');
        return trimmed.Length > 120 ? trimmed[..120] : trimmed;
    }

    private static bool SamePath(string a, string b)
    {
        try { return string.Equals(System.IO.Path.GetFullPath(a).TrimEnd('\\', '/'), System.IO.Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or System.IO.PathTooLongException) { return false; }
    }

    /// <summary>Traffic from the dedicated browser, only while that browser is the one running.</summary>
    private DedicatedBrowserProxyTraffic EdgeTraffic(Session session) =>
        session.Edge is not { Running: true } ? DedicatedBrowserProxyTraffic.Unknown
        : session.EdgeTrafficObservedAt is not null ? DedicatedBrowserProxyTraffic.Observed
        // Without a way to tell who owns a connection, "not observed" would be a guess.
        : !_inspector.CanAttributeConnections || session.AttributionUnavailable ? DedicatedBrowserProxyTraffic.Unknown
        : DedicatedBrowserProxyTraffic.NotObserved;

    public static string DefaultEdgeProfileDirectory() => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BirkNext", "LocalHttpsProxyEdgeProfile");

    /// <summary>
    /// Loopback traffic is proxied (<c>&lt;-loopback&gt;</c> removes Chromium's implicit loopback bypass, so a locally
    /// hosted target is captured too), EXCEPT the BirkNext backend itself. The Browser Companion talks to that backend on
    /// loopback, and routing it through the interception proxy made the Companion's connection depend on the proxy: a
    /// backend restart took the in-process proxy down, and Dedicated Edge could not reach the restarted backend until the
    /// proxy was started again. The proxy and the Companion are independent paths.
    /// </summary>
    public const string ProxyBypassList = "<-loopback>;127.0.0.1:5000;localhost:5000";

    /// <summary>Only the loopback proxy, the BirkNext-backend bypass, a dedicated profile, first-run suppression and the validated target URL.</summary>
    public static IReadOnlyList<string> BuildEdgeArguments(int port, string profileDirectory, string targetUrl, string? companionDirectory = null)
    {
        if (port is < 1 or > 65535) throw new ArgumentException("A bound loopback proxy port is required.");
        if (!System.IO.Path.IsPathFullyQualified(profileDirectory) || ManagedEdgePreflightService.IsNormalEdgeProfile(profileDirectory))
            throw new ArgumentException("A dedicated BirkNext profile directory is required; the normal Edge profile is never reused.");
        ManagedEdgePolicy.Origin(targetUrl);
        if (companionDirectory is not null) DedicatedCompanionProvisioner.ValidateManagedPath(companionDirectory);
        return [$"--proxy-server=127.0.0.1:{port}", $"--proxy-bypass-list={ProxyBypassList}", $"--user-data-dir={profileDirectory}", "--no-first-run", "--no-default-browser-check",
            .. companionDirectory is null ? Array.Empty<string>() : [$"--load-extension={companionDirectory}"], new Uri(targetUrl, UriKind.Absolute).AbsoluteUri];
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
        var edgeEvidence = VerifyEdge(session);
        var state = session.Stopped ? LocalHttpsProxyState.Stopped
            : session.Server?.Listening != true ? LocalHttpsProxyState.Failed
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
        if (state == LocalHttpsProxyState.Failed && !session.FailureLogged)
        {
            session.FailureLogged = true;
            store.Invalidate(session.Scope.ProfileId);
            logger?.LogWarning("ProxyFailed {RuntimeId} {ProfileId} {Port} listener exited", session.Id, session.Scope.ProfileId, session.Port);
        }
        return status with
        {
            RuntimeId = session.Id, ProfileId = session.Scope.ProfileId, ContextFingerprint = session.Scope.ContextFingerprint,
            RuntimeStatus = session.Stopped ? LocalHttpsProxyRuntimePhase.Stopped : session.Server?.Listening == true ? LocalHttpsProxyRuntimePhase.Running : LocalHttpsProxyRuntimePhase.Failed,
            ProxyListening = session.Server?.Listening == true, StartedAt = session.CreatedAt, LastHealthCheckAt = now,
            EdgeProcessId = session.Edge?.Id, EdgeRunning = session.Edge?.Running == true,
            Companion = companion?.Status(session.Edge?.Running == true) ?? new(),
            EdgeStartedAt = session.Edge?.StartedAt, EdgeProfileDirectory = session.EdgeProfileDirectory,
            ExpectedProxyPort = session.Port, EdgeProxyPort = session.EdgeProxyPort,
            ProxyArgumentConfigured = session.EdgeProxyArgument is not null,
            EdgeVerification = edgeEvidence.Verification,
            EdgeProxyArgument = edgeEvidence.Argument, ObservedEdgeProxyEndpoint = edgeEvidence.ObservedEndpoint,
            EdgeProfileVerified = edgeEvidence.ProfileVerified, EdgeLaunchVerifiedAt = edgeEvidence.ReadAt,
            EdgeProxyTraffic = EdgeTraffic(session),
            EdgeProxyTrafficObservedAt = session.Edge is { Running: true } ? session.EdgeTrafficObservedAt : null,
            UnattributedProxyConnections = session.UnattributedConnections,
            SessionId = session.Id, State = state, Port = session.Port, CanStart = false,
            InterceptedRequests = session.Intercepted, PassThroughConnections = session.PassThrough, TlsHandshakeFailures = session.TlsFailures,
            AuthenticatedRequestsObserved = session.BearerObserved, LastInterceptedHost = session.LastHost,
            ObservedEndpoints = session.ObservedEndpoints, ObservedNetworkEndpoints = session.ObservedNetworkEndpoints,
            AuthenticatedCredentialAvailable = available && state != LocalHttpsProxyState.Failed, CredentialExpired = expired,
            CredentialObservedHost = descriptor?.ObservedHost, CredentialObservedAt = descriptor?.ObservedAt, CredentialExpiresAt = descriptor?.ExpiresAt, CredentialFormat = descriptor?.Format,
            Evidence = evidence, FailureReason = state == LocalHttpsProxyState.Failed ? "Proxy listener faulted." : status.FailureReason
        };
    }

    private async Task StopSessionAsync(Session session, string reason)
    {
        logger?.LogInformation("ProxyStopRequested {RuntimeId} {ProfileId} {Port} {Reason}", session.Id, session.Scope.ProfileId, session.Port, reason);
        _last = Describe(session.Scope, session) with { RuntimeStatus = LocalHttpsProxyRuntimePhase.Stopping, StopReason = reason };
        session.Stopped = true;
        companion?.Retire();
        if (session.Server is { } server) await server.DisposeAsync().ConfigureAwait(false);
        if (session.Edge is { } edge)
        {
            try { await edge.StopAsync().ConfigureAwait(false); }
            finally { edge.Dispose(); session.Edge = null; }
        }
        store.Invalidate(session.Scope.ProfileId);
        if (ReferenceEquals(_session, session)) _session = null;
        _last = Describe(session.Scope, null) with { RuntimeId = session.Id, ProfileId = session.Scope.ProfileId, ContextFingerprint = session.Scope.ContextFingerprint, State = LocalHttpsProxyState.Stopped, RuntimeStatus = LocalHttpsProxyRuntimePhase.Stopped, StopReason = reason, Evidence = "Proxy stopped. The in-memory authenticated API context was wiped." };
        logger?.LogInformation("ProxyStopped {RuntimeId} {ProfileId} {Port} {Reason}", session.Id, session.Scope.ProfileId, session.Port, reason);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                store.PurgeExpired();
                if (_session is { Edge: { Running: false }, EdgeExitLogged: false } exited)
                {
                    exited.EdgeExitLogged = true;
                    logger?.LogInformation("ProxyEdgeExited {RuntimeId} {ProfileId} {Port}", exited.Id, exited.Scope.ProfileId, exited.Port);
                }
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
        _shuttingDown = true;
        if (_session is { } session) StopSessionAsync(session, "backend disposed").GetAwaiter().GetResult();
        store.InvalidateAll();
        base.Dispose();
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _shuttingDown = true;
        await base.StopAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try { if (_session is { } session) await StopSessionAsync(session, "backend shutdown"); }
        finally { _gate.Release(); }
    }

    /// <summary>Per-session counters and the traffic observer that decides whether an observed credential is promoted. Holds no credential itself.</summary>
    private sealed class Session(LocalHttpsProxyScopeRequest scope, ApprovedHostSet hosts, TransientAuthenticatedApiContextStore store, LocalHttpsProxyOptions options, Func<DateTimeOffset> clock, ILogger? logger, IOwnedEdgeProcessInspector inspector) : IProxyTrafficObserver
    {
        // Evidence about the currently owned Edge process. Reset whenever a different process is launched.
        private long _edgeTrafficObservedTicks;
        private int _unattributed, _attributionAnswers, _attributionUnknown;
        private OwnedEdgeLaunchEvidence? _launchEvidence;
        private int? _launchEvidencePid, _argumentLoggedPid;
        private DateTimeOffset? _launchEvidenceReadAt;

        private int _intercepted, _passThrough, _tlsFailures, _bearerObserved;
        private volatile string? _lastHost;
        private volatile string? _lastRejection;
        private long _credentialExpiresAtTicks;
        private readonly ObservedEndpointRegistry _endpoints = new();
        private readonly ObservedNetworkRegistry _networkEndpoints = new();

        public string Id { get; } = Guid.NewGuid().ToString("N");
        public LocalHttpsProxyScopeRequest Scope { get; } = scope;
        public ApprovedHostSet Hosts { get; } = hosts;
        public LocalHttpsProxyServer? Server { get; private set; }
        public int Port { get; private set; }
        public DateTimeOffset CreatedAt { get; } = clock();
        public volatile bool Stopped;
        public IProxyEdgeProcess? Edge;
        /// <summary>The proxy port this browser was launched with. Compared against the live port, never assumed equal to it.</summary>
        public int? EdgeProxyPort;
        /// <summary>The exact --proxy-server argument handed to the browser, for the technical details disclosure.</summary>
        public string? EdgeProxyArgument;
        public string? EdgeProfileDirectory;
        public bool EdgeExitLogged;
        public bool FailureLogged;
        public int Intercepted => _intercepted;
        public int PassThrough => _passThrough;
        public int TlsFailures => _tlsFailures;
        public int BearerObserved => _bearerObserved;
        public string? LastHost => _lastHost;
        public string? LastRejection => _lastRejection;
        /// <summary>Authenticated API endpoints discovered from this session's observed traffic. Runtime-only, no credential.</summary>
        public IReadOnlyList<ObservedAuthenticatedEndpoint> ObservedEndpoints => _endpoints.Snapshot();
        /// <summary>All page-correlated browser-observed network endpoints for this session. Runtime-only, no credential.</summary>
        public IReadOnlyList<ObservedNetworkEndpoint> ObservedNetworkEndpoints => _networkEndpoints.Snapshot();
        public DateTimeOffset? CredentialExpiresAt { get { var ticks = Interlocked.Read(ref _credentialExpiresAtTicks); return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero); } }

        public void Attach(LocalHttpsProxyServer server, int port) { Server = server; Port = port; }

        public DateTimeOffset? EdgeTrafficObservedAt { get { var ticks = Interlocked.Read(ref _edgeTrafficObservedTicks); return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero); } }
        public int UnattributedConnections => _unattributed;
        /// <summary>Connections arrived but none of their owners could be determined, so absence of evidence means nothing.</summary>
        public bool AttributionUnavailable => _attributionUnknown > 0 && _attributionAnswers == 0;

        public void ResetEdgeEvidence()
        {
            Interlocked.Exchange(ref _edgeTrafficObservedTicks, 0);
            Interlocked.Exchange(ref _unattributed, 0);
            Interlocked.Exchange(ref _attributionAnswers, 0);
            Interlocked.Exchange(ref _attributionUnknown, 0);
            _launchEvidence = null; _launchEvidencePid = null; _launchEvidenceReadAt = null; _argumentLoggedPid = null;
        }

        /// <summary>
        /// The owned process's arguments, read once per process id: a running process's command line does not change,
        /// but a read that failed is retried on the next status query.
        /// </summary>
        public (OwnedEdgeLaunchEvidence? Evidence, DateTimeOffset? ReadAt) LaunchEvidence(int processId, IOwnedEdgeProcessInspector reader)
        {
            if (_launchEvidencePid != processId || _launchEvidence is null)
            {
                _launchEvidence = reader.ReadLaunchEvidence(processId);
                _launchEvidencePid = processId;
                _launchEvidenceReadAt = _launchEvidence is null ? null : clock();
            }
            return (_launchEvidence, _launchEvidenceReadAt);
        }

        public void LogArgumentOnce(int processId, Action log)
        {
            if (_argumentLoggedPid == processId) return;
            _argumentLoggedPid = processId;
            log();
        }

        /// <summary>
        /// Attributes one accepted proxy connection. Only while the owned browser is running and nothing from it has been
        /// seen yet — once observed, there is nothing more to prove for this launch and no further lookups are made.
        /// </summary>
        public void OnClientConnected(IPEndPoint client, int proxyPort)
        {
            if (Stopped || Interlocked.Read(ref _edgeTrafficObservedTicks) != 0 || Edge is not { Running: true } edge) return;
            switch (inspector.ConnectionBelongsTo(client, proxyPort, edge.Id, edge.StartedAt))
            {
                case true:
                    Interlocked.Increment(ref _attributionAnswers);
                    if (Interlocked.CompareExchange(ref _edgeTrafficObservedTicks, clock().UtcTicks, 0) == 0)
                        logger?.LogInformation("ProxyTrafficObserved {RuntimeId} {EdgeProcessId} {Port}", Id, edge.Id, proxyPort);
                    break;
                case false:
                    Interlocked.Increment(ref _attributionAnswers);
                    Interlocked.Increment(ref _unattributed);
                    break;
                default:
                    if (Interlocked.Increment(ref _attributionUnknown) == 1)
                        logger?.LogInformation("ProxyTrafficVerificationUnknown {RuntimeId} {EdgeProcessId}", Id, edge.Id);
                    break;
            }
        }

        public void OnPassThrough(string host, int port) => Interlocked.Increment(ref _passThrough);
        public void OnInterceptedConnection(string host) => _lastHost = host;
        public void OnTlsHandshakeFailed(string host) => Interlocked.Increment(ref _tlsFailures);

        public void OnExchange(ProxyExchange exchange)
        {
            Interlocked.Increment(ref _intercepted);
            _lastHost = exchange.Host;
            // Page-oriented network discovery records every approved-host exchange (authenticated or not); credential promotion is separate.
            RecordNetworkEndpoint(exchange);
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

        /// <summary>Classifies an observed exchange into a safe page-correlated network endpoint. No token, header value or body is retained.</summary>
        private void RecordNetworkEndpoint(ProxyExchange exchange)
        {
            if (!Hosts.Contains(exchange.Host, exchange.Port)) return;
            _networkEndpoints.Record(NetworkTrafficClassifier.Classify(new NetworkRequestMetadata
            {
                Provenance = exchange.Provenance,
                Host = exchange.Host,
                Port = exchange.Port,
                Method = exchange.Method,
                Target = exchange.Path ?? "/",
                RequestContentType = exchange.RequestContentType,
                ResponseStatus = exchange.StatusCode,
                ResponseContentType = exchange.ResponseContentType,
                BearerObserved = exchange.BearerToken is not null,
                IsWebSocket = exchange.IsWebSocket,
                GraphQlOperationType = exchange.GraphQlOperationType,
                GraphQlOperationName = exchange.GraphQlOperationName,
                Referer = exchange.Referer,
                DurationMs = exchange.DurationMs,
                CacheDirectives = exchange.CacheDirectives,
                HasEtag = exchange.HasEtag,
                HasLastModified = exchange.HasLastModified,
                ResponseBytes = exchange.ResponseBytes,
            }, clock()));
        }

        /// <summary>Classifies an observed authenticated request into a safe endpoint record. No token, header value or body is retained.</summary>
        private void RecordObservedEndpoint(ProxyExchange exchange)
        {
            if (!Hosts.Contains(exchange.Host, exchange.Port) || exchange.Provenance is RequestProvenance.DiscoveryProbe or RequestProvenance.BirkNextDiagnostic
                || NetworkEvidencePolicy.Classify(exchange.Path) != NetworkResourceKind.Unknown) return;
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
