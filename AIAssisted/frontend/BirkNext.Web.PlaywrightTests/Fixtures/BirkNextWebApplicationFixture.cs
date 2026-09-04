using Microsoft.Playwright;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace BirkNext.Web.PlaywrightTests.Fixtures;

/// <summary>
/// Test fixture that starts both backend and frontend applications,
/// waits for them to be ready, and provides a Playwright browser instance.
///
/// This fixture orchestrates the full-stack startup needed for Playwright
/// browser testing of the WASM frontend with real backend integration.
/// </summary>
public sealed class BirkNextWebApplicationFixture : IAsyncLifetime
{
#if DEBUG
    private const string BuildConfiguration = "Debug";
#else
    private const string BuildConfiguration = "Release";
#endif

    private readonly string _repoRoot;
    private readonly string _backendPath;
    private readonly string _frontendPath;

    private Process? _backendProcess;
    private Process? _frontendProcess;
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IBrowserContext? _context;

    public IBrowserContext Context => _context ?? throw new InvalidOperationException("Browser context not initialized");
    public string FrontendUrl => "http://localhost:5173";
    public string BackendUrl => "http://localhost:5000";

    public BirkNextWebApplicationFixture()
    {
        // Find repo root from current test project location
        var testProjectDir = AppDomain.CurrentDomain.BaseDirectory;
        _repoRoot = FindRepositoryRoot(testProjectDir);

        _backendPath = Path.Combine(_repoRoot, "AIAssisted", "backend");
        _frontendPath = Path.Combine(_repoRoot, "AIAssisted", "frontend");
    }

    public async Task InitializeAsync()
    {
        // 1. Verify paths exist
        if (!Directory.Exists(_backendPath))
            throw new DirectoryNotFoundException($"Backend path not found: {_backendPath}");
        if (!Directory.Exists(_frontendPath))
            throw new DirectoryNotFoundException($"Frontend path not found: {_frontendPath}");

        try
        {
            // 2. Start backend (extended timeout for migrations on first run)
            await StartBackendAsync();

            // 3. Start frontend
            await StartFrontendAsync();

            // 4. Initialize Playwright
            await InitializePlaywrightAsync();
        }
        catch
        {
            // Clean up on initialization failure
            await DisposeAsync();
            throw;
        }
    }

    public async Task DisposeAsync()
    {
        // Clean up in reverse order
        if (_context != null)
        {
            await _context.CloseAsync();
        }

        if (_browser != null)
        {
            await _browser.CloseAsync();
        }

        _playwright?.Dispose();

        // Stop processes
        StopProcess(_frontendProcess, "Frontend");
        StopProcess(_backendProcess, "Backend");
        await WaitForPortReleasedAsync(5173);
        await WaitForPortReleasedAsync(5000);
    }

    private async Task StartBackendAsync()
    {
        // Check if port 5000 is available
        if (IsPortInUse(5000))
        {
            throw new InvalidOperationException(
                "Backend port 5000 is already in use. Please stop any existing BirkNext backend process.");
        }

        // Ensure PostgreSQL is running before starting backend
        await EnsurePostgresqlAsync();

        var apiProjectPath = Path.Combine(_backendPath, "BirkNext.Api", "BirkNext.Api.csproj");
        if (!File.Exists(apiProjectPath))
        {
            throw new FileNotFoundException($"Backend project not found: {apiProjectPath}");
        }

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"run --project \"{apiProjectPath}\" --no-build --configuration {BuildConfiguration}",
            WorkingDirectory = _backendPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        // Set database connection string (uses default local postgres)
        psi.Environment["ConnectionStrings__Default"] =
            "Host=localhost;Port=5432;Database=birknext;Username=birknext;Password=birknext";
        psi.Environment["ASPNETCORE_URLS"] = "http://localhost:5000";
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";

        _backendProcess = Process.Start(psi);
        if (_backendProcess == null)
        {
            throw new InvalidOperationException("Failed to start backend process");
        }

        DrainProcessOutput(_backendProcess);

        // Wait for backend to be ready (extended timeout for migrations on first run)
        await WaitForPortReadyAsync(5000, "Backend", maxRetries: 120, delayMs: 500);
    }

    private async Task EnsurePostgresqlAsync()
    {
        // Check if PostgreSQL port is already available
        if (IsPortReachable(5432))
        {
            return; // PostgreSQL already running
        }

        // Try to start PostgreSQL using docker compose
        var composeFilePath = Path.Combine(_repoRoot, "AIAssisted", "docker-compose.yml");
        if (!File.Exists(composeFilePath))
        {
            throw new InvalidOperationException(
                $"PostgreSQL is not running and docker-compose.yml not found at {composeFilePath}. " +
                "Please start PostgreSQL manually or ensure Docker/Podman is available.");
        }

        try
        {
            // Detect available container runtime
            var runtime = TryDetectContainerRuntime();
            if (string.IsNullOrEmpty(runtime))
            {
                throw new InvalidOperationException(
                    "No container runtime (docker/podman) found. Install Docker or Podman to run PostgreSQL.");
            }

            // Start PostgreSQL container
            var psi = new ProcessStartInfo
            {
                FileName = runtime,
                Arguments = $"compose -f \"{composeFilePath}\" up -d postgres",
                WorkingDirectory = Path.Combine(_repoRoot, "AIAssisted"),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            var process = Process.Start(psi);
            if (process == null)
            {
                throw new InvalidOperationException("Failed to start PostgreSQL container");
            }

            process.WaitForExit(30000);

            // Wait for PostgreSQL to be ready
            await WaitForPortReadyAsync(5432, "PostgreSQL", maxRetries: 30, delayMs: 1000);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Failed to start PostgreSQL via docker-compose. Please start it manually: " +
                "cd AIAssisted && docker-compose up -d postgres", ex);
        }
    }

    private static string? TryDetectContainerRuntime()
    {
        // Try docker first
        if (ExecutableExists("docker"))
            return "docker";

        // Try podman second
        if (ExecutableExists("podman"))
            return "podman";

        return null;
    }

    private static bool ExecutableExists(string executableName)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = executableName,
                Arguments = "--version",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            var process = Process.Start(psi);
            if (process == null)
                return false;

            process.WaitForExit(5000);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private async Task StartFrontendAsync()
    {
        // Check if port 5173 is available
        if (IsPortInUse(5173))
        {
            throw new InvalidOperationException(
                "Frontend port 5173 is already in use. Please stop any existing BirkNext frontend process.");
        }

        var webProjectPath = Path.Combine(_frontendPath, "BirkNext.Web", "BirkNext.Web.csproj");
        if (!File.Exists(webProjectPath))
        {
            throw new FileNotFoundException($"Frontend project not found: {webProjectPath}");
        }

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"run --project \"{webProjectPath}\" --no-build --configuration {BuildConfiguration}",
            WorkingDirectory = _frontendPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        _frontendProcess = Process.Start(psi);
        if (_frontendProcess == null)
        {
            throw new InvalidOperationException("Failed to start frontend process");
        }

        DrainProcessOutput(_frontendProcess);

        // Wait for frontend to be ready
        await WaitForPortReadyAsync(5173, "Frontend");
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

    private static bool IsPortInUse(int port)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return false;
        }
        catch (SocketException)
        {
            return true;
        }
    }

    private static async Task WaitForPortReleasedAsync(int port)
    {
        for (var attempt = 0; attempt < 100 && IsPortInUse(port); attempt++)
            await Task.Delay(50);
    }

    private static async Task WaitForPortReadyAsync(int port, string serviceName, int maxRetries = 30, int delayMs = 1000)
    {
        for (int i = 0; i < maxRetries; i++)
        {
            if (IsPortReachable(port))
            {
                return; // Port is ready
            }

            await Task.Delay(delayMs);
        }

        throw new TimeoutException(
            $"{serviceName} port {port} did not become ready within {maxRetries * delayMs}ms");
    }

    private static bool IsPortReachable(int port)
    {
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
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

    private static void StopProcess(Process? process, string name)
    {
        if (process == null || process.HasExited)
        {
            return;
        }

        try
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
        }
        catch
        {
            // Process may have already exited
        }
    }

    private static void DrainProcessOutput(Process process)
    {
        process.OutputDataReceived += static (_, _) => { };
        process.ErrorDataReceived += static (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
    }

    private static string FindRepositoryRoot(string startPath)
    {
        var current = new DirectoryInfo(startPath);

        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "azure-pipelines.yml")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException(
            "Could not find repository root. Expected azure-pipelines.yml in ancestor directories.");
    }
}
