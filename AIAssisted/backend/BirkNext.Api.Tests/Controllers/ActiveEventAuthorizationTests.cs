using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using BirkNext.Api.Services.ActiveEventTesting;
using BirkNext.Api.Services.Integrations;
using BirkNext.Api.Tests.Services.ActiveEventTesting;
using BirkNext.Api.Tests.Utilities;
using BirkNext.Integrations;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace BirkNext.Api.Tests.Controllers;

/// <summary>
/// The real host pipeline with the real JWT bearer handler (Program's options, including disabled inbound claim mapping) validating
/// locally-signed tokens shaped like Entra v2 access tokens: delegated <c>scp</c> (space-separated) and app <c>roles</c> (array).
/// </summary>
public sealed class ActiveEventAuthorizationTests
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
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = Issuer, ValidAudience = Audience, IssuerSigningKey = Key, ValidateLifetime = true,
                };
            });
            services.AddScoped<IActiveEventLifecycleService, StubLifecycle>();
            services.AddScoped<IIntegrationCatalogService, ActiveEventTestHarness.FakeCatalog>();
        }));

    private static string Token(string? scp = null, string[]? roles = null, SigningCredentials? signing = null)
    {
        var claims = new Dictionary<string, object> { ["oid"] = "11111111-1111-1111-1111-111111111111", ["tid"] = "00000000-0000-0000-0000-000000000001", ["ver"] = "2.0" };
        if (scp is not null) claims["scp"] = scp;
        if (roles is not null) claims["roles"] = roles;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer, Audience = Audience, Claims = claims, Expires = DateTime.UtcNow.AddMinutes(10),
            SigningCredentials = signing ?? new SigningCredentials(Key, SecurityAlgorithms.HmacSha256),
        });
    }

    private static async Task<HttpStatusCode> Get(HttpClient client, string url, string? token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return (await client.SendAsync(request)).StatusCode;
    }

    [Fact]
    public async Task ActiveEventEndpoints_RequireAnAuthenticatedPrincipalWithActiveEventExecute()
    {
        await using var factory = Host();
        using var client = factory.CreateClient();
        const string url = "/api/active-events/runs?environmentId=dev-env";

        (await Get(client, url, null)).Should().Be(HttpStatusCode.Unauthorized);
        (await Get(client, url, Token(scp: "User.Read"))).Should().Be(HttpStatusCode.Forbidden);
        (await Get(client, url, Token(scp: BirkNextPermissions.IntegrationConfigurationWrite))).Should().Be(HttpStatusCode.Forbidden, "configuration rights do not imply execution");
        (await Get(client, url, Token(scp: "User.Read ActiveEventExecute"))).Should().Be(HttpStatusCode.OK, "a realistic space-separated delegated scp claim");
        (await Get(client, url, Token(roles: ["Reader", "ActiveEventExecute"]))).Should().Be(HttpStatusCode.OK, "an app role array");
        var otherKey = new SigningCredentials(new SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes("another-key-0123456789-abcdef-0123456789-abcdefgh")), SecurityAlgorithms.HmacSha256);
        (await Get(client, url, Token(scp: "ActiveEventExecute", signing: otherKey))).Should().Be(HttpStatusCode.Unauthorized);
        (await Get(client, url, Token(scp: "ActiveEventExecuteX"))).Should().Be(HttpStatusCode.Forbidden, "scope matching is exact");
        using var start = new HttpRequestMessage(HttpMethod.Post, "/api/active-events/runs") { Content = JsonContent.Create(new ActiveEventRunRequest()) };
        (await client.SendAsync(start)).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "anonymous start");
        (await Get(client, $"/api/active-events/runs/{Guid.NewGuid()}", Token(scp: "User.Read"))).Should().Be(HttpStatusCode.Forbidden);
        (await Get(client, "/api/active-events/providers?environmentId=dev-env", null)).Should().Be(HttpStatusCode.OK, "the catalog and readiness explain why execution is unavailable");
        (await Get(client, "/api/active-events/environments/dev-env/trust", null)).Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task IntegrationCatalogWrites_RequireIntegrationConfigurationWrite_ReadsStayOpen()
    {
        await using var factory = Host();
        using var client = factory.CreateClient();
        async Task<HttpStatusCode> Create(string? token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/integrations?environmentId=dev-env")
            {
                Content = JsonContent.Create(new IntegrationDefinition { Id = "x", DisplayName = "x", Kind = IntegrationKind.EventHub }),
            };
            if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return (await client.SendAsync(request)).StatusCode;
        }

        (await Create(null)).Should().Be(HttpStatusCode.Unauthorized);
        (await Create(Token(scp: "ActiveEventExecute"))).Should().Be(HttpStatusCode.Forbidden, "execution rights do not imply configuration rights");
        (await Create(Token(scp: "User.Read IntegrationConfiguration.Write"))).Should().Be(HttpStatusCode.OK);
        (await Create(Token(roles: ["IntegrationConfiguration.Write"]))).Should().Be(HttpStatusCode.OK);
        using var delete = new HttpRequestMessage(HttpMethod.Delete, "/api/integrations/x?environmentId=dev-env");
        (await client.SendAsync(delete)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Get(client, "/api/integrations?environmentId=dev-env", null)).Should().Be(HttpStatusCode.OK, "catalog reads are configuration metadata and stay open");
    }

    [Theory]
    [InlineData("Development", "true", "127.0.0.1", true)]
    [InlineData("Development", "true", "::1", true)]
    [InlineData("Development", "true", "10.1.2.3", false)]
    [InlineData("Development", "false", "127.0.0.1", false)]
    [InlineData("Test", "true", "127.0.0.1", false)]
    [InlineData("Production", "true", "127.0.0.1", false)]
    public async Task LocalConfigurationWrites_AreDevelopmentLoopbackOnly_AndNeverAHeader(string environment, string setting, string remote, bool expected)
    {
        var host = new StubHostEnvironment { EnvironmentName = environment };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Authorization:LocalConfigurationWrites"] = setting }).Build();
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(remote);
        context.Connection.LocalIpAddress = IPAddress.Loopback;
        context.Request.Headers["X-Test-Permission"] = BirkNextPermissions.IntegrationConfigurationWrite;
        var handler = new IntegrationConfigurationWriteHandler(host, configuration, new HttpContextAccessor { HttpContext = context });
        var requirement = new IntegrationConfigurationWriteRequirement();
        var authorization = new AuthorizationHandlerContext([requirement], new ClaimsPrincipal(new ClaimsIdentity()), null);
        await handler.HandleAsync(authorization);
        authorization.HasSucceeded.Should().Be(expected);
    }

    private sealed class StubHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "BirkNext.Api";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    private sealed class StubLifecycle : IActiveEventLifecycleService
    {
        public ActiveEventEnvironmentTrust EnvironmentTrust(string environmentId) => new() { EnvironmentId = environmentId, Detail = "stub" };
        public Task<IReadOnlyList<ActiveEventProviderSummary>> ProvidersAsync(string environmentId, CancellationToken ct) => Task.FromResult<IReadOnlyList<ActiveEventProviderSummary>>([]);
        public Task<IReadOnlyList<ActiveEventScenarioDescriptor>> ScenariosAsync(string environmentId, string integrationId, CancellationToken ct) => Task.FromResult<IReadOnlyList<ActiveEventScenarioDescriptor>>([]);
        public Task<ActiveEventReadiness> ReadinessAsync(string environmentId, string integrationId, string extensionId, string scenarioId, Guid? snapshotId, CancellationToken ct) => throw new NotSupportedException();
        public Task<ActiveEventRunResult> StartAsync(ActiveEventRunRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<ActiveEventRunResult?> GetAsync(Guid runId, CancellationToken ct) => Task.FromResult<ActiveEventRunResult?>(null);
        public Task<IReadOnlyList<ActiveEventRunSummary>> HistoryAsync(ActiveEventHistoryQuery query, CancellationToken ct) => Task.FromResult<IReadOnlyList<ActiveEventRunSummary>>([]);
        public bool Cancel(Guid runId) => false;
    }
}
