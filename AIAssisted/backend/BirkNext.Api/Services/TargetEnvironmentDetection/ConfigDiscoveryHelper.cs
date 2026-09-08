using System.Text.Json;

namespace BirkNext.Api.Services.TargetEnvironmentDetection;

/// <summary>
/// Helper for discovering endpoints and integrations from structured configuration files.
/// Safely parses JSON/structured config without exposing sensitive data.
/// </summary>
public sealed class ConfigDiscoveryHelper
{
    /// <summary>
    /// Attempts to extract a string value from a JSON element by field name.
    /// Recursively searches if needed for common config patterns.
    /// </summary>
    public string? ExtractStringField(JsonElement element, string fieldName)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        if (element.TryGetProperty(fieldName, out var prop) && prop.ValueKind == JsonValueKind.String)
            return prop.GetString();

        return null;
    }

    /// <summary>
    /// Attempts to extract the REST Base URL from common configuration patterns.
    /// Looks for: ApiBaseUrl, RestBaseUrl, BackendUrl, etc.
    /// </summary>
    public string? ExtractRestBaseUrl(JsonElement root)
    {
        var candidates = new[] { "ApiBaseUrl", "RestBaseUrl", "BackendUrl", "BaseUrl", "ApiUrl" };

        foreach (var fieldName in candidates)
        {
            var value = ExtractStringField(root, fieldName);
            if (!string.IsNullOrWhiteSpace(value) && value.StartsWith("http"))
                return value;
        }

        return null;
    }

    /// <summary>
    /// Attempts to extract GraphQL endpoint from common configuration patterns.
    /// Looks for: GraphqlEndpoint, GraphQLEndpoint, GraphQLUrl, etc.
    /// </summary>
    public string? ExtractGraphQlEndpoint(JsonElement root)
    {
        var candidates = new[] { "GraphqlEndpoint", "GraphQLEndpoint", "GraphQLUrl", "GraphqlUrl", "GraphQlEndpoint" };

        foreach (var fieldName in candidates)
        {
            var value = ExtractStringField(root, fieldName);
            if (!string.IsNullOrWhiteSpace(value) && value.StartsWith("http"))
                return value;
        }

        // Check for nested in GraphQL section
        if (root.TryGetProperty("GraphQL", out var graphQlSection) ||
            root.TryGetProperty("GraphQL", out graphQlSection))
        {
            var value = ExtractStringField(graphQlSection, "Endpoint");
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    /// <summary>
    /// Attempts to extract Swagger/OpenAPI URL from configuration.
    /// Looks for: SwaggerUrl, OpenApiUrl, OpenAPIUrl, etc.
    /// </summary>
    public string? ExtractSwaggerUrl(JsonElement root)
    {
        var candidates = new[] { "SwaggerUrl", "OpenApiUrl", "OpenAPIUrl", "SwaggerEndpoint", "OpenApiEndpoint" };

        foreach (var fieldName in candidates)
        {
            var value = ExtractStringField(root, fieldName);
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    /// <summary>
    /// Attempts to extract Health endpoint from configuration.
    /// Looks for: HealthEndpoint, HealthUrl, HealthCheckUrl, etc.
    /// </summary>
    public string? ExtractHealthEndpoint(JsonElement root)
    {
        var candidates = new[] { "HealthEndpoint", "HealthUrl", "HealthCheckUrl", "HealthCheck", "Health" };

        foreach (var fieldName in candidates)
        {
            var value = ExtractStringField(root, fieldName);
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        // Check for nested in Health section
        if (root.TryGetProperty("Health", out var healthSection))
        {
            var value = ExtractStringField(healthSection, "Endpoint");
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    /// <summary>
    /// Extracts Event Hub configuration (safe fields only: namespace, name).
    /// Never extracts SharedAccessKey or connection strings with secrets.
    /// </summary>
    public (string? Namespace, string? Name)? ExtractEventHubConfig(JsonElement root)
    {
        if (root.TryGetProperty("EventHub", out var section) || root.TryGetProperty("Eventhub", out section))
        {
            var ns = ExtractStringField(section, "Namespace");
            var name = ExtractStringField(section, "Name");

            if (!string.IsNullOrWhiteSpace(ns) || !string.IsNullOrWhiteSpace(name))
                return (ns, name);
        }

        return null;
    }

    /// <summary>
    /// Extracts Service Bus configuration (safe fields only: namespace, name).
    /// Never extracts connection strings with secrets.
    /// </summary>
    public (string? Namespace, string? Name)? ExtractServiceBusConfig(JsonElement root)
    {
        if (root.TryGetProperty("ServiceBus", out var section) || root.TryGetProperty("Servicebus", out section))
        {
            var ns = ExtractStringField(section, "Namespace");
            var name = ExtractStringField(section, "Name");

            if (!string.IsNullOrWhiteSpace(ns) || !string.IsNullOrWhiteSpace(name))
                return (ns, name);
        }

        return null;
    }

    /// <summary>
    /// Extracts Kafka broker configuration (safe fields only: broker addresses, no SASL credentials).
    /// </summary>
    public string? ExtractKafkaBrokers(JsonElement root)
    {
        if (root.TryGetProperty("Kafka", out var section))
        {
            var brokers = ExtractStringField(section, "Brokers");
            if (!string.IsNullOrWhiteSpace(brokers))
                return brokers;

            var bootstrapServers = ExtractStringField(section, "BootstrapServers");
            if (!string.IsNullOrWhiteSpace(bootstrapServers))
                return bootstrapServers;
        }

        return null;
    }

    /// <summary>
    /// Extracts RabbitMQ configuration (safe fields only: hostname, port, no credentials).
    /// </summary>
    public (string? Hostname, int? Port)? ExtractRabbitMqConfig(JsonElement root)
    {
        if (root.TryGetProperty("RabbitMq", out var section) || root.TryGetProperty("RabbitMQ", out section))
        {
            var hostname = ExtractStringField(section, "Hostname");
            var portStr = ExtractStringField(section, "Port");
            int? port = null;

            if (!string.IsNullOrWhiteSpace(portStr) && int.TryParse(portStr, out var parsedPort))
                port = parsedPort;

            if (!string.IsNullOrWhiteSpace(hostname) || port.HasValue)
                return (hostname, port);
        }

        return null;
    }

    /// <summary>
    /// Checks if a configuration looks like it's for Azure AD / Microsoft Entra.
    /// Used to confirm auth type detection aligns with config.
    /// </summary>
    public bool HasAzureAdConfig(JsonElement root)
    {
        return root.TryGetProperty("AzureAd", out _) || root.TryGetProperty("Azure", out _);
    }

    /// <summary>
    /// Safely parses a connection string and extracts only safe metadata.
    /// Never returns actual secrets/credentials.
    /// Example: "Endpoint=sb://namespace.servicebus.windows.net/;..." → "namespace"
    /// </summary>
    public string? ExtractSafeNamespaceFromConnectionString(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            return null;

        // Parse Event Hub / Service Bus connection string format
        // Endpoint=sb://NAMESPACE.servicebus.windows.net/;
        if (connectionString.Contains("Endpoint=sb://", StringComparison.OrdinalIgnoreCase))
        {
            var startIdx = connectionString.IndexOf("Endpoint=sb://", StringComparison.OrdinalIgnoreCase) + "Endpoint=sb://".Length;
            var endIdx = connectionString.IndexOf(".servicebus.windows.net", startIdx, StringComparison.OrdinalIgnoreCase);

            if (endIdx > startIdx)
            {
                return connectionString.Substring(startIdx, endIdx - startIdx);
            }
        }

        return null;
    }

    /// <summary>
    /// Checks if a string contains sensitive credential patterns.
    /// Used to filter config values before returning them.
    /// </summary>
    public bool ContainsSensitiveData(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var sensitivePatterns = new[]
        {
            "SharedAccessKey",
            "SharedAccessKeyName",
            "password",
            "passwd",
            "pwd",
            "secret",
            "token",
            "Bearer ",
            "X-API-Key",
            "ApiKey",
            "api_key",
            "Access_Key",
            "AccessKey",
            "AMQP",
            "amqp://",
            "user:",
            "username:"
        };

        foreach (var pattern in sensitivePatterns)
        {
            if (value.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
