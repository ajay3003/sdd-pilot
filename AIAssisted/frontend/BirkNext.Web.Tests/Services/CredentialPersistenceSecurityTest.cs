using System.Text.Json;
using BirkNext.Web.Models;
using FluentAssertions;
using Xunit;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// P1 SECURITY TEST: Prove that secret credentials are NOT serialized to browser storage.
/// </summary>
public sealed class CredentialPersistenceSecurityTest
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    [Fact]
    public void CredentialSerialization_BearerTokenNotInJson()
    {
        var credentials = new TargetApiCredentials
        {
            AuthType = TargetApiAuthType.BearerToken,
            BearerToken = "SECRET-BEARER-DO-NOT-PERSIST-12345",
        };

        var json = JsonSerializer.Serialize(credentials, JsonOptions);

        // The secret MUST NOT appear in the serialized JSON
        json.Should().NotContain("SECRET-BEARER-DO-NOT-PERSIST-12345");
    }

    [Fact]
    public void CredentialSerialization_ApiKeyNotInJson()
    {
        var credentials = new TargetApiCredentials
        {
            AuthType = TargetApiAuthType.ApiKey,
            ApiKey = "SECRET-APIKEY-DO-NOT-PERSIST-67890",
            ApiKeyHeaderName = "X-API-Key",
        };

        var json = JsonSerializer.Serialize(credentials, JsonOptions);

        // The secret MUST NOT appear in the serialized JSON
        json.Should().NotContain("SECRET-APIKEY-DO-NOT-PERSIST-67890");
        // But the header name SHOULD be there
        json.Should().Contain("X-API-Key");
    }

    [Fact]
    public void CredentialSerialization_BasicPasswordNotInJson()
    {
        var credentials = new TargetApiCredentials
        {
            AuthType = TargetApiAuthType.BasicAuth,
            BasicUsername = "admin",
            BasicPassword = "SECRET-PASSWORD-DO-NOT-PERSIST-ABCDE",
        };

        var json = JsonSerializer.Serialize(credentials, JsonOptions);

        // The secret MUST NOT appear in the serialized JSON
        json.Should().NotContain("SECRET-PASSWORD-DO-NOT-PERSIST-ABCDE");
        // But the username SHOULD be there
        json.Should().Contain("admin");
    }

    [Fact]
    public void CredentialDeserialization_SecretsAreNull()
    {
        var json = """
        {"authType":"BearerToken","apiKeyHeaderName":null,"basicUsername":null,"bearerToken":"OLD-VALUE","apiKey":"OLD-VALUE","basicPassword":"OLD-VALUE"}
        """;

        var deserialized = JsonSerializer.Deserialize<TargetApiCredentials>(json, JsonOptions);

        // After deserialization from storage, secrets should be null
        // (This test will fail if the current implementation actually serializes secrets)
        deserialized!.BearerToken.Should().BeNull("Bearer token must not be deserialized from storage");
        deserialized.ApiKey.Should().BeNull("API key must not be deserialized from storage");
        deserialized.BasicPassword.Should().BeNull("Basic password must not be deserialized from storage");
    }

    [Fact]
    public void ProfileSerialization_NoSecretsInStorage()
    {
        var profile = new FrontendAnalysisProfile
        {
            Id = "p1",
            Name = "Test",
            EnvironmentType = FrontendEnvironmentType.QA,
            TargetUrl = "https://example.com",
            ApiAuth = new TargetApiCredentials
            {
                AuthType = TargetApiAuthType.BearerToken,
                BearerToken = "SECRET-BEARER-DO-NOT-PERSIST-12345",
            },
            Performance = new(),
            CoreWebVitals = new(),
            Security = new(),
            Features = new(),
        };

        var json = JsonSerializer.Serialize(profile, JsonOptions);

        // The secret MUST NOT appear anywhere in the profile serialization
        json.Should().NotContain("SECRET-BEARER-DO-NOT-PERSIST-12345");
        // But the target URL should be there
        json.Should().Contain("https://example.com");
    }
}
