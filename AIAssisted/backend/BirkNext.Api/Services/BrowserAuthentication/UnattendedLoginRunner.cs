using System.Diagnostics;
using System.Text.RegularExpressions;
using BirkNext.BrowserAuthentication;
using Microsoft.Playwright;

namespace BirkNext.Api.Services.BrowserAuthentication;

public sealed record UnattendedLoginAttempt(UnattendedLoginOutcome Outcome, string Message, long DurationMs)
{
    /// <summary>Per-step results (Microsoft Entra ID flow); empty for the generic login form.</summary>
    public List<UnattendedLoginStep> Steps { get; init; } = [];
}

/// <param name="RedirectWaitMs">Microsoft Entra ID: how long the application may take (script start, sign-in button) to redirect to Entra ID.</param>
public sealed record UnattendedLoginTimeouts(int LoginTimeoutMs = 60_000, int NavigationTimeoutMs = 15_000, int FieldWaitMs = 5_000, int RedirectWaitMs = 20_000);

/// <summary>Starts the isolated automation browser. Replaceable in tests; the default launches bundled Chromium headless.</summary>
public interface IUnattendedLoginBrowserLauncher
{
    Task<(IPlaywright Playwright, IBrowser Browser)> LaunchAsync(CancellationToken ct);
}

public sealed class PlaywrightUnattendedLoginBrowserLauncher : IUnattendedLoginBrowserLauncher
{
    public async Task<(IPlaywright Playwright, IBrowser Browser)> LaunchAsync(CancellationToken ct)
    {
        var playwright = await Playwright.CreateAsync();
        try
        {
            var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true, Args = ["--no-sandbox", "--disable-dev-shm-usage"] });
            return (playwright, browser);
        }
        catch
        {
            playwright.Dispose();
            throw;
        }
    }
}

/// <summary>
/// The deterministic unattended login: a fresh browser and a new, empty browser context per run (no profile directory, cookies, storage or
/// session from any attended flow), navigate → recognise the login page → fill login name and password → submit → wait for explicit
/// success evidence. Then, and only then, the consumer runs on the same page; the context and browser are closed afterwards.
/// <para>Safety: one login attempt. Only a failed navigation (transient) is retried once; a rejected credential is never retried, so the
/// test account is not locked. An MFA, one-time-code, authenticator or conditional-access challenge stops the login — it is never automated.
/// The whole login is bounded by <see cref="UnattendedLoginTimeouts.LoginTimeoutMs"/>. Logs carry outcome, duration and target origin only.</para>
/// </summary>
/// <para>Microsoft Entra ID: with that provider, or whenever the generic flow lands on an Entra ID host, <see cref="EntraIdLoginFlow"/> signs in
/// and reports each step. <paramref name="entraHosts"/> is the set of Entra ID sign-in hosts (tests: a loopback fake).</para>
public sealed class UnattendedLoginRunner(IUnattendedLoginBrowserLauncher launcher, ILogger<UnattendedLoginRunner> logger, UnattendedLoginTimeouts? timeouts = null,
    IReadOnlySet<string>? entraHosts = null)
{
    private readonly UnattendedLoginTimeouts _timeouts = timeouts ?? new();
    private readonly EntraIdLoginFlow _entra = new(entraHosts ?? EntraIdLoginFlow.DefaultEntraHosts, timeouts ?? new());

    private const string DefaultUsernameSelector = "input[autocomplete='username'], input[type='email'], input[name='loginfmt'], input[name*='user' i], input[id*='user' i], input[name*='login' i], input[name*='email' i], input[type='text']";
    private const string DefaultPasswordSelector = "input[type='password']";
    private const string DefaultSubmitSelector = "button[type='submit'], input[type='submit'], button:has-text('Sign in'), button:has-text('Log in'), button:has-text('Logg inn'), button:has-text('Next'), button:has-text('Neste')";
    internal const string ChallengeSelector = "input[autocomplete='one-time-code'], input[name*='otp' i], input[id*='otp' i], input[name*='totp' i], #idTxtBx_SAOTCC_OTC, #idDiv_SAOTCAS_Title, #idDiv_SAOTCC_Title, [data-mfa-challenge]";

    private static readonly Regex ChallengeText = new(@"verification code|one-time (pass)?code|one-time password|authenticator|approve (the |this |your )?sign[- ]?in|two-step verification|two-factor|2-step|multi-factor|\bmfa\b|enter (the )?code|security code|engangskode|tofaktor|bekreft (at det er deg|innlogging)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    internal static readonly Regex ConditionalAccessText = new(@"conditional access|you can't get there from here|you cannot access this right now|device (is not|must be) (managed|compliant|registered)|blocked by (your|an?) (organization|admin|policy)|sign-?in (was )?blocked|access (has been |is )?blocked",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RejectionText = new(@"incorrect|invalid (user|login|password|credentials)|wrong (user|password)|not recognized|does not exist|login failed|sign-?in failed|authentication failed|feil (brukernavn|passord)|ugyldig",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Signs in and, on success, runs <paramref name="consumer"/> on the authenticated page.</summary>
    public async Task<(UnattendedLoginAttempt Attempt, T? Result)> RunAsync<T>(UnattendedLoginSettings settings, UnattendedLoginCredential credential,
        Func<IPage, CancellationToken, Task<T>>? consumer, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        var origin = SafeOrigin(settings.LoginUrl);
        IPlaywright? playwright = null; IBrowser? browser = null; IBrowserContext? context = null;
        try
        {
            try { (playwright, browser) = await launcher.LaunchAsync(ct); }
            catch (Exception ex) when (ex is PlaywrightException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return (Done(UnattendedLoginOutcome.BrowserUnavailable, "The automation browser could not start.", clock, origin), default);
            }
            // A new context is empty: nothing from an attended session, profile or earlier run is visible to it.
            context = await browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = false, AcceptDownloads = false });
            var page = await context.NewPageAsync();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(_timeouts.LoginTimeoutMs);
            UnattendedLoginAttempt attempt;
            try { attempt = await LoginAsync(page, settings, credential, clock, deadline.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { attempt = Done(UnattendedLoginOutcome.Timeout, "The login did not finish within the time limit.", clock, origin); }
            catch (TimeoutException) { attempt = Done(UnattendedLoginOutcome.Timeout, "The login did not finish within the time limit.", clock, origin); }
            if (attempt.Outcome != UnattendedLoginOutcome.Succeeded)
            {
                await ClearSensitiveInputsAsync(page, settings);
                return (attempt, default);
            }
            var result = consumer is null ? default : await consumer(page, ct);
            return (attempt, result);
        }
        finally
        {
            try { if (context is not null) await context.CloseAsync(); } catch (PlaywrightException) { }
            try { if (browser is not null) await browser.CloseAsync(); } catch (PlaywrightException) { }
            playwright?.Dispose();
        }
    }

    private async Task<UnattendedLoginAttempt> LoginAsync(IPage page, UnattendedLoginSettings settings, UnattendedLoginCredential credential, Stopwatch clock, CancellationToken ct)
    {
        var origin = SafeOrigin(settings.LoginUrl);
        if (settings.Provider == UnattendedLoginProvider.MicrosoftEntraId) return await EntraAsync(page, settings, credential, clock, alreadyNavigated: false, ct);
        if (await NavigateAsync(page, settings.LoginUrl!, ct) is { } navigationFailure) return Done(navigationFailure.Outcome, navigationFailure.Message, clock, origin);

        var usernameSelector = Selector(settings.UsernameSelector, DefaultUsernameSelector);
        var passwordSelector = Selector(settings.PasswordSelector, DefaultPasswordSelector);
        var submitSelector = Selector(settings.SubmitSelector, DefaultSubmitSelector);

        // Recognise the login page: a password field, or a login-name field (two-step login pages ask for the name first).
        var fieldsDeadline = DateTime.UtcNow.AddMilliseconds(_timeouts.FieldWaitMs);
        bool passwordVisible = false, usernameVisible = false;
        while (DateTime.UtcNow < fieldsDeadline)
        {
            ct.ThrowIfCancellationRequested();
            // Redirected to Microsoft Entra ID (the login form there has its own screens): continue with the Entra ID flow.
            if (_entra.IsEntraHost(page.Url)) return await EntraAsync(page, settings, credential, clock, alreadyNavigated: true, ct);
            if (await ChallengeAsync(page) is { } early) return Done(early, ChallengeMessage(early), clock, origin);
            passwordVisible = await VisibleAsync(page, passwordSelector);
            usernameVisible = await VisibleAsync(page, usernameSelector);
            if (passwordVisible || usernameVisible) break;
            await Task.Delay(150, ct);
        }
        if (!passwordVisible && !usernameVisible)
            return Done(UnattendedLoginOutcome.LoginPageNotRecognized, "No login name or password field was found on the login page.", clock, origin);

        if (usernameVisible)
            await page.Locator(usernameSelector).First.FillAsync(credential.LoginName, new LocatorFillOptions { Timeout = _timeouts.FieldWaitMs });

        if (!passwordVisible)
        {
            // Two-step login: submit the name, then wait for the password field (or a challenge/rejection).
            await SubmitAsync(page, submitSelector, usernameSelector);
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (await ChallengeAsync(page) is { } challenge) return Done(challenge, ChallengeMessage(challenge), clock, origin);
                if (await VisibleAsync(page, passwordSelector)) break;
                if (await RejectedAsync(page)) return Done(UnattendedLoginOutcome.AuthenticationRejected, "The login page rejected the login name.", clock, origin);
                await Task.Delay(150, ct);
            }
        }

        await page.Locator(passwordSelector).First.FillAsync(credential.Password, new LocatorFillOptions { Timeout = _timeouts.FieldWaitMs });
        await SubmitAsync(page, submitSelector, passwordSelector);
        var submittedAt = DateTime.UtcNow;

        // Exactly one submission. Wait for explicit evidence: a challenge stops, success needs the configured URL/marker.
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (await ChallengeAsync(page) is { } challenge) return Done(challenge, ChallengeMessage(challenge), clock, origin);
            var passwordStillVisible = await VisibleAsync(page, passwordSelector);
            if (!passwordStillVisible && await SuccessAsync(page, settings))
                return Done(UnattendedLoginOutcome.Succeeded, "Signed in: the authenticated state was confirmed.", clock, origin);
            if (passwordStillVisible && DateTime.UtcNow - submittedAt > TimeSpan.FromMilliseconds(400) && await RejectedAsync(page))
                return Done(UnattendedLoginOutcome.AuthenticationRejected, "The login page rejected the credential. It was not retried.", clock, origin);
            if (!passwordStillVisible && DateTime.UtcNow - submittedAt > TimeSpan.FromMilliseconds(_timeouts.LoginTimeoutMs / 2.0))
                return Done(UnattendedLoginOutcome.SuccessNotConfirmed, "The login page was left, but the configured success URL or marker did not appear.", clock, origin);
            await Task.Delay(200, ct);
        }
    }

    private async Task<UnattendedLoginAttempt> EntraAsync(IPage page, UnattendedLoginSettings settings, UnattendedLoginCredential credential, Stopwatch clock, bool alreadyNavigated, CancellationToken ct)
    {
        var (outcome, message, steps) = await _entra.LoginAsync(page, settings, credential, NavigateAsync, alreadyNavigated, ct);
        return Done(outcome, message, clock, SafeOrigin(settings.LoginUrl)) with { Steps = steps };
    }

    private async Task<UnattendedLoginAttempt?> NavigateAsync(IPage page, string url, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var response = await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = _timeouts.NavigationTimeoutMs });
                if (response is { Status: >= 500 } && attempt == 0) continue;
                if (response is { Status: >= 400 }) return new(UnattendedLoginOutcome.TargetUnavailable, $"The login target answered HTTP {response.Status}.", 0);
                return null;
            }
            catch (TimeoutException) when (attempt == 0) { }
            catch (PlaywrightException) when (attempt == 0) { }
            catch (TimeoutException) { return new(UnattendedLoginOutcome.Timeout, "The login page did not load in time.", 0); }
            catch (PlaywrightException) { return new(UnattendedLoginOutcome.TargetUnavailable, "The login target could not be reached.", 0); }
        }
        return new(UnattendedLoginOutcome.TargetUnavailable, "The login target could not be reached.", 0);
    }

    private static async Task SubmitAsync(IPage page, string submitSelector, string fieldSelector)
    {
        var submit = page.Locator(submitSelector).First;
        if (await VisibleAsync(page, submitSelector)) await submit.ClickAsync(new LocatorClickOptions { Timeout = 5_000, NoWaitAfter = true });
        else await page.Locator(fieldSelector).First.PressAsync("Enter", new LocatorPressOptions { Timeout = 5_000, NoWaitAfter = true });
    }

    internal static async Task<UnattendedLoginOutcome?> ChallengeAsync(IPage page)
    {
        if (await VisibleAsync(page, ChallengeSelector)) return UnattendedLoginOutcome.InteractiveChallengeDetected;
        var text = await BodyTextAsync(page);
        if (ConditionalAccessText.IsMatch(text)) return UnattendedLoginOutcome.ConditionalAccessChallenge;
        if (ChallengeText.IsMatch(text)) return UnattendedLoginOutcome.InteractiveChallengeDetected;
        return null;
    }

    private static async Task<bool> RejectedAsync(IPage page) =>
        RejectionText.IsMatch(await BodyTextAsync(page)) || await VisibleAsync(page, "[role='alert']:not(:empty), .error:not(:empty), .alert-danger:not(:empty)");

    /// <summary>Success needs every configured condition: the URL contains the configured text and/or the authenticated marker is visible.</summary>
    private static async Task<bool> SuccessAsync(IPage page, UnattendedLoginSettings settings)
    {
        var urlOk = string.IsNullOrWhiteSpace(settings.SuccessUrlContains) || page.Url.Contains(settings.SuccessUrlContains, StringComparison.OrdinalIgnoreCase);
        var markerOk = string.IsNullOrWhiteSpace(settings.SuccessSelector) || await VisibleAsync(page, settings.SuccessSelector);
        return urlOk && markerOk;
    }

    internal static async Task<bool> VisibleAsync(IPage page, string selector)
    {
        try { return await page.Locator(selector).First.IsVisibleAsync(); }
        catch (PlaywrightException) { return false; }
    }

    internal static async Task<string> BodyTextAsync(IPage page)
    {
        try
        {
            var text = await page.EvaluateAsync<string>("() => (document.body?.innerText ?? '').slice(0, 6000)");
            return text ?? "";
        }
        catch (PlaywrightException) { return ""; }
    }

    private static async Task ClearSensitiveInputsAsync(IPage page, UnattendedLoginSettings settings)
    {
        try
        {
            var password = page.Locator(Selector(settings.PasswordSelector, DefaultPasswordSelector));
            if (await password.CountAsync() > 0) await password.First.FillAsync("", new LocatorFillOptions { Timeout = 1_000 });
        }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException) { }
    }

    private UnattendedLoginAttempt Done(UnattendedLoginOutcome outcome, string message, Stopwatch clock, string origin)
    {
        logger.LogInformation("Unattended browser login finished. Mode {Mode}; outcome {Outcome}; duration {DurationMs} ms; target {TargetOrigin}",
            "UnattendedBrowser", outcome, clock.ElapsedMilliseconds, origin);
        return new(outcome, message, clock.ElapsedMilliseconds);
    }

    private static string ChallengeMessage(UnattendedLoginOutcome outcome) => outcome == UnattendedLoginOutcome.ConditionalAccessChallenge
        ? "Unattended login blocked: an interactive/conditional access challenge was detected."
        : "Unattended login blocked: MFA or an interactive challenge was detected. Interactive authentication is required.";

    private static string Selector(string? configured, string fallback) => string.IsNullOrWhiteSpace(configured) ? fallback : configured;

    internal static string SafeOrigin(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.GetLeftPart(UriPartial.Authority) : "invalid-target";
}
