using BirkNext.Governance;
using BirkNext.Interpretation;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using BirkNext.Web.Tests.Components;
using BirkNext.Web.Tests.Services;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Moq;

namespace BirkNext.Web.Tests.Pages;

/// <summary>Release Readiness page: decision with label + icon and reasons, dimension cards with applicability, module scope sent to the server, freeze →
/// the exact report by id (never the latest), and sign-off recorded beside the report.</summary>
public sealed class ReleaseReadinessPageTests : BunitContext
{
    static readonly DateTimeOffset Now = new(2026, 11, 4, 8, 0, 0, TimeSpan.Zero);

    static ReleaseReadinessAssessment A(GateDecision decision, params string[] modules) => new(new("dev", modules.Length == 0 ? "Whole project" : string.Join(", ", modules), modules, Guid.NewGuid()),
        decision, decision == GateDecision.Blocked ? "Blocked by governance." : "Limitations in test design.",
        [
            new(ReadinessDimensionKey.Governance, "Governance", DimensionApplicability.Required, decision == GateDecision.Blocked ? GateDecision.Blocked : GateDecision.Ready,
                "Blocked: 1 blocking finding", ["Blocking · Identity conflict — SkoleAdapter"], "Open the governance findings in Relationship Review.", "correlation-review"),
            new(ReadinessDimensionKey.Performance, "Performance", DimensionApplicability.Optional, GateDecision.NotAssessed,
                "Not read into release readiness yet: open Performance for its own readiness and results.", [], null, "performance-test-review"),
        ],
        decision == GateDecision.Blocked ? ["Identity conflict: Confirm the correct identity in Relationship Review."] : [], ["Governance: Open the governance findings in Relationship Review."],
        "release/1", "governance/1", Now) { AvailableModules = ["SkoleAdapter"], Facts = new(1, 4, 3, 1, 0, 6, 5, 1, 0, 2) };

    private async Task<Mock<ICanonicalInterpretationApiService>> Setup(string? application = null)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var storage = new ActiveTargetEnvironmentTests.Storage();
        var settings = new FrontendAnalysisSettingsService();
        await settings.LoadAsync(storage);
        settings.Settings.ActiveProfileId = "dev";
        if (application is not null) settings.Settings.Profiles.Single(p => p.Id == "dev").TargetApplication = new BirkNext.Web.Models.TargetApplicationIdentity { Name = application };
        Services.AddSingleton<IJSRuntime>(storage);
        Services.AddSingleton<IFrontendAnalysisSettingsService>(settings);
        Services.AddSingleton<IFrontendAnalysisContextFactory>(ActiveTargetEnvironmentTests.Factory(settings, storage));
        var feature = new CanonicalEntityView(Guid.NewGuid(), CanonicalEntityType.Feature, "skoleadapter/001", "001", "001", "x", null,
            new Dictionary<string, string> { ["module"] = "SkoleAdapter" }, Now, Now);
        var api = new Mock<ICanonicalInterpretationApiService>();
        api.Setup(a => a.TraceFeaturesAsync("dev", null, It.IsAny<CancellationToken>())).ReturnsAsync(([new TraceFeatureRow(feature, 1, 1, 1, 0, 0, 1, 0)], (string?)null));
        api.Setup(a => a.ReleaseReadinessAsync("dev", It.IsAny<IReadOnlyList<string>>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, IReadOnlyList<string>? modules, string? _, CancellationToken _) => (A(modules is { Count: > 0 } ? GateDecision.Blocked : GateDecision.ReadyWithLimitations, [.. modules ?? []]), (string?)null));
        api.Setup(a => a.ReleaseReportsAsync("dev", It.IsAny<CancellationToken>())).ReturnsAsync(([], (string?)null));
        Services.AddSingleton(api.Object);
        return api;
    }

    [Fact]
    public async Task Shows_the_decision_and_dimensions_and_sends_the_chosen_modules()
    {
        var api = await Setup();
        var cut = Render<ReleaseReadiness>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=rr-decision]").GetAttribute("data-decision").Should().Be("ReadyWithLimitations"));
        cut.Find("[data-testid=rr-decision]").TextContent.Should().Contain("!").And.Contain("Ready with limitations");
        var perf = cut.FindAll("[data-testid=rr-dimension]").Single(d => d.GetAttribute("data-key") == "Performance");
        perf.TextContent.Should().Contain("Not assessed").And.Contain("Open Performance Test Review");
        perf.Closest("[data-testid=rr-dim-group]")!.GetAttribute("data-applicability").Should().Be("Optional", "dimensions are grouped Required / Optional");
        cut.Find("[data-testid=rr-fact-requirements]").TextContent.Should().Be("4");
        cut.Find("[data-testid=rr-fact-tested]").TextContent.Should().Be("3");
        cut.Find("[data-testid=rr-fact-executed]").TextContent.Should().Be("Not assessed", "a linked test is never counted as executed");
        cut.Find("[data-testid=rr-fact-decisions]").TextContent.Should().Be("2");

        cut.Find("[data-testid=rr-tab-scope]").Click();
        cut.FindAll("[data-testid=rr-module]").Should().BeEmpty("the entire project is the default; modules are an explicit choice");
        cut.Find("[data-testid=rr-scope-selected]").Change(true);
        cut.Find("[data-testid=rr-module-search]").Input("skole");
        cut.Find("[data-testid=rr-module][data-module=SkoleAdapter]").Change(true);
        cut.WaitForAssertion(() => cut.Find("[data-testid=rr-scope-label]").TextContent.Should().Be("SkoleAdapter"));
        api.Verify(a => a.ReleaseReadinessAsync("dev", It.Is<IReadOnlyList<string>>(m => m.SequenceEqual(new[] { "SkoleAdapter" })), It.IsAny<string?>(), It.IsAny<CancellationToken>()));
        cut.Find("[data-testid=rr-tab-overview]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=rr-decision]").GetAttribute("data-decision").Should().Be("Blocked"));
        cut.Find("[data-testid=rr-blockers]").TextContent.Should().Contain("Identity conflict");
        cut.Find("[data-testid=rr-tab-history]").Click();
        cut.Find("[data-testid=rr-freeze]").HasAttribute("disabled").Should().BeTrue("a report needs a name");

        cut.Find("[data-testid=rr-tab-scope]").Click();
        cut.Find("[data-testid=rr-scope-entire]").Change(true);
        cut.WaitForAssertion(() => cut.Find("[data-testid=rr-scope-label]").TextContent.Should().Be("Entire project"));
        api.Verify(a => a.ReleaseReadinessAsync("dev", It.Is<IReadOnlyList<string>>(m => m.Count == 0), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.AtLeast(2));
    }

    [Fact]
    public async Task Freezing_opens_the_exact_report_and_a_sign_off_is_recorded_beside_it()
    {
        var api = await Setup();
        var id = Guid.NewGuid();
        var report = new ReleaseReadinessReport(id, "Skolenærvær 1.4", Now, A(GateDecision.Blocked, "SkoleAdapter"), []);
        api.Setup(a => a.FreezeReleaseAsync("dev", It.IsAny<FreezeReleaseReadinessRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync((report, (string?)null));
        api.Setup(a => a.ReleaseReportAsync("dev", id, It.IsAny<CancellationToken>())).ReturnsAsync((report, (string?)null));
        api.Setup(a => a.SignOffReleaseAsync("dev", id, It.IsAny<ReleaseSignOffRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((report with { SignOffs = [new(SignOffOutcome.AcceptedWithRisk, "local user", Now, "MU accepted")] }, (string?)null));

        var cut = Render<ReleaseReadiness>();
        cut.WaitForElement("[data-testid=rr-decision]");
        cut.Find("[data-testid=rr-tab-history]").Click();
        cut.Find("[data-testid=rr-name]").Input("Skolenærvær 1.4");
        cut.Find("[data-testid=rr-freeze]").Click();
        var nav = Services.GetRequiredService<NavigationManager>();
        cut.WaitForAssertion(() => nav.Uri.Should().EndWith($"release-readiness?report={id}"));
        api.Verify(a => a.FreezeReleaseAsync("dev", It.Is<FreezeReleaseReadinessRequest>(r => r.Name == "Skolenærvær 1.4"), It.IsAny<CancellationToken>()));

        cut.WaitForAssertion(() => cut.Find("[data-testid=rr-report-frozen]").TextContent.Should().Contain("Frozen 2026-11-04 08:00 UTC").And.Contain("never changes"));
        cut.Find("[data-testid=rr-decision]").GetAttribute("data-decision").Should().Be("Blocked");
        cut.Find("[data-testid=rr-signoff-outcome]").Change(nameof(SignOffOutcome.AcceptedWithRisk));
        cut.Find("[data-testid=rr-signoff-note]").Change("MU accepted");
        cut.Find("[data-testid=rr-signoff-submit]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=rr-signoff-entry]").TextContent.Should().Contain("Accepted with risk").And.Contain("MU accepted"));
        cut.Find("[data-testid=rr-decision]").GetAttribute("data-decision").Should().Be("Blocked", "a sign-off never changes the computed decision");
    }

    [Fact]
    public async Task A_missing_report_is_an_error_never_the_latest()
    {
        var api = await Setup();
        api.Setup(a => a.ReleaseReportAsync("dev", It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(((ReleaseReadinessReport?)null, "Correlation review request failed (404)."));
        Services.GetRequiredService<NavigationManager>().NavigateTo($"release-readiness?report={Guid.NewGuid()}");
        var cut = Render<ReleaseReadiness>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=rr-report-error]").TextContent.Should().Contain("404"));
        cut.FindAll("[data-testid=rr-decision]").Should().BeEmpty();
        api.Verify(a => a.ReleaseReadinessAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never());
    }

    [Fact]
    public async Task Without_a_relationship_analysis_nothing_is_assessed_and_the_next_step_is_Relationship_Review()
    {
        var api = await Setup("M2LB");
        var unprepared = new ReleaseReadinessAssessment(new("dev", "Whole project", [], null), GateDecision.NotAssessed, "Required evidence not assessed: scope.",
            [new(ReadinessDimensionKey.ScopeClarity, "Scope", DimensionApplicability.Required, GateDecision.NotAssessed,
                "Required relationship evidence has not been prepared: no relationship analysis exists for this project yet, so the release scope is unknown.", [],
                "Analyze relationships in Relationship Review.", "correlation-review")],
            [], ["Scope: Analyze relationships in Relationship Review."], "release/1", "governance/1", Now);
        api.Setup(a => a.ReleaseReadinessAsync("dev", It.IsAny<IReadOnlyList<string>>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync((unprepared, (string?)null));

        var cut = Render<ReleaseReadiness>();

        cut.WaitForAssertion(() => cut.Find("[data-testid=rr-decision]").TextContent.Should().Contain("Release Readiness — Not assessed")
            .And.Contain("Required relationship evidence has not been prepared."));
        cut.Find("[data-testid=rr-application]").TextContent.Should().Be("M2LB", "the release is about the Target Application, not the environment name");
        cut.Find("[data-testid=rr-intro]").TextContent.Should().Contain("Assess whether a release is ready");
        cut.Find("[data-testid=rr-analyze]").GetAttribute("href").Should().Be("correlation-review");
        cut.FindAll("[data-testid=rr-facts]").Should().BeEmpty("no count is shown before anything is assessed");
        cut.Find("[data-testid=rr-prerequisites]").Click();
        cut.Find("[data-testid=rr-missing]").TextContent.Should().Contain("Scope").And.Contain("has not been prepared");
        cut.Find("[data-testid=rr-evidence-analysis]").TextContent.Should().Contain("Not prepared");
        cut.Find("[data-testid=rr-scope-selected]").Change(true);
        cut.Find("[data-testid=rr-no-modules]").TextContent.Should().Contain("has not been prepared yet");
        cut.Find("[data-testid=rr-tab-findings]").Click();
        cut.Find("[data-testid=rr-findings-not-assessed]").TextContent.Should().Contain("Governance not assessed");
        api.Verify(a => a.GovernanceAsync(It.IsAny<string>(), It.IsAny<GateAction>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()), Times.Never());
        cut.Find("[data-testid=rr-tab-history]").Click();
        cut.Find("[data-testid=rr-freeze]").TextContent.Should().Contain("Freeze incomplete assessment");
        cut.Find("[data-testid=rr-freeze-incomplete]").TextContent.Should().Contain("labelled incomplete");
    }

    [Fact]
    public async Task Findings_follow_the_chosen_modules()
    {
        var api = await Setup();
        var cut = Render<ReleaseReadiness>();
        cut.WaitForElement("[data-testid=rr-decision]");
        cut.Find("[data-testid=rr-tab-scope]").Click();
        cut.Find("[data-testid=rr-scope-selected]").Change(true);
        cut.Find("[data-testid=rr-module][data-module=SkoleAdapter]").Change(true);
        cut.WaitForAssertion(() => cut.Find("[data-testid=rr-scope-label]").TextContent.Should().Be("SkoleAdapter"));
        cut.Find("[data-testid=rr-tab-findings]").Click();
        cut.WaitForAssertion(() => api.Verify(a => a.GovernanceAsync("dev", GateAction.ReleaseReadiness,
            It.Is<IReadOnlyList<string>?>(m => m != null && m.SequenceEqual(new[] { "SkoleAdapter" })), It.IsAny<CancellationToken>()), Times.Once()));
    }

    // ── NEXT57: pre-freeze summary and the history inspector ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task History_shows_what_a_freeze_records_and_inspects_a_frozen_assessment_against_the_current_draft()
    {
        var api = await Setup();
        var reportId = Guid.NewGuid();
        var frozen = A(GateDecision.Blocked);
        api.Setup(a => a.ReleaseReportsAsync("dev", It.IsAny<CancellationToken>()))
            .ReturnsAsync(([new ReleaseReadinessReportSummary(reportId, "Sprint 47", Now, GateDecision.Blocked, [], frozen.ScopeReference.InterpretationId, null)], (string?)null));
        api.Setup(a => a.ReleaseReportAsync("dev", reportId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((new ReleaseReadinessReport(reportId, "Sprint 47", Now, frozen, []), (string?)null));
        var cut = Render<ReleaseReadiness>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=rr-decision]"));
        cut.Find("[data-testid=rr-tab-history]").Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid=rr-freeze-summary]").TextContent.Should().Contain("Entire project").And.Contain("Ready with limitations"));
        cut.Find("[data-testid=rr-report-inspect]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=rr-history-inspector]").TextContent.Should().Contain("Sprint 47"));
        cut.Find("[data-testid=rr-compare-summary]").TextContent.Should().Contain("1 dimension changed").And.Contain("Result then: Blocked").And.Contain("now: Ready with limitations");
        var governance = cut.FindAll("[data-testid=rr-compare-row]").Single(r => r.GetAttribute("data-key") == "Governance");
        governance.GetAttribute("data-changed").Should().Be("true");
        governance.TextContent.Should().Contain("Blocked").And.Contain("Ready");
        cut.Find("[data-testid=rr-inspect-open]").GetAttribute("href").Should().Be($"release-readiness?report={reportId}");
        api.Verify(a => a.FreezeReleaseAsync(It.IsAny<string>(), It.IsAny<FreezeReleaseReadinessRequest>(), It.IsAny<CancellationToken>()), Times.Never, "inspecting never freezes or changes anything");
    }

    [Fact]
    public async Task The_executive_summary_groups_dimensions_by_applicability_with_a_state_legend()
    {
        await Setup();
        var cut = Render<ReleaseReadiness>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=rr-dimensions]"));
        cut.FindAll("[data-testid=rr-dim-group]").Select(g => g.GetAttribute("data-applicability")).Should().Equal("Required", "Optional");
        cut.Find("[data-testid=rr-legend]").TextContent.Should().Contain("Ready").And.Contain("Ready with limitations").And.Contain("Blocked").And.Contain("Not assessed");
        cut.FindAll("[data-testid=rr-metric]").Select(m => m.GetAttribute("data-key")).Should().Equal("packs", "requirements", "tested", "executed", "blocking", "decisions", "gaps");
        cut.Find("[data-testid=rr-fact-packs]").TextContent.Should().Be("1");
    }
}
