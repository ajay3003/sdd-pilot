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
    public string FrontendUrl => BrowserTestServers.FrontendUrl;
    public string BackendUrl => BrowserTestServers.BackendUrl;

    public async Task InitializeAsync()
    {
        try
        {
            if (BrowserTestServers.NotConfiguredReason() is { } reason) throw new InvalidOperationException(reason);
            // Verify backend and frontend are available (the configured isolated instance)
            await VerifyServiceReadyAsync(BackendUrl, "Backend", timeoutMs: 30000);
            await VerifyServiceReadyAsync(FrontendUrl, "Frontend", timeoutMs: 30000);

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

    private async Task VerifyServiceReadyAsync(string url, string serviceName, int timeoutMs = 30000, int checkIntervalMs = 500)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        Exception? lastException = null;

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                // Use HTTP GET to verify actual service readiness, not just TCP connection
                using var handler = new HttpClientHandler { UseProxy = false };
                using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
                // IPv4 loopback explicitly (a "localhost" lookup may try ::1 first, where the isolated servers do not listen).
                var response = await client.GetAsync(url.Replace("://localhost:", "://127.0.0.1:", StringComparison.OrdinalIgnoreCase));

                // Any answer below 500 means the service is responding (the API answers 401 to requests without the web client's header).
                if ((int)response.StatusCode < 500)
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
            $"{serviceName} did not become ready within {timeoutMs}ms at {url}. " +
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
        await BrowserTestServers.ConnectAsync(_context);
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
