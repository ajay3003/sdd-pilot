using BirkNext.Api.Data;
using BirkNext.Api.GraphQL;
using BirkNext.Api.Middleware;
using BirkNext.Api.Services;
using HotChocolate.AspNetCore;
using Microsoft.EntityFrameworkCore;
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

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Default")));

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ScenarioService>();
builder.Services.AddScoped<ReviewedCandidateService>();
builder.Services.AddScoped<CandidateLinkService>();

builder.Services
    .AddGraphQLServer()
    .AddQueryType<Query>()
    .AddMutationType<Mutation>()
    .AddType<ScenarioObjectType>()
    .AddType<CreateScenarioResultType>()
    .AddType<ReviewedCandidateObjectType>()
    .AddType<CandidateLinkObjectType>()
    .AddDiagnosticEventListener<OperationDiagnosticEventListener>()
    .ConfigureSchema(b => b.ModifyOptions(o => o.UseXmlDocumentation = true));

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();
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
