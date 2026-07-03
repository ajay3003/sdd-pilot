using BirkNext.Api.Models.Admin;

namespace BirkNext.Api.Services;

public interface IConfigurationHealthService
{
    Task<ConfigurationHealthReport> GetConfigurationHealthAsync();
}

public sealed class ConfigurationHealthService : IConfigurationHealthService
{
    private readonly IConfiguration _config;
    private readonly IWebHostEnvironment _env;
    private readonly ISystemSettingsStatusEngine _statusEngine;
    private readonly ILogger<ConfigurationHealthService> _logger;

    public ConfigurationHealthService(
        IConfiguration config,
        IWebHostEnvironment env,
        ISystemSettingsStatusEngine statusEngine,
        ILogger<ConfigurationHealthService> logger)
    {
        _config = config;
        _env = env;
        _statusEngine = statusEngine;
        _logger = logger;
    }

    public async Task<ConfigurationHealthReport> GetConfigurationHealthAsync()
    {
        var report = new ConfigurationHealthReport();

        // Required checks
        report.RequiredChecks.Add(CheckEnvironment());
        report.RequiredChecks.Add(CheckDatabaseConfiguration());
        report.RequiredChecks.Add(CheckLoggingConfiguration());
        report.RequiredChecks.Add(CheckAPIConfiguration());

        // Optional checks
        report.OptionalChecks.Add(CheckAIConfiguration());
        report.OptionalChecks.Add(CheckAzureDevOpsConfiguration());
        report.OptionalChecks.Add(CheckExportConfiguration());

        // Calculate counts
        var allChecks = report.RequiredChecks.Concat(report.OptionalChecks).ToList();
        report.PassCount = allChecks.Count(c => c.Status == "Pass");
        report.WarningCount = allChecks.Count(c => c.Status == "Warning");
        report.FailCount = allChecks.Count(c => c.Status == "Fail");
        report.UnavailableCount = allChecks.Count(c => c.Status == "Unavailable");

        // Calculate overall status using the shared status engine
        var allStatusEnums = allChecks.Select(c => ConvertStringStatusToEnum(c.Status)).ToArray();
        var overallStatusEnum = _statusEngine.CalculateOverallStatus(allStatusEnums);
        report.OverallStatus = ConvertEnumStatusToString(overallStatusEnum);

        return await Task.FromResult(report);
    }

    private ConfigurationHealthCheck CheckEnvironment()
    {
        var envName = _env.EnvironmentName;
        return new ConfigurationHealthCheck
        {
            Name = "Environment",
            Status = "Pass",
            Message = envName,
            Details = $"ASPNETCORE_ENVIRONMENT={envName}",
            IsRequired = true
        };
    }

    private ConfigurationHealthCheck CheckDatabaseConfiguration()
    {
        var provider = _config["DatabaseSettings:Provider"] ?? "Unknown";
        var mode = _config["DatabaseSettings:Mode"] ?? "Unknown";
        var host = _config["DatabaseSettings:Host"];

        if (string.IsNullOrWhiteSpace(host))
        {
            return new ConfigurationHealthCheck
            {
                Name = "Database Configuration",
                Status = "Fail",
                Message = "Missing database host configuration",
                Details = "DatabaseSettings:Host not configured",
                IsRequired = true
            };
        }

        return new ConfigurationHealthCheck
        {
            Name = "Database Configuration",
            Status = "Pass",
            Message = $"{provider} ({mode})",
            Details = $"Host: {host}, Provider: {provider}",
            IsRequired = true
        };
    }

    private ConfigurationHealthCheck CheckLoggingConfiguration()
    {
        var level = _config["Logging:LogLevel:Default"] ?? "Information";
        var provider = _config["Logging:Provider"] ?? "Console";

        return new ConfigurationHealthCheck
        {
            Name = "Logging Configuration",
            Status = "Pass",
            Message = $"{level} ({provider})",
            Details = $"Logging level: {level}, Provider: {provider}",
            IsRequired = true
        };
    }

    private ConfigurationHealthCheck CheckAPIConfiguration()
    {
        var backendUrl = _config["BACKEND_URL"];
        if (string.IsNullOrWhiteSpace(backendUrl))
        {
            backendUrl = Environment.GetEnvironmentVariable("ASPNETCORE_URLS")?.Split(';')[0];
        }

        if (string.IsNullOrWhiteSpace(backendUrl))
        {
            return new ConfigurationHealthCheck
            {
                Name = "API Configuration",
                Status = "Fail",
                Message = "Backend URL not configured",
                Details = "BACKEND_URL or ASPNETCORE_URLS environment variable not set",
                IsRequired = true
            };
        }

        return new ConfigurationHealthCheck
        {
            Name = "API Configuration",
            Status = "Pass",
            Message = "Configured",
            Details = $"Backend URL: {backendUrl}",
            IsRequired = true
        };
    }

    private ConfigurationHealthCheck CheckAIConfiguration()
    {
        var aiProvider = _config["AI:Provider"];
        var status = string.IsNullOrWhiteSpace(aiProvider) ? "Warning" : "Pass";
        var message = string.IsNullOrWhiteSpace(aiProvider) ? "Not configured" : aiProvider;

        return new ConfigurationHealthCheck
        {
            Name = "AI Provider Configuration",
            Status = status,
            Message = message,
            Details = status == "Warning"
                ? "AI features will be disabled or use defaults"
                : $"Provider: {aiProvider}",
            IsRequired = false
        };
    }

    private ConfigurationHealthCheck CheckAzureDevOpsConfiguration()
    {
        var enabled = _config.GetValue<bool>("AzureDevOps:Enabled");
        var patConfigured = _config.GetValue<bool>("AzureDevOps:PatConfigured");

        if (!enabled)
        {
            return new ConfigurationHealthCheck
            {
                Name = "Azure DevOps Integration",
                Status = "Warning",
                Message = "Not enabled",
                Details = "Azure DevOps integration is disabled. Implementation Traceability will not be available.",
                IsRequired = false
            };
        }

        if (!patConfigured)
        {
            return new ConfigurationHealthCheck
            {
                Name = "Azure DevOps Integration",
                Status = "Warning",
                Message = "PAT not configured",
                Details = "Azure DevOps is enabled but PAT (Personal Access Token) is not configured. Set the ADO_PAT environment variable.",
                IsRequired = false
            };
        }

        return new ConfigurationHealthCheck
        {
            Name = "Azure DevOps Integration",
            Status = "Pass",
            Message = "Fully configured",
            Details = "Azure DevOps integration is enabled and PAT is configured",
            IsRequired = false
        };
    }

    private ConfigurationHealthCheck CheckExportConfiguration()
    {
        var htmlExportEnabled = _config.GetValue<bool>("Features:ExportHTML");
        var jsonExportEnabled = _config.GetValue<bool>("Features:ExportJSON");

        var enabledFormats = new List<string>();
        if (htmlExportEnabled) enabledFormats.Add("HTML");
        if (jsonExportEnabled) enabledFormats.Add("JSON");

        var message = enabledFormats.Count > 0 ? string.Join(", ", enabledFormats) : "Using defaults";

        return new ConfigurationHealthCheck
        {
            Name = "Export Configuration",
            Status = "Pass",
            Message = message,
            Details = $"Export formats: {(enabledFormats.Count > 0 ? string.Join(", ", enabledFormats) : "Default (JSON, HTML)")}",
            IsRequired = false
        };
    }

    private static SystemSettingsStatus ConvertStringStatusToEnum(string status) => status switch
    {
        "Pass" => SystemSettingsStatus.Pass,
        "Warning" => SystemSettingsStatus.Warning,
        "Fail" => SystemSettingsStatus.Fail,
        "Unavailable" => SystemSettingsStatus.Unavailable,
        _ => SystemSettingsStatus.Pass
    };

    private static string ConvertEnumStatusToString(SystemSettingsStatus status) => status switch
    {
        SystemSettingsStatus.Pass => "Pass",
        SystemSettingsStatus.Warning => "Warning",
        SystemSettingsStatus.Fail => "Fail",
        SystemSettingsStatus.Unavailable => "Unavailable",
        _ => "Pass"
    };
}
