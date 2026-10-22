using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>Where the read-only "Check authentication settings" stands for the selected Target Environment.</summary>
public enum AuthenticationSettingsCheckState
{
    /// <summary>No check has been run for this target in this session (and none was restored).</summary>
    NotChecked,
    Checking,
    /// <summary>Every detected value agrees with the configuration being compared.</summary>
    Matches,
    /// <summary>At least one difference has no decision yet.</summary>
    ReviewRequired,
    /// <summary>Every difference has a decision and none of them is waiting to be applied; kept saved values stay differences.</summary>
    Reviewed,
    /// <summary>The check ran, but the public sign-in flow exposed nothing to compare. Never reported as a match.</summary>
    NothingDetected,
    /// <summary>The check no longer describes the current target or configuration.</summary>
    RecheckRequired,
    Failed,
    /// <summary>The check cannot run here at all (for example, no Frontend URL).</summary>
    Unavailable,
}

/// <summary>How one setting compares. Only <see cref="SafeChange"/> and <see cref="Conflict"/> are differences.</summary>
public enum AuthenticationDifferenceKind
{
    NoChange,
    /// <summary>Nothing is saved and detection found a value; using it loses nothing.</summary>
    SafeChange,
    /// <summary>Saved and detected disagree. Never applied without an explicit decision.</summary>
    Conflict,
    /// <summary>Saved, but this check found nothing about it. The saved value stays.</summary>
    NotDetected,
    /// <summary>This detection method cannot discover the setting.</summary>
    NotDiscoverable,
    /// <summary>The setting does not apply because no provider is saved or detected.</summary>
    NotApplicable,
}

/// <summary>The reviewer's decision for one difference. Undecided is the default; nothing is pre-selected.</summary>
public enum AuthenticationDifferenceDecision { Undecided, KeepSaved, UseDetected }

/// <summary>One row of the saved-versus-detected comparison, with what the reviewer decided about it.</summary>
public sealed record AuthenticationSettingRow(
    string Setting, string Saved, string Detected, AuthenticationDifferenceKind Kind, AuthenticationDifferenceDecision Decision)
{
    public bool IsDifference => Kind is AuthenticationDifferenceKind.SafeChange or AuthenticationDifferenceKind.Conflict;
    public bool Open => IsDifference && Decision == AuthenticationDifferenceDecision.Undecided;
}

/// <summary>The derived check: one state, the rows behind it, and the counts the card and the panel both read.</summary>
public sealed record AuthenticationSettingsCheckResult(
    AuthenticationSettingsCheckState State,
    IReadOnlyList<AuthenticationSettingRow> Rows,
    string? Reason = null,
    DateTimeOffset? CheckedAt = null,
    bool ComparedWithDraft = false)
{
    public IReadOnlyList<AuthenticationSettingRow> Differences => [.. Rows.Where(r => r.IsDifference)];
    public int OpenDifferences => Rows.Count(r => r.Open);
    public int KeptSaved => Rows.Count(r => r.IsDifference && r.Decision == AuthenticationDifferenceDecision.KeepSaved);
    public int SelectedChanges => Rows.Count(r => r.IsDifference && r.Decision == AuthenticationDifferenceDecision.UseDetected);
    /// <summary>The differences panel exists only while there is something to review; a match never renders it.</summary>
    public bool ShowDifferences => State is AuthenticationSettingsCheckState.ReviewRequired or AuthenticationSettingsCheckState.Reviewed && Differences.Count > 0;
}

/// <summary>
/// The fourth authentication prerequisite: a read-only comparison of what the target publicly exposes against the
/// authentication configuration. It never writes configuration. Detected values reach the draft only through
/// "Apply selected changes", one explicitly selected setting at a time, and the draft is persisted only by Save changes.
///
/// Pure over its inputs so the rules can be tested as rules.
/// </summary>
public static class AuthenticationSettingsChecks
{
    public const string Id = "auth-settings";
    public const string Title = "Authentication settings";
    public const string CheckActionId = "auth-settings-check";
    public const string RedirectSetting = "Allowed Redirect URLs";

    /// <summary>Settings public detection never reports. Listed so the reviewer is not left guessing whether they were compared.</summary>
    public const string NotDiscoverableNote = "Scopes, client secrets and certificates are not discoverable from the public sign-in flow and are never compared or stored.";

    /// <summary>Classifies an existing comparison row. Redirect URLs merge on apply, so adding detected URLs never removes a saved one.</summary>
    public static AuthenticationDifferenceKind Classify(AuthenticationComparisonRow row, bool anyProvider) => row.Status switch
    {
        AuthenticationComparisonStatus.Match => AuthenticationDifferenceKind.NoChange,
        AuthenticationComparisonStatus.NotConfigured => AuthenticationDifferenceKind.SafeChange,
        AuthenticationComparisonStatus.Different when row.Setting == RedirectSetting => AuthenticationDifferenceKind.SafeChange,
        AuthenticationComparisonStatus.Different => AuthenticationDifferenceKind.Conflict,
        AuthenticationComparisonStatus.NotDetected => AuthenticationDifferenceKind.NotDetected,
        _ => anyProvider ? AuthenticationDifferenceKind.NotDiscoverable : AuthenticationDifferenceKind.NotApplicable,
    };

    public static AuthenticationSettingsCheckResult Evaluate(
        bool targetConfigured,
        bool checking,
        TargetEnvironmentDetectionResult? detection,
        string? failure,
        string? recheckReason,
        IReadOnlyList<AuthenticationComparisonRow> comparison,
        IReadOnlyDictionary<string, AuthenticationDifferenceDecision> decisions,
        DateTimeOffset? checkedAt = null,
        bool comparedWithDraft = false)
    {
        if (!targetConfigured)
            return new(AuthenticationSettingsCheckState.Unavailable, [], "Configure a Frontend URL for this environment first.");
        if (checking)
            return new(AuthenticationSettingsCheckState.Checking, [], CheckedAt: checkedAt);
        if (detection is null)
            return failure is { Length: > 0 }
                ? new(AuthenticationSettingsCheckState.Failed, [], failure)
                : new(AuthenticationSettingsCheckState.NotChecked, []);
        if (!detection.Success)
            return new(AuthenticationSettingsCheckState.Failed, [], detection.Message is { Length: > 0 } m ? m : failure ?? "The target could not be checked.", checkedAt);

        var anyProvider = comparison.Any(r => r.Setting == "Identity provider" && r.Status != AuthenticationComparisonStatus.NotAvailable);
        var rows = comparison.Select(r =>
        {
            var kind = Classify(r, anyProvider);
            var decision = kind is AuthenticationDifferenceKind.SafeChange or AuthenticationDifferenceKind.Conflict
                && decisions.TryGetValue(r.Setting, out var d) ? d : AuthenticationDifferenceDecision.Undecided;
            return new AuthenticationSettingRow(r.Setting, r.Configured, r.Detected, kind, decision);
        }).ToList();

        // A stale comparison keeps its rows for reference, but its verdict is "check again", never a match or a review.
        if (recheckReason is { Length: > 0 })
            return new(AuthenticationSettingsCheckState.RecheckRequired, rows, recheckReason, checkedAt, comparedWithDraft);

        if (!rows.Any(r => r.IsDifference || r.Kind == AuthenticationDifferenceKind.NoChange))
            return new(AuthenticationSettingsCheckState.NothingDetected, rows, null, checkedAt, comparedWithDraft);

        var state = rows.Any(r => r.Open || r.Decision == AuthenticationDifferenceDecision.UseDetected && r.IsDifference)
            ? AuthenticationSettingsCheckState.ReviewRequired
            : rows.Any(r => r.IsDifference) ? AuthenticationSettingsCheckState.Reviewed
            : AuthenticationSettingsCheckState.Matches;
        return new(state, rows, null, checkedAt, comparedWithDraft);
    }

    public static string StatusLabel(AuthenticationSettingsCheckResult check) => check.State switch
    {
        AuthenticationSettingsCheckState.Checking => "Checking…",
        AuthenticationSettingsCheckState.Matches => check.ComparedWithDraft ? "Matches draft — not saved yet" : "Matches saved configuration",
        AuthenticationSettingsCheckState.ReviewRequired => $"Review required ({Count(check.Differences.Count, "difference")})",
        AuthenticationSettingsCheckState.Reviewed => $"Reviewed — {check.KeptSaved} kept saved",
        AuthenticationSettingsCheckState.RecheckRequired => "Recheck required",
        AuthenticationSettingsCheckState.NothingDetected => "Nothing detected",
        AuthenticationSettingsCheckState.Failed => "Check failed",
        AuthenticationSettingsCheckState.Unavailable => "Unavailable",
        _ => "Not checked",
    };

    public static AuthPrerequisiteState PrerequisiteState(AuthenticationSettingsCheckState state) => state switch
    {
        AuthenticationSettingsCheckState.Matches or AuthenticationSettingsCheckState.Reviewed => AuthPrerequisiteState.Ok,
        AuthenticationSettingsCheckState.ReviewRequired or AuthenticationSettingsCheckState.RecheckRequired => AuthPrerequisiteState.NeedsAttention,
        AuthenticationSettingsCheckState.Failed => AuthPrerequisiteState.Failed,
        AuthenticationSettingsCheckState.Checking => AuthPrerequisiteState.Checking,
        AuthenticationSettingsCheckState.Unavailable or AuthenticationSettingsCheckState.NothingDetected => AuthPrerequisiteState.Unknown,
        _ => AuthPrerequisiteState.Idle,
    };

    /// <summary>
    /// The card. It sits beside the three proxy prerequisites but is deliberately not one of their inputs: a configuration
    /// difference is a review question, not a reason the proxy, certificate or browser is unavailable.
    /// </summary>
    public static AuthenticationPrerequisite Card(AuthenticationSettingsCheckResult check, string? targetUrl)
    {
        var facts = new List<(string, string)>();
        if (targetUrl is { Length: > 0 } && check.State is not AuthenticationSettingsCheckState.Unavailable) facts.Add(("Target", targetUrl));
        if (check.CheckedAt is { } at) facts.Add(("Last checked", at.ToLocalTime().ToString("d MMM yyyy HH:mm")));
        if (check.State is AuthenticationSettingsCheckState.Matches or AuthenticationSettingsCheckState.ReviewRequired or AuthenticationSettingsCheckState.Reviewed)
            facts.Add(("Compared with", check.ComparedWithDraft ? "Unsaved draft" : "Saved configuration"));

        var (explanation, action) = check.State switch
        {
            AuthenticationSettingsCheckState.Unavailable => (check.Reason ?? "The check cannot run for this environment.", (string?)null),
            AuthenticationSettingsCheckState.Checking => ("Reading the target's public sign-in configuration. Nothing is saved.", null),
            AuthenticationSettingsCheckState.Matches => ("The detected sign-in configuration matches. Nothing needs to be applied.", "Recheck"),
            AuthenticationSettingsCheckState.ReviewRequired => (check.OpenDifferences > 0
                ? $"{Count(check.OpenDifferences, "setting")} differ from the configuration. Review them under Authentication differences; nothing is applied automatically."
                : "Selected changes are ready. Apply them to the draft under Authentication differences.", null),
            AuthenticationSettingsCheckState.Reviewed => ($"Every difference has been reviewed. {Count(check.KeptSaved, "saved value")} kept; kept values remain differences, not matches.", "Recheck"),
            AuthenticationSettingsCheckState.NothingDetected => ("The target's public sign-in flow exposed no authentication settings to compare. Saved values are kept unchanged.", "Recheck"),
            AuthenticationSettingsCheckState.RecheckRequired => (check.Reason ?? "The last check no longer describes this configuration.", "Recheck authentication settings"),
            AuthenticationSettingsCheckState.Failed => (check.Reason ?? "The target could not be checked.", "Check again"),
            _ => ("Compares the target's public sign-in configuration with the saved settings. Read-only; nothing is saved.", "Check authentication settings"),
        };
        return new(Id, Title, StatusLabel(check), PrerequisiteState(check.State), explanation, action, action is null ? null : CheckActionId,
            facts.Count > 0 ? facts : null);
    }

    public static string KindLabel(AuthenticationDifferenceKind kind) => kind switch
    {
        AuthenticationDifferenceKind.NoChange => "No change",
        AuthenticationDifferenceKind.SafeChange => "Safe change available",
        AuthenticationDifferenceKind.Conflict => "Conflict requiring review",
        AuthenticationDifferenceKind.NotDetected => "Not detected",
        AuthenticationDifferenceKind.NotDiscoverable => "Not discoverable",
        _ => "Not applicable",
    };

    public static string KindCss(AuthenticationDifferenceKind kind) => kind switch
    {
        AuthenticationDifferenceKind.NoChange => "match",
        AuthenticationDifferenceKind.SafeChange or AuthenticationDifferenceKind.Conflict => "different",
        _ => "neutral",
    };

    /// <summary>Copies only the settings marked Use detected into <paramref name="target"/>. Returns the settings that were applied.</summary>
    public static IReadOnlyList<string> ApplySelected(
        FrontendAuthenticationSettings target, TargetEnvironmentDetectionResult detection, IEnumerable<string> settings)
    {
        var applied = new List<string>();
        foreach (var setting in settings)
        {
            switch (setting)
            {
                case "Identity provider" when detection.DetectedAuthenticationType != FrontendAuthenticationType.None:
                    target.AuthenticationType = detection.DetectedAuthenticationType; break;
                case "Expected Authority" when !string.IsNullOrWhiteSpace(detection.DetectedAuthority):
                    target.ExpectedAuthority = detection.DetectedAuthority; break;
                // Same rule as the proposal consistency check: the concrete tenant first, the tenant mode otherwise.
                case "Expected Tenant" when (!string.IsNullOrWhiteSpace(detection.DetectedTenantId) ? detection.DetectedTenantId : detection.TenantMode) is { Length: > 0 } tenant:
                    target.ExpectedTenant = tenant; break;
                case "Expected Client ID" when !string.IsNullOrWhiteSpace(detection.DetectedClientId):
                    target.ExpectedClientId = detection.DetectedClientId; break;
                case RedirectSetting when detection.DetectedRedirectUrls.Count > 0:
                    var merged = new List<string>(target.AllowedRedirectUrls);
                    foreach (var url in detection.DetectedRedirectUrls)
                        if (!merged.Contains(url, StringComparer.OrdinalIgnoreCase)) merged.Add(url);
                    target.AllowedRedirectUrls = merged; break;
                default: continue;
            }
            applied.Add(setting);
        }
        return applied;
    }

    private static string Count(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";
}
