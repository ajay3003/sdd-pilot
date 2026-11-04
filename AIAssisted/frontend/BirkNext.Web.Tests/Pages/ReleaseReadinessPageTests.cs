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
                "Blocked: 1 blocking finding", ["Blocking · Identity conflict — SkoleAdapter"], "Open the governance findings in Correlation Review.", "correlation-review"),
            new(ReadinessDimensionKey.Performance, "Performance", DimensionApplicability.Optional, GateDecision.NotAssessed,
                "Not read into release readiness yet: open Performance for its own readiness and results.", [], null, "performance-test-review"),
        ],
        decision == GateDecision.Blocked ? ["Identity conflict: Confirm the correct identity in Correlation Review."] : [], ["Governance: Open the governance findings in Correlation Review."],
        "release/1", "governance/1", Now) { AvailableModules = ["SkoleAdapter"] };

    private async Task<Mock<ICanonicalInterpretationApiService>> Setup()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var storage = new ActiveTargetEnvironmentTests.Storage();
        var settings = new FrontendAnalysisSettingsService();
        await settings.LoadAsync(storage);
        settings.Settings.ActiveProfileId = "dev";
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
        perf.TextContent.Should().Contain("Optional").And.Contain("Not assessed").And.Contain("Open Performance Test Review");

        cut.Find("[data-testid=rr-module][data-module=SkoleAdapter]").Change(true);
        cut.WaitForAssertion(() => cut.Find("[data-testid=rr-decision]").GetAttribute("data-decision").Should().Be("Blocked"));
        api.Verify(a => a.ReleaseReadinessAsync("dev", It.Is<IReadOnlyList<string>>(m => m.SequenceEqual(new[] { "SkoleAdapter" })), It.IsAny<string?>(), It.IsAny<CancellationToken>()));
        cut.Find("[data-testid=rr-blockers]").TextContent.Should().Contain("Identity conflict");
        cut.Find("[data-testid=rr-freeze]").HasAttribute("disabled").Should().BeTrue("a report needs a name");
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
}
