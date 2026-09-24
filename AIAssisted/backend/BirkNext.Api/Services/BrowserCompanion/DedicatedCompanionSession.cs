using BirkNext.BrowserCompanion;
using BirkNext.LocalHttpsProxy;

namespace BirkNext.Api.Services.BrowserCompanion;

public sealed partial class BrowserCompanionService
{
    // Invoked only after the provisioning service authenticates the local launch bootstrap.
    // Keep the existing one-session rule; opening a proxy browser cannot revoke a normal browser.
    internal BrowserCompanionPairResult PairDedicated(BrowserCompanionPairingStartRequest scope,
        string launchId, string version, string origin)
    {
        lock (_gate)
        {
            if (_sessionsByProfile.TryGetValue(scope.ProfileId, out var existing) && !existing.Expired(time.GetUtcNow()))
                return existing.DedicatedLaunchId == launchId
                    ? ValidateSession(existing.SessionId, scope.ProfileId, origin)
                    : Reject("Another Companion session is paired with this Target Environment. Unpair it explicitly in Browser Companion setup before connecting Dedicated Edge.");
            if (_challengesByProfile.TryGetValue(scope.ProfileId, out var pending) && !pending.Used && pending.ExpiresAt > time.GetUtcNow())
                return Reject("Another Companion pairing is pending. Complete or cancel it in Browser Companion setup first.");
            var challenge = StartPairing(scope);
            var result = CompletePairing(new(challenge.PairingCode, version), origin);
            if (result.Accepted) _sessionsByProfile[scope.ProfileId].DedicatedLaunchId = launchId;
            return result;
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
            var connected = !session.Expired(time.GetUtcNow()) && session.LastHeartbeatAt is { } beat &&
                time.GetUtcNow() - beat <= BrowserCompanionLimits.ConnectedWindow;
            var compatible = connected && session.ExtensionVersion == version && session.BuildId == buildId;
            var live = LiveSession(session, connected, time.GetUtcNow());
            return new()
            {
                Connected = connected, VersionCompatible = compatible, ObservedVersion = session.ExtensionVersion,
                ApprovedPageAvailable = connected && live.LivePages.Count > 0,
                ElementPickAvailable = compatible && live.LivePages.Count == 1 && session.Capabilities.Contains("element-pick"),
                State = !connected ? "AwaitingHeartbeat" : !compatible ? "VersionMismatch" : "Connected",
                Message = !connected ? "Companion loaded; waiting for a heartbeat. Open its popup to grant access to the approved application origin."
                    : !compatible ? "Companion build differs from the running BirkNext build. Restart Dedicated Edge with Browser Companion."
                    : "Companion heartbeat verified in Dedicated Edge."
            };
        }
    }
}
