using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using BirkNext.Api.Services.FrontendAccessibility;
using BirkNext.Api.Services.FrontendBrowserRuntime;
using Deque.AxeCore.Commons;
using Microsoft.Extensions.Logging.Abstractions;

namespace BirkNext.Api.Tests.Unit.FrontendAccessibility;

[Trait("Category", "FrontendAccessibilityIntegration")]
[Collection("Frontend accessibility browser")]
public sealed class RealAxeIntegrationTests : IAsyncLifetime
{
    private LocalHtmlServer? _server;
    private FrontendAccessibilityReviewService? _service;

    public async Task InitializeAsync()
    {
        _server = new LocalHtmlServer();
        await _server.StartAsync();
        var options = Microsoft.Extensions.Options.Options.Create(new FrontendAccessibilityOptions { Enabled = true });
        _service = new FrontendAccessibilityReviewService(
            NullLogger<FrontendAccessibilityReviewService>.Instance,
            new BrowserTargetValidator(allowLoopback: true),
            new AccessibilityNormalizer(new AccessibilityEvidenceSanitizer()),
            options);
    }

    public async Task DisposeAsync()
    {
        if (_server is not null) await _server.DisposeAsync();
    }

    [Fact]
    public async Task Accessibility_HealthyPage_ExecutesRealAxe()
    {
        var healthy = await Review("/healthy");
        Assert.Equal(AccessibilityExecutionStatus.Assessed, healthy.ExecutionStatus);
        Assert.StartsWith("4.13", healthy.AxeVersion);
        Assert.Equal("Chromium", healthy.BrowserName);
        Assert.False(string.IsNullOrWhiteSpace(healthy.BrowserVersion));
        Assert.Equal(0, healthy.ViolationCount);
        Assert.True(healthy.PassCount > 0);
        Assert.Contains(FrontendAccessibilityReviewService.ManualTestingLimitation, healthy.Limitations);
    }

    [Fact]
    public async Task Accessibility_MissingButtonName_ReportsViolation()
    {
        var button = await Review("/button");
        var buttonFinding = Assert.Single(button.Findings.Where(f => f.RuleId == "button-name" && f.Kind == AccessibilityFindingKind.Violation));
        Assert.True(button.ViolationCount >= 1);
        Assert.Equal("critical", buttonFinding.Impact);
        Assert.Equal(AccessibilityFindingSeverity.Critical, buttonFinding.Severity);
        Assert.True(buttonFinding.AffectedNodeCount >= 1);
        Assert.Contains(buttonFinding.Selectors, selector => selector.Contains("#empty-button", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Accessibility_MissingImageAlt_ReportsViolation()
    {
        var image = await Review("/image");
        Assert.Contains(image.Findings, f => f.RuleId == "image-alt" && f.Kind == AccessibilityFindingKind.Violation);
        Assert.Contains(image.Findings.SelectMany(f => f.Selectors), selector => selector.Contains("#hero", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Accessibility_SecretEvidence_IsSanitized()
    {
        var secret = await Review("/secret");
        var evidence = JsonSerializer.Serialize(secret.Findings);
        Assert.DoesNotContain("SECRET-AXE-DOM-12345", evidence);
        Assert.Contains("#secret-input", evidence);
    }

    [Fact]
    public async Task Accessibility_Readiness_ReportsAxeAndChromiumVersions()
    {
        var readiness = await _service!.CheckReadinessAsync();
        Assert.True(readiness.Available, readiness.Error);
        Assert.Equal(AccessibilityReadinessState.Ready, readiness.State);
        Assert.StartsWith("4.13", readiness.AxeVersion);
        Assert.Equal("Chromium", readiness.BrowserName);
        Assert.False(string.IsNullOrWhiteSpace(readiness.BrowserVersion));
    }

    private async Task<AccessibilityReviewResult> Review(string path)
    {
        return await _service!.ReviewAsync(_server!.Url(path), new AccessibilityReviewOptions(StabilizationMs: 50));
    }

    private sealed class LocalHtmlServer : IAsyncDisposable
    {
        private TcpListener? _listener;
        private CancellationTokenSource? _cts;
        private Task? _loop;
        public int Port { get; private set; }
        public string Url(string path) => $"http://127.0.0.1:{Port}{path}";

        public Task StartAsync()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _cts = new CancellationTokenSource();
            _loop = AcceptAsync(_cts.Token);
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
                    var path = request.Split(' ')[1];
                    var body = Html(path);
                    var bytes = Encoding.UTF8.GetBytes(body);
                    var header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(header, ct);
                    await stream.WriteAsync(bytes, ct);
                }
                catch (OperationCanceledException) { break; }
            }
        }

        private static string Html(string path) => path switch
        {
            "/button" => Shell("<main><h1>Test</h1><button id='empty-button'></button></main>"),
            "/image" => Shell("<main><h1>Test</h1><img id='hero' src='data:image/gif;base64,R0lGODlhAQABAAAAACw='></main>"),
            "/secret" => Shell("<main><h1>Test</h1><input id='secret-input' value='SECRET-AXE-DOM-12345'></main>"),
            _ => Shell("<main><h1>Accessible test</h1><button type='button'>Continue</button><img alt='Decorative sample' src='data:image/gif;base64,R0lGODlhAQABAAAAACw='></main>")
        };

        private static string Shell(string body) => $"<!doctype html><html lang='en'><head><meta charset='utf-8'><title>Accessibility fixture</title></head><body>{body}</body></html>";

        public async ValueTask DisposeAsync()
        {
            _cts?.Cancel();
            _listener?.Stop();
            if (_loop is not null) try { await _loop; } catch { }
            _cts?.Dispose();
        }
    }

}

[CollectionDefinition("Frontend accessibility browser", DisableParallelization = true)]
public sealed class FrontendAccessibilityBrowserCollection;
