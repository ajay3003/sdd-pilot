using System.Collections.Concurrent;
using System.Security.Cryptography;
using BirkNext.BrowserCompanion;
using BirkNext.CriticalE2E;

namespace BirkNext.Api.Services.BrowserCompanion;

public interface IBrowserCompanionService
{
    /// <summary>UI: begin pairing for one Target Environment. Replaces any pending challenge and invalidates the environment's current session.</summary>
    BrowserCompanionPairingChallenge StartPairing(BrowserCompanionPairingStartRequest request);
    /// <summary>Extension: present the pairing code. Each code is single-use; a replay is rejected.</summary>
    BrowserCompanionPairResult CompletePairing(BrowserCompanionPairRequest request, string extensionOrigin);
    BrowserCompanionAcceptResult Heartbeat(BrowserCompanionHeartbeat heartbeat, string extensionOrigin);
    BrowserCompanionAcceptResult AcceptEvidence(BrowserCompanionEvidenceEnvelope envelope, string extensionOrigin);
    BrowserCompanionStatus Status(string profileId);
    BrowserCompanionStatus Unpair(string profileId);
    /// <summary>Extension: is this session still valid (used on browser start to decide whether the popup shows Connected).</summary>
    BrowserCompanionPairResult ValidateSession(string sessionId, string profileId, string extensionOrigin);
    /// <summary>
    /// BirkNext: tell the companion a Critical E2E run is expected for this environment, so it polls at step speed
    /// rather than at the 30-second liveness cadence. Called when the user opens the run surface, not only when they
    /// press Run — otherwise the first step of every run waits for the next scheduled heartbeat.
    /// </summary>
    void OpenAutomationWindow(string profileId);
    /// <summary>BirkNext: queue one typed browser command for the paired session. Refused unless every gate in <see cref="BrowserCompanionService.Dispatch"/> holds.</summary>
    CompanionCommandDispatchResult Dispatch(CompanionAutomationCommand command);
    /// <summary>BirkNext: wait for the outcome of a queued command, or for its deadline.</summary>
    Task<CompanionAutomationResult> AwaitResultAsync(string commandId, CancellationToken cancellationToken);
    /// <summary>BirkNext: give up on a queued command (user cancelled, or the run ended).</summary>
    void CancelCommand(string commandId, string reason);
    /// <summary>Extension: report the outcome of a command. Idempotent — a replayed result never overwrites the first one.</summary>
    BrowserCompanionAcceptResult CompleteCommand(CompanionAutomationResultEnvelope envelope, string extensionOrigin);
}

/// <summary>
/// Memory-only companion pairing, session and evidence registry for the local workstation. One session per Target Environment
/// (profile id), bound to the extension origin that paired it. Evidence is accepted only when the session is current, belongs to the
/// stated environment, comes from the same extension origin, targets an approved origin and respects the payload limits. Nothing is
/// persisted here: the frontend merges accepted evidence into its per-environment Endpoint Discovery store.
/// </summary>
public sealed partial class BrowserCompanionService(BrowserCompanionEvidenceSanitizer sanitizer, TimeProvider time, ILogger<BrowserCompanionService> logger)
    : BackgroundService, IBrowserCompanionService
{
    private const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // no 0/O/1/I
    private readonly object _gate = new();
    private readonly Dictionary<string, Challenge> _challengesByProfile = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Session> _sessionsByProfile = new(StringComparer.Ordinal);

    private sealed class Challenge
    {
        public required string Code { get; init; }
        public required string ProfileId { get; init; }
        public required string EnvironmentName { get; init; }
        public string? EnvironmentType { get; init; }
        public required IReadOnlyList<string> ApprovedOrigins { get; init; }
        public required DateTimeOffset ExpiresAt { get; init; }
        public bool Used { get; set; }
    }

    private sealed class Session
    {
        public string? EnvironmentType { get; init; }
        public required string SessionId { get; init; }
        public required string ProfileId { get; init; }
        public required string EnvironmentName { get; init; }
        public required IReadOnlyList<string> ApprovedOrigins { get; init; }
        public required string ExtensionOrigin { get; init; }
        public required DateTimeOffset PairedAt { get; init; }
        public DateTimeOffset LastSeenAt { get; set; }
        public DateTimeOffset LastEnvelopeAt { get; set; } = DateTimeOffset.MinValue;
        /// <summary>While this is in the future the companion polls quickly, because a Critical E2E run is expected.</summary>
        public DateTimeOffset AutomationWindowUntil { get; set; } = DateTimeOffset.MinValue;
        public string ExtensionVersion { get; set; } = "";
        /// <summary>Approved pages with a live content script, keyed by PageId. Only the heartbeat writes this.</summary>
        public Dictionary<string, BrowserCompanionLivePage> LivePages { get; } = new(StringComparer.Ordinal);
        public DateTimeOffset? LastContentScriptSeenAt { get; set; }
        /// <summary>Derived from LivePages, never from evidence: the single open page, or null when zero or several.</summary>
        public BrowserCompanionLivePage? CurrentPage => LivePages.Count == 1 ? LivePages.Values.First() : null;
        public string? CurrentPageOrigin => CurrentPage?.Origin;
        public string? CurrentPagePath => CurrentPage?.Route;
        public int RejectedMessages { get; set; }
        public Dictionary<string, BrowserPageEvidence> Pages { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, PendingCommand> Commands { get; } = new(StringComparer.Ordinal);
        public DateTimeOffset AbsoluteExpiry => PairedAt + BrowserCompanionLimits.SessionAbsoluteLifetime;
        public bool Expired(DateTimeOffset now) => now > AbsoluteExpiry || now - LastSeenAt > BrowserCompanionLimits.SessionIdleLifetime;
    }

    public BrowserCompanionPairingChallenge StartPairing(BrowserCompanionPairingStartRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ProfileId)) throw new ArgumentException("ProfileId is required.");
        var origins = NormalizeOrigins(request.ApprovedOrigins);
        if (origins.Count == 0) throw new ArgumentException("At least one approved https/http origin is required.");
        var now = time.GetUtcNow();
        var challenge = new Challenge
        {
            Code = NewCode(), ProfileId = request.ProfileId, EnvironmentName = request.EnvironmentName ?? request.ProfileId,
            EnvironmentType = request.EnvironmentType, ApprovedOrigins = origins, ExpiresAt = now + BrowserCompanionLimits.PairingCodeLifetime,
        };
        lock (_gate)
        {
            _challengesByProfile[request.ProfileId] = challenge;
            // Pairing again means the previous extension session for this environment is no longer trusted.
            _sessionsByProfile.Remove(request.ProfileId);
        }
        logger.LogInformation("Browser Companion pairing started for environment {ProfileId} ({Origins} approved origin(s))", request.ProfileId, origins.Count);
        return new BrowserCompanionPairingChallenge { PairingCode = challenge.Code, ExpiresAt = challenge.ExpiresAt, ProfileId = request.ProfileId };
    }

    public BrowserCompanionPairResult CompletePairing(BrowserCompanionPairRequest request, string extensionOrigin)
    {
        if (!IsExtensionOrigin(extensionOrigin)) return Reject("Pairing is only accepted from a browser extension origin.");
        var code = (request.PairingCode ?? "").Trim().ToUpperInvariant().Replace("-", "");
        var now = time.GetUtcNow();
        lock (_gate)
        {
            var challenge = _challengesByProfile.Values.FirstOrDefault(c => c.Code == code);
            if (challenge is null) { logger.LogWarning("Browser Companion pairing rejected: unknown code"); return Reject("Pairing code not recognised. Generate a new code in BirkNext."); }
            if (challenge.Used) { logger.LogWarning("Browser Companion pairing rejected: code replay"); return Reject("Pairing code already used. Generate a new code in BirkNext."); }
            if (now > challenge.ExpiresAt) { _challengesByProfile.Remove(challenge.ProfileId); return Reject("Pairing code expired. Generate a new code in BirkNext."); }
            challenge.Used = true;
            var session = new Session
            {
                SessionId = NewSessionId(), ProfileId = challenge.ProfileId, EnvironmentName = challenge.EnvironmentName,
                EnvironmentType = challenge.EnvironmentType,
                ApprovedOrigins = challenge.ApprovedOrigins, ExtensionOrigin = extensionOrigin, PairedAt = now, LastSeenAt = now,
                ExtensionVersion = Safe(request.ExtensionVersion, 40),
            };
            _sessionsByProfile[challenge.ProfileId] = session;
            _challengesByProfile.Remove(challenge.ProfileId);
            logger.LogInformation("Browser Companion paired for environment {ProfileId}", challenge.ProfileId);
            return new BrowserCompanionPairResult
            {
                Accepted = true, SessionId = session.SessionId, ProfileId = session.ProfileId, EnvironmentName = session.EnvironmentName,
                ApprovedOrigins = session.ApprovedOrigins, ExpiresAt = session.AbsoluteExpiry, Message = "Paired.", EnvironmentType = session.EnvironmentType,
            };
        }
    }

    public BrowserCompanionPairResult ValidateSession(string sessionId, string profileId, string extensionOrigin)
    {
        lock (_gate)
        {
            var session = Resolve(sessionId, profileId, extensionOrigin, out var reason);
            if (session is null) return Reject(reason);
            return new BrowserCompanionPairResult
            {
                Accepted = true, SessionId = session.SessionId, ProfileId = session.ProfileId, EnvironmentName = session.EnvironmentName,
                ApprovedOrigins = session.ApprovedOrigins, ExpiresAt = session.AbsoluteExpiry, Message = "Session valid.", EnvironmentType = session.EnvironmentType,
            };
        }
    }

    public BrowserCompanionAcceptResult Heartbeat(BrowserCompanionHeartbeat heartbeat, string extensionOrigin)
    {
        lock (_gate)
        {
            var session = Resolve(heartbeat.SessionId, heartbeat.ProfileId, extensionOrigin, out var reason);
            if (session is null) return new BrowserCompanionAcceptResult { Accepted = false, Message = reason };
            var heartbeatAt = time.GetUtcNow();
            session.LastSeenAt = heartbeatAt;
            session.ExtensionVersion = Safe(heartbeat.ExtensionVersion, 40);
            ReconcileLivePages(session, heartbeat, heartbeatAt);
            // A queued command rides back on this response. It is only handed out when a live page is actually there to
            // receive it — a command aimed at a page that is no longer open is a stale click, not a pending one.
            var command = session.CurrentPage is null ? null : ClaimNextCommand(session, heartbeatAt);
            var fast = command is not null || heartbeatAt < session.AutomationWindowUntil;
            return new BrowserCompanionAcceptResult { Accepted = true, Message = "OK", PendingCommand = command, NextHeartbeatMs = fast ? 500 : null };
        }
    }

    public BrowserCompanionAcceptResult AcceptEvidence(BrowserCompanionEvidenceEnvelope envelope, string extensionOrigin)
    {
        lock (_gate)
        {
            var session = Resolve(envelope.SessionId, envelope.ProfileId, extensionOrigin, out var reason);
            if (session is null)
            {
                // Safe outcome trace only: why an envelope was refused, never a page, selector or any evidence value.
                logger.LogWarning("Browser Companion evidence rejected before validation: {Reason}", reason);
                return new BrowserCompanionAcceptResult { Accepted = false, Message = reason };
            }
            var now = time.GetUtcNow();
            if (now - session.LastEnvelopeAt < TimeSpan.FromMilliseconds(BrowserCompanionLimits.MinMillisecondsBetweenEnvelopes))
            {
                session.RejectedMessages++;
                return new BrowserCompanionAcceptResult { Accepted = false, Message = "Rate limited: coalesce evidence before sending." };
            }
            if (envelope.Pages.Count > BrowserCompanionLimits.MaxPagesPerEnvelope)
            {
                session.RejectedMessages++;
                return new BrowserCompanionAcceptResult { Accepted = false, Message = $"Too many pages in one message (max {BrowserCompanionLimits.MaxPagesPerEnvelope})." };
            }
            session.LastEnvelopeAt = now;
            session.LastSeenAt = now;
            session.ExtensionVersion = Safe(envelope.ExtensionVersion, 40);

            var accepted = 0; var rejected = 0;
            foreach (var raw in envelope.Pages)
            {
                var page = sanitizer.Sanitize(raw);
                if (session.EnvironmentType is not ("Local" or "Development" or "QA" or "Test" or "RC") && page.Accessibility is { } passive)
                    page = page with { Accessibility = passive with
                    {
                        Checks = passive.Checks.Where(c => c.CheckId is not ("text-spacing" or "resize-text" or "keyboard-traversal" or "focus-indicator")).ToList(),
                    } };
                if (!ApplicationPagePolicy.IsApplicationOrigin(page.PageOrigin) || page.PageOrigin.Length == 0 || !session.ApprovedOrigins.Contains(page.PageOrigin, StringComparer.OrdinalIgnoreCase)
                    || !string.Equals(page.ProfileId, session.ProfileId, StringComparison.Ordinal) || page.VisitStartedAt == default)
                {
                    rejected++;
                    continue;
                }
                page = page with { ProfileId = session.ProfileId, CapturedAt = page.CapturedAt == default ? now : page.CapturedAt };
                if (!session.Pages.ContainsKey(page.Identity) && session.Pages.Count >= BrowserCompanionLimits.MaxPagesPerEnvironment)
                {
                    var oldest = session.Pages.Values.OrderBy(p => p.CapturedAt).First();
                    session.Pages.Remove(oldest.Identity);
                }
                // Latest snapshot per page wins; an older visit never overwrites a newer one.
                if (session.Pages.TryGetValue(page.Identity, out var existing) && (existing.VisitStartedAt > page.VisitStartedAt ||
                    (existing.VisitStartedAt == page.VisitStartedAt && existing.SnapshotSequence > page.SnapshotSequence)))
                {
                    rejected++;
                    continue;
                }
                session.Pages[page.Identity] = page;
                // Evidence does not move the browser. Where the browser IS comes from live page registration only.
                accepted++;
            }
            session.RejectedMessages += rejected;
            // Counts and the environment only. An envelope whose pages are all refused used to be indistinguishable from one that
            // never arrived: heartbeats kept the session Connected while nothing was ever stored, and no surface said so.
            if (rejected > 0)
                logger.LogWarning("Browser Companion evidence for environment {ProfileId}: {Accepted} page(s) stored, {Rejected} page(s) rejected (origin not approved for this environment, wrong environment, or a stale visit)", session.ProfileId, accepted, rejected);
            else
                logger.LogInformation("Browser Companion evidence for environment {ProfileId}: {Accepted} page(s) stored ({Total} page(s) now have evidence)", session.ProfileId, accepted, session.Pages.Count);
            return new BrowserCompanionAcceptResult { Accepted = accepted > 0 || rejected == 0, AcceptedPages = accepted, RejectedPages = rejected, Message = rejected == 0 ? "OK" : "Some pages were rejected (origin not approved for this environment or stale)." };
        }
    }

    public BrowserCompanionStatus Status(string profileId)
    {
        var now = time.GetUtcNow();
        lock (_gate)
        {
            if (_sessionsByProfile.TryGetValue(profileId, out var session))
            {
                if (session.Expired(now))
                {
                    _sessionsByProfile.Remove(profileId);
                    return new BrowserCompanionStatus { State = BrowserCompanionState.Expired, ProfileId = profileId, EnvironmentName = session.EnvironmentName, Message = "Companion session expired. Pair the browser companion again." };
                }
                var connected = now - session.LastSeenAt <= BrowserCompanionLimits.ConnectedWindow;
                return new BrowserCompanionStatus
                {
                    State = connected ? BrowserCompanionState.Connected : BrowserCompanionState.Disconnected,
                    ProfileId = profileId, EnvironmentName = session.EnvironmentName, PairedAt = session.PairedAt, LastSeenAt = session.LastSeenAt,
                    ExtensionVersion = session.ExtensionVersion, ApprovedOrigins = session.ApprovedOrigins,
                    CurrentPageOrigin = session.CurrentPageOrigin, CurrentPagePath = session.CurrentPagePath,
                    PagesWithEvidence = session.Pages.Count, RejectedMessages = session.RejectedMessages,
                    Pages = session.Pages.Values.OrderByDescending(p => p.CapturedAt).ToList(),
                    Live = LiveSession(session, connected, now),
                    Evidence = EvidenceSummary(session),
                    Message = connected ? "Browser Companion connected." : "Browser Companion paired but not reporting. Open an approved page in the paired browser, or check that the extension is enabled.",
                };
            }
            if (_challengesByProfile.TryGetValue(profileId, out var challenge) && !challenge.Used)
            {
                if (now > challenge.ExpiresAt)
                {
                    _challengesByProfile.Remove(profileId);
                    return new BrowserCompanionStatus { State = BrowserCompanionState.NotPaired, ProfileId = profileId, Message = "Pairing code expired. Generate a new code." };
                }
                return new BrowserCompanionStatus
                {
                    State = BrowserCompanionState.PairingPending, ProfileId = profileId, EnvironmentName = challenge.EnvironmentName,
                    PairingCode = challenge.Code, PairingExpiresAt = challenge.ExpiresAt, ApprovedOrigins = challenge.ApprovedOrigins,
                    Message = "Enter the pairing code in the BirkNext Browser Companion extension.",
                };
            }
            return new BrowserCompanionStatus { State = BrowserCompanionState.NotPaired, ProfileId = profileId, Message = "Browser Companion not paired." };
        }
    }

    public BrowserCompanionStatus Unpair(string profileId)
    {
        lock (_gate)
        {
            _challengesByProfile.Remove(profileId);
            _sessionsByProfile.Remove(profileId);
        }
        logger.LogInformation("Browser Companion unpaired for environment {ProfileId}", profileId);
        return new BrowserCompanionStatus { State = BrowserCompanionState.NotPaired, ProfileId = profileId, Message = "Browser Companion unpaired." };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                var now = time.GetUtcNow();
                lock (_gate)
                {
                    foreach (var key in _challengesByProfile.Where(c => now > c.Value.ExpiresAt || c.Value.Used).Select(c => c.Key).ToList()) _challengesByProfile.Remove(key);
                    foreach (var key in _sessionsByProfile.Where(s => s.Value.Expired(now)).Select(s => s.Key).ToList()) _sessionsByProfile.Remove(key);
                    foreach (var session in _sessionsByProfile.Values) ExpireLivePages(session, now);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// How long a live page survives without the content script saying it is still there. The companion heartbeats
    /// every 30 seconds and faster during a run, so this tolerates three misses — long enough to ride out a suspended
    /// worker, short enough that a browser which was killed stops being reported as having pages open.
    /// </summary>
    public static readonly TimeSpan LivePageLifetime = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Replaces the live page set with what the extension just reported. A page the heartbeat no longer mentions is
    /// gone — a closed tab, a navigation away from an approved origin, or a reload that replaced the instance.
    /// Unapproved origins are dropped here, so an approved-origin check never has to be repeated downstream.
    /// </summary>
    private void ReconcileLivePages(Session session, BrowserCompanionHeartbeat heartbeat, DateTimeOffset now)
    {
        var reported = heartbeat.LivePages ?? [];
        // Compatibility with an extension build that predates live page reporting: its single current page still counts
        // as one live page. It cannot describe two tabs, which is exactly why the newer field exists.
        if (reported.Count == 0 && ApplicationPagePolicy.CanonicalOrigin(heartbeat.CurrentPageOrigin) is { } legacy
            && session.ApprovedOrigins.Contains(legacy, StringComparer.OrdinalIgnoreCase))
            reported = [new BrowserCompanionLivePageReport("legacy", legacy, BrowserCompanionEvidenceSanitizer.NormalizePath(heartbeat.CurrentPagePath), "legacy")];

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var page in reported.Take(BrowserCompanionLimits.MaxLivePages))
        {
            var origin = ApplicationPagePolicy.CanonicalOrigin(page.Origin);
            if (origin is null || !session.ApprovedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase)) continue;
            var pageId = BrowserCompanionEvidenceSanitizer.Identifier(page.PageId);
            var instance = BrowserCompanionEvidenceSanitizer.Identifier(page.ContentScriptInstanceId);
            if (pageId.Length == 0 || instance.Length == 0) continue;
            seen.Add(pageId);
            var route = BrowserCompanionEvidenceSanitizer.NormalizePath(page.Route);
            // A new instance on the same tab is a reload: it replaces the page rather than adding one.
            session.LivePages[pageId] = session.LivePages.TryGetValue(pageId, out var existing) && existing.ContentScriptInstanceId == instance
                ? existing with { Route = route, LastSeenAt = now }
                : new BrowserCompanionLivePage
                {
                    PageId = pageId, Origin = origin, Route = route, ContentScriptInstanceId = instance,
                    RegisteredAt = now, LastSeenAt = now,
                };
        }
        foreach (var gone in session.LivePages.Keys.Where(k => !seen.Contains(k)).ToList()) session.LivePages.Remove(gone);
        if (seen.Count > 0) session.LastContentScriptSeenAt = now;
        ExpireLivePages(session, now);
    }

    private static void ExpireLivePages(Session session, DateTimeOffset now)
    {
        foreach (var stale in session.LivePages.Where(p => now - p.Value.LastSeenAt > LivePageLifetime).Select(p => p.Key).ToList())
            session.LivePages.Remove(stale);
    }

    private static BrowserCompanionLiveSession LiveSession(Session session, bool connected, DateTimeOffset now)
    {
        // A session that is not currently reporting has no live pages, whatever it last said: liveness that outlives
        // its own evidence of life is not liveness.
        if (!connected) return new BrowserCompanionLiveSession { ProfileId = session.ProfileId, ExtensionConnected = false, LastExtensionHeartbeatAt = session.LastSeenAt };
        ExpireLivePages(session, now);
        return new BrowserCompanionLiveSession
        {
            ProfileId = session.ProfileId,
            ExtensionConnected = true,
            LastExtensionHeartbeatAt = session.LastSeenAt,
            LastContentScriptHeartbeatAt = session.LastContentScriptSeenAt,
            LivePages = session.LivePages.Values.OrderBy(p => p.RegisteredAt).ToList(),
        };
    }

    private static BrowserCompanionEvidenceSummary EvidenceSummary(Session session) => new()
    {
        PagesWithEvidence = session.Pages.Count,
        LastEvidenceAt = session.Pages.Count == 0 ? null : session.Pages.Values.Max(p => p.CapturedAt),
        DomEvidencePageCount = session.Pages.Values.Count(p => p.Dom is not null),
        AccessibilityEvidencePageCount = session.Pages.Values.Count(p => p.Accessibility is not null),
        PerformanceEvidencePageCount = session.Pages.Values.Count(p => p.Performance is not null),
        HistoricalRoutes = session.Pages.Values.Select(p => p.PagePath).Distinct(StringComparer.Ordinal).OrderBy(r => r, StringComparer.Ordinal).Take(50).ToList(),
    };

    // ── helpers ──────────────────────────────────────────────────────────────

    private Session? Resolve(string? sessionId, string? profileId, string extensionOrigin, out string reason)
    {
        reason = "";
        if (!IsExtensionOrigin(extensionOrigin)) { reason = "Only a browser extension origin may report evidence."; return null; }
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(profileId)) { reason = "Session and environment are required."; return null; }
        if (!_sessionsByProfile.TryGetValue(profileId, out var session)) { reason = "No companion session for this environment. Pair again in BirkNext."; return null; }
        if (!CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(session.SessionId), System.Text.Encoding.UTF8.GetBytes(sessionId)))
        { session.RejectedMessages++; reason = "Session does not match this environment. Pair again in BirkNext."; return null; }
        if (!string.Equals(session.ExtensionOrigin, extensionOrigin, StringComparison.Ordinal)) { session.RejectedMessages++; reason = "Session was paired by a different extension."; return null; }
        // Expired sessions stay until Status reports them as Expired (or the purge loop removes them), so the UI can say why.
        if (session.Expired(time.GetUtcNow())) { reason = "Companion session expired. Pair again in BirkNext."; return null; }
        return session;
    }

    private static readonly System.Text.RegularExpressions.Regex ExtensionOriginPattern =
        new("^(chrome|moz)-extension://[a-z0-9-]{8,80}$", System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>Only a browser-extension origin (as sent by the extension's own fetch) may pair or report evidence; a web page never can.</summary>
    public static bool IsExtensionOrigin(string? origin) => origin is not null && ExtensionOriginPattern.IsMatch(origin.Trim());

    /// <summary>The approved list is built with the one canonicalization, so an approved origin and a reported origin
    /// are the same string whenever they are the same application.</summary>
    private static IReadOnlyList<string> NormalizeOrigins(IReadOnlyList<string>? origins) =>
        (origins ?? []).Select(ApplicationPagePolicy.CanonicalOrigin)
            .Where(o => o is not null && ApplicationPagePolicy.IsApplicationOrigin(o)).Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).Take(32).ToList();

    private static string NewCode()
    {
        var bytes = RandomNumberGenerator.GetBytes(BrowserCompanionLimits.PairingCodeLength);
        return new string(bytes.Select(b => CodeAlphabet[b % CodeAlphabet.Length]).ToArray());
    }

    private static string NewSessionId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private static BrowserCompanionPairResult Reject(string message) => new() { Accepted = false, Message = message };
    private static string Safe(string? value, int max) => string.IsNullOrWhiteSpace(value) ? "" : new string(value.Where(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_').Take(max).ToArray());
}
