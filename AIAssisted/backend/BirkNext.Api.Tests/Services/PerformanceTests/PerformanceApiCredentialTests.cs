using BirkNext.Api.Services.ContainerRuntime;
using BirkNext.Api.Services.LocalHttpsProxy;
using BirkNext.Api.Services.PerformanceTests;
using BirkNext.LocalHttpsProxy;
using BirkNext.PerformanceTests;
using FluentAssertions;

namespace BirkNext.Api.Tests.Services.PerformanceTests;

/// <summary>
/// Target Environment API credential for performance runs: the token the Local HTTPS proxy captured from the person's browser session. It
/// satisfies an authentication requirement observed by Endpoint Discovery, is executable by k6, is checked (present, in scope, valid for the
/// whole run, this environment's own identity) before a run, and reaches k6 only through the container's process environment — never a
/// script, a command line or a log. Attended Target Environment sign-in stays non-executable.
/// </summary>
public sealed class PerformanceApiCredentialTests
{
    const string Env = "dev";
    const string Fp = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    static readonly DateTimeOffset Now = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
    static readonly string Token = BearerTokenInspector.BuildUnsignedJwt(new { exp = Now.AddHours(1).ToUnixTimeSeconds(), aud = "api://dev" });
    static readonly AuthenticatedReviewIdentity Identity = new(AuthenticatedTestingMethod.LocalHttpsProxy, Env, Fp);

    static PerformanceTestDefinition Definition(string? auth, int steadySeconds = 300) => new()
    {
        Id = "def", EnvironmentId = Env, TargetOrigin = "https://api.example.test", EnvironmentType = "Development", AuthenticationReference = auth,
        Workload = new() { SteadyStateSeconds = steadySeconds },
        Scenario = new()
        {
            Steps = [new HttpPerformanceStep
            {
                Id = "s1", Name = "getchildrenforuser", Method = "GET", RelativePath = "/api/autorisasjon/v1/brukere/{brukerId}/barn", ExpectedStatusCodes = [200],
                Source = new() { ObservedAuthentication = "Authenticated requests observed by Endpoint Discovery" },
            }],
        },
    };

    static TransientAuthenticatedApiContextStore Store(DateTimeOffset expires, string host = "api.example.test")
    {
        var store = new TransientAuthenticatedApiContextStore(() => Now);
        ((ITransientCredentialSink)store).Store(Env, Fp, host, ApprovedHostSet.Create($"https://{host}/", [host]), Token, expires, "JWT");
        return store;
    }

    [Fact]
    public void The_api_credential_satisfies_an_observed_requirement_and_is_executable_while_attended_sign_in_is_not()
    {
        var withApi = Definition(PerformanceScenarioRuntime.TargetEnvironmentApiCredential);
        PerformanceScenarioRuntime.AuthenticationRequirements(withApi).Single().State.Should().Be(StepAuthenticationState.Configured);
        PerformanceScenarioRuntime.ExecutionCredential(withApi, withApi.Scenario.Steps[0])!.PerformanceCompatible.Should().BeTrue();
        PerformanceScenarioRuntime.ExecutionCredentialBlocked(withApi).Should().BeFalse();
        PerformanceScenarioRuntime.UsesApiCredential(withApi).Should().BeTrue();
        PerformanceTestSafety.Check(withApi, null, new PerformanceTestOptions()).Should().NotContain(i => i.Key == "authentication",
            "the API credential is a configured, executable authentication (its presence is checked when the run starts)");

        var none = Definition(null);
        PerformanceTestSafety.Check(none, null, new PerformanceTestOptions()).Should().Contain(i => i.Message.Contains("None is not a confirmation"), "the original error stays for None");
        var attended = Definition(PerformanceScenarioRuntime.TargetEnvironmentAuthentication);
        PerformanceScenarioRuntime.ExecutionCredentialBlocked(attended).Should().BeTrue("attended sign-in is never executed by k6");
    }

    [Fact]
    public void A_run_needs_the_credential_present_in_scope_from_this_environment_and_valid_for_the_whole_run()
    {
        var d = Definition(PerformanceScenarioRuntime.TargetEnvironmentApiCredential, steadySeconds: 600);
        using var valid = Store(Now.AddMinutes(30));
        PerformanceApiCredential.Problem(valid, d, Env, Identity, Now).Should().BeNull();

        PerformanceApiCredential.Problem(new TransientAuthenticatedApiContextStore(() => Now), d, Env, Identity, Now).Should().StartWith("No API credential has been captured");
        PerformanceApiCredential.Problem(valid, d, Env, null, Now).Should().Contain("proxy identity was not sent");
        PerformanceApiCredential.Problem(valid, d, Env, Identity with { ProfileId = "qa" }, Now).Should().Contain("another Target Environment");
        PerformanceApiCredential.Problem(valid, d, Env, Identity with { ContextFingerprint = new string('B', 64) }, Now).Should().StartWith("No API credential");
        using var shortLived = Store(Now.AddMinutes(11));
        PerformanceApiCredential.Problem(shortLived, d, Env, Identity, Now).Should().Contain("expires at 12:11 UTC, before this run would end",
            "a 10-minute run plus a 2-minute margin outlives an 11-minute token");
        using var otherHost = Store(Now.AddMinutes(30), "other.example.test");
        PerformanceApiCredential.Problem(otherHost, d, Env, Identity, Now).Should().Contain("does not cover the scenario's target host");
        PerformanceApiCredential.Problem(null, Definition(null), Env, null, Now).Should().BeNull("a scenario that does not use it needs nothing");
    }

    [Fact]
    public void The_lease_returns_the_value_for_one_run_and_its_text_form_never_shows_it()
    {
        var d = Definition(PerformanceScenarioRuntime.TargetEnvironmentApiCredential);
        using var store = Store(Now.AddMinutes(30));
        var (credential, problem) = PerformanceApiCredential.Lease(store, d, Env, Identity, Now);
        problem.Should().BeNull();
        credential!.Token.Should().Be(Token);
        credential.ExpiresAt.Should().Be(Now.AddMinutes(30));
        credential.ToString().Should().NotContain(Token);
        new PerformanceProviderInput(Guid.NewGuid(), d, null, "w", TimeSpan.FromMinutes(1)) { Credential = credential }.ToString().Should().NotContain(Token);

        ((ITransientCredentialSink)store).TryLease(Env, Fp, new Uri("https://evil.example.test/"), out _, out _).Should().BeFalse("only approved hosts");
        ((ITransientCredentialSink)store).TryLease(Env, Fp, new Uri("http://api.example.test/"), out _, out _).Should().BeFalse("HTTPS only");
    }

    [Fact]
    public void Validation_requests_get_the_header_in_process_and_only_for_approved_https_hosts()
    {
        using var store = Store(Now.AddMinutes(30));
        using var ok = new HttpRequestMessage(HttpMethod.Get, "https://api.example.test/api/autorisasjon/v1/brukere/1/barn");
        PerformanceApiCredential.Apply(store, Env, Identity, ok).Should().BeNull();
        ok.Headers.Authorization!.Scheme.Should().Be("Bearer");
        using var http = new HttpRequestMessage(HttpMethod.Get, "http://api.example.test/x");
        PerformanceApiCredential.Apply(store, Env, Identity, http).Should().Contain("does not cover");
        http.Headers.Authorization.Should().BeNull();
    }

    [Fact]
    public void K6_reads_the_credential_from_the_container_environment_never_from_the_script_or_command_line()
    {
        var d = Definition(PerformanceScenarioRuntime.TargetEnvironmentApiCredential);
        var script = K6ScriptGenerator.Generate(d, null, "/birknext/out/summary.json");
        script.Should().Contain("\"auth\":true").And.Contain($"__ENV.{K6PerformanceTestProvider.ApiAuthorizationVariable}").And.NotContain("Bearer").And.NotContain(Token);

        var spec = new ContainerRunSpec
        {
            Name = "birknext-k6-run-1", Image = "docker.io/grafana/k6:0.52.0", Command = ["run", "/birknext/in/test.js"],
            SecretEnvironment = new Dictionary<string, string> { [K6PerformanceTestProvider.ApiAuthorizationVariable] = "Bearer " + Token },
        };
        var args = PodmanContainerExecutionRuntime.RunArguments(spec);
        args.Should().ContainInConsecutiveOrder(["--env", K6PerformanceTestProvider.ApiAuthorizationVariable], "only the name; podman reads the value from its own environment");
        string.Join(" ", args).Should().NotContain(Token);
    }
}
