using System.Net;
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
/// Altinn TT02 submission for Skolenærvær Rapportmottak: host/app/environment policy, ID-porten TEST sign-in (PKCE, single-use state,
/// in-memory token), the app contract from uploaded files and public metadata, typed synthetic inputs, and the shared journey run in which
/// only the Altinn steps can be evidenced. All network traffic goes to in-process fakes.
/// </summary>
public sealed class AltinnTestingTests
{
    private const string Env = ActiveEventTestHarness.Env;
    private const string Tester = "11111111-1111-1111-1111-111111111111";

    private static IntegrationDefinition AltinnIntake => new()
    {
        Id = "dev:http:altinn-skolenaervaer", EnvironmentId = Env, DisplayName = "Skolenærvær – Altinn report intake", Kind = IntegrationKind.HttpApi,
        Enabled = true, Producer = "Altinn (TT02)", Consumer = new IntegrationConsumer { DisplayName = "MU" },
    };

    // ── Policy ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("https://test.idporten.no/token", true)]
    [InlineData("https://login.test.idporten.no/authorize", true)]
    [InlineData("https://platform.tt02.altinn.no/authentication/api/v1/exchange/id-porten", true)]
    [InlineData("https://buf.apps.tt02.altinn.no/buf/skolenaervaer/instances", true)]
    [InlineData("https://idporten.no/token", false)]
    [InlineData("https://login.idporten.no/authorize", false)]
    [InlineData("https://buf.apps.altinn.no/buf/skolenaervaer/instances", false)]
    [InlineData("https://platform.altinn.no/authentication/api/v1/exchange/id-porten", false)]
    [InlineData("https://example.com/anything", false)]
    [InlineData("http://test.idporten.no/token", false)]
    [InlineData("https://test.idporten.no:8443/token", false)]
    [InlineData("https://user:pass@test.idporten.no/token", false)]
    [InlineData("https://evil.apps.tt02.altinn.no.example.com/x", false)]
    public void OnlyIdPortenTestAndAltinnTt02Hosts_AreReachable(string url, bool allowed) =>
        (AltinnTestingPolicy.RequestBlock(new Uri(url)) is null).Should().Be(allowed);

    [Fact]
    public async Task TheRequestGuard_RefusesABlockedHost_BeforeAnythingIsSent()
    {
        var kit = new AltinnTestKit();
        var client = kit.Factory.CreateClient(AltinnTestingRequestGuard.ClientName);
        await client.Invoking(item => item.GetAsync("https://buf.apps.altinn.no/buf/skolenaervaer/api/v1/applicationmetadata"))
            .Should().ThrowAsync<AltinnSignInException>().WithMessage("*production host*");
        kit.Http.Calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData("buf/skolenaervaer", true)]
    [InlineData("buf/other-app", false)]
    [InlineData("https://buf.apps.tt02.altinn.no/buf/skolenaervaer", false)]
    [InlineData("buf/../skolenaervaer", false)]
    [InlineData("BUF/skolenaervaer", false)]
    [InlineData("", false)]
    public void OnlyExactlyApprovedAppIds_AreAccepted(string appId, bool approved) =>
        (new AltinnTestKit().Policy.ApprovedApp(appId, out _) is not null).Should().Be(approved);

    [Theory]
    [InlineData(ExternalTestEnvironment.Test, true, true)]
    [InlineData(ExternalTestEnvironment.Production, true, false)]
    [InlineData(ExternalTestEnvironment.Unknown, true, false)]
    [InlineData(ExternalTestEnvironment.QualityAssurance, true, false)]
    [InlineData(ExternalTestEnvironment.Test, false, false)]
    public void OnlyTt02WithProductionBlocked_IsAllowed(ExternalTestEnvironment environment, bool productionBlocked, bool allowed) =>
        (AltinnTestingPolicy.AltinnEnvironmentBlock(AltinnTestKit.ReadyConfiguration with { Environment = environment, ProductionBlocked = productionBlocked }) is null)
            .Should().Be(allowed);

    [Fact]
    public void SignInConfiguration_ReportsEachMissingPartSeparately()
    {
        var kit = new AltinnTestKit();
        kit.Options.IdPortenTest.RedirectUri = null;
        var problems = kit.Policy.SignInProblems(new AltinnTestConfiguration());
        problems.Should().HaveCount(4);
        problems.Should().Contain(item => item.Contains("not ID-porten")).And.Contain(item => item.Contains("client id"))
            .And.Contain(item => item.Contains("openid")).And.Contain(item => item.Contains("redirect URI"));
        kit.Options.IdPortenTest.RedirectUri = "https://birknext.example.test/somewhere-else";
        kit.Policy.SignInProblems(AltinnTestKit.ReadyConfiguration).Should().ContainSingle().Which.Should().Contain("must be https");
    }

    // ── ID-porten TEST sign-in ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SignIn_BuildsAPkceRequest_OnIdPortenTestOnly_WithoutLoginHintOrSecret()
    {
        var kit = new AltinnTestKit();
        kit.Options.IdPortenTest.ClientSecret = AltinnTestKit.ClientSecret;
        var flows = new IntegrationJourneyHarness.FakeMessageFlows { Configuration = AltinnTestKit.ReadyConfiguration };
        var start = await kit.SignIn(flows, new AltinnTestSessionCache(TimeProvider.System)).StartAsync(Tester, Env, default);

        var url = new Uri(start.AuthorizationUrl);
        url.Host.Should().Be("login.test.idporten.no");
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(url.Query);
        query["response_type"].ToString().Should().Be("code");
        query["client_id"].ToString().Should().Be("birknext-test-client");
        query["code_challenge_method"].ToString().Should().Be("S256");
        query["code_challenge"].ToString().Should().MatchRegex("^[A-Za-z0-9_-]{43}$");
        query["state"].ToString().Should().NotBeEmpty();
        query["scope"].ToString().Should().Be("openid altinn:instances.read altinn:instances.write");
        query["response_mode"].ToString().Should().Be("form_post", "the code stays out of URLs when ID-porten offers form_post");
        query.ContainsKey("login_hint").Should().BeFalse("ID-porten documents no TestID identity hint");
        start.AuthorizationUrl.Should().NotContain(AltinnTestKit.ClientSecret);
    }

    [Fact]
    public async Task SignIn_RefusesADiscoveryDocument_ThatIsNotIdPortenTest()
    {
        var kit = new AltinnTestKit();
        var flows = new IntegrationJourneyHarness.FakeMessageFlows { Configuration = AltinnTestKit.ReadyConfiguration };
        kit.Http.DiscoveryIssuer = "https://idporten.no";
        await kit.SignIn(flows, new AltinnTestSessionCache(TimeProvider.System)).Invoking(item => item.StartAsync(Tester, Env, default))
            .Should().ThrowAsync<AltinnSignInException>().WithMessage("*issuer*");
        kit.Http.DiscoveryIssuer = "https://test.idporten.no";
        kit.Http.AuthorizationEndpoint = "https://login.idporten.no/authorize";
        await kit.SignIn(flows, new AltinnTestSessionCache(TimeProvider.System)).Invoking(item => item.StartAsync(Tester, Env, default))
            .Should().ThrowAsync<AltinnSignInException>().WithMessage("*authorization_endpoint*");
    }

    [Fact]
    public async Task SignIn_IsRefused_WhenNotConfigured_OrTheAltinnEnvironmentIsProduction()
    {
        var kit = new AltinnTestKit();
        var flows = new IntegrationJourneyHarness.FakeMessageFlows { Configuration = AltinnTestKit.ReadyConfiguration with { Environment = ExternalTestEnvironment.Production } };
        await kit.SignIn(flows, new AltinnTestSessionCache(TimeProvider.System)).Invoking(item => item.StartAsync(Tester, Env, default))
            .Should().ThrowAsync<AltinnSignInException>().WithMessage("*Production*");
        kit.Http.Calls.Should().BeEmpty("nothing is contacted before the configuration is acceptable");
    }

    [Fact]
    public async Task Completion_ExchangesTheCodeServerSide_HoldsOnlyTheAltinnToken_InMemory_AndNeverLogsSecrets()
    {
        var kit = new AltinnTestKit();
        var flows = new IntegrationJourneyHarness.FakeMessageFlows { Configuration = AltinnTestKit.ReadyConfiguration };
        var cache = new AltinnTestSessionCache(TimeProvider.System);
        var logger = new ListLogger<IdPortenTestSignIn>();
        var signIn = kit.SignIn(flows, cache, logger: logger);
        var state = State((await signIn.StartAsync(Tester, Env, default)).AuthorizationUrl);

        (await signIn.CompleteAsync(AltinnTestKit.AuthorizationCode, state, null, default)).Should().Be(Env);

        var token = kit.Http.Calls.Single(call => call.Uri.AbsolutePath == "/token");
        token.Uri.Query.Should().BeEmpty("the code travels in the POST body, never in a URL");
        token.Body.Should().Contain("grant_type=authorization_code").And.Contain("code_verifier=");
        kit.Http.Calls.Single(call => call.Uri.ToString() == AltinnTestingPolicy.AltinnTokenExchange).Authorization.Should().Be($"Bearer {AltinnTestKit.IdPortenToken}");
        cache.Get(Tester, Env).Should().Be(AltinnTestKit.AltinnToken);
        cache.Get("someone-else", Env).Should().BeNull("a token belongs to the person who signed in");
        (await signIn.StatusAsync(Tester, Env, default)).SignedIn.Should().BeTrue();
        string.Join("\n", logger.Messages).Should().NotContainAny(AltinnTestKit.AuthorizationCode, AltinnTestKit.IdPortenToken, AltinnTestKit.AltinnToken);

        await signIn.Invoking(item => item.CompleteAsync(AltinnTestKit.AuthorizationCode, state, null, default))
            .Should().ThrowAsync<AltinnSignInException>("a state is single-use");
        signIn.SignOut(Tester, Env);
        cache.Get(Tester, Env).Should().BeNull();
    }

    [Fact]
    public async Task Completion_RefusesUnknownOrExpiredState_AndAnIdPortenError()
    {
        var kit = new AltinnTestKit();
        var flows = new IntegrationJourneyHarness.FakeMessageFlows { Configuration = AltinnTestKit.ReadyConfiguration };
        var clock = new ManualClock(DateTimeOffset.Parse("2026-10-12T08:00:00Z"));
        var cache = new AltinnTestSessionCache(clock);
        var signIn = kit.SignIn(flows, cache, clock);

        await signIn.Invoking(item => item.CompleteAsync("code", "never-issued", null, default)).Should().ThrowAsync<AltinnSignInException>();
        var expired = State((await signIn.StartAsync(Tester, Env, default)).AuthorizationUrl);
        clock.Advance(TimeSpan.FromMinutes(11));
        await signIn.Invoking(item => item.CompleteAsync("code", expired, null, default)).Should().ThrowAsync<AltinnSignInException>().WithMessage("*expired*");
        var refused = State((await signIn.StartAsync(Tester, Env, default)).AuthorizationUrl);
        await signIn.Invoking(item => item.CompleteAsync(null, refused, "access_denied", default)).Should().ThrowAsync<AltinnSignInException>();
        kit.Http.Calls.Should().NotContain(call => call.Uri.AbsolutePath == "/token");
        cache.Get(Tester, Env).Should().BeNull();
    }

    [Fact]
    public async Task HeldTokens_Expire()
    {
        var kit = new AltinnTestKit();
        var flows = new IntegrationJourneyHarness.FakeMessageFlows { Configuration = AltinnTestKit.ReadyConfiguration };
        var clock = new ManualClock(DateTimeOffset.Parse("2026-10-12T08:00:00Z"));
        var cache = new AltinnTestSessionCache(clock);
        var signIn = kit.SignIn(flows, cache, clock);
        await signIn.CompleteAsync("code", State((await signIn.StartAsync(Tester, Env, default)).AuthorizationUrl), null, default);
        cache.Get(Tester, Env).Should().NotBeNull();
        clock.Advance(TimeSpan.FromSeconds(121));
        cache.Get(Tester, Env).Should().BeNull("the token lives no longer than ID-porten's expires_in (120 s here)");
    }

    [Fact]
    public async Task SignIn_HonoursCancellation()
    {
        var kit = new AltinnTestKit();
        var flows = new IntegrationJourneyHarness.FakeMessageFlows { Configuration = AltinnTestKit.ReadyConfiguration };
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await kit.SignIn(flows, new AltinnTestSessionCache(TimeProvider.System)).Invoking(item => item.StartAsync(Tester, Env, cancelled.Token))
            .Should().ThrowAsync<OperationCanceledException>();
    }

    // ── App contract ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PublicMetadata_IsReadWithThreeAnonymousGets_AndTheDeployedOpenApiConfirmsTheProcessOperation_WithoutProcessBpmn()
    {
        var kit = new AltinnTestKit();
        var flows = new IntegrationJourneyHarness.FakeMessageFlows { Configuration = AltinnTestKit.ReadyConfiguration };
        var view = await kit.Contracts(flows).DiscoverPublicAsync(Env, default);

        kit.Http.Calls.Select(call => $"{call.Method} {call.Uri.AbsolutePath}").Should().Equal(
            "GET /buf/skolenaervaer/api/v1/applicationmetadata", "GET /buf/skolenaervaer/api/jsonschema/model", "GET /buf/skolenaervaer/v1/customOpenapi.json",
            "GET /resourceregistry/api/v1/resource/app_buf_skolenaervaer/policy");
        kit.Http.Calls.Should().OnlyContain(call => call.Authorization == null, "discovery never authenticates");
        view.FormDataTypeId.Should().Be("model");
        view.Bindings.Should().OnlyContain(binding => binding.Present);
        var process = view.Parts.Single(part => part.Part == AltinnContractPartKind.ProcessDefinition);
        process.Present.Should().BeTrue();
        process.Source.Should().Be(AltinnContractSourceKind.DeployedOpenApi);
        view.Process.Status.Should().Be(AltinnProcessContractStatus.ConfirmedForCurrentOperation);
        view.Process.StaticDefinition.Should().Be("Not provided · Optional");
        view.Process.TopologyKnown.Should().BeFalse("the deployed contract does not describe gateways or events");
        view.Complete.Should().BeTrue(string.Join(" | ", view.Missing));
        var snapshot = flows.Contract!.PublicMetadata!;
        snapshot.Source.Should().Be(AltinnContractSourceKind.PublicTt02Metadata);
        snapshot.Documents.Should().Contain("v1/customOpenapi.json");
        snapshot.DeployedProcess!.Tasks.Should().ContainSingle().Which.TaskId.Should().Be("Task_1");
        System.Text.Json.JsonSerializer.Serialize(flows.Contract).Should().NotContain("Altinn 3 App API", "only normalized facts are kept, never the document");
    }

    [Fact]
    public async Task PublicMetadata_WithoutADeployedOpenApi_LeavesTheProcessNotConfirmed_AndDoesNotFail()
    {
        var kit = new AltinnTestKit();
        kit.Http.OpenApiJson = null;
        var flows = new IntegrationJourneyHarness.FakeMessageFlows { Configuration = AltinnTestKit.ReadyConfiguration };
        var view = await kit.Contracts(flows).DiscoverPublicAsync(Env, default);

        view.Process.Status.Should().Be(AltinnProcessContractStatus.NotConfirmed);
        view.Complete.Should().BeFalse();
        view.Missing.Should().Contain(item => item.Contains("No process evidence"));
        flows.Contract!.PublicMetadata!.Notes.Should().Contain(item => item.Contains("deployed OpenAPI contract is not available"));
    }

    [Fact]
    public async Task PublicMetadata_CanBeSupplementedByTheAppSourceInSourceAnalysis_WhichWinsPerPart()
    {
        var kit = new AltinnTestKit();
        var flows = new IntegrationJourneyHarness.FakeMessageFlows { Configuration = AltinnTestKit.ReadyConfiguration };
        var sources = new IntegrationJourneyHarness.FakeSources();
        var contracts = kit.Contracts(flows, sources);
        await contracts.DiscoverPublicAsync(Env, default);
        sources.Snapshots.Add(AltinnTestKit.Snapshot(AltinnTestKit.AppFiles()));

        var view = await contracts.UseSourceAnalysisAsync(Env, null, default);

        view.Complete.Should().BeTrue(string.Join(" | ", view.Missing));
        view.Parts.Single(part => part.Part == AltinnContractPartKind.ProcessDefinition).Source.Should().Be(AltinnContractSourceKind.SourceAnalysisSnapshot);
        view.Parts.Single(part => part.Part == AltinnContractPartKind.DataModelSchema).Source.Should().Be(AltinnContractSourceKind.SourceAnalysisSnapshot, "the app source is authoritative");
        view.ProcessTasks.Should().Equal(new AltinnProcessTask("Task_1", "data"));
        view.Sources.Should().HaveCount(2);
        view.SourceCandidates.Should().ContainSingle().Which.ArchiveName.Should().Be("skolenaervaer-app.zip");
        flows.Contract!.SourceAnalysis!.SourceArchive.Should().Be("skolenaervaer-app.zip");
    }

    [Fact]
    public async Task TheAppSourceInSourceAnalysis_AloneGivesACompleteContract_WithoutAnyNetworkCall()
    {
        var kit = new AltinnTestKit();
        var flows = new IntegrationJourneyHarness.FakeMessageFlows { Configuration = AltinnTestKit.ReadyConfiguration };
        var sources = new IntegrationJourneyHarness.FakeSources();
        sources.Snapshots.Add(AltinnTestKit.Snapshot(AltinnTestKit.AppFiles()));

        var view = await kit.Contracts(flows, sources).UseSourceAnalysisAsync(Env, null, default);

        view.Complete.Should().BeTrue(string.Join(" | ", view.Missing));
        view.Sources.Single().Documents.Should().HaveCount(3, "only the app's contract files are used");
        kit.Http.Calls.Should().BeEmpty();
        JsonSerializer.Serialize(flows.Contract).Should().NotContain("class Calculation", "file contents are never stored");
    }

    [Fact]
    public async Task WithoutTheAppSourceInSourceAnalysis_TheTesterIsToldToUploadIt()
    {
        var kit = new AltinnTestKit();
        var flows = new IntegrationJourneyHarness.FakeMessageFlows { Configuration = AltinnTestKit.ReadyConfiguration };
        var sources = new IntegrationJourneyHarness.FakeSources();
        sources.Snapshots.Add(AltinnTestKit.Snapshot(AltinnTestKit.AppFiles(metadata: AltinnTestKit.ApplicationMetadata(id: "buf/other"))));
        await kit.Contracts(flows, sources).Invoking(item => item.UseSourceAnalysisAsync(Env, null, default))
            .Should().ThrowAsync<AltinnContractException>().WithMessage("*Upload the app repository in Source Analysis*");
        (await kit.Contracts(flows, sources).ViewAsync(Env, default)).SourceCandidates.Should().BeEmpty("a snapshot of another app is no candidate");
    }

    [Theory]
    [InlineData("ambiguous")]
    [InlineData("missing-field")]
    [InlineData("date-without-format")]
    [InlineData("xml-without-root")]
    [InlineData("confirmation-task")]
    [InlineData("xml-only")]
    public async Task IncompleteOrAmbiguousContracts_StayNotReady_WithTheReason(string variant)
    {
        var kit = new AltinnTestKit();
        var flows = new IntegrationJourneyHarness.FakeMessageFlows { Configuration = AltinnTestKit.ReadyConfiguration };
        var sources = new IntegrationJourneyHarness.FakeSources();
        var metadata = variant switch
        {
            "ambiguous" => AltinnTestKit.ApplicationMetadata(secondFormType: true),
            "xml-only" or "xml-without-root" => AltinnTestKit.ApplicationMetadata(allowed: variant == "xml-only" ? "\"application/pdf\"" : "\"application/xml\""),
            _ => AltinnTestKit.ApplicationMetadata(),
        };
        var schema = AltinnTestKit.Schema(withPlannedHours: variant != "missing-field", dateFormat: variant == "date-without-format" ? "" : "date");
        if (variant == "xml-without-root") schema = schema.Replace("\"@xsdRootElement\": \"Skolenaervaer\",", "");
        sources.Snapshots.Add(AltinnTestKit.Snapshot(AltinnTestKit.AppFiles(metadata, schema,
            AltinnTestKit.Process(variant == "confirmation-task" ? "confirmation" : ""))));

        var view = await kit.Contracts(flows, sources).UseSourceAnalysisAsync(Env, null, default);

        view.Complete.Should().BeFalse();
        var expected = variant switch
        {
            "ambiguous" => "Ambiguous",
            "missing-field" => "AntallTimerEtterPlanen",
            "date-without-format" => "Periode.StartDato",
            "confirmation-task" => "confirmation",
            "xml-without-root" => "no XML root element",
            _ => "application/json or application/xml only",
        };
        view.Missing.Should().Contain(item => item.Contains(expected));
    }

    [Fact]
    public void TheExtractor_FindsEachAppInItsOwnFolder_ToleratesBom_AndGuessesNothing()
    {
        var bom = "\uFEFF";
        var files = AltinnTestKit.AppFiles(metadata: bom + AltinnTestKit.ApplicationMetadata(), root: "repo/apps/one/App/")
            .Concat(AltinnTestKit.AppFiles(metadata: AltinnTestKit.ApplicationMetadata(id: "buf/second"), root: "repo/apps/two/App/").Where(file => !file.Path.EndsWith(".bpmn")))
            .Append(("repo/appsettings.json", """{ "id": "not-an-app" }"""))
            .ToArray();

        var apps = AltinnAppSourceExtractor.Extract(files, Guid.NewGuid(), DateTimeOffset.UnixEpoch);

        apps.Select(app => app.AppId).Should().Equal("buf/skolenaervaer", "buf/second");
        apps[0].ProcessTasks.Should().ContainSingle();
        apps[0].Fields.Should().Contain(field => field.Path == "Periode.StartDato" && field.Format == "date");
        apps[0].XmlRootElement.Should().Be("Skolenaervaer");
        apps[1].ProcessTasks.Should().BeNull("the second app's folder has no process.bpmn; another app's process is never borrowed");
        apps[1].Notes.Should().Contain(note => note.Contains("No process.bpmn"));
    }

    [Fact]
    public void SourceAnalysisIngestion_RecordsTheAltinnApp_InTheContractsDomain()
    {
        BirkNext.Api.Services.Integrations.SourceEvidence.IqrSourceArchiveReader.IsEvidenceFile("App/config/process/process.bpmn").Should().BeTrue("the process definition is read as contract evidence");
        var files = AltinnTestKit.AppFiles();
        var workspace = new BirkNext.Api.Services.Integrations.SourceEvidence.IqrSourceArchiveReader.Workspace(new SourceArchive("app.zip", new string('d', 64), 1),
            [new BirkNext.Api.Services.Integrations.ApplicationMessaging.SourceFile(files[3].Path, files[3].Content)], [],
            ConfigurationFiles: [new BirkNext.Api.Services.Integrations.ApplicationMessaging.SourceFile(files[0].Path, files[0].Content), new BirkNext.Api.Services.Integrations.ApplicationMessaging.SourceFile(files[1].Path, files[1].Content)],
            EvidenceFiles: [new BirkNext.Api.Services.Integrations.ApplicationMessaging.SourceFile(files[2].Path, files[2].Content)]);

        var domains = BirkNext.Api.Services.SourceAnalysis.Evidence.SourceEvidenceAnalyzer.Analyze(Guid.NewGuid(), workspace, DateTimeOffset.UnixEpoch, null, null, null, null);

        var app = domains.Contracts.AltinnApps.Should().ContainSingle().Subject;
        app.AppId.Should().Be(AltinnTestKit.AppId);
        app.FormDataTypeId.Should().Be("model");
        app.ProcessTasks.Should().Equal(new AltinnProcessTask("Task_1", "data"));
    }

    [Fact]
    public async Task ContractEvidence_IsKept_WhenTheIqrMessageFlowIsSavedAgain()
    {
        var services = new ServiceCollection();
        services.AddDbContext<BirkNext.Api.Data.AppDbContext>(options => Microsoft.EntityFrameworkCore.InMemoryDbContextOptionsExtensions.UseInMemoryDatabase(options, Guid.NewGuid().ToString()));
        await using var provider = services.BuildServiceProvider();
        var store = new BirkNext.Api.Services.Integrations.IntegrationMessageFlowStore(provider.GetRequiredService<BirkNext.Api.Data.AppDbContext>());
        var record = new AltinnAppContractRecord(new AltinnContractSnapshot { AppId = AltinnTestKit.AppId, Fingerprint = new string('a', 64) }, null);
        await store.SaveAppContractAsync(Env, record);

        var package = await store.GetAsync(Env);
        var (saved, error) = await store.SaveAsync(Env, package with { AltinnConfiguration = AltinnTestKit.ReadyConfiguration, AppContract = null });

        error.Should().BeNull();
        saved!.AppContract.Should().BeEquivalentTo(record);
        (await store.GetAsync(Env)).AppContract!.SourceAnalysis!.Fingerprint.Should().Be(new string('a', 64));
    }

    // ── Typed, synthetic input ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SyntheticIdentifiers_OnlyTheTestNorgeFormIsAccepted()
    {
        for (var i = 0; i < 200; i++) SyntheticPersonIdentifier.IsSynthetic(SyntheticPersonIdentifier.Generate()).Should().BeTrue();
        var synthetic = SyntheticPersonIdentifier.Generate();
        var realMonth = synthetic[..2] + (int.Parse(synthetic[2..4]) - 80).ToString("00") + synthetic[4..];
        SyntheticPersonIdentifier.IsSynthetic(realMonth).Should().BeFalse("a real-format fødselsnummer (month 01–12) is never accepted");
        // D-numbers (day + 40): the real form (month 01–12) is refused; the synthetic form (month + 80, as TestID issues) is accepted.
        static string WithControl(string nine)
        {
            int Control(string digits, int[] weights) { var c = 11 - digits.Select((d, i) => (d - '0') * weights[i]).Sum() % 11; return c == 11 ? 0 : c; }
            var k1 = Control(nine, [3, 7, 6, 1, 8, 9, 4, 5, 2]);
            if (k1 == 10) return "";
            var k2 = Control(nine + k1, [5, 4, 3, 2, 7, 6, 5, 4, 3, 2]);
            return k2 == 10 ? "" : $"{nine}{k1}{k2}";
        }
        var syntheticDNumbers = Enumerable.Range(500, 400).Select(individual => WithControl($"418212{individual:000}")).Where(id => id.Length == 11).Take(5).ToArray();
        syntheticDNumbers.Should().NotBeEmpty().And.OnlyContain(id => SyntheticPersonIdentifier.IsSynthetic(id), "a synthetic D-number (day + 40, month + 80) is a TestID person");
        var realDNumbers = Enumerable.Range(500, 400).Select(individual => WithControl($"410212{individual:000}")).Where(id => id.Length == 11).Take(5).ToArray();
        realDNumbers.Should().NotBeEmpty().And.OnlyContain(id => !SyntheticPersonIdentifier.IsSynthetic(id), "a real-format D-number (month 01–12) is refused");
        var badDays = new[] { "32", "40", "72", "00" }.Select(day => Enumerable.Range(500, 400).Select(individual => WithControl($"{day}8212{individual:000}")).First(id => id.Length == 11));
        badDays.Should().OnlyContain(id => !SyntheticPersonIdentifier.IsSynthetic(id), "days outside 01–31 and 41–71 are not identifiers");
        var hNumber = Enumerable.Range(500, 400).Select(individual => WithControl($"154212{individual:000}")).First(id => id.Length == 11);
        SyntheticPersonIdentifier.IsSynthetic(hNumber).Should().BeFalse("H-numbers (month + 40) are refused");
        SyntheticPersonIdentifier.IsSynthetic(synthetic[..10] + ((synthetic[10] - '0' + 1) % 10)).Should().BeFalse("control digits are checked");
        SyntheticPersonIdentifier.IsSynthetic("1234567890").Should().BeFalse();
        SyntheticPersonIdentifier.Reference(synthetic).Should().MatchRegex("^#[0-9a-f]{8}$").And.NotContain(synthetic);
    }

    [Theory]
    [InlineData("registered", "", "2026-10-01", "2026-10-31", "100", "90", "synthetic test identifier")]
    [InlineData("registered", "{id}", "2026-10-31", "2026-10-01", "100", "90", "on or before")]
    [InlineData("registered", "{id}", "2026-10-01", "2026-10-31", "-1", "90", "non-negative")]
    [InlineData("registered", "{id}", "2026-10-01", "2026-10-31", "NaN", "90", "finite")]
    [InlineData("registered", "{id}", "1.10.2026", "2026-10-31", "100", "90", "yyyy-MM-dd")]
    [InlineData("generated", "", "2026-10-01", "2026-10-31", "100", "90", "not allowed")]
    public void ReportInputs_AreValidatedServerSide(string source, string identifier, string from, string to, string planned, string actual, string error)
    {
        var scenario = ValidReport();
        var result = SkoleNaervaerReportInput.Prepare(scenario, Inputs(source, identifier.Replace("{id}", SyntheticPersonIdentifier.Generate()), from, to, planned, actual));
        result.Valid.Should().BeFalse();
        string.Join(" ", result.Errors).Should().Contain(error);
    }

    [Fact]
    public void ReportInputs_AttendanceAbovePlanIsAllowed_AndTheSummaryNeverContainsTheIdentifier()
    {
        var identifier = SyntheticPersonIdentifier.Generate();
        var result = SkoleNaervaerReportInput.Prepare(ValidReport(), Inputs("registered", identifier, "2026-10-01", "2026-10-31", "100", "120,5"));
        result.Valid.Should().BeTrue("attendance above plan is flagged by the domain, not rejected (DEC-50)");
        string.Join(" ", result.Summary).Should().NotContain(identifier).And.Contain(SyntheticPersonIdentifier.Reference(identifier)).And.Contain("not verified by BirkNext");

        var generated = SkoleNaervaerReportInput.Prepare(Scenario("person-not-found"), Inputs("generated", "", "2026-10-01", "2026-10-31", "100", "80"));
        generated.Valid.Should().BeTrue();
        SyntheticPersonIdentifier.IsSynthetic(generated.Values[SkoleNaervaerAltinnForm.ChildIdentifier]).Should().BeTrue();
        string.Join(" ", generated.Summary).Should().Contain("unknown child");
    }

    [Fact]
    public void TheSchemaParser_ReadsTheRealAltinnShape_DoubleEncoded_Inherited_AndIntegerTyped()
    {
        var document = AltinnAppContractService.ParseSchema(AltinnTestKit.Schema(doubleEncoded: true));
        document.XmlRootElement.Should().Be("Skolenaervaer");
        document.XmlNamespace.Should().Be("https://example.test/skolenaervaer.v2.altinn");
        document.XmlElementsQualified.Should().BeTrue();
        document.Fields.Where(field => field.Type != "object").Select(field => (field.Path, field.Type, field.Format, field.Required)).Should().Equal(
            ("Meldingshode.Id", "string", null, true), ("Meldingshode.SendtTidspunkt", "string", "date-time", true),
            ("Klient.Identifikator.Fodselsnummer", "string", null, false), ("Periode.StartDato", "string", "date", true), ("Periode.SluttDato", "string", "date", true),
            ("AntallTimerEtterPlanen", "integer", null, false), ("AntallTimerFaktiskNaervaer", "integer", null, false));
    }

    [Fact]
    public void TheFormModel_UsesTheVerifiedSchemaPaths_TheSchemasDateFormat_AndWholeHours()
    {
        var fields = AltinnAppContractService.FlattenSchema(AltinnTestKit.Schema());
        var input = new SkoleNaervaerReportInput("registered", "12345678901", new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31), 100, 92);
        var (model, problem) = SkoleNaervaerAltinnExecutor.BuildModel(input, fields);
        problem.Should().BeNull();
        model!.ToJsonString().Should().Be("""{"Klient":{"Identifikator":{"Fodselsnummer":"12345678901"}},"Periode":{"StartDato":"2026-10-01","SluttDato":"2026-10-31"},"AntallTimerEtterPlanen":100,"AntallTimerFaktiskNaervaer":92}""");
        var dateTime = SkoleNaervaerAltinnExecutor.BuildModel(input, AltinnAppContractService.FlattenSchema(AltinnTestKit.Schema(dateFormat: "date-time"))).Model!;
        dateTime["Periode"]!["StartDato"]!.GetValue<string>().Should().Be("2026-10-01T00:00:00");
        SkoleNaervaerAltinnExecutor.BuildModel(input with { ActualHours = 92.5 }, fields).Problem.Should().Contain("integer");
    }

    [Fact]
    public void TheXmlForm_FollowsTheSchemasRootNamespaceAndElementOrder()
    {
        var schema = AltinnAppContractService.ParseSchema(AltinnTestKit.Schema());
        var input = new SkoleNaervaerReportInput("registered", "12345678901", new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31), 100, 92);
        var model = SkoleNaervaerAltinnExecutor.BuildModel(input, schema.Fields).Model!;
        // Insertion order differs from schema order on purpose: hours first.
        var reordered = new System.Text.Json.Nodes.JsonObject { ["AntallTimerFaktiskNaervaer"] = 92, ["AntallTimerEtterPlanen"] = 100,
            ["Periode"] = model["Periode"]!.DeepClone(), ["Klient"] = model["Klient"]!.DeepClone() };

        var xml = SkoleNaervaerAltinnExecutor.ToXml(reordered, schema.Fields, schema.XmlRootElement!, schema.XmlNamespace!, schema.XmlElementsQualified);

        xml.Should().Be("""<?xml version="1.0" encoding="utf-8"?><Skolenaervaer xmlns="https://example.test/skolenaervaer.v2.altinn"><Klient><Identifikator><Fodselsnummer>12345678901</Fodselsnummer></Identifikator></Klient><Periode><StartDato>2026-10-01</StartDato><SluttDato>2026-10-31</SluttDato></Periode><AntallTimerEtterPlanen>100</AntallTimerEtterPlanen><AntallTimerFaktiskNaervaer>92</AntallTimerFaktiskNaervaer></Skolenaervaer>""");
    }

    [Fact]
    public async Task AnXmlOnlyForm_IsSubmittedAsXml_AndFractionalHoursStopBeforeAnythingIsSent()
    {
        var kit = new AltinnTestKit().ConfigureUnattended();
        await using var harness = Harness(kit);
        harness.MessageFlows.Configuration = AltinnTestKit.ReadyConfiguration;
        harness.Sources.Snapshots.Add(AltinnTestKit.Snapshot(AltinnTestKit.AppFiles(metadata: AltinnTestKit.ApplicationMetadata(allowed: "\"application/xml\""))));
        var view = await kit.Contracts(harness.MessageFlows, harness.Sources).UseSourceAnalysisAsync(Env, null, default);
        view.SubmissionContentType.Should().Be("application/xml");
        harness.Tokens.Hold(Tester, Env, AltinnTestKit.AltinnToken);

        var fractional = await Service(harness, kit).StartAsync(Request("valid-report", Inputs("registered", SyntheticPersonIdentifier.Generate(), "2026-10-01", "2026-10-31", "100", "92.5")), Tester, default);
        fractional.Steps[0].State.Should().Be(JourneyStepState.Unavailable);
        fractional.Steps[0].Reason.Should().Contain("integer").And.Contain("Nothing was sent to Altinn");
        kit.Http.Calls.Should().BeEmpty("no instance is created for a form that cannot be filled");

        var run = await Service(harness, kit).StartAsync(Request("valid-report", Inputs("registered", SyntheticPersonIdentifier.Generate(), "2026-10-01", "2026-10-31", "100", "92")), Tester, default);
        run.Steps.Take(6).Should().OnlyContain(step => step.State == JourneyStepState.Verified);
        var data = kit.Http.Calls.Single(call => call.Method == HttpMethod.Put && call.Uri.AbsolutePath.Contains("/data/"));
        data.Body.Should().StartWith("<?xml").And.Contain("<Skolenaervaer xmlns=\"https://example.test/skolenaervaer.v2.altinn\">");
        run.Steps.Single(step => step.StepId == "altinn-data").Evidence.Should().Contain("application/xml");
    }

    // ── The journey: only Altinn steps are evidenced ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Readiness_ListsEachMissingPrerequisiteSeparately_AndBecomesReadyWhenComplete()
    {
        var kit = new AltinnTestKit();
        await using var harness = Harness(kit);
        var service = Service(harness, kit);

        var before = Rapportmottak(await service.PacksAsync(Env, default));
        before.CanRun.Should().BeFalse();
        before.Prerequisites.Where(item => item.Mandatory && item.State != JourneyReadinessState.Ready).Select(item => item.Key)
            .Should().Contain(["altinn-environment", "altinn-app", "reporter-auth", "submission-contract", "test-reporter", "synthetic-data", "app-policy"]);

        await kit.ConfigureReadyAsync(harness);
        var after = Rapportmottak(await service.PacksAsync(Env, default));
        after.Prerequisites.Where(item => item.Mandatory).Should().OnlyContain(item => item.State == JourneyReadinessState.Ready,
            string.Join(" | ", after.Prerequisites.Where(item => item.Mandatory && item.State != JourneyReadinessState.Ready).Select(item => $"{item.Key}: {item.Detail}")));
        after.CanRun.Should().BeTrue();
        after.Prerequisites.Single(item => item.Key == "observation").State.Should().Be(JourneyReadinessState.Partial, "MU/MM/report steps have no observer");
        after.StepStatus.Where(step => step.StepId.StartsWith("altinn-", StringComparison.Ordinal)).Should().OnlyContain(step => step.Observable, "a registered executor produces their evidence");
    }

    [Fact]
    public async Task ASubmission_EvidencesTheSixAltinnSteps_Separately_AndLeavesEveryDownstreamStepNotAssessed()
    {
        var kit = new AltinnTestKit();
        await using var harness = Harness(kit);
        await kit.ConfigureReadyAsync(harness);
        var service = Service(harness, kit);
        harness.Tokens.Hold(Tester, Env, AltinnTestKit.AltinnToken);
        var identifier = SyntheticPersonIdentifier.Generate();

        var run = await service.StartAsync(Request("valid-report", Inputs("registered", identifier, "2026-10-01", "2026-10-31", "100", "92")), Tester, default);

        run.Steps.Take(6).Select(step => (step.StepId, step.State)).Should().Equal(
            ("altinn-auth", JourneyStepState.Verified), ("altinn-instance", JourneyStepState.Verified), ("altinn-data", JourneyStepState.Verified),
            ("altinn-validate", JourneyStepState.Verified), ("altinn-submit", JourneyStepState.Verified), ("altinn-receipt", JourneyStepState.Verified));
        run.Steps.Skip(6).Should().OnlyContain(step => step.State == JourneyStepState.NotAssessed, "MU, MM, Skolenærværsrapport and Person are not observed");
        run.OverallState.Should().Be(JourneyRunState.Partial, "an Altinn receipt never completes Rapportmottak");
        run.Steps.Single(step => step.StepId == "altinn-auth").Reason.Should().Contain("Not a submission");
        run.Steps.Single(step => step.StepId == "altinn-submit").Reason.Should().Contain("Not received by MU");
        run.Steps.Single(step => step.StepId == "altinn-receipt").Evidence.Should().Contain("receipt: instance inst-6a1b2c3d").And.Contain("completed 2026-10-12");

        kit.Http.Calls.Select(call => $"{call.Method} {call.Uri.PathAndQuery.Replace(AltinnTestKit.InstanceGuid.ToString("D"), "{i}").Replace(AltinnTestKit.DataGuid.ToString("D"), "{d}")}").Should().Equal(
            "GET /buf/skolenaervaer/api/authorization/parties/current?returnPartyObject=true",
            $"POST /buf/skolenaervaer/instances?instanceOwnerPartyId={AltinnTestKit.PartyId}",
            $"PUT /buf/skolenaervaer/instances/{AltinnTestKit.PartyId}/{{i}}/data/{{d}}",
            $"GET /buf/skolenaervaer/instances/{AltinnTestKit.PartyId}/{{i}}/validate",
            $"GET /buf/skolenaervaer/instances/{AltinnTestKit.PartyId}/{{i}}",
            $"PUT /buf/skolenaervaer/instances/{AltinnTestKit.PartyId}/{{i}}/process/next",
            $"GET /buf/skolenaervaer/instances/{AltinnTestKit.PartyId}/{{i}}");
        var body = JsonDocument.Parse(kit.Http.Calls.Single(call => call.Method == HttpMethod.Put && call.Uri.AbsolutePath.Contains("/data/")).Body!).RootElement;
        body.GetProperty("Klient").GetProperty("Identifikator").GetProperty("Fodselsnummer").GetString().Should().Be(identifier);
        body.GetProperty("AntallTimerFaktiskNaervaer").GetInt32().Should().Be(92);

        var stored = JsonSerializer.Serialize(await harness.Store.GetAsync(run.RunId, default));
        stored.Should().NotContainAny(identifier, AltinnTestKit.AltinnToken, AltinnTestKit.IdPortenToken, AltinnTestKit.PartyId.ToString(), AltinnTestKit.InstanceGuid.ToString("D"));
        // History keeps the form payload only masked (Payload & Mapping Flow): the child's element holds the mask, never the identifier.
        var kept = JsonSerializer.Deserialize<IntegrationJourneyRun>(stored)!.Steps.Single(step => step.StepId == SkoleNaervaerAltinnExecutor.DataStep).Payload!;
        JsonDocument.Parse(kept.MaskedContent).RootElement.GetProperty("Klient").GetProperty("Identifikator").GetProperty("Fodselsnummer").GetString().Should().Be(SkoleNaervaerPayloadFlow.Mask);
        stored.Should().Contain(SyntheticPersonIdentifier.Reference(identifier));
        // The run's own id (N format, the runner's default step correlation) is random hex; it can contain 11 digits by chance and is not an identifier of a person.
        Regex.IsMatch(stored.Replace(run.RunId.ToString("N"), "", StringComparison.Ordinal), @"(?<!\d)\d{11}(?!\d)").Should().BeFalse("no 11-digit identifier is persisted");
    }

    [Fact]
    public async Task WithoutSignIn_NothingIsSentToAltinn_AndTheRunStopsAtAuthentication()
    {
        var kit = new AltinnTestKit();
        await using var harness = Harness(kit);
        await kit.ConfigureReadyAsync(harness);
        kit.Http.Calls.Clear();

        var run = await Service(harness, kit).StartAsync(Request("valid-report", Inputs("registered", SyntheticPersonIdentifier.Generate(), "2026-10-01", "2026-10-31", "100", "90")), Tester, default);

        run.Steps[0].State.Should().Be(JourneyStepState.Unavailable);
        run.Steps[0].Reason.Should().Contain("Not signed in").And.Contain("Nothing was sent to Altinn");
        run.Steps.Skip(1).Should().OnlyContain(step => step.State == JourneyStepState.NotAssessed);
        run.OverallState.Should().Be(JourneyRunState.NotVerified);
        kit.Http.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task ValidationErrors_StopBeforeSubmission_AndTheRunFails()
    {
        var kit = new AltinnTestKit();
        await using var harness = Harness(kit);
        await kit.ConfigureReadyAsync(harness);
        harness.Tokens.Hold(Tester, Env, AltinnTestKit.AltinnToken);
        kit.Http.ValidationJson = """[{"severity":1,"code":"required","description":"Feltet er påkrevd for 12345678901","field":"Klient.Identifikator"},{"severity":2,"code":"hint"}]""";

        var run = await Service(harness, kit).StartAsync(Request("valid-report", Inputs("registered", SyntheticPersonIdentifier.Generate(), "2026-10-01", "2026-10-31", "100", "90")), Tester, default);

        var validate = run.Steps.Single(step => step.StepId == "altinn-validate");
        validate.State.Should().Be(JourneyStepState.UnexpectedResult);
        validate.Evidence.Should().Contain("1 error(s), 1 warning(s)").And.Contain("codes required, hint").And.NotContain("12345678901", "descriptions are never kept");
        run.Steps.Single(step => step.StepId == "altinn-submit").State.Should().Be(JourneyStepState.NotAssessed);
        run.OverallState.Should().Be(JourneyRunState.Failed);
        kit.Http.Calls.Should().NotContain(call => call.Uri.AbsolutePath.EndsWith("/process/next"));
    }

    [Theory]
    [InlineData("party", HttpStatusCode.Unauthorized, "altinn-auth", "sign in again")]
    [InlineData("instance", HttpStatusCode.InternalServerError, "altinn-instance", "HTTP 500")]
    [InlineData("data", HttpStatusCode.BadRequest, "altinn-data", "HTTP 400")]
    [InlineData("validate", HttpStatusCode.ServiceUnavailable, "altinn-validate", "HTTP 503")]
    [InlineData("live", HttpStatusCode.InternalServerError, "altinn-submit", "HTTP 500")]
    [InlineData("next", HttpStatusCode.Conflict, "altinn-submit", "HTTP 409")]
    [InlineData("read", HttpStatusCode.InternalServerError, "altinn-receipt", "HTTP 500")]
    public async Task EachStageFailure_IsThatStepsEvidence_AndNothingAfterItRuns(string stage, HttpStatusCode status, string step, string evidence)
    {
        var kit = new AltinnTestKit();
        await using var harness = Harness(kit);
        await kit.ConfigureReadyAsync(harness);
        harness.Tokens.Hold(Tester, Env, AltinnTestKit.AltinnToken);
        kit.Http.Fail[stage] = status;

        var run = await Service(harness, kit).StartAsync(Request("valid-report", Inputs("registered", SyntheticPersonIdentifier.Generate(), "2026-10-01", "2026-10-31", "100", "90")), Tester, default);

        var failed = run.Steps.Single(item => item.StepId == step);
        failed.State.Should().Be(JourneyStepState.NotVerified);
        (failed.Evidence + failed.Reason).Should().Contain(evidence);
        (failed.Evidence + failed.Reason).Should().NotContain("SECRET-LOOKING", "Altinn's response body is never recorded");
        run.Steps.SkipWhile(item => item.StepId != step).Skip(1).Should().OnlyContain(item => item.State == JourneyStepState.NotAssessed);
        run.OverallState.Should().NotBe(JourneyRunState.Completed);
    }

    [Fact]
    public async Task TheExecutor_RechecksPolicyAtExecution_EvenIfReadinessWasBypassed()
    {
        var kit = new AltinnTestKit();
        await using var harness = Harness(kit);
        await kit.ConfigureReadyAsync(harness);
        harness.Tokens.Hold(Tester, Env, AltinnTestKit.AltinnToken);
        harness.MessageFlows.Configuration = AltinnTestKit.ReadyConfiguration with { Environment = ExternalTestEnvironment.Production };
        var executor = Executor(harness, kit);
        var journey = Pack(harness, kit).Journeys.Single(item => item.JourneyId == "rapportmottak");
        var trusted = BirkNext.Api.Services.ActiveEventTesting.TrustedExecutionEnvironmentRegistry.From(harness.Active.Configuration()).Resolve(Env, out _);

        var result = await executor.ExecuteAsync(new JourneyStepContext(Guid.NewGuid(), Env, trusted, journey, journey.Scenarios[0], journey.Steps[0], null, [],
            new Dictionary<string, string> { ["x"] = "y" }, Tester), default);

        result.State.Should().Be(JourneyStepState.Unavailable);
        result.Reason.Should().Contain("Production");
        kit.Http.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task UnknownOrMissingInputs_AreRefused_BeforeAnythingRuns()
    {
        var kit = new AltinnTestKit();
        await using var harness = Harness(kit);
        await kit.ConfigureReadyAsync(harness);
        var service = Service(harness, kit);
        var inputs = Inputs("registered", SyntheticPersonIdentifier.Generate(), "2026-10-01", "2026-10-31", "100", "90");

        await service.Invoking(item => item.StartAsync(Request("valid-report", new Dictionary<string, string>(inputs) { ["url"] = "https://evil.example" }), Tester, default))
            .Should().ThrowAsync<IntegrationJourneyRequestException>().WithMessage("*does not declare*");
        await service.Invoking(item => item.StartAsync(Request("valid-report", inputs.Where(item => item.Key != SkoleNaervaerAltinnForm.PeriodTo).ToDictionary()), Tester, default))
            .Should().ThrowAsync<IntegrationJourneyRequestException>().WithMessage("*Dato til*");
        await service.Invoking(item => item.StartAsync(Request("valid-report", new Dictionary<string, string>(inputs) { [SkoleNaervaerReportInput.IdentitySourceKey] = "generated", [SkoleNaervaerAltinnForm.ChildIdentifier] = "" }), Tester, default))
            .Should().ThrowAsync<IntegrationJourneyRequestException>().WithMessage("*not allowed*");
        (await harness.Store.ListAsync(new IntegrationJourneyHistoryQuery(Env), 10, default)).Should().BeEmpty("refused inputs record no run");
        kit.Http.Calls.Should().BeEmpty();
    }

    [Fact]
    public void OnlyApiRelevantReportScenarios_AreSupported_AndTheRestSayWhy()
    {
        var journey = Pack(new IntegrationJourneyHarness(), new AltinnTestKit()).Journeys.Single(item => item.JourneyId == "rapportmottak");
        journey.Scenarios.Where(item => item.Support == JourneyScenarioSupport.Supported).Select(item => item.ScenarioId)
            .Should().Equal("valid-report", "person-not-found", "overlapping-report", "instance-evidence", "message-verification");
        journey.Scenarios.Single(item => item.ScenarioId == "correction").NotAssessedBecause.Should().Contain("verified app contract");
        journey.Scenarios.Single(item => item.ScenarioId == "multiple-reporters").NotAssessedBecause.Should().Contain("approved reporters");
        journey.Scenarios.Single(item => item.ScenarioId == "valid-report").Inputs!.Single(item => item.Key == SkoleNaervaerReportInput.IdentitySourceKey).Options!
            .Select(item => item.Value).Should().Equal("registered");
        journey.Scenarios.Single(item => item.ScenarioId == "person-not-found").Inputs!.Single(item => item.Key == SkoleNaervaerReportInput.IdentitySourceKey).Options!
            .Select(item => item.Value).Should().Equal("supplied", "generated");
        journey.Scenarios.SelectMany(item => item.Inputs ?? []).Should().NotContain(item => item.Key.Contains("url", StringComparison.OrdinalIgnoreCase) || item.Key.Contains("payload", StringComparison.OrdinalIgnoreCase));
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────────────────────────────

    private static string State(string authorizationUrl) => Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri(authorizationUrl).Query)["state"].ToString();
    private static Stream Text(string value) => new MemoryStream(System.Text.Encoding.UTF8.GetBytes(value));

    /// <summary>Report inputs. The reporter defaults to the interactive fallback (a held sign-in), which these tests exercise; unattended runs
    /// are covered in UnattendedAltinnAuthenticationTests.</summary>
    private static Dictionary<string, string> Inputs(string source, string identifier, string from, string to, string planned, string actual,
        string reporter = AltinnTestingPolicy.InteractiveReporter) => new()
    {
        [SkoleNaervaerReportInput.ReporterKey] = reporter, [SkoleNaervaerReportInput.IdentitySourceKey] = source, [SkoleNaervaerAltinnForm.ChildIdentifier] = identifier, [SkoleNaervaerAltinnForm.PeriodFrom] = from,
        [SkoleNaervaerAltinnForm.PeriodTo] = to, [SkoleNaervaerAltinnForm.PlannedHours] = planned, [SkoleNaervaerAltinnForm.ActualHours] = actual,
    };

    private static JourneyScenarioDescriptor Scenario(string id) =>
        Pack(new IntegrationJourneyHarness(), new AltinnTestKit()).Journeys.Single(item => item.JourneyId == "rapportmottak").Scenarios.Single(item => item.ScenarioId == id);

    private static JourneyScenarioDescriptor ValidReport() => Scenario("valid-report");

    private static IntegrationJourneyRunRequest Request(string scenario, IReadOnlyDictionary<string, string> inputs) => new()
    {
        EnvironmentId = Env, PackId = "skolenaervaer", JourneyId = "rapportmottak", ScenarioId = scenario, Confirmed = true, Inputs = inputs,
    };

    private static IntegrationJourneyView Rapportmottak(IReadOnlyList<IntegrationJourneyPackView> packs) =>
        packs.Single().Journeys.Single(view => view.Journey.JourneyId == "rapportmottak");

    private static SkoleNaervaerJourneyPack Pack(IntegrationJourneyHarness harness, AltinnTestKit kit) => new(harness.MessageFlows, kit.Policy, kit.Contracts(harness.MessageFlows, harness.Sources));

    private static IntegrationJourneyHarness Harness(AltinnTestKit kit)
    {
        var harness = new IntegrationJourneyHarness();
        harness.Active.Catalog.Extra.Add(AltinnIntake);
        harness.Enroll(AltinnIntake.Id);
        harness.Executors.Add(Executor(harness, kit));
        return harness;
    }

    private static SkoleNaervaerAltinnExecutor Executor(IntegrationJourneyHarness harness, AltinnTestKit kit)
    {
        var services = new ServiceCollection();
        services.AddSingleton<BirkNext.Api.Services.Integrations.IIntegrationMessageFlowStore>(harness.MessageFlows);
        services.AddSingleton(kit.Policy);
        services.AddSingleton(_ => kit.Contracts(harness.MessageFlows, harness.Sources));
        return new SkoleNaervaerAltinnExecutor(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), kit.Authenticator(harness.Tokens),
            new AltinnAppApiClient(kit.Factory), TimeProvider.System);
    }

    private static IntegrationJourneyService Service(IntegrationJourneyHarness harness, AltinnTestKit kit) => harness.Service(Pack(harness, kit));
}
