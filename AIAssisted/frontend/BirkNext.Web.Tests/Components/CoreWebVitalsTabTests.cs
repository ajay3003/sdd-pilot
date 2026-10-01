using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Component = BirkNext.Web.Components.FrontendAnalysisSettings;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Target Environment → Core Web Vitals: configured thresholds shown as Good / Needs improvement / Poor bands that match the review's
/// comparator, the review owner, a derived Default/Custom profile, and a confirmed restore that touches only this target's CWV values.
/// </summary>
public sealed class CoreWebVitalsTabTests : BunitContext
{
    private const string Url = "https://application.example.test/";
    private readonly FrontendAnalysisSettingsService _settings = new();

    public CoreWebVitalsTabTests()
    {
        Services.AddSingleton<IFrontendAnalysisSettingsService>(_settings);
        Services.AddSingleton(Mock.Of<ITargetEnvironmentDetectionApiService>());
        Services.AddSingleton<IEndpointDiscoveryService, EndpointDiscoveryService>();
        JSInterop.SetupVoid("birkNextStorage.setItem", _ => true).SetVoidResult();
        JSInterop.SetupVoid("birkNextStorage.setDiscovery", _ => true).SetVoidResult();
        JSInterop.Setup<string?>("birkNextStorage.getDiscovery").SetResult(null);
    }

    private IRenderedComponent<Component> Open(string devCwv = "{}", string extra = "")
    {
        JSInterop.Setup<string?>("birkNextStorage.getItem", _ => true).SetResult($$$"""
        {"activeProfileId":"dev","profiles":[
          {"id":"dev","name":"Dev","environmentType":"Development","targetUrl":"{{{Url}}}","coreWebVitals":{{{devCwv}}}{{{extra}}}},
          {"id":"qa","name":"QA","environmentType":"QA","targetUrl":"https://qa.example.test/","coreWebVitals":{"lcpGoodMs":1500}}
        ]}
        """);
        var cut = Render<Component>();
        cut.FindAll(".fa-profile-chip").Single(b => b.TextContent.Contains("Dev")).Click();
        cut.FindAll("[role=tab]").Single(t => t.TextContent.Trim() == "Core Web Vitals").Click();
        return cut;
    }

    private static AngleSharp.Dom.IElement Row(IRenderedComponent<Component> cut, string metric) =>
        cut.FindAll("[data-testid=cwv-row]").Single(r => r.GetAttribute("data-metric") == metric);

    private static string Text(AngleSharp.Dom.IElement row, string id) => row.QuerySelector($"[data-testid={id}]")!.TextContent.Trim();

    [Fact]
    public void ThreeMetricsRenderWithDescriptionsAndAllThreeBands()
    {
        var cut = Open();
        cut.FindAll("[data-testid=cwv-table] thead th").Select(h => h.TextContent.Trim()).Should().Equal("Metric", "Good", "Needs improvement", "Poor");
        cut.FindAll("[data-testid=cwv-row]").Select(r => r.GetAttribute("data-metric")).Should().Equal("LCP", "INP", "CLS");
        var lcp = Row(cut, "LCP");
        lcp.QuerySelector("abbr")!.GetAttribute("title").Should().Be("Largest Contentful Paint");
        lcp.QuerySelector("th")!.TextContent.Should().Contain("Largest Contentful Paint");
        Text(lcp, "cwv-measures").Should().Be("Loading performance");
        (Text(lcp, "cwv-good"), Text(lcp, "cwv-needs-improvement"), Text(lcp, "cwv-poor")).Should().Be(("≤ 2500 ms", "> 2500 ms and ≤ 4000 ms", "> 4000 ms"));
        var inp = Row(cut, "INP");
        Text(inp, "cwv-measures").Should().Be("Interaction responsiveness");
        (Text(inp, "cwv-good"), Text(inp, "cwv-needs-improvement"), Text(inp, "cwv-poor")).Should().Be(("≤ 200 ms", "> 200 ms and ≤ 500 ms", "> 500 ms"));
        var cls = Row(cut, "CLS");
        Text(cls, "cwv-measures").Should().Be("Visual stability");
        (Text(cls, "cwv-good"), Text(cls, "cwv-needs-improvement"), Text(cls, "cwv-poor")).Should().Be(("≤ 0.1", "> 0.1 and ≤ 0.25", "> 0.25"));
        cls.TextContent.Should().NotContain("ms", "CLS is unitless");
        cls.GetAttribute("data-unit").Should().Be("unitless");
    }

    [Fact]
    public void TheOwnerAndConfigurationNatureAreStated_AndNothingLooksMeasured()
    {
        var cut = Open();
        cut.Find("[data-testid=cwv-owner-note]").TextContent.Should().Contain("Frontend Quality Review").And.Contain("BirkNext Performance Quality")
            .And.Contain("Browser Quality").And.Contain("configuration values, not current measurements");
        cut.Find("[data-testid=cwv-browser-quality-note]").TextContent.Should().Contain("Not measured — never zero");
        var tab = cut.Find("[data-testid=cwv-table]").TextContent;
        tab.Should().NotContainAny("Observed", "Measured", "Current", "Failed", "Warning", "Passed");
        cut.Markup.Should().NotContainAny("Run Lighthouse", "Launch browser", "Check capability", "Browser Companion status");
    }

    [Fact]
    public void DefaultThresholdsShowTheDefaultProfile()
    {
        var cut = Open();
        cut.Find("[data-testid=cwv-profile]").TextContent.Should().Contain("Threshold profile");
        cut.Find("[data-testid=cwv-profile-state]").TextContent.Should().Be("Default");
    }

    [Fact]
    public void OneChangedThresholdShowsCustom_NeutrallyAndWithTheCustomBand()
    {
        var cut = Open("""{"inpPoorMs":600}""");
        var state = cut.Find("[data-testid=cwv-profile-state]");
        state.TextContent.Should().Be("Custom");
        state.ClassList.Should().NotContainMatch("*warn*").And.NotContainMatch("*fail*").And.NotContainMatch("*error*");
        Text(Row(cut, "INP"), "cwv-poor").Should().Be("> 600 ms");
    }

    [Fact]
    public void RestoreNeedsConfirmation_StatesItsScope_AndCancelKeepsValues()
    {
        var cut = Open("""{"lcpGoodMs":1800}""");
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Restore default Core Web Vitals").Click();
        cut.Find("[data-testid=cwv-restore-confirm]").GetAttribute("role").Should().Be("group");
        cut.Find("[data-testid=cwv-restore-text]").TextContent.Should().Contain("LCP, INP and CLS thresholds for this Target Environment")
            .And.Contain("Performance Thresholds").And.Contain("Frontend Review Engines").And.Contain("other Target Environments").And.Contain("previous review results");
        _settings.Settings.Profiles.Single(p => p.Id == "dev").CoreWebVitals.LcpGoodMs.Should().Be(1800, "opening the confirmation changes nothing");
        cut.Find("[data-testid=cwv-restore-cancel]").Click();
        cut.FindAll("[data-testid=cwv-restore-confirm]").Should().BeEmpty();
        _settings.Settings.Profiles.Single(p => p.Id == "dev").CoreWebVitals.LcpGoodMs.Should().Be(1800);
    }

    [Fact]
    public void ConfirmedRestoreChangesOnlyThisTargetsCoreWebVitals()
    {
        var cut = Open("""{"lcpGoodMs":1800,"clsPoor":0.3}""", ",\"performance\":{\"apiResponseWarningMs\":999},\"features\":{\"enableLighthouseEngine\":false}");
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Restore default Core Web Vitals").Click();
        cut.Find("[data-testid=cwv-restore-confirm] button").Click();

        var dev = _settings.Settings.Profiles.Single(p => p.Id == "dev");
        dev.CoreWebVitals.Should().BeEquivalentTo(new CoreWebVitalsThresholds());
        dev.Performance.ApiResponseWarningMs.Should().Be(999, "Performance Thresholds are not part of the restore");
        dev.Features.EnableLighthouseEngine.Should().BeFalse("Frontend Review Engines are not part of the restore");
        _settings.Settings.Profiles.Single(p => p.Id == "qa").CoreWebVitals.LcpGoodMs.Should().Be(1500, "another Target Environment is never touched");
        cut.Find("[data-testid=cwv-profile-state]").TextContent.Should().Be("Default");
        JSInterop.Invocations.Select(i => i.Identifier).Should().OnlyContain(i => i.StartsWith("birkNextStorage."), "no capability probe or measurement");
    }

    [Fact]
    public void EditModeKeepsTheInputsWithAccessibleNamesAndSavesThroughTheDraft()
    {
        var cut = Open();
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Edit Environment").Click();
        cut.FindAll("[role=tab]").Single(t => t.TextContent.Trim() == "Core Web Vitals").Click();
        var lcpGood = cut.Find("[data-testid=cwv-input-lcp-good]");
        lcpGood.GetAttribute("aria-label").Should().Be("LCP good threshold (ms)");
        cut.Find("[data-testid=cwv-input-cls-poor]").GetAttribute("aria-label").Should().Be("CLS poor threshold (unitless)");
        lcpGood.Change("2000");
        cut.Find("[data-testid=cwv-profile-state]").TextContent.Should().Be("Custom", "the profile reflects the draft");
        _settings.Settings.Profiles.Single(p => p.Id == "dev").CoreWebVitals.LcpGoodMs.Should().Be(2500, "the change lives in the draft until Save changes");
        cut.FindAll("[data-testid=cwv-restore-confirm], [data-testid=cwv-defaults]").Should().BeEmpty("restore is a view-mode action, as before");
    }

    [Fact]
    public void TheTableIsAKeyboardReachableRegionWithRowHeaders()
    {
        var cut = Open();
        cut.Find(".fa-cwv-tablewrap").GetAttribute("tabindex").Should().Be("0");
        cut.FindAll("[data-testid=cwv-row] th").Should().OnlyContain(h => h.GetAttribute("scope") == "row");
        cut.FindAll("[data-testid=cwv-row] td").Select(d => d.GetAttribute("data-label")).Distinct().Should().Equal("Good", "Needs improvement", "Poor");
        cut.FindAll(".fa-reset-row button").Should().ContainSingle().Which.GetAttribute("type").Should().Be("button");
    }
}
