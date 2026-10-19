using BirkNext.Api.Controllers;
using BirkNext.Api.Data;
using BirkNext.Api.Services.Integrations;
using BirkNext.Api.Services.SourceAnalysis;
using BirkNext.Integrations;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Tests.Services.Integrations;

/// <summary>
/// IQR → Extensions → Altinn Test Configuration → Save. Regression for the save that failed with "Remove any credential-like value" even
/// with only Environment = Test: once an IQR binding existed, the form sent the bound snapshot as the configuration's schema snapshot with no
/// schema contract selected, the controller refused that (400), and the form reported every refusal as a credential problem. Credential
/// protection stays on, per field: safe references and mechanism names are stored; credential material is refused with the field named and
/// the value never echoed.
/// </summary>
public sealed class AltinnConfigurationSaveTests
{
    private const string Env = "dev";

    private sealed class Sources(params IqrSourceSnapshot[] snapshots) : IReviewSourceEvidenceProvider
    {
        public bool SourceAnalysisEnabled => true;
        public Task<IReadOnlyList<IqrSourceSnapshot>> ListAsync(string environmentId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<IqrSourceSnapshot>>(snapshots);
        public Task<IqrSourceSnapshot?> ResolveAsync(string environmentId, Guid snapshotId, CancellationToken ct = default) => Task.FromResult(snapshots.FirstOrDefault(item => item.Id == snapshotId));
    }

    private static readonly IqrSourceSnapshot Bound = new() { Id = Guid.NewGuid(), Archive = new("Tiltak 2 - Sikkert digitalt økosystem (1).zip", new string('a', 64), 1) };

    private static (IntegrationMessageFlowController Controller, IntegrationMessageFlowStore Store, AppDbContext Db) Create()
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var store = new IntegrationMessageFlowStore(db);
        return (new IntegrationMessageFlowController(store), store, db);
    }

    /// <summary>What the form sends: the stored package back, with the edited configuration.</summary>
    private static async Task<IntegrationMessageFlowPackage> FormAsync(IntegrationMessageFlowStore store, Func<AltinnTestConfiguration, AltinnTestConfiguration> edit)
    {
        var package = await store.GetAsync(Env);
        return package with { AltinnConfiguration = edit(package.AltinnConfiguration) };
    }

    private static async Task<(IntegrationMessageFlowPackage? Saved, MessageFlowSaveProblem? Problem)> SaveAsync(Func<AltinnTestConfiguration, AltinnTestConfiguration> edit)
    {
        var (controller, store, db) = Create();
        await using var _ = db;
        var result = (await controller.Save(Env, await FormAsync(store, edit), new Sources(Bound), default)).Result;
        return result switch
        {
            OkObjectResult { Value: IntegrationMessageFlowPackage saved } => (saved, null),
            ObjectResult { Value: MessageFlowSaveProblem problem } => (null, problem),
            _ => throw new InvalidOperationException($"Unexpected result {result?.GetType().Name}"),
        };
    }

    // ── The reported defect ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_EnvironmentTestAlone_IsSaved_EvenWhenTheFormCarriesTheBoundSnapshotWithoutASchemaContract()
    {
        // Exactly what the form sent after an IQR binding existed: the bound snapshot, no XML Schema contract.
        var (saved, problem) = await SaveAsync(config => config with { Environment = ExternalTestEnvironment.Test, SourceSnapshotId = Bound.Id, SourceContractId = null });
        problem.Should().BeNull();
        saved!.AltinnConfiguration.Environment.Should().Be(ExternalTestEnvironment.Test);
        saved.AltinnConfiguration.SourceSnapshotId.Should().BeNull("without a schema contract no snapshot is stored: the canonical evidence is the IQR binding");
    }

    [Fact]
    public async Task ASelectedSchemaContract_StillHasToBeInTheExactSnapshot_AndTheProblemNamesTheField()
    {
        var (saved, problem) = await SaveAsync(config => config with { Environment = ExternalTestEnvironment.Test, SourceSnapshotId = Bound.Id, SourceContractId = "xsd:missing.xsd" });
        saved.Should().BeNull();
        problem!.Errors.Should().ContainSingle().Which.Should().Match<ConfigurationFieldError>(error => error.Field == "Source XML Schema contract" && error.Section == "Contracts & correlation");
        problem.Message.Should().NotContain("credential", "a contract problem is never reported as a credential problem");
    }

    // ── Save succeeds (B–H) ─────────────────────────────────────────────────────────────────────────────────────────

    public static TheoryData<string, Func<AltinnTestConfiguration, AltinnTestConfiguration>> Safe => new()
    {
        { "B app identifier", c => c with { Environment = ExternalTestEnvironment.Test, AppIdentifier = "buf/skolenaervaer" } },
        { "C base URL", c => c with { BaseUrlReference = "https://buf.apps.tt02.altinn.no/buf/skolenaervaer" } },
        { "D endpoint URL", c => c with { EndpointReference = "https://buf-meldings-utveksler.azurewebsites.net/api/altinn/webhook" } },
        { "E mechanism text", c => c with { ProductOrServiceType = "ID-porten · Bearer JWT · AltinnToken", DocumentedScopes = ["openid", "altinn:instances.read"], MessageType = "id-porten / maskinporten" } },
        { "F client reference", c => c with { ClientReference = "6cd084fd-c16f-41e7-8efc-06c2fb29ec01" } },
        { "G tenant reference", c => c with { TenantReference = "bufdir-test-tenant" } },
        { "H certificate and key references", c => c with { CertificateReference = "6F475F3160B976C7599BDF7F4064322FE48F7529", KeyReference = "kv-qa/secrets/my-key-vault-secret-name" } },
        { "queue and topic names", c => c with { QueueReference = "m2lb_skolenaervaer", MuChannelReference = "sbns-meldings-utveksler-2.servicebus.windows.net/meldingsutvekslerincoming" } },
    };

    [Theory]
    [MemberData(nameof(Safe))]
    public async Task SafeReferencesAndMechanismNames_AreSaved(string _, Func<AltinnTestConfiguration, AltinnTestConfiguration> edit)
    {
        var (saved, problem) = await SaveAsync(edit);
        problem.Should().BeNull();
        saved.Should().NotBeNull();
    }

    // ── Save fails, naming the field (I–O) ──────────────────────────────────────────────────────────────────────────

    private const string Jwt = "eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiJ0ZXN0In0.c2lnbmF0dXJl";

    public static TheoryData<string, Func<AltinnTestConfiguration, AltinnTestConfiguration>, string, string> Unsafe => new()
    {
        { "I raw JWT", c => c with { ClientReference = Jwt }, "Client / app reference", "JSON Web Token" },
        { "J bearer token", c => c with { TenantReference = "Authorization: Bearer abcdefghijklmnopqrstuvwxyz0123456789" }, "Tenant reference", "bearer token" },
        { "K client secret", c => c with { KeyReference = "client_secret=Q8x~AbCdEfGhIjKlMnOpQrStUvWxYz012345" }, "Key reference", "secret" },
        { "K raw secret value", c => c with { ClientReference = "Q8x~AbCdEfGhIjKlMnOpQrStUvWxYz0123456789" }, "Client / app reference", "raw secret" },
        { "L connection string", c => c with { QueueReference = "Endpoint=sb://ns.servicebus.windows.net/;SharedAccessKeyName=root;SharedAccessKey=abc123=" }, "Queue reference", "key" },
        { "M private key", c => c with { CertificateReference = "-----BEGIN PRIVATE KEY-----MIIEv" }, "Certificate reference", "key or certificate material" },
        { "N password", c => c with { ReporterReference = "password: hunter2-please" }, "Test reporter reference", "password" },
        { "O SAS token", c => c with { BaseUrlReference = "https://st.blob.core.windows.net/c?sv=2022-11-02&sig=abc%2Fdef" }, "Safe base URL reference", "SAS" },
        { "credentials in URL", c => c with { EndpointReference = "https://user:pw@host.example.invalid/webhook" }, "Endpoint reference", "credentials" },
        { "personal identifier", c => c with { SyntheticIdentitySetReference = "set-12345678901" }, "Approved synthetic identity-set reference", "personal identifier" },
    };

    [Theory]
    [MemberData(nameof(Unsafe))]
    public async Task CredentialMaterial_IsRefused_WithTheFieldNamed_AndTheValueNeverEchoed(string _, Func<AltinnTestConfiguration, AltinnTestConfiguration> edit, string field, string reason)
    {
        var (saved, problem) = await SaveAsync(edit);
        saved.Should().BeNull();
        var error = problem!.Errors.Should().ContainSingle().Subject;
        error.Field.Should().Be(field);
        error.Reason.Should().StartWith($"{field} was rejected because").And.Contain(reason);
        var echoed = System.Text.Json.JsonSerializer.Serialize(problem);
        echoed.Should().NotContain(Jwt).And.NotContain("hunter2").And.NotContain("Q8x~").And.NotContain("sig=abc").And.NotContain("12345678901").And.NotContain(":pw@");
    }

    [Fact]
    public async Task CredentialProtection_CoversTheDocumentedFlowToo()
    {
        var (controller, store, db) = Create();
        await using var _ = db;
        var package = await store.GetAsync(Env);
        package = package with { Flow = MessageFlowReviewExamples.GenericFixture with { EnvironmentId = Env, Description = $"token sample {Jwt}" } };
        var result = (await controller.Save(Env, package, new Sources(), default)).Result as ObjectResult;
        var problem = result!.Value.Should().BeOfType<MessageFlowSaveProblem>().Subject;
        problem.Errors.Should().ContainSingle(error => error.Section == "Message flow" && error.Path == "flow.description");
    }

    [Fact]
    public async Task TheDocumentedExampleFlows_SaveWithEnvironmentTest()
    {
        foreach (var flow in new[] { MessageFlowReviewExamples.GenericFixture, MessageFlowReviewExamples.SkolenærværDocumented })
        {
            var (controller, store, db) = Create();
            await using var _ = db;
            var package = (await store.GetAsync(Env)) with { Flow = flow with { EnvironmentId = Env } };
            package = package with { AltinnConfiguration = package.AltinnConfiguration with { Environment = ExternalTestEnvironment.Test } };
            (await controller.Save(Env, package, new Sources(), default)).Result.Should().BeOfType<OkObjectResult>(flow.Name);
        }
    }

    [Theory]
    [InlineData("Test")]
    [InlineData("buf/skolenaervaer")]
    [InlineData("Bearer JWT")]
    [InlineData("AltinnToken")]
    [InlineData("id-porten")]
    [InlineData("maskinporten")]
    [InlineData("openid")]
    [InlineData("my-key-vault-secret-name")]
    [InlineData("https://buf.apps.tt02.altinn.no/buf/skolenaervaer")]
    [InlineData("Environment.Server.Test")]
    [InlineData("dev:http:altinn-skolenaervaer")]
    [InlineData("Buf.Meldingsutveksler.Service.Api.Controllers.AltinnWebhookController")]
    public void SafeMetadata_IsNotCredentialMaterial(string value) => MessageFlowCredentialGuard.Reason(value).Should().BeNull();

    [Fact]
    public void ApplyingSafeSourceValues_NeedsTheConfigurationPermission_NotTheActiveTestPermission()
    {
        var type = typeof(BirkNext.Api.Controllers.TestPackSourceGuidanceController);
        type.GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), true).Should().BeEmpty("no class-level execute policy");
        var apply = type.GetMethod(nameof(BirkNext.Api.Controllers.TestPackSourceGuidanceController.Apply))!;
        apply.GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), true).Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>()
            .Select(item => item.Policy).Should().Equal(BirkNext.Api.Services.ActiveEventTesting.BirkNextPermissions.IntegrationConfigurationWritePolicy);
        apply.GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute), true).Should().BeEmpty();
    }
}
