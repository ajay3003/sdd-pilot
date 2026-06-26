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
    public FeatureVisibilityInfo FeatureVisibility { get; set; } = new();
    public AzureDevOpsInfo AzureDevOps { get; set; } = new();
}

public class AzureDevOpsInfo
{
    public bool Enabled { get; set; }
    public string OrganizationUrl { get; set; } = "";
    public string Project { get; set; } = "";
    public string RepositoryId { get; set; } = "";
    public string DefaultBranch { get; set; } = "main";
    public bool PatConfigured { get; set; }
    public string PatSource { get; set; } = "Missing"; // "EnvironmentVariable" | "Configuration" | "Missing"
    public bool ActivelyUsed { get; set; }             // true = ADO provider selected at startup
}

public class AzureDevOpsConnectionTestResult
{
    public bool OverallSuccess { get; set; }
    public string? ErrorMessage { get; set; }
    public List<AzureDevOpsCheckResult> Checks { get; set; } = [];
}

public class AzureDevOpsCheckResult
{
    public string Name { get; set; } = "";
    public bool Success { get; set; }
    public string? Detail { get; set; }
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

public class LogFileEntry
{
    public string Label { get; set; } = "";
    public string Path { get; set; } = "";
    public bool Exists { get; set; }
}

public class LoggingInfo
{
    public string Provider { get; set; } = "";
    public string MinimumLevel { get; set; } = "";
    public List<string> Sinks { get; set; } = [];
    public string LogPath { get; set; } = "";
    public string ResolvedLogsFolder { get; set; } = "";
    public string SeqUrl { get; set; } = "";
    public bool StructuredLogging { get; set; }
    public List<LogFileEntry> LogFiles { get; set; } = [];
}

public class MaintenanceInfo
{
    public bool ResetAllowed { get; set; }
    public string DatabaseMode { get; set; } = "";
    public string ResetNotAllowedReason { get; set; } = "";
}

public class FeatureVisibilityInfo
{
    public bool RecommendedWorkflow { get; set; } = true;
    public bool UserGuide { get; set; } = true;
    public bool Dashboard { get; set; } = true;
    public bool SpecificationReview { get; set; } = true;
    public bool QaArtifactLibrary { get; set; } = true;
    public bool CreateTestScenario { get; set; } = true;
    public bool LegacyTraceabilityNavigationEnabled { get; set; } = false;
    public bool TraceabilityCoverage { get; set; } = true;
    public bool TraceabilitySuggestions { get; set; } = true;
    public bool CodeTraceability { get; set; } = true;
    public bool SpecComparison { get; set; } = true;
    public bool SpecificationDeltas { get; set; } = true;
    public bool TaskDeltas { get; set; } = true;
    public bool ImpactAnalysis { get; set; } = true;
    public bool SpecDrift { get; set; } = true;
    public bool ImplementationReview { get; set; } = true;
    public bool ImplementationTraceability { get; set; } = true;
    public bool ConstitutionExplorer { get; set; } = true;
    public bool PlanExplorer { get; set; } = true;
    public bool ArtifactTraceability { get; set; } = true;
    public bool ConstitutionCompliance { get; set; } = true;
    public bool BlazorWasmSecurityReview { get; set; } = true;
    public bool BlazorWasmPerformanceReview { get; set; } = true;
    public bool TaskExplorer { get; set; } = true;
    public bool QaAuditor { get; set; } = true;
    public bool DeliveryReadiness { get; set; } = true;
    public bool AiChangeReview { get; set; } = true;
    public bool QaReadiness { get; set; } = true;
    public bool EnableExtractionReview { get; set; } = false;
    public bool EnableArchitectureView { get; set; } = false;
    public bool AdminSystemSettings { get; set; } = true;
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

// ── Editable Settings ──────────────────────────────────────────────────────

public class EditableSettingsResponse
{
    public EditableFeatureVisibilitySection FeatureVisibility { get; set; } = new();
    public EditableLoggingSection Logging { get; set; } = new();
    public EditableAdminSection Admin { get; set; } = new();
}

public class EditableFeatureVisibilitySection
{
    public List<FeatureVisibilityEntry> Platform { get; set; } = [];
    public List<FeatureVisibilityEntry> Core { get; set; } = [];
    public List<FeatureVisibilityEntry> Advanced { get; set; } = [];
}

public class FeatureVisibilityEntry
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public bool Value { get; set; }
    public bool Locked { get; set; }
}

public class EditableLoggingSection
{
    public string MinimumLevel { get; set; } = "Information";
    public string SeqUrl { get; set; } = "";
}

public class EditableAdminSection
{
    public bool ShowDiagnostics { get; set; } = true;
}

// ── Save Settings ──────────────────────────────────────────────────────────

public class SaveSettingsRequest
{
    public Dictionary<string, bool>? FeatureVisibility { get; set; }
    public SaveLoggingSettings? Logging { get; set; }
    public SaveAdminSettings? Admin { get; set; }
}

public class SaveLoggingSettings
{
    public string? MinimumLevel { get; set; }
    public string? SeqUrl { get; set; }
}

public class SaveAdminSettings
{
    public bool? ShowDiagnostics { get; set; }
}

public class SaveSettingsResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public List<string> Errors { get; set; } = [];
}
