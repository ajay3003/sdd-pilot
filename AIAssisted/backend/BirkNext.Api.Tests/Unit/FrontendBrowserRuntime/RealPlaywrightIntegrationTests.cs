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
    private LoopbackHttpTestServer? _server;
    private FrontendBrowserRuntimeReviewService? _service;
    private readonly ILogger<FrontendBrowserRuntimeReviewService> _logger = new TestLogger();
    private readonly ITestOutputHelper _output;

    public RealPlaywrightIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public Task InitializeAsync()
    {
        _server = new LoopbackHttpTestServer(ServePageAsync);
        _server.Start();

        _service = CreateService();
        return Task.CompletedTask;
    }

    // Deterministic shutdown (see LoopbackHttpTestServer): a stopped listener's pending accept is a normal stop on every
    // platform; any other server failure fails the test here.
    public async Task DisposeAsync()
    {
        if (_server != null)
            await _server.DisposeAsync();
    }

    [Fact]
    public async Task BrowserRuntime_HealthyPage_StartsSuccessfully()
    {
        if (!ExternalFrontendQualityTestGate.IsEnabled) return;
        var url = _server!.Url("/healthy.html");
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
        var url = _server!.Url("/console-error.html");
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
        var url = _server!.Url("/uncaught-error.html");
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
        var url = _server!.Url("/missing-resource.html");
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

    // ── Test pages served by the loopback server ──────────────────────────
    private static async Task ServePageAsync(HttpListenerContext context)
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

        context.Response.StatusCode = path == "/missing.js" ? 404 : 200;
        context.Response.ContentType = "text/html";
        var buffer = System.Text.Encoding.UTF8.GetBytes(html);
        await context.Response.OutputStream.WriteAsync(buffer);
        context.Response.Close();
    }

    private sealed class TestLogger : ILogger<FrontendBrowserRuntimeReviewService>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
    }
}
