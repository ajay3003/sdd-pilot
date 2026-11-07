using BirkNext.GitSources;

namespace BirkNext.Web.Services;

/// <summary>
/// System Settings → Git Sources in words: what is configured, what an administrator must still set up, what the person can do now and why an action
/// is unavailable. Missing configuration is a prerequisite, never a runtime failure; "not tested" is said only when a test is possible.
/// </summary>
public static partial class GitSourcesPresentation
{
    /// <summary>Setup steps not yet in place (Missing/Invalid configuration, set in Microsoft sign-in setup) — setup, not failures.</summary>
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
        if (prerequisites > 0) parts.Add($"{prerequisites} setup step{(prerequisites == 1 ? "" : "s")} not done — see Microsoft sign-in setup");
        if (failures > 0) parts.Add($"{failures} check{(failures == 1 ? "" : "s")} failed");
        return $"Configuration health — {string.Join(" · ", parts)}";
    }

    /// <summary>The Azure DevOps access fact: verified only by the explicit read-only test; "not tested" only when a test is possible.</summary>
    public static string AccessText(GitConnectionStatus status) => status.LastTest is { } t
        ? $"{(t.Ready ? "Verified" : "Not verified")} by the read-only Git test · {t.TestedAt.UtcDateTime:yyyy-MM-dd HH:mm} UTC{(t.RepositoryCount is { } n ? $" · {n} repositor{(n == 1 ? "y" : "ies")} readable" : "")}"
        : status.State switch
        {
            GitConnectionState.NotConfigured => "Not available yet — Microsoft sign-in is not set up on this installation (see Microsoft sign-in setup below)",
            GitConnectionState.Connected => "Not verified yet — a Microsoft sign-in alone does not prove repository access; run Test Git connection",
            GitConnectionState.AccessDenied => "Denied — the signed-in account cannot read this Azure DevOps organization",
            GitConnectionState.SigningIn or GitConnectionState.AwaitingDeviceCode => "Not tested — finish signing in first",
            _ => "Not tested — sign in, then run Test Git connection",
        };

    /// <summary>Why Test Git connection is unavailable (null when it can run).</summary>
    public static string? TestBlockedReason(GitConnectionStatus status) => status.State switch
    {
        GitConnectionState.NotConfigured => "Available once Microsoft sign-in is set up (Microsoft sign-in setup above) and BirkNext has been restarted.",
        _ when string.IsNullOrWhiteSpace(status.OrganizationUrl) && status.Organizations.Count == 0 => "Enter and save the Azure DevOps organization URL first.",
        _ => null,
    };

    /// <summary>Why Sign in is unavailable (null when it can run), naming the first missing prerequisite.</summary>
    public static string? SignInBlockedReason(GitConnectionStatus status) =>
        status.SignInAvailable ? null
        : Prerequisites(status).FirstOrDefault() is { } first ? $"Sign-in needs Microsoft sign-in setup first: {first.Label} — {first.Detail}"
        : "Sign-in is not available on this installation.";

    /// <summary>Checks fixed in Microsoft sign-in setup on this page; the rest (app registration consent, Azure DevOps membership) need an Entra or DevOps administrator.</summary>
    private static readonly HashSet<string> SetUpHere = ["enabled", "client-id", "tenant", "method"];

    /// <summary>Who acts on a check that is not OK: "Set up here" for the sign-in settings on this page, otherwise "Administrator".</summary>
    public static string ActionLabel(GitConfigurationCheck check) => SetUpHere.Contains(check.Key) ? "Set up here" : "Administrator";

    /// <summary>Why the Microsoft sign-in setup cannot be saved as entered (null when it can); the backend validates the same rules.</summary>
    public static string? SignInSetupProblem(bool enabled, string? clientId, string? tenant)
    {
        if (!string.IsNullOrWhiteSpace(clientId) && !Guid.TryParse(clientId.Trim(), out _))
            return "The Application (client) ID is a GUID, for example 00000000-0000-0000-0000-000000000000.";
        if (enabled && string.IsNullOrWhiteSpace(clientId))
            return "Enter the Application (client) ID before turning Microsoft sign-in on.";
        if (!string.IsNullOrWhiteSpace(tenant) && !TenantPattern().IsMatch(tenant.Trim()))
            return "The tenant is a Directory (tenant) ID, a verified domain such as contoso.no, \"organizations\" or \"common\" — or empty for your home tenant.";
        return null;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^(organizations|common|consumers|[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}|[A-Za-z0-9-]+(\.[A-Za-z0-9-]+)+)$")]
    private static partial System.Text.RegularExpressions.Regex TenantPattern();
}
