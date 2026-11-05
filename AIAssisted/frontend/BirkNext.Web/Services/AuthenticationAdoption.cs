using System.Text.Json.Serialization;

using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>What happens to one detected authentication value.</summary>
public enum AuthenticationAdoptionOutcome
{
    /// <summary>Nothing detected for it: the saved value (if any) stays.</summary>
    NotDetected,
    /// <summary>Saved and detected are the same: nothing to show.</summary>
    Equal,
    /// <summary>Nothing saved, the detected value passed the trust policy: populated automatically (with provenance).</summary>
    AutoAdopt,
    /// <summary>Saved and detected differ: the saved value stays and the difference is flagged for review. Never applied automatically.</summary>
    Conflict,
    /// <summary>Detected, but the trust policy cannot establish it is safe (or confidence is too low): kept as evidence, needs review.</summary>
    Suggestion,
    /// <summary>A person rejected this exact detected value before; not asked again until the detected value changes.</summary>
    Rejected,
}

/// <summary>One setting's adoption decision, with the reason a person can read.</summary>
public sealed record AuthenticationAdoptionItem(string Setting, string? Saved, string? Detected, AuthenticationAdoptionOutcome Outcome, string Reason)
{
    public bool NeedsReview => Outcome is AuthenticationAdoptionOutcome.Conflict or AuthenticationAdoptionOutcome.Suggestion;
}

/// <summary>Where a saved authentication value came from (non-secret metadata only; never a credential).</summary>
public sealed class AuthenticationValueProvenance
{
    [JsonPropertyName("setting")] public string Setting { get; set; } = "";
    [JsonPropertyName("value")] public string Value { get; set; } = "";
    /// <summary>"Detected" (adopted after trust validation), "Reviewed" (a person chose a detected value), "Manual" (entered by a person).</summary>
    [JsonPropertyName("source")] public string Source { get; set; } = "";
    [JsonPropertyName("detectedAt")] public DateTimeOffset? DetectedAt { get; set; }
    [JsonPropertyName("validation")] public string Validation { get; set; } = "";
    [JsonPropertyName("recordedAt")] public DateTimeOffset RecordedAt { get; set; }
    /// <summary>The value this one replaced (for "Restore previous"); null when the setting was empty.</summary>
    [JsonPropertyName("previous")] public string? Previous { get; set; }
}

/// <summary>A detected value a person chose not to use: respected until the detected value changes.</summary>
public sealed class AuthenticationRejectedProposal
{
    [JsonPropertyName("setting")] public string Setting { get; set; } = "";
    [JsonPropertyName("detected")] public string Detected { get; set; } = "";
    [JsonPropertyName("rejectedAt")] public DateTimeOffset RejectedAt { get; set; }
}

/// <summary>Adoption history and reviewed decisions of one Target Environment. Kept outside the authentication settings so that recording a
/// decision never changes the fingerprints existing sessions and sign-in checks are bound to.</summary>
public sealed class AuthenticationAdoptionHistory
{
    [JsonPropertyName("provenance")] public List<AuthenticationValueProvenance> Provenance { get; set; } = [];
    [JsonPropertyName("rejected")] public List<AuthenticationRejectedProposal> Rejected { get; set; } = [];

    public AuthenticationValueProvenance? Latest(string setting) => Provenance.LastOrDefault(p => p.Setting == setting);
}

/// <summary>
/// Detect → validate → populate safe missing settings → review exceptions. The trust policy a detected value must pass before it is
/// adopted without a person:
/// <list type="bullet">
/// <item>The detection succeeded for this environment's own Frontend URL (same origin) — never another target's evidence.</item>
/// <item>Confidence is at least High.</item>
/// <item>Authority: https on a Microsoft identity platform host (login.microsoftonline.com and its national clouds, *.ciamlogin.com,
/// *.b2clogin.com); its tenant segment, when it has one, agrees with the detected tenant.</item>
/// <item>Tenant: a concrete tenant (GUID or *.onmicrosoft.com). "common", "organizations" and "consumers" describe multi-tenant
/// sign-in, not this environment's tenant — a suggestion only.</item>
/// <item>Client ID: a GUID.</item>
/// <item>Redirect URLs: only URLs on the target's own origin are adopted; others are suggestions.</item>
/// </list>
/// Only EMPTY settings are populated. A saved value that differs is a conflict (never overwritten); an equal one is not shown; a value
/// a person rejected stays rejected until the detected value changes. Pure, so every rule is testable.
/// </summary>
public static class AuthenticationAdoption
{
    public const string Provider = "Identity provider", Authority = "Expected Authority", Tenant = "Expected Tenant", ClientId = "Expected Client ID",
        RedirectUrls = AuthenticationSettingsChecks.RedirectSetting;

    private static readonly string[] AuthorityHosts = ["login.microsoftonline.com", "login.microsoftonline.us", "login.partner.microsoftonline.cn", "login.microsoftonline.de"];
    private static readonly string[] MultiTenant = ["common", "organizations", "consumers"];

    public static IReadOnlyList<AuthenticationAdoptionItem> Evaluate(TargetEnvironmentDetectionResult? detection, FrontendAuthenticationSettings saved, string? targetUrl,
        AuthenticationAdoptionHistory? history = null)
    {
        if (detection is not { Success: true }) return [];
        var sameTarget = SameOrigin(detection.NormalizedTargetUrl ?? detection.OriginalUrl, targetUrl) || SameOrigin(detection.OriginalUrl, targetUrl);
        var confident = detection.Confidence is DetectionConfidence.High or DetectionConfidence.VeryHigh;
        var tenant = !string.IsNullOrWhiteSpace(detection.DetectedTenantId) ? detection.DetectedTenantId : detection.TenantMode;
        var items = new List<AuthenticationAdoptionItem>();

        string? Trust(string setting, string value) => !sameTarget ? "The detection was not made for this environment's Frontend URL."
            : !confident ? $"Detection confidence is {detection.Confidence}; a person must confirm it."
            : setting switch
            {
                Provider => null,
                Authority => AuthorityProblem(value, detection.DetectedTenantId),
                Tenant => MultiTenant.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase) ? $"\"{value}\" is multi-tenant sign-in, not this environment's tenant."
                    : Guid.TryParse(value, out _) || value.EndsWith(".onmicrosoft.com", StringComparison.OrdinalIgnoreCase) ? null
                    : "Not a recognisable tenant id.",
                ClientId => Guid.TryParse(value, out _) ? null : "Not a recognisable application (client) id.",
                _ => null,
            };

        void Add(string setting, string? savedValue, string? detected)
        {
            if (string.IsNullOrWhiteSpace(detected)) { items.Add(new(setting, savedValue, null, AuthenticationAdoptionOutcome.NotDetected, "Not exposed by the public sign-in flow; the saved value stays.")); return; }
            if (!string.IsNullOrWhiteSpace(savedValue) && string.Equals(savedValue.Trim(), detected.Trim(), StringComparison.OrdinalIgnoreCase))
            { items.Add(new(setting, savedValue, detected, AuthenticationAdoptionOutcome.Equal, "Saved and detected agree.")); return; }
            if (history?.Rejected.Any(r => r.Setting == setting && string.Equals(r.Detected, detected, StringComparison.OrdinalIgnoreCase)) == true)
            { items.Add(new(setting, savedValue, detected, AuthenticationAdoptionOutcome.Rejected, "You chose not to use this detected value; it is not proposed again unless detection finds a different one.")); return; }
            if (!string.IsNullOrWhiteSpace(savedValue))
            { items.Add(new(setting, savedValue, detected, AuthenticationAdoptionOutcome.Conflict, "The saved value differs from what the target exposes. The saved value stays until you decide.")); return; }
            var problem = Trust(setting, detected);
            items.Add(problem is null
                ? new(setting, savedValue, detected, AuthenticationAdoptionOutcome.AutoAdopt, "Nothing was saved and the detected value passed the trust policy.")
                : new(setting, savedValue, detected, AuthenticationAdoptionOutcome.Suggestion, problem));
        }

        // The provider is never copied: an observed provider is already effective without saving (EffectiveAuthentication), and copying it
        // would turn evidence into a configured override. Only a saved provider that differs is a conflict.
        var provider = detection.DetectedAuthenticationType == FrontendAuthenticationType.None ? null : detection.DetectedAuthenticationType.ToString();
        var savedProvider = saved.AuthenticationType == FrontendAuthenticationType.None ? null : saved.AuthenticationType.ToString();
        if (savedProvider is null && provider is not null) items.Add(new(Provider, null, provider, AuthenticationAdoptionOutcome.Equal, "Observed and used directly — no save needed."));
        else Add(Provider, savedProvider, provider);
        Add(Authority, saved.ExpectedAuthority, detection.DetectedAuthority);
        Add(Tenant, saved.ExpectedTenant, tenant);
        Add(ClientId, saved.ExpectedClientId, detection.DetectedClientId);

        // Redirect URLs merge: same-origin ones are safe to add; any other origin needs a person.
        var rejectedUrls = (history?.Rejected.Where(r => r.Setting == RedirectUrls) ?? []).SelectMany(r => r.Detected.Split(", ", StringSplitOptions.RemoveEmptyEntries))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = detection.DetectedRedirectUrls.Where(u => !saved.AllowedRedirectUrls.Contains(u, StringComparer.OrdinalIgnoreCase) && !rejectedUrls.Contains(u)).ToList();
        if (missing.Count > 0)
        {
            var foreign = missing.Where(u => !SameOrigin(u, targetUrl)).ToList();
            var safe = missing.Except(foreign).ToList();
            if (safe.Count > 0)
                items.Add(sameTarget && confident
                    ? new(RedirectUrls, null, string.Join(", ", safe), AuthenticationAdoptionOutcome.AutoAdopt, "Redirect URLs on the target's own origin; existing URLs are kept.")
                    : new(RedirectUrls, null, string.Join(", ", safe), AuthenticationAdoptionOutcome.Suggestion, Trust(RedirectUrls, "") ?? "Needs review."));
            if (foreign.Count > 0)
                items.Add(new(RedirectUrls, null, string.Join(", ", foreign), AuthenticationAdoptionOutcome.Suggestion, "Redirect URLs on another origin are never added automatically."));
        }
        return items;
    }

    /// <summary>Writes the AutoAdopt values into <paramref name="settings"/> and records their provenance. Idempotent: a second run with the
    /// same detection adopts nothing (the values are then Equal). Returns the adopted settings.</summary>
    public static IReadOnlyList<string> Adopt(IReadOnlyList<AuthenticationAdoptionItem> items, TargetEnvironmentDetectionResult detection, FrontendAuthenticationSettings settings,
        AuthenticationAdoptionHistory history, DateTimeOffset now, DateTimeOffset? detectedAt)
    {
        var adopted = new List<string>();
        // Adopted values are evidence-backed, not a person's override. Profiles saved before overrides were tracked treat every saved
        // non-default value as configured; make that list explicit first so what a person saved stays an override and what is adopted
        // here does not become one.
        if (settings.ConfiguredOverrides is null && items.Any(i => i.Outcome == AuthenticationAdoptionOutcome.AutoAdopt))
            settings.ConfiguredOverrides = [.. new[] { EffectiveAuthentication.ProviderKey, EffectiveAuthentication.SignInRequiredKey }
                .Where(key => EffectiveAuthentication.IsOverridden(settings, key))];
        foreach (var item in items.Where(i => i.Outcome == AuthenticationAdoptionOutcome.AutoAdopt))
        {
            if (!Write(item.Setting, item.Detected!, detection, settings)) continue;
            history.Provenance.Add(new() { Setting = item.Setting, Value = item.Detected!, Source = "Detected", DetectedAt = detectedAt, Validation = "Passed trust policy", RecordedAt = now });
            adopted.Add(item.Setting);
        }
        return adopted;
    }

    /// <summary>A person's decision on a conflict or suggestion. Use detected writes the value (keeping the previous one for Restore);
    /// Keep existing records the rejection; Custom writes the person's value as a manual override.</summary>
    public static void Decide(string setting, AuthenticationAdoptionItem item, string decision, string? customValue, TargetEnvironmentDetectionResult detection,
        FrontendAuthenticationSettings settings, AuthenticationAdoptionHistory history, DateTimeOffset now)
    {
        var previous = Current(setting, settings);
        switch (decision)
        {
            case "use" when item.Detected is { } detected:
                Write(setting, detected, detection, settings);
                history.Provenance.Add(new() { Setting = setting, Value = detected, Source = "Reviewed", Validation = item.Outcome == AuthenticationAdoptionOutcome.Suggestion ? $"Accepted by a person: {item.Reason}" : "Accepted by a person", RecordedAt = now, Previous = previous });
                break;
            case "keep" when item.Detected is { } rejected:
                if (setting != RedirectUrls) history.Rejected.RemoveAll(r => r.Setting == setting);
                history.Rejected.Add(new() { Setting = setting, Detected = rejected, RejectedAt = now });
                break;
            case "custom" when customValue is { Length: > 0 } custom && setting != Provider && setting != RedirectUrls:
                SetText(setting, custom.Trim(), settings);
                history.Provenance.Add(new() { Setting = setting, Value = custom.Trim(), Source = "Manual", Validation = "Entered by a person", RecordedAt = now, Previous = previous });
                break;
            case "restore" when history.Provenance.LastOrDefault(p => p.Setting == setting && p.Previous is not null) is { } last && setting != Provider && setting != RedirectUrls:
                SetText(setting, last.Previous, settings);
                history.Provenance.Add(new() { Setting = setting, Value = last.Previous ?? "", Source = "Manual", Validation = "Previous value restored", RecordedAt = now, Previous = previous });
                break;
        }
    }

    public static string? Current(string setting, FrontendAuthenticationSettings s) => setting switch
    {
        Provider => s.AuthenticationType == FrontendAuthenticationType.None ? null : s.AuthenticationType.ToString(),
        Authority => s.ExpectedAuthority,
        Tenant => s.ExpectedTenant,
        ClientId => s.ExpectedClientId,
        RedirectUrls => s.AllowedRedirectUrls.Count == 0 ? null : string.Join(", ", s.AllowedRedirectUrls),
        _ => null,
    };

    /// <summary>"Manual" when a value is saved with no detection provenance (or a person entered it), else its recorded source.</summary>
    public static string OverrideState(string setting, FrontendAuthenticationSettings s, AuthenticationAdoptionHistory? history) =>
        string.IsNullOrWhiteSpace(Current(setting, s)) ? "Not set"
        : history?.Latest(setting) is { } p && string.Equals(p.Value, Current(setting, s), StringComparison.OrdinalIgnoreCase)
            ? p.Source switch { "Detected" => "Detected and validated", "Reviewed" => "Detected, accepted by a person", _ => "Manual override" }
            : "Manual override";

    private static bool Write(string setting, string value, TargetEnvironmentDetectionResult detection, FrontendAuthenticationSettings s)
    {
        switch (setting)
        {
            case Provider when detection.DetectedAuthenticationType != FrontendAuthenticationType.None: s.AuthenticationType = detection.DetectedAuthenticationType; return true;
            case RedirectUrls:
                foreach (var url in value.Split(", ", StringSplitOptions.RemoveEmptyEntries))
                    if (!s.AllowedRedirectUrls.Contains(url, StringComparer.OrdinalIgnoreCase)) s.AllowedRedirectUrls.Add(url);
                return true;
            case Authority or Tenant or ClientId: SetText(setting, value, s); return true;
            default: return false;
        }
    }

    private static void SetText(string setting, string? value, FrontendAuthenticationSettings s)
    {
        switch (setting)
        {
            case Authority: s.ExpectedAuthority = value; break;
            case Tenant: s.ExpectedTenant = value; break;
            case ClientId: s.ExpectedClientId = value; break;
        }
    }

    private static string? AuthorityProblem(string authority, string? detectedTenant)
    {
        if (!Uri.TryCreate(authority, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return "The authority is not an https URL.";
        var host = uri.Host.ToLowerInvariant();
        var known = AuthorityHosts.Contains(host) || host.EndsWith(".ciamlogin.com", StringComparison.Ordinal) || host.EndsWith(".b2clogin.com", StringComparison.Ordinal);
        if (!known) return $"{uri.Host} is not a Microsoft identity platform authority; a person must confirm it.";
        var segment = uri.AbsolutePath.Trim('/').Split('/').FirstOrDefault();
        if (detectedTenant is { Length: > 0 } t && segment is { Length: > 0 } s && !MultiTenant.Contains(s, StringComparer.OrdinalIgnoreCase)
            && !string.Equals(s, t, StringComparison.OrdinalIgnoreCase))
            return $"The authority's tenant ({s}) differs from the detected tenant ({t}).";
        return null;
    }

    private static bool SameOrigin(string? a, string? b) =>
        Uri.TryCreate(a, UriKind.Absolute, out var x) && Uri.TryCreate(b, UriKind.Absolute, out var y)
        && string.Equals(x.Scheme, y.Scheme, StringComparison.OrdinalIgnoreCase) && string.Equals(x.Host, y.Host, StringComparison.OrdinalIgnoreCase) && x.Port == y.Port;
}
