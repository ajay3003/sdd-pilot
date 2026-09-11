using BirkNext.Api.Models;
using BirkNext.Api.Services.FrontendBrowserRuntime;
using BirkNext.Api.Services.TargetEnvironmentDetection;
using BirkNext.Api.Tests.TestInfrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BirkNext.Api.Tests.Services.TargetEnvironmentDetection;

/// <summary>
/// Live M2LB environment detection tests. They run the real <see cref="TargetEnvironmentDetectionService"/> (real DNS,
/// real HTTP) against the M2LB Dev target and therefore depend on network access and target availability.
///
/// Category <c>LiveM2LB</c>; gated by <see cref="LiveM2LBTestGate"/> (RUN_LIVE_M2LB_TESTS=true). Without the opt-in every
/// test is reported as skipped with an explicit reason; it is never a silent pass and never part of deterministic CI.
/// The equivalent deterministic behaviour (SSRF validation, hostname classification, authentication detection,
/// configuration detection) is covered by the always-on TargetEnvironmentDetection unit tests with fake HTTP/DNS.
///
/// Only unauthenticated public discovery GETs are performed - the same requests as the "Detect settings" feature.
/// No credentials, MFA, browser, CDP or MCAS interaction is involved.
/// </summary>
[Trait("Category", LiveM2LBTestGate.Category)]
public sealed class RealM2LBDetectionTests
{
    private readonly BrowserTargetValidator _validator = new();
    private readonly ILogger<TargetEnvironmentDetectionService> _logger;
    private readonly ILogger<DnsTargetHostResolver> _resolverLogger;

    public RealM2LBDetectionTests()
    {
        _logger = new NullLogger<TargetEnvironmentDetectionService>();
        _resolverLogger = new NullLogger<DnsTargetHostResolver>();
    }

    [LiveM2LBFact]
    public async Task RealM2LBDev_Reachable_DetectionSucceeds()
    {
        // Arrange
        const string m2lbUrl = "https://m2lbdev.bufetat.no/";
        var httpClient = new HttpClient();
        var resolver = new DnsTargetHostResolver(_resolverLogger); // Use real DNS
        var service = new TargetEnvironmentDetectionService(_validator, httpClient, resolver, new ClientFrameworkDetector(), _logger);

        // Act
        var result = await service.DetectFromUrlAsync(m2lbUrl, CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.Success, $"Detection failed: {result.ErrorCode} - {result.Message}");

        // Verify M2LB is detected as reachable
        Assert.True(result.Reachability == TargetReachability.Reachable || result.Reachability == TargetReachability.AuthenticationRequired,
            $"Expected Reachable or AuthenticationRequired, got {result.Reachability}");

        // Verify the target identity is recognized. OriginalUrl is the sanitized application target (scheme://host[/path], no query,
        // fragment or trailing slash for the root path) - see TargetEnvironmentDetectionService.GetNormalizedApplicationTarget.
        Assert.Equal("https://m2lbdev.bufetat.no", new Uri(result.OriginalUrl!, UriKind.Absolute).GetLeftPart(UriPartial.Authority));
        Assert.DoesNotContain("?", result.OriginalUrl);
        Assert.DoesNotContain("#", result.OriginalUrl);
    }

    [LiveM2LBFact]
    public async Task RealM2LBDev_M2LBProfileDetected()
    {
        // Arrange
        const string m2lbUrl = "https://m2lbdev.bufetat.no/";
        var httpClient = new HttpClient();
        var resolver = new DnsTargetHostResolver(_resolverLogger);
        var service = new TargetEnvironmentDetectionService(_validator, httpClient, resolver, new ClientFrameworkDetector(), _logger);

        // Act
        var result = await service.DetectFromUrlAsync(m2lbUrl, CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.Success);

        // Should detect M2LB profile from hostname pattern
        Assert.NotNull(result.SuggestedProfileName);
        Assert.Contains("M2LB", result.SuggestedProfileName, StringComparison.OrdinalIgnoreCase);
    }

    [LiveM2LBFact]
    public async Task RealM2LBDev_AuthenticationDetection()
    {
        // Arrange
        const string m2lbUrl = "https://m2lbdev.bufetat.no/";
        var httpClient = new HttpClient();
        var resolver = new DnsTargetHostResolver(_resolverLogger);
        var service = new TargetEnvironmentDetectionService(_validator, httpClient, resolver, new ClientFrameworkDetector(), _logger);

        // Act
        var result = await service.DetectFromUrlAsync(m2lbUrl, CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.Success);

        // Log authentication detection for review
        if (result.AuthenticationRequired)
        {
            Assert.NotEqual(FrontendAuthenticationType.None, result.DetectedAuthenticationType);
        }
    }

    [LiveM2LBFact]
    public async Task RealM2LBDev_NoSSRFErrors()
    {
        // Arrange
        const string m2lbUrl = "https://m2lbdev.bufetat.no/";
        var httpClient = new HttpClient();
        var resolver = new DnsTargetHostResolver(_resolverLogger);
        var service = new TargetEnvironmentDetectionService(_validator, httpClient, resolver, new ClientFrameworkDetector(), _logger);

        // Act
        var result = await service.DetectFromUrlAsync(m2lbUrl, CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.Success);

        // Verify DNS resolution succeeded (reachability should not be DnsError)
        Assert.NotEqual(TargetReachability.DnsError, result.Reachability);
    }

    [LiveM2LBFact]
    public async Task RealM2LBDev_ConfigurationDetection()
    {
        // Arrange
        const string m2lbUrl = "https://m2lbdev.bufetat.no/";
        var httpClient = new HttpClient();
        var resolver = new DnsTargetHostResolver(_resolverLogger);
        var service = new TargetEnvironmentDetectionService(_validator, httpClient, resolver, new ClientFrameworkDetector(), _logger);

        // Act
        var result = await service.DetectFromUrlAsync(m2lbUrl, CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.Success);

        // Integration/endpoint detection optional but should not error
        if (result.DetectedIntegrations.Count > 0)
        {
            Assert.NotEmpty(result.DiscoveryEvidence);
        }
    }
}
