using BirkNext.Api.Data;
using BirkNext.Api.Services.PerformanceTests;
using BirkNext.BuildInfo;
using BirkNext.PerformanceTests;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services.SystemInfo;

/// <summary>
/// The installation's runtime profile and what it actually runs on: the profile from configuration (set by the launcher; a source checkout
/// defaults to DeveloperContainer), the PostgreSQL server it is connected to (version read from the server itself) and the k6 runner the
/// profile selected with its real availability. Read-only: nothing is started, switched or migrated, and no secret or user path is returned.
/// </summary>
public sealed class RuntimeEnvironmentService(IConfiguration configuration, IServiceScopeFactory scopes, PerformanceTestProviderRegistry? providers = null, TimeProvider? clock = null)
{
    /// <summary>The configured profile; DeveloperContainer for an unconfigured source checkout. An invalid value throws (checked at startup).</summary>
    public static (RuntimeProfile Profile, bool Configured) Resolve(IConfiguration configuration)
    {
        var (profile, error) = RuntimeProfiles.Parse(configuration[RuntimeProfiles.ConfigurationKey]);
        if (error is not null) throw new InvalidOperationException(error);
        return (profile ?? RuntimeProfile.DeveloperContainer, profile is not null);
    }

    public async Task<RuntimeEnvironmentInfo> GetAsync(CancellationToken ct)
    {
        var (profile, configured) = Resolve(configuration);
        var native = profile == RuntimeProfile.TesterNative;
        var packaged = string.Equals(configuration[RuntimeProfiles.DistributionKey], RuntimeProfiles.WindowsTesterPackage, StringComparison.Ordinal);
        var notes = new List<string>();
        var (status, version) = await DatabaseAsync(ct);
        var info = new RuntimeEnvironmentInfo
        {
            Profile = profile, ProfileName = RuntimeProfiles.DisplayName(profile),
            ProfileSource = configured ? "Set by the launcher (fixed until BirkNext is restarted with another launcher)" : "Default for a source checkout (no profile configured)",
            Distribution = packaged ? "Windows Tester Package" : "Source checkout / developer build",
            DatabaseMode = native ? "Bundled PostgreSQL, managed by the BirkNext launcher (this user only, local connections only)" : "PostgreSQL configured for development (normally the Podman container)",
            DatabaseStatus = status, DatabaseServerVersion = version, EvaluatedAt = (clock ?? TimeProvider.System).GetUtcNow(),
        };
        if (native && !packaged) notes.Add("TesterNative is configured outside a Windows Tester Package.");
        var k6 = providers?.Find(PerformanceProviderIds.K6);
        if (k6 is null) return info with { PerformanceRunner = "Not registered", PerformanceRunnerAvailability = "Unavailable", PerformanceRunnerDetail = "No k6 provider in this host.", Notes = notes };
        PerformanceProviderStatus provider;
        try { provider = await k6.StatusAsync(ct); }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return info with { PerformanceRunner = Runner(null), PerformanceRunnerAvailability = "Unknown", PerformanceRunnerDetail = "The k6 runner status could not be read.", Notes = notes };
        }
        return info with
        {
            PerformanceRunner = Runner(provider.Runtime?.RuntimeId), PerformanceRunnerAvailability = provider.Availability.ToString(), K6Version = provider.Version,
            PerformanceRunnerDetail = provider.Detail, Notes = notes,
        };
    }

    private static string Runner(string? runtimeId) => runtimeId switch
    {
        PerformanceProviderIds.NativeRuntime => "Bundled k6.exe (native Windows process)",
        PerformanceProviderIds.PodmanRuntime => "k6 container (Podman)",
        _ => "k6",
    };

    /// <summary>The connected server's own version (SHOW server_version), never a configured value. Unreachable = Unavailable.</summary>
    private async Task<(string Status, string? Version)> DatabaseAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetService<AppDbContext>();
        if (db is null || !db.Database.IsRelational()) return ("Not a relational database in this host", null);
        try
        {
            var version = await db.Database.SqlQueryRaw<string>("SELECT current_setting('server_version') AS \"Value\"").SingleAsync(ct);
            return ("Connected", version);
        }
        catch (Exception e) when (e is not OperationCanceledException) { return ("Unavailable", null); }
    }
}
