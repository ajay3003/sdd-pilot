using BirkNext.Api.Models.Admin;

namespace BirkNext.Api.Services;

/// <summary>
/// Provides structured, validated information for System Settings → General page.
/// Shows application, runtime, configuration, and endpoint information.
/// Uses shared status calculation to ensure consistency across all pages.
/// </summary>
public interface IGeneralPageService
{
    Task<List<SettingsSection>> GetGeneralPageSectionsAsync();
    Task<StatusSummary> GetStatusSummaryAsync();
    Task<SystemSettingsStatus> GetOverallStatusAsync();
}

public class GeneralPageService : IGeneralPageService
{
    private readonly IConfiguration _config;
    private readonly IWebHostEnvironment _env;
    private readonly ISystemSettingsStatusEngine _statusEngine;

    public GeneralPageService(
        IConfiguration config,
        IWebHostEnvironment env,
        ISystemSettingsStatusEngine statusEngine)
    {
        _config = config;
        _env = env;
        _statusEngine = statusEngine;
    }

    public async Task<List<SettingsSection>> GetGeneralPageSectionsAsync()
    {
        var sections = new List<SettingsSection>();

        sections.Add(BuildApplicationSection());
        sections.Add(BuildRuntimeSection());
        sections.Add(BuildConfigurationSection());
        sections.Add(BuildEndpointsSection());

        return await Task.FromResult(sections);
    }

    public async Task<StatusSummary> GetStatusSummaryAsync()
    {
        var sections = await GetGeneralPageSectionsAsync();
        var allItems = sections.SelectMany(s => s.Items).ToList();
        return _statusEngine.SummarizeStatuses(allItems.Select(i => i.Status));
    }

    public async Task<SystemSettingsStatus> GetOverallStatusAsync()
    {
        var summary = await GetStatusSummaryAsync();
        return summary.OverallStatus;
    }

    private SettingsSection BuildApplicationSection()
    {
        var appName = _config["ApplicationName"] ?? "QA Review Studio";
        var version = _config["ApplicationVersion"] ?? "Unknown";
        var buildNumber = _config["BuildNumber"];
        var packageMode = _config["PackageMode"] ?? "Unknown";
        var environment = _env.EnvironmentName;

        var items = new List<SettingsItem>
        {
            _statusEngine.CreatePassItem(
                "Application Name",
                appName,
                "Official name of the application"),

            _statusEngine.CreatePassItem(
                "Version",
                SemVerCore(version),
                "Semantic version of the application"),

            _statusEngine.CreatePassItem(
                "Environment",
                environment,
                $"ASP.NET Core environment (set via ASPNETCORE_ENVIRONMENT)"),

            _statusEngine.CreatePassItem(
                "Package Mode",
                packageMode,
                "Deployment package model (development, staging, release)")
        };

        if (!string.IsNullOrWhiteSpace(buildNumber))
        {
            items.Add(_statusEngine.CreatePassItem(
                "Build Number",
                buildNumber,
                "Build number from CI/CD pipeline"));
        }

        var section = new SettingsSection
        {
            Title = "Application",
            Description = "Application name, version, and deployment information",
            Items = items,
            IsRequired = true
        };

        section.Status = _statusEngine.CalculateOverallStatus(items.Select(i => i.Status).ToArray());
        return section;
    }

    private SettingsSection BuildRuntimeSection()
    {
        var runtimeVersion = Environment.Version.ToString();
        var osDescription = System.Runtime.InteropServices.RuntimeInformation.OSDescription;
        var processorCount = Environment.ProcessorCount;
        var arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture;

        var items = new List<SettingsItem>
        {
            _statusEngine.CreatePassItem(
                ".NET Runtime",
                runtimeVersion,
                $".NET version running the application"),

            _statusEngine.CreatePassItem(
                "Operating System",
                osDescription,
                "Server operating system"),

            _statusEngine.CreatePassItem(
                "Processor Count",
                processorCount.ToString(),
                "Number of logical processors available"),

            _statusEngine.CreatePassItem(
                "Architecture",
                arch.ToString(),
                "CPU architecture (x64, arm64, etc.)")
        };

        var section = new SettingsSection
        {
            Title = "Runtime",
            Description = "Runtime environment and server hardware information",
            Items = items,
            IsRequired = true
        };

        section.Status = _statusEngine.CalculateOverallStatus(items.Select(i => i.Status).ToArray());
        return section;
    }

    private SettingsSection BuildConfigurationSection()
    {
        var dbProvider = _config["DatabaseSettings:Provider"] ?? "Unknown";
        var dbMode = _config["DatabaseSettings:Mode"] ?? "Unknown";
        var logLevel = _config["Logging:LogLevel:Default"] ?? "Information";
        var migrationStatus = _config["DatabaseSettings:MigrationStatus"] ?? "Unknown";

        var items = new List<SettingsItem>
        {
            _statusEngine.CreatePassItem(
                "Database Provider",
                dbProvider,
                "Database system being used"),

            _statusEngine.CreatePassItem(
                "Database Mode",
                dbMode,
                "Local or shared database deployment"),

            _statusEngine.CreatePassItem(
                "Logging Level",
                logLevel,
                "Application logging verbosity"),

            string.IsNullOrWhiteSpace(migrationStatus) || migrationStatus == "Up to date"
                ? _statusEngine.CreatePassItem(
                    "Database Migrations",
                    "Current",
                    "All pending migrations have been applied")
                : _statusEngine.CreateWarningItem(
                    "Database Migrations",
                    migrationStatus,
                    "Database schema is not up to date",
                    "Run pending migrations to ensure compatibility",
                    isRequired: true)
        };

        var section = new SettingsSection
        {
            Title = "Configuration",
            Description = "Database, logging, and application configuration status",
            Items = items,
            IsRequired = true
        };

        section.Status = _statusEngine.CalculateOverallStatus(items.Select(i => i.Status).ToArray());
        return section;
    }

    private SettingsSection BuildEndpointsSection()
    {
        var backendUrl = _config["BACKEND_URL"] ?? _config["Backend:BackendBaseUrl"] ?? "Not configured";
        var frontendUrl = _config["Frontend:FrontendBaseUrl"] ?? "Not configured";
        var graphqlEndpoint = _config["Frontend:GraphQlEndpoint"] ?? "Not configured";

        var items = new List<SettingsItem>
        {
            _statusEngine.CreatePassItem(
                "Backend URL",
                backendUrl,
                "Backend API base URL"),

            _statusEngine.CreatePassItem(
                "Frontend URL",
                frontendUrl,
                "Frontend application URL"),

            _statusEngine.CreatePassItem(
                "GraphQL Endpoint",
                graphqlEndpoint,
                "GraphQL API endpoint for data queries")
        };

        var section = new SettingsSection
        {
            Title = "Endpoints",
            Description = "Service endpoints and URLs",
            Items = items,
            IsRequired = true
        };

        section.Status = _statusEngine.CalculateOverallStatus(items.Select(i => i.Status).ToArray());
        return section;
    }

    private static string SemVerCore(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return "Unknown";
        var idx = version.IndexOfAny(new[] { '+', '-' });
        return idx > 0 ? version[..idx] : version;
    }
}
