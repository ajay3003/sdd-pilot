using BirkNext.Api.Services.AuthenticatedReview;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace BirkNext.Api.Tests.Services.AuthenticatedReview;

[Trait("Category", "AuthenticatedReviewPhaseA2RealAcceptance")]
public sealed class AuthenticatedReviewPhaseA2RealAcceptanceTests
{
    [Fact]
    public async Task Synthetic_Entra_Mcas_App_Flow_AuthenticatesSameContext()
    {
        if (!Enabled()) return;
        await using var fixture = await SyntheticFixture.StartAsync();
        await using var manager = CreateManager();
        var session = await StartAndAuthenticateAsync(manager, fixture);
        await using var authLease = await manager.AcquireAuthenticationPageLeaseAsync(session.SessionId, Review, Profile, fixture.TargetUrl);

        await authLease.Page.ClickAsync("#synthetic-sign-in");
        await WaitForStatusAsync(manager, session.SessionId, AuthenticatedBrowserSessionStatus.AwaitingUserContinuation);
        await authLease.Page.ClickAsync("#synthetic-continue");
        var authenticated = await WaitForStatusAsync(manager, session.SessionId, AuthenticatedBrowserSessionStatus.Authenticated);

        authenticated.DeliveryContext.Should().Be(AuthenticatedDeliveryContext.ConditionalAccessMonitoredSession);
        authenticated.ApplicationValidationCurrent.Should().BeTrue();
        await using var engineLease = await manager.AcquirePageLeaseAsync(session.SessionId, Review, Profile, fixture.TargetUrl);
        engineLease.Page.Should().BeSameAs(authLease.Page);
        engineLease.Context.Should().BeSameAs(authLease.Context);
    }

    [Fact]
    public async Task Synthetic_ProxiedApplicationDelivery_IsValidatedAndLabeled()
    {
        if (!Enabled()) return;
        await using var fixture = await SyntheticFixture.StartAsync(); fixture.UseProxiedDelivery = true;
        await using var manager = CreateManager();
        var session = await StartAndAuthenticateAsync(manager, fixture);
        await using var lease = await manager.AcquireAuthenticationPageLeaseAsync(session.SessionId, Review, Profile, fixture.TargetUrl);
        await lease.Page.ClickAsync("#synthetic-sign-in");
        await WaitForStatusAsync(manager, session.SessionId, AuthenticatedBrowserSessionStatus.AwaitingUserContinuation);
        await lease.Page.ClickAsync("#synthetic-continue");
        var status = await WaitForStatusAsync(manager, session.SessionId, AuthenticatedBrowserSessionStatus.Authenticated);
        status.DeliveryContext.Should().Be(AuthenticatedDeliveryContext.ProxiedApplicationDelivery);
        status.ApplicationValidationCurrent.Should().BeTrue();
    }

    [Fact]
    public async Task CancelAtEntra_DisposesSessionAndInvalidatesLease()
    {
        if (!Enabled()) return;
        await using var fixture = await SyntheticFixture.StartAsync(); await using var manager = CreateManager();
        var session = await StartAndAuthenticateAsync(manager, fixture);
        await using var lease = await manager.AcquireAuthenticationPageLeaseAsync(session.SessionId, Review, Profile, fixture.TargetUrl);
        (await manager.CancelAsync(session.SessionId, Review, Profile)).Should().BeTrue();
        lease.SessionCancellation.IsCancellationRequested.Should().BeTrue();
        (await manager.GetStatusAsync(session.SessionId, Review, Profile)).Should().BeNull();
    }

    [Fact]
    public async Task CancelAtMcas_AndMcasNeverReturns_NeverBecomeAuthenticated()
    {
        if (!Enabled()) return;
        await using var fixture = await SyntheticFixture.StartAsync(); await using var manager = CreateManager();
        var session = await StartAndAuthenticateAsync(manager, fixture);
        await using var lease = await manager.AcquireAuthenticationPageLeaseAsync(session.SessionId, Review, Profile, fixture.TargetUrl);
        await lease.Page.ClickAsync("#synthetic-sign-in");
        var waiting = await WaitForStatusAsync(manager, session.SessionId, AuthenticatedBrowserSessionStatus.AwaitingUserContinuation);
        waiting.ApplicationValidationCurrent.Should().BeFalse();
        await AssertEngineLeaseDenied(manager, session.SessionId, fixture.TargetUrl);
        await manager.CancelAsync(session.SessionId, Review, Profile);
        lease.SessionCancellation.IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    public async Task UnexpectedOrigin_FailsClosedAndDeniesEngineLease()
    {
        if (!Enabled()) return;
        await using var fixture = await SyntheticFixture.StartAsync(); await using var manager = CreateManager();
        var session = await StartAndAuthenticateAsync(manager, fixture);
        await using var lease = await manager.AcquireAuthenticationPageLeaseAsync(session.SessionId, Review, Profile, fixture.TargetUrl);
        await lease.Page.ClickAsync("#synthetic-unexpected");
        await WaitForStatusAsync(manager, session.SessionId, AuthenticatedBrowserSessionStatus.UnexpectedOrigin);
        await AssertEngineLeaseDenied(manager, session.SessionId, fixture.TargetUrl);
    }

    [Fact]
    public async Task EntraAndMcasCanNeverBeFinalSuccess()
    {
        if (!Enabled()) return;
        await using var fixture = await SyntheticFixture.StartAsync(); await using var manager = CreateManager();
        var session = await StartAndAuthenticateAsync(manager, fixture);
        await AssertEngineLeaseDenied(manager, session.SessionId, fixture.TargetUrl);
        await using var lease = await manager.AcquireAuthenticationPageLeaseAsync(session.SessionId, Review, Profile, fixture.TargetUrl);
        await lease.Page.ClickAsync("#synthetic-sign-in");
        await WaitForStatusAsync(manager, session.SessionId, AuthenticatedBrowserSessionStatus.AwaitingUserContinuation);
        await AssertEngineLeaseDenied(manager, session.SessionId, fixture.TargetUrl);
    }

    [Fact]
    public async Task AuthenticatedReturnToEntraOrMcasRevokesEligibility()
    {
        if (!Enabled()) return;
        await using var fixture = await SyntheticFixture.StartAsync(); await using var manager = CreateManager();
        var session = await ReachAuthenticatedAsync(manager, fixture);
        await using var raw = await manager.AcquireAuthenticationPageLeaseAsync(session.SessionId, Review, Profile, fixture.TargetUrl);
        await raw.Page.GotoAsync(fixture.EntraUrl);
        await WaitForStatusAsync(manager, session.SessionId, AuthenticatedBrowserSessionStatus.AuthenticationExpired);
        await AssertEngineLeaseDenied(manager, session.SessionId, fixture.TargetUrl);
    }

    [Fact]
    public async Task AuthenticatedReturnToMcasNoticeRevokesEligibility()
    {
        if (!Enabled()) return;
        await using var fixture = await SyntheticFixture.StartAsync(); await using var manager = CreateManager();
        var session = await ReachAuthenticatedAsync(manager, fixture);
        await using var raw = await manager.AcquireAuthenticationPageLeaseAsync(session.SessionId, Review, Profile, fixture.TargetUrl);
        await raw.Page.GotoAsync(fixture.McasOrigin + "/notice");
        await WaitForStatusAsync(manager, session.SessionId, AuthenticatedBrowserSessionStatus.AuthenticationExpired);
        await AssertEngineLeaseDenied(manager, session.SessionId, fixture.TargetUrl);
    }

    [Fact]
    public async Task AuthenticatedSession_CannotValidateDifferentTarget()
    {
        if (!Enabled()) return;
        await using var fixture = await SyntheticFixture.StartAsync(); await using var manager = CreateManager();
        var session = await ReachAuthenticatedAsync(manager, fixture);
        var act = () => manager.AcquirePageLeaseAsync(session.SessionId, Review, Profile, fixture.UnexpectedOrigin);
        await act.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    private const string Review = "phase-a2-review";
    private const string Profile = "phase-a2-profile";
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

    private static async Task AssertEngineLeaseDenied(AuthenticatedBrowserSessionManager manager, string sessionId, string targetUrl)
    {
        var act = () => manager.AcquirePageLeaseAsync(sessionId, Review, Profile, targetUrl);
        await act.Should().ThrowAsync<AuthenticatedSessionNotEligibleException>();
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
        public bool UseProxiedDelivery { get; set; }

        public static Task<SyntheticFixture> StartAsync()
        {
            var target = new Server(); var entra = new Server(); var mcas = new Server(); var unexpected = new Server();
            target.Start(); entra.Start(); mcas.Start(); unexpected.Start();
            var fixture = new SyntheticFixture(target, entra, mcas, unexpected);
            target.Handler = path => path.StartsWith("/authenticated", StringComparison.Ordinal)
                ? Response.Ok("<html data-birknext-auth-fixture='app'><title>Protected app</title><body><main>Authenticated application</main></body></html>")
                : Response.Redirect(fixture.EntraUrl);
            entra.Handler = _ => Response.Ok($"<html data-birknext-auth-fixture='login'><body><a id='synthetic-sign-in' href='{fixture.McasOrigin}/notice'>Sign in fixture</a><a id='synthetic-unexpected' href='{fixture.UnexpectedOrigin}/outside'>Unexpected fixture</a></body></html>");
            mcas.Handler = path => path.StartsWith("/proxied-application", StringComparison.Ordinal)
                ? Response.Ok("<html data-birknext-auth-fixture='app'><body><main>Proxied authenticated application</main></body></html>")
                : Response.Ok($"<html data-birknext-auth-fixture='mcas-notice'><body><form action='{(fixture.UseProxiedDelivery ? fixture.McasOrigin + "/proxied-application" : fixture.TargetUrl.Replace("/protected-app", "/authenticated"))}'><button id='synthetic-continue' type='submit'>Continue fixture</button></form></body></html>");
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
