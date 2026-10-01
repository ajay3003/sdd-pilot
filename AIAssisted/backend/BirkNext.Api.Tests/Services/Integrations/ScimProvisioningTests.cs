using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using BirkNext.Api.Data;
using BirkNext.Api.Services.Integrations;
using BirkNext.Api.Services.Integrations.ApplicationMessaging;
using BirkNext.Api.Services.Integrations.Scim;
using BirkNext.Integrations;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace BirkNext.Api.Tests.Services.Integrations;

/// <summary>
/// SCIM identity provisioning review: detection from a mapped SCIM route surface (never from Service Bus alone), source facts with provenance
/// (publish/commit order, no-op guards, PatchOp parser, retry, health, events), specification classification, stage evidence that keeps source,
/// configuration and runtime apart, GET-only safe checks that never mutate or enumerate users, Production refusal, redaction and snapshots.
/// </summary>
public sealed class ScimProvisioningTests
{
    // ── Synthetic SCIM adapter fixture (no real identities) ───────────────────────────────────────────────────────────

    private sealed record Variant
    {
        public bool PublishBeforeSave { get; init; } = true;
        public bool StringBooleans { get; init; }
        public bool HealthChecks { get; init; }
        public bool Consumer { get; init; }
        public string BasePath { get; init; } = "/scim/v2";
        public bool Spec { get; init; } = true;
        public bool KeyVault { get; init; }
    }

    private static List<SourceFile> Adapter(Variant? v = null)
    {
        v ??= new Variant();
        var publish = """
                    var evt = new BrukerDeaktivertEvent(Guid.NewGuid().ToString(), "BrukerDeaktivert", id.ToString(), DateTimeOffset.UtcNow, "SCIM-PATCH");
                    await _pipeline.ExecuteAsync(async token => await _publisher.PublishAsync(EventPublisher.Topics.EntraBrukere, evt, "BrukerDeaktivert", token), ct);
            """;
        var save = """
                    await _db.SaveChangesAsync(ct);
                    await tx.CommitAsync(ct);
            """;
        var files = new List<SourceFile>
        {
            new("Auth/src/Scim/Scim.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\"></Project>"),
            new("Auth/src/Infra/Infra.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>"),
            new("Auth/src/Scim/Program.cs", $$"""
                var builder = WebApplication.CreateBuilder(args);
                builder.Services.AddDbContext<AuthDb>((sp, opt) => opt.UseSqlServer("x", sql => { sql.EnableRetryOnFailure(maxRetryCount: 3); }));
                var serviceBusDisabled = builder.Configuration.GetValue<bool>("ServiceBus:Disabled") || string.IsNullOrWhiteSpace(builder.Configuration["ServiceBus:FQDN"]);
                if (!serviceBusDisabled) builder.Services.AddScoped<IEventPublisher, EventPublisher>();
                else builder.Services.AddScoped<IEventPublisher, DisabledScimEventPublisher>();
                if (builder.Environment.IsLocal()) { builder.Services.AddPublicDevAuthentication(builder.Configuration); }
                else { builder.Services.AddPublicAuthentication(builder.Configuration); }
                var scimPipeline = new ResiliencePipelineBuilder().AddRetry(new RetryStrategyOptions { MaxRetryAttempts = 3, Delay = TimeSpan.FromMilliseconds(500), BackoffType = DelayBackoffType.Exponential, UseJitter = false }).Build();
                {{(v.KeyVault ? "builder.Configuration.AddAzureKeyVault(new Uri(\"https://kv\"), new DefaultAzureCredential());" : "")}}
                builder.Services.AddHealthChecks(){{(v.HealthChecks ? ".AddDbContextCheck<AuthDb>(tags: [\"ready\"]).AddAzureServiceBusTopic(\"x\", \"entra.brukere\", tags: [\"ready\"])" : "")}};
                var app = builder.Build();
                app.UseCors(c => { c.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader(); });
                app.MapOpenApi();
                app.UseAuthentication();
                app.MapScimUsers();
                app.MapHealthChecks("/health/ready");
                app.Run();
                """),
            new("Auth/src/Scim/Endpoints/UsersEndpoints.cs", $$"""
                public static class UsersEndpoints
                {
                    public static IEndpointRouteBuilder MapScimUsers(this IEndpointRouteBuilder app)
                    {
                        var group = app.MapGroup("{{v.BasePath}}").RequireAuthorization("SCIMPolicy");
                        group.MapPost("/Users", async (ScimUser request, ScimUserService service, CancellationToken ct) => { var (user, isNew) = await service.CreateOrActivateAsync(request, ct); return isNew ? Results.Created("/x", user) : Results.Ok(user); });
                        group.MapGet("/Users", async (ScimUserService service, CancellationToken ct, int startIndex = 1, int count = 20, string? filter = null) => Results.Ok(await service.ListAsync(startIndex, Math.Min(count, 200), filter, ct)));
                        group.MapGet("/Users/{id:guid}", async (Guid id, ScimUserService service, CancellationToken ct) => await service.GetByIdAsync(id, ct) is { } u ? Results.Ok(u) : Results.NotFound());
                        group.MapPatch("/Users/{id:guid}", async (Guid id, ScimPatchRequest request, ScimUserService service, CancellationToken ct) => await service.PatchAsync(id, request, ct) is { } u ? Results.Ok(u) : Results.NotFound());
                        group.MapDelete("/Users/{id:guid}", async (Guid id, ScimUserService service, CancellationToken ct) => { await service.DeactivateAsync(id, ct); return Results.NoContent(); });
                        return app;
                    }
                }
                """),
            new("Auth/src/Scim/Services/ScimUserService.cs", $$"""
                public class ScimUserService
                {
                    private readonly ILogger<ScimUserService> _logger;
                    public async Task<(ScimUser User, bool IsNew)> CreateOrActivateAsync(ScimUser request, CancellationToken ct)
                    {
                        var entraObjectId = request.Id is not null && Guid.TryParse(request.Id, out var parsed) ? parsed : Guid.NewGuid();
                        var strategy = _db.Database.CreateExecutionStrategy();
                        await strategy.ExecuteAsync(async () =>
                        {
                            await using var tx = await _db.Database.BeginTransactionAsync(ct);
                            var existing = await _db.KjentBrukere.FirstOrDefaultAsync(k => k.EntraObjectId == entraObjectId, ct);
                            if (existing is not null && existing.IsActive == request.Active) { return; }
                            if (existing is null) { _db.KjentBrukere.Add(new KjentBruker { EntraObjectId = entraObjectId, IsActive = request.Active }); }
                            var evt = new BrukerAktivertEvent(Guid.NewGuid().ToString(), "BrukerAktivert", entraObjectId.ToString(), DateTimeOffset.UtcNow, "SCIM-POST");
                            await _pipeline.ExecuteAsync(async token => await _publisher.PublishAsync(EventPublisher.Topics.EntraBrukere, evt, "BrukerAktivert", token), ct);
                            _metrics.RecordEventPublished("BrukerAktivert");
                            await _db.SaveChangesAsync(ct);
                            await tx.CommitAsync(ct);
                        });
                        return default;
                    }

                    public async Task<ScimUser?> PatchAsync(Guid id, ScimPatchRequest request, CancellationToken ct)
                    {
                        bool? requestedActive = ParseActiveFromPatch(request);
                        if (requestedActive is null)
                        {
                            var existing = await _db.KjentBrukere.FirstOrDefaultAsync(k => k.EntraObjectId == id, ct);
                            return existing is null ? null : ToScimUser(existing);
                        }
                        var strategy = _db.Database.CreateExecutionStrategy();
                        await strategy.ExecuteAsync(async () =>
                        {
                            await using var tx = await _db.Database.BeginTransactionAsync(ct);
                            var kjentBruker = await _db.KjentBrukere.FirstOrDefaultAsync(k => k.EntraObjectId == id, ct);
                            if (kjentBruker is null) { kjentBruker = new KjentBruker { EntraObjectId = id }; _db.KjentBrukere.Add(kjentBruker); }
                            else if (kjentBruker.IsActive == requestedActive.Value) { return; }
                            kjentBruker.IsActive = requestedActive.Value;
                {{(v.PublishBeforeSave ? publish + save : save + publish)}}
                        });
                        return null;
                    }

                    public async Task DeactivateAsync(Guid id, CancellationToken ct)
                    {
                        var kjentBruker = await _db.KjentBrukere.FirstOrDefaultAsync(k => k.EntraObjectId == id, ct);
                        if (kjentBruker is null) { kjentBruker = new KjentBruker { EntraObjectId = id }; _db.KjentBrukere.Add(kjentBruker); }
                        else if (!kjentBruker.IsActive) { return; }
                        kjentBruker.IsActive = false;
                        var evt = new BrukerDeaktivertEvent(Guid.NewGuid().ToString(), "BrukerDeaktivert", id.ToString(), DateTimeOffset.UtcNow, "SCIM-DELETE");
                        await _pipeline.ExecuteAsync(async token => await _publisher.PublishAsync(EventPublisher.Topics.EntraBrukere, evt, "BrukerDeaktivert", token), ct);
                        await _db.SaveChangesAsync(ct);
                    }

                    public async Task<ScimListResponse> ListAsync(int startIndex, int count, string? filter, CancellationToken ct)
                    {
                        IQueryable<KjentBruker> query = _db.KjentBrukere;
                        if (!string.IsNullOrWhiteSpace(filter))
                        {
                            var match = Regex.Match(filter, "^(\\w+) eq \"(.*)\"$");
                            if (match.Success && match.Groups[1].Value.Equals("userName", StringComparison.OrdinalIgnoreCase)) query = query.Where(k => k.UserName == match.Groups[2].Value);
                            else _logger.LogDebug("Unsupported SCIM filter field: {Field}", filter);
                        }
                        return new(await query.Skip(startIndex - 1).Take(count).ToListAsync(ct));
                    }

                    public Task<ScimUser?> GetByIdAsync(Guid id, CancellationToken ct) => null!;

                    private static bool? ParseActiveFromPatch(ScimPatchRequest request)
                    {
                        foreach (var op in request.Operations)
                        {
                            if (op.Op.Equals("Replace", StringComparison.OrdinalIgnoreCase) && op.Path?.Equals("active", StringComparison.OrdinalIgnoreCase) == true)
                            {
                                {{(v.StringBooleans ? "if (op.Value.ValueKind == JsonValueKind.String && bool.TryParse(op.Value.GetString(), out var parsedString)) return parsedString;" : "")}}
                                try { return op.Value.GetBoolean(); }
                                catch { }
                            }
                        }
                        return null;
                    }
                }
                """),
            new("Auth/src/Scim/Auth/ScimAuthenticationExtensions.cs", """
                public static class ScimAuthenticationExtensions
                {
                    public static IServiceCollection AddPublicAuthentication(this IServiceCollection services, IConfiguration configuration)
                    {
                        services.AddAuthentication().AddMicrosoftIdentityWebApi(configuration, "AzureAd");
                        return services.AddAuthorization(options => options.AddPolicy("SCIMPolicy", policy => { policy.RequireClaim("appid", "00000014-0000-0000-c000-000000000000"); policy.RequireClaim("oid", configuration["AzureAd:ObjectId"]!); }));
                    }
                    public static IServiceCollection AddPublicDevAuthentication(this IServiceCollection services, IConfiguration configuration) =>
                        services.AddAuthorization(options => options.AddPolicy("SCIMPolicy", policy => { policy.RequireAssertion(_ => true); }));
                }
                """),
            new("Auth/src/Scim/Configuration/ScimOptions.cs", "public class ScimOptions { public string ProvisioningSecret { get; set; } = string.Empty; }"),
            new("Auth/src/Scim/Services/DisabledScimEventPublisher.cs", """
                public class DisabledScimEventPublisher : IEventPublisher
                {
                    public Task PublishAsync<T>(string topic, T evt, string eventType, CancellationToken ct = default) where T : class { _logger.LogDebug("Service Bus disabled"); return Task.CompletedTask; }
                }
                """),
            new("Auth/src/Scim/Models/Events.cs", """
                public record BrukerAktivertEvent(string HendelsesId, string HendelsesType, string EntraObjectId, DateTimeOffset Tidsstempel, string KildeReferanse);
                public record BrukerDeaktivertEvent(string HendelsesId, string HendelsesType, string EntraObjectId, DateTimeOffset Tidsstempel, string KildeReferanse);
                """),
            new("Auth/src/Scim/Telemetry/ScimMetrics.cs", """
                public class ScimMetrics
                {
                    public ScimMetrics(IMeterFactory f) { var m = f.Create("scim"); _a = m.CreateCounter<long>("scim.events.published"); _b = m.CreateCounter<long>("scim.publish.failures"); }
                    public void RecordEventPublished(string eventType) => _a.Add(1);
                    public void RecordPublishFailure() => _b.Add(1);
                }
                """),
            new("Auth/src/Infra/EventPublisher.cs", """
                public class EventPublisher : IEventPublisher
                {
                    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
                    public static class Topics { public const string EntraBrukere = "entra.brukere"; }
                    public async Task PublishAsync<T>(string topic, T evt, string eventType, CancellationToken ct = default) where T : class
                    {
                        var eventId = Guid.NewGuid().ToString();
                        var message = new ServiceBusMessage(JsonSerializer.Serialize(evt, JsonOptions)) { Subject = eventType, MessageId = eventId, ApplicationProperties = { ["HendelsesId"] = eventId, ["HendelsesType"] = eventType } };
                        await using var sender = _client.CreateSender(topic);
                        await sender.SendMessageAsync(message, ct);
                        _logger.LogInformation("Event published: {EventType} ({EventId}) → {Topic}", eventType, eventId, topic);
                    }
                }
                """),
            new("Auth/src/Infra/AuthDb.cs", """
                public class AuthDb : DbContext
                {
                    public DbSet<KjentBruker> KjentBrukere => Set<KjentBruker>();
                }
                public class KjentBruker
                {
                    public Guid EntraObjectId { get; set; }
                    public string UserName { get; set; } = "";
                    public string? ExternalId { get; set; }
                    public bool IsActive { get; set; }
                }
                """),
            new("Auth/src/Infra/DevDataSeeder.cs", """
                public class DevDataSeeder
                {
                    public void Seed() => ctx.KjentBrukere.Add(new KjentBruker { UserName = "synthetic.person@example.test", IsActive = true });
                }
                """),
            new("Auth/tests/Scim.Tests/Scim.Tests.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><PackageReference Include=\"Microsoft.NET.Test.Sdk\" /></ItemGroup></Project>"),
            new("Auth/tests/Scim.Tests/ScimUsersEndpointTests.cs", """
                public class ScimTestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions> { }
                public class FakeEventPublisher : IEventPublisher { }
                public class ScimUsersEndpointTests
                {
                    [Fact] public async Task PostUser_WrongBearerToken_Returns401_NoEvent() { await _client.PostAsync("/scim/v2/Users", null); }
                    [Fact] public async Task PatchUser_ActiveFalse_OnActiveUser_Returns200_AndBrukerDeaktivertPublished() { }
                    [Fact] public async Task PostUser_SameRequestFiveTimes_PublishesExactlyOneEvent() { }
                    [Fact] public async Task GetReady_ReturnsOk_WithDatabaseAndServicebusConnected() { }
                }
                """),
        };
        if (v.Consumer)
        {
            files.Add(new("Auth/src/Api/Api.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\"></Project>"));
            files.Add(new("Auth/src/Api/BrukerHandlers.cs", """
                public class BrukerDeaktivertHandler
                {
                    public async Task Handle(BrukerDeaktivertEvent message, AuthDb db) { var bruker = await db.KjentBrukere.FindAsync(message.EntraObjectId); }
                }
                public class AccessCheck { public bool Allowed(KjentBruker kjentBruker) => kjentBruker.IsActive; }
                public static class Listen { public static void Configure(WolverineOptions o) => o.ListenToAzureServiceBusSubscription("autorisasjon").FromTopic("entra.brukere"); }
                """));
        }
        return files;
    }

    private const string SpecDoc = """
        ## Requirements
        - **FR-001**: The adapter MUST expose a SCIM 2.0 endpoint at base path `/scim/v2`.
        - **FR-003**: The endpoint MUST authenticate all incoming requests using a Bearer token (provisioning secret).
        - **FR-008**: All Service Bus events MUST be published before the HTTP 200/204 response is returned.
        - **FR-010**: All SCIM operations MUST be processed idempotently.
        - **FR-014**: The Service Bus dead-letter sub-queue is the durable store for events that cannot be delivered.
        - **FR-021**: The adapter MUST expose a health endpoint indicating Service Bus connectivity and SQL Server connectivity.
        - **FR-022**: If the provisioning secret cannot be retrieved from Key Vault at startup, the adapter MUST fail fast.
        - **FR-023**: If SQL Server is unreachable the adapter MUST return 5xx.
        - **FR-099**: Something BirkNext has no rule for.
        """;

    private static ScimSourceEvidence Analyze(Variant? v = null, List<SourceFile>? code = null, List<ScimSettingsFile>? settings = null)
    {
        v ??= new Variant();
        var documents = v.Spec ? new List<SourceFile> { new("Auth/specs/004-scim-user-sync/spec.md", SpecDoc) } : [];
        return ScimSourceAnalyzer.Analyze("dev", [new SourceArchive("fixture.zip", new string('a', 64), 1)], new ScimSourceSet(code ?? Adapter(v), documents, settings ?? []), DateTimeOffset.UtcNow);
    }

    private static ScimSourceFact Fact(ScimSourceEvidence e, string id) => e.Facts.Single(f => f.Id == id);

    private static IntegrationPlatform Platform(string? baseUrl = null, ScimSyntheticTestContext? synthetic = null) =>
        M2lbDevIntegrationSeed.ScimPlatform("dev", DateTimeOffset.UtcNow) with
        {
            ScimProvisioning = M2lbDevIntegrationSeed.ScimPlatform("dev", DateTimeOffset.UtcNow).ScimProvisioning! with { BaseUrl = baseUrl, SyntheticTest = synthetic ?? new() },
        };

    private static ScimEvidenceCheck Evaluate(ScimSourceEvidence? source, ScimRuntimeEvidence? runtime = null, IntegrationPlatform? platform = null, ServiceBusEvidenceCheck? bus = null, string? environmentType = "Development")
    {
        platform ??= Platform();
        return ScimEvidenceService.Evaluate(platform, platform.ScimProvisioning!, source, runtime ?? new ScimRuntimeEvidence { State = IntegrationEvidenceState.NotConfigured, Reason = "No base URL." },
            M2lbDevIntegrationSeed.ServiceBusPlatform("dev", DateTimeOffset.UtcNow), bus, environmentType, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    }

    // ── 48–49 Detection and base path ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ScimIsDetectedFromTheMappedRouteSurface()
    {
        var evidence = Analyze();

        evidence.Detected.Should().BeTrue();
        evidence.Project.Should().Be("Scim");
        evidence.BasePath.Should().Be("/scim/v2");
        evidence.Operations.Select(o => $"{o.Method} {o.Path}").Should().BeEquivalentTo(["POST /scim/v2/Users", "GET /scim/v2/Users", "GET /scim/v2/Users/{id:guid}", "PATCH /scim/v2/Users/{id:guid}", "DELETE /scim/v2/Users/{id:guid}"]);
        Fact(evidence, "scim-detected").Locations.Should().ContainSingle().Which.File.Should().EndWith("UsersEndpoints.cs");
    }

    [Fact]
    public void ServiceBusAloneDoesNotImplyScim()
    {
        var onlyBus = Adapter().Where(f => f.Path.Contains("/Infra/", StringComparison.Ordinal)).ToList();

        var evidence = Analyze(code: onlyBus);

        evidence.Detected.Should().BeFalse();
        evidence.Facts.Single().State.Should().Be(ScimEvidenceState.NotFound);
        evidence.Operations.Should().BeEmpty();
    }

    [Fact]
    public void BasePathIsReportedAsImplementedAndADifferentPathIsNotAssumed()
    {
        Analyze().Requirements.Single(r => r.Id == "FR-001").Status.Should().Be(ScimRequirementStatus.Implemented);

        var other = Analyze(new Variant { BasePath = "/provisioning/scim" });

        other.Detected.Should().BeTrue();
        other.BasePath.Should().Be("/provisioning/scim");
        other.Requirements.Single(r => r.Id == "FR-001").Status.Should().Be(ScimRequirementStatus.PartiallyImplemented);
        other.Requirements.Single(r => r.Id == "FR-001").Evidence.Should().Contain("/provisioning/scim").And.Contain("/scim/v2");
    }

    // ── 50–52 Activation / deactivation behaviour from source ──────────────────────────────────────────────────────

    [Fact]
    public void PostActivationPublishesBeforeTheCommitAndFallsBackToARandomId()
    {
        var evidence = Analyze();

        var post = evidence.Operations.Single(o => o.Method == "POST");
        post.Behaviour.Should().Contain(b => b.Contains("BrukerAktivert") && b.Contains("entra.brukere") && b.Contains("before SaveChanges/Commit") && b.Contains("retry pipeline"));
        post.Behaviour.Should().Contain(b => b.Contains("new random GUID"));
        Fact(evidence, "scim-post-generated-id").State.Should().Be(ScimEvidenceState.NeedsReview);
        Fact(evidence, "scim-idempotent-noop").State.Should().Be(ScimEvidenceState.SourceVerified);
    }

    [Fact]
    public void PatchAndDeleteDeactivationPublishBrukerDeaktivertAndUpsertUnknownUsers()
    {
        var evidence = Analyze();

        evidence.Events.Single(e => e.EventType == "BrukerDeaktivert").Operations.Should().Contain(["PatchAsync", "DeactivateAsync"]);
        Fact(evidence, "scim-delete-semantics").Detail.Should().Contain("Soft delete");
        Fact(evidence, "scim-unknown-user-upsert").Detail.Should().Contain("PATCH").And.Contain("DELETE").And.Contain("no 404");
    }

    [Fact]
    public void DeactivationPropagationIsAnIssueWithoutAConsumerAndNeverVerifiedFromSource()
    {
        var without = Evaluate(Analyze());
        var with = Evaluate(Analyze(new Variant { Consumer = true }));

        without.Checks.Single(c => c.CheckId == "scim-deactivation-propagation").State.Should().Be(ScimEvidenceState.IssueDetected);
        without.Findings.Should().Contain(f => f.RuleId == "scim-deactivation-not-established" && f.Severity == ScimFindingSeverity.High);
        without.Checks.Single(c => c.CheckId == "scim-deactivation-propagation").Detail.Should().Contain("no subscription").And.Contain("not revoked access");
        with.Checks.Single(c => c.CheckId == "scim-deactivation-propagation").State.Should().Be(ScimEvidenceState.NotTested);
        with.Stages.Single(s => s.Stage == ScimStage.DownstreamProcessing).Source.Should().Be(ScimEvidenceState.SourceVerified);
        with.Stages.Single(s => s.Stage == ScimStage.DownstreamProcessing).Runtime.Should().Be(ScimEvidenceState.NotAssessed);
        with.OverallState.Should().NotBe(ScimOverallState.EndToEndVerified);
    }

    // ── 53 Authentication (runtime challenge) ───────────────────────────────────────────────────────────────────────

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string? Authorization)> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.Method, request.RequestUri!.AbsolutePath, request.Headers.Authorization?.ToString()));
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string body) => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Scim(HttpRequestMessage r) => r.RequestUri!.AbsolutePath switch
    {
        "/health/live" or "/health/ready" => Json(HttpStatusCode.OK, "{\"status\":\"Healthy\",\"secretUserData\":\"synthetic.person@example.test\"}"),
        "/openapi/v1.json" => Json(HttpStatusCode.OK, "{\"paths\":{\"/scim/v2/Users\":{\"get\":{},\"post\":{}}}}"),
        var p when p.StartsWith("/scim/v2/Users/", StringComparison.Ordinal) => new HttpResponseMessage(HttpStatusCode.Unauthorized),
        _ => new HttpResponseMessage(HttpStatusCode.NotFound),
    };

    private static (HttpScimRuntimeProbe Probe, RecordingHandler Handler) Probe(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new RecordingHandler(respond);
        return (new HttpScimRuntimeProbe(new HttpClient(handler), NullLogger<HttpScimRuntimeProbe>.Instance), handler);
    }

    private static readonly ScimProvisioningSettings Settings = new() { BaseUrl = "https://scim.dev.example.test" };

    [Fact]
    public async Task InvalidAndMissingTokensAreRejectedAndOnlyGetIsEverSent()
    {
        var (probe, handler) = Probe(Scim);

        var runtime = await probe.ProbeAsync(Settings, "Development");

        runtime.State.Should().Be(IntegrationEvidenceState.Available);
        runtime.Observations.Single(o => o.CheckId == "scim-auth-missing").State.Should().Be(ScimEvidenceState.Verified);
        runtime.Observations.Single(o => o.CheckId == "scim-auth-invalid").State.Should().Be(ScimEvidenceState.Verified);
        handler.Requests.Should().OnlyContain(r => r.Method == HttpMethod.Get);
        handler.Requests.Should().NotContain(r => r.Path == "/scim/v2/Users", "the user collection is never requested, so no user is enumerated");
        handler.Requests.Where(r => r.Authorization is not null).Should().OnlyContain(r => r.Authorization == $"Bearer {HttpScimRuntimeProbe.InvalidToken}");
        runtime.Observations.Single(o => o.CheckId == "scim-auth-missing").Path.Should().Be("/scim/v2/Users/{random-id}");
    }

    [Fact]
    public async Task ARouteThatAnswersWithoutATokenIsAHighFinding()
    {
        var (probe, _) = Probe(r => r.RequestUri!.AbsolutePath.StartsWith("/scim/v2/Users/", StringComparison.Ordinal) ? new HttpResponseMessage(HttpStatusCode.NotFound) : Scim(r));

        var check = Evaluate(Analyze(), await probe.ProbeAsync(Settings, "Development"));

        check.Stages.Single(s => s.Stage == ScimStage.Authentication).Runtime.Should().Be(ScimEvidenceState.IssueDetected);
        check.Findings.Should().Contain(f => f.RuleId == "scim-auth-not-enforced" && f.Severity == ScimFindingSeverity.High);
        check.OverallState.Should().Be(ScimOverallState.IssueDetected);
        ScimEvidenceService.ReviewChecks(Platform(), check).Checks.Single(c => c.CheckId == "scim-auth-missing").Status.Should().Be(IntegrationCheckStatus.Fail);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData(null)]
    [InlineData("Custom")]
    public async Task ProductionAndUnknownEnvironmentsAreNeverContacted(string? environmentType)
    {
        var (probe, handler) = Probe(Scim);

        var runtime = await probe.ProbeAsync(Settings, environmentType);

        runtime.State.Should().Be(IntegrationEvidenceState.NotSupported);
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task MissingBaseUrlIsNotConfiguredNotZero()
    {
        var (probe, handler) = Probe(Scim);

        var runtime = await probe.ProbeAsync(new ScimProvisioningSettings(), "Development");

        runtime.State.Should().Be(IntegrationEvidenceState.NotConfigured);
        runtime.Observations.Should().BeEmpty();
        handler.Requests.Should().BeEmpty();
        Evaluate(Analyze(), runtime).Stages.Single(s => s.Stage == ScimStage.ScimEndpoint).Runtime.Should().Be(ScimEvidenceState.NotConfigured);
    }

    [Fact]
    public async Task ResponseBodiesAreNeverStored()
    {
        var (probe, _) = Probe(Scim);

        var runtime = await probe.ProbeAsync(Settings, "Development");

        JsonSerializer.Serialize(runtime).Should().NotContain("synthetic.person@example.test").And.NotContain("secretUserData");
        runtime.Observations.Single(o => o.CheckId == "scim-health-ready").Detail.Should().Contain("status \"Healthy\"").And.Contain("not dependency health");
        runtime.Observations.Single(o => o.CheckId == "scim-openapi").Detail.Should().Contain("GET/POST /scim/v2/Users");
    }

    // ── 54 Malformed PATCH ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void PatchParserLimitsAreReportedNotAssumedAcceptable()
    {
        var evidence = Analyze();

        Fact(evidence, "scim-patch-parser").Detail.Should().Contain("op Replace").And.Contain("path active").And.Contain("JSON Merge Patch is not parsed");
        Fact(evidence, "scim-patch-missing-operations").Detail.Should().Contain("HTTP 500");
        Fact(evidence, "scim-patch-string-boolean").State.Should().Be(ScimEvidenceState.NeedsReview);
        Fact(evidence, "scim-patch-unrecognised-200").Detail.Should().Contain("HTTP 200");
        Evaluate(evidence).Findings.Should().Contain(f => f.RuleId == "scim-patch-silent-noop");
        Analyze(new Variant { StringBooleans = true }).Facts.Should().NotContain(f => f.Id == "scim-patch-string-boolean");
    }

    // ── 55–58 Failure order, retry, idempotency ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void SqlAndPublishFailureOrderIsDerivedFromTheHandler()
    {
        var evidence = Analyze();

        var order = Fact(evidence, "scim-order-publish-before-commit");
        order.Detail.Should().Contain("publish failure therefore rolls back and returns 5xx").And.Contain("event is already on the topic").And.Contain("NEW event id");
        evidence.Requirements.Single(r => r.Id == "FR-023").Status.Should().Be(ScimRequirementStatus.PartiallyImplemented);
        evidence.Requirements.Single(r => r.Id == "FR-008").Status.Should().Be(ScimRequirementStatus.Implemented);
        Fact(evidence, "scim-strategy-replays-publish").State.Should().Be(ScimEvidenceState.NeedsReview);
    }

    [Fact]
    public void CommitBeforePublishWithANoOpGuardCanLoseTheEventPermanently()
    {
        var evidence = Analyze(new Variant { PublishBeforeSave = false });

        Fact(evidence, "scim-order-commit-before-publish").Detail.Should().Contain("lost permanently");
    }

    [Fact]
    public void RetryIsConfiguredButNeverReportedAsObserved()
    {
        var evidence = Analyze();
        var check = Evaluate(evidence);

        Fact(evidence, "scim-retry-policy").Detail.Should().Contain("3 retries").And.Contain("500 ms / 1000 ms / 2000 ms").And.Contain("Configured, not observed");
        check.Checks.Single(c => c.CheckId == "scim-retry-observed").State.Should().Be(ScimEvidenceState.NotTested);
        Fact(evidence, "scim-dlq-sender").Detail.Should().Contain("never reaches a dead-letter queue");
        evidence.Requirements.Single(r => r.Id == "FR-014").Status.Should().Be(ScimRequirementStatus.DocumentedOnly);
    }

    [Fact]
    public void IdempotencyIsPartialWhenRetriesRepublishUnderNewIds()
    {
        var evidence = Analyze();

        evidence.Requirements.Single(r => r.Id == "FR-010").Status.Should().Be(ScimRequirementStatus.PartiallyImplemented);
        Evaluate(evidence).Findings.Should().Contain(f => f.RuleId == "scim-duplicate-events");
    }

    // ── 59–62 List, contract, route, consumer ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void ListKeepsActiveStateAndIgnoredFiltersAreReported()
    {
        var evidence = Analyze();

        Fact(evidence, "scim-list").Detail.Should().Contain("active and inactive").And.Contain("userName");
        Fact(evidence, "scim-list-unsupported-filter").Area.Should().Be(ScimArea.Privacy);
        Fact(evidence, "scim-list-cap").Detail.Should().Contain("200");
    }

    [Fact]
    public void EventContractComesFromSourceWithTheIdentifierRelation()
    {
        var evidence = Analyze();

        var deactivated = evidence.Events.Single(e => e.EventType == "BrukerDeaktivert");
        deactivated.Topic.Should().Be("entra.brukere");
        deactivated.BodyFields.Should().Equal("hendelsesId", "hendelsesType", "entraObjectId", "tidsstempel", "kildeReferanse");
        deactivated.IdentityFields.Should().Equal("entraObjectId");
        deactivated.SessionIdSet.Should().BeFalse();
        Fact(evidence, "scim-message-id-mismatch").State.Should().Be(ScimEvidenceState.NeedsReview);
    }

    [Fact]
    public void ServiceBusRouteKeepsConfiguredMatchedAndObservedApart()
    {
        var configured = Evaluate(Analyze());
        var observed = Evaluate(Analyze(), bus: new ServiceBusEvidenceCheck
        {
            PlatformId = M2lbDevIntegrationSeed.ServiceBusPlatformId,
            Runtime = new ServiceBusRuntimeEvidence { State = IntegrationEvidenceState.Available, Entities = [new() { EntityType = ServiceBusEntityType.Topic, Name = "entra.brukere", Status = "Active" }] },
        });

        var route = configured.Stages.Single(s => s.Stage == ScimStage.ServiceBusRoute);
        route.Source.Should().Be(ScimEvidenceState.Matched);
        route.SourceDetail.Should().Contain("no subscription");
        route.Runtime.Should().Be(ScimEvidenceState.NotAssessed);
        observed.Stages.Single(s => s.Stage == ScimStage.ServiceBusRoute).Runtime.Should().Be(ScimEvidenceState.Observed);
        observed.Stages.Single(s => s.Stage == ScimStage.DownstreamProcessing).Runtime.Should().Be(ScimEvidenceState.NotAssessed);
    }

    [Fact]
    public void ADifferentSourceTopicIsNotAssumed()
    {
        var platform = Platform() with { ScimProvisioning = Platform().ScimProvisioning! with { Topic = "entra.users" } };

        Evaluate(Analyze(), platform: platform).Stages.Single(s => s.Stage == ScimStage.ServiceBusRoute).Source.Should().Be(ScimEvidenceState.IssueDetected);
    }

    [Fact]
    public void DownstreamConsumerAndAccessUseAreFoundInOtherProjects()
    {
        var evidence = Analyze(new Variant { Consumer = true });

        evidence.Consumers.Should().Contain(c => c.Kind == "Handler of the event type" && c.Detail.Contains("BrukerDeaktivertHandler.Handle"));
        evidence.Consumers.Should().Contain(c => c.Kind.StartsWith("FromTopic"));
        Fact(evidence, "scim-state-usage").State.Should().Be(ScimEvidenceState.SourceVerified);
    }

    // ── 63–64 Health and Key Vault ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void HealthWithoutDependencyChecksIsAnIssue()
    {
        var none = Analyze();
        var with = Analyze(new Variant { HealthChecks = true });

        Fact(none, "scim-health-checks").State.Should().Be(ScimEvidenceState.IssueDetected);
        none.Requirements.Single(r => r.Id == "FR-021").Status.Should().Be(ScimRequirementStatus.NotFound);
        Evaluate(none).Findings.Should().Contain(f => f.RuleId == "scim-health-without-dependencies");
        Fact(with, "scim-health-checks").State.Should().Be(ScimEvidenceState.SourceVerified);
        with.Requirements.Single(r => r.Id == "FR-021").Status.Should().Be(ScimRequirementStatus.Implemented);
    }

    [Fact]
    public void KeyVaultFailFastIsClassifiedFromSourceOnly()
    {
        Analyze().Requirements.Single(r => r.Id == "FR-022").Status.Should().Be(ScimRequirementStatus.NotFound);
        Analyze(new Variant { KeyVault = true }).Requirements.Single(r => r.Id == "FR-022").Status.Should().Be(ScimRequirementStatus.CannotAssess);
    }

    // ── Specification vs implementation ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SpecificationIsClassifiedNeverTakenAsImplemented()
    {
        var evidence = Analyze();

        var auth = evidence.Requirements.Single(r => r.Id == "FR-003");
        auth.Status.Should().Be(ScimRequirementStatus.PartiallyImplemented);
        auth.Evidence.Should().Contain("not a provisioning Bearer secret");
        auth.SpecLocation!.File.Should().EndWith("spec.md");
        evidence.Requirements.Single(r => r.Id == "FR-099").Status.Should().Be(ScimRequirementStatus.CannotAssess);
        Analyze(new Variant { Spec = false }).Requirements.Should().BeEmpty();
        Analyze(new Variant { Spec = false }).Limitations.Should().Contain(l => l.Contains("No SCIM specification"));
    }

    [Fact]
    public void RepositoryTestsAreMappedWithTheirFakes()
    {
        var coverage = Analyze().TestCoverage;

        coverage.Single(c => c.Scenario.StartsWith("Invalid token")).State.Should().Be(ScimTestCoverageState.TestedWithFake);
        coverage.Single(c => c.Scenario.StartsWith("Invalid token")).Note.Should().Contain("production appid/oid policy is not exercised");
        coverage.Single(c => c.Scenario.StartsWith("SQL failure")).State.Should().Be(ScimTestCoverageState.NotTested);
        coverage.Single(c => c.Scenario == "Health endpoints").Note.Should().Contain("cannot detect");
    }

    // ── 65–66 Secret and PII redaction ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SettingsAreReducedToKeysAndStructuralValues()
    {
        var reduced = ScimSourceReader.Reduce("""{ "ConnectionStrings": { "Db": "Server=x;Password=Hunter2!" }, "ServiceBus": { "FQDN": "Endpoint=sb://x;SharedAccessKey=abc=", "HendelsesTopics": { "EntraBrukere": "entra.brukere" } }, "AzureAd": { "TenantId": "common", "ClientSecret": "s3cr3t" } }""");

        reduced["ConnectionStrings:Db"].Should().Be("(set)");
        reduced["ServiceBus:FQDN"].Should().Be("(set)");
        reduced["ServiceBus:HendelsesTopics:EntraBrukere"].Should().Be("entra.brukere");
        reduced["AzureAd:TenantId"].Should().Be("common");
        reduced["AzureAd:ClientSecret"].Should().Be("(set)");
        string.Join(" ", reduced.Values).Should().NotContainAny("Hunter2", "SharedAccessKey", "s3cr3t");
    }

    [Fact]
    public void EvidenceNeverCarriesSourceLiteralsTokensOrUserData()
    {
        var settings = new List<ScimSettingsFile> { new("Auth/src/Scim/appsettings.Development.json", ScimSourceReader.Reduce("""{ "ServiceBus": { "FQDN": "" }, "AzureAd": { "TenantId": "common", "ObjectId": "0000" } }""")) };
        var evidence = Analyze(settings: settings);
        var check = Evaluate(evidence);

        var json = JsonSerializer.Serialize(check) + JsonSerializer.Serialize(evidence);
        json.Should().NotContain("synthetic.person@example.test");
        json.Should().NotContain(HttpScimRuntimeProbe.InvalidToken);
        evidence.StateUsage.Should().Contain(u => u.Kind == "Seed data" && u.Detail.Contains("\"…\""));
        Fact(evidence, "scim-disabled-publisher").Detail.Should().Contain("ServiceBus:FQDN is empty in appsettings.Development.json");
        Fact(evidence, "scim-auth-multitenant").State.Should().Be(ScimEvidenceState.NeedsReview);
    }

    // ── Synthetic mutation capability, stages, IQR mapping ─────────────────────────────────────────────────────────

    [Fact]
    public void SyntheticMutationIsOffByDefaultAndNeverRunsAgainstProduction()
    {
        ScimEvidenceService.Mutation(new ScimProvisioningSettings(), "Development").State.Should().Be(ScimMutationState.NotConfigured);
        var context = new ScimSyntheticTestContext { Environment = "QA", TestUserPrefix = ScimSyntheticTestContext.RequiredPrefix + "1", ApprovedByTestLead = true, CleanupPlan = "delete", Enabled = true };
        ScimEvidenceService.Mutation(new ScimProvisioningSettings { SyntheticTest = context with { Enabled = false } }, "QA").State.Should().Be(ScimMutationState.Disabled);
        ScimEvidenceService.Mutation(new ScimProvisioningSettings { SyntheticTest = context }, "QA").State.Should().Be(ScimMutationState.NotImplemented);
        ScimEvidenceService.Mutation(new ScimProvisioningSettings { SyntheticTest = context }, "Production").State.Should().Be(ScimMutationState.NotAllowedForEnvironment);
        (context with { Environment = "PROD" }).Validate().Should().Contain("DEV or QA");
        (context with { TestUserPrefix = "ola.nordmann" }).Validate().Should().Contain(ScimSyntheticTestContext.RequiredPrefix);
    }

    [Fact]
    public void SettingsRejectCredentialsInTheBaseUrl()
    {
        new ScimProvisioningSettings { BaseUrl = "https://user:pass@scim.example.test" }.Validate().Should().Contain("credentials");
        new ScimProvisioningSettings { BaseUrl = "https://scim.example.test?code=x" }.Validate().Should().Contain("query");
        new ScimProvisioningSettings { BaseUrl = "http://scim.example.test" }.Validate().Should().Contain("https");
        new ScimProvisioningSettings { BaseUrl = "http://localhost:5051" }.Validate().Should().BeNull();
    }

    [Fact]
    public void StagesKeepSourceAndRuntimeApartAndSourceNeverYieldsEndToEnd()
    {
        var check = Evaluate(Analyze());

        check.Stages.Select(s => s.Stage).Should().Equal(Enum.GetValues<ScimStage>());
        check.Stages.Single(s => s.Stage == ScimStage.KjentBrukerPersistence).Source.Should().Be(ScimEvidenceState.SourceVerified);
        check.Stages.Single(s => s.Stage == ScimStage.KjentBrukerPersistence).Runtime.Should().Be(ScimEvidenceState.NotTested);
        check.Stages.Should().NotContain(s => ScimLabels.IsRuntime(s.Runtime));
        check.OverallState.Should().NotBe(ScimOverallState.EndToEndVerified);
        check.Missing.Should().Contain(m => m.Contains("public SCIM base URL")).And.Contain(m => m.Contains("synthetic SCIM test context"));
        Evaluate(null).OverallState.Should().Be(ScimOverallState.NotTestable);
    }

    [Fact]
    public void IqrContributionKeepsSourceFactsOutOfPassAndRuntimeDomainsNotAssessed()
    {
        var (checks, findings) = ScimEvidenceService.ReviewChecks(Platform(), Evaluate(Analyze()));

        checks.Where(c => c.Provenance == IntegrationEvidenceSource.SourceCode).Should().NotContain(c => c.Status == IntegrationCheckStatus.Pass);
        checks.Single(c => c.CheckId == "scim-message-flow").Status.Should().Be(IntegrationCheckStatus.NotAssessed);
        checks.Single(c => c.CheckId == "scim-performance").Status.Should().Be(IntegrationCheckStatus.NotAssessed);
        checks.Single(c => c.CheckId == "scim-persistence").Domain.Should().Be(IntegrationReviewDomain.Configuration);
        checks.Single(c => c.CheckId == "scim-deactivation-propagation").Domain.Should().Be(IntegrationReviewDomain.Security);
        checks.Single(c => c.CheckId == "scim-retry-policy").Domain.Should().Be(IntegrationReviewDomain.Reliability);
        checks.Single(c => c.CheckId == "scim-health-checks").Domain.Should().Be(IntegrationReviewDomain.Observability);
        checks.Single(c => c.CheckId == "scim-post-generated-id").Domain.Should().Be(IntegrationReviewDomain.DataQuality);
        findings.Should().Contain(f => f.Key.StartsWith("scim:dev:scim:m2lb:") && f.Domain == IntegrationReviewDomain.Security);
    }

    // ── Store, seed, IQR integration ────────────────────────────────────────────────────────────────────────────────

    private static byte[] Zip(IEnumerable<SourceFile> files)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var file in files)
            {
                using var writer = new StreamWriter(zip.CreateEntry(file.Path).Open(), Encoding.UTF8);
                writer.Write(file.Content);
            }
        return stream.ToArray();
    }

    private sealed class FixedProbe(ScimRuntimeEvidence runtime) : IScimRuntimeProbe
    {
        public List<string?> Environments { get; } = [];
        public Task<ScimRuntimeEvidence> ProbeAsync(ScimProvisioningSettings settings, string? environmentType, CancellationToken ct = default) { Environments.Add(environmentType); return Task.FromResult(runtime); }
    }

    private static AppDbContext Db() => new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static IntegrationCatalogService Catalog(AppDbContext db) => new(db, NullLogger<IntegrationCatalogService>.Instance);

    [Fact]
    public async Task AnalysesAndChecksAreStoredAsImmutableSnapshots()
    {
        await using var db = Db();
        var catalog = Catalog(db);
        await catalog.GetAsync("dev", "Development", "https://m2lbdev.bufetat.no/");
        var probe = new FixedProbe(new ScimRuntimeEvidence { State = IntegrationEvidenceState.NotConfigured, Reason = "No base URL." });
        var service = new ScimEvidenceService(db, catalog, probe, NullLogger<ScimEvidenceService>.Instance);

        var (first, error) = await service.AnalyzeAsync("dev", [("M2LB.zip", Zip(Adapter()))]);
        error.Should().BeNull();
        await service.AnalyzeAsync("dev", [("M2LB.zip", Zip(Adapter(new Variant { HealthChecks = true })))]);
        var (check, checkError) = await service.RunSafeChecksAsync("dev", M2lbDevIntegrationSeed.ScimPlatformId, "Development", null);

        checkError.Should().BeNull();
        first!.Detected.Should().BeTrue();
        db.ScimEvidence.Count(r => r.Kind == "source").Should().Be(2);
        (await service.OverviewAsync("dev")).Source!.Facts.Single(f => f.Id == "scim-health-checks").State.Should().Be(ScimEvidenceState.SourceVerified);
        (await service.OverviewAsync("dev")).History.Should().ContainSingle().Which.RunId.Should().Be(check!.RunId);
        (await service.GetRunAsync(check.RunId))!.Findings.Count.Should().Be(check.Findings.Count);
        probe.Environments.Should().Equal("Development");
        (await service.RunSafeChecksAsync("dev", M2lbDevIntegrationSeed.ServiceBusPlatformId, "Development", null)).Error.Should().Contain("Not an identity provisioning platform");
    }

    [Fact]
    public async Task SeedV3AddsTheScimPlatformWithUnknownBaseUrl()
    {
        await using var db = Db();

        var catalog = await Catalog(db).GetAsync("dev", "Development", "https://m2lbdev.bufetat.no/");

        var scim = catalog.Platforms.Single(p => p.Id == M2lbDevIntegrationSeed.ScimPlatformId);
        scim.Kind.Should().Be(IntegrationKind.IdentityProvisioning);
        scim.ScimProvisioning!.BaseUrl.Should().BeNull();
        scim.ScimProvisioning.Topic.Should().Be("entra.brukere");
        scim.ScimProvisioning.OutboundPlatformId.Should().Be(M2lbDevIntegrationSeed.ServiceBusPlatformId);
        scim.ScimProvisioning.SyntheticTest.Enabled.Should().BeFalse();
        IntegrationConfigurationRules.KindLabel(IntegrationKind.IdentityProvisioning).Should().Be("Identity provisioning");
    }

    [Fact]
    public async Task SeedUpgradeFromV2AddsScimButNeverRestoresADeletedServiceBusPlatform()
    {
        await using var db = Db();
        db.IntegrationEnvironmentStates.Add(new BirkNext.Api.Models.IntegrationEnvironmentStateRecord { EnvironmentId = "dev", SeedName = M2lbDevIntegrationSeed.Name, SeedVersion = 2, UpdatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();

        var catalog = await Catalog(db).GetAsync("dev", "Development", "https://m2lbdev.bufetat.no/");

        catalog.Platforms.Select(p => p.Id).Should().Contain(M2lbDevIntegrationSeed.ScimPlatformId).And.NotContain(M2lbDevIntegrationSeed.ServiceBusPlatformId);
    }

    // ── Real read-only acceptance (runs only where the developer's archives exist) ─────────────────────────────────

    [Fact]
    public void RealM2lbSourceAcceptance()
    {
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        var names = new[] { "M2LB (1).zip", "M2LB.Common.zip" };
        if (names.Any(n => !File.Exists(Path.Combine(downloads, n)))) return;
        var archives = new List<SourceArchive>();
        var code = new List<SourceFile>();
        var documents = new List<SourceFile>();
        var settings = new List<ScimSettingsFile>();
        foreach (var name in names)
        {
            var (archive, files, error) = ScimSourceReader.Read(name, File.ReadAllBytes(Path.Combine(downloads, name)));
            error.Should().BeNull();
            archives.Add(archive!);
            code.AddRange(files.Code);
            documents.AddRange(files.Documents);
            settings.AddRange(files.Settings);
        }

        var evidence = ScimSourceAnalyzer.Analyze("dev", archives, new ScimSourceSet(code, documents, settings), DateTimeOffset.UtcNow);
        var check = Evaluate(evidence);

        evidence.Project.Should().Be("M2LB.Autorisasjon.ScimAdapter");
        evidence.BasePath.Should().Be("/scim/v2");
        evidence.Operations.Should().HaveCount(5);
        evidence.Events.Select(e => e.Topic).Distinct().Should().Equal("entra.brukere");
        new[] { "scim-order-publish-before-commit", "scim-message-id-mismatch", "scim-health-ready-empty", "scim-patch-string-boolean" }.Should().OnlyContain(id => evidence.Facts.Any(f => f.Id == id));
        Fact(evidence, "scim-downstream-consumer").State.Should().Be(ScimEvidenceState.NotFound);
        evidence.Requirements.Single(r => r.Id == "FR-021").Status.Should().Be(ScimRequirementStatus.NotFound);
        evidence.Requirements.Single(r => r.Id == "FR-003").Status.Should().Be(ScimRequirementStatus.PartiallyImplemented);
        evidence.Requirements.Single(r => r.Id == "FR-016").Status.Should().Be(ScimRequirementStatus.NotFound);
        check.Findings.Should().Contain(f => f.RuleId == "scim-deactivation-not-established" && f.Severity == ScimFindingSeverity.High);
        check.Stages.Single(s => s.Stage == ScimStage.ServiceBusRoute).Source.Should().Be(ScimEvidenceState.Matched);
        JsonSerializer.Serialize(evidence).Should().NotContain("@example.com").And.NotContain("SharedAccessKey").And.NotContain("M2LB_Dev123");
    }
    // ── Source Analysis owns the archive; SCIM consumes one snapshot ──────────────────────────────────────────────

    [Fact]
    public async Task ScimEvidenceComesFromExactlyOneSourceAnalysisSnapshot()
    {
        await using var db = Db();
        var catalog = Catalog(db);
        var service = new ScimEvidenceService(db, catalog, new FixedProbe(new ScimRuntimeEvidence { State = IntegrationEvidenceState.NotConfigured, Reason = "No base URL." }), NullLogger<ScimEvidenceService>.Instance);
        var (snapshot, error) = await new BirkNext.Api.Services.Integrations.SourceEvidence.IqrSourceStore(db).AnalyzeAsync("dev", "source-analysis", "M2LB.zip", Zip(Adapter()));
        error.Should().BeNull();

        var (evidence, useError) = await service.UseSourceScopeAsync("dev", new() { PrimarySnapshotId = snapshot!.Id });
        var (direct, _) = await service.AnalyzeAsync("other", [("M2LB.zip", Zip(Adapter()))]);

        useError.Should().BeNull();
        evidence!.Detected.Should().BeTrue();
        evidence.Facts.Select(f => (f.Id, f.State)).Should().Equal(direct!.Facts.Select(f => (f.Id, f.State)), "the same analyzer read the same archive");
        evidence.SourceScope!.Primary.SnapshotId.Should().Be(snapshot.Id);
        (await service.OverviewAsync("dev")).Source!.SourceScope!.Primary.Fingerprint.Should().Be(snapshot.Archive.Sha256);
        (await service.UseSourceScopeAsync("dev", new() { PrimarySnapshotId = snapshot.Id, RelatedSnapshotIds = [Guid.NewGuid()] })).Error.Should().Contain("one source snapshot");
        typeof(BirkNext.Api.Controllers.ScimProvisioningController).GetMethods().SelectMany(m => m.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.HttpPostAttribute), false).Cast<Microsoft.AspNetCore.Mvc.HttpPostAttribute>())
            .Select(a => a.Template).Should().NotContain("source", "the archive upload endpoint is gone");
    }

}

/// <summary>A SCIM runtime probe that contacts nothing and records the environment type it was asked for.</summary>
internal sealed class StubScimRuntimeProbe : IScimRuntimeProbe
{
    public List<string?> EnvironmentTypes { get; } = [];
    public Task<ScimRuntimeEvidence> ProbeAsync(ScimProvisioningSettings settings, string? environmentType, CancellationToken ct = default)
    {
        EnvironmentTypes.Add(environmentType);
        return Task.FromResult(HttpScimRuntimeProbe.Gate(settings, environmentType) is { } gate
            ? new ScimRuntimeEvidence { State = gate.State, Reason = gate.Reason, CapturedAt = DateTimeOffset.UtcNow }
            : new ScimRuntimeEvidence { State = IntegrationEvidenceState.Unavailable, Reason = "Stub: not contacted.", CapturedAt = DateTimeOffset.UtcNow });
    }

}
