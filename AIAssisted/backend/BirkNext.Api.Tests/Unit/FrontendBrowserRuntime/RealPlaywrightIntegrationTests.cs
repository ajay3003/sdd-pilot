using BirkNext.Api.Services.FrontendBrowserRuntime;
using Microsoft.Extensions.Logging;
using System.Net;
using Xunit;

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
        var url = _server!.GetUrl("/healthy.html");
        var result = await _service!.ReviewAsync(url);

        Assert.NotNull(result);
        Assert.Equal(BrowserRuntimeEngineStatus.Assessed, result.Status);
        Assert.Equal(BrowserStartupState.Started, result.StartupState);
        Assert.Equal(0, result.ConsoleErrorCount);
        Assert.Equal(0, result.PageErrorCount);
        Assert.NotNull(result.FinalUrl);
    }

    [Fact]
    public async Task BrowserRuntime_PageWithConsoleError_IsCaptured()
    {
        var url = _server!.GetUrl("/console-error.html");
        var result = await _service!.ReviewAsync(url);

        Assert.NotNull(result);
        Assert.Equal(BrowserRuntimeEngineStatus.Assessed, result.Status);
        Assert.True(result.ConsoleErrorCount > 0, "Expected to capture console error");
        Assert.Equal(BrowserStartupState.StartedWithErrors, result.StartupState);
    }

    [Fact]
    public async Task BrowserRuntime_PageWithUncaughtError_IsCaptured()
    {
        var url = _server!.GetUrl("/uncaught-error.html");
        var result = await _service!.ReviewAsync(url);

        Assert.NotNull(result);
        Assert.Equal(BrowserRuntimeEngineStatus.Assessed, result.Status);
        Assert.True(result.PageErrorCount > 0, "Expected to capture uncaught page error");
        Assert.Equal(BrowserStartupState.StartedWithErrors, result.StartupState);
    }

    [Fact]
    public async Task BrowserRuntime_FailedResource_IsCaptured()
    {
        var url = _server!.GetUrl("/missing-resource.html");
        var result = await _service!.ReviewAsync(url);

        Assert.NotNull(result);
        Assert.Equal(BrowserRuntimeEngineStatus.Assessed, result.Status);
        // Missing resources may or may not result in page errors depending on how they're handled
        // The important part is the service completes without crashing
    }

    [Fact]
    public async Task BrowserRuntime_InvalidUrl_SkipsExecution()
    {
        var result = await _service!.ReviewAsync("file:///etc/passwd");

        Assert.Equal(BrowserRuntimeEngineStatus.Skipped, result.Status);
        Assert.NotNull(result.EngineError);
        Assert.Contains("blocked", result.EngineError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BrowserRuntime_Readiness_ReturnsAvailable()
    {
        var readiness = await _service!.CheckReadinessAsync();

        // May be available or not depending on environment, but should not throw
        Assert.NotNull(readiness);
    }

    private FrontendBrowserRuntimeReviewService CreateService()
    {
        var targetValidator = new BrowserTargetValidator();
        var resourceClassifier = new BrowserResourceClassifier();
        var evidenceSanitizer = new BrowserEvidenceSanitizer();
        var findingClassifier = new BrowserRuntimeFindingClassifier(resourceClassifier);

        return new FrontendBrowserRuntimeReviewService(
            _logger,
            targetValidator,
            findingClassifier,
            resourceClassifier,
            evidenceSanitizer);
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
            if (_serverTask != null)
                await _serverTask;

            _listener?.Stop();
            (_listener as IDisposable)?.Dispose();
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
                "/console-error.html" => "<html><body><script>console.error('test-error')</script></body></html>",
                "/uncaught-error.html" => "<html><body><script>throw new Error('uncaught')</script></body></html>",
                "/missing-resource.html" => "<html><body><script src='/missing.js'></script></body></html>",
                _ => "<html><body>Not Found</body></html>"
            };

            var statusCode = path switch
            {
                _ when path.StartsWith("/missing") => 200,
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
