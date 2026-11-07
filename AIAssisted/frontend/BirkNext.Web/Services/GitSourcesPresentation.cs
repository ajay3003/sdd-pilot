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

    /// <summary>Why Test Git connection is unavailable (null when it can run): the test reads Azure DevOps as the signed-in person, so it needs
    /// the sign-in settings, a sign-in and an organization. A saved setting is never reported as a verified connection.</summary>
    public static string? TestBlockedReason(GitConnectionStatus status) => status.State switch
    {
        GitConnectionState.NotConfigured => "Available once Microsoft sign-in is set up (steps 1 and 2).",
        GitConnectionState.SigningIn or GitConnectionState.AwaitingDeviceCode => "Finish signing in first.",
        GitConnectionState.NotConnected or GitConnectionState.ReconnectRequired or GitConnectionState.Failed => "Sign in first: the test reads Azure DevOps as you.",
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

    // ── Guided setup ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>What a Microsoft Entra administrator registers once, as text to copy and send. Exact values; no secret is ever needed.</summary>
    public const string RegistrationRequest = """
        Microsoft Entra app registration for BirkNext (Git Sources)
        - Name: any, for example "BirkNext"
        - Supported account types: Accounts in this organizational directory only
        - Platform: Mobile and desktop applications
        - Redirect URI: http://localhost
        - Authentication > Allow public client flows: Yes (only needed for device-code sign-in)
        - API permissions (delegated): Azure DevOps > user_impersonation
          (resource 499b84ac-1321-427f-aa17-267ca6975798)
        - Optional, for Azure Environment Analysis: Azure Service Management > user_impersonation
        - Grant admin consent if users in the tenant may not consent themselves
        - No client secret and no certificate: BirkNext is a public client
        Send back: the Application (client) ID and the Directory (tenant) ID from the Overview page.
        """;

    private static GitConfigurationCheck? Check(GitConnectionStatus status, string key) => status.Diagnostics.FirstOrDefault(d => d.Key == key);

    private static bool IsOk(GitConnectionStatus status, string key) => Check(status, key)?.State == GitCheckState.Ok;

    /// <summary>
    /// Git Sources setup as five guided steps from the backend's own checks: the Entra app registration (outside BirkNext), the sign-in settings
    /// (here), sign-in (the person), Azure DevOps access (the read-only test) and repositories/branches. Each says who acts and what is verified;
    /// at most one is current. Saved settings or a sign-in alone never mark access as verified.
    /// </summary>
    public static IReadOnlyList<GuidedStep> SetupSteps(GitConnectionStatus status, bool restartRequired = false)
    {
        var client = Check(status, "client-id");
        var registrationFailure = status.Diagnostics.FirstOrDefault(d => d.State == GitCheckState.Failed
            && (d.Key is "redirect" or "consent" || d.Key == "sign-in" && d.Detail.Contains("AADSTS700016", StringComparison.Ordinal)));
        var signedIn = status.State is GitConnectionState.Connected or GitConnectionState.AccessDenied;

        var registration = registrationFailure is { } rf
            ? new GuidedStep("registration", "Microsoft app registration", GuidedStepState.Blocked, "Needs an administrator",
                $"Microsoft rejected the sign-in because of the app registration: {rf.Detail}", $"Who acts: a Microsoft Entra administrator · {rf.AdminAction}",
                "Copy details for the administrator", "copy-registration")
            : client?.State is GitCheckState.Ok
                ? new GuidedStep("registration", "Microsoft app registration", GuidedStepState.Done, signedIn ? "Confirmed by sign-in" : "Client ID entered",
                    "BirkNext signs in through your organization's Microsoft Entra app registration.",
                    signedIn ? "Verified: a sign-in with this registration succeeded." : "Not verified yet: Microsoft confirms the registration at the first sign-in (step 3).")
                : new GuidedStep("registration", "Microsoft app registration", GuidedStepState.Current, "Needs an administrator",
                    "BirkNext signs in through a Microsoft Entra app registration that your organization creates once, outside BirkNext. It is not a secret.",
                    "Who acts: a Microsoft Entra administrator, or you if your account may register applications. Send them the details below; they send back the Application (client) ID.",
                    "Copy details for the administrator", "copy-registration");

        var settingsReady = IsOk(status, "enabled") && client?.State == GitCheckState.Ok && IsOk(status, "tenant") && !restartRequired;
        var settingsProblem = status.Diagnostics.FirstOrDefault(d => d.Key is "enabled" or "client-id" or "tenant" && d.State is not GitCheckState.Ok);
        var settings = settingsReady
            ? new GuidedStep("settings", "BirkNext sign-in settings", GuidedStepState.Done, "Saved and in use",
                "This installation uses the saved client ID and tenant.", client?.Detail)
            : new GuidedStep("settings", "BirkNext sign-in settings", registration.State == GuidedStepState.Done ? GuidedStepState.Current : GuidedStepState.Waiting,
                restartRequired ? "Saved — restart needed" : "Not set up",
                "Turn Microsoft sign-in on and enter the Application (client) ID (and the tenant, if it is not your home tenant) in Microsoft sign-in setup on this page.",
                $"Who acts: you, on this page. {(restartRequired ? "The running backend still uses its previous settings." : settingsProblem?.Detail)}".TrimEnd(),
                "Open Microsoft sign-in setup", "open-setup");

        var signInFailure = status.Diagnostics.FirstOrDefault(d => d.Key == "sign-in" && d.State == GitCheckState.Failed);
        var signInAction = status.SignInAvailable ? ("Sign in", "sign-in") : status.DeviceCodeAllowed ? ("Sign in with a device code", "device") : ((string?)null, (string?)null);
        var signIn = signedIn
            ? new GuidedStep("sign-in", "Sign in", GuidedStepState.Done, "Signed in",
                "Signed in with Microsoft Entra ID; Azure DevOps access is your own account's access.", status.Account is null ? null : $"Account: {status.Account}")
            : !settingsReady
                ? new GuidedStep("sign-in", "Sign in", GuidedStepState.Waiting, "Waiting for step 2", "You sign in yourself; MFA and Conditional Access apply.")
                : status.State is GitConnectionState.SigningIn or GitConnectionState.AwaitingDeviceCode
                    ? new GuidedStep("sign-in", "Sign in", GuidedStepState.Current, "Signing in", status.Message)
                    : new GuidedStep("sign-in", "Sign in", GuidedStepState.Current, status.State == GitConnectionState.ReconnectRequired ? "Sign in again" : "Not signed in",
                        "Sign in with your work account. BirkNext keeps the token in memory only and never stores a password or PAT.",
                        signInFailure is { } f ? $"Last attempt: {f.Detail}" : "Who acts: you.",
                        signInAction.Item1, signInAction.Item2,
                        DisabledReason: signInAction.Item2 is null ? SignInBlockedReason(status) : null);

        var hasOrganization = !string.IsNullOrWhiteSpace(status.OrganizationUrl) || status.Organizations.Count > 0;
        var access = status.LastTest is { Ready: true } && status.State == GitConnectionState.Connected
            ? new GuidedStep("access", "Azure DevOps access", GuidedStepState.Done, "Verified",
                "The read-only test reached the organization, its projects and repositories with your account.", AccessText(status))
            : status.State == GitConnectionState.AccessDenied
                ? new GuidedStep("access", "Azure DevOps access", GuidedStepState.Blocked, "Access denied",
                    "Signed in, but Azure DevOps refused this account.",
                    "Who acts: an Azure DevOps organization administrator (Basic or Stakeholder access and read permission on the repositories).")
                : !signedIn
                    ? new GuidedStep("access", "Azure DevOps access", GuidedStepState.Waiting, "Not tested",
                        "Tested after sign-in: a saved organization or a sign-in is not proof of repository access.")
                    : new GuidedStep("access", "Azure DevOps access", GuidedStepState.Current, status.LastTest is { Ready: false } ? "Last test failed" : "Not tested",
                        "Run the read-only test: the organization, its projects, repositories and a branch are read; nothing is written.",
                        status.LastTest is { Ready: false } failed ? failed.Summary : hasOrganization ? "Who acts: you." : "Enter the Azure DevOps organization URL below, or find your organizations.",
                        hasOrganization ? "Test Git connection" : "Find organizations", hasOrganization ? "test" : "find-orgs");

        var repositories = status.TrackedBranches.Count > 0
            ? new GuidedStep("repositories", "Repositories and branches", GuidedStepState.Done,
                $"{status.TrackedBranches.Count} branch{(status.TrackedBranches.Count == 1 ? "" : "es")} tracked",
                "Tracked branches can be imported as projects (read-only).", string.Join(" · ", status.TrackedBranches.Take(3).Select(t => $"{t.Repository} @ {t.Branch}")))
            : access.State == GuidedStepState.Done
                ? new GuidedStep("repositories", "Repositories and branches", GuidedStepState.Current, "Choose a branch",
                    "Load the repositories you can read, choose one and track a branch. BirkNext never writes to a repository.", null, "Load repositories", "load-repos")
                : new GuidedStep("repositories", "Repositories and branches", GuidedStepState.Waiting, "After the access test",
                    "Repository discovery needs verified Azure DevOps access (step 4).");

        return [registration, settings, signIn, access, repositories];
    }

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
