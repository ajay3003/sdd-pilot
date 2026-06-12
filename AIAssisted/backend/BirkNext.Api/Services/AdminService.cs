using BirkNext.Api.Data;
using BirkNext.Api.Models.Admin;
using Microsoft.EntityFrameworkCore;
using System.Reflection;

namespace BirkNext.Api.Services;

public class AdminService
{
    private readonly IConfiguration _config;
    private readonly IWebHostEnvironment _env;
    private readonly AppDbContext _db;
    private readonly ILogger<AdminService> _logger;

    public AdminService(IConfiguration config, IWebHostEnvironment env, AppDbContext db, ILogger<AdminService> logger)
    {
        _config = config;
        _env = env;
        _db = db;
        _logger = logger;
    }

    public bool IsEnabled => _config.GetValue<bool>("AdminSettings:Enabled", true);

    public SystemSettingsResponse BuildSettings()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var version = assembly.GetName().Version?.ToString() ?? "1.0.0.0";
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? version;

        var configuredMode = _config["RuntimeSettings:PackageMode"] ?? "Auto";
        var packageMode = ResolvePackageMode(configuredMode);

        var frontendOrigin = _config["FRONTEND_ORIGIN"] ?? "http://localhost:5173";
        var composeProjectName = _config["RuntimeSettings:ComposeProjectName"] ?? "birknext-studio-local";
        var expectedVolume = _config["RuntimeSettings:ExpectedDatabaseVolume"] ?? "birknext-studio-local_postgres_data";
        var dbMode = _config["DatabaseSettings:Mode"] ?? "Local";
        var dbProvider = _config["DatabaseSettings:Provider"] ?? "PostgreSQL";
        var dbHost = _config["DatabaseSettings:Host"] ?? "localhost";
        var dbPort = _config.GetValue<int>("DatabaseSettings:Port", 5432);
        var dbName = _config["DatabaseSettings:DatabaseName"] ?? "birknext";

        var connStr = _config.GetConnectionString("Default") ?? "";
        var dbUsername = ParseConnectionStringParam(connStr, "Username")
            ?? _config["POSTGRES_USER"]
            ?? "birknext";

        var migrationStatus = ResolveMigrationStatus();

        var listeningUrls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? "http://localhost:5000";
        var primaryUrl = listeningUrls.Split(';')[0];
        var aspNetEnv = _env.EnvironmentName;

        var loggingProvider = _config["LoggingSettings:Provider"] ?? "Serilog";
        var loggingMinLevel = _config["LoggingSettings:MinimumLevel"] ?? "Information";
        var logPath = _config["LoggingSettings:LogPath"] ?? "./logs";
        var seqUrl = _config["LoggingSettings:SeqUrl"] ?? "";
        var structuredLogging = _config.GetValue<bool>("LoggingSettings:StructuredLogging", true);
        var sinks = ResolveSinks(logPath, seqUrl);

        var resetAllowed = _config.GetValue<bool>("AdminSettings:AllowLocalDatabaseReset", true);
        var isLocalMode = dbMode.Equals("Local", StringComparison.OrdinalIgnoreCase);
        var resetNotAllowedReason = ResolveResetNotAllowedReason(resetAllowed, isLocalMode);

        return new SystemSettingsResponse
        {
            Application = new ApplicationInfo
            {
                ApplicationName = _config["RuntimeSettings:ApplicationName"] ?? "QA Review Studio",
                Environment = aspNetEnv,
                Version = informationalVersion,
                PackageMode = packageMode
            },
            Frontend = new FrontendInfo
            {
                FrontendBaseUrl = frontendOrigin,
                ApiBaseUrl = primaryUrl,
                GraphQlEndpoint = $"{primaryUrl}/graphql",
                EnvironmentName = aspNetEnv,
                StaticHostingMode = true
            },
            Backend = new BackendInfo
            {
                BackendBaseUrl = primaryUrl,
                AspNetCoreEnvironment = aspNetEnv,
                ListeningUrls = listeningUrls,
                CorsAllowedOrigins = frontendOrigin
            },
            Database = new DatabaseInfo
            {
                Mode = dbMode,
                Host = dbHost,
                Port = dbPort,
                DatabaseName = dbName,
                Username = dbUsername,
                Provider = dbProvider,
                MigrationStatus = migrationStatus,
                ComposeProjectName = composeProjectName,
                ExpectedVolumeName = expectedVolume
            },
            Runtime = new RuntimeInfo
            {
                ComposeProjectName = composeProjectName,
                ExpectedDatabaseVolume = expectedVolume,
                PackageMode = packageMode,
                RunningFromPublishedArtifact = packageMode == "Tester Package"
            },
            Logging = new LoggingInfo
            {
                Provider = loggingProvider,
                MinimumLevel = loggingMinLevel,
                Sinks = sinks,
                LogPath = logPath,
                SeqUrl = seqUrl,
                StructuredLogging = structuredLogging
            },
            Maintenance = new MaintenanceInfo
            {
                ResetAllowed = resetAllowed && isLocalMode,
                DatabaseMode = dbMode,
                ResetNotAllowedReason = resetNotAllowedReason
            }
        };
    }

    public async Task<(bool Success, string Message)> ResetLocalDatabaseAsync()
    {
        var resetAllowed = _config.GetValue<bool>("AdminSettings:AllowLocalDatabaseReset", true);
        var dbMode = _config["DatabaseSettings:Mode"] ?? "Local";

        if (!resetAllowed)
            return (false, "Reset is disabled in AdminSettings.");

        if (!dbMode.Equals("Local", StringComparison.OrdinalIgnoreCase))
            return (false, "Reset is only available in Local database mode. Shared database cannot be reset from the UI.");

        _logger.LogWarning("Local database reset initiated by admin action");

        try
        {
            // Delete in dependency order: CodeLinks depend on CodeFiles and Scenarios
            await _db.CodeLinks.ExecuteDeleteAsync();
            await _db.TraceLinks.ExecuteDeleteAsync();
            await _db.CandidateLinks.ExecuteDeleteAsync();
            await _db.CodeFiles.ExecuteDeleteAsync();
            await _db.QaDeltaReviews.ExecuteDeleteAsync();
            await _db.ReviewedCandidates.ExecuteDeleteAsync();
            await _db.Scenarios.ExecuteDeleteAsync();

            _logger.LogWarning("Local database reset completed — all application data cleared");
            return (true, "Database reset successfully. All application data has been cleared.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Local database reset failed");
            return (false, "Reset failed. See server logs for details.");
        }
    }

    private string ResolvePackageMode(string configured)
    {
        if (!configured.Equals("Auto", StringComparison.OrdinalIgnoreCase))
            return configured;

        try
        {
            var hasCsproj = Directory
                .GetFiles(AppContext.BaseDirectory, "*.csproj", SearchOption.AllDirectories)
                .Length > 0;
            return hasCsproj ? "Source" : "Tester Package";
        }
        catch
        {
            return "Unknown";
        }
    }

    private string ResolveMigrationStatus()
    {
        try
        {
            var pending = _db.Database.GetPendingMigrations().ToList();
            return pending.Count == 0 ? "Up to date" : $"{pending.Count} pending";
        }
        catch
        {
            return "Unknown";
        }
    }

    private static List<string> ResolveSinks(string logPath, string seqUrl)
    {
        var sinks = new List<string> { "Console" };
        if (!string.IsNullOrWhiteSpace(logPath))
            sinks.Add("File");
        if (!string.IsNullOrWhiteSpace(seqUrl))
            sinks.Add("Seq");
        return sinks;
    }

    private static string ResolveResetNotAllowedReason(bool resetAllowed, bool isLocalMode)
    {
        if (!resetAllowed)
            return "Reset is disabled in AdminSettings.";
        if (!isLocalMode)
            return "Reset is only available in Local database mode.";
        return "";
    }

    private static string? ParseConnectionStringParam(string connStr, string paramName)
    {
        if (string.IsNullOrWhiteSpace(connStr)) return null;
        foreach (var part in connStr.Split(';'))
        {
            var idx = part.IndexOf('=');
            if (idx < 1) continue;
            var key = part[..idx].Trim();
            var val = part[(idx + 1)..].Trim();
            if (key.Equals(paramName, StringComparison.OrdinalIgnoreCase))
                return val;
        }
        return null;
    }
}
