using BirkNext.ApiReview;
using BirkNext.RuntimeSecurity;
using Microsoft.Extensions.Options;

namespace BirkNext.Api.Services.ApiQuality.Security;

/// <summary>One server-registered Target Environment (section <c>SecurityTesting:TrustedTargets:{profileId}</c>).</summary>
public sealed class TrustedSecurityTarget
{
    /// <summary>Local, Development, QA or Test. Anything else (including Production) is never trusted for these checks.</summary>
    public string EnvironmentType { get; set; } = "";
    /// <summary>API origins (scheme://host[:port]) the checks may contact for this profile.</summary>
    public List<string> ApiOrigins { get; set; } = [];
}

public sealed class SecurityTestingOptions
{
    public const string SectionName = "SecurityTesting";
    public Dictionary<string, TrustedSecurityTarget> TrustedTargets { get; set; } = new(StringComparer.Ordinal);
    /// <summary>
    /// When true, the protected security execution endpoints (authorization scenarios, body fuzzing) need an authenticated BirkNext user.
    /// This instance has no user authentication scheme configured, so they then fail closed ("requires Entra authentication configuration").
    /// </summary>
    public bool RequireAuthenticatedUser { get; set; }
}

public interface ITrustedSecurityTargetRegistry
{
    /// <summary>Resolves the profile server-side and checks every destination origin. Client claims are never the decision.</summary>
    TrustedTargetDecision Resolve(string? profileId, IEnumerable<string> destinationUrls);
}

/// <summary>
/// The backend authority for authorization scenarios and request-body fuzzing — the checks that cross the read-only boundary. A profile
/// must be registered by the server with an allowed classification, and each destination must be one of its registered API origins.
/// Production is refused even when registered. Unknown fails closed.
/// </summary>
public sealed class TrustedSecurityTargetRegistry(IOptions<SecurityTestingOptions> options) : ITrustedSecurityTargetRegistry
{
    public TrustedTargetDecision Resolve(string? profileId, IEnumerable<string> destinationUrls)
    {
        if (string.IsNullOrWhiteSpace(profileId) || !options.Value.TrustedTargets.TryGetValue(profileId, out var registered))
            return new TrustedTargetDecision
            {
                State = TrustedTargetState.NotRegistered, ProfileId = profileId ?? "",
                Reason = $"This Target Environment is not registered by the server for authorization scenarios or body fuzzing ({SecurityTestingOptions.SectionName}:TrustedTargets).",
            };
        var origins = registered.ApiOrigins.Select(CorsProbeRules.NormalizeOrigin).Where(o => o is not null).Select(o => o!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var type = registered.EnvironmentType?.Trim();
        TrustedTargetDecision Decide(TrustedTargetState state, string reason) => new() { State = state, ProfileId = profileId, ServerEnvironmentType = type, RegisteredOrigins = origins, Reason = reason };
        if (string.Equals(type, "Production", StringComparison.OrdinalIgnoreCase))
            return Decide(TrustedTargetState.ProductionBlocked, "The server classifies this Target Environment as Production: active role comparison and body fuzzing are never run against Production.");
        if (type is null || !ApiActiveTestingEnvironments.IsAllowed(type))
            return Decide(TrustedTargetState.TypeNotPermitted, $"The server classification '{type ?? "none"}' is not Local, Development, QA or Test.");
        foreach (var url in destinationUrls)
        {
            var origin = CorsProbeRules.NormalizeOrigin(url);
            if (origin is null || !origins.Contains(origin, StringComparer.OrdinalIgnoreCase))
                return Decide(TrustedTargetState.OriginNotRegistered, $"The destination {(origin ?? "(invalid URL)")} is not a registered API origin of this Target Environment; arbitrary URLs are never contacted.");
        }
        return Decide(TrustedTargetState.Trusted, $"Registered by the server as {type} with {origins.Count} API origin(s).");
    }
}
