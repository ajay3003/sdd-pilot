using System.Net;
using System.Net.Sockets;
using BirkNext.Api.Data;
using BirkNext.Api.Services;
using BirkNext.Api.Services.LocalSecurity;
using BirkNext.Api.Tests.Utilities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using Npgsql;

namespace BirkNext.Api.Tests.BrowserVerification;

/// <summary>
/// An isolated BirkNext a real browser can use (Testoversikt V12, 2026-11-27). Nothing of the developer's BirkNext is started, read or changed:
/// <list type="bullet">
/// <item>API: the test assembly's own in-process host (<see cref="WebApplicationFactory{TEntryPoint}"/>), so the assembly's fail-closed guards apply —
/// the run's own PostgreSQL database <c>birknext_testrun_&lt;hex&gt;</c> (<see cref="TestDatabaseIsolation"/>; any other database stops startup) and a
/// temporary App_Data (<see cref="TestAppDataIsolation"/>). Real Npgsql, real migrations, real controllers and middleware.</item>
/// <item>A loopback-only relay (Kestrel on a free port, never 5000/5173) hands each browser request to that in-process server unchanged; the
/// socket peer it reports is loopback, which is what it is. No forwarded-for header is added (the API refuses relayed requests).</item>
/// <item>Frontend: the published BirkNext.Web (<c>BIRKNEXT_ISOLATED_WEB_ROOT</c>) served on a second free port, with <c>appsettings.json</c>'s
/// <c>BackendUrl</c> replaced by the relay — the published copy on disk is never edited and never points at 5000.</item>
/// <item>Browser: a fresh Chromium context per test; every request to any other origin is aborted and recorded (<see cref="Violations"/>).</item>
/// </list>
/// <see cref="ProveIsolation"/> checks the EFFECTIVE runtime (the database the API's context opened, the App_Data root it resolves, the launch-code
/// file, the bound ports) and throws before any browser step when one of them is not this run's own.
/// </summary>
public sealed class IsolatedBrowserHost : IAsyncLifetime
{
    public const string WebRootVariable = "BIRKNEXT_ISOLATED_WEB_ROOT";
    public const string HeadedVariable = "BIRKNEXT_ISOLATED_HEADED";
    public const string EvidenceVariable = "BIRKNEXT_ISOLATED_EVIDENCE_DIR";
    private static readonly int[] DeveloperPorts = [5000, 5001, 5173, 5097];

    private WebApplicationFactory<Program>? _factory;
    private WebApplication? _relay;
    private WebApplication? _web;
    private IPlaywright? _playwright;
    private readonly string _staging = Path.Combine(Path.GetTempPath(), "birknext-isolated-browser", Guid.NewGuid().ToString("N"));
    private string? _savedOrigin;

    public string ApiUrl { get; private set; } = "";
    public string WebUrl { get; private set; } = "";
    public IBrowser Browser { get; private set; } = null!;
    public WebApplicationFactory<Program> Factory => _factory ?? throw new InvalidOperationException("Host not started.");
    public IsolationProof Proof { get; private set; } = null!;
    public string EvidenceDirectory { get; } = Environment.GetEnvironmentVariable(EvidenceVariable) is { Length: > 0 } e ? e
        : Path.Combine(Path.GetTempPath(), "birknext-isolated-browser-evidence");

    /// <summary>Requests the browser tried to send anywhere else (aborted). A test fails when this is not empty.</summary>
    public List<string> Violations { get; } = [];

    /// <summary>Why the host cannot start here (no built frontend), or null. <see cref="WebRootVariable"/> is either a published wwwroot folder or
    /// a build's <c>BirkNext.Web.staticwebassets.runtime.json</c> (the asset manifest a Debug build writes; no publish needed).</summary>
    public static string? NotConfiguredReason() =>
        Environment.GetEnvironmentVariable(WebRootVariable) is { Length: > 0 } root
        && (File.Exists(Path.Combine(root, "index.html")) || root.EndsWith(".staticwebassets.runtime.json", StringComparison.OrdinalIgnoreCase) && File.Exists(root))
            ? null
            : $"Isolated browser verification not run (environment prerequisite): set {WebRootVariable} to a built BirkNext.Web " +
              "(its staticwebassets.runtime.json, or a published wwwroot); scripts/run-testoverview-browser-verification.ps1 builds one and runs these tests.";

    public async Task InitializeAsync()
    {
        // Not configured: every journey is reported SKIPPED with the prerequisite (IsolatedBrowserFact); nothing is started.
        if (NotConfiguredReason() is not null) return;
        // The run database must exist; without it hosts fail closed, and a browser run would only measure that failure.
        if (TestDatabaseIsolation.Unavailable is { } unavailable) throw new InvalidOperationException(unavailable);

        var webPort = FreeLoopbackPort();
        var apiPort = FreeLoopbackPort(webPort);
        WebUrl = $"http://localhost:{webPort}";
        ApiUrl = $"http://localhost:{apiPort}";

        // Program.cs reads FRONTEND_ORIGIN (CORS) while it builds, before WithWebHostBuilder's settings apply: the environment is the only way in.
        // It is restored as soon as the host has started (the collection runs alone, see IsolatedBrowserCollection).
        _savedOrigin = Environment.GetEnvironmentVariable("FRONTEND_ORIGIN");
        Environment.SetEnvironmentVariable("FRONTEND_ORIGIN", WebUrl);
        try
        {
            _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("FRONTEND_ORIGIN", WebUrl);
                builder.UseSetting("ProjectImport:StagingDirectory", _staging);
                // Logs go to this run's folder (never the API project's ./logs), and only warnings and errors.
                builder.UseSetting("LoggingSettings:LogPath", Path.Combine(_staging, "logs"));
                builder.UseSetting("LoggingSettings:MinimumLevel", "Warning");
                builder.ConfigureAppConfiguration((_, config) =>
                {
                    foreach (var local in config.Sources.OfType<Microsoft.Extensions.Configuration.Json.JsonConfigurationSource>().Where(s => s.Path?.Contains("Local") ?? false).ToList())
                        config.Sources.Remove(local);
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["FrontendBrowserRuntime:Enabled"] = "false", ["FrontendAccessibility:Enabled"] = "false", ["FrontendLighthouse:Enabled"] = "false",
                        ["FrontendPassiveSecurity:Enabled"] = "false", ["AuthenticatedReview:Enabled"] = "false",
                        ["LoggingSettings:LogPath"] = Path.Combine(_staging, "logs"), ["LoggingSettings:MinimumLevel"] = "Warning",
                    });
                });
            });
            _ = _factory.Server; // starts the host (migrations run against the run database)
        }
        finally { Environment.SetEnvironmentVariable("FRONTEND_ORIGIN", _savedOrigin); }

        _relay = await StartRelayAsync(apiPort, _factory);
        _web = await StartFrontendAsync(webPort, Environment.GetEnvironmentVariable(WebRootVariable)!, ApiUrl);
        Proof = ProveIsolation();

        _playwright = await Playwright.CreateAsync();
        Browser = await _playwright.Chromium.LaunchAsync(new() { Headless = Environment.GetEnvironmentVariable(HeadedVariable) != "1" });
    }

    public async Task DisposeAsync()
    {
        if (Browser is not null) await Browser.CloseAsync();
        _playwright?.Dispose();
        if (_web is not null) await _web.DisposeAsync();
        if (_relay is not null) await _relay.DisposeAsync();
        if (_factory is not null) await _factory.DisposeAsync();
        try { Directory.Delete(_staging, recursive: true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>A browser context connected to the isolated API (the launcher's launch-code exchange) whose requests can reach only the two
    /// isolated origins; everything else is aborted and recorded.</summary>
    public async Task<IBrowserContext> NewContextAsync()
    {
        var context = await Browser.NewContextAsync(new() { ViewportSize = new() { Width = 1440, Height = 1000 }, Locale = "nb-NO" });
        await context.RouteAsync("**/*", async route =>
        {
            var url = route.Request.Url;
            if (url.StartsWith(WebUrl + "/", StringComparison.OrdinalIgnoreCase) || url.StartsWith(ApiUrl + "/", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("data:", StringComparison.Ordinal) || url.StartsWith("blob:", StringComparison.Ordinal))
            {
                await route.ContinueAsync();
                return;
            }
            lock (Violations) Violations.Add($"{route.Request.Method} {url}");
            await route.AbortAsync("blockedbyclient");
        });
        await context.Tracing.StartAsync(new() { Screenshots = true, Snapshots = true });
        var code = Factory.Services.GetRequiredService<LocalSessionService>().CurrentCode;
        var response = await context.APIRequest.PostAsync($"{ApiUrl}/api/local-session", new()
        {
            DataObject = new { code },
            Headers = new Dictionary<string, string> { ["Origin"] = WebUrl, [LocalSessionService.ClientHeader] = "isolated-browser-tests" },
        });
        if (!response.Ok) throw new InvalidOperationException($"The isolated API refused the local-session exchange ({response.Status}).");
        return context;
    }

    /// <summary>Saves the context's trace (and a screenshot of each page) when a journey failed; nothing is kept for a passing journey.</summary>
    public async Task FinishContextAsync(IBrowserContext context, string name, bool failed)
    {
        if (failed)
        {
            Directory.CreateDirectory(EvidenceDirectory);
            var stamp = $"{name}-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
            var i = 0;
            foreach (var page in context.Pages)
                try { await page.ScreenshotAsync(new() { Path = Path.Combine(EvidenceDirectory, $"{stamp}-{i++}.png"), FullPage = true }); } catch (PlaywrightException) { }
            try { await context.Tracing.StopAsync(new() { Path = Path.Combine(EvidenceDirectory, $"{stamp}-trace.zip") }); } catch (PlaywrightException) { }
        }
        else
            try { await context.Tracing.StopAsync(); } catch (PlaywrightException) { }
        await context.CloseAsync();
    }

    /// <summary>An in-process API client with a local session (for fixture writes through the public API and for reading what the API returns).</summary>
    public HttpClient ApiClient()
    {
        var client = Factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri(ApiUrl) });
        client.DefaultRequestHeaders.Add(LocalSessionService.ClientHeader, "isolated-browser-tests");
        client.DefaultRequestHeaders.Add("Origin", WebUrl);
        var code = Factory.Services.GetRequiredService<LocalSessionService>().CurrentCode;
        using var exchange = new HttpRequestMessage(HttpMethod.Post, "api/local-session") { Content = System.Net.Http.Json.JsonContent.Create(new { code }) };
        var status = Task.Run(async () => { using var r = await client.SendAsync(exchange); return r.StatusCode; }).GetAwaiter().GetResult();
        if ((int)status is < 200 or > 299) throw new InvalidOperationException($"Local-session bootstrap failed in the isolated host: {(int)status}");
        return client;
    }

    /// <summary>The effective runtime, checked after start-up — not the variables that were meant to produce it.</summary>
    private IsolationProof ProveIsolation()
    {
        using var scope = Factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.GetDbConnection().Database;
        var contexts = new[]
        {
            scope.ServiceProvider.GetRequiredService<BirkNext.Api.Data.DeliveryReporting.DeliveryReportingDbContext>().Database.GetDbConnection().Database,
            scope.ServiceProvider.GetRequiredService<BirkNext.Api.Data.CanonicalInterpretation.CanonicalInterpretationDbContext>().Database.GetDbConnection().Database,
            scope.ServiceProvider.GetRequiredService<BirkNext.Api.Data.ScopeRegistry.ScopeRegistryDbContext>().Database.GetDbConnection().Database,
            scope.ServiceProvider.GetRequiredService<BirkNext.Api.Data.TaskAlignment.TaskAlignmentDbContext>().Database.GetDbConnection().Database,
            scope.ServiceProvider.GetRequiredService<BirkNext.Api.Data.SnapshotRegistry.SnapshotRegistryDbContext>().Database.GetDbConnection().Database,
            scope.ServiceProvider.GetRequiredService<BirkNext.Api.Data.IntegrationJourneys.IntegrationJourneysDbContext>().Database.GetDbConnection().Database,
        };
        var appData = Path.GetFullPath(AppDataLocation.Root(Factory.Services.GetRequiredService<IWebHostEnvironment>()));
        var realAppData = TestAppDataIsolation.ApiProjectFolder is { } api ? Path.GetFullPath(Path.Combine(api, "App_Data")) : null;
        var codePath = Path.GetFullPath(Factory.Services.GetRequiredService<LocalSessionService>().CodePath);
        var apiPort = new Uri(ApiUrl).Port;
        var webPort = new Uri(WebUrl).Port;
        var proof = new IsolationProof(database, contexts, TestDatabaseIsolation.RunDatabase, TestDatabaseIsolation.UserDatabase, appData, realAppData, codePath, apiPort, webPort,
            Factory.Services.GetRequiredService<IHostEnvironment>().EnvironmentName);

        var problems = new List<string>();
        if (!TestDatabaseIsolation.RunName().IsMatch(database) || database != TestDatabaseIsolation.RunDatabase) problems.Add($"API database is '{database}', not this run's {TestDatabaseIsolation.RunDatabase}");
        if (contexts.Any(c => c != database)) problems.Add($"a context opened another database ({string.Join(", ", contexts.Distinct())})");
        if (string.Equals(database, TestDatabaseIsolation.UserDatabase, StringComparison.OrdinalIgnoreCase)) problems.Add("API database is the developer's database");
        if (realAppData is null) problems.Add("the developer's App_Data could not be located, so its exclusion cannot be proven");
        else if (appData.StartsWith(realAppData, StringComparison.OrdinalIgnoreCase) || codePath.StartsWith(realAppData, StringComparison.OrdinalIgnoreCase))
            problems.Add("App_Data or the launch-code file is the developer's App_Data");
        if (!appData.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) problems.Add($"App_Data {appData} is not a temporary folder");
        if (DeveloperPorts.Contains(apiPort) || DeveloperPorts.Contains(webPort) || apiPort == webPort) problems.Add("a developer port is in use by the isolated host");
        if (proof.Environment != "Test") problems.Add($"hosting environment is {proof.Environment}");
        if (problems.Count > 0) throw new InvalidOperationException("Isolation not proven — no browser step runs: " + string.Join("; ", problems));
        return proof;
    }

    private static int FreeLoopbackPort(params int[] exclude)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            if (!DeveloperPorts.Contains(port) && !exclude.Contains(port)) return port;
        }
        throw new InvalidOperationException("No free loopback port.");
    }

    private static readonly HashSet<string> HopByHop = new(StringComparer.OrdinalIgnoreCase)
        { "Connection", "Keep-Alive", "Transfer-Encoding", "Upgrade", "Proxy-Connection", "TE", "Trailer" };

    /// <summary>Loopback relay: each request goes unchanged to the in-process server; its socket peer is the browser on this computer.</summary>
    private static async Task<WebApplication> StartRelayAsync(int port, WebApplicationFactory<Program> factory)
    {
        var invoker = new HttpMessageInvoker(factory.Server.CreateHandler(ctx =>
        {
            ctx.Connection.RemoteIpAddress = IPAddress.Loopback;
            ctx.Connection.LocalIpAddress = IPAddress.Loopback;
            ctx.Connection.LocalPort = port;
        }));
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = "Test", ContentRootPath = Path.GetTempPath() });
        builder.WebHost.UseKestrel().UseUrls($"http://localhost:{port}");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.Run(async ctx =>
        {
            using var request = new HttpRequestMessage(new HttpMethod(ctx.Request.Method), $"http://localhost:{port}{ctx.Request.PathBase}{ctx.Request.Path}{ctx.Request.QueryString}");
            var body = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(body, ctx.RequestAborted);
            if (body.Length > 0 || !HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method) && !HttpMethods.IsOptions(ctx.Request.Method))
            {
                body.Position = 0;
                request.Content = new StreamContent(body);
            }
            foreach (var (name, values) in ctx.Request.Headers)
            {
                if (HopByHop.Contains(name) || name.Equals("Host", StringComparison.OrdinalIgnoreCase)) continue;
                if (!request.Headers.TryAddWithoutValidation(name, values.ToArray())) request.Content?.Headers.TryAddWithoutValidation(name, values.ToArray());
            }
            request.Headers.Host = $"localhost:{port}";
            using var response = await invoker.SendAsync(request, ctx.RequestAborted);
            ctx.Response.StatusCode = (int)response.StatusCode;
            foreach (var header in response.Headers.Concat(response.Content.Headers))
                if (!HopByHop.Contains(header.Key)) ctx.Response.Headers[header.Key] = header.Value.ToArray();
            await response.Content.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
        });
        await app.StartAsync();
        return app;
    }

    /// <summary>The built frontend, unchanged on disk; only the served appsettings.json names the isolated API.</summary>
    private static async Task<WebApplication> StartFrontendAsync(int port, string webRoot, string apiUrl)
    {
        var files = new WebAssetFiles(webRoot);
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = "Test", ContentRootPath = Path.GetTempPath() });
        builder.WebHost.UseKestrel().UseUrls($"http://localhost:{port}");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        var settings = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(files.Resolve("appsettings.json")
            ?? throw new InvalidOperationException("The built frontend has no appsettings.json.")))!.AsObject();
        settings["BackendUrl"] = apiUrl;
        var served = settings.ToJsonString();
        var types = new FileExtensionContentTypeProvider();
        types.Mappings[".dat"] = "application/octet-stream";
        types.Mappings[".blat"] = "application/octet-stream";
        types.Mappings[".wasm"] = "application/wasm";
        types.Mappings[".webcil"] = "application/octet-stream";
        types.Mappings[".dll"] = "application/octet-stream";
        types.Mappings[".pdb"] = "application/octet-stream";
        types.Mappings[".json"] = "application/json";
        app.Run(async ctx =>
        {
            var path = Uri.UnescapeDataString(ctx.Request.Path.Value ?? "/").TrimStart('/');
            ctx.Response.Headers.CacheControl = "no-store";
            if (path.Equals("appsettings.json", StringComparison.OrdinalIgnoreCase))
            {
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync(served);
                return;
            }
            // Environment-specific settings would override BackendUrl: none is served.
            if (path.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase)) { ctx.Response.StatusCode = 404; return; }
            var file = path.Length == 0 ? null : files.Resolve(path);
            if (file is null)
            {
                // A file-like request that does not exist is a 404; anything else is a client-side route → index.html.
                if (Path.HasExtension(path)) { ctx.Response.StatusCode = 404; return; }
                file = files.Resolve("index.html")!;
            }
            ctx.Response.ContentType = types.TryGetContentType(file, out var type) ? type : "application/octet-stream";
            await ctx.Response.SendFileAsync(file);
        });
        await app.StartAsync();
        return app;
    }

    /// <summary>Static web assets of a published folder, or of a build through its runtime manifest (content roots + path tree + patterns).</summary>
    private sealed class WebAssetFiles
    {
        private readonly string? _folder;
        private readonly string[] _roots = [];
        private readonly System.Text.Json.JsonElement _tree;

        public WebAssetFiles(string location)
        {
            if (Directory.Exists(location)) { _folder = location; return; }
            using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(location));
            _roots = [.. json.RootElement.GetProperty("ContentRoots").EnumerateArray().Select(r => r.GetString()!)];
            _tree = json.RootElement.GetProperty("Root").Clone();
        }

        public string? Resolve(string relative)
        {
            if (relative.Contains("..", StringComparison.Ordinal)) return null;
            if (_folder is not null) { var f = Path.Combine(_folder, relative.Replace('/', Path.DirectorySeparatorChar)); return File.Exists(f) ? f : null; }
            var segments = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var visited = new List<(System.Text.Json.JsonElement Node, int Depth)>();
            var node = _tree;
            var i = 0;
            for (; ; i++)
            {
                visited.Add((node, i));
                if (i == segments.Length)
                {
                    if (node.TryGetProperty("Asset", out var asset) && asset.ValueKind == System.Text.Json.JsonValueKind.Object)
                        return Existing(_roots[asset.GetProperty("ContentRootIndex").GetInt32()], asset.GetProperty("SubPath").GetString()!);
                    break;
                }
                if (node.TryGetProperty("Children", out var children) && children.ValueKind == System.Text.Json.JsonValueKind.Object
                    && children.TryGetProperty(segments[i], out var child)) { node = child; continue; }
                break;
            }
            for (var v = visited.Count - 1; v >= 0; v--)
            {
                if (!visited[v].Node.TryGetProperty("Patterns", out var patterns) || patterns.ValueKind != System.Text.Json.JsonValueKind.Array) continue;
                var rest = string.Join('/', segments[visited[v].Depth..]);
                foreach (var pattern in patterns.EnumerateArray())
                    if (Existing(_roots[pattern.GetProperty("ContentRootIndex").GetInt32()], rest) is { } hit) return hit;
            }
            return null;
        }

        private static string? Existing(string root, string sub)
        {
            var f = Path.Combine(root, sub.Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(f) ? f : null;
        }
    }
}

/// <summary>What the isolated runtime actually used (reported in the verification report).</summary>
public sealed record IsolationProof(string Database, IReadOnlyList<string> OtherContexts, string RunDatabase, string? DeveloperDatabase, string AppData, string? DeveloperAppData,
    string LaunchCodePath, int ApiPort, int WebPort, string Environment);

/// <summary>Browser classes share one isolated host and run alone (FRONTEND_ORIGIN is set while the host builds; no other host may build then).</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class IsolatedBrowserCollection : ICollectionFixture<IsolatedBrowserHost>
{
    public const string Name = "Isolated browser (Testoversikt)";
}

/// <summary>A browser journey: reported SKIPPED with its exact prerequisite when no published frontend is configured — never as passed.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class IsolatedBrowserFactAttribute : FactAttribute
{
    public IsolatedBrowserFactAttribute() { if (IsolatedBrowserHost.NotConfiguredReason() is { } reason) Skip = reason; }
}
