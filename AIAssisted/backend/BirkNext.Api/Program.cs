using BirkNext.Api.Data;
using BirkNext.Api.Middleware;
using HotChocolate.AspNetCore;
using HotChocolate.Types;
using Microsoft.EntityFrameworkCore;
using Serilog;
using Serilog.Formatting.Compact;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((ctx, lc) => lc
    .Enrich.FromLogContext()
    .WriteTo.Console(new CompactJsonFormatter()));

var frontendOrigin = builder.Configuration["FRONTEND_ORIGIN"] ?? "http://localhost:5173";

builder.Services.AddCors(options =>
    options.AddPolicy("Frontend", policy => policy
        .WithOrigins(frontendOrigin)
        .AllowAnyHeader()
        .AllowAnyMethod()));

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Default")));

builder.Services
    .AddGraphQLServer()
    .AddQueryType(d => d.Name("Query").Field("ping").Type<StringType>().Resolve("pong"));

var app = builder.Build();

app.UseCors("Frontend");
app.UseMiddleware<CorrelationIdMiddleware>();

app.MapGraphQL()
   .WithOptions(new GraphQLServerOptions
   {
       Tool = { Enable = app.Environment.IsDevelopment() }
   });

app.Run();
