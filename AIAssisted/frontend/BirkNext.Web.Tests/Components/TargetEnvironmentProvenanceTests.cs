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
    private static readonly string[] Actions = ["Apply REST", "Apply GraphQL", "Apply Swagger", "Apply Health"];
    private static readonly string[] Values = ["https://m2lbdev.bufetat.no/api/", "https://m2lbdev.bufetat.no/graphql", "https://m2lbdev.bufetat.no/swagger", "https://m2lbdev.bufetat.no/health"];

    public TargetEnvironmentProvenanceTests()
    {
        Services.AddSingleton<IFrontendAnalysisSettingsService>(_settings);
        Services.AddSingleton(_detection.Object);
        Services.AddSingleton<IEndpointDiscoveryService, EndpointDiscoveryService>();
        JSInterop.SetupVoid("birkNextStorage.setDiscovery", _ => true).SetVoidResult();
        JSInterop.Setup<string?>("birkNextStorage.getDiscovery").SetResult(null);
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

    private static void OpenTab(IRenderedComponent<TargetSettings> cut, string label) =>
        cut.FindAll("[role=tab]").Single(t => t.TextContent.Trim() == label).Click();

    private IRenderedComponent<TargetSettings> Detect()
    {
        var cut = Render<TargetSettings>();
        cut.FindAll(".fa-profile-chip").Single(c => c.TextContent.Contains("Dev")).Click();
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Detect settings").Click();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Detected from target"));
        return cut;
    }

    [Fact]
    public void DetectedEndpointProposals_AreNotShownAsApplyToConfigUiOnAnyTab()
    {
        // The "Discovered API Endpoints" apply-to-config proposal UI was removed entirely. Detection still records the endpoints in the
        // result model (other engines still read RestBaseUrl/GraphQlEndpoint/etc.), but they are no longer surfaced as proposals.
        var cut = Detect();
        foreach (var tab in new[] { "Target Application", "Endpoint Discovery" })
        {
            OpenTab(cut, tab);
            cut.FindAll(".fa-endpoint-proposal").Should().BeEmpty(tab);
            cut.Markup.Should().NotContain("Discovered API Endpoints");
            foreach (var action in Actions) cut.FindAll("button").Should().NotContain(b => b.TextContent.Trim() == action, tab);
        }
        // The saved configuration model is untouched by removing the proposal UI.
        var dev = _settings.Settings.Profiles.Single(p => p.Id == "dev");
        foreach (var field in Fields)
            typeof(FrontendAnalysisProfile).GetProperty(field)!.GetValue(dev).Should().BeNull();
        _settings.Settings.ActiveProfileId.Should().Be("qa");
        JSInterop.Invocations.Should().NotContain(i => i.Identifier == "birkNextStorage.setItem");
    }

    /// <summary>
    /// The selected environment (being inspected) and the active review environment are shown
    /// explicitly and separately: the selected one in the detail header, the active one in the
    /// Active Environment card. Selecting never activates.
    /// </summary>
    [Fact]
    public void SelectedDevAndActiveQa_AreExplicitInHeaderAndActiveCard()
    {
        var cut = Render<TargetSettings>();
        cut.FindAll(".fa-profile-chip").Single(c => c.TextContent.Contains("Dev")).Click();

        cut.Find(".fa-detail-kicker").TextContent.Trim().Should().Be("Selected environment");
        cut.Find(".fa-detail-name").TextContent.Trim().Should().Be("Dev");
        cut.Find(".fa-summary-url").TextContent.Trim().Should().Be("https://m2lbdev.bufetat.no/");

        cut.Find(".fa-active-card-name").TextContent.Trim().Should().Be("QA");
        cut.Find(".fa-active-card").TextContent.Should().Contain("https://example-qa.local").And.NotContain("m2lbdev");

        var chips = cut.FindAll(".fa-profile-chip");
        chips.Single(c => c.TextContent.Contains("QA")).TextContent.Should().Contain("Active").And.NotContain("Selected");
        chips.Single(c => c.TextContent.Contains("Dev")).TextContent.Should().Contain("Selected").And.NotContain("Active");
        _settings.Settings.ActiveProfileId.Should().Be("qa");
    }

    /// <summary>When the selected environment is also the active one, both roles are shown on the same profile.</summary>
    [Fact]
    public void SameProfile_ShowsActiveAndSelectedTogether()
    {
        var cut = Render<TargetSettings>();
        _settings.Settings.ActiveProfileId = "dev";
        cut.FindAll(".fa-profile-chip").Single(c => c.TextContent.Contains("Dev")).Click();

        cut.Find(".fa-detail-name").TextContent.Trim().Should().Be("Dev");
        cut.Find(".fa-active-card-name").TextContent.Trim().Should().Be("Dev");
        var chips = cut.FindAll(".fa-profile-chip");
        chips.Single(c => c.TextContent.Contains("Dev")).TextContent.Should().Contain("Active").And.Contain("Selected");
        chips.Single(c => c.TextContent.Contains("QA")).TextContent.Should().NotContain("Active").And.NotContain("Selected");
    }

    [Fact]
    public void FrameworkEvidenceAndIdentitySummaryRemainOnTargetApplication()
    {
        // Identity/runtime characteristics (framework evidence, the detected summary) stay on Target Application; the endpoint proposals do not.
        var cut = Detect();
        cut.Find(".fa-framework-evidence").TextContent.Should().Contain("_framework/blazor.webassembly.js").And.Contain("High");
        cut.Markup.Should().Contain("Blazor WebAssembly");
        cut.FindAll(".fa-endpoint-proposal").Should().BeEmpty();
    }

    [Fact]
    public void LegacyProposalWithoutEvidence_IsNotPresentedAsConfirmed()
    {
        var cut = Render<EndpointProposal>(p => p.Add(x => x.Value, Values[1]).Add(x => x.Label, "GraphQL"));
        cut.Markup.Should().Contain("Candidate").And.Contain("Low").And.Contain("Evidence unavailable").And.NotContain("Confirmed");
    }
}
