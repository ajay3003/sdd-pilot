using System.Collections.Concurrent;
using System.Net;
using System.Text;
using BirkNext.Api.Services.BrowserAuthentication;
using BirkNext.Api.Tests.TestInfrastructure;
using BirkNext.BrowserAuthentication;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Playwright;
using Xunit;

namespace BirkNext.Api.Tests.BrowserAuthentication;

/// <summary>
/// Microsoft Entra ID unattended login against a local fake (loopback only — login.microsoftonline.com is never contacted) with the real
/// headless Chromium. The application and the fake Entra ID sign-in pages are two loopback servers; the runner is told that the second one's
/// authority (localhost:port) is an Entra ID host. The fake pages use the element names of the real sign-in page (loginfmt/passwd/idSIButton9/idBtn_Back/
/// KmsiCheckboxField/tilesHolder/usernameError/passwordError/idDiv_SAOTCAS_Title) including the off-screen password input on the username
/// screen. Gated like the other real-browser tests (RUN_EXTERNAL_FRONTEND_QUALITY_TESTS=true).
/// </summary>
[Trait("Category", "FrontendBrowserRuntimeIntegration")]
public sealed class UnattendedEntraLoginTests : IAsyncLifetime
{
    private const string TestUser = "testbruker01@bufdir.no";
    private const string Password = "fake-entra-password-1";
    private LoopbackHttpTestServer _server = null!, _entra = null!;
    private readonly ConcurrentQueue<string> _passwordPosts = new();
    private readonly ConcurrentQueue<string> _firstAppCookies = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "birknext-entra-fake-" + Guid.NewGuid().ToString("N"));
    private readonly ListLogger<UnattendedLoginRunner> _runnerLog = new();

    public Task InitializeAsync()
    {
        _server = new LoopbackHttpTestServer(c => ServeAsync(c, entra: false));
        _server.Start();
        _entra = new LoopbackHttpTestServer(c => ServeAsync(c, entra: true));
        _entra.Start();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        await _entra.DisposeAsync();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string App(string scenario, string path = "") => $"http://localhost:{_server.Port}/app/{scenario}/{path}";
    private string Entra(string path) => $"http://localhost:{_entra.Port}/fake-tenant/{path}";

    private UnattendedLoginService Service(string scenario, UnattendedLoginProvider provider = UnattendedLoginProvider.MicrosoftEntraId, string password = Password,
        string? protectedResource = null, string? marker = null, int fieldWaitMs = 3_000)
    {
        var store = new UnattendedLoginStore(_dir);
        var runner = new UnattendedLoginRunner(new PlaywrightUnattendedLoginBrowserLauncher(), _runnerLog,
            new UnattendedLoginTimeouts(LoginTimeoutMs: 40_000, NavigationTimeoutMs: 10_000, FieldWaitMs: fieldWaitMs, RedirectWaitMs: 10_000),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { $"localhost:{_entra.Port}" });
        var service = new UnattendedLoginService(store, runner, TimeProvider.System, NullLogger<UnattendedLoginService>.Instance);
        service.Save(new SaveUnattendedLoginRequest
        {
            Provider = provider, LoginName = TestUser, Password = password, LoginUrl = App(scenario), ProtectedResourceUrl = protectedResource,
            SuccessSelector = marker, SuccessUrlContains = provider == UnattendedLoginProvider.Generic ? "/app/" : null,
            IdentityAlias = provider == UnattendedLoginProvider.Generic ? "m2lb-test-user" : null, Enabled = true,
        }).Errors.Should().BeEmpty();
        return service;
    }

    private static UnattendedLoginStepStatus Step(UnattendedLoginValidationResult r, string key) => r.Steps.Single(s => s.Key == key).Status;

    [Fact]
    public async Task UsernamePasswordAndStaySignedIn_Succeeds_EveryStepPassed_Connected()
    {
        if (!ExternalFrontendQualityTestGate.IsEnabled) return;
        var result = await Service("ok", protectedResource: "/app/ok/admin").ValidateAsync(default);

        result.Outcome.Should().Be(UnattendedLoginOutcome.Succeeded, result.Message);
        result.Steps.Select(s => s.Key).Should().Equal("application", "entra-redirect", "login-form", "credential-submission", "authentication", "identity", "protected-resource");
        result.Steps.Should().OnlyContain(s => s.Status == UnattendedLoginStepStatus.Passed, string.Join("; ", result.Steps.Select(s => $"{s.Key}={s.Status}:{s.Detail}")));
        result.View.ConnectionStatus.Should().Be(UnattendedConnectionStatus.Connected);
        result.View.LastValidation!.Steps.Should().HaveCount(7);
        _passwordPosts.Should().ContainSingle("the password is submitted exactly once");
        string.Join("\n", _runnerLog.Messages).Should().NotContain(Password).And.NotContain("app_session");
        result.Steps.Should().OnlyContain(s => !s.Detail.Contains(Password) && !s.Detail.Contains(TestUser), "details show a masked identity only");
    }

    [Fact]
    public async Task SlowSpaRedirect_IsFollowed_ByTheEntraFlow_WhileTheOldGenericFieldWaitGivesUp()
    {
        if (!ExternalFrontendQualityTestGate.IsEnabled) return;
        // Root cause of "Login page not recognized": the application redirects to Entra ID only after its script starts.
        var generic = await Service("slowboot", UnattendedLoginProvider.Generic, fieldWaitMs: 2_000).ValidateAsync(default);
        generic.Outcome.Should().Be(UnattendedLoginOutcome.LoginPageNotRecognized);

        var entra = await Service("slowboot").ValidateAsync(default);
        entra.Outcome.Should().Be(UnattendedLoginOutcome.Succeeded, entra.Message);
    }

    [Fact]
    public async Task GenericProvider_RedirectedToEntra_HandsOverToTheEntraFlow()
    {
        if (!ExternalFrontendQualityTestGate.IsEnabled) return;
        var result = await Service("ok", UnattendedLoginProvider.Generic).ValidateAsync(default);
        result.Outcome.Should().Be(UnattendedLoginOutcome.Succeeded, result.Message);
        Step(result, "entra-redirect").Should().Be(UnattendedLoginStepStatus.Passed);
    }

    [Fact]
    public async Task PasswordOnlyScreen_WithTheTestIdentitySelected_Succeeds()
    {
        if (!ExternalFrontendQualityTestGate.IsEnabled) return;
        var result = await Service("passwordonly").ValidateAsync(default);
        result.Outcome.Should().Be(UnattendedLoginOutcome.Succeeded, result.Message);
        result.Steps.Single(s => s.Key == "login-form").Detail.Should().Contain("already selected");
    }

    [Fact]
    public async Task AccountPicker_ChoosesTheTestIdentity()
    {
        if (!ExternalFrontendQualityTestGate.IsEnabled) return;
        (await Service("picker").ValidateAsync(default)).Outcome.Should().Be(UnattendedLoginOutcome.Succeeded);
    }

    [Fact]
    public async Task ApplicationSignInButton_IsClickedOnce_ThenRedirects()
    {
        if (!ExternalFrontendQualityTestGate.IsEnabled) return;
        (await Service("button").ValidateAsync(default)).Outcome.Should().Be(UnattendedLoginOutcome.Succeeded);
    }

    [Fact]
    public async Task WrongPassword_IsRejected_SubmittedOnce_Failed_NoSecretLogged()
    {
        if (!ExternalFrontendQualityTestGate.IsEnabled) return;
        var result = await Service("ok", password: "wrong-password-7").ValidateAsync(default);

        result.Outcome.Should().Be(UnattendedLoginOutcome.AuthenticationRejected, result.Message);
        result.View.ConnectionStatus.Should().Be(UnattendedConnectionStatus.Failed);
        Step(result, "authentication").Should().Be(UnattendedLoginStepStatus.Failed);
        Step(result, "identity").Should().Be(UnattendedLoginStepStatus.NotReached);
        _passwordPosts.Should().ContainSingle("a rejected password is never retried");
        string.Join("\n", _runnerLog.Messages).Should().NotContain("wrong-password-7");
    }

    [Theory]
    [InlineData("mfa", UnattendedLoginOutcome.InteractiveChallengeDetected)]
    [InlineData("proofup", UnattendedLoginOutcome.InteractiveChallengeDetected)]
    [InlineData("ca", UnattendedLoginOutcome.ConditionalAccessChallenge)]
    public async Task MfaProofUpAndConditionalAccess_StopTheLogin_RequiresInteraction(string scenario, UnattendedLoginOutcome expected)
    {
        if (!ExternalFrontendQualityTestGate.IsEnabled) return;
        var result = await Service(scenario).ValidateAsync(default);

        result.Outcome.Should().Be(expected, result.Message);
        result.View.ConnectionStatus.Should().Be(UnattendedConnectionStatus.RequiresInteraction);
        Step(result, "authentication").Should().Be(UnattendedLoginStepStatus.RequiresInteraction);
        result.Recommendation.Should().Contain("attended login");
        _passwordPosts.Should().ContainSingle();
    }

    [Fact]
    public async Task PasskeyOnly_IsUnsupported_NotConnected()
    {
        if (!ExternalFrontendQualityTestGate.IsEnabled) return;
        var result = await Service("passkey").ValidateAsync(default);
        result.Outcome.Should().Be(UnattendedLoginOutcome.UnsupportedAuthenticationFlow);
        result.View.ConnectionStatus.Should().Be(UnattendedConnectionStatus.Failed);
        _passwordPosts.Should().BeEmpty();
    }

    [Fact]
    public async Task ApplicationWithoutEntraRedirect_IsNeverConnected()
    {
        if (!ExternalFrontendQualityTestGate.IsEnabled) return;
        var result = await Service("noredirect").ValidateAsync(default);
        result.Outcome.Should().Be(UnattendedLoginOutcome.EntraRedirectNotDetected);
        result.View.ConnectionStatus.Should().NotBe(UnattendedConnectionStatus.Connected);
        Step(result, "application").Should().Be(UnattendedLoginStepStatus.Passed);
        Step(result, "entra-redirect").Should().Be(UnattendedLoginStepStatus.Failed);
    }

    [Fact]
    public async Task OtherAccountSignedIn_IsIdentityMismatch_NotConnected()
    {
        if (!ExternalFrontendQualityTestGate.IsEnabled) return;
        var result = await Service("otheruser").ValidateAsync(default);
        result.Outcome.Should().Be(UnattendedLoginOutcome.IdentityMismatch, result.Message);
        result.View.ConnectionStatus.Should().Be(UnattendedConnectionStatus.Failed);
        Step(result, "authentication").Should().Be(UnattendedLoginStepStatus.Passed, "the URL is back on the application — which alone is not success");
    }

    [Fact]
    public async Task IdentityFromTheApplicationsMsalAccount_IsVerified()
    {
        if (!ExternalFrontendQualityTestGate.IsEnabled) return;
        (await Service("msal").ValidateAsync(default)).Outcome.Should().Be(UnattendedLoginOutcome.Succeeded);
    }

    [Fact]
    public async Task IdentityNotShown_IsNotVerified()
    {
        if (!ExternalFrontendQualityTestGate.IsEnabled) return;
        var result = await Service("anonymoushome").ValidateAsync(default);
        result.Outcome.Should().Be(UnattendedLoginOutcome.IdentityNotVerified);
        result.View.ConnectionStatus.Should().Be(UnattendedConnectionStatus.Failed);
    }

    [Fact]
    public async Task ProtectedResourceDenied_IsNotConnected()
    {
        if (!ExternalFrontendQualityTestGate.IsEnabled) return;
        var result = await Service("ok", protectedResource: "/app/ok/forbidden").ValidateAsync(default);
        result.Outcome.Should().Be(UnattendedLoginOutcome.ProtectedResourceDenied, result.Message);
        Step(result, "identity").Should().Be(UnattendedLoginStepStatus.Passed);
        Step(result, "protected-resource").Should().Be(UnattendedLoginStepStatus.Failed);
        result.View.ConnectionStatus.Should().Be(UnattendedConnectionStatus.Failed);
    }

    [Fact]
    public async Task ExistingInteractiveSession_IsNeverUsed()
    {
        if (!ExternalFrontendQualityTestGate.IsEnabled) return;
        using var playwright = await Playwright.CreateAsync();
        await using var attendedBrowser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var attended = await attendedBrowser.NewContextAsync();
        await attended.AddCookiesAsync([new Microsoft.Playwright.Cookie { Name = "app_ok", Value = "ok", Url = $"http://localhost:{_server.Port}/" }]);
        await (await attended.NewPageAsync()).GotoAsync(App("ok"));
        _firstAppCookies.Should().Contain(c => c.Contains("app_ok"), "the interactive context is signed in");
        _firstAppCookies.Clear();

        var result = await Service("ok").ValidateAsync(default);

        result.Outcome.Should().Be(UnattendedLoginOutcome.Succeeded);
        _firstAppCookies.First().Should().NotContain("app_ok", "the unattended context starts empty and must sign in itself");
        _passwordPosts.Should().ContainSingle();
    }

    // ── Fake application (localhost) + fake Entra ID (127.0.0.1) ───────────────────────────────────────────────────────────────

    private async Task ServeAsync(HttpListenerContext c, bool entra)
    {
        var parts = c.Request.Url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var cookies = c.Request.Headers["Cookie"] ?? "";
        if (!entra && parts is ["app", var app, ..])
        {
            var s = app;
            var rest = parts.Length > 2 ? parts[2] : "";
            var signedIn = cookies.Contains($"app_{s}=ok");
            if (rest == "" && !_firstAppCookies.Any(_ => true)) _firstAppCookies.Enqueue(cookies);
            var authorize = Entra($"oauth2/v2.0/authorize?s={s}");
            switch (rest)
            {
                case "callback":
                    c.Response.Headers.Add("Set-Cookie", $"app_{s}=ok; Path=/; HttpOnly");
                    c.Response.Redirect(App(s)); c.Response.Close(); return;
                case "forbidden":
                    c.Response.StatusCode = 403; await Html(c, "<html><body>Forbidden</body></html>"); return;
                case "admin":
                    if (!signedIn) { c.Response.Redirect(authorize); c.Response.Close(); return; }
                    await Html(c, $"<html><body><header id=\"user-menu\">{TestUser}</header><h1>Administration</h1></body></html>"); return;
            }
            if (s == "noredirect") { await Html(c, "<html><body><h1>M2LB</h1><p>Velkommen</p></body></html>"); return; }
            if (!signedIn)
            {
                var html = s == "button"
                    ? $"<html><body><h1>M2LB</h1><button onclick=\"location.href='{authorize}'\">Logg inn</button></body></html>"
                    : $"<html><body><div id=\"app\">Loading…</div><script>setTimeout(() => location.href = '{authorize}', {(s == "slowboot" ? 6000 : 600)});</script></body></html>";
                await Html(c, html); return;
            }
            var shown = s switch { "otheruser" => "annen.bruker@bufdir.no", "msal" or "anonymoushome" => "", _ => TestUser };
            var storage = s switch
            {
                "otheruser" => "localStorage.setItem('x.login.windows.net-acc', JSON.stringify({ homeAccountId: 'h1', username: 'annen.bruker@bufdir.no' }));",
                "msal" => $"sessionStorage.setItem('h2.login.windows.net-acc', JSON.stringify({{ homeAccountId: 'h2', localAccountId: 'l2', username: '{TestUser}' }}));",
                _ => "",
            };
            await Html(c, $"<html><body><header id=\"user-menu\">{shown}</header><main>Dashboard</main><script>{storage}</script></body></html>");
            return;
        }
        if (entra && parts is ["fake-tenant", ..])
        {
            var s = c.Request.QueryString["s"] ?? "ok";
            var step = parts.Length > 1 ? parts[^1] : "";
            if (step == "authorize")
            {
                if (s == "passwordonly") { await Html(c, PasswordScreen(s, "")); return; }
                if (s == "passkey") { await Html(c, "<html><body><div id=\"fidoDialogTitle\">Logg på med en tilgangsnøkkel</div><p>Bruk sikkerhetsnøkkelen.</p></body></html>"); return; }
                if (s == "picker")
                {
                    await Html(c, $"""
                        <html><body><div id="loginHeader">Velg en konto</div><div id="tilesHolder">
                        <div class="table" role="button" data-test-id="{TestUser}" onclick="location.href='{Entra($"password?s={s}")}'">{TestUser}</div>
                        <div id="otherTile" role="button" onclick="location.href='{Entra($"authorize?s=ok")}'">Bruk en annen konto</div></div></body></html>
                        """); return;
                }
                await Html(c, UsernameScreen(s, "")); return;
            }
            if (step == "password") { await Html(c, PasswordScreen(s, "")); return; }
            if (step == "kmsi") { c.Response.Redirect(App(s, "callback")); c.Response.Close(); return; }
            if (step == "login" && c.Request.HttpMethod == "POST")
            {
                var form = await new StreamReader(c.Request.InputStream).ReadToEndAsync();
                var fields = form.Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => WebUtility.UrlDecode(p[0]), p => p.Length > 1 ? WebUtility.UrlDecode(p[1]) : "");
                var login = fields.GetValueOrDefault("loginfmt") ?? "";
                var password = fields.GetValueOrDefault("passwd") ?? "";
                if (password.Length == 0)
                {
                    await Html(c, login == TestUser ? PasswordScreen(s, "") : UsernameScreen(s, "Vi finner ingen konto med det brukernavnet.")); return;
                }
                _passwordPosts.Enqueue(s);
                if (password != Password) { await Html(c, PasswordScreen(s, "Passordet er feil. Prøv på nytt.")); return; }
                switch (s)
                {
                    case "mfa": await Html(c, "<html><body><div id=\"idDiv_SAOTCAS_Title\">Godkjenn påloggingsforespørsel</div><p>Åpne Authenticator-appen.</p></body></html>"); return;
                    case "proofup": await Html(c, "<html><body><div>Mer informasjon kreves</div><p>Organisasjonen trenger mer informasjon for å holde kontoen sikker.</p></body></html>"); return;
                    case "ca": await Html(c, "<html><body><h1>You can't get there from here</h1><p>This application contains sensitive information and can only be accessed from managed devices.</p></body></html>"); return;
                }
                await Html(c, $"""
                    <html><body><div id="KmsiCheckboxField"></div><div id="idDiv_KMSI_Title">Vil du forbli pålogget?</div>
                    <input type="button" id="idBtn_Back" value="Nei" onclick="location.href='{Entra($"kmsi?s={s}")}'"><input type="submit" id="idSIButton9" value="Ja"></body></html>
                    """);
                return;
            }
        }
        c.Response.StatusCode = 404;
        await Html(c, "<html><body>Not found</body></html>");
    }

    private string UsernameScreen(string s, string error) => $"""
        <html><body><form id="f1" method="post" action="{Entra($"login?s={s}")}"><div id="loginHeader">Logg på</div>
        <input type="email" name="loginfmt" id="i0116" placeholder="E-post, telefon eller Skype">
        <input type="password" name="passwd" class="moveOffScreen" tabindex="-1" aria-hidden="true" style="position:absolute;left:-10000px;top:-10000px">
        <div id="usernameError">{error}</div><input type="submit" id="idSIButton9" value="Neste"></form></body></html>
        """;

    private string PasswordScreen(string s, string error) => $"""
        <html><body><form id="f1" method="post" action="{Entra($"login?s={s}")}"><input type="hidden" name="loginfmt" value="{TestUser}">
        <div id="displayName">{TestUser}</div><div id="loginHeader">Skriv inn passord</div><div id="passwordError">{error}</div>
        <input type="password" name="passwd" id="i0118" placeholder="Passord"><a id="idA_PWD_ForgotPassword" href="#">Glemt passordet?</a>
        <input type="submit" id="idSIButton9" value="Logg på"></form></body></html>
        """;

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
