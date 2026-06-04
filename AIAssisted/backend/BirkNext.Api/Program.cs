using BirkNext.Api.Data;
using BirkNext.Api.Configuration;
using BirkNext.Api.GraphQL;
using BirkNext.Api.Middleware;
using BirkNext.Api.Services;
using HotChocolate.AspNetCore;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Serilog;
using Serilog.Formatting.Json;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((ctx, lc) => lc
    .Enrich.FromLogContext()
    .WriteTo.Console(new JsonFormatter()));

var frontendOrigin = builder.Configuration["FRONTEND_ORIGIN"] ?? "http://localhost:5173";

builder.Services.AddCors(options =>
    options.AddPolicy("Frontend", policy => policy
        .WithOrigins(frontendOrigin)
        .AllowAnyHeader()
        .AllowAnyMethod()));

var databaseConnectionString = DatabaseConnection.GetConnectionString(builder.Configuration);

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(databaseConnectionString));

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ScenarioService>();
builder.Services.AddScoped<ReviewedCandidateService>();
builder.Services.AddScoped<CandidateLinkService>();
builder.Services.AddScoped<QaDeltaReviewService>();
builder.Services.AddScoped<TraceLinkService>();
builder.Services.AddScoped<ImpactAnalysisService>();
builder.Services.AddScoped<AIChangeAuditService>();
builder.Services.AddScoped<SpecDriftDetectionService>();
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

app.UseCors("Frontend");
app.UseMiddleware<CorrelationIdMiddleware>();

app.MapGraphQL()
   .WithOptions(new GraphQLServerOptions
   {
       Tool = { Enable = app.Environment.IsDevelopment() }
   });

app.Run();

public partial class Program { }
