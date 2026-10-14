using System.Text.Json;
using System.Text.RegularExpressions;
using BirkNext.Api.Services.AltinnTesting;
using BirkNext.Api.Services.IntegrationJourneys;
using BirkNext.Api.Services.IntegrationJourneys.Packs.SkoleNaervaer;
using BirkNext.Api.Tests.Services.ActiveEventTesting;
using BirkNext.Api.Tests.Services.AltinnTesting;
using BirkNext.Integrations;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Api.Tests.Services.IntegrationJourneys;

/// <summary>
/// The scenario capability view: every scenario of the existing catalog with an execution status computed from current readiness (Runnable,
/// Conditionally runnable, Operator required, Not assessed, Blocked) and, separately, how far its result can be verified. Delegated CDC rows come
/// from the Active Event provider's own catalog. Nothing is executed, sent or upgraded to a pass.
/// </summary>
public sealed class ScenarioCapabilityTests
{
    private const string Env = ActiveEventTestHarness.Env;

    private static IntegrationDefinition AltinnIntake => new()
    {
        Id = "dev:http:altinn-skolenaervaer", EnvironmentId = Env, DisplayName = "Skolenærvær – Altinn report intake", Kind = IntegrationKind.HttpApi,
        Enabled = true, Producer = "Altinn (TT02)", Consumer = new IntegrationConsumer { DisplayName = "MU" },
    };

    [Fact]
    public async Task EveryCatalogScenario_HasOneCapability_WithItsDeclaredProfile_AndTheStatusFollowsCurrentReadiness()
    {
        await using var harness = new IntegrationJourneyHarness();
        var pack = Pack(harness, new AltinnTestKit());
        var views = await Views(harness, pack);

        foreach (var journey in pack.Journeys.Where(item => item.ExecutionMode == JourneyExecutionMode.JourneyRunner))
        {
            var capabilities = views[journey.JourneyId].ScenarioCapabilities;
            capabilities.Select(item => item.ScenarioId).Should().Equal(journey.Scenarios.Select(item => item.ScenarioId), "scenario identity is the catalog's; nothing is added or invented");
            capabilities.Should().OnlyContain(item => item.Profile != null && item.Profile.ResponsibleTeam.Length > 0 && item.Profile.NextAction.Length > 0);
        }
        var report = views[SkoleNaervaerJourneyPack.RapportmottakId].ScenarioCapabilities.ToDictionary(item => item.ScenarioId);
        report["valid-report"].Status.Should().Be(JourneyScenarioCapabilityStatus.Blocked, "the Altinn app contract is not read and no executor is registered here");
        report["valid-report"].Blockers.Should().Contain(item => item.StartsWith("Reporter authentication"));
        report["valid-report"].Profile!.Verification.Should().Be(JourneyResultVerification.Partial, "execution capability and result verification are separate");
        report["person-not-found"].Profile!.Verification.Should().Be(JourneyResultVerification.NotVerifiable);
        foreach (var id in new[] { "correction", "multiple-reporters", "invalid-xsd", "period-locked", "mapping-validation", "duplicate-report", "retrieval-decrypt-failure" })
            report[id].Status.Should().Be(JourneyScenarioCapabilityStatus.NotAssessed, id);

        var closure = views[SkoleNaervaerJourneyPack.PeriodeavslutningId].ScenarioCapabilities.ToDictionary(item => item.ScenarioId);
        closure["close-valid-period"].Status.Should().Be(JourneyScenarioCapabilityStatus.OperatorRequired);
        closure["close-valid-period"].StatusDetail.Should().Contain("only postpones");
        closure.Values.Where(item => item.ScenarioId != "close-valid-period").Should().OnlyContain(item => item.Status == JourneyScenarioCapabilityStatus.NotAssessed);
    }

    [Fact]
    public async Task AFullyConfiguredAltinnTarget_MakesTheSupportedReportScenariosRunnable_ButNeverMoreVerifiableThanDeclared()
    {
        var kit = new AltinnTestKit();
        await using var harness = await ReadyAsync(kit, enrol: true);
        var view = (await Views(harness, Pack(harness, kit)))[SkoleNaervaerJourneyPack.RapportmottakId];
        var report = view.ScenarioCapabilities.ToDictionary(item => item.ScenarioId);

        report["valid-report"].Status.Should().Be(JourneyScenarioCapabilityStatus.Runnable);
        report["valid-report"].StatusDetail.Should().Contain("explicit confirmation");
        report["valid-report"].Profile!.Verification.Should().Be(JourneyResultVerification.Partial, "runnable ≠ verifiable: MU, MM and Skoletjenesten stay unobserved");
        view.RunnableScenarios.Should().Contain("valid-report", "the capability view agrees with the existing run gate");
        report.Values.Where(item => item.Status == JourneyScenarioCapabilityStatus.Runnable)
            .Select(item => item.ScenarioId).Should().BeSubsetOf(view.RunnableScenarios, "a scenario is never shown runnable that the run gate would refuse");
        report["correction"].Status.Should().Be(JourneyScenarioCapabilityStatus.NotAssessed, "configuration never upgrades a Not assessed scenario");
    }

    [Fact]
    public async Task OnlyMissingConfiguration_MakesAScenarioConditionallyRunnable_AndNamesWhatToConfigure()
    {
        var kit = new AltinnTestKit();
        await using var harness = await ReadyAsync(kit, enrol: false);
        var report = (await Views(harness, Pack(harness, kit)))[SkoleNaervaerJourneyPack.RapportmottakId].ScenarioCapabilities.ToDictionary(item => item.ScenarioId);

        report["valid-report"].Status.Should().Be(JourneyScenarioCapabilityStatus.ConditionallyRunnable);
        report["valid-report"].StatusDetail.Should().StartWith("Runs once configured:").And.Contain("report");
    }

    [Fact]
    public async Task CdcScenarios_AreTheProvidersOwnCatalog_CreateReplayAndInvalidThenValidSupported_TheRestNotAssessed_AndNothingIsSent()
    {
        await using var harness = new IntegrationJourneyHarness();
        var provider = harness.Active.Skole();
        var cdc = (await Views(harness, Pack(harness, new AltinnTestKit())))[SkoleNaervaerJourneyPack.UtdanningsdataId].ScenarioCapabilities;

        cdc.Select(item => item.ScenarioId).Should().Equal(provider.CatalogScenarios.Select(item => item.ScenarioId), "the CDC rows are the provider catalog, not a second list");
        var supported = cdc.Where(item => item.Status != JourneyScenarioCapabilityStatus.NotAssessed).ToArray();
        supported.Select(item => Regex.Replace(item.ScenarioId, "^.*\\.", "")).Distinct().Should().BeEquivalentTo(["create", "exact-replay", "invalid-then-valid"]);
        supported.Should().OnlyContain(item => item.Status == JourneyScenarioCapabilityStatus.ConditionallyRunnable && item.StatusDetail.Contains("Event Hub integration"),
            "without a configured Education CDC integration they run only once it is configured");
        cdc.Where(item => item.Status == JourneyScenarioCapabilityStatus.NotAssessed).Select(item => Regex.Replace(item.ScenarioId, "^.*\\.", "")).Distinct()
            .Should().BeEquivalentTo(["update", "delete", "tombstone", "snapshot-read", "natural-key-duplicate", "unknown-child-late-linkage"]);
        cdc.Should().OnlyContain(item => item.Profile != null && item.Profile.Verification == JourneyResultVerification.Partial && item.Resource != null);
        harness.Active.Producers.Sent.Should().BeEmpty("reading capabilities never sends an event");
    }

    [Fact]
    public async Task EvidenceLimitations_ShowTheSevenKnownGaps_AndTheAltinnOneFollowsTheConfiguration()
    {
        await using var harness = new IntegrationJourneyHarness();
        var limitations = (await harness.Service(Pack(harness, new AltinnTestKit())).PacksAsync(Env, default)).Single().EvidenceLimitations.ToDictionary(item => item.Key);

        limitations.Keys.Should().BeEquivalentTo(["altinn-tt02", "mm-skole-contract", "skoleadapter-source", "period-lock", "bufdata-delivery", "graphql-coverage", "target-system-tests"]);
        limitations["altinn-tt02"].Status.Should().StartWith("Blocked: ID-porten TEST configuration incomplete");
        limitations["altinn-tt02"].Detail.Should().Contain("ID-porten TEST client").And.Contain("Client secret").And.Contain("Reporter Altinn profile")
            .And.Contain("never invents");
        limitations["mm-skole-contract"].Status.Should().Be("Not runtime verified");
        limitations["skoleadapter-source"].Status.Should().Be("Not available");
        limitations["period-lock"].Status.Should().Be("Operator required");
        limitations["bufdata-delivery"].Detail.Should().Contain("Klargjort");
        limitations["graphql-coverage"].Status.Should().Be("Needs an approved client and role");
        limitations["target-system-tests"].Status.Should().Be("Discovered, not executed");
        JsonSerializer.Serialize(limitations).Should().NotContainAny("Passed", "\"Verified\"");

        var kit = new AltinnTestKit();
        await using var ready = await ReadyAsync(kit, enrol: true);
        (await ready.Service(Pack(ready, kit)).PacksAsync(Env, default)).Single().EvidenceLimitations.Single(item => item.Key == "altinn-tt02")
            .Status.Should().Be("Configured, not verified", "configured is never shown as verified");
    }

    [Fact]
    public void CapabilityProfiles_ContainNoPersonalDataOrSecrets()
    {
        var json = JsonSerializer.Serialize(Pack(new IntegrationJourneyHarness(), new AltinnTestKit()).Journeys.SelectMany(item => item.Scenarios).Select(item => item.Capability));
        Regex.IsMatch(json, @"\b\d{6,}\b").Should().BeFalse();
        json.Should().NotContain("@").And.NotContainAny("secret=", "password", "Bearer ");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────────────────────────

    private static async Task<Dictionary<string, IntegrationJourneyView>> Views(IntegrationJourneyHarness harness, SkoleNaervaerJourneyPack pack) =>
        (await harness.Service(pack).PacksAsync(Env, default)).Single().Journeys.ToDictionary(view => view.Journey.JourneyId);

    /// <summary>A trusted DEV target with the Altinn executor, unattended reporter and the public contract read (fake HTTP; no network).</summary>
    private static async Task<IntegrationJourneyHarness> ReadyAsync(AltinnTestKit kit, bool enrol)
    {
        var harness = new IntegrationJourneyHarness();
        if (enrol)
        {
            harness.Active.Catalog.Extra.Add(AltinnIntake);
            harness.Enroll(AltinnIntake.Id);
        }
        harness.Executors.Add(new SkoleNaervaerAltinnExecutor(new ServiceCollection()
            .AddSingleton<BirkNext.Api.Services.Integrations.IIntegrationMessageFlowStore>(harness.MessageFlows)
            .AddTransient(_ => kit.Policy)
            .AddTransient(_ => kit.Contracts(harness.MessageFlows, harness.Sources))
            .BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), kit.Authenticator(harness.Tokens), new AltinnAppApiClient(kit.Factory), TimeProvider.System));
        kit.ConfigureUnattended();
        harness.MessageFlows.Configuration = AltinnTestKit.ReadyConfiguration;
        await kit.Contracts(harness.MessageFlows, harness.Sources).DiscoverPublicAsync(Env, default);
        kit.Http.Calls.Clear();
        return harness;
    }

    private static SkoleNaervaerJourneyPack Pack(IntegrationJourneyHarness harness, AltinnTestKit kit) => new(harness.MessageFlows, kit.Policy, kit.Contracts(harness.MessageFlows, harness.Sources));
}
