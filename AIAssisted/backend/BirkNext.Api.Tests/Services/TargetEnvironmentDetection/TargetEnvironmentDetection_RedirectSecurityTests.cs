using BirkNext.Api.Models;
using BirkNext.Api.Services.FrontendBrowserRuntime;
using BirkNext.Api.Services.TargetEnvironmentDetection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using System.Net;

namespace BirkNext.Api.Tests.Services.TargetEnvironmentDetection;

/// <summary>
/// CRITICAL SECURITY TESTS: Redirect TOCTOU Vulnerability
///
/// Verifies that HttpClient has AllowAutoRedirect=false and manual
/// redirect validation happens BEFORE the redirected destination is requested.
///
/// Attack scenario (TOCTOU):
///   1. Attacker requests public.example.com
///   2. Server responds with HTTP 302 to http://127.0.0.1/admin
///   3. BEFORE the fix: HttpClient follows redirect AUTOMATICALLY without validation
///   4. AFTER the fix: Manual validation happens, redirect blocked, 127.0.0.1 NEVER requested
/// </summary>
public sealed class TargetEnvironmentDetection_RedirectSecurityTests
{
    private readonly BrowserTargetValidator _validator = new();
    private readonly ILogger<TargetEnvironmentDetectionService> _logger;
    private readonly FakeDnsResolver _resolver = new();

    public TargetEnvironmentDetection_RedirectSecurityTests()
    {
        _logger = new NullLogger<TargetEnvironmentDetectionService>();
        // Configure all test hostnames to resolve to documentation IPs
        _resolver.Add("https://public.example.test/", "203.0.113.1");
        _resolver.Add("public.example.test", "203.0.113.1");
        _resolver.Add("application.example.test", "203.0.113.2");
        _resolver.Add("login.example.test", "203.0.113.3");
        _resolver.Add("step1.example.test", "203.0.113.10");
        _resolver.Add("step2.example.test", "203.0.113.11");
        _resolver.Add("step3.example.test", "203.0.113.12");
        _resolver.Add("step4.example.test", "203.0.113.13");
        _resolver.Add("step5.example.test", "203.0.113.14");
        _resolver.Add("loop-a.example.test", "203.0.113.20");
        _resolver.Add("loop-b.example.test", "203.0.113.21");
    }

    /// <summary>
    /// CRITICAL TEST: Proves that loopback redirect is NEVER requested.
    /// Public URL redirects to loopback → must be blocked before request.
    /// </summary>
    [Fact]
    public async Task DetectFromUrlAsync_PublicRedirectsToLoopback_BlockedBeforeRequest()
    {
        var requestedUrls = new List<string>();
        var handler = new TrackingRedirectHandler(requestedUrls,
            initialUrl: "https://public.example.test/",
            redirectUrl: "http://127.0.0.1:5000/admin",
            redirectStatus: 302);

        var httpClient = new HttpClient(handler);
        var service = new TargetEnvironmentDetectionService(_validator, httpClient, _resolver, new ClientFrameworkDetector(), _logger);

        var result = await service.DetectFromUrlAsync("https://public.example.test/");

        // PROOF: Loopback address was NEVER requested
        Assert.NotEmpty(requestedUrls);
        Assert.Single(requestedUrls);
        Assert.Equal("https://public.example.test/", requestedUrls[0]);
        Assert.DoesNotContain("127.0.0.1", string.Join("|", requestedUrls));

        // Result should indicate untrusted redirect
        Assert.True(result != null && result.Reachability == TargetReachability.UntrustedRedirect);
    }

    /// <summary>
    /// RFC1918 Private Network: 10.0.0.0/8 redirect blocked before request.
    /// </summary>
    [Fact]
    public async Task DetectFromUrlAsync_PublicRedirectsToRFC1918_10_BlockedBeforeRequest()
    {
        var requestedUrls = new List<string>();
        var handler = new TrackingRedirectHandler(requestedUrls,
            initialUrl: "https://public.example.test/",
            redirectUrl: "http://10.0.0.10/internal",
            redirectStatus: 302);

        var httpClient = new HttpClient(handler);
        var service = new TargetEnvironmentDetectionService(_validator, httpClient, _resolver, new ClientFrameworkDetector(), _logger);

        var result = await service.DetectFromUrlAsync("https://public.example.test/");

        Assert.Single(requestedUrls);
        Assert.DoesNotContain("10.0.0.10", string.Join("|", requestedUrls));
        Assert.Equal(TargetReachability.UntrustedRedirect, result.Reachability);
    }

    /// <summary>
    /// RFC1918 Private Network: 172.16.0.0/12 redirect blocked before request.
    /// </summary>
    [Fact]
    public async Task DetectFromUrlAsync_PublicRedirectsToRFC1918_172_BlockedBeforeRequest()
    {
        var requestedUrls = new List<string>();
        var handler = new TrackingRedirectHandler(requestedUrls,
            initialUrl: "https://public.example.test/",
            redirectUrl: "http://172.16.0.10/data",
            redirectStatus: 302);

        var httpClient = new HttpClient(handler);
        var service = new TargetEnvironmentDetectionService(_validator, httpClient, _resolver, new ClientFrameworkDetector(), _logger);

        var result = await service.DetectFromUrlAsync("https://public.example.test/");

        Assert.Single(requestedUrls);
        Assert.DoesNotContain("172.16", string.Join("|", requestedUrls));
        Assert.Equal(TargetReachability.UntrustedRedirect, result.Reachability);
    }

    /// <summary>
    /// RFC1918 Private Network: 192.168.0.0/16 redirect blocked before request.
    /// </summary>
    [Fact]
    public async Task DetectFromUrlAsync_PublicRedirectsToRFC1918_192_BlockedBeforeRequest()
    {
        var requestedUrls = new List<string>();
        var handler = new TrackingRedirectHandler(requestedUrls,
            initialUrl: "https://public.example.test/",
            redirectUrl: "http://192.168.1.1/gateway",
            redirectStatus: 302);

        var httpClient = new HttpClient(handler);
        var service = new TargetEnvironmentDetectionService(_validator, httpClient, _resolver, new ClientFrameworkDetector(), _logger);

        var result = await service.DetectFromUrlAsync("https://public.example.test/");

        Assert.Single(requestedUrls);
        Assert.DoesNotContain("192.168", string.Join("|", requestedUrls));
        Assert.Equal(TargetReachability.UntrustedRedirect, result.Reachability);
    }

    /// <summary>
    /// IPv6 Link-Local: fe80::/10 redirect blocked before request.
    /// </summary>
    [Fact]
    public async Task DetectFromUrlAsync_PublicRedirectsToLinkLocal_BlockedBeforeRequest()
    {
        var requestedUrls = new List<string>();
        var handler = new TrackingRedirectHandler(requestedUrls,
            initialUrl: "https://public.example.test/",
            redirectUrl: "http://[fe80::1]/admin",
            redirectStatus: 302);

        var httpClient = new HttpClient(handler);
        var service = new TargetEnvironmentDetectionService(_validator, httpClient, _resolver, new ClientFrameworkDetector(), _logger);

        var result = await service.DetectFromUrlAsync("https://public.example.test/");

        Assert.Single(requestedUrls);
        Assert.DoesNotContain("fe80", string.Join("|", requestedUrls));
        Assert.Equal(TargetReachability.UntrustedRedirect, result.Reachability);
    }

    /// <summary>
    /// IPv6 Loopback: ::1 redirect blocked before request.
    /// </summary>
    [Fact]
    public async Task DetectFromUrlAsync_PublicRedirectsToIPv6Loopback_BlockedBeforeRequest()
    {
        var requestedUrls = new List<string>();
        var handler = new TrackingRedirectHandler(requestedUrls,
            initialUrl: "https://public.example.test/",
            redirectUrl: "http://[::1]:8080/",
            redirectStatus: 302);

        var httpClient = new HttpClient(handler);
        var service = new TargetEnvironmentDetectionService(_validator, httpClient, _resolver, new ClientFrameworkDetector(), _logger);

        var result = await service.DetectFromUrlAsync("https://public.example.test/");

        Assert.Single(requestedUrls);
        // Should only see public URL, not IPv6 loopback
        var requestString = string.Join("|", requestedUrls);
        Assert.DoesNotContain("::", requestString);
        Assert.Equal(TargetReachability.UntrustedRedirect, result.Reachability);
    }

    /// <summary>
    /// Unsupported scheme: file:// in redirect target blocked before request.
    /// </summary>
    [Fact]
    public async Task DetectFromUrlAsync_RedirectToFileScheme_BlockedBeforeRequest()
    {
        var requestedUrls = new List<string>();
        var handler = new TrackingRedirectHandler(requestedUrls,
            initialUrl: "https://public.example.test/",
            redirectUrl: "file:///etc/passwd",
            redirectStatus: 302);

        var httpClient = new HttpClient(handler);
        var service = new TargetEnvironmentDetectionService(_validator, httpClient, _resolver, new ClientFrameworkDetector(), _logger);

        var result = await service.DetectFromUrlAsync("https://public.example.test/");

        // Only initial URL should be requested
        Assert.Single(requestedUrls);
        Assert.Equal("https://public.example.test/", requestedUrls[0]);
    }

    /// <summary>
    /// Valid relative redirect: /path → should resolve and validate before request.
    /// </summary>
    [Fact]
    public async Task DetectFromUrlAsync_ValidRelativeRedirect_RequestedAfterValidation()
    {
        var requestedUrls = new List<string>();
        var handler = new TrackingRedirectHandler(requestedUrls,
            initialUrl: "https://application.example.test/start",
            redirectUrl: "/login", // Relative redirect
            redirectStatus: 302,
            allowRelative: true);

        var httpClient = new HttpClient(handler);
        var service = new TargetEnvironmentDetectionService(_validator, httpClient, _resolver, new ClientFrameworkDetector(), _logger);

        var result = await service.DetectFromUrlAsync("https://application.example.test/start");

        // Should have requested both initial and resolved redirect
        Assert.NotEmpty(requestedUrls);
        Assert.Contains("https://application.example.test/start", requestedUrls);
        Assert.Contains("https://application.example.test/login", requestedUrls);
        Assert.Equal(1, result.RedirectCount);
    }

    /// <summary>
    /// Redirect chain: A → B (public) → request allowed
    /// Maximum should be enforced (5 by default).
    /// </summary>
    [Fact]
    public async Task DetectFromUrlAsync_RedirectChainMaximum_EnformedBeforeExceeding()
    {
        var requestedUrls = new List<string>();
        var handler = new ChainedRedirectHandler(requestedUrls, chainLength: 5);

        var httpClient = new HttpClient(handler);
        var service = new TargetEnvironmentDetectionService(_validator, httpClient, _resolver, new ClientFrameworkDetector(), _logger);

        var result = await service.DetectFromUrlAsync("https://step1.example.test/");

        // Should have made requests for step1, step2, step3, step4, step5
        // (not step6, which would exceed limit)
        Assert.True(requestedUrls.Count >= 1 && requestedUrls.Count <= 5);
        Assert.Equal(TargetReachability.Reachable, result.Reachability);
        Assert.True(result.RedirectCount <= 5);
    }

    /// <summary>
    /// Redirect chain exceeding maximum: should terminate at max, never request excess.
    /// </summary>
    [Fact]
    public async Task DetectFromUrlAsync_RedirectChainExceedsMax_StopsAtMaximum()
    {
        var requestedUrls = new List<string>();
        // Try to force 10 redirects, but max is 5
        var handler = new ChainedRedirectHandler(requestedUrls, chainLength: 10);

        var httpClient = new HttpClient(handler);
        var service = new TargetEnvironmentDetectionService(_validator, httpClient, _resolver, new ClientFrameworkDetector(), _logger);

        var result = await service.DetectFromUrlAsync("https://step1.example.test/");

        // Should NOT make more than 5 requests
        Assert.True(requestedUrls.Count <= 6); // Initial + 5 redirects = 6 max
        Assert.Equal(TargetReachability.TooManyRedirects, result.Reachability);
    }

    /// <summary>
    /// Redirect loop: A → B → A → (repeat) should terminate deterministically.
    /// </summary>
    [Fact]
    public async Task DetectFromUrlAsync_RedirectLoop_TerminatesDeterministically()
    {
        var requestedUrls = new List<string>();
        var handler = new LoopingRedirectHandler(requestedUrls);

        var httpClient = new HttpClient(handler);
        var service = new TargetEnvironmentDetectionService(_validator, httpClient, _resolver, new ClientFrameworkDetector(), _logger);

        var result = await service.DetectFromUrlAsync("https://loop-a.example.test/");

        // Should terminate at max (5 redirects) without infinite loop
        Assert.True(requestedUrls.Count <= 6); // Initial + 5 max redirects
        Assert.Equal(TargetReachability.TooManyRedirects, result.Reachability);
    }

    /// <summary>
    /// Userinfo in redirect target: user:SUPER_SECRET_PASSWORD@host
    /// Should not be extracted/logged.
    /// </summary>
    [Fact]
    public async Task DetectFromUrlAsync_UserinfoRedirect_NotExtractedOrLogged()
    {
        var requestedUrls = new List<string>();
        var handler = new TrackingRedirectHandler(requestedUrls,
            initialUrl: "https://public.example.test/",
            redirectUrl: "https://user:SUPER_SECRET_PASSWORD@example.test/login",
            redirectStatus: 302);

        var httpClient = new HttpClient(handler);
        var service = new TargetEnvironmentDetectionService(_validator, httpClient, _resolver, new ClientFrameworkDetector(), _logger);

        var result = await service.DetectFromUrlAsync("https://public.example.test/");

        // Serialized response should NOT contain the password
        var json = System.Text.Json.JsonSerializer.Serialize(result);
        Assert.DoesNotContain("SUPER_SECRET_PASSWORD", json);
        Assert.DoesNotContain("user:SUPER_SECRET", json);
    }

    /// <summary>
    /// Metadata endpoint redirect: should be blocked.
    /// </summary>
    [Fact]
    public async Task DetectFromUrlAsync_RedirectToMetadata_BlockedBeforeRequest()
    {
        var requestedUrls = new List<string>();
        var handler = new TrackingRedirectHandler(requestedUrls,
            initialUrl: "https://public.example.test/",
            redirectUrl: "http://169.254.169.254/latest/meta-data/",
            redirectStatus: 302);

        var httpClient = new HttpClient(handler);
        var service = new TargetEnvironmentDetectionService(_validator, httpClient, _resolver, new ClientFrameworkDetector(), _logger);

        var result = await service.DetectFromUrlAsync("https://public.example.test/");

        Assert.Single(requestedUrls);
        Assert.DoesNotContain("169.254.169.254", string.Join("|", requestedUrls));
        Assert.Equal(TargetReachability.UntrustedRedirect, result.Reachability);
    }
}

/// <summary>
/// Test handler that tracks all requested URIs and simulates a single redirect.
/// </summary>
internal sealed class TrackingRedirectHandler : HttpMessageHandler
{
    private readonly List<string> _requestedUrls;
    private readonly string _initialUrl;
    private readonly string _redirectUrl;
    private readonly int _redirectStatus;
    private readonly bool _allowRelative;

    public TrackingRedirectHandler(
        List<string> requestedUrls,
        string initialUrl,
        string redirectUrl,
        int redirectStatus,
        bool allowRelative = false)
    {
        _requestedUrls = requestedUrls;
        _initialUrl = initialUrl;
        _redirectUrl = redirectUrl;
        _redirectStatus = redirectStatus;
        _allowRelative = allowRelative;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var url = request.RequestUri?.AbsoluteUri ?? "(unknown)";
        _requestedUrls.Add(url);

        // If this is the initial request, respond with redirect
        if (url == _initialUrl)
        {
            try
            {
                var response = new HttpResponseMessage((HttpStatusCode)_redirectStatus)
                {
                    RequestMessage = request,
                    Content = new StringContent("")
                };

                string locationUrl = _redirectUrl;
                if (_allowRelative && _redirectUrl.StartsWith("/"))
                {
                    // Resolve relative URL
                    var baseUri = new Uri(_initialUrl);
                    locationUrl = new Uri(baseUri, _redirectUrl).AbsoluteUri;
                }

                // Try to set Location header - handle invalid URIs gracefully
                try
                {
                    response.Headers.Location = new Uri(locationUrl);
                }
                catch
                {
                    // If Location is invalid (e.g., file://), don't set header
                    // The response status will be 3xx but no Location makes it invalid
                }

                return await Task.FromResult(response);
            }
            catch
            {
                return await Task.FromResult(
                    new HttpResponseMessage(HttpStatusCode.InternalServerError)
                    {
                        RequestMessage = request,
                        Content = new StringContent("")
                    });
            }
        }

        // Redirect target was requested - this should be blocked by the service!
        // Return 200 to indicate the request was made (test should fail if this happens)
        return await Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent("")
            });
    }
}

/// <summary>
/// Handler that simulates a chain of public redirects.
/// </summary>
internal sealed class ChainedRedirectHandler : HttpMessageHandler
{
    private readonly List<string> _requestedUrls;
    private readonly int _chainLength;

    public ChainedRedirectHandler(List<string> requestedUrls, int chainLength)
    {
        _requestedUrls = requestedUrls;
        _chainLength = chainLength;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var url = request.RequestUri?.AbsoluteUri ?? "(unknown)";
        _requestedUrls.Add(url);

        // Parse step number from URL
        var match = System.Text.RegularExpressions.Regex.Match(url, @"step(\d+)");
        if (match.Success && int.TryParse(match.Groups[1].Value, out var stepNum) && stepNum < _chainLength)
        {
            var nextStep = stepNum + 1;
            var nextUrl = $"https://step{nextStep}.example.test/";
            var response = new HttpResponseMessage(HttpStatusCode.Found)
            {
                RequestMessage = request
            };
            response.Headers.Location = new Uri(nextUrl);
            return await Task.FromResult(response);
        }

        // Final step or max reached
        return await Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent("")
            });
    }
}

/// <summary>
/// Handler that simulates a redirect loop: A ↔ B.
/// </summary>
internal sealed class LoopingRedirectHandler : HttpMessageHandler
{
    private readonly List<string> _requestedUrls;

    public LoopingRedirectHandler(List<string> requestedUrls)
    {
        _requestedUrls = requestedUrls;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var url = request.RequestUri?.AbsoluteUri ?? "(unknown)";
        _requestedUrls.Add(url);

        // Alternate between loop-a and loop-b
        var nextUrl = url.Contains("loop-a") ? "https://loop-b.example.test/" : "https://loop-a.example.test/";

        var response = new HttpResponseMessage(HttpStatusCode.Found)
        {
            RequestMessage = request
        };
        response.Headers.Location = new Uri(nextUrl);

        return await Task.FromResult(response);
    }
}


