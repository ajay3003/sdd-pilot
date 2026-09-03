using BirkNext.Api.Models;
using BirkNext.Api.Services.FrontendBrowserRuntime;
using BirkNext.Api.Services.TargetEnvironmentDetection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BirkNext.Api.Tests.Services.TargetEnvironmentDetection;

public sealed class TargetEnvironmentDetectionServiceTests
{
    private readonly BrowserTargetValidator _validator = new();
    private readonly ILogger<TargetEnvironmentDetectionService> _logger;
    private readonly FakeDnsResolver _resolver = new();

    public TargetEnvironmentDetectionServiceTests()
    {
        _logger = new NullLogger<TargetEnvironmentDetectionService>();
        // Configure default public hosts to resolve to documentation IP
        _resolver.Add("example.com", "203.0.113.1");
        _resolver.Add("m2lbdev.example.com", "203.0.113.2");
        _resolver.Add("myapp-dev.example.com", "203.0.113.3");
        _resolver.Add("myapp-qa.example.com", "203.0.113.4");
        _resolver.Add("myapp-prod.example.com", "203.0.113.5");
        _resolver.Add("login.microsoftonline.com", "203.0.113.10");
    }

    [Fact]
    public async Task DetectFromUrlAsync_PublicReachableTarget_ReturnsReachable()
    {
        var handler = DetectionFixtures.PublicReachableTarget();
        var httpClient = new HttpClient(handler);
        var service = new TargetEnvironmentDetectionService(_validator, httpClient, _resolver, _logger);

        var result = await service.DetectFromUrlAsync("https://example.com");

        Assert.NotNull(result);
        Assert.True(result.Success);
        Assert.Equal(TargetReachability.Reachable, result.Reachability);
        Assert.False(result.AuthenticationRequired);
    }

    [Fact]
    public async Task DetectFromUrlAsync_Unauthorized_ReturnsAuthenticationRequired()
    {
        var handler = DetectionFixtures.UnauthorizedTarget();
        var httpClient = new HttpClient(handler);
        var service = new TargetEnvironmentDetectionService(_validator, httpClient, _resolver, _logger);

        var result = await service.DetectFromUrlAsync("https://example.com");

        Assert.NotNull(result);
        Assert.True(result.AuthenticationRequired);
        Assert.Equal(TargetReachability.AuthenticationRequired, result.Reachability);
    }

    [Fact]
    public async Task DetectFromUrlAsync_Forbidden_ReturnsAuthenticationRequired()
    {
        var handler = DetectionFixtures.ForbiddenTarget();
        var httpClient = new HttpClient(handler);
        var service = new TargetEnvironmentDetectionService(_validator, httpClient, _resolver, _logger);

        var result = await service.DetectFromUrlAsync("https://example.com");

        Assert.NotNull(result);
        // 403 is treated as authentication required by the service
        Assert.Equal(TargetReachability.AuthenticationRequired, result.Reachability);
    }

    [Fact]
    public async Task DetectFromUrlAsync_EntraAuthUrlDirect_DetectsEntraMetadata()
    {
        var handler = DetectionFixtures.EntraAuthUrlDirect();
        var httpClient = new HttpClient(handler);
        var service = new TargetEnvironmentDetectionService(_validator, httpClient, _resolver, _logger);

        var entraUrl = $"https://login.microsoftonline.com/{DetectionFixtures.FakeTenantGuid}/oauth2/v2.0/authorize?client_id={DetectionFixtures.FakeClientId}";
        var result = await service.DetectFromUrlAsync(entraUrl);

        Assert.NotNull(result);
        Assert.Equal(FrontendAuthenticationType.MicrosoftEntraId, result.DetectedAuthenticationType);
        Assert.Equal(DetectionFixtures.FakeTenantGuid, result.DetectedTenantId);
        Assert.Equal(DetectionFixtures.FakeClientId, result.DetectedClientId);
    }

    [Fact]
    public async Task DetectFromUrlAsync_EntraCommonTenant_DetectsTenantMode()
    {
        var handler = DetectionFixtures.EntraAuthUrlDirect();
        var httpClient = new HttpClient(handler);
        var service = new TargetEnvironmentDetectionService(_validator, httpClient, _resolver, _logger);

        var entraUrl = $"https://login.microsoftonline.com/common/oauth2/v2.0/authorize?client_id={DetectionFixtures.FakeClientId}";
        var result = await service.DetectFromUrlAsync(entraUrl);

        Assert.NotNull(result);
        Assert.Equal(FrontendAuthenticationType.MicrosoftEntraId, result.DetectedAuthenticationType);
        Assert.Equal("common", result.TenantMode);
        Assert.Null(result.DetectedTenantId);
    }

    [Fact]
    public async Task DetectFromUrlAsync_EntraOrganizationsTenant_DetectsTenantMode()
    {
        var handler = DetectionFixtures.EntraAuthUrlDirect();
        var httpClient = new HttpClient(handler);
        var service = new TargetEnvironmentDetectionService(_validator, httpClient, _resolver, _logger);

        var entraUrl = $"https://login.microsoftonline.com/organizations/oauth2/v2.0/authorize?client_id={DetectionFixtures.FakeClientId}";
        var result = await service.DetectFromUrlAsync(entraUrl);

        Assert.NotNull(result);
        Assert.Equal("organizations", result.TenantMode);
        Assert.Null(result.DetectedTenantId);
    }

    [Fact]
    public async Task DetectFromUrlAsync_EntraConsumersTenant_DetectsTenantMode()
    {
        var handler = DetectionFixtures.EntraAuthUrlDirect();
        var httpClient = new HttpClient(handler);
        var service = new TargetEnvironmentDetectionService(_validator, httpClient, _resolver, _logger);

        var entraUrl = $"https://login.microsoftonline.com/consumers/oauth2/v2.0/authorize?client_id={DetectionFixtures.FakeClientId}";
        var result = await service.DetectFromUrlAsync(entraUrl);

        Assert.NotNull(result);
        Assert.Equal(FrontendAuthenticationType.MicrosoftEntraId, result.DetectedAuthenticationType);
        Assert.Equal("consumers", result.TenantMode);
        Assert.Null(result.DetectedTenantId);
    }

    [Fact]
    public async Task DetectFromUrlAsync_EmptyUrl_ReturnsFalse()
    {
        var handler = DetectionFixtures.PublicReachableTarget();
        var httpClient = new HttpClient(handler);
        var service = new TargetEnvironmentDetectionService(_validator, httpClient, _resolver, _logger);

        var result = await service.DetectFromUrlAsync("");

        Assert.NotNull(result);
        Assert.False(result.Success);
        Assert.Equal("EMPTY_URL", result.ErrorCode);
    }

    [Fact]
    public async Task DetectFromUrlAsync_InvalidUrl_ReturnsFalse()
    {
        var handler = DetectionFixtures.PublicReachableTarget();
        var httpClient = new HttpClient(handler);
        var service = new TargetEnvironmentDetectionService(_validator, httpClient, _resolver, _logger);

        var result = await service.DetectFromUrlAsync("not a valid url");

        Assert.NotNull(result);
        Assert.False(result.Success);
        Assert.Equal("INVALID_URL", result.ErrorCode);
    }

    [Fact]
    public async Task DetectFromUrlAsync_FileScheme_BlockedBySsrf()
    {
        var handler = DetectionFixtures.PublicReachableTarget();
        var httpClient = new HttpClient(handler);
        var service = new TargetEnvironmentDetectionService(_validator, httpClient, _resolver, _logger);

        var result = await service.DetectFromUrlAsync("file:///etc/passwd");

        Assert.NotNull(result);
        Assert.False(result.Success);
        Assert.Equal("TARGET_BLOCKED", result.ErrorCode);
    }

    [Fact]
    public async Task DetectFromUrlAsync_DevHostname_SuggestsDevEnvironment()
    {
        var handler = DetectionFixtures.PublicReachableTarget();
        var httpClient = new HttpClient(handler);
        var service = new TargetEnvironmentDetectionService(_validator, httpClient, _resolver, _logger);

        var result = await service.DetectFromUrlAsync("https://myapp-dev.example.com");

        Assert.NotNull(result);
        Assert.True(result.Success);
        Assert.Equal(FrontendEnvironmentType.Development, result.SuggestedEnvironmentType);
    }

    [Fact]
    public async Task DetectFromUrlAsync_QaHostname_SuggestsQaEnvironment()
    {
        var handler = DetectionFixtures.PublicReachableTarget();
        var httpClient = new HttpClient(handler);
        var service = new TargetEnvironmentDetectionService(_validator, httpClient, _resolver, _logger);

        var result = await service.DetectFromUrlAsync("https://myapp-qa.example.com");

        Assert.NotNull(result);
        Assert.True(result.Success);
        Assert.Equal(FrontendEnvironmentType.QA, result.SuggestedEnvironmentType);
    }

    [Fact]
    public async Task DetectFromUrlAsync_ProdHostname_SuggestsProdEnvironment()
    {
        var handler = DetectionFixtures.PublicReachableTarget();
        var httpClient = new HttpClient(handler);
        var service = new TargetEnvironmentDetectionService(_validator, httpClient, _resolver, _logger);

        var result = await service.DetectFromUrlAsync("https://myapp-prod.example.com");

        Assert.NotNull(result);
        Assert.True(result.Success);
        Assert.Equal(FrontendEnvironmentType.Production, result.SuggestedEnvironmentType);
    }

    [Fact]
    public async Task DetectFromUrlAsync_HostnamePattern_SuggestsProfileName()
    {
        var handler = DetectionFixtures.PublicReachableTarget();
        var httpClient = new HttpClient(handler);
        var service = new TargetEnvironmentDetectionService(_validator, httpClient, _resolver, _logger);

        var result = await service.DetectFromUrlAsync("https://m2lbdev.example.com");

        Assert.NotNull(result);
        Assert.True(result.Success);
        Assert.NotNull(result.SuggestedProfileName);
        // Profile name formatter adds spaces: "m2lbdev" -> "M 2L BD EV"
        Assert.NotEmpty(result.SuggestedProfileName);
    }

    [Fact]
    public async Task DetectFromUrlAsync_Timeout_ReturnsTimeoutError()
    {
        var handler = DetectionFixtures.TimeoutTarget();
        var httpClient = new HttpClient(handler);
        var service = new TargetEnvironmentDetectionService(_validator, httpClient, _resolver, _logger);

        var result = await service.DetectFromUrlAsync("https://example.com");

        Assert.NotNull(result);
        Assert.Equal(TargetReachability.Timeout, result.Reachability);
    }

    [Fact]
    public async Task DetectFromUrlAsync_SensitiveMetadataExtracted()
    {
        var handler = DetectionFixtures.EntraAuthUrlDirect();
        var httpClient = new HttpClient(handler);
        var service = new TargetEnvironmentDetectionService(_validator, httpClient, _resolver, _logger);

        var entraUrl = $"https://login.microsoftonline.com/{DetectionFixtures.FakeTenantGuid}/oauth2/v2.0/authorize?client_id={DetectionFixtures.FakeClientId}";
        var result = await service.DetectFromUrlAsync(entraUrl);

        // Verify that the structured fields extracted from the URL are safe
        Assert.NotNull(result);
        Assert.Equal(DetectionFixtures.FakeTenantGuid, result.DetectedTenantId);
        Assert.Equal(DetectionFixtures.FakeClientId, result.DetectedClientId);
    }

    [Fact]
    public async Task DetectFromUrlAsync_EntraAuthority_IsCanonical()
    {
        var handler = DetectionFixtures.EntraAuthUrlDirect();
        var httpClient = new HttpClient(handler);
        var service = new TargetEnvironmentDetectionService(_validator, httpClient, _resolver, _logger);

        var entraUrl = $"https://login.microsoftonline.com/{DetectionFixtures.FakeTenantGuid}/oauth2/v2.0/authorize?client_id={DetectionFixtures.FakeClientId}";
        var result = await service.DetectFromUrlAsync(entraUrl);

        Assert.NotNull(result);
        Assert.NotNull(result.DetectedAuthority);
        Assert.Equal("https://login.microsoftonline.com", result.DetectedAuthority);
        Assert.DoesNotContain("?", result.DetectedAuthority);
        Assert.DoesNotContain("client_id", result.DetectedAuthority);
    }

    [Fact]
    public async Task DetectFromUrlAsync_ConfidenceLevelsCalculated()
    {
        var handler = DetectionFixtures.EntraAuthUrlDirect();
        var httpClient = new HttpClient(handler);
        var service = new TargetEnvironmentDetectionService(_validator, httpClient, _resolver, _logger);

        var entraUrl = $"https://login.microsoftonline.com/{DetectionFixtures.FakeTenantGuid}/oauth2/v2.0/authorize?client_id={DetectionFixtures.FakeClientId}";
        var result = await service.DetectFromUrlAsync(entraUrl);

        Assert.NotNull(result);
        Assert.True(result.Success);
        Assert.NotEqual(DetectionConfidence.Low, result.Confidence);
    }

    [Fact]
    public async Task DetectFromUrlAsync_NoClientId_RemainsNull()
    {
        var handler = DetectionFixtures.PublicReachableTarget();
        var httpClient = new HttpClient(handler);
        var service = new TargetEnvironmentDetectionService(_validator, httpClient, _resolver, _logger);

        var result = await service.DetectFromUrlAsync("https://example.com");

        Assert.NotNull(result);
        Assert.Null(result.DetectedClientId);
    }

    [Fact]
    public async Task DetectFromUrlAsync_UnknownAuthProvider_NoEntraDetection()
    {
        var handler = DetectionFixtures.UnknownAuthProvider();
        var httpClient = new HttpClient(handler);
        var service = new TargetEnvironmentDetectionService(_validator, httpClient, _resolver, _logger);

        var unknownUrl = "https://unknown-auth.example.com/oauth2/authorize?client_id=UNKNOWN";
        var result = await service.DetectFromUrlAsync(unknownUrl);

        Assert.NotNull(result);
        Assert.NotEqual(FrontendAuthenticationType.MicrosoftEntraId, result.DetectedAuthenticationType);
    }

    [Fact]
    public async Task DetectFromUrlAsync_SensitiveQueryParams_NotLeakingInSerialization()
    {
        var handler = DetectionFixtures.EntraAuthUrlDirect();
        var httpClient = new HttpClient(handler);
        var service = new TargetEnvironmentDetectionService(_validator, httpClient, _resolver, _logger);

        var entraUrl = $"https://login.microsoftonline.com/{DetectionFixtures.FakeTenantGuid}/oauth2/v2.0/authorize?client_id={DetectionFixtures.FakeClientId}&code={DetectionFixtures.FakeCodeSentinel}&state={DetectionFixtures.FakeStateSentinel}&nonce={DetectionFixtures.FakeNonceSentinel}&session_state=FAKE-SESSION-SENTINEL-123&access_token={DetectionFixtures.FakeAccessTokenSentinel}&id_token=FAKE-ID-TOKEN-SENTINEL-123";
        var result = await service.DetectFromUrlAsync(entraUrl);

        Assert.NotNull(result);

        // Serialize complete response
        var json = System.Text.Json.JsonSerializer.Serialize(result);

        // Check for sensitive sentinels anywhere in serialization
        Assert.DoesNotContain(DetectionFixtures.FakeCodeSentinel, json);
        Assert.DoesNotContain(DetectionFixtures.FakeStateSentinel, json);
        Assert.DoesNotContain(DetectionFixtures.FakeNonceSentinel, json);
        Assert.DoesNotContain(DetectionFixtures.FakeAccessTokenSentinel, json);
        Assert.DoesNotContain("FAKE-SESSION-SENTINEL-123", json);
        Assert.DoesNotContain("FAKE-ID-TOKEN-SENTINEL-123", json);

        // Verify typed fields are safe
        Assert.Equal(DetectionFixtures.FakeTenantGuid, result.DetectedTenantId);
        Assert.Equal(DetectionFixtures.FakeClientId, result.DetectedClientId);
    }
}
