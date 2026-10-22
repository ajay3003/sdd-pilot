using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// Engine display names live in one table and never touch persisted ids; history reads in today's names; new targets start with
/// all eight engines eligible and only the two HTTP engines required; the capabilities page groups capabilities by what they do
/// and names which review methods use them, without probing a target.
/// </summary>
public sealed class FrontendReviewEngineNamingTests
{
    [Fact]
    public void DisplayNamesAreCentral_AndIdsAreUnchanged()
    {
        Enum.GetValues<FrontendQualityEngineId>().Select(FrontendQualityActiveEngines.DisplayName).Should().Equal(
            "HTTP Security", "HTTP Performance", "Browser Runtime", "Accessibility", "Lighthouse", "Passive Web Security", "Browser Quality", "Browser Performance Quality");
        Enum.GetNames<FrontendQualityEngineId>().Should().Equal(
            "StaticSecurity", "PassivePerformance", "BrowserRuntime", "Accessibility", "Lighthouse", "PassiveSecurity", "BrowserQuality", "PerformanceQuality");
    }

    [Theory]
    [InlineData("Static Security", "HTTP Security")]
    [InlineData("Security", "HTTP Security")]
    [InlineData("Passive Performance", "HTTP Performance")]
    [InlineData("Passive Security", "Passive Web Security")]
    [InlineData("Passive Security (ZAP)", "Passive Web Security")]
    [InlineData("BirkNext Performance Quality", "Browser Performance Quality")]
    [InlineData("BrowserRuntime", "Browser Runtime")]
    [InlineData("Lighthouse", "Lighthouse")]
    [InlineData("Something else", "Something else")]
    public void LabelsStoredByEarlierReviewsReadInTodaysNames(string stored, string current) =>
        FrontendQualityActiveEngines.CurrentLabel(stored).Should().Be(current);

    [Fact]
    public void StoredOutcomesAreNamedFromTheirIdNotTheirStoredName()
    {
        var legacy = new FrontendQualityEngineOutcome { EngineId = FrontendQualityEngineId.PassivePerformance, DisplayName = "Passive Performance",
            ExecutionState = FrontendQualityEngineExecutionState.Assessed };
        FrontendQualityEngineCompatibility.Assessed([legacy]).Should().Equal("HTTP Performance");
    }

    [Fact]
    public void NewTargetSummary_EightEnabledTwoRequiredSixOptional()
    {
        var rows = FrontendReviewEnginePresentation.Rows(FrontendAnalysisFeatureToggles.Recommended(), FrontendQualityEngineRequirementSettings.Recommended());
        FrontendReviewEnginePresentation.Summary(rows).Should().Be("8 enabled · 2 required · 6 optional · availability checked at review time");
        rows.Single(r => r.EngineId == FrontendQualityEngineId.PassiveSecurity).Clarification.Should().Contain("never starts an active scan");
        rows.Single(r => r.EngineId == FrontendQualityEngineId.Lighthouse).Requires.Should().Contain("Chromium/CDP");
        rows.Single(r => r.EngineId == FrontendQualityEngineId.Accessibility).Requires.Should().StartWith("Browser DOM");
    }

    [Fact]
    public void ARequiredEngineThatIsDisabledIsNeverSilentlyBypassed()
    {
        var toggles = FrontendAnalysisFeatureToggles.Recommended();
        toggles.EnableSecurityEngine = false;
        var snapshot = FrontendQualityActiveEngines.Resolve(toggles, new(), new());
        snapshot.RequiredButDisabled.Select(e => e.DisplayName).Should().Equal("HTTP Security");
    }

    private static FrontendQualityEngineStatusDto Status(FrontendQualityEngineIdDto id, bool allowed = true, bool enabled = true, bool ready = true, string? reason = null) => new()
    {
        EngineId = id, Layer1Allowed = allowed, Layer2Enabled = enabled, Available = allowed && enabled && ready,
        Layer3Readiness = new() { EngineId = id, IsAvailable = ready, StatusReason = reason },
    };

    [Fact]
    public void Capabilities_AreGroupedWithUsedBy_AndStatesComeFromTheLoadedStatus()
    {
        var report = new FrontendQualityEngineStatusReportDto
        {
            Engines =
            [
                Status(FrontendQualityEngineIdDto.BrowserRuntime),
                Status(FrontendQualityEngineIdDto.Accessibility),
                Status(FrontendQualityEngineIdDto.Lighthouse, ready: false, reason: "Node runtime not found."),
                Status(FrontendQualityEngineIdDto.PassiveSecurity, enabled: false),
            ],
        };
        var capabilities = FrontendTestingCapabilitiesPresentation.Build(report);

        capabilities.Select(c => c.Group).Distinct().Should().Equal(FrontendTestingCapabilitiesPresentation.Groups);
        capabilities.Single(c => c.Id == "browser-dom").UsedBy.Should().Equal("Browser Runtime", "Accessibility");
        capabilities.Single(c => c.Id == "http").UsedBy.Should().Equal("HTTP Security", "HTTP Performance");
        var lighthouse = capabilities.Single(c => c.Id == "lighthouse");
        (lighthouse.State, lighthouse.Reason, lighthouse.NextAction).Should().Be((FrontendTestingCapabilityState.Unavailable, "Node runtime not found.", "Refresh local capabilities"));
        capabilities.Single(c => c.Id == "zap").State.Should().Be(FrontendTestingCapabilityState.RequiresSetup);
        capabilities.Single(c => c.Id == "authenticated").State.Should().Be(FrontendTestingCapabilityState.TargetDependent, "authenticated testing is a Target Environment's own setup");
        FrontendTestingCapabilitiesPresentation.Summary(capabilities).Should().Be("3 available · 1 need setup · 1 unavailable · 2 set up per target");
        FrontendTestingCapabilitiesPresentation.NeedsSetup(capabilities).Select(c => c.Id).Should().Equal("lighthouse", "zap");
    }

    [Fact]
    public void WithoutAStatusNothingIsClaimedAvailable()
    {
        var capabilities = FrontendTestingCapabilitiesPresentation.Build(null);
        capabilities.Should().NotContain(c => c.State == FrontendTestingCapabilityState.Available, "supported is never available without evidence");
        capabilities.Where(c => c.State != FrontendTestingCapabilityState.TargetDependent).Should().OnlyContain(c => c.State == FrontendTestingCapabilityState.NotDetected);
    }

    [Fact]
    public void TheNavigationUsesTheNewName_AndKeepsTheRouteKey()
    {
        DocumentationCatalog.SettingsGroups.SelectMany(g => g.Items).Should().Contain(i => i.Id == "frontend-quality-engines" && i.Label == "Frontend Testing Capabilities");
        DocumentationCatalog.SettingsSections.Should().NotContain("Frontend Engine Capabilities");
    }
}
