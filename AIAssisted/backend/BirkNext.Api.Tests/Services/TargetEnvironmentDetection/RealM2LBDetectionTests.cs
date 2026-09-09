using BirkNext.Api.Models;
using BirkNext.Api.Services.FrontendBrowserRuntime;
using BirkNext.Api.Services.TargetEnvironmentDetection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BirkNext.Api.Tests.Services.TargetEnvironmentDetection;

/// <summary>
/// Phase 3: Real M2LB Environment Detection Tests
/// Tests the TargetEnvironmentDetectionService against actual M2LB dev environment
/// Verifies: DNS resolution, SSRF validation, hostname detection, authentication detection
/// </summary>
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

    [Fact(Skip = "Real M2LB integration test - only run against live environment")]
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

        // Verify hostname is recognized
        Assert.Equal("https://m2lbdev.bufetat.no/", result.OriginalUrl);
    }

    [Fact(Skip = "Real M2LB integration test - only run against live environment")]
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

    [Fact(Skip = "Real M2LB integration test - only run against live environment")]
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

    [Fact(Skip = "Real M2LB integration test - only run against live environment")]
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

    [Fact(Skip = "Real M2LB integration test - only run against live environment")]
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
