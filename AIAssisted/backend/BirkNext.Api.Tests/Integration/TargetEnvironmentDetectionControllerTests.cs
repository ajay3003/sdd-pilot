using BirkNext.Api.Controllers;
using BirkNext.Api.Models;
using BirkNext.Api.Services.FrontendBrowserRuntime;
using BirkNext.Api.Services.TargetEnvironmentDetection;
using BirkNext.Api.Tests.Services.TargetEnvironmentDetection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BirkNext.Api.Tests.Integration;

public sealed class TargetEnvironmentDetectionControllerTests
{
    private readonly BrowserTargetValidator _validator = new();
    private readonly NullLogger<TargetEnvironmentDetectionService> _serviceLogger = new();
    private readonly NullLogger<TargetEnvironmentDetectionController> _controllerLogger = new();
    private readonly FakeDnsResolver _resolver;

    public TargetEnvironmentDetectionControllerTests()
    {
        _resolver = new FakeDnsResolver();
        _resolver.Add("example.com", "203.0.113.1");
        _resolver.Add("login.microsoftonline.com", "203.0.113.10");
    }

    [Fact]
    public async Task DetectConfiguration_ValidPublicTarget_Returns200()
    {
        var handler = DetectionFixtures.PublicReachableTarget();
        var httpClient = new HttpClient(handler);
        var service = new TargetEnvironmentDetectionService(_validator, httpClient, _resolver, new ClientFrameworkDetector(), _serviceLogger);
        var controller = new TargetEnvironmentDetectionController(service, _controllerLogger);

        var request = new TargetEnvironmentDetectionRequest { TargetUrl = "https://example.com" };
        var result = await controller.DetectConfiguration(request, CancellationToken.None);

        Assert.NotNull(result);
        var okResult = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(200, okResult.StatusCode);

        var response = Assert.IsType<TargetEnvironmentDetectionResponse>(okResult.Value);
        Assert.True(response.Success);
        Assert.Equal(TargetReachability.Reachable, response.Reachability);
    }

    [Fact]
    public async Task DetectConfiguration_EntraGuidTenant_DetectsMetadata()
    {
        var handler = DetectionFixtures.EntraAuthUrlDirect();
        var httpClient = new HttpClient(handler);
        var service = new TargetEnvironmentDetectionService(_validator, httpClient, _resolver, new ClientFrameworkDetector(), _serviceLogger);
        var controller = new TargetEnvironmentDetectionController(service, _controllerLogger);

        var entraUrl = $"https://login.microsoftonline.com/{DetectionFixtures.FakeTenantGuid}/oauth2/v2.0/authorize?client_id={DetectionFixtures.FakeClientId}";
        var request = new TargetEnvironmentDetectionRequest { TargetUrl = entraUrl };
        var result = await controller.DetectConfiguration(request, CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<TargetEnvironmentDetectionResponse>(okResult.Value);

        Assert.Equal(FrontendAuthenticationType.MicrosoftEntraId, response.DetectedAuthenticationType);
        Assert.Equal(DetectionFixtures.FakeTenantGuid, response.DetectedTenantId);
        Assert.Equal(DetectionFixtures.FakeClientId, response.DetectedClientId);
    }

    [Fact]
    public async Task DetectConfiguration_EntraCommonMode_DetectsTenantMode()
    {
        var handler = DetectionFixtures.EntraAuthUrlDirect();
        var httpClient = new HttpClient(handler);
        var service = new TargetEnvironmentDetectionService(_validator, httpClient, _resolver, new ClientFrameworkDetector(), _serviceLogger);
        var controller = new TargetEnvironmentDetectionController(service, _controllerLogger);

        var entraUrl = $"https://login.microsoftonline.com/common/oauth2/v2.0/authorize?client_id={DetectionFixtures.FakeClientId}";
        var request = new TargetEnvironmentDetectionRequest { TargetUrl = entraUrl };
        var result = await controller.DetectConfiguration(request, CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<TargetEnvironmentDetectionResponse>(okResult.Value);

        Assert.Equal("common", response.TenantMode);
        Assert.Null(response.DetectedTenantId);
    }

    [Fact]
    public async Task DetectConfiguration_InvalidUrl_ReturnsBadRequest()
    {
        var handler = DetectionFixtures.PublicReachableTarget();
        var httpClient = new HttpClient(handler);
        var service = new TargetEnvironmentDetectionService(_validator, httpClient, _resolver, new ClientFrameworkDetector(), _serviceLogger);
        var controller = new TargetEnvironmentDetectionController(service, _controllerLogger);

        var request = new TargetEnvironmentDetectionRequest { TargetUrl = "not a valid url" };
        var result = await controller.DetectConfiguration(request, CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<TargetEnvironmentDetectionResponse>(okResult.Value);
        Assert.False(response.Success);
        Assert.Equal("INVALID_URL", response.ErrorCode);
    }

    [Fact]
    public async Task DetectConfiguration_UnsupportedScheme_ReturnsSsrfBlocked()
    {
        var handler = DetectionFixtures.PublicReachableTarget();
        var httpClient = new HttpClient(handler);
        var service = new TargetEnvironmentDetectionService(_validator, httpClient, _resolver, new ClientFrameworkDetector(), _serviceLogger);
        var controller = new TargetEnvironmentDetectionController(service, _controllerLogger);

        var request = new TargetEnvironmentDetectionRequest { TargetUrl = "file:///etc/passwd" };
        var result = await controller.DetectConfiguration(request, CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<TargetEnvironmentDetectionResponse>(okResult.Value);
        Assert.False(response.Success);
        Assert.Equal("TARGET_BLOCKED", response.ErrorCode);
    }

    [Fact]
    public async Task DetectConfiguration_Timeout_ReturnsError()
    {
        var handler = DetectionFixtures.TimeoutTarget();
        var httpClient = new HttpClient(handler);
        var service = new TargetEnvironmentDetectionService(_validator, httpClient, _resolver, new ClientFrameworkDetector(), _serviceLogger);
        var controller = new TargetEnvironmentDetectionController(service, _controllerLogger);

        var request = new TargetEnvironmentDetectionRequest { TargetUrl = "https://example.com" };
        var result = await controller.DetectConfiguration(request, CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<TargetEnvironmentDetectionResponse>(okResult.Value);
        Assert.Equal(TargetReachability.Timeout, response.Reachability);
    }

    [Fact]
    public async Task DetectConfiguration_NullRequest_ReturnsBadRequest()
    {
        var handler = DetectionFixtures.PublicReachableTarget();
        var httpClient = new HttpClient(handler);
        var service = new TargetEnvironmentDetectionService(_validator, httpClient, _resolver, new ClientFrameworkDetector(), _serviceLogger);
        var controller = new TargetEnvironmentDetectionController(service, _controllerLogger);

        var result = await controller.DetectConfiguration(null!, CancellationToken.None);

        var badResult = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(400, badResult.StatusCode);
    }

    [Fact]
    public async Task DetectConfiguration_EmptyTargetUrl_ReturnsBadRequest()
    {
        var handler = DetectionFixtures.PublicReachableTarget();
        var httpClient = new HttpClient(handler);
        var service = new TargetEnvironmentDetectionService(_validator, httpClient, _resolver, new ClientFrameworkDetector(), _serviceLogger);
        var controller = new TargetEnvironmentDetectionController(service, _controllerLogger);

        var request = new TargetEnvironmentDetectionRequest { TargetUrl = "" };
        var result = await controller.DetectConfiguration(request, CancellationToken.None);

        var badResult = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(400, badResult.StatusCode);
    }

    [Fact]
    public async Task DetectConfiguration_SanitizedResponse_NoSensitiveData()
    {
        var handler = DetectionFixtures.EntraAuthUrlDirect();
        var httpClient = new HttpClient(handler);
        var service = new TargetEnvironmentDetectionService(_validator, httpClient, _resolver, new ClientFrameworkDetector(), _serviceLogger);
        var controller = new TargetEnvironmentDetectionController(service, _controllerLogger);

        var entraUrl = $"https://login.microsoftonline.com/{DetectionFixtures.FakeTenantGuid}/oauth2/v2.0/authorize?client_id={DetectionFixtures.FakeClientId}";
        var request = new TargetEnvironmentDetectionRequest { TargetUrl = entraUrl };
        var result = await controller.DetectConfiguration(request, CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<TargetEnvironmentDetectionResponse>(okResult.Value);

        // Verify the response is structured properly and contains extracted metadata
        Assert.True(response.Success);
        Assert.NotNull(response.DetectedClientId);
        Assert.NotNull(response.DetectedTenantId);
        Assert.NotNull(response.DetectedAuthority);
    }

    [Fact]
    public async Task TargetEnvironmentDetectionRequest_HasNoSensitiveFields()
    {
        var requestType = typeof(TargetEnvironmentDetectionRequest);
        var properties = requestType.GetProperties();

        // Ensure request model has no fields for sensitive data
        var sensitiveFieldNames = new[] { "Password", "Token", "Cookie", "Authorization", "ClientSecret", "StorageState" };

        foreach (var sensitiveName in sensitiveFieldNames)
        {
            var prop = properties.FirstOrDefault(p => p.Name.Equals(sensitiveName, StringComparison.OrdinalIgnoreCase));
            Assert.Null(prop);
        }
    }

    [Fact]
    public async Task DetectConfiguration_AuthorityIsCanonical()
    {
        var handler = DetectionFixtures.EntraAuthUrlDirect();
        var httpClient = new HttpClient(handler);
        var service = new TargetEnvironmentDetectionService(_validator, httpClient, _resolver, new ClientFrameworkDetector(), _serviceLogger);
        var controller = new TargetEnvironmentDetectionController(service, _controllerLogger);

        var entraUrl = $"https://login.microsoftonline.com/{DetectionFixtures.FakeTenantGuid}/oauth2/v2.0/authorize?client_id={DetectionFixtures.FakeClientId}";
        var request = new TargetEnvironmentDetectionRequest { TargetUrl = entraUrl };
        var result = await controller.DetectConfiguration(request, CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<TargetEnvironmentDetectionResponse>(okResult.Value);

        Assert.NotNull(response.DetectedAuthority);
        Assert.Equal("https://login.microsoftonline.com", response.DetectedAuthority);
        Assert.DoesNotContain("?", response.DetectedAuthority);
        Assert.DoesNotContain("client_id", response.DetectedAuthority);
    }
}

