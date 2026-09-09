using System.Net;
using System.Text;
using BirkNext.Api.Models;
using BirkNext.Api.Services.FrontendBrowserRuntime;
using BirkNext.Api.Services.TargetEnvironmentDetection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BirkNext.Api.Tests.Services.TargetEnvironmentDetection;

public sealed class EndpointProvenanceFrameworkTests
{
    private const string Url = "https://application-dev.example.org/";
    private const string Shell = "<html><script src=\"_framework/blazor.webassembly.js\"></script></html>";
    private const string Config = """{"AzureAd":{"Authority":"https://login.microsoftonline.com/11111111-1111-1111-1111-111111111111/v2.0","ClientId":"public-client"},"ApiBaseUrl":"https://application-dev.example.org/api/"}""";

    private static TargetEnvironmentDetectionService Service(HttpMessageHandler handler, ITargetHostResolver? resolver = null)
    {
        var dns = new FakeDnsResolver();
        dns.Add("application-dev.example.org", "203.0.113.1");
        return new(new BrowserTargetValidator(), new HttpClient(handler), resolver ?? dns,
            new ClientFrameworkDetector(), NullLogger<TargetEnvironmentDetectionService>.Instance);
    }

    [Theory]
    [InlineData("<script src=\"_framework/blazor.webassembly.js\"></script>", true)]
    [InlineData("<html><div id=\"app\"></div><script src=\"app.js\"></script></html>", false)]
    [InlineData("<script src=\"_framework/blazor.server.js\"></script>", false)]
    [InlineData("<script src=\"_framework/dotnet.js\"></script>", false)]
    public void Framework_RequiresSpecificPositiveEvidence(string html, bool detected)
    {
        var framework = new ClientFrameworkDetector().DetectFramework(html, "TEXT/HTML; charset=utf-8");
        Assert.Equal(detected ? ClientFrameworkType.BlazorWebAssembly : (ClientFrameworkType?)null, framework);
    }

    [Fact]
    public async Task BlazorAndMsal_Coexist_EndpointStagesCannotEraseFramework()
    {
        var handler = new FixtureHandler(Shell, Config);
        var result = await Service(handler).DetectFromUrlAsync(Url);
        Assert.Equal(ClientFrameworkType.BlazorWebAssembly, result.DetectedClientFramework);
        Assert.Equal(FrontendAuthenticationType.MicrosoftEntraId, result.DetectedAuthenticationType);
        Assert.Contains("_framework/blazor.webassembly.js", result.FrameworkEvidence);
        Assert.Equal(DetectionConfidence.High, result.FrameworkConfidence);
        Assert.Contains(handler.Requests, p => p == "/graphql");
        Assert.Contains(handler.Requests, p => p == "/health");
        var rest = Assert.Single(result.DiscoveryEvidence.Where(e => e.TargetField == "RestBaseUrl"));
        Assert.Equal(EndpointEvidenceStatus.Observed, rest.Status);
        Assert.Equal(DetectionConfidence.VeryHigh, rest.Confidence);
        Assert.Equal(EndpointProbeStatus.NotPerformed, rest.ProbeStatus);
        Assert.Equal(DiscoveryEvidence.EvidenceType.StructuredConfig, rest.Type);
        Assert.DoesNotContain("/api/", handler.Requests);
    }

    [Fact]
    public async Task Html200_IsOnlyCandidate_ForEveryEndpoint()
    {
        var result = await Service(new FixtureHandler(Shell, "{}", Shell, "text/html")).DetectFromUrlAsync(Url);
        var endpoints = result.DiscoveryEvidence.Where(e => e.TargetField is "RestBaseUrl" or "GraphQlEndpoint" or "SwaggerUrl" or "HealthEndpoint").ToList();
        Assert.Equal(4, endpoints.Count);
        Assert.All(endpoints, e =>
        {
            Assert.Equal(EndpointEvidenceStatus.Candidate, e.Status);
            Assert.Equal(DetectionConfidence.Low, e.Confidence);
            Assert.Equal(DiscoveryEvidence.EvidenceType.ConventionalCandidate, e.Type);
            Assert.Equal(EndpointProbeStatus.ResponseReceived, e.ProbeStatus);
            Assert.Equal(200, e.HttpStatus);
            Assert.Equal("text/html", e.ContentType);
        });
    }

    [Theory]
    [InlineData("RestBaseUrl", "{\"items\":[]}", "application/json")]
    [InlineData("GraphQlEndpoint", "{\"errors\":[{\"message\":\"Query required\"}]}", "application/json")]
    [InlineData("SwaggerUrl", "{\"openapi\":\"3.0.0\",\"info\":{},\"paths\":{}}", "application/json")]
    [InlineData("HealthEndpoint", "Healthy", "text/plain")]
    [InlineData("HealthEndpoint", "{\"status\":\"Healthy\"}", "application/json")]
    public async Task ProtocolEvidence_ConfirmsOnlySupportedEndpoint(string field, string body, string contentType)
    {
        var result = await Service(new FixtureHandler(Shell, "{}", body, contentType)).DetectFromUrlAsync(Url);
        var evidence = Assert.Single(result.DiscoveryEvidence.Where(e => e.TargetField == field));
        Assert.Equal(EndpointEvidenceStatus.Confirmed, evidence.Status);
        Assert.Equal(DetectionConfidence.High, evidence.Confidence);
        Assert.Equal(DiscoveryEvidence.EvidenceType.SafeProbe, evidence.Type);
        if (field == "SwaggerUrl") Assert.Equal(OpenApiResourceKind.OpenApiDocument, evidence.OpenApiKind);
    }

    [Fact]
    public async Task SwaggerUi_IsObserved_NotAConfirmedDocument()
    {
        var result = await Service(new FixtureHandler(Shell, "{}", "<html><script>SwaggerUIBundle({url:'/schema.json'})</script></html>", "text/html")).DetectFromUrlAsync(Url);
        var evidence = Assert.Single(result.DiscoveryEvidence.Where(e => e.TargetField == "SwaggerUrl"));
        Assert.Equal(EndpointEvidenceStatus.Observed, evidence.Status);
        Assert.Equal(OpenApiResourceKind.SwaggerUi, evidence.OpenApiKind);
        Assert.Equal(DetectionConfidence.Medium, evidence.Confidence);
    }

    [Theory]
    [InlineData(404, EndpointProbeStatus.ResponseReceived)]
    [InlineData(302, EndpointProbeStatus.Blocked)]
    public async Task ProbeNonSuccess_PreservesCandidateAndStatus(int status, EndpointProbeStatus expected)
    {
        var handler = new FixtureHandler(Shell, "{}", "", "text/plain", status);
        var result = await Service(handler).DetectFromUrlAsync(Url);
        var evidence = Assert.Single(result.DiscoveryEvidence.Where(e => e.TargetField == "GraphQlEndpoint"));
        Assert.Equal(EndpointEvidenceStatus.Candidate, evidence.Status);
        Assert.Equal(expected, evidence.ProbeStatus);
        Assert.Equal(status, evidence.HttpStatus);
        Assert.DoesNotContain(handler.Requests, p => p.Contains("private"));
    }

    [Fact]
    public async Task OversizedProbe_IsBoundedAndUnconfirmed()
    {
        var result = await Service(new FixtureHandler(Shell, "{}", new string('x', 1_000_001), "application/json")).DetectFromUrlAsync(Url);
        var evidence = Assert.Single(result.DiscoveryEvidence.Where(e => e.TargetField == "GraphQlEndpoint"));
        Assert.Equal(EndpointProbeStatus.SizeLimitExceeded, evidence.ProbeStatus);
        Assert.Equal(EndpointEvidenceStatus.Candidate, evidence.Status);
    }

    [Fact]
    public async Task ProbeDnsChangesToPrivate_FailsClosed()
    {
        var handler = new FixtureHandler(Shell, "{}");
        var result = await Service(handler, new ChangingResolver()).DetectFromUrlAsync(Url);
        var evidence = Assert.Single(result.DiscoveryEvidence.Where(e => e.TargetField == "GraphQlEndpoint"));
        Assert.Equal(EndpointProbeStatus.Blocked, evidence.ProbeStatus);
        Assert.DoesNotContain("/graphql", handler.Requests);
    }

    private sealed class ChangingResolver : ITargetHostResolver
    {
        private int _calls;
        public Task<IReadOnlyList<IPAddress>> ResolveHostAsync(string hostname, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<IPAddress>>(new[] { IPAddress.Parse(++_calls <= 2 ? "203.0.113.1" : "10.0.0.1") });
    }

    private sealed class FixtureHandler(string shell, string config, string probeBody = "<html>SPA fallback</html>", string probeType = "text/html", int probeStatus = 200) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add(path);
            var initial = request.Method == HttpMethod.Head || path == "/";
            var isConfig = path == "/appsettings.json";
            var response = new HttpResponseMessage((HttpStatusCode)(initial || isConfig ? 200 : probeStatus))
            {
                Content = new StringContent(initial ? shell : isConfig ? config : probeBody, Encoding.UTF8, initial ? "text/html" : isConfig ? "application/json" : probeType)
            };
            if (probeStatus == 302 && !initial && !isConfig) response.Headers.Location = new Uri("https://private.example.org/");
            return Task.FromResult(response);
        }
    }
}
