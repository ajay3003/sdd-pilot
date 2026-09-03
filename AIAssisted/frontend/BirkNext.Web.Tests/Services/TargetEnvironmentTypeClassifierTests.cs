using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

public sealed class TargetEnvironmentTypeClassifierTests
{
    [Theory]
    [InlineData("https://localhost:5001", FrontendEnvironmentType.Local)]
    [InlineData("http://127.0.0.42:8080", FrontendEnvironmentType.Local)]
    [InlineData("http://[::1]:5000", FrontendEnvironmentType.Local)]
    [InlineData("https://application-dev.example.test", FrontendEnvironmentType.Development)]
    [InlineData("https://application-qa.example.test", FrontendEnvironmentType.QA)]
    [InlineData("https://application-prod.example.test", FrontendEnvironmentType.Production)]
    public void InferFromUrl_UsesSharedTargetHostnameRules(string url, FrontendEnvironmentType expected) =>
        TargetEnvironmentTypeClassifier.InferFromUrl(url).Should().Be(expected);

    [Fact]
    public void InferFromUrl_UnclassifiedHostname_ReturnsNull() =>
        TargetEnvironmentTypeClassifier.InferFromUrl("https://application.example.com").Should().BeNull();
}
