using BirkNext.Api.Services.FrontendBrowserRuntime;
using BirkNext.Api.Tests.TestInfrastructure;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Net;
using Xunit;
using Xunit.Abstractions;

namespace BirkNext.Api.Tests.Unit.FrontendBrowserRuntime;

/// <summary>
/// Real Playwright integration tests that invoke ACTUAL Chromium browser.
/// These tests verify the production FrontendBrowserRuntimeReviewService works correctly.
/// </summary>
[Trait("Category", "FrontendBrowserRuntimeIntegration")]
public sealed class RealPlaywrightIntegrationTests : IAsyncLifetime
{
    private SimpleHttpTestServer? _server;
    private FrontendBrowserRuntimeReviewService? _service;
    private readonly ILogger<FrontendBrowserRuntimeReviewService> _logger = new TestLogger();
    private readonly ITestOutputHelper _output;

    public RealPlaywrightIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public async Task InitializeAsync()
    {
        _server = new SimpleHttpTestServer();
        await _server.StartAsync();

        _service = CreateService();
    }

    public async Task DisposeAsync()
    {
        if (_server != null)
            await _server.StopAsync();
    }

    [Fact]
    public async Task BrowserRuntime_HealthyPage_StartsSuccessfully()
    {
        if (!ExternalFrontendQualityTestGate.IsEnabled) return;
        var url = _server!.GetUrl("/healthy.html");
        var result = await _service!.ReviewAsync(url);
        WriteResult("healthy", result);

        Assert.NotNull(result);
        Assert.Equal(BrowserRuntimeEngineStatus.Assessed, result.Status);
        Assert.Equal(BrowserStartupState.Started, result.StartupState);
        Assert.Equal(0, result.ConsoleErrorCount);
        Assert.Equal(0, result.PageErrorCount);
        Assert.NotNull(result.FinalUrl);
        Assert.Equal("Chromium", result.BrowserName);
        Assert.False(string.IsNullOrWhiteSpace(result.BrowserVersion));
        Assert.NotEqual("1.48.0.0", result.BrowserVersion);
    }

    [Fact]
    public async Task BrowserRuntime_PageWithConsoleError_IsCaptured()
    {
        if (!ExternalFrontendQualityTestGate.IsEnabled) return;
        var url = _server!.GetUrl("/console-error.html");
        var result = await _service!.ReviewAsync(url);
        WriteResult("console-error", result);

        Assert.NotNull(result);
        Assert.Equal(BrowserRuntimeEngineStatus.Assessed, result.Status);
        Assert.True(result.ConsoleErrorCount > 0, "Expected to capture console error");
        Assert.Contains(result.Findings ?? [], finding =>
            finding.Category == "ConsoleError" &&
            finding.Description.Contains("runtime-test-error", StringComparison.Ordinal));
        Assert.Equal(BrowserStartupState.StartedWithErrors, result.StartupState);
    }

    [Fact]
    public async Task BrowserRuntime_PageWithUncaughtError_IsCaptured()
    {
        if (!ExternalFrontendQualityTestGate.IsEnabled) return;
        var url = _server!.GetUrl("/uncaught-error.html");
        var result = await _service!.ReviewAsync(url);
        WriteResult("page-error", result);

        Assert.NotNull(result);
        Assert.Equal(BrowserRuntimeEngineStatus.Assessed, result.Status);
        Assert.True(result.PageErrorCount > 0, "Expected to capture uncaught page error");
        Assert.Equal(BrowserStartupState.StartedWithErrors, result.StartupState);
    }

    [Fact]
    public async Task BrowserRuntime_FailedResource_IsCaptured()
    {
        if (!ExternalFrontendQualityTestGate.IsEnabled) return;
        var url = _server!.GetUrl("/missing-resource.html");
        var result = await _service!.ReviewAsync(url);
        WriteResult("failed-resource", result);

        Assert.NotNull(result);
        Assert.Equal(BrowserRuntimeEngineStatus.Assessed, result.Status);
        Assert.Contains(result.Findings ?? [], finding =>
            finding.Category == "ResourceFailure" &&
            finding.Evidence?.Any(evidence => evidence.Contains("missing.js", StringComparison.Ordinal)) == true);
    }

    [Fact]
    public async Task BrowserRuntime_InvalidUrl_SkipsExecution()
    {
        if (!ExternalFrontendQualityTestGate.IsEnabled) return;
        var result = await _service!.ReviewAsync("file:///etc/passwd");

        Assert.Equal(BrowserRuntimeEngineStatus.Skipped, result.Status);
        Assert.NotNull(result.EngineError);
        Assert.Contains("not allowed", result.EngineError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BrowserRuntime_Readiness_ReturnsAvailable()
    {
        if (!ExternalFrontendQualityTestGate.IsEnabled) return;
        var readiness = await _service!.CheckReadinessAsync();
        _output.WriteLine("readiness: {0}", JsonSerializer.Serialize(readiness));

        // May be available or not depending on environment, but should not throw
        Assert.NotNull(readiness);
        Assert.True(readiness.IsAvailable, readiness.ErrorMessage);
        Assert.Equal("Chromium", readiness.BrowserName);
        Assert.False(string.IsNullOrWhiteSpace(readiness.BrowserVersion));
        Assert.NotEqual("1.48.0.0", readiness.BrowserVersion);
        Assert.Null(readiness.ErrorMessage);
    }

    private void WriteResult(string scenario, BrowserRuntimeResult result) =>
        _output.WriteLine("{0}: {1}", scenario, JsonSerializer.Serialize(result));

    private FrontendBrowserRuntimeReviewService CreateService()
    {
        var targetValidator = new BrowserTargetValidator(allowLoopback: true);
        var resourceClassifier = new BrowserResourceClassifier();
        var evidenceSanitizer = new BrowserEvidenceSanitizer();
        var findingClassifier = new BrowserRuntimeFindingClassifier(resourceClassifier);
        var options = Microsoft.Extensions.Options.Options.Create(new FrontendBrowserRuntimeOptions { Enabled = true });

        return new FrontendBrowserRuntimeReviewService(
            _logger,
            targetValidator,
            findingClassifier,
            resourceClassifier,
            evidenceSanitizer,
            options);
    }

    // ── Simple Test HTTP Server ────────────────────────────────────
    private sealed class SimpleHttpTestServer
    {
        private HttpListener? _listener;
        private CancellationTokenSource? _cts;
        private Task? _serverTask;
        private string _port = "9999";

        public string GetUrl(string path) => $"http://localhost:{_port}{path}";

        public async Task StartAsync()
        {
            _listener = new HttpListener();

            // Find an available port by trying to bind; HttpListener with port 0 doesn't work,
            // so try ports starting from 9999 until one succeeds
            int basePort = 9999;
            int maxAttempts = 10;
            for (int i = 0; i < maxAttempts; i++)
            {
                int attemptPort = basePort + i;
                try
                {
                    var prefix = $"http://localhost:{attemptPort}/";
                    _listener.Prefixes.Clear();
                    _listener.Prefixes.Add(prefix);
                    _listener.Start();
                    _port = attemptPort.ToString();
                    break;
                }
                catch (HttpListenerException)
                {
                    if (i == maxAttempts - 1) throw;
                    _listener.Close();
                    _listener = new HttpListener();
                }
            }

            _cts = new CancellationTokenSource();
            _serverTask = RunServerAsync(_cts.Token);

            // Give server time to start
            await Task.Delay(100);
        }

        public async Task StopAsync()
        {
            _cts?.Cancel();
            _listener?.Stop();
            if (_serverTask != null)
                await _serverTask;

            (_listener as IDisposable)?.Dispose();
            _cts?.Dispose();
        }

        private async Task RunServerAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested && _listener != null)
                {
                    var context = await _listener.GetContextAsync();
                    _ = HandleRequestAsync(context);
                }
            }
            catch (HttpListenerException) when (ct.IsCancellationRequested)
            {
                // Server stopped normally
            }
        }

        private async Task HandleRequestAsync(HttpListenerContext context)
        {
            var path = context.Request.Url?.AbsolutePath ?? "/";
            var html = path switch
            {
                "/healthy.html" => "<html><body>Healthy</body></html>",
                "/console-error.html" => "<html><body><script>console.error('runtime-test-error')</script></body></html>",
                "/uncaught-error.html" => "<html><body><script>throw new Error('uncaught')</script></body></html>",
                "/missing-resource.html" => "<html><body><script src='/missing.js'></script></body></html>",
                _ => "<html><body>Not Found</body></html>"
            };

            var statusCode = path switch
            {
                "/missing.js" => 404,
                _ => 200
            };

            context.Response.StatusCode = statusCode;
            context.Response.ContentType = "text/html";

            var buffer = System.Text.Encoding.UTF8.GetBytes(html);
            context.Response.OutputStream.Write(buffer, 0, buffer.Length);
            context.Response.Close();

            await Task.CompletedTask;
        }
    }

    private sealed class TestLogger : ILogger<FrontendBrowserRuntimeReviewService>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
    }
}
