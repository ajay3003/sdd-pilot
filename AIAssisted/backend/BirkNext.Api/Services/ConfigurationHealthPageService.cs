using BirkNext.Api.Models.Admin;

namespace BirkNext.Api.Services;

public interface IConfigurationHealthPageService
{
    Task<List<SettingsSection>> GetSectionsAsync();
    Task<StatusSummary> GetStatusSummaryAsync();
}

public sealed class ConfigurationHealthPageService : IConfigurationHealthPageService
{
    private readonly IConfiguration _config;
    private readonly IWebHostEnvironment _env;
    private readonly ISystemSettingsStatusEngine _statusEngine;
    private readonly ILogger<ConfigurationHealthPageService> _logger;

    public ConfigurationHealthPageService(
        IConfiguration config,
        IWebHostEnvironment env,
        ISystemSettingsStatusEngine statusEngine,
        ILogger<ConfigurationHealthPageService> logger)
    {
        _config = config;
        _env = env;
        _statusEngine = statusEngine;
        _logger = logger;
    }

    public async Task<List<SettingsSection>> GetSectionsAsync()
    {
        var sections = new List<SettingsSection>();

        // Environment section
        sections.Add(CreateEnvironmentSection());

        // Database section
        sections.Add(CreateDatabaseSection());

        // Logging section
        sections.Add(CreateLoggingSection());

        // API section
        sections.Add(CreateAPISection());

        // Optional features section
        sections.Add(CreateOptionalFeaturesSection());

        return await Task.FromResult(sections);
    }

    public async Task<StatusSummary> GetStatusSummaryAsync()
    {
        var sections = await GetSectionsAsync();
        var summary = new StatusSummary();

        foreach (var item in sections.SelectMany(s => s.Items))
        {
            summary.AddStatus(item.Status);
        }

        return summary;
    }

    private SettingsSection CreateEnvironmentSection()
    {
        var items = new List<SettingsItem>();
        var envName = _env.EnvironmentName;

        items.Add(new SettingsItem
        {
            Name = "Environment",
            Value = envName,
            Status = SystemSettingsStatus.Pass,
            Description = $"Current environment: {envName}",
            Recommendation = "",
            IsRequired = true
        });

        return new SettingsSection
        {
            Title = "Environment",
            Description = "Application environment configuration",
            Status = SystemSettingsStatus.Pass,
            Items = items,
            IsRequired = true
        };
    }

    private SettingsSection CreateDatabaseSection()
    {
        var items = new List<SettingsItem>();

        var provider = _config["DatabaseSettings:Provider"] ?? "Unknown";
        var host = _config["DatabaseSettings:Host"];
        var status = string.IsNullOrWhiteSpace(host)
            ? SystemSettingsStatus.Fail
            : SystemSettingsStatus.Pass;

        items.Add(new SettingsItem
        {
            Name = "Database Configuration",
            Value = provider,
            Status = status,
            Description = status == SystemSettingsStatus.Pass
                ? $"Connected to {provider} at {host}"
                : "Database connection not properly configured",
            Recommendation = status == SystemSettingsStatus.Fail
                ? "Configure DatabaseSettings:Host in settings"
                : "",
            IsRequired = true
        });

        var dbStatus = _statusEngine.CalculateOverallStatus(items.Select(i => i.Status).ToArray());

        return new SettingsSection
        {
            Title = "Database",
            Description = "Database connection and configuration",
            Status = dbStatus,
            Items = items,
            IsRequired = true
        };
    }

    private SettingsSection CreateLoggingSection()
    {
        var items = new List<SettingsItem>();

        var logLevel = _config["Logging:LogLevel:Default"] ?? "Information";
        var provider = _config["Logging:Provider"] ?? "Console";

        items.Add(new SettingsItem
        {
            Name = "Logging Level",
            Value = logLevel,
            Status = SystemSettingsStatus.Pass,
            Description = $"Logging configured at {logLevel} level using {provider}",
            Recommendation = "",
            IsRequired = true
        });

        var loggingStatus = _statusEngine.CalculateOverallStatus(items.Select(i => i.Status).ToArray());

        return new SettingsSection
        {
            Title = "Logging",
            Description = "Logging configuration",
            Status = loggingStatus,
            Items = items,
            IsRequired = true
        };
    }

    private SettingsSection CreateAPISection()
    {
        var items = new List<SettingsItem>();

        var backendUrl = _config["BACKEND_URL"];
        if (string.IsNullOrWhiteSpace(backendUrl))
        {
            backendUrl = Environment.GetEnvironmentVariable("ASPNETCORE_URLS")?.Split(';')[0];
        }

        var status = string.IsNullOrWhiteSpace(backendUrl)
            ? SystemSettingsStatus.Fail
            : SystemSettingsStatus.Pass;

        items.Add(new SettingsItem
        {
            Name = "API Configuration",
            Value = backendUrl ?? "Not configured",
            Status = status,
            Description = status == SystemSettingsStatus.Pass
                ? $"Backend available at {backendUrl}"
                : "Backend URL not configured",
            Recommendation = status == SystemSettingsStatus.Fail
                ? "Set BACKEND_URL environment variable or ASPNETCORE_URLS"
                : "",
            IsRequired = true
        });

        var apiStatus = _statusEngine.CalculateOverallStatus(items.Select(i => i.Status).ToArray());

        return new SettingsSection
        {
            Title = "API",
            Description = "API endpoint configuration",
            Status = apiStatus,
            Items = items,
            IsRequired = true
        };
    }

    private SettingsSection CreateOptionalFeaturesSection()
    {
        var items = new List<SettingsItem>();

        // AI Configuration
        var aiProvider = _config["AI:Provider"];
        var aiStatus = string.IsNullOrWhiteSpace(aiProvider)
            ? SystemSettingsStatus.Warning
            : SystemSettingsStatus.Pass;

        items.Add(new SettingsItem
        {
            Name = "AI Provider Configuration",
            Value = aiProvider ?? "Not configured",
            Status = aiStatus,
            Description = aiStatus == SystemSettingsStatus.Pass
                ? $"AI enabled with {aiProvider}"
                : "AI features disabled",
            Recommendation = aiStatus == SystemSettingsStatus.Warning
                ? "Configure AI:Provider to enable AI features"
                : "",
            IsRequired = false
        });

        // Azure DevOps Integration
        var adoEnabled = _config.GetValue<bool>("AzureDevOps:Enabled");
        var adoStatus = !adoEnabled
            ? SystemSettingsStatus.Warning
            : SystemSettingsStatus.Pass;

        items.Add(new SettingsItem
        {
            Name = "Azure DevOps Integration",
            Value = adoEnabled ? "Enabled" : "Disabled",
            Status = adoStatus,
            Description = adoStatus == SystemSettingsStatus.Pass
                ? "Azure DevOps integration enabled"
                : "Azure DevOps integration not enabled",
            Recommendation = adoStatus == SystemSettingsStatus.Warning
                ? "Enable AzureDevOps:Enabled to use ADO integration"
                : "",
            IsRequired = false
        });

        // Export Configuration
        var htmlExportEnabled = _config.GetValue<bool>("Features:ExportHTML");
        var jsonExportEnabled = _config.GetValue<bool>("Features:ExportJSON");
        var exportFormats = new List<string>();
        if (htmlExportEnabled) exportFormats.Add("HTML");
        if (jsonExportEnabled) exportFormats.Add("JSON");

        items.Add(new SettingsItem
        {
            Name = "Export Configuration",
            Value = exportFormats.Count > 0 ? string.Join(", ", exportFormats) : "Using defaults",
            Status = SystemSettingsStatus.Pass,
            Description = $"Export formats available: {(exportFormats.Count > 0 ? string.Join(", ", exportFormats) : "JSON, HTML")}",
            Recommendation = "",
            IsRequired = false
        });

        var optionalStatus = _statusEngine.CalculateOverallStatus(items.Select(i => i.Status).ToArray());

        return new SettingsSection
        {
            Title = "Optional Features",
            Description = "Optional feature configuration",
            Status = optionalStatus,
            Items = items,
            IsRequired = false
        };
    }
}
