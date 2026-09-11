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
    IOptions<AuthenticatedReviewOptions> runtime) : BackgroundService, IManagedEdgeCdpService
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
            var status = new ManagedEdgeStatus { Endpoint = endpoint, TargetOrigin = origin, ContextCount = browser.ContextCount, PageCount = browser.Pages.Count, DiscoveredTargetTabs = discovered };
            if (matches.Length != 1)
            {
                browser.Dispose();
                // The browser advertises the tab but refused to expose it to the debugger: report that precisely instead of "not found".
                if (matches.Length == 0 && discovered > 0)
                    return status with { State = ManagedEdgeState.TargetTabNotInspectable,
                        Evidence = "The target tab is open, but Edge refused debugger attachment to it. This is expected for a Microsoft Defender for Cloud Apps protected session in a signed-in Edge work profile, where developer tools are turned off. BirkNext does not bypass browser protection, so authenticated access cannot be verified through CDP for this tab." };
                return status with { State = matches.Length == 0 ? ManagedEdgeState.TargetTabNotFound : ManagedEdgeState.AmbiguousTargetTabs,
                    Evidence = matches.Length == 0 ? "Open the target application in Edge, then reconnect." : "Keep exactly one tab for this target origin open, then reconnect." };
            }
            var id = Guid.NewGuid().ToString("N");
            var rule = options.Value.Targets.SingleOrDefault(r => ManagedEdgePolicy.MatchesOrigin(r.Origin, origin));
            var session = new Session(request, browser, matches[0], rule, status with { SessionId = id, OriginMatched = true, State = ManagedEdgeState.ConnectedUnproven,
                Evidence = "Expected origin matched. Authenticated access is not yet proven." });
            _sessions[id] = session;
            return session.Status;
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
            if (!string.IsNullOrWhiteSpace(rule?.AuthenticatedOnlySelector))
                shell = await session.Page.HasAuthenticatedElementAsync(session.Status.TargetOrigin!, rule.AuthenticatedOnlySelector).WaitAsync(TimeSpan.FromSeconds(6));
            if (!string.IsNullOrWhiteSpace(rule?.ProtectedGetPath))
            {
                ManagedEdgePolicy.SafePath(session.Status.TargetOrigin!, rule.ProtectedGetPath);
                try
                {
                    probe = await session.Page.FetchAsync(session.Status.TargetOrigin!, rule.ProtectedGetPath, null).WaitAsync(TimeSpan.FromSeconds(6));
                    rest = probe.StatusCode == 200 && probe.ContentType == rule.ProtectedContentType;
                }
                catch { /* API failure must not discard independently proven browser shell access. */ }
            }
            if (!Current(session)) return Stale(session);
            session.Status = session.Status with { State = shell || rest ? ManagedEdgeState.ConnectedAuthenticated : ManagedEdgeState.ConnectedUnproven,
                Probe = probe, RestAvailable = rest, GraphQlAvailable = false,
                Evidence = rest ? "Expected origin matched; administrator-approved protected GET returned the expected status and content type."
                    : shell ? "Expected origin matched; administrator-approved authenticated-only application element is visible."
                    : "Expected origin matched, but no configured positive authentication proof succeeded. API/GraphQL token acquisition is not reproduced." };
            session.ProvenAt = DateTimeOffset.UtcNow;
            return session.Status;
        }
        catch
        {
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
            ManagedEdgePolicy.SafePath(session.Status.TargetOrigin!, request.Path);
            if (request.GraphQlQuery is { } query)
            {
                ManagedEdgePolicy.QueryOnly(query);
                if (session.Rule?.GraphQlPath != request.Path || !session.Rule.AllowedGraphQlQueries.Contains(query, StringComparer.Ordinal))
                    throw new ArgumentException("GraphQL query is not administrator-approved.");
            }
            else if (session.Rule is null || !session.Rule.SafeGetPaths.Contains(request.Path, StringComparer.Ordinal))
                throw new ArgumentException("GET path is not administrator-approved.");
            var result = await session.Page.FetchAsync(session.Status.TargetOrigin!, request.Path, request.GraphQlQuery).WaitAsync(TimeSpan.FromSeconds(6));
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
        ManagedEdgePolicy.MatchesOrigin(s.Page.Url, s.Status.TargetOrigin!) && DateTimeOffset.UtcNow - s.CreatedAt < TimeSpan.FromMinutes(30) &&
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

    private sealed class Session(ManagedEdgeConnectRequest request, IManagedEdgeBrowser browser, IManagedEdgePage page, ManagedEdgeTargetRule? rule, ManagedEdgeStatus status)
    {
        public ManagedEdgeConnectRequest Request { get; } = request;
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
