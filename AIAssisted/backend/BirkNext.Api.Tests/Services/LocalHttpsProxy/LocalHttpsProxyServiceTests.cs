using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using BirkNext.Api.Services.AuthenticatedReview;
using BirkNext.Api.Services.LocalHttpsProxy;
using BirkNext.Api.Services.ManagedEdge;
using BirkNext.LocalHttpsProxy;
using Microsoft.Extensions.Options;
using Moq;

namespace BirkNext.Api.Tests.Services.LocalHttpsProxy;

/// <summary>
/// End-to-end loopback proof of the proxy session: loopback-only binding (A), Production/remote rejection (B, C), pass-through for
/// unrelated hosts (D), interception of approved hosts (E-H), bearer detection on a successful approved request (I), credential never
/// in status JSON or logs (J, K, M), wipe on stop / environment switch / target change (Q, R, S), expiry (T).
/// </summary>
public sealed class LocalHttpsProxyServiceTests : IAsyncLifetime
{
    private const string Target = "https://app.example.test/";
    private const string ApiHost = "api.example.test";
    private const string OtherHost = "unrelated.example.test";
    private static readonly string Fp = new('A', 64);
    private static readonly string Fp2 = new('B', 64);

    private DateTimeOffset _now = DateTimeOffset.UtcNow;
    private readonly EphemeralCertificateStore _certificateStore = new();
    private readonly CapturingLogger<LocalHttpsProxyService> _log = new();
    private readonly Mock<IEdgeInstallationLocator> _edge = new();
    private readonly Mock<IManagedEdgeLauncher> _launcher = new();
    private ProxyCertificateAuthority _authority = null!;
    private TransientAuthenticatedApiContextStore _store = null!;
    private FakeUpstream _upstream = null!;
    private LocalHttpsProxyService _service = null!;

    public async Task InitializeAsync()
    {
        _upstream = await FakeUpstream.StartAsync();
        // A unique CA common name per test: SslStreamCertificateContext.Create caches the intermediate into the SChannel CurrentUser\CA
        // store, so distinct-key CAs sharing one subject would otherwise collide across tests/runs and break chain building.
        _authority = new ProxyCertificateAuthority(_certificateStore, () => _now, commonNameOverride: $"BirkNext DEV HTTPS Inspection CA {Guid.NewGuid():N}");
        _store = new TransientAuthenticatedApiContextStore(() => _now);
        _service = Create(new AuthenticatedReviewOptions { Enabled = true, Runtime = "LocalWorkstation" });
    }

    public async Task DisposeAsync()
    {
        _service.Dispose();
        _authority.Dispose();
        await _upstream.DisposeAsync();
        // SslStreamCertificateContext.Create caches the intermediate into the SChannel CurrentUser\CA store, and the write can land just
        // after this test disposes. Purge every BirkNext DEV inspection CA (test runs never coexist with a real user proxy) so runs never
        // accumulate certificates or slow down chain building.
        PurgeInspectionCertificates();
    }

    internal static void PurgeInspectionCertificates()
    {
        if (!OperatingSystem.IsWindows()) return;
        foreach (var name in new[] { StoreName.CertificateAuthority, StoreName.My, StoreName.Root })
            try
            {
                using var store = new X509Store(name, StoreLocation.CurrentUser);
                store.Open(OpenFlags.ReadWrite);
                foreach (var certificate in store.Certificates.Cast<X509Certificate2>().Where(c => c.Subject.Contains("BirkNext DEV HTTPS Inspection CA", StringComparison.Ordinal)).ToArray())
                    try { store.Remove(certificate); } catch (CryptographicException) { }
            }
            catch (CryptographicException) { }
    }

    private LocalHttpsProxyService Create(AuthenticatedReviewOptions runtime) => new(
        Options.Create(new LocalHttpsProxyOptions { Port = 0, CredentialLifetimeMinutes = 30 }), Options.Create(runtime), _authority, _store,
        new TestConnector(_upstream), _edge.Object, _launcher.Object, _log, () => _now);

    private static LocalHttpsProxyScopeRequest Scope(string profile = "dev", string fingerprint = null!, string environment = "Development", string target = Target, string? tenant = null) =>
        new(profile, fingerprint ?? Fp, environment, target, [ApiHost], tenant);

    private static string Jwt(DateTimeOffset expires, Guid? tenant = null) =>
        BearerTokenInspector.BuildUnsignedJwt(new { exp = expires.ToUnixTimeSeconds(), iss = $"https://login.microsoftonline.com/{tenant ?? Guid.NewGuid()}/v2.0", tid = (tenant ?? Guid.NewGuid()).ToString(), aud = "api://dev" });

    private async Task<int> StartAsync(LocalHttpsProxyScopeRequest scope)
    {
        var status = await _service.StartAsync(scope);
        Assert.NotNull(status.SessionId);
        Assert.True(status.Port > 0);
        _sessionId = status.SessionId!;
        return status.Port;
    }

    private string _sessionId = "";
    private LocalHttpsProxySessionRequest Session(string profile = "dev", string fingerprint = null!) => new(_sessionId, profile, fingerprint ?? Fp);

    private async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(25);
        Assert.True(condition(), "Condition not reached in time.");
    }

    // ── gates ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Production")]
    [InlineData("Custom")]
    public async Task ProductionAndCustomEnvironmentsAreRejected(string environment)
    {
        var compatibility = await _service.CheckCompatibilityAsync(Scope(environment: environment));
        Assert.False(compatibility.EnvironmentAllowed);
        Assert.False(compatibility.CanStart);
        Assert.Contains("Production", compatibility.FailureReason);
        var start = await _service.StartAsync(Scope(environment: environment));
        Assert.Equal(LocalHttpsProxyState.Failed, start.State);
        Assert.Null(start.SessionId);
        Assert.Equal(0, start.Port);
    }

    [Fact]
    public async Task RemoteDeploymentIsRejected()
    {
        using var remote = Create(new AuthenticatedReviewOptions { Enabled = false, Runtime = "Unsupported" });
        var status = await remote.StartAsync(Scope());
        Assert.False(status.LocalIntegrationAvailable);
        Assert.Equal(LocalHttpsProxyState.Failed, status.State);
        Assert.Null(status.SessionId);
        Assert.Equal(LocalHttpsProxyEnvironmentPolicy.RemoteDeploymentReason, status.FailureReason);
        await Assert.ThrowsAsync<InvalidOperationException>(() => remote.InstallCertificateAsync(new(true)));
    }

    [Fact]
    public async Task IdentityAndConfirmationAreRequired()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _service.StartAsync(Scope(fingerprint: "short")));
        await Assert.ThrowsAsync<ArgumentException>(() => _service.StartAsync(Scope(profile: "")));
        await Assert.ThrowsAsync<ArgumentException>(() => _service.InstallCertificateAsync(new(false)));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => _service.StatusAsync(new("nope", "dev", Fp)));
    }

    [Fact]
    public async Task StartWithNonInterceptableApprovedHostFailsClosed()
    {
        var status = await _service.StartAsync(new("dev", Fp, "Development", Target, ["login.microsoftonline.com"]));
        Assert.Equal(LocalHttpsProxyState.Failed, status.State);
        Assert.Null(status.SessionId);
        Assert.Contains("cannot be intercepted", status.FailureReason);
    }

    // ── binding ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task ProxyBindsLoopbackOnly()
    {
        var port = await StartAsync(Scope());
        var status = await _service.StatusAsync(Session());
        Assert.StartsWith("127.0.0.1:", status.Endpoint);
        using (var loopback = new TcpClient()) await loopback.ConnectAsync(IPAddress.Loopback, port);
        using (var v6 = new TcpClient(AddressFamily.InterNetworkV6))
            await Assert.ThrowsAsync<SocketException>(() => v6.ConnectAsync(IPAddress.IPv6Loopback, port));
        var lan = (await Dns.GetHostAddressesAsync(Dns.GetHostName())).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));
        if (lan is not null)
        {
            using var external = new TcpClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await Assert.ThrowsAnyAsync<Exception>(async () => await external.ConnectAsync(lan, port, cts.Token));
        }
    }

    // ── traffic ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task UnrelatedHostIsTunnelledWithoutInterception()
    {
        var port = await StartAsync(Scope());
        using var client = await ProxyClient.ConnectAsync(port, $"{OtherHost}:443");
        var payload = Encoding.ASCII.GetBytes("opaque-bytes-not-tls");
        await client.Stream.WriteAsync(payload);
        var echoed = await client.ReadExactAsync(payload.Length);
        Assert.Equal(payload, echoed);
        Assert.Contains(payload, _upstream.PlainPayloads);
        var status = await _service.StatusAsync(Session());
        Assert.Equal(1, status.PassThroughConnections);
        Assert.Equal(0, status.InterceptedRequests);
        Assert.False(status.AuthenticatedCredentialAvailable);
        Assert.Equal(LocalHttpsProxyState.WaitingForCertificateTrust, status.State);
    }

    [Fact]
    public async Task BearerOnSuccessfulApprovedRequestEstablishesMemoryOnlyContextAndNothingLeaks()
    {
        _authority.Install();
        var port = await StartAsync(Scope());
        var token = Jwt(_now.AddHours(1));
        var response = await ProxyClient.InterceptedRequestAsync(port, ApiHost, $"GET /api/me HTTP/1.1\r\nHost: {ApiHost}\r\nAuthorization: Bearer {token}\r\nCookie: session=abc\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 200", response);
        Assert.Contains("{\"ok\":true}", response);
        Assert.Contains("Bearer " + token, _upstream.AuthorizationHeaders.Select(h => h ?? ""));

        await WaitForAsync(() => _store.IsAuthenticatedApiContextAvailable("dev", Fp));
        var status = await _service.StatusAsync(Session());
        Assert.Equal(LocalHttpsProxyState.Ready, status.State);
        Assert.True(status.AuthenticatedCredentialAvailable);
        Assert.True(status.RestAvailable);
        Assert.True(status.GraphQlQueryAvailable);
        Assert.False(status.BrowserDomAvailable);
        Assert.Equal(ApiHost, status.CredentialObservedHost);
        Assert.Equal("JWT", status.CredentialFormat);
        Assert.NotNull(status.CredentialExpiresAt);
        Assert.Equal(1, status.InterceptedRequests);
        Assert.Equal(1, status.AuthenticatedRequestsObserved);

        var json = JsonSerializer.Serialize(status);
        Assert.DoesNotContain(token, json);
        Assert.DoesNotContain("eyJ", json);
        Assert.DoesNotContain("session=abc", json);
        Assert.DoesNotContain("Cookie", json);
        foreach (var message in _log.Messages)
        {
            Assert.DoesNotContain(token, message);
            Assert.DoesNotContain("eyJ", message);
            Assert.DoesNotContain("abc", message);
        }
        Assert.Contains(_log.Messages, m => m.Contains("Authenticated API context established"));
        var statusProperties = typeof(LocalHttpsProxyStatus).GetProperties().Select(p => p.Name);
        Assert.DoesNotContain(statusProperties, p => p.Contains("Token", StringComparison.OrdinalIgnoreCase) || p.Contains("Bearer", StringComparison.OrdinalIgnoreCase) || p.Contains("Cookie", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task BearerOnDeniedRequestIsNotPromoted()
    {
        _upstream.NextStatus = 401;
        var port = await StartAsync(Scope());
        var response = await ProxyClient.InterceptedRequestAsync(port, ApiHost, $"GET /api/me HTTP/1.1\r\nHost: {ApiHost}\r\nAuthorization: Bearer {Jwt(_now.AddHours(1))}\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 401", response);
        await WaitForAsync(() => _service.StatusAsync(Session()).Result.AuthenticatedRequestsObserved == 1);
        var status = await _service.StatusAsync(Session());
        Assert.False(status.AuthenticatedCredentialAvailable);
        Assert.Equal(LocalHttpsProxyState.AuthenticatedTrafficDetected, status.State);
        Assert.Contains("HTTP 401", status.Evidence);
    }

    [Fact]
    public async Task ExpiredTokenAndForeignTenantAreNotPromoted()
    {
        var expectedTenant = Guid.NewGuid();
        var port = await StartAsync(Scope(tenant: expectedTenant.ToString()));
        await ProxyClient.InterceptedRequestAsync(port, ApiHost, $"GET /api/a HTTP/1.1\r\nHost: {ApiHost}\r\nAuthorization: Bearer {Jwt(_now.AddMinutes(-5), expectedTenant)}\r\n\r\n");
        await WaitForAsync(() => _service.StatusAsync(Session()).Result.AuthenticatedRequestsObserved == 1);
        Assert.False(_store.IsAuthenticatedApiContextAvailable("dev", Fp));
        Assert.Contains("already expired", (await _service.StatusAsync(Session())).Evidence);

        await ProxyClient.InterceptedRequestAsync(port, ApiHost, $"GET /api/b HTTP/1.1\r\nHost: {ApiHost}\r\nAuthorization: Bearer {Jwt(_now.AddHours(1), Guid.NewGuid())}\r\n\r\n");
        await WaitForAsync(() => _service.StatusAsync(Session()).Result.AuthenticatedRequestsObserved == 2);
        Assert.False(_store.IsAuthenticatedApiContextAvailable("dev", Fp));
        Assert.Contains("different tenant", (await _service.StatusAsync(Session())).Evidence);

        await ProxyClient.InterceptedRequestAsync(port, ApiHost, $"GET /api/c HTTP/1.1\r\nHost: {ApiHost}\r\nAuthorization: Bearer {Jwt(_now.AddHours(1), expectedTenant)}\r\n\r\n");
        await WaitForAsync(() => _store.IsAuthenticatedApiContextAvailable("dev", Fp));
    }

    [Fact]
    public async Task RequestsWithoutBearerAreRelayedButEstablishNothing()
    {
        var port = await StartAsync(Scope());
        var response = await ProxyClient.InterceptedRequestAsync(port, ApiHost, $"GET /public HTTP/1.1\r\nHost: {ApiHost}\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 200", response);
        await WaitForAsync(() => _service.StatusAsync(Session()).Result.InterceptedRequests == 1);
        var status = await _service.StatusAsync(Session());
        Assert.False(status.AuthenticatedCredentialAvailable);
        Assert.Equal(0, status.AuthenticatedRequestsObserved);
        Assert.Equal(LocalHttpsProxyState.WaitingForAuthenticatedTraffic, status.State);
    }

    // ── wipe rules ───────────────────────────────────────────────────────────

    private async Task<int> StartWithCredentialAsync(LocalHttpsProxyScopeRequest? scope = null)
    {
        var effective = scope ?? Scope();
        var port = await StartAsync(effective);
        await ProxyClient.InterceptedRequestAsync(port, ApiHost, $"GET /api/me HTTP/1.1\r\nHost: {ApiHost}\r\nAuthorization: Bearer {Jwt(_now.AddHours(1))}\r\n\r\n");
        await WaitForAsync(() => _store.IsAuthenticatedApiContextAvailable(effective.ProfileId, effective.ContextFingerprint));
        return port;
    }

    [Fact]
    public async Task StopWipesTheCredentialImmediatelyAndInvalidatesTheSession()
    {
        var port = await StartWithCredentialAsync();
        var stopped = await _service.StopAsync(Session());
        Assert.Equal(LocalHttpsProxyState.Stopped, stopped.State);
        Assert.False(stopped.AuthenticatedCredentialAvailable);
        Assert.False(_store.IsAuthenticatedApiContextAvailable("dev", Fp));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => _service.StatusAsync(Session()));
        using var probe = new TcpClient();
        await Assert.ThrowsAnyAsync<SocketException>(() => probe.ConnectAsync(IPAddress.Loopback, port));
    }

    [Fact]
    public async Task EnvironmentSwitchWipesTheCredentialOfThePreviousEnvironment()
    {
        await StartWithCredentialAsync();
        var qa = await _service.StartAsync(Scope(profile: "qa", environment: "QA"));
        Assert.NotNull(qa.SessionId);
        Assert.False(_store.IsAuthenticatedApiContextAvailable("dev", Fp));
        Assert.False(qa.AuthenticatedCredentialAvailable);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => _service.StatusAsync(Session()));
    }

    [Fact]
    public async Task TargetConfigurationChangeWipesTheCredential()
    {
        await StartWithCredentialAsync();
        // Same profile, changed target-relevant configuration => new context fingerprint => old credential must not survive.
        var replaced = await _service.StartAsync(Scope(fingerprint: Fp2, target: "https://app2.example.test/"));
        Assert.NotNull(replaced.SessionId);
        Assert.False(_store.IsAuthenticatedApiContextAvailable("dev", Fp));
        Assert.False(_store.IsAuthenticatedApiContextAvailable("dev", Fp2));
        Assert.False(replaced.AuthenticatedCredentialAvailable);
    }

    [Fact]
    public async Task ExpiryInvalidatesTheContextAndAsksForANewAuthenticatedAction()
    {
        await StartWithCredentialAsync();
        _now = _now.AddMinutes(31);
        var status = await _service.StatusAsync(Session());
        Assert.False(status.AuthenticatedCredentialAvailable);
        Assert.True(status.CredentialExpired);
        Assert.Equal(LocalHttpsProxyState.WaitingForAuthenticatedTraffic, status.State);
        Assert.Contains("Expired", status.Evidence);
        Assert.False(_store.IsAuthenticatedApiContextAvailable("dev", Fp));
    }

    [Fact]
    public async Task SessionLifetimeStopsTheProxyAndWipes()
    {
        await StartWithCredentialAsync();
        _now = _now.AddMinutes(121);
        var status = await _service.StatusAsync(Session());
        Assert.Equal(LocalHttpsProxyState.Stale, status.State);
        Assert.False(_store.IsAuthenticatedApiContextAvailable("dev", Fp));
    }

    [Fact]
    public async Task DisposeWipesEverything()
    {
        await StartWithCredentialAsync();
        _service.Dispose();
        Assert.False(_store.IsAuthenticatedApiContextAvailable("dev", Fp));
    }

    [Fact]
    public async Task RepeatedStartForTheSameEnvironmentReusesTheSession()
    {
        var port = await StartWithCredentialAsync();
        var again = await _service.StartAsync(Scope());
        Assert.Equal(_sessionId, again.SessionId);
        Assert.Equal(port, again.Port);
        Assert.True(again.AuthenticatedCredentialAvailable);
    }

    [Fact]
    public void EdgeLaunchArgumentsNeverTouchTheNormalProfileOrGlobalProxySettings()
    {
        var arguments = LocalHttpsProxyService.BuildEdgeArguments(8888, @"C:\Users\tester\AppData\Local\BirkNext\LocalHttpsProxyEdgeProfile", Target);
        Assert.Contains("--proxy-server=127.0.0.1:8888", arguments);
        Assert.Contains("--proxy-bypass-list=<-loopback>", arguments);
        Assert.Contains(Target, arguments);
        Assert.DoesNotContain(arguments, a => a.Contains("remote-debugging", StringComparison.OrdinalIgnoreCase));
        Assert.Throws<ArgumentException>(() => LocalHttpsProxyService.BuildEdgeArguments(8888, @"C:\Users\tester\AppData\Local\Microsoft\Edge\User Data", Target));
        Assert.Throws<ArgumentException>(() => LocalHttpsProxyService.BuildEdgeArguments(0, @"C:\Users\tester\AppData\Local\BirkNext\P", Target));
        Assert.Throws<ArgumentException>(() => LocalHttpsProxyService.BuildEdgeArguments(8888, @"C:\Users\tester\AppData\Local\BirkNext\P", "ftp://x"));
    }

    // ── test infrastructure ──────────────────────────────────────────────────

    /// <summary>Routes approved hosts to a fake TLS origin and everything else to a plain echo listener; accepts the fake origin certificate.</summary>
    private sealed class TestConnector(FakeUpstream upstream) : IUpstreamConnector
    {
        public RemoteCertificateValidationCallback? CertificateValidation => (_, _, _, _) => true;
        public async Task<Stream> ConnectAsync(string host, int port, CancellationToken cancellationToken)
        {
            var target = host is ApiHost or "app.example.test" or "app2.example.test" ? upstream.TlsPort : upstream.PlainPort;
            var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, target, cancellationToken);
            return tcp.GetStream();
        }
    }

    private sealed class FakeUpstream : IAsyncDisposable
    {
        private readonly TcpListener _tls = new(IPAddress.Loopback, 0);
        private readonly TcpListener _plain = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cts = new();
        private readonly X509Certificate2 _certificate = SelfSigned(ApiHost);
        private readonly List<string?> _authorization = [];
        private readonly List<byte[]> _plainPayloads = [];
        public int TlsPort => ((IPEndPoint)_tls.LocalEndpoint).Port;
        public int PlainPort => ((IPEndPoint)_plain.LocalEndpoint).Port;
        public volatile int NextStatus = 200;
        public IReadOnlyList<string?> AuthorizationHeaders { get { lock (_authorization) return _authorization.ToList(); } }
        public IReadOnlyList<byte[]> PlainPayloads { get { lock (_plainPayloads) return _plainPayloads.ToList(); } }

        public static Task<FakeUpstream> StartAsync()
        {
            var upstream = new FakeUpstream();
            upstream._tls.Start();
            upstream._plain.Start();
            _ = upstream.AcceptTlsAsync();
            _ = upstream.AcceptPlainAsync();
            return Task.FromResult(upstream);
        }

        private async Task AcceptTlsAsync()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var client = await _tls.AcceptTcpClientAsync(_cts.Token);
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            using var owned = client;
                            await using var ssl = new SslStream(client.GetStream(), false);
                            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = _certificate, ApplicationProtocols = [SslApplicationProtocol.Http11] }, _cts.Token);
                            using var reader = new BufferedNetworkReader(ssl);
                            while (await reader.ReadHeadAsync(65536, _cts.Token) is { } raw && HttpHead.TryParse(raw, out var request))
                            {
                                await reader.CopyBodyAsync(Stream.Null, BodyFraming.ForRequest(request), _cts.Token);
                                lock (_authorization) _authorization.Add(request.Header("Authorization"));
                                var status = NextStatus;
                                var body = "{\"ok\":true}";
                                await ssl.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {(status == 200 ? "OK" : "Denied")}\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}"), _cts.Token);
                                await ssl.FlushAsync(_cts.Token);
                                break;
                            }
                        }
                        catch { }
                    });
                }
            }
            catch { }
        }

        private async Task AcceptPlainAsync()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var client = await _plain.AcceptTcpClientAsync(_cts.Token);
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            using var owned = client;
                            var stream = client.GetStream();
                            var buffer = new byte[4096];
                            int read;
                            while ((read = await stream.ReadAsync(buffer, _cts.Token)) > 0)
                            {
                                lock (_plainPayloads) _plainPayloads.Add(buffer[..read]);
                                await stream.WriteAsync(buffer.AsMemory(0, read), _cts.Token);
                            }
                        }
                        catch { }
                    });
                }
            }
            catch { }
        }

        private static X509Certificate2 SelfSigned(string host)
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest($"CN={host}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName(host);
            request.CertificateExtensions.Add(san.Build());
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2));
            return new X509Certificate2(certificate.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.Exportable);
        }

        public ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _tls.Stop();
            _plain.Stop();
            _certificate.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Minimal browser stand-in: CONNECT through the proxy, then either raw bytes (pass-through) or TLS + HTTP/1.1 (interception).</summary>
    private sealed class ProxyClient : IDisposable
    {
        private readonly TcpClient _tcp;
        public Stream Stream { get; }
        private ProxyClient(TcpClient tcp, Stream stream) { _tcp = tcp; Stream = stream; }

        public static async Task<ProxyClient> ConnectAsync(int proxyPort, string authority)
        {
            var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, proxyPort);
            var stream = tcp.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"CONNECT {authority} HTTP/1.1\r\nHost: {authority}\r\n\r\n"));
            var head = await ReadHeadAsync(stream);
            Assert.StartsWith("HTTP/1.1 200", head);
            return new ProxyClient(tcp, stream);
        }

        public static async Task<string> InterceptedRequestAsync(int proxyPort, string host, string request)
        {
            // Stand-in for a browser, not part of the product. The upstream sends "Connection: close" and drops the socket after the
            // response; a strict SslStream client can see that as an abrupt reset while draining. curl and Edge tolerate it, so this
            // helper does too: the response head (which the content-asserting tests check) always arrives before the close, and the
            // proxy-side counters this test really asserts on are recorded independently.
            using var client = await ConnectAsync(proxyPort, $"{host}:443");
            await using var ssl = new SslStream(client.Stream, false, (_, _, _, _) => true);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host, ApplicationProtocols = [SslApplicationProtocol.Http11] });
            await ssl.WriteAsync(Encoding.ASCII.GetBytes(request));
            await ssl.FlushAsync();
            using var response = new MemoryStream();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await ssl.CopyToAsync(response, cts.Token); }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException) { }
            return Encoding.ASCII.GetString(response.ToArray());
        }

        public async Task<byte[]> ReadExactAsync(int count)
        {
            var buffer = new byte[count];
            var offset = 0;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (offset < count)
            {
                var read = await Stream.ReadAsync(buffer.AsMemory(offset), cts.Token);
                if (read <= 0) break;
                offset += read;
            }
            return buffer[..offset];
        }

        private static async Task<string> ReadHeadAsync(Stream stream)
        {
            var builder = new StringBuilder();
            var single = new byte[1];
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!builder.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                if (await stream.ReadAsync(single, cts.Token) <= 0) break;
                builder.Append((char)single[0]);
            }
            return builder.ToString();
        }

        public void Dispose() { Stream.Dispose(); _tcp.Dispose(); }
    }
}
