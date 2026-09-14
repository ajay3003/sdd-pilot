using System.Net;
using System.Text;
using System.Text.Json;
using BirkNext.Api.Services.LocalHttpsProxy;
using BirkNext.LocalHttpsProxy;
using Microsoft.Extensions.Options;
using Moq;

namespace BirkNext.Api.Tests.Services.LocalHttpsProxy;

/// <summary>REST safety (U, V), GraphQL safety (W, X), credential applied internally and never returned.</summary>
public sealed class AuthenticatedApiExecutionServiceTests
{
    private const string Fp = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private static readonly string Token = BearerTokenInspector.BuildUnsignedJwt(new { exp = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds() });
    private readonly ApprovedHostSet _scope = ApprovedHostSet.Create("https://app.example.test/", ["api.example.test", "graphql.example.test"]);
    private readonly TransientAuthenticatedApiContextStore _store = new();
    private readonly Mock<ILocalHttpsProxySessionAccess> _sessions = new(MockBehavior.Strict);
    private readonly RecordingHandler _handler = new();
    private readonly LocalHttpsProxySessionRequest _session = new("s1", "dev", Fp);

    public AuthenticatedApiExecutionServiceTests()
    {
        _sessions.Setup(s => s.GetScope(_session)).Returns(_scope);
        _sessions.Setup(s => s.GetScope(It.Is<LocalHttpsProxySessionRequest>(r => r.SessionId != "s1"))).Throws(new KeyNotFoundException());
        ((ITransientCredentialSink)_store).Store("dev", Fp, "api.example.test", _scope, Token, DateTimeOffset.UtcNow.AddMinutes(30), "JWT");
    }

    private AuthenticatedApiExecutionService Service() => new(_sessions.Object, _store, Options.Create(new LocalHttpsProxyOptions()), _handler);
    private AuthenticatedRestRequest Rest(string method, string url) => new("s1", "dev", Fp, method, url);
    private AuthenticatedGraphQlRequest GraphQl(string url, string query) => new("s1", "dev", Fp, url, query);

    [Fact]
    public async Task ApprovedRestGetCarriesTheCredentialInternallyAndReturnsSanitizedResult()
    {
        _handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"user\":\"x\"}", Encoding.UTF8, "application/json") };
        using var service = Service();
        var result = await service.ExecuteRestAsync(Rest("GET", "https://api.example.test/v1/me"));
        Assert.Equal(200, result.StatusCode);
        Assert.True(result.Succeeded);
        Assert.Equal("application/json", result.ContentType);
        Assert.Equal(12, result.ContentLength);
        Assert.Contains("response body not captured", result.Outcome);
        Assert.Equal("Bearer", _handler.LastRequest!.Headers.Authorization!.Scheme);
        Assert.Equal(Token, _handler.LastRequest.Headers.Authorization.Parameter);
        var json = JsonSerializer.Serialize(result);
        Assert.DoesNotContain(Token, json);
        Assert.DoesNotContain("eyJ", json);
        Assert.DoesNotContain("user", json);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    [InlineData("")]
    public async Task UnsafeRestMethodsAreRejectedBeforeAnyRequest(string method)
    {
        using var service = Service();
        await Assert.ThrowsAsync<ArgumentException>(() => service.ExecuteRestAsync(Rest(method, "https://api.example.test/v1/me")));
        Assert.Null(_handler.LastRequest);
    }

    [Theory]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    [InlineData("get")]
    public async Task SafeMethodsAreAllowed(string method)
    {
        _handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.NoContent);
        using var service = Service();
        var result = await service.ExecuteRestAsync(Rest(method, "https://api.example.test/v1/me"));
        Assert.Equal(204, result.StatusCode);
        Assert.Equal(method.ToUpperInvariant(), _handler.LastRequest!.Method.Method);
    }

    [Theory]
    [InlineData("https://evil.example.test/v1/me")]
    [InlineData("https://app.example.test.evil.test/")]
    [InlineData("http://api.example.test/v1/me")]
    [InlineData("https://api.example.test:8443/v1/me")]
    [InlineData("https://user:pw@api.example.test/v1/me")]
    [InlineData("not-a-url")]
    public async Task CrossHostAndNonHttpsExecutionIsRejected(string url)
    {
        using var service = Service();
        await Assert.ThrowsAsync<ArgumentException>(() => service.ExecuteRestAsync(Rest("GET", url)));
        Assert.Null(_handler.LastRequest);
    }

    [Fact]
    public async Task ExecutionWithoutAuthenticatedContextIsRefused()
    {
        _store.Invalidate("dev");
        using var service = Service();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExecuteRestAsync(Rest("GET", "https://api.example.test/v1/me")));
        Assert.Null(_handler.LastRequest);
    }

    [Fact]
    public async Task ForeignSessionIsRejected()
    {
        using var service = Service();
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.ExecuteRestAsync(new("other", "dev", Fp, "GET", "https://api.example.test/v1/me")));
    }

    [Fact]
    public async Task GraphQlQueryIsAcceptedAndSemanticallySummarized()
    {
        _handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"data\":{\"__typename\":\"Query\"}}", Encoding.UTF8, "application/json") };
        using var service = Service();
        var result = await service.ExecuteGraphQlQueryAsync(GraphQl("https://graphql.example.test/graphql", "query { __typename }"));
        Assert.Equal(200, result.StatusCode);
        Assert.Equal(0, result.GraphQlErrorCount);
        Assert.True(result.GraphQlHasData);
        Assert.Contains("without errors", result.Outcome);
        Assert.Equal(HttpMethod.Post, _handler.LastRequest!.Method);
        Assert.Equal(Token, _handler.LastRequest.Headers.Authorization!.Parameter);
        Assert.Contains("\"query\":\"query { __typename }\"", _handler.LastBody);
        Assert.DoesNotContain("Query", JsonSerializer.Serialize(result).Replace("GraphQl", ""));
    }

    [Fact]
    public async Task GraphQlErrorsAreReportedNotHidden()
    {
        _handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"errors\":[{\"message\":\"denied\"}],\"data\":null}", Encoding.UTF8, "application/json") };
        using var service = Service();
        var result = await service.ExecuteGraphQlQueryAsync(GraphQl("https://graphql.example.test/graphql", "query { __typename }"));
        Assert.Equal(1, result.GraphQlErrorCount);
        Assert.False(result.GraphQlHasData);
        Assert.Contains("1 error(s) and no data", result.Outcome);
        Assert.DoesNotContain("denied", JsonSerializer.Serialize(result));
    }

    [Theory]
    [InlineData("mutation { deleteUser(id: 1) }")]
    [InlineData("subscription { events }")]
    [InlineData("query { a } mutation { b }")]
    [InlineData("query($id: ID) { user(id: $id) }")]
    [InlineData("not graphql")]
    [InlineData("")]
    public async Task GraphQlMutationsSubscriptionsAndInvalidDocumentsAreRejected(string query)
    {
        using var service = Service();
        await Assert.ThrowsAsync<ArgumentException>(() => service.ExecuteGraphQlQueryAsync(GraphQl("https://graphql.example.test/graphql", query)));
        Assert.Null(_handler.LastRequest);
    }

    [Fact]
    public async Task GraphQlCrossHostIsRejected()
    {
        using var service = Service();
        await Assert.ThrowsAsync<ArgumentException>(() => service.ExecuteGraphQlQueryAsync(GraphQl("https://other.example.test/graphql", "query { __typename }")));
        Assert.Null(_handler.LastRequest);
    }

    [Fact]
    public async Task AccessDeniedIsReportedAsAuthenticationRejected()
    {
        _handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized);
        using var service = Service();
        var result = await service.ExecuteRestAsync(Rest("GET", "https://api.example.test/v1/me"));
        Assert.True(result.AuthenticationRejected);
        Assert.Contains("Access denied", result.Outcome);
    }

    // ── profile-keyed execution for reviews (no runtime session id; scope resolved from the store) ──

    [Fact]
    public async Task ProfileKeyedRestResolvesScopeFromStoreAndAppliesCredentialInternally()
    {
        _handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        using var service = Service();
        var result = await service.ExecuteRestForProfileAsync("dev", Fp, "GET", "https://api.example.test/health");
        Assert.Equal(200, result.StatusCode);
        Assert.Equal("Bearer", _handler.LastRequest!.Headers.Authorization!.Scheme);
        Assert.Equal(Token, _handler.LastRequest.Headers.Authorization.Parameter);
        Assert.DoesNotContain(Token, JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task ProfileKeyedRestWithoutContextThrowsNoContext()
    {
        _store.Invalidate("dev");
        using var service = Service();
        var ex = await Assert.ThrowsAsync<AuthenticatedContextUnavailableException>(() => service.ExecuteRestForProfileAsync("dev", Fp, "GET", "https://api.example.test/health"));
        Assert.Equal(AuthenticatedExecutionStatus.NoContext, ex.Status);
        Assert.Null(_handler.LastRequest);
    }

    [Fact]
    public async Task ProfileKeyedRestOutOfScopeHostThrowsOutOfScope()
    {
        using var service = Service();
        var ex = await Assert.ThrowsAsync<AuthenticatedContextUnavailableException>(() => service.ExecuteRestForProfileAsync("dev", Fp, "GET", "https://evil.example.test/x"));
        Assert.Equal(AuthenticatedExecutionStatus.OutOfScope, ex.Status);
        Assert.Null(_handler.LastRequest);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("DELETE")]
    public async Task ProfileKeyedRestUnsafeMethodRejected(string method)
    {
        using var service = Service();
        await Assert.ThrowsAsync<ArgumentException>(() => service.ExecuteRestForProfileAsync("dev", Fp, method, "https://api.example.test/x"));
        Assert.Null(_handler.LastRequest);
    }

    [Fact]
    public async Task ProfileKeyedGraphQlQueryAcceptedButMutationRejected()
    {
        _handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"data\":{}}", Encoding.UTF8, "application/json") };
        using var service = Service();
        var ok = await service.ExecuteGraphQlQueryForProfileAsync("dev", Fp, "https://graphql.example.test/graphql", "query { __typename }");
        Assert.Equal(200, ok.StatusCode);
        await Assert.ThrowsAsync<ArgumentException>(() => service.ExecuteGraphQlQueryForProfileAsync("dev", Fp, "https://graphql.example.test/graphql", "mutation { deleteUser(id: 1) }"));
    }

    [Fact]
    public async Task ProfileKeyedExpiredContextThrowsExpiredAndNeverSends()
    {
        var clockStore = new TransientAuthenticatedApiContextStore(() => DateTimeOffset.UtcNow);
        ((ITransientCredentialSink)clockStore).Store("dev", Fp, "api.example.test", _scope, Token, DateTimeOffset.UtcNow.AddMilliseconds(-1), "JWT");
        using var service = new AuthenticatedApiExecutionService(_sessions.Object, clockStore, Options.Create(new LocalHttpsProxyOptions()), _handler);
        var ex = await Assert.ThrowsAsync<AuthenticatedContextUnavailableException>(() => service.ExecuteRestForProfileAsync("dev", Fp, "GET", "https://api.example.test/health"));
        Assert.Contains(ex.Status, new[] { AuthenticatedExecutionStatus.NoContext, AuthenticatedExecutionStatus.Expired });
        Assert.Null(_handler.LastRequest);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.OK);
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return Respond(request);
        }
    }
}
