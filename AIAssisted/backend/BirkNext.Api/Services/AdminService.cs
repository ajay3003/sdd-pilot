using BirkNext.Api.Data;
using BirkNext.Api.Models.Admin;
using Microsoft.EntityFrameworkCore;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BirkNext.Api.Services;

public class AdminService
{
    private readonly IConfiguration _config;
    private readonly IWebHostEnvironment _env;
    private readonly AppDbContext _db;
    private readonly ILogger<AdminService> _logger;

    private static readonly HashSet<string> PlatformFeatureKeys =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Dashboard", "UserGuide", "RecommendedWorkflow", "AdminSystemSettings"
        };

    private static readonly IReadOnlyList<(string Key, string Label)> CoreFeatures =
    [
        ("SpecificationReview",        "Specification Review"),
        ("ConstitutionExplorer",       "Constitution Explorer"),
        ("PlanExplorer",               "Plan Explorer"),
        ("TaskExplorer",               "Task Explorer"),
        ("QaArtifactLibrary",          "QA Artifact Library"),
        ("TraceabilityCoverage",       "Traceability & Coverage"),
        ("ArtifactTraceability",       "Artifact Traceability"),
        ("ConstitutionCompliance",     "Constitution Compliance"),
        ("BlazorWasmSecurityReview",   "Blazor WASM Security Review"),
        ("BlazorWasmPerformanceReview","WASM Performance Review"),
        ("QaAuditor",                  "QA Auditor"),
        ("DeliveryReadiness",          "Delivery Readiness"),
        ("ImplementationReview",       "Implementation Review"),
        ("ImplementationTraceability", "Implementation Traceability")
    ];

    private static readonly IReadOnlyList<(string Key, string Label)> AdvancedFeatures =
    [
        ("EnableExtractionReview", "Extraction Review"),
        ("EnableArchitectureView", "Architecture View"),
        ("CreateTestScenario",  "Create Test Scenario"),
        ("LegacyTraceabilityNavigationEnabled", "Legacy Traceability Navigation"),
        ("TraceabilitySuggestions", "Traceability Suggestions"),
        ("CodeTraceability",    "Code Traceability"),
        ("ImpactAnalysis",      "Impact Analysis"),
        ("SpecDrift",           "Spec Drift"),
        ("AiChangeReview",      "AI Change Review"),
        ("QaReadiness",         "QA Readiness")
    ];

    private static readonly string[] ValidLogLevels =
        ["Verbose", "Debug", "Information", "Warning", "Error", "Fatal"];

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

        var absoluteLogPath = System.IO.Path.IsPathRooted(logPath)
            ? logPath
            : System.IO.Path.GetFullPath(System.IO.Path.Combine(_env.ContentRootPath, logPath));
        var logFiles = BuildLogFileEntries(absoluteLogPath);

        var resetAllowed = _config.GetValue<bool>("AdminSettings:AllowLocalDatabaseReset", true);
        var isLocalMode = dbMode.Equals("Local", StringComparison.OrdinalIgnoreCase);
        var resetNotAllowedReason = ResolveResetNotAllowedReason(resetAllowed, isLocalMode);

        var featureVisibility = BuildFeatureVisibility();

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
                ResolvedLogsFolder = absoluteLogPath,
                SeqUrl = seqUrl,
                StructuredLogging = structuredLogging,
                LogFiles = logFiles
            },
            Maintenance = new MaintenanceInfo
            {
                ResetAllowed = resetAllowed && isLocalMode,
                DatabaseMode = dbMode,
                ResetNotAllowedReason = resetNotAllowedReason
            },
            FeatureVisibility = featureVisibility,
            AzureDevOps = BuildAzureDevOpsInfo()
        };
    }

    private AzureDevOpsInfo BuildAzureDevOpsInfo()
    {
        var enabled  = _config.GetValue<bool>("AzureDevOps:Enabled");
        var orgUrl   = _config["AzureDevOps:OrganizationUrl"] ?? "";
        var project  = _config["AzureDevOps:Project"] ?? "";
        var repoId   = _config["AzureDevOps:RepositoryId"] ?? "";
        var branch   = _config["AzureDevOps:DefaultBranch"] ?? "main";

        // Check PAT source — never expose the value
        var configPat = _config["AzureDevOps:Pat"] ?? "";
        var envPat    = Environment.GetEnvironmentVariable("ADO_PAT") ?? "";

        var patConfigured = !string.IsNullOrWhiteSpace(configPat) || !string.IsNullOrWhiteSpace(envPat);
        var patSource = !string.IsNullOrWhiteSpace(envPat)    ? "EnvironmentVariable"
                      : !string.IsNullOrWhiteSpace(configPat) ? "Configuration"
                      : "Missing";

        return new AzureDevOpsInfo
        {
            Enabled       = enabled,
            OrganizationUrl = orgUrl,
            Project       = project,
            RepositoryId  = repoId,
            DefaultBranch = branch,
            PatConfigured = patConfigured,
            PatSource     = patSource,
            ActivelyUsed  = enabled && patConfigured,
        };
    }

    public FeatureVisibilityInfo BuildFeatureVisibility()
    {
        var s = _config.GetSection("FeatureVisibility");
        return new FeatureVisibilityInfo
        {
            RecommendedWorkflow  = s.GetValue("RecommendedWorkflow",  true),
            UserGuide            = s.GetValue("UserGuide",            true),
            Dashboard            = s.GetValue("Dashboard",            true),
            SpecificationReview  = s.GetValue("SpecificationReview",  true),
            QaArtifactLibrary    = s.GetValue("QaArtifactLibrary",    true),
            CreateTestScenario   = s.GetValue("CreateTestScenario",   true),
            LegacyTraceabilityNavigationEnabled = s.GetValue("LegacyTraceabilityNavigationEnabled", false),
            TraceabilityCoverage = s.GetValue("TraceabilityCoverage", true),
            TraceabilitySuggestions = s.GetValue("TraceabilitySuggestions", true),
            CodeTraceability     = s.GetValue("CodeTraceability",     true),
            SpecComparison       = s.GetValue("SpecComparison",       true),
            SpecificationDeltas  = s.GetValue("SpecificationDeltas",  true),
            TaskDeltas           = s.GetValue("TaskDeltas",           true),
            ImpactAnalysis       = s.GetValue("ImpactAnalysis",       true),
            SpecDrift            = s.GetValue("SpecDrift",            true),
            ImplementationReview        = s.GetValue("ImplementationReview",        true),
            ImplementationTraceability  = s.GetValue("ImplementationTraceability",  true),
            ConstitutionExplorer        = s.GetValue("ConstitutionExplorer",        true),
            PlanExplorer                = s.GetValue("PlanExplorer",                true),
            ArtifactTraceability        = s.GetValue("ArtifactTraceability",        true),
            ConstitutionCompliance      = s.GetValue("ConstitutionCompliance",      true),
            BlazorWasmSecurityReview    = s.GetValue("BlazorWasmSecurityReview",    true),
            BlazorWasmPerformanceReview = s.GetValue("BlazorWasmPerformanceReview", true),
            TaskExplorer                = s.GetValue("TaskExplorer",                true),
            QaAuditor                   = s.GetValue("QaAuditor",                   true),
            DeliveryReadiness           = s.GetValue("DeliveryReadiness",           true),
            AiChangeReview       = s.GetValue("AiChangeReview",       true),
            QaReadiness          = s.GetValue("QaReadiness",          true),
            EnableExtractionReview = s.GetValue("EnableExtractionReview", false),
            EnableArchitectureView = s.GetValue("EnableArchitectureView", false),
            AdminSystemSettings  = s.GetValue("AdminSystemSettings",  true)
        };
    }

    public EditableSettingsResponse BuildEditableSettings()
    {
        var fv = _config.GetSection("FeatureVisibility");

        var platform = new List<FeatureVisibilityEntry>
        {
            new() { Key = "Dashboard",             Label = "Dashboard",             Value = true, Locked = true },
            new() { Key = "UserGuide",             Label = "User Guide",            Value = true, Locked = true },
            new() { Key = "RecommendedWorkflow",   Label = "Recommended Workflow",  Value = true, Locked = true },
            new() { Key = "AdminSystemSettings",   Label = "System Settings",       Value = true, Locked = true }
        };

        var core = CoreFeatures.Select(f => new FeatureVisibilityEntry
        {
            Key = f.Key, Label = f.Label,
            Value = fv.GetValue(f.Key, true), Locked = false
        }).ToList();

        var advanced = AdvancedFeatures.Select(f => new FeatureVisibilityEntry
        {
            Key = f.Key, Label = f.Label,
            Value = fv.GetValue(f.Key, false), Locked = false
        }).ToList();

        return new EditableSettingsResponse
        {
            FeatureVisibility = new EditableFeatureVisibilitySection
            {
                Platform = platform,
                Core = core,
                Advanced = advanced
            },
            Logging = new EditableLoggingSection
            {
                MinimumLevel = _config["LoggingSettings:MinimumLevel"] ?? "Information",
                SeqUrl = _config["LoggingSettings:SeqUrl"] ?? ""
            },
            Admin = new EditableAdminSection
            {
                ShowDiagnostics = _config.GetValue("AdminSettings:ShowDiagnostics", true)
            }
        };
    }

    public (bool Valid, string Error) ValidateSettingsUpdate(SaveSettingsRequest request)
    {
        if (request.FeatureVisibility != null)
        {
            foreach (var (key, value) in request.FeatureVisibility)
            {
                if (PlatformFeatureKeys.Contains(key) && !value)
                    return (false, $"Platform features cannot be disabled. '{key}' is a platform feature and must always remain enabled.");
            }
        }

        if (request.Logging?.MinimumLevel is not null &&
            !ValidLogLevels.Contains(request.Logging.MinimumLevel, StringComparer.OrdinalIgnoreCase))
        {
            return (false, $"Invalid log level '{request.Logging.MinimumLevel}'. Valid levels: {string.Join(", ", ValidLogLevels)}.");
        }

        return (true, "");
    }

    public async Task<(bool Success, string Message)> SaveSettingsAsync(SaveSettingsRequest request)
    {
        var (valid, error) = ValidateSettingsUpdate(request);
        if (!valid) return (false, error);

        var path = System.IO.Path.Combine(_env.ContentRootPath, "appsettings.Local.json");

        JsonObject root;
        try
        {
            root = File.Exists(path)
                ? JsonNode.Parse(await File.ReadAllTextAsync(path))?.AsObject() ?? new JsonObject()
                : new JsonObject();
        }
        catch
        {
            root = new JsonObject();
        }

        if (request.FeatureVisibility is { Count: > 0 })
        {
            var fvNode = root.TryGetPropertyValue("FeatureVisibility", out var existing)
                && existing is JsonObject obj ? obj : new JsonObject();
            foreach (var (key, value) in request.FeatureVisibility)
            {
                if (!PlatformFeatureKeys.Contains(key))
                    fvNode[key] = JsonValue.Create(value);
            }
            root["FeatureVisibility"] = fvNode;
        }

        if (request.Logging != null)
        {
            var lsNode = root.TryGetPropertyValue("LoggingSettings", out var lsExisting)
                && lsExisting is JsonObject lsObj ? lsObj : new JsonObject();
            if (request.Logging.MinimumLevel is not null)
                lsNode["MinimumLevel"] = JsonValue.Create(request.Logging.MinimumLevel);
            if (request.Logging.SeqUrl is not null)
                lsNode["SeqUrl"] = JsonValue.Create(request.Logging.SeqUrl);
            root["LoggingSettings"] = lsNode;
        }

        if (request.Admin?.ShowDiagnostics.HasValue == true)
        {
            var adminNode = root.TryGetPropertyValue("AdminSettings", out var adminExisting)
                && adminExisting is JsonObject adminObj ? adminObj : new JsonObject();
            adminNode["ShowDiagnostics"] = JsonValue.Create(request.Admin.ShowDiagnostics!.Value);
            root["AdminSettings"] = adminNode;
        }

        var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(path, json);

        try { ((IConfigurationRoot)_config).Reload(); }
        catch (Exception ex) { _logger.LogWarning(ex, "IConfiguration reload after settings save encountered an issue"); }

        _logger.LogInformation("Local settings saved to {Path}", path);
        return (true, "Settings saved. Feature visibility changes apply immediately. Refresh the page to update the navigation sidebar. Logging changes require a backend restart.");
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
            await using var transaction = await _db.Database.BeginTransactionAsync();

            // Delete in dependency order: child tables first, then parent tables
            await _db.CodeLinks.ExecuteDeleteAsync();
            await _db.TraceLinks.ExecuteDeleteAsync();
            await _db.CandidateLinks.ExecuteDeleteAsync();
            await _db.TraceabilitySuggestions.ExecuteDeleteAsync();
            await _db.CodeFiles.ExecuteDeleteAsync();
            await _db.QaDeltaReviews.ExecuteDeleteAsync();
            await _db.ReviewedCandidates.ExecuteDeleteAsync();
            await _db.Scenarios.ExecuteDeleteAsync();

            await transaction.CommitAsync();

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

    private static List<LogFileEntry> BuildLogFileEntries(string absoluteLogPath)
    {
        LogFileEntry Entry(string label, string fileName)
        {
            var fullPath = System.IO.Path.Combine(absoluteLogPath, fileName);
            return new LogFileEntry { Label = label, Path = fullPath, Exists = File.Exists(fullPath) };
        }

        var files = new List<LogFileEntry>
        {
            Entry("Launcher Log",    "launcher.log"),
            Entry("Backend Stdout",  "backend.out.log"),
            Entry("Backend Stderr",  "backend.err.log"),
            Entry("Frontend Stdout", "frontend.out.log"),
            Entry("Frontend Stderr", "frontend.err.log"),
        };

        var latestSerilog = Directory.Exists(absoluteLogPath)
            ? Directory.GetFiles(absoluteLogPath, "backend-serilog-*.log")
                .OrderByDescending(f => f)
                .FirstOrDefault()
            : null;

        files.Add(new LogFileEntry
        {
            Label = "Backend Serilog",
            Path = latestSerilog ?? System.IO.Path.Combine(absoluteLogPath, "backend-serilog-<date>.log"),
            Exists = latestSerilog is not null
        });

        return files;
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
