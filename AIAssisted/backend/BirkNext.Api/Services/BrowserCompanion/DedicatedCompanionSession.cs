using BirkNext.BrowserCompanion;
using BirkNext.LocalHttpsProxy;

namespace BirkNext.Api.Services.BrowserCompanion;

public sealed partial class BrowserCompanionService
{
    /// <summary>
    /// Invoked only after the provisioning service authenticates the local launch bootstrap. One session per Target
    /// Environment, shared by normal and Dedicated Edge:
    /// <list type="bullet">
    /// <item>The same launch asking again gets the same session back, and asking counts as activity. The extension asks
    /// every 30 seconds, so a session parked for want of a permission stays one session instead of idling out and being
    /// replaced by a new identity every three minutes.</item>
    /// <item>A session from another browser that is not heartbeating is stale — alive only by the idle window — and is
    /// superseded. A session that IS heartbeating is live and is never taken over; the user unpairs it explicitly.</item>
    /// </list>
    /// </summary>
    internal BrowserCompanionPairResult PairDedicated(BrowserCompanionPairingStartRequest scope,
        string launchId, string version, string origin, bool permissionPending = false)
    {
        lock (_gate)
        {
            var now = time.GetUtcNow();
            if (_sessionsByProfile.TryGetValue(scope.ProfileId, out var existing) && !existing.Expired(now))
            {
                if (existing.DedicatedLaunchId == launchId)
                {
                    var result = ValidateSession(existing.SessionId, scope.ProfileId, origin);
                    if (result.Accepted)
                    {
                        existing.LastSeenAt = now;
                        existing.AwaitingOriginPermission = permissionPending && !existing.IsConnected(now);
                    }
                    return result;
                }
                if (existing.IsConnected(now))
                    return Reject("Another browser's Companion is live for this Target Environment. Only one Companion session is supported per environment; unpair it in Browser Companion setup to connect Dedicated Edge.");
                _sessionsByProfile.Remove(scope.ProfileId);
                logger.LogInformation("Browser Companion session for environment {ProfileId} superseded by Dedicated Edge: it had no recent heartbeat", scope.ProfileId);
            }
            if (_challengesByProfile.TryGetValue(scope.ProfileId, out var pending) && !pending.Used && pending.ExpiresAt > now)
                return Reject("Another Companion pairing is pending. Complete or cancel it in Browser Companion setup first.");
            var challenge = StartPairing(scope);
            var paired = CompletePairing(new(challenge.PairingCode, version), origin);
            if (paired.Accepted)
            {
                _sessionsByProfile[scope.ProfileId].DedicatedLaunchId = launchId;
                _sessionsByProfile[scope.ProfileId].AwaitingOriginPermission = permissionPending;
            }
            return paired;
        }
    }

    internal void RetireDedicated(string profileId, string launchId)
    {
        lock (_gate)
            if (_sessionsByProfile.TryGetValue(profileId, out var session) && session.DedicatedLaunchId == launchId)
                _sessionsByProfile.Remove(profileId);
    }

    internal DedicatedCompanionReadiness DedicatedReadiness(string profileId, string launchId, string version, string buildId)
    {
        lock (_gate)
        {
            if (!_sessionsByProfile.TryGetValue(profileId, out var session) || session.DedicatedLaunchId != launchId)
                return new();
            var now = time.GetUtcNow();
            // The same rule Browser Discovery reads through Status(); there is no second connection calculation.
            var connected = session.IsConnected(now);
            var compatible = connected && session.ExtensionVersion == version && session.BuildId == buildId;
            var live = LiveSession(session, connected, now);
            var permissionRequired = !connected && session.AwaitingOriginPermission;
            return new()
            {
                Connected = connected, VersionCompatible = compatible, ObservedVersion = session.ExtensionVersion,
                ApprovedPageAvailable = connected && live.LivePages.Count > 0,
                ElementPickAvailable = compatible && live.LivePages.Count == 1 && session.Capabilities.Contains("element-pick"),
                PermissionOrigins = permissionRequired ? session.ApprovedOrigins : [],
                State = connected ? (compatible ? "Connected" : "VersionMismatch") : permissionRequired ? "PermissionRequired" : "AwaitingHeartbeat",
                Message = connected
                    ? compatible ? "Companion heartbeat verified in Dedicated Edge." : "Companion build differs from the running BirkNext build. Restart Dedicated Edge with Browser Companion."
                    : permissionRequired
                        ? $"Companion is paired but has no access to {string.Join(", ", session.ApprovedOrigins)}. In Dedicated Edge, open the Companion popup and choose Allow access; the grant is kept in the dedicated profile."
                        : "Companion loaded; waiting for its first heartbeat."
            };
        }
    }
}
