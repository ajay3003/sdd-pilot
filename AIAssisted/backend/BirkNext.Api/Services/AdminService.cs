using BirkNext.Api.Data;
using BirkNext.Api.Models.Admin;
using BirkNext.Api.Services.FrontendQualityEngines;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BirkNext.Api.Services;

public class AdminService : BirkNext.Api.Services.LocalDataReset.ILocalDatabaseReset
{
    private static readonly SemaphoreSlim ResetGate = new(1, 1);
    // Every table in the EF model is application data and is reset. Target-environment integration catalogs, applied templates (the M2LB
    // domain extension is derived from integration_environment_states) and performance test definitions are project state, not installation
    // configuration. Installation settings live in appsettings files, never in these tables. See docs/local-data-reset.md.
    private static readonly HashSet<string> PreservedConfigurationTables = new(StringComparer.OrdinalIgnoreCase);
    public static IReadOnlyList<string> ResetPreservedDomains => BirkNext.Api.Services.LocalDataReset.LocalDataResetCoordinator.Preserved;
    private readonly IConfiguration _config;
    private readonly IWebHostEnvironment _env;
    private readonly AppDbContext _db;
    private readonly ILogger<AdminService> _logger;
    private readonly FrontendQualityEngineLegacyConfigInterpreter? _legacyInterpreter;

    private static readonly HashSet<string> PlatformFeatureKeys =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Dashboard", "UserGuide", "RecommendedWorkflow", "AdminSystemSettings"
        };

    private static readonly IReadOnlyList<(string Key, string Label)> CoreFeatures =
    [
        ("SpecificationExplorer",      "Specification Explorer"),
        ("ConstitutionExplorer",       "Constitution Explorer"),
        ("DataModelExplorer",          "Data Model Explorer"),
        ("PlanExplorer",               "Plan Explorer"),
        ("TaskExplorer",               "Task Explorer"),
        ("QaArtifactLibrary",          "QA Artifact Library"),
        ("SampleProjects",             "Sample Projects"),
        ("TraceabilityCoverage",       "Traceability & Coverage"),
        ("ArtifactTraceability",       "Artifact Traceability"),
        ("QualityReview",              "Document Quality Review"),
        ("FrontendQualityReview",      "Frontend Quality Review"),
        ("ApiQualityReview",           "API Quality Review"),
        ("IntegrationQualityReview",   "Integration Quality Review"),
        ("CriticalE2ERegression",      "Critical E2E Regression"),
        ("BlazorWasmSecurityReview",   "WASM Security Review"),
        ("BlazorWasmPerformanceReview","WASM Performance Review"),
        ("ImplementationReview",       "Implementation Review"),
        ("ImplementationTraceability", "Implementation Traceability"),
        ("SourceAnalysis",             "Source Analysis"),
        ("AzureEnvironmentAnalysis",   "Azure Environment Analysis")
    ];

    private static readonly IReadOnlyList<(string Key, string Label)> AdvancedFeatures =
    [
        ("EnableExtractionReview", "Extraction Review"),
        ("EnableArchitectureView", "Architecture View"),
        ("LegacyTraceabilityNavigationEnabled", "Legacy Traceability Navigation"),
        ("TraceabilitySuggestions", "Traceability Suggestions"),
        ("CodeTraceability",    "Code Traceability"),
        ("ImpactAnalysis",      "Impact Analysis"),
        ("SpecDrift",           "Spec Drift"),
        ("AiChangeReview",      "AI Change Review")
    ];

    private static readonly string[] ValidLogLevels =
        ["Verbose", "Debug", "Information", "Warning", "Error", "Fatal"];

    public AdminService(
        IConfiguration config,
        IWebHostEnvironment env,
        AppDbContext db,
        ILogger<AdminService> logger,
        FrontendQualityEngineLegacyConfigInterpreter? legacyInterpreter = null)
    {
        _config = config;
        _env = env;
        _db = db;
        _logger = logger;
        _legacyInterpreter = legacyInterpreter;
    }

    /// <summary>Where the backend is running from; the process base directory unless a test points it elsewhere.</summary>
    internal string RuntimeBaseDirectory { get; init; } = AppContext.BaseDirectory;

    public bool IsEnabled => _config.GetValue<bool>("AdminSettings:Enabled", true);

    public SystemSettingsResponse BuildSettings()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var version = assembly.GetName().Version?.ToString() ?? "1.0.0.0";
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? version;

        var configuredMode = _config["RuntimeSettings:PackageMode"] ?? "Auto";
        var isSourceBuild = RuntimeSourceDetector.IsSourceBuild(RuntimeBaseDirectory, $"{assembly.GetName().Name}.csproj");
        var packageMode = ResolvePackageMode(configuredMode, isSourceBuild);

        var frontendOrigin = _config["FRONTEND_ORIGIN"] ?? "http://localhost:5173";
        var composeProjectName = _config["RuntimeSettings:ComposeProjectName"] ?? "birknext-studio-local";
        var expectedVolume = _config["RuntimeSettings:ExpectedDatabaseVolume"] ?? "birknext-studio-local_postgres_data";
        var dbMode = _config["DatabaseSettings:Mode"] ?? "Unknown";
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

        var resetPolicyEnabled = _config.GetValue<bool>("AdminSettings:AllowLocalDatabaseReset", true);
        var isSafeLocalMode = IsSafeLocalResetEnvironment(dbMode);
        var resetAllowed = resetPolicyEnabled && isSafeLocalMode;
        var resetNotAllowedReason = ResolveResetNotAllowedReason(resetPolicyEnabled, isSafeLocalMode);
        var maintenanceDbMode = dbMode.Equals("Local", StringComparison.OrdinalIgnoreCase) && !IsLoopbackDatabaseHost()
            ? "Unverified"
            : dbMode;

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
                RunningFromPublishedArtifact = !isSourceBuild
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
                ResetAllowed = resetAllowed,
                DatabaseMode = maintenanceDbMode,
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
            SpecificationExplorer = s.GetValue("SpecificationExplorer", true),
            QaArtifactLibrary    = s.GetValue("QaArtifactLibrary",    true),
            SampleProjects       = s.GetValue("SampleProjects",       true),
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
            // Missing key (older configuration) resolves to enabled, like every Core feature.
            SourceAnalysis              = s.GetValue("SourceAnalysis",              true),
            AzureEnvironmentAnalysis    = s.GetValue("AzureEnvironmentAnalysis",    true),
            ConstitutionExplorer        = s.GetValue("ConstitutionExplorer",        true),
            DataModelExplorer           = s.GetValue("DataModelExplorer",           true),
            PlanExplorer                = s.GetValue("PlanExplorer",                true),
            ArtifactTraceability        = s.GetValue("ArtifactTraceability",        true),
            FrontendQualityReview       = s.GetValue("FrontendQualityReview",       true),
            ApiQualityReview            = s.GetValue("ApiQualityReview",            true),
            IntegrationQualityReview    = s.GetValue("IntegrationQualityReview",    true),
            BlazorWasmSecurityReview    = s.GetValue("BlazorWasmSecurityReview",    true),
            BlazorWasmPerformanceReview = s.GetValue("BlazorWasmPerformanceReview", true),
            TaskExplorer                = s.GetValue("TaskExplorer",                true),
            QualityReview               = s.GetValue("QualityReview",               true),
            AiChangeReview       = s.GetValue("AiChangeReview",       false),
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
            },
            FrontendQualityEngines = BuildEditableFrontendQualityEngines()
        };
    }

    private EditableFrontendQualityEnginesSection BuildEditableFrontendQualityEngines()
    {
        if (_legacyInterpreter == null)
        {
            return new EditableFrontendQualityEnginesSection();
        }

        return new EditableFrontendQualityEnginesSection
        {
            BrowserRuntimeEnabled = _legacyInterpreter.ResolveLayer1And2(FrontendQualityEngineId.BrowserRuntime).Enabled,
            AccessibilityEnabled = _legacyInterpreter.ResolveLayer1And2(FrontendQualityEngineId.Accessibility).Enabled,
            LighthouseEnabled = _legacyInterpreter.ResolveLayer1And2(FrontendQualityEngineId.Lighthouse).Enabled,
            PassiveSecurityEnabled = _legacyInterpreter.ResolveLayer1And2(FrontendQualityEngineId.PassiveSecurity).Enabled,
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

        if (request.FrontendQualityEngines != null)
        {
            var fqeNode = root.TryGetPropertyValue("FrontendQualityEnginePreferences", out var fqeExisting)
                && fqeExisting is JsonObject fqeObj ? fqeObj : new JsonObject();
            if (request.FrontendQualityEngines.BrowserRuntimeEnabled.HasValue)
                fqeNode["BrowserRuntimeEnabled"] = JsonValue.Create(request.FrontendQualityEngines.BrowserRuntimeEnabled!.Value);
            if (request.FrontendQualityEngines.AccessibilityEnabled.HasValue)
                fqeNode["AccessibilityEnabled"] = JsonValue.Create(request.FrontendQualityEngines.AccessibilityEnabled!.Value);
            if (request.FrontendQualityEngines.LighthouseEnabled.HasValue)
                fqeNode["LighthouseEnabled"] = JsonValue.Create(request.FrontendQualityEngines.LighthouseEnabled!.Value);
            if (request.FrontendQualityEngines.PassiveSecurityEnabled.HasValue)
                fqeNode["PassiveSecurityEnabled"] = JsonValue.Create(request.FrontendQualityEngines.PassiveSecurityEnabled!.Value);
            root["FrontendQualityEnginePreferences"] = fqeNode;
        }

        var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(path, json);

        try { ((IConfigurationRoot)_config).Reload(); }
        catch (Exception ex) { _logger.LogWarning(ex, "IConfiguration reload after settings save encountered an issue"); }

        _logger.LogInformation("Local settings saved to {Path}", path);
        return (true, "Settings saved. Feature visibility changes apply immediately. Logging changes require a backend restart.");
    }

    public async Task<(bool Success, string Message, int DeletedRows, DateTimeOffset? ResetAtUtc)> ResetLocalDatabaseAsync()
    {
        var dbMode = _config["DatabaseSettings:Mode"] ?? "Unknown";
        if (!_config.GetValue<bool>("AdminSettings:AllowLocalDatabaseReset", true))
            return (false, "Reset is disabled by local administrator policy.", 0, null);
        if (!IsSafeLocalResetEnvironment(dbMode))
            return (false, "Reset is available only for a Local database on a non-production backend.", 0, null);

        if (!await ResetGate.WaitAsync(0))
            return (false, "A local database reset is already in progress.", 0, null);

        _logger.LogWarning("Local database reset initiated by admin action");

        try
        {
            await using var transaction = await _db.Database.BeginTransactionAsync();
            var tables = _db.Model.GetRelationalModel().Tables
                .Where(t => !PreservedConfigurationTables.Contains(t.Name))
                .ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);

            // Refuse to remove records while a tracked CDC or performance operation is active.
            var activeCdc = await _db.ActiveCdcRuns.AnyAsync(r => r.Status.ToLower() == "running");
            var activePerformance = await _db.PerformanceTestRuns.AnyAsync(r =>
                r.State.ToLower() == "queued" || r.State.ToLower() == "preparing"
                || r.State.ToLower() == "running" || r.State.ToLower() == "cancelling");
            if (activeCdc || activePerformance)
                return (false, "Reset is blocked while a CDC or performance test is running.", 0, null);

            var orderedTables = OrderTablesForDelete(tables);
            var sql = _db.GetService<ISqlGenerationHelper>();
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var connection = _db.Database.GetDbConnection();
            foreach (var table in orderedTables)
            {
                var tableName = sql.DelimitIdentifier(table.Name, table.Schema);
                await using var command = connection.CreateCommand();
                command.Transaction = transaction.GetDbTransaction();
                command.CommandText = $"DELETE FROM {tableName}";
                counts[table.Name] = await command.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
            _logger.LogWarning("Local database reset completed. Cleared {TableCount} data tables; preserved installation configuration tables.", counts.Count);
            var deleted = counts.Values.Sum();
            return (true, $"Local database reset completed. {deleted} data records were removed. Installation and provider configuration, database schema, and migration history were preserved.", deleted, DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Local database reset failed");
            return (false, "Reset failed. See server logs for details.", 0, null);
        }
        finally
        {
            ResetGate.Release();
        }
    }

    private bool IsSafeLocalResetEnvironment(string databaseMode)
    {
        if (!(_env.IsDevelopment() || _env.IsEnvironment("Local") || _env.IsEnvironment("Test"))
            || !databaseMode.Equals("Local", StringComparison.OrdinalIgnoreCase)) return false;
        return IsLoopbackDatabaseHost();
    }

    private bool IsLoopbackDatabaseHost()
    {
        var connection = _config.GetConnectionString("Default") ?? "";
        var host = ParseConnectionStringParam(connection, "Host") ?? _config["DatabaseSettings:Host"];
        return host is not null && (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || host.Equals("::1", StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<ITable> OrderTablesForDelete(IReadOnlyDictionary<string, ITable> tables)
    {
        var result = new List<ITable>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Visit(ITable table)
        {
            if (!visited.Add(table.Name)) return;
            foreach (var fk in table.ForeignKeyConstraints)
                if (tables.TryGetValue(fk.PrincipalTable.Name, out var parent)) Visit(parent);
            result.Add(table);
        }
        foreach (var table in tables.Values) Visit(table);
        result.Reverse();
        return result;
    }

    /// <summary>
    /// The configured package mode names the package; Auto derives it from how the backend is running. Whether it is
    /// running from a published artifact is always read from the runtime layout, never from the configured name.
    /// </summary>
    private static string ResolvePackageMode(string configured, bool isSourceBuild) =>
        configured.Equals("Auto", StringComparison.OrdinalIgnoreCase)
            ? isSourceBuild ? "Source" : "Tester Package"
            : configured;

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

    private string ResolveResetNotAllowedReason(bool resetPolicyEnabled, bool isSafeLocalMode)
    {
        if (!resetPolicyEnabled)
            return "Reset is disabled in AdminSettings.";
        if (!_env.IsDevelopment() && !_env.IsEnvironment("Local") && !_env.IsEnvironment("Test"))
            return "Reset is disabled outside Development, Local, or Test backend environments.";
        if (!isSafeLocalMode)
            return "Reset requires Local database mode and a loopback database host.";
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
