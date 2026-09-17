using BirkNext.BrowserCompanion;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Microsoft.JSInterop;

namespace BirkNext.Web.Tests.Services;

public sealed class WcagAssessmentTests
{
    private static BrowserPageEvidence Evidence(string path = "/a", BrowserAccessibilitySummary? a = null) => new()
    {
        ProfileId = "dev", PageOrigin = "https://app.test", PagePath = path,
        VisitStartedAt = DateTimeOffset.UtcNow.AddMinutes(-1), CapturedAt = DateTimeOffset.UtcNow,
        Accessibility = a ?? new BrowserAccessibilitySummary(),
    };
    private static EndpointDiscoverySnapshot Snapshot(BrowserAccessibilitySummary? a = null) => new()
    {
        Pages = [new PageAnalysis { PageOrigin = "https://app.test", PagePath = "/a", BrowserEvidence = Evidence(a: a) }],
    };
    private static WcagCriterionResult Result(EndpointDiscoverySnapshot s, string id) =>
        WcagAssessmentEngine.Evaluate(s).Results.Single(r => r.Definition.CriterionId == id);

    [Fact]
    public void RegistryIncludesAllAAndAaCriteriaForBothTargets()
    {
        Assert.Equal(50, WcagRegistry.For(new() { Version = WcagVersion.Wcag21 }).Count());
        Assert.Equal(48, WcagRegistry.For(new()).Count()); // Norwegian legal default
        Assert.Equal(56, WcagRegistry.All.Select(d => d.CriterionId).Distinct().Count());
        Assert.All(WcagRegistry.For(new() { Level = WcagLevel.A }), d => Assert.Equal(WcagLevel.A, d.Level));
        Assert.Equal(5, Enum.GetValues<WcagStatus>().Length);
    }

    [Theory]
    [InlineData("1.3.2")][InlineData("1.4.1")][InlineData("2.1.1")][InlineData("2.4.4")]
    [InlineData("3.3.3")][InlineData("4.1.3")][InlineData("1.2.5")][InlineData("3.3.4")]
    public void NoAutomatedFailureDoesNotPassPartialOrManualCriteria(string id)
    {
        var s = Snapshot(new() { Checks = [new() { CheckId = "mouse-only", Outcome = "Pass", Tested = 10 }] });
        Assert.Equal(WcagStatus.NotTested, Result(s, id).Status);
    }

    [Theory]
    [InlineData("1.1.1", "a11y-image-alt")][InlineData("1.4.3", "text-contrast")]
    [InlineData("2.4.2", "a11y-page-title")][InlineData("2.5.3", "label-in-name")]
    [InlineData("3.1.1", "a11y-document-lang")][InlineData("3.3.2", "a11y-control-label")]
    [InlineData("4.1.2", "a11y-aria-reference")]
    public void DeterministicFailuresAreFailures(string id, string check)
    {
        var s = Snapshot(new() { Checks = [new() { CheckId = check, Outcome = "Fail", Tested = 2, Failed = 1 }] });
        Assert.Equal(WcagStatus.Fail, Result(s, id).Status);
        Assert.Equal(WcagConfidence.High, Result(s, id).Confidence);
    }

    [Theory]
    [InlineData("1.2.1")][InlineData("1.2.2")][InlineData("1.2.5")]
    public void ProvenAbsenceOfMediaIsNotApplicable(string id)
    {
        var s = Snapshot(new() { VideoCount = 0, AudioCount = 0, MediaScopeComplete = true });
        Assert.Equal(WcagStatus.NotApplicable, Result(s, id).Status);
        s.Pages[0].BrowserEvidence = Evidence(a: new() { VideoCount = 0, AudioCount = 0 });
        Assert.Equal(WcagStatus.NotTested, Result(s, id).Status);
    }

    [Fact]
    public void NoEvidenceAndUnexecutedInteractionsAreNotTested()
    {
        var s = Snapshot();
        Assert.Equal(WcagStatus.NotTested, Result(s, "2.4.7").Status);
        Assert.Equal(WcagStatus.NotTested, Result(s, "1.4.12").Status);
        s.Pages[0].BrowserEvidence = null;
        Assert.Equal(WcagStatus.NotTested, Result(s, "1.1.1").Status);
    }

    [Fact]
    public void ParsingIsObsoleteOnlyFor22()
    {
        var s = Snapshot();
        s.Wcag.Version = WcagVersion.Wcag22;
        Assert.Equal(WcagStatus.NotApplicable, Result(s, "4.1.1").Status);
        s.Wcag.Version = WcagVersion.Wcag21;
        Assert.Equal(WcagStatus.NotTested, Result(s, "4.1.1").Status);
    }

    [Theory]
    [InlineData("2.4.5")][InlineData("3.2.3")][InlineData("3.2.4")]
    public void CrossPageNeedsMultiplePagesAndNeverProvesSemantics(string id)
    {
        var s = Snapshot(new() { Checks = [new() { CheckId = "navigation-structure", Outcome = "Pass" }, new() { CheckId = "component-structure", Outcome = "Pass" }], NavigationStructure = [2, 3] });
        Assert.Equal(WcagStatus.NotTested, Result(s, id).Status);
        s.Pages.Add(new() { PageOrigin = "https://app.test", PagePath = "/b", BrowserEvidence = Evidence("/b", new()
        { Checks = [new() { CheckId = "navigation-structure", Outcome = "Pass" }, new() { CheckId = "component-structure", Outcome = "Pass" }], NavigationStructure = [3, 2], ComponentStructure = [3, 2] }) });
        var r = Result(s, id);
        Assert.Equal(WcagStatus.ManualReviewRequired, r.Status);
        Assert.Equal(WcagEvidenceSource.CrossPage, r.EvidenceSource);
        Assert.Contains("2 pages", r.AutomatedEvidence);
    }

    [Fact]
    public async Task FivePageRefreshPreservesOthersAndStalesPersistedManualEvidence()
    {
        var js = new Store(); var service = new EndpointDiscoveryService();
        await service.MergeBrowserEvidenceAsync(js, "dev", Enumerable.Range(1, 5).Select(i => Evidence($"/{i}")).ToList());
        var snapshot = service.GetSnapshot("dev"); var page = snapshot.Pages[0];
        await service.RecordWcagReviewAsync(js, "dev", page.Identity, new()
        {
            CriterionId = "3.3.4", Version = WcagVersion.Wcag21, AssessmentProfileId = WcagProfiles.NorwegianId, Generation = 1, Result = WcagStatus.Pass,
            EvidenceNote = "Checked reversible workflow", ReviewedBy = "tester",
        });
        Assert.Equal(WcagStatus.Pass, WcagAssessmentEngine.Evaluate(snapshot, page).Results.Single(r => r.Definition.CriterionId == "3.3.4").Status);
        var reloaded = new EndpointDiscoveryService(); await reloaded.LoadAsync(js);
        Assert.Single(reloaded.GetSnapshot("dev").Pages[0].WcagReviews);
        await service.RefreshPageAsync(js, "dev", page.Identity);
        Assert.Equal(2, page.AnalysisGeneration); Assert.Null(page.BrowserEvidence);
        Assert.All(snapshot.Pages.Skip(1), p => { Assert.Equal(1, p.AnalysisGeneration); Assert.NotNull(p.BrowserEvidence); });
        var r = WcagAssessmentEngine.Evaluate(snapshot, page).Results.Single(r => r.Definition.CriterionId == "3.3.4");
        Assert.True(r.ManualReviewStale); Assert.Equal(WcagStatus.NotTested, r.Status);
        Assert.Single(page.WcagReviews);
        Assert.Empty(service.GetSnapshot("prod").Pages);
    }

    [Theory]
    [InlineData("Bearer abc")][InlineData("<div>raw DOM</div>")][InlineData("person@example.org")][InlineData("password=value")]
    public void ManualNotesRejectSensitiveContent(string text) => Assert.Throws<ArgumentException>(() => WcagReviewText.Validate(text, 1000));

    [Fact]
    public void ExportContainsNativeCoverageAndNeverClaimsConformance()
    {
        var report = new FrontendQualityReviewReport { Wcag = WcagAssessmentEngine.Evaluate(Snapshot()) };
        var html = new ReportExportService().ExportFrontendQualityReview(report, "test");
        Assert.Contains("Norwegian legal baseline", html);
        Assert.DoesNotContain("ManualReviewRequired", html);
        Assert.Contains("NotTested", html);
        Assert.Contains("No manual review recorded", html);
        Assert.Contains("does not establish WCAG conformance", html);
        Assert.DoesNotContain("100% compliant", html);
    }

    [Fact]
    public async Task CrossPageApprovalBecomesStaleWhenAnyParticipatingPageRefreshes()
    {
        var js = new Store(); var service = new EndpointDiscoveryService();
        await service.MergeBrowserEvidenceAsync(js, "dev", [Evidence("/a"), Evidence("/b")]);
        var s = service.GetSnapshot("dev");
        await service.RecordWcagReviewAsync(js, "dev", null, new()
        {
            CriterionId = "3.2.3", Version = WcagVersion.Wcag21, AssessmentProfileId = WcagProfiles.NorwegianId, ScopeGeneration = WcagAssessmentEngine.ScopeGeneration(s),
            Result = WcagStatus.Pass, EvidenceNote = "Navigation compared across both pages", ReviewedBy = "tester",
        });
        Assert.Equal(WcagStatus.Pass, Result(s, "3.2.3").Status);
        await service.RefreshPageAsync(js, "dev", s.Pages[1].Identity);
        Assert.True(Result(s, "3.2.3").ManualReviewStale);
        Assert.Equal(WcagStatus.NotTested, Result(s, "3.2.3").Status);
    }

    [Fact]
    public void CurrentAutomatedFailureCannotBeOverriddenByManualApproval()
    {
        var s = Snapshot(new() { Checks = [new() { CheckId = "text-contrast", Outcome = "Fail", Failed = 1 }] });
        s.Pages[0].WcagReviews.Add(new() { CriterionId = "1.4.3", Version = WcagVersion.Wcag21, AssessmentProfileId = WcagProfiles.NorwegianId, Generation = 1, Result = WcagStatus.Pass });
        Assert.Equal(WcagStatus.Fail, Result(s, "1.4.3").Status);
    }

    [Fact]
    public async Task ChangedTargetAndOldEditorCannotSilentlyApproveNewGeneration()
    {
        var js = new Store(); var service = new EndpointDiscoveryService();
        await service.MergeBrowserEvidenceAsync(js, "dev", [Evidence()]);
        var s = service.GetSnapshot("dev");
        var review = new WcagManualReview { CriterionId = "3.3.4", Version = WcagVersion.Wcag21, AssessmentProfileId = WcagProfiles.NorwegianId, Generation = 1,
            Result = WcagStatus.NotApplicable, EvidenceNote = "No high-impact workflow in scope", ReviewedBy = "tester" };
        await service.RecordWcagReviewAsync(js, "dev", s.Pages[0].Identity, review);
        await service.SaveWcagSettingsAsync(js, "dev", new() { ProfileId = WcagProfiles.ExtendedId });
        Assert.True(Result(s, "3.3.4").ManualReviewStale);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RecordWcagReviewAsync(js, "dev", s.Pages[0].Identity, review));
    }

    [Fact]
    public async Task StorageFailureDoesNotLeaveAnApparentlySavedApproval()
    {
        var js = new Store(); var service = new EndpointDiscoveryService();
        await service.MergeBrowserEvidenceAsync(js, "dev", [Evidence()]);
        js.FailWrites = true;
        var page = service.GetSnapshot("dev").Pages[0];
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RecordWcagReviewAsync(js, "dev", page.Identity, new()
        {
            CriterionId = "3.3.4", Version = WcagVersion.Wcag21, AssessmentProfileId = WcagProfiles.NorwegianId, Generation = 1, Result = WcagStatus.Pass,
            EvidenceNote = "Checked workflow", ReviewedBy = "tester",
        }));
        Assert.Empty(page.WcagReviews);
    }

    private sealed class Store : IJSRuntime
    {
        public bool FailWrites;
        private string? _json;
        public ValueTask<TValue> InvokeAsync<TValue>(string id, object?[]? args)
        {
            if (id == "birkNextStorage.setDiscovery" && FailWrites) throw new JSException("Storage unavailable");
            if (id == "birkNextStorage.setDiscovery") _json = (string?)args![0];
            return new(id == "birkNextStorage.getDiscovery" ? (TValue)(object)_json! : default!);
        }
        public ValueTask<TValue> InvokeAsync<TValue>(string id, CancellationToken ct, object?[]? args) => InvokeAsync<TValue>(id, args);
    }
}
