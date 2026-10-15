using System.Text.Json;
using BirkNext.Api.Data;
using BirkNext.Api.Services.AltinnTesting;
using BirkNext.Api.Services.IntegrationJourneys.Packs.SkoleNaervaer;
using BirkNext.Api.Services.Integrations;
using BirkNext.Api.Tests.Services.ActiveEventTesting;
using BirkNext.Api.Tests.Services.IntegrationJourneys;
using BirkNext.Integrations;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace BirkNext.Api.Tests.Services.AltinnTesting;

/// <summary>
/// The Skolenærvær Altinn test defaults (Environment Test, app buf/skolenaervaer): filled only where IQR has no value, saved to IQR by the
/// Skolenærvær metadata read so the read, the stored configuration and readiness agree, never applied to a generic read, and never extended to
/// client id, scopes, secrets or identities. The read stays anonymous.
/// </summary>
public sealed class SkoleNaervaerAltinnDefaultsTests
{
    private const string Env = ActiveEventTestHarness.Env;

    [Fact]
    public void Apply_FillsOnlyTheAbsentEnvironmentAndApp()
    {
        var applied = SkoleNaervaerAltinnDefaults.Apply(new AltinnTestConfiguration());
        (applied.Environment, applied.AppIdentifier).Should().Be((ExternalTestEnvironment.Test, "buf/skolenaervaer"));
        (applied.ClientReference, applied.ReporterReference, applied.SyntheticIdentitySetReference).Should().Be((null, null, null));
        applied.ConfiguredScopes.Should().BeEmpty();
        applied.VerificationState.Should().Be(FlowEvidenceState.NotAssessed);
    }

    [Theory]
    [InlineData(ExternalTestEnvironment.Test, "buf/skolenaervaer")]
    [InlineData(ExternalTestEnvironment.Test, "another/test-app")]
    [InlineData(ExternalTestEnvironment.Staging, "another/test-app")]
    public void Apply_KeepsExplicitValues(ExternalTestEnvironment environment, string app)
    {
        var applied = SkoleNaervaerAltinnDefaults.Apply(new AltinnTestConfiguration { Environment = environment, AppIdentifier = app });
        (applied.Environment, applied.AppIdentifier).Should().Be((environment, app));
    }

    [Fact]
    public void NewSkolenaervaerConfiguration_StartsWithTheDefaults_AndNothingElse()
    {
        var initial = MessageFlowReviewExamples.SkolenærværInitialConfiguration;
        (initial.Environment, initial.AppIdentifier).Should().Be((ExternalTestEnvironment.Test, "buf/skolenaervaer"));
        (initial.ClientReference, initial.ReporterReference, initial.SyntheticIdentitySetReference).Should().Be((null, null, null));
        initial.ConfiguredScopes.Should().BeEmpty();
        (initial.ReporterApprovedForTest, initial.ApprovedSyntheticData).Should().Be((false, false));
    }

    [Fact]
    public async Task SkolenaervaerRead_WithoutEnvironmentOrApp_SavesTheDefaultsToIqr_ThenReadsAnonymously()
    {
        var kit = new AltinnTestKit();
        var flows = new IntegrationJourneyHarness.FakeMessageFlows { Configuration = new AltinnTestConfiguration() };

        var view = await kit.Contracts(flows).DiscoverPublicWithSkoleNaervaerDefaultsAsync(Env, default);

        flows.Saves.Should().Be(1);
        (flows.Configuration.Environment, flows.Configuration.AppIdentifier).Should().Be((ExternalTestEnvironment.Test, "buf/skolenaervaer"));
        flows.Configuration.ClientReference.Should().BeNull("no ID-porten client id is defaulted");
        flows.Configuration.ConfiguredScopes.Should().BeEmpty();
        (flows.Configuration.ReporterReference, flows.Configuration.SyntheticIdentitySetReference).Should().Be((null, null));
        view.ConfiguredAppId.Should().Be("buf/skolenaervaer");
        view.ConfiguredEnvironment.Should().Be(ExternalTestEnvironment.Test);
        view.AppApproved.Should().BeTrue();
        view.Complete.Should().BeTrue(string.Join(" | ", view.Missing));
        kit.Http.Calls.Should().NotBeEmpty().And.OnlyContain(call => call.Method == HttpMethod.Get && call.Authorization == null, "metadata discovery stays anonymous and read-only");
        kit.Http.Calls.Should().NotContain(call => call.Uri.Host.Contains("idporten", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SkolenaervaerRead_WithAConfiguredEnvironmentAndApp_ChangesNothing()
    {
        var kit = new AltinnTestKit();
        var configured = AltinnTestKit.ReadyConfiguration;
        var flows = new IntegrationJourneyHarness.FakeMessageFlows { Configuration = configured };

        await kit.Contracts(flows).DiscoverPublicWithSkoleNaervaerDefaultsAsync(Env, default);

        flows.Saves.Should().Be(0);
        flows.Configuration.Should().BeSameAs(configured);
    }

    [Fact]
    public async Task SkolenaervaerRead_NeverReplacesAnExplicitOtherApp()
    {
        var kit = new AltinnTestKit();
        var flows = new IntegrationJourneyHarness.FakeMessageFlows { Configuration = new AltinnTestConfiguration { AppIdentifier = "another/test-app" } };

        await kit.Contracts(flows).Invoking(contracts => contracts.DiscoverPublicWithSkoleNaervaerDefaultsAsync(Env, default))
            .Should().ThrowAsync<AltinnContractException>("the explicit app is kept, and it is not an approved app");

        flows.Saves.Should().Be(0, "nothing is saved when the effective app cannot be read");
        flows.Configuration.AppIdentifier.Should().Be("another/test-app");
        kit.Http.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task SkolenaervaerRead_KeepsAnExplicitNonTt02Environment_AndSavesNothing()
    {
        var kit = new AltinnTestKit();
        var flows = new IntegrationJourneyHarness.FakeMessageFlows { Configuration = new AltinnTestConfiguration { Environment = ExternalTestEnvironment.Staging } };

        await kit.Contracts(flows).Invoking(contracts => contracts.DiscoverPublicWithSkoleNaervaerDefaultsAsync(Env, default))
            .Should().ThrowAsync<AltinnContractException>().WithMessage("*Staging*");

        flows.Saves.Should().Be(0);
        (flows.Configuration.Environment, flows.Configuration.AppIdentifier).Should().Be((ExternalTestEnvironment.Staging, null));
        kit.Http.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task GenericRead_NeverAppliesTheSkolenaervaerDefaults()
    {
        var kit = new AltinnTestKit();
        var flows = new IntegrationJourneyHarness.FakeMessageFlows { Configuration = new AltinnTestConfiguration() };

        await kit.Contracts(flows).Invoking(contracts => contracts.DiscoverPublicAsync(Env, default)).Should().ThrowAsync<AltinnContractException>();

        flows.Saves.Should().Be(0);
        (flows.Configuration.Environment, flows.Configuration.AppIdentifier).Should().Be((ExternalTestEnvironment.Unknown, null));
    }

    [Fact]
    public async Task SkolenaervaerRead_WithTheRealStore_PersistsTheDefaults_KeepsOtherValues_AndStoresNoSecret()
    {
        var kit = new AltinnTestKit();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var store = new IntegrationMessageFlowStore(db);
        var blank = await store.GetAsync(Env);
        (await store.SaveAsync(Env, blank with { AltinnConfiguration = new AltinnTestConfiguration { ClientReference = "existing-client-ref", ConfiguredScopes = ["openid"] } })).Error.Should().BeNull();
        var contracts = new AltinnAppContractService(kit.Factory, kit.Policy, store, new IntegrationJourneyHarness.FakeSources(), new SkoleNaervaerAltinnForm(), TimeProvider.System,
            new BirkNext.Api.Services.ContractAnalysis.OpenApiExtractor(NullLogger<BirkNext.Api.Services.ContractAnalysis.OpenApiExtractor>.Instance));

        var view = await contracts.DiscoverPublicWithSkoleNaervaerDefaultsAsync(Env, default);

        var stored = await store.GetAsync(Env);
        (stored.AltinnConfiguration.Environment, stored.AltinnConfiguration.AppIdentifier).Should().Be((ExternalTestEnvironment.Test, "buf/skolenaervaer"));
        stored.AltinnConfiguration.ClientReference.Should().Be("existing-client-ref");
        stored.AltinnConfiguration.ConfiguredScopes.Should().Equal("openid");
        stored.AltinnConfiguration.VerifiedScopes.Should().BeEmpty("configured scopes are not verified");
        stored.AltinnConfiguration.VerificationState.Should().Be(FlowEvidenceState.NotAssessed);
        stored.AppContract!.PublicMetadata.Should().NotBeNull("the read's contract evidence is kept with the saved configuration");
        view.Complete.Should().BeTrue(string.Join(" | ", view.Missing));
        JsonSerializer.Serialize(stored).Should().NotContainAny(AltinnTestKit.ClientSecret, AltinnTestKit.ReporterIdentifier, "Bearer ");

        var again = await contracts.DiscoverPublicWithSkoleNaervaerDefaultsAsync(Env, default);
        again.ConfiguredAppId.Should().Be("buf/skolenaervaer");
    }
}
