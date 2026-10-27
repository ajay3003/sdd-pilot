using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>Where one effective authentication value comes from. Never collapsed: an observed value is not a configured one.</summary>
public enum AuthenticationValueOrigin
{
    /// <summary>Neither an override, current observation nor deterministic source/config evidence establishes the value.</summary>
    Unknown,
    /// <summary>Deterministic source/config evidence for this environment (for example a sign-in authority declared in configuration).</summary>
    DetectedFromConfiguration,
    /// <summary>The current authentication settings check of THIS environment and Frontend URL observed it.</summary>
    Observed,
    /// <summary>A person explicitly set it in Edit Environment. Wins over every other origin.</summary>
    ConfiguredOverride,
}

/// <summary>One effective authentication value, its origin, and — when an override is in effect — what is observed beside it.</summary>
public sealed record EffectiveAuthenticationValue(
    string Setting,
    string Value,
    AuthenticationValueOrigin Origin,
    string? ObservedValue = null)
{
    /// <summary>An override and current observation disagree. A configuration/evidence discrepancy, never a defect by itself.</summary>
    public bool ReviewRequired => Origin == AuthenticationValueOrigin.ConfiguredOverride && ObservedValue is not null
        && !string.Equals(ObservedValue, Value, StringComparison.Ordinal);

    /// <summary>An override that current observation confirms.</summary>
    public bool ObservedMatches => Origin == AuthenticationValueOrigin.ConfiguredOverride && ObservedValue is not null && !ReviewRequired;
}

/// <summary>
/// The authentication settings reviews use for a Target Environment: computed, never persisted. Precedence per value is
/// configured override &gt; observed &gt; detected from source/config &gt; unknown. Observed values come only from the persisted
/// detection snapshot of THIS environment identity (profile id + normalized Frontend URL); a snapshot of another profile or
/// URL, a failed or low-confidence check is never applied. Nothing here is a secret, a sign-in result, a trust decision or an
/// execution authorization: a known provider never authorizes testing.
/// </summary>
public sealed record EffectiveAuthenticationSettings(
    FrontendAuthenticationType Provider,
    bool SignInRequired,
    EffectiveAuthenticationValue ProviderValue,
    EffectiveAuthenticationValue SignInValue,
    FrontendAuthenticationSettings Settings,
    DateTimeOffset? ObservedAt)
{
    public IReadOnlyList<EffectiveAuthenticationValue> Values => [ProviderValue, SignInValue];
    public bool ReviewRequired => Values.Any(v => v.ReviewRequired);
    public bool HasOverride => Values.Any(v => v.Origin == AuthenticationValueOrigin.ConfiguredOverride);
    public bool HasObserved => Values.Any(v => v.Origin == AuthenticationValueOrigin.Observed);
}

public static class EffectiveAuthentication
{
    public const string ProviderKey = "provider";
    public const string SignInRequiredKey = "signInRequired";
    public const string ReviewRequiredText = "Review required — configured value differs from observed evidence";

    /// <summary>Whether a person explicitly configured <paramref name="key"/>. Profiles saved before overrides were tracked
    /// (null list) count a non-default saved value as configured, so nothing a person saved is ever reinterpreted.</summary>
    public static bool IsOverridden(FrontendAuthenticationSettings settings, string key) => settings.ConfiguredOverrides is { } list
        ? list.Contains(key, StringComparer.Ordinal)
        : key switch
        {
            ProviderKey => settings.AuthenticationType != FrontendAuthenticationType.None,
            SignInRequiredKey => settings.RequiresAuthentication,
            _ => false,
        };

    /// <summary>The detection snapshot's result when it is current evidence for <paramref name="profile"/>; otherwise null.</summary>
    public static TargetEnvironmentDetectionResult? CurrentObservation(FrontendAnalysisProfile profile, DetectionSnapshot? snapshot)
    {
        if (snapshot?.Result is not { Success: true } result) return null;
        if (TargetDiscoveryFingerprint.For(snapshot.DetectedProfileId ?? "", snapshot.DetectedUrl).StaleReasonFor(profile) != TargetDiscoveryStaleReason.None) return null;
        if (result.Confidence == DetectionConfidence.Low) return null;
        return result.Reachability is TargetReachability.Reachable or TargetReachability.AuthenticationRequired ? result : null;
    }

    /// <summary>The provider a sign-in authority deterministically identifies; null when the host does not identify one.</summary>
    public static FrontendAuthenticationType? ProviderFromAuthority(string? authority) =>
        Uri.TryCreate(authority, UriKind.Absolute, out var uri)
        && (uri.Host.Equals("login.microsoftonline.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".ciamlogin.com", StringComparison.OrdinalIgnoreCase))
            ? FrontendAuthenticationType.MicrosoftEntraId : null;

    public static EffectiveAuthenticationSettings Resolve(FrontendAnalysisProfile profile, DetectionSnapshot? snapshot, string? sourceAuthority = null)
    {
        var saved = profile.Authentication ?? new FrontendAuthenticationSettings();
        var observed = CurrentObservation(profile, snapshot);
        var observedProvider = observed is { DetectedAuthenticationType: not FrontendAuthenticationType.None } ? observed.DetectedAuthenticationType : (FrontendAuthenticationType?)null;
        var observedSignIn = observed?.AuthenticationRequired;
        var sourceProvider = ProviderFromAuthority(sourceAuthority);

        var (provider, providerOrigin) = IsOverridden(saved, ProviderKey) ? (saved.AuthenticationType, AuthenticationValueOrigin.ConfiguredOverride)
            : observedProvider is { } op ? (op, AuthenticationValueOrigin.Observed)
            : sourceProvider is { } sp ? (sp, AuthenticationValueOrigin.DetectedFromConfiguration)
            : (FrontendAuthenticationType.None, AuthenticationValueOrigin.Unknown);
        var (signIn, signInOrigin) = IsOverridden(saved, SignInRequiredKey) ? (saved.RequiresAuthentication, AuthenticationValueOrigin.ConfiguredOverride)
            : observedSignIn is { } os ? (os, AuthenticationValueOrigin.Observed)
            : (false, AuthenticationValueOrigin.Unknown);

        var providerValue = new EffectiveAuthenticationValue("Provider", providerOrigin == AuthenticationValueOrigin.Unknown ? "Not determined" : AuthenticationPresentation.ProviderLabel(provider), providerOrigin,
            providerOrigin == AuthenticationValueOrigin.ConfiguredOverride && observedProvider is { } o1 ? AuthenticationPresentation.ProviderLabel(o1) : null);
        var signInValue = new EffectiveAuthenticationValue("Sign-in required", signInOrigin == AuthenticationValueOrigin.Unknown ? "Not determined" : YesNo(signIn), signInOrigin,
            signInOrigin == AuthenticationValueOrigin.ConfiguredOverride && observedSignIn is { } o2 ? YesNo(o2) : null);

        // A copy consumers read exactly like the saved settings. Saved identifiers win; empty ones are completed from observation,
        // then from source/config. Policy choices (testing method, verification mode, delivery trust) are only ever the saved ones.
        var effective = new FrontendAuthenticationSettings
        {
            VerificationMode = saved.VerificationMode,
            RequiresAuthentication = signIn,
            AuthenticationType = provider,
            UseExistingBrowserSession = saved.UseExistingBrowserSession,
            AutomaticallyOpenLoginPage = saved.AutomaticallyOpenLoginPage,
            ExpectedAuthority = First(saved.ExpectedAuthority, observed?.DetectedAuthority, sourceAuthority),
            ExpectedTenant = First(saved.ExpectedTenant, observed?.DetectedTenantId),
            ExpectedClientId = First(saved.ExpectedClientId, observed?.DetectedClientId),
            AllowedRedirectUrls = saved.AllowedRedirectUrls.Count > 0 ? [.. saved.AllowedRedirectUrls] : [.. observed?.DetectedRedirectUrls ?? []],
            BrowserDeliveryTrust = saved.BrowserDeliveryTrust,
            AuthenticatedTestingMethod = saved.AuthenticatedTestingMethod,
            ConfiguredOverrides = saved.ConfiguredOverrides is null ? null : [.. saved.ConfiguredOverrides],
        };
        return new(provider, signIn, providerValue, signInValue, effective, observed is null ? null : snapshot!.DetectedAt);
    }

    /// <summary>
    /// Records which values a Save makes explicit. A value the person changed in Edit Environment — typed, or applied from detection,
    /// which is an explicit decision too — becomes a configured override; an unchanged value keeps its status. Nothing observed or
    /// source-derived is copied in. A legacy profile (null list) keeps a null list when the result is what legacy inference already
    /// says, so a save that changes nothing keeps the manual-verification fingerprint.
    /// </summary>
    public static void ApplyEdit(FrontendAuthenticationSettings saved, FrontendAuthenticationSettings draft)
    {
        var overrides = new List<string>();
        if (draft.AuthenticationType != saved.AuthenticationType || IsOverridden(saved, ProviderKey)) overrides.Add(ProviderKey);
        if (draft.RequiresAuthentication != saved.RequiresAuthentication || IsOverridden(saved, SignInRequiredKey)) overrides.Add(SignInRequiredKey);
        draft.ConfiguredOverrides = null;
        var legacyEquivalent = saved.ConfiguredOverrides is null
            && IsOverridden(draft, ProviderKey) == overrides.Contains(ProviderKey)
            && IsOverridden(draft, SignInRequiredKey) == overrides.Contains(SignInRequiredKey);
        draft.ConfiguredOverrides = legacyEquivalent ? null : overrides;
    }

    /// <summary>Removes an override so the value is effective from evidence again (the saved value returns to its default).</summary>
    public static void ClearOverride(FrontendAuthenticationSettings settings, string key)
    {
        var overrides = new List<string>([ProviderKey, SignInRequiredKey]).Where(k => k != key && IsOverridden(settings, k)).ToList();
        if (key == ProviderKey) settings.AuthenticationType = FrontendAuthenticationType.None;
        if (key == SignInRequiredKey) settings.RequiresAuthentication = false;
        settings.ConfiguredOverrides = overrides;
    }

    public static string OriginLabel(AuthenticationValueOrigin origin) => origin switch
    {
        AuthenticationValueOrigin.ConfiguredOverride => "Configured override",
        AuthenticationValueOrigin.Observed => "Observed",
        AuthenticationValueOrigin.DetectedFromConfiguration => "Detected from configuration",
        _ => "Unknown",
    };

    public static string OriginCss(AuthenticationValueOrigin origin) => origin switch
    {
        AuthenticationValueOrigin.ConfiguredOverride => "override",
        AuthenticationValueOrigin.Observed => "observed",
        AuthenticationValueOrigin.DetectedFromConfiguration => "detected",
        _ => "unknown",
    };

    /// <summary>The section status: what the effective settings actually rest on. "Configured" only when an override exists.</summary>
    public static string SourceLabel(EffectiveAuthenticationSettings e) =>
        e.HasOverride ? "Configured override"
        : e.HasObserved ? "Observed configuration"
        : e.Values.Any(v => v.Origin == AuthenticationValueOrigin.DetectedFromConfiguration) ? "Detected configuration"
        : "Not configured";

    private static string YesNo(bool value) => value ? "Yes" : "No";

    private static string? First(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}
