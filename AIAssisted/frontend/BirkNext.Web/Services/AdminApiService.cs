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

    public async Task<FeatureVisibilityDto?> GetFeatureVisibilityAsync()
    {
        try
        {
            return await _client.GetFromJsonAsync<FeatureVisibilityDto>("api/admin/feature-visibility");
        }
        catch
        {
            return null;
        }
    }

    public async Task<EditableSettingsDto?> GetEditableSettingsAsync()
    {
        try
        {
            return await _client.GetFromJsonAsync<EditableSettingsDto>("api/admin/editable-settings");
        }
        catch
        {
            return null;
        }
    }

    public async Task<(bool Success, string Message)> SaveSettingsAsync(SaveSettingsRequest request)
    {
        try
        {
            var response = await _client.PostAsJsonAsync("api/admin/system-settings", request);
            var result = await response.Content.ReadFromJsonAsync<SaveSettingsResponse>();
            return (result?.Success == true, result?.Message ?? (response.IsSuccessStatusCode ? "Saved." : "Save failed."));
        }
        catch (Exception ex)
        {
            return (false, $"Request failed: {ex.Message}");
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
    [JsonPropertyName("application")]      public ApplicationDto      Application      { get; set; } = new();
    [JsonPropertyName("frontend")]         public FrontendDto         Frontend         { get; set; } = new();
    [JsonPropertyName("backend")]          public BackendDto          Backend          { get; set; } = new();
    [JsonPropertyName("database")]         public DatabaseDto         Database         { get; set; } = new();
    [JsonPropertyName("runtime")]          public RuntimeDto          Runtime          { get; set; } = new();
    [JsonPropertyName("logging")]          public LoggingDto          Logging          { get; set; } = new();
    [JsonPropertyName("maintenance")]      public MaintenanceDto      Maintenance      { get; set; } = new();
    [JsonPropertyName("featureVisibility")] public FeatureVisibilityDto FeatureVisibility { get; set; } = new();
    [JsonPropertyName("azureDevOps")]      public AzureDevOpsInfoDto  AzureDevOps      { get; set; } = new();
}

public class AzureDevOpsInfoDto
{
    [JsonPropertyName("enabled")]         public bool   Enabled         { get; set; }
    [JsonPropertyName("organizationUrl")] public string OrganizationUrl { get; set; } = "";
    [JsonPropertyName("project")]         public string Project         { get; set; } = "";
    [JsonPropertyName("repositoryId")]    public string RepositoryId    { get; set; } = "";
    [JsonPropertyName("defaultBranch")]   public string DefaultBranch   { get; set; } = "main";
    [JsonPropertyName("patConfigured")]   public bool   PatConfigured   { get; set; }
    [JsonPropertyName("patSource")]       public string PatSource       { get; set; } = "Missing";
    [JsonPropertyName("activelyUsed")]    public bool   ActivelyUsed    { get; set; }
}

public class AzureDevOpsConnectionTestResultDto
{
    [JsonPropertyName("overallSuccess")] public bool                          OverallSuccess { get; set; }
    [JsonPropertyName("errorMessage")]   public string?                       ErrorMessage   { get; set; }
    [JsonPropertyName("checks")]         public List<AzureDevOpsCheckDto>     Checks         { get; set; } = [];
}

public class AzureDevOpsCheckDto
{
    [JsonPropertyName("name")]    public string  Name    { get; set; } = "";
    [JsonPropertyName("success")] public bool    Success { get; set; }
    [JsonPropertyName("detail")]  public string? Detail  { get; set; }
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

public class LogFileEntryDto
{
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("exists")] public bool Exists { get; set; }
}

public class LoggingDto
{
    [JsonPropertyName("provider")] public string Provider { get; set; } = "";
    [JsonPropertyName("minimumLevel")] public string MinimumLevel { get; set; } = "";
    [JsonPropertyName("sinks")] public List<string> Sinks { get; set; } = [];
    [JsonPropertyName("logPath")] public string LogPath { get; set; } = "";
    [JsonPropertyName("resolvedLogsFolder")] public string ResolvedLogsFolder { get; set; } = "";
    [JsonPropertyName("seqUrl")] public string SeqUrl { get; set; } = "";
    [JsonPropertyName("structuredLogging")] public bool StructuredLogging { get; set; }
    [JsonPropertyName("logFiles")] public List<LogFileEntryDto> LogFiles { get; set; } = [];
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

public class EditableSettingsDto
{
    [JsonPropertyName("featureVisibility")] public EditableFeatureVisibilityDto FeatureVisibility { get; set; } = new();
    [JsonPropertyName("logging")]           public EditableLoggingDto           Logging           { get; set; } = new();
    [JsonPropertyName("admin")]             public EditableAdminDto             Admin             { get; set; } = new();
}

public class EditableFeatureVisibilityDto
{
    [JsonPropertyName("platform")] public List<FeatureEntryDto> Platform { get; set; } = [];
    [JsonPropertyName("core")]     public List<FeatureEntryDto> Core     { get; set; } = [];
    [JsonPropertyName("advanced")] public List<FeatureEntryDto> Advanced { get; set; } = [];
}

public class FeatureEntryDto
{
    [JsonPropertyName("key")]    public string Key    { get; set; } = "";
    [JsonPropertyName("label")]  public string Label  { get; set; } = "";
    [JsonPropertyName("value")]  public bool   Value  { get; set; }
    [JsonPropertyName("locked")] public bool   Locked { get; set; }
}

public class EditableLoggingDto
{
    [JsonPropertyName("minimumLevel")] public string MinimumLevel { get; set; } = "Information";
    [JsonPropertyName("seqUrl")]       public string SeqUrl       { get; set; } = "";
}

public class EditableAdminDto
{
    [JsonPropertyName("showDiagnostics")] public bool ShowDiagnostics { get; set; } = true;
}

public class SaveSettingsRequest
{
    [JsonPropertyName("featureVisibility")] public Dictionary<string, bool>? FeatureVisibility { get; set; }
    [JsonPropertyName("logging")]           public SaveLoggingRequest?        Logging           { get; set; }
    [JsonPropertyName("admin")]             public SaveAdminRequest?          Admin             { get; set; }
}

public class SaveLoggingRequest
{
    [JsonPropertyName("minimumLevel")] public string? MinimumLevel { get; set; }
    [JsonPropertyName("seqUrl")]       public string? SeqUrl       { get; set; }
}

public class SaveAdminRequest
{
    [JsonPropertyName("showDiagnostics")] public bool? ShowDiagnostics { get; set; }
}

public class SaveSettingsResponse
{
    [JsonPropertyName("success")] public bool   Success { get; set; }
    [JsonPropertyName("message")] public string Message { get; set; } = "";
}

public class FeatureVisibilityDto
{
    [JsonPropertyName("recommendedWorkflow")]  public bool RecommendedWorkflow  { get; set; } = true;
    [JsonPropertyName("userGuide")]            public bool UserGuide            { get; set; } = true;
    [JsonPropertyName("dashboard")]            public bool Dashboard            { get; set; } = true;
    [JsonPropertyName("specificationReview")]  public bool SpecificationReview  { get; set; } = true;
    [JsonPropertyName("qaArtifactLibrary")]    public bool QaArtifactLibrary    { get; set; } = true;
    [JsonPropertyName("sampleProjects")]       public bool SampleProjects       { get; set; } = true;
    [JsonPropertyName("createTestScenario")]   public bool CreateTestScenario   { get; set; } = true;
    [JsonPropertyName("legacyTraceabilityNavigationEnabled")] public bool LegacyTraceabilityNavigationEnabled { get; set; } = false;
    [JsonPropertyName("traceabilityCoverage")]    public bool TraceabilityCoverage    { get; set; } = true;
    [JsonPropertyName("traceabilitySuggestions")] public bool TraceabilitySuggestions { get; set; } = true;
    [JsonPropertyName("codeTraceability")]        public bool CodeTraceability        { get; set; } = true;
    [JsonPropertyName("specComparison")]       public bool SpecComparison       { get; set; } = false;
    [JsonPropertyName("specificationDeltas")]  public bool SpecificationDeltas  { get; set; } = false;
    [JsonPropertyName("taskDeltas")]           public bool TaskDeltas           { get; set; } = false;
    [JsonPropertyName("impactAnalysis")]       public bool ImpactAnalysis       { get; set; } = true;
    [JsonPropertyName("specDrift")]            public bool SpecDrift            { get; set; } = true;
    [JsonPropertyName("implementationReview")]       public bool ImplementationReview       { get; set; } = true;
    [JsonPropertyName("implementationTraceability")] public bool ImplementationTraceability { get; set; } = true;
    [JsonPropertyName("blazorWasmSecurityReview")]        public bool BlazorWasmSecurityReview        { get; set; } = true;
    [JsonPropertyName("blazorWasmPerformanceReview")]     public bool BlazorWasmPerformanceReview     { get; set; } = true;
    [JsonPropertyName("taskExplorer")]         public bool TaskExplorer         { get; set; } = true;
    [JsonPropertyName("aiChangeReview")]       public bool AiChangeReview       { get; set; } = true;
    [JsonPropertyName("enableExtractionReview")] public bool EnableExtractionReview { get; set; } = false;
    [JsonPropertyName("enableArchitectureView")] public bool EnableArchitectureView { get; set; } = false;
    [JsonPropertyName("constitutionExplorer")]  public bool ConstitutionExplorer  { get; set; } = true;
    [JsonPropertyName("dataModelExplorer")]     public bool DataModelExplorer     { get; set; } = true;
    [JsonPropertyName("planExplorer")]              public bool PlanExplorer             { get; set; } = true;
    [JsonPropertyName("artifactTraceability")]        public bool ArtifactTraceability       { get; set; } = true;
    [JsonPropertyName("adminSystemSettings")]         public bool AdminSystemSettings        { get; set; } = true;
    [JsonPropertyName("qualityReview")]               public bool QualityReview              { get; set; } = true;
}
