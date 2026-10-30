using System.Security.Cryptography;
using System.Text;
using BirkNext.BrowserAuthentication;
using Microsoft.Playwright;

namespace BirkNext.Api.Services.BrowserAuthentication;

public sealed record UnattendedLoginSaveOutcome(UnattendedLoginView? View, List<string> Errors);

/// <summary>The result a consumer receives from an unattended browser run: the login outcome, and the consumer's own result when it ran.</summary>
public sealed record UnattendedBrowserRun<T>(UnattendedLoginOutcome Outcome, string Message, T? Result);

/// <summary>
/// The unattended browser login capability: configuration, readiness, explicit validation and the consumer entry point. Completely
/// separate from the attended flows — it neither reads nor writes any attended session, profile, Target Environment setting or Test
/// Data value, and nothing here changes what attended login does or defaults to.
/// <para>State: Not configured → (saved) Not verified → (validated with the current configuration) Ready / Validation failed.
/// Disabled keeps the configuration. Changing the identity, credential, login target or success detection changes the configuration
/// fingerprint, so an earlier validation stops counting and the state returns to Not verified.</para>
/// <para>Validation and consumer runs happen only on an explicit request; reading the settings never starts a browser or a login.</para>
/// </summary>
public sealed class UnattendedLoginService(UnattendedLoginStore store, UnattendedLoginRunner runner, TimeProvider clock, ILogger<UnattendedLoginService> logger)
{
    private static readonly SemaphoreSlim RunGate = new(1, 1);

    public UnattendedLoginView View()
    {
        var settings = store.Load();
        var hasCredential = store.HasCredential();
        if (settings is null && !hasCredential) return NotConfiguredView();
        settings ??= new UnattendedLoginSettings();
        var fingerprint = Fingerprint(settings);
        var requirements = Requirements(settings, hasCredential, fingerprint);
        var state = State(settings, hasCredential, fingerprint);
        return new UnattendedLoginView
        {
            Configured = true,
            Enabled = settings.Enabled,
            State = state,
            StateLabel = BrowserAuthenticationText.State(state),
            IdentityAlias = settings.IdentityAlias,
            CredentialConfigured = hasCredential,
            LoginNameHint = hasCredential ? settings.LoginNameHint : null,
            CredentialUpdatedAt = hasCredential ? settings.CredentialUpdatedAt : null,
            CredentialStorage = hasCredential ? UnattendedLoginStore.StorageDescription : "No credential stored",
            LoginUrl = settings.LoginUrl,
            SuccessUrlContains = settings.SuccessUrlContains,
            SuccessSelector = settings.SuccessSelector,
            UsernameSelector = settings.UsernameSelector,
            PasswordSelector = settings.PasswordSelector,
            SubmitSelector = settings.SubmitSelector,
            Provider = settings.Provider,
            TestIdentity = settings.Provider == UnattendedLoginProvider.MicrosoftEntraId && hasCredential ? settings.TestIdentity : null,
            ProtectedResourceUrl = settings.ProtectedResourceUrl,
            ConnectionStatus = Connection(state, settings),
            ConnectionStatusLabel = BrowserAuthenticationText.Connection(Connection(state, settings)),
            LastValidation = settings.LastValidation is { } v && Enum.TryParse<UnattendedLoginOutcome>(v.Outcome, out var outcome)
                ? new UnattendedLoginValidation(outcome, v.Message, v.ValidatedAt, v.Fingerprint == fingerprint, v.DurationMs) { Steps = v.Steps } : null,
            Requirements = requirements,
            Audit = settings.Audit.AsEnumerable().Reverse().Take(10).Select(a => new UnattendedLoginAuditEntry(a.At, a.Event)).ToList(),
            CreatedAt = settings.CreatedAt == default ? null : settings.CreatedAt,
            UpdatedAt = settings.UpdatedAt == default ? null : settings.UpdatedAt,
        };
    }

    public UnattendedLoginSaveOutcome Save(SaveUnattendedLoginRequest request)
    {
        var errors = new List<string>();
        var existing = store.Load();
        var hasCredential = store.HasCredential();
        var entra = request.Provider == UnattendedLoginProvider.MicrosoftEntraId;
        var loginUrl = Clean(request.LoginUrl, 2000);
        var loginName = request.LoginName?.Trim();
        var password = request.Password;
        var identity = Clean(request.IdentityAlias, 120) ?? (entra ? Clean(loginName, 120) ?? existing?.TestIdentity : null);
        var protectedResource = entra ? Clean(request.ProtectedResourceUrl, 2000) : null;
        if (identity is null) errors.Add(entra ? "Choose the test identity." : "Enter the automation identity name.");
        if (loginUrl is null) errors.Add(entra ? "Enter the application URL." : "Enter the login URL.");
        else if (!ValidLoginUrl(loginUrl, out var urlError)) errors.Add(urlError!);
        if (protectedResource is not null && loginUrl is not null && !ValidProtectedResource(loginUrl, protectedResource))
            errors.Add("The protected resource must be a path or a URL on the application.");
        // Microsoft Entra ID confirms success itself (identity + protected resource); a generic login form needs explicit success detection.
        if (!entra && Clean(request.SuccessUrlContains, 500) is null && Clean(request.SuccessSelector, 500) is null)
            errors.Add("Enter how to recognise a successful login: text the URL contains after login, or an authenticated page marker.");

        // Entra ID: the selected test identity is kept. The password alone can be replaced (same identity); choosing another identity needs its
        // password. Generic: the login name is write-only, so both are entered together to replace the credential.
        var storedIdentity = existing?.Provider == UnattendedLoginProvider.MicrosoftEntraId ? existing.TestIdentity : null;
        if (entra && string.IsNullOrEmpty(password) && hasCredential && (string.IsNullOrEmpty(loginName) || string.Equals(loginName, storedIdentity, StringComparison.OrdinalIgnoreCase)))
            loginName = null; // unchanged identity, no new password: keep the stored credential
        if (entra && !string.IsNullOrEmpty(password) && string.IsNullOrEmpty(loginName)) loginName = storedIdentity;
        var newCredential = !string.IsNullOrEmpty(loginName) || !string.IsNullOrEmpty(password);
        if (newCredential && (string.IsNullOrEmpty(loginName) || string.IsNullOrEmpty(password)))
            errors.Add(entra ? "Enter the password of the selected test identity." : "Enter both the login name and the password to set or replace the credential.");
        if (!newCredential && !hasCredential) errors.Add(entra ? "Choose the test identity and enter its password." : "Enter the login name and password of the automation identity.");
        if (entra && loginName is { Length: > 0 } && (!loginName.Contains('@') || loginName.Length > 254 || loginName.Any(char.IsWhiteSpace)))
            errors.Add("The test identity must be an account name such as testbruker01@bufdir.no.");
        if (errors.Count > 0) return new(null, errors);

        var now = clock.GetUtcNow();
        var settings = (existing ?? new UnattendedLoginSettings { CreatedAt = now }) with
        {
            Enabled = request.Enabled,
            IdentityAlias = identity,
            LoginUrl = loginUrl,
            SuccessUrlContains = Clean(request.SuccessUrlContains, 500),
            SuccessSelector = Clean(request.SuccessSelector, 500),
            UsernameSelector = Clean(request.UsernameSelector, 500),
            PasswordSelector = Clean(request.PasswordSelector, 500),
            SubmitSelector = Clean(request.SubmitSelector, 500),
            Provider = request.Provider,
            ProtectedResourceUrl = protectedResource,
            TestIdentity = entra ? (newCredential ? loginName : storedIdentity) : null,
            UpdatedAt = now,
        };
        var events = new List<string> { existing is null ? "Configuration created" : "Configuration changed" };
        if (newCredential)
        {
            store.SaveCredential(loginName!, password!);
            settings = settings with { CredentialVersion = settings.CredentialVersion + 1, CredentialUpdatedAt = now, LoginNameHint = UnattendedLoginStore.MaskLoginName(loginName!) };
            if (entra && !string.Equals(loginName, storedIdentity, StringComparison.OrdinalIgnoreCase) && storedIdentity is not null) events.Add("Test identity changed");
            events.Add(hasCredential ? "Credential replaced" : "Credential set");
        }
        if (existing is not null && existing.Enabled != settings.Enabled) events.Add(settings.Enabled ? "Enabled" : "Disabled");
        store.Save(settings with { Audit = [.. settings.Audit, .. events.Select(e => new StoredAuditEntry(now, e))] });
        logger.LogInformation("Unattended browser login configuration saved. Events {Events}; credential changed {CredentialChanged}", string.Join(", ", events), newCredential);
        return new(View(), []);
    }

    public UnattendedLoginView? SetEnabled(bool enabled)
    {
        var settings = store.Load();
        if (settings is null) return null;
        var now = clock.GetUtcNow();
        store.Save(settings with { Enabled = enabled, UpdatedAt = now, Audit = [.. settings.Audit, new(now, enabled ? "Enabled" : "Disabled")] });
        return View();
    }

    /// <summary>Removes the unattended configuration and credential only.</summary>
    public void Clear()
    {
        store.Clear();
        logger.LogInformation("Unattended browser login configuration removed");
    }

    /// <summary>Explicit validation: one isolated login with the stored configuration. Never runs on page load.</summary>
    public async Task<UnattendedLoginValidationResult> ValidateAsync(CancellationToken ct)
    {
        var run = await RunAsync<bool>(null, requireReady: false, ct);
        var settings = store.Load();
        if (settings is not null && run.Outcome is not (UnattendedLoginOutcome.NotConfigured or UnattendedLoginOutcome.Disabled))
        {
            var now = clock.GetUtcNow();
            store.Save(settings with
            {
                LastValidation = new StoredValidation(run.Outcome.ToString(), run.Message, now, Fingerprint(settings), run.DurationMs) { Steps = run.Steps },
                Audit = [.. settings.Audit, new(now, $"Validation performed: {BrowserAuthenticationText.Outcome(run.Outcome)}")],
            });
        }
        return new(run.Outcome, run.Message, BrowserAuthenticationText.Recommendation(run.Outcome), run.DurationMs, View()) { Steps = run.Steps };
    }

    /// <summary>
    /// The consumer entry point (an explicitly requested <see cref="BrowserAuthenticationMode.UnattendedBrowser"/> run): a fresh unattended
    /// login, then <paramref name="consumer"/> on the authenticated page. Requires the capability to be Ready. On any failure it returns
    /// the outcome and never falls back to attended login.
    /// </summary>
    public async Task<UnattendedBrowserRun<T>> RunAuthenticatedAsync<T>(Func<IPage, CancellationToken, Task<T>> consumer, CancellationToken ct)
    {
        var run = await RunAsync(consumer, requireReady: true, ct);
        return new(run.Outcome, run.Message, run.Result);
    }

    public AutomatedBrowserExecutionReadiness AutomatedReadiness()
    {
        var view = View();
        if (view.State == UnattendedLoginState.Ready)
            return new(true, "Available", "Unattended browser authentication is ready. Each consumer still checks its own backend authentication, TLS trust and environment safety.", view.State, view.Requirements);
        var reason = view.State switch
        {
            UnattendedLoginState.NotConfigured => "Unattended browser authentication not configured",
            UnattendedLoginState.Disabled => "Unattended browser authentication is disabled",
            UnattendedLoginState.NotVerified => "Unattended browser authentication has not been validated with the current configuration",
            _ => $"Unattended browser login validation failed: {(view.LastValidation is { } v ? BrowserAuthenticationText.Outcome(v.Outcome) : "unknown")}",
        };
        return new(false, "Automated browser execution unavailable", reason, view.State, view.Requirements);
    }

    private async Task<(UnattendedLoginOutcome Outcome, string Message, long DurationMs, T? Result, List<UnattendedLoginStep> Steps)> RunAsync<T>(Func<IPage, CancellationToken, Task<T>>? consumer, bool requireReady, CancellationToken ct)
    {
        var settings = store.Load();
        if (settings is null) return (UnattendedLoginOutcome.NotConfigured, "Unattended login is not configured.", 0, default, []);
        if (!settings.Enabled) return (UnattendedLoginOutcome.Disabled, "Unattended login is disabled. Enable it to use it.", 0, default, []);
        if (string.IsNullOrWhiteSpace(settings.IdentityAlias)) return (UnattendedLoginOutcome.IdentityMissing, "No automation identity is configured.", 0, default, []);
        if (string.IsNullOrWhiteSpace(settings.LoginUrl)) return (UnattendedLoginOutcome.LoginTargetMissing, "No login URL is configured.", 0, default, []);
        if (!HasSuccessDetection(settings))
            return (UnattendedLoginOutcome.SuccessDetectionMissing, "No success detection is configured.", 0, default, []);
        var credential = store.ReadCredential();
        if (credential is null) return (UnattendedLoginOutcome.CredentialMissing, "No usable credential is stored for the automation identity.", 0, default, []);
        if (requireReady && State(settings, true, Fingerprint(settings)) != UnattendedLoginState.Ready)
            return (UnattendedLoginOutcome.NotConfigured, "Unattended login is not Ready: validate it with the current configuration first.", 0, default, []);

        // One login at a time: concurrent runs could look like repeated failed logins to the identity provider.
        await RunGate.WaitAsync(ct);
        try
        {
            var (attempt, result) = await runner.RunAsync(settings, credential, consumer, ct);
            return (attempt.Outcome, attempt.Message, attempt.DurationMs, result, attempt.Steps);
        }
        finally { RunGate.Release(); }
    }

    /// <summary>Microsoft Entra ID verifies identity and protected resource itself; a generic form needs a URL text or a page marker.</summary>
    private static bool HasSuccessDetection(UnattendedLoginSettings s) =>
        s.Provider == UnattendedLoginProvider.MicrosoftEntraId || !string.IsNullOrWhiteSpace(s.SuccessUrlContains) || !string.IsNullOrWhiteSpace(s.SuccessSelector);

    /// <summary>Configured / Connected / Failed / Requires interaction. Connected only for a successful validation of the current configuration.</summary>
    private static UnattendedConnectionStatus Connection(UnattendedLoginState state, UnattendedLoginSettings settings) => state switch
    {
        UnattendedLoginState.NotConfigured => UnattendedConnectionStatus.NotConfigured,
        UnattendedLoginState.Disabled => UnattendedConnectionStatus.Disabled,
        UnattendedLoginState.Ready => UnattendedConnectionStatus.Connected,
        UnattendedLoginState.ValidationFailed when settings.LastValidation is { } v && Enum.TryParse<UnattendedLoginOutcome>(v.Outcome, out var o)
            && BrowserAuthenticationText.RequiresInteraction(o) => UnattendedConnectionStatus.RequiresInteraction,
        UnattendedLoginState.ValidationFailed => UnattendedConnectionStatus.Failed,
        _ => UnattendedConnectionStatus.Configured,
    };

    private static UnattendedLoginState State(UnattendedLoginSettings settings, bool hasCredential, string fingerprint)
    {
        if (!settings.Enabled) return UnattendedLoginState.Disabled;
        if (!hasCredential || string.IsNullOrWhiteSpace(settings.IdentityAlias) || string.IsNullOrWhiteSpace(settings.LoginUrl) || !HasSuccessDetection(settings))
            return UnattendedLoginState.NotVerified;
        if (settings.LastValidation is not { } validation || validation.Fingerprint != fingerprint) return UnattendedLoginState.NotVerified;
        return validation.Outcome == nameof(UnattendedLoginOutcome.Succeeded) ? UnattendedLoginState.Ready : UnattendedLoginState.ValidationFailed;
    }

    private static List<UnattendedLoginRequirement> Requirements(UnattendedLoginSettings settings, bool hasCredential, string fingerprint)
    {
        var validation = settings.LastValidation;
        var validated = validation is not null && validation.Fingerprint == fingerprint && validation.Outcome == nameof(UnattendedLoginOutcome.Succeeded);
        return
        [
            new("enabled", "Unattended login enabled", settings.Enabled, settings.Enabled ? "Enabled" : "Disabled: automated execution does not use it"),
            new("identity", "Automation identity configured", !string.IsNullOrWhiteSpace(settings.IdentityAlias), settings.IdentityAlias ?? "Not configured"),
            new("credential", "Credential configured", hasCredential, hasCredential ? "Configured securely" : "Missing"),
            new("login-target", "Login target configured", !string.IsNullOrWhiteSpace(settings.LoginUrl), settings.LoginUrl is { } url ? UnattendedLoginRunner.SafeOrigin(url) : "Not configured"),
            settings.Provider == UnattendedLoginProvider.MicrosoftEntraId
                ? new("success-detection", "Success detection configured", true, "Authenticated application session: test identity and protected resource"
                    + (settings.SuccessSelector is { } marker ? $" · marker {marker}" : ""))
                : new("success-detection", "Success detection configured", HasSuccessDetection(settings),
                    string.Join(" · ", new[] { settings.SuccessUrlContains is { } u ? $"URL contains “{u}”" : null, settings.SuccessSelector is { } s ? $"marker {s}" : null }.Where(x => x is not null)) is { Length: > 0 } d ? d : "Not configured"),
            new("validated", "Validated with the current configuration", validated,
                validation is null ? "Not validated yet" : validation.Fingerprint != fingerprint ? "Not verified since the configuration changed"
                : Enum.TryParse<UnattendedLoginOutcome>(validation.Outcome, out var o) ? BrowserAuthenticationText.Outcome(o) : validation.Outcome),
        ];
    }

    /// <summary>What a validation vouches for. The password itself is not hashed: its change is captured by the credential version.</summary>
    internal static string Fingerprint(UnattendedLoginSettings s)
    {
        var text = string.Join("\n", s.IdentityAlias, s.CredentialVersion, s.LoginUrl, s.SuccessUrlContains, s.SuccessSelector, s.UsernameSelector, s.PasswordSelector, s.SubmitSelector)
            + (s.Provider == UnattendedLoginProvider.Generic ? "" : string.Join("\n", "", s.Provider, s.TestIdentity, s.ProtectedResourceUrl));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32].ToLowerInvariant();
    }

    private static bool ValidLoginUrl(string url, out string? error)
    {
        error = null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        { error = "The login URL must be an absolute http or https URL."; return false; }
        if (uri.Scheme == "http" && !uri.IsLoopback)
        { error = "Use https for the login URL. Plain http is only accepted for a local (loopback) login page."; return false; }
        if (!string.IsNullOrEmpty(uri.UserInfo))
        { error = "The login URL must not contain a user name or password."; return false; }
        return true;
    }

    private static bool ValidProtectedResource(string appUrl, string resource) =>
        Uri.TryCreate(new Uri(appUrl), resource, out var resolved) && string.Equals(resolved.Host, new Uri(appUrl).Host, StringComparison.OrdinalIgnoreCase)
        && resolved.Scheme == new Uri(appUrl).Scheme && string.IsNullOrEmpty(resolved.UserInfo);

    private static string? Clean(string? value, int max)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed.Length > max ? trimmed[..max] : trimmed;
    }

    private static UnattendedLoginView NotConfiguredView() => new()
    {
        Configured = false,
        State = UnattendedLoginState.NotConfigured,
        StateLabel = BrowserAuthenticationText.State(UnattendedLoginState.NotConfigured),
        CredentialStorage = "No credential stored",
        Requirements =
        [
            new("enabled", "Unattended login enabled", false, "Not configured"),
            new("identity", "Automation identity configured", false, "Not configured"),
            new("credential", "Credential configured", false, "Missing"),
            new("login-target", "Login target configured", false, "Not configured"),
            new("success-detection", "Success detection configured", false, "Not configured"),
            new("validated", "Validated with the current configuration", false, "Not validated yet"),
        ],
    };
}
