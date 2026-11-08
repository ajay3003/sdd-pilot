using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using BirkNext.Api.Data;
using BirkNext.Api.Services.ApiQuality;
using BirkNext.Api.Services.ExecutionEvidence;
using BirkNext.Api.Services.LocalHttpsProxy;
using BirkNext.ExecutionEvidence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BirkNext.Api.Tests.Services.ContractTesting;

/// <summary>
/// The attended session across a multi-call run: one session reused by every call, expiry mid-run (Blocked for authentication, never sent
/// anonymously), a bounded wait for a renewed sign-in of the same identity (next generation), an identity change refused, cancellation while
/// waiting, an ended session, and per-call outcomes for 401 / 403 / GraphQL UNAUTHENTICATED. The synthetic HTTPS target is an in-memory handler
/// behind the real capture pipeline; it records every request that reached it and is the oracle. Tokens are synthetic unsigned JWTs.
/// </summary>
public sealed class ExecutionAuthenticationLifecycleTests : IAsyncDisposable
{
    private const string Environment = "dev";
    private const string Fingerprint = "fp-dev";
    private readonly ConcurrentQueue<(string Path, string? Authorization)> _seen = new();
    private readonly TransientAuthenticatedApiContextStore _attended;
    private readonly ServiceProvider _services;
    private DateTimeOffset _now = DateTimeOffset.UtcNow;
    private Action<string>? _onRequest;

    public ExecutionAuthenticationLifecycleTests()
    {
        _attended = new TransientAuthenticatedApiContextStore(() => _now);
        var services = new ServiceCollection();
        var db = $"auth-lifecycle-{Guid.NewGuid():N}";
        services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(db));
        services.AddLogging();
        services.AddScoped<ExecutionEvidenceStore>();
        services.AddTransient<EvidenceCaptureHandler>();
        services.AddHttpClient(ExecutionRunService.ClientName).ConfigurePrimaryHttpMessageHandler(() => new Target(this)).AddHttpMessageHandler<EvidenceCaptureHandler>();
        services.AddSingleton<IApiEnvironmentSafetyPolicy>(ApiEnvironmentSafetyPolicy.Default);
        services.AddSingleton(_attended);
        services.AddSingleton(sp => new ExecutionCredentialResolver(_attended, null, sp.GetRequiredService<IHttpClientFactory>()));
        services.AddSingleton<ExecutionRunService>();
        _services = services.BuildServiceProvider();
    }

    public async ValueTask DisposeAsync() => await _services.DisposeAsync();

    /// <summary>The synthetic API: accepts any bearer token, except /deny401, /deny403 and /graphql-unauth which answer as their names say.</summary>
    private sealed class Target(ExecutionAuthenticationLifecycleTests owner) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            owner._seen.Enqueue((path, request.Headers.Authorization?.ToString()));
            owner._onRequest?.Invoke(path);
            var (status, body) = path switch
            {
                "/deny401" => (401, """{"error":"invalid_token"}"""),
                "/deny403" => (403, """{"error":"insufficient_scope"}"""),
                "/graphql-unauth" => (200, """{"data":null,"errors":[{"message":"Not signed in","extensions":{"code":"UNAUTHENTICATED"}}]}"""),
                _ => (200, """{"ok":true}"""),
            };
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json"), RequestMessage = request });
        }
    }

    private static string Token(string subject, int generation) =>
        BearerTokenInspector.BuildUnsignedJwt(new { iss = "https://idp.synthetic.test/", sub = subject, aud = "api://synthetic", jti = $"g{generation}", exp = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds() });

    private void SignIn(string subject, int generation, TimeSpan lifetime)
    {
        var token = Token(subject, generation);
        ((ITransientCredentialSink)_attended).Store(Environment, Fingerprint, "api.example.test", ApprovedHostSet.Create("https://api.example.test/", null), token, _now + lifetime, "JWT",
            BearerTokenInspector.Inspect(token).Identity);
    }

    private static ExecutionRunRequest Run(int calls, int waitSeconds = 0, Func<int, string>? path = null) => new()
    {
        Name = "Attended lifecycle", EnvironmentId = Environment, ProfileId = Environment, ContextFingerprint = Fingerprint, TargetUrl = "https://api.example.test/",
        EnvironmentType = "Development", Authentication = ExecutionAuthenticationKind.AttendedSession, ReauthenticationWaitSeconds = waitSeconds,
        Calls = [.. Enumerable.Range(1, calls).Select(i => new ExecutionCallDefinition
        {
            Name = $"call {i}", Method = "GET", Path = path?.Invoke(i) ?? $"/children/{i}", Assertions = [new("status", AssertionKind.StatusCode, AssertionOperator.Equals, null, "200")],
        })],
    };

    private async Task<(ExecutionRunStartResult Start, ExecutionRunDetail Detail, string Json)> RunAsync(ExecutionRunRequest request, Action<Guid>? whileRunning = null)
    {
        var start = await _services.GetRequiredService<ExecutionRunService>().StartAsync(request, "test", default);
        start.RunId.Should().NotBeNull(string.Join(" ", start.Blockers));
        var id = start.RunId!.Value;
        whileRunning?.Invoke(id);
        for (var i = 0; i < 600 && ExecutionRunService.IsRunning(id); i++) await Task.Delay(50);
        using var scope = _services.CreateScope();
        var detail = (await scope.ServiceProvider.GetRequiredService<ExecutionEvidenceStore>().GetAsync(id, new(Take: 200), default))!;
        return (start, detail, JsonSerializer.Serialize(detail, ExecutionEvidenceStore.Json));
    }

    [Fact]
    public async Task One_attended_session_serves_every_call_of_a_long_run_and_never_reaches_the_evidence()
    {
        SignIn("tester-a", 1, TimeSpan.FromMinutes(10));
        var (_, run, json) = await RunAsync(Run(40));
        run.Run.State.Should().Be(ExecutionState.Passed);
        run.Calls.Should().OnlyContain(c => c.Authentication == CallAuthenticationOutcome.Authenticated && c.Attempts.Single().AuthenticationSession == "attended-session:dev#g1");
        run.Run.AuthenticationSessions.Should().Equal("attended-session:dev#g1");
        _seen.Should().HaveCount(40).And.OnlyContain(s => s.Authorization != null && s.Authorization.StartsWith("Bearer ey"));
        var token = _seen.First().Authorization!["Bearer ".Length..];
        json.Should().NotContain(token).And.NotContain(token.Split('.')[1], "not even the payload segment is stored");
    }

    [Fact]
    public async Task A_session_that_expires_mid_run_blocks_the_remaining_calls_and_nothing_is_sent_anonymously()
    {
        SignIn("tester-a", 1, TimeSpan.FromMinutes(10));
        _onRequest = p => { if (p == "/children/5") _now = _now.AddMinutes(11); };
        var (_, run, _) = await RunAsync(Run(12));
        run.Calls.Take(5).Should().OnlyContain(c => c.State == ExecutionState.Passed && c.Authentication == CallAuthenticationOutcome.Authenticated);
        run.Calls.Skip(5).Should().OnlyContain(c => c.State == ExecutionState.Blocked && c.Authentication == CallAuthenticationOutcome.BlockedForAuthentication
            && c.Attempts.Single().Transport == TransportResult.NotSent && c.Attempts.Single().Request == null && c.Attempts.Single().Response == null);
        _seen.Should().HaveCount(5, "no request after the expiry reached the target").And.OnlyContain(s => s.Authorization != null);
        run.Run.Counts.Blocked.Should().Be(7);
    }

    [Fact]
    public async Task A_renewed_sign_in_of_the_same_identity_resumes_the_run_with_the_next_generation()
    {
        SignIn("tester-a", 1, TimeSpan.FromMinutes(10));
        _onRequest = p =>
        {
            if (p != "/children/4") return;
            _now = _now.AddMinutes(11);
            // The tester signs in again a moment later; the waiting call must pick up the renewed session.
            _ = Task.Run(async () => { await Task.Delay(1200); SignIn("tester-a", 2, TimeSpan.FromMinutes(10)); });
        };
        var (_, run, _) = await RunAsync(Run(10, waitSeconds: 30));
        run.Run.State.Should().Be(ExecutionState.Passed);
        run.Calls.Should().OnlyContain(c => c.Authentication == CallAuthenticationOutcome.Authenticated);
        run.Calls.Take(4).Should().OnlyContain(c => c.Attempts.Single().AuthenticationSession == "attended-session:dev#g1");
        run.Calls.Skip(4).Should().OnlyContain(c => c.Attempts.Single().AuthenticationSession == "attended-session:dev#g2");
        run.Run.AuthenticationSessions.Should().Equal("attended-session:dev#g1", "attended-session:dev#g2");
        run.Run.ReauthenticationWaitSeconds.Should().Be(30);
        _seen.Should().HaveCount(10).And.OnlyContain(s => s.Authorization != null);
    }

    [Fact]
    public async Task A_renewed_session_of_another_identity_is_never_used_by_the_run()
    {
        SignIn("tester-a", 1, TimeSpan.FromMinutes(10));
        _onRequest = p =>
        {
            if (p != "/children/3") return;
            _now = _now.AddMinutes(11);
            _ = Task.Run(async () => { await Task.Delay(800); SignIn("tester-b", 2, TimeSpan.FromMinutes(10)); });
        };
        var (_, run, _) = await RunAsync(Run(6, waitSeconds: 20));
        run.Calls.Take(3).Should().OnlyContain(c => c.State == ExecutionState.Passed);
        run.Calls.Skip(3).Should().OnlyContain(c => c.State == ExecutionState.Blocked && c.Authentication == CallAuthenticationOutcome.BlockedForAuthentication);
        run.Calls[3].Attempts.Single().Error.Should().Contain("different account");
        _seen.Should().HaveCount(3);
    }

    [Fact]
    public async Task Cancelling_while_waiting_for_a_renewed_session_sends_nothing_more()
    {
        SignIn("tester-a", 1, TimeSpan.FromMinutes(10));
        _onRequest = p => { if (p == "/children/2") _now = _now.AddMinutes(11); };
        var (_, run, _) = await RunAsync(Run(5, waitSeconds: 60), id => _ = Task.Run(async () =>
        {
            // Cancel once the third call shows that it is waiting.
            for (var i = 0; i < 200; i++)
            {
                await Task.Delay(50);
                using var scope = _services.CreateScope();
                var call = (await scope.ServiceProvider.GetRequiredService<ExecutionEvidenceStore>().GetAsync(id, new(Take: 10), default))!.Calls[2];
                if (call.Reason?.StartsWith("Waiting for the attended session") == true) { ExecutionRunService.Cancel(id); return; }
            }
        }));
        run.Calls[2].State.Should().Be(ExecutionState.Cancelled);
        run.Calls.Skip(3).Should().OnlyContain(c => c.State == ExecutionState.Cancelled);
        _seen.Should().HaveCount(2);
        run.Run.State.Should().Be(ExecutionState.Cancelled);
    }

    [Fact]
    public async Task Ending_the_attended_session_blocks_later_runs_until_a_new_sign_in_starts_the_next_generation()
    {
        SignIn("tester-a", 1, TimeSpan.FromMinutes(10));
        (await RunAsync(Run(2))).Detail.Run.State.Should().Be(ExecutionState.Passed);
        _attended.Invalidate(Environment);
        var refused = await _services.GetRequiredService<ExecutionRunService>().StartAsync(Run(2), "test", default);
        refused.State.Should().Be(ExecutionState.Blocked);
        refused.Blockers.Should().Contain(b => b.Contains("No attended session"));
        SignIn("tester-a", 2, TimeSpan.FromMinutes(10));
        var (_, again, _) = await RunAsync(Run(2));
        again.Run.AuthenticationSessions.Should().Equal("attended-session:dev#g2");
        _seen.Should().HaveCount(4);
    }

    [Fact]
    public async Task Rejections_by_the_target_are_classified_per_call()
    {
        SignIn("tester-a", 1, TimeSpan.FromMinutes(10));
        var request = Run(1) with
        {
            Calls =
            [
                new() { Name = "ok", Method = "GET", Path = "/children/1" },
                new() { Name = "401", Method = "GET", Path = "/deny401" },
                new() { Name = "403", Method = "GET", Path = "/deny403" },
                new() { Name = "graphql", Protocol = ExecutionProtocol.GraphQl, Path = "/graphql-unauth", GraphQlQuery = "query { me { id } }" },
                new() { Name = "negative", Method = "GET", Path = "/children/9", WithoutAuthentication = true },
            ],
        };
        var (_, run, _) = await RunAsync(request);
        run.Calls.Select(c => c.Authentication).Should().Equal(CallAuthenticationOutcome.Authenticated, CallAuthenticationOutcome.FailedAuthentication,
            CallAuthenticationOutcome.AuthorizationDenied, CallAuthenticationOutcome.FailedAuthentication, CallAuthenticationOutcome.AnonymousByDesign);
        _seen.Last().Authorization.Should().BeNull("the negative test is sent without the credential on purpose");
    }

    [Fact]
    public async Task Re_observing_the_same_token_keeps_the_generation_and_a_new_token_increments_it()
    {
        var token = Token("tester-a", 1);
        var sink = (ITransientCredentialSink)_attended;
        var scope = ApprovedHostSet.Create("https://api.example.test/", null);
        sink.Store(Environment, Fingerprint, "api.example.test", scope, token, _now.AddMinutes(5), "JWT", "id-a");
        sink.Store(Environment, Fingerprint, "api.example.test", scope, token, _now.AddMinutes(5), "JWT", "id-a");
        _attended.Describe(Environment, Fingerprint)!.Generation.Should().Be(1);
        sink.Store(Environment, Fingerprint, "api.example.test", scope, Token("tester-a", 2), _now.AddMinutes(5), "JWT", "id-a");
        _attended.Describe(Environment, Fingerprint)!.Generation.Should().Be(2);
        BearerTokenInspector.Inspect(token).Identity.Should().MatchRegex("^[0-9a-f]{12}$").And.NotContain("tester");
        BearerTokenInspector.Inspect(token).Identity.Should().Be(BearerTokenInspector.Inspect(Token("tester-a", 9)).Identity, "same issuer and subject");
        BearerTokenInspector.Inspect(token).Identity.Should().NotBe(BearerTokenInspector.Inspect(Token("tester-b", 1)).Identity);
        await Task.CompletedTask;
    }
}
