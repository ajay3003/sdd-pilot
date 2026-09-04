using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

public sealed class DetectionPresentationTests
{
    [Theory]
    [InlineData("m2lbdev.bufetat.no", "M2LB DEV")]
    [InlineData("myappqa.example.no", "MYAPP QA")]
    [InlineData("customer-prod.example.no", "CUSTOMER PROD")]
    [InlineData("simplehost.example.no", "SIMPLEHOST")]
    public void ProfileName_ProducesStableReadableName(string host, string expected) =>
        DetectionPresentation.ProfileName(host).Should().Be(expected);

    [Theory]
    [InlineData(ClientFrameworkType.BlazorWebAssembly, "Blazor WebAssembly")]
    [InlineData(ClientFrameworkType.React, "React")]
    [InlineData(ClientFrameworkType.Angular, "Angular")]
    [InlineData(ClientFrameworkType.Vue, "Vue")]
    [InlineData(ClientFrameworkType.Other, "Other")]
    public void FrameworkLabel_DoesNotExposeEnumFormatting(ClientFrameworkType framework, string expected) =>
        DetectionPresentation.FrameworkLabel(framework).Should().Be(expected);
}
