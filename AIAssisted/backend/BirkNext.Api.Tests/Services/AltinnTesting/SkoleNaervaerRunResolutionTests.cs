using System.Text.Json;
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
/// Run readiness resolves every Rapportmottak input from its owner — Target Environment, Integration Quality Review, local execution config,
/// approved test data and live (anonymous) Altinn metadata — and only an unresolved input stays not ready. Each row names its source, never a
/// value; nothing is guessed, generated, verified or authenticated by resolving; secrets and identifiers never leave the backend.
/// </summary>
public sealed class SkoleNaervaerRunResolutionTests
{
    private const string Env = ActiveEventTestHarness.Env;
    private const string ReportIntake = "dev:http:altinn-skolenaervaer";
    private static readonly string ChildIdentifier = Synthetic("0285", "05");
    private static readonly string OtherChildIdentifier = Synthetic("0386", "06");

    /// <summary>The run inputs a submission needs (the run page's "Resolved automatically" / "Needs attention" rows).</summary>
    private static readonly string[] RunInputs =
    [
        "trusted-environment", "integration:report-intake", "altinn-environment", "altinn-app", "submission-contract", "public-metadata", "process-contract",
        "idporten-client", "redirect-uri", "client-secret", "unattended-provider", "synthetic-reporter", "reporter-profile", "app-policy", "test-reporter", "synthetic-data",
    ];

    private sealed record Bed(AltinnTestKit Kit, IntegrationJourneyHarness Harness);

    private static async Task<Bed> ReadyAsync(Action<AltinnTestKit, IntegrationJourneyHarness>? change = null, bool enrol = true, bool liveContract = true,
        AltinnTestConfiguration? configuration = null, bool child = true)
    {
        var kit = new AltinnTestKit();
        kit.Http.MetadataJson = AltinnTestKit.ApplicationMetadata(allowed: "\"application/xml\"");
        var harness = new IntegrationJourneyHarness();
        harness.Active.Catalog.Extra.Add(new IntegrationDefinition
        {
            Id = ReportIntake, EnvironmentId = Env, DisplayName = "Skolenærvær – Altinn report intake", Kind = IntegrationKind.HttpApi,
            Enabled = true, Producer = "Altinn (TT02)", Consumer = new IntegrationConsumer { DisplayName = "MU" },
        });
        if (enrol) harness.Enroll(ReportIntake);
        harness.Executors.Add(new SkoleNaervaerAltinnExecutor(new ServiceCollection()
            .AddSingleton<BirkNext.Api.Services.Integrations.IIntegrationMessageFlowStore>(harness.MessageFlows)
            .AddTransient(_ => kit.Policy).AddTransient(_ => kit.Contracts(harness.MessageFlows, harness.Sources))
            .BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), kit.Authenticator(harness.Tokens), new AltinnAppApiClient(kit.Factory), TimeProvider.System));
        kit.ConfigureUnattended();
        if (child) kit.Options.SyntheticChildren = [new SyntheticChildOptions { Alias = "synthetic-child-1", Label = "Synthetic child 1", TestPersonIdentifier = ChildIdentifier }];
        harness.MessageFlows.Configuration = configuration ?? AltinnTestKit.ReadyConfiguration;
        change?.Invoke(kit, harness);
        if (liveContract) await kit.Contracts(harness.MessageFlows, harness.Sources).DiscoverPublicAsync(Env, default);
        kit.Http.Calls.Clear();
        return new(kit, harness);
    }

    private static SkoleNaervaerJourneyPack Pack(Bed bed) => new(bed.Harness.MessageFlows, bed.Kit.Policy, bed.Kit.Contracts(bed.Harness.MessageFlows, bed.Harness.Sources));
    private static IntegrationJourneyService Service(Bed bed) => bed.Harness.Service(Pack(bed));

    private static async Task<IntegrationJourneyView> RapportmottakAsync(Bed bed, string env = Env) =>
        (await Service(bed).PacksAsync(env, default)).Single().Journeys.Single(view => view.Journey.JourneyId == SkoleNaervaerJourneyPack.RapportmottakId);

    private static string[] Unresolved(IntegrationJourneyView view) =>
        RunInputs.Where(key => view.Prerequisites.Single(row => row.Key == key).State != JourneyReadinessState.Ready).ToArray();

    private static JourneyPrerequisite Row(IntegrationJourneyView view, string key) => view.Prerequisites.Single(row => row.Key == key);
    private static JourneyScenarioInput? Input(IntegrationJourneyView view, string key, string scenario = "valid-report") =>
        view.Journey.Scenarios.Single(item => item.ScenarioId == scenario).Inputs!.FirstOrDefault(input => input.Key == key);

    private static void NothingSensitive(string json, string because)
    {
        json.Should().NotContainAny([AltinnTestKit.ClientSecret, AltinnTestKit.ReporterIdentifier, ChildIdentifier, OtherChildIdentifier, AltinnTestKit.IdPortenToken, AltinnTestKit.AltinnToken,
            "Bearer ", "Authorization:", "Cookie:", "Set-Cookie", AltinnTestKit.PartyId.ToString(System.Globalization.CultureInfo.InvariantCulture), AltinnTestKit.InstanceGuid.ToString("D")], because);
    }

    // ── 1. Everything available ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EverythingAvailable_ResolvesEveryRunInput_WithItsSource_AndNoSecretOrIdentityLeavesTheBackend()
    {
        var bed = await ReadyAsync();
        var view = await RapportmottakAsync(bed);

        Unresolved(view).Should().BeEmpty();
        view.RunnableScenarios.Should().Contain("valid-report");
        var sources = RunInputs.ToDictionary(key => key, key => Row(view, key).Source);
        sources["trusted-environment"].Should().Be("Target Environment");
        sources["integration:report-intake"].Should().Be("Integration Quality Review + Target Environment");
        (sources["idporten-client"], sources["altinn-app"], sources["test-reporter"]).Should().Be(("Integration Quality Review", "Integration Quality Review", "Integration Quality Review"));
        (sources["redirect-uri"], sources["client-secret"], sources["unattended-provider"]).Should().Be(("Local execution config", "Local execution config", "Local execution config"));
        (sources["synthetic-reporter"], sources["reporter-profile"], sources["synthetic-data"]).Should().Be(("Approved test data", "Approved test data", "Approved test data"));
        (sources["public-metadata"], sources["submission-contract"], sources["process-contract"]).Should().Be(("Live Altinn metadata", "Live Altinn metadata", "Live Altinn metadata"));
        Row(view, "synthetic-data").Detail.Should().StartWith("Resolved: synthetic-child-1").And.Contain("not verified");
        Row(view, "synthetic-reporter").Detail.Should().StartWith($"Resolved: {AltinnTestKit.ReporterAlias}").And.Contain("Configured is not authenticated");
        Row(view, "client-secret").Detail.Should().Be("Available to the backend (value never shown).");
        view.Prerequisites.Should().NotContain(row => row.Detail.Contains("Authenticated", StringComparison.Ordinal) || row.Detail.Contains("registration-verified", StringComparison.OrdinalIgnoreCase));

        // The run form is prefilled from the same sources: reporter alias and approved child alias, never identifiers.
        Input(view, SkoleNaervaerReportInput.ReporterKey)!.Default.Should().Be(AltinnTestKit.ReporterAlias);
        Input(view, SkoleNaervaerReportInput.IdentitySourceKey)!.Default.Should().Be(SkoleNaervaerReportInput.Approved);
        Input(view, SkoleNaervaerReportInput.ChildKey)!.Default.Should().Be("synthetic-child-1");
        NothingSensitive(JsonSerializer.Serialize(view), "the readiness view carries sources and aliases only");
        bed.Kit.Http.Calls.Should().BeEmpty("resolving readiness makes no ID-porten or Altinn call");
    }

    [Fact]
    public async Task AResolvedDryRun_UsesTheApprovedChildByAlias_GeneratesThePayload_AndCallsNothing()
    {
        var bed = await ReadyAsync();
        var view = await RapportmottakAsync(bed);
        var inputs = view.Journey.Scenarios.Single(item => item.ScenarioId == "valid-report").Inputs!
            .Where(input => input.Default is not null).ToDictionary(input => input.Key, input => input.Default!);
        inputs[SkoleNaervaerAltinnForm.PeriodFrom] = "2026-09-01"; inputs[SkoleNaervaerAltinnForm.PeriodTo] = "2026-09-30";
        inputs[SkoleNaervaerAltinnForm.PlannedHours] = "100"; inputs[SkoleNaervaerAltinnForm.ActualHours] = "92";

        var validation = await Service(bed).ValidateAsync(new IntegrationJourneyRunRequest
        {
            EnvironmentId = Env, PackId = SkoleNaervaerJourneyPack.Id, JourneyId = SkoleNaervaerJourneyPack.RapportmottakId, ScenarioId = "valid-report", Inputs = inputs,
        }, default);

        validation.Valid.Should().BeTrue(string.Join("; ", validation.Checks.Where(c => !c.Passed).Select(c => $"{c.Key}: {c.Detail}")));
        validation.InputSummary.Should().Contain(line => line.Contains("approved synthetic child synthetic-child-1") && line.Contains("not verified by BirkNext"));
        validation.PayloadFlow!.Stages[0].Level.Should().Be(FlowStageEvidenceLevel.Generated, "a dry run generates only");
        validation.PayloadFlow.Stages.Skip(1).Should().OnlyContain(stage => stage.Level == FlowStageEvidenceLevel.Expected, "resolving inputs creates no downstream evidence");
        NothingSensitive(JsonSerializer.Serialize(validation), "the dry run shows the child by alias and the payload masked");
        bed.Kit.Http.Calls.Should().BeEmpty("a dry run makes no ID-porten or authenticated Altinn call");
    }

    [Fact]
    public async Task ARunWithResolvedInputs_SubmitsTheApprovedChildsForm_AndHistoryKeepsOnlyTheAlias()
    {
        var bed = await ReadyAsync();
        var view = await RapportmottakAsync(bed);
        var inputs = view.Journey.Scenarios.Single(item => item.ScenarioId == "valid-report").Inputs!
            .Where(input => input.Default is not null).ToDictionary(input => input.Key, input => input.Default!);
        inputs[SkoleNaervaerAltinnForm.PeriodFrom] = "2026-09-01"; inputs[SkoleNaervaerAltinnForm.PeriodTo] = "2026-09-30";
        inputs[SkoleNaervaerAltinnForm.PlannedHours] = "100"; inputs[SkoleNaervaerAltinnForm.ActualHours] = "92";

        var run = await Service(bed).StartAsync(new IntegrationJourneyRunRequest
        {
            EnvironmentId = Env, PackId = SkoleNaervaerJourneyPack.Id, JourneyId = SkoleNaervaerJourneyPack.RapportmottakId, ScenarioId = "valid-report", Confirmed = true, Inputs = inputs,
        }, "local-session:loopback", default);

        run.OverallState.Should().NotBe(JourneyRunState.Blocked, run.StateReason);
        bed.Kit.Http.Calls.Where(call => call.Body is not null).Should().Contain(call => call.Body!.Contains(ChildIdentifier), "the in-process fake app received the approved child's form");
        var persisted = (await bed.Harness.PersistedJsonAsync(run.RunId))!;
        persisted.Should().Contain("approved synthetic child synthetic-child-1");
        NothingSensitive(persisted.Replace(run.RunId.ToString("N"), "", StringComparison.Ordinal), "history keeps the alias, never the identifier, secret or token");
    }

    // ── 2–6. ID-porten / local execution config ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ClientIdMissing_IsTheOnlyUnresolvedInput_AndPointsToIqr()
    {
        var bed = await ReadyAsync(configuration: AltinnTestKit.ReadyConfiguration with { ClientReference = null });
        var view = await RapportmottakAsync(bed);
        Unresolved(view).Should().Equal("idporten-client");
        Row(view, "idporten-client").Source.Should().Be("Integration Quality Review");
        Row(view, "idporten-client").Detail.Should().Contain("Integration Quality Review → Altinn test configuration");
        Row(view, "reporter-auth").State.Should().NotBe(JourneyReadinessState.Ready, "unattended authentication genuinely depends on the client id");
    }

    [Fact]
    public async Task ScopesMissing_IsUnresolved_AndConfiguredScopesAreNeverCalledVerified()
    {
        var bed = await ReadyAsync(configuration: AltinnTestKit.ReadyConfiguration with { ConfiguredScopes = [] });
        var view = await RapportmottakAsync(bed);
        Unresolved(view).Should().Equal("idporten-client");
        Row(view, "idporten-client").Detail.Should().Contain("openid scope");

        var ready = await RapportmottakAsync(await ReadyAsync());
        Row(ready, "idporten-client").Detail.Should().StartWith("Configured:").And.NotContain("Verified", "configured scopes are not a verified client registration");
    }

    [Fact]
    public async Task RedirectMissing_IsALocalExecutionBlocker_AndNoDefaultIsInvented()
    {
        var bed = await ReadyAsync((kit, _) => { kit.Options.UnattendedIdPortenTest.RedirectUri = null; kit.Options.IdPortenTest.RedirectUri = null; });
        var view = await RapportmottakAsync(bed);
        Unresolved(view).Should().Equal("redirect-uri");
        Row(view, "redirect-uri").Source.Should().Be("Local execution config");
        bed.Kit.Policy.UnattendedRedirect().Should().BeNull("nothing guesses a redirect URI");
    }

    [Fact]
    public async Task SecretMissing_IsUnavailable_AndTheSecretValueIsNeverInTheResponse()
    {
        var missing = await RapportmottakAsync(await ReadyAsync((kit, _) => kit.ConfigureUnattended(secret: false)));
        Unresolved(missing).Should().Equal("client-secret");
        Row(missing, "client-secret").Detail.Should().StartWith("Missing:");

        var available = await RapportmottakAsync(await ReadyAsync());
        Row(available, "client-secret").State.Should().Be(JourneyReadinessState.Ready);
        JsonSerializer.Serialize(available).Should().NotContain(AltinnTestKit.ClientSecret, "only Available / Missing is exposed");
    }

    [Fact]
    public async Task EnabledFalse_IsUnresolved_AndIsNeverSwitchedOnByResolving()
    {
        var bed = await ReadyAsync((kit, _) => kit.ConfigureUnattended(enabled: false));
        var view = await RapportmottakAsync(bed);
        Unresolved(view).Should().Equal("unattended-provider");
        bed.Kit.Options.UnattendedIdPortenTest.Enabled.Should().BeFalse();
    }

    // ── 7–10. Reporter and child ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ReporterMissing_IsATestIdentityBlocker_AndNothingIsPreselected()
    {
        var view = await RapportmottakAsync(await ReadyAsync((kit, _) => kit.ConfigureUnattended(reporter: false)));
        Unresolved(view).Should().Contain(["synthetic-reporter", "reporter-profile"]);
        Input(view, SkoleNaervaerReportInput.ReporterKey)!.Default.Should().BeNull();
    }

    [Fact]
    public async Task ReporterUnconfirmed_IsAProfileBlocker_AndTheReporterIsNotCalledAuthenticated()
    {
        var view = await RapportmottakAsync(await ReadyAsync((kit, _) => kit.ConfigureUnattended(profile: false)));
        Unresolved(view).Should().Equal("reporter-profile");
        Row(view, "synthetic-reporter").Detail.Should().Contain("Configured is not authenticated").And.NotStartWith("Resolved:");
        Input(view, SkoleNaervaerReportInput.ReporterKey)!.Default.Should().BeNull("an unconfirmed reporter is not offered, so nothing is preselected");
    }

    [Fact]
    public async Task ChildMissing_IsOneTestDataBlocker_AndNoIdentityIsGenerated()
    {
        var bed = await ReadyAsync(child: false, configuration: AltinnTestKit.ReadyConfiguration with { ApprovedSyntheticData = false });
        var view = await RapportmottakAsync(bed);
        Unresolved(view).Should().Equal("synthetic-data");
        Row(view, "synthetic-data").Detail.Should().StartWith("No approved synthetic child could be resolved").And.Contain("never generates");
        Input(view, SkoleNaervaerReportInput.ChildKey).Should().BeNull();
        Input(view, SkoleNaervaerReportInput.IdentitySourceKey)!.Options!.Select(option => option.Value).Should().NotContain(SkoleNaervaerReportInput.Approved);
        bed.Kit.Options.SyntheticChildren.Should().BeEmpty("resolving never adds test data");
    }

    [Fact]
    public async Task AChildThatIsTheReporter_IsRejectedInTheBackend()
    {
        var bed = await ReadyAsync((kit, _) => kit.Options.SyntheticChildren = [new SyntheticChildOptions { Alias = "synthetic-child-1", TestPersonIdentifier = AltinnTestKit.ReporterIdentifier }],
            child: false, configuration: AltinnTestKit.ReadyConfiguration with { ApprovedSyntheticData = false });
        var view = await RapportmottakAsync(bed);
        Unresolved(view).Should().Equal("synthetic-data");
        Row(view, "synthetic-data").Detail.Should().Contain("same person as reporter").And.NotContain(AltinnTestKit.ReporterIdentifier);

        // Even a crafted request cannot use it: the alias is not offered, so preparation refuses it before anything is built.
        var prepared = Pack(bed).PrepareInputs(view.Journey, view.Journey.Scenarios.Single(item => item.ScenarioId == "valid-report"), new Dictionary<string, string>
        {
            [SkoleNaervaerReportInput.ReporterKey] = AltinnTestKit.ReporterAlias, [SkoleNaervaerReportInput.IdentitySourceKey] = SkoleNaervaerReportInput.Approved,
            [SkoleNaervaerReportInput.ChildKey] = "synthetic-child-1", [SkoleNaervaerAltinnForm.PeriodFrom] = "2026-09-01", [SkoleNaervaerAltinnForm.PeriodTo] = "2026-09-30",
            [SkoleNaervaerAltinnForm.PlannedHours] = "100", [SkoleNaervaerAltinnForm.ActualHours] = "92",
        });
        prepared.Errors.Should().NotBeEmpty();
    }

    // ── 11–13. Target, enrolment, contract ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TargetMissing_IsATrustedTargetBlocker()
    {
        var bed = await ReadyAsync((_, harness) => harness.Active.Settings["TargetEnvironments:Trusted:0:ExecutionAllowed"] = "false");
        var view = await RapportmottakAsync(bed);
        Unresolved(view).Should().Contain("trusted-environment");
        Row(view, "trusted-environment").Source.Should().Be("Target Environment");
    }

    [Fact]
    public async Task IntegrationNotEnrolled_IsAnEnrolmentBlocker_NamingTheExactId()
    {
        var view = await RapportmottakAsync(await ReadyAsync(enrol: false));
        Unresolved(view).Should().Equal("integration:report-intake");
        Row(view, "integration:report-intake").Detail.Should().Contain(ReportIntake);
    }

    [Fact]
    public async Task LiveContractMissing_IsAContractBlocker()
    {
        var view = await RapportmottakAsync(await ReadyAsync(liveContract: false));
        Unresolved(view).Should().Contain(["public-metadata", "submission-contract"]);
        Row(view, "submission-contract").Source.Should().Be("Live Altinn metadata");
    }

    // ── 14–15. Ambiguity: never guessed ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnotherTrustedTarget_IsNeverPickedForThisEnvironment()
    {
        var bed = await ReadyAsync((_, harness) =>
        {
            harness.Active.Settings["TargetEnvironments:Trusted:1:EnvironmentId"] = "other-env";
            harness.Active.Settings["TargetEnvironments:Trusted:1:DisplayName"] = "Other";
            harness.Active.Settings["TargetEnvironments:Trusted:1:EnvironmentType"] = "Development";
            harness.Active.Settings["TargetEnvironments:Trusted:1:ExecutionAllowed"] = "true";
            harness.Active.Settings["TargetEnvironments:Trusted:1:IntegrationIds:0"] = ReportIntake;
        });
        var unknown = await RapportmottakAsync(bed, env: "selected-but-untrusted");
        Row(unknown, "trusted-environment").State.Should().NotBe(JourneyReadinessState.Ready, "the run's own environment decides; a different trusted target is never substituted");
    }

    [Fact]
    public async Task SeveralApprovedChildren_AreChosenPerRun_UnlessExactlyOneIsTheDefault()
    {
        SyntheticChildOptions[] children =
        [
            new() { Alias = "synthetic-child-1", TestPersonIdentifier = ChildIdentifier },
            new() { Alias = "synthetic-child-2", TestPersonIdentifier = OtherChildIdentifier },
        ];
        var noDefault = await RapportmottakAsync(await ReadyAsync((kit, _) => kit.Options.SyntheticChildren = [.. children], child: false));
        Row(noDefault, "synthetic-data").Detail.Should().Contain("2 approved synthetic children and no single default");
        Input(noDefault, SkoleNaervaerReportInput.ChildKey)!.Default.Should().BeNull("several children without a default are not guessed");
        Input(noDefault, SkoleNaervaerReportInput.ChildKey)!.Options!.Select(option => option.Value).Should().Equal("synthetic-child-1", "synthetic-child-2");

        children[1].Default = true;
        var withDefault = await RapportmottakAsync(await ReadyAsync((kit, _) => kit.Options.SyntheticChildren = [.. children], child: false));
        Input(withDefault, SkoleNaervaerReportInput.ChildKey)!.Default.Should().Be("synthetic-child-2");
        NothingSensitive(JsonSerializer.Serialize(withDefault), "children are offered by alias only");
    }

    [Fact]
    public async Task UnknownChildScenarios_OnlyOfferChildrenDeclaredUnknownToPerson()
    {
        var bed = await ReadyAsync((kit, _) => kit.Options.SyntheticChildren =
        [
            new() { Alias = "synthetic-child-1", TestPersonIdentifier = ChildIdentifier },
            new() { Alias = "unknown-child-1", TestPersonIdentifier = OtherChildIdentifier, KnownToPerson = false },
        ], child: false);
        var view = await RapportmottakAsync(bed);
        Input(view, SkoleNaervaerReportInput.ChildKey, "person-not-found")!.Options!.Select(option => option.Value).Should().Equal("unknown-child-1");
        Input(view, SkoleNaervaerReportInput.ChildKey, "valid-report")!.Options!.Select(option => option.Value).Should().Equal("synthetic-child-1");
    }

    private static string Synthetic(string dayMonth, string year)
    {
        for (var individual = 500; individual < 1000; individual++)
            for (var k = 0; k < 100; k++)
            {
                var candidate = $"{dayMonth}{year}{individual:000}{k:00}";
                if (SyntheticPersonIdentifier.IsSynthetic(candidate)) return candidate;
            }
        throw new InvalidOperationException("No synthetic identifier found.");
    }
}
