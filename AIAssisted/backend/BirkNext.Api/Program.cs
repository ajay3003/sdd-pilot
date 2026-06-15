using BirkNext.Api.Data;
using BirkNext.Api.Configuration;
using BirkNext.Api.GraphQL;
using BirkNext.Api.Middleware;
using BirkNext.Api.Services;
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
builder.Services.AddScoped<ScenarioService>();
builder.Services.AddScoped<ReviewedCandidateService>();
builder.Services.AddScoped<CandidateLinkService>();
builder.Services.AddScoped<QaDeltaReviewService>();
builder.Services.AddScoped<TraceLinkService>();
builder.Services.AddScoped<TraceabilitySuggestionService>();
builder.Services.AddScoped<ImpactAnalysisService>();
builder.Services.AddScoped<AIChangeAuditService>();
builder.Services.AddScoped<SpecDriftDetectionService>();
builder.Services.AddScoped<CodeTraceabilityService>();
builder.Services.AddScoped<AIQaAuditorService>();
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
