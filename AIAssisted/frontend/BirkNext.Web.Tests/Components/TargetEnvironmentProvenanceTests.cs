using BirkNext.Web.Components;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using TargetSettings = BirkNext.Web.Components.FrontendAnalysisSettings;

namespace BirkNext.Web.Tests.Components;

public sealed class TargetEnvironmentProvenanceTests : BunitContext
{
    private readonly FrontendAnalysisSettingsService _settings = new();
    private readonly Mock<ITargetEnvironmentDetectionApiService> _detection = new();
    private static readonly string[] Fields = ["RestBaseUrl", "GraphQlEndpoint", "SwaggerUrl", "HealthEndpoint"];
    private static readonly string[] Labels = ["REST Base URL", "GraphQL Endpoint", "Swagger / OpenAPI URL", "Health Endpoint"];
    private static readonly string[] Actions = ["Apply REST", "Apply GraphQL", "Apply Swagger", "Apply Health"];
    private static readonly string[] Values = ["https://m2lbdev.bufetat.no/api/", "https://m2lbdev.bufetat.no/graphql", "https://m2lbdev.bufetat.no/swagger", "https://m2lbdev.bufetat.no/health"];

    public TargetEnvironmentProvenanceTests()
    {
        Services.AddSingleton<IFrontendAnalysisSettingsService>(_settings);
        Services.AddSingleton(_detection.Object);
        JSInterop.Setup<string?>("birkNextStorage.getItem", _ => true).SetResult("""
        {"activeProfileId":"qa","profiles":[
          {"id":"qa","name":"QA","environmentType":"QA","targetUrl":"https://example-qa.local"},
          {"id":"dev","name":"Dev","environmentType":"Development","targetUrl":"https://m2lbdev.bufetat.no/"}
        ]}
        """);
        JSInterop.SetupVoid("birkNextStorage.setItem", _ => true).SetVoidResult();
        _detection.Setup(d => d.DetectFromUrlAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(Result());
    }

    private static TargetEnvironmentDetectionResult Result() => new()
    {
        Success = true, State = DetectionState.Complete, Reachability = TargetReachability.Reachable,
        Confidence = DetectionConfidence.VeryHigh, OriginalUrl = "https://m2lbdev.bufetat.no/",
        NormalizedTargetUrl = "https://m2lbdev.bufetat.no/",
        DetectedClientFramework = ClientFrameworkType.BlazorWebAssembly,
        FrameworkEvidence = "Public HTML reference: _framework/blazor.webassembly.js", FrameworkConfidence = DetectionConfidence.High,
        DetectedRestBaseUrl = Values[0], DetectedGraphQlEndpoint = Values[1], DetectedSwaggerUrl = Values[2], DetectedHealthEndpoint = Values[3],
        DiscoveryEvidence = Fields.Select((field, index) => new DiscoveryEvidence
        {
            TargetField = field, LocationCategory = index == 0 ? "/appsettings.json" : Values[index],
            Type = index == 0 ? DiscoveryEvidence.EvidenceType.StructuredConfig : DiscoveryEvidence.EvidenceType.ConventionalCandidate,
            Status = index == 0 ? EndpointEvidenceStatus.Observed : EndpointEvidenceStatus.Candidate,
            Confidence = index == 0 ? DetectionConfidence.VeryHigh : DetectionConfidence.Low,
            ProbeStatus = index == 0 ? EndpointProbeStatus.NotPerformed : EndpointProbeStatus.ResponseReceived,
            HttpStatus = index == 0 ? null : 200, ContentType = index == 0 ? null : "text/html"
        }).ToList()
    };

    private IRenderedComponent<TargetSettings> Detect()
    {
        var cut = Render<TargetSettings>();
        cut.FindAll(".fa-profile-chip").Single(c => c.TextContent.Contains("Dev")).Click();
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Detect settings").Click();
        cut.WaitForAssertion(() => cut.FindAll(".fa-endpoint-proposal").Should().HaveCount(4));
        return cut;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ExplicitApply_ChangesOnlyItsFieldInDraft_NoSaveOrActivation(int index)
    {
        var cut = Detect();
        foreach (var action in Actions) cut.FindAll("button").Should().Contain(b => b.TextContent.Trim() == action);
        var row = cut.Find($".fa-endpoint-proposal[data-field='{Fields[index]}']");
        row.QuerySelector("button")!.TextContent.Should().Be(Actions[index]);
        row.QuerySelector("button")!.Click();
        for (var i = 0; i < Fields.Length; i++)
        {
            var input = cut.FindAll(".form-field").Single(f => f.QuerySelector("label")?.TextContent.Trim() == Labels[i]).QuerySelector("input")!;
            (input.GetAttribute("value") ?? "").Should().Be(i == index ? Values[i] : "");
            typeof(FrontendAnalysisProfile).GetProperty(Fields[i])!.GetValue(_settings.Settings.Profiles.Single(p => p.Id == "dev")).Should().BeNull();
        }
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Save changes").HasAttribute("disabled").Should().BeFalse();
        _settings.Settings.ActiveProfileId.Should().Be("qa");
        JSInterop.Invocations.Should().NotContain(i => i.Identifier == "birkNextStorage.setItem");
    }

    [Fact]
    public void Diagnostics_SelectedDevAndActiveQa_AreExplicit()
    {
        var cut = Render<TargetSettings>();
        cut.FindAll(".fa-profile-chip").Single(c => c.TextContent.Contains("Dev")).Click();
        cut.FindAll("[role=tab]").Single(b => b.TextContent.Trim() == "Diagnostics").Click();
        var context = cut.Find(".fa-diagnostics-context").TextContent;
        context.Should().Contain("Selected environment: Dev").And.Contain("https://m2lbdev.bufetat.no/")
            .And.Contain("Active review environment: QA").And.Contain("https://example-qa.local")
            .And.Contain("Review diagnostics below use the active environment.");
        _settings.Settings.ActiveProfileId.Should().Be("qa");
    }

    [Fact]
    public void Diagnostics_SameProfile_UsesOneContextLine()
    {
        var cut = Render<TargetSettings>();
        cut.FindAll("[role=tab]").Single(b => b.TextContent.Trim() == "Diagnostics").Click();
        cut.Find(".fa-diagnostics-context").TextContent.Should().Contain("Selected environment / active review environment: QA").And.NotContain("You are inspecting");
    }

    [Fact]
    public void ProposalEvidence_IsPerField_AndFrameworkEvidenceIsVisible()
    {
        var cut = Detect();
        cut.Find("[data-field='RestBaseUrl']").TextContent.Should().Contain("Observed · VeryHigh").And.Contain("StructuredConfig").And.Contain("NotPerformed");
        foreach (var field in Fields.Skip(1))
            cut.Find($"[data-field='{field}']").TextContent.Should().Contain("Candidate · Low").And.Contain("ConventionalCandidate").And.Contain("HTTP 200").And.Contain("text/html").And.NotContain("Confirmed");
        cut.Find(".fa-framework-evidence").TextContent.Should().Contain("_framework/blazor.webassembly.js").And.Contain("High");
        cut.Markup.Should().Contain("Blazor WebAssembly").And.Contain("endpoint confidence is shown per proposal");
    }

    [Fact]
    public void LegacyProposalWithoutEvidence_IsNotPresentedAsConfirmed()
    {
        var cut = Render<EndpointProposal>(p => p.Add(x => x.Value, Values[1]).Add(x => x.Label, "GraphQL"));
        cut.Markup.Should().Contain("Candidate").And.Contain("Low").And.Contain("Evidence unavailable").And.NotContain("Confirmed");
    }
}
