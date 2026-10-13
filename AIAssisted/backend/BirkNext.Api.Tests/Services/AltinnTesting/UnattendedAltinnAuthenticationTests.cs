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
/// Unattended reporter authentication: ID-porten TEST's documented headless login for synthetic TestID users (authorization code + PKCE with a
/// testid: login_hint, 302 Location read and never followed, client_secret_basic), the Altinn TT02 token exchange, the in-memory token cache,
/// and the one-click Rapportmottak run that uses it. Every request goes through the production request guard into in-process fakes; nothing
/// reaches ID-porten, TestID or Altinn.
/// </summary>
public sealed class UnattendedAltinnAuthenticationTests
{
    private const string Env = ActiveEventTestHarness.Env;
    private const string Actor = "local-session:loopback";
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-10-13T08:00:00Z");

    private static AltinnAuthenticationRequest Request(string reporter = AltinnTestKit.ReporterAlias, AltinnTestConfiguration? configuration = null) =>
        new(Env, Actor, reporter, configuration ?? AltinnTestKit.ReadyConfiguration);

    // ── Authorization request ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheHeadlessAuthorizationRequest_IsExactlyTheDocumentedTestFlow()
    {
        var kit = new AltinnTestKit().ConfigureUnattended();
        var result = await kit.Unattended(new AltinnTestSessionCache(TimeProvider.System)).AuthenticateAsync(Request(), default);

        result.Success.Should().BeTrue(result.Detail);
        var authorize = kit.Http.Calls.Single(call => call.Uri.AbsolutePath == "/authorize");
        authorize.Method.Should().Be(HttpMethod.Get);
        authorize.Uri.GetLeftPart(UriPartial.Path).Should().Be("https://login.test.idporten.no/authorize");
        var query = kit.Http.LastAuthorize;
        query["response_type"].Should().Be("code");
        query["client_id"].Should().Be(AltinnTestKit.ClientId);
        query["redirect_uri"].Should().Be(AltinnTestKit.HeadlessRedirect);
        query["scope"].Should().Be("openid altinn:instances.read altinn:instances.write");
        query["login_hint"].Should().Be($"testid:{AltinnTestKit.ReporterIdentifier}_idporten-loa-substantial");
        query["acr_values"].Should().Be("idporten-loa-substantial");
        query["code_challenge_method"].Should().Be("S256");
        query["code_challenge"].Should().MatchRegex("^[A-Za-z0-9_-]{43}$");
        query["state"].Should().MatchRegex("^[A-Za-z0-9_-]{43}$");
        query["nonce"].Should().MatchRegex("^[A-Za-z0-9_-]{43}$");
        query.Should().NotContainKey("response_mode", "the code is read from the 302 Location of the backend's own request");
        query.Should().NotContainKey("client_secret");
        kit.Http.Calls.Select(call => $"{call.Method} {call.Uri.Host}{call.Uri.AbsolutePath}").Should().Equal(
            "GET test.idporten.no/.well-known/openid-configuration", "GET login.test.idporten.no/authorize", "POST test.idporten.no/token",
            "GET platform.tt02.altinn.no/authentication/api/v1/exchange/id-porten");
    }

    [Fact]
    public async Task EachRun_UsesFreshStateNonceAndPkce()
    {
        var kit = new AltinnTestKit().ConfigureUnattended();
        var clock = new ManualClock(Start);
        var provider = kit.Unattended(new AltinnTestSessionCache(clock), clock);
        (await provider.AuthenticateAsync(Request(), default)).Success.Should().BeTrue();
        var first = kit.Http.LastAuthorize;
        clock.Advance(TimeSpan.FromMinutes(5));
        (await provider.AuthenticateAsync(Request(), default)).Success.Should().BeTrue();
        var second = kit.Http.LastAuthorize;
        second["state"].Should().NotBe(first["state"]);
        second["nonce"].Should().NotBe(first["nonce"]);
        second["code_challenge"].Should().NotBe(first["code_challenge"]);
    }

    [Theory]
    [InlineData("https://login.idporten.no/authorize")]
    [InlineData("https://evil.example/authorize")]
    [InlineData("http://login.test.idporten.no/authorize")]
    public async Task AnAuthorizationEndpointOutsideIdPortenTest_IsRefusedBeforeAnyLogin(string endpoint)
    {
        var kit = new AltinnTestKit().ConfigureUnattended();
        kit.Http.AuthorizationEndpoint = endpoint;
        var result = await kit.Unattended(new AltinnTestSessionCache(TimeProvider.System)).AuthenticateAsync(Request(), default);

        result.Success.Should().BeFalse();
        result.Stage.Should().Be(AltinnAuthenticationStage.IdPortenDiscovery);
        kit.Http.Calls.Should().ContainSingle("only the pinned discovery document was read").Which.Uri.Host.Should().Be("test.idporten.no");
    }

    [Fact]
    public async Task DiscoveryFromAnotherIssuer_OrWithoutClientSecretBasic_IsRefused()
    {
        var kit = new AltinnTestKit().ConfigureUnattended();
        kit.Http.DiscoveryIssuer = "https://idporten.no";
        (await kit.Unattended(new AltinnTestSessionCache(TimeProvider.System)).AuthenticateAsync(Request(), default)).Stage.Should().Be(AltinnAuthenticationStage.IdPortenDiscovery);

        var basic = new AltinnTestKit().ConfigureUnattended();
        basic.Http.AuthMethods = ["client_secret_post"];
        var result = await basic.Unattended(new AltinnTestSessionCache(TimeProvider.System)).AuthenticateAsync(Request(), default);
        result.Stage.Should().Be(AltinnAuthenticationStage.IdPortenDiscovery);
        result.Detail.Should().Contain("client_secret_basic");
        basic.Http.Calls.Should().NotContain(call => call.Uri.AbsolutePath == "/authorize");
    }

    // ── Redirect handling ──────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("error", AltinnAuthenticationStage.IdPortenRedirect, "access_denied")]
    [InlineData("wrong-state", AltinnAuthenticationStage.IdPortenRedirect, "state")]
    [InlineData("wrong-iss", AltinnAuthenticationStage.IdPortenRedirect, "issuer")]
    [InlineData("foreign-redirect", AltinnAuthenticationStage.IdPortenRedirect, "not followed")]
    [InlineData("no-code", AltinnAuthenticationStage.IdPortenRedirect, "no authorization code")]
    [InlineData("no-location", AltinnAuthenticationStage.IdPortenRedirect, "without a location")]
    [InlineData("no-redirect", AltinnAuthenticationStage.IdPortenAuthorization, "no redirect")]
    public async Task ABadAuthorizationResponse_StopsBeforeTheCodeIsUsed(string mode, AltinnAuthenticationStage stage, string detail)
    {
        var kit = new AltinnTestKit().ConfigureUnattended();
        kit.Http.Headless = mode;
        var result = await kit.Unattended(new AltinnTestSessionCache(TimeProvider.System)).AuthenticateAsync(Request(), default);

        result.Success.Should().BeFalse();
        result.Stage.Should().Be(stage);
        result.Detail.Should().Contain(detail);
        kit.Http.Calls.Should().NotContain(call => call.Uri.AbsolutePath == "/token", "an unverified code is never exchanged");
        kit.Http.Calls.Should().NotContain(call => call.Uri.Host == "attacker.example", "a redirect is read, never followed");
        kit.Http.Calls.Should().NotContain(call => call.Uri.Host.Contains("altinn"));
    }

    // ── Token request and id_token ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheCode_IsExchangedOnce_WithClientSecretBasic_AndThePkceVerifier()
    {
        var kit = new AltinnTestKit().ConfigureUnattended();
        var clock = new ManualClock(Start);
        var cache = new AltinnTestSessionCache(clock);
        var result = await kit.Unattended(cache, clock).AuthenticateAsync(Request(), default);

        result.Success.Should().BeTrue(result.Detail);
        var token = kit.Http.Calls.Single(call => call.Uri.AbsolutePath == "/token");
        token.Uri.ToString().Should().Be("https://test.idporten.no/token");
        token.Authorization.Should().StartWith("Basic ");
        token.Body.Should().Contain("grant_type=authorization_code").And.Contain("code_verifier=").And.NotContain("client_secret", "client_secret_basic sends the secret only in the header");
        result.ExpiresAt.Should().Be(Start + TimeSpan.FromSeconds(120) - TimeSpan.FromSeconds(10), "expires_in bounds the Altinn token, with a safety margin");

        (await kit.Unattended(cache, clock).AuthenticateAsync(Request(), default)).Success.Should().BeTrue();
        kit.Http.Calls.Count(call => call.Uri.AbsolutePath == "/token").Should().Be(1, "an unexpired token is reused from memory; the code is never exchanged twice");
    }

    [Theory]
    [InlineData("aud", "another-client", "not addressed to this client")]
    [InlineData("iss", "https://idporten.no", "not issued by ID-porten TEST")]
    [InlineData("nonce", "replayed-nonce", "nonce")]
    [InlineData("amr", "BankID", "TestID")]
    [InlineData("pid", "01019012345", "another person")]
    public async Task AnIdTokenForAnotherClientIssuerRequestOrPerson_IsRejectedBeforeTheAltinnExchange(string claim, string value, string detail)
    {
        var kit = new AltinnTestKit().ConfigureUnattended();
        kit.Http.IdTokenClaims[claim] = claim == "amr" ? new[] { value } : value;
        var result = await kit.Unattended(new AltinnTestSessionCache(TimeProvider.System)).AuthenticateAsync(Request(), default);

        result.Stage.Should().Be(AltinnAuthenticationStage.IdPortenTokenValidation);
        result.Detail.Should().Contain(detail);
        kit.Http.Calls.Should().NotContain(call => call.Uri.Host == "platform.tt02.altinn.no");
    }

    [Fact]
    public async Task AMalformedOrRefusedTokenResponse_IsAnIdPortenTokenFailure_NotAnAltinnOne()
    {
        var malformed = new AltinnTestKit().ConfigureUnattended();
        malformed.Http.TokenMalformed = true;
        (await malformed.Unattended(new AltinnTestSessionCache(TimeProvider.System)).AuthenticateAsync(Request(), default)).Stage.Should().Be(AltinnAuthenticationStage.IdPortenToken);

        var refused = new AltinnTestKit().ConfigureUnattended();
        refused.Http.Fail["token"] = HttpStatusCode.BadRequest;
        var result = await refused.Unattended(new AltinnTestSessionCache(TimeProvider.System)).AuthenticateAsync(Request(), default);
        result.Stage.Should().Be(AltinnAuthenticationStage.IdPortenToken);
        result.Detail.Should().Contain("HTTP 400").And.NotContain("SECRET-LOOKING");

        var lifetime = new AltinnTestKit().ConfigureUnattended();
        lifetime.Http.TokenExpiresIn = 0;
        (await lifetime.Unattended(new AltinnTestSessionCache(TimeProvider.System)).AuthenticateAsync(Request(), default)).Stage.Should().Be(AltinnAuthenticationStage.IdPortenToken);
    }

    // ── Altinn exchange and cache ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheAccessToken_IsExchangedAtAltinnTt02_AndOnlyTheAltinnTokenIsHeld_InMemory()
    {
        var kit = new AltinnTestKit().ConfigureUnattended();
        var cache = new AltinnTestSessionCache(TimeProvider.System);
        var logger = new ListLogger<UnattendedIdPortenTestAuthentication>();
        var result = await kit.Unattended(cache, logger: logger).AuthenticateAsync(Request(), default);

        result.Success.Should().BeTrue();
        var exchange = kit.Http.Calls.Single(call => call.Uri.Host == "platform.tt02.altinn.no");
        exchange.Uri.ToString().Should().Be("https://platform.tt02.altinn.no/authentication/api/v1/exchange/id-porten");
        exchange.Authorization.Should().Be($"Bearer {AltinnTestKit.IdPortenToken}", "the ID-porten access token, never the id_token, is exchanged");
        result.Token.Should().Be(AltinnTestKit.AltinnToken);
        cache.UnattendedTokens.Keys.Should().Equal((Env, AltinnTestKit.ReporterAlias));
        cache.UnattendedTokens.Values.Single().AltinnToken.Should().Be(AltinnTestKit.AltinnToken);
        cache.Tokens.Should().BeEmpty("unattended tokens are not keyed by a BirkNext execution context");
        result.ToString().Should().NotContain(AltinnTestKit.AltinnToken);

        var logs = string.Join("\n", logger.Messages);
        logs.Should().NotContainAny(AltinnTestKit.IdPortenToken, AltinnTestKit.AltinnToken, AltinnTestKit.HeadlessCode, AltinnTestKit.ClientSecret, AltinnTestKit.ReporterIdentifier, "testid:");
        logs.Should().Contain(AltinnTestKit.ReporterAlias);
    }

    [Fact]
    public async Task AnAltinnExchangeFailure_IsItsOwnStage()
    {
        var kit = new AltinnTestKit().ConfigureUnattended();
        kit.Http.Fail["exchange"] = HttpStatusCode.Unauthorized;
        var cache = new AltinnTestSessionCache(TimeProvider.System);
        var result = await kit.Unattended(cache).AuthenticateAsync(Request(), default);

        result.Stage.Should().Be(AltinnAuthenticationStage.AltinnExchange);
        result.Detail.Should().Contain("Altinn profile");
        cache.UnattendedTokens.Should().BeEmpty();
    }

    [Fact]
    public async Task HeldTokens_Expire_AreNeverSharedAcrossReporters_AndConcurrentRunsLogInOnce()
    {
        var kit = new AltinnTestKit().ConfigureUnattended();
        kit.Options.UnattendedIdPortenTest.Reporters.Add(new SyntheticReporterOptions { Alias = "reporter-b", Label = "B", TestIdPersonIdentifier = AltinnTestKit.ReporterIdentifier, AltinnProfileConfirmed = true });
        var clock = new ManualClock(Start);
        var cache = new AltinnTestSessionCache(clock);
        var provider = kit.Unattended(cache, clock);

        var parallel = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => provider.AuthenticateAsync(Request(), default)));
        parallel.Should().OnlyContain(item => item.Success);
        kit.Http.Calls.Count(call => call.Uri.AbsolutePath == "/authorize").Should().Be(1, "concurrent runs for one reporter share one headless login");

        (await provider.AuthenticateAsync(Request("reporter-b"), default)).Success.Should().BeTrue();
        kit.Http.Calls.Count(call => call.Uri.AbsolutePath == "/authorize").Should().Be(2, "another reporter never reuses reporter-a's token");
        cache.UnattendedTokens.Keys.Should().BeEquivalentTo([(Env, AltinnTestKit.ReporterAlias), (Env, "reporter-b")]);

        clock.Advance(TimeSpan.FromMinutes(3));
        (await provider.AuthenticateAsync(Request(), default)).Success.Should().BeTrue();
        kit.Http.Calls.Count(call => call.Uri.AbsolutePath == "/authorize").Should().Be(3, "an expired token is not used; a new login is made");
    }

    [Fact]
    public async Task Cancellation_StopsTheLogin()
    {
        var kit = new AltinnTestKit().ConfigureUnattended();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await kit.Unattended(new AltinnTestSessionCache(TimeProvider.System)).Invoking(item => item.AuthenticateAsync(Request(), cancelled.Token))
            .Should().ThrowAsync<OperationCanceledException>();
        kit.Http.Calls.Should().BeEmpty();
    }

    // ── Configuration, reporter and Production boundaries ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("disabled", "not enabled")]
    [InlineData("no-secret", "client secret is not available")]
    [InlineData("no-reporter", "No approved synthetic TEST reporter")]
    [InlineData("unknown-alias", "not an approved synthetic TEST reporter")]
    [InlineData("real-identifier", "not a synthetic Test-Norge identifier")]
    [InlineData("bad-acr", "security level")]
    [InlineData("http-redirect", "redirect URI")]
    [InlineData("production", "Production")]
    [InlineData("interactive-type", "Authentication type is not ID-porten")]
    public async Task MissingOrUnsafeConfiguration_SendsNothing_AndSaysWhatIsMissing(string problem, string detail)
    {
        var kit = new AltinnTestKit().ConfigureUnattended(enabled: problem != "disabled", secret: problem != "no-secret", reporter: problem != "no-reporter");
        var configuration = AltinnTestKit.ReadyConfiguration;
        var reporter = AltinnTestKit.ReporterAlias;
        switch (problem)
        {
            case "unknown-alias": reporter = "someone-else"; break;
            case "real-identifier": kit.Options.UnattendedIdPortenTest.Reporters[0].TestIdPersonIdentifier = "01019012345"; break;
            case "bad-acr": kit.Options.UnattendedIdPortenTest.Reporters[0].AcrValue = "level4"; break;
            case "http-redirect": kit.Options.UnattendedIdPortenTest.RedirectUri = "http://birknext.example/cb"; kit.Options.IdPortenTest.RedirectUri = null; break;
            case "production": configuration = configuration with { Environment = ExternalTestEnvironment.Production }; break;
            case "interactive-type": configuration = configuration with { AuthenticationType = ExternalAuthenticationType.Maskinporten }; break;
        }

        var result = await kit.Unattended(new AltinnTestSessionCache(TimeProvider.System)).AuthenticateAsync(Request(reporter, configuration), default);

        result.Success.Should().BeFalse();
        result.Stage.Should().Be(AltinnAuthenticationStage.Configuration);
        result.Detail.Should().Contain(detail).And.NotContain(AltinnTestKit.ClientSecret).And.NotContain(AltinnTestKit.ReporterIdentifier).And.NotContain("01019012345");
        kit.Http.Calls.Should().BeEmpty();
    }

    [Fact]
    public void ProductionIdPortenAndAltinnHosts_StayBlockedForEveryRequest()
    {
        AltinnTestingPolicy.RequestBlock(new Uri("https://login.idporten.no/authorize?login_hint=testid:x")).Should().Contain("production");
        AltinnTestingPolicy.RequestBlock(new Uri("https://idporten.no/token")).Should().Contain("production");
        AltinnTestingPolicy.RequestBlock(new Uri("https://platform.altinn.no/authentication/api/v1/exchange/id-porten")).Should().Contain("production");
        AltinnTestingPolicy.RequestBlock(new Uri("https://attacker.example/cb?code=x")).Should().NotBeNull();
    }

    // ── The one-click Rapportmottak run ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task OneClick_AuthenticatesUnattended_SubmitsInAltinn_AndLeavesEveryDownstreamStepNotAssessed()
    {
        var kit = new AltinnTestKit();
        await using var harness = Harness(kit);
        await kit.ConfigureReadyAsync(harness);
        kit.Http.Calls.Clear();
        var child = SyntheticPersonIdentifier.Generate();

        var run = await Service(harness, kit).StartAsync(Run("valid-report", Report("registered", child)), Actor, default);

        run.Steps.Take(6).Select(step => (step.StepId, step.State)).Should().Equal(
            ("altinn-auth", JourneyStepState.Verified), ("altinn-instance", JourneyStepState.Verified), ("altinn-data", JourneyStepState.Verified),
            ("altinn-validate", JourneyStepState.Verified), ("altinn-submit", JourneyStepState.Verified), ("altinn-receipt", JourneyStepState.Verified));
        var auth = run.Steps[0];
        auth.Facts.Should().Contain(new JourneyEvidenceFact("Authentication", "Unattended ID-porten TEST (synthetic reporter)"));
        auth.Reason.Should().Contain("reporter-a").And.Contain("Not a submission");
        run.Steps.Single(step => step.StepId == "altinn-submit").Facts.Select(fact => fact.Label).Should().Contain(["Process advanced", "Submitted"]);
        run.Steps.Skip(6).Should().OnlyContain(step => step.State == JourneyStepState.NotAssessed, "MU, MM, Skolenærværsrapport and Person have no evidence");
        run.OverallState.Should().Be(JourneyRunState.Partial, "Altinn success never completes Rapportmottak");
        run.InputSummary.Should().Contain(item => item.Contains("reporter-a"));
        run.InitiatedBy.Should().Be(Actor);

        kit.Http.Calls.Select(call => $"{call.Method} {call.Uri.Host}{call.Uri.AbsolutePath}".Replace(AltinnTestKit.InstanceGuid.ToString("D"), "{i}").Replace(AltinnTestKit.DataGuid.ToString("D"), "{d}")).Should().Equal(
            "GET test.idporten.no/.well-known/openid-configuration", "GET login.test.idporten.no/authorize", "POST test.idporten.no/token",
            "GET platform.tt02.altinn.no/authentication/api/v1/exchange/id-porten",
            "GET buf.apps.tt02.altinn.no/buf/skolenaervaer/api/authorization/parties/current",
            "POST buf.apps.tt02.altinn.no/buf/skolenaervaer/instances",
            $"PUT buf.apps.tt02.altinn.no/buf/skolenaervaer/instances/{AltinnTestKit.PartyId}/{{i}}/data/{{d}}",
            $"GET buf.apps.tt02.altinn.no/buf/skolenaervaer/instances/{AltinnTestKit.PartyId}/{{i}}/validate",
            $"GET buf.apps.tt02.altinn.no/buf/skolenaervaer/instances/{AltinnTestKit.PartyId}/{{i}}",
            $"PUT buf.apps.tt02.altinn.no/buf/skolenaervaer/instances/{AltinnTestKit.PartyId}/{{i}}/process/next",
            $"GET buf.apps.tt02.altinn.no/buf/skolenaervaer/instances/{AltinnTestKit.PartyId}/{{i}}");

        var stored = JsonSerializer.Serialize(await harness.Store.GetAsync(run.RunId, default));
        stored.Should().NotContainAny(AltinnTestKit.IdPortenToken, AltinnTestKit.AltinnToken, AltinnTestKit.HeadlessCode, AltinnTestKit.ClientSecret,
            AltinnTestKit.ReporterIdentifier, child, "testid:", "refresh-DO-NOT-PERSIST", AltinnTestKit.HeadlessRedirect);
        Regex.IsMatch(stored, @"(?<!\d)\d{11}(?!\d)").Should().BeFalse("neither the reporter's nor the child's identifier is persisted");
    }

    [Fact]
    public async Task WithoutTheServiceOwnersPolicyConfirmation_SubmissionsStayNotReady_AndNothingIsSent()
    {
        var kit = new AltinnTestKit();
        await using var harness = Harness(kit);
        await kit.ConfigureReadyAsync(harness);
        kit.Options.ConfirmedAppPolicies.Clear();
        kit.Http.Calls.Clear();
        var service = Service(harness, kit);

        var view = (await service.PacksAsync(Env, default)).Single().Journeys.Single(item => item.Journey.JourneyId == "rapportmottak");
        var policy = view.Prerequisites.Single(item => item.Key == "app-policy");
        policy.State.Should().Be(JourneyReadinessState.NotReady);
        policy.Detail.Should().Contain("App authorization policy not confirmed");
        view.RunnableScenarios.Should().NotContain(SkoleNaervaerJourneyPack.SubmissionScenarios);

        var run = await service.StartAsync(Run("valid-report", Report("registered", SyntheticPersonIdentifier.Generate())), Actor, default);
        run.OverallState.Should().Be(JourneyRunState.Blocked);
        run.StateReason.Should().Contain("App authorization policy not confirmed");
        kit.Http.Calls.Should().BeEmpty("no ID-porten or Altinn request without the policy confirmation");

        // Even if readiness were bypassed, the executor re-checks it before authenticating.
        var executor = Executor(harness, kit);
        var journey = Pack(harness, kit).Journeys.Single(item => item.JourneyId == "rapportmottak");
        var trusted = BirkNext.Api.Services.ActiveEventTesting.TrustedExecutionEnvironmentRegistry.From(harness.Active.Configuration()).Resolve(Env, out _);
        var prepared = SkoleNaervaerReportInput.Prepare(journey.Scenarios[0], Report("registered", SyntheticPersonIdentifier.Generate()));
        var result = await executor.ExecuteAsync(new JourneyStepContext(Guid.NewGuid(), Env, trusted, journey, journey.Scenarios[0], journey.Steps[0], null, [], prepared.Values, Actor), default);
        result.State.Should().Be(JourneyStepState.Unavailable);
        result.Reason.Should().Contain("not confirmed");
        kit.Http.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Readiness_ShowsEachUnattendedPrerequisite_OnItsOwn()
    {
        var kit = new AltinnTestKit();
        await using var harness = Harness(kit);
        await kit.ConfigureReadyAsync(harness);
        kit.ConfigureUnattended(secret: false);
        kit.Options.IdPortenTest.RedirectUri = null;
        var view = (await Service(harness, kit).PacksAsync(Env, default)).Single().Journeys.Single(item => item.Journey.JourneyId == "rapportmottak");

        string State(string key) => view.Prerequisites.Single(item => item.Key == key).State.ToString();
        State("unattended-provider").Should().Be("Ready");
        State("idporten-client").Should().Be("Ready");
        State("client-secret").Should().Be("NotConfigured");
        State("synthetic-reporter").Should().Be("Ready");
        State("app-policy").Should().Be("Ready");
        State("reporter-auth").Should().Be("NotConfigured", "neither the unattended (secret missing) nor the interactive mode is configured");
        view.Prerequisites.Single(item => item.Key == "client-secret").Detail.Should().NotContain(AltinnTestKit.ClientSecret);
        view.RunnableScenarios.Should().BeEmpty();
        JsonSerializer.Serialize(view).Should().NotContain(AltinnTestKit.ReporterIdentifier, "the reporter is offered by alias only");
        view.Journey.Scenarios.Single(item => item.ScenarioId == "valid-report").Inputs!.Single(item => item.Key == "reporter").Options!
            .Select(item => item.Value).Should().Equal(AltinnTestKit.ReporterAlias, AltinnTestingPolicy.InteractiveReporter);
    }

    [Theory]
    [InlineData("authorize", HttpStatusCode.BadRequest, JourneyStepState.NotVerified, "ID-porten TEST authorization")]
    [InlineData("token", HttpStatusCode.BadRequest, JourneyStepState.NotVerified, "ID-porten TEST token request")]
    [InlineData("exchange", HttpStatusCode.Forbidden, JourneyStepState.NotVerified, "Altinn TT02 token exchange")]
    [InlineData("party", HttpStatusCode.Forbidden, JourneyStepState.NotVerified, "Altinn app authorization (current party)")]
    [InlineData("secret", HttpStatusCode.OK, JourneyStepState.Unavailable, "Authentication configuration")]
    public async Task EachAuthenticationFailure_NamesItsStage_AndNothingAfterItRuns(string stage, HttpStatusCode status, JourneyStepState state, string label)
    {
        var kit = new AltinnTestKit();
        await using var harness = Harness(kit);
        await kit.ConfigureReadyAsync(harness);
        if (stage == "secret") kit.Options.UnattendedIdPortenTest.ClientSecret = null; else kit.Http.Fail[stage] = status;
        // Keep readiness satisfied through the interactive fallback configuration so the run reaches the auth step and fails there.
        var run = await Service(harness, kit).StartAsync(Run("valid-report", Report("registered", SyntheticPersonIdentifier.Generate())), Actor, default);

        var auth = run.Steps[0];
        auth.State.Should().Be(state);
        auth.Facts.Should().Contain(new JourneyEvidenceFact("Failed stage", label));
        (auth.Evidence + auth.Reason).Should().NotContain("SECRET-LOOKING");
        run.Steps.Skip(1).Should().OnlyContain(step => step.State == JourneyStepState.NotAssessed);
        kit.Http.Calls.Should().NotContain(call => call.Uri.AbsolutePath.EndsWith("/instances", StringComparison.Ordinal));
        run.OverallState.Should().NotBe(JourneyRunState.Completed);
    }

    [Fact]
    public async Task ChildNotFound_AndOverlappingReport_RunUnattended_WithTheChildSeparateFromTheReporter()
    {
        var kit = new AltinnTestKit();
        await using var harness = Harness(kit);
        await kit.ConfigureReadyAsync(harness);
        var service = Service(harness, kit);

        var unknown = await service.StartAsync(Run("person-not-found", Report("generated", "")), Actor, default);
        unknown.Steps.Take(6).Should().OnlyContain(step => step.State == JourneyStepState.Verified);
        unknown.InputSummary.Should().Contain(item => item.Contains("unknown child; not verified by BirkNext"));
        unknown.Steps.Single(step => step.StepId == "report-person").State.Should().Be(JourneyStepState.NotAssessed, "Person's outcome is downstream and unobserved");

        var overlap = await service.StartAsync(Run("overlapping-report", new Dictionary<string, string>(Report("registered", SyntheticPersonIdentifier.Generate())) { ["priorReport"] = "declared" }), Actor, default);
        overlap.Steps.Take(6).Should().OnlyContain(step => step.State == JourneyStepState.Verified);
        overlap.OverallState.Should().Be(JourneyRunState.Partial);
        var body = JsonDocument.Parse(kit.Http.Calls.Last(call => call.Method == HttpMethod.Put && call.Uri.AbsolutePath.Contains("/data/")).Body!).RootElement;
        body.GetProperty("Klient").GetProperty("Identifikator").GetProperty("Fodselsnummer").GetString().Should().NotBe(AltinnTestKit.ReporterIdentifier, "the reporter is never the child");
    }

    [Fact]
    public async Task ReadOnlyStorageEvidence_ReusesTheUnattendedReporterToken()
    {
        var kit = new AltinnTestKit();
        await using var harness = Harness(kit);
        await kit.ConfigureReadyAsync(harness);
        kit.Http.Calls.Clear();

        var run = await Service(harness, kit).StartAsync(Run(SkoleNaervaerJourneyPack.InstanceEvidenceScenario, new Dictionary<string, string>
        {
            ["reporter"] = AltinnTestKit.ReporterAlias, [SkoleNaervaerInstanceReference.PartyKey] = AltinnTestKit.PartyId.ToString(),
            [SkoleNaervaerInstanceReference.InstanceKey] = AltinnTestKit.InstanceGuid.ToString("D"),
        }), Actor, default);

        run.Steps[0].State.Should().Be(JourneyStepState.Observed);
        var storage = kit.Http.Calls.Single(call => call.Uri.AbsolutePath.StartsWith("/storage/", StringComparison.Ordinal));
        storage.Method.Should().Be(HttpMethod.Get);
        storage.Authorization.Should().Be($"Bearer {AltinnTestKit.AltinnToken}");
        kit.Http.Calls.Should().NotContain(call => call.Method != HttpMethod.Get && call.Uri.Host.Contains("altinn"), "Storage evidence never submits");
        run.Steps.Should().NotContain(step => step.State == JourneyStepState.Verified, "an observed instance is not a verified submission");
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────────────────────────────

    private static Dictionary<string, string> Report(string source, string child) => new()
    {
        ["reporter"] = AltinnTestKit.ReporterAlias, [SkoleNaervaerReportInput.IdentitySourceKey] = source, [SkoleNaervaerAltinnForm.ChildIdentifier] = child,
        [SkoleNaervaerAltinnForm.PeriodFrom] = "2026-10-01", [SkoleNaervaerAltinnForm.PeriodTo] = "2026-10-31",
        [SkoleNaervaerAltinnForm.PlannedHours] = "100", [SkoleNaervaerAltinnForm.ActualHours] = "92",
    };

    private static IntegrationJourneyRunRequest Run(string scenario, IReadOnlyDictionary<string, string> inputs) => new()
    {
        EnvironmentId = Env, PackId = "skolenaervaer", JourneyId = "rapportmottak", ScenarioId = scenario, Confirmed = true, Inputs = inputs,
    };

    private static SkoleNaervaerJourneyPack Pack(IntegrationJourneyHarness harness, AltinnTestKit kit) => new(harness.MessageFlows, kit.Policy, kit.Contracts(harness.MessageFlows, harness.Sources));

    private static IServiceScopeFactory Scopes(IntegrationJourneyHarness harness, AltinnTestKit kit)
    {
        var services = new ServiceCollection();
        services.AddSingleton<BirkNext.Api.Services.Integrations.IIntegrationMessageFlowStore>(harness.MessageFlows);
        services.AddTransient(_ => kit.Policy);
        services.AddTransient(_ => kit.Contracts(harness.MessageFlows, harness.Sources));
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

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
        var authenticator = kit.Authenticator(harness.Tokens);
        harness.Executors.Add(new SkoleNaervaerAltinnExecutor(Scopes(harness, kit), authenticator, new AltinnAppApiClient(kit.Factory), TimeProvider.System));
        harness.Observers.Add(new SkoleNaervaerAltinnInstanceObserver(Scopes(harness, kit), authenticator, new AltinnStorageInstanceReader(kit.Factory), TimeProvider.System));
        return harness;
    }

    private static IntegrationJourneyService Service(IntegrationJourneyHarness harness, AltinnTestKit kit) => harness.Service(Pack(harness, kit));
}
