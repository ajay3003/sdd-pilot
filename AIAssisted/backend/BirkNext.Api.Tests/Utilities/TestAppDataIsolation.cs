using System.Runtime.CompilerServices;
using BirkNext.Api.Services;

namespace BirkNext.Api.Tests.Utilities;

/// <summary>
/// Keeps every test host away from the developer's App_Data (2026-11-24). WebApplicationFactory hosts use the API project folder as content root,
/// so they read and wrote the developer's own App_Data: a full suite rotated the running BirkNext's launch code (local-session/launch-code.txt), so
/// the developer could no longer sign in, and wrote settings, audit and stores into the real installation. Hosts with that content root now use a
/// temporary folder of this run (removed afterwards); tests with their own content root are unaffected.
/// </summary>
internal static class TestAppDataIsolation
{
    public static string? ApiProjectFolder { get; private set; }
    public static string? Redirected { get; private set; }

    [ModuleInitializer]
    internal static void RedirectTheApiProjectsAppData()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BirkNext.Api", "BirkNext.Api.csproj"))) dir = dir.Parent;
        if (dir is null) return; // a packaged run without the source tree: nothing of the developer's to protect
        ApiProjectFolder = Path.Combine(dir.FullName, "BirkNext.Api");
        Redirected = Path.Combine(Path.GetTempPath(), "birknext-test-content", $"{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Redirected);
        AppDataLocation.ContentRootRedirect = (ApiProjectFolder, Redirected);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { Directory.Delete(Redirected, recursive: true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } };
    }
}
