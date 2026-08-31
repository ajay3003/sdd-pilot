using BirkNext.Web;
using BirkNext.Web.GraphQL;
using BirkNext.Web.Services;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services
    .AddBirkNextClient()
    .ConfigureHttpClient(client =>
        client.BaseAddress = new Uri("http://localhost:5000/graphql"));

builder.Services.AddHttpClient<AdminApiService>(client =>
    client.BaseAddress = new Uri("http://localhost:5000/"));

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
builder.Services.AddScoped<IFrontendQualityReviewOrchestrator, FrontendQualityReviewOrchestrator>();
builder.Services.AddHttpClient<IQualityReviewPageModelService, QualityReviewPageModelService>(client =>
    client.BaseAddress = new Uri("http://localhost:5000/"));
builder.Services.AddHttpClient<IAnalysisPageModelService, AnalysisPageModelService>(client =>
    client.BaseAddress = new Uri("http://localhost:5000/"));
builder.Services.AddHttpClient<ILibraryPageModelService, LibraryPageModelService>(client =>
    client.BaseAddress = new Uri("http://localhost:5000/"));
builder.Services.AddHttpClient<ReviewPageModelService>(client =>
    client.BaseAddress = new Uri("http://localhost:5000/"));
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
builder.Services.AddHttpClient<IWorkspacePersistenceApiService, WorkspacePersistenceApiService>(client =>
    client.BaseAddress = new Uri("http://localhost:5000/"));
builder.Services.AddHttpClient<IRecommendedWorkflowApiService, RecommendedWorkflowApiService>(client =>
    client.BaseAddress = new Uri("http://localhost:5000/"));
builder.Services.AddScoped<IWorkflowReadinessService, WorkflowReadinessService>();
builder.Services.AddScoped<IWorkspaceAutoSaveService, WorkspaceAutoSaveService>();
builder.Services.AddScoped<RuntimeReviewSessionService>();
builder.Services.AddScoped<QualityReviewSessionService>();
builder.Services.AddScoped<ApplicationRuntimeResetService>();
builder.Services.AddScoped<TaskAlignmentSessionService>();

builder.Services.AddHttpClient<ImplementationTraceabilityApiService>(client =>
    client.BaseAddress = new Uri("http://localhost:5000/"));

builder.Services.AddHttpClient<WasmSecurityApiService>(client =>
    client.BaseAddress = new Uri("http://localhost:5000/"));

builder.Services.AddSingleton<IFrontendAnalysisSettingsService, FrontendAnalysisSettingsService>();
builder.Services.AddSingleton<ITargetEnvironmentService, TargetEnvironmentService>();
builder.Services.AddSingleton<ITargetEnvironmentHintExtractor, TargetEnvironmentHintExtractor>();
builder.Services.AddSingleton<IIntegrationTargetRegistryService, IntegrationTargetRegistryService>();
builder.Services.AddScoped<IAuthenticatedBrowserSessionService, AuthenticatedBrowserSessionService>();
builder.Services.AddScoped<IFrontendAnalysisContextFactory, FrontendAnalysisContextFactory>();
builder.Services.AddHttpClient<ITargetPreflightService, TargetPreflightService>(client =>
    client.BaseAddress = new Uri("http://localhost:5000/"));

// Target environment detection — uses backend API
builder.Services.AddHttpClient<ITargetEnvironmentDetectionApiService, TargetEnvironmentDetectionApiService>(client =>
    client.BaseAddress = new Uri("http://localhost:5000/"));

builder.Services.AddHttpClient<IBlazorWasmPerformanceReviewService, BlazorWasmPerformanceReviewService>(client =>
    client.BaseAddress = new Uri("http://localhost:5000/"));

builder.Services.AddHttpClient<ProjectDocumentApiService>(client =>
    client.BaseAddress = new Uri("http://localhost:5000/"));

builder.Services.AddHttpClient<SampleProjectsApiService>(client =>
    client.BaseAddress = new Uri("http://localhost:5000/"));

builder.Services.AddSingleton<ISampleProjectDocumentResolver>(sp =>
    new SampleProjectDocumentResolver(
        sp.GetRequiredService<SampleProjectsApiService>(),
        sp.GetRequiredService<IWorkspaceSessionService>()));

builder.Services.AddHttpClient<IApiQualityReviewService, ApiQualityReviewService>(client =>
    client.BaseAddress = new Uri("http://localhost:5000/"));

builder.Services.AddHttpClient<IIntegrationQualityReviewService, IntegrationQualityReviewService>(client =>
    client.BaseAddress = new Uri("http://localhost:5000/"));

builder.Services.AddHttpClient<IFrontendBrowserRuntimeReviewApiService, FrontendBrowserRuntimeReviewApiService>(client =>
    client.BaseAddress = new Uri("http://localhost:5000/"));
builder.Services.AddHttpClient<IFrontendAccessibilityReviewApiService, FrontendAccessibilityReviewApiService>(client =>
    client.BaseAddress = new Uri("http://localhost:5000/"));
builder.Services.AddHttpClient<IFrontendLighthouseReviewApiService, FrontendLighthouseReviewApiService>(client =>
    client.BaseAddress = new Uri("http://localhost:5000/"));
builder.Services.AddHttpClient<IFrontendPassiveSecurityApiService, FrontendPassiveSecurityApiService>(client =>
    client.BaseAddress = new Uri("http://localhost:5000/"));
builder.Services.AddFrontendQualityEngineStatusApi(new Uri("http://localhost:5000/"));

await builder.Build().RunAsync();
