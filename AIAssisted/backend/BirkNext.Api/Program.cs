using BirkNext.Api.Services.ManagedEdge;
using BirkNext.Api.Services.LocalHttpsProxy;
using BirkNext.Api.Controllers;
using BirkNext.Api.Data;
using BirkNext.Api.Data.Migrations;
using BirkNext.Api.Configuration;
using BirkNext.Api.GraphQL;
using BirkNext.Api.Filters;
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
using BirkNext.Api.Services.ContractAnalysis;
using HotChocolate.AspNetCore;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using System.Net;
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

// Active Event execution uses an Entra-issued API bearer token. Missing authority/audience leaves the scheme unable to validate
// callers; there is no development bypass and no trusted identity header path.
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    options.Authority = builder.Configuration["Authentication:Entra:Authority"];
    options.Audience = builder.Configuration["Authentication:Entra:Audience"];
    options.RequireHttpsMetadata = true;
});
builder.Services.AddSingleton<IAuthorizationHandler, BirkNext.Api.Services.ActiveEventTesting.ActiveEventPermissionHandler>();
builder.Services.AddAuthorization(options => options.AddPolicy("ActiveEventExecute", policy =>
    policy.RequireAuthenticatedUser().AddRequirements(new BirkNext.Api.Services.ActiveEventTesting.ActiveEventPermissionRequirement())));

var databaseConnectionString = DatabaseConnection.GetConnectionString(builder.Configuration);

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(databaseConnectionString));

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<AdminService>();
builder.Services.AddScoped<BirkNext.Api.Services.LocalDataReset.ILocalDatabaseReset>(sp => sp.GetRequiredService<AdminService>());
builder.Services.AddSingleton<BirkNext.Api.Services.LocalDataReset.LocalDataResetState>();
builder.Services.AddScoped<BirkNext.Api.Services.LocalDataReset.LocalDataResetCoordinator>();
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
builder.Services.AddScoped<SourceChangeImpactService>();
builder.Services.AddScoped<ImpactAnalysisRunService>();
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
    var config = provider.GetRequiredService<IConfiguration>();
    // Allow loopback only if explicitly configured (Development environment by default)
    var allowLoopback = config.GetValue<bool>("TargetDetection:AllowLoopback", false);
    return new BrowserTargetValidator(allowLoopback);
});
builder.Services.AddScoped<BrowserResourceClassifier>();
builder.Services.AddScoped<BrowserEvidenceSanitizer>();
builder.Services.AddScoped<BrowserRuntimeFindingClassifier>();
builder.Services.AddScoped<IFrontendBrowserRuntimeReviewService, FrontendBrowserRuntimeReviewService>();

// Interactive authenticated review is local-workstation-only and disabled by default.
builder.Services.Configure<AuthenticatedReviewOptions>(
    builder.Configuration.GetSection(AuthenticatedReviewOptions.SectionName));
builder.Services.Configure<ManagedEdgeOptions>(builder.Configuration.GetSection("ManagedEdge"));
builder.Services.AddSingleton<IManagedEdgeConnector, ManagedEdgeConnector>();
builder.Services.AddSingleton<ManagedEdgeCdpService>();
builder.Services.AddSingleton<IManagedEdgeCdpService>(sp => sp.GetRequiredService<ManagedEdgeCdpService>());
builder.Services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<ManagedEdgeCdpService>());
builder.Services.AddScoped<ManagedEdgeLocalCallerFilter>();
builder.Services.AddSingleton<IEdgeInstallationLocator, WindowsEdgeInstallationLocator>();
builder.Services.AddSingleton<IEdgePolicyReader, WindowsEdgePolicyReader>();
builder.Services.AddSingleton<IManagedEdgeLauncher, ProcessManagedEdgeLauncher>();
builder.Services.AddSingleton<IManagedEdgePreflightService, ManagedEdgePreflightService>();
builder.Services.Configure<BirkNext.Api.Services.HeadlessAuthDiagnostic.HeadlessDiagnosticOptions>(builder.Configuration.GetSection("HeadlessAuthDiagnostic"));
builder.Services.AddSingleton<BirkNext.Api.Services.HeadlessAuthDiagnostic.BrowserAutomationEvidenceStore>();
builder.Services.AddSingleton<BirkNext.Api.Services.HeadlessAuthDiagnostic.IHeadlessBrowserFactory, BirkNext.Api.Services.HeadlessAuthDiagnostic.PlaywrightHeadlessBrowserFactory>();
builder.Services.AddSingleton<BirkNext.Api.Services.HeadlessAuthDiagnostic.IHeadlessDiagnosticService, BirkNext.Api.Services.HeadlessAuthDiagnostic.HeadlessDiagnosticService>();

// Browser Automation Diagnostic: Playwright launches its OWN Microsoft Edge against a dedicated BirkNext profile and
// reports whether automation control survives the configured target. It never signs in, never touches the normal Edge
// profile and never changes a browser or system setting — it observes behaviour so IT has something concrete to read.
builder.Services.AddSingleton<BirkNext.Api.Services.BrowserAutomationDiagnostic.IDiagnosticBrowserFactory,
    BirkNext.Api.Services.BrowserAutomationDiagnostic.PlaywrightDiagnosticBrowserFactory>();
builder.Services.AddSingleton<BirkNext.Api.Services.BrowserAutomationDiagnostic.IBrowserAutomationDiagnosticService>(sp =>
    new BirkNext.Api.Services.BrowserAutomationDiagnostic.BrowserAutomationDiagnosticService(
        sp.GetRequiredService<BirkNext.Api.Services.BrowserAutomationDiagnostic.IDiagnosticBrowserFactory>(),
        sp.GetRequiredService<IEdgeInstallationLocator>(),
        sp.GetRequiredService<ILogger<BirkNext.Api.Services.BrowserAutomationDiagnostic.BrowserAutomationDiagnosticService>>(),
        isLocalWorkstation: () => sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AuthenticatedReviewOptions>>().Value.IsLocalWorkstation,
        // The pre-authentication application marker comes from the same per-Target-Environment verification
        // contract the authentication diagnostic reads, so a target is identified by one configuration, not two.
        applicationMarker: request => BirkNext.Api.Services.HeadlessAuthDiagnostic.HeadlessVerificationContract.ApplicationMarkerFor(
            sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<BirkNext.Api.Services.HeadlessAuthDiagnostic.HeadlessDiagnosticOptions>>().Value,
            request.TargetEnvironmentId, request.TargetUrl),
        policyReader: sp.GetRequiredService<IEdgePolicyReader>()));
// It reports whether automation survives in HEADED and HEADLESS Edge separately, because headless is what unattended
// CI would use and "it worked when I watched it" is not an answer for CI.

// DEV-only loopback HTTPS inspection proxy: explicit opt-in per Target Environment, LocalWorkstation runtime only, credential memory-only.
builder.Services.Configure<LocalHttpsProxyOptions>(builder.Configuration.GetSection(LocalHttpsProxyOptions.SectionName));
builder.Services.AddSingleton<IProxyCertificateStore>(_ => OperatingSystem.IsWindows() ? new WindowsUserCertificateStore() : new EphemeralCertificateStore { TrustManagementSupported = false });
builder.Services.AddSingleton<IProxyCertificateAuthority, ProxyCertificateAuthority>();
builder.Services.AddSingleton<TransientAuthenticatedApiContextStore>();
builder.Services.AddSingleton<ITransientAuthenticatedApiContextStore>(sp => sp.GetRequiredService<TransientAuthenticatedApiContextStore>());
builder.Services.AddSingleton<IUpstreamConnector>(sp => new DirectUpstreamConnector(sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<LocalHttpsProxyOptions>>().Value.UpstreamProxy));
builder.Services.AddSingleton<LocalHttpsProxyService>();
builder.Services.AddSingleton<DedicatedCompanionProvisioner>();
builder.Services.AddSingleton<IProxyEdgeLauncher, ProxyEdgeLauncher>();
// Runtime evidence about the one Edge process the proxy launched: its own arguments and the owner of each proxy connection.
builder.Services.AddSingleton<IOwnedEdgeProcessInspector, WindowsOwnedEdgeProcessInspector>();
builder.Services.AddSingleton<ILocalHttpsProxyService>(sp => sp.GetRequiredService<LocalHttpsProxyService>());
builder.Services.AddSingleton<ILocalHttpsProxySessionAccess>(sp => sp.GetRequiredService<LocalHttpsProxyService>());
builder.Services.AddSingleton<ILocalHttpsProxyStatusQuery>(sp => sp.GetRequiredService<LocalHttpsProxyService>());
builder.Services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<LocalHttpsProxyService>());
builder.Services.AddSingleton<IAuthenticatedApiExecutionService, AuthenticatedApiExecutionService>();
builder.Services.AddSingleton<IAuthenticatedReviewGateway, AuthenticatedReviewGateway>();
builder.Services.AddSingleton<BirkNext.Api.Services.FrontendQualityEngines.IFrontendAuthenticatedApiSurfaceService, BirkNext.Api.Services.FrontendQualityEngines.FrontendAuthenticatedApiSurfaceService>();
builder.Services.AddSingleton(TimeProvider.System);
// BirkNext Browser Companion: loopback-only pairing + safe page evidence from the user's normal managed Edge (no CDP, no Playwright, no token).
builder.Services.AddSingleton<BirkNext.Api.Services.BrowserCompanion.BrowserCompanionEvidenceSanitizer>(sp => new BirkNext.Api.Services.BrowserCompanion.BrowserCompanionEvidenceSanitizer(new BrowserEvidenceSanitizer()));
builder.Services.AddSingleton<BirkNext.Api.Services.BrowserCompanion.BrowserCompanionService>();
builder.Services.AddSingleton<BirkNext.Api.Services.BrowserCompanion.IBrowserCompanionService>(sp => sp.GetRequiredService<BirkNext.Api.Services.BrowserCompanion.BrowserCompanionService>());
builder.Services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<BirkNext.Api.Services.BrowserCompanion.BrowserCompanionService>());

// Critical E2E Regression. One runner, several step executors: browser steps and integration steps differ entirely in
// how they reach the system and not at all in what a result means.
builder.Services.AddSingleton<BirkNext.Api.Services.CriticalE2E.ICriticalE2EStore, BirkNext.Api.Services.CriticalE2E.CriticalE2EStore>();
builder.Services.AddSingleton<BirkNext.Api.Services.CriticalE2E.ICriticalE2EStepExecutor, BirkNext.Api.Services.CriticalE2E.CompanionBrowserStepExecutor>();
builder.Services.AddSingleton<BirkNext.Api.Services.CriticalE2E.ICriticalE2EStepExecutor, BirkNext.Api.Services.CriticalE2E.IntegrationStepExecutor>();
builder.Services.AddSingleton<BirkNext.Api.Services.CriticalE2E.ICriticalE2EStepExecutor, BirkNext.Api.Services.CriticalE2E.EventHubStepExecutor>();
builder.Services.AddSingleton<BirkNext.Api.Services.CriticalE2E.CriticalE2ERunner>();
builder.Services.AddSingleton<BirkNext.Api.Services.CriticalE2E.ICriticalE2EService, BirkNext.Api.Services.CriticalE2E.CriticalE2EService>();
builder.Services.AddScoped<BirkNext.Api.Controllers.BrowserCompanionExtensionCallerFilter>();
builder.Services.AddSingleton<IAuthenticatedBrowserHost, PlaywrightAuthenticatedBrowserHost>();
builder.Services.AddSingleton<AuthenticationOriginPolicy>();
builder.Services.AddSingleton<AuthenticatedBrowserSessionManager>();
builder.Services.AddSingleton<IAuthenticatedBrowserSessionManager>(sp => sp.GetRequiredService<AuthenticatedBrowserSessionManager>());
builder.Services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<AuthenticatedBrowserSessionManager>());

// Target Environment Detection — for configuration discovery
builder.Services.Configure<TargetDetectionOptions>(
    builder.Configuration.GetSection("TargetDetection"));
builder.Services.AddScoped<RequireTargetDetectionHttpsFilter>();
builder.Services.AddScoped<DevelopmentOnlyControllerFilter>();
builder.Services.AddScoped<ITargetHostResolver, DnsTargetHostResolver>();
builder.Services.AddScoped<IClientFrameworkDetector, ClientFrameworkDetector>();
// Rate limiter removed (API complexity) - replaced with controller-level input validation
// and per-minute request monitoring via logging.
// SECURITY: Disable automatic redirect following to prevent TOCTOU gap.
// Manual redirect handling in TargetEnvironmentDetectionService validates each redirect before following.
builder.Services.AddHttpClient<ITargetEnvironmentDetectionService, TargetEnvironmentDetectionService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("BirkNext-TargetDetection/1.0");
})
.ConfigurePrimaryHttpMessageHandler((_) => new SocketsHttpHandler
{
    // Disable automatic redirect following to prevent TOCTOU gap.
    // This prevents TOCTOU (Time-of-check Time-of-use) vulnerability where redirect destination
    // is followed before BrowserTargetValidator checks it.
    // Manual redirect handling in TargetEnvironmentDetectionService validates each redirect before following.
    // See: CheckTargetWithRedirectAsync in TargetEnvironmentDetectionService
    AllowAutoRedirect = false,
    UseProxy = false  // Also disable proxy for security
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
// API Quality Review v2 engine (public client: no cookies, no automatic decompression so compression evidence stays visible, no redirects into other hosts)
builder.Services.AddScoped<BirkNext.Api.Services.ApiQuality.IGraphQlSchemaArtifactStore, BirkNext.Api.Services.ApiQuality.GraphQlSchemaArtifactService>();
builder.Services.Configure<BirkNext.Api.Services.ApiQuality.ApiActiveTestingOptions>(builder.Configuration.GetSection(BirkNext.Api.Services.ApiQuality.ApiActiveTestingOptions.SectionName));
builder.Services.AddSingleton<BirkNext.Api.Services.ApiQuality.IApiEnvironmentSafetyPolicy, BirkNext.Api.Services.ApiQuality.ApiEnvironmentSafetyPolicy>();
// Runtime security checks that cross the read-only boundary (authorization scenarios, body fuzzing) need a server-registered trusted target.
builder.Services.Configure<BirkNext.Api.Services.ApiQuality.Security.SecurityTestingOptions>(builder.Configuration.GetSection(BirkNext.Api.Services.ApiQuality.Security.SecurityTestingOptions.SectionName));
builder.Services.AddSingleton<BirkNext.Api.Services.ApiQuality.Security.ITrustedSecurityTargetRegistry, BirkNext.Api.Services.ApiQuality.Security.TrustedSecurityTargetRegistry>();
builder.Services.AddHttpClient<BirkNext.Api.Services.ApiQuality.Security.IAuthorizationScenarioService, BirkNext.Api.Services.ApiQuality.Security.AuthorizationScenarioService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("BirkNext-ApiReview-Authorization/1.0");
    client.DefaultRequestHeaders.TryAddWithoutValidation(BirkNext.LocalHttpsProxy.NetworkEvidencePolicy.ProvenanceHeader, "BirkNextDiagnostic");
}).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false, AutomaticDecompression = System.Net.DecompressionMethods.None, PooledConnectionLifetime = TimeSpan.FromMinutes(5) });
builder.Services.AddSingleton<BirkNext.Api.Services.ApiQuality.Fuzzing.ApiFuzzingRunCoordinator>();
// Safe fuzzing: same public-client shape as the review engine (no cookies, no redirects, bodies decoded by ResponseBodyReader).
builder.Services.AddHttpClient<BirkNext.Api.Services.ApiQuality.Fuzzing.IApiFuzzingService, BirkNext.Api.Services.ApiQuality.Fuzzing.ApiFuzzingService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("BirkNext-ApiReview-SafeFuzzing/1.0");
    client.DefaultRequestHeaders.TryAddWithoutValidation(BirkNext.LocalHttpsProxy.NetworkEvidencePolicy.ProvenanceHeader, "BirkNextDiagnostic");
}).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false, AutomaticDecompression = System.Net.DecompressionMethods.None, PooledConnectionLifetime = TimeSpan.FromMinutes(5) });
builder.Services.AddHttpClient<IApiReviewEngine, ApiReviewEngine>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("BirkNext-ApiReview/2.0");
}).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false, AutomaticDecompression = System.Net.DecompressionMethods.None, PooledConnectionLifetime = TimeSpan.FromMinutes(5) });

// Contract Analysis — messaging schema discovery
builder.Services.AddScoped<BirkNext.Api.Services.ContractAnalysis.IMessageSchemaDiscoveryService, BirkNext.Api.Services.ContractAnalysis.MessageSchemaDiscoveryService>();

// Contract Analysis (Phase 3-4)
builder.Services.AddHttpClient<IOpenApiSourceFetcher, OpenApiSourceFetcher>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("BirkNext-ContractAnalysis/1.0");
});
builder.Services.AddScoped<IOpenApiExtractor, OpenApiExtractor>();

// Contract Analysis - GraphQL (Phase 4)
builder.Services.AddHttpClient<IGraphQlSourceFetcher, GraphQlSourceFetcher>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("BirkNext-ContractAnalysis-GraphQL/1.0");
});
builder.Services.AddScoped<IGraphQlExtractor, GraphQlExtractor>();

builder.Services.AddScoped<IContractComparer, ContractComparer>();

// Integration catalog (Target Environment → Integrations) and Integration Quality Review over it. Read-only evidence ports:
// a DNS/TCP/TLS namespace probe; no runtime or contract adapter exists in this build, so those domains report Not assessed.
builder.Services.AddScoped<BirkNext.Api.Services.Integrations.IIntegrationCatalogService, BirkNext.Api.Services.Integrations.IntegrationCatalogService>();
builder.Services.AddScoped<BirkNext.Api.Services.Integrations.IIntegrationMessageFlowStore, BirkNext.Api.Services.Integrations.IntegrationMessageFlowStore>();
builder.Services.AddSingleton<BirkNext.Api.Services.Integrations.IIntegrationNamespaceProbe, BirkNext.Api.Services.Integrations.TlsNamespaceProbe>();
// Runtime evidence adapters are read-only and off unless IntegrationReview:Azure:Enabled is true (instance identity; no secrets in config/UI).
builder.Services.AddSingleton<BirkNext.Api.Services.Integrations.IIntegrationAzureCredential, BirkNext.Api.Services.Integrations.IntegrationAzureCredential>();
builder.Services.AddSingleton<BirkNext.Api.Services.Integrations.IEventHubMetadataSource, BirkNext.Api.Services.Integrations.AzureEventHubMetadataSource>();
builder.Services.AddHttpClient<BirkNext.Api.Services.Integrations.IEventHubConsumerGroupSource, BirkNext.Api.Services.Integrations.ArmConsumerGroupSource>(client => client.Timeout = TimeSpan.FromSeconds(20));
builder.Services.AddSingleton<BirkNext.Api.Services.Integrations.ICheckpointEvidenceSource, BirkNext.Api.Services.Integrations.BlobCheckpointEvidenceSource>();
builder.Services.AddSingleton<BirkNext.Api.Services.Integrations.ITelemetryEvidenceSource, BirkNext.Api.Services.Integrations.LogAnalyticsTelemetrySource>();
// Event Hub namespace + hub list (Azure Resource Manager GETs) and namespace metrics (Azure Monitor): same Azure gate and identity, never the data plane.
builder.Services.AddHttpClient<BirkNext.Api.Services.Integrations.EventHub.IEventHubNamespaceSource, BirkNext.Api.Services.Integrations.EventHub.ArmEventHubNamespaceSource>(client => client.Timeout = TimeSpan.FromSeconds(20))
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });
builder.Services.AddSingleton<BirkNext.Api.Services.Integrations.EventHub.IEventHubMetricsSource, BirkNext.Api.Services.Integrations.EventHub.AzureMonitorEventHubMetricsSource>();
builder.Services.AddScoped<BirkNext.Api.Services.Integrations.IIntegrationContractStore, BirkNext.Api.Services.Integrations.IntegrationContractStore>();
builder.Services.AddHttpClient<BirkNext.Api.Services.Integrations.IntegrationReviewEngine>(client => client.Timeout = TimeSpan.FromSeconds(15))
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });
builder.Services.AddScoped<BirkNext.Api.Services.Integrations.IIntegrationReviewService, BirkNext.Api.Services.Integrations.IntegrationReviewService>();
builder.Services.AddScoped<BirkNext.Api.Services.Integrations.SourceEvidence.IqrSourceStore>();
// Project Import: one archive feeds the artifact repository (frontend) and Source Analysis. Stages are bounded, per-upload ids, and durable
// across restarts in ProjectImport:StagingDirectory (default: a BirkNext folder under the system temp directory).
builder.Services.AddSingleton(sp => new BirkNext.Api.Services.ProjectImport.ProjectImportStagingStore(null,
    sp.GetRequiredService<IConfiguration>()["ProjectImport:StagingDirectory"] is { Length: > 0 } dir ? dir : BirkNext.Api.Services.ProjectImport.ProjectImportStagingStore.DefaultDirectory,
    sp.GetRequiredService<ILogger<BirkNext.Api.Services.ProjectImport.ProjectImportStagingStore>>()));
builder.Services.AddScoped<BirkNext.Api.Services.ProjectImport.ProjectImportService>();
builder.Services.AddSingleton<BirkNext.Api.Services.ProjectImport.ProjectCompatibilityDiagnosticService>();
builder.Services.AddSingleton<BirkNext.Api.Services.MarkdownDiagnosticsService>();
// Generated Documentation Health: reads generated-documentation evidence stored on Source Analysis snapshots (no archive access).
builder.Services.AddScoped<BirkNext.Api.Services.SourceAnalysis.GeneratedDocumentation.GeneratedDocumentationDiagnosticService>();
// Test evidence: execution-result providers (TRX first) and stateless preview/correlation against Source Analysis test discovery.
builder.Services.AddSingleton(sp => BirkNext.Api.Services.TestEvidence.TestEvidenceOptions.From(sp.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton<BirkNext.Api.Services.TestEvidence.ITestExecutionEvidenceProvider, BirkNext.Api.Services.TestEvidence.TrxTestExecutionEvidenceProvider>();
builder.Services.AddScoped<BirkNext.Api.Services.TestEvidence.TestExecutionImportService>();
// Performance Test Review: generic definitions/runs/baselines; k6 is the first provider (external executable, generated script, no user scripts).
builder.Services.AddSingleton(sp => BirkNext.Api.Services.PerformanceTests.PerformanceTestOptions.From(sp.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton<BirkNext.Api.Services.PerformanceTests.IProcessRunner, BirkNext.Api.Services.PerformanceTests.SystemProcessRunner>();
builder.Services.AddSingleton<BirkNext.Api.Services.ContainerRuntime.IContainerExecutionRuntime, BirkNext.Api.Services.ContainerRuntime.PodmanContainerExecutionRuntime>();
builder.Services.AddSingleton<BirkNext.Api.Services.PerformanceTests.IPerformanceTestProvider, BirkNext.Api.Services.PerformanceTests.K6PerformanceTestProvider>();
builder.Services.AddSingleton<BirkNext.Api.Services.PerformanceTests.PerformanceTestProviderRegistry>();
builder.Services.AddSingleton<BirkNext.Api.Services.PerformanceTests.PerformanceTestReadinessService>();
// Resource Stability: provider-based observation of approved components (Podman container stats; .NET runtime counters of BirkNext's own process).
builder.Services.AddSingleton<BirkNext.Api.Services.PerformanceTests.Resources.IResourceObservationProvider, BirkNext.Api.Services.PerformanceTests.Resources.PodmanResourceObservationProvider>();
builder.Services.AddSingleton<BirkNext.Api.Services.PerformanceTests.Resources.IResourceObservationProvider, BirkNext.Api.Services.PerformanceTests.Resources.DotNetRuntimeSelfObservationProvider>();
builder.Services.AddSingleton<BirkNext.Api.Services.PerformanceTests.Resources.ResourceObservationRegistry>();
builder.Services.AddSingleton<BirkNext.Api.Services.PerformanceTests.PerformanceTestExecutionService>();
builder.Services.AddSingleton<IHostedService, BirkNext.Api.Services.PerformanceTests.Resources.PerformanceTestShutdownService>();
builder.Services.AddScoped<BirkNext.Api.Services.PerformanceTests.PerformanceTestStore>();
// Shared read-only access to Source Analysis snapshots for every source-aware review (Source Analysis owns ingestion; reviews own meaning).
builder.Services.AddScoped<BirkNext.Api.Services.SourceAnalysis.IReviewSourceEvidenceProvider>(sp => BirkNext.Api.Services.SourceAnalysis.ReviewSourceEvidenceProvider.FromConfiguration(
    sp.GetRequiredService<BirkNext.Api.Services.Integrations.SourceEvidence.IqrSourceStore>(), sp.GetRequiredService<IConfiguration>()));
builder.Services.AddScoped<BirkNext.Api.Services.SecurityExpectations.ISecurityExpectationDiscoveryService>(sp => new BirkNext.Api.Services.SecurityExpectations.SecurityExpectationDiscoveryService(
    sp.GetRequiredService<BirkNext.Api.Data.AppDbContext>(), sp.GetRequiredService<BirkNext.Api.Services.SourceAnalysis.IReviewSourceEvidenceProvider>()));
builder.Services.AddScoped<BirkNext.Api.Services.Integrations.SourceDiscovery.SourceIntegrationService>();
// Azure Environment Analysis: the person's own interactive sign-in (dedicated Edge profile / device code; MFA and PIM are theirs), token in
// memory only, read-only Azure Resource Manager GETs + predefined Resource Graph queries. Off unless AzureEnvironment:Enabled and a ClientId.
builder.Services.Configure<BirkNext.Api.Services.AzureEnvironment.AzureEnvironmentOptions>(builder.Configuration.GetSection(BirkNext.Api.Services.AzureEnvironment.AzureEnvironmentOptions.SectionName));
builder.Services.AddSingleton<BirkNext.Api.Services.AzureEnvironment.IAzureTokenBroker, BirkNext.Api.Services.AzureEnvironment.MsalAzureTokenBroker>();
builder.Services.AddSingleton<BirkNext.Api.Services.AzureEnvironment.IAzureSignInBrowser, BirkNext.Api.Services.AzureEnvironment.DedicatedEdgeSignInBrowser>();
builder.Services.AddSingleton<BirkNext.Api.Services.AzureEnvironment.IAzureSignInService, BirkNext.Api.Services.AzureEnvironment.AzureSignInService>();
builder.Services.AddHttpClient<BirkNext.Api.Services.AzureEnvironment.IAzureManagementClient, BirkNext.Api.Services.AzureEnvironment.HttpAzureManagementClient>(client => client.Timeout = TimeSpan.FromSeconds(30))
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });
builder.Services.AddScoped<BirkNext.Api.Services.AzureEnvironment.IAzureEnvironmentCollector, BirkNext.Api.Services.AzureEnvironment.AzureEnvironmentCollector>();
builder.Services.AddScoped<BirkNext.Api.Services.AzureEnvironment.AzureEnvironmentSnapshotStore>();
builder.Services.AddScoped<BirkNext.Api.Services.AzureEnvironment.IAzureEnvironmentEvidenceProvider>(sp => BirkNext.Api.Services.AzureEnvironment.AzureEnvironmentEvidenceProvider.FromConfiguration(
    sp.GetRequiredService<BirkNext.Api.Services.AzureEnvironment.AzureEnvironmentSnapshotStore>(), sp.GetRequiredService<IConfiguration>()));
// Pipeline Review: delivery-flow interpretation over Source Analysis CI/CD evidence (no YAML parsing here); optional read-only Azure DevOps metadata.
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient<BirkNext.Api.Services.PipelineReview.IPipelineMetadataSource, BirkNext.Api.Services.PipelineReview.AzureDevOpsPipelineMetadataSource>(client => client.Timeout = TimeSpan.FromSeconds(20))
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });
builder.Services.AddScoped<BirkNext.Api.Services.PipelineReview.IPipelineReviewService, BirkNext.Api.Services.PipelineReview.PipelineReviewService>();
// AI-Generated Code Review: deterministic rules over Source Analysis snapshots (shared source-evidence provider; no upload, no model).
builder.Services.AddScoped<BirkNext.Api.Services.AiCodeReview.IAiCodeReviewService, BirkNext.Api.Services.AiCodeReview.AiCodeReviewService>();
// Active CDC tests (Phase 1): the one Event Hub SEND path, off unless ActiveCdcTests:Enabled; DEV/QA + enrolled destinations only, instance identity only.
builder.Services.AddSingleton(sp => new BirkNext.Api.Services.ActiveCdcTests.ActiveCdcPolicy(BirkNext.Api.Services.ActiveCdcTests.ActiveCdcOptions.From(sp.GetRequiredService<IConfiguration>())));
builder.Services.AddSingleton<BirkNext.Api.Services.ActiveCdcTests.IEventHubTestProducerFactory, BirkNext.Api.Services.ActiveCdcTests.AzureEventHubTestProducerFactory>();
builder.Services.AddSingleton<BirkNext.Api.Services.ActiveCdcTests.IEventHubTestSender, BirkNext.Api.Services.ActiveCdcTests.AzureEventHubTestSender>();
builder.Services.AddSingleton<BirkNext.Api.Services.ActiveEventTesting.IActiveEventTransportProvider, BirkNext.Api.Services.ActiveEventTesting.EventHubActiveEventTransportProvider>();
builder.Services.AddSingleton<BirkNext.Api.Services.ActiveEventTesting.IActiveEventTransportRegistry, BirkNext.Api.Services.ActiveEventTesting.ActiveEventTransportRegistry>();
builder.Services.AddSingleton<BirkNext.Api.Services.ActiveEventTesting.ActiveEventExecutionRunner>();
builder.Services.AddScoped<BirkNext.Api.Services.ActiveEventTesting.IActiveEventScenarioProvider, BirkNext.Api.Services.ActiveEventTesting.M2lbPersonScenarioProvider>();
builder.Services.AddScoped<BirkNext.Api.Services.ActiveEventTesting.IActiveEventScenarioRegistry, BirkNext.Api.Services.ActiveEventTesting.ActiveEventScenarioRegistry>();
builder.Services.AddSingleton<BirkNext.Api.Services.ActiveCdcTests.ActiveCdcRunStore>();
builder.Services.AddSingleton<BirkNext.Api.Services.ActiveEventTesting.ActiveEventRunStore>();
builder.Services.AddScoped<BirkNext.Api.Services.ActiveEventTesting.IActiveEventLifecycleService, BirkNext.Api.Services.ActiveEventTesting.ActiveEventLifecycleService>();
builder.Services.AddSingleton<BirkNext.Api.Services.ActiveCdcTests.ActiveCdcRunCoordinator>();
builder.Services.AddSingleton<BirkNext.Api.Services.ActiveCdcTests.ActiveCdcRunner>();
builder.Services.AddScoped<BirkNext.Api.Services.ActiveCdcTests.IActiveCdcTestService, BirkNext.Api.Services.ActiveCdcTests.ActiveCdcTestService>();
builder.Services.AddScoped<BirkNext.Api.Services.Integrations.IntegrationMappingEvidenceService>();
// Application messaging (Wolverine) evidence: syntax-only analysis of uploaded source + read-only handler telemetry (Azure-gated).
builder.Services.AddScoped<BirkNext.Api.Services.Integrations.ApplicationMessaging.IApplicationMessagingStore, BirkNext.Api.Services.Integrations.ApplicationMessaging.ApplicationMessagingStore>();
builder.Services.AddSingleton<BirkNext.Api.Services.Integrations.ApplicationMessaging.IApplicationMessagingTelemetrySource, BirkNext.Api.Services.Integrations.ApplicationMessaging.LogAnalyticsApplicationMessagingSource>();
// Service Bus transport evidence: read-only Azure Resource Manager GETs (Azure-gated); never the data plane.
builder.Services.AddHttpClient<BirkNext.Api.Services.Integrations.ServiceBus.IServiceBusMetadataSource, BirkNext.Api.Services.Integrations.ServiceBus.ArmServiceBusMetadataSource>(client => client.Timeout = TimeSpan.FromSeconds(20))
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });
// Azure Monitor metrics of the namespace: read-only, same instance-level Azure gate and credential as the Resource Manager metadata.
builder.Services.AddSingleton<BirkNext.Api.Services.Integrations.ServiceBus.IServiceBusMetricsSource, BirkNext.Api.Services.Integrations.ServiceBus.AzureMonitorServiceBusMetricsSource>();
builder.Services.AddScoped<BirkNext.Api.Services.Integrations.ServiceBus.ServiceBusEvidenceService>();
// SCIM identity provisioning evidence: source analysis + safe GET-only runtime checks (never a user write, list or publish; no redirects followed).
builder.Services.AddHttpClient<BirkNext.Api.Services.Integrations.Scim.IScimRuntimeProbe, BirkNext.Api.Services.Integrations.Scim.HttpScimRuntimeProbe>(client => client.Timeout = TimeSpan.FromSeconds(15))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddScoped<BirkNext.Api.Services.Integrations.Scim.IScimEvidenceService, BirkNext.Api.Services.Integrations.Scim.ScimEvidenceService>();
// Security Classification / Gradert tilgang review: source analysis + fixed safe GraphQL queries for configured synthetic test children only.
builder.Services.AddHttpClient<BirkNext.Api.Services.SecurityClassification.IClassificationLiveProbe, BirkNext.Api.Services.SecurityClassification.GraphQlClassificationProbe>(client => client.Timeout = TimeSpan.FromSeconds(15))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
// The temporary security test context lives in process memory only (lost on restart, never in the database).
builder.Services.AddSingleton<BirkNext.Api.Services.SecurityClassification.ClassificationTestContextStore>();
builder.Services.AddScoped<BirkNext.Api.Services.SecurityClassification.IClassificationReviewService>(sp => new BirkNext.Api.Services.SecurityClassification.ClassificationReviewService(
    sp.GetRequiredService<BirkNext.Api.Data.AppDbContext>(), sp.GetRequiredService<BirkNext.Api.Services.SecurityClassification.IClassificationLiveProbe>(),
    sp.GetRequiredService<BirkNext.Api.Services.SecurityClassification.ClassificationTestContextStore>(), sp.GetRequiredService<ILogger<BirkNext.Api.Services.SecurityClassification.ClassificationReviewService>>(),
    sp.GetService<IHttpContextAccessor>()?.HttpContext?.User.Identity is { IsAuthenticated: true, Name: { Length: > 0 } name } ? name : "local",
    sp.GetRequiredService<BirkNext.Api.Services.SourceAnalysis.IReviewSourceEvidenceProvider>()));
// Dependency / supply-chain review (Renovate policy): offline, read-only analysis of Source Analysis snapshots, inventories and SBOMs.
builder.Services.AddScoped<BirkNext.Api.Services.DependencyReview.IDependencyReviewService, BirkNext.Api.Services.DependencyReview.DependencyReviewService>();
builder.Services.AddScoped<BirkNext.Api.Services.DependencyReview.IDependencyReviewSourceScopeService, BirkNext.Api.Services.DependencyReview.DependencyReviewSourceScopeService>();
// Dependency health over stored inventories (no source upload): nuget.org registry metadata (GET only) and OSV advisories, bounded and cached;
// Renovate runtime from the existing Azure DevOps options (GET only, Not configured otherwise); deployed Blazor boot-manifest evidence.
builder.Services.Configure<BirkNext.Api.Services.DependencyReview.DependencyHealthOptions>(builder.Configuration.GetSection(BirkNext.Api.Services.DependencyReview.DependencyHealthOptions.SectionName));
builder.Services.AddSingleton<BirkNext.Api.Services.DependencyReview.DependencyEvidenceCache>();
builder.Services.AddHttpClient<BirkNext.Api.Services.DependencyReview.IPackageRegistryProvider, BirkNext.Api.Services.DependencyReview.NuGetRegistryProvider>(client => client.Timeout = TimeSpan.FromSeconds(30))
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = System.Net.DecompressionMethods.All, MaxConnectionsPerServer = 8 });
builder.Services.AddHttpClient<BirkNext.Api.Services.DependencyReview.IAdvisoryProvider, BirkNext.Api.Services.DependencyReview.OsvAdvisoryProvider>(client => client.Timeout = TimeSpan.FromSeconds(60))
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = System.Net.DecompressionMethods.All, MaxConnectionsPerServer = 4 });
builder.Services.AddHttpClient<BirkNext.Api.Services.DependencyReview.IDependencyAutomationSource, BirkNext.Api.Services.DependencyReview.AzureDevOpsRenovateAutomationSource>(client => client.Timeout = TimeSpan.FromSeconds(20))
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });
builder.Services.AddHttpClient<BirkNext.Api.Services.DependencyReview.IDeployedDependencySource, BirkNext.Api.Services.DependencyReview.BootManifestDeployedSource>(client => client.Timeout = TimeSpan.FromSeconds(20))
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = System.Net.DecompressionMethods.All });
builder.Services.AddScoped<BirkNext.Api.Services.DependencyReview.IDependencyHealthService, BirkNext.Api.Services.DependencyReview.DependencyHealthService>();
builder.Services.AddScoped<IContractDiscoveryService, ContractDiscoveryService>();

// Contract Analysis - Messaging/EventHub (Phase 5)
builder.Services.AddScoped<IAssemblyMetadataInspector, AssemblyMetadataInspector>();
builder.Services.AddScoped<IMessagingContractDiscoveryService, MessagingContractDiscoveryService>();

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

var authenticatedReview = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<AuthenticatedReviewOptions>>().Value;
app.Logger.LogInformation("Authenticated review configuration: Enabled={Enabled}, Runtime={Runtime}",
    authenticatedReview.Enabled, authenticatedReview.Runtime);

try
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    // One process migrates at a time (parallel test hosts and multiple instances share the database).
    if (db.Database.IsRelational())
        await DatabaseMigrationLock.MigrateAsync(db);
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
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.MapGraphQL()
   .WithOptions(new GraphQLServerOptions
   {
       Tool = { Enable = app.Environment.IsDevelopment() }
   });

app.MapFallbackToFile("index.html");

app.Run();

public partial class Program { }
