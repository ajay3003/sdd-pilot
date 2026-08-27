using BirkNext.Api.Models;
using BirkNext.Api.Services.FrontendBrowserRuntime;
using BirkNext.Api.Services.TargetEnvironmentDetection;
using Xunit;

namespace BirkNext.Api.Tests.Services.TargetEnvironmentDetection;

public sealed class TargetEnvironmentDetectionServiceTests
{
    private readonly BrowserTargetValidator _validator = new();
    private readonly ILogger<TargetEnvironmentDetectionService> _logger;

    public TargetEnvironmentDetectionServiceTests()
    {
        _logger = new NullLogger<TargetEnvironmentDetectionService>();
    }

    /// <summary>
    /// Test 1: Public reachable target
    /// </summary>
    [Fact]
    public async Task DetectFromUrlAsync_PublicReachableTarget_ReturnsReachable()
    {
        var handler = DetectionFixtures.PublicReachableTarget();
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5000/") };
        var service = new TargetEnvironmentDetectionService(_validator, httpClient, _logger);

        var result = await service.DetectFromUrlAsync("https://example.com");

        Assert.NotNull(result);
        Assert.True(result.Success);
        Assert.Equal(TargetReachability.Reachable, result.Reachability);
        Assert.False(result.AuthenticationRequired);
    }

    /// <summary>
    /// Test 2: HTTP 401 Unauthorized
    /// </summary>
    [Fact]
    public async Task DetectFromUrlAsync_Unauthorized_ReturnsAuthenticationRequired()
    {
        var handler = DetectionFixtures.UnauthorizedTarget();
        var httpClient = new HttpClient(handler);
        var service = new TargetEnvironmentDetectionService(_validator, httpClient, _logger);

        var result = await service.DetectFromUrlAsync("https://example.com");

        Assert.NotNull(result);
        Assert.True(result.AuthenticationRequired);
        Assert.Equal(TargetReachability.AuthenticationRequired, result.Reachability);
    }

    /// <summary>
    /// Test 3: HTTP 403 Forbidden
    /// </summary>
    [Fact]
    public async Task DetectFromUrlAsync_Forbidden_ReturnsUnreachable()
    {
        var handler = DetectionFixtures.ForbiddenTarget();
        var httpClient = new HttpClient(handler);
        var service = new TargetEnvironmentDetectionService(_validator, httpClient, _logger);

        var result = await service.DetectFromUrlAsync("https://example.com");

        Assert.NotNull(result);
        Assert.Equal(TargetReachability.Unreachable, result.Reachability);
    }

    /// <summary>
    /// Test 4: Microsoft Entra with concrete tenant GUID
    /// </summary>
    [Fact]
    public async Task DetectFromUrlAsync_EntraWithGuidTenant_DetectsTenantAndClientId()
    {
        var handler = DetectionFixtures.EntraWithGuidTenant();
        var httpClient = new HttpClient(handler);
        var service = new TargetEnvironmentDetectionService(_validator, httpClient, _logger);

        var result = await service.DetectFromUrlAsync("https://example.com");

        Assert.NotNull(result);
        Assert.Equal(FrontendAuthenticationType.MicrosoftEntraId, result.DetectedAuthenticationType);
        Assert.Equal(DetectionFixtures.FakeTenantGuid, result.DetectedTenantId);
        Assert.Equal(DetectionFixtures.FakeClientId, result.DetectedClientId);
        // Sensitive sentinels should NOT be in result
        Assert.DoesNotContain(DetectionFixtures.FakeStateSentinel, result.NormalizedTargetUrl ?? "");
        Assert.DoesNotContain(DetectionFixtures.FakeNonceSentinel, result.NormalizedTargetUrl ?? "");
    }

    /// <summary>
    /// Test 5: Microsoft Entra with "common" tenant
    /// </summary>
    [Fact]
    public async Task DetectFromUrlAsync_EntraWithCommonTenant_DetectsTenantMode()
    {
        var handler = DetectionFixtures.EntraWithCommonTenant();
        var httpClient = new HttpClient(handler);
        var service = new TargetEnvironmentDetectionService(_validator, httpClient, _logger);

        var result = await service.DetectFromUrlAsync("https://example.com");

        Assert.NotNull(result);
        Assert.Equal(FrontendAuthenticationType.MicrosoftEntraId, result.DetectedAuthenticationType);
        Assert.Equal("common", result.TenantMode);
        Assert.Null(result.DetectedTenantId); // Should NOT set DetectedTenantId for "common"
    }

    /// <summary>
    /// Test 6: Microsoft Entra with "organizations" tenant
    /// </summary>
    [Fact]
    public async Task DetectFromUrlAsync_EntraWithOrganizationsTenant_DetectsTenantMode()
    {
        var handler = DetectionFixtures.EntraWithOrganizationsTenant();
        var httpClient = new HttpClient(handler);
        var service = new TargetEnvironmentDetectionService(_validator, httpClient, _logger);

        var result = await service.DetectFromUrlAsync("https://example.com");

        Assert.NotNull(result);
        Assert.Equal("organizations", result.TenantMode);
        Assert.Null(result.DetectedTenantId);
    }

    /// <summary>
    /// Test 7: Empty URL
    /// </summary>
    [Fact]
    public async Task DetectFromUrlAsync_EmptyUrl_ReturnsFalse()
    {
        var handler = DetectionFixtures.PublicReachableTarget();
        var httpClient = new HttpClient(handler);
        var service = new TargetEnvironmentDetectionService(_validator, httpClient, _logger);

        var result = await service.DetectFromUrlAsync("");

        Assert.NotNull(result);
        Assert.False(result.Success);
        Assert.Equal("EMPTY_URL", result.ErrorCode);
    }

    /// <summary>
    /// Test 8: Invalid URL format
    /// </summary>
    [Fact]
    public async Task DetectFromUrlAsync_InvalidUrl_ReturnsFalse()
    {
        var handler = DetectionFixtures.PublicReachableTarget();
        var httpClient = new HttpClient(handler);
        var service = new TargetEnvironmentDetectionService(_validator, httpClient, _logger);

        var result = await service.DetectFromUrlAsync("not a valid url");

        Assert.NotNull(result);
        Assert.False(result.Success);
        Assert.Equal("INVALID_URL", result.ErrorCode);
    }

    /// <summary>
    /// Test 9: Unsupported scheme (should be blocked by SSRF validator)
    /// </summary>
    [Fact]
    public async Task DetectFromUrlAsync_FileScheme_BlockedBySsrf()
    {
        var handler = DetectionFixtures.PublicReachableTarget();
        var httpClient = new HttpClient(handler);
        var service = new TargetEnvironmentDetectionService(_validator, httpClient, _logger);

        var result = await service.DetectFromUrlAsync("file:///etc/passwd");

        Assert.NotNull(result);
        Assert.False(result.Success);
        Assert.Equal("TARGET_BLOCKED", result.ErrorCode);
    }

    /// <summary>
    /// Test 10: Environment suggestion from hostname - DEV
    /// </summary>
    [Fact]
    public async Task DetectFromUrlAsync_DevHostname_SuggestsDevEnvironment()
    {
        var handler = DetectionFixtures.PublicReachableTarget();
        var httpClient = new HttpClient(handler);
        var service = new TargetEnvironmentDetectionService(_validator, httpClient, _logger);

        var result = await service.DetectFromUrlAsync("https://myapp-dev.example.com");

        Assert.NotNull(result);
        Assert.True(result.Success);
        Assert.Equal(FrontendEnvironmentType.Development, result.SuggestedEnvironmentType);
    }

    /// <summary>
    /// Test 11: Environment suggestion from hostname - PROD
    /// </summary>
    [Fact]
    public async Task DetectFromUrlAsync_ProdHostname_SuggestsProdEnvironment()
    {
        var handler = DetectionFixtures.PublicReachableTarget();
        var httpClient = new HttpClient(handler);
        var service = new TargetEnvironmentDetectionService(_validator, httpClient, _logger);

        var result = await service.DetectFromUrlAsync("https://myapp-prod.example.com");

        Assert.NotNull(result);
        Assert.True(result.Success);
        Assert.Equal(FrontendEnvironmentType.Production, result.SuggestedEnvironmentType);
    }

    /// <summary>
    /// Test 12: Profile name suggestion from hostname
    /// </summary>
    [Fact]
    public async Task DetectFromUrlAsync_HostnamePattern_SuggestsProfileName()
    {
        var handler = DetectionFixtures.PublicReachableTarget();
        var httpClient = new HttpClient(handler);
        var service = new TargetEnvironmentDetectionService(_validator, httpClient, _logger);

        var result = await service.DetectFromUrlAsync("https://m2lbdev.example.com");

        Assert.NotNull(result);
        Assert.True(result.Success);
        Assert.NotNull(result.SuggestedProfileName);
        Assert.Contains("M2LB", result.SuggestedProfileName);
    }

    /// <summary>
    /// Test 13: Timeout handling
    /// </summary>
    [Fact]
    public async Task DetectFromUrlAsync_Timeout_ReturnsTimeoutError()
    {
        var handler = DetectionFixtures.TimeoutTarget();
        var httpClient = new HttpClient(handler);
        var service = new TargetEnvironmentDetectionService(_validator, httpClient, _logger);

        var result = await service.DetectFromUrlAsync("https://example.com");

        Assert.NotNull(result);
        Assert.False(result.Success);
        Assert.Equal("TIMEOUT", result.ErrorCode);
    }

    /// <summary>
    /// Test 14: Sensitive query parameters are NOT included in result
    /// </summary>
    [Fact]
    public async Task DetectFromUrlAsync_SensitiveMetadataNotPersisted_StateNotIncluded()
    {
        var handler = DetectionFixtures.EntraWithGuidTenant();
        var httpClient = new HttpClient(handler);
        var service = new TargetEnvironmentDetectionService(_validator, httpClient, _logger);

        var result = await service.DetectFromUrlAsync("https://example.com");

        // Serialize and check for sentinels
        var json = System.Text.Json.JsonSerializer.Serialize(result);
        Assert.DoesNotContain(DetectionFixtures.FakeStateSentinel, json);
        Assert.DoesNotContain(DetectionFixtures.FakeNonceSentinel, json);
        Assert.DoesNotContain(DetectionFixtures.FakeAccessTokenSentinel, json);
        Assert.DoesNotContain(DetectionFixtures.FakeCodeSentinel, json);
    }

    /// <summary>
    /// Test 15: Authority is canonical (no query parameters)
    /// </summary>
    [Fact]
    public async Task DetectFromUrlAsync_EntraRedirect_AuthorityIsCanonical()
    {
        var handler = DetectionFixtures.EntraWithGuidTenant();
        var httpClient = new HttpClient(handler);
        var service = new TargetEnvironmentDetectionService(_validator, httpClient, _logger);

        var result = await service.DetectFromUrlAsync("https://example.com");

        Assert.NotNull(result);
        Assert.NotNull(result.DetectedAuthority);
        Assert.Equal("https://login.microsoftonline.com", result.DetectedAuthority);
        Assert.DoesNotContain("?", result.DetectedAuthority);
        Assert.DoesNotContain("client_id", result.DetectedAuthority);
    }

    /// <summary>
    /// Test 16: Confidence levels are calculated
    /// </summary>
    [Fact]
    public async Task DetectFromUrlAsync_SuccessfulDetection_ConfidenceLevelsSet()
    {
        var handler = DetectionFixtures.EntraWithGuidTenant();
        var httpClient = new HttpClient(handler);
        var service = new TargetEnvironmentDetectionService(_validator, httpClient, _logger);

        var result = await service.DetectFromUrlAsync("https://m2lbdev.example.com");

        Assert.NotNull(result);
        Assert.True(result.Success);
        Assert.NotEqual(DetectionConfidence.Low, result.Confidence);
    }
}
