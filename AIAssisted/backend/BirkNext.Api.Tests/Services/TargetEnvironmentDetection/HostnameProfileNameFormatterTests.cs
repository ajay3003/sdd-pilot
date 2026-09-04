using BirkNext.Api.Services.TargetEnvironmentDetection;
using FluentAssertions;

namespace BirkNext.Api.Tests.Services.TargetEnvironmentDetection;

public sealed class HostnameProfileNameFormatterTests
{
    [Theory]
    [InlineData("m2lbdev.bufetat.no", "M2LB DEV")]
    [InlineData("myappqa.example.no", "MYAPP QA")]
    [InlineData("customer-prod.example.no", "CUSTOMER PROD")]
    [InlineData("stablehost.example.no", "STABLEHOST")]
    public void Format_ReturnsReadableStableSuggestion(string host, string expected) =>
        HostnameProfileNameFormatter.Format(host).Should().Be(expected);
}
