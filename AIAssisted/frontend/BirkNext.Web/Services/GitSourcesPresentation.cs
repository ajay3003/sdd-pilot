using BirkNext.GitSources;

namespace BirkNext.Web.Services;

/// <summary>
/// System Settings → Git Sources in words: what is configured, what an administrator must still set up, what the person can do now and why an action
/// is unavailable. Missing configuration is a prerequisite, never a runtime failure; "not tested" is said only when a test is possible.
/// </summary>
public static class GitSourcesPresentation
{
    /// <summary>Administrator prerequisites not yet in place (Missing/Invalid configuration) — set up, not failures.</summary>
    public static IReadOnlyList<GitConfigurationCheck> Prerequisites(GitConnectionStatus status) =>
        [.. status.Diagnostics.Where(d => d.State is GitCheckState.Missing or GitCheckState.Invalid)];

    /// <summary>Checks that ran and failed (for example a token request or an Azure DevOps call).</summary>
    public static IReadOnlyList<GitConfigurationCheck> Failures(GitConnectionStatus status) =>
        [.. status.Diagnostics.Where(d => d.State == GitCheckState.Failed)];

    public static string HealthSummary(GitConnectionStatus status)
    {
        var prerequisites = Prerequisites(status).Count;
        var failures = Failures(status).Count;
        if (prerequisites == 0 && failures == 0) return "Configuration health — all prerequisites in place";
        var parts = new List<string>();
        if (prerequisites > 0) parts.Add($"{prerequisites} administrator prerequisite{(prerequisites == 1 ? "" : "s")} not set up");
        if (failures > 0) parts.Add($"{failures} check{(failures == 1 ? "" : "s")} failed");
        return $"Configuration health — {string.Join(" · ", parts)}";
    }

    /// <summary>The Azure DevOps access fact: verified only by the explicit read-only test; "not tested" only when a test is possible.</summary>
    public static string AccessText(GitConnectionStatus status) => status.LastTest is { } t
        ? $"{(t.Ready ? "Verified" : "Not verified")} by the read-only Git test · {t.TestedAt.UtcDateTime:yyyy-MM-dd HH:mm} UTC{(t.RepositoryCount is { } n ? $" · {n} repositor{(n == 1 ? "y" : "ies")} readable" : "")}"
        : status.State switch
        {
            GitConnectionState.NotConfigured => "Not available yet — Git Sources is not set up on this installation (an administrator step, see below)",
            GitConnectionState.Connected => "Not verified yet — a Microsoft sign-in alone does not prove repository access; run Test Git connection",
            GitConnectionState.AccessDenied => "Denied — the signed-in account cannot read this Azure DevOps organization",
            GitConnectionState.SigningIn or GitConnectionState.AwaitingDeviceCode => "Not tested — finish signing in first",
            _ => "Not tested — sign in, then run Test Git connection",
        };

    /// <summary>Why Test Git connection is unavailable (null when it can run).</summary>
    public static string? TestBlockedReason(GitConnectionStatus status) => status.State switch
    {
        GitConnectionState.NotConfigured => "Available once an administrator has set up Git Sources (see Configuration health).",
        _ when string.IsNullOrWhiteSpace(status.OrganizationUrl) && status.Organizations.Count == 0 => "Enter and save the Azure DevOps organization URL first.",
        _ => null,
    };

    /// <summary>Why Sign in is unavailable (null when it can run), naming the first missing prerequisite.</summary>
    public static string? SignInBlockedReason(GitConnectionStatus status) =>
        status.SignInAvailable ? null
        : Prerequisites(status).FirstOrDefault() is { } first ? $"Sign-in needs an administrator first: {first.Label} — {first.Detail}"
        : "Sign-in is not available on this installation.";
}
