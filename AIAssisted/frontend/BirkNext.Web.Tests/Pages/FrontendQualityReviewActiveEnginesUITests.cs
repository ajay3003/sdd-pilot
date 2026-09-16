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
    private static FrontendAnalysisContext Context(Action<FrontendAnalysisFeatureToggles>? toggles = null)
    {
        var profile = new FrontendAnalysisProfile { Id = "dev", Name = "M2LB DEV", EnvironmentType = FrontendEnvironmentType.Development, TargetUrl = "https://m2lbdev.example.test/" };
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

    /// <summary>The landing's primary button: its label is the run text or the "Checking N active engines…" progress text.</summary>
    private static IElement RunButton(IRenderedComponent<FrontendQualityReview> page) =>
        page.FindAll("button").Single(b => b.TextContent.Contains("Run Frontend Quality Review") || b.TextContent.Contains("Checking ") || b.TextContent.Contains("Analysing"));

    [Fact]
    public void DefaultConfiguration_TwoActiveEngines_NoStatusProbe_RunEnabledImmediately()
    {
        var status = Register(Context());

        var page = Render<FrontendQualityReview>();

        page.Find("[data-testid=fqr-active-count]").TextContent.Should().Be("2 enabled");
        page.Find("[data-testid=fqr-active-breakdown]").TextContent.Should().Be("Required 2 · Optional 0");
        page.FindAll("[data-testid=fqr-active-engine]").Select(e => e.TextContent).Should().Contain(t => t.Contains("Static Security")).And.Contain(t => t.Contains("Passive Performance"));
        page.Find("[data-testid=fqr-inactive-engines]").TextContent.Should().Contain("4 engines not active").And.Contain("Browser Runtime (disabled)");
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

        page.Find("[data-testid=fqr-active-none]").TextContent.Should().Contain("No review engines are enabled").And.Contain("Enable at least one Frontend Quality Review engine");
        page.Find("[data-testid=fqr-required-disabled]").TextContent.Should().Contain("Static Security").And.Contain("Passive Performance");
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

        page.Find("[data-testid=fqr-active-count]").TextContent.Should().Be("3 enabled");
        page.Find("[data-testid=fqr-engine-status-pending]").TextContent.Should().Be("Checking 1 active engine…");
        RunButton(page).TextContent.Trim().Should().Be("Checking 1 active engine…");
        RunButton(page).HasAttribute("disabled").Should().BeTrue("Layer 1–3 status for the active backend engine feeds the execution snapshot");
        status.Verify(s => s.GetStatusAsync(It.IsAny<ReviewAuthenticationModeDto>(),
            It.Is<ReviewEngineSelectionDto>(sel => sel.ReadinessEngines != null && sel.ReadinessEngines.Count == 1 && sel.ReadinessEngines[0] == FrontendQualityEngineIdDto.BrowserRuntime),
            It.IsAny<CancellationToken>()), Times.Once);

        pending.SetResult(new FrontendQualityEngineStatusReportDto
        {
            Engines = [new() { EngineId = FrontendQualityEngineIdDto.BrowserRuntime, DisplayName = "Browser Runtime", Layer1Allowed = true, Layer2Enabled = true, AuthModeSupported = true, Available = true }],
        });
        page.WaitForAssertion(() => RunButton(page).HasAttribute("disabled").Should().BeFalse());
        page.FindAll(".fqr-engine-card").Should().ContainSingle().Which.GetAttribute("data-engine-id").Should().Be("BrowserRuntime");
        await Task.CompletedTask;
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
        var context = new FrontendAnalysisContext();
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
        cut.Find("[data-testid=fqr-inactive-count]").TextContent.Should().Contain("4 engines not active");
        cut.FindAll("tr[data-engine-id]").Should().HaveCount(2, "disabled engines are hidden by default");
        cut.Markup.Should().NotContain("Not assessed");

        cut.Find("[data-testid=fqr-show-inactive]").Change(true);
        cut.FindAll("tr[data-engine-id]").Should().HaveCount(6);
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
        var context = new FrontendAnalysisContext();
        context.FeatureToggles.EnableAccessibilityEngine = true;
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
