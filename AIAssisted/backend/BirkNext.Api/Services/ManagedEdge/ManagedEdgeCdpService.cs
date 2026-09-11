using System.Collections.Concurrent;
using BirkNext.ManagedEdge;
using BirkNext.Api.Services.AuthenticatedReview;
using Microsoft.Extensions.Options;

namespace BirkNext.Api.Services.ManagedEdge;

public interface IManagedEdgeCdpService
{
    Task<ManagedEdgeStatus> ConnectAsync(ManagedEdgeConnectRequest request, CancellationToken cancellationToken = default);
    Task<ManagedEdgeStatus> StatusAsync(ManagedEdgeSessionRequest request, bool verify = false);
    Task<ManagedEdgeProbeResult> ExecuteSameOriginFetchAsync(ManagedEdgeFetchRequest request);
    Task DisconnectAsync(ManagedEdgeSessionRequest request);
}

public sealed class ManagedEdgeCdpService(IManagedEdgeConnector connector, IOptions<ManagedEdgeOptions> options,
    IOptions<AuthenticatedReviewOptions> runtime, ILogger<ManagedEdgeCdpService>? logger = null) : BackgroundService, IManagedEdgeCdpService
{
    private readonly ConcurrentDictionary<string, Session> _sessions = new();
    private readonly SemaphoreSlim _connectGate = new(1);

    public async Task<ManagedEdgeStatus> ConnectAsync(ManagedEdgeConnectRequest request, CancellationToken cancellationToken = default)
    {
        if (!runtime.Value.IsLocalWorkstation) return new() { State = ManagedEdgeState.NotConfigured, Evidence = "Managed Edge requires the LocalWorkstation runtime." };
        var endpoint = ManagedEdgePolicy.Endpoint(options.Value.Endpoint).AbsoluteUri.TrimEnd('/');
        var origin = ManagedEdgePolicy.Origin(request.TargetUrl);
        if (string.IsNullOrWhiteSpace(request.ProfileId) || request.ProfileId.Length > 128 || request.ContextFingerprint.Length != 64 || !request.ContextFingerprint.All(Uri.IsHexDigit))
            throw new ArgumentException("Profile identity and context fingerprint are required.");
        await _connectGate.WaitAsync(cancellationToken);
        IManagedEdgeBrowser? browser = null;
        try
        {
            if (_sessions.Count >= 8) return new() { State = ManagedEdgeState.Failed, Evidence = "Disconnect an existing session before connecting another." };
            browser = await connector.ConnectAsync(endpoint, cancellationToken);
            var matches = browser.Pages.Where(p => !p.IsClosed && ManagedEdgePolicy.MatchesOrigin(p.Url, origin)).ToArray();
            var discovered = (browser.DiscoveredPageUrls ?? []).Count(u => ManagedEdgePolicy.MatchesOrigin(u, origin));
            // Candidate Defender for Cloud Apps reverse-proxy deliveries of THIS application (host identity), regardless of trust model.
            var proxied = browser.Pages.Where(p => !p.IsClosed && ManagedEdgePolicy.IsCorrelatedMcasProxyOrigin(p.Url, origin)).ToArray();
            var status = new ManagedEdgeStatus { Endpoint = endpoint, TargetOrigin = origin, TrustModel = request.TrustModel, ContextCount = browser.ContextCount, PageCount = browser.Pages.Count, DiscoveredTargetTabs = discovered };

            // Exact origin always wins, whatever trust model was requested.
            if (matches.Length == 1)
                return Register(request, browser, matches[0], origin, status with { DeliveryOrigin = origin, TrustModel = ManagedEdgeTrustModel.ExactOrigin,
                    Evidence = "Expected origin matched. Authenticated access is not yet proven." });
            if (matches.Length > 1)
            {
                browser.Dispose();
                return status with { State = ManagedEdgeState.AmbiguousTargetTabs, Evidence = "Keep exactly one tab for this target origin open, then reconnect." };
            }

            if (request.TrustModel == ManagedEdgeTrustModel.ApprovedMcasProxyOrigin && proxied.Length > 0)
            {
                if (proxied.Length > 1)
                {
                    browser.Dispose();
                    return status with { State = ManagedEdgeState.AmbiguousTargetTabs, Evidence = "Keep exactly one Defender for Cloud Apps proxied tab for this application open, then reconnect." };
                }
                var page = proxied[0];
                var delivery = ManagedEdgePolicy.Origin(page.Url);
                var correlation = await CorrelateProxiedDeliveryAsync(page, origin, delivery);
                if (correlation is not null)
                    return Register(request, browser, page, delivery, status with { DeliveryOrigin = delivery, CorrelationEvidence = correlation,
                        Evidence = "Approved Defender for Cloud Apps proxied delivery is correlated to the configured target. Authenticated access is not yet proven." });
                browser.Dispose();
                return status with { State = ManagedEdgeState.ProxiedDeliveryUncorrelated, DeliveryOrigin = delivery,
                    Evidence = "A proxied tab for this application is open, but BirkNext could not correlate it to the configured target through the browser session (live document origin and navigation from the target, Entra or the Defender sign-in intermediary). Open the configured target URL in that tab, sign in, then reconnect. Arbitrary access.mcas.ms origins are never trusted." };
            }

            browser.Dispose();
            // The browser advertises the tab but refused to expose it to the debugger: report that precisely instead of "not found".
            if (discovered > 0)
                return status with { State = ManagedEdgeState.TargetTabNotInspectable,
                    Evidence = "The target tab is open, but Edge refused debugger attachment to it. This is expected for a Microsoft Defender for Cloud Apps protected session in a signed-in Edge work profile, where developer tools are turned off. BirkNext does not bypass browser protection, so authenticated access cannot be verified through CDP for this tab." };
            return status with { State = ManagedEdgeState.TargetTabNotFound,
                Evidence = proxied.Length > 0
                    ? "No exact-origin tab is open. A Defender for Cloud Apps proxied tab for this application exists, but exact origin trust is active; enable approved MCAS proxy trust to evaluate it."
                    : "Open the target application in Edge, then reconnect." };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            browser?.Dispose();
            // Never return/log transport exceptions: they may contain URLs or protocol data.
            return new() { Endpoint = endpoint, TargetOrigin = origin, State = ManagedEdgeState.Failed, Evidence = "Local CDP connection failed. Check Edge and port 9222, then retry." };
        }
        catch (OperationCanceledException) { browser?.Dispose(); throw; }
        finally { _connectGate.Release(); }
    }

    private ManagedEdgeStatus Register(ManagedEdgeConnectRequest request, IManagedEdgeBrowser browser, IManagedEdgePage page, string probeOrigin, ManagedEdgeStatus status)
    {
        var id = Guid.NewGuid().ToString("N");
        // Rules are keyed by the configured target, never by the proxy origin.
        var rule = options.Value.Targets.SingleOrDefault(r => ManagedEdgePolicy.MatchesOrigin(r.Origin, status.TargetOrigin!));
        var session = new Session(request, browser, page, rule, probeOrigin, status with { SessionId = id, OriginMatched = true, State = ManagedEdgeState.ConnectedUnproven });
        _sessions[id] = session;
        return session.Status;
    }

    /// <summary>
    /// Approved MCAS proxied delivery requires every signal: user opt-in (trust model), HTTPS access.mcas.ms host with the target's
    /// application identity (already checked by the caller), the live document origin equal to the delivery origin, and navigation history
    /// that ties the tab to the same sign-in flow (configured target, Entra authority, or the Defender sign-in intermediary).
    /// Returns a non-sensitive evidence summary, or null when correlation fails.
    /// </summary>
    private static async Task<string?> CorrelateProxiedDeliveryAsync(IManagedEdgePage page, string targetOrigin, string deliveryOrigin)
    {
        string? live;
        IReadOnlyList<string> history;
        try
        {
            live = await page.GetLocationOriginAsync().WaitAsync(TimeSpan.FromSeconds(6));
            history = await page.GetNavigationOriginsAsync().WaitAsync(TimeSpan.FromSeconds(6));
        }
        catch { return null; }
        if (live is null || !ManagedEdgePolicy.MatchesOrigin(live, deliveryOrigin)) return null;
        var sawTarget = history.Any(h => ManagedEdgePolicy.MatchesOrigin(h, targetOrigin));
        var sawEntra = history.Any(ManagedEdgePolicy.IsEntraAuthorityOrigin);
        var sawIntermediary = history.Any(h => ManagedEdgePolicy.IsMcasIntermediaryOrigin(h, deliveryOrigin));
        if (!sawTarget && !sawEntra && !sawIntermediary) return null;
        var flow = string.Join(", ", new[] { sawTarget ? "configured target" : null, sawEntra ? "Entra authority" : null, sawIntermediary ? "Defender sign-in intermediary" : null }.Where(s => s is not null));
        return $"User opted in; HTTPS access.mcas.ms delivery; application identity {new Uri(targetOrigin).IdnHost.Replace('.', '-')}; live document origin matches; navigation history includes {flow}.";
    }

    private Session Get(ManagedEdgeSessionRequest request)
    {
        if (!_sessions.TryGetValue(request.SessionId, out var session) || session.Request.ProfileId != request.ProfileId || session.Request.ContextFingerprint != request.ContextFingerprint)
            throw new System.Collections.Generic.KeyNotFoundException("Runtime session is no longer current.");
        return session;
    }

    public async Task<ManagedEdgeStatus> StatusAsync(ManagedEdgeSessionRequest request, bool verify = false)
    {
        var session = Get(request);
        await session.Gate.WaitAsync();
        try
        {
            if (!Current(session)) return Stale(session);
            if (!verify) return session.Status;
            var rule = session.Rule;
            bool shell = false, rest = false;
            ManagedEdgeProbeResult? probe = null;
            var bound = session.Status.ProxiedDelivery ? "Approved proxied delivery matched" : "Expected origin matched";
            if (!string.IsNullOrWhiteSpace(rule?.AuthenticatedOnlySelector))
                shell = await session.Page.HasAuthenticatedElementAsync(session.ProbeOrigin, rule.AuthenticatedOnlySelector).WaitAsync(TimeSpan.FromSeconds(6));
            if (!string.IsNullOrWhiteSpace(rule?.ProtectedGetPath))
            {
                ManagedEdgePolicy.SafePath(session.ProbeOrigin, rule.ProtectedGetPath);
                try
                {
                    probe = await session.Page.FetchAsync(session.ProbeOrigin, rule.ProtectedGetPath, null).WaitAsync(TimeSpan.FromSeconds(6));
                    rest = probe.StatusCode == 200 && probe.ContentType == rule.ProtectedContentType;
                }
                catch { /* API failure must not discard independently proven browser shell access. */ }
            }
            if (!Current(session)) return Stale(session);
            session.Status = session.Status with { State = shell || rest ? ManagedEdgeState.ConnectedAuthenticated : ManagedEdgeState.ConnectedUnproven,
                Probe = probe, RestAvailable = rest, GraphQlAvailable = false,
                Evidence = rest ? $"{bound}; administrator-approved protected GET returned the expected status and content type."
                    : shell ? $"{bound}; administrator-approved authenticated-only application element is visible."
                    : $"{bound}, but no configured positive authentication proof succeeded. API/GraphQL token acquisition is not reproduced." };
            session.ProvenAt = DateTimeOffset.UtcNow;
            return session.Status;
        }
        catch (Exception ex)
        {
            // Exception type only: messages may contain URLs or protocol data.
            logger?.LogWarning("Managed Edge verification failed with {ExceptionType}.", ex.GetType().Name);
            session.Status = session.Status with { State = ManagedEdgeState.ConnectedUnproven, RestAvailable = false, GraphQlAvailable = false, Probe = null,
                Evidence = "Safe verification did not succeed. Sign in manually and retry; no authentication material was inspected." };
            return Current(session) ? session.Status : Stale(session);
        }
        finally { session.Gate.Release(); }
    }

    public async Task<ManagedEdgeProbeResult> ExecuteSameOriginFetchAsync(ManagedEdgeFetchRequest request)
    {
        var session = Get(new(request.SessionId, request.ProfileId, request.ContextFingerprint));
        await session.Gate.WaitAsync();
        try
        {
            if (!Current(session) || !session.Status.AuthenticatedBrowserAvailable) throw new InvalidOperationException("Verify authenticated access first.");
            ManagedEdgePolicy.SafePath(session.ProbeOrigin, request.Path);
            if (request.GraphQlQuery is { } query)
            {
                ManagedEdgePolicy.QueryOnly(query);
                if (session.Rule?.GraphQlPath != request.Path || !session.Rule.AllowedGraphQlQueries.Contains(query, StringComparer.Ordinal))
                    throw new ArgumentException("GraphQL query is not administrator-approved.");
            }
            else if (session.Rule is null || !session.Rule.SafeGetPaths.Contains(request.Path, StringComparer.Ordinal))
                throw new ArgumentException("GET path is not administrator-approved.");
            var result = await session.Page.FetchAsync(session.ProbeOrigin, request.Path, request.GraphQlQuery).WaitAsync(TimeSpan.FromSeconds(6));
            if (!Current(session)) { Stale(session); throw new InvalidOperationException("Runtime verification is stale."); }
            if (result.StatusCode is 401 or 403)
                session.Status = session.Status with { State = ManagedEdgeState.ConnectedUnproven, RestAvailable = false, GraphQlAvailable = false,
                    Evidence = "An approved request denied access. Verify the signed-in context again." };
            // A transport 200 alone cannot establish GraphQL semantic success; coverage remains unavailable.
            return result;
        }
        finally { session.Gate.Release(); }
    }

    private static bool Current(Session s) => !s.Disposed && s.Browser.IsConnected && !s.Page.IsClosed && !s.Page.NavigationChanged &&
        ManagedEdgePolicy.MatchesOrigin(s.Page.Url, s.ProbeOrigin) && DateTimeOffset.UtcNow - s.CreatedAt < TimeSpan.FromMinutes(30) &&
        (!s.Status.AuthenticatedBrowserAvailable || DateTimeOffset.UtcNow - s.ProvenAt < TimeSpan.FromMinutes(2));

    private static ManagedEdgeStatus Stale(Session session)
    {
        session.Status = session.Status with { State = ManagedEdgeState.Stale, OriginMatched = false, RestAvailable = false, GraphQlAvailable = false, Probe = null,
            Evidence = "Browser connection, page or verification changed. Reconnect and verify again." };
        if (!session.Disposed) { session.Disposed = true; session.Browser.Dispose(); }
        return session.Status;
    }

    public async Task DisconnectAsync(ManagedEdgeSessionRequest request)
    {
        var session = Get(request);
        await session.Gate.WaitAsync();
        try { Stale(session); _sessions.TryRemove(request.SessionId, out _); }
        finally { session.Gate.Release(); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        while (await timer.WaitForNextTickAsync(stoppingToken))
            foreach (var pair in _sessions)
            {
                await pair.Value.Gate.WaitAsync(stoppingToken);
                try
                {
                    if (!Current(pair.Value)) Stale(pair.Value);
                    if (DateTimeOffset.UtcNow - pair.Value.CreatedAt > TimeSpan.FromMinutes(30)) _sessions.TryRemove(pair.Key, out _);
                }
                finally { pair.Value.Gate.Release(); }
            }
    }

    public override void Dispose()
    {
        foreach (var s in _sessions.Values) if (!s.Disposed) { s.Disposed = true; s.Browser.Dispose(); }
        base.Dispose();
    }

    private sealed class Session(ManagedEdgeConnectRequest request, IManagedEdgeBrowser browser, IManagedEdgePage page, ManagedEdgeTargetRule? rule, string probeOrigin, ManagedEdgeStatus status)
    {
        public ManagedEdgeConnectRequest Request { get; } = request;
        /// <summary>Origin used for same-origin selector and fetch checks: the delivery origin, which equals the target origin unless a proxy was approved.</summary>
        public string ProbeOrigin { get; } = probeOrigin;
        public IManagedEdgeBrowser Browser { get; } = browser;
        public IManagedEdgePage Page { get; } = page;
        public ManagedEdgeTargetRule? Rule { get; } = rule;
        public ManagedEdgeStatus Status { get; set; } = status;
        public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
        public DateTimeOffset ProvenAt { get; set; }
        public bool Disposed { get; set; }
        public SemaphoreSlim Gate { get; } = new(1);
    }
}
