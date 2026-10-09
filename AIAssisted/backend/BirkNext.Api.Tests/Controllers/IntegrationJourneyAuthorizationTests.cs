using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using BirkNext.Api.Services.ActiveEventTesting;
using BirkNext.Api.Services.Integrations;
using BirkNext.Api.Services.IntegrationJourneys;
using BirkNext.Api.Tests.Services.IntegrationJourneys;
using BirkNext.Api.Tests.Utilities;
using BirkNext.Integrations;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace BirkNext.Api.Tests.Controllers;

/// <summary>
/// Journey runs and history use the existing ActiveEventExecute permission (no second execution permission); readiness, journeys, rules and
/// the catalog stay readable. Message-flow configuration (read by journey readiness) can only be written with IntegrationConfiguration.Write.
/// Real host pipeline and JWT bearer handler with locally signed Entra-shaped tokens.
/// </summary>
public sealed class IntegrationJourneyAuthorizationTests
{
    private const string Issuer = "https://login.microsoftonline.com/00000000-0000-0000-0000-000000000001/v2.0";
    private const string Audience = "api://birknext-test";
    private static readonly SymmetricSecurityKey Key = new(System.Text.Encoding.UTF8.GetBytes("birknext-test-signing-key-0123456789-abcdef-0123456789"));

    private static WebApplicationFactory<Program> Host() => TestHostConfiguration.CreateDefaultHostWithEnginesDisabled()
        .WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                options.Authority = null;
                options.RequireHttpsMetadata = false;
                options.Configuration = new OpenIdConnectConfiguration();
                options.TokenValidationParameters = new TokenValidationParameters { ValidIssuer = Issuer, ValidAudience = Audience, IssuerSigningKey = Key, ValidateLifetime = true };
            });
            services.AddScoped<IIntegrationJourneyService, StubJourneys>();
            services.AddScoped<IIntegrationMessageFlowStore, IntegrationJourneyHarness.FakeMessageFlows>();
        }));

    private static string Token(string scp) => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
    {
        Issuer = Issuer, Audience = Audience, Expires = DateTime.UtcNow.AddMinutes(10), SigningCredentials = new SigningCredentials(Key, SecurityAlgorithms.HmacSha256),
        Claims = new Dictionary<string, object> { ["oid"] = "11111111-1111-1111-1111-111111111111", ["scp"] = scp, ["ver"] = "2.0" },
    });

    private static async Task<HttpStatusCode> Send(HttpClient client, HttpMethod method, string url, string? token, object? body = null)
    {
        using var request = new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body) };
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return (await client.SendAsync(request)).StatusCode;
    }

    [Fact]
    public async Task JourneyRunsAndHistory_RequireActiveEventExecute_ReadinessStaysOpen()
    {
        await using var factory = Host();
        using var client = factory.CreateClient();
        var start = new IntegrationJourneyRunRequest { EnvironmentId = "dev-env", PackId = "p", JourneyId = "j", ScenarioId = "s", Confirmed = true };

        (await Send(client, HttpMethod.Post, "/api/integration-journeys/runs", null, start)).Should().Be(HttpStatusCode.Unauthorized);
        (await Send(client, HttpMethod.Post, "/api/integration-journeys/runs", Token("User.Read"), start)).Should().Be(HttpStatusCode.Forbidden);
        (await Send(client, HttpMethod.Post, "/api/integration-journeys/runs", Token(BirkNextPermissions.IntegrationConfigurationWrite), start)).Should().Be(HttpStatusCode.Forbidden);
        (await Send(client, HttpMethod.Post, "/api/integration-journeys/runs", Token("User.Read ActiveEventExecute"), start)).Should().Be(HttpStatusCode.OK);
        (await Send(client, HttpMethod.Get, "/api/integration-journeys/runs?environmentId=dev-env", null)).Should().Be(HttpStatusCode.Unauthorized);
        (await Send(client, HttpMethod.Get, $"/api/integration-journeys/runs/{Guid.NewGuid()}", Token("User.Read"))).Should().Be(HttpStatusCode.Forbidden);
        (await Send(client, HttpMethod.Get, "/api/integration-journeys/packs?environmentId=dev-env", null)).Should().Be(HttpStatusCode.OK);
        (await Send(client, HttpMethod.Get, "/api/integration-journeys/packs/p/architecture-rules?environmentId=dev-env", null)).Should().Be(HttpStatusCode.OK);
        (await Send(client, HttpMethod.Get, "/api/integration-journeys/catalog", null)).Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task MessageFlowConfigurationWrites_RequireIntegrationConfigurationWrite_ReadsStayOpen()
    {
        await using var factory = Host();
        using var client = factory.CreateClient();
        var package = new IntegrationMessageFlowPackage(new MessageFlowDefinition(), new AltinnTestConfiguration(), new(false, 0, 0, [], []), [], []);

        (await Send(client, HttpMethod.Put, "/api/integration-message-flow/dev-env", null, package)).Should().Be(HttpStatusCode.Unauthorized);
        (await Send(client, HttpMethod.Put, "/api/integration-message-flow/dev-env", Token("ActiveEventExecute"), package)).Should().Be(HttpStatusCode.Forbidden);
        (await Send(client, HttpMethod.Get, "/api/integration-message-flow/dev-env", null)).Should().Be(HttpStatusCode.OK);
    }

    private sealed class StubJourneys : IIntegrationJourneyService
    {
        public Task<IReadOnlyList<IntegrationJourneyPackView>> PacksAsync(string environmentId, CancellationToken ct) => Task.FromResult<IReadOnlyList<IntegrationJourneyPackView>>([]);
        public Task<ArchitectureRuleReport> RulesAsync(string environmentId, string packId, Guid? snapshotId, CancellationToken ct) => Task.FromResult(new ArchitectureRuleReport(packId, null, null, [], []));
        public IReadOnlyList<IntegrationJourneyCatalogEntry> Catalog() => [];
        public Task<IntegrationJourneyRun> StartAsync(IntegrationJourneyRunRequest request, CancellationToken ct) => Task.FromResult(new IntegrationJourneyRun { OverallState = JourneyRunState.Blocked });
        public Task<IntegrationJourneyRun?> GetAsync(Guid runId, CancellationToken ct) => Task.FromResult<IntegrationJourneyRun?>(null);
        public Task<IReadOnlyList<IntegrationJourneyRunSummary>> HistoryAsync(IntegrationJourneyHistoryQuery query, CancellationToken ct) => Task.FromResult<IReadOnlyList<IntegrationJourneyRunSummary>>([]);
    }
}
