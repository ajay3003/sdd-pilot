using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// The review preflight is executed by the backend (same network path as the HTTP engines), never as a browser-side fetch.
/// Every probe outcome maps to a precise status; the timeout wording appears only for a real probe timeout.
/// </summary>
public sealed class TargetPreflightServiceMappingTests
{
    private const string Target = "https://m2lbdev.example.test/";

    private static TargetReachabilityProbeDto Probe(TargetReachability reachability, int? status = null, string message = "", string? finalUrl = null, string? blockReason = null, int redirects = 0) =>
        new() { TargetUrl = Target, Reachability = reachability, StatusCode = status, Message = message, FinalUrl = finalUrl ?? Target, BlockReason = blockReason, RedirectCount = redirects, ElapsedMs = 12.5,
            AuthenticationRequired = reachability == TargetReachability.AuthenticationRequired };

    [Fact]
    public void Reachable200_IsReady()
    {
        var result = TargetPreflightService.Map(Probe(TargetReachability.Reachable, 200, "Target is reachable (HTTP 200)."), Target);

        result.Status.Should().Be(PreflightStatus.Ready);
        result.ResponseStatusCode.Should().Be(200);
        result.RedirectOccurred.Should().BeFalse();
        result.Message.Should().NotContain("timeout");
        result.ElapsedMs.Should().Be(12.5);
    }

    [Fact]
    public void Timeout_IsTheOnlyTimedOutStatusAndKeepsExactWording()
    {
        var result = TargetPreflightService.Map(Probe(TargetReachability.Timeout, null, "Target did not respond within timeout period."), Target);

        result.Status.Should().Be(PreflightStatus.TimedOut);
        result.Message.Should().Be(TargetPreflightService.TimeoutMessage);
        result.ResponseStatusCode.Should().BeNull();
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public void AuthGateStatus_IsAuthenticationRequiredWithStatusNotTimeout(int status)
    {
        var result = TargetPreflightService.Map(Probe(TargetReachability.AuthenticationRequired, status, $"Target returned HTTP {status}. The frontend host requires authentication."), Target);

        result.Status.Should().Be(PreflightStatus.AuthenticationRequired);
        result.ResponseStatusCode.Should().Be(status);
        result.Message.Should().Contain($"HTTP {status}").And.NotContain("timeout");
        result.IsLikelyLoginPage.Should().BeFalse();
    }

    [Fact]
    public void SignInRedirect_IsAuthenticationRequiredWithLoginPageFlag()
    {
        var result = TargetPreflightService.Map(Probe(TargetReachability.AuthenticationRequired, 200, "Target redirected to a sign-in page.", "https://login.microsoftonline.com/common/oauth2/v2.0/authorize", redirects: 1), Target);

        result.Status.Should().Be(PreflightStatus.AuthenticationRequired);
        result.RedirectOccurred.Should().BeTrue();
        result.IsLikelyLoginPage.Should().BeTrue();
    }

    [Theory]
    [InlineData(404)]
    [InlineData(410)]
    public void ClientErrorStatus_IsUnreachableWithStatusMessage(int status)
    {
        var result = TargetPreflightService.Map(Probe(TargetReachability.Unreachable, status, $"Target returned HTTP {status}."), Target);

        result.Status.Should().Be(PreflightStatus.Unreachable);
        result.Message.Should().Be($"Target returned HTTP {status}.");
    }

    [Fact]
    public void ServerError_IsReadyWithWarnings()
    {
        var result = TargetPreflightService.Map(Probe(TargetReachability.Reachable, 500, "Target returned HTTP 500. Server error detected."), Target);

        result.Status.Should().Be(PreflightStatus.ReadyWithWarnings);
        result.ResponseStatusCode.Should().Be(500);
    }

    [Theory]
    [InlineData(TargetReachability.DnsError, "Target hostname could not be resolved.")]
    [InlineData(TargetReachability.TlsError, "TLS handshake with the target failed.")]
    [InlineData(TargetReachability.Unreachable, "Target unreachable: the connection could not be established.")]
    [InlineData(TargetReachability.TooManyRedirects, "Target redirected too many times.")]
    [InlineData(TargetReachability.UntrustedRedirect, "Target redirected to a location that is not trusted for this probe.")]
    public void NetworkFailures_AreUnreachableNeverTimeout(TargetReachability reachability, string message)
    {
        var result = TargetPreflightService.Map(Probe(reachability, null, message), Target);

        result.Status.Should().Be(PreflightStatus.Unreachable);
        result.Message.Should().Be(message);
        result.Message.Should().NotContain("timeout");
    }

    [Fact]
    public void InvalidUrlBlock_IsInvalidTarget()
    {
        TargetPreflightService.Map(Probe(TargetReachability.Unknown, null, "Target URL is not a valid absolute http(s) URL.", blockReason: "INVALID_URL"), Target)
            .Status.Should().Be(PreflightStatus.InvalidTarget);
    }

    [Fact]
    public void EmptyMessage_GetsDefaultForStatus()
    {
        TargetPreflightService.Map(Probe(TargetReachability.Unreachable, 404), Target).Message.Should().Be("Target returned HTTP 404.");
        TargetPreflightService.Map(Probe(TargetReachability.Reachable, 200), Target).Message.Should().Contain("reachable");
    }

    [Fact]
    public async Task CheckTarget_InvalidUrl_NeverCallsBackend()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var service = new TargetPreflightService(new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5000/") });

        var result = await service.CheckTargetAsync("not a url");

        result.Status.Should().Be(PreflightStatus.InvalidTarget);
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task CheckTarget_UsesBackendReachabilityEndpoint_NotTheTarget()
    {
        var probe = Probe(TargetReachability.Reachable, 200, "Target is reachable (HTTP 200).");
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(probe) });
        var service = new TargetPreflightService(new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5000/") });

        var result = await service.CheckTargetAsync(Target);

        result.Status.Should().Be(PreflightStatus.Ready);
        handler.Requests.Should().ContainSingle();
        handler.Requests[0].RequestUri!.ToString().Should().Be("http://localhost:5000/api/frontend-target/reachability");
        handler.Requests[0].Method.Should().Be(HttpMethod.Post);
        handler.Requests[0].RequestUri!.Host.Should().NotBe(new Uri(Target).Host, "the browser must never fetch the cross-origin target directly");
        handler.Bodies[0].Should().Contain(Target);
        handler.Bodies[0].Should().NotContainAny("Authorization", "Bearer", "Cookie");
    }

    [Fact]
    public async Task CheckTarget_BackendDown_IsScannerUnavailableNotTimeout()
    {
        var handler = new RecordingHandler(_ => throw new HttpRequestException("connection refused"));
        var service = new TargetPreflightService(new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5000/") });

        var result = await service.CheckTargetAsync(Target);

        result.Status.Should().Be(PreflightStatus.ScannerUnavailable);
        result.Message.Should().Be(TargetPreflightService.BackendUnavailableMessage);
    }

    [Fact]
    public async Task CheckTarget_BackendError_IsScannerUnavailableWithStatus()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var service = new TargetPreflightService(new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5000/") });

        var result = await service.CheckTargetAsync(Target);

        result.Status.Should().Be(PreflightStatus.ScannerUnavailable);
        result.Message.Should().Contain("HTTP 500");
    }

    [Fact]
    public async Task CheckTarget_BackendTimeoutProbe_IsTimedOut()
    {
        var probe = Probe(TargetReachability.Timeout, null, "Target did not respond within timeout period.");
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(probe) });
        var service = new TargetPreflightService(new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5000/") });

        var result = await service.CheckTargetAsync(Target);

        result.Status.Should().Be(PreflightStatus.TimedOut);
        result.Message.Should().Be(TargetPreflightService.TimeoutMessage);
    }

    [Fact]
    public void ProbeDto_RoundTripsStringEnums()
    {
        var json = """{"targetUrl":"https://x.example.test/","reachability":"AuthenticationRequired","statusCode":401,"message":"Target returned HTTP 401.","authenticationRequired":true,"redirectCount":0,"elapsedMs":3.2}""";
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

        var dto = JsonSerializer.Deserialize<TargetReachabilityProbeDto>(json, options)!;

        dto.Reachability.Should().Be(TargetReachability.AuthenticationRequired);
        dto.StatusCode.Should().Be(401);
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            return respond(request);
        }
    }
}
