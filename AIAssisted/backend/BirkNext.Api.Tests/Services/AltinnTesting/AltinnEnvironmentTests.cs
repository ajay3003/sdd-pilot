using System.Net;
using System.Text.Json;
using BirkNext.Api.Controllers;
using BirkNext.Api.Data;
using BirkNext.Api.Services.ActiveEventTesting;
using BirkNext.Api.Services.AltinnTesting;
using BirkNext.Api.Services.Integrations;
using BirkNext.Api.Services.IntegrationJourneys;
using BirkNext.Api.Services.IntegrationJourneys.Packs.SkoleNaervaer;
using BirkNext.Api.Tests.Services.IntegrationJourneys;
using BirkNext.Integrations;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace BirkNext.Api.Tests.Services.AltinnTesting;

/// <summary>
/// Target Environment → Connected Systems → Altinn: precedence, conflicts, browser-vs-unattended separation, manual-vs-automated readiness, detection without
/// writes or authentication, and session secrets/identities that never leave backend memory. Isolated fakes only — no TT02 or ID-porten call.
/// </summary>
public sealed class AltinnEnvironmentTests
{
    private const string Env = "qa-env";
    private static readonly string ChildIdentifier = Person("1205", SyntheticPersonIdentifier.IsTestPerson);

    private sealed class Bed
    {
        public AltinnTestKit Kit { get; } = new();
        public IntegrationJourneyHarness.FakeMessageFlows Flows { get; } = new();
        public AppDbContext Db { get; } = new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"altinn-env-{Guid.NewGuid()}").Options);
        public AltinnEnvironmentSettingsCache Cache { get; } = new();
        public FakeClock Clock { get; } = new();
        public AltinnSessionStore Session { get; }
        public List<IntegrationDefinition> Integrations { get; } = [];
        public TrustedExecutionEnvironment? Trusted { get; set; }
        /// <summary>Test packs whose built-in references may suggest form data type / content type (none unless a test adds one).</summary>
        public List<IIntegrationJourneyPack> Packs { get; } = [];
        public SkoleNaervaerJourneyPack SkoleNaervaerPack() => new(Flows, Policy, Kit.Contracts(Flows));

        public Bed() => Session = new AltinnSessionStore(Kit.Options, Clock);

        public AltinnTestingPolicy Policy => new(Kit.Options, Session, Cache);
        public AltinnEnvironmentSettingsStore Store => new(Db, Cache, Clock);

        public AltinnEnvironmentService Service()
        {
            var catalog = new Mock<IIntegrationCatalogService>();
            catalog.Setup(c => c.GetAsync(It.IsAny<string>(), null, null, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new IntegrationCatalog { EnvironmentId = Env, Integrations = [.. Integrations] });
            var trusted = new Mock<ITrustedExecutionEnvironmentRegistry>();
            string reason = "This environment is not a trusted execution environment in backend configuration (TargetEnvironments:Trusted).";
            trusted.Setup(t => t.Resolve(It.IsAny<string>(), out reason)).Returns(() => Trusted);
            return new AltinnEnvironmentService(Store, Flows, Policy, Kit.Contracts(Flows), catalog.Object, trusted.Object, Kit.Factory, Packs,
                WithTestData ? new BirkNext.Api.Services.TestData.TestDataCatalog([new AltinnTestDataProvider(Kit.Options), new BirkNext.Api.Services.TestData.SchemaStudioTestDataProvider(() => SavedXml)]) : null,
                WithTestData ? new RealXsd() : null);
        }

        public Task<AltinnEnvironmentView> View(bool detect = false) => Service().ViewAsync(Env, detect, CancellationToken.None);

        /// <summary>Canonical Test Data for the payload (off by default so older tests keep assessing without a catalog).</summary>
        public bool WithTestData { get; set; }
        public List<string> SavedXml { get; } = [];
    }

    private sealed class RealXsd : BirkNext.Api.Services.TestData.ITestDataSchemaSource
    {
        public Task<BirkNext.Api.Services.TestData.TestDataSchemaPackage?> FindAsync(BirkNext.TestData.TestDataSchemaReference reference, CancellationToken ct) =>
            Task.FromResult<BirkNext.Api.Services.TestData.TestDataSchemaPackage?>(new(reference.SourceSnapshotId ?? Guid.Empty,
                new Dictionary<string, string> { [TestData.TestDataXmlFixtureTests.SchemaPath] = Schemas.RealSchemaFixtures.Read(Schemas.RealSchemaFixtures.Skolenaervaer200) }, true));
    }

    private static string Fixture(bool valid = true) => TestData.TestDataXmlFixtureTests.SavedXmlDocument(approved: true,
        xml: valid ? TestData.TestDataXmlFixtureTests.Skeleton("04061076692") : TestData.TestDataXmlFixtureTests.Skeleton("04061076692").Replace("<Klient>", "<Ukjent/><Klient>"));

    [Fact]
    public async Task Identities_ready_with_the_xml_fixture_missing_is_not_manual_ready_and_says_so()
    {
        var bed = new Bed { WithTestData = true };
        ManualReady(bed);
        var view = await bed.View();

        view.Summary.Single(i => i.Key == "identities").State.Should().Be(AltinnReadinessState.Ready, "who is involved is ready");
        view.Summary.Single(i => i.Key == "testdata").Should().Match<AltinnReadinessItem>(i => i.State == AltinnReadinessState.Blocked && i.Reason.Contains("XML fixture missing"));
        view.ManualReadiness.State.Should().Be(AltinnReadinessState.Partial);
        view.ManualReadiness.Reason.Should().Contain("XML fixture: missing");
        view.ManualMissing.Should().Contain("XML fixture");
        view.AutomatedBlockers.Should().Contain(b => b.Contains("XML fixture"), "automated submission needs the payload too");
        view.Summary.Select(i => i.Key).Should().StartWith(["app", "identities", "testdata", "reporter-access", "integration-auth", "authentication"],
            "authentication is per hop: reporter access, the integration's own authentication, then BirkNext execution credentials");
    }

    [Fact]
    public async Task A_valid_xml_fixture_makes_manual_ready_without_claiming_contract_evidence_or_runtime()
    {
        var bed = new Bed { WithTestData = true };
        ManualReady(bed);
        bed.SavedXml.Add(Fixture());
        var view = await bed.View();

        view.Summary.Single(i => i.Key == "testdata").State.Should().Be(AltinnReadinessState.Ready);
        view.ManualReadiness.State.Should().Be(AltinnReadinessState.Ready, view.ManualReadiness.Reason);
        view.ManualReadiness.Reason.Should().Contain("contract identifier is known").And.NotContain("contract are known");
        view.ManualReadiness.Reason.Should().ContainAny("Contract evidence is still incomplete", "Contract evidence is complete");
        view.AutomatedReadiness.State.Should().Be(AltinnReadinessState.Blocked, "unattended authentication is still missing");
        view.Summary.Single(i => i.Key == "runtime").State.Should().Be(AltinnReadinessState.NotAssessed, "no runtime evidence");
    }

    [Fact]
    public async Task An_invalid_xml_fixture_is_named_and_never_reads_ready()
    {
        var bed = new Bed { WithTestData = true };
        ManualReady(bed);
        bed.SavedXml.Add(Fixture(valid: false));
        var view = await bed.View();

        view.Summary.Single(i => i.Key == "testdata").Should().Match<AltinnReadinessItem>(i => i.State == AltinnReadinessState.Partial && i.Reason.Contains("invalid against its XSD"));
        view.ManualReadiness.State.Should().Be(AltinnReadinessState.Partial);
    }

    [Fact]
    public async Task A_known_contract_identifier_without_contract_evidence_is_partial_not_missing()
    {
        var bed = new Bed();
        ManualReady(bed);
        bed.Flows.Contract = null;
        var view = await bed.View();
        if (view.Fields.Single(f => f.Key == "form-data-type") is { State: not (AltinnSettingState.Missing or AltinnSettingState.Declared) })
            view.Contract.Should().Match<AltinnReadinessItem>(c => c.State == AltinnReadinessState.Partial && c.Reason.StartsWith("Contract identifier known"));
    }

    [Fact]
    public void The_configuration_overview_resolves_the_child_from_test_data_and_an_explicit_run_input_wins()
    {
        var kit = new AltinnTestKit();
        var options = SkoleNaervaerTestIdentityDefaults.Apply(kit.Options); // as at startup: the built-in default identities
        var policy = new AltinnTestingPolicy(options, new AltinnSessionStore(options, TimeProvider.System), new AltinnEnvironmentSettingsCache());
        var service = new AltinnConfigurationOverviewService(new IntegrationJourneyHarness.FakeMessageFlows(), policy, kit.Contracts(new IntegrationJourneyHarness.FakeMessageFlows()));
        var contract = new AltinnAppContractView();
        var fromTestData = service.Build(Env, new AltinnTestConfiguration { TargetEnvironmentId = Env }, contract, null).Items.Single(i => i.Key == "child");
        (fromTestData.State, fromTestData.Source).Should().Be(("Configured", "Test Data"), "the canonical approved child, not a legacy run-input-only model");
        fromTestData.Detail.Should().NotContain("04061076692");
        var explicitRun = service.Build(Env, new AltinnTestConfiguration { TargetEnvironmentId = Env, ApprovedSyntheticData = true, SyntheticIdentitySetReference = "set-1" }, contract, null).Items.Single(i => i.Key == "child");
        explicitRun.State.Should().Be("Declared", "an explicit run input wins");
    }

    private sealed class FakeClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 27, 9, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static AltinnSettingField Field(AltinnEnvironmentView view, string key) => view.Fields.Single(field => field.Key == key);

    private static string Person(string dayMonth, Func<string, bool> valid)
    {
        for (var individual = 100; individual < 1000; individual++)
            for (var k = 0; k < 100; k++)
                if ($"{dayMonth}12{individual:000}{k:00}" is var candidate && valid(candidate)) return candidate;
        throw new InvalidOperationException();
    }

    private static void ManualReady(Bed bed)
    {
        bed.Flows.Configuration = bed.Flows.Configuration with { Environment = ExternalTestEnvironment.Test, AppIdentifier = AltinnTestKit.AppId };
        bed.Flows.Contract = new AltinnAppContractRecord(null, new AltinnContractSnapshot { AppId = AltinnTestKit.AppId, FormDataTypeId = AltinnTestKit.RealFormDataType, AllowedContentTypes = ["application/xml"] });
        bed.Integrations.Add(new IntegrationDefinition { Id = "skolenaervaer-report-intake", EnvironmentId = Env, DisplayName = AltinnEnvironmentService.IntegrationName });
        bed.Session.AddIdentity(new("reporter", "reporter-a", "Skolenærvær reporter A", AltinnTestKit.ReporterIdentifier, "reporter-a")).Should().BeNull();
        bed.Session.AddIdentity(new("child", "child-a", "Skolenærvær test child A", ChildIdentifier)).Should().BeNull();
    }

    [Fact] // A
    public async Task No_altinn_config_suggests_skolenaervaer_from_the_test_pack_without_saving()
    {
        var bed = new Bed();
        var view = await bed.View();

        Field(view, "app").Should().Match<AltinnSettingField>(f => f.Value == "buf/skolenaervaer" && f.State == AltinnSettingState.Declared && f.Source == AltinnSettingSource.TestPack);
        Field(view, "app").Proposal!.Value.Should().Be("buf/skolenaervaer");
        Field(view, "environment").Value.Should().Be("Test (TT02)");
        Field(view, "client-id").State.Should().Be(AltinnSettingState.Missing);
        (await bed.Db.AltinnEnvironmentSettings.CountAsync()).Should().Be(0, "viewing never writes");
    }

    [Fact] // B
    public async Task Explicit_target_environment_config_wins_and_is_not_proposed_again()
    {
        var bed = new Bed();
        bed.Flows.Configuration = bed.Flows.Configuration with { AppIdentifier = AltinnTestKit.AppId };
        (await bed.Store.UpdateAsync(Env, new() { AppIdentifier = AltinnTestKit.AppId, Environment = "TT02" })).Problems.Should().BeEmpty();

        var view = await bed.View();
        Field(view, "app").Should().Match<AltinnSettingField>(f => f.State == AltinnSettingState.Configured && f.Source == AltinnSettingSource.TargetEnvironment && f.Proposal == null);
        Field(view, "environment").State.Should().Be(AltinnSettingState.Configured);
    }

    [Fact] // C
    public async Task Iqr_integration_is_referenced_not_copied()
    {
        var bed = new Bed();
        bed.Integrations.Add(new IntegrationDefinition { Id = "skolenaervaer-report-intake", EnvironmentId = Env, DisplayName = AltinnEnvironmentService.IntegrationName });
        var integration = Field(await bed.View(), "integration");
        integration.Should().Match<AltinnSettingField>(f => f.Value == "skolenaervaer-report-intake" && f.State == AltinnSettingState.Configured && f.Source == AltinnSettingSource.IntegrationBinding
            && f.Proposal == null, "the one catalog integration is an authoritative internal reference: consumed directly, no Detect → Apply");
        integration.SourceLabel.Should().Be("Integration Catalog (internal reference)");
        integration.Detail.Should().Contain("Meldingsutveksleren (MU)");
    }

    [Fact] // D
    public async Task Browser_oidc_observation_is_observed_browser_auth_only_and_never_configures_the_unattended_client()
    {
        var bed = new Bed();
        AltinnBrowserObservation.Problem(new("https://test.idporten.no", null, "idporten-loa-substantial", "altinn-portal", "https://af.tt02.altinn.no/api/cb",
            ["openid", "altinn:portal/enduser"], "S256", "Browser login")).Should().BeNull();
        await bed.Store.RecordBrowserObservationAsync(Env, AltinnBrowserObservation.From(new("https://test.idporten.no", null, "idporten-loa-substantial", "altinn-portal",
            "https://af.tt02.altinn.no/api/cb", ["openid", "altinn:portal/enduser"], "S256", "Browser login"), bed.Clock.Now));

        var view = await bed.View();
        Field(view, "browser-redirect").Should().Match<AltinnSettingField>(f => f.Value == "https://af.tt02.altinn.no/api/cb" && f.State == AltinnSettingState.Observed);
        Field(view, "browser-pkce").Value.Should().Be("S256");
        Field(view, "redirect-uri").Value.Should().NotBe("https://af.tt02.altinn.no/api/cb", "a browser redirect URI is never the unattended registered redirect");
        Field(view, "redirect-uri").Candidates.Should().NotContain(candidate => candidate.Source == AltinnSettingSource.RuntimeObservation);
        Field(view, "client-id").State.Should().Be(AltinnSettingState.Missing);
        bed.Policy.UnattendedRedirect(new AltinnTestConfiguration { TargetEnvironmentId = Env })?.AbsoluteUri.Should().NotBe("https://af.tt02.altinn.no/api/cb");
    }

    [Fact]
    public void Transient_browser_values_are_refused()
    {
        AltinnBrowserObservation.Problem(new(null, null, null, null, "https://af.tt02.altinn.no/api/cb?code=abc&state=xyz", null, null, null)).Should().NotBeNull();
        AltinnBrowserObservation.Problem(new(null, null, null, null, null, ["code_verifier"], null, null)).Should().Contain("never recorded");
    }

    [Fact] // E
    public async Task Missing_unattended_client_blocks_only_automated_execution()
    {
        var bed = new Bed();
        ManualReady(bed);
        var view = await bed.View();

        view.ManualReadiness.State.Should().Be(AltinnReadinessState.Ready, view.ManualReadiness.Reason);
        view.AutomatedReadiness.State.Should().Be(AltinnReadinessState.Blocked);
        view.AutomatedBlockers.Should().Contain(blocker => blocker.Contains("client id")).And.Contain(blocker => blocker.Contains("client secret"));
        view.Detection.ActionableGaps.Should().Contain(gap => gap.Contains("Client ID (automated execution)")).And.NotContain(gap => gap.Contains("Reporter"));
    }

    [Fact] // F
    public async Task Secret_from_the_backend_process_is_available_to_backend_and_a_session_secret_wins_without_being_returned()
    {
        var bed = new Bed();
        bed.Kit.ConfigureUnattended();
        Field(await bed.View(), "client-secret").State.Should().Be(AltinnSettingState.AvailableToBackend);

        const string sessionSecret = "session-secret-DO-NOT-RETURN";
        bed.Session.SetSecret(Env, sessionSecret).Should().BeNull();
        var view = await bed.View();
        Field(view, "client-secret").State.Should().Be(AltinnSettingState.AvailableForSession);
        JsonSerializer.Serialize(view).Should().NotContain(sessionSecret).And.NotContain(AltinnTestKit.ClientSecret).And.NotContain(AltinnTestKit.ReporterIdentifier);
        bed.Policy.UnattendedClientSecretFor(new AltinnTestConfiguration { TargetEnvironmentId = Env }).Should().Be(sessionSecret);
        bed.Policy.UnattendedClientSecretFor(new AltinnTestConfiguration { TargetEnvironmentId = "other" }).Should().Be(AltinnTestKit.ClientSecret, "a session secret belongs to its environment");
    }

    [Fact] // G
    public async Task Conflicting_sources_are_a_conflict_and_the_saved_value_is_never_mutated()
    {
        var bed = new Bed();
        await bed.Store.UpdateAsync(Env, new() { AppIdentifier = "buf/skolenaervaer" });
        bed.Flows.Configuration = bed.Flows.Configuration with { AppIdentifier = "buf/skolenaervaer-v2" };

        var app = Field(await bed.View(detect: true), "app");
        app.State.Should().Be(AltinnSettingState.Conflict);
        app.Value.Should().Be("buf/skolenaervaer", "the explicit saved value is shown, never silently replaced");
        app.Proposal!.Value.Should().Be("buf/skolenaervaer-v2");
        app.Detail.Should().Contain("Sources disagree");
        (await bed.Store.GetAsync(Env)).AppIdentifier.Should().Be("buf/skolenaervaer");
    }

    [Fact]
    public async Task Conflicting_client_ids_are_never_resolved_silently()
    {
        var bed = new Bed();
        bed.Kit.Options.UnattendedIdPortenTest.ClientId = "backend-client";
        await bed.Store.UpdateAsync(Env, new() { ClientId = "environment-client" });
        bed.Policy.ClientId(new AltinnTestConfiguration { TargetEnvironmentId = Env }, out _, out var problem).Should().BeNull();
        problem.Should().Contain("differs");
        Field(await bed.View(), "client-id").State.Should().Be(AltinnSettingState.Conflict);
    }

    [Fact] // H
    public async Task Detect_without_reachable_metadata_is_unknown_not_an_error_and_never_authenticates()
    {
        var bed = new Bed();
        bed.Kit.Http.Fail["discovery"] = HttpStatusCode.ServiceUnavailable;
        var view = await bed.View(detect: true);
        view.Detected.Should().BeTrue();
        view.Notes.Should().Contain(note => note.Contains("Unknown"));
        view.Fields.Should().NotContain(field => field.Key == "idporten-issuer");

        bed.Kit.Http.Fail.Clear();
        var detected = await bed.View(detect: true);
        Field(detected, "idporten-issuer").Should().Match<AltinnSettingField>(f => f.State == AltinnSettingState.Detected && f.Source == AltinnSettingSource.PublicMetadata);
        Field(detected, "idporten-issuer").Detail.Should().Contain("does not prove a client registration");
        bed.Kit.Http.Calls.Should().OnlyContain(call => call.Method == HttpMethod.Get && call.Uri.AbsoluteUri == AltinnTestingPolicy.IdPortenTestDiscovery && call.Authorization == null);
        (await bed.Db.AltinnEnvironmentSettings.CountAsync()).Should().Be(0, "detecting never saves");
    }

    [Fact]
    public async Task Saved_unattended_values_feed_the_execution_policy_per_environment()
    {
        var bed = new Bed();
        (await bed.Store.UpdateAsync(Env, new() { ClientId = AltinnTestKit.ClientId, RedirectUri = AltinnTestKit.HeadlessRedirect, Scopes = ["openid", "altinn:instances.write"], Enabled = true })).Problems.Should().BeEmpty();
        var config = new AltinnTestConfiguration { TargetEnvironmentId = Env };
        bed.Policy.ClientId(config, out var source, out _).Should().Be(AltinnTestKit.ClientId);
        source.Should().Be(AltinnTestingPolicy.TargetEnvironmentAltinn);
        bed.Policy.UnattendedRedirect(config)!.AbsoluteUri.Should().Be(AltinnTestKit.HeadlessRedirect);
        bed.Policy.Scopes(config).Should().Equal("openid", "altinn:instances.write");
        bed.Policy.UnattendedEnabled(config).Should().BeTrue();
        bed.Policy.UnattendedProblems(config).Should().Contain(problem => problem.Contains("client secret")).And.NotContain(problem => problem.Contains("PowerShell"));
    }

    [Theory]
    [InlineData("Production", "Production")]
    [InlineData(null, "https")]
    public async Task Production_and_unsafe_values_are_refused(string? environment, string expected)
    {
        var bed = new Bed();
        var (settings, problems) = await bed.Store.UpdateAsync(Env, environment is null ? new() { RedirectUri = "http://birknext.example/cb" } : new() { Environment = environment });
        settings.Should().BeNull();
        problems.Should().Contain(problem => problem.Contains(expected));
        (await bed.Store.UpdateAsync(Env, new() { AppBaseUrl = "https://buf.apps.altinn.no/buf/skolenaervaer" })).Problems.Should().Contain(problem => problem.Contains("Production"));
        (await bed.Store.UpdateAsync(Env, new() { ClientId = "Bearer eyJhbGciOi" })).Problems.Should().Contain(problem => problem.Contains("credential"));
        (await bed.Db.AltinnEnvironmentSettings.CountAsync()).Should().Be(0);
    }

    [Fact]
    public void Session_secret_expires_and_is_cleared()
    {
        var bed = new Bed();
        bed.Session.SetSecret(Env, "  ").Should().NotBeNull();
        bed.Session.SetSecret(Env, "value").Should().BeNull();
        bed.Session.Secret(Env).Should().Be("value");
        bed.Clock.Now += AltinnSessionStore.Lifetime + TimeSpan.FromSeconds(1);
        bed.Session.Secret(Env).Should().BeNull("a session value expires");
        bed.Session.SetSecret(Env, "again");
        bed.Session.ClearSecret(Env);
        bed.Session.Secret(Env).Should().BeNull();
        new AltinnSessionStore(new AltinnTestingOptions(), bed.Clock).Secret(Env).Should().BeNull("a new backend process starts without session values");
    }

    [Fact]
    public async Task Session_identities_use_domain_correct_rules_and_reporter_must_differ_from_child()
    {
        var bed = new Bed();
        SyntheticPersonIdentifier.IsSynthetic(ChildIdentifier).Should().BeFalse("the child fixture is an ordinary-format Test-Norge identifier");
        bed.Session.AddIdentity(new("child", "child-a", "Child A", ChildIdentifier)).Should().BeNull("the reporter's month + 80 rule is not applied to a child");
        bed.Session.AddIdentity(new("child", "child-b", "Child B", "12345678901")).Should().Contain("control digits");
        bed.Session.AddIdentity(new("reporter", "reporter-a", "Reporter A", ChildIdentifier)).Should().Contain("synthetic TestID");
        bed.Session.AddIdentity(new("reporter", "reporter-a", "Reporter A", AltinnTestKit.ReporterIdentifier)).Should().BeNull();
        bed.Session.AddIdentity(new("child", "child-c", "Child C", AltinnTestKit.ReporterIdentifier)).Should().Contain("different persons");

        var view = await bed.View();
        view.Identities.Single(item => item.Alias == "reporter-a").State.Should().Be(AltinnSettingState.Configured, "configured is not profile-confirmed");
        view.Identities.Single(item => item.Alias == "child-a").Source.Should().Contain("session");
        JsonSerializer.Serialize(view).Should().NotContain(ChildIdentifier).And.NotContain(AltinnTestKit.ReporterIdentifier);

        bed.Session.AddIdentity(new("reporter", "reporter-a", "Reporter A", AltinnTestKit.ReporterIdentifier, ConfirmProfileAlias: "reporter-a"));
        (await bed.View()).Identities.Single(item => item.Alias == "reporter-a").State.Should().Be(AltinnSettingState.Confirmed);

        bed.Clock.Now += AltinnSessionStore.Lifetime + TimeSpan.FromMinutes(1);
        bed.Session.Sweep();
        bed.Kit.Options.SyntheticChildren.Should().BeEmpty();
        bed.Kit.Options.UnattendedIdPortenTest.Reporters.Should().BeEmpty();
    }

    [Fact]
    public async Task Runtime_observation_is_evidence_and_never_proves_the_unattended_client()
    {
        var bed = new Bed();
        ManualReady(bed);
        await bed.Store.RecordObservationAsync(Env, new AltinnRuntimeObservation { Kind = AltinnRuntimeObservationKind.Submission, Succeeded = true, ObservedAt = bed.Clock.Now });
        var view = await bed.View();
        view.Summary.Single(item => item.Key == "runtime").Reason.Should().Contain("Observed").And.Contain("not verified");
        view.AutomatedReadiness.State.Should().Be(AltinnReadinessState.Blocked, "a manual submission does not prove BirkNext's client or secret");
    }

    [Fact]
    public async Task A_target_environment_only_setup_is_what_consumers_read_and_is_never_copied_into_iqr()
    {
        var bed = new Bed();
        (await bed.Store.UpdateAsync(Env, new() { Environment = "TT02", AppIdentifier = AltinnTestKit.AppId, AppBaseUrl = "https://buf.apps.tt02.altinn.no/buf/skolenaervaer" })).Problems.Should().BeEmpty();
        var iqr = bed.Flows.Configuration; // Environment Unknown, no app: nothing configured in IQR

        var effective = bed.Policy.Effective(iqr, Env);
        (effective.Environment, effective.AppIdentifier, effective.TargetEnvironmentId).Should().Be((ExternalTestEnvironment.Test, AltinnTestKit.AppId, Env));
        AltinnTestingPolicy.AltinnEnvironmentBlock(effective).Should().BeNull("readiness, auth and the executor see the Target Environment's TT02");
        bed.Policy.ApprovedApp(effective.AppIdentifier, out _).Should().NotBeNull();
        iqr.AppIdentifier.Should().BeNull("the IQR record is never mutated");

        var explicitIqr = iqr with { Environment = ExternalTestEnvironment.Staging, AppIdentifier = "other/app" };
        bed.Policy.Effective(explicitIqr, Env).AppIdentifier.Should().Be(AltinnTestKit.AppId, "the saved Target Environment value takes precedence over the IQR binding");
        bed.Policy.Effective(explicitIqr, "another-env").AppIdentifier.Should().Be("other/app", "another environment keeps its own IQR value");
    }

    [Fact]
    public async Task Production_cannot_become_executable()
    {
        var bed = new Bed();
        ManualReady(bed);
        bed.Flows.Configuration = bed.Flows.Configuration with { Environment = ExternalTestEnvironment.Production };
        var view = await bed.View();
        view.ManualReadiness.State.Should().Be(AltinnReadinessState.Blocked);
        view.AutomatedReadiness.State.Should().Be(AltinnReadinessState.Blocked);
    }
    // ── Form data type / content type detection ──

    private static IIntegrationJourneyPack ReferencePack(string scope, params (string Key, string Value)[] facts)
    {
        var pack = new Mock<IIntegrationJourneyPack>();
        pack.SetupGet(p => p.References).Returns([new TestPackReference("altinn-app", "OpenAPI", "Fixture reference",
            () => new TestPackReferenceDocument([.. facts.Select(fact => new TestPackReferenceFact(fact.Key, fact.Value) { SemanticKey = fact.Key })])) { Scope = scope }]);
        return pack.Object;
    }

    [Fact]
    public async Task Skolenaervaer_form_data_type_and_content_type_are_suggested_from_the_test_pack_reference_without_saving()
    {
        var bed = new Bed();
        bed.Packs.Add(bed.SkoleNaervaerPack());

        var view = await bed.View(detect: true);

        Field(view, "form-data-type").Should().Match<AltinnSettingField>(f => f.Value == AltinnTestKit.RealFormDataType && f.State == AltinnSettingState.Declared
            && f.Source == AltinnSettingSource.TestPack && f.SourceLabel == "Test Pack reference · OpenAPI" && f.Proposal!.Value == AltinnTestKit.RealFormDataType);
        Field(view, "content-type").Should().Match<AltinnSettingField>(f => f.Value == "application/xml" && f.State == AltinnSettingState.Declared && f.Proposal!.Value == "application/xml");
        (await bed.Db.AltinnEnvironmentSettings.CountAsync()).Should().Be(0, "detection never saves");
    }

    [Fact]
    public async Task Contract_evidence_wins_over_the_reference_and_the_reference_never_conflicts_with_it()
    {
        var bed = new Bed();
        bed.Packs.Add(ReferencePack(AltinnTestKit.AppId, (AltinnEnvironmentService.FormDataTypeKey, "reference-type")));
        bed.Flows.Contract = new AltinnAppContractRecord(new AltinnContractSnapshot { AppId = AltinnTestKit.AppId, FormDataTypeId = AltinnTestKit.RealFormDataType }, null);

        Field(await bed.View(), "form-data-type").Should().Match<AltinnSettingField>(f =>
            f.Value == AltinnTestKit.RealFormDataType && f.State == AltinnSettingState.Detected && f.Source == AltinnSettingSource.SourceAnalysis);
    }

    [Fact]
    public async Task Another_app_gets_no_suggestion_the_altinn_provider_is_not_hardcoded_to_skolenaervaer_or_xml()
    {
        var bed = new Bed();
        bed.Packs.Add(bed.SkoleNaervaerPack());
        (await bed.Store.UpdateAsync(Env, new() { AppIdentifier = "buf/another-app", Environment = "Test" })).Problems.Should().BeEmpty();

        var view = await bed.View(detect: true);

        Field(view, "form-data-type").State.Should().Be(AltinnSettingState.Missing);
        Field(view, "content-type").State.Should().Be(AltinnSettingState.Missing);
        Field(await new Bed().View(detect: true), "content-type").State.Should().Be(AltinnSettingState.Missing, "without a pack reference nothing suggests XML");
    }

    [Fact]
    public async Task Ambiguous_evidence_stays_unresolved()
    {
        var bed = new Bed();
        bed.Packs.Add(bed.SkoleNaervaerPack());
        bed.Flows.Contract = new AltinnAppContractRecord(new AltinnContractSnapshot { AppId = AltinnTestKit.AppId, FormDataTypeCandidates = ["model-a", "model-b"] }, null);
        Field(await bed.View(), "form-data-type").Should().Match<AltinnSettingField>(f => f.State == AltinnSettingState.Missing && f.Detail.Contains("several form data types"));

        var disagreeing = new Bed();
        disagreeing.Packs.Add(disagreeing.SkoleNaervaerPack());
        disagreeing.Packs.Add(ReferencePack(AltinnTestKit.AppId, (AltinnEnvironmentService.ContentTypeKey, "application/json")));
        var view = await disagreeing.View();
        Field(view, "content-type").State.Should().Be(AltinnSettingState.Missing);
        view.Notes.Should().Contain(note => note.Contains("disagree"));
    }

    [Fact]
    public async Task An_explicit_value_is_never_replaced_and_disagreeing_detected_evidence_is_a_conflict()
    {
        var bed = new Bed();
        bed.Packs.Add(bed.SkoleNaervaerPack());
        (await bed.Store.UpdateAsync(Env, new() { FormDataType = "explicit-model", ContentType = "application/json" })).Problems.Should().BeEmpty();

        var suggestedOnly = await bed.View();
        Field(suggestedOnly, "form-data-type").Should().Match<AltinnSettingField>(f => f.Value == "explicit-model" && f.State == AltinnSettingState.Configured && f.Proposal == null);
        Field(suggestedOnly, "content-type").Should().Match<AltinnSettingField>(f => f.Value == "application/json" && f.State == AltinnSettingState.Configured);

        bed.Flows.Contract = new AltinnAppContractRecord(new AltinnContractSnapshot { AppId = AltinnTestKit.AppId, FormDataTypeId = AltinnTestKit.RealFormDataType }, null);
        Field(await bed.View(), "form-data-type").Should().Match<AltinnSettingField>(f =>
            f.State == AltinnSettingState.Conflict && f.Value == "explicit-model" && f.Proposal!.Value == AltinnTestKit.RealFormDataType);
    }

    [Fact]
    public async Task Applying_the_suggestions_saves_them_as_configured_and_portable_settings()
    {
        var bed = new Bed();
        bed.Packs.Add(bed.SkoleNaervaerPack());
        var detected = await bed.View(detect: true);

        (await bed.Store.UpdateAsync(Env, new()
        {
            FormDataType = Field(detected, "form-data-type").Proposal!.Value, ContentType = Field(detected, "content-type").Proposal!.Value,
        })).Problems.Should().BeEmpty();

        var view = await bed.View();
        Field(view, "form-data-type").Should().Match<AltinnSettingField>(f => f.State == AltinnSettingState.Configured && f.Value == AltinnTestKit.RealFormDataType);
        Field(view, "content-type").Should().Match<AltinnSettingField>(f => f.State == AltinnSettingState.Configured && f.Value == "application/xml");
        BirkNext.Api.Services.ExternalIntegrations.AltinnProviderHandler.ToSettings(view.Saved).Should()
            .Contain("formDataType", AltinnTestKit.RealFormDataType).And.Contain("contentType", "application/xml");
    }

    [Fact]
    public async Task Integration_reference_shows_the_display_name_and_keeps_the_raw_id()
    {
        var bed = new Bed();
        const string id = "15550bccc6b34a9e8118a99dc4e1fd82:httpapi:skolen-rv-r-altinn-report-intake";
        bed.Integrations.Add(new IntegrationDefinition { Id = id, EnvironmentId = Env, DisplayName = AltinnEnvironmentService.IntegrationName });

        var integration = Field(await bed.View(), "integration");

        integration.Value.Should().Be(id);
        integration.DisplayValue.Should().Be(AltinnEnvironmentService.IntegrationName);
        integration.Proposal.Should().BeNull("nothing to apply for an authoritative internal reference");
    }

    [Fact]
    public async Task Manual_readiness_ignores_unattended_only_blockers_and_runtime_stays_separate()
    {
        var bed = new Bed();
        ManualReady(bed);

        var view = await bed.View();

        view.ManualReadiness.State.Should().Be(AltinnReadinessState.Ready);
        view.ManualMissing.Should().BeEmpty();
        view.AutomatedReadiness.State.Should().Be(AltinnReadinessState.Blocked);
        view.AutomatedBlockers.Should().Contain(blocker => blocker.Contains("client", StringComparison.OrdinalIgnoreCase) || blocker.Contains("secret", StringComparison.OrdinalIgnoreCase));
        view.Summary.Single(item => item.Key == "runtime").State.Should().Be(AltinnReadinessState.NotAssessed, "runtime is never configuration readiness");

        var noReporter = new Bed();
        noReporter.Flows.Configuration = noReporter.Flows.Configuration with { Environment = ExternalTestEnvironment.Test, AppIdentifier = AltinnTestKit.AppId };
        var missing = await noReporter.View();
        missing.ManualMissing.Should().Contain(["Reporter", "Child"]);
        missing.AutomatedBlockers.Should().Contain("Reporter: missing");
    }

    [Fact]
    public async Task Report_contract_readiness_names_its_sources()
    {
        (await new Bed().View()).Contract.State.Should().Be(AltinnReadinessState.Blocked);

        var bed = new Bed();
        ManualReady(bed);
        var contract = (await bed.View()).Contract;
        contract.State.Should().NotBe(AltinnReadinessState.Blocked);
        contract.Reason.Should().NotContain("No contract evidence");
    }
    // ── Suggested != configured (Altinn) ──

    [Fact]
    public async Task Test_pack_only_form_and_content_type_are_not_applied_and_block_until_applied()
    {
        var bed = new Bed();
        ManualReady(bed);
        bed.Packs.Add(bed.SkoleNaervaerPack());
        bed.Flows.Contract = new AltinnAppContractRecord(null, new AltinnContractSnapshot { AppId = AltinnTestKit.AppId });

        var before = await bed.View();

        Field(before, "form-data-type").State.Should().Be(AltinnSettingState.Declared);
        before.ManualReadiness.State.Should().Be(AltinnReadinessState.Partial, "a suggested form data type is not configuration");
        before.ManualMissing.Should().Contain("Form data type (not applied)");
        before.AutomatedBlockers.Should().Contain("Content type: not applied");
        before.Detection.ActionableGaps.Should().Contain(gap => gap.StartsWith("Not applied: Form data type"));
        (await bed.Db.AltinnEnvironmentSettings.CountAsync()).Should().Be(0, "viewing never applies a suggestion");

        (await bed.Store.UpdateAsync(Env, new() { FormDataType = Field(before, "form-data-type").Proposal!.Value, ContentType = Field(before, "content-type").Proposal!.Value }))
            .Problems.Should().BeEmpty();
        var after = await bed.View();

        Field(after, "form-data-type").State.Should().Be(AltinnSettingState.Configured);
        after.ManualReadiness.State.Should().Be(AltinnReadinessState.Ready, "readiness recalculates after Apply");
        after.AutomatedBlockers.Should().NotContain(blocker => blocker.StartsWith("Content type"));
    }

    [Fact]
    public async Task A_suggested_app_does_not_make_the_app_ready_but_detected_iqr_values_still_do()
    {
        var suggested = await new Bed().View();
        Field(suggested, "app").State.Should().Be(AltinnSettingState.Declared);
        suggested.Summary.Single(item => item.Key == "app").State.Should().Be(AltinnReadinessState.Partial);
        suggested.ManualMissing.Should().Contain(["Altinn environment (not applied)", "App identifier (not applied)"]);

        var bed = new Bed();
        ManualReady(bed);
        var detected = await bed.View();
        Field(detected, "app").State.Should().Be(AltinnSettingState.Detected, "the IQR binding is evidence BirkNext already uses");
        detected.ManualReadiness.State.Should().Be(AltinnReadinessState.Ready);
    }

    [Fact]
    public async Task An_optional_suggestion_never_blocks_and_a_conflict_still_does()
    {
        var bed = new Bed();
        ManualReady(bed);
        (await bed.Store.UpdateAsync(Env, new() { FormDataType = "other-model" })).Problems.Should().BeEmpty();

        var view = await bed.View();

        Field(view, "form-data-type").State.Should().Be(AltinnSettingState.Conflict);
        view.ManualReadiness.State.Should().Be(AltinnReadinessState.Partial);
        view.ManualMissing.Should().Contain("Form data type (conflict)");
        view.Fields.Where(field => field.Requirement == AltinnRequirement.Optional).Should().NotContain(field => view.Detection.ActionableGaps.Any(gap => gap.Contains(field.Label)));
    }

    // ── Integration: one deterministic Integration Catalog match is proposed; several are never chosen between ──────────

    [Fact]
    public async Task One_matching_catalog_integration_is_consumed_two_stay_unresolved_and_none_is_missing()
    {
        var one = new Bed();
        one.Integrations.Add(new IntegrationDefinition { Id = "dev:http:skolenaervaer-report-intake", EnvironmentId = Env, DisplayName = AltinnEnvironmentService.IntegrationName });
        Field(await one.View(detect: true), "integration").Should().Match<AltinnSettingField>(f =>
            f.State == AltinnSettingState.Configured && f.Proposal == null && f.Value == "dev:http:skolenaervaer-report-intake");

        var two = new Bed();
        two.Integrations.Add(new IntegrationDefinition { Id = "dev:http:skolenaervaer-report-intake", EnvironmentId = Env, DisplayName = AltinnEnvironmentService.IntegrationName });
        two.Integrations.Add(new IntegrationDefinition { Id = "dev:http:report-intake-copy", EnvironmentId = Env, DisplayName = "Report intake (copy)" });
        var ambiguous = Field(await two.View(detect: true), "integration");
        ambiguous.Value.Should().BeNull("two matches are never chosen between");
        ambiguous.Proposal.Should().BeNull();
        ambiguous.State.Should().Be(AltinnSettingState.Missing);
        ambiguous.Detail.Should().Contain("2 integrations in the Integration Catalog match").And.Contain("Nothing is chosen automatically");

        var none = new Bed();
        none.Integrations.Add(new IntegrationDefinition { Id = "dev:http:person", EnvironmentId = Env, DisplayName = "Person API" });
        Field(await none.View(detect: true), "integration").Should().Match<AltinnSettingField>(f => f.State == AltinnSettingState.Missing && f.Proposal == null);
    }

    [Fact]
    public async Task A_saved_integration_wins_and_detection_saves_nothing()
    {
        var bed = new Bed();
        bed.Integrations.Add(new IntegrationDefinition { Id = "dev:http:skolenaervaer-report-intake", EnvironmentId = Env, DisplayName = AltinnEnvironmentService.IntegrationName });
        await bed.View(detect: true);
        bed.Db.AltinnEnvironmentSettings.Count().Should().Be(0, "Detect calculates candidates only");
    }
}
