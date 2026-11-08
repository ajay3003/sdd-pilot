using System.Net;
using BirkNext.Api.Services.ApiQuality;
using BirkNext.Api.Services.ContractAnalysis;
using BirkNext.ApiReview;
using BirkNext.LocalHttpsProxy;
using Microsoft.Extensions.Logging.Abstractions;

namespace BirkNext.Api.Tests.Services.ApiQuality;

/// <summary>
/// CI-DEF-3 (contract import audit 2026-11-08): every contract-URL fetch goes through one policy. AQR used to GET any configured contract
/// URL, including a cloud metadata address; the unused fetchers blocked by host-name prefix (so "fd..."/"fc..." host names were refused).
/// </summary>
public sealed partial class ApiReviewEngineTests
{
    [Theory]
    [InlineData("http://169.254.169.254/latest/meta-data/openapi.json")]
    [InlineData("http://[::ffff:169.254.169.254]/openapi.json")]
    [InlineData("http://[fe80::1]/openapi.json")]
    [InlineData("http://0.0.0.0/openapi.json")]
    [InlineData("http://224.0.0.1/openapi.json")]
    [InlineData("http://metadata.google.internal/openapi.json")]
    [InlineData("http://user:secret@api-dev.example.test/openapi.json")]
    [InlineData("file:///c:/openapi.json")]
    [InlineData("ftp://api-dev.example.test/openapi.json")]
    [InlineData("/relative/openapi.json")]
    public void ContractSourcePolicy_refuses(string url) => Assert.Null(ContractSourcePolicy.Validate(url).Uri);

    [Theory]
    [InlineData("http://localhost:5288/openapi.json")]
    [InlineData("http://127.0.0.1:5288/openapi.json")]
    [InlineData("https://api-dev.example.test/swagger/v1/swagger.json")]
    [InlineData("https://fdic-api.example.test/openapi.json")]
    [InlineData("https://fc-api.example.test/openapi.json")]
    [InlineData("http://10.1.2.3/openapi.json")]
    public void ContractSourcePolicy_allows(string url) => Assert.NotNull(ContractSourcePolicy.Validate(url).Uri);

    [Fact]
    public async Task CI_DEF_3_AQR_never_requests_a_contract_url_the_policy_refuses()
    {
        var fixture = new Fixture { Respond = (req, _) => Json(HttpStatusCode.OK, "{\"items\":[]}") };
        var report = await Engine(fixture).RunAsync(Request(AuthenticatedTestingMethod.ManagedEdgeCdp, false, Rest(contract: "http://169.254.169.254/latest/meta-data/openapi.json")));

        Assert.DoesNotContain(fixture.Requests, r => r.RequestUri!.Host == "169.254.169.254");
        var contract = Assert.Single(report.Targets).Contract!;
        Assert.False(contract.Available);
        Assert.Equal(ApiReviewCheckResult.Blocked, contract.Status);
        Assert.Contains("forbidden address", contract.Note);
    }

    [Fact]
    public async Task CI_DEF_3_AQR_follows_a_same_host_redirect_but_refuses_a_redirect_to_a_metadata_address()
    {
        const string doc = """{ "openapi": "3.0.3", "info": { "title": "t", "version": "1" }, "paths": { "/api/children": { "get": { "responses": { "200": { "description": "ok" } } } } } }""";
        var fixture = new Fixture { Respond = (req, _) => req.RequestUri!.AbsolutePath switch
        {
            "/swagger.json" => Redirect("/v2/swagger.json"),
            "/v2/swagger.json" => Json(HttpStatusCode.OK, doc),
            "/evil.json" => Redirect("http://169.254.169.254/latest/meta-data/"),
            _ => Json(HttpStatusCode.OK, "{\"items\":[]}"),
        } };

        var followed = await Engine(fixture).RunAsync(Request(AuthenticatedTestingMethod.ManagedEdgeCdp, false, Rest(contract: "https://api-dev.example.test/swagger.json")));
        Assert.True(Assert.Single(followed.Targets).Contract!.Available);

        var refused = await Engine(fixture).RunAsync(Request(AuthenticatedTestingMethod.ManagedEdgeCdp, false, Rest(contract: "https://api-dev.example.test/evil.json")));
        Assert.DoesNotContain(fixture.Requests, r => r.RequestUri!.Host == "169.254.169.254");
        var contract = Assert.Single(refused.Targets).Contract!;
        Assert.Equal(ApiReviewCheckResult.Blocked, contract.Status);
        Assert.Contains("Redirect refused", contract.Note);

        static HttpResponseMessage Redirect(string location)
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
            return response;
        }
    }

    [Fact]
    public async Task CI_DEF_3_the_discovery_fetcher_uses_the_same_policy_not_a_host_name_prefix()
    {
        var fixture = new Fixture { Respond = (_, _) => Json(HttpStatusCode.OK, """{ "openapi": "3.0.3", "info": { "title": "t", "version": "1" }, "paths": {} }""") };
        var fetcher = new OpenApiSourceFetcher(new HttpClient(fixture), NullLogger<OpenApiSourceFetcher>.Instance);

        Assert.True((await fetcher.FetchAsync("https://fdic-api.example.test/openapi.json")).IsSuccess);
        var metadata = await fetcher.FetchAsync("http://169.254.169.254/openapi.json");
        Assert.Equal(OpenApiSourceFailureReason.BlockedByPolicy, metadata.FailureReason);
        Assert.DoesNotContain(fixture.Requests, r => r.RequestUri!.Host == "169.254.169.254");
    }

    [Fact]
    public async Task CI_DEF_3_the_connect_callback_lets_an_allowed_host_through()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serve = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            var buffer = new byte[4096];
            _ = await stream.ReadAsync(buffer);
            var body = "openapi: 3.0.3"u8.ToArray();
            await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/yaml\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"));
            await stream.WriteAsync(body);
        });
        using var http = new HttpClient(ContractSourcePolicy.CreateHandler());
        var result = await new ContractSourceFetcher(http).FetchAsync($"http://127.0.0.1:{port}/openapi.yaml", CancellationToken.None);
        await serve;

        Assert.Equal(ContractFetchStatus.Fetched, result.Status);
        Assert.Equal("openapi: 3.0.3", result.Text);
        Assert.Equal(64, result.Sha256!.Length);
    }
}
