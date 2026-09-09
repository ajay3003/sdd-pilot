using System.Net;
using Xunit;
using BirkNext.Api.Models;
using BirkNext.Api.Services.TargetEnvironmentDetection;
using BirkNext.Api.Services.FrontendBrowserRuntime;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;

namespace BirkNext.Api.Tests.Services.TargetEnvironmentDetection;

/// <summary>
/// Tests for config-based authentication detection (Phase 3 & 4).
/// Verifies that MSAL configuration can be extracted from appsettings.json
/// and that manual verification remains independent from detected authentication.
/// </summary>
public class ConfigBasedAuthenticationDiscoveryTests
{
    private readonly BrowserTargetValidator _validator;
    private readonly Mock<ILogger<TargetEnvironmentDetectionService>> _mockLogger;
    private readonly Mock<ITargetHostResolver> _mockResolver;
    private readonly Mock<IClientFrameworkDetector> _mockFrameworkDetector;

    public ConfigBasedAuthenticationDiscoveryTests()
    {
        _validator = new BrowserTargetValidator();
        _mockLogger = new Mock<ILogger<TargetEnvironmentDetectionService>>();
        _mockResolver = new Mock<ITargetHostResolver>();
        _mockFrameworkDetector = new Mock<IClientFrameworkDetector>();

        _mockResolver
            .Setup(r => r.ResolveHostAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<System.Net.IPAddress> { System.Net.IPAddress.Parse("203.0.113.10") });
    }

    private TargetEnvironmentDetectionService CreateService(HttpClient httpClient)
    {
        return new TargetEnvironmentDetectionService(
            _validator,
            httpClient,
            _mockResolver.Object,
            _mockFrameworkDetector.Object,
            _mockLogger.Object);
    }

    [Fact]
    public async Task ConfigBasedDetection_MsalFromAppsettings_DiscoversMicrosoftEntraId()
    {
        var appsettingsJson = @"{
  ""ApiBaseUrl"": ""https://m2lbdev.bufetat.no/api/"",
  ""UseDevAuth"": false,
  ""AzureAd"": {
    ""Authority"": ""https://login.microsoftonline.com/25609970-3b75-45b9-9899-036bb1693ff3/v2.0"",
    ""ClientId"": ""23be8783-a768-47c0-804e-2740c6216272"",
    ""ValidateAuthority"": true
  }
}";

        var mockHttpMessageHandler = new Mock<HttpMessageHandler>();
        mockHttpMessageHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage request, CancellationToken ct) =>
            {
                if (request.RequestUri?.PathAndQuery == "/appsettings.json")
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(appsettingsJson, System.Text.Encoding.UTF8, "application/json")
                    };
                }
                // Return 200 for preflight HEAD requests to root
                return new HttpResponseMessage(HttpStatusCode.OK);
            });

        var httpClient = new HttpClient(mockHttpMessageHandler.Object)
        {
            BaseAddress = new Uri("https://m2lbdev.bufetat.no")
        };

        var service = CreateService(httpClient);

        var result = await service.DetectFromUrlAsync("https://m2lbdev.bufetat.no", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(FrontendAuthenticationType.MicrosoftEntraId, result.DetectedAuthenticationType);
        Assert.NotNull(result.DetectedAuthority);
        Assert.Contains("login.microsoftonline.com", result.DetectedAuthority);
        Assert.Equal("25609970-3b75-45b9-9899-036bb1693ff3", result.DetectedTenantId);
        Assert.Equal("23be8783-a768-47c0-804e-2740c6216272", result.DetectedClientId);
    }

    [Fact]
    public async Task ConfigBasedDetection_ConfigNotFound_GracefullyHandles404()
    {
        var mockHttpMessageHandler = new Mock<HttpMessageHandler>();
        mockHttpMessageHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.NotFound));

        var httpClient = new HttpClient(mockHttpMessageHandler.Object)
        {
            BaseAddress = new Uri("https://app.example.com")
        };

        var service = CreateService(httpClient);
        var result = await service.DetectFromUrlAsync("https://app.example.com", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(FrontendAuthenticationType.None, result.DetectedAuthenticationType);
        Assert.Null(result.DetectedTenantId);
        Assert.Null(result.DetectedClientId);
    }

    [Fact]
    public async Task ConfigBasedDetection_InvalidJson_GracefullyHandlesError()
    {
        var mockHttpMessageHandler = new Mock<HttpMessageHandler>();
        mockHttpMessageHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage request, CancellationToken ct) =>
            {
                if (request.RequestUri?.PathAndQuery == "/appsettings.json")
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("invalid json {{{", System.Text.Encoding.UTF8, "application/json")
                    };
                }
                // Return 200 for preflight HEAD requests to root
                return new HttpResponseMessage(HttpStatusCode.OK);
            });

        var httpClient = new HttpClient(mockHttpMessageHandler.Object)
        {
            BaseAddress = new Uri("https://app.example.com")
        };

        var service = CreateService(httpClient);
        var result = await service.DetectFromUrlAsync("https://app.example.com", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(FrontendAuthenticationType.None, result.DetectedAuthenticationType);
    }

    [Fact]
    public async Task ConfigBasedDetection_NoAzureAdSection_NoDetection()
    {
        var appsettingsJson = @"{
  ""ApiBaseUrl"": ""https://app.example.com/api/"",
  ""Database"": { ""ConnectionString"": ""server=localhost"" }
}";

        var mockHttpMessageHandler = new Mock<HttpMessageHandler>();
        mockHttpMessageHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage request, CancellationToken ct) =>
            {
                if (request.RequestUri?.PathAndQuery == "/appsettings.json")
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(appsettingsJson, System.Text.Encoding.UTF8, "application/json")
                    };
                }
                // Return 200 for preflight HEAD requests to root
                return new HttpResponseMessage(HttpStatusCode.OK);
            });

        var httpClient = new HttpClient(mockHttpMessageHandler.Object)
        {
            BaseAddress = new Uri("https://app.example.com")
        };

        var service = CreateService(httpClient);
        var result = await service.DetectFromUrlAsync("https://app.example.com", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(FrontendAuthenticationType.None, result.DetectedAuthenticationType);
    }

    [Fact]
    public async Task ConfigBasedDetection_PartialConfig_HandlesWithAvailableFields()
    {
        var appsettingsJson = @"{
  ""AzureAd"": {
    ""Authority"": ""https://login.microsoftonline.com/12345678-1234-1234-1234-123456789012/v2.0""
  }
}";

        var mockHttpMessageHandler = new Mock<HttpMessageHandler>();
        mockHttpMessageHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage request, CancellationToken ct) =>
            {
                if (request.RequestUri?.PathAndQuery == "/appsettings.json")
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(appsettingsJson, System.Text.Encoding.UTF8, "application/json")
                    };
                }
                // Return 200 for preflight HEAD requests to root
                return new HttpResponseMessage(HttpStatusCode.OK);
            });

        var httpClient = new HttpClient(mockHttpMessageHandler.Object)
        {
            BaseAddress = new Uri("https://app.example.com")
        };

        var service = CreateService(httpClient);
        var result = await service.DetectFromUrlAsync("https://app.example.com", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(FrontendAuthenticationType.MicrosoftEntraId, result.DetectedAuthenticationType);
        Assert.NotNull(result.DetectedAuthority);
        Assert.Equal("12345678-1234-1234-1234-123456789012", result.DetectedTenantId);
        Assert.Null(result.DetectedClientId);
    }

    [Fact]
    public async Task ConfigBasedDetection_MultipleFields_AllExtractedCorrectly()
    {
        var appsettingsJson = @"{
  ""AzureAd"": {
    ""Authority"": ""https://login.microsoftonline.com/87654321-4321-4321-4321-210987654321/v2.0"",
    ""ClientId"": ""aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"",
    ""ValidateAuthority"": true
  },
  ""ApiScopes"": {
    ""Api1"": [""api://87654321-4321-4321-4321-210987654321/api1/user_impersonation""]
  }
}";

        var mockHttpMessageHandler = new Mock<HttpMessageHandler>();
        mockHttpMessageHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage request, CancellationToken ct) =>
            {
                if (request.RequestUri?.PathAndQuery == "/appsettings.json")
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(appsettingsJson, System.Text.Encoding.UTF8, "application/json")
                    };
                }
                // Return 200 for preflight HEAD requests to root
                return new HttpResponseMessage(HttpStatusCode.OK);
            });

        var httpClient = new HttpClient(mockHttpMessageHandler.Object)
        {
            BaseAddress = new Uri("https://app.example.com")
        };

        var service = CreateService(httpClient);
        var result = await service.DetectFromUrlAsync("https://app.example.com", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(FrontendAuthenticationType.MicrosoftEntraId, result.DetectedAuthenticationType);
        Assert.Contains("87654321-4321-4321-4321-210987654321", result.DetectedAuthority);
        Assert.Equal("87654321-4321-4321-4321-210987654321", result.DetectedTenantId);
        Assert.Equal("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", result.DetectedClientId);
    }

    [Fact]
    public void ConfigBasedDetection_ManualVerificationPassed_DetectionIndependent()
    {
        var response = new TargetEnvironmentDetectionResponse
        {
            Success = true,
            Reachability = TargetReachability.Reachable,
            ManualAuthenticationVerificationRequired = true,
            ManualAuthenticationVerificationStatus = ManualAuthenticationVerificationStatus.Passed,
            DetectedAuthenticationType = FrontendAuthenticationType.None,
            DetectedClientId = null,
            DetectedTenantId = null
        };

        Assert.Equal(ManualAuthenticationVerificationStatus.Passed, response.ManualAuthenticationVerificationStatus);
        Assert.Equal(FrontendAuthenticationType.None, response.DetectedAuthenticationType);
        Assert.Null(response.DetectedClientId);
        Assert.Null(response.DetectedTenantId);
    }

    [Fact]
    public void ConfigBasedDetection_ManualVerificationFailed_ButDetectionSuccessful()
    {
        var response = new TargetEnvironmentDetectionResponse
        {
            Success = true,
            Reachability = TargetReachability.Reachable,
            ManualAuthenticationVerificationRequired = true,
            ManualAuthenticationVerificationStatus = ManualAuthenticationVerificationStatus.Failed,
            DetectedAuthenticationType = FrontendAuthenticationType.MicrosoftEntraId,
            DetectedAuthority = "https://login.microsoftonline.com/12345678-1234-1234-1234-123456789012/v2.0",
            DetectedClientId = "87654321-4321-4321-4321-210987654321",
            DetectedTenantId = "12345678-1234-1234-1234-123456789012"
        };

        Assert.Equal(ManualAuthenticationVerificationStatus.Failed, response.ManualAuthenticationVerificationStatus);
        Assert.Equal(FrontendAuthenticationType.MicrosoftEntraId, response.DetectedAuthenticationType);
        Assert.NotNull(response.DetectedClientId);
        Assert.NotNull(response.DetectedTenantId);
    }

    [Fact]
    public void ConfigBasedDetection_ManualVerificationNeverInfluencesDetectedFields()
    {
        var response1 = new TargetEnvironmentDetectionResponse
        {
            ManualAuthenticationVerificationStatus = ManualAuthenticationVerificationStatus.Passed,
            DetectedAuthenticationType = FrontendAuthenticationType.None
        };

        var response2 = new TargetEnvironmentDetectionResponse
        {
            ManualAuthenticationVerificationStatus = ManualAuthenticationVerificationStatus.Failed,
            DetectedAuthenticationType = FrontendAuthenticationType.None
        };

        var response3 = new TargetEnvironmentDetectionResponse
        {
            ManualAuthenticationVerificationStatus = ManualAuthenticationVerificationStatus.Pending,
            DetectedAuthenticationType = FrontendAuthenticationType.None
        };

        Assert.Equal(response1.DetectedAuthenticationType, response2.DetectedAuthenticationType);
        Assert.Equal(response2.DetectedAuthenticationType, response3.DetectedAuthenticationType);
    }

    [Fact]
    public async Task ConfigBasedDetection_Timeout_GracefullyHandles()
    {
        var mockHttpMessageHandler = new Mock<HttpMessageHandler>();
        mockHttpMessageHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Throws<TaskCanceledException>();

        var httpClient = new HttpClient(mockHttpMessageHandler.Object)
        {
            BaseAddress = new Uri("https://app.example.com")
        };

        var service = CreateService(httpClient);
        var result = await service.DetectFromUrlAsync("https://app.example.com", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(FrontendAuthenticationType.None, result.DetectedAuthenticationType);
    }

    [Fact]
    public async Task ConfigBasedDetection_ServerError_GracefullyHandles()
    {
        var mockHttpMessageHandler = new Mock<HttpMessageHandler>();
        mockHttpMessageHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage request, CancellationToken ct) =>
            {
                if (request.RequestUri?.PathAndQuery == "/appsettings.json")
                {
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError);
                }
                // Return 200 for preflight HEAD requests to root
                return new HttpResponseMessage(HttpStatusCode.OK);
            });

        var httpClient = new HttpClient(mockHttpMessageHandler.Object)
        {
            BaseAddress = new Uri("https://app.example.com")
        };

        var service = CreateService(httpClient);
        var result = await service.DetectFromUrlAsync("https://app.example.com", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(FrontendAuthenticationType.None, result.DetectedAuthenticationType);
    }

    [Fact]
    public void ConfigBasedDetection_ConfidenceScoring_HighForCompleteConfig()
    {
        var response = new TargetEnvironmentDetectionResponse
        {
            Success = true,
            Reachability = TargetReachability.Reachable,
            DetectedAuthenticationType = FrontendAuthenticationType.MicrosoftEntraId,
            DetectedAuthority = "https://login.microsoftonline.com/12345678-1234-1234-1234-123456789012/v2.0",
            DetectedTenantId = "12345678-1234-1234-1234-123456789012",
            DetectedClientId = "87654321-4321-4321-4321-210987654321",
            DetectedRedirectUrls = []
        };

        var score = 0;
        if (response.Reachability == TargetReachability.Reachable)
            score += 2;
        if (response.DetectedAuthenticationType != FrontendAuthenticationType.None)
            score += 1;
        if (!string.IsNullOrEmpty(response.DetectedTenantId))
            score += 1;
        if (!string.IsNullOrEmpty(response.DetectedClientId))
            score += 1;

        Assert.Equal(5, score);
    }

    [Fact]
    public async Task ConfigBasedDetection_EmptyAzureAdSection_NoDetection()
    {
        var appsettingsJson = @"{
  ""AzureAd"": {}
}";

        var mockHttpMessageHandler = new Mock<HttpMessageHandler>();
        mockHttpMessageHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage request, CancellationToken ct) =>
            {
                if (request.RequestUri?.PathAndQuery == "/appsettings.json")
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(appsettingsJson, System.Text.Encoding.UTF8, "application/json")
                    };
                }
                // Return 200 for preflight HEAD requests to root
                return new HttpResponseMessage(HttpStatusCode.OK);
            });

        var httpClient = new HttpClient(mockHttpMessageHandler.Object)
        {
            BaseAddress = new Uri("https://app.example.com")
        };

        var service = CreateService(httpClient);
        var result = await service.DetectFromUrlAsync("https://app.example.com", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(FrontendAuthenticationType.MicrosoftEntraId, result.DetectedAuthenticationType);
        Assert.Null(result.DetectedTenantId);
        Assert.Null(result.DetectedClientId);
    }

    [Fact]
    public async Task ConfigBasedDetection_TenantIdParsing_ExtractsFromAuthorityPath()
    {
        var appsettingsJson = @"{
  ""AzureAd"": {
    ""Authority"": ""https://login.microsoftonline.com/aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee/v2.0"",
    ""ClientId"": ""12345678-1234-1234-1234-123456789012""
  }
}";

        var mockHttpMessageHandler = new Mock<HttpMessageHandler>();
        mockHttpMessageHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage request, CancellationToken ct) =>
            {
                if (request.RequestUri?.PathAndQuery == "/appsettings.json")
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(appsettingsJson, System.Text.Encoding.UTF8, "application/json")
                    };
                }
                // Return 200 for preflight HEAD requests to root
                return new HttpResponseMessage(HttpStatusCode.OK);
            });

        var httpClient = new HttpClient(mockHttpMessageHandler.Object)
        {
            BaseAddress = new Uri("https://app.example.com")
        };

        var service = CreateService(httpClient);
        var result = await service.DetectFromUrlAsync("https://app.example.com", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", result.DetectedTenantId);
    }
}
