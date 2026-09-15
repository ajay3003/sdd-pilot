using System.Net;
using System.Net.Sockets;
using BirkNext.Api.Controllers;
using BirkNext.Api.Models;
using BirkNext.Api.Services.FrontendBrowserRuntime;
using BirkNext.Api.Services.TargetEnvironmentDetection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BirkNext.Api.Tests.Services.TargetEnvironmentDetection;

/// <summary>
/// Server-side reachability probe used by the Frontend Quality Review preflight. Every outcome must be classified precisely:
/// a real HTTP status is never reported as a timeout, and the timeout wording appears only for a real timeout.
/// </summary>
public sealed class TargetReachabilityProbeTests
{
    private const string TimeoutWording = "Target did not respond within timeout period.";
    private readonly FakeDnsResolver _resolver = new();

    public TargetReachabilityProbeTests()
    {
        _resolver.Add("example.com", "203.0.113.1");
        _resolver.Add("login.microsoftonline.com", "203.0.113.10");
    }

    private TargetEnvironmentDetectionService Service(HttpMessageHandler handler) =>
        new(new BrowserTargetValidator(), new HttpClient(handler), _resolver, new ClientFrameworkDetector(),
            NullLogger<TargetEnvironmentDetectionService>.Instance);

    private static RoutingHandler Status(HttpStatusCode code) => new(_ => new HttpResponseMessage(code));

    [Fact]
    public async Task Ok_IsReachableWithStatusCode()
    {
        var probe = await Service(Status(HttpStatusCode.OK)).ProbeReachabilityAsync("https://example.com/app?code=SECRET#frag");

        Assert.Equal(TargetReachability.Reachable, probe.Reachability);
        Assert.Equal(200, probe.StatusCode);
        Assert.True(probe.ResponseReceived);
        Assert.False(probe.AuthenticationRequired);
        Assert.Contains("HTTP 200", probe.Message);
        Assert.DoesNotContain("SECRET", probe.TargetUrl);
        Assert.DoesNotContain("SECRET", probe.FinalUrl);
        Assert.DoesNotContain(TimeoutWording, probe.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, 401)]
    [InlineData(HttpStatusCode.Forbidden, 403)]
    public async Task AuthGate_IsAuthenticationRequiredWithPreciseStatus(HttpStatusCode code, int expected)
    {
        var probe = await Service(Status(code)).ProbeReachabilityAsync("https://example.com/");

        Assert.Equal(TargetReachability.AuthenticationRequired, probe.Reachability);
        Assert.Equal(expected, probe.StatusCode);
        Assert.True(probe.AuthenticationRequired);
        Assert.Contains($"HTTP {expected}", probe.Message);
        Assert.DoesNotContain(TimeoutWording, probe.Message);
    }

    [Fact]
    public async Task NotFound_IsUnreachableWithStatusNotTimeout()
    {
        var probe = await Service(Status(HttpStatusCode.NotFound)).ProbeReachabilityAsync("https://example.com/");

        Assert.Equal(TargetReachability.Unreachable, probe.Reachability);
        Assert.Equal(404, probe.StatusCode);
        Assert.Equal("Target returned HTTP 404.", probe.Message);
    }

    [Fact]
    public async Task ServerError_IsReachableButFlagged()
    {
        var probe = await Service(Status(HttpStatusCode.InternalServerError)).ProbeReachabilityAsync("https://example.com/");

        Assert.Equal(TargetReachability.Reachable, probe.Reachability);
        Assert.Equal(500, probe.StatusCode);
        Assert.Contains("HTTP 500", probe.Message);
        Assert.Contains("Server error", probe.Message);
    }

    [Fact]
    public async Task RealTimeout_IsTheOnlyTimeoutWording()
    {
        var handler = new RoutingHandler(_ => throw new TaskCanceledException());

        var probe = await Service(handler).ProbeReachabilityAsync("https://example.com/");

        Assert.Equal(TargetReachability.Timeout, probe.Reachability);
        Assert.Null(probe.StatusCode);
        Assert.False(probe.ResponseReceived);
        Assert.Equal(TimeoutWording, probe.Message);
    }

    [Fact]
    public async Task ConnectionFailure_IsUnreachableNotTimeout()
    {
        var handler = new RoutingHandler(_ => throw new HttpRequestException("connection refused", new SocketException((int)SocketError.ConnectionRefused)));

        var probe = await Service(handler).ProbeReachabilityAsync("https://example.com/");

        Assert.Equal(TargetReachability.Unreachable, probe.Reachability);
        Assert.Null(probe.StatusCode);
        Assert.DoesNotContain(TimeoutWording, probe.Message);
        Assert.Contains("unreachable", probe.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DnsFailure_IsDnsError()
    {
        var handler = new RoutingHandler(_ => throw new HttpRequestException("no such host", new SocketException((int)SocketError.HostNotFound)));

        var probe = await Service(handler).ProbeReachabilityAsync("https://example.com/");

        Assert.Equal(TargetReachability.DnsError, probe.Reachability);
        Assert.Contains("resolved", probe.Message);
    }

    [Fact]
    public async Task TlsFailure_IsTlsError()
    {
        var handler = new RoutingHandler(_ => throw new HttpRequestException("tls", new System.Security.Authentication.AuthenticationException("bad cert")));

        var probe = await Service(handler).ProbeReachabilityAsync("https://example.com/");

        Assert.Equal(TargetReachability.TlsError, probe.Reachability);
        Assert.Contains("TLS", probe.Message);
    }

    [Fact]
    public async Task SignInRedirect_IsAuthenticationRequired()
    {
        var handler = new RoutingHandler(request =>
        {
            if (request.RequestUri!.Host == "example.com")
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = new Uri("https://login.microsoftonline.com/common/oauth2/v2.0/authorize?client_id=FAKE&state=FAKE");
                return redirect;
            }
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var probe = await Service(handler).ProbeReachabilityAsync("https://example.com/");

        Assert.Equal(TargetReachability.AuthenticationRequired, probe.Reachability);
        Assert.True(probe.AuthenticationRequired);
        Assert.Equal(1, probe.RedirectCount);
        Assert.Contains("sign-in", probe.Message);
        Assert.DoesNotContain("state=", probe.FinalUrl);
        Assert.DoesNotContain("client_id", probe.FinalUrl);
    }

    [Fact]
    public async Task HeadRejected_FallsBackToGet()
    {
        var methods = new List<string>();
        var handler = new RoutingHandler(request =>
        {
            methods.Add(request.Method.Method);
            return request.Method == HttpMethod.Head
                ? new HttpResponseMessage(HttpStatusCode.MethodNotAllowed)
                : new HttpResponseMessage(HttpStatusCode.OK);
        });

        var probe = await Service(handler).ProbeReachabilityAsync("https://example.com/");

        Assert.Equal(TargetReachability.Reachable, probe.Reachability);
        Assert.Equal(200, probe.StatusCode);
        Assert.Equal(new[] { "HEAD", "GET" }, methods);
    }

    [Fact]
    public async Task InvalidUrl_IsRejectedWithoutRequest()
    {
        var handler = new RoutingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));

        var probe = await Service(handler).ProbeReachabilityAsync("not a url");

        Assert.Equal(TargetReachability.Unknown, probe.Reachability);
        Assert.Equal("INVALID_URL", probe.BlockReason);
        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task PolicyBlockedTarget_IsUnreachableWithoutRequest()
    {
        var handler = new RoutingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));

        var probe = await Service(handler).ProbeReachabilityAsync("http://169.254.169.254/latest/meta-data/");

        Assert.Equal(TargetReachability.Unreachable, probe.Reachability);
        Assert.NotNull(probe.BlockReason);
        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task Controller_ValidTarget_ReturnsProbeResult()
    {
        var controller = new TargetEnvironmentDetectionController(Service(Status(HttpStatusCode.OK)), NullLogger<TargetEnvironmentDetectionController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var result = await controller.ProbeReachability(new TargetReachabilityRequest { TargetUrl = "https://example.com/" }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var probe = Assert.IsType<TargetReachabilityProbeResult>(ok.Value);
        Assert.Equal(TargetReachability.Reachable, probe.Reachability);
        Assert.Equal("no-store", controller.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public async Task Controller_MissingTarget_ReturnsBadRequest()
    {
        var controller = new TargetEnvironmentDetectionController(Service(Status(HttpStatusCode.OK)), NullLogger<TargetEnvironmentDetectionController>.Instance);

        var result = await controller.ProbeReachability(new TargetReachabilityRequest { TargetUrl = " " }, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    private sealed class RoutingHandler(Func<HttpRequestMessage, HttpResponseMessage> route) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            var response = route(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
}
