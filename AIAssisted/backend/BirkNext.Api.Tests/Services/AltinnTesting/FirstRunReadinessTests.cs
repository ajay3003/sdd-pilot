using System.Text.Json;
using System.Text.RegularExpressions;
using BirkNext.Api.Services.AltinnTesting;
using BirkNext.Api.Services.IntegrationJourneys;
using BirkNext.Api.Services.IntegrationJourneys.Packs.SkoleNaervaer;
using BirkNext.Api.Tests.Services.ActiveEventTesting;
using BirkNext.Api.Tests.Services.IntegrationJourneys;
using BirkNext.Integrations;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Api.Tests.Services.AltinnTesting;

/// <summary>
/// Readiness for the first supervised unattended TT02 run: the reporter's Altinn profile, the app policy's verified facts checked against the
/// reporter's role, and the dry run ("Validate run") that checks everything the executor would do before its first call — without any network
/// call, token, instance or history. All HTTP goes to the in-process fakes, and the dry-run tests assert that none happens.
/// </summary>
public sealed class FirstRunReadinessTests
{
    private const string Env = ActiveEventTestHarness.Env;
    private const string Actor = "local-session:loopback";

    /// <summary>The facts of the deployed buf/skolenaervaer TT02 policy (end-user rule), as recorded in a confirmation. Synthetic hash.</summary>
    private static AltinnAppPolicyConfirmation VerifiedPolicy(params string[] roles) => new()
    {
        AppId = AltinnTestKit.AppId, Reference = "TT02 Resource Registry policy (test)", ConfirmedBy = "audit (test)", ConfirmedOn = new DateOnly(2026, 10, 13),
        PolicySha256 = new string('a', 64), EndUserRoles = roles.Length > 0 ? [.. roles] : ["PRIV", "DAGL"], EndUserActions = ["read", "write", "delete", "instantiate"],
        MinimumAuthenticationLevel = 1,
    };

    [Fact]
    public void AReporterWithoutAConfirmedAltinnProfile_IsNotOffered_AndSaysWhy()
    {
        var kit = new AltinnTestKit().ConfigureUnattended(profile: false);
        var reporter = kit.Options.UnattendedIdPortenTest.Reporters.Single();
        AltinnTestingPolicy.ReporterProblem(reporter).Should().Contain("Altinn TT02 profile").And.NotContain(AltinnTestKit.ReporterIdentifier);
        kit.Policy.Reporter(AltinnTestKit.ReporterAlias, out var reason).Should().BeNull();
        reason.Should().Contain("profile");
        reporter.AltinnRole = "DAGL";
        reporter.AltinnProfileConfirmed = true;
        AltinnTestingPolicy.ReporterProblem(reporter).Should().Contain("only the Altinn role PRIV", "a run's instance owner is the reporter's own party");
    }

    [Fact]
    public async Task Readiness_ShowsTheReporterProfile_AndTheVerifiedPolicyFacts_OnTheirOwnRows()
    {
        var kit = new AltinnTestKit();
        await using var harness = Harness(kit);
        await kit.ConfigureReadyAsync(harness);
        kit.Options.ConfirmedAppPolicies = [VerifiedPolicy()];
        kit.Options.UnattendedIdPortenTest.Reporters[0].AltinnProfileConfirmed = false;

        var view = await Rapportmottak(harness, kit);
        Row(view, "reporter-profile").State.Should().Be(JourneyReadinessState.NotConfigured);
        Row(view, "reporter-profile").Detail.Should().Contain("Not confirmed for 1");
        Row(view, "synthetic-reporter").State.Should().Be(JourneyReadinessState.Ready, "configured is not confirmed: the alias is configured, its profile is not yet confirmed");
        Row(view, "reporter-role").Detail.Should().StartWith("PRIV");
        Row(view, "reporter-auth").Detail.Should().StartWith("Only the interactive fallback", "an unconfirmed profile keeps unattended authentication from being ready");
        var policy = Row(view, "app-policy");
        policy.State.Should().Be(JourneyReadinessState.Ready);
        policy.Detail.Should().Contain("end-user roles PRIV, DAGL").And.Contain("end-user actions read, write, delete, instantiate").And.Contain("minimum authentication level 1");

        kit.Options.UnattendedIdPortenTest.Reporters[0].AltinnProfileConfirmed = true;
        Row(await Rapportmottak(harness, kit), "reporter-profile").State.Should().Be(JourneyReadinessState.Ready);
    }

    [Fact]
    public async Task AReporterConfigurationProblem_IsNamed_NotReportedAsMissing()
    {
        var kit = new AltinnTestKit();
        await using var harness = Harness(kit);
        await kit.ConfigureReadyAsync(harness);
        kit.Options.UnattendedIdPortenTest.Reporters[0].AltinnRole = "DAGL";
        var view = await Rapportmottak(harness, kit);
        Row(view, "synthetic-reporter").Detail.Should().Contain("only the Altinn role PRIV").And.NotContain(AltinnTestKit.ReporterIdentifier);
        Row(view, "reporter-role").State.Should().Be(JourneyReadinessState.NotConfigured);
        Row(view, "reporter-profile").Detail.Should().Contain("Not applicable", "a profile is only confirmable for a usable reporter");

        kit.Options.UnattendedIdPortenTest.Reporters.Clear();
        Row(await Rapportmottak(harness, kit), "synthetic-reporter").Detail.Should().Contain("Test Data → session test identities").And.NotContain("PowerShell");
    }

    [Fact]
    public async Task PublicTt02Metadata_IsMissing_UntilRead_ThenLoaded_WithoutClaimingMore()
    {
        var kit = new AltinnTestKit();
        await using var harness = Harness(kit);
        await kit.ConfigureReadyAsync(harness);
        var missing = Row(await Rapportmottak(harness, kit), "public-metadata");
        missing.State.Should().Be(JourneyReadinessState.NotConfigured);
        missing.Detail.Should().Contain("Read public TT02 metadata").And.Contain("restarting the backend");
        missing.Mandatory.Should().BeFalse("the app source can establish the contract instead");

        await kit.Contracts(harness.MessageFlows, harness.Sources).DiscoverPublicAsync(Env, default);
        var loaded = Row(await Rapportmottak(harness, kit), "public-metadata");
        loaded.State.Should().Be(JourneyReadinessState.Ready);
        loaded.Detail.Should().StartWith("Loaded").And.Contain(AltinnTestKit.AppId).And.Contain("anonymous reads");
        kit.Http.Calls.Should().OnlyContain(c => c.Method == HttpMethod.Get && c.Authorization == null, "public metadata is read anonymously");
    }

    [Theory]
    [InlineData("1.0.0+8c68d872de7d2d7079f64fd93d2993d4400f53e5", "Running commit 8c68d872.")]
    [InlineData("1.0.0", "Running version 1.0.0 (no commit recorded).")]
    [InlineData("1.0.0+local", "Running version 1.0.0+local (no commit recorded).")]
    public void TheBackendBuild_ShowsTheRunningCommit_SoAStaleBackendIsVisible(string version, string expected) =>
        BackendBuild.Describe(version).Should().Be(expected);

    [Fact]
    public async Task TheBackendBuildRow_IsInformational_AndTellsTheTesterToRestart()
    {
        var kit = new AltinnTestKit();
        await using var harness = Harness(kit);
        await kit.ConfigureReadyAsync(harness);
        var row = Row(await Rapportmottak(harness, kit), "backend-build");
        row.Mandatory.Should().BeFalse();
        row.Detail.Should().StartWith("Running ").And.Contain("Restart the backend after changing branch, commit or configuration");
    }

    [Theory]
    [InlineData("roles-without-priv")]
    [InlineData("actions-without-write")]
    public async Task APolicyThatDoesNotAllowTheReportersRoleOrActions_BlocksSubmission_EvenIfReadinessIsBypassed(string problem)
    {
        var kit = new AltinnTestKit();
        await using var harness = Harness(kit);
        await kit.ConfigureReadyAsync(harness);
        var confirmation = problem == "roles-without-priv" ? VerifiedPolicy("DAGL") : VerifiedPolicy();
        if (problem == "actions-without-write") confirmation.EndUserActions = ["read", "instantiate"];
        kit.Options.ConfirmedAppPolicies = [confirmation];
        kit.Http.Calls.Clear();

        var view = await Rapportmottak(harness, kit);
        Row(view, "app-policy").State.Should().Be(JourneyReadinessState.NotReady);
        Row(view, "app-policy").Detail.Should().Contain("Not compatible");
        view.RunnableScenarios.Should().NotContain("valid-report");

        var journey = Pack(harness, kit).Journeys.Single(item => item.JourneyId == "rapportmottak");
        var trusted = BirkNext.Api.Services.ActiveEventTesting.TrustedExecutionEnvironmentRegistry.From(harness.Active.Configuration()).Resolve(Env, out _);
        var prepared = SkoleNaervaerReportInput.Prepare(journey.Scenarios[0], Report());
        var result = await Executor(harness, kit).ExecuteAsync(new JourneyStepContext(Guid.NewGuid(), Env, trusted, journey, journey.Scenarios[0], journey.Steps[0], null, [], prepared.Values, Actor), default);
        result.State.Should().Be(JourneyStepState.Unavailable);
        result.Reason.Should().Contain("confirmed app policy");
        kit.Http.Calls.Should().BeEmpty("nothing is authenticated or sent when the policy does not fit the reporter");
    }

    [Fact]
    public async Task TheDryRun_ChecksEverythingBeforeTheFirstCall_WithoutAnyNetworkCallTokenOrHistory()
    {
        var kit = new AltinnTestKit();
        await using var harness = Harness(kit);
        await kit.ConfigureReadyAsync(harness);
        // Like the real buf/skolenaervaer app: the form data type accepts application/xml only.
        harness.Sources.Snapshots.Clear();
        harness.Sources.Snapshots.Add(AltinnTestKit.Snapshot(AltinnTestKit.AppFiles(metadata: AltinnTestKit.ApplicationMetadata(allowed: "\"application/xml\""))));
        await kit.Contracts(harness.MessageFlows, harness.Sources).UseSourceAnalysisAsync(Env, null, default);
        kit.Options.ConfirmedAppPolicies = [VerifiedPolicy()];
        kit.Http.Calls.Clear();
        var child = SyntheticPersonIdentifier.Generate();

        var validation = await Service(harness, kit).ValidateAsync(Run("valid-report", Report(child)), default);

        validation.Valid.Should().BeTrue(string.Join(" | ", validation.Checks.Where(c => !c.Passed).Select(c => $"{c.Key}: {c.Detail}")));
        validation.Checks.Select(c => c.Key).Should().Contain(["scenario", "inputs", "reporter", "unattended-configuration", "production-safety", "endpoints", "app-policy", "contract", "form", "process-plan"]);
        validation.Checks.Single(c => c.Key == "endpoints").Detail.Should().Contain("10 endpoints").And.Contain("approved app's operations");
        validation.Checks.Single(c => c.Key == "process-plan").Detail.Should().Contain("process/next × 1").And.Contain("Task_1 (data)");
        validation.Checks.Single(c => c.Key == "form").Detail.Should().Contain("bytes, not shown");
        validation.Preview.Should().Contain(new JourneyEvidenceFact("XML root", "Skolenaervaer")).And.Contain(new JourneyEvidenceFact("Content type", "application/xml"));
        validation.Preview.Single(f => f.Label == "Klient.Identifikator.Fodselsnummer").Value.Should().Contain(SyntheticPersonIdentifier.Reference(child)).And.Contain("masked");

        var serialized = JsonSerializer.Serialize(validation);
        serialized.Should().NotContainAny(child, AltinnTestKit.ReporterIdentifier, AltinnTestKit.ClientSecret, "testid:", "<?xml");
        Regex.IsMatch(serialized, @"(?<!\d)\d{11}(?!\d)").Should().BeFalse("no 11-digit identifier appears in the dry run");
        kit.Http.Calls.Should().BeEmpty("the dry run makes no ID-porten, Altinn or app call");
        (await harness.Store.ListAsync(new IntegrationJourneyHistoryQuery(Env), 10, default)).Should().BeEmpty("a dry run records no history");
    }

    [Fact]
    public async Task TheDryRun_ReportsEachBlocker_AndTheUnfilledRequiredFieldsAsARisk()
    {
        var kit = new AltinnTestKit();
        await using var harness = Harness(kit);
        // A contract without the process definition, no policy confirmation and no client secret.
        harness.MessageFlows.Configuration = AltinnTestKit.ReadyConfiguration;
        kit.ConfigureUnattended(confirmPolicy: false, secret: false);
        harness.Sources.Snapshots.Add(AltinnTestKit.Snapshot(AltinnTestKit.AppFiles().Where(f => !f.Path.EndsWith("process.bpmn", StringComparison.Ordinal)).ToArray()));
        await kit.Contracts(harness.MessageFlows, harness.Sources).UseSourceAnalysisAsync(Env, null, default);
        kit.Http.Calls.Clear();

        var validation = await Service(harness, kit).ValidateAsync(Run("valid-report", Report(SyntheticPersonIdentifier.Generate())), default);

        validation.Valid.Should().BeFalse();
        Failed(validation).Should().Contain(["unattended-configuration", "app-policy", "contract", "process-plan"]);
        validation.Checks.Single(c => c.Key == "unattended-configuration").Detail.Should().Contain("client secret").And.NotContain(AltinnTestKit.ClientSecret);
        validation.Checks.Single(c => c.Key == "process-plan").Detail.Should().Contain("process/next is never called");
        var unfilled = validation.Checks.Single(c => c.Key == "unfilled-required");
        unfilled.Blocking.Should().BeFalse("a field the app may fill is a stated risk, not a blocker");
        unfilled.Detail.Should().Contain("Meldingshode");
        kit.Http.Calls.Should().BeEmpty();

        var invalid = await Service(harness, kit).ValidateAsync(Run("valid-report", new Dictionary<string, string>(Report("12345678901"))), default);
        invalid.Valid.Should().BeFalse();
        invalid.Checks.Single(c => c.Key == "inputs").Passed.Should().BeFalse();
        invalid.Checks.Single(c => c.Key == "inputs").Detail.Should().Contain("synthetic").And.NotContain("12345678901");
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────────────────────────────

    private static IEnumerable<string> Failed(JourneyRunValidation validation) => validation.Checks.Where(c => !c.Passed && c.Blocking).Select(c => c.Key);
    private static JourneyPrerequisite Row(IntegrationJourneyView view, string key) => view.Prerequisites.Single(item => item.Key == key);

    private static Dictionary<string, string> Report(string? child = null) => new()
    {
        ["reporter"] = AltinnTestKit.ReporterAlias, [SkoleNaervaerReportInput.IdentitySourceKey] = "registered", [SkoleNaervaerAltinnForm.ChildIdentifier] = child ?? SyntheticPersonIdentifier.Generate(),
        [SkoleNaervaerAltinnForm.PeriodFrom] = "2026-10-01", [SkoleNaervaerAltinnForm.PeriodTo] = "2026-10-31",
        [SkoleNaervaerAltinnForm.PlannedHours] = "100", [SkoleNaervaerAltinnForm.ActualHours] = "92",
    };

    private static IntegrationJourneyRunRequest Run(string scenario, IReadOnlyDictionary<string, string> inputs) => new()
    {
        EnvironmentId = Env, PackId = "skolenaervaer", JourneyId = "rapportmottak", ScenarioId = scenario, Confirmed = false, Inputs = inputs,
    };

    private static async Task<IntegrationJourneyView> Rapportmottak(IntegrationJourneyHarness harness, AltinnTestKit kit) =>
        (await Service(harness, kit).PacksAsync(Env, default)).Single().Journeys.Single(item => item.Journey.JourneyId == "rapportmottak");

    private static SkoleNaervaerJourneyPack Pack(IntegrationJourneyHarness harness, AltinnTestKit kit) => new(harness.MessageFlows, kit.Policy, kit.Contracts(harness.MessageFlows, harness.Sources));

    private static IServiceScopeFactory Scopes(IntegrationJourneyHarness harness, AltinnTestKit kit) => new ServiceCollection()
        .AddSingleton<BirkNext.Api.Services.Integrations.IIntegrationMessageFlowStore>(harness.MessageFlows)
        .AddTransient(_ => kit.Policy)
        .AddTransient(_ => kit.Contracts(harness.MessageFlows, harness.Sources))
        .BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

    private static SkoleNaervaerAltinnExecutor Executor(IntegrationJourneyHarness harness, AltinnTestKit kit) =>
        new(Scopes(harness, kit), kit.Authenticator(harness.Tokens), new AltinnAppApiClient(kit.Factory), TimeProvider.System);

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
