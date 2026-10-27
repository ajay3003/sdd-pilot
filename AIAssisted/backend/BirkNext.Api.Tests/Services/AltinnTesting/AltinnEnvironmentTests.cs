using System.Net;
using System.Text.Json;
using BirkNext.Api.Controllers;
using BirkNext.Api.Data;
using BirkNext.Api.Services.ActiveEventTesting;
using BirkNext.Api.Services.AltinnTesting;
using BirkNext.Api.Services.Integrations;
using BirkNext.Api.Services.IntegrationJourneys.Packs.SkoleNaervaer;
using BirkNext.Api.Tests.Services.IntegrationJourneys;
using BirkNext.Integrations;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace BirkNext.Api.Tests.Services.AltinnTesting;

/// <summary>
/// Target Environment → External Integrations → Altinn: precedence, conflicts, browser-vs-unattended separation, manual-vs-automated readiness, detection without
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
            return new AltinnEnvironmentService(Store, Flows, Policy, Kit.Contracts(Flows), catalog.Object, trusted.Object, Kit.Factory);
        }

        public Task<AltinnEnvironmentView> View(bool detect = false) => Service().ViewAsync(Env, detect, CancellationToken.None);
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
        integration.Should().Match<AltinnSettingField>(f => f.Value == "skolenaervaer-report-intake" && f.State == AltinnSettingState.Detected && f.Source == AltinnSettingSource.IntegrationBinding);
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
    public async Task Production_cannot_become_executable()
    {
        var bed = new Bed();
        ManualReady(bed);
        bed.Flows.Configuration = bed.Flows.Configuration with { Environment = ExternalTestEnvironment.Production };
        var view = await bed.View();
        view.ManualReadiness.State.Should().Be(AltinnReadinessState.Blocked);
        view.AutomatedReadiness.State.Should().Be(AltinnReadinessState.Blocked);
    }
}
