using BirkNext.Api.Models;
using BirkNext.Api.Services.TargetEnvironmentDetection;
using FluentAssertions;

namespace BirkNext.Api.Tests.Services.TargetEnvironmentDetection;

public sealed class TargetEnvironmentTypeClassifierTests
{
    [Theory]
    [InlineData("m2lbdev.bufetat.no", FrontendEnvironmentType.Development)]
    [InlineData("localhost", FrontendEnvironmentType.Local)]
    [InlineData("127.0.0.1", FrontendEnvironmentType.Local)]
    [InlineData("myapp-qa.example.no", FrontendEnvironmentType.QA)]
    [InlineData("myapp-prod.example.no", FrontendEnvironmentType.Production)]
    public void Infer_ClassifiesKnownHostPatterns(string host, FrontendEnvironmentType expected) =>
        TargetEnvironmentTypeClassifier.Infer(host).Should().Be(expected);

    [Fact]
    public void Infer_UnknownRemoteHost_DoesNotDefaultToLocal() =>
        TargetEnvironmentTypeClassifier.Infer("customer.example.no").Should().BeNull();
}
