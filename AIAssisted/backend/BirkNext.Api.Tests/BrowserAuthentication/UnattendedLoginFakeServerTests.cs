using System.Collections.Concurrent;
using System.Net;
using System.Text;
using BirkNext.Api.Services.AuthenticatedReview;
using BirkNext.Api.Services.BrowserAuthentication;
using BirkNext.Api.Services.FrontendBrowserRuntime;
using BirkNext.Api.Tests.TestInfrastructure;
using BirkNext.BrowserAuthentication;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Playwright;
using Moq;
using Xunit;
using static BirkNext.Api.Tests.BrowserAuthentication.UnattendedLoginConfigurationTests;

namespace BirkNext.Api.Tests.BrowserAuthentication;

/// <summary>
/// Unattended login against a local fake login server (loopback only — no real identity provider is contacted) with the real headless
/// Chromium. Gated like the other real-browser tests (RUN_EXTERNAL_FRONTEND_QUALITY_TESTS=true).
/// Fake routes: /ok (password login → /ok/app with the signed-in marker), /twostep (name first, then password), /mfa (one-time code page),
/// /ca (conditional access block), /slow (never answers in time), /plain (no login form), /nomarker (logs in but shows no marker).
/// </summary>
[Trait("Category", "FrontendBrowserRuntimeIntegration")]
public sealed class UnattendedLoginFakeServerTests : IAsyncLifetime
{
    private LoopbackHttpTestServer _server = null!;
    private readonly ConcurrentQueue<string> _posts = new();
    private readonly ConcurrentQueue<string> _loginCookies = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "birknext-unattended-fake-" + Guid.NewGuid().ToString("N"));
    private readonly ListLogger<UnattendedLoginRunner> _runnerLog = new();

    public Task InitializeAsync()
    {
        _server = new LoopbackHttpTestServer(ServeAsync);
        _server.Start();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private UnattendedLoginService Service(string route, int loginTimeoutMs = 15_000, string? successUrl = "/app", string? marker = "#signed-in")
    {
        var store = new UnattendedLoginStore(_dir);
        var runner = new UnattendedLoginRunner(new PlaywrightUnattendedLoginBrowserLauncher(), _runnerLog,
            new UnattendedLoginTimeouts(LoginTimeoutMs: loginTimeoutMs, NavigationTimeoutMs: Math.Min(loginTimeoutMs, 10_000), FieldWaitMs: 3_000));
        var service = new UnattendedLoginService(store, runner, TimeProvider.System, NullLogger<UnattendedLoginService>.Instance);
        service.Save(new SaveUnattendedLoginRequest
        {
            IdentityAlias = "fake-automation-identity", LoginName = LoginName, Password = Secret, LoginUrl = _server.Url($"/{route}/login"),
            SuccessUrlContains = successUrl, SuccessSelector = marker, Enabled = true,
        }).Errors.Should().BeEmpty();
        return service;
    }

    [Fact]
    public async Task ValidLogin_SucceedsWithoutInteraction_IsReady_AndTheConsumerContinuesOnTheAuthenticatedPage()
    {
        if (!ExternalFrontendQualityTestGate.IsEnabled) return;
        var service = Service("ok");

        var validation = await service.ValidateAsync(default);

        validation.Outcome.Should().Be(UnattendedLoginOutcome.Succeeded, validation.Message);
        validation.View.State.Should().Be(UnattendedLoginState.Ready);
        validation.View.LastValidation!.Current.Should().BeTrue();
        var run = await service.RunAuthenticatedAsync(async (page, _) => await page.Locator("#signed-in").InnerTextAsync(), default);
        run.Outcome.Should().Be(UnattendedLoginOutcome.Succeeded);
        run.Result.Should().Be("Signed in as the automation identity");
        _posts.Should().HaveCount(2, "one validation login and one consumer login; each submits exactly once");
    }

    [Fact]
    public async Task TwoStepLogin_SubmitsTheNameThenThePassword()
    {
        if (!ExternalFrontendQualityTestGate.IsEnabled) return;
        (await Service("twostep").ValidateAsync(default)).Outcome.Should().Be(UnattendedLoginOutcome.Succeeded);
    }

    [Fact]
    public async Task InvalidCredential_IsAuthenticationRejected_SubmittedOnce_AndNoSecretLogged()
    {
        if (!ExternalFrontendQualityTestGate.IsEnabled) return;
        var service = Service("ok");
        service.Save(new SaveUnattendedLoginRequest
        {
            IdentityAlias = "fake-automation-identity", LoginName = LoginName, Password = "wrong-password-9", LoginUrl = _server.Url("/ok/login"),
            SuccessUrlContains = "/app", SuccessSelector = "#signed-in",
        });

        var result = await service.ValidateAsync(default);

        result.Outcome.Should().Be(UnattendedLoginOutcome.AuthenticationRejected);
        result.View.State.Should().Be(UnattendedLoginState.ValidationFailed);
        result.Recommendation.Should().Contain("does not retry");
        _posts.Should().ContainSingle("a rejected credential is never retried");
        string.Join("\n", _runnerLog.Messages).Should().NotContain("wrong-password-9").And.NotContain(Secret).And.NotContain(LoginName);
        result.Message.Should().NotContain("wrong-password-9");
    }

    [Fact]
    public async Task MfaChallenge_StopsTheLogin_InteractiveAuthenticationRequired()
    {
        if (!ExternalFrontendQualityTestGate.IsEnabled) return;
        var result = await Service("mfa").ValidateAsync(default);

        result.Outcome.Should().Be(UnattendedLoginOutcome.InteractiveChallengeDetected);
        result.Message.Should().Contain("Unattended login blocked").And.Contain("Interactive authentication is required");
        result.Recommendation.Should().Contain("Use attended login");
        _posts.Should().ContainSingle("the one-time code page is never answered");
    }

    [Fact]
    public async Task ConditionalAccess_IsReportedAsItsOwnChallenge()
    {
        if (!ExternalFrontendQualityTestGate.IsEnabled) return;
        (await Service("ca").ValidateAsync(default)).Outcome.Should().Be(UnattendedLoginOutcome.ConditionalAccessChallenge);
    }

    [Fact]
    public async Task SlowLoginTarget_TimesOut_WithinTheBound()
    {
        if (!ExternalFrontendQualityTestGate.IsEnabled) return;
        var started = DateTime.UtcNow;
        var result = await Service("slow", loginTimeoutMs: 3_000).ValidateAsync(default);

        result.Outcome.Should().BeOneOf(UnattendedLoginOutcome.Timeout, UnattendedLoginOutcome.TargetUnavailable);
        (DateTime.UtcNow - started).Should().BeLessThan(TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task PageWithoutLoginForm_IsLoginPageNotRecognized()
    {
        if (!ExternalFrontendQualityTestGate.IsEnabled) return;
        (await Service("plain").ValidateAsync(default)).Outcome.Should().Be(UnattendedLoginOutcome.LoginPageNotRecognized);
    }

    [Fact]
    public async Task MissingSuccessMarker_IsSuccessNotConfirmed_NeverReady()
    {
        if (!ExternalFrontendQualityTestGate.IsEnabled) return;
        var result = await Service("nomarker", loginTimeoutMs: 4_000, successUrl: null).ValidateAsync(default);

        result.Outcome.Should().BeOneOf(UnattendedLoginOutcome.SuccessNotConfirmed, UnattendedLoginOutcome.Timeout);
        result.View.State.Should().Be(UnattendedLoginState.ValidationFailed);
    }

    [Fact]
    public async Task UnattendedContext_NeverSeesAttendedCookies()
    {
        if (!ExternalFrontendQualityTestGate.IsEnabled) return;
        // An attended-like session: another browser context that holds a session cookie for the same site.
        using var playwright = await Playwright.CreateAsync();
        await using var attendedBrowser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var attended = await attendedBrowser.NewContextAsync();
        await attended.AddCookiesAsync([new Microsoft.Playwright.Cookie { Name = "attended_session", Value = "human-signed-in", Url = _server.Url("/") }]);
        var attendedPage = await attended.NewPageAsync();
        await attendedPage.GotoAsync(_server.Url("/ok/login"));
        _loginCookies.Should().Contain(c => c.Contains("attended_session"), "the attended context does carry its cookie");
        _loginCookies.Clear();

        (await Service("ok").ValidateAsync(default)).Outcome.Should().Be(UnattendedLoginOutcome.Succeeded);

        _loginCookies.Should().NotBeEmpty().And.OnlyContain(c => !c.Contains("attended_session"), "the unattended context starts empty");
    }

    [Fact]
    public async Task BrowserRuntime_UnattendedMode_ObservesTheAuthenticatedPage_AndNeverUsesTheAttendedSession()
    {
        if (!ExternalFrontendQualityTestGate.IsEnabled) return;
        var service = Service("ok");
        (await service.ValidateAsync(default)).Outcome.Should().Be(UnattendedLoginOutcome.Succeeded);
        var attended = new Mock<IAuthenticatedBrowserSessionManager>(MockBehavior.Strict);
        var runtime = Runtime(attended.Object, service);

        var result = await runtime.ReviewAsync(new BrowserRuntimeExecutionRequest(_server.Url("/ok/app"), BrowserRuntimeExecutionMode.UnattendedLoginPage));

        result.Status.Should().Be(BrowserRuntimeEngineStatus.Assessed, result.EngineError);
        result.ExecutionMode.Should().Be(BrowserRuntimeExecutionMode.UnattendedLoginPage);
        result.Limitations.Should().Contain(l => l.StartsWith("Unattended login with the automation identity"));
        attended.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task BrowserRuntime_UnattendedModeNotReady_FailsClearly_WithoutAttendedFallback()
    {
        if (!ExternalFrontendQualityTestGate.IsEnabled) return;
        var service = Service("ok"); // saved but not validated → Not verified
        var attended = new Mock<IAuthenticatedBrowserSessionManager>(MockBehavior.Strict);

        var result = await Runtime(attended.Object, service).ReviewAsync(new BrowserRuntimeExecutionRequest(_server.Url("/ok/app"), BrowserRuntimeExecutionMode.UnattendedLoginPage));

        result.Status.Should().Be(BrowserRuntimeEngineStatus.Skipped);
        result.OutcomeReason.Should().Be(BrowserRuntimeOutcomeReason.AuthenticationRequired);
        result.EngineError.Should().Contain("Unattended login");
        _posts.Should().BeEmpty("no login is attempted before the capability is Ready");
        attended.VerifyNoOtherCalls();
    }

    private static FrontendBrowserRuntimeReviewService Runtime(IAuthenticatedBrowserSessionManager attended, UnattendedLoginService unattended)
    {
        var resources = new BrowserResourceClassifier();
        return new FrontendBrowserRuntimeReviewService(NullLogger<FrontendBrowserRuntimeReviewService>.Instance, new BrowserTargetValidator(allowLoopback: true),
            new BrowserRuntimeFindingClassifier(resources), resources, new BrowserEvidenceSanitizer(),
            Microsoft.Extensions.Options.Options.Create(new FrontendBrowserRuntimeOptions { Enabled = true, }) , attended, unattended);
    }

    // ── Fake login server ─────────────────────────────────────────────────────────────────────────────────────────────

    private const string LoginForm = """
        <html><body><h1>Sign in</h1>{0}
        <form method="post"><label>User name <input name="username" autocomplete="username"></label>
        <label>Password <input type="password" name="password"></label><button type="submit">Sign in</button></form></body></html>
        """;

    private async Task ServeAsync(HttpListenerContext context)
    {
        var path = context.Request.Url!.AbsolutePath;
        var route = path.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        var isLogin = path.EndsWith("/login", StringComparison.Ordinal);
        if (isLogin && context.Request.HttpMethod == "GET") _loginCookies.Enqueue(context.Request.Headers["Cookie"] ?? "");

        if (route == "slow") { await Task.Delay(TimeSpan.FromSeconds(12)); await Html(context, "<html><body>late</body></html>"); return; }
        if (route == "plain") { await Html(context, "<html><body><h1>Welcome</h1><p>No sign-in here.</p></body></html>"); return; }

        if (isLogin && context.Request.HttpMethod == "POST")
        {
            var form = await new StreamReader(context.Request.InputStream).ReadToEndAsync();
            var fields = form.Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => WebUtility.UrlDecode(p[0]), p => p.Length > 1 ? WebUtility.UrlDecode(p[1]) : "");
            if (route == "twostep" && !fields.ContainsKey("password"))
            {
                await Html(context, $"""<html><body><form method="post"><input type="hidden" name="username" value="{WebUtility.HtmlEncode(fields.GetValueOrDefault("username"))}"><label>Password <input type="password" name="password"></label><button type="submit">Sign in</button></form></body></html>""");
                return;
            }
            _posts.Enqueue(route);
            var valid = fields.GetValueOrDefault("username") == LoginName && fields.GetValueOrDefault("password") == Secret;
            if (!valid) { await Html(context, string.Format(LoginForm, "<p role=\"alert\">Incorrect user name or password.</p>")); return; }
            switch (route)
            {
                case "mfa":
                    await Html(context, "<html><body><h1>Verify your identity</h1><p>Enter the verification code from your authenticator app.</p><input autocomplete=\"one-time-code\" name=\"otc\"><button>Verify</button></body></html>");
                    return;
                case "ca":
                    await Html(context, "<html><body><h1>You cannot access this right now</h1><p>Your sign-in was blocked by your organization's conditional access policy.</p></body></html>");
                    return;
                case "nomarker":
                    context.Response.Redirect($"/{route}/home");
                    context.Response.Close();
                    return;
                default:
                    context.Response.Headers.Add("Set-Cookie", "fake_session=automation; Path=/; HttpOnly");
                    context.Response.Redirect($"/{route}/app");
                    context.Response.Close();
                    return;
            }
        }
        if (isLogin) { await Html(context, route == "twostep"
            ? """<html><body><form method="post"><label>Email <input type="email" name="username"></label><button type="submit">Next</button></form></body></html>"""
            : string.Format(LoginForm, "")); return; }
        if (path.EndsWith("/app", StringComparison.Ordinal)) { await Html(context, "<html><body><div id=\"signed-in\">Signed in as the automation identity</div></body></html>"); return; }
        await Html(context, "<html><body><p>Home</p></body></html>");
    }

    private static async Task Html(HttpListenerContext context, string html)
    {
        try
        {
            context.Response.ContentType = "text/html; charset=utf-8";
            var bytes = Encoding.UTF8.GetBytes(html);
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        }
        catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException) { }
    }
}
