using BirkNext.Api.Data;
using BirkNext.Api.Data.Migrations;
using BirkNext.Api.Configuration;
using BirkNext.Api.GraphQL;
using BirkNext.Api.Middleware;
using BirkNext.Api.Services;
using BirkNext.Api.Services.ImplementationTraceability;
using BirkNext.Api.Services.ApiQuality;
using BirkNext.Api.Services.QualityReview;
using BirkNext.Api.Services.Analysis;
using BirkNext.Api.Services.Library;
using BirkNext.Api.Services.Review;
using BirkNext.Api.Services.WasmPerformance;
using BirkNext.Api.Services.WasmSecurity;
using BirkNext.Api.Services.FrontendBrowserRuntime;
using BirkNext.Api.Services.FrontendAccessibility;
using BirkNext.Api.Services.FrontendLighthouse;
using BirkNext.Api.Services.FrontendPassiveSecurity;
using BirkNext.Api.Services.FrontendQualityEngines;
using BirkNext.Api.Services.FrontendQualityEngines.Readiness;
using BirkNext.Api.Services.TargetEnvironmentDetection;
using BirkNext.Api.Services.AuthenticatedReview;
using HotChocolate.AspNetCore;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Json;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

builder.Host.UseSerilog((ctx, lc) =>
{
    var logPath = ctx.Configuration["LoggingSettings:LogPath"] ?? "./logs";
    var levelStr = ctx.Configuration["LoggingSettings:MinimumLevel"] ?? "Information";

    var level = Enum.TryParse<LogEventLevel>(levelStr, ignoreCase: true, out var parsedLevel)
        ? parsedLevel
        : LogEventLevel.Information;

    var absoluteLogPath = System.IO.Path.IsPathRooted(logPath)
        ? logPath
        : System.IO.Path.GetFullPath(System.IO.Path.Combine(ctx.HostingEnvironment.ContentRootPath, logPath));

    Directory.CreateDirectory(absoluteLogPath);

    lc.MinimumLevel.Is(level)
      .Enrich.FromLogContext()
      .WriteTo.Console(new JsonFormatter())
      .WriteTo.File(
          path: System.IO.Path.Combine(absoluteLogPath, "backend-serilog-.log"),
          rollingInterval: RollingInterval.Day,
          outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} {Level:u3}] {Message:lj}{NewLine}{Exception}",
          retainedFileCountLimit: 31,
          shared: true);
});

var frontendOrigin = builder.Configuration["FRONTEND_ORIGIN"] ?? "http://localhost:5173";

builder.Services.AddCors(options =>
    options.AddPolicy("Frontend", policy => policy
        .WithOrigins(frontendOrigin)
        .AllowAnyHeader()
        .AllowAnyMethod()));

builder.Services.AddControllers();

var databaseConnectionString = DatabaseConnection.GetConnectionString(builder.Configuration);

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(databaseConnectionString));

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<AdminService>();
builder.Services.AddScoped<ISystemSettingsStatusEngine, SystemSettingsStatusEngine>();
builder.Services.AddScoped<IGeneralPageService, GeneralPageService>();
builder.Services.AddScoped<IConfigurationHealthPageService, ConfigurationHealthPageService>();
builder.Services.AddScoped<IEnvironmentDiagnosticsPageService, EnvironmentDiagnosticsPageService>();
builder.Services.AddScoped<IRuntimeDiagnosticsPageService, RuntimeDiagnosticsPageService>();
builder.Services.AddScoped<IReviewContextValidationPageService, ReviewContextValidationPageService>();
builder.Services.AddScoped<IDocumentationHealthPageService, DocumentationHealthPageService>();
builder.Services.AddScoped<IPlatformPageService, PlatformPageService>();
builder.Services.AddScoped<IFeatureVisibilityPageService, FeatureVisibilityPageService>();
builder.Services.AddScoped<ITargetEnvironmentsPageService, TargetEnvironmentsPageService>();
builder.Services.AddScoped<IAIPageService, AIPageService>();
builder.Services.AddScoped<IMaintenancePageService, MaintenancePageService>();
builder.Services.AddScoped<ISystemDiagnosticsPageService, SystemDiagnosticsPageService>();
// ── Quality Review Page Model Builders ─────────────────────────────────────
builder.Services.AddScoped<IQualityReviewPageModelBuilder_QualityReview, QualityReviewPageModelBuilder>();
builder.Services.AddScoped<IQualityReviewPageModelBuilder_ApiQuality, ApiQualityReviewPageModelBuilder>();
builder.Services.AddScoped<IQualityReviewPageModelBuilder_FrontendQuality, FrontendQualityReviewPageModelBuilder>();
builder.Services.AddScoped<IQualityReviewPageModelBuilder_IntegrationQuality, IntegrationQualityReviewPageModelBuilder>();
builder.Services.AddScoped<IQualityReviewPageModelService, QualityReviewPageModelService>();
// ── Analysis Page Model Builders ─────────────────────────────────────────────
builder.Services.AddScoped<ISpecDriftPageModelBuilder, SpecDriftPageModelBuilder>();
builder.Services.AddScoped<IImpactAnalysisPageModelBuilder, ImpactAnalysisPageModelBuilder>();
builder.Services.AddScoped<IRequirementsTraceabilityPageModelBuilder, RequirementsTraceabilityPageModelBuilder>();
builder.Services.AddScoped<IImplementationReviewPageModelBuilder, ImplementationReviewPageModelBuilder>();
builder.Services.AddScoped<IImplementationTraceabilityPageModelBuilder, ImplementationTraceabilityPageModelBuilder>();
builder.Services.AddScoped<IAnalysisPageModelService, AnalysisPageModelService>();
// ── Library Page Model Builders ──────────────────────────────────────────
builder.Services.AddScoped<ISampleProjectCatalogService, SampleProjectCatalogService>();
builder.Services.AddScoped<IQAArtifactLibraryPageModelBuilder, QAArtifactLibraryPageModelBuilder>();
builder.Services.AddScoped<ISampleProjectsPageModelBuilder, SampleProjectsPageModelBuilder>();
builder.Services.AddScoped<ILibraryPageModelService, LibraryPageModelService>();
// ── Review Page Model Builders ───────────────────────────────────────────
builder.Services.AddScoped<IDashboardPageModelBuilder, DashboardPageModelBuilder>();
builder.Services.AddScoped<IConstitutionExplorerPageModelBuilder, ConstitutionExplorerPageModelBuilder>();
builder.Services.AddScoped<IDataModelExplorerPageModelBuilder, DataModelExplorerPageModelBuilder>();
builder.Services.AddScoped<IPlanExplorerPageModelBuilder, PlanExplorerPageModelBuilder>();
builder.Services.AddScoped<ITaskExplorerPageModelBuilder, TaskExplorerPageModelBuilder>();
builder.Services.AddScoped<ReviewPageModelService>();
builder.Services.AddScoped<IMigrationIntegrityValidator, MigrationIntegrityValidator>();
builder.Services.AddScoped<IEnvironmentDiagnosticsService, EnvironmentDiagnosticsService>();
builder.Services.AddScoped<IConfigurationHealthService, ConfigurationHealthService>();
builder.Services.AddScoped<IWorkspacePersistenceService, WorkspacePersistenceService>();
builder.Services.AddScoped<IAutoSaveService, AutoSaveService>();
builder.Services.AddScoped<IWorkspaceArtifactStatusService, WorkspaceArtifactStatusService>();
builder.Services.AddScoped<IRecommendedWorkflowService, RecommendedWorkflowService>();
builder.Services.AddScoped<ScenarioService>();
builder.Services.AddScoped<ReviewedCandidateService>();
builder.Services.AddScoped<CandidateLinkService>();
builder.Services.AddScoped<QaDeltaReviewService>();
builder.Services.AddScoped<ProjectDocumentService>();
builder.Services.AddScoped<TraceLinkService>();
builder.Services.AddScoped<TraceabilitySuggestionService>();
builder.Services.AddScoped<ImpactAnalysisService>();
builder.Services.AddScoped<AIChangeAuditService>();
builder.Services.AddScoped<SpecDriftDetectionService>();
builder.Services.AddScoped<CodeTraceabilityService>();
builder.Services.AddScoped<AIQaAuditorService>();
// ── Azure DevOps Implementation Traceability ────────────────────────────────
builder.Services.Configure<AzureDevOpsOptions>(options =>
{
    builder.Configuration.GetSection(AzureDevOpsOptions.SectionName).Bind(options);
    // Environment variable overrides appsettings — never log the value
    var envPat = Environment.GetEnvironmentVariable("ADO_PAT");
    if (!string.IsNullOrWhiteSpace(envPat))
        options.Pat = envPat;
});

{
    var adoEnabled  = builder.Configuration.GetValue<bool>($"{AzureDevOpsOptions.SectionName}:Enabled");
    var configPat   = builder.Configuration.GetValue<string>($"{AzureDevOpsOptions.SectionName}:Pat") ?? string.Empty;
    var adoEnvPat   = Environment.GetEnvironmentVariable("ADO_PAT") ?? string.Empty;
    var hasValidPat = !string.IsNullOrWhiteSpace(configPat) || !string.IsNullOrWhiteSpace(adoEnvPat);

    if (adoEnabled && hasValidPat)
    {
        builder.Services.AddHttpClient<AzureDevOpsImplementationEvidenceProvider>();
        builder.Services.AddScoped<IImplementationEvidenceProvider, AzureDevOpsImplementationEvidenceProvider>();
    }
    else
    {
        builder.Services.AddScoped<IImplementationEvidenceProvider, MockImplementationEvidenceProvider>();
    }
}

// Connection tester is always registered — checks options at runtime.
builder.Services.AddHttpClient<AzureDevOpsConnectionTester>();

// Blazor WASM Security Review
builder.Services.AddHttpClient<IBlazorWasmSecurityReviewService, BlazorWasmSecurityReviewService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("BirkNext-WasmSecurityScanner/1.0");
});

// Blazor WASM Performance Review — asset discovery + startup analysis
builder.Services.AddHttpClient<IWasmAssetDiscoveryService, WasmAssetDiscoveryService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(120);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("BirkNext-WasmPerfScanner/1.0");
});
builder.Services.AddSingleton<IWasmStartupAnalysisService, WasmStartupAnalysisService>();
builder.Services.AddSingleton<IWasmCachingAnalysisService, WasmCachingAnalysisService>();
builder.Services.AddHttpClient<IWasmApiAnalysisService, WasmApiAnalysisService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("BirkNext-WasmPerfScanner/1.0");
});
builder.Services.AddSingleton<IWasmPerformanceReadinessService, WasmPerformanceReadinessService>();

// Frontend Browser Runtime Review — Chromium-based runtime analysis (disabled by default)
builder.Services.Configure<FrontendBrowserRuntimeOptions>(
    builder.Configuration.GetSection(FrontendBrowserRuntimeOptions.SectionName));
builder.Services.PostConfigure<FrontendBrowserRuntimeOptions>(options =>
    options.Enabled = FrontendQualityEngineEnablement.Resolve(
        builder.Configuration, FrontendQualityEngineId.BrowserRuntime, options.Enabled));
builder.Services.AddScoped<BrowserTargetValidator>(provider =>
{
    var environment = provider.GetRequiredService<IWebHostEnvironment>();
    // Allow loopback for test/development scenarios only where TestFixtureController is available
    var allowLoopback = environment.IsDevelopment();
    return new BrowserTargetValidator(allowLoopback);
});
builder.Services.AddScoped<BrowserResourceClassifier>();
builder.Services.AddScoped<BrowserEvidenceSanitizer>();
builder.Services.AddScoped<BrowserRuntimeFindingClassifier>();
builder.Services.AddScoped<IFrontendBrowserRuntimeReviewService, FrontendBrowserRuntimeReviewService>();

// Interactive authenticated review is local-workstation-only and disabled by default.
builder.Services.Configure<AuthenticatedReviewOptions>(
    builder.Configuration.GetSection(AuthenticatedReviewOptions.SectionName));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IAuthenticatedBrowserHost, PlaywrightAuthenticatedBrowserHost>();
builder.Services.AddSingleton<AuthenticationOriginPolicy>();
builder.Services.AddSingleton<AuthenticatedBrowserSessionManager>();
builder.Services.AddSingleton<IAuthenticatedBrowserSessionManager>(sp => sp.GetRequiredService<AuthenticatedBrowserSessionManager>());
builder.Services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<AuthenticatedBrowserSessionManager>());

// Target Environment Detection — for configuration discovery
builder.Services.AddHttpClient<ITargetEnvironmentDetectionService, TargetEnvironmentDetectionService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("BirkNext-TargetDetection/1.0");
});
builder.Services.Configure<FrontendAccessibilityOptions>(
    builder.Configuration.GetSection(FrontendAccessibilityOptions.SectionName));
builder.Services.PostConfigure<FrontendAccessibilityOptions>(options =>
    options.Enabled = FrontendQualityEngineEnablement.Resolve(
        builder.Configuration, FrontendQualityEngineId.Accessibility, options.Enabled));
builder.Services.AddScoped<AccessibilityEvidenceSanitizer>();
builder.Services.AddScoped<AccessibilityNormalizer>();
builder.Services.AddScoped<IFrontendAccessibilityReviewService, FrontendAccessibilityReviewService>();
builder.Services.Configure<FrontendLighthouseOptions>(
    builder.Configuration.GetSection(FrontendLighthouseOptions.SectionName));
builder.Services.PostConfigure<FrontendLighthouseOptions>(options =>
    options.Enabled = FrontendQualityEngineEnablement.Resolve(
        builder.Configuration, FrontendQualityEngineId.Lighthouse, options.Enabled));
builder.Services.AddScoped<LighthouseEvidenceSanitizer>();
builder.Services.AddScoped<IFrontendLighthouseReviewService, FrontendLighthouseReviewService>();
builder.Services.AddScoped<PassiveSecurityEvidenceSanitizer>();
builder.Services.AddScoped<PassiveSecurityTargetAuthorizer>();
builder.Services.AddSingleton<IZapProcessRunner, ZapProcessRunner>();
builder.Services.AddScoped<IFrontendZapPassiveReviewService, FrontendZapPassiveReviewService>();

// Frontend Quality Engine Capability Model (Phase 1 backend foundation)
builder.Services.Configure<FrontendQualityCapabilitiesPolicy>(
    builder.Configuration.GetSection(FrontendQualityCapabilitiesPolicy.SectionName));
builder.Services.Configure<FrontendQualityEnginePreferences>(
    builder.Configuration.GetSection(FrontendQualityEnginePreferences.SectionName));
builder.Services.AddScoped<FrontendQualityEngineLegacyConfigInterpreter>();
builder.Services.AddScoped<BrowserRuntimeReadinessProvider>();
builder.Services.AddScoped<AccessibilityReadinessProvider>();
builder.Services.AddScoped<LighthouseReadinessProvider>();
builder.Services.AddScoped<PassiveSecurityReadinessProvider>();
builder.Services.AddScoped<IFrontendQualityEngineReadinessProvider>(sp => sp.GetRequiredService<BrowserRuntimeReadinessProvider>());
builder.Services.AddScoped<IFrontendQualityEngineReadinessProvider>(sp => sp.GetRequiredService<AccessibilityReadinessProvider>());
builder.Services.AddScoped<IFrontendQualityEngineReadinessProvider>(sp => sp.GetRequiredService<LighthouseReadinessProvider>());
builder.Services.AddScoped<IFrontendQualityEngineReadinessProvider>(sp => sp.GetRequiredService<PassiveSecurityReadinessProvider>());
builder.Services.AddScoped<IFrontendQualityEngineReadinessAggregator, FrontendQualityEngineReadinessAggregator>();
builder.Services.AddScoped<IFrontendQualityEngineStatusService, FrontendQualityEngineStatusService>();

// API Quality Review
builder.Services.AddHttpClient<IApiQualityReviewService, ApiQualityReviewService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(60);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("BirkNext-ApiQualityScanner/1.0");
});

builder.Services.AddHttpClient("Anthropic", client =>
{
    client.DefaultRequestHeaders.Add("x-api-key", builder.Configuration["Anthropic:ApiKey"] ?? string.Empty);
    client.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
    client.Timeout = TimeSpan.FromSeconds(60);
});

builder.Services
    .AddGraphQLServer()
    .AddQueryType<Query>()
    .AddMutationType<Mutation>()
    .AddType<ScenarioObjectType>()
    .AddType<CreateScenarioResultType>()
    .AddType<DeleteScenarioPayloadObjectType>()
    .AddType<ReviewedCandidateObjectType>()
    .AddType<CandidateLinkObjectType>()
    .AddType<QaDeltaReviewObjectType>()
    .AddType<TraceLinkObjectType>()
    .AddType<TraceLinkWithTestObjectType>()
    .AddType<TraceabilityMatrixRowObjectType>()
    .AddType<CoverageSummaryObjectType>()
    .AddType<ImpactedTestObjectType>()
    .AddType<RegressionItemObjectType>()
    .AddType<RequirementImpactSummaryObjectType>()
    .AddType<RequirementImpactObjectType>()
    .AddType<RequirementRiskItemObjectType>()
    .AddType<ImpactSummaryObjectType>()
    .AddType<AuditAffectedRequirementObjectType>()
    .AddType<AuditAffectedTestObjectType>()
    .AddType<ChangeAuditReportObjectType>()
    .AddType<DriftRequirementObjectType>()
    .AddType<DriftFindingObjectType>()
    .AddType<SpecDriftReportObjectType>()
    .AddType<CodeFileObjectType>()
    .AddType<CodeLinkObjectType>()
    .AddType<CodeLinkWithScenarioObjectType>()
    .AddType<CodeImpactObjectType>()
    .AddType<CodeSummaryObjectType>()
    .AddType<QaScoreDeductionObjectType>()
    .AddType<QaAuditReportObjectType>()
    .AddType<TraceabilitySuggestionObjectType>()
    .AddType<TraceabilitySuggestionItemObjectType>()
    .AddType<SuggestionGenerationResultObjectType>()
    .AddDiagnosticEventListener<OperationDiagnosticEventListener>()
    .ConfigureSchema(b => b.ModifyOptions(o => o.UseXmlDocumentation = true));

var app = builder.Build();

try
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    if (db.Database.IsRelational())
        await db.Database.MigrateAsync();
}
catch (PostgresException ex) when (ex.SqlState == "28P01")
{
    throw new InvalidOperationException(
        DatabaseConnection.AuthFailureMessage(databaseConnectionString),
        ex);
}

app.UseStaticFiles();

app.UseCors("Frontend");
app.UseMiddleware<CorrelationIdMiddleware>();
app.MapControllers();

app.MapGraphQL()
   .WithOptions(new GraphQLServerOptions
   {
       Tool = { Enable = app.Environment.IsDevelopment() }
   });

app.MapFallbackToFile("index.html");

app.Run();

public partial class Program { }
