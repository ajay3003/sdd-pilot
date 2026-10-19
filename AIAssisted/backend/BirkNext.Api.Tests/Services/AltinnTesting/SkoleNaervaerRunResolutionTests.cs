using System.Text.Json;
using BirkNext.Api.Services.AltinnTesting;
using BirkNext.Api.Services.IntegrationJourneys;
using BirkNext.Api.Services.IntegrationJourneys.Packs.SkoleNaervaer;
using BirkNext.Api.Tests.Services.ActiveEventTesting;
using BirkNext.Api.Tests.Services.IntegrationJourneys;
using BirkNext.Integrations;
using BirkNext.SourceDomains;
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
    public async Task SkolenaervaerDefaults_SavedByTheMetadataRead_ResolveEnvironmentAndAppFromIqr()
    {
        var bed = await ReadyAsync(configuration: AltinnTestKit.ReadyConfiguration with { Environment = ExternalTestEnvironment.Unknown, AppIdentifier = null }, liveContract: false);
        var before = await RapportmottakAsync(bed);
        Unresolved(before).Should().Contain(["altinn-environment", "altinn-app"], "a default is not configuration until it is saved to IQR");

        await bed.Kit.Contracts(bed.Harness.MessageFlows, bed.Harness.Sources).DiscoverPublicWithSkoleNaervaerDefaultsAsync(Env, default);

        var after = await RapportmottakAsync(bed);
        foreach (var key in new[] { "altinn-environment", "altinn-app" })
        {
            Row(after, key).State.Should().Be(JourneyReadinessState.Ready, key);
            Row(after, key).Source.Should().Be("Integration Quality Review", key);
        }
        Unresolved(after).Should().BeEmpty("every other input was already configured by its own owner");
        bed.Harness.MessageFlows.Configuration.ClientReference.Should().Be(AltinnTestKit.ClientId, "the existing client id is kept, never defaulted");
        NothingSensitive(JsonSerializer.Serialize(after), "readiness never shows a secret or identity");
    }

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
        (sources["synthetic-reporter"], sources["reporter-profile"], sources["synthetic-data"]).Should().Be(("Test Data", "Test Data", "Test Data"));
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

    // ── Contract change impact (generic contract-evolution engine, Skolenærvær profile) ──────────────────────────────

    /// <summary>The live form changes while the confirmed baseline stays (as before a review); nothing is fetched again.</summary>
    private static void LiveFormChanges(Bed bed, Func<IReadOnlyList<AltinnSchemaField>, IEnumerable<AltinnSchemaField>> change)
    {
        var record = bed.Harness.MessageFlows.Contract!;
        bed.Harness.MessageFlows.Contract = record with { PublicMetadata = record.PublicMetadata! with { Fields = [.. change(record.PublicMetadata.Fields!)] } };
    }

    [Fact]
    public async Task ContractImpact_Unchanged_IsReady_AndBlocksNothing()
    {
        var bed = await ReadyAsync();
        var view = await RapportmottakAsync(bed);
        Row(view, "contract-impact").Should().Match<JourneyPrerequisite>(row => row.State == JourneyReadinessState.Ready && !row.Mandatory && row.Source == "Live Altinn metadata");
        Row(view, "contract-impact").Detail.Should().StartWith("Unchanged since the confirmed contract").And.Contain("not runtime observed");
        view.RunnableScenarios.Should().Contain("valid-report");
    }

    [Fact]
    public async Task ContractImpact_ARequiredFieldBirkNextDoesNotProduce_BlocksTheRealRunAndTheDryRun_AtTheProducerBoundary()
    {
        var bed = await ReadyAsync();
        LiveFormChanges(bed, fields => [.. fields, new AltinnSchemaField("Valuta", "string", null, true)]);
        var view = await RapportmottakAsync(bed);

        var row = Row(view, "contract-impact");
        (row.State, row.Mandatory).Should().Be((JourneyReadinessState.NotReady, true));
        row.Detail.Should().Contain("BirkNext producer: Breaking change").And.Contain("Valuta").And.Contain("First incompatible boundary: BirkNext → Altinn")
            .And.Contain("nothing is generated for unknown business fields");
        row.Detail.Should().Contain("MU → MM (transport): Not evaluated (potential)").And.Contain("Bufdata: Not evaluated (potential)", "downstream is never claimed compatible without evidence");
        view.RunnableScenarios.Should().NotContain("valid-report", "the real run is gated");

        var inputs = view.Journey.Scenarios.Single(item => item.ScenarioId == "valid-report").Inputs!.Where(input => input.Default is not null).ToDictionary(input => input.Key, input => input.Default!);
        inputs[SkoleNaervaerAltinnForm.PeriodFrom] = "2026-09-01"; inputs[SkoleNaervaerAltinnForm.PeriodTo] = "2026-09-30";
        inputs[SkoleNaervaerAltinnForm.PlannedHours] = "100"; inputs[SkoleNaervaerAltinnForm.ActualHours] = "92";
        var validation = await Service(bed).ValidateAsync(new IntegrationJourneyRunRequest
        {
            EnvironmentId = Env, PackId = SkoleNaervaerJourneyPack.Id, JourneyId = SkoleNaervaerJourneyPack.RapportmottakId, ScenarioId = "valid-report", Inputs = inputs,
        }, default);
        validation.Checks.Should().ContainSingle(check => check.Key == "contract-impact").Which.Passed.Should().BeFalse();
        bed.Kit.Http.Calls.Should().BeEmpty("contract impact is computed from stored evidence; nothing is fetched or submitted");
    }

    [Fact]
    public async Task ContractImpact_AnOptionalTechnicalHeaderField_IsCompatible_AndDoesNotBlock()
    {
        var bed = await ReadyAsync();
        LiveFormChanges(bed, fields => [.. fields, new AltinnSchemaField("Meldingshode.Sporingsnummer", "string", null, false)]);
        var view = await RapportmottakAsync(bed);
        var row = Row(view, "contract-impact");
        (row.State, row.Mandatory).Should().Be((JourneyReadinessState.Partial, false));
        row.Detail.Should().Contain("BirkNext producer: compatible.");
        view.RunnableScenarios.Should().Contain("valid-report");
    }

    [Fact]
    public async Task ContractImpact_AnOptionalBusinessField_NeedsBusinessReview_BeforeARealRun()
    {
        var bed = await ReadyAsync();
        LiveFormChanges(bed, fields => [.. fields, new AltinnSchemaField("Fravaersaarsak", "string", null, false)]);
        var view = await RapportmottakAsync(bed);
        var row = Row(view, "contract-impact");
        (row.State, row.Mandatory).Should().Be((JourneyReadinessState.NotReady, true));
        row.Detail.Should().Contain("BirkNext producer: Needs review").And.Contain("business meaning");
        row.Detail.Should().NotContain("First incompatible boundary", "needing review is not a known incompatibility");
    }

    [Fact]
    public void SkolenaervaerSemantics_ClassifyByExplicitPathsOnly_AndNeverMapANewCodeValue()
    {
        var rules = new SkoleNaervaerContractSemantics();
        rules.Assess(new(ContractChangeClass.FieldRemoved, "Klient/Identifikator/Fodselsnummer", "x", null, ""))!.Compatibility.Should().Be(ContractCompatibility.Breaking);
        rules.Assess(new(ContractChangeClass.FieldAddedOptional, "Klient/Identifikator/DufNummer", null, "x", ""))!.Compatibility.Should().Be(ContractCompatibility.NeedsReview);
        rules.Assess(new(ContractChangeClass.EnumValueAdded, "Fravaer/Type", null, "Permisjon", ""))!
            .Should().Match<ContractSemanticAssessment>(item => item.Compatibility == ContractCompatibility.NeedsReview && item.Reason.Contains("never mapped"));
        rules.Assess(new(ContractChangeClass.FieldAddedOptional, "Meldingshode/Sporingsnummer", null, "x", ""))!.Compatibility.Should().Be(ContractCompatibility.Compatible);
        rules.Assess(new(ContractChangeClass.TypeChanged, "Periode/StartDato", "string:date", "string:date-time", ""))!.Compatibility.Should().Be(ContractCompatibility.NeedsReview);
    }

    [Fact]
    public void SkolenaervaerGraph_IsData_MmInputAndMappingAreSeparate_AndOnlyMappingsLinkContracts()
    {
        var graph = SkoleNaervaerContractProfile.Graph("altinn:app:form");
        graph.Boundaries.Select(item => (item.Id, item.Role)).Should().Equal(
            ("producer", ContractBoundaryRole.Produces), ("altinn-to-mu", ContractBoundaryRole.Maps), ("mu-transport", ContractBoundaryRole.Transports),
            ("mm-input", ContractBoundaryRole.Consumes), ("mm-to-skole", ContractBoundaryRole.Maps), ("skole-intake", ContractBoundaryRole.Consumes),
            ("skole-to-bufdata", ContractBoundaryRole.Maps), ("bufdata", ContractBoundaryRole.Consumes));
        graph.Boundaries.Where(item => item.ContractId == "altinn:app:form").Select(item => item.Id).Should().Equal("producer", "altinn-to-mu");
        graph.Boundaries.Where(item => item.Role == ContractBoundaryRole.Maps).Should().OnlyContain(item => item.Mapping == null, "no mapping is imported as data yet, so none is guessed");
        graph.Boundaries.Should().OnlyContain(item => item.Evidence.Length > 0);
    }

    [Fact]
    public void AltinnComparer_NoLongerAcceptsANonFieldFormChangeSilently()
    {
        var before = new AltinnContractSnapshot { AppId = AltinnTestKit.AppId, FormDataTypeId = "model", Fingerprint = "a", XmlRootElement = "Root", Fields = [new("A", "string", null, false)] };
        var after = before with { Fingerprint = "b", Fields = [new("A", "string", null, true)] };
        var changes = AltinnContractComparer.Compare(before, after, DateTimeOffset.UtcNow);
        changes.Impact.Should().Be(AltinnContractChangeImpact.NeedsReview, "a field that became required must be reviewed, not auto-accepted");
        changes.Changes.Should().ContainSingle(change => change.Area == "Form contract").Which.Detail.Should().Contain("optional → required");
    }

    // ── Test data through the generic Test Data capability ───────────────────────────────────────────────────────────

    private static AltinnTestKit TestDataKit(params SyntheticChildOptions[] children)
    {
        var kit = new AltinnTestKit().ConfigureUnattended();
        kit.Options.SyntheticChildren = [.. children];
        return kit;
    }

    [Fact]
    public void ReporterAndChild_ResolveThroughTheGenericTestDataResolver_ByAliasOnly()
    {
        var kit = TestDataKit(new SyntheticChildOptions { Alias = "synthetic-child-1", Label = "Synthetic child 1", TestPersonIdentifier = ChildIdentifier });
        var result = new SkoleNaervaerTestData(kit.Options).Resolve(knownToPerson: true);

        result.Satisfied.Should().BeTrue(string.Join("; ", result.Roles.Select(role => role.Reason)));
        result.Role(SkoleNaervaerTestData.ReporterRole)!.Should().Match<BirkNext.TestData.TestDataRoleResolution>(role => role.AssetAlias == AltinnTestKit.ReporterAlias && role.Source == "Local approved configuration");
        result.Role(SkoleNaervaerTestData.ChildRole)!.AssetAlias.Should().Be("synthetic-child-1");
        NothingSensitive(JsonSerializer.Serialize(result), "a resolution carries aliases only");

        var catalog = new BirkNext.Api.Services.TestData.TestDataCatalog([new AltinnTestDataProvider(kit.Options)]).View();
        catalog.Assets.Select(asset => (asset.Alias, asset.Kind, asset.Sensitivity)).Should().BeEquivalentTo(new[]
        {
            (AltinnTestKit.ReporterAlias, BirkNext.TestData.TestDataKind.EntityReference, BirkNext.TestData.TestDataSensitivity.Sensitive),
            ("synthetic-child-1", BirkNext.TestData.TestDataKind.EntityReference, BirkNext.TestData.TestDataSensitivity.Sensitive),
        });
        NothingSensitive(JsonSerializer.Serialize(catalog), "the Test Data page never receives an identifier");
    }

    [Fact]
    public void SeveralChildrenWithoutADefault_AreAmbiguous_AndAnExplicitChoiceIsRespected()
    {
        var kit = TestDataKit(new SyntheticChildOptions { Alias = "synthetic-child-1", TestPersonIdentifier = ChildIdentifier },
            new SyntheticChildOptions { Alias = "synthetic-child-2", TestPersonIdentifier = OtherChildIdentifier });
        var data = new SkoleNaervaerTestData(kit.Options);

        data.DefaultChild(knownToPerson: true).Should().BeNull("no single default decides");
        data.Resolve(knownToPerson: true).Role(SkoleNaervaerTestData.ChildRole)!
            .Should().Match<BirkNext.TestData.TestDataRoleResolution>(role => role.State == BirkNext.TestData.TestDataResolutionState.Ambiguous && role.Candidates.Count == 2);
        data.Resolve(knownToPerson: true, new Dictionary<string, string> { [SkoleNaervaerTestData.ChildRole] = "synthetic-child-2" }).Role(SkoleNaervaerTestData.ChildRole)!
            .Should().Match<BirkNext.TestData.TestDataRoleResolution>(role => role.State == BirkNext.TestData.TestDataResolutionState.Selected && role.AssetAlias == "synthetic-child-2");
    }

    [Fact]
    public void SeveralReporters_AreNeverPickedAutomatically_AndAMissingReporterIsUnresolved()
    {
        var kit = TestDataKit(new SyntheticChildOptions { Alias = "synthetic-child-1", TestPersonIdentifier = ChildIdentifier });
        kit.Options.UnattendedIdPortenTest.Reporters.Add(new SyntheticReporterOptions { Alias = "reporter-b", TestIdPersonIdentifier = Synthetic("0587", "07"), AltinnProfileConfirmed = true });
        new SkoleNaervaerTestData(kit.Options).DefaultReporter.Should().BeNull();
        new SkoleNaervaerTestData(kit.Options).Resolve(knownToPerson: true).Role(SkoleNaervaerTestData.ReporterRole)!.State.Should().Be(BirkNext.TestData.TestDataResolutionState.Ambiguous);

        kit.Options.UnattendedIdPortenTest.Reporters.Clear();
        var missing = new SkoleNaervaerTestData(kit.Options).Resolve(knownToPerson: true);
        missing.Satisfied.Should().BeFalse();
        missing.Role(SkoleNaervaerTestData.ReporterRole)!.Should().Match<BirkNext.TestData.TestDataRoleResolution>(role => role.State == BirkNext.TestData.TestDataResolutionState.Unresolved && role.Reason.Contains("Nothing is generated"));
    }

    [Fact]
    public void AChildThatIsTheReportersPerson_IsNeverResolved()
    {
        var kit = TestDataKit(new SyntheticChildOptions { Alias = "synthetic-child-1", TestPersonIdentifier = AltinnTestKit.ReporterIdentifier });
        var result = new SkoleNaervaerTestData(kit.Options).Resolve(knownToPerson: true);
        result.Role(SkoleNaervaerTestData.ChildRole)!.State.Should().Be(BirkNext.TestData.TestDataResolutionState.Unresolved);
        result.Satisfied.Should().BeFalse();
        JsonSerializer.Serialize(result).Should().NotContain(AltinnTestKit.ReporterIdentifier);
    }

    // ── Reference test pack: prerequisite metadata, no false readiness, canonical evidence ───────────────────────────

    /// <summary>Every implemented Rapportmottak prerequisite, with the category its metadata declares (blockers are grouped by it, never by text).</summary>
    [Theory]
    [InlineData("trusted-environment", JourneyPrerequisiteCategory.TrustedEnvironment)]
    [InlineData("integration:report-intake", JourneyPrerequisiteCategory.Integration)]
    [InlineData("altinn-environment", JourneyPrerequisiteCategory.TrustedEnvironment)]
    [InlineData("altinn-app", JourneyPrerequisiteCategory.Integration)]
    [InlineData("idporten-client", JourneyPrerequisiteCategory.Authentication)]
    [InlineData("redirect-uri", JourneyPrerequisiteCategory.Authentication)]
    [InlineData("client-secret", JourneyPrerequisiteCategory.Authentication)]
    [InlineData("unattended-provider", JourneyPrerequisiteCategory.Authentication)]
    [InlineData("reporter-auth", JourneyPrerequisiteCategory.Authentication)]
    [InlineData("synthetic-reporter", JourneyPrerequisiteCategory.SyntheticData)]
    [InlineData("reporter-profile", JourneyPrerequisiteCategory.SyntheticData)]
    [InlineData("test-reporter", JourneyPrerequisiteCategory.SyntheticData)]
    [InlineData("synthetic-data", JourneyPrerequisiteCategory.SyntheticData)]
    [InlineData("app-policy", JourneyPrerequisiteCategory.Authorization)]
    [InlineData("submission-contract", JourneyPrerequisiteCategory.Contract)]
    [InlineData("public-metadata", JourneyPrerequisiteCategory.Contract)]
    [InlineData("process-contract", JourneyPrerequisiteCategory.Contract)]
    [InlineData("report-xsd", JourneyPrerequisiteCategory.Contract)]
    [InlineData("altinn-auth", JourneyPrerequisiteCategory.Authentication)]
    [InlineData("reporter-role", JourneyPrerequisiteCategory.Authorization)]
    [InlineData("meldingshode", JourneyPrerequisiteCategory.Contract)]
    [InlineData("backend-build", JourneyPrerequisiteCategory.Executor)]
    [InlineData("executor", JourneyPrerequisiteCategory.Executor)]
    [InlineData("observation", JourneyPrerequisiteCategory.Observation)]
    [InlineData("source:MU", JourneyPrerequisiteCategory.SourceEvidence)]
    [InlineData("source:MM", JourneyPrerequisiteCategory.SourceEvidence)]
    [InlineData("source:Skolenærværsrapport", JourneyPrerequisiteCategory.SourceEvidence)]
    [InlineData("report-verifier", JourneyPrerequisiteCategory.DownstreamVerification)]
    [InlineData("evidence:mm-telemetry", JourneyPrerequisiteCategory.Observation)]
    [InlineData("evidence:skole-telemetry", JourneyPrerequisiteCategory.Observation)]
    [InlineData("evidence:skole-graphql", JourneyPrerequisiteCategory.Observation)]
    [InlineData("contract-impact", JourneyPrerequisiteCategory.Contract)]
    public async Task EveryImplementedPrerequisite_AppearsExactlyOnce_WithItsCategoryAndASource(string key, JourneyPrerequisiteCategory category)
    {
        var view = await RapportmottakAsync(await ReadyAsync());
        view.Prerequisites.Where(row => row.Key == key).Should().ContainSingle($"{key} is reported exactly once");
        Row(view, key).Category.Should().Be(category);
        Row(view, key).Source.Should().NotBeNullOrWhiteSpace("every prerequisite names where it was resolved from");
        view.Prerequisites.Select(row => row.Key).Should().OnlyHaveUniqueItems();
    }

    /// <summary>Every key the theory above covers individually. A new Rapportmottak prerequisite fails this test until it is covered there.</summary>
    private static readonly string[] CoveredPrerequisites =
    [
        "trusted-environment", "integration:report-intake", "altinn-environment", "altinn-app", "idporten-client", "redirect-uri", "client-secret",
        "unattended-provider", "reporter-auth", "synthetic-reporter", "reporter-profile", "test-reporter", "synthetic-data", "app-policy",
        "submission-contract", "public-metadata", "process-contract", "report-xsd", "altinn-auth", "reporter-role", "meldingshode", "backend-build",
        "executor", "observation", "source:MU", "source:MM", "source:Skolenærværsrapport", "report-verifier",
        "evidence:mm-telemetry", "evidence:skole-telemetry", "evidence:skole-graphql", "contract-impact",
    ];

    [Fact]
    public async Task EveryEmittedRapportmottakPrerequisite_IsCoveredIndividually()
    {
        var view = await RapportmottakAsync(await ReadyAsync());
        view.Prerequisites.Select(row => row.Key).Should().BeEquivalentTo(CoveredPrerequisites,
            "every implemented Rapportmottak prerequisite has its own row in the category theory; none is untested");
    }

    [Fact]
    public async Task NoFalseReadiness_ConfiguredAndResolvedNeverMeanVerifiedObservedOrPassed()
    {
        var bed = await ReadyAsync();
        var view = await RapportmottakAsync(bed);

        view.RunnableScenarios.Should().Contain("valid-report", "every input is resolved");
        // configured scopes ≠ verified scopes; a configured credential reference ≠ an authenticated reporter
        bed.Harness.MessageFlows.Configuration.ConfiguredScopes.Should().NotBeEmpty();
        bed.Harness.MessageFlows.Configuration.VerifiedScopes.Should().BeEmpty("resolving never verifies a scope");
        Row(view, "synthetic-reporter").Detail.Should().Contain("Configured is not authenticated");
        // a synthetic reporter alias ≠ a verified reporter profile; contract configured ≠ runtime message validated
        Row(view, "report-xsd").State.Should().NotBe(JourneyReadinessState.Ready, "an XSD in source is never a validated runtime message");
        // message received ≠ MM processed ≠ Skolenærværsrapport accepted: no downstream step is verifiable before a run
        view.StepStatus.Where(step => step.StepId is not ("altinn-instance" or "altinn-data" or "altinn-validate" or "altinn-submit"))
            .Should().OnlyContain(step => step.State != JourneyStepState.Verified && !step.Verifiable);
        view.Maturity.RuntimeVerified.Should().BeFalse("ready to run is not runtime verified, and never passed");
        view.Readiness.Should().Be(JourneyReadinessState.Ready, "execution readiness: the journey may be started after confirmation");
        view.LastRun.Should().BeNull("ready to run is not a run, and never a pass");
    }

    [Fact]
    public async Task ReportSchema_ComesFromTheCanonicalIqrBinding_AndALegacyChoiceInAnotherSnapshotIsReviewRequired()
    {
        var bound = new IqrSourceSnapshot { Id = Guid.NewGuid(), Archive = new("skole-app.zip", new string('b', 64), 1), AnalyzedAt = DateTimeOffset.UtcNow };
        var bed = await ReadyAsync(configuration: AltinnTestKit.ReadyConfiguration with { SourceSnapshotId = bound.Id, SourceContractId = "report.xsd" });
        bed.Harness.Sources.Snapshots.Add(bound);
        bed.Harness.Bindings.Bind(ReportIntake, bound);
        Row(await RapportmottakAsync(bed), "report-xsd").State.Should().Be(JourneyReadinessState.Partial, "the schema is bound through the canonical binding; XSD exists ≠ payload validated");

        bed.Harness.MessageFlows.Configuration = AltinnTestKit.ReadyConfiguration with { SourceSnapshotId = Guid.NewGuid(), SourceContractId = "report.xsd" };
        var mismatch = Row(await RapportmottakAsync(bed), "report-xsd");
        mismatch.State.Should().Be(JourneyReadinessState.NotReady);
        mismatch.Detail.Should().StartWith("Review required").And.Contain("nothing was reassigned automatically");
    }

    // ── Source guidance from the app's OpenAPI document (read-only fixture) ─────────────────────────────────────────────────

    private static IqrSourceSnapshot? FixtureSnapshot()
    {
        if (BirkNext.Api.Tests.Services.SourceAnalysis.Evidence.OpenApiConfigurationEvidenceTests.Fixture() is not { } text) return null;
        return new IqrSourceSnapshot
        {
            Id = Guid.NewGuid(), Archive = new("skolenaervaer-app.zip", new string('e', 64), 1), AnalyzedAt = DateTimeOffset.UtcNow,
            EvidenceDomains = BirkNext.Api.Tests.Services.SourceAnalysis.Evidence.SourceEvidenceFixtures.Analyze(("skolenaervaer.json", text)),
        };
    }

    private static async Task<IReadOnlyDictionary<string, BirkNext.SourceDomains.SourceConfigurationSuggestion>> GuidanceAsync(Bed bed) =>
        (await Service(bed).SourceGuidanceAsync(Env, SkoleNaervaerJourneyPack.Id, default)).Suggestions.ToDictionary(item => item.Requirement.Key);

    [Fact]
    [Trait("Category", "LocalFixture")]
    public async Task BoundOpenApi_SuggestsTheAltinnValues_WithProvenance_AndNeverConfiguresOrVerifiesThem()
    {
        if (FixtureSnapshot() is not { } snapshot) return;
        var bed = await ReadyAsync();
        bed.Harness.Sources.Snapshots.Add(snapshot);
        bed.Harness.Bindings.Bind(ReportIntake, snapshot);
        var before = bed.Harness.MessageFlows.Configuration;
        var readinessBefore = (await RapportmottakAsync(bed)).Prerequisites.Select(row => (row.Key, row.State)).ToArray();

        var guidance = await GuidanceAsync(bed);
        var fromOpenApi = new[] { "altinn-app-route", "altinn-test-server", "altinn-form-data-type", "altinn-content-type", "altinn-process-task", "altinn-auth-scheme" };
        fromOpenApi.Select(key => guidance[key]).Should().OnlyContain(item => item.State == BirkNext.SourceDomains.SourceSuggestionState.Suggested && !item.RuntimeVerified);
        guidance.Values.Should().OnlyContain(item => !item.RuntimeVerified);
        guidance["altinn-event-webhook"].State.Should().Be(BirkNext.SourceDomains.SourceSuggestionState.NotDeclared, "an API document declares no event subscription");
        guidance["altinn-app-route"].Value.Should().Be("buf/skolenaervaer");
        guidance["altinn-test-server"].Value.Should().Be("https://buf.apps.tt02.altinn.no");
        guidance["altinn-form-data-type"].Value.Should().Be("bufdir_barnevern_tiltak_skolenaervaer_v2_0_0_altinn");
        guidance["altinn-content-type"].Value.Should().Be("application/xml");
        guidance["altinn-process-task"].Value.Should().Be("Task_1");
        guidance["altinn-auth-scheme"].Value.Should().Be("Bearer JWT");
        guidance["altinn-test-server"].Candidates.Single().Provenance.Single().Location.Should().Be("servers[1]");
        fromOpenApi.Select(key => guidance[key]).Should().OnlyContain(item => item.BoundSources.SequenceEqual(new[] { "skolenaervaer-app.zip" }), "provenance names the bound snapshot");
        guidance["altinn-process-task"].Requirement.EvidenceType.Should().Be("OpenAPI / Swagger document", "no process.bpmn is required for the task");

        // Configured next to suggested — and nothing was written or changed by reading guidance.
        guidance["altinn-app-route"].ConfiguredValue.Should().Be(before.AppIdentifier);
        guidance["altinn-test-server"].ConfiguredValue.Should().Be("https://buf.apps.tt02.altinn.no", "the backend's test host for the configured app agrees with the source");
        bed.Harness.MessageFlows.Configuration.Should().Be(before, "a suggestion is never applied");
        (await RapportmottakAsync(bed)).Prerequisites.Select(row => (row.Key, row.State)).Should().Equal(readinessBefore, "declared values never change readiness by themselves");
    }

    [Fact]
    [Trait("Category", "LocalFixture")]
    public async Task UnboundOpenApi_IsAvailableButNotBound_AndOffersNoValue()
    {
        if (FixtureSnapshot() is not { } snapshot) return;
        var bed = await ReadyAsync();
        bed.Harness.Sources.Snapshots.Add(snapshot);
        var guidance = await GuidanceAsync(bed);
        guidance.Values.Where(item => item.State != BirkNext.SourceDomains.SourceSuggestionState.NotDeclared)
            .Should().OnlyContain(item => item.State == BirkNext.SourceDomains.SourceSuggestionState.AvailableNotBound && item.Value == null && item.Candidates.Count == 0);
        guidance.Values.Should().NotContain(item => item.Value != null, "an unbound snapshot offers no value");
        guidance["altinn-test-server"].UnboundSources.Should().Equal("skolenaervaer-app.zip");
        guidance["altinn-test-server"].Detail.Should().Contain("not bound in Integration Quality Review").And.Contain("not source verified");
    }

    [Fact]
    [Trait("Category", "LocalFixture")]
    public async Task AnExplicitlyConfiguredValue_IsShownNextToADifferentSuggestion_AndNeverOverwritten()
    {
        if (FixtureSnapshot() is not { } snapshot) return;
        var bed = await ReadyAsync(configuration: AltinnTestKit.ReadyConfiguration with { AppIdentifier = "buf/another-app" }, liveContract: false);
        bed.Harness.Sources.Snapshots.Add(snapshot);
        bed.Harness.Bindings.Bind(ReportIntake, snapshot);
        var route = (await GuidanceAsync(bed))["altinn-app-route"];
        route.ConfiguredValue.Should().Be("buf/another-app");
        route.Value.Should().Be("buf/skolenaervaer");
        route.ConfiguredDiffers.Should().BeTrue();
        bed.Harness.MessageFlows.Configuration.AppIdentifier.Should().Be("buf/another-app", "explicit configuration wins and is never silently replaced");
    }

    [Fact]
    public async Task WithoutAnySource_TheGuidanceNamesTheExpectedEvidenceType_NotAFile()
    {
        var bed = await ReadyAsync();
        var guidance = await GuidanceAsync(bed);
        guidance.Values.Should().OnlyContain(item => item.State == BirkNext.SourceDomains.SourceSuggestionState.SourceMissing
            && item.Detail.Contains(item.Requirement.EvidenceType, StringComparison.Ordinal) && item.SourceRoleLabel != null && item.Detail.Contains(item.SourceRoleLabel, StringComparison.Ordinal),
            "without any source the required logical source is named as missing");
        guidance.Values.Should().NotContain(item => item.Detail.Contains("process.bpmn", StringComparison.Ordinal) || item.Detail.Contains("skolenaervaer.json", StringComparison.Ordinal),
            "a requirement names the evidence type, never a particular file");
    }

    // ── Safe source-derived setup from the bound report-intake source (message hub configuration) ─────────────────────────

    /// <summary>A message hub's development settings as Source Analysis reads them: two Altinn event subscriptions (two apps) and an app id.
    /// Synthetic; shaped like a real hub's configuration. Nothing here is contacted.</summary>
    private static string HubSettings(string skoleEndpoint = "https://hub.example.invalid/api/altinn/webhook", string? extraSkoleEndpoint = null)
    {
        string Subscription(string trigger, string endpoint, string app) =>
            "{ \"Trigger\": \"" + trigger + "\", \"Endpoint\": \"" + endpoint + "\", \"SourceFilter\": \"https://buf.apps.tt02.altinn.no/buf/" + app + "\" }";
        var subscriptions = new List<string> { Subscription("Webhook", skoleEndpoint, "skolenaervaer"), Subscription("FormOrder", "https://hub.example.invalid/api/altinn/order-form", "other-app") };
        if (extraSkoleEndpoint is not null) subscriptions.Add(Subscription("Webhook", extraSkoleEndpoint, "skolenaervaer"));
        return "{ \"Altinn\": { \"PlatformUrl\": \"https://platform.tt02.altinn.no\", \"StorageAppId\": \"buf/skolenaervaer\" }, "
            + "\"AltinnWebhookSettings\": { \"Subscriptions\": [ " + string.Join(", ", subscriptions) + " ] } }";
    }

    private static IqrSourceSnapshot HubSnapshot(string archive, string settings, DateTimeOffset? at = null) => new()
    {
        Id = Guid.NewGuid(), Archive = new(archive, new string('c', 64), 1), AnalyzedAt = at ?? DateTimeOffset.UtcNow,
        Projects = [new("Buf.Meldingsutveksler.Service", "Hub.Service/Hub.Service.csproj", false, "Web", SourceConfidence.Confirmed)],
        EvidenceDomains = BirkNext.Api.Tests.Services.SourceAnalysis.Evidence.SourceEvidenceFixtures.Analyze(("Hub.Service/Hub.Service.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\"></Project>"),
            ("Hub.Service/appsettings.Development.json", settings)),
    };

    private static async Task<Bed> UnconfiguredAsync() =>
        await ReadyAsync(configuration: AltinnTestKit.ReadyConfiguration with { Environment = ExternalTestEnvironment.Unknown, AppIdentifier = null, BaseUrlReference = null, EndpointReference = null }, liveContract: false);

    private static int MandatoryReady(IntegrationJourneyView view) => view.Prerequisites.Count(item => item.Mandatory && item.State == JourneyReadinessState.Ready);

    [Fact]
    public async Task TheHubConfiguration_DeclaresTheAppRouteBaseUrlTestEnvironmentAndWebhook_PerApp_WithoutConflict()
    {
        var values = SourceConfigurationValueCollectorValues(HubSnapshot("hub.zip", HubSettings()));
        values.Where(value => value.SemanticKey == SourceConfigurationKeys.ApplicationRoute && value.Scope == "buf/skolenaervaer")
            .Select(value => value.Location).Should().HaveCount(2, "the subscription's source filter and the app id agree");
        values.Should().Contain(value => value.SemanticKey == SourceConfigurationKeys.ApplicationBaseUrl && value.Value == "https://buf.apps.tt02.altinn.no/buf/skolenaervaer");
        values.Should().Contain(value => value.SemanticKey == SourceConfigurationKeys.ApplicationEnvironment && value.Value == "Test" && value.Scope == "buf/skolenaervaer");
        var webhook = values.Single(value => value.SemanticKey == SourceConfigurationKeys.EndpointEventWebhook && value.Scope == "buf/skolenaervaer");
        webhook.Value.Should().Be("https://hub.example.invalid/api/altinn/webhook");
        webhook.Attributes.Should().Contain("Trigger", "Webhook");
        webhook.Artifact.Should().Be("Hub.Service/appsettings.Development.json");
        SourceConfigurationResolver.Resolve(values).Should().NotContain(item => item.State == SourceConfigurationResolutionState.Conflict,
            "two subscribed apps are two scopes, not one app with two routes");
    }

    private static IReadOnlyList<SourceConfigurationValue> SourceConfigurationValueCollectorValues(IqrSourceSnapshot snapshot) =>
        BirkNext.Api.Services.SourceAnalysis.Evidence.SourceConfigurationValueCollector.Values(snapshot.EvidenceDomains);

    [Fact]
    public async Task ApplyingSafeSourceConfiguration_FillsOnlyEmptyFields_AndResolvesExactlyEnvironmentAndApp()
    {
        var bed = await UnconfiguredAsync();
        var hub = HubSnapshot("Tiltak hub.zip", HubSettings());
        bed.Harness.Sources.Snapshots.Add(hub);
        bed.Harness.Bindings.Bind(ReportIntake, hub);
        var before = await RapportmottakAsync(bed);
        Row(before, "altinn-environment").State.Should().NotBe(JourneyReadinessState.Ready);
        Row(before, "altinn-app").State.Should().NotBe(JourneyReadinessState.Ready);
        var otherBefore = before.Prerequisites.Where(item => item.Key is not ("altinn-environment" or "altinn-app")).Select(item => (item.Key, item.State)).ToArray();

        var guidance = await GuidanceAsync(bed);
        guidance["altinn-environment"].Value.Should().Be("Test");
        guidance["altinn-app-route"].Value.Should().Be("buf/skolenaervaer");
        guidance["altinn-app-route"].Candidates.Single().Provenance.Should().HaveCount(2, "source filter and app id agree");
        bed.Harness.MessageFlows.Configuration.AppIdentifier.Should().BeNull("reading guidance applies nothing");

        var result = await Service(bed).ApplySourceConfigurationAsync(Env, SkoleNaervaerJourneyPack.Id, default);
        result.Applied.Select(item => item.Key).Should().BeEquivalentTo(["altinn-environment", "altinn-app-route", "altinn-base-url", "altinn-event-webhook"]);
        result.Applied.Should().OnlyContain(item => item.Provenance.Contains("Hub.Service/appsettings.Development.json", StringComparison.Ordinal));
        var config = bed.Harness.MessageFlows.Configuration;
        config.Environment.Should().Be(ExternalTestEnvironment.Test);
        config.AppIdentifier.Should().Be("buf/skolenaervaer");
        config.BaseUrlReference.Should().Be("https://buf.apps.tt02.altinn.no/buf/skolenaervaer");
        config.EndpointReference.Should().Be("https://hub.example.invalid/api/altinn/webhook");
        config.ClientReference.Should().Be(AltinnTestKit.ReadyConfiguration.ClientReference, "nothing else is touched");
        config.VerifiedScopes.Should().BeEmpty("source never verifies");

        var after = await RapportmottakAsync(bed);
        Row(after, "altinn-environment").State.Should().Be(JourneyReadinessState.Ready);
        Row(after, "altinn-app").State.Should().Be(JourneyReadinessState.Ready);
        // Environment and app resolve from the applied values; the app policy resolves because the backend holds an independently confirmed
        // policy for that app — it only needed to know which app. Nothing else changes.
        after.Prerequisites.Where(item => before.Prerequisites.Single(old => old.Key == item.Key).State != item.State).Select(item => item.Key)
            .Should().BeEquivalentTo(["altinn-environment", "altinn-app", "app-policy"]);
        MandatoryReady(after).Should().Be(MandatoryReady(before) + 3);
        after.Prerequisites.Where(item => item.Key is not ("altinn-environment" or "altinn-app" or "app-policy")).Select(item => (item.Key, item.State)).Should().Equal(
            otherBefore.Where(item => item.Key != "app-policy"), "no unrelated blocker changes");
        after.Maturity.RuntimeVerified.Should().BeFalse();
    }

    [Fact]
    public async Task ExplicitConfiguration_IsNeverOverwritten_ByApplying()
    {
        var bed = await ReadyAsync(configuration: AltinnTestKit.ReadyConfiguration with { EndpointReference = "https://explicit.example.invalid/hook", BaseUrlReference = null }, liveContract: false);
        var hub = HubSnapshot("hub.zip", HubSettings());
        bed.Harness.Sources.Snapshots.Add(hub);
        bed.Harness.Bindings.Bind(ReportIntake, hub);
        var before = bed.Harness.MessageFlows.Configuration;
        var result = await Service(bed).ApplySourceConfigurationAsync(Env, SkoleNaervaerJourneyPack.Id, default);
        result.Applied.Select(item => item.Key).Should().Equal("altinn-base-url");
        result.NotApplied.Should().Contain(item => item.Key == "altinn-event-webhook" && item.Reason.Contains("already configured"));
        bed.Harness.MessageFlows.Configuration.EndpointReference.Should().Be("https://explicit.example.invalid/hook");
        bed.Harness.MessageFlows.Configuration.AppIdentifier.Should().Be(before.AppIdentifier);
    }

    [Fact]
    public async Task AnUnboundSnapshot_OrANewerUnboundOne_ConfiguresNothing()
    {
        var bed = await UnconfiguredAsync();
        bed.Harness.Sources.Snapshots.Add(HubSnapshot("unbound.zip", HubSettings()));
        var unbound = await Service(bed).ApplySourceConfigurationAsync(Env, SkoleNaervaerJourneyPack.Id, default);
        unbound.Applied.Should().BeEmpty();
        unbound.NotApplied.Should().OnlyContain(item => item.Reason.StartsWith("Available but not bound", StringComparison.Ordinal));
        bed.Harness.MessageFlows.Configuration.AppIdentifier.Should().BeNull();

        var bound = HubSnapshot("bound.zip", HubSettings("https://bound.example.invalid/hook"), DateTimeOffset.UtcNow.AddDays(-3));
        bed.Harness.Sources.Snapshots.Add(bound);
        bed.Harness.Bindings.Bind(ReportIntake, bound);
        bed.Harness.Sources.Snapshots.Add(HubSnapshot("newer.zip", HubSettings("https://newer.example.invalid/hook"), DateTimeOffset.UtcNow.AddDays(1)));
        await Service(bed).ApplySourceConfigurationAsync(Env, SkoleNaervaerJourneyPack.Id, default);
        bed.Harness.MessageFlows.Configuration.EndpointReference.Should().Be("https://bound.example.invalid/hook", "a newer unbound snapshot never replaces the bound one");
    }

    [Fact]
    public async Task ConflictingValuesForTheSameApp_AreReviewRequired_AndNotApplied()
    {
        var bed = await UnconfiguredAsync();
        var hub = HubSnapshot("hub.zip", HubSettings(extraSkoleEndpoint: "https://second.example.invalid/hook"));
        bed.Harness.Sources.Snapshots.Add(hub);
        bed.Harness.Bindings.Bind(ReportIntake, hub);
        (await GuidanceAsync(bed))["altinn-event-webhook"].State.Should().Be(SourceSuggestionState.Conflict);
        var result = await Service(bed).ApplySourceConfigurationAsync(Env, SkoleNaervaerJourneyPack.Id, default);
        result.NotApplied.Should().ContainSingle(item => item.Key == "altinn-event-webhook").Which.Reason.Should().StartWith("Review required");
        bed.Harness.MessageFlows.Configuration.EndpointReference.Should().BeNull();
        result.Applied.Select(item => item.Key).Should().Contain("altinn-app-route", "an unrelated value without conflict is still applied");
    }

    [Fact]
    public async Task ACredentialBearingSourceValue_IsNeverApplied()
    {
        var bed = await UnconfiguredAsync();
        var hub = HubSnapshot("hub.zip", HubSettings("https://hub.example.invalid/api/altinn/webhook?code=SECRET_SENTINEL_abc"));
        bed.Harness.Sources.Snapshots.Add(hub);
        bed.Harness.Bindings.Bind(ReportIntake, hub);
        await Service(bed).ApplySourceConfigurationAsync(Env, SkoleNaervaerJourneyPack.Id, default);
        (bed.Harness.MessageFlows.Configuration.EndpointReference ?? "").Should().NotContain("SECRET_SENTINEL").And.NotContain("code=");
        JsonSerializer.Serialize(await GuidanceAsync(bed)).Should().NotContain("SECRET_SENTINEL");
    }

    [Fact]
    [Trait("Category", "LocalFixture")]
    public async Task TheLocalOpenApiFixture_NeverConfiguresTheRealIntegration_WhenItIsNotTheBoundSource()
    {
        if (FixtureSnapshot() is not { } fixture) return;
        var bed = await UnconfiguredAsync();
        var hub = HubSnapshot("Tiltak hub.zip", HubSettings());
        bed.Harness.Sources.Snapshots.AddRange([hub, fixture]);
        bed.Harness.Bindings.Bind(ReportIntake, hub);
        var result = await Service(bed).ApplySourceConfigurationAsync(Env, SkoleNaervaerJourneyPack.Id, default);
        result.Applied.Should().OnlyContain(item => !item.Provenance.Contains("skolenaervaer.json", StringComparison.Ordinal), "only the bound import supplies values");
        (await GuidanceAsync(bed)).Values.SelectMany(item => item.BoundSources).Should().OnlyContain(name => name == "Tiltak hub.zip");
    }
}
