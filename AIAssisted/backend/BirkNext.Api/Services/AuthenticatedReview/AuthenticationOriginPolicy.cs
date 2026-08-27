namespace BirkNext.Api.Services.AuthenticatedReview;

internal enum AuthenticationOriginClass
{
    Application,
    EntraAuthority,
    McasIntermediary,
    Unexpected
}

internal sealed class AuthenticationOriginPolicy
{
    private readonly bool _allowSyntheticHttp;
    public AuthenticationOriginPolicy(Microsoft.Extensions.Options.IOptions<AuthenticatedReviewOptions> options) =>
        _allowSyntheticHttp = options.Value.AllowSyntheticHttpOrigins;

    public AuthenticationOriginClass Classify(
        Uri candidate,
        Uri applicationOrigin,
        Uri expectedAuthority,
        bool authenticationActive,
        bool entraObserved,
        Uri? syntheticMcasOrigin)
    {
        if (!IsSafeOrigin(candidate)) return AuthenticationOriginClass.Unexpected;
        if (SameOrigin(candidate, applicationOrigin)) return AuthenticationOriginClass.Application;
        if (IsExpectedEntraNavigation(candidate, expectedAuthority)) return AuthenticationOriginClass.EntraAuthority;
        if (!authenticationActive || !entraObserved) return AuthenticationOriginClass.Unexpected;

        if (_allowSyntheticHttp && syntheticMcasOrigin is not null && SameOrigin(candidate, syntheticMcasOrigin))
            return AuthenticationOriginClass.McasIntermediary;

        return IsTargetCorrelatedMcas(candidate, applicationOrigin)
            ? AuthenticationOriginClass.McasIntermediary
            : AuthenticationOriginClass.Unexpected;
    }

    public bool IsValidEntraAuthority(Uri authority)
    {
        if (!IsSafeOrigin(authority)) return false;
        if (_allowSyntheticHttp && authority.IsLoopback) return true;
        return authority.Scheme == Uri.UriSchemeHttps &&
               string.Equals(authority.IdnHost, "login.microsoftonline.com", StringComparison.OrdinalIgnoreCase);
    }

    private bool IsExpectedEntraNavigation(Uri candidate, Uri expectedAuthority)
    {
        if (!IsValidEntraAuthority(expectedAuthority) || !SameOrigin(candidate, expectedAuthority)) return false;
        var configuredTenant = expectedAuthority.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(configuredTenant)) return true;
        var observedTenant = candidate.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return string.Equals(configuredTenant, observedTenant, StringComparison.OrdinalIgnoreCase);
    }

    public bool IsTargetCorrelatedMcas(Uri candidate, Uri applicationOrigin)
    {
        if (candidate.Scheme != Uri.UriSchemeHttps || !candidate.IdnHost.EndsWith(".access.mcas.ms", StringComparison.OrdinalIgnoreCase)) return false;
        var prefix = candidate.IdnHost[..^".access.mcas.ms".Length].TrimEnd('.');
        var encodedTarget = applicationOrigin.IdnHost.Replace('.', '-');
        return prefix.Equals(encodedTarget, StringComparison.OrdinalIgnoreCase) ||
               prefix.StartsWith(encodedTarget + "-", StringComparison.OrdinalIgnoreCase);
    }

    public static bool SameOrigin(Uri left, Uri right) =>
        string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.IdnHost, right.IdnHost, StringComparison.OrdinalIgnoreCase) &&
        left.Port == right.Port;

    private bool IsSafeOrigin(Uri uri) =>
        string.IsNullOrEmpty(uri.UserInfo) &&
        (uri.Scheme == Uri.UriSchemeHttps || (_allowSyntheticHttp && uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback));
}
