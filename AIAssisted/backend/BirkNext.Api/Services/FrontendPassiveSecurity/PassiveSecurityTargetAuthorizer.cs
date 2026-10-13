using BirkNext.Api.Services.FrontendBrowserRuntime;

namespace BirkNext.Api.Services.FrontendPassiveSecurity;

public sealed class PassiveSecurityTargetAuthorizer(BrowserTargetValidator validator, IConfiguration configuration)
{
    private static readonly HashSet<string> AllowedEnvironmentTypes = new(StringComparer.OrdinalIgnoreCase)
        { "Local", "Development", "QA", "Test", "RC", "Internal", "LoopbackLocalTest" };

    public BrowserTargetValidator.ValidationResult Authorize(PassiveSecurityReviewRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.EnvironmentProfileId)) return new(false, "An explicit configured target-environment profile is required", "Untrusted");
        var section = configuration.GetSection($"FrontendPassiveSecurity:TrustedProfiles:{request.EnvironmentProfileId}");
        var configuredUrl = section["BaseUrl"];
        var environmentType = section["EnvironmentType"];
        if (!Uri.TryCreate(configuredUrl, UriKind.Absolute, out var configured) || string.IsNullOrWhiteSpace(environmentType))
            return new(false, "Target-environment profile is not registered by the server", "Untrusted");
        if (!AllowedEnvironmentTypes.Contains(environmentType))
            return new(false, environmentType.Equals("Production", StringComparison.OrdinalIgnoreCase)
                ? "Passive Security is blocked for Production targets."
                : "Passive Security requires a server-registered non-production environment type.", "Untrusted");
        if (!HasSafeTargetShape(configured) || !Uri.TryCreate(request.TargetUrl, UriKind.Absolute, out var target) || !HasSafeTargetShape(target))
            return new(false, "Passive Security target must be an absolute HTTP(S) URL without credentials or fragment.", "Untrusted");
        if (!string.Equals(target.Host, configured.Host, StringComparison.OrdinalIgnoreCase) || target.Port != configured.Port || target.Scheme != configured.Scheme)
            return new(false, "Target URL does not belong to the configured trusted profile origin", "Untrusted");
        var validation = validator.ValidateTarget(request.TargetUrl, environmentType);
        if (!validation.IsValid && environmentType == "LoopbackLocalTest" &&
            (target.Host is "localhost" or "127.0.0.1" or "::1"))
            return new(true, ClassifiedType: "LoopbackLocalTest");
        return validation;
    }

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
