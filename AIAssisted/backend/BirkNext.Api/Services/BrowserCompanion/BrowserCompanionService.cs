using System.Collections.Concurrent;
using System.Security.Cryptography;
using BirkNext.BrowserCompanion;

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
}

/// <summary>
/// Memory-only companion pairing, session and evidence registry for the local workstation. One session per Target Environment
/// (profile id), bound to the extension origin that paired it. Evidence is accepted only when the session is current, belongs to the
/// stated environment, comes from the same extension origin, targets an approved origin and respects the payload limits. Nothing is
/// persisted here: the frontend merges accepted evidence into its per-environment Endpoint Discovery store.
/// </summary>
public sealed class BrowserCompanionService(BrowserCompanionEvidenceSanitizer sanitizer, TimeProvider time, ILogger<BrowserCompanionService> logger)
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
        public string ExtensionVersion { get; set; } = "";
        public string? CurrentPageOrigin { get; set; }
        public string? CurrentPagePath { get; set; }
        public int RejectedMessages { get; set; }
        public Dictionary<string, BrowserPageEvidence> Pages { get; } = new(StringComparer.Ordinal);
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
            session.LastSeenAt = time.GetUtcNow();
            session.ExtensionVersion = Safe(heartbeat.ExtensionVersion, 40);
            // The same canonicalization the approved list was built with, for the same reason as the evidence path:
            // this origin is compared, not displayed, so it must never be put through free-text redaction first.
            var origin = ApplicationPagePolicy.CanonicalOrigin(heartbeat.CurrentPageOrigin);
            if (origin is not null && session.ApprovedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase))
            {
                session.CurrentPageOrigin = origin;
                session.CurrentPagePath = BrowserCompanionEvidenceSanitizer.NormalizePath(heartbeat.CurrentPagePath);
            }
            else
            {
                // The active tab is not an approved page: the companion is connected but idle for this environment.
                session.CurrentPageOrigin = null;
                session.CurrentPagePath = null;
            }
            return new BrowserCompanionAcceptResult { Accepted = true, Message = "OK" };
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
                session.CurrentPageOrigin = page.PageOrigin;
                session.CurrentPagePath = page.PagePath;
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
                }
            }
        }
        catch (OperationCanceledException) { }
    }

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
