using BirkNext.Api.Services.AuthenticatedReview;
using BirkNext.Api.Services.FrontendBrowserRuntime;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace BirkNext.Api.Tests.Services.AuthenticatedReview;

[Trait("Category", "AuthenticatedReviewPhaseA3RealAcceptance")]
public sealed class AuthenticatedReviewPhaseA3RealAcceptanceTests
{
    [Fact]
    public async Task AuthenticatedRuntime_ReusesExactSamePageAndContext_NoSecondBrowser()
    {
        if (!Enabled()) return;
        await using var fixture = await SyntheticFixture.StartAsync();
        await using var manager = CreateManager();
        var session = await StartAndAuthenticateAsync(manager, fixture);

        // Acquire auth lease and store references
        await using var authLease = await manager.AcquireAuthenticationPageLeaseAsync(session.SessionId, Review, Profile, fixture.TargetUrl);
        await authLease.Page.ClickAsync("#synthetic-sign-in");
        await WaitForStatusAsync(manager, session.SessionId, AuthenticatedBrowserSessionStatus.AwaitingUserContinuation);
        await authLease.Page.ClickAsync("#synthetic-continue");
        await WaitForStatusAsync(manager, session.SessionId, AuthenticatedBrowserSessionStatus.Authenticated);

        var authPageRef = authLease.Page;
        var authContextRef = authLease.Context;

        // Run browser runtime on authenticated page
        var resourceClassifier = new BrowserResourceClassifier();
        var options = Options.Create(new FrontendBrowserRuntimeOptions { Enabled = true });
        var runtime = new FrontendBrowserRuntimeReviewService(
            NullLogger<FrontendBrowserRuntimeReviewService>.Instance,
            new BrowserTargetValidator(),
            new BrowserRuntimeFindingClassifier(resourceClassifier),
            resourceClassifier,
            new BrowserEvidenceSanitizer(),
            options,
            manager);

        var runtimeRequest = new BrowserRuntimeExecutionRequest(
            fixture.TargetUrl,
            BrowserRuntimeExecutionMode.AuthenticatedSessionPage,
            Review,
            Profile,
            session.SessionId);

        var runtimeResult = await runtime.ReviewAsync(runtimeRequest);

        // Verify runtime succeeded and found the fixture error
        runtimeResult.Status.Should().Be(BrowserRuntimeEngineStatus.Assessed);
        runtimeResult.Findings.Should().NotBeEmpty();

        // Verify page/context are still the same after runtime
        await using var freshlease = await manager.AcquirePageLeaseAsync(session.SessionId, Review, Profile, fixture.TargetUrl);
        freshlease.Page.Should().BeSameAs(authPageRef);
        freshlease.Context.Should().BeSameAs(authContextRef);

        // Verify session still authenticated
        var finalStatus = await manager.GetStatusAsync(session.SessionId, Review, Profile);
        finalStatus.Should().NotBeNull();
        finalStatus!.Status.Should().Be(AuthenticatedBrowserSessionStatus.Authenticated);
    }

    [Fact]
    public async Task AuthenticatedRuntime_SensitiveSentinels_NeverAppearInResult()
    {
        if (!Enabled()) return;
        await using var fixture = await SyntheticFixture.StartAsync();
        await using var manager = CreateManager();
        var session = await ReachAuthenticatedAsync(manager, fixture);

        var rc_sent = new BrowserResourceClassifier();
        var options_sent = Options.Create(new FrontendBrowserRuntimeOptions { Enabled = true });
        var runtime = new FrontendBrowserRuntimeReviewService(
            NullLogger<FrontendBrowserRuntimeReviewService>.Instance,
            new BrowserTargetValidator(),
            new BrowserRuntimeFindingClassifier(rc_sent),
            rc_sent,
            new BrowserEvidenceSanitizer(),
            options_sent,
            manager);

        var runtimeRequest = new BrowserRuntimeExecutionRequest(
            fixture.TargetUrl,
            BrowserRuntimeExecutionMode.AuthenticatedSessionPage,
            Review,
            Profile,
            session.SessionId);

        var runtimeResult = await runtime.ReviewAsync(runtimeRequest);

        // Serialize and check for sentinel values
        var serialized = System.Text.Json.JsonSerializer.Serialize(runtimeResult);
        serialized.Should().NotContain("user@example.test");
        serialized.Should().NotContain("case-sensitive-sentinel");
        serialized.Should().NotContain("SECRET_CODE");
        serialized.Should().NotContain("Authorization");
    }

    [Fact]
    public async Task AuthenticatedRuntime_EntraRedirectMidRun_StopsWithNoEvidence()
    {
        if (!Enabled()) return;
        await using var fixture = await SyntheticFixture.StartAsync();
        await using var manager = CreateManager();
        var session = await ReachAuthenticatedAsync(manager, fixture);

        var rc3 = new BrowserResourceClassifier();
        var options3 = Options.Create(new FrontendBrowserRuntimeOptions { Enabled = true });
        var runtime = new FrontendBrowserRuntimeReviewService(
            NullLogger<FrontendBrowserRuntimeReviewService>.Instance,
            new BrowserTargetValidator(),
            new BrowserRuntimeFindingClassifier(rc3),
            rc3,
            new BrowserEvidenceSanitizer(),
            options3,
            manager);

        var runtimeTask = Task.Run(async () =>
        {
            var request = new BrowserRuntimeExecutionRequest(
                fixture.TargetUrl,
                BrowserRuntimeExecutionMode.AuthenticatedSessionPage,
                Review,
                Profile,
                session.SessionId,
                new BrowserRuntimeOptions(StartupObservationMs: 100));
            return await runtime.ReviewAsync(request);
        });

        // Let runtime start, then redirect
        await Task.Delay(50);
        await using var lease = await manager.AcquireAuthenticationPageLeaseAsync(session.SessionId, Review, Profile, fixture.TargetUrl);
        await lease.Page.GotoAsync(fixture.EntraUrl);

        var result = await runtimeTask;
        result.OutcomeReason.Should().Be(BrowserRuntimeOutcomeReason.AuthenticationExpired);
        result.Findings.Should().BeEmpty();
    }

    [Fact]
    public async Task AuthenticatedRuntime_McasRedirectMidRun_StopsWithNoEvidence()
    {
        if (!Enabled()) return;
        await using var fixture = await SyntheticFixture.StartAsync();
        await using var manager = CreateManager();
        var session = await ReachAuthenticatedAsync(manager, fixture);

        var rc4 = new BrowserResourceClassifier();
        var options4 = Options.Create(new FrontendBrowserRuntimeOptions { Enabled = true });
        var runtime = new FrontendBrowserRuntimeReviewService(
            NullLogger<FrontendBrowserRuntimeReviewService>.Instance,
            new BrowserTargetValidator(),
            new BrowserRuntimeFindingClassifier(rc4),
            rc4,
            new BrowserEvidenceSanitizer(),
            options4,
            manager);

        var runtimeTask = Task.Run(async () =>
        {
            var request = new BrowserRuntimeExecutionRequest(
                fixture.TargetUrl,
                BrowserRuntimeExecutionMode.AuthenticatedSessionPage,
                Review,
                Profile,
                session.SessionId,
                new BrowserRuntimeOptions(StartupObservationMs: 100));
            return await runtime.ReviewAsync(request);
        });

        await Task.Delay(50);
        await using var lease = await manager.AcquireAuthenticationPageLeaseAsync(session.SessionId, Review, Profile, fixture.TargetUrl);
        await lease.Page.GotoAsync(fixture.McasOrigin + "/notice");

        var result = await runtimeTask;
        result.OutcomeReason.Should().Be(BrowserRuntimeOutcomeReason.AuthenticationExpired);
        result.Findings.Should().BeEmpty();
    }

    [Fact]
    public async Task AuthenticatedRuntime_UnexpectedOriginMidRun_StopsWithNoEvidence()
    {
        if (!Enabled()) return;
        await using var fixture = await SyntheticFixture.StartAsync();
        await using var manager = CreateManager();
        var session = await ReachAuthenticatedAsync(manager, fixture);

        var rc5 = new BrowserResourceClassifier();
        var options5 = Options.Create(new FrontendBrowserRuntimeOptions { Enabled = true });
        var runtime = new FrontendBrowserRuntimeReviewService(
            NullLogger<FrontendBrowserRuntimeReviewService>.Instance,
            new BrowserTargetValidator(),
            new BrowserRuntimeFindingClassifier(rc5),
            rc5,
            new BrowserEvidenceSanitizer(),
            options5,
            manager);

        var runtimeTask = Task.Run(async () =>
        {
            var request = new BrowserRuntimeExecutionRequest(
                fixture.TargetUrl,
                BrowserRuntimeExecutionMode.AuthenticatedSessionPage,
                Review,
                Profile,
                session.SessionId,
                new BrowserRuntimeOptions(StartupObservationMs: 100));
            return await runtime.ReviewAsync(request);
        });

        await Task.Delay(50);
        await using var lease = await manager.AcquireAuthenticationPageLeaseAsync(session.SessionId, Review, Profile, fixture.TargetUrl);
        await lease.Page.GotoAsync(fixture.UnexpectedOrigin);

        var result = await runtimeTask;
        result.OutcomeReason.Should().Be(BrowserRuntimeOutcomeReason.UnexpectedOrigin);
        result.Findings.Should().BeEmpty();
    }

    [Fact]
    public async Task AuthenticatedRuntime_MissingSessionId_Rejected()
    {
        if (!Enabled()) return;
        await using var fixture = await SyntheticFixture.StartAsync();
        var rc6 = new BrowserResourceClassifier();
        var options6 = Options.Create(new FrontendBrowserRuntimeOptions { Enabled = true });
        var runtime = new FrontendBrowserRuntimeReviewService(
            NullLogger<FrontendBrowserRuntimeReviewService>.Instance,
            new BrowserTargetValidator(),
            new BrowserRuntimeFindingClassifier(rc6),
            rc6,
            new BrowserEvidenceSanitizer(),
            options6,
            CreateManager());

        var request = new BrowserRuntimeExecutionRequest(
            fixture.TargetUrl,
            BrowserRuntimeExecutionMode.AuthenticatedSessionPage,
            Review,
            Profile,
            "");

        var result = await runtime.ReviewAsync(request);
        result.Status.Should().Be(BrowserRuntimeEngineStatus.Skipped);
        result.OutcomeReason.Should().Be(BrowserRuntimeOutcomeReason.AuthenticationRequired);
    }

    [Fact]
    public async Task AuthenticatedRuntime_ExpiredSession_Rejected()
    {
        if (!Enabled()) return;
        await using var fixture = await SyntheticFixture.StartAsync();
        await using var manager = CreateManager();
        var session = await StartAndAuthenticateAsync(manager, fixture);

        // Immediately expire by cancelling
        await manager.CancelAsync(session.SessionId, Review, Profile);

        var rc7 = new BrowserResourceClassifier();
        var options7 = Options.Create(new FrontendBrowserRuntimeOptions { Enabled = true });
        var runtime = new FrontendBrowserRuntimeReviewService(
            NullLogger<FrontendBrowserRuntimeReviewService>.Instance,
            new BrowserTargetValidator(),
            new BrowserRuntimeFindingClassifier(rc7),
            rc7,
            new BrowserEvidenceSanitizer(),
            options7,
            manager);

        var request = new BrowserRuntimeExecutionRequest(
            fixture.TargetUrl,
            BrowserRuntimeExecutionMode.AuthenticatedSessionPage,
            Review,
            Profile,
            session.SessionId);

        var result = await runtime.ReviewAsync(request);
        result.Status.Should().Be(BrowserRuntimeEngineStatus.Skipped);
        result.OutcomeReason.Should().Be(BrowserRuntimeOutcomeReason.AuthenticationCancelled);
    }

    [Fact]
    public async Task AnonymousRuntime_StillOwnsItsOwnBrowser_Unaffected()
    {
        if (!Enabled()) return;
        var rc8 = new BrowserResourceClassifier();
        var options8 = Options.Create(new FrontendBrowserRuntimeOptions { Enabled = true });
        var runtime = new FrontendBrowserRuntimeReviewService(
            NullLogger<FrontendBrowserRuntimeReviewService>.Instance,
            new BrowserTargetValidator(),
            new BrowserRuntimeFindingClassifier(rc8),
            rc8,
            new BrowserEvidenceSanitizer(),
            options8,
            null);

        // Use a simple test URL — we're just verifying mode doesn't break anonymous path
        var request = new BrowserRuntimeExecutionRequest(
            "https://httpbin.org/html",
            BrowserRuntimeExecutionMode.AnonymousOwnedBrowser);

        var result = await runtime.ReviewAsync(request);
        // Result status varies by network; we're just checking that anonymous mode works at all
        result.ExecutionMode.Should().Be(BrowserRuntimeExecutionMode.AnonymousOwnedBrowser);
        result.BrowserName.Should().Be("Chromium");
    }

    private const string Review = "phase-a3-review";
    private const string Profile = "phase-a3-profile";
    private static bool Enabled() => string.Equals(Environment.GetEnvironmentVariable("RUN_LOCAL_AUTHENTICATED_BROWSER_TESTS"), "true", StringComparison.OrdinalIgnoreCase);

    private static AuthenticatedBrowserSessionManager CreateManager() => new(
        new PlaywrightAuthenticatedBrowserHost(),
        Options.Create(new AuthenticatedReviewOptions { Enabled = true, Runtime = "LocalWorkstation", AllowSyntheticHttpOrigins = true }),
        TimeProvider.System,
        NullLogger<AuthenticatedBrowserSessionManager>.Instance);

    private static async Task<AuthenticatedBrowserSessionDescriptor> StartAndAuthenticateAsync(AuthenticatedBrowserSessionManager manager, SyntheticFixture fixture)
    {
        var session = await manager.StartAsync(new AuthenticatedBrowserSessionRequest(Review, Profile, fixture.TargetUrl));
        await manager.BeginAuthenticationAsync(new(session.SessionId, Review, Profile, fixture.EntraOrigin, fixture.McasOrigin));
        return session;
    }

    private static async Task<AuthenticatedBrowserSessionDescriptor> ReachAuthenticatedAsync(AuthenticatedBrowserSessionManager manager, SyntheticFixture fixture)
    {
        var session = await StartAndAuthenticateAsync(manager, fixture);
        await using var lease = await manager.AcquireAuthenticationPageLeaseAsync(session.SessionId, Review, Profile, fixture.TargetUrl);
        await lease.Page.ClickAsync("#synthetic-sign-in");
        await WaitForStatusAsync(manager, session.SessionId, AuthenticatedBrowserSessionStatus.AwaitingUserContinuation);
        await lease.Page.ClickAsync("#synthetic-continue");
        await WaitForStatusAsync(manager, session.SessionId, AuthenticatedBrowserSessionStatus.Authenticated);
        return session;
    }

    private static async Task<AuthenticatedBrowserSessionDescriptor> WaitForStatusAsync(AuthenticatedBrowserSessionManager manager, string sessionId, AuthenticatedBrowserSessionStatus expected)
    {
        for (var i = 0; i < 100; i++)
        {
            var status = await manager.GetStatusAsync(sessionId, Review, Profile);
            if (status?.Status == expected) return status;
            await Task.Delay(25);
        }
        var final = await manager.GetStatusAsync(sessionId, Review, Profile);
        throw new Xunit.Sdk.XunitException($"Expected {expected}; observed {final?.Status.ToString() ?? "missing"}.");
    }

    private sealed class SyntheticFixture : IAsyncDisposable
    {
        private readonly Server _target;
        private readonly Server _entra;
        private readonly Server _mcas;
        private readonly Server _unexpected;

        private SyntheticFixture(Server target, Server entra, Server mcas, Server unexpected)
        { _target = target; _entra = entra; _mcas = mcas; _unexpected = unexpected; }

        public string TargetUrl => $"{_target.Origin}/protected-app";
        public string EntraOrigin => _entra.Origin;
        public string EntraUrl => $"{_entra.Origin}/login";
        public string McasOrigin => _mcas.Origin;
        public string UnexpectedOrigin => _unexpected.Origin;

        public static Task<SyntheticFixture> StartAsync()
        {
            var target = new Server(); var entra = new Server(); var mcas = new Server(); var unexpected = new Server();
            target.Start(); entra.Start(); mcas.Start(); unexpected.Start();
            var fixture = new SyntheticFixture(target, entra, mcas, unexpected);
            target.Handler = path => path.StartsWith("/authenticated", StringComparison.Ordinal)
                ? Response.Ok("""
                    <html data-birknext-auth-fixture='app'>
                    <title>Protected app</title>
                    <body>
                    <main>Authenticated application</main>
                    <script>
                    console.error('deterministic-fixture-error');
                    throw new Error('deterministic-page-error');
                    </script>
                    <img src='/missing-resource-404' alt='broken' />
                    <p>user@example.test case-sensitive-sentinel ?code=SECRET_CODE</p>
                    </body>
                    </html>
                    """)
                : Response.Redirect(fixture.EntraUrl);
            entra.Handler = _ => Response.Ok($"<html data-birknext-auth-fixture='login'><body><a id='synthetic-sign-in' href='{fixture.McasOrigin}/notice'>Sign in fixture</a><a id='synthetic-unexpected' href='{fixture.UnexpectedOrigin}/outside'>Unexpected fixture</a></body></html>");
            mcas.Handler = path => path.StartsWith("/proxied-application", StringComparison.Ordinal)
                ? Response.Ok("<html data-birknext-auth-fixture='app'><body><main>Proxied authenticated application</main></body></html>")
                : Response.Ok($"<html data-birknext-auth-fixture='mcas-notice'><body><form action='{fixture.TargetUrl.Replace("/protected-app", "/authenticated")}'><button id='synthetic-continue' type='submit'>Continue fixture</button></form></body></html>");
            unexpected.Handler = _ => Response.Ok("<html><body>Unexpected</body></html>");
            return Task.FromResult(fixture);
        }

        public async ValueTask DisposeAsync()
        { await _target.DisposeAsync(); await _entra.DisposeAsync(); await _mcas.DisposeAsync(); await _unexpected.DisposeAsync(); }
    }

    private sealed class Server : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private Task? _loop;
        public Func<string, Response> Handler { get; set; } = _ => Response.Ok("ok");
        public string Origin => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
        public void Start() { _listener.Start(); _loop = LoopAsync(); }
        private async Task LoopAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    await using var stream = client.GetStream();
                    var buffer = new byte[8192]; var read = await stream.ReadAsync(buffer, _stop.Token);
                    var line = Encoding.ASCII.GetString(buffer, 0, read).Split("\r\n", 2)[0];
                    var path = line.Split(' ').ElementAtOrDefault(1) ?? "/";
                    var response = Handler(path); var bodyBytes = Encoding.UTF8.GetBytes(response.Body);
                    var headers = response.Status == 302
                        ? $"HTTP/1.1 302 Found\r\nLocation: {response.Location}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
                        : $"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {bodyBytes.Length}\r\nConnection: close\r\n\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), _stop.Token);
                    if (response.Status == 200) await stream.WriteAsync(bodyBytes, _stop.Token);
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
        }
        public async ValueTask DisposeAsync()
        { _stop.Cancel(); _listener.Stop(); if (_loop is not null) try { await _loop; } catch { } _stop.Dispose(); }
    }

    private sealed record Response(int Status, string Body, string? Location)
    {
        public static Response Ok(string body) => new(200, body, null);
        public static Response Redirect(string location) => new(302, "", location);
    }
}
