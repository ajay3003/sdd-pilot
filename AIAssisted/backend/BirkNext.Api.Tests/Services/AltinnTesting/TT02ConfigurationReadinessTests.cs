using System.Text.Json;
using BirkNext.Api.Services.AltinnTesting;
using BirkNext.Api.Services.IntegrationJourneys;
using BirkNext.Api.Services.IntegrationJourneys.Packs.SkoleNaervaer;
using BirkNext.Api.Tests.Services.ActiveEventTesting;
using BirkNext.Api.Tests.Services.IntegrationJourneys;
using BirkNext.Integrations;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Api.Tests.Services.AltinnTesting;

/// <summary>
/// The configuration for the first supervised TT02 run: the committed app-policy confirmation (bound from the real appsettings.json), its tie
/// to buf/skolenaervaer in TT02, the reporter ≠ child guard, the separate readiness rows and the run's stated effects. No network call.
/// </summary>
public sealed class TT02ConfigurationReadinessTests
{
    private const string Env = ActiveEventTestHarness.Env;
    private const string Actor = "local-session:loopback";
    private const string AppId = "buf/skolenaervaer";
    private const string PolicyHash = "f8f418aa794af4f79403f578c5d29356b3b92fdb1d40bd077f722e511ee2b370";

    /// <summary>The backend's committed appsettings.json, bound exactly as the API binds it.</summary>
    private static AltinnTestingOptions CommittedOptions()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "BirkNext.Api", "appsettings.json"))) directory = directory.Parent;
        directory.Should().NotBeNull("the backend folder holds BirkNext.Api/appsettings.json");
        var configuration = new ConfigurationBuilder().AddJsonFile(Path.Combine(directory!.FullName, "BirkNext.Api", "appsettings.json")).Build();
        return configuration.GetSection(AltinnTestingOptions.Section).Get<AltinnTestingOptions>()!;
    }

    private static AltinnAppPolicyConfirmation Verified() => new()
    {
        AppId = AppId, Environment = "TT02", Reference = "TT02 Resource Registry (test)", ConfirmedBy = "audit (test)", ConfirmedOn = new DateOnly(2026, 10, 13),
        PolicySource = "https://platform.tt02.altinn.no/resourceregistry/api/v1/resource/app_buf_skolenaervaer/policy", PolicySha256 = PolicyHash,
        EndUserRoles = ["PRIV", "DAGL"], EndUserActions = ["read", "write", "delete", "instantiate"], MinimumAuthenticationLevel = 1,
    };

    [Fact]
    public void TheCommittedConfirmation_IsTheVerifiedBufSkolenaervaerTt02Policy_AndAllowsAPrivReporter()
    {
        var options = CommittedOptions();
        options.ApprovedApps.Should().Equal(AppId);
        var confirmation = new AltinnTestingPolicy(options).AppPolicy(AppId, out var detail);

        confirmation.Should().NotBeNull(detail);
        confirmation!.Environment.Should().Be("TT02");
        confirmation.PolicySource.Should().Be("https://platform.tt02.altinn.no/resourceregistry/api/v1/resource/app_buf_skolenaervaer/policy");
        confirmation.PolicySha256.Should().Be(PolicyHash);
        confirmation.EndUserRoles.Should().Equal("PRIV", "DAGL");
        confirmation.EndUserActions.Should().Contain(["instantiate", "write"]);
        confirmation.MinimumAuthenticationLevel.Should().Be(1);
        AltinnTestingPolicy.PolicyProblem(confirmation, "PRIV").Should().BeNull();
        detail.Should().Contain("end-user roles PRIV, DAGL").And.Contain("minimum authentication level 1").And.Contain("policy sha256 f8f418aa794a…");
        options.ConfirmedAppPolicies.Should().ContainSingle("no wildcard or second app is confirmed");
    }

    [Fact]
    public void TheCommittedConfiguration_HoldsNoSecretReporterIdentityOrEnablement()
    {
        var options = CommittedOptions();
        options.UnattendedIdPortenTest.Enabled.Should().BeFalse("enabling is an installation decision, made outside committed files");
        options.UnattendedIdPortenTest.ClientSecret.Should().BeNullOrEmpty();
        options.IdPortenTest.ClientSecret.Should().BeNullOrEmpty();
        options.UnattendedIdPortenTest.Reporters.Should().BeEmpty("a reporter's TestID identifier is configured outside committed files");
        options.MaxProcessTransitions.Should().Be(1);
    }

    [Fact]
    public void AnotherApp_IsNotConfirmed()
    {
        var policy = new AltinnTestingPolicy(new AltinnTestingOptions { ApprovedApps = [AppId], ConfirmedAppPolicies = [Verified()] });
        policy.AppPolicy("buf/other-app", out var detail).Should().BeNull();
        detail.Should().Contain("not confirmed");
        new AltinnTestingPolicy(new AltinnTestingOptions()).AppPolicy(AppId, out var missing).Should().BeNull("a missing confirmation is never assumed");
        missing.Should().Contain("not confirmed");
    }

    [Theory]
    [InlineData("Production", "not TT02")]
    [InlineData("TT", "not TT02")]
    [InlineData("", "not TT02")]
    public void AConfirmationForAnotherEnvironment_IsIgnored(string environment, string reason)
    {
        var confirmation = Verified();
        confirmation.Environment = environment;
        new AltinnTestingPolicy(new AltinnTestingOptions { ConfirmedAppPolicies = [confirmation] }).AppPolicy(AppId, out var detail).Should().BeNull();
        detail.Should().Contain(reason);
    }

    [Theory]
    [InlineData("https://platform.altinn.no/resourceregistry/api/v1/resource/app_buf_skolenaervaer/policy")]
    [InlineData("https://platform.tt02.altinn.no/resourceregistry/api/v1/resource/app_buf_other/policy")]
    [InlineData("https://evil.example/app_buf_skolenaervaer/policy")]
    public void APolicySourceThatIsNotTheAppsTt02Policy_IsRejected(string source)
    {
        var confirmation = Verified();
        confirmation.PolicySource = source;
        new AltinnTestingPolicy(new AltinnTestingOptions { ConfirmedAppPolicies = [confirmation] }).AppPolicy(AppId, out var detail).Should().BeNull();
        detail.Should().Contain("not buf/skolenaervaer's TT02 Resource Registry policy");
    }

    [Fact]
    public void AMalformedPolicyHash_IsRejected()
    {
        var confirmation = Verified();
        confirmation.PolicySha256 = "not-a-hash";
        new AltinnTestingPolicy(new AltinnTestingOptions { ConfirmedAppPolicies = [confirmation] }).AppPolicy(AppId, out var detail).Should().BeNull();
        detail.Should().Contain("not a SHA-256 hex digest");
    }

    [Fact]
    public void AnOrganisationOnlyPolicy_DoesNotSatisfyAPersonRun_AndAMissingWriteBlocks()
    {
        var organisationOnly = Verified();
        organisationOnly.EndUserRoles = ["DAGL"];
        AltinnTestingPolicy.PolicyProblem(organisationOnly, "PRIV").Should().Contain("not PRIV");
        var noWrite = Verified();
        noWrite.EndUserActions = ["read", "instantiate"];
        AltinnTestingPolicy.PolicyProblem(noWrite, "PRIV").Should().Contain("write");
    }

    // ── Reporter ≠ child ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheReportersOwnSyntheticPerson_CannotBeTheChild_InTheDryRunOrAtExecution()
    {
        var kit = new AltinnTestKit();
        await using var harness = Harness(kit);
        await kit.ConfigureReadyAsync(harness);
        kit.Http.Calls.Clear();
        var inputs = Report(AltinnTestKit.ReporterIdentifier);

        var validation = await Service(harness, kit).ValidateAsync(Run(inputs), default);
        validation.Valid.Should().BeFalse();
        validation.Checks.Single(c => c.Key == "reporter-child").Detail.Should().Contain("must not be the reporter's own synthetic person");
        JsonSerializer.Serialize(validation).Should().NotContain(AltinnTestKit.ReporterIdentifier);

        var journey = Pack(harness, kit).Journeys.Single(item => item.JourneyId == "rapportmottak");
        var trusted = BirkNext.Api.Services.ActiveEventTesting.TrustedExecutionEnvironmentRegistry.From(harness.Active.Configuration()).Resolve(Env, out _);
        var prepared = SkoleNaervaerReportInput.Prepare(journey.Scenarios[0], inputs);
        var result = await Executor(harness, kit).ExecuteAsync(new JourneyStepContext(Guid.NewGuid(), Env, trusted, journey, journey.Scenarios[0], journey.Steps[0], null, [], prepared.Values, Actor), default);
        result.State.Should().Be(JourneyStepState.Unavailable);
        result.Reason.Should().Contain("must not be the reporter's own synthetic person");
        kit.Http.Calls.Should().BeEmpty("nothing is authenticated or sent");

        var different = await Service(harness, kit).ValidateAsync(Run(Report(SyntheticPersonIdentifier.Generate())), default);
        different.Checks.Single(c => c.Key == "reporter-child").Passed.Should().BeTrue();
    }

    // ── Readiness rows and the run's stated effects ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Readiness_SeparatesClientRedirectSecretReporterProfile_KnownChildAndTheMeldingshodeRisk()
    {
        var kit = new AltinnTestKit();
        await using var harness = Harness(kit);
        await kit.ConfigureReadyAsync(harness);
        var ready = await Rapportmottak(harness, kit);
        Row(ready, "idporten-client").State.Should().Be(JourneyReadinessState.Ready);
        Row(ready, "redirect-uri").State.Should().Be(JourneyReadinessState.Ready);
        Row(ready, "redirect-uri").Detail.Should().NotContain(AltinnTestKit.HeadlessRedirect, "the redirect value is not echoed");
        Row(ready, "synthetic-data").Label.Should().Be("Known synthetic child (test data)");
        Row(ready, "synthetic-data").Detail.Should().StartWith("Declared test prerequisite").And.Contain("not verified by BirkNext");
        Row(ready, "meldingshode").Mandatory.Should().BeFalse("a first-run risk is not a missing prerequisite");
        Row(ready, "meldingshode").State.Should().Be(JourneyReadinessState.Partial);
        Row(ready, "meldingshode").Detail.Should().StartWith("Risk, not a blocker").And.Contain("Meldingshode");
        ready.RunnableScenarios.Should().Contain("valid-report", "the risk does not block");

        kit.Options.UnattendedIdPortenTest.RedirectUri = null;
        kit.Options.IdPortenTest.RedirectUri = null;
        var noRedirect = await Rapportmottak(harness, kit);
        Row(noRedirect, "redirect-uri").State.Should().Be(JourneyReadinessState.NotConfigured);
        Row(noRedirect, "idporten-client").State.Should().Be(JourneyReadinessState.Ready, "the client and the redirect are separate facts");
        noRedirect.RunnableScenarios.Should().NotContain("valid-report");
    }

    [Fact]
    public void SubmissionScenarios_StateTheirRealEffects_ForTheConfirmation()
    {
        var journey = Pack(new IntegrationJourneyHarness(), new AltinnTestKit()).Journeys.Single(item => item.JourneyId == "rapportmottak");
        foreach (var id in SkoleNaervaerJourneyPack.SubmissionScenarios)
        {
            var effects = journey.Scenarios.Single(item => item.ScenarioId == id).ExecutionEffects;
            effects.Should().NotBeNull();
            string.Join(" ", effects!).Should().Contain("real external call to Altinn TT02").And.Contain("Create one new instance").And.Contain("at most one process transition")
                .And.Contain("No automatic retry").And.Contain("Production is never called");
        }
        journey.Scenarios.Single(item => item.ScenarioId == SkoleNaervaerJourneyPack.InstanceEvidenceScenario).ExecutionEffects.Should().BeNull();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────────────────────────────

    private static JourneyPrerequisite Row(IntegrationJourneyView view, string key) => view.Prerequisites.Single(item => item.Key == key);

    private static Dictionary<string, string> Report(string child) => new()
    {
        ["reporter"] = AltinnTestKit.ReporterAlias, [SkoleNaervaerReportInput.IdentitySourceKey] = "registered", [SkoleNaervaerAltinnForm.ChildIdentifier] = child,
        [SkoleNaervaerAltinnForm.PeriodFrom] = "2026-10-01", [SkoleNaervaerAltinnForm.PeriodTo] = "2026-10-31",
        [SkoleNaervaerAltinnForm.PlannedHours] = "100", [SkoleNaervaerAltinnForm.ActualHours] = "92",
    };

    private static IntegrationJourneyRunRequest Run(IReadOnlyDictionary<string, string> inputs) => new()
    {
        EnvironmentId = Env, PackId = "skolenaervaer", JourneyId = "rapportmottak", ScenarioId = "valid-report", Confirmed = false, Inputs = inputs,
    };

    private static async Task<IntegrationJourneyView> Rapportmottak(IntegrationJourneyHarness harness, AltinnTestKit kit) =>
        (await Service(harness, kit).PacksAsync(Env, default)).Single().Journeys.Single(item => item.Journey.JourneyId == "rapportmottak");

    private static SkoleNaervaerJourneyPack Pack(IntegrationJourneyHarness harness, AltinnTestKit kit) => new(harness.MessageFlows, kit.Policy, kit.Contracts(harness.MessageFlows, harness.Sources));

    private static SkoleNaervaerAltinnExecutor Executor(IntegrationJourneyHarness harness, AltinnTestKit kit) => new(new ServiceCollection()
        .AddSingleton<BirkNext.Api.Services.Integrations.IIntegrationMessageFlowStore>(harness.MessageFlows)
        .AddTransient(_ => kit.Policy)
        .AddTransient(_ => kit.Contracts(harness.MessageFlows, harness.Sources))
        .BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), kit.Authenticator(harness.Tokens), new AltinnAppApiClient(kit.Factory), TimeProvider.System);

    private static IntegrationJourneyHarness Harness(AltinnTestKit kit)
    {
        var harness = new IntegrationJourneyHarness();
        harness.Active.Catalog.Extra.Add(new IntegrationDefinition
        {
            Id = "dev:http:altinn-skolenaervaer", EnvironmentId = Env, DisplayName = "Skolenærvær – Altinn report intake", Kind = IntegrationKind.HttpApi,
            Enabled = true, Producer = "Altinn (TT02)", Consumer = new IntegrationConsumer { DisplayName = "MU" },
        });
        harness.Enroll("dev:http:altinn-skolenaervaer");
        harness.Executors.Add(Executor(harness, kit));
        return harness;
    }

    private static IntegrationJourneyService Service(IntegrationJourneyHarness harness, AltinnTestKit kit) => harness.Service(Pack(harness, kit));
}
