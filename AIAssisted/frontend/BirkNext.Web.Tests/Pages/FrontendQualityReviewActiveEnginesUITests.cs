using AngleSharp.Dom;
using BirkNext.Web.Components;
using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// The review page derives the active engine set from the saved configuration, shows it, probes readiness only for active backend
/// engines, never blocks Run on disabled engines, and refuses to start with zero active engines.
/// </summary>
public sealed class FrontendQualityReviewActiveEnginesUITests : BunitContext
{
    /// <summary>Baseline for scenario tests: only the two HTTP engines enabled. <see cref="FactoryDefaults"/> uses the real defaults.</summary>
    private static FrontendAnalysisContext Context(Action<FrontendAnalysisFeatureToggles>? toggles = null)
    {
        var profile = new FrontendAnalysisProfile { Id = "dev", Name = "M2LB DEV", EnvironmentType = FrontendEnvironmentType.Development, TargetUrl = "https://m2lbdev.example.test/" };
        profile.Features.EnableBrowserRuntimeEngine = false;
        profile.Features.EnableAccessibilityEngine = false;
        profile.Features.EnableLighthouseEngine = false;
        profile.Features.EnablePassiveSecurityEngine = false;
        toggles?.Invoke(profile.Features);
        return new FrontendAnalysisContext
        {
            ActiveProfile = profile, TargetUrl = profile.TargetUrl!, FeatureToggles = profile.Features,
            EngineRequirements = profile.EngineRequirements, ReviewEngineSelection = profile.ReviewEngineSelection,
        };
    }

    private Mock<IFrontendQualityEngineStatusApiService> Register(FrontendAnalysisContext context, TaskCompletionSource<FrontendQualityEngineStatusReportDto?>? pendingStatus = null)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var status = new Mock<IFrontendQualityEngineStatusApiService>();
        status.Setup(s => s.GetStatusAsync(It.IsAny<ReviewAuthenticationModeDto>(), It.IsAny<ReviewEngineSelectionDto>(), It.IsAny<CancellationToken>()))
            .Returns((ReviewAuthenticationModeDto _, ReviewEngineSelectionDto selection, CancellationToken _) =>
                pendingStatus?.Task ?? Task.FromResult<FrontendQualityEngineStatusReportDto?>(new FrontendQualityEngineStatusReportDto
                {
                    Engines = (selection.ReadinessEngines ?? []).Select(id => new FrontendQualityEngineStatusDto
                    {
                        EngineId = id, DisplayName = id.ToString(), Layer1Allowed = true, Layer2Enabled = true, AuthModeSupported = true, Available = true,
                        Layer3Readiness = new FrontendQualityEngineReadinessDto { EngineId = id, IsAvailable = true },
                    }).ToList(),
                }));
        var factory = new Mock<IFrontendAnalysisContextFactory>();
        factory.Setup(f => f.GetActiveContextAsync()).ReturnsAsync(context);
        Services.AddSingleton(status.Object);
        Services.AddSingleton(factory.Object);
        Services.AddSingleton(Mock.Of<IFrontendQualityReviewOrchestrator>());
        Services.AddSingleton<RuntimeReviewSessionService>();
        Services.AddSingleton(Mock.Of<IWorkspaceSessionService>());
        Services.AddSingleton(Mock.Of<IReportExportService>());
        Services.AddSingleton(Mock.Of<IAuthenticatedBrowserSessionService>());
        return status;
    }

    /// <summary>One engine's row in the single engine list, whichever disclosure state it is in.</summary>
    private static IElement Row(IRenderedComponent<FrontendQualityReview> page, FrontendQualityEngineId engineId) =>
        page.Find($"[data-testid=fqr-capability][data-engine-id='{engineId}']");

    /// <summary>The landing's primary button: its label is the run text or the "Checking N active engines…" progress text.</summary>
    private static IElement RunButton(IRenderedComponent<FrontendQualityReview> page) =>
        page.FindAll("button").Single(b => b.TextContent.Contains("Run Frontend Quality Review") || b.TextContent.Contains("Checking ") || b.TextContent.Contains("Analysing"));

    [Fact]
    public void DefaultConfiguration_TwoActiveEngines_NoStatusProbe_RunEnabledImmediately()
    {
        var status = Register(Context());

        var page = Render<FrontendQualityReview>();

        // 15, 45. ONE engine list. The "Active review engines" strip listed the same engines again, in a second
        // vocabulary, directly below this one.
        page.FindAll("[data-testid=fqr-active-engines]").Should().BeEmpty();
        page.Find("[data-testid=fqr-engine-summary]").TextContent.Should().Be("2 enabled · 2 available · 6 disabled");
        Row(page, FrontendQualityEngineId.StaticSecurity).GetAttribute("data-state").Should().Be("Enabled");
        Row(page, FrontendQualityEngineId.PassivePerformance).GetAttribute("data-state").Should().Be("Enabled");
        // 3, 17. Switched off is its own state, never "unavailable".
        Row(page, FrontendQualityEngineId.BrowserRuntime).GetAttribute("data-state").Should().Be("Disabled");
        Row(page, FrontendQualityEngineId.BrowserRuntime).TextContent.Should().NotContain("Unavailable");
        status.Verify(s => s.GetStatusAsync(It.IsAny<ReviewAuthenticationModeDto>(), It.IsAny<ReviewEngineSelectionDto>(), It.IsAny<CancellationToken>()), Times.Never,
            "no backend engine is active, so no readiness probe is made");
        RunButton(page).HasAttribute("disabled").Should().BeFalse();
        page.FindAll("[data-testid=fqr-engine-status-pending]").Should().BeEmpty();
        page.Markup.Should().NotContain("Checking ");
    }

    [Fact]
    public void ZeroActiveEngines_RunDisabled_ClearMessage_NoProbe()
    {
        var status = Register(Context(t => { t.EnableSecurityEngine = false; t.EnablePerformanceEngine = false; }));

        var page = Render<FrontendQualityReview>();

        // 6. The blocked state is stated once, by the readiness panel, and the engine list carries the inconsistency.
        page.Find("[data-testid=fqr-readiness-title]").TextContent.Should().Contain("No review engines are enabled");
        page.Find("[data-testid=fqr-readiness]").GetAttribute("data-readiness").Should().Be("Blocked");
        page.Find("[data-testid=fqr-capabilities-required-disabled]").TextContent.Should().Contain("Static Security").And.Contain("Passive Performance");
        RunButton(page).HasAttribute("disabled").Should().BeTrue();
        page.Markup.Should().Contain("No review engines are enabled.");
        status.Verify(s => s.GetStatusAsync(It.IsAny<ReviewAuthenticationModeDto>(), It.IsAny<ReviewEngineSelectionDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EnabledBackendEngine_ProbesOnlyThatEngine_AndRunWaitsWhileChecking()
    {
        var pending = new TaskCompletionSource<FrontendQualityEngineStatusReportDto?>();
        var status = Register(Context(t => t.EnableBrowserRuntimeEngine = true), pending);

        var page = Render<FrontendQualityReview>();

        page.Find("[data-testid=fqr-engine-summary]").TextContent.Should().StartWith("3 enabled");
        page.Find("[data-testid=fqr-engine-status-pending]").TextContent.Should().Be("Checking 1 active engine…");
        RunButton(page).TextContent.Trim().Should().Be("Checking 1 active engine…");
        RunButton(page).HasAttribute("disabled").Should().BeTrue("Layer 1–2 status for the active backend engine feeds the execution snapshot");
        status.Verify(s => s.GetStatusAsync(It.IsAny<ReviewAuthenticationModeDto>(),
            It.Is<ReviewEngineSelectionDto>(sel => sel.ReadinessEngines != null && sel.ReadinessEngines.Count == 0),
            It.IsAny<CancellationToken>()), Times.Once, "phase 1 fetches layers only, no readiness probe");

        pending.SetResult(new FrontendQualityEngineStatusReportDto
        {
            Engines = [new() { EngineId = FrontendQualityEngineIdDto.BrowserRuntime, DisplayName = "Browser Runtime", Layer1Allowed = true, Layer2Enabled = true, AuthModeSupported = true, Available = true }],
        });
        page.WaitForAssertion(() => RunButton(page).HasAttribute("disabled").Should().BeFalse());
        page.WaitForAssertion(() => status.Verify(s => s.GetStatusAsync(It.IsAny<ReviewAuthenticationModeDto>(),
            It.Is<ReviewEngineSelectionDto>(sel => sel.ReadinessEngines != null && sel.ReadinessEngines.Count == 1 && sel.ReadinessEngines[0] == FrontendQualityEngineIdDto.BrowserRuntime),
            It.IsAny<CancellationToken>()), Times.Once, "phase 2 probes readiness for the active backend engine only"));
        page.FindAll(".fqr-engine-card").Should().ContainSingle().Which.GetAttribute("data-engine-id").Should().Be("BrowserRuntime");
        await Task.CompletedTask;
    }

    [Fact]
    public void FactoryDefaults_AllEnginesEnabledExceptBrowserRuntime_RunWaitsOnlyForLayersNotReadiness()
    {
        // Real defaults: a fresh profile has every engine enabled except Browser Runtime.
        var profile = new FrontendAnalysisProfile { Id = "dev", Name = "M2LB DEV", EnvironmentType = FrontendEnvironmentType.Development, TargetUrl = "https://m2lbdev.example.test/" };
        var context = new FrontendAnalysisContext { ActiveProfile = profile, TargetUrl = profile.TargetUrl!, FeatureToggles = profile.Features, EngineRequirements = profile.EngineRequirements, ReviewEngineSelection = profile.ReviewEngineSelection };
        var readiness = new TaskCompletionSource<FrontendQualityEngineStatusReportDto?>();
        var calls = new List<List<FrontendQualityEngineIdDto>>();
        JSInterop.Mode = JSRuntimeMode.Loose;
        var status = new Mock<IFrontendQualityEngineStatusApiService>();
        status.Setup(s => s.GetStatusAsync(It.IsAny<ReviewAuthenticationModeDto>(), It.IsAny<ReviewEngineSelectionDto>(), It.IsAny<CancellationToken>()))
            .Returns((ReviewAuthenticationModeDto _, ReviewEngineSelectionDto selection, CancellationToken _) =>
            {
                calls.Add(selection.ReadinessEngines ?? [FrontendQualityEngineIdDto.BrowserRuntime]);
                var layers = new FrontendQualityEngineStatusReportDto
                {
                    Engines = Enum.GetValues<FrontendQualityEngineIdDto>().Select(id => new FrontendQualityEngineStatusDto { EngineId = id, DisplayName = id.ToString(), Layer1Allowed = true, Layer2Enabled = true, AuthModeSupported = true, Available = true }).ToList(),
                };
                return selection.ReadinessEngines is { Count: 0 } ? Task.FromResult<FrontendQualityEngineStatusReportDto?>(layers) : readiness.Task;
            });
        var factory = new Mock<IFrontendAnalysisContextFactory>();
        factory.Setup(f => f.GetActiveContextAsync()).ReturnsAsync(context);
        Services.AddSingleton(status.Object);
        Services.AddSingleton(factory.Object);
        Services.AddSingleton(Mock.Of<IFrontendQualityReviewOrchestrator>());
        Services.AddSingleton<RuntimeReviewSessionService>();
        Services.AddSingleton(Mock.Of<IWorkspaceSessionService>());
        Services.AddSingleton(Mock.Of<IReportExportService>());
        Services.AddSingleton(Mock.Of<IAuthenticatedBrowserSessionService>());

        var page = Render<FrontendQualityReview>();

        page.Find("[data-testid=fqr-engine-summary]").TextContent.Should().Be("5 enabled · 5 available · 3 disabled");
        // Required first, then Optional: the ROLE axis, carried by the grouping rather than by a per-row status word.
        page.FindAll("[data-testid=fqr-capability-group]").Select(g => g.GetAttribute("data-policy")).Should().Equal("Required", "Optional");
        foreach (var off in new[] { FrontendQualityEngineId.BrowserRuntime, FrontendQualityEngineId.BrowserQuality })
            Row(page, off).GetAttribute("data-state").Should().Be("Disabled");
        // Phase 1 (layers, no probe) answered immediately → Run enabled; phase 2 (readiness) still pending and informational.
        page.WaitForAssertion(() => RunButton(page).HasAttribute("disabled").Should().BeFalse());
        page.WaitForAssertion(() => page.Find("[data-testid=fqr-readiness-pending]").TextContent.Should().Contain("Checking runtime readiness of 3 active engines"));
        calls.Should().HaveCount(2);
        calls[0].Should().BeEmpty("first call fetches Layer 1–2 only");
        calls[1].Should().BeEquivalentTo([FrontendQualityEngineIdDto.Accessibility, FrontendQualityEngineIdDto.Lighthouse, FrontendQualityEngineIdDto.PassiveSecurity], "readiness is probed only for active backend engines");
        // 19, 32, 53. The per-engine diagnostic cards survive — under Technical details, not in the engine list.
        page.Find("[data-testid=fqr-technical-disclosure-body]").QuerySelectorAll(".fqr-engine-card")
            .Select(c => c.GetAttribute("data-engine-id")).Should().BeEquivalentTo(["Accessibility", "Lighthouse", "PassiveSecurity"]);

        readiness.SetResult(new FrontendQualityEngineStatusReportDto { Engines = [] });
        page.WaitForAssertion(() => page.FindAll("[data-testid=fqr-readiness-pending]").Should().BeEmpty());
    }

    [Fact]
    public void DisabledBackendEngineWithUnavailableRuntime_NeverBlocksRun()
    {
        // Browser Runtime is disabled in the saved configuration: no readiness probe, no card, and Run stays enabled.
        Register(Context());

        var page = Render<FrontendQualityReview>();

        page.FindAll(".fqr-engine-card").Should().BeEmpty();
        RunButton(page).HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public void DecisionSupport_HidesInactiveEnginesByDefault_ShowsThemOnDemand_WithDisabledWording()
    {
        var policy = new FrontendQualityEngineRequirementSettings().ToPolicy();
        var context = Context();
        var outcomes = FrontendQualityEngineOutcomeNormalizer.NormalizeAll("https://m2lbdev.example.test/", context, new FrontendQualityReviewOrchestrationResult(
            SecurityReport: new WasmSecurityReviewReport { ScannedAt = DateTime.UtcNow, Findings = [], Assets = [new WasmDiscoveredAsset { Url = "x", AssetType = "HTML", Status = "200 OK", Analyzed = true }] },
            PerformanceReport: new WasmPerformanceReviewReport { ReviewedAt = DateTime.UtcNow, Assets = [new DiscoveredAsset { Url = "x", Type = AssetType.Index, StatusCode = 200 }] }),
            true, true, true, true);
        var report = new FrontendQualityReviewReport
        {
            TargetUrl = "https://m2lbdev.example.test/", GeneratedAt = DateTime.UtcNow, EngineOutcomes = outcomes,
            Coverage = FrontendQualityCoverage.Evaluate(outcomes), ReleaseDisposition = FrontendQualityReleaseDisposition.NoAutomatedBlockDetected,
            ActiveEngines = FrontendQualityActiveEngines.Resolve(context),
        };

        var cut = Render<FrontendQualityDecisionSupport>(p => p.Add(x => x.Report, report));

        cut.Find("[data-testid=fqr-required-assessed]").TextContent.Trim().Should().Be("2 / 2");
        cut.Find("[data-testid=fqr-optional-assessed]").TextContent.Trim().Should().Be("0 / 0 (no optional engine enabled)");
        cut.Find("[data-testid=fqr-inactive-count]").TextContent.Should().Contain("6 engines not active");
        cut.FindAll("tr[data-engine-id]").Should().HaveCount(2, "disabled engines are hidden by default");
        cut.Markup.Should().NotContain("Not assessed");

        cut.Find("[data-testid=fqr-show-inactive]").Change(true);
        cut.FindAll("tr[data-engine-id]").Should().HaveCount(8);
        var lighthouse = cut.Find("tr[data-engine-id='Lighthouse']");
        lighthouse.GetAttribute("data-engine-active").Should().Be("false");
        lighthouse.QuerySelector("[data-testid=fqr-enabled]")!.TextContent.Should().Be("No");
        lighthouse.QuerySelector("[data-testid=fqr-assessment]")!.TextContent.Should().Be("Disabled");
        lighthouse.QuerySelector("[data-testid=fqr-state]")!.TextContent.Should().Be("Disabled");
        cut.Find("tr[data-engine-id='StaticSecurity'] [data-testid=fqr-enabled]").TextContent.Should().Be("Yes");
        cut.Find("tr[data-engine-id='StaticSecurity'] [data-testid=fqr-state]").TextContent.Should().Be("Completed — no findings");
    }

    [Fact]
    public void Export_PreservesActiveEngineSnapshotAndActiveDenominators()
    {
        var context = Context(t => t.EnableAccessibilityEngine = true);
        var active = FrontendQualityActiveEngines.Resolve(context);
        var outcomes = FrontendQualityEngineOutcomeNormalizer.NormalizeAll("https://m2lbdev.example.test/", context, new FrontendQualityReviewOrchestrationResult(
            SecurityReport: new WasmSecurityReviewReport { ScannedAt = DateTime.UtcNow, Assets = [new WasmDiscoveredAsset { Url = "x", AssetType = "HTML", Status = "200 OK", Analyzed = true }] },
            PerformanceReport: new WasmPerformanceReviewReport { ReviewedAt = DateTime.UtcNow, Assets = [new DiscoveredAsset { Url = "x", Type = AssetType.Index, StatusCode = 200 }] }),
            true, true, true, true);
        var report = new FrontendQualityReviewReport
        {
            TargetUrl = "https://m2lbdev.example.test/", GeneratedAt = DateTime.UtcNow, EngineOutcomes = outcomes,
            Coverage = FrontendQualityCoverage.Evaluate(outcomes), ReleaseDisposition = FrontendQualityReleaseDisposition.NoAutomatedBlockDetected, ActiveEngines = active,
        };
        // Settings change AFTER the review: the export must still describe the snapshot.
        context.FeatureToggles.EnableAccessibilityEngine = false;
        context.FeatureToggles.EnableLighthouseEngine = true;

        var html = new ReportExportService().ExportFrontendQualityReview(report, "Project");

        html.Should().Contain("Active review engines").And.Contain("<strong>3 enabled</strong>").And.Contain("Enabled at review start");
        html.Should().Contain("Required assessed:</strong> 2 / 2").And.Contain("Optional assessed:</strong> 0 / 1");
        html.Should().MatchRegex(@"<td>Accessibility</td><td>Optional</td><td>Yes</td>");
        html.Should().MatchRegex(@"<td>Lighthouse</td><td>Optional</td><td>No</td>");
    }
}
