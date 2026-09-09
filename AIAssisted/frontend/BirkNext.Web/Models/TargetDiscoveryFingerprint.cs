namespace BirkNext.Web.Models;

/// <summary>Why current target discovery evidence no longer applies to the selected profile.</summary>
public enum TargetDiscoveryStaleReason
{
    None,
    /// <summary>The Frontend URL differs from the URL that was detected.</summary>
    TargetUrlChanged,
    /// <summary>The evidence belongs to a different environment profile.</summary>
    ProfileChanged
}

/// <summary>
/// Identity of the target that discovery evidence (reachability, framework, environment,
/// endpoint and integration proposals) was gathered for.
///
/// Deliberately limited to profile identity and normalized Frontend URL. Authentication
/// configuration, thresholds, feature toggles and other profile settings are NOT part of
/// this fingerprint: changing them must never mark target discovery as stale. Those values
/// are covered by <see cref="ManualAuthenticationVerificationEvidence.Fingerprint"/>, which
/// governs manual authentication verification validity only.
/// </summary>
public sealed record TargetDiscoveryFingerprint(string ProfileId, string? NormalizedTargetUrl)
{
    public static TargetDiscoveryFingerprint For(FrontendAnalysisProfile profile) =>
        new(profile.Id, TargetUrlNormalizer.Normalize(profile.TargetUrl));

    public static TargetDiscoveryFingerprint For(string profileId, string? targetUrl) =>
        new(profileId, TargetUrlNormalizer.Normalize(targetUrl));

    public TargetDiscoveryStaleReason StaleReasonFor(FrontendAnalysisProfile current)
    {
        if (!string.Equals(ProfileId, current.Id, StringComparison.Ordinal))
            return TargetDiscoveryStaleReason.ProfileChanged;
        if (!string.Equals(NormalizedTargetUrl, TargetUrlNormalizer.Normalize(current.TargetUrl), StringComparison.OrdinalIgnoreCase))
            return TargetDiscoveryStaleReason.TargetUrlChanged;
        return TargetDiscoveryStaleReason.None;
    }
}

/// <summary>Normalizes Frontend URLs for identity comparison (scheme/host case, default port, fragment, trailing slash).</summary>
public static class TargetUrlNormalizer
{
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri))
            return value?.Trim();

        var builder = new UriBuilder(uri)
        {
            Scheme = uri.Scheme.ToLowerInvariant(),
            Host = uri.Host.ToLowerInvariant(),
            Fragment = ""
        };
        if ((builder.Scheme == Uri.UriSchemeHttps && builder.Port == 443) ||
            (builder.Scheme == Uri.UriSchemeHttp && builder.Port == 80))
            builder.Port = -1;

        return builder.Uri.AbsoluteUri.TrimEnd('/');
    }

    public static bool Match(string? left, string? right) =>
        string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);
}
