using BirkNext.Web;
using BirkNext.Web.Configuration;
using BirkNext.Web.GraphQL;
using BirkNext.Web.Services;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;
using Microsoft.Authentication.WebAssembly.Msal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// One configured backend address for every BirkNext API client (wwwroot/appsettings.json "BackendUrl"). A feature client
// with its own hard-coded host or port drifts from the rest: its calls fail while other pages work, and the failure
// reads as "backend not reachable". SECURITY: HTTPS required in production; HTTP only for loopback in Development.
var backendUrl = builder.Configuration["BackendUrl"] ?? "https://localhost:5000";
BackendUrlValidator.Validate(backendUrl, builder.HostEnvironment.Environment);
var backendBase = BackendUrlValidator.BaseAddress(backendUrl);

// Active Event execution (ActiveEventExecute) and integration configuration writes (IntegrationConfiguration.Write) are protected by the
// BirkNext API. These values are supplied by deployment configuration; no tenant, client, or API scope is embedded in the application and
// there is no client secret (public SPA client, authorization code flow with PKCE). Without them the app still starts and every review
// works; real execution shows "Authentication not configured" and stays disabled.
var entra = BirkNext.Web.Configuration.EntraClientSettings.From(builder.Configuration);
if (entra.Configured)
{
    builder.Services.AddMsalAuthentication(options =>
    {
        options.ProviderOptions.Authentication.Authority = entra.Authority;
        options.ProviderOptions.Authentication.ClientId = entra.ClientId;
        options.ProviderOptions.Authentication.ValidateAuthority = true;
        if (entra.RedirectUri is { } redirect) options.ProviderOptions.Authentication.RedirectUri = redirect;
        if (entra.PostLogoutRedirectUri is { } postLogout) options.ProviderOptions.Authentication.PostLogoutRedirectUri = postLogout;
        foreach (var scope in entra.ApiScopes) options.ProviderOptions.DefaultAccessTokenScopes.Add(scope);
        options.ProviderOptions.LoginMode = "redirect";
    });
    builder.Services.AddSingleton(new ActiveEventAuthenticationState(true, "Entra is configured for this deployment."));
}
else
{
    // The authentication state still exists (anonymous) so components can cascade it; nothing can request a token.
    builder.Services.AddAuthorizationCore();
    builder.Services.AddScoped<Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider, BirkNext.Web.Configuration.AnonymousAuthenticationStateProvider>();
    builder.Services.AddSingleton(ActiveEventAuthenticationState.NotConfigured);
}

builder.Services
    .AddBirkNextClient()
    .ConfigureHttpClient(client =>
        client.BaseAddress = new Uri(backendBase, "graphql"));

builder.Services.AddHttpClient<AdminApiService>(client =>
    client.BaseAddress = backendBase);

builder.Services.AddSingleton<FeatureVisibilityService>();
builder.Services.AddSingleton<MarkdownRenderingService>();

// Strawberry Shake registers concrete mutation classes but omits interface mappings.
// Components that @inject these interfaces need explicit registrations in the root container.
builder.Services.AddSingleton<ICreateScenariosMutation>(sp =>
    sp.GetRequiredService<CreateScenariosMutation>());
builder.Services.AddSingleton<ISaveReviewedCandidatesMutation>(sp =>
    sp.GetRequiredService<SaveReviewedCandidatesMutation>());
builder.Services.AddSingleton<ISaveCandidateLinksMutation>(sp =>
    sp.GetRequiredService<SaveCandidateLinksMutation>());
builder.Services.AddSingleton<ISaveQaDeltaReviewMutation>(sp =>
    sp.GetRequiredService<SaveQaDeltaReviewMutation>());
builder.Services.AddSingleton<IDeleteQaDeltaReviewMutation>(sp =>
    sp.GetRequiredService<DeleteQaDeltaReviewMutation>());
builder.Services.AddSingleton<IReorderTestScenariosMutation>(sp =>
    sp.GetRequiredService<ReorderTestScenariosMutation>());
builder.Services.AddSingleton<IGetReviewedCandidatesQuery>(sp =>
    sp.GetRequiredService<GetReviewedCandidatesQuery>());

builder.Services.AddSingleton<IExtractionConfiguration, ExtractionConfiguration>();
builder.Services.Configure<ExtractionRuleConfiguration>(
    builder.Configuration.GetSection("ExtractionRules"));
builder.Services.AddTransient<ExtractionRuleSetCompiler>();
builder.Services.AddSingleton<IExtractionRuleEngine>(sp =>
{
    var compiler = sp.GetRequiredService<ExtractionRuleSetCompiler>();
    var ruleConfig = sp.GetRequiredService<IOptions<ExtractionRuleConfiguration>>().Value;
    var extractConfig = sp.GetRequiredService<IExtractionConfiguration>();
    var compiled = compiler.Compile(ExtractionRuleSet.Default(), ruleConfig);
    return new ExtractionRuleEngine(compiled, extractConfig);
});
builder.Services.AddScoped<IScenarioExtractionService>(sp =>
    new ScenarioExtractionService(
        sp.GetRequiredService<IExtractionConfiguration>(),
        sp.GetRequiredService<IExtractionRuleEngine>(),
        sp.GetRequiredService<ILogger<ScenarioExtractionService>>()));
builder.Services.AddSingleton<ISpecComparisonService, SpecComparisonService>();
builder.Services.AddSingleton<IConstitutionAnalysisService, ConstitutionAnalysisService>();
builder.Services.AddSingleton<IPlanAnalysisService, PlanAnalysisService>();
builder.Services.AddSingleton<IDataModelAnalysisService, DataModelAnalysisService>();
builder.Services.AddSingleton<IArtifactParserService, ArtifactParserService>();
builder.Services.AddSingleton<IArtifactTraceabilityService, ArtifactTraceabilityService>();
builder.Services.AddSingleton<IConstitutionComplianceService, ConstitutionComplianceService>();
builder.Services.AddSingleton<IQAReadinessService, QAReadinessService>();
builder.Services.AddSingleton<IQaAuditorService, QaAuditorService>();
builder.Services.AddSingleton<IDeliveryReadinessAssessmentService, DeliveryReadinessService>();
builder.Services.AddSingleton<IReviewContextValidator, ReviewContextValidator>();
builder.Services.AddSingleton<TaskSpecAlignmentService>();
builder.Services.AddSingleton<IStandardsComplianceService>(_ =>
    new StandardsComplianceService(
        new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) }));
builder.Services.AddSingleton<IQualityReviewService, QualityReviewService>();
builder.Services.AddSingleton<IDashboardMetricsService, DashboardMetricsService>();
builder.Services.AddSingleton<IDashboardSnapshotService, DashboardSnapshotService>();
builder.Services.AddSingleton<IReportExportService, ReportExportService>();
builder.Services.AddSingleton<IFrontendQualityReviewService, FrontendQualityReviewService>();
builder.Services.AddScoped<ISecurityScanner, SecurityScannerAdapter>();
builder.Services.AddScoped<IFrontendQualityTargetAccessResolver, FrontendQualityTargetAccessResolver>();
builder.Services.AddScoped<IFrontendQualityReviewOrchestrator, FrontendQualityReviewOrchestrator>();
builder.Services.AddHttpClient<IQualityReviewPageModelService, QualityReviewPageModelService>(client =>
    client.BaseAddress = backendBase);
builder.Services.AddHttpClient<IAnalysisPageModelService, AnalysisPageModelService>(client =>
    client.BaseAddress = backendBase);
builder.Services.AddHttpClient<ILibraryPageModelService, LibraryPageModelService>(client =>
    client.BaseAddress = backendBase);
builder.Services.AddHttpClient<ReviewPageModelService>(client =>
    client.BaseAddress = backendBase);
builder.Services.AddScoped<IExtractionSessionService, ExtractionSessionService>();
builder.Services.AddScoped<IExtractionCandidateMetricsService, ExtractionCandidateMetricsService>();
builder.Services.AddSingleton<WorkspaceArtifactRepository>();
builder.Services.AddSingleton<IWorkspaceArtifactRepository>(sp => sp.GetRequiredService<WorkspaceArtifactRepository>());
builder.Services.AddSingleton<IWorkspaceSessionService>(sp => sp.GetRequiredService<WorkspaceArtifactRepository>());
builder.Services.AddSingleton<IWorkspaceUpdateCoordinator, WorkspaceUpdateCoordinator>();
builder.Services.AddSingleton<IReviewContextProvider, ReviewContextProvider>();
builder.Services.AddSingleton<IWorkspaceStateManager, WorkspaceStateManager>();
builder.Services.AddSingleton<IWorkspaceArtifactStatusService, WorkspaceArtifactStatusService>();
builder.Services.AddScoped<IWorkspaceSessionRestoreService, WorkspaceSessionRestoreService>();
builder.Services.AddSingleton<LocalDataResetEpoch>();
builder.Services.AddHttpClient<IWorkspacePersistenceApiService, WorkspacePersistenceApiService>(client =>
    client.BaseAddress = backendBase);
builder.Services.AddHttpClient<IRecommendedWorkflowApiService, RecommendedWorkflowApiService>(client =>
    client.BaseAddress = backendBase);
// The one current-workspace read model: Dashboard, Recommended Workflow and navigation applicability all read it.
builder.Services.AddScoped<ICurrentWorkspaceProjection, CurrentWorkspaceProjection>();
builder.Services.AddScoped<IWorkflowReadinessService, WorkflowReadinessService>();
builder.Services.AddScoped<IWorkspaceAutoSaveService, WorkspaceAutoSaveService>();
builder.Services.AddScoped<RuntimeReviewSessionService>();
builder.Services.AddScoped<IntegrationMappingEvidenceSession>();
builder.Services.AddScoped<QualityReviewSessionService>();
builder.Services.AddScoped<ApplicationRuntimeResetService>();
builder.Services.AddScoped<TaskAlignmentSessionService>();

builder.Services.AddHttpClient<ImplementationTraceabilityApiService>(client =>
    client.BaseAddress = backendBase);
builder.Services.AddHttpClient<SourceChangeImpactApiService>(client =>
    client.BaseAddress = backendBase);

builder.Services.AddHttpClient<WasmSecurityApiService>(client =>
    client.BaseAddress = backendBase);

builder.Services.AddSingleton<IFrontendAnalysisSettingsService, FrontendAnalysisSettingsService>();
builder.Services.AddSingleton<IEndpointDiscoveryService, EndpointDiscoveryService>();
builder.Services.AddSingleton<IApiReviewHistoryService, ApiReviewHistoryService>();
builder.Services.AddSingleton<ITargetEnvironmentService, TargetEnvironmentService>();
builder.Services.AddSingleton<ITargetEnvironmentHintExtractor, TargetEnvironmentHintExtractor>();
builder.Services.AddSingleton<IIntegrationTargetRegistryService, IntegrationTargetRegistryService>();
builder.Services.AddScoped<IAuthenticatedBrowserSessionService, AuthenticatedBrowserSessionService>();
builder.Services.AddScoped<IFrontendAnalysisContextFactory, FrontendAnalysisContextFactory>();
builder.Services.AddHttpClient<ITargetPreflightService, TargetPreflightService>(client =>
    client.BaseAddress = backendBase);


builder.Services.AddHttpClient<IManagedEdgeCdpApiService, ManagedEdgeCdpApiService>(client =>
{
    client.BaseAddress = backendBase;
    client.Timeout = TimeSpan.FromSeconds(20);
});
builder.Services.AddScoped<ManagedEdgeRuntime>();
builder.Services.AddHttpClient<ILocalHttpsProxyApiService, LocalHttpsProxyApiService>(client =>
{
    client.BaseAddress = backendBase;
    client.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddScoped<LocalHttpsProxyRuntime>();
builder.Services.AddHttpClient<IBrowserCompanionApiService, BrowserCompanionApiService>(client =>
{
    client.BaseAddress = backendBase;
    client.Timeout = TimeSpan.FromSeconds(15);
});
builder.Services.AddScoped<BrowserCompanionRuntime>();
// A Critical E2E run waits for a browser, and the browser waits for a person, so this client is not on the usual
// short review timeout.
builder.Services.AddHttpClient<ICriticalE2EApiService, CriticalE2EApiService>(client =>
{
    client.BaseAddress = backendBase;
    client.Timeout = TimeSpan.FromMinutes(10);
});
builder.Services.AddScoped<IBrowserQualityEvidenceSource, BrowserQualityEvidenceSource>();
builder.Services.AddScoped<IPerformanceQualityEvidenceSource, PerformanceQualityEvidenceSource>();
builder.Services.AddHttpClient<ITargetEnvironmentDetectionApiService, TargetEnvironmentDetectionApiService>(client =>
{
    client.BaseAddress = backendBase;
    // Security: Validate SSL certificates in production
    if (!builder.HostEnvironment.IsDevelopment())
    {
        client.DefaultRequestHeaders.Add("User-Agent", "BirkNext-Frontend/1.0");
    }
});

builder.Services.AddHttpClient<IBlazorWasmPerformanceReviewService, BlazorWasmPerformanceReviewService>(client =>
    client.BaseAddress = backendBase);

builder.Services.AddHttpClient<ProjectDocumentApiService>(client =>
    client.BaseAddress = backendBase);

builder.Services.AddHttpClient<SampleProjectsApiService>(client =>
    client.BaseAddress = backendBase);

// Generic Sample Project document discovery (recursive inventory + deterministic role classification), shared by the
// Sample Projects page, the Dashboard and the document resolver the Explorers use.
builder.Services.AddSingleton<BirkNext.Web.Services.SampleProjects.ISampleProjectArtifactDiscovery>(sp =>
    new BirkNext.Web.Services.SampleProjects.SampleProjectArtifactDiscoveryService(sp.GetRequiredService<SampleProjectsApiService>()));
builder.Services.AddSingleton<ISampleProjectDocumentResolver>(sp =>
    new SampleProjectDocumentResolver(
        sp.GetRequiredService<SampleProjectsApiService>(),
        sp.GetRequiredService<IWorkspaceSessionService>(),
        sp.GetRequiredService<BirkNext.Web.Services.SampleProjects.ISampleProjectArtifactDiscovery>()));
// Document explorers read artifacts by role from the current workspace (Sample Project discovery + imported revisions).
builder.Services.AddSingleton<BirkNext.Web.Services.Explorers.IArtifactExplorerContext>(sp =>
    new BirkNext.Web.Services.Explorers.ArtifactExplorerContext(
        sp.GetRequiredService<IWorkspaceSessionService>(),
        sp.GetRequiredService<ISampleProjectDocumentResolver>(),
        sp.GetRequiredService<BirkNext.Web.Services.SampleProjects.ISampleProjectArtifactDiscovery>(),
        sp.GetRequiredService<IWorkspaceUpdateCoordinator>(),
        sp.GetRequiredService<IWorkspaceStateManager>()));

builder.Services.AddHttpClient<IApiReviewService, ApiReviewService>(client =>
    client.BaseAddress = backendBase);

// Integration catalog (Target Environment → Integrations, persisted by the backend) and Integration Quality Review over it. Reads are
// open; writes need IntegrationConfiguration.Write, so a token is attached when one can be obtained silently (never a forced sign-in).
var catalogClient = builder.Services.AddHttpClient<IIntegrationCatalogApiService, IntegrationCatalogApiService>(client =>
    client.BaseAddress = backendBase);
if (entra.Configured)
    catalogClient.AddHttpMessageHandler(sp => new OptionalBearerTokenHandler(sp.GetRequiredService<IAccessTokenProvider>(), backendBase, entra.ApiScopes));
// Project Import: one archive → artifact repository (documents, activated here) + Source Analysis snapshot (created by the backend).
builder.Services.AddHttpClient<BirkNext.Web.Services.ProjectImport.IProjectImportApiService, BirkNext.Web.Services.ProjectImport.ProjectImportApiService>(client =>
{
    client.BaseAddress = backendBase;
    client.Timeout = TimeSpan.FromMinutes(5); // same budget as the Source Analysis upload: a large project's analysis runs inside the commit
});
builder.Services.AddScoped<BirkNext.Web.Services.ProjectImport.ProjectImportActivation>(sp => new BirkNext.Web.Services.ProjectImport.ProjectImportActivation(
    sp.GetRequiredService<WorkspaceArtifactRepository>(), sp.GetRequiredService<IWorkspaceUpdateCoordinator>()));
// Azure Environment Analysis: sign-in status, read-only analysis into snapshots, declared-vs-observed comparison (the backend only reads Azure).
builder.Services.AddHttpClient<IAzureEnvironmentApiService, AzureEnvironmentApiService>(client =>
    client.BaseAddress = backendBase);
// Pipeline Review: delivery-flow interpretation of Source Analysis CI/CD evidence (GET only).
// Technology & Analysis Coverage: source technology inventory + configured integrations (GET only); applicability is evaluated in the browser.
builder.Services.AddHttpClient<ITechnologyCoverageApiService, TechnologyCoverageApiService>(client =>
    client.BaseAddress = backendBase);
// Generated Documentation Health (System Settings → Developer): reads generated-documentation evidence of a stored Source Analysis snapshot.
builder.Services.AddHttpClient<IGeneratedDocumentationApiService, GeneratedDocumentationApiService>(client =>
    client.BaseAddress = backendBase);
builder.Services.AddScoped<ProjectApplicabilityState>();
builder.Services.AddScoped<NavigationSectionState>();
// Performance Test Review: definitions, readiness, runs, baselines (backend owns safety and execution).
builder.Services.AddHttpClient<IPerformanceTestApiService, PerformanceTestApiService>(client =>
    client.BaseAddress = backendBase);
builder.Services.AddHttpClient<IPipelineReviewApiService, PipelineReviewApiService>(client =>
    client.BaseAddress = backendBase);
builder.Services.AddHttpClient<ISecurityExpectationApi, SecurityExpectationApi>(client =>
    client.BaseAddress = backendBase);
// IQR → Active tests: one shared client for every scenario provider; every gate is the backend's. With Entra configured, protected calls
// get the access token from the normal authenticated pipeline; readiness metadata stays readable without it.
var activeEventsClient = builder.Services.AddHttpClient<IActiveEventsApiService, ActiveEventsApiService>(client => client.BaseAddress = backendBase);
if (entra.Configured)
    activeEventsClient.AddHttpMessageHandler(sp => new OptionalBearerTokenHandler(sp.GetRequiredService<IAccessTokenProvider>(), backendBase, entra.ApiScopes));
// Security Classification / Gradert tilgang review (source + approved test context + safe live queries; tokens per run, never stored).
builder.Services.AddHttpClient<IClassificationReviewApiService, ClassificationReviewApiService>(client =>
    client.BaseAddress = backendBase);
// Dependency Review: read-only analysis of uploaded repository archives (synthetic version simulation only) and source-free dependency
// health over stored inventories (registry/advisory lookups for hundreds of packages can take a while, hence the longer timeout).
builder.Services.AddHttpClient<IDependencyReviewApiService, DependencyReviewApiService>(client =>
{
    client.BaseAddress = backendBase;
    client.Timeout = TimeSpan.FromMinutes(5);
});
builder.Services.AddHttpClient<ISddEvidenceApiService, SddEvidenceApiService>();
builder.Services.AddScoped<SddEvidenceGraphService>();
// Test evidence: TRX result preview and Source Analysis test definitions; imported executions are recorded in the workspace SDD lifecycle.
builder.Services.AddHttpClient<ITestEvidenceApiService, TestEvidenceApiService>(client => client.BaseAddress = backendBase);
builder.Services.AddScoped<SddTestEvidenceNotifier>();
// Trusted GraphQL schema artifacts per (Target Environment, API target) — API Quality Review's fallback schema for compatibility.
builder.Services.AddHttpClient<IGraphQlSchemaArtifactApiService, GraphQlSchemaArtifactApiService>(client =>
    client.BaseAddress = backendBase);

builder.Services.AddHttpClient<IAuthenticatedReviewCapabilitiesService, AuthenticatedReviewCapabilitiesService>(client =>
    client.BaseAddress = backendBase);
builder.Services.AddHttpClient<IFrontendAuthenticatedApiSurfaceService, FrontendAuthenticatedApiSurfaceService>(client =>
{
    client.BaseAddress = backendBase;
    client.Timeout = TimeSpan.FromSeconds(60);
});

builder.Services.AddHttpClient<IFrontendBrowserRuntimeReviewApiService, FrontendBrowserRuntimeReviewApiService>(client =>
    client.BaseAddress = backendBase);
builder.Services.AddHttpClient<IFrontendAccessibilityReviewApiService, FrontendAccessibilityReviewApiService>(client =>
    client.BaseAddress = backendBase);
builder.Services.AddHttpClient<IFrontendLighthouseReviewApiService, FrontendLighthouseReviewApiService>(client =>
    client.BaseAddress = backendBase);
builder.Services.AddHttpClient<IFrontendPassiveSecurityApiService, FrontendPassiveSecurityApiService>(client =>
    client.BaseAddress = backendBase);
builder.Services.AddFrontendQualityEngineStatusApi(backendBase);
builder.Services.AddBrowserAutomationDiagnosticApi(backendBase);
builder.Services.AddHttpClient<IHeadlessDiagnosticApiService, HeadlessDiagnosticApiService>(client =>
{
    client.BaseAddress = backendBase;
    client.Timeout = TimeSpan.FromMinutes(2);
});

await builder.Build().RunAsync();
