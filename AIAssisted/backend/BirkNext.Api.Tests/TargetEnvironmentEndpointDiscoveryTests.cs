using System.Text.Json;
using BirkNext.Api.Models;
using BirkNext.Api.Services.TargetEnvironmentDetection;
using Xunit;

namespace BirkNext.Api.Tests;

/// <summary>
/// Comprehensive tests for endpoint and integration discovery.
/// Tests 13 scenarios covering REST, GraphQL, Swagger, Health endpoints and integrations.
/// </summary>
public class TargetEnvironmentEndpointDiscoveryTests
{
    private readonly ConfigDiscoveryHelper _configHelper = new();
    private readonly EndpointDiscoveryHelper _endpointHelper = new();

    // ────────────────────────────────────────────────────────────────────────────
    // Scenario 1: REST endpoint discovery from config
    // ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ExtractRestBaseUrl_FromApiBaseUrlField_ReturnsUrl()
    {
        var json = JsonDocument.Parse("""
            {
              "ApiBaseUrl": "https://api.example.com"
            }
            """).RootElement;

        var result = _configHelper.ExtractRestBaseUrl(json);

        Assert.Equal("https://api.example.com", result);
    }

    // ────────────────────────────────────────────────────────────────────────────
    // Scenario 2: GraphQL endpoint discovery from config
    // ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ExtractGraphQlEndpoint_FromGraphQLEndpointField_ReturnsUrl()
    {
        var json = JsonDocument.Parse("""
            {
              "GraphQLEndpoint": "https://api.example.com/graphql"
            }
            """).RootElement;

        var result = _configHelper.ExtractGraphQlEndpoint(json);

        Assert.Equal("https://api.example.com/graphql", result);
    }

    // ────────────────────────────────────────────────────────────────────────────
    // Scenario 3: Swagger endpoint discovery from config
    // ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ExtractSwaggerUrl_FromSwaggerUrlField_ReturnsUrl()
    {
        var json = JsonDocument.Parse("""
            {
              "SwaggerUrl": "https://api.example.com/swagger/v1/swagger.json"
            }
            """).RootElement;

        var result = _configHelper.ExtractSwaggerUrl(json);

        Assert.Equal("https://api.example.com/swagger/v1/swagger.json", result);
    }

    // ────────────────────────────────────────────────────────────────────────────
    // Scenario 4: Health endpoint discovery from config
    // ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ExtractHealthEndpoint_FromHealthEndpointField_ReturnsUrl()
    {
        var json = JsonDocument.Parse("""
            {
              "HealthEndpoint": "https://api.example.com/health"
            }
            """).RootElement;

        var result = _configHelper.ExtractHealthEndpoint(json);

        Assert.Equal("https://api.example.com/health", result);
    }

    // ────────────────────────────────────────────────────────────────────────────
    // Scenario 5: REST endpoint classification via probe
    // ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void IsLikelyRestEndpoint_WithApiPath_ReturnsTrue()
    {
        var path = "/api/users";

        var result = _endpointHelper.IsLikelyRestEndpoint(path);

        Assert.True(result);
    }

    // ────────────────────────────────────────────────────────────────────────────
    // Scenario 6: GraphQL endpoint classification via probe
    // ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void IsLikelyGraphQlEndpoint_WithGraphQLPath_ReturnsTrue()
    {
        var path = "/graphql";

        var result = _endpointHelper.IsLikelyGraphQlEndpoint(path);

        Assert.True(result);
    }

    // ────────────────────────────────────────────────────────────────────────────
    // Scenario 7: Swagger endpoint classification via probe
    // ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void IsLikelySwaggerEndpoint_WithSwaggerPath_ReturnsTrue()
    {
        var path = "/swagger/v1/swagger.json";

        var result = _endpointHelper.IsLikelySwaggerEndpoint(path);

        Assert.True(result);
    }

    // ────────────────────────────────────────────────────────────────────────────
    // Scenario 8: Health endpoint classification via probe
    // ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void IsLikelyHealthEndpoint_WithHealthPath_ReturnsTrue()
    {
        var path = "/health";

        var result = _endpointHelper.IsLikelyHealthEndpoint(path);

        Assert.True(result);
    }

    // ────────────────────────────────────────────────────────────────────────────
    // Scenario 9: Event Hub integration discovery from config
    // ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ExtractEventHubConfig_WithValidConfig_ReturnsNamespaceAndName()
    {
        var json = JsonDocument.Parse("""
            {
              "EventHub": {
                "Namespace": "mynamespace",
                "Name": "myeventhub"
              }
            }
            """).RootElement;

        var result = _configHelper.ExtractEventHubConfig(json);

        Assert.NotNull(result);
        Assert.Equal("mynamespace", result.Value.Namespace);
        Assert.Equal("myeventhub", result.Value.Name);
    }

    // ────────────────────────────────────────────────────────────────────────────
    // Scenario 10: Service Bus integration discovery from config
    // ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ExtractServiceBusConfig_WithValidConfig_ReturnsNamespaceAndName()
    {
        var json = JsonDocument.Parse("""
            {
              "ServiceBus": {
                "Namespace": "myservicebus",
                "Name": "myqueue"
              }
            }
            """).RootElement;

        var result = _configHelper.ExtractServiceBusConfig(json);

        Assert.NotNull(result);
        Assert.Equal("myservicebus", result.Value.Namespace);
        Assert.Equal("myqueue", result.Value.Name);
    }

    // ────────────────────────────────────────────────────────────────────────────
    // Scenario 11: Kafka integration discovery from config
    // ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ExtractKafkaBrokers_WithValidConfig_ReturnsBrokers()
    {
        var json = JsonDocument.Parse("""
            {
              "Kafka": {
                "Brokers": "broker1:9092,broker2:9092"
              }
            }
            """).RootElement;

        var result = _configHelper.ExtractKafkaBrokers(json);

        Assert.Equal("broker1:9092,broker2:9092", result);
    }

    // ────────────────────────────────────────────────────────────────────────────
    // Scenario 12: RabbitMQ integration discovery from config
    // ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ExtractRabbitMqConfig_WithValidConfig_ReturnsHostnameAndPort()
    {
        var json = JsonDocument.Parse("""
            {
              "RabbitMQ": {
                "Hostname": "rabbitmq.example.com",
                "Port": "5672"
              }
            }
            """).RootElement;

        var result = _configHelper.ExtractRabbitMqConfig(json);

        Assert.NotNull(result);
        Assert.Equal("rabbitmq.example.com", result.Value.Hostname);
        Assert.Equal(5672, result.Value.Port);
    }

    // ────────────────────────────────────────────────────────────────────────────
    // Scenario 13: Secret filtering - verify secrets are never exposed
    // ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ContainsSensitiveData_WithSharedAccessKey_ReturnsTrue()
    {
        var config = "Endpoint=sb://namespace.servicebus.windows.net/;SharedAccessKeyName=key;SharedAccessKey=secret123";

        var result = _configHelper.ContainsSensitiveData(config);

        Assert.True(result);
    }

    [Fact]
    public void ContainsSensitiveData_WithPassword_ReturnsTrue()
    {
        var config = "password=MySecurePassword123";

        var result = _configHelper.ContainsSensitiveData(config);

        Assert.True(result);
    }

    [Fact]
    public void ContainsSensitiveData_WithBearerToken_ReturnsTrue()
    {
        var config = "Authorization: Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9";

        var result = _configHelper.ContainsSensitiveData(config);

        Assert.True(result);
    }

    [Fact]
    public void ContainsSensitiveData_WithoutSecrets_ReturnsFalse()
    {
        var config = "https://api.example.com/health";

        var result = _configHelper.ContainsSensitiveData(config);

        Assert.False(result);
    }

    // ────────────────────────────────────────────────────────────────────────────
    // Additional tests for edge cases and helper methods
    // ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ExtractSafeNamespaceFromConnectionString_WithServiceBusConnectionString_ReturnsNamespace()
    {
        var connectionString = "Endpoint=sb://myNamespace.servicebus.windows.net/;SharedAccessKeyName=key;SharedAccessKey=secret";

        var result = _configHelper.ExtractSafeNamespaceFromConnectionString(connectionString);

        Assert.Equal("myNamespace", result);
    }

    [Fact]
    public void IsSafeProbeCandidate_WithHttpsUrl_ReturnsTrue()
    {
        var url = "https://api.example.com/health";

        var result = _endpointHelper.IsSafeProbeCandidate(url);

        Assert.True(result);
    }

    [Fact]
    public void IsSafeProbeCandidate_WithHttpUrl_ReturnsFalse()
    {
        var url = "http://api.example.com/health";

        var result = _endpointHelper.IsSafeProbeCandidate(url);

        Assert.False(result);
    }

    [Fact]
    public void IsSafeProbeCandidate_WithLocalhostUrl_ReturnsFalse()
    {
        var url = "https://localhost:5000/health";

        var result = _endpointHelper.IsSafeProbeCandidate(url);

        Assert.False(result);
    }

    [Fact]
    public void ConstructUrl_WithBaseAndPath_ReturnsCorrectUrl()
    {
        var baseUrl = "https://api.example.com";
        var path = "/health";

        var result = _endpointHelper.ConstructUrl(baseUrl, path);

        Assert.Equal("https://api.example.com/health", result);
    }

    [Fact]
    public void GetRestEndpointCandidates_ReturnsMultiplePaths()
    {
        var baseUrl = "https://api.example.com";

        var result = _endpointHelper.GetRestEndpointCandidates(baseUrl);

        Assert.NotEmpty(result);
        Assert.Contains("https://api.example.com/api", result);
        Assert.Contains("https://api.example.com/v1", result);
    }

    [Fact]
    public void GetGraphQlEndpointCandidates_ReturnsMultiplePaths()
    {
        var baseUrl = "https://api.example.com";

        var result = _endpointHelper.GetGraphQlEndpointCandidates(baseUrl);

        Assert.NotEmpty(result);
        Assert.Contains("https://api.example.com/graphql", result);
    }

    [Fact]
    public void GetSwaggerEndpointCandidates_ReturnsMultiplePaths()
    {
        var baseUrl = "https://api.example.com";

        var result = _endpointHelper.GetSwaggerEndpointCandidates(baseUrl);

        Assert.NotEmpty(result);
        Assert.Contains("https://api.example.com/swagger/v1/swagger.json", result);
    }

    [Fact]
    public void GetHealthEndpointCandidates_ReturnsMultiplePaths()
    {
        var baseUrl = "https://api.example.com";

        var result = _endpointHelper.GetHealthEndpointCandidates(baseUrl);

        Assert.NotEmpty(result);
        Assert.Contains("https://api.example.com/health", result);
    }

    [Fact]
    public void ExtractPath_FromUrl_ReturnsPath()
    {
        var url = "https://api.example.com/health?live";

        var result = _endpointHelper.ExtractPath(url);

        Assert.Equal("/health?live", result);
    }

    [Fact]
    public void ClassifyEndpointPath_WithHealthPath_ReturnsHealth()
    {
        var path = "/health";

        var result = _endpointHelper.ClassifyEndpointPath(path);

        Assert.Equal("Health", result);
    }

    [Fact]
    public void ClassifyEndpointPath_WithSwaggerPath_ReturnsSwagger()
    {
        var path = "/swagger/v1/swagger.json";

        var result = _endpointHelper.ClassifyEndpointPath(path);

        Assert.Equal("Swagger", result);
    }

    [Fact]
    public void ClassifyEndpointPath_WithGraphQLPath_ReturnsGraphQL()
    {
        var path = "/graphql";

        var result = _endpointHelper.ClassifyEndpointPath(path);

        Assert.Equal("GraphQL", result);
    }

    [Fact]
    public void ClassifyEndpointPath_WithRestPath_ReturnsREST()
    {
        var path = "/api/v1/users";

        var result = _endpointHelper.ClassifyEndpointPath(path);

        Assert.Equal("REST", result);
    }
}
