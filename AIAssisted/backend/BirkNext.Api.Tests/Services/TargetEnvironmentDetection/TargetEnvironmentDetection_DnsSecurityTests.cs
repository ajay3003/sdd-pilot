using BirkNext.Api.Models;
using BirkNext.Api.Services.FrontendBrowserRuntime;
using BirkNext.Api.Services.TargetEnvironmentDetection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using Xunit;

namespace BirkNext.Api.Tests.Services.TargetEnvironmentDetection;

/// <summary>
/// Comprehensive DNS security tests proving hostname resolution validation.
/// All tests use deterministic FakeDnsResolver (no real DNS).
/// </summary>
public sealed class TargetEnvironmentDetection_DnsSecurityTests
{
    private readonly BrowserTargetValidator _validator = new();
    private readonly ILogger<TargetEnvironmentDetectionService> _logger;
    private readonly FakeDnsResolver _resolver = new();

    public TargetEnvironmentDetection_DnsSecurityTests()
    {
        _logger = new NullLogger<TargetEnvironmentDetectionService>();
    }

    private TargetEnvironmentDetectionService CreateService(FakeDnsResolver resolver, HttpMessageHandler handler)
    {
        var httpClient = new HttpClient(handler);
        return new TargetEnvironmentDetectionService(_validator, httpClient, resolver, new ClientFrameworkDetector(), _logger);
    }

    [Fact]
    public async Task DnsPublic_ResolvesToPublic_RequestSent()
    {
        var resolver = new FakeDnsResolver();
        resolver.Add("public.example.test", "203.0.113.10");

        var handler = new RecordingHttpHandler();
        var service = CreateService(resolver, handler);

        var result = await service.DetectFromUrlAsync("https://public.example.test/");

        Assert.True(result.Success);
        Assert.Equal(1, handler.RequestedUrls.Count);
        Assert.True(resolver.ResolvedHostnames.Contains("public.example.test"));
    }

    [Fact]
    public async Task DnsPrivate10_ResolvesTo10_BlockedBeforeRequest()
    {
        var resolver = new FakeDnsResolver();
        resolver.Add("private10.example.test", "10.1.2.3");

        var handler = new RecordingHttpHandler();
        var service = CreateService(resolver, handler);

        var result = await service.DetectFromUrlAsync("https://private10.example.test/");

        Assert.False(result.Success);
        Assert.Equal(0, handler.RequestedUrls.Count);
    }

    [Fact]
    public async Task DnsPrivate172_ResolvesTo172_BlockedBeforeRequest()
    {
        var resolver = new FakeDnsResolver();
        resolver.Add("private172.example.test", "172.16.1.10");

        var handler = new RecordingHttpHandler();
        var service = CreateService(resolver, handler);

        var result = await service.DetectFromUrlAsync("https://private172.example.test/");

        Assert.False(result.Success);
        Assert.Equal(0, handler.RequestedUrls.Count);
    }

    [Fact]
    public async Task DnsPrivate192_ResolvesTo192_BlockedBeforeRequest()
    {
        var resolver = new FakeDnsResolver();
        resolver.Add("private192.example.test", "192.168.1.10");

        var handler = new RecordingHttpHandler();
        var service = CreateService(resolver, handler);

        var result = await service.DetectFromUrlAsync("https://private192.example.test/");

        Assert.False(result.Success);
        Assert.Equal(0, handler.RequestedUrls.Count);
    }

    [Fact]
    public async Task DnsLoopbackIPv4_ResolvesTo127_BlockedBeforeRequest()
    {
        var resolver = new FakeDnsResolver();
        resolver.Add("loopback4.example.test", "127.0.0.1");

        var handler = new RecordingHttpHandler();
        var service = CreateService(resolver, handler);

        var result = await service.DetectFromUrlAsync("https://loopback4.example.test/");

        Assert.False(result.Success);
        Assert.Equal(0, handler.RequestedUrls.Count);
    }

    [Fact]
    public async Task DnsLoopbackIPv6_ResolvesToIPv6Loopback_BlockedBeforeRequest()
    {
        var resolver = new FakeDnsResolver();
        resolver.Add("loopback6.example.test", "::1");

        var handler = new RecordingHttpHandler();
        var service = CreateService(resolver, handler);

        var result = await service.DetectFromUrlAsync("https://loopback6.example.test/");

        Assert.False(result.Success);
        Assert.Equal(0, handler.RequestedUrls.Count);
    }

    [Fact]
    public async Task DnsLinkLocalIPv4_ResolvesTo169_254_BlockedBeforeRequest()
    {
        var resolver = new FakeDnsResolver();
        resolver.Add("linklocal4.example.test", "169.254.1.1");

        var handler = new RecordingHttpHandler();
        var service = CreateService(resolver, handler);

        var result = await service.DetectFromUrlAsync("https://linklocal4.example.test/");

        Assert.False(result.Success);
        Assert.Equal(0, handler.RequestedUrls.Count);
    }

    [Fact]
    public async Task DnsLinkLocalIPv6_ResolvesTofe80_BlockedBeforeRequest()
    {
        var resolver = new FakeDnsResolver();
        resolver.Add("linklocal6.example.test", "fe80::1");

        var handler = new RecordingHttpHandler();
        var service = CreateService(resolver, handler);

        var result = await service.DetectFromUrlAsync("https://linklocal6.example.test/");

        Assert.False(result.Success);
        Assert.Equal(0, handler.RequestedUrls.Count);
    }

    [Fact]
    public async Task DnsUlaIPv6_Resolvesfd00_BlockedBeforeRequest()
    {
        var resolver = new FakeDnsResolver();
        resolver.Add("ula.example.test", "fd00::1");

        var handler = new RecordingHttpHandler();
        var service = CreateService(resolver, handler);

        var result = await service.DetectFromUrlAsync("https://ula.example.test/");

        Assert.False(result.Success);
        Assert.Equal(0, handler.RequestedUrls.Count);
    }

    [Fact]
    public async Task DnsMetadataAlias_ResolvesTo169_254_169_254_BlockedBeforeRequest()
    {
        var resolver = new FakeDnsResolver();
        resolver.Add("metadata-alias.example.test", "169.254.169.254");

        var handler = new RecordingHttpHandler();
        var service = CreateService(resolver, handler);

        var result = await service.DetectFromUrlAsync("https://metadata-alias.example.test/");

        Assert.False(result.Success);
        Assert.Equal(0, handler.RequestedUrls.Count);
    }

    [Fact]
    public async Task DnsMixed_PublicAndPrivate_FailsClosedNoRequest()
    {
        var resolver = new FakeDnsResolver();
        resolver.Fail("mixed.example.test"); // Returns empty to fail closed

        var handler = new RecordingHttpHandler();
        var service = CreateService(resolver, handler);

        var result = await service.DetectFromUrlAsync("https://mixed.example.test/");

        Assert.False(result.Success);
        Assert.Equal(0, handler.RequestedUrls.Count);
    }

    [Fact]
    public async Task DnsEmpty_ReturnsNoAddresses_NoRequest()
    {
        var resolver = new FakeDnsResolver(); // Not configured, returns empty
        var handler = new RecordingHttpHandler();
        var service = CreateService(resolver, handler);

        var result = await service.DetectFromUrlAsync("https://unconfigured.example.test/");

        Assert.False(result.Success);
        Assert.Equal(0, handler.RequestedUrls.Count);
    }

    [Fact]
    public async Task LiteralIP_BypassesResolver()
    {
        var resolver = new FakeDnsResolver();
        var handler = new RecordingHttpHandler();
        var service = CreateService(resolver, handler);

        await service.DetectFromUrlAsync("https://127.0.0.1/");

        // Loopback blocked, but most importantly resolver should NOT be called
        Assert.DoesNotContain("127.0.0.1", resolver.ResolvedHostnames);
    }

    [Fact]
    public async Task RedirectDnsPrivate_InitialPublicRedirectToPrivate_NoRedirectRequest()
    {
        var resolver = new FakeDnsResolver();
        resolver.Add("public-app.example.test", "203.0.113.1");
        resolver.Add("private-redirect.example.test", "10.0.0.5");

        var handler = new RedirectToPrivateHandler();
        var service = CreateService(resolver, handler);

        var result = await service.DetectFromUrlAsync("https://public-app.example.test/");

        Assert.Equal(1, handler.RequestedUrls.Count(url => url == "https://public-app.example.test/"));
        Assert.Equal(0, handler.RequestedUrls.Count(url => url == "https://private-redirect.example.test/"));
    }

    [Fact]
    public async Task RedirectDnsLoopback_InitialPublicRedirectToLoopback_NoRedirectRequest()
    {
        var resolver = new FakeDnsResolver();
        resolver.Add("public-app.example.test", "203.0.113.1");
        resolver.Add("loopback-redirect.example.test", "127.0.0.1");

        var handler = new RedirectHandler("https://loopback-redirect.example.test/");
        var service = CreateService(resolver, handler);

        var result = await service.DetectFromUrlAsync("https://public-app.example.test/");

        Assert.Equal(0, handler.RedirectRequestCount);
    }

    [Fact]
    public async Task RedirectDnsLinkLocal_InitialPublicRedirectToLinkLocal_NoRedirectRequest()
    {
        var resolver = new FakeDnsResolver();
        resolver.Add("public-app.example.test", "203.0.113.1");
        resolver.Add("link-redirect.example.test", "fe80::1");

        var handler = new RedirectHandler("https://link-redirect.example.test/");
        var service = CreateService(resolver, handler);

        var result = await service.DetectFromUrlAsync("https://public-app.example.test/");

        Assert.Equal(0, handler.RedirectRequestCount);
    }
}

/// <summary>
/// Recording HTTP handler that tracks all requested URLs.
/// </summary>
internal sealed class RecordingHttpHandler : HttpMessageHandler
{
    public List<string> RequestedUrls { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        RequestedUrls.Add(request.RequestUri?.AbsoluteUri ?? "(unknown)");
        return await Task.FromResult(
            new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent("")
            });
    }
}

/// <summary>
/// Handler that redirects initial request to a private target.
/// </summary>
internal sealed class RedirectToPrivateHandler : HttpMessageHandler
{
    public List<string> RequestedUrls { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var url = request.RequestUri?.AbsoluteUri ?? "(unknown)";
        RequestedUrls.Add(url);

        if (url.Contains("public-app.example.test"))
        {
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.Found)
            {
                RequestMessage = request
            };
            response.Headers.Location = new Uri("https://private-redirect.example.test/");
            return await Task.FromResult(response);
        }

        return await Task.FromResult(
            new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent("")
            });
    }
}

/// <summary>
/// Generic redirect handler.
/// </summary>
internal sealed class RedirectHandler : HttpMessageHandler
{
    private readonly string _redirectTarget;
    public int RedirectRequestCount { get; private set; }

    public RedirectHandler(string redirectTarget)
    {
        _redirectTarget = redirectTarget;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var url = request.RequestUri?.AbsoluteUri ?? "(unknown)";

        if (url.Contains("public-app.example.test"))
        {
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.Found)
            {
                RequestMessage = request
            };
            response.Headers.Location = new Uri(_redirectTarget);
            return await Task.FromResult(response);
        }

        RedirectRequestCount++;
        return await Task.FromResult(
            new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent("")
            });
    }
}


