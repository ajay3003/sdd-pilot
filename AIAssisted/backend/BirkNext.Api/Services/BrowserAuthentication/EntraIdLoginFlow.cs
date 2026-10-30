using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using BirkNext.BrowserAuthentication;
using Microsoft.Playwright;

namespace BirkNext.Api.Services.BrowserAuthentication;

/// <summary>
/// Microsoft Entra ID unattended sign-in, in the fresh browser context the runner created:
/// application → redirect to Entra ID → (account picker) → username → password → (stay signed in: No) → back to the application → test
/// identity confirmed in the application → protected resource accessible. Each step is reported on its own.
/// <para>Recognition uses the stable element names of the Entra ID sign-in page (<c>loginfmt</c>, <c>passwd</c>, <c>idSIButton9</c>,
/// <c>idBtn_Back</c>, <c>KmsiCheckboxField</c>, <c>tilesHolder</c>/<c>otherTile</c>, <c>usernameError</c>/<c>passwordError</c>,
/// <c>idDiv_SAOTCAS_Title</c>/<c>idDiv_SAOTCC_Title</c>) — not a generic "first text box". The hidden password field Entra renders off-screen
/// on the username screen is ignored. One password submission only; MFA, proof-up and conditional access stop the login (never answered);
/// passkey/certificate/federated screens are reported as unsupported. A URL alone never counts as signed in: the identity must be confirmed.</para>
/// </summary>
internal sealed class EntraIdLoginFlow(IReadOnlySet<string> entraHosts, UnattendedLoginTimeouts timeouts)
{
    public static readonly IReadOnlySet<string> DefaultEntraHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "login.microsoftonline.com", "login.microsoft.com" };

    /// <summary>
    /// Development-only seam for a local fake Entra ID sign-in page: BrowserAuthentication:Unattended:DevelopmentEntraHosts (comma-separated),
    /// entries "host" or "host:port", honoured only in Development and only for loopback hosts, added to the real Entra ID hosts. Production always uses the real hosts only.
    /// </summary>
    public static IReadOnlySet<string>? DevelopmentHosts(IHostEnvironment environment, IConfiguration configuration)
    {
        if (!environment.IsDevelopment() || configuration["BrowserAuthentication:Unattended:DevelopmentEntraHosts"] is not { Length: > 0 } value) return null;
        var loopback = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(h => h.Split(':')[0] is var host && (host is "localhost" || (System.Net.IPAddress.TryParse(host, out var ip) && System.Net.IPAddress.IsLoopback(ip)))).ToList();
        return loopback.Count == 0 ? null : new HashSet<string>(DefaultEntraHosts.Concat(loopback), StringComparer.OrdinalIgnoreCase);
    }

    private const string UsernameField = "input[name='loginfmt'], #i0116";
    private const string PasswordField = "input[name='passwd'], #i0118";
    private const string PrimaryButton = "#idSIButton9, input[type='submit'][data-report-event='Signin_Submit']";
    private const string KmsiScreen = "#KmsiCheckboxField, #idDiv_KMSI_Title, [data-viewid='4']";
    private const string KmsiNo = "#idBtn_Back";
    private const string AccountTiles = "#tilesHolder, .table[role='button'][data-test-id], div[data-test-id]";
    private const string OtherAccountTile = "#otherTile, #otherTileText";
    private const string ErrorElements = "#usernameError:not(:empty), #passwordError:not(:empty), #idTD_Error:not(:empty), #errorText:not(:empty)";
    private const string EntraChallenge = "#idDiv_SAOTCAS_Title, #idDiv_SAOTCC_Title, #idTxtBx_SAOTCC_OTC, #idDiv_SAOTCS_Title, #idDiv_ProofUp_Title, #idRichContext_DisplaySign";
    private const string Unsupported = "#fidoDialogTitle, #idDiv_RemoteNGC_PollingDescription, #certificateAuthBtn, [data-unsupported-auth]";
    private static readonly Regex ProofUpText = new(@"more information required|mer informasjon (er )?påkrevd|mer informasjon kreves|keep your account secure|help us protect your account", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex MfaText = new(@"approve (the |this |your )?sign[- ]?in( request)?|enter (the )?code|verify your identity|godkjenn påloggings(forespørsel|forespørselen)|bekreft identiteten|skriv inn kode",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SignInPrompt = new(@"^\s*(logg (inn|på)|sign in|log in)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private sealed class Steps
    {
        public static readonly (string Key, string Label)[] All =
        [
            ("application", "Application connectivity"), ("entra-redirect", "Entra ID redirect"), ("login-form", "Login form detection"),
            ("credential-submission", "Credential submission"), ("authentication", "Authentication completion"),
            ("identity", "Test-user identity verification"), ("protected-resource", "Protected-resource access"),
        ];
        private readonly Dictionary<string, (UnattendedLoginStepStatus Status, string Detail)> _done = [];
        public void Pass(string key, string detail) => _done[key] = (UnattendedLoginStepStatus.Passed, detail);
        public void Fail(string key, string detail) => _done[key] = (UnattendedLoginStepStatus.Failed, detail);
        public void Interaction(string key, string detail) => _done[key] = (UnattendedLoginStepStatus.RequiresInteraction, detail);
        public bool Has(string key) => _done.ContainsKey(key);
        public List<UnattendedLoginStep> Build() =>
            [.. All.Select(s => _done.TryGetValue(s.Key, out var d) ? new UnattendedLoginStep(s.Key, s.Label, d.Status, d.Detail) : new UnattendedLoginStep(s.Key, s.Label, UnattendedLoginStepStatus.NotReached, "Not reached"))];
    }

    private sealed record Result(UnattendedLoginOutcome Outcome, string Message);

    /// <summary>An Entra ID sign-in page: its host (or host:port, for a local fake) is one of the Entra ID hosts.</summary>
    public bool IsEntraHost(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && (entraHosts.Contains(uri.Host) || entraHosts.Contains(uri.Authority));

    /// <param name="alreadyNavigated">True when the generic flow already opened the application and was redirected to Entra ID.</param>
    public async Task<(UnattendedLoginOutcome Outcome, string Message, List<UnattendedLoginStep> Steps)> LoginAsync(IPage page, UnattendedLoginSettings settings,
        UnattendedLoginCredential credential, Func<IPage, string, CancellationToken, Task<UnattendedLoginAttempt?>> navigate, bool alreadyNavigated, CancellationToken ct)
    {
        var steps = new Steps();
        var result = await RunAsync(page, settings, credential, navigate, alreadyNavigated, steps, ct);
        return (result.Outcome, result.Message, steps.Build());
    }

    private async Task<Result> RunAsync(IPage page, UnattendedLoginSettings settings, UnattendedLoginCredential credential,
        Func<IPage, string, CancellationToken, Task<UnattendedLoginAttempt?>> navigate, bool alreadyNavigated, Steps steps, CancellationToken ct)
    {
        var appUrl = settings.LoginUrl!;
        var appAuthority = new Uri(appUrl).Authority;

        // 1. Application connectivity (a fresh context: no cookie, storage or session from any attended flow).
        if (!alreadyNavigated && await navigate(page, appUrl, ct) is { } failure)
        {
            steps.Fail("application", failure.Message);
            return new(failure.Outcome, failure.Message);
        }
        steps.Pass("application", $"{UnattendedLoginRunner.SafeOrigin(appUrl)} answered.");

        // 2. Redirect to Entra ID. SPAs redirect after their script starts; some show a sign-in button first (clicked once — no credential).
        var redirectDeadline = Stopwatch.StartNew();
        var clickedSignIn = false;
        while (!IsEntraHost(page.Url))
        {
            ct.ThrowIfCancellationRequested();
            if (redirectDeadline.ElapsedMilliseconds > timeouts.RedirectWaitMs)
            {
                const string message = "The application did not redirect to Microsoft Entra ID in a fresh browser context, so no sign-in happened. An existing or anonymous session is never accepted as proof.";
                steps.Fail("entra-redirect", message);
                return new(UnattendedLoginOutcome.EntraRedirectNotDetected, message);
            }
            if (!clickedSignIn && redirectDeadline.ElapsedMilliseconds > 2_500 && await SignInButtonAsync(page) is { } button)
            {
                clickedSignIn = true;
                try { await button.ClickAsync(new LocatorClickOptions { Timeout = 3_000, NoWaitAfter = true }); } catch (PlaywrightException) { }
            }
            await Task.Delay(150, ct);
        }
        steps.Pass("entra-redirect", $"Redirected to {new Uri(page.Url).Host}{(clickedSignIn ? " after the application's sign-in button" : "")}.");

        // 3–5. Entra ID screens until the browser is back on the application.
        var submitted = false;
        var screenDeadline = Stopwatch.StartNew();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var url = page.Url;
            if (Uri.TryCreate(url, UriKind.Absolute, out var current) && string.Equals(current.Authority, appAuthority, StringComparison.OrdinalIgnoreCase) && !IsEntraHost(url))
            {
                if (!submitted)
                {
                    const string message = "The browser returned to the application without a credential being submitted. Without a sign-in this is not accepted as proof of authentication.";
                    steps.Fail("credential-submission", message);
                    return new(UnattendedLoginOutcome.SuccessNotConfirmed, message);
                }
                break;
            }
            if (!IsEntraHost(url) && Uri.TryCreate(url, UriKind.Absolute, out var other) && other.Scheme is "http" or "https")
            {
                var message = $"Sign-in was handed to another identity provider ({other.Host}); BirkNext automates Microsoft Entra ID only.";
                steps.Fail(steps.Has("login-form") ? "authentication" : "login-form", message);
                return new(UnattendedLoginOutcome.UnsupportedAuthenticationFlow, message);
            }

            if (await ChallengeAsync(page) is { } challenge)
            {
                var message = challenge == UnattendedLoginOutcome.ConditionalAccessChallenge
                    ? "Unattended login blocked: Microsoft Entra ID requires an interactive/conditional access step. It was not bypassed."
                    : "Unattended login blocked: Microsoft Entra ID asks for MFA or another interactive verification. It was not answered.";
                steps.Interaction(submitted ? "authentication" : steps.Has("login-form") ? "credential-submission" : "login-form", message);
                return new(challenge, message);
            }
            if (await AnyVisibleAsync(page, Unsupported))
            {
                const string message = "Microsoft Entra ID offers a sign-in method BirkNext does not automate (passkey, security key, phone sign-in or certificate).";
                steps.Fail(submitted ? "authentication" : "login-form", message);
                return new(UnattendedLoginOutcome.UnsupportedAuthenticationFlow, message);
            }
            if (await AnyVisibleAsync(page, ErrorElements))
            {
                var message = submitted ? "Microsoft Entra ID rejected the password. It was not retried, so the account is not locked by repeated attempts."
                    : "Microsoft Entra ID did not accept the test identity name.";
                steps.Fail(submitted ? "authentication" : "credential-submission", message);
                return new(UnattendedLoginOutcome.AuthenticationRejected, message);
            }

            if (await OnScreenAsync(page, PasswordField))
            {
                if (submitted)
                {
                    // Entra shows the password screen again only with an error; give the error a moment to render.
                    await Task.Delay(200, ct);
                    if (screenDeadline.ElapsedMilliseconds > timeouts.FieldWaitMs * 2L)
                    {
                        const string message = "Microsoft Entra ID kept the password screen after the single submission (credential not accepted). It was not retried.";
                        steps.Fail("authentication", message);
                        return new(UnattendedLoginOutcome.AuthenticationRejected, message);
                    }
                    continue;
                }
                if (await DisplayedAccountAsync(page) is { Length: > 0 } displayed && !Same(displayed, credential.LoginName))
                {
                    const string message = "The Microsoft Entra ID password screen is for another account than the configured test identity. No password was entered.";
                    steps.Fail("login-form", message);
                    return new(UnattendedLoginOutcome.IdentityMismatch, message);
                }
                if (!steps.Has("login-form")) steps.Pass("login-form", "Password screen with the test identity already selected.");
                await page.Locator(PasswordField).First.FillAsync(credential.Password, new LocatorFillOptions { Timeout = timeouts.FieldWaitMs });
                await ClickPrimaryAsync(page, PasswordField);
                submitted = true;
                screenDeadline.Restart();
                steps.Pass("credential-submission", "Test identity and password submitted once.");
                continue;
            }
            if (await OnScreenAsync(page, UsernameField))
            {
                if (steps.Has("login-form") && screenDeadline.ElapsedMilliseconds < 1_000) { await Task.Delay(150, ct); continue; }
                steps.Pass("login-form", "Microsoft Entra ID username and password screens.");
                await page.Locator(UsernameField).First.FillAsync(credential.LoginName, new LocatorFillOptions { Timeout = timeouts.FieldWaitMs });
                await ClickPrimaryAsync(page, UsernameField);
                screenDeadline.Restart();
                await Task.Delay(300, ct);
                continue;
            }
            if (await AnyVisibleAsync(page, KmsiScreen) && await UnattendedLoginRunner.VisibleAsync(page, KmsiNo))
            {
                // "Stay signed in?" → No: nothing persists beyond this isolated context anyway.
                await page.Locator(KmsiNo).First.ClickAsync(new LocatorClickOptions { Timeout = 3_000, NoWaitAfter = true });
                await Task.Delay(300, ct);
                continue;
            }
            if (!submitted && await AnyVisibleAsync(page, AccountTiles))
            {
                var tile = page.Locator($"[data-test-id='{credential.LoginName.Replace("'", "")}'], #tilesHolder [role='button']:has-text('{credential.LoginName.Replace("'", "")}')").First;
                if (await tile.CountAsync() > 0 && await tile.IsVisibleAsync()) await tile.ClickAsync(new LocatorClickOptions { Timeout = 3_000, NoWaitAfter = true });
                else if (await AnyVisibleAsync(page, OtherAccountTile)) await page.Locator(OtherAccountTile).First.ClickAsync(new LocatorClickOptions { Timeout = 3_000, NoWaitAfter = true });
                if (!steps.Has("login-form")) steps.Pass("login-form", "Account picker: the test identity was chosen.");
                await Task.Delay(300, ct);
                continue;
            }
            if (screenDeadline.ElapsedMilliseconds > timeouts.FieldWaitMs * 2L)
            {
                var message = submitted
                    ? "After the password, Microsoft Entra ID showed a screen BirkNext does not recognise and did not return to the application."
                    : "No Microsoft Entra ID username, account or password screen was recognised.";
                steps.Fail(submitted ? "authentication" : "login-form", message);
                return new(submitted ? UnattendedLoginOutcome.UnsupportedAuthenticationFlow : UnattendedLoginOutcome.LoginPageNotRecognized, message);
            }
            await Task.Delay(150, ct);
        }
        steps.Pass("authentication", $"Microsoft Entra ID returned to {UnattendedLoginRunner.SafeOrigin(page.Url)}.");

        // 6. Test identity: the application must show it (page text, or the signed-in account the application's MSAL keeps in web storage —
        //    account metadata only, never a token). A different account is a mismatch; none found is "not verified". Never URL alone.
        var identityDeadline = Stopwatch.StartNew();
        string? seen = null;
        while (identityDeadline.ElapsedMilliseconds < timeouts.FieldWaitMs * 2L)
        {
            ct.ThrowIfCancellationRequested();
            if (IsEntraHost(page.Url)) break;
            var (inPage, accounts) = await IdentityEvidenceAsync(page, credential.LoginName, settings.SuccessSelector);
            if (inPage || accounts.Any(a => Same(a, credential.LoginName))) { seen = credential.LoginName; break; }
            if (accounts.Count > 0 && identityDeadline.ElapsedMilliseconds > 1_500)
            {
                const string message = "The application is signed in with another account than the configured test identity.";
                steps.Fail("identity", message);
                return new(UnattendedLoginOutcome.IdentityMismatch, message);
            }
            await Task.Delay(250, ct);
        }
        if (seen is null)
        {
            var message = IsEntraHost(page.Url)
                ? "The application sent the browser back to Microsoft Entra ID: the session was not established."
                : "The application did not show the configured test identity after sign-in.";
            steps.Fail("identity", message);
            return new(UnattendedLoginOutcome.IdentityNotVerified, message);
        }
        if (!string.IsNullOrWhiteSpace(settings.SuccessSelector) && !await UnattendedLoginRunner.VisibleAsync(page, settings.SuccessSelector))
        {
            const string message = "The test identity was seen, but the configured authenticated page marker did not appear.";
            steps.Fail("identity", message);
            return new(UnattendedLoginOutcome.SuccessNotConfirmed, message);
        }
        steps.Pass("identity", $"Signed in as the configured test identity ({UnattendedLoginStore.MaskLoginName(seen)}).");

        // 7. Protected resource in the same authenticated context.
        var resource = ResolveResource(appUrl, settings.ProtectedResourceUrl);
        if (await navigate(page, resource, ct) is { } denied)
        {
            var message = $"The protected resource could not be opened: {denied.Message}";
            steps.Fail("protected-resource", message);
            return new(UnattendedLoginOutcome.ProtectedResourceDenied, message);
        }
        var settle = Stopwatch.StartNew();
        while (settle.ElapsedMilliseconds < 2_000 && !IsEntraHost(page.Url)) await Task.Delay(200, ct);
        if (IsEntraHost(page.Url) || await OnScreenAsync(page, PasswordField) || await OnScreenAsync(page, UsernameField))
        {
            const string message = "The protected resource asked for sign-in again: the authenticated session does not reach it.";
            steps.Fail("protected-resource", message);
            return new(UnattendedLoginOutcome.ProtectedResourceDenied, message);
        }
        if (!string.IsNullOrWhiteSpace(settings.SuccessSelector) && !await WaitVisibleAsync(page, settings.SuccessSelector, timeouts.FieldWaitMs, ct))
        {
            const string message = "The protected resource opened without the authenticated page marker.";
            steps.Fail("protected-resource", message);
            return new(UnattendedLoginOutcome.ProtectedResourceDenied, message);
        }
        steps.Pass("protected-resource", $"{new Uri(resource).AbsolutePath} opened in the authenticated session.");
        return new(UnattendedLoginOutcome.Succeeded, "Signed in with Microsoft Entra ID: identity and protected resource confirmed.");
    }

    internal static string ResolveResource(string appUrl, string? protectedResource) =>
        string.IsNullOrWhiteSpace(protectedResource) ? appUrl : Uri.TryCreate(new Uri(appUrl), protectedResource, out var resolved) ? resolved.ToString() : appUrl;

    private static bool Same(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Entra challenge screens by their element ids first; text only when no username/password field is on screen (the password
    /// screen itself may mention other sign-in options, which is not a challenge).</summary>
    private static async Task<UnattendedLoginOutcome?> ChallengeAsync(IPage page)
    {
        if (await AnyVisibleAsync(page, EntraChallenge) || await UnattendedLoginRunner.VisibleAsync(page, UnattendedLoginRunner.ChallengeSelector))
            return UnattendedLoginOutcome.InteractiveChallengeDetected;
        var text = await UnattendedLoginRunner.BodyTextAsync(page);
        if (UnattendedLoginRunner.ConditionalAccessText.IsMatch(text)) return UnattendedLoginOutcome.ConditionalAccessChallenge;
        if (await OnScreenAsync(page, PasswordField) || await OnScreenAsync(page, UsernameField)) return null;
        return ProofUpText.IsMatch(text) || MfaText.IsMatch(text) ? UnattendedLoginOutcome.InteractiveChallengeDetected : null;
    }

    /// <summary>Visible and on screen: Entra keeps an off-screen password input (class moveOffScreen) on the username screen.</summary>
    private static async Task<bool> OnScreenAsync(IPage page, string selector)
    {
        try
        {
            var locator = page.Locator(selector).First;
            if (!await locator.IsVisibleAsync()) return false;
            return await locator.EvaluateAsync<bool>("""
                e => { const r = e.getBoundingClientRect(); const s = getComputedStyle(e);
                       return r.width > 1 && r.height > 1 && r.right > 0 && r.bottom > 0 && r.left < innerWidth && r.top < innerHeight
                              && !e.classList.contains('moveOffScreen') && e.getAttribute('aria-hidden') !== 'true' && s.visibility !== 'hidden' && !e.disabled; }
                """);
        }
        catch (PlaywrightException) { return false; }
    }

    private static async Task ClickPrimaryAsync(IPage page, string field)
    {
        if (await UnattendedLoginRunner.VisibleAsync(page, PrimaryButton)) await page.Locator(PrimaryButton).First.ClickAsync(new LocatorClickOptions { Timeout = 5_000, NoWaitAfter = true });
        else await page.Locator(field).First.PressAsync("Enter", new LocatorPressOptions { Timeout = 5_000, NoWaitAfter = true });
    }

    private static async Task<string?> DisplayedAccountAsync(IPage page)
    {
        try
        {
            var name = page.Locator("#displayName, #userDisplayName, [data-bind*='unsafe_displayName']").First;
            return await name.CountAsync() > 0 && await name.IsVisibleAsync() ? (await name.InnerTextAsync(new LocatorInnerTextOptions { Timeout = 1_000 })).Trim() : null;
        }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException) { return null; }
    }

    private static async Task<ILocator?> SignInButtonAsync(IPage page)
    {
        try
        {
            var candidates = page.Locator("button, a, [role='button'], input[type='submit'], input[type='button']");
            var count = Math.Min(await candidates.CountAsync(), 40);
            for (var i = 0; i < count; i++)
            {
                var c = candidates.Nth(i);
                if (!await c.IsVisibleAsync()) continue;
                var text = (await c.InnerTextAsync(new LocatorInnerTextOptions { Timeout = 500 })).Trim();
                if (text.Length == 0) text = await c.GetAttributeAsync("value") ?? "";
                if (SignInPrompt.IsMatch(text)) return c;
            }
        }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException) { }
        return null;
    }

    /// <summary>
    /// Whether the page shows the login name, plus the usernames of MSAL account entries in web storage (objects with <c>username</c> and
    /// <c>homeAccountId</c>/<c>localAccountId</c> — account metadata, never token entries). Values are compared here and never logged.
    /// </summary>
    private static async Task<(bool InPage, List<string> Accounts)> IdentityEvidenceAsync(IPage page, string loginName, string? marker)
    {
        try
        {
            var json = await page.EvaluateAsync<string>("""
                ([login, marker]) => {
                  const l = login.toLowerCase();
                  const scope = marker ? Array.from(document.querySelectorAll(marker)).map(e => e.innerText || '').join(' ') : '';
                  const text = ((document.body?.innerText ?? '') + ' ' + scope).toLowerCase();
                  const accounts = [];
                  for (const store of [window.localStorage, window.sessionStorage]) {
                    try {
                      for (let i = 0; i < store.length; i++) {
                        const v = store.getItem(store.key(i));
                        if (!v || v.length > 20000 || v[0] !== '{') continue;
                        try { const o = JSON.parse(v); if (o && typeof o.username === 'string' && (o.homeAccountId || o.localAccountId) && !o.secret) accounts.push(o.username); } catch {}
                      }
                    } catch {}
                  }
                  return JSON.stringify({ inPage: text.includes(l), accounts });
                }
                """, new object?[] { loginName, string.IsNullOrWhiteSpace(marker) ? null : marker });
            using var doc = JsonDocument.Parse(json);
            return (doc.RootElement.GetProperty("inPage").GetBoolean(), [.. doc.RootElement.GetProperty("accounts").EnumerateArray().Select(a => a.GetString() ?? "").Where(a => a.Length > 0)]);
        }
        catch (Exception ex) when (ex is PlaywrightException or JsonException) { return (false, []); }
    }

    /// <summary>Any element of a selector list visible (a locator's First is the first in document order, which may be a hidden one).</summary>
    private static async Task<bool> AnyVisibleAsync(IPage page, string selector)
    {
        try
        {
            var all = page.Locator(selector);
            var count = Math.Min(await all.CountAsync(), 10);
            for (var i = 0; i < count; i++) if (await all.Nth(i).IsVisibleAsync()) return true;
        }
        catch (PlaywrightException) { }
        return false;
    }

    private static async Task<bool> WaitVisibleAsync(IPage page, string selector, int timeoutMs, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < timeoutMs)
        {
            if (await UnattendedLoginRunner.VisibleAsync(page, selector)) return true;
            await Task.Delay(200, ct);
        }
        return false;
    }
}
