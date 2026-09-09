using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;
using Xunit;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// Tests for new profile creation endpoint defaults.
/// Verifies that newly created environments do NOT inherit sample endpoint values.
/// </summary>
public sealed class NewProfileEndpointDefaultsTests
{
    private readonly FrontendAnalysisSettingsService _sut = new();

    [Fact]
    public void CreateProfile_NewEnvironment_RestBaseUrlIsNull()
    {
        var profile = _sut.CreateProfile("M2LB DEV", FrontendEnvironmentType.Development);

        profile.RestBaseUrl.Should().BeNull();
    }

    [Fact]
    public void CreateProfile_NewEnvironment_GraphQlEndpointIsNull()
    {
        var profile = _sut.CreateProfile("M2LB DEV", FrontendEnvironmentType.Development);

        profile.GraphQlEndpoint.Should().BeNull();
    }

    [Fact]
    public void CreateProfile_NewEnvironment_HealthEndpointIsNull()
    {
        var profile = _sut.CreateProfile("M2LB DEV", FrontendEnvironmentType.Development);

        profile.HealthEndpoint.Should().BeNull();
    }

    [Fact]
    public void CreateProfile_NewEnvironment_SwaggerUrlIsNull()
    {
        var profile = _sut.CreateProfile("M2LB DEV", FrontendEnvironmentType.Development);

        profile.SwaggerUrl.Should().BeNull();
    }

    [Fact]
    public void CreateProfile_NewEnvironment_IntegrationsIsEmpty()
    {
        var profile = _sut.CreateProfile("M2LB DEV", FrontendEnvironmentType.Development);

        profile.Integrations.Should().BeEmpty();
    }

    [Fact]
    public void CreateProfile_NewEnvironment_AuthenticationIsNone()
    {
        var profile = _sut.CreateProfile("M2LB DEV", FrontendEnvironmentType.Development);

        profile.Authentication.AuthenticationType.Should().Be(FrontendAuthenticationType.None);
    }

    [Fact]
    public void CreateProfile_NewEnvironment_AllEndpointsEmpty()
    {
        var profile = _sut.CreateProfile("Test Env", FrontendEnvironmentType.QA);

        profile.RestBaseUrl.Should().BeNull();
        profile.GraphQlEndpoint.Should().BeNull();
        profile.HealthEndpoint.Should().BeNull();
        profile.SwaggerUrl.Should().BeNull();
    }

    [Fact]
    public void DuplicateProfile_WithConfiguredEndpoints_PreservesConfiguredValues()
    {
        var original = _sut.CreateProfile("Original", FrontendEnvironmentType.QA);
        original.RestBaseUrl = "https://original-api.example.com";
        original.GraphQlEndpoint = "https://original-api.example.com/graphql";
        original.HealthEndpoint = "https://original-api.example.com/health";
        original.SwaggerUrl = "https://original-api.example.com/swagger.json";
        _sut.UpdateProfile(original);

        var duplicate = _sut.DuplicateProfile(original.Id);

        duplicate.RestBaseUrl.Should().Be("https://original-api.example.com");
        duplicate.GraphQlEndpoint.Should().Be("https://original-api.example.com/graphql");
        duplicate.HealthEndpoint.Should().Be("https://original-api.example.com/health");
        duplicate.SwaggerUrl.Should().Be("https://original-api.example.com/swagger.json");
    }

    [Fact]
    public void DuplicateProfile_FromEmpty_StaysEmpty()
    {
        var original = _sut.CreateProfile("Empty", FrontendEnvironmentType.Local);
        _sut.UpdateProfile(original);

        var duplicate = _sut.DuplicateProfile(original.Id);

        duplicate.RestBaseUrl.Should().BeNull();
        duplicate.GraphQlEndpoint.Should().BeNull();
        duplicate.HealthEndpoint.Should().BeNull();
        duplicate.SwaggerUrl.Should().BeNull();
    }
}
