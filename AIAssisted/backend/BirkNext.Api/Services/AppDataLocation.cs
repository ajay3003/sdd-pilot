using Path = System.IO.Path;

namespace BirkNext.Api.Services;

/// <summary>
/// Where BirkNext keeps its own mutable files (Test Data catalog, Critical E2E history, local session, credentials, backups, …). By default the
/// content root's <c>App_Data</c> folder, unchanged for development. A Tester Package launcher sets <see cref="Variable"/> to the per-user data
/// directory so nothing mutable is written inside the extracted application folder (which an upgrade replaces). Only a fully qualified path is
/// honoured; anything else keeps the default.
/// </summary>
public static class AppDataLocation
{
    public const string Variable = "BIRKNEXT_APP_DATA_DIR";

    public static string? Override => Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } value && Path.IsPathFullyQualified(value) ? value : null;

    public static string Root(IHostEnvironment environment) => Root(environment.ContentRootPath);

    public static string Root(string contentRoot) => Override ?? Path.Combine(Redirected(contentRoot), "App_Data");

    /// <summary>Tests only: hosts whose content root is <c>From</c> (the API project folder itself) use <c>To</c> instead, so a test host never writes
    /// the developer's own App_Data (launch code, settings, stores). Tests with their own content root are unaffected. Null in BirkNext itself.</summary>
    internal static (string From, string To)? ContentRootRedirect { get; set; }

    private static string Redirected(string contentRoot) =>
        ContentRootRedirect is { } r && string.Equals(Path.GetFullPath(contentRoot).TrimEnd('\\', '/'), Path.GetFullPath(r.From).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)
            ? r.To : contentRoot;

    /// <summary>The Microsoft sign-in settings file (public client values only).</summary>
    public static string SignInSettingsPath(string contentRoot) => Path.Combine(Root(contentRoot), "microsoft-sign-in.json");
}
