using BirkNext.Api.Services.AuthenticatedReview;
using BirkNext.Api.Tests.TestInfrastructure;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace BirkNext.Api.Tests.Services.AuthenticatedReview;

public sealed class AuthenticatedBrowserSessionLocalAcceptanceTests
{
    [Fact]
    [Trait("Category", "LocalHeadedPlaywright")]
    public async Task AuthenticatedBrowserSession_StartsVisibleEphemeralBrowser()
    {
        if (!ExternalFrontendQualityTestGate.IsLocalHeadedEnabled) return;

        await using var fixture = await LocalFixture.StartAsync();
        await using var manager = new AuthenticatedBrowserSessionManager(
            new PlaywrightAuthenticatedBrowserHost(),
            Options.Create(new AuthenticatedReviewOptions { Enabled = true, Runtime = "LocalWorkstation" }),
            TimeProvider.System,
            NullLogger<AuthenticatedBrowserSessionManager>.Instance);

        var session = await manager.StartAsync(new AuthenticatedBrowserSessionRequest("real-browser-review", "fixture-profile", fixture.Url));
        session.Status.Should().Be(AuthenticatedBrowserSessionStatus.BrowserReady);

        await using var lease = await manager.AcquireAuthenticationPageLeaseAsync(session.SessionId, "real-browser-review", "fixture-profile", fixture.Url);
        (await lease.Page.TitleAsync()).Should().Be("Authenticated review fixture");
        lease.Context.Pages.Should().ContainSingle().Which.Should().BeSameAs(lease.Page);

        (await manager.CancelAsync(session.SessionId, "real-browser-review", "fixture-profile")).Should().BeTrue();
        lease.SessionCancellation.IsCancellationRequested.Should().BeTrue();
        var accessAfterCancel = () => lease.Page;
        accessAfterCancel.Should().Throw<ObjectDisposedException>();
    }

    private sealed class LocalFixture : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _server;
        private LocalFixture(TcpListener listener)
        {
            _listener = listener;
            Url = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/protected-app";
            _server = ServeAsync();
        }
        public string Url { get; }
        public static Task<LocalFixture> StartAsync()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            return Task.FromResult(new LocalFixture(listener));
        }
        private async Task ServeAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    await using var stream = client.GetStream();
                    var buffer = new byte[4096]; await stream.ReadAsync(buffer, _stop.Token);
                    var body = "<!doctype html><title>Authenticated review fixture</title><main>Local fixture</main>";
                    var bytes = Encoding.UTF8.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}");
                    await stream.WriteAsync(bytes, _stop.Token);
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
        }
        public async ValueTask DisposeAsync()
        {
            _stop.Cancel(); _listener.Stop();
            try { await _server; } catch { }
            _stop.Dispose();
        }
    }
}
