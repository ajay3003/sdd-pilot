using System.Net;
using System.Text.RegularExpressions;
using BirkNext.Integrations;
using BirkNext.LocalHttpsProxy;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Component = BirkNext.Web.Components.FrontendAnalysisSettings;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// The Target Environment state model without a global manual verification: Detection (reachability), Configuration, Execution trust (backend
/// only), Runtime evidence. Detect settings never grants trust or runtime verification; a recorded manual sign-in check — Passed or Failed,
/// fresh or persisted from an older version — never changes trust, readiness or what a review may run. Generic, for any environment type.
/// </summary>
public sealed class TargetEnvironmentStateModelTests : BunitContext
{
    private const string Url = "https://app-qa.example.test/";
    private readonly FrontendAnalysisSettingsService _settings = new();
    private readonly Mock<ITargetEnvironmentDetectionApiService> _api = new();
    private readonly Mock<IIntegrationJourneysApiService> _journeys = new();

    private void Arrange(string? manualVerificationJson)
    {
        Services.AddSingleton<IFrontendAnalysisSettingsService>(_settings);
        Services.AddSingleton(_api.Object);
        Services.AddSingleton(_journeys.Object);
        Services.AddSingleton<IEndpointDiscoveryService, EndpointDiscoveryService>();
        JSInterop.Mode = JSRuntimeMode.Loose;
        var manual = manualVerificationJson is null ? "" : $",\"manualVerification\":{manualVerificationJson}";
        JSInterop.Setup<string?>("birkNextStorage.getItem", _ => true).SetResult($$"""
            {"activeProfileId":"qa","profiles":[{"id":"qa","name":"App QA","environmentType":"QA","targetUrl":"{{Url}}"{{manual}}}]}
            """);
        _journeys.Setup(x => x.PacksAsync("qa", It.IsAny<CancellationToken>())).ReturnsAsync(new ActiveEventApiResult<IReadOnlyList<IntegrationJourneyPackView>>(
            [new IntegrationJourneyPackView { EnvironmentTrusted = false, EnvironmentDetail = "Not enrolled in TargetEnvironments:Trusted." }], null, HttpStatusCode.OK));
    }

    private IRenderedComponent<Component> Open()
    {
        var cut = Render<Component>();
        cut.FindAll(".fa-profile-chip").Single(b => b.TextContent.Contains("App QA")).Click();
        cut.WaitForAssertion(() => Chip(cut, "trust").Should().Be("Not trusted"));
        return cut;
    }

    private static string Chip(IRenderedComponent<Component> cut, string dimension) => cut.Find($"[data-testid=te-state-{dimension}]").TextContent.Trim();

    private static TargetEnvironmentDetectionResult Reachable() => new()
    {
        OriginalUrl = Url, Success = true, Reachability = TargetReachability.Reachable, DetectedClientFramework = ClientFrameworkType.BlazorWebAssembly,
        State = DetectionState.ManualAuthenticationVerificationRequired, ManualAuthenticationVerificationRequired = true,
        ManualAuthenticationVerificationStatus = ManualAuthenticationVerificationStatus.Required,
    };

    [Fact]
    public void DetectReachable_SetsDetectionOnly_NeverTrustOrRuntimeVerification()
    {
        Arrange(null);
        _api.Setup(x => x.DetectFromUrlAsync(Url, default)).ReturnsAsync(Reachable);
        var cut = Open();
        Chip(cut, "detection").Should().Be("Not checked");
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Detect settings").Click();

        cut.WaitForAssertion(() => Chip(cut, "detection").Should().Be("Reachable"));
        Chip(cut, "trust").Should().Be("Not trusted", "only backend trust configuration grants trust");
        Chip(cut, "runtime").Should().Be("Not verified", "detection is not runtime verification");
        cut.FindAll("[data-testid=te-state-verification]").Should().BeEmpty();
        cut.FindAll("[data-testid=te-state] dt").Select(dt => dt.TextContent).Should().Equal("Environment state", "Configuration", "Detection", "Execution trust", "Runtime evidence");
    }

    [Fact]
    public void DetectUnreachable_IsADetectionResult_WithNoManualWorkflow()
    {
        Arrange(null);
        _api.Setup(x => x.DetectFromUrlAsync(Url, default)).ReturnsAsync(new TargetEnvironmentDetectionResult
        {
            OriginalUrl = Url, Success = false, Reachability = TargetReachability.Unreachable, State = DetectionState.Failed, Message = "Connection refused.",
        });
        var cut = Open();
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Detect settings").Click();

        cut.WaitForAssertion(() => Chip(cut, "detection").Should().Be("Unreachable"));
        cut.Markup.Should().NotContain("Mark verification").And.NotContain("sign-in check completed");
        cut.FindAll("[data-testid=detection-auth-limitation]").Should().BeEmpty("nothing about sign-in was learned");
        Chip(cut, "trust").Should().Be("Not trusted");
    }

    [Theory]
    [InlineData("Passed")]
    [InlineData("Failed")]
    public void APersistedOldVerificationRecord_ChangesNeitherTrustNorReadiness(string result)
    {
        var profile = new FrontendAnalysisProfile { Id = "qa", Name = "App QA", EnvironmentType = FrontendEnvironmentType.QA, TargetUrl = Url };
        var record = ManualAuthenticationVerificationEvidence.Record(profile, Enum.Parse<ManualAuthenticationVerificationStatus>(result));
        Arrange(System.Text.Json.JsonSerializer.Serialize(record));
        _api.Setup(x => x.DetectFromUrlAsync(Url, default)).ReturnsAsync(Reachable);
        var cut = Open();
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Detect settings").Click();

        cut.WaitForAssertion(() => Chip(cut, "detection").Should().Be("Reachable"));
        Chip(cut, "trust").Should().Be("Not trusted", "a recorded Passed never bypasses execution trust");
        Chip(cut, "runtime").Should().Be("Not verified");
        cut.Find(".fa-detection-value").TextContent.Trim().Should().Be("Target detection complete", "a recorded Failed never blocks detection readiness either");
        cut.FindAll("[role=tab]").Single(t => t.TextContent.Trim() == "Authentication").Click();
        cut.Find("[data-testid=authentication-verification-status]").TextContent.Trim()
            .Should().Be(result == "Passed" ? "Completed (recorded manually)" : "Failed (recorded manually)", "the history stays readable");
    }

    [Theory]
    [InlineData(ManualAuthenticationVerificationStatus.Failed)]
    [InlineData(ManualAuthenticationVerificationStatus.Passed)]
    [InlineData(ManualAuthenticationVerificationStatus.Required)]
    public void FrontendQualityReview_IsNotLimitedOrUnlockedByAManualRecord(ManualAuthenticationVerificationStatus status)
    {
        var context = new FrontendAnalysisContext { TargetUrl = Url }.ForReviewScope(FrontendReviewAccessScope.PublicAndAuthenticated);
        var access = new FrontendQualityTargetAccessContext
        {
            ReviewScope = FrontendReviewAccessScope.PublicAndAuthenticated, RequiresAuthentication = true, AuthenticationType = FrontendAuthenticationType.MicrosoftEntraId,
            Method = AuthenticatedTestingMethod.LocalHttpsProxy, AuthenticatedApiAvailable = true, ManualVerificationStatus = status,
        };
        var scope = FrontendQualityReviewScopes.Resolve(context, access,
            [new(FrontendQualityEngineId.StaticSecurity, "Static Security", FrontendQualityEngineRequirement.Required, FrontendQualityCapabilityState.Enabled, null, null)]);
        FrontendQualityReviewScopes.LimitationSentence(scope).Should().BeNull();
    }

    [Fact]
    public void IntegrationReviewAndExecutionCode_NeverReadTheManualRecord()
    {
        var web = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "BirkNext.Web");
        if (!Directory.Exists(web)) return; // Source-level check runs where the repository sources are present.
        var files = new[] { "Pages/IntegrationQualityReview.razor", "Services/IntegrationReviewPrerunPresentation.cs", "Components/IntegrationJourneyPanel.razor",
            "Services/TestPackPresentation.cs", "Pages/SkoleNaervaerTesting.razor", "Components/TargetEnvironmentStateSummary.razor" };
        foreach (var file in files.Select(f => Path.Combine(web, f)).Where(File.Exists))
            Regex.IsMatch(File.ReadAllText(file), @"ManualVerification(Status|Passed|Required)?\b").Should().BeFalse($"{Path.GetFileName(file)} must not gate on a manual sign-in record");
    }
}
