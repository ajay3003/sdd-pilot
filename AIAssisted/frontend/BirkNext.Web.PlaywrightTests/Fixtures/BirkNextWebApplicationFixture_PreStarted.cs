using Microsoft.Playwright;
using System.Net;
using System.Net.Sockets;

namespace BirkNext.Web.PlaywrightTests.Fixtures;

/// <summary>
/// Simplified Playwright fixture for pre-started application infrastructure.
///
/// This fixture assumes backend and frontend are already running on their default ports:
/// - Backend: http://localhost:5000
/// - Frontend: http://localhost:5173
///
/// Use this for CI/CD environments where services are started separately and managed
/// by orchestration tooling (docker-compose, etc.).
///
/// For local development that needs to start services automatically,
/// use BirkNextWebApplicationFixture instead.
/// </summary>
public sealed class BirkNextWebApplicationFixture_PreStarted : IAsyncLifetime
{
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IBrowserContext? _context;

    public IBrowserContext Context => _context ?? throw new InvalidOperationException("Browser context not initialized");
    public string FrontendUrl => "http://localhost:5173";
    public string BackendUrl => "http://localhost:5000";

    public async Task InitializeAsync()
    {
        try
        {
            // Verify backend is available
            await VerifyServiceReadyAsync(5000, "Backend", timeoutMs: 30000);

            // Verify frontend is available
            await VerifyServiceReadyAsync(5173, "Frontend", timeoutMs: 30000);

            // Initialize Playwright
            await InitializePlaywrightAsync();
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    public async Task DisposeAsync()
    {
        if (_context != null)
        {
            await _context.CloseAsync();
        }

        if (_browser != null)
        {
            await _browser.CloseAsync();
        }

        _playwright?.Dispose();
    }

    private async Task VerifyServiceReadyAsync(int port, string serviceName, int timeoutMs = 30000, int checkIntervalMs = 500)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        string url = $"http://127.0.0.1:{port}";
        Exception? lastException = null;

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                // Use HTTP GET to verify actual service readiness, not just TCP connection
                using var handler = new HttpClientHandler { UseProxy = false };
                using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(1000) };
                var response = await client.GetAsync(url);

                // Accept any successful response or redirect (even 404 means service is responding)
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound ||
                    response.StatusCode == System.Net.HttpStatusCode.MovedPermanently ||
                    response.StatusCode == System.Net.HttpStatusCode.OK)
                {
                    return; // Service is ready
                }
            }
            catch
            {
                // Service not ready yet, continue polling
            }

            await Task.Delay(checkIntervalMs);
        }

        throw new TimeoutException(
            $"{serviceName} on port {port} did not become ready within {timeoutMs}ms at {url}. " +
            $"Last error: {lastException?.Message ?? "No response"}");
    }

    private async Task InitializePlaywrightAsync()
    {
        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true,
        });

        _context = await _browser.NewContextAsync();
    }

    private static bool IsPortReachable(int port)
    {
        try
        {
            using var client = new TcpClient();
            var result = client.BeginConnect("localhost", port, null, null);
            bool connected = result.AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(500));

            if (connected && client.Connected)
            {
                client.EndConnect(result);
                client.Close();
                return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }
}
