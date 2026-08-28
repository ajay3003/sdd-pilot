using BirkNext.Api.Services.AuthenticatedReview;
using BirkNext.Api.Services.FrontendAccessibility;
using BirkNext.Api.Services.FrontendBrowserRuntime;
using BirkNext.Api.Tests.TestInfrastructure;
using Deque.AxeCore.Commons;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace BirkNext.Api.Tests.Services.AuthenticatedReview;

[Trait("Category", "AuthenticatedReviewPhaseA4RealAcceptance")]
public sealed class AuthenticatedReviewPhaseA4RealAcceptanceTests
{
    [Fact]
    public async Task AuthenticatedAccessibility_ReusesExactSamePageAndContext_NoSecondBrowser()
    {
        if (!ExternalFrontendQualityTestGate.IsLocalHeadedEnabled) return;
        await using var fixture = await SyntheticFixture.StartAsync();
        await using var manager = CreateManager();
        var session = await ReachAuthenticatedAsync(manager, fixture);

        // Acquire auth lease and store references
        await using var authLease = await manager.AcquireAuthenticationPageLeaseAsync(session.SessionId, Review, Profile, fixture.TargetUrl);
        var authPageRef = authLease.Page;
        var authContextRef = authLease.Context;

        // Run accessibility on authenticated page with real bundled axe provider
        var accessibilityService = CreateAuthenticatedAccessibilityService(manager);

        var accessibilityRequest = new AccessibilityExecutionRequest(
            fixture.TargetUrl,
            AccessibilityExecutionMode.AuthenticatedSessionPage,
            Review,
            Profile,
            session.SessionId);

        var accessibilityResult = await accessibilityService.ReviewAsync(accessibilityRequest);

        // Verify accessibility succeeded and found the deterministic violation
        accessibilityResult.ExecutionStatus.Should().Be(AccessibilityExecutionStatus.Assessed,
            $"Expected Assessed but got {accessibilityResult.ExecutionStatus} ({accessibilityResult.OutcomeReason})");
        accessibilityResult.Findings.Should().NotBeEmpty("Accessibility should find deterministic button-name violation");

        // Verify the finding is the deterministic one (empty button)
        var buttonNameFinding = accessibilityResult.Findings.FirstOrDefault(f => f.RuleId == "button-name");
        buttonNameFinding.Should().NotBeNull("Should find the button-name rule violation");
        buttonNameFinding!.Kind.Should().Be(AccessibilityFindingKind.Violation);

        // Verify page/context are still the same after accessibility
        await using var freshLease = await manager.AcquireAuthenticationPageLeaseAsync(session.SessionId, Review, Profile, fixture.TargetUrl);
        freshLease.Page.Should().BeSameAs(authPageRef, "Accessibility should reuse exact same page");
        freshLease.Context.Should().BeSameAs(authContextRef, "Accessibility should reuse exact same context");

        // Verify session still authenticated
        var finalStatus = await manager.GetStatusAsync(session.SessionId, Review, Profile);
        finalStatus.Should().NotBeNull();
        finalStatus!.Status.Should().Be(AuthenticatedBrowserSessionStatus.Authenticated, "Session should remain authenticated after accessibility");
    }

    [Fact]
    public async Task AuthenticatedAccessibility_SameSessionAsBrowserRuntime_SinglePageContextBrowser()
    {
        if (!ExternalFrontendQualityTestGate.IsLocalHeadedEnabled) return;
        await using var fixture = await SyntheticFixture.StartAsync();
        await using var manager = CreateManager();
        var session = await ReachAuthenticatedAsync(manager, fixture);

        // Get initial lease references
        await using var initialLease = await manager.AcquireAuthenticationPageLeaseAsync(session.SessionId, Review, Profile, fixture.TargetUrl);
        var sessionPageRef = initialLease.Page;
        var sessionContextRef = initialLease.Context;

        // Run browser runtime
        var rcBrowser = new BrowserResourceClassifier();
        var optionsBrowser = Options.Create(new FrontendBrowserRuntimeOptions { Enabled = true });
        var runtime = new FrontendBrowserRuntimeReviewService(
            NullLogger<FrontendBrowserRuntimeReviewService>.Instance,
            CreateA4TargetValidator(),
            new BrowserRuntimeFindingClassifier(rcBrowser),
            rcBrowser,
            new BrowserEvidenceSanitizer(),
            optionsBrowser,
            manager);

        var runtimeRequest = new BrowserRuntimeExecutionRequest(
            fixture.TargetUrl,
            BrowserRuntimeExecutionMode.AuthenticatedSessionPage,
            Review,
            Profile,
            session.SessionId);

        var runtimeResult = await runtime.ReviewAsync(runtimeRequest);
        runtimeResult.Status.Should().Be(BrowserRuntimeEngineStatus.Assessed);

        // Run accessibility on same session
        var accessibilityService = CreateAuthenticatedAccessibilityService(manager);

        var accessibilityRequest = new AccessibilityExecutionRequest(
            fixture.TargetUrl,
            AccessibilityExecutionMode.AuthenticatedSessionPage,
            Review,
            Profile,
            session.SessionId);

        var accessibilityResult = await accessibilityService.ReviewAsync(accessibilityRequest);
        accessibilityResult.ExecutionStatus.Should().Be(AccessibilityExecutionStatus.Assessed);

        // Verify both engines used the same page/context
        await using var finalLease = await manager.AcquireAuthenticationPageLeaseAsync(session.SessionId, Review, Profile, fixture.TargetUrl);
        finalLease.Page.Should().BeSameAs(sessionPageRef, "Both runtime and accessibility should use same page");
        finalLease.Context.Should().BeSameAs(sessionContextRef, "Both runtime and accessibility should use same context");

        // Session remains authenticated
        var status = await manager.GetStatusAsync(session.SessionId, Review, Profile);
        status!.Status.Should().Be(AuthenticatedBrowserSessionStatus.Authenticated);
    }

    [Fact]
    public async Task AuthenticatedAccessibility_SensitiveSentinels_NeverAppearInResult()
    {
        if (!ExternalFrontendQualityTestGate.IsLocalHeadedEnabled) return;
        await using var fixture = await SyntheticFixture.StartAsync();
        await using var manager = CreateManager();
        var session = await ReachAuthenticatedAsync(manager, fixture);

        var accessibilityService = CreateAuthenticatedAccessibilityService(manager);

        var request = new AccessibilityExecutionRequest(
            fixture.TargetUrl,
            AccessibilityExecutionMode.AuthenticatedSessionPage,
            Review,
            Profile,
            session.SessionId);

        var result = await accessibilityService.ReviewAsync(request);

        // Serialize and check for sentinel values
        var serialized = System.Text.Json.JsonSerializer.Serialize(result);
        serialized.Should().NotContain("user@example.test");
        serialized.Should().NotContain("case-sensitive-sentinel");
        serialized.Should().NotContain("SECRET_CODE");
        serialized.Should().NotContain("Authorization");
    }

    [Fact]
    public async Task AuthenticatedAccessibility_EntraRedirectMidRun_StopsWithNoEvidence()
    {
        if (!ExternalFrontendQualityTestGate.IsLocalHeadedEnabled) return;
        await using var fixture = await SyntheticFixture.StartAsync();
        await using var manager = CreateManager();
        var session = await ReachAuthenticatedAsync(manager, fixture);

        var accessibilityService = CreateAuthenticatedAccessibilityService(manager);

        var accessibilityTask = Task.Run(async () =>
        {
            var request = new AccessibilityExecutionRequest(
                fixture.TargetUrl,
                AccessibilityExecutionMode.AuthenticatedSessionPage,
                Review,
                Profile,
                session.SessionId);
            return await accessibilityService.ReviewAsync(request);
        });

        // Let accessibility start, then redirect
        // Increase delay to allow axe.run() to start execution
        await Task.Delay(500);
        await using var lease = await manager.AcquireAuthenticationPageLeaseAsync(session.SessionId, Review, Profile, fixture.TargetUrl);
        await lease.Page.GotoAsync(fixture.EntraUrl);

        var result = await accessibilityTask;
        result.ExecutionStatus.Should().BeOneOf(AccessibilityExecutionStatus.AuthenticationRequired, AccessibilityExecutionStatus.Skipped);
        result.OutcomeReason.Should().Be(AccessibilityOutcomeReason.AuthenticationExpired, because: "Unexpected navigation invalidates session");
        (result.Findings?.Count ?? 0).Should().Be(0);
    }

    [Fact]
    public async Task AuthenticatedAccessibility_McasRedirectMidRun_StopsWithNoEvidence()
    {
        if (!ExternalFrontendQualityTestGate.IsLocalHeadedEnabled) return;
        await using var fixture = await SyntheticFixture.StartAsync();
        await using var manager = CreateManager();
        var session = await ReachAuthenticatedAsync(manager, fixture);

        var accessibilityService = CreateAuthenticatedAccessibilityService(manager);

        var accessibilityTask = Task.Run(async () =>
        {
            var request = new AccessibilityExecutionRequest(
                fixture.TargetUrl,
                AccessibilityExecutionMode.AuthenticatedSessionPage,
                Review,
                Profile,
                session.SessionId);
            return await accessibilityService.ReviewAsync(request);
        });

        await Task.Delay(500);
        await using var lease = await manager.AcquireAuthenticationPageLeaseAsync(session.SessionId, Review, Profile, fixture.TargetUrl);
        await lease.Page.GotoAsync(fixture.McasOrigin + "/notice");

        var result = await accessibilityTask;
        result.ExecutionStatus.Should().Be(AccessibilityExecutionStatus.AuthenticationRequired);
        result.OutcomeReason.Should().Be(AccessibilityOutcomeReason.AuthenticationExpired);
        (result.Findings?.Count ?? 0).Should().Be(0);
    }

    [Fact]
    public async Task AuthenticatedAccessibility_UnexpectedOriginMidRun_StopsWithNoEvidence()
    {
        if (!ExternalFrontendQualityTestGate.IsLocalHeadedEnabled) return;
        await using var fixture = await SyntheticFixture.StartAsync();
        await using var manager = CreateManager();
        var session = await ReachAuthenticatedAsync(manager, fixture);

        var accessibilityService = CreateAuthenticatedAccessibilityService(manager);

        var accessibilityTask = Task.Run(async () =>
        {
            var request = new AccessibilityExecutionRequest(
                fixture.TargetUrl,
                AccessibilityExecutionMode.AuthenticatedSessionPage,
                Review,
                Profile,
                session.SessionId);
            return await accessibilityService.ReviewAsync(request);
        });

        await Task.Delay(500);
        await using var lease = await manager.AcquireAuthenticationPageLeaseAsync(session.SessionId, Review, Profile, fixture.TargetUrl);
        await lease.Page.GotoAsync(fixture.UnexpectedOrigin);

        var result = await accessibilityTask;
        result.ExecutionStatus.Should().Be(AccessibilityExecutionStatus.AuthenticationRequired, because: "Navigation outside allowed scope");
        result.OutcomeReason.Should().Be(AccessibilityOutcomeReason.UnexpectedOrigin);
        (result.Findings?.Count ?? 0).Should().Be(0);
    }

    [Fact]
    public async Task AuthenticatedAccessibility_MissingSessionId_Rejected()
    {
        if (!ExternalFrontendQualityTestGate.IsLocalHeadedEnabled) return;
        await using var fixture = await SyntheticFixture.StartAsync();

        var accessibilityService = CreateAuthenticatedAccessibilityService(CreateManager());

        var request = new AccessibilityExecutionRequest(
            fixture.TargetUrl,
            AccessibilityExecutionMode.AuthenticatedSessionPage,
            Review,
            Profile,
            "");

        var result = await accessibilityService.ReviewAsync(request);
        result.ExecutionStatus.Should().Be(AccessibilityExecutionStatus.AuthenticationRequired);
        result.OutcomeReason.Should().Be(AccessibilityOutcomeReason.AuthenticationRequired);
    }

    [Fact]
    public async Task AuthenticatedAccessibility_ExpiredSession_Rejected()
    {
        if (!ExternalFrontendQualityTestGate.IsLocalHeadedEnabled) return;
        await using var fixture = await SyntheticFixture.StartAsync();
        await using var manager = CreateManager();
        var session = await StartAndAuthenticateAsync(manager, fixture);

        // Immediately expire by cancelling
        await manager.CancelAsync(session.SessionId, Review, Profile);

        var accessibilityService = CreateAuthenticatedAccessibilityService(manager);

        var request = new AccessibilityExecutionRequest(
            fixture.TargetUrl,
            AccessibilityExecutionMode.AuthenticatedSessionPage,
            Review,
            Profile,
            session.SessionId);

        var result = await accessibilityService.ReviewAsync(request);
        result.ExecutionStatus.Should().Be(AccessibilityExecutionStatus.AuthenticationRequired);
        result.OutcomeReason.Should().Be(AccessibilityOutcomeReason.AuthenticationExpired);
    }

    [Fact]
    public async Task AnonymousAccessibility_StillOwnsItsOwnBrowser_Unaffected()
    {
        if (!ExternalFrontendQualityTestGate.IsLocalHeadedEnabled) return;

        // Anonymous path should still work - use public constructor
        var options = Options.Create(new FrontendAccessibilityOptions { Enabled = true });
        var accessibilityService = new FrontendAccessibilityReviewService(
            NullLogger<FrontendAccessibilityReviewService>.Instance,
            CreateA4TargetValidator(),
            new AccessibilityNormalizer(new AccessibilityEvidenceSanitizer()),
            options);

        // Use a simple test URL — we're just verifying mode doesn't break anonymous path
        var result = await accessibilityService.ReviewAsync("https://httpbin.org/html", requiresAuthentication: false);
        result.ExecutionMode.Should().Be(AccessibilityExecutionMode.AnonymousOwnedBrowser);
        result.ExecutionStatus.Should().BeOneOf(AccessibilityExecutionStatus.Assessed, AccessibilityExecutionStatus.EngineError);
    }

    private const string Review = "phase-a4-review";
    private const string Profile = "phase-a4-profile";

    private static AuthenticatedBrowserSessionManager CreateManager() => new(
        new PlaywrightAuthenticatedBrowserHost(),
        Options.Create(new AuthenticatedReviewOptions { Enabled = true, Runtime = "LocalWorkstation", AllowSyntheticHttpOrigins = true }),
        TimeProvider.System,
        NullLogger<AuthenticatedBrowserSessionManager>.Instance);

    private static BrowserTargetValidator CreateA4TargetValidator()
        => new BrowserTargetValidator(allowLoopback: true);

    private static FrontendAccessibilityReviewService CreateAuthenticatedAccessibilityService(IAuthenticatedBrowserSessionManager? manager = null)
    {
        var sanitizer = new AccessibilityEvidenceSanitizer();
        // Use the internal constructor to bypass BundledAxeScriptProvider dependency
        // which is provided by Deque.AxeCore.Commons but not easily accessible in tests
        return new FrontendAccessibilityReviewService(
            NullLogger<FrontendAccessibilityReviewService>.Instance,
            CreateA4TargetValidator(),
            new AccessibilityNormalizer(sanitizer),
            new RealAxeScriptProvider(),
            sanitizer,
            manager,
            true);
    }

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
            // Protected app with deterministic axe violation: empty button (button-name rule)
            target.Handler = path => path.StartsWith("/authenticated", StringComparison.Ordinal)
                ? Response.Ok("""
                    <html data-birknext-auth-fixture='app'>
                    <title>Protected app</title>
                    <body>
                    <main>Authenticated application</main>
                    <button id='empty-button'></button>
                    <script>
                    setTimeout(() => console.error('deterministic-fixture-error'), 100);
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

    private sealed class RealAxeScriptProvider : IAxeScriptProvider
    {
        private static string? _script;

        public string GetScript()
        {
            if (_script != null) return _script;

            // Use Deque's bundled provider via direct type resolution
            // The package is now directly referenced in BirkNext.Api.Tests.csproj
            var dequeAssembly = typeof(IAxeScriptProvider).Assembly;
            var bundledType = dequeAssembly.GetType("Deque.AxeCore.Commons.BundledAxeScriptProvider");

            if (bundledType == null)
            {
                // Debug: list available types to understand what's exposed
                var availableTypes = dequeAssembly.GetTypes()
                    .Where(t => t.Name.Contains("Axe") || t.Name.Contains("Script"))
                    .Select(t => t.FullName)
                    .ToList();

                throw new TypeLoadException(
                    $"BundledAxeScriptProvider not found in {dequeAssembly.FullName}. " +
                    $"Available types with 'Axe' or 'Script': {string.Join(", ", availableTypes)}");
            }

            var bundledProvider = (IAxeScriptProvider)Activator.CreateInstance(bundledType)!;
            _script = bundledProvider.GetScript();

            if (string.IsNullOrEmpty(_script))
            {
                throw new InvalidOperationException($"{bundledType.FullName}.GetScript() returned null or empty");
            }

            return _script;
        }
    }
}
