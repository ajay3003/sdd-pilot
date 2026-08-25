using System.Net;
using System.Net.Sockets;
using System.Text;
using BirkNext.Api.Services.FrontendBrowserRuntime;
using BirkNext.Api.Services.FrontendLighthouse;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace BirkNext.Api.Tests.Unit.FrontendLighthouse;

[Trait("Category", "FrontendLighthouseIntegration")]
[Collection("Frontend Lighthouse browser")]
public sealed class RealLighthouseIntegrationTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private LocalServer? _server;
    private FrontendLighthouseReviewService? _service;
    public RealLighthouseIntegrationTests(ITestOutputHelper output) => _output = output;

    public async Task InitializeAsync()
    {
        _server = new LocalServer();
        await _server.StartAsync();
        var runner = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "BirkNext.Api", "Tools", "LighthouseRunner", "run-lighthouse.mjs"));
        _service = new FrontendLighthouseReviewService(
            NullLogger<FrontendLighthouseReviewService>.Instance,
            new BrowserTargetValidator(allowLoopback: true),
            new LighthouseEvidenceSanitizer(new BrowserEvidenceSanitizer()), runner, "node");
    }

    public async Task DisposeAsync() { if (_server is not null) await _server.DisposeAsync(); }

    [Fact]
    public async Task Lighthouse_HealthyPage_ProducesRealLabMetrics()
    {
        var result = await _service!.ReviewAsync(_server!.Url("/healthy"));
        Assert.Equal(LighthouseExecutionStatus.Assessed, result.ExecutionStatus);
        Assert.Equal("12.2.1", result.LighthouseVersion);
        Assert.StartsWith("v", result.NodeVersion);
        Assert.Equal("Chromium", result.BrowserName);
        Assert.False(string.IsNullOrWhiteSpace(result.BrowserVersion));
        Assert.NotNull(result.PerformanceScore);
        Assert.Contains(result.Metrics, m => m.Name == "LCP" && m.ObservedValue.HasValue);
        Assert.Contains(result.Metrics, m => m.Name == "CLS" && m.ObservedValue.HasValue);
        Assert.Contains(result.Metrics, m => m.Name == "TBT" && m.ObservedValue.HasValue);
        Assert.Contains(result.Metrics, m => m.Name == "INP" && m.Status == LighthouseMetricStatus.FieldDataRequired);
        Assert.Equal("Lab", result.MeasurementType);
        Assert.False(result.FieldDataAvailable);
        _output.WriteLine("LighthouseVersion={0}; NodeVersion={1}; ChromiumVersion={2}; Score={3}; LCP={4}; CLS={5}; TBT={6}",
            result.LighthouseVersion, result.NodeVersion, result.BrowserVersion, result.PerformanceScore,
            result.Metrics.Single(m => m.Name == "LCP").ObservedValue,
            result.Metrics.Single(m => m.Name == "CLS").ObservedValue,
            result.Metrics.Single(m => m.Name == "TBT").ObservedValue);
    }

    [Fact]
    public async Task Lighthouse_LargeUnusedScript_ProducesActionableDiagnostic()
    {
        var result = await _service!.ReviewAsync(_server!.Url("/diagnostic"));
        Assert.Equal(LighthouseExecutionStatus.Assessed, result.ExecutionStatus);
        Assert.Contains(result.Audits, a => a.AuditId is "unused-javascript" or "total-byte-weight");
        _output.WriteLine("Diagnostics={0}", string.Join(", ", result.Audits.Select(a => $"{a.AuditId}:{a.DisplayValue}")));
    }

    [Fact]
    public async Task Lighthouse_Readiness_ReportsRealToolVersions()
    {
        var readiness = await _service!.CheckReadinessAsync();
        Assert.True(readiness.Available, readiness.Error);
        Assert.Equal("12.2.1", readiness.LighthouseVersion);
        Assert.StartsWith("v", readiness.NodeVersion);
        Assert.Equal("Chromium", readiness.BrowserName);
        Assert.False(string.IsNullOrWhiteSpace(readiness.BrowserVersion));
        _output.WriteLine("Available={0}; LighthouseVersion={1}; NodeVersion={2}; Browser={3} {4}", readiness.Available,
            readiness.LighthouseVersion, readiness.NodeVersion, readiness.BrowserName, readiness.BrowserVersion);
    }

    private sealed class LocalServer : IAsyncDisposable
    {
        private TcpListener? _listener;
        private CancellationTokenSource? _cts;
        private Task? _loop;
        public int Port { get; private set; }
        public string Url(string path) => $"http://127.0.0.1:{Port}{path}";
        public Task StartAsync()
        {
            _listener = new(IPAddress.Loopback, 0); _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port; _cts = new(); _loop = AcceptAsync(_cts.Token);
            return Task.CompletedTask;
        }
        private async Task AcceptAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using var client = await _listener!.AcceptTcpClientAsync(ct);
                    await using var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                    var request = await reader.ReadLineAsync(ct) ?? "GET /healthy HTTP/1.1";
                    while (!string.IsNullOrEmpty(await reader.ReadLineAsync(ct))) { }
                    var path = request.Split(' ')[1].Split('?')[0];
                    var isScript = path == "/unused.js";
                    var body = isScript ? LargeScript() : Html(path);
                    var bytes = Encoding.UTF8.GetBytes(body);
                    var header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: {(isScript ? "text/javascript" : "text/html")}; charset=utf-8\r\nCache-Control: no-store\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(header, ct); await stream.WriteAsync(bytes, ct);
                }
                catch (OperationCanceledException) { break; }
            }
        }
        private static string Html(string path) => path == "/diagnostic"
            ? "<!doctype html><html lang='en'><head><meta charset='utf-8'><title>Diagnostic</title><script src='/unused.js'></script></head><body><main><h1>Diagnostic</h1></main></body></html>"
            : "<!doctype html><html lang='en'><head><meta charset='utf-8'><title>Healthy</title></head><body><main><h1>Healthy</h1><p>Deterministic local content.</p></main></body></html>";
        private static string LargeScript() => string.Join("\n", Enumerable.Range(0, 12000).Select(i => $"function unusedFunction{i}() {{ return '{new string('x', 24)}'; }}"));
        public async ValueTask DisposeAsync()
        {
            _cts?.Cancel(); _listener?.Stop();
            if (_loop is not null) try { await _loop; } catch { }
            _cts?.Dispose();
        }
    }
}

[CollectionDefinition("Frontend Lighthouse browser", DisableParallelization = true)]
public sealed class FrontendLighthouseBrowserCollection;
