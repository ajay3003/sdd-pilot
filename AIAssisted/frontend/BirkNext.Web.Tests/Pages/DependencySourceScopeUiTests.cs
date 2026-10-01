using BirkNext.Dependencies;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// Dependency Review consumes Source Analysis snapshots: no upload, one primary + explicitly included related snapshots, visible missing and
/// ambiguous related sources, newer-snapshot notices that never re-bind, feature visibility respected, and results that state their exact scope.
/// </summary>
public sealed class DependencySourceScopeUiTests : BunitContext
{
    private sealed class FakeApi : IDependencyReviewApiService
    {
        public ReviewSourceOptions Options { get; set; } = new() { Snapshots = [SourceScopeFixture.App, SourceScopeFixture.Common] };
        public List<RelatedSourceCandidate> Candidates { get; set; } = [SourceScopeFixture.CommonCandidate];
        public List<Guid?> ScopeCalls { get; } = [];
        public SourceDependencyReviewRequest? LastRequest { get; private set; }
        public DependencyReviewResult? Result { get; set; }
        public List<ReviewSourceScopeRequest?> ScopeRequests { get; } = [];
        public Task<ReviewSourceOptions> SourceScopeAsync(string environmentId, ReviewSourceScopeRequest? scope, CancellationToken ct = default)
        { ScopeCalls.Add(scope?.PrimarySnapshotId); ScopeRequests.Add(scope); return Task.FromResult(SourceScopeFixture.Resolve(Options, scope, Candidates)); }
        public Task<(DependencyReviewResult? Result, string? Error)> RunSourceAsync(SourceDependencyReviewRequest request, CancellationToken ct = default)
        { LastRequest = request; return Task.FromResult<(DependencyReviewResult?, string?)>((Result ?? Run(scoped: true), null)); }
        public Task<IReadOnlyList<DependencyReviewRunSummary>> HistoryAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<DependencyReviewRunSummary>>([]);
        public Task<DependencyReviewResult?> GetAsync(Guid runId, CancellationToken ct = default) => Task.FromResult(Result);
        public Task<(PolicySimulation? Simulation, string? Error)> SimulateAsync(PolicySimulationRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<InventorySummary>> InventoriesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<InventorySummary>>([]);
        public Task<InventoryImportResult> ImportInventoryAsync(string fileName, Stream content, SbomRole role, string? environment, string? name, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<InventoryImportResult> CaptureDeployedAsync(DeployedCaptureRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<(DependencyHealthRun? Run, string? Error)> RunHealthAsync(DependencyHealthRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<DependencyHealthRunSummary>> HealthHistoryAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<DependencyHealthRunSummary>>([]);
        public Task<DependencyHealthRun?> GetHealthAsync(Guid runId, CancellationToken ct = default) => Task.FromResult<DependencyHealthRun?>(null);
        public Task<(DependencyHealthRun? Run, string? Error)> RefreshHealthAsync(Guid runId, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private readonly FakeApi _api = new();

    private void Setup(bool sourceAnalysisEnabled = true)
    {
        Services.AddSingleton<IDependencyReviewApiService>(_api);
        Services.AddSingleton<IReportExportService, ReportExportService>();
        Services.AddSingleton(Moq.Mock.Of<IFrontendAnalysisSettingsService>());
        SourceScopeFixture.Register(this, sourceAnalysisEnabled: sourceAnalysisEnabled);
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private IRenderedComponent<DependencyReview> Open(bool sourceAnalysisEnabled = true)
    {
        Setup(sourceAnalysisEnabled);
        var cut = Render<DependencyReview>();
        cut.Find("[data-testid='dr-mode'][data-mode='Source']").Click();
        return cut;
    }

    private static string Next(IRenderedComponent<DependencyReview> cut) => cut.Find("[data-testid='dr-next-text']").TextContent;

    private static DependencyReviewResult Run(bool scoped) => new()
    {
        RunId = Guid.NewGuid(), CompletedAt = DateTimeOffset.Parse("2026-10-01T12:00:00Z"), Label = "M2LB + M2LB.Common", EvaluationMechanism = "subset",
        Repositories = [new() { Repository = "M2LB", ArchiveSha256 = SourceScopeFixture.App.Fingerprint, Coverage = RenovateCoverage.Configured }, new() { Repository = "M2LB.Common", ArchiveSha256 = SourceScopeFixture.Common.Fingerprint, Coverage = RenovateCoverage.Missing }],
        SourceScope = scoped ? new ReviewSourceScope
        {
            Primary = new() { SnapshotId = SourceScopeFixture.App.SnapshotId, Repository = "M2LB", ArchiveName = "M2LB (1).zip", Fingerprint = SourceScopeFixture.App.Fingerprint, SourceStatus = "Partial" },
            Related = [new() { SnapshotId = SourceScopeFixture.Common.SnapshotId, Repository = "M2LB.Common", ArchiveName = "M2LB.Common.zip", Fingerprint = SourceScopeFixture.Common.Fingerprint, SourceStatus = "Ready" }],
            Limitations = ["Related source detected but not included in this review scope: Shared.Contracts (no analyzed snapshot)."],
        } : null,
        CrossSource = [new("Version difference observed", "nuget", "HotChocolate.AspNetCore", [new("M2LB", "16.6.7", ["Person/src/Person.Api/Person.Api.csproj"]), new("M2LB.Common", "16.6.6", ["Directory.Packages.props"])])],
        SourceRelationships = [new("M2LB", "M2LB.Common", ["2.1.0"], 15, "M2LB.Common", "2.0.0")],
    };

    [Fact]
    public void NoSnapshot_ShowsAClearEmptyState_LinkingToSourceAnalysis_WithoutAnyUpload()
    {
        _api.Options = new();
        var cut = Open();
        cut.Find("[data-testid='dr-source-empty']").TextContent.Should().Contain("No source snapshot available").And.Contain("managed by Source Analysis");
        cut.Find("[data-testid='dr-open-source-analysis']").GetAttribute("href").Should().Be("source-analysis?returnTo=dependency-review");
        cut.FindAll("[data-testid='dr-setup'] input[type=file]").Select(i => i.GetAttribute("accept")).Should().Equal(new[] { ".json,.json5,.renovaterc" }, "only the optional Renovate config override remains — no source ZIP");
        Next(cut).Should().Be("Open Source Analysis");
        cut.Find("[data-testid='dr-readiness-state']").ClassList.Should().NotContain("dr-badge-attention", "no snapshot is not a failure");
        cut.Find("[data-testid='dr-run']").HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void SelectorListsSnapshotsPerRepository_WithShortFingerprints_AndOffersTheCurrentSnapshotWithoutRunning()
    {
        var cut = Open();
        cut.FindAll("[data-testid='dr-primary'] optgroup").Select(g => g.GetAttribute("label")).Should().Equal("M2LB", "M2LB.Common");
        var option = cut.FindAll("[data-testid='dr-primary'] option").Single(o => o.GetAttribute("value") == SourceScopeFixture.App.SnapshotId.ToString()).TextContent;
        option.Should().Be("M2LB (1).zip · c850a1b2… · 2026-10-01 11:14 UTC · Source Analysis: Partial · latest");
        option.Should().NotContain(SourceScopeFixture.App.Fingerprint);
        cut.Find("[data-testid='dr-current-snapshot']").TextContent.Should().Contain("M2LB").And.Contain("c850a1b2…");
        cut.Find("[data-testid='dr-use-current']").Click();
        cut.Find("[data-testid='dr-primary-summary']").GetAttribute("data-snapshot").Should().Be(SourceScopeFixture.App.SnapshotId.ToString());
        _api.LastRequest.Should().BeNull("choosing a snapshot never starts a review");
        cut.Find("[data-testid='dr-primary-status']").TextContent.Should().Contain("Partial").And.Contain("not a review result");
    }

    [Fact]
    public void RelatedSources_AreSuggestedWithEvidence_IncludedOnlyExplicitly_AndRemovable()
    {
        var cut = Open();
        cut.Find("[data-testid='dr-primary']").Change(SourceScopeFixture.App.SnapshotId.ToString());
        var candidate = cut.Find("[data-testid='dr-related-candidate']");
        candidate.TextContent.Should().Contain("M2LB.Common").And.Contain("Suggested").And.Contain("Snapshot available").And.Contain("Referenced by 15 project(s)");
        candidate.QuerySelector("[data-testid='dr-related-included']").Should().BeNull("detected is not included");
        cut.Find("[data-testid='dr-related-count']").TextContent.Should().Contain("None included");
        cut.Find("[data-testid='dr-scope-incomplete']").TextContent.Should().Contain("Review scope may be incomplete").And.Contain("M2LB.Common");
        cut.Find("[data-testid='dr-run']").HasAttribute("disabled").Should().BeFalse("a suggested source is optional");

        cut.Find("[data-testid='dr-related-include']").Click();
        cut.Find("[data-testid='dr-related-count']").TextContent.Should().Contain("1 included");
        cut.Find("[data-testid='dr-related-chip']").TextContent.Should().Contain("M2LB.Common").And.Contain("a91c0000…");
        cut.FindAll("[data-testid='dr-scope-incomplete']").Should().BeEmpty();
        cut.Find("[data-testid='dr-ready-row'][data-row='related']").TextContent.Should().Contain("1 included").And.Contain("M2LB.Common · a91c0000…");
        cut.Find("[data-testid='dr-run']").Click();
        _api.LastRequest!.RelatedSnapshotIds.Should().Equal(SourceScopeFixture.Common.SnapshotId);

        cut.Find("[data-testid='dr-mode'][data-mode='Source']").Click();
        cut.Find("[data-testid='dr-related-remove']").Click();
        cut.Find("[data-testid='dr-related-count']").TextContent.Should().Contain("None included");
        cut.Find("[data-testid='dr-primary-summary']").Should().NotBeNull("removing a related source keeps the primary");
    }

    [Fact]
    public void MissingAndAmbiguousRelatedSources_StayVisible_AndContinueWithoutIsRecorded()
    {
        _api.Candidates =
        [
            new RelatedSourceCandidate { RepositoryKey = "external:shared.contracts", Repository = "Shared.Contracts", State = RelatedSourceState.SnapshotUnavailable, Reason = "Referenced from source, but no analyzed snapshot of this source is available.", Evidence = [new("ProjectReference", "ProjectReference to Shared.Contracts.csproj outside this archive.", 2, [])] },
            SourceScopeFixture.CommonCandidate with { State = RelatedSourceState.NeedsReview, Reason = "The match is not unique — review before including." },
        ];
        var cut = Open();
        cut.Find("[data-testid='dr-primary']").Change(SourceScopeFixture.App.SnapshotId.ToString());
        var missing = cut.FindAll("[data-testid='dr-related-candidate']").Single(c => c.GetAttribute("data-repository") == "Shared.Contracts");
        missing.QuerySelector("[data-testid='dr-related-missing']")!.TextContent.Should().Contain("No analyzed Source Analysis snapshot is available");
        missing.QuerySelector("[data-testid='dr-related-open-source-analysis']")!.GetAttribute("href").Should().Be("source-analysis");
        missing.QuerySelector("[data-testid='dr-related-include']").Should().BeNull("nothing to include");
        cut.FindAll("[data-testid='dr-related-candidate']").Single(c => c.GetAttribute("data-repository") == "M2LB.Common").QuerySelector("[data-testid='dr-related-state']")!.TextContent.Should().Be("Needs review");
        cut.Find("[data-testid='dr-run']").HasAttribute("disabled").Should().BeFalse();
        cut.Find("[data-testid='dr-run']").Click();
        _api.LastRequest!.RelatedSnapshotIds.Should().BeEmpty();
    }

    [Fact]
    public void ContinueWithout_RecordsTheExclusion()
    {
        var cut = Open();
        cut.Find("[data-testid='dr-primary']").Change(SourceScopeFixture.App.SnapshotId.ToString());
        cut.Find("[data-testid='dr-continue-without']").Click();
        cut.Find("[data-testid='dr-run']").Click();
        _api.LastRequest!.ExcludedSuggestions.Should().Equal("M2LB.Common");
    }

    [Fact]
    public void SeveralRelatedSources_CanBeAddedManually_OnePerRepository_NeverThePrimaryRepository()
    {
        var shared = SourceScopeFixture.Common with { SnapshotId = Guid.NewGuid(), RepositoryKey = "shared.contracts", Repository = "Shared.Contracts", ArchiveName = "Shared.Contracts.zip", Fingerprint = "beef" + new string('2', 60) };
        var olderApp = SourceScopeFixture.App with { SnapshotId = Guid.NewGuid(), AnalyzedAt = SourceScopeFixture.App.AnalyzedAt.AddDays(-3), Latest = false };
        _api.Options = new() { Snapshots = [SourceScopeFixture.App, olderApp, SourceScopeFixture.Common, shared] };
        var cut = Open();
        cut.Find("[data-testid='dr-primary']").Change(SourceScopeFixture.App.SnapshotId.ToString());
        cut.FindAll("[data-testid='dr-add-related'] option").Select(o => o.GetAttribute("value")).Should().Equal("", shared.SnapshotId.ToString());
        cut.Find("[data-testid='dr-related-include']").Click();
        cut.Find("[data-testid='dr-add-related']").Change(shared.SnapshotId.ToString());
        cut.FindAll("[data-testid='dr-related-chip']").Should().HaveCount(2);
        cut.Find("[data-testid='dr-run']").Click();
        _api.LastRequest!.RelatedSnapshotIds.Should().Equal(SourceScopeFixture.Common.SnapshotId, shared.SnapshotId);
    }

    [Fact]
    public void NewerSnapshot_IsOffered_NeverApplied()
    {
        var newer = SourceScopeFixture.App with { SnapshotId = Guid.NewGuid(), ArchiveName = "M2LB (2).zip", Fingerprint = "de42" + new string('3', 60), AnalyzedAt = SourceScopeFixture.App.AnalyzedAt.AddDays(1) };
        _api.Options = new() { Snapshots = [SourceScopeFixture.App with { Latest = false }, newer with { Latest = true }, SourceScopeFixture.Common] };
        var cut = Open();
        cut.Find("[data-testid='dr-primary']").Change(SourceScopeFixture.App.SnapshotId.ToString());
        cut.Find("[data-testid='dr-newer-snapshot']").TextContent.Should().Contain("Newer source snapshot available").And.Contain("c850a1b2…").And.Contain("de423333…").And.Contain("not switched automatically");
        cut.Find("[data-testid='dr-primary-summary']").GetAttribute("data-snapshot").Should().Be(SourceScopeFixture.App.SnapshotId.ToString());
        cut.Find("[data-testid='dr-keep-current']").Click();
        cut.FindAll("[data-testid='dr-newer-snapshot']").Should().BeEmpty();
        cut.Find("[data-testid='dr-run']").Click();
        _api.LastRequest!.PrimarySnapshotId.Should().Be(SourceScopeFixture.App.SnapshotId, "keeping the current snapshot is honoured");
    }

    [Fact]
    public void SnapshotWithoutDependencyEvidence_IsNotAvailable_NotFailed()
    {
        var old = SourceScopeFixture.App with { HasConsumerEvidence = false, ConsumerEvidenceNote = "Analyzed before dependency evidence was captured. Analyze the archive again in Source Analysis to create a new snapshot." };
        _api.Options = new() { Snapshots = [old] };
        var cut = Open();
        cut.Find("[data-testid='dr-primary']").Change(old.SnapshotId.ToString());
        cut.Find("[data-testid='dr-primary-evidence']").TextContent.Should().Contain("Not available").And.Contain("Analyze the archive again in Source Analysis");
        Next(cut).Should().Be("Choose a snapshot with dependency evidence");
        cut.Find("[data-testid='dr-ready-row'][data-row='declared'] [data-testid='dr-ready-state']").TextContent.Should().Be("Not available");
        cut.Find("[data-testid='dr-readiness']").TextContent.Should().NotContain("Failed");
        cut.Find("[data-testid='dr-run']").HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void SourceAnalysisDisabled_OffersNoSnapshots_OtherEvidenceRemains()
    {
        var cut = Open(sourceAnalysisEnabled: false);
        cut.Find("[data-testid='dr-source-disabled']").TextContent.Should().Contain("Source Analysis is disabled").And.Contain("no snapshot or result is removed").And.Contain("inventory, SBOM and deployed evidence remain available");
        _api.ScopeCalls.Should().BeEmpty("hidden Source Analysis is not bypassed");
        Next(cut).Should().Be("Choose other dependency evidence");
        cut.FindAll("[data-testid='dr-mode']").Should().HaveCount(4, "existing inventory, SBOM and deployed evidence stay available");
    }

    [Fact]
    public void Result_StatesItsExactScope_CrossSourceDifferencesNeutral_AndExportCarriesTheScope()
    {
        var cut = Open();
        cut.Find("[data-testid='dr-primary']").Change(SourceScopeFixture.App.SnapshotId.ToString());
        cut.Find("[data-testid='dr-related-include']").Click();
        cut.Find("[data-testid='dr-run']").Click();
        cut.WaitForElement("[data-testid='dr-result-scope']");

        var entries = cut.FindAll("[data-testid='dr-result-scope-entry']");
        entries.Select(e => (e.GetAttribute("data-role"), e.GetAttribute("data-snapshot"))).Should().Equal(("Primary", SourceScopeFixture.App.SnapshotId.ToString()), ("Related", SourceScopeFixture.Common.SnapshotId.ToString()));
        entries[0].TextContent.Should().Contain("c850a1b2…").And.Contain("Source Analysis: Partial");
        cut.FindAll("[data-testid='dr-result-scope-renovate']").Select(r => r.TextContent).Should().HaveCount(2, "Renovate policy is shown per repository");
        cut.Find("[data-testid='dr-result-scope-limitations']").TextContent.Should().Contain("not included in this review scope");
        cut.Find("[data-testid='dr-cross-count']").TextContent.Should().Be("1 package");
        cut.Find("[data-testid='dr-cross-source']").TextContent.Should().Contain("not a failure, vulnerability or incompatibility");
        cut.Find("[data-testid='dr-cross-detail-toggle']").Click();
        var row = cut.Find("[data-testid='dr-cross-row']");
        row.TextContent.Should().Contain("M2LB 16.6.7").And.Contain("M2LB.Common 16.6.6").And.Contain("Version difference observed");
        cut.Find("[data-testid='dr-source-relationships']").TextContent.Should().Contain("M2LB references package M2LB.Common 2.1.0 (15 project(s)) — produced by M2LB.Common, which declares version 2.0.0");

        var html = DependencyReviewExport.Build(_api.Result ?? Run(scoped: true), (h, rows) => string.Join("|", h) + "\n" + string.Join("\n", rows.Select(r => string.Join("|", r))), b => b, s => s ?? "", (t, a, b, body) => body);
        html.Should().Contain("Source scope").And.Contain("Primary|M2LB|M2LB (1).zip|" + SourceScopeFixture.App.Fingerprint).And.Contain("Related|M2LB.Common").And.Contain("HotChocolate.AspNetCore");
    }

    [Fact]
    public void LegacyRuns_AreMarkedAsLegacySourceInput()
    {
        _api.Result = Run(scoped: false);
        var cut = Open();
        cut.Find("[data-testid='dr-primary']").Change(SourceScopeFixture.App.SnapshotId.ToString());
        cut.Find("[data-testid='dr-run']").Click();
        cut.WaitForElement("[data-testid='dr-result-scope']");
        cut.Find("[data-testid='dr-result-legacy']").TextContent.Should().Contain("Legacy source input").And.Contain("No Source Analysis snapshot is linked");
        cut.FindAll("[data-testid='dr-result-scope-entry']").Should().BeEmpty("no fake snapshot ids");
    }
}
