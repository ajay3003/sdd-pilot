namespace BirkNext.Api.Models.Admin;

public class SystemSettingsResponse
{
    public ApplicationInfo Application { get; set; } = new();
    public FrontendInfo Frontend { get; set; } = new();
    public BackendInfo Backend { get; set; } = new();
    public DatabaseInfo Database { get; set; } = new();
    public RuntimeInfo Runtime { get; set; } = new();
    public LoggingInfo Logging { get; set; } = new();
    public MaintenanceInfo Maintenance { get; set; } = new();
}

public class ApplicationInfo
{
    public string ApplicationName { get; set; } = "";
    public string Environment { get; set; } = "";
    public string Version { get; set; } = "";
    public string? BuildNumber { get; set; }
    public string? CommitSha { get; set; }
    public string PackageMode { get; set; } = "";
}

public class FrontendInfo
{
    public string FrontendBaseUrl { get; set; } = "";
    public string ApiBaseUrl { get; set; } = "";
    public string GraphQlEndpoint { get; set; } = "";
    public string EnvironmentName { get; set; } = "";
    public bool StaticHostingMode { get; set; }
}

public class BackendInfo
{
    public string BackendBaseUrl { get; set; } = "";
    public string AspNetCoreEnvironment { get; set; } = "";
    public string ListeningUrls { get; set; } = "";
    public string CorsAllowedOrigins { get; set; } = "";
}

public class DatabaseInfo
{
    public string Mode { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public string DatabaseName { get; set; } = "";
    public string Username { get; set; } = "";
    public string Provider { get; set; } = "";
    public string MigrationStatus { get; set; } = "";
    public string ComposeProjectName { get; set; } = "";
    public string ExpectedVolumeName { get; set; } = "";
}

public class RuntimeInfo
{
    public string ComposeProjectName { get; set; } = "";
    public string ExpectedDatabaseVolume { get; set; } = "";
    public string PackageMode { get; set; } = "";
    public string? LocalPackageRoot { get; set; }
    public bool RunningFromPublishedArtifact { get; set; }
}

public class LoggingInfo
{
    public string Provider { get; set; } = "";
    public string MinimumLevel { get; set; } = "";
    public List<string> Sinks { get; set; } = [];
    public string LogPath { get; set; } = "";
    public string SeqUrl { get; set; } = "";
    public bool StructuredLogging { get; set; }
}

public class MaintenanceInfo
{
    public bool ResetAllowed { get; set; }
    public string DatabaseMode { get; set; } = "";
    public string ResetNotAllowedReason { get; set; } = "";
}

public class ResetDatabaseRequest
{
    public string Confirmation { get; set; } = "";
}

public class ResetDatabaseResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
}
