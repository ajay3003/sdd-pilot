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
        GitConnectionState.NotConfigured => "Available once Git Sources is enabled and the Microsoft Entra application is set up (steps 1 and 2).",
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

    // ── Guided setup (six steps) ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>GS-06: the redirect URI of the MSAL public client (MsalAzureTokenBroker.WithRedirectUri). Loopback: MSAL listens on a free local port.</summary>
    public const string RedirectUri = "http://localhost";

    /// <summary>GS-07: the Azure DevOps resource and the scope Git Sources requests (GitSourcePolicy.AzureDevOpsScope).</summary>
    public const string AzureDevOpsResourceId = "499b84ac-1321-427f-aa17-267ca6975798";

    /// <summary>Where System Settings saves the sign-in values (AdminService.SignInSettingsFile), relative to the BirkNext backend folder.</summary>
    public const string SignInSettingsLocation = "App_Data/microsoft-sign-in.json";

    /// <summary>What a Microsoft Entra administrator does once, as text to copy and send. Exact values; no secret is ever needed.</summary>
    public const string RegistrationRequest = """
        Microsoft Entra app registration for BirkNext (Git Sources)
        1. Register an application (or choose an existing one for BirkNext):
           - Supported account types: Accounts in this organizational directory only
        2. Authentication > Add a platform > Mobile and desktop applications:
           - Redirect URI: http://localhost
           - Allow public client flows: Yes (only needed for device-code sign-in)
           - No client secret and no certificate: BirkNext is a public client
        3. API permissions > Add a permission > Azure DevOps (499b84ac-1321-427f-aa17-267ca6975798):
           - Delegated permission: user_impersonation
           - Optional, for Azure Environment Analysis: Azure Service Management > user_impersonation (delegated)
        4. Grant admin consent if users in the tenant may not consent themselves.
        5. Send back: the Application (client) ID and the Directory (tenant) ID from the Overview page.
        6. The person using BirkNext also needs Azure DevOps access to the organization (Basic or Stakeholder)
           and read permission on the repositories to import (an Azure DevOps administrator grants this).
        """;

    private static GitConfigurationCheck? Check(GitConnectionStatus status, string key) => status.Diagnostics.FirstOrDefault(d => d.Key == key);

    private static bool IsOk(GitConnectionStatus status, string key) => Check(status, key)?.State == GitCheckState.Ok;

    private static bool SignedIn(GitConnectionStatus status) => status.State is GitConnectionState.Connected or GitConnectionState.AccessDenied;

    private static GitConfigurationCheck? ConsentFailure(GitConnectionStatus status) =>
        status.Diagnostics.FirstOrDefault(d => d.Key == "consent" && d.State == GitCheckState.Failed);

    private static GitConfigurationCheck? RegistrationFailure(GitConnectionStatus status) =>
        status.Diagnostics.FirstOrDefault(d => d.State == GitCheckState.Failed
            && (d.Key == "redirect" || d.Key == "sign-in" && d.Detail.Contains("AADSTS700016", StringComparison.Ordinal)));

    /// <summary>GS-09: the Microsoft sign-in state in the specification's words.</summary>
    public static string SignInState(GitConnectionStatus status) =>
        ConsentFailure(status) is not null && !SignedIn(status) ? "Consent required"
        : status.State switch
        {
            GitConnectionState.NotConfigured => "Not configured",
            GitConnectionState.NotConnected => "Ready to sign in",
            GitConnectionState.SigningIn or GitConnectionState.AwaitingDeviceCode => "Signing in",
            GitConnectionState.Connected or GitConnectionState.AccessDenied => "Signed in",
            GitConnectionState.ReconnectRequired => "Session expired",
            _ => "Sign-in failed",
        };

    /// <summary>GS-10: the Azure DevOps connection state. Connected only from a current, passed read-only test; never from settings or a sign-in.</summary>
    public static string ConnectionState(GitConnectionStatus status, bool testing = false) =>
        testing ? "Testing"
        : status.State == GitConnectionState.NotConfigured ? "Not configured"
        : status.State == GitConnectionState.AccessDenied ? "Permission denied"
        : !SignedIn(status) ? "Sign-in required"
        : status.LastTest is null ? "Not tested"
        : status.LastTestStale ? "Previous result stale"
        : status.LastTest.Ready ? "Connected"
        : status.LastTest.Steps.Any(s => s.State == GitTestStepState.Failed && s.Name.Contains("organization", StringComparison.OrdinalIgnoreCase)) ? "Organization not accessible"
        : "Failed";

    /// <summary>GS-04: why the client id cannot be saved (null when it can).</summary>
    public static string? ClientIdProblem(bool enabled, string? clientId) =>
        !string.IsNullOrWhiteSpace(clientId) && !Guid.TryParse(clientId.Trim(), out _)
            ? "Enter the Application (Client) ID from your Microsoft Entra app registration: a GUID such as 00000000-0000-0000-0000-000000000000."
            : enabled && string.IsNullOrWhiteSpace(clientId) ? "Enter the Application (Client) ID before enabling Git Sources." : null;

    /// <summary>GS-05: a specific tenant is a Directory (tenant) ID or a verified domain ("organizations" is the other mode).</summary>
    public static string? TenantProblem(bool specific, string? tenant) =>
        !specific ? null
        : string.IsNullOrWhiteSpace(tenant) ? "Enter the Directory (tenant) ID, or choose Organizations."
        : !TenantPattern().IsMatch(tenant.Trim()) || tenant.Trim() is "organizations" or "common" or "consumers"
            ? "A specific tenant is a Directory (tenant) ID such as 00000000-0000-0000-0000-000000000000, or a verified domain such as contoso.no." : null;

    /// <summary>GS-08: why an organization URL is not accepted (null when it is). Only https://dev.azure.com/{org} and https://{org}.visualstudio.com.</summary>
    public static string? OrganizationProblem(string? url) =>
        string.IsNullOrWhiteSpace(url) ? "Enter the Azure DevOps organization URL, for example https://dev.azure.com/your-organization."
        : url.Contains('@') ? "Remove the user name or credentials from the URL."
        : GitSourcePolicy.NormalizeOrganizationUrl(url) is null
            ? "Use https://dev.azure.com/{organization} (or https://{organization}.visualstudio.com): HTTPS, no project or repository path, no other host." : null;

    /// <summary>
    /// The six setup steps from the backend's own checks: provider and activation, the Microsoft Entra application, the Azure DevOps
    /// organization, authentication and permissions, the read-only connection test, browsing and importing. Each says who acts and what is
    /// verified; at most one is current. Saved settings, a sign-in or a saved organization never mark access as verified.
    /// </summary>
    public static IReadOnlyList<GuidedStep> SetupSteps(GitConnectionStatus status, bool restartRequired = false, bool imported = false, bool testing = false)
    {
        var enabled = IsOk(status, "enabled");
        var client = Check(status, "client-id");
        var signedIn = SignedIn(status);
        var provider = enabled
            ? new GuidedStep("provider", "Provider & activation", GuidedStepState.Done, "Enabled", "Azure DevOps, read-only, with Microsoft Entra ID delegated sign-in.")
            : new GuidedStep("provider", "Provider & activation", GuidedStepState.Current, "Setup required",
                "Enable Git Sources for read-only import from Azure DevOps. Enabling it never signs in by itself.", "Who acts: you, on this page.");

        var registrationFailure = RegistrationFailure(status);
        var applicationReady = client?.State == GitCheckState.Ok && IsOk(status, "tenant") && !restartRequired;
        var application = registrationFailure is { } rf
            ? new GuidedStep("application", "Microsoft Entra application", GuidedStepState.Blocked, "Needs an Entra administrator",
                $"Microsoft rejected the sign-in because of the app registration: {rf.Detail}", $"Who acts: a Microsoft Entra administrator · {rf.AdminAction}")
            : applicationReady
                ? new GuidedStep("application", "Microsoft Entra application", GuidedStepState.Done, signedIn ? "Confirmed by sign-in" : "Client ID entered",
                    "BirkNext signs in through your organization's app registration (a public client).",
                    signedIn ? "Verified: a sign-in with this registration succeeded." : "Not verified yet: Microsoft confirms the registration at the first sign-in (step 4).")
                : new GuidedStep("application", "Microsoft Entra application", enabled ? GuidedStepState.Current : GuidedStepState.Waiting,
                    restartRequired ? "Saved — restart required" : enabled ? "Configuration incomplete" : "Client ID missing",
                    "Enter the Application (Client) ID and tenant of the app registration. An Entra administrator creates the registration once; the setup text below says exactly what to register.",
                    restartRequired ? "The running backend still uses its previous settings." : client?.Detail);

        var hasOrganization = !string.IsNullOrWhiteSpace(status.OrganizationUrl) || status.Organizations.Count > 0;
        var organization = hasOrganization
            ? new GuidedStep("organization", "Azure DevOps organization", GuidedStepState.Done, "Saved",
                "The organization BirkNext reads from.", $"{status.OrganizationUrl ?? status.Organizations[0]} · saved, not yet proof of access")
            : new GuidedStep("organization", "Azure DevOps organization", enabled && applicationReady ? GuidedStepState.Current : GuidedStepState.Waiting, "Organization required",
                "Enter the organization URL, for example https://dev.azure.com/your-organization.", "Who acts: you.");

        var consent = ConsentFailure(status);
        var signInReady = enabled && applicationReady;
        var authentication = signedIn
            ? new GuidedStep("authentication", "Authentication & permissions", GuidedStepState.Done, "Signed in",
                "Signed in with Microsoft Entra ID; Azure DevOps access is limited to this account's permissions.", status.Account is null ? null : $"Account: {status.Account}")
            : consent is { } c
                ? new GuidedStep("authentication", "Authentication & permissions", GuidedStepState.Blocked, "Consent required", c.Detail, $"Who acts: a Microsoft Entra administrator · {c.AdminAction}")
                : !signInReady
                    ? new GuidedStep("authentication", "Authentication & permissions", GuidedStepState.Waiting, "Not configured", "You sign in yourself, after steps 1 and 2; MFA and Conditional Access apply.")
                    : new GuidedStep("authentication", "Authentication & permissions", hasOrganization ? GuidedStepState.Current : GuidedStepState.Waiting, SignInState(status),
                        "Sign in as yourself. The token stays in memory only; no password, PAT or token is stored.", status.Message);

        var connection = ConnectionState(status, testing);
        var test = connection == "Connected"
            ? new GuidedStep("test", "Test connection", GuidedStepState.Done, "Connected", "The read-only test reached the organization, its projects and repositories.", AccessText(status))
            : connection == "Permission denied"
                ? new GuidedStep("test", "Test connection", GuidedStepState.Blocked, "Permission denied", "Signed in, but Azure DevOps refused this account.",
                    "Who acts: an Azure DevOps organization administrator (Basic or Stakeholder access and read permission on the repositories).")
                : !signedIn || !hasOrganization
                    ? new GuidedStep("test", "Test connection", GuidedStepState.Waiting, connection, "Tested after sign-in: a saved organization or a sign-in is not proof of repository access.")
                    : new GuidedStep("test", "Test connection", GuidedStepState.Current, connection,
                        "Run the read-only test: organization, projects, repositories and a branch are read; nothing is written.",
                        status.LastTest is { Ready: false } failed && !status.LastTestStale ? failed.Summary : status.LastTestStale ? "The settings changed after the last test." : null);

        var import = imported || status.TrackedBranches.Count > 0
            ? new GuidedStep("import", "Browse and import repositories", GuidedStepState.Done, imported ? "Source imported" : $"{status.TrackedBranches.Count} branch{(status.TrackedBranches.Count == 1 ? "" : "es")} tracked",
                "Import another repository or fetch the latest commit of a tracked branch at any time.")
            : test.State == GuidedStepState.Done
                ? new GuidedStep("import", "Browse and import repositories", GuidedStepState.Current, "Choose a source",
                    "Choose a project, repository and branch, then import it read-only through Import Project.")
                : new GuidedStep("import", "Browse and import repositories", GuidedStepState.Waiting, "After the connection test", "Discovery needs a passed connection test (step 5).");

        return [provider, application, organization, authentication, test, import];
    }

    /// <summary>One line per area for the section header (application health is System Settings' own Config Health).</summary>
    public static string SetupSummary(GitConnectionStatus status) =>
        $"Git Sources: {(IsOk(status, "enabled") ? "Enabled" : "Setup required")} · Entra: {(Check(status, "client-id")?.State == GitCheckState.Ok ? (SignedIn(status) ? "Signed in" : "Client ID set") : "Client ID missing")}"
        + $" · Azure DevOps: {ConnectionState(status)}";

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
