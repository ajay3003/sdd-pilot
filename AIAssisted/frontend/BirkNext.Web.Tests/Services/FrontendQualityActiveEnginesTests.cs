using System.Text.Json;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// One authoritative active-engine rule: Active = saved Enabled toggle && per-review selection. Policy (Required/Optional) is
/// separate. Capability, readiness, catalog membership and adapter registration never make an engine active.
/// </summary>
public sealed class FrontendQualityActiveEnginesTests
{
    private static FrontendAnalysisProfile Profile(string id = "dev", Action<FrontendAnalysisFeatureToggles>? toggles = null, Action<ReviewEngineSelection>? selection = null, Action<FrontendQualityEngineRequirementSettings>? policy = null)
    {
        var profile = new FrontendAnalysisProfile { Id = id, Name = id.ToUpperInvariant(), TargetUrl = $"https://{id}.example.test/" };
        toggles?.Invoke(profile.Features);
        selection?.Invoke(profile.ReviewEngineSelection);
        policy?.Invoke(profile.EngineRequirements);
        return profile;
    }

    private static FrontendAnalysisContext Context(FrontendAnalysisProfile profile) => new()
    {
        ActiveProfile = profile, TargetUrl = profile.TargetUrl!, FeatureToggles = profile.Features,
        EngineRequirements = profile.EngineRequirements, ReviewEngineSelection = profile.ReviewEngineSelection,
    };

    [Fact]
    public void Defaults_EveryEngineEnabledExceptBrowserRuntime()
    {
        var snapshot = FrontendQualityActiveEngines.Resolve(Context(Profile()));

        snapshot.Engines.Should().HaveCount(7);
        snapshot.ActiveCount.Should().Be(5);
        snapshot.RequiredActiveCount.Should().Be(2);
        snapshot.OptionalActiveCount.Should().Be(3);
        snapshot.DisabledCount.Should().Be(2);
        snapshot.Active.Select(e => e.EngineId).Should().BeEquivalentTo([
            FrontendQualityEngineId.StaticSecurity, FrontendQualityEngineId.PassivePerformance,
            FrontendQualityEngineId.Accessibility, FrontendQualityEngineId.Lighthouse, FrontendQualityEngineId.PassiveSecurity]);
        snapshot.Inactive.Select(e => e.EngineId).Should().Equal(FrontendQualityEngineId.BrowserRuntime, FrontendQualityEngineId.BrowserQuality);
        snapshot.Get(FrontendQualityEngineId.BrowserRuntime)!.Enabled.Should().BeFalse("Browser Runtime is opt-in");
        snapshot.Get(FrontendQualityEngineId.BrowserQuality)!.Enabled.Should().BeFalse("Browser Quality (Browser Companion) is opt-in until the extension is paired");
        snapshot.RequiredButDisabled.Should().BeEmpty();
        snapshot.ProfileId.Should().Be("dev");
    }

    [Fact]
    public void EnabledIsActivation_RequiredIsPolicy_TheyAreIndependent()
    {
        var profile = Profile(toggles: t => { t.EnableAccessibilityEngine = true; t.EnablePerformanceEngine = false; },
            policy: p => p.Accessibility = FrontendQualityEngineRequirement.Optional);

        var snapshot = FrontendQualityActiveEngines.Resolve(Context(profile));

        snapshot.Get(FrontendQualityEngineId.Accessibility)!.Should().Match<FrontendQualityEngineActivation>(a => a.Active && a.Policy == FrontendQualityEngineRequirement.Optional);
        var performance = snapshot.Get(FrontendQualityEngineId.PassivePerformance)!;
        performance.Active.Should().BeFalse();
        performance.Policy.Should().Be(FrontendQualityEngineRequirement.Required);
        performance.RequiredButDisabled.Should().BeTrue("a disabled required engine is a configuration inconsistency, not a silently skipped one");
        snapshot.RequiredButDisabled.Select(e => e.EngineId).Should().Equal(FrontendQualityEngineId.PassivePerformance);
    }

    [Fact]
    public void SelectionNeverActivatesADisabledEngine()
    {
        var profile = Profile(toggles: t => t.EnableLighthouseEngine = false, selection: s => { s.BrowserRuntimeSelected = true; s.LighthouseSelected = true; });

        var snapshot = FrontendQualityActiveEngines.Resolve(Context(profile));

        snapshot.IsActive(FrontendQualityEngineId.BrowserRuntime).Should().BeFalse();
        snapshot.IsActive(FrontendQualityEngineId.Lighthouse).Should().BeFalse();
        snapshot.ActiveCount.Should().Be(4);
    }

    [Fact]
    public void DeselectingAnEnabledOptionalEngine_MakesItInactiveButStillEnabled()
    {
        var profile = Profile(toggles: t => t.EnableBrowserRuntimeEngine = true, selection: s => s.BrowserRuntimeSelected = false);

        var activation = FrontendQualityActiveEngines.Resolve(Context(profile)).Get(FrontendQualityEngineId.BrowserRuntime)!;

        activation.Enabled.Should().BeTrue();
        activation.Selected.Should().BeFalse();
        activation.Active.Should().BeFalse();
    }

    [Fact]
    public void HttpEnginesAreAlwaysSelectedWhenEnabled()
    {
        var map = new Dictionary<FrontendQualityEngineIdDto, bool>();
        FrontendQualityActiveEngines.IsSelected(FrontendQualityEngineId.StaticSecurity, map).Should().BeTrue();
        FrontendQualityActiveEngines.IsSelected(FrontendQualityEngineId.PassivePerformance, map).Should().BeTrue();
        FrontendQualityActiveEngines.IsSelected(FrontendQualityEngineId.BrowserRuntime, map).Should().BeFalse("missing selection for a backend engine is not selected");
    }

    [Fact]
    public void ZeroActiveEngines_IsExplicit()
    {
        var profile = Profile(toggles: t =>
        {
            t.EnableSecurityEngine = false; t.EnablePerformanceEngine = false; t.EnableBrowserRuntimeEngine = false;
            t.EnableAccessibilityEngine = false; t.EnableLighthouseEngine = false; t.EnablePassiveSecurityEngine = false;
        });

        var snapshot = FrontendQualityActiveEngines.Resolve(Context(profile));

        snapshot.HasActiveEngines.Should().BeFalse();
        snapshot.ActiveCount.Should().Be(0);
        snapshot.RequiredButDisabled.Should().HaveCount(2);
    }

    [Fact]
    public void LegacyProfileJsonWithoutEngineData_MigratesToDeterministicDefaults()
    {
        const string legacy = """{"id":"legacy","name":"Legacy","targetUrl":"https://legacy.example.test/"}""";
        var profile = JsonSerializer.Deserialize<FrontendAnalysisProfile>(legacy)!;

        var snapshot = FrontendQualityActiveEngines.Resolve(Context(profile));

        snapshot.ActiveCount.Should().Be(5, "legacy profiles get the same deterministic defaults as new ones");
        snapshot.Get(FrontendQualityEngineId.StaticSecurity)!.Policy.Should().Be(FrontendQualityEngineRequirement.Required);
        snapshot.Get(FrontendQualityEngineId.Accessibility)!.Should().Match<FrontendQualityEngineActivation>(a => a.Active && a.Policy == FrontendQualityEngineRequirement.Optional);
        snapshot.Get(FrontendQualityEngineId.BrowserRuntime)!.Should().Match<FrontendQualityEngineActivation>(a => !a.Enabled && a.Selected && a.Policy == FrontendQualityEngineRequirement.Optional);
    }

    [Fact]
    public void EngineToggles_SurviveSettingsRoundTrip()
    {
        var settings = new FrontendAnalysisSettings { ActiveProfileId = "dev", Profiles = [Profile(toggles: t => { t.EnableAccessibilityEngine = true; t.EnableLighthouseEngine = true; t.EnablePerformanceEngine = false; })] };

        var restored = JsonSerializer.Deserialize<FrontendAnalysisSettings>(JsonSerializer.Serialize(settings))!;
        var before = FrontendQualityActiveEngines.Resolve(Context(settings.Profiles[0]));
        var after = FrontendQualityActiveEngines.Resolve(Context(restored.Profiles[0]));

        after.Engines.Select(e => (e.EngineId, e.Enabled, e.Selected, e.Policy)).Should().Equal(before.Engines.Select(e => (e.EngineId, e.Enabled, e.Selected, e.Policy)));
        after.ActiveCount.Should().Be(4, "Static Security, Accessibility, Lighthouse and Passive Security remain enabled; Passive Performance was turned off");
    }

    [Fact]
    public void ProfileSwitch_LoadsEachProfilesOwnEngineSet_NoLeakage()
    {
        var a = Profile("a", toggles: t => { t.EnableAccessibilityEngine = false; t.EnableLighthouseEngine = false; t.EnablePassiveSecurityEngine = false; });
        var b = Profile("b", toggles: t => t.EnableBrowserRuntimeEngine = true);

        FrontendQualityActiveEngines.Resolve(Context(a)).ActiveCount.Should().Be(2);
        FrontendQualityActiveEngines.Resolve(Context(b)).ActiveCount.Should().Be(6);
        FrontendQualityActiveEngines.Resolve(Context(a)).ActiveCount.Should().Be(2, "resolving B must not change A");
    }

    // ── Coverage denominators ────────────────────────────────────────────────

    private static FrontendQualityEngineOutcome Outcome(FrontendQualityEngineId id, FrontendQualityEngineRequirement policy, bool enabled, FrontendQualityEngineExecutionState state, FrontendQualityEngineOutcomeReason reason = FrontendQualityEngineOutcomeReason.None, int? findings = null) =>
        new() { EngineId = id, DisplayName = id.ToString(), Requirement = policy, Enabled = enabled, ExecutionState = state, OutcomeReason = reason, FindingCount = findings };

    [Fact]
    public void Coverage_TwoRequiredAssessed_FourOptionalDisabled_IsTwoOfTwoAndZeroOfZero()
    {
        var coverage = FrontendQualityCoverage.Evaluate([
            Outcome(FrontendQualityEngineId.StaticSecurity, FrontendQualityEngineRequirement.Required, true, FrontendQualityEngineExecutionState.Assessed, findings: 0),
            Outcome(FrontendQualityEngineId.PassivePerformance, FrontendQualityEngineRequirement.Required, true, FrontendQualityEngineExecutionState.Assessed, findings: 3),
            Outcome(FrontendQualityEngineId.BrowserRuntime, FrontendQualityEngineRequirement.Optional, false, FrontendQualityEngineExecutionState.Disabled, FrontendQualityEngineOutcomeReason.DisabledInSystemSettings),
            Outcome(FrontendQualityEngineId.Accessibility, FrontendQualityEngineRequirement.Optional, false, FrontendQualityEngineExecutionState.Disabled),
            Outcome(FrontendQualityEngineId.Lighthouse, FrontendQualityEngineRequirement.Optional, false, FrontendQualityEngineExecutionState.Disabled),
            Outcome(FrontendQualityEngineId.PassiveSecurity, FrontendQualityEngineRequirement.Optional, false, FrontendQualityEngineExecutionState.Disabled),
        ]);

        coverage.RequiredCoverageState.Should().Be(FrontendQualityRequiredCoverageState.AllRequiredAssessed);
        coverage.RequiredTotal.Should().Be(2);
        coverage.RequiredAssessed.Should().Be(2);
        coverage.OptionalTotal.Should().Be(0, "disabled optional engines are not missed assessments");
        coverage.OptionalAssessed.Should().Be(0);
        coverage.InactiveCount.Should().Be(4);
    }

    [Fact]
    public void Coverage_EnabledOptionalBlocked_IsZeroOfOne_AndDoesNotTouchRequired()
    {
        var coverage = FrontendQualityCoverage.Evaluate([
            Outcome(FrontendQualityEngineId.StaticSecurity, FrontendQualityEngineRequirement.Required, true, FrontendQualityEngineExecutionState.Assessed),
            Outcome(FrontendQualityEngineId.PassivePerformance, FrontendQualityEngineRequirement.Required, true, FrontendQualityEngineExecutionState.Assessed),
            Outcome(FrontendQualityEngineId.Accessibility, FrontendQualityEngineRequirement.Optional, true, FrontendQualityEngineExecutionState.NotApplicable, FrontendQualityEngineOutcomeReason.BrowserDomUnavailableForMethod),
            Outcome(FrontendQualityEngineId.Lighthouse, FrontendQualityEngineRequirement.Optional, false, FrontendQualityEngineExecutionState.Disabled),
        ]);

        coverage.RequiredCoverageState.Should().Be(FrontendQualityRequiredCoverageState.AllRequiredAssessed);
        coverage.OptionalTotal.Should().Be(1);
        coverage.OptionalAssessed.Should().Be(0);
        coverage.InactiveCount.Should().Be(1);
    }

    [Fact]
    public void Coverage_NotSelectedOptionalEngine_IsExcluded()
    {
        var coverage = FrontendQualityCoverage.Evaluate([
            Outcome(FrontendQualityEngineId.StaticSecurity, FrontendQualityEngineRequirement.Required, true, FrontendQualityEngineExecutionState.Assessed),
            Outcome(FrontendQualityEngineId.BrowserRuntime, FrontendQualityEngineRequirement.Optional, true, FrontendQualityEngineExecutionState.NotApplicable, FrontendQualityEngineOutcomeReason.NotSelected),
        ]);

        coverage.OptionalTotal.Should().Be(0);
        coverage.InactiveCount.Should().Be(1);
    }

    [Fact]
    public void Coverage_RequiredDisabledEngine_KeepsRequiredIncomplete()
    {
        var coverage = FrontendQualityCoverage.Evaluate([
            Outcome(FrontendQualityEngineId.StaticSecurity, FrontendQualityEngineRequirement.Required, true, FrontendQualityEngineExecutionState.Assessed),
            Outcome(FrontendQualityEngineId.PassivePerformance, FrontendQualityEngineRequirement.Required, false, FrontendQualityEngineExecutionState.Disabled),
        ]);

        coverage.RequiredTotal.Should().Be(2);
        coverage.RequiredAssessed.Should().Be(1);
        coverage.RequiredCoverageState.Should().Be(FrontendQualityRequiredCoverageState.SomeRequiredNotAssessed);
        FrontendQualityDecisionSupportService.EvaluateReleaseDisposition(coverage, [], [], [], new()).Should().Be(FrontendQualityReleaseDisposition.Blocked);
    }

    [Fact]
    public void Coverage_RequiredActiveBlocked_IsOneOfTwo_Blocked()
    {
        var coverage = FrontendQualityCoverage.Evaluate([
            Outcome(FrontendQualityEngineId.StaticSecurity, FrontendQualityEngineRequirement.Required, true, FrontendQualityEngineExecutionState.Assessed),
            Outcome(FrontendQualityEngineId.PassivePerformance, FrontendQualityEngineRequirement.Required, true, FrontendQualityEngineExecutionState.Unavailable, FrontendQualityEngineOutcomeReason.TargetHttpError),
        ]);

        coverage.RequiredAssessed.Should().Be(1);
        coverage.RequiredTotal.Should().Be(2);
        FrontendQualityDecisionSupportService.EvaluateReleaseDisposition(coverage, [], [], [], new()).Should().Be(FrontendQualityReleaseDisposition.Blocked);
    }

    [Fact]
    public void DisabledEngine_PresentsAsDisabledNotAsNotAssessed()
    {
        var disabled = Outcome(FrontendQualityEngineId.Lighthouse, FrontendQualityEngineRequirement.Optional, false, FrontendQualityEngineExecutionState.Disabled, FrontendQualityEngineOutcomeReason.DisabledInSystemSettings);
        var notSelected = Outcome(FrontendQualityEngineId.BrowserRuntime, FrontendQualityEngineRequirement.Optional, true, FrontendQualityEngineExecutionState.NotApplicable, FrontendQualityEngineOutcomeReason.NotSelected);
        var blocked = Outcome(FrontendQualityEngineId.Accessibility, FrontendQualityEngineRequirement.Optional, true, FrontendQualityEngineExecutionState.Unavailable, FrontendQualityEngineOutcomeReason.AuthenticationRequired);

        FrontendQualityEngineOutcomePresentation.AssessmentLabel(disabled).Should().Be("Disabled");
        FrontendQualityEngineOutcomePresentation.StateLabel(disabled).Should().Be("Disabled");
        FrontendQualityEngineOutcomePresentation.AssessmentLabel(notSelected).Should().Be("Not selected");
        FrontendQualityEngineOutcomePresentation.AssessmentLabel(blocked).Should().Be("Not assessed");
        disabled.FindingCount.Should().BeNull("a disabled engine has no zero-finding assessment");
        FrontendQualityCoverage.IsInactive(disabled).Should().BeTrue();
        FrontendQualityCoverage.IsInactive(notSelected).Should().BeTrue();
        FrontendQualityCoverage.IsInactive(blocked).Should().BeFalse();
    }
}
