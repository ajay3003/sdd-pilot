using Microsoft.Playwright;

namespace BirkNext.Web.PlaywrightTests.Fixtures;

/// <summary>
/// Where the browser tests find BirkNext — never the developer's own instance by default.
///
/// External (recommended, used by the isolated acceptance runs): set <c>BIRKNEXT_PLAYWRIGHT_FRONTEND_URL</c> and <c>BIRKNEXT_PLAYWRIGHT_BACKEND_URL</c>
/// to an already started, isolated BirkNext (its own ports and a disposable database). Nothing is started or stopped.
///
/// Self-started (opt-in): <c>BIRKNEXT_PLAYWRIGHT_START_SERVERS=1</c> lets the fixture start the API and web dev servers itself, on the dev ports
/// the web app's configuration expects (5000/5173) and only when both are free, against the database named by <c>BIRKNEXT_PLAYWRIGHT_DATABASE</c>
/// (default <c>birknext_playwright</c>, never the shared <c>birknext</c> database). It stops only the processes it started. PostgreSQL is started
/// with compose only when <c>BIRKNEXT_PLAYWRIGHT_START_POSTGRES=1</c>.
///
/// Neither configured: browser tests are reported as skipped with these prerequisites — never as passed.
///
/// Local session: the API only answers a browser that exchanged its launch code (the "Connect this browser" screen). Every browser context is
/// connected through <see cref="ConnectAsync"/> with the code from <c>BIRKNEXT_PLAYWRIGHT_LAUNCH_CODE</c>, the file named by
/// <c>BIRKNEXT_PLAYWRIGHT_LAUNCH_CODE_FILE</c> (the isolated API's <c>App_Data/local-session/launch-code.txt</c>), or — self-started only — the
/// repository API's own launch-code file. The code is never logged.
/// </summary>
public static class BrowserTestServers
{
    public const string FrontendVariable = "BIRKNEXT_PLAYWRIGHT_FRONTEND_URL";
    public const string BackendVariable = "BIRKNEXT_PLAYWRIGHT_BACKEND_URL";
    public const string StartVariable = "BIRKNEXT_PLAYWRIGHT_START_SERVERS";
    public const string DatabaseVariable = "BIRKNEXT_PLAYWRIGHT_DATABASE";
    public const string StartPostgresVariable = "BIRKNEXT_PLAYWRIGHT_START_POSTGRES";
    public const string AllowDevPortsVariable = "BIRKNEXT_PLAYWRIGHT_ALLOW_DEV_PORTS";
    public const string LaunchCodeVariable = "BIRKNEXT_PLAYWRIGHT_LAUNCH_CODE";
    public const string LaunchCodeFileVariable = "BIRKNEXT_PLAYWRIGHT_LAUNCH_CODE_FILE";
    public const string DefaultDatabase = "birknext_playwright";
    public const int DevApiPort = 5000, DevWebPort = 5173;

    private static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v.Trim() : null;

    public static string? ExternalFrontendUrl => Env(FrontendVariable)?.TrimEnd('/');
    public static string? ExternalBackendUrl => Env(BackendVariable)?.TrimEnd('/');
    public static bool External => ExternalFrontendUrl is not null && ExternalBackendUrl is not null;
    public static bool SelfStart => !External && Env(StartVariable) == "1";
    public static string Database => Env(DatabaseVariable) ?? DefaultDatabase;
    public static bool StartPostgres => Env(StartPostgresVariable) == "1";

    public static string FrontendUrl => ExternalFrontendUrl ?? $"http://localhost:{DevWebPort}";
    public static string BackendUrl => ExternalBackendUrl ?? $"http://localhost:{DevApiPort}";

    /// <summary>Why browser tests cannot run here, or null when they can.</summary>
    public static string? NotConfiguredReason()
    {
        if (External)
        {
            var devPorts = new[] { ExternalFrontendUrl!, ExternalBackendUrl! }.Where(u => Uri.TryCreate(u, UriKind.Absolute, out var x) && x.IsLoopback && x.Port is DevApiPort or DevWebPort).ToList();
            return devPorts.Count > 0 && Env(AllowDevPortsVariable) != "1"
                ? $"Browser test not run: {string.Join(", ", devPorts)} is a developer port (5000/5173); point {FrontendVariable}/{BackendVariable} at an isolated instance, or set {AllowDevPortsVariable}=1 deliberately."
                : null;
        }
        if (SelfStart) return Database.Equals("birknext", StringComparison.OrdinalIgnoreCase)
            ? $"Browser test not run: {DatabaseVariable} names the shared 'birknext' database; use a disposable one." : null;
        return $"Browser test not run (environment prerequisite): set {FrontendVariable} and {BackendVariable} to an isolated, already started BirkNext " +
               $"(its own ports and a disposable database), or set {StartVariable}=1 to let the fixture start its own on free dev ports with database '{DefaultDatabase}'.";
    }

    /// <summary>The launch code of the BirkNext under test, or null when none is available.</summary>
    public static string? LaunchCode(string? selfStartedApiDirectory = null)
    {
        if (Env(LaunchCodeVariable) is { } code) return code;
        var file = Env(LaunchCodeFileVariable)
                   ?? (SelfStart && selfStartedApiDirectory is not null ? Path.Combine(selfStartedApiDirectory, "App_Data", "local-session", "launch-code.txt") : null);
        return file is not null && File.Exists(file) ? File.ReadAllText(file).Trim() is { Length: > 0 } c ? c : null : null;
    }

    /// <summary>
    /// Connects <paramref name="context"/> to the BirkNext under test (the local-session cookie the launcher would set), so pages load real data instead
    /// of the "Connect this browser" screen. Without a launch code it does nothing and the pages show that screen — tests then fail on what they
    /// expected, not silently pass.
    /// </summary>
    public static async Task ConnectAsync(IBrowserContext context, string? selfStartedApiDirectory = null)
    {
        if (LaunchCode(selfStartedApiDirectory) is not { } code) return;
        var response = await context.APIRequest.PostAsync($"{BackendUrl}/api/local-session", new APIRequestContextOptions
        {
            DataObject = new { code },
            Headers = new Dictionary<string, string> { ["Origin"] = FrontendUrl, ["X-BirkNext-Client"] = "playwright-tests" },
        });
        if (!response.Ok)
            throw new InvalidOperationException($"Could not connect the test browser to {BackendUrl} (local session answered {response.Status}); check {LaunchCodeVariable}/{LaunchCodeFileVariable}.");
    }

    /// <summary>Why an acceptance test with its own variables cannot run (missing <paramref name="required"/>), or null.</summary>
    public static string? MissingVariables(params string[] required) =>
        required.Where(r => Env(r) is null).ToList() is { Count: > 0 } missing ? $"Browser acceptance not run (environment prerequisite): set {string.Join(" and ", missing)} to an isolated, already started BirkNext." : null;
}

/// <summary>A browser test: skipped with its exact prerequisite unless an isolated BirkNext is configured (see <see cref="BrowserTestServers"/>).</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class BrowserFactAttribute : FactAttribute
{
    public BrowserFactAttribute(params string[] requiredVariables)
    {
        var reason = requiredVariables.Length > 0 ? BrowserTestServers.MissingVariables(requiredVariables) : BrowserTestServers.NotConfiguredReason();
        if (reason is not null) Skip = reason;
    }
}

/// <summary>A data-driven browser test, skipped like <see cref="BrowserFactAttribute"/> when no isolated BirkNext is configured.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class BrowserTheoryAttribute : TheoryAttribute
{
    public BrowserTheoryAttribute()
    {
        if (BrowserTestServers.NotConfiguredReason() is { } reason) Skip = reason;
    }
}

/// <summary>Browser classes that share servers run one at a time (no port or database collisions between classes).</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class BrowserServersCollection
{
    public const string Name = "BirkNext browser servers";
}
