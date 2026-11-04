using BirkNext.Api.Services.LocalHttpsProxy;
using BirkNext.LocalHttpsProxy;
using BirkNext.PerformanceTests;

namespace BirkNext.Api.Services.PerformanceTests;

/// <summary>A leased API credential for exactly one run. Its text form never shows the value (logs, records, exceptions).</summary>
public sealed class LeasedApiCredential(string token, DateTimeOffset expiresAt)
{
    internal string Token { get; } = token;
    public DateTimeOffset ExpiresAt { get; } = expiresAt;
    public override string ToString() => "[leased API credential]";
}

/// <summary>
/// The Target Environment API credential for performance runs: the one the Local HTTPS proxy captured from the person's own browser session
/// (memory only, bound to the environment's approved HTTPS hosts). Checked before a run starts and leased when its container starts.
/// </summary>
internal static class PerformanceApiCredential
{
    /// <summary>Why the scenario cannot run with the captured credential (null = it can, or the scenario does not use it).</summary>
    public static string? Problem(TransientAuthenticatedApiContextStore? store, PerformanceTestDefinition d, string environmentId, AuthenticatedReviewIdentity? identity,
        DateTimeOffset now)
    {
        if (!PerformanceScenarioRuntime.UsesApiCredential(d)) return null;
        if (store is null) return "The Local HTTPS proxy credential store is not available in this BirkNext instance.";
        if (IdentityProblem(environmentId, identity) is { } problem) return problem;
        var descriptor = store.Describe(identity!.ProfileId!, identity.ContextFingerprint!);
        var inScope = descriptor is not null && Uri.TryCreate(d.TargetOrigin, UriKind.Absolute, out var target)
            && ((ITransientCredentialSink)store).ScopeOf(identity.ProfileId!, identity.ContextFingerprint!)?.ContainsUri(target) == true;
        return PerformanceScenarioRuntime.ApiCredentialProblem(descriptor is not null, inScope, descriptor?.ExpiresAt, now, d.Workload.TotalSeconds);
    }

    /// <summary>The credential for this run, or null with the reason (it may have expired or been replaced since the run was accepted).</summary>
    public static (LeasedApiCredential? Credential, string? Problem) Lease(TransientAuthenticatedApiContextStore? store, PerformanceTestDefinition d, string environmentId,
        AuthenticatedReviewIdentity? identity, DateTimeOffset now)
    {
        if (Problem(store, d, environmentId, identity, now) is { } problem) return (null, problem);
        return ((ITransientCredentialSink)store!).TryLease(identity!.ProfileId!, identity.ContextFingerprint!, new Uri(d.TargetOrigin), out var token, out var expiresAt)
            ? (new LeasedApiCredential(token, expiresAt), null)
            : (null, PerformanceScenarioRuntime.ApiCredentialProblem(false, false, null, now, 0));
    }

    /// <summary>Applies the captured credential to one validation request (in-process; the value never leaves the request).</summary>
    public static string? Apply(TransientAuthenticatedApiContextStore? store, string environmentId, AuthenticatedReviewIdentity? identity, HttpRequestMessage request)
    {
        if (store is null) return "The Local HTTPS proxy credential store is not available in this BirkNext instance.";
        if (IdentityProblem(environmentId, identity) is { } problem) return problem;
        return ((ITransientCredentialSink)store).TryApply(identity!.ProfileId!, identity.ContextFingerprint!, request) ? null
            : PerformanceScenarioRuntime.ApiCredentialProblem(store.Describe(identity.ProfileId!, identity.ContextFingerprint!) is not null, false, null, DateTimeOffset.UtcNow, 0);
    }

    /// <summary>The credential belongs to one Target Environment: the request must come from that environment's own proxy identity.</summary>
    private static string? IdentityProblem(string environmentId, AuthenticatedReviewIdentity? identity) =>
        identity is not { ProfileId.Length: > 0, ContextFingerprint.Length: > 0 }
            ? "The Target Environment's proxy identity was not sent with the request: open Performance Test Review from the Target Environment and try again."
            : !string.Equals(identity.ProfileId, environmentId, StringComparison.Ordinal)
                ? "The API credential belongs to another Target Environment than this performance test."
                : null;
}
