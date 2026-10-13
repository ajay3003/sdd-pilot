using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using BirkNext.Api.Services.ActiveEventTesting;
using BirkNext.Api.Services.Integrations;
using BirkNext.Api.Services.IntegrationJourneys;
using BirkNext.Api.Tests.Services.IntegrationJourneys;
using BirkNext.Api.Tests.Utilities;
using BirkNext.Integrations;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
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
/// ActiveEventExecute as a capability: an authenticated principal with the permission, OR the explicitly enabled local trusted execution of a
/// loopback-only Development backend (backend setting + Development + loopback peer + loopback Host + exact configured frontend Origin). Every
/// other combination is refused. The rule-level tests drive <see cref="LocalTrustedExecution"/> and the real permission handler directly;
/// the pipeline tests use the real host (JWT handler, policy, controllers) with the socket peer set by a startup filter.
/// </summary>
public sealed class LocalTrustedExecutionTests
{
    private const string Frontend = "http://localhost:5173";
    private const string Issuer = "https://login.microsoftonline.com/00000000-0000-0000-0000-000000000001/v2.0";
    private const string Audience = "api://birknext-test";
    private static readonly SymmetricSecurityKey Key = new(System.Text.Encoding.UTF8.GetBytes("birknext-test-signing-key-0123456789-abcdef-0123456789"));

    // ── Rules ──────────────────────────────────────────────────────────────────────────────────────────────────────────

    private static LocalTrustedExecution Local(string environment = "Development", string? setting = "true", string? frontend = Frontend) =>
        new(new StubHostEnvironment { EnvironmentName = environment },
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [LocalTrustedExecution.Setting] = setting, ["FRONTEND_ORIGIN"] = frontend }).Build());

    private static DefaultHttpContext Request(string? remote = "127.0.0.1", string? local = "127.0.0.1", string host = "localhost:5000", string? origin = Frontend,
        string? authorization = null, string? forwardedFor = null)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = remote is null ? null : IPAddress.Parse(remote);
        context.Connection.LocalIpAddress = local is null ? null : IPAddress.Parse(local);
        context.Request.Host = new HostString(host);
        if (origin is not null) context.Request.Headers.Origin = origin;
        if (authorization is not null) context.Request.Headers.Authorization = authorization;
        if (forwardedFor is not null) context.Request.Headers["X-Forwarded-For"] = forwardedFor;
        return context;
    }

    [Fact]
    public void AllConditionsTogether_GrantLocalTrustedExecution()
    {
        Local().Evaluate(Request()).Allowed.Should().BeTrue();
        Local().Evaluate(Request(remote: "::1", local: "::1", host: "[::1]:5000")).Allowed.Should().BeTrue();
        Local().Evaluate(Request(remote: "::ffff:127.0.0.1", host: "127.0.0.1:5000")).Allowed.Should().BeTrue();
        Local(frontend: "http://127.0.0.1:5173/").Evaluate(Request(origin: "http://127.0.0.1:5173")).Allowed.Should().BeTrue();
    }

    [Theory]
    [InlineData("no-grant")]
    [InlineData("grant-false")]
    [InlineData("grant-malformed")]
    [InlineData("production")]
    [InlineData("staging")]
    [InlineData("qa")]
    [InlineData("test")]
    [InlineData("remote-lan")]
    [InlineData("remote-public")]
    [InlineData("remote-missing")]
    [InlineData("local-lan")]
    [InlineData("host-lan")]
    [InlineData("host-suffix")]
    [InlineData("host-ip-suffix")]
    [InlineData("host-public")]
    [InlineData("origin-missing")]
    [InlineData("origin-null")]
    [InlineData("origin-wrong")]
    [InlineData("origin-wrong-port")]
    [InlineData("origin-wrong-scheme")]
    [InlineData("origin-suffix")]
    [InlineData("origin-file")]
    [InlineData("origin-wildcard")]
    [InlineData("origin-malformed")]
    [InlineData("origin-path")]
    [InlineData("origin-userinfo")]
    [InlineData("frontend-not-loopback")]
    [InlineData("frontend-malformed")]
    [InlineData("frontend-missing")]
    [InlineData("frontend-wildcard")]
    [InlineData("xff-spoof")]
    [InlineData("authorization-header-only")]
    public void AnyMissingCondition_DeniesLocalTrustedExecution(string problem)
    {
        var (local, request) = problem switch
        {
            "no-grant" => (Local(setting: null), Request()),
            "grant-false" => (Local(setting: "false"), Request()),
            "grant-malformed" => (Local(setting: "yes"), Request()),
            "production" => (Local("Production"), Request()),
            "staging" => (Local("Staging"), Request()),
            "qa" => (Local("QA"), Request()),
            "test" => (Local("Test"), Request()),
            "remote-lan" => (Local(), Request(remote: "192.168.1.20")),
            "remote-public" => (Local(), Request(remote: "203.0.113.7")),
            "remote-missing" => (Local(), Request(remote: null)),
            "local-lan" => (Local(), Request(local: "10.0.0.5")),
            "host-lan" => (Local(), Request(host: "192.168.1.10:5000")),
            "host-suffix" => (Local(), Request(host: "localhost.evil.example")),
            "host-ip-suffix" => (Local(), Request(host: "127.0.0.1.evil.example")),
            "host-public" => (Local(), Request(host: "birknext.example.com")),
            "origin-missing" => (Local(), Request(origin: null)),
            "origin-null" => (Local(), Request(origin: "null")),
            "origin-wrong" => (Local(), Request(origin: "https://evil.example")),
            "origin-wrong-port" => (Local(), Request(origin: "http://localhost:5174")),
            "origin-wrong-scheme" => (Local(), Request(origin: "https://localhost:5173")),
            "origin-suffix" => (Local(), Request(origin: "http://localhost.evil.example:5173")),
            "origin-file" => (Local(), Request(origin: "file://")),
            "origin-wildcard" => (Local(), Request(origin: "*")),
            "origin-malformed" => (Local(), Request(origin: "http//localhost:5173")),
            "origin-path" => (Local(), Request(origin: "http://localhost:5173/evil")),
            "origin-userinfo" => (Local(), Request(origin: "http://user@localhost:5173")),
            "frontend-not-loopback" => (Local(frontend: "http://birknext.example.com"), Request(origin: "http://birknext.example.com")),
            "frontend-malformed" => (Local(frontend: "localhost:5173"), Request(origin: "localhost:5173")),
            "frontend-missing" => (Local(frontend: null), Request()),
            "frontend-wildcard" => (Local(frontend: "*"), Request(origin: "*")),
            "xff-spoof" => (Local(), Request(remote: "192.168.1.20", forwardedFor: "127.0.0.1")),
            "authorization-header-only" => (Local(setting: null), Request(authorization: "LocalTrustedExecution")),
            _ => throw new ArgumentOutOfRangeException(nameof(problem)),
        };
        var decision = local.Evaluate(request);
        decision.Allowed.Should().BeFalse(problem);
        decision.Reason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task TheHandler_GrantsForPermissionOrLocalTrust_AndLocalTrustAddsNoClaims()
    {
        async Task<bool> Handle(ClaimsPrincipal user, LocalTrustedExecution local, HttpContext context)
        {
            var handler = new ActiveEventPermissionHandler(local, new HttpContextAccessor { HttpContext = context });
            var authorization = new AuthorizationHandlerContext([new ActiveEventPermissionRequirement()], user, null);
            await handler.HandleAsync(authorization);
            return authorization.HasSucceeded;
        }
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());
        var permitted = new ClaimsPrincipal(new ClaimsIdentity([new Claim("oid", "u1"), new Claim("scp", "User.Read ActiveEventExecute")], "Bearer"));
        var unpermitted = new ClaimsPrincipal(new ClaimsIdentity([new Claim("oid", "u2"), new Claim("scp", "User.Read")], "Bearer"));
        var remote = Request(remote: "192.168.1.20");

        (await Handle(anonymous, Local(), Request())).Should().BeTrue("local trusted execution");
        (await Handle(anonymous, Local(setting: null), Request())).Should().BeFalse("anonymous without local trust");
        (await Handle(anonymous, Local(), remote)).Should().BeFalse("not loopback");
        (await Handle(permitted, Local(setting: null), remote)).Should().BeTrue("authenticated with the permission, anywhere");
        (await Handle(unpermitted, Local(setting: null), remote)).Should().BeFalse("authenticated without the permission");
        BirkNextPermissions.Holds(anonymous, BirkNextPermissions.ActiveEventExecute).Should().BeFalse("the local grant never manufactures a permission claim");
    }

    [Fact]
    public void ExecutionActors_AreDerived_Distinct_AndCarryNoPersonalData()
    {
        var permitted = new ClaimsPrincipal(new ClaimsIdentity([new Claim("oid", "11111111-1111-1111-1111-111111111111"), new Claim("iss", Issuer),
            new Claim("preferred_username", "tester@example.test"), new Claim("scp", "ActiveEventExecute")], "Bearer"));
        var other = new ClaimsPrincipal(new ClaimsIdentity([new Claim("oid", "22222222-2222-2222-2222-222222222222"), new Claim("iss", Issuer), new Claim("scp", "ActiveEventExecute")], "Bearer"));
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());

        var authenticated = ExecutionActor.Resolve(permitted, localTrusted: true);
        authenticated.Should().MatchRegex("^authenticated-user:[0-9a-f]{16}$", "a permitted principal is never folded into the local session");
        authenticated.Should().NotContain("1111").And.NotContain("tester");
        ExecutionActor.Resolve(permitted, false).Should().Be(authenticated, "stable for the same principal");
        ExecutionActor.Resolve(other, false).Should().NotBe(authenticated);
        ExecutionActor.Resolve(anonymous, localTrusted: true).Should().Be("local-session:loopback");
        ExecutionActor.Resolve(anonymous, localTrusted: false).Should().BeNull();
        IntegrationJourneyService.SafeInitiator("tester@example.test").Should().BeNull("a raw identity is never persisted");
        IntegrationJourneyService.SafeInitiator("11111111-1111-1111-1111-111111111111").Should().BeNull();
    }

    // ── The real pipeline ──────────────────────────────────────────────────────────────────────────────────────────────

    private static WebApplicationFactory<Program> Host(string remote = "127.0.0.1", bool grant = true, string environment = "Development") =>
        TestHostConfiguration.CreateDefaultHostWithEnginesDisabled().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            builder.UseSetting(LocalTrustedExecution.Setting, grant ? "true" : "false");
            builder.UseSetting("FRONTEND_ORIGIN", Frontend);
            builder.ConfigureTestServices(services =>
            {
                services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                {
                    options.Authority = null;
                    options.RequireHttpsMetadata = false;
                    options.Configuration = new OpenIdConnectConfiguration();
                    options.TokenValidationParameters = new TokenValidationParameters { ValidIssuer = Issuer, ValidAudience = Audience, IssuerSigningKey = Key, ValidateLifetime = true };
                });
                services.AddSingleton<IStartupFilter>(new Peer(IPAddress.Parse(remote)));
                services.AddSingleton<RecordingJourneys>();
                services.AddScoped<IIntegrationJourneyService>(sp => sp.GetRequiredService<RecordingJourneys>());
                services.AddScoped<IIntegrationMessageFlowStore, IntegrationJourneyHarness.FakeMessageFlows>();
            });
        });

    private static string Token(string scp) => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
    {
        Issuer = Issuer, Audience = Audience, Expires = DateTime.UtcNow.AddMinutes(10), SigningCredentials = new SigningCredentials(Key, SecurityAlgorithms.HmacSha256),
        Claims = new Dictionary<string, object> { ["oid"] = "11111111-1111-1111-1111-111111111111", ["scp"] = scp, ["ver"] = "2.0", ["preferred_username"] = "tester@example.test" },
    });

    private static async Task<HttpResponseMessage> Send(HttpClient client, HttpMethod method, string url, string? origin = Frontend, string? token = null, object? body = null, string? host = null)
    {
        var request = new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body) };
        if (origin is not null) request.Headers.Add("Origin", origin);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (host is not null) request.Headers.Host = host;
        return await client.SendAsync(request);
    }

    private static readonly IntegrationJourneyRunRequest Start = new() { EnvironmentId = "dev-env", PackId = "skolenaervaer", JourneyId = "rapportmottak", ScenarioId = "valid-report", Confirmed = true,
        Inputs = new Dictionary<string, string> { ["reporter"] = "reporter-a", ["childIdentifier"] = "01821512345" } };

    [Fact]
    public async Task LocalTrustedExecution_RunsJourneys_WithoutEntra_AsTheLocalSession()
    {
        await using var factory = Host();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://localhost") });
        var journeys = factory.Services.GetRequiredService<RecordingJourneys>();

        (await Send(client, HttpMethod.Post, "/api/integration-journeys/runs", body: Start)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Send(client, HttpMethod.Get, "/api/integration-journeys/runs?environmentId=dev-env")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Send(client, HttpMethod.Get, "/api/active-events/runs?environmentId=dev-env")).StatusCode.Should().NotBe(HttpStatusCode.Unauthorized).And.NotBe(HttpStatusCode.Forbidden);
        journeys.Initiators.Should().Equal("local-session:loopback");
        journeys.Initiators.Should().NotContain(item => item!.Contains("reporter-a") || item.Contains("01821512345"), "neither the Altinn reporter nor the child is the BirkNext actor");

        (await Send(client, HttpMethod.Post, "/api/integration-journeys/runs", origin: null, body: Start)).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "no Origin");
        (await Send(client, HttpMethod.Post, "/api/integration-journeys/runs", origin: "https://evil.example", body: Start)).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "cross-site origin");
        (await Send(client, HttpMethod.Post, "/api/integration-journeys/runs", host: "localhost.evil.example", body: Start)).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "rebinding host");
        journeys.Initiators.Should().HaveCount(1, "refused requests never reach the service");
    }

    [Theory]
    [InlineData("192.168.1.20", true, "Development")]
    [InlineData("127.0.0.1", false, "Development")]
    [InlineData("127.0.0.1", true, "Production")]
    [InlineData("127.0.0.1", true, "Staging")]
    public async Task WithoutEveryCondition_AnonymousExecutionIsRefused(string remote, bool grant, string environment)
    {
        await using var factory = Host(remote, grant, environment);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://localhost") });

        var response = await Send(client, HttpMethod.Post, "/api/integration-journeys/runs", body: Start);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/integration-journeys/runs") { Content = JsonContent.Create(Start) };
        request.Headers.Add("Origin", Frontend);
        request.Headers.TryAddWithoutValidation("Authorization", "LocalTrustedExecution");
        request.Headers.Add("X-Forwarded-For", "127.0.0.1");
        (await client.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "no header grants execution");
        factory.Services.GetRequiredService<RecordingJourneys>().Initiators.Should().BeEmpty();
    }

    [Fact]
    public async Task TheAuthenticatedPath_IsUnchanged_AndKeptDistinctFromTheLocalSession()
    {
        await using var factory = Host(remote: "192.168.1.20", grant: false, environment: "Test");
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://localhost") });
        var journeys = factory.Services.GetRequiredService<RecordingJourneys>();

        (await Send(client, HttpMethod.Post, "/api/integration-journeys/runs", token: Token("User.Read ActiveEventExecute"), body: Start)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Send(client, HttpMethod.Post, "/api/integration-journeys/runs", token: Token("User.Read"), body: Start)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        journeys.Initiators.Should().ContainSingle().Which.Should().MatchRegex("^authenticated-user:[0-9a-f]{16}$");
        journeys.Initiators.Single()!.Should().NotContain("1111").And.NotContain("tester");
    }

    [Fact]
    public async Task ExecutionAccess_ReportsOnlyTheModeAndAReason_AndExecutesNothing()
    {
        await using (var local = Host())
        {
            using var client = local.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://localhost") });
            var response = await Send(client, HttpMethod.Get, "/api/execution-access");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Headers.CacheControl!.NoStore.Should().BeTrue();
            var json = await response.Content.ReadAsStringAsync();
            var view = JsonSerializer.Deserialize<ExecutionAccessView>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            view.CanExecute.Should().BeTrue();
            view.Mode.Should().Be(ExecutionAccessMode.LocalTrusted);
            JsonDocument.Parse(json).RootElement.EnumerateObject().Select(item => item.Name).Should().BeEquivalentTo(["mode", "canExecute", "detail"]);
            json.Should().NotContain("127.0.0.1").And.NotContain("localhost:").And.NotContain("local-session");

            var denied = JsonSerializer.Deserialize<ExecutionAccessView>(await (await Send(client, HttpMethod.Get, "/api/execution-access", origin: "https://evil.example")).Content.ReadAsStringAsync(),
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            denied.CanExecute.Should().BeFalse();
            denied.Mode.Should().Be(ExecutionAccessMode.None);
            local.Services.GetRequiredService<RecordingJourneys>().Initiators.Should().BeEmpty("checking access never starts or records a run");
        }

        await using var remote = Host(remote: "192.168.1.20", grant: false, environment: "Test");
        using var remoteClient = remote.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://localhost") });
        async Task<ExecutionAccessView> Access(string? token) => JsonSerializer.Deserialize<ExecutionAccessView>(
            await (await Send(remoteClient, HttpMethod.Get, "/api/execution-access", token: token)).Content.ReadAsStringAsync(), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        (await Access(Token("ActiveEventExecute"))).Should().BeEquivalentTo(new { Mode = ExecutionAccessMode.Authenticated, CanExecute = true });
        var unpermitted = await Access(Token("User.Read"));
        unpermitted.CanExecute.Should().BeFalse();
        unpermitted.Detail.Should().NotContain("tester").And.NotContain("1111");
        (await Access(null)).CanExecute.Should().BeFalse();
    }

    [Fact]
    public async Task RunValidation_NeedsTheSameExecutionCapability_AndStartsNothing()
    {
        await using var factory = Host();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://localhost") });
        var journeys = factory.Services.GetRequiredService<RecordingJourneys>();

        (await Send(client, HttpMethod.Post, "/api/integration-journeys/runs/validate", body: Start)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Send(client, HttpMethod.Post, "/api/integration-journeys/runs/validate", origin: "https://evil.example", body: Start)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        journeys.Validations.Should().Be(1);
        journeys.Initiators.Should().BeEmpty("validation never starts a run");
    }

    [Fact]
    public async Task ConfigurationWrites_AreNotGrantedByLocalTrustedExecution()
    {
        await using var factory = Host();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://localhost") });
        var package = new IntegrationMessageFlowPackage(new MessageFlowDefinition(), new AltinnTestConfiguration(), new(false, 0, 0, [], []), [], []);
        // LocalConfigurationWrites is a separate setting; with it off, local execution access does not imply configuration rights.
        factory.Services.GetRequiredService<IConfiguration>()["Authorization:LocalConfigurationWrites"] = "false";
        (await Send(client, HttpMethod.Put, "/api/integration-message-flow/dev-env", body: package)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Sets the connection's socket peer like Kestrel would (TestServer leaves it empty). Not a header: nothing in the request can change it.</summary>
    private sealed class Peer(IPAddress remote) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress = remote;
                context.Connection.LocalIpAddress = IPAddress.Loopback;
                return nextMiddleware(context);
            });
            next(app);
        };
    }

    private sealed class RecordingJourneys : IIntegrationJourneyService
    {
        public List<string?> Initiators { get; } = [];
        public Task<IReadOnlyList<IntegrationJourneyPackView>> PacksAsync(string environmentId, CancellationToken ct) => Task.FromResult<IReadOnlyList<IntegrationJourneyPackView>>([]);
        public Task<ArchitectureRuleReport> RulesAsync(string environmentId, string packId, Guid? snapshotId, CancellationToken ct) => Task.FromResult(new ArchitectureRuleReport(packId, null, null, [], []));
        public IReadOnlyList<IntegrationJourneyCatalogEntry> Catalog() => [];
        public Task<IntegrationJourneyRun> StartAsync(IntegrationJourneyRunRequest request, string? initiator, CancellationToken ct)
        {
            Initiators.Add(initiator);
            return Task.FromResult(new IntegrationJourneyRun { OverallState = JourneyRunState.Blocked, InitiatedBy = IntegrationJourneyService.SafeInitiator(initiator) });
        }
        public int Validations { get; private set; }
        public Task<JourneyRunValidation> ValidateAsync(IntegrationJourneyRunRequest request, CancellationToken ct)
        {
            Validations++;
            return Task.FromResult(new JourneyRunValidation { PackId = request.PackId, JourneyId = request.JourneyId, ScenarioId = request.ScenarioId, Valid = false });
        }
        public Task<IntegrationJourneyRun?> GetAsync(Guid runId, CancellationToken ct) => Task.FromResult<IntegrationJourneyRun?>(null);
        public Task<IReadOnlyList<IntegrationJourneyRunSummary>> HistoryAsync(IntegrationJourneyHistoryQuery query, CancellationToken ct) => Task.FromResult<IReadOnlyList<IntegrationJourneyRunSummary>>([]);
    }

    private sealed class StubHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "BirkNext.Api";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
