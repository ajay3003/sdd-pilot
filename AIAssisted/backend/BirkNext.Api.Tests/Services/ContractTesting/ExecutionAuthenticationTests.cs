using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using BirkNext.Api.Data;
using BirkNext.Api.Services.ApiQuality;
using BirkNext.Api.Services.ContractAnalysis;
using BirkNext.Api.Services.ContractTesting;
using BirkNext.Api.Services.ExecutionEvidence;
using BirkNext.Api.Services.LocalHttpsProxy;
using BirkNext.Api.Services.TestData;
using BirkNext.Api.Tests.TestInfrastructure;
using BirkNext.ContractTesting;
using BirkNext.ExecutionEvidence;
using BirkNext.TestData;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BirkNext.Api.Tests.Services.ContractTesting;

/// <summary>
/// GAP-CDT-1: authenticated execution through the existing run service. Unattended: OAuth2 client credentials (token from a synthetic local token
/// endpoint, cached) and an API key — encrypted at rest, applied in the backend, recorded only as redacted headers and a configuration reference.
/// Attended: the Local HTTPS proxy credential of the run's own profile, HTTPS + approved hosts only. Attended and unattended never substitute for
/// each other; a credential that cannot be used blocks the call before anything is sent. The synthetic server is the oracle.
/// </summary>
public sealed class ExecutionAuthenticationTests : IAsyncLifetime
{
    private const string ClientSecret = "synthetic-client-secret-5521";
    private const string AccessToken = "synthetic-access-token-000111222";
    private const string ApiKey = "synthetic-api-key-987654321";
    private LoopbackHttpTestServer _server = null!;
    private readonly ConcurrentQueue<(string Method, string Path, string? Authorization, string? Key, string Body)> _seen = new();
    private int _tokenRequests;
    private ServiceProvider _services = null!;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "birknext-auth-" + Guid.NewGuid().ToString("N"));
    private ApiServiceCredentialStore _credentials = null!;
    private TransientAuthenticatedApiContextStore _attended = null!;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        _server = new LoopbackHttpTestServer(Handle);
        _server.Start();
        _credentials = new ApiServiceCredentialStore(Path.Combine(_dir, "credentials"));
        _attended = new TransientAuthenticatedApiContextStore();
        var authored = new AuthoredTestDataStore(Path.Combine(_dir, "td"));
        var registry = new TestDataRegistryStore(Path.Combine(_dir, "td"), NullLogger<TestDataRegistryStore>.Instance);
        var services = new ServiceCollection();
        var db = $"auth-{Guid.NewGuid():N}";
        services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(db));
        services.AddLogging();
        services.AddScoped<ExecutionEvidenceStore>();
        services.AddTransient<EvidenceCaptureHandler>();
        services.AddHttpClient(ExecutionRunService.ClientName).ConfigurePrimaryHttpMessageHandler(ExecutionRunService.CreatePrimaryHandler).AddHttpMessageHandler<EvidenceCaptureHandler>();
        services.AddHttpClient(ExecutionCredentialResolver.TokenClientName).ConfigurePrimaryHttpMessageHandler(ContractSourcePolicy.CreateHandler);
        services.AddSingleton<IApiEnvironmentSafetyPolicy>(ApiEnvironmentSafetyPolicy.Default);
        services.AddSingleton(_credentials);
        services.AddSingleton(_attended);
        services.AddSingleton<ExecutionCredentialResolver>();
        services.AddSingleton<ExecutionRunService>();
        services.AddSingleton<IContractSourceFetcher>(new ContractSourceFetcher(new HttpClient(ContractSourcePolicy.CreateHandler())));
        services.AddSingleton(authored);
        services.AddSingleton(new TestDataCatalog([new AuthoredTestDataProvider(authored)], null, registry));
        services.AddScoped<ContractRegistryService>();
        services.AddScoped<ContractTestService>();
        services.AddScoped<IExecutionContractValidator, ContractCallValidator>();
        _services = services.BuildServiceProvider();
        ExecutionCredentialResolver.Forget("local-synthetic");
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _server.StopAsync();
        await _services.DisposeAsync();
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private async Task Handle(HttpListenerContext c)
    {
        string body;
        using (var reader = new StreamReader(c.Request.InputStream, Encoding.UTF8)) body = await reader.ReadToEndAsync();
        var path = c.Request.Url!.AbsolutePath;
        _seen.Enqueue((c.Request.HttpMethod, path, c.Request.Headers["Authorization"], c.Request.Headers["X-Synthetic-Key"], body));
        async Task Send(int status, string text)
        {
            c.Response.StatusCode = status; c.Response.ContentType = "application/json";
            var bytes = Encoding.UTF8.GetBytes(text);
            c.Response.ContentLength64 = bytes.Length;
            await c.Response.OutputStream.WriteAsync(bytes);
            c.Response.Close();
        }
        if (path == "/oauth/huge") { await Send(200, "{\"access_token\":\"" + new string('a', ExecutionCredentialResolver.MaxTokenResponseBytes) + "\"}"); return; }
        if (path == "/oauth/unsafe") { await Send(200, "{\"access_token\":\"abc\\r\\nX-Injected: 1\",\"expires_in\":60}"); return; }
        if (path == "/oauth/redirect") { c.Response.StatusCode = 302; c.Response.RedirectLocation = _server.Url("/oauth/token"); c.Response.Close(); return; }
        if (path == "/oauth/token")
        {
            Interlocked.Increment(ref _tokenRequests);
            var form = System.Web.HttpUtility.ParseQueryString(body);
            if (form["grant_type"] == "client_credentials" && form["client_id"] == "synthetic-client" && form["client_secret"] == ClientSecret)
                await Send(200, $$"""{"access_token":"{{AccessToken}}","token_type":"Bearer","expires_in":120}""");
            else await Send(401, """{"error":"invalid_client"}""");
            return;
        }
        if (path.StartsWith("/secure/", StringComparison.Ordinal))
            await (c.Request.Headers["Authorization"] == $"Bearer {AccessToken}" ? Send(200, """{"id":7,"name":"Testbarn"}""") : Send(401, """{"error":"unauthorized"}"""));
        else if (path.StartsWith("/key/", StringComparison.Ordinal))
            await (c.Request.Headers["X-Synthetic-Key"] == ApiKey ? Send(200, """{"id":7,"name":"Testbarn"}""") : Send(401, """{"error":"unauthorized"}"""));
        else await Send(404, "{}");
    }

    private ExecutionRunRequest Run(ExecutionAuthenticationKind kind, params string[] paths) => new()
    {
        Name = "Authenticated synthetic run", EnvironmentId = "local-synthetic", TargetUrl = _server.Url("/"), EnvironmentType = "Local", Authentication = kind,
        Calls = [.. paths.Select(p => new ExecutionCallDefinition { Name = p, Method = "GET", Path = p, Assertions = [new("status", AssertionKind.StatusCode, AssertionOperator.Equals, null, "200")] })],
    };

    private async Task<(ExecutionRunStartResult Start, ExecutionRunDetail? Detail, string Json)> RunAsync(ExecutionRunRequest request)
    {
        var start = await _services.GetRequiredService<ExecutionRunService>().StartAsync(request, "test", default);
        if (start.RunId is not { } id) return (start, null, "");
        for (var i = 0; i < 600 && ExecutionRunService.IsRunning(id); i++) await Task.Delay(50);
        using var scope = _services.CreateScope();
        var detail = await scope.ServiceProvider.GetRequiredService<ExecutionEvidenceStore>().GetAsync(id, new(Take: 200), default);
        return (start, detail, JsonSerializer.Serialize(detail, ExecutionEvidenceStore.Json));
    }

    private void SaveClientCredentials(string secret = ClientSecret) =>
        _credentials.Save(new() { EnvironmentId = "local-synthetic", Kind = ExecutionAuthenticationKind.ClientCredentials, TokenUrl = _server.Url("/oauth/token"), ClientId = "synthetic-client", Secret = secret },
            DateTimeOffset.UtcNow).Error.Should().BeNull();

    [Fact]
    public async Task Unattended_client_credentials_authenticate_the_run_and_never_reach_the_evidence()
    {
        SaveClientCredentials();
        var (start, detail, json) = await RunAsync(Run(ExecutionAuthenticationKind.ClientCredentials, "/secure/children/7", "/secure/children/7"));
        start.Blockers.Should().BeEmpty();
        detail!.Calls.Should().OnlyContain(c => c.State == ExecutionState.Passed, string.Join("; ", detail.Calls.Select(c => $"{c.State} {c.Reason}")));
        _seen.Where(s => s.Path.StartsWith("/secure/", StringComparison.Ordinal)).Should().HaveCount(2).And.OnlyContain(s => s.Authorization == $"Bearer {AccessToken}");
        _tokenRequests.Should().Be(1, "the token is cached in memory until shortly before it expires");
        detail.Run.AuthenticationMode.Should().Be("Unattended: OAuth2 client credentials");
        detail.Run.AuthenticationReference.Should().Be("api-credential:local-synthetic:ClientCredentials:v1");
        detail.Calls[0].Attempts[0].Request!.Headers.Should().ContainSingle(h => h.Name == "Authorization").Which.Should().Match<CapturedHeader>(h => h.Redacted && h.Value == ExecutionEvidenceRedaction.Mask);
        json.Should().NotContain(AccessToken).And.NotContain(ClientSecret);
        File.ReadAllText(Directory.GetFiles(Path.Combine(_dir, "credentials"), "*.json").Single()).Should().NotContain(ClientSecret, "the configuration file holds no secret");
        File.ReadAllText(Directory.GetFiles(Path.Combine(_dir, "credentials"), "secret-*.bin").Single()).Should().NotContain(ClientSecret, "the secret is encrypted at rest");
        _credentials.Get("local-synthetic")!.SecretStored.Should().BeTrue();
    }

    [Fact]
    public async Task An_API_key_header_of_any_name_is_applied_and_recorded_only_as_redacted()
    {
        _credentials.Save(new() { EnvironmentId = "local-synthetic", Kind = ExecutionAuthenticationKind.ApiKey, HeaderName = "X-Synthetic-Key", Secret = ApiKey }, DateTimeOffset.UtcNow).Error.Should().BeNull();
        var (_, detail, json) = await RunAsync(Run(ExecutionAuthenticationKind.ApiKey, "/key/children/7"));
        detail!.Calls.Single().State.Should().Be(ExecutionState.Passed);
        _seen.Should().ContainSingle(s => s.Path == "/key/children/7" && s.Key == ApiKey && s.Authorization == null);
        detail.Calls[0].Attempts[0].Request!.Headers.Should().ContainSingle(h => h.Name == "X-Synthetic-Key").Which.Redacted.Should().BeTrue();
        json.Should().NotContain(ApiKey);
    }

    [Fact]
    public async Task A_missing_authentication_negative_runs_in_the_same_run_without_the_credential()
    {
        SaveClientCredentials();
        var request = Run(ExecutionAuthenticationKind.ClientCredentials, "/secure/children/7", "/secure/children/8");
        request = request with
        {
            Calls = [request.Calls[0], request.Calls[1] with { WithoutAuthentication = true, Assertions = [new("status", AssertionKind.StatusCode, AssertionOperator.Equals, null, "401")] }],
        };
        var (start, detail, json) = await RunAsync(request);
        start.Blockers.Should().BeEmpty();
        detail!.Calls.Should().OnlyContain(c => c.State == ExecutionState.Passed);
        _seen.Should().ContainSingle(s => s.Path == "/secure/children/7" && s.Authorization == $"Bearer {AccessToken}");
        _seen.Should().ContainSingle(s => s.Path == "/secure/children/8" && s.Authorization == null, "the flagged call never carries the run's credential");
        detail.Calls[1].Attempts[0].Request!.Headers.Should().NotContain(h => h.Name == "Authorization");
        json.Should().NotContain(AccessToken);
    }

    [Fact]
    public async Task A_refused_token_request_blocks_the_call_without_sending_it()
    {
        SaveClientCredentials("wrong-secret-value-000");
        var (_, detail, _) = await RunAsync(Run(ExecutionAuthenticationKind.ClientCredentials, "/secure/children/7"));
        var call = detail!.Calls.Single();
        call.State.Should().Be(ExecutionState.Blocked);
        call.Attempts.Single().Transport.Should().Be(TransportResult.NotSent);
        call.Attempts.Single().Error.Should().Contain("refused the client credentials (HTTP 401)");
        _seen.Should().NotContain(s => s.Path.StartsWith("/secure/", StringComparison.Ordinal), "nothing is sent without the credential");
    }

    [Fact]
    public async Task Attended_and_unattended_credentials_are_never_substituted_for_each_other()
    {
        // An attended session exists for the profile, but the run asks for the unattended credential, which is not configured: Blocked, never the session.
        ((ITransientCredentialSink)_attended).Store("local-synthetic", "fp-1", "api.example.test", ApprovedHostSet.Create("https://api.example.test/", null), AccessToken, DateTimeOffset.UtcNow.AddMinutes(10), "opaque");
        var (start, _, _) = await RunAsync(Run(ExecutionAuthenticationKind.ClientCredentials, "/secure/children/7") with { ProfileId = "local-synthetic", ContextFingerprint = "fp-1" });
        start.State.Should().Be(ExecutionState.Blocked);
        start.Blockers.Should().ContainSingle(b => b.Contains("No unattended API credential"));
        // An unattended credential exists, but the run asks for the attended session over plain HTTP: refused (attended is HTTPS + approved hosts only).
        SaveClientCredentials();
        var (attended, _, _) = await RunAsync(Run(ExecutionAuthenticationKind.AttendedSession, "/secure/children/7") with { ProfileId = "local-synthetic", ContextFingerprint = "fp-1" });
        attended.State.Should().Be(ExecutionState.Blocked);
        attended.Blockers.Should().ContainSingle(b => b.Contains("only HTTPS hosts approved"));
        _seen.Should().NotContain(s => s.Path.StartsWith("/secure/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_attended_session_applies_only_to_its_own_profile_and_approved_HTTPS_hosts()
    {
        var resolver = _services.GetRequiredService<ExecutionCredentialResolver>();
        ((ITransientCredentialSink)_attended).Store("dev", "fp-dev", "api.example.test", ApprovedHostSet.Create("https://api.example.test/", null), AccessToken, DateTimeOffset.UtcNow.AddMinutes(10), "opaque");
        var request = new ExecutionRunRequest { EnvironmentId = "dev", EnvironmentType = "Development", ProfileId = "dev", ContextFingerprint = "fp-dev", Authentication = ExecutionAuthenticationKind.AttendedSession };
        var origin = new Uri("https://api.example.test/");
        resolver.Prepare(request, origin).Problem.Should().BeNull();
        using var message = new HttpRequestMessage(HttpMethod.Get, "https://api.example.test/children/7");
        var (headers, problem) = await resolver.ApplyAsync(request, message, default);
        problem.Should().BeNull(); headers.Should().Equal("Authorization");
        message.Headers.Authorization!.Parameter.Should().Be(AccessToken);
        resolver.Prepare(request with { ProfileId = "other" }, origin).Problem.Should().Contain("another Target Environment");
        resolver.Prepare(request with { ContextFingerprint = "stale" }, origin).Problem.Should().Contain("No attended session");
        resolver.Prepare(request, new Uri("https://evil.example.test/")).Problem.Should().Contain("only HTTPS hosts approved");
        resolver.Prepare(request with { ContextFingerprint = null }, origin).Problem.Should().Contain("proxy identity");
    }

    [Theory]
    [InlineData("/oauth/huge", "larger than 64 KB")]
    [InlineData("/oauth/unsafe", "not a usable bearer token")]
    [InlineData("/oauth/redirect", "HTTP 302")]
    public async Task An_oversized_unsafe_or_redirected_token_answer_is_never_used(string tokenPath, string reason)
    {
        _credentials.Save(new() { EnvironmentId = "local-synthetic", Kind = ExecutionAuthenticationKind.ClientCredentials, TokenUrl = _server.Url(tokenPath), ClientId = "synthetic-client", Secret = ClientSecret },
            DateTimeOffset.UtcNow).Error.Should().BeNull();
        ExecutionCredentialResolver.Forget("local-synthetic");
        var (_, detail, _) = await RunAsync(Run(ExecutionAuthenticationKind.ClientCredentials, "/secure/children/7"));
        var call = detail!.Calls.Single();
        call.State.Should().Be(ExecutionState.Blocked);
        call.Attempts.Single().Error.Should().Contain(reason);
        _seen.Should().NotContain(s => s.Path.StartsWith("/secure/", StringComparison.Ordinal));
        _seen.Should().NotContain(s => s.Path == "/oauth/token", "a redirect from the token endpoint is not followed");
    }

    [Theory]
    [InlineData("http://169.254.169.254/token", "forbidden address")]
    [InlineData("http://token.example.test/token", "plain HTTP is allowed only for a loopback address")]
    [InlineData("ftp://token.example.test/token", "not allowed")]
    public void An_unsafe_token_endpoint_is_refused_when_saved(string url, string reason) =>
        _credentials.Save(new() { EnvironmentId = "local-synthetic", Kind = ExecutionAuthenticationKind.ClientCredentials, TokenUrl = url, ClientId = "c", Secret = "s-123456" }, DateTimeOffset.UtcNow)
            .Error.Should().Contain(reason);

    [Theory]
    [InlineData("Authorization")]
    [InlineData("Cookie")]
    [InlineData("X Bad Header")]
    public void An_API_key_cannot_use_a_reserved_or_malformed_header(string header) =>
        _credentials.Save(new() { EnvironmentId = "local-synthetic", Kind = ExecutionAuthenticationKind.ApiKey, HeaderName = header, Secret = ApiKey }, DateTimeOffset.UtcNow).Error.Should().NotBeNull();

    [Fact]
    public void Unattended_credentials_need_HTTPS_unless_the_target_is_loopback_in_a_Local_environment()
    {
        SaveClientCredentials();
        var resolver = _services.GetRequiredService<ExecutionCredentialResolver>();
        var request = new ExecutionRunRequest { EnvironmentId = "local-synthetic", EnvironmentType = "Development", Authentication = ExecutionAuthenticationKind.ClientCredentials };
        resolver.Prepare(request, new Uri("http://api.example.test/")).Problem.Should().Contain("only over HTTPS");
        resolver.Prepare(request, new Uri("http://localhost:5288/")).Problem.Should().Contain("only over HTTPS", "loopback HTTP needs a Local environment");
        resolver.Prepare(request with { EnvironmentType = "Local" }, new Uri("http://localhost:5288/")).Problem.Should().BeNull();
        resolver.Prepare(request, new Uri("https://api.example.test/")).Problem.Should().BeNull();
        resolver.Prepare(request with { Authentication = ExecutionAuthenticationKind.ApiKey }, new Uri("https://api.example.test/")).Problem.Should().Contain("not Unattended: API key");
    }

    [Fact]
    public async Task Readiness_checks_the_operations_actual_security_requirement_against_the_bound_authentication()
    {
        using var scope = _services.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<ContractRegistryService>();
        var tests = scope.ServiceProvider.GetRequiredService<ContractTestService>();
        (await registry.ImportAsync(new() { FileName = "children.yaml", Content = ContractTestingTests.V1Yaml }, default)).Imported.Should().BeTrue();
        var (created, _) = await tests.GenerateAsync("synthetic-children-api", null, ["GET /admin/reports"], default);
        var env = new ContractTestEnvironment { EnvironmentId = "local-synthetic", TargetUrl = _server.Url("/"), EnvironmentType = "Local" };
        string Auth() => tests.GetAsync(created[0].Id, env, default).Result!.Readiness.Items.Single(i => i.Area == "Authentication").Text;
        Auth().Should().Contain("requires bearer (bearer)").And.Contain("never sent anonymously");
        (await tests.SaveBindingAsync("synthetic-children-api", new("local-synthetic", "/api", null, ExecutionAuthenticationKind.ApiKey), default)).Error.Should().BeNull();
        Auth().Should().Contain("does not provide that");
        (await tests.SaveBindingAsync("synthetic-children-api", new("local-synthetic", "/api", null, ExecutionAuthenticationKind.ClientCredentials), default)).Error.Should().BeNull();
        Auth().Should().Contain("No unattended API credential is configured");
        SaveClientCredentials();
        Auth().Should().Contain("Unattended: OAuth2 client credentials (api-credential:local-synthetic:ClientCredentials:v1)");
        (await tests.GetAsync(created[0].Id, env, default))!.Readiness.State.Should().Be(ContractTestReadinessState.Ready);
        (await tests.SaveBindingAsync("synthetic-children-api", new("local-synthetic", "//evil.example.test", null, ExecutionAuthenticationKind.None), default)).Error.Should().Contain("path inside");
    }

    [Fact]
    public void Declared_servers_become_base_path_proposals_only()
    {
        var doc = new BirkNext.SourceDomains.SourceContract { Servers = ["https://api.example.test/v1", "/internal/api/", "https://other.example.test"] };
        ContractTestService.ServerProposals(doc).Select(p => p.BasePath).Should().Equal("/v1", "/internal/api", "");
    }
}
