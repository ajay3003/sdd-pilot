using BirkNext.Api.Services.FrontendBrowserRuntime;

namespace BirkNext.Api.Services.FrontendPassiveSecurity;

/// <summary>Why a target is (not) trusted for Passive Web Security. Distinct states, never one generic "unavailable".</summary>
public enum PassiveWebSecurityTrustState
{
    Trusted,
    /// <summary>No server registration for this Target Environment (FrontendPassiveSecurity:TrustedProfiles:{profileId}).</summary>
    NotRegistered,
    /// <summary>Registered, but the target origin is not the registered origin (scheme, host and port must all match).</summary>
    OriginNotTrusted,
    ProductionBlocked,
    /// <summary>Registered with an environment type that is not an allowed non-production type.</summary>
    TypeNotPermitted,
    /// <summary>The registered or requested URL is not an absolute HTTP(S) URL without credentials or fragment.</summary>
    InvalidTarget,
}

/// <summary>The trust decision for one target, with the origins it was decided on. Contains no secret.</summary>
public sealed record PassiveWebSecurityTrust(PassiveWebSecurityTrustState State, string Reason, string? RegisteredOrigin = null, string? TargetOrigin = null)
{
    public bool IsTrusted => State == PassiveWebSecurityTrustState.Trusted;
}

/// <summary>
/// The single trust source for Passive Web Security (OWASP ZAP): a server-registered Target Environment profile, exactly one origin, a
/// non-production type. Engine status and the scan itself both ask this class; nothing else decides ZAP trust. Trust is per origin:
/// a registered origin never extends to sibling hosts, subdomains or other ports.
/// </summary>
public sealed class PassiveSecurityTargetAuthorizer(BrowserTargetValidator validator, IConfiguration configuration)
{
    private static readonly HashSet<string> AllowedEnvironmentTypes = new(StringComparer.OrdinalIgnoreCase)
        { "Local", "Development", "QA", "Test", "RC", "Internal", "LoopbackLocalTest" };

    /// <summary>Registration, environment type and origin, as a typed state. <see cref="Authorize"/> adds the target validator on top.</summary>
    public PassiveWebSecurityTrust Trust(PassiveSecurityReviewRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.EnvironmentProfileId))
            return new(PassiveWebSecurityTrustState.NotRegistered, "An explicit configured target-environment profile is required");
        var section = configuration.GetSection($"FrontendPassiveSecurity:TrustedProfiles:{request.EnvironmentProfileId}");
        var configuredUrl = section["BaseUrl"];
        var environmentType = section["EnvironmentType"];
        var targetOrigin = Uri.TryCreate(request.TargetUrl, UriKind.Absolute, out var parsedTarget) ? Origin(parsedTarget) : null;
        if (!Uri.TryCreate(configuredUrl, UriKind.Absolute, out var configured) || string.IsNullOrWhiteSpace(environmentType))
            return new(PassiveWebSecurityTrustState.NotRegistered, "Target-environment profile is not registered by the server", TargetOrigin: targetOrigin);
        var registeredOrigin = Origin(configured);
        if (!AllowedEnvironmentTypes.Contains(environmentType))
            return environmentType.Equals("Production", StringComparison.OrdinalIgnoreCase)
                ? new(PassiveWebSecurityTrustState.ProductionBlocked, "Passive Web Security is blocked for Production targets.", registeredOrigin, targetOrigin)
                : new(PassiveWebSecurityTrustState.TypeNotPermitted, "Passive Web Security requires a server-registered non-production environment type.", registeredOrigin, targetOrigin);
        if (!HasSafeTargetShape(configured) || parsedTarget is null || !HasSafeTargetShape(parsedTarget))
            return new(PassiveWebSecurityTrustState.InvalidTarget, "Passive Web Security target must be an absolute HTTP(S) URL without credentials or fragment.", registeredOrigin, targetOrigin);
        if (!string.Equals(parsedTarget.Host, configured.Host, StringComparison.OrdinalIgnoreCase) || parsedTarget.Port != configured.Port || parsedTarget.Scheme != configured.Scheme)
            return new(PassiveWebSecurityTrustState.OriginNotTrusted, "Target URL does not belong to the configured trusted profile origin", registeredOrigin, targetOrigin);
        return new(PassiveWebSecurityTrustState.Trusted, $"Registered by the server for {registeredOrigin} ({environmentType}).", registeredOrigin, targetOrigin);
    }

    public BrowserTargetValidator.ValidationResult Authorize(PassiveSecurityReviewRequest request)
    {
        var trust = Trust(request);
        if (!trust.IsTrusted) return new(false, trust.Reason, "Untrusted");
        var environmentType = configuration[$"FrontendPassiveSecurity:TrustedProfiles:{request.EnvironmentProfileId}:EnvironmentType"]!;
        var validation = validator.ValidateTarget(request.TargetUrl, environmentType);
        if (!validation.IsValid && environmentType == "LoopbackLocalTest" &&
            Uri.TryCreate(request.TargetUrl, UriKind.Absolute, out var target) && (target.Host is "localhost" or "127.0.0.1" or "::1"))
            return new(true, ClassifiedType: "LoopbackLocalTest");
        return validation;
    }

    private static string Origin(Uri uri) => uri.GetLeftPart(UriPartial.Authority);

    private static bool HasSafeTargetShape(Uri uri) =>
        uri.Scheme is "http" or "https" && !string.IsNullOrEmpty(uri.Host) &&
        string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Fragment);

    public BrowserTargetValidator.ValidationResult AuthorizeRedirect(PassiveSecurityReviewRequest request, string redirectUrl)
    {
        var initial = Authorize(request);
        if (!initial.IsValid) return initial;
        var section = configuration.GetSection($"FrontendPassiveSecurity:TrustedProfiles:{request.EnvironmentProfileId}");
        var configuredUrl = section["BaseUrl"];
        var environmentType = section["EnvironmentType"];
        if (!Uri.TryCreate(configuredUrl, UriKind.Absolute, out var configured) || !Uri.TryCreate(redirectUrl, UriKind.Absolute, out var redirect))
            return new(false, "Invalid redirect URL", "Untrusted");
        if (redirect.Scheme != configured.Scheme || !string.Equals(redirect.Host, configured.Host, StringComparison.OrdinalIgnoreCase) || redirect.Port != configured.Port)
            return new(false, "Redirect leaves the configured trusted profile origin", "Untrusted");
        if (environmentType == "LoopbackLocalTest" && redirect.Host is "localhost" or "127.0.0.1" or "::1")
            return new(true, ClassifiedType: "LoopbackLocalTest");
        return validator.ValidateRedirectTarget(redirectUrl, configured.Host, environmentType);
    }
}
