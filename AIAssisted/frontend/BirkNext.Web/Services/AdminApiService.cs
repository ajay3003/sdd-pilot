using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace BirkNext.Web.Services;

public class AdminApiService
{
    private readonly HttpClient _client;

    public AdminApiService(HttpClient client)
    {
        _client = client;
    }

    public async Task<SystemSettingsDto?> GetSystemSettingsAsync()
    {
        try
        {
            return await _client.GetFromJsonAsync<SystemSettingsDto>("api/admin/system-settings");
        }
        catch
        {
            return null;
        }
    }

    public async Task<(bool Success, string Message)> ResetLocalDatabaseAsync()
    {
        try
        {
            var response = await _client.PostAsJsonAsync(
                "api/admin/reset-local-database",
                new { confirmation = "RESET" });

            var result = await response.Content.ReadFromJsonAsync<ResetResponseDto>();
            return (result?.Success == true, result?.Message ?? (response.IsSuccessStatusCode ? "Done." : "Reset failed."));
        }
        catch (Exception ex)
        {
            return (false, $"Request failed: {ex.Message}");
        }
    }
}

public class SystemSettingsDto
{
    [JsonPropertyName("application")] public ApplicationDto Application { get; set; } = new();
    [JsonPropertyName("frontend")] public FrontendDto Frontend { get; set; } = new();
    [JsonPropertyName("backend")] public BackendDto Backend { get; set; } = new();
    [JsonPropertyName("database")] public DatabaseDto Database { get; set; } = new();
    [JsonPropertyName("runtime")] public RuntimeDto Runtime { get; set; } = new();
    [JsonPropertyName("logging")] public LoggingDto Logging { get; set; } = new();
    [JsonPropertyName("maintenance")] public MaintenanceDto Maintenance { get; set; } = new();
}

public class ApplicationDto
{
    [JsonPropertyName("applicationName")] public string ApplicationName { get; set; } = "";
    [JsonPropertyName("environment")] public string Environment { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("buildNumber")] public string? BuildNumber { get; set; }
    [JsonPropertyName("commitSha")] public string? CommitSha { get; set; }
    [JsonPropertyName("packageMode")] public string PackageMode { get; set; } = "";
}

public class FrontendDto
{
    [JsonPropertyName("frontendBaseUrl")] public string FrontendBaseUrl { get; set; } = "";
    [JsonPropertyName("apiBaseUrl")] public string ApiBaseUrl { get; set; } = "";
    [JsonPropertyName("graphQlEndpoint")] public string GraphQlEndpoint { get; set; } = "";
    [JsonPropertyName("environmentName")] public string EnvironmentName { get; set; } = "";
    [JsonPropertyName("staticHostingMode")] public bool StaticHostingMode { get; set; }
}

public class BackendDto
{
    [JsonPropertyName("backendBaseUrl")] public string BackendBaseUrl { get; set; } = "";
    [JsonPropertyName("aspNetCoreEnvironment")] public string AspNetCoreEnvironment { get; set; } = "";
    [JsonPropertyName("listeningUrls")] public string ListeningUrls { get; set; } = "";
    [JsonPropertyName("corsAllowedOrigins")] public string CorsAllowedOrigins { get; set; } = "";
}

public class DatabaseDto
{
    [JsonPropertyName("mode")] public string Mode { get; set; } = "";
    [JsonPropertyName("host")] public string Host { get; set; } = "";
    [JsonPropertyName("port")] public int Port { get; set; }
    [JsonPropertyName("databaseName")] public string DatabaseName { get; set; } = "";
    [JsonPropertyName("username")] public string Username { get; set; } = "";
    [JsonPropertyName("provider")] public string Provider { get; set; } = "";
    [JsonPropertyName("migrationStatus")] public string MigrationStatus { get; set; } = "";
    [JsonPropertyName("composeProjectName")] public string ComposeProjectName { get; set; } = "";
    [JsonPropertyName("expectedVolumeName")] public string ExpectedVolumeName { get; set; } = "";
}

public class RuntimeDto
{
    [JsonPropertyName("composeProjectName")] public string ComposeProjectName { get; set; } = "";
    [JsonPropertyName("expectedDatabaseVolume")] public string ExpectedDatabaseVolume { get; set; } = "";
    [JsonPropertyName("packageMode")] public string PackageMode { get; set; } = "";
    [JsonPropertyName("localPackageRoot")] public string? LocalPackageRoot { get; set; }
    [JsonPropertyName("runningFromPublishedArtifact")] public bool RunningFromPublishedArtifact { get; set; }
}

public class LoggingDto
{
    [JsonPropertyName("provider")] public string Provider { get; set; } = "";
    [JsonPropertyName("minimumLevel")] public string MinimumLevel { get; set; } = "";
    [JsonPropertyName("sinks")] public List<string> Sinks { get; set; } = [];
    [JsonPropertyName("logPath")] public string LogPath { get; set; } = "";
    [JsonPropertyName("seqUrl")] public string SeqUrl { get; set; } = "";
    [JsonPropertyName("structuredLogging")] public bool StructuredLogging { get; set; }
}

public class MaintenanceDto
{
    [JsonPropertyName("resetAllowed")] public bool ResetAllowed { get; set; }
    [JsonPropertyName("databaseMode")] public string DatabaseMode { get; set; } = "";
    [JsonPropertyName("resetNotAllowedReason")] public string ResetNotAllowedReason { get; set; } = "";
}

public class ResetResponseDto
{
    [JsonPropertyName("success")] public bool Success { get; set; }
    [JsonPropertyName("message")] public string Message { get; set; } = "";
}
