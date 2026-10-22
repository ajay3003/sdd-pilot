using BirkNext.Dependencies;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// Dependency Review result identity across evidence modes: a result is current only under the mode that produced it (the stale
/// Existing-inventory result never shows under Review SBOM), stored results name their mode, Review SBOM first checks the selected source
/// snapshots for SBOM files (none in M2LB.Common/M2LB; source declarations are never SBOMs), multiple candidates are never auto-selected,
/// and the source dependency mapping shows producer → consumer with per-version consumers and no incompatibility claim.
/// </summary>
public sealed class DependencyReviewEvidenceModeTests : BunitContext
{
    private sealed class FakeApi : IDependencyReviewApiService
    {
        public List<InventorySummary> Inventories { get; } = [];
        public List<DependencyHealthRunSummary> HealthRuns { get; } = [];
        public Dictionary<Guid, DependencyHealthRun> Stored { get; } = [];
        public DependencyHealthRequest? LastRequest { get; private set; }
        public SbomCandidateScan Scan { get; set; } = new();
        public List<IReadOnlyList<Guid>> ScanRequests { get; } = [];
        public int Imports { get; private set; }
        public DependencyReviewResult? Source { get; set; }

        public Task<ReviewSourceOptions> SourceScopeAsync(string environmentId, ReviewSourceScopeRequest? scope, CancellationToken ct = default) =>
            Task.FromResult(SourceScopeFixture.Resolve(new ReviewSourceOptions { Snapshots = [SourceScopeFixture.App, SourceScopeFixture.Common] }, scope, [SourceScopeFixture.CommonCandidate]));
        public Task<(DependencyReviewResult? Result, string? Error)> RunSourceAsync(SourceDependencyReviewRequest request, CancellationToken ct = default) =>
            Task.FromResult<(DependencyReviewResult?, string?)>((Source, Source is null ? "not used" : null));
        public Task<IReadOnlyList<DependencyReviewRunSummary>> HistoryAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<DependencyReviewRunSummary>>([]);
        public Task<DependencyReviewResult?> GetAsync(Guid runId, CancellationToken ct = default) => Task.FromResult<DependencyReviewResult?>(null);
        public Task<(PolicySimulation? Simulation, string? Error)> SimulateAsync(PolicySimulationRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<InventorySummary>> InventoriesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<InventorySummary>>(Inventories.ToList());
        public Task<InventoryImportResult> ImportInventoryAsync(string fileName, Stream content, SbomRole role, string? environment, string? name, CancellationToken ct = default)
        { Imports++; return Task.FromResult(new InventoryImportResult(null, null, "not used")); }
        public Task<InventoryImportResult> CaptureDeployedAsync(DeployedCaptureRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<(DependencyHealthRun? Run, string? Error)> RunHealthAsync(DependencyHealthRequest request, CancellationToken ct = default)
        {
            LastRequest = request;
            var run = HealthRun(request);
            Stored[run.RunId] = run;
            HealthRuns.Insert(0, new DependencyHealthRunSummary(run.RunId, run.CompletedAt, run.Label, run.Inventory.Name, 1, 0, null, request.EvidenceMode));
            return Task.FromResult<(DependencyHealthRun?, string?)>((run, null));
        }
        public Task<IReadOnlyList<DependencyHealthRunSummary>> HealthHistoryAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<DependencyHealthRunSummary>>(HealthRuns.ToList());
        public Task<DependencyHealthRun?> GetHealthAsync(Guid runId, CancellationToken ct = default) => Task.FromResult(Stored.GetValueOrDefault(runId));
        public Task<(DependencyHealthRun? Run, string? Error)> RefreshHealthAsync(Guid runId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SbomCandidateScan> SbomCandidatesAsync(string environmentId, IReadOnlyList<Guid> snapshotIds, CancellationToken ct = default)
        { ScanRequests.Add(snapshotIds); return Task.FromResult(Scan); }
    }

    private static readonly InventorySummary Common = new()
    {
        Id = Guid.NewGuid(), Name = "M2LB.Common · source review", SourceType = InventorySourceType.SourceReview, SourceName = "M2LB.Common.zip", Stage = InventoryStage.Declared,
        CapturedAt = DateTimeOffset.Parse("2026-10-22T06:07:00Z"), RecordedAt = DateTimeOffset.Parse("2026-10-22T06:07:00Z"), Dependencies = 32, Freshness = InventoryFreshness.Current, SourceRunId = Guid.NewGuid(),
    };

    private static DependencyHealthRun HealthRun(DependencyHealthRequest request) => new()
    {
        RunId = Guid.NewGuid(), CompletedAt = DateTimeOffset.Parse("2026-10-22T06:21:00Z"), Label = Common.Name, Request = request,
        Inventory = new DependencyInventorySnapshot { Id = request.InventoryId, Name = Common.Name, SourceType = InventorySourceType.SourceReview, Stage = InventoryStage.Declared, SourceRunId = Common.SourceRunId },
        Items = [new DependencyHealthItem { Key = "nuget:xunit@2.9.3", Dependency = new InventoryDependency { PackageName = "xunit", Version = "2.9.3", PackageManager = "nuget" } }],
        Summary = new DependencyHealthSummary { Dependencies = 1 },
    };

    private readonly FakeApi _api = new();

    public DependencyReviewEvidenceModeTests()
    {
        _api.Inventories.Add(Common);
        Services.AddSingleton<IDependencyReviewApiService>(_api);
        Services.AddSingleton<IReportExportService, ReportExportService>();
        Services.AddSingleton(Moq.Mock.Of<IFrontendAnalysisSettingsService>());
        SourceScopeFixture.Register(this);
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private IRenderedComponent<DependencyReview> RunInventoryReview()
    {
        var cut = Render<DependencyReview>();
        cut.Find("[data-testid='dr-mode'][data-mode='Inventory']").Click();
        cut.Find("[data-testid='dh-inventory']").Change(Common.Id.ToString());
        cut.Find("[data-testid='dr-next-action']").Click();
        cut.WaitForElement("[data-testid='dh-result']");
        return cut;
    }

    [Fact]
    public void ReviewSbomWithoutAnSbom_NeverShowsThePreviousInventoryResultAsCurrent()
    {
        var cut = RunInventoryReview();
        var run = _api.HealthRuns.Single().RunId;

        cut.Find("[data-testid='dr-mode'][data-mode='Sbom']").Click();

        cut.FindAll("[data-testid='dh-result']").Should().BeEmpty("an Existing inventory result is not the current SBOM result");
        cut.Find("[data-testid='dr-readiness-state']").TextContent.Should().Be("Not ready");
        cut.Find("[data-testid='dr-next-action']").TextContent.Should().Be("Upload SBOM");
        var other = cut.Find("[data-testid='dr-other-result']");
        other.TextContent.Should().Contain("Previous result available").And.Contain("Dependency health review · Existing inventory").And.Contain(Common.Name);
        cut.FindAll("[data-testid='dr-step'][data-state='done']").Select(s => s.GetAttribute("data-step")).Should().NotContain("3", "nothing ran in SBOM mode");

        cut.Find("[data-testid='dr-other-result-open']").Click();
        cut.Find("[data-testid='dr-mode'][aria-pressed='true']").GetAttribute("data-mode").Should().Be(nameof(DependencyReviewMode.Inventory));
        cut.Find("[data-testid='dh-result']").GetAttribute("data-run").Should().Be(run.ToString(), "switching back shows the stored result again");
    }

    [Fact]
    public void EachEvidenceModeOnlyShowsItsOwnResult()
    {
        var cut = RunInventoryReview();

        foreach (var mode in new[] { "Source", "Deployed" })
        {
            cut.Find($"[data-testid='dr-mode'][data-mode='{mode}']").Click();
            cut.FindAll("[data-testid='dh-result']").Should().BeEmpty($"{mode} mode never shows the inventory result");
        }
        cut.Find("[data-testid='dr-mode'][data-mode='Inventory']").Click();
        cut.Find("[data-testid='dh-result']").GetAttribute("data-mode").Should().Be(DependencyEvidenceModes.Inventory);
        cut.FindAll("[data-testid='dh-section']").Select(s => s.GetAttribute("data-section")).Should().Equal("summary", "findings", "dependencies", "evidence", "automation", "history");
        cut.Markup.Should().NotContain("Dependency mapping", "inventory results have no source-only sections");
    }

    [Fact]
    public void StoredResultsNameTheirEvidenceMode_AndOpenUnderIt()
    {
        RunInventoryReview();
        var cut = Render<DependencyReview>();

        cut.Find("[data-testid='dr-history-disclosure-toggle']").Click();
        var option = cut.FindAll("[data-testid='dh-history'] option").Skip(1).Single();
        option.TextContent.Should().StartWith("Dependency health review · Existing inventory · 2026-10-22 06:21 UTC");
        cut.Find("[data-testid='dh-history']").Change(option.GetAttribute("value"));

        cut.Find("[data-testid='dr-mode'][aria-pressed='true']").GetAttribute("data-mode").Should().Be(nameof(DependencyReviewMode.Inventory));
        cut.Find("[data-testid='dh-identity']").TextContent.Should().Contain("Evidence: Existing inventory").And.Contain("Previous source review");
    }

    [Fact]
    public void LegacyRunsDeriveTheirEvidenceMode_FromTheirOwnEvidence()
    {
        var sbom = HealthRun(new DependencyHealthRequest()) with { Inventory = new DependencyInventorySnapshot { SourceType = InventorySourceType.Sbom } };
        DependencyEvidenceModes.Of(sbom).Should().Be(DependencyEvidenceModes.Sbom);
        DependencyEvidenceModes.Of(sbom with { Request = new DependencyHealthRequest { DeployedInventoryId = Guid.NewGuid() } }).Should().Be(DependencyEvidenceModes.Deployed);
        DependencyEvidenceModes.Of(HealthRun(new DependencyHealthRequest())).Should().Be(DependencyEvidenceModes.Inventory);
        DependencyEvidenceModes.Of(HealthRun(new DependencyHealthRequest { EvidenceMode = DependencyEvidenceModes.Sbom })).Should().Be(DependencyEvidenceModes.Sbom, "a stored mode wins");
    }

    [Fact]
    public void ReviewSbomChecksTheSourceSnapshotsFirst_AndSaysNoneWasFound()
    {
        _api.Scan = new SbomCandidateScan { Checked = [new(SourceScopeFixture.Common.SnapshotId, "M2LB.Common", true, 120), new(SourceScopeFixture.App.SnapshotId, "M2LB", true, 2300)] };
        var cut = Render<DependencyReview>();

        cut.Find("[data-testid='dr-mode'][data-mode='Sbom']").Click();

        _api.ScanRequests.Should().ContainSingle();
        var empty = cut.Find("[data-testid='dh-sbom-scan-empty']");
        empty.TextContent.Should().Be("No supported SBOM was detected in the selected source snapshots.");
        cut.Find("[data-testid='dh-sbom-scan']").TextContent.Should().Contain("Checked: M2LB.Common, M2LB").And.Contain("No CycloneDX, SPDX or packages.lock.json file was found");
        cut.Find("[data-testid='dr-next-reason']").TextContent.Should().StartWith("No supported SBOM was detected in the checked source snapshots.");
        cut.FindAll("[data-testid='dh-result']").Should().BeEmpty();
        _api.Imports.Should().Be(0);
    }

    [Fact]
    public void MultipleSbomCandidatesAreListed_NeverSelectedOrImportedAutomatically()
    {
        _api.Scan = new SbomCandidateScan
        {
            Checked = [new(SourceScopeFixture.App.SnapshotId, "App", true, 40)],
            Candidates = [new(SourceScopeFixture.App.SnapshotId, "App", "artifacts/bom.json", SbomFormat.CycloneDxJson, DateTimeOffset.Parse("2026-10-01T11:14:00Z")),
                          new(SourceScopeFixture.App.SnapshotId, "App", "src/App/packages.lock.json", SbomFormat.NuGetLockFile, DateTimeOffset.Parse("2026-10-01T11:14:00Z"))],
        };
        var cut = Render<DependencyReview>();

        cut.Find("[data-testid='dr-mode'][data-mode='Sbom']").Click();

        cut.Find("[data-testid='dh-sbom-scan-found']").TextContent.Should().Contain("2 SBOM candidates found");
        cut.FindAll("[data-testid='dh-sbom-candidate']").Select(r => r.TextContent).Should().SatisfyRespectively(
            r => r.Should().Contain("CycloneDX JSON").And.Contain("artifacts/bom.json").And.Contain("Not validated until uploaded"),
            r => r.Should().Contain("NuGet packages.lock.json").And.Contain("src/App/packages.lock.json"));
        cut.Find("[data-testid='dh-sbom-scan']").TextContent.Should().Contain("never merged").And.Contain("nothing is imported or selected automatically");
        cut.Find("[data-testid='dh-sbom-picked']").TextContent.Should().Be("No SBOM selected.");
        cut.Find("[data-testid='dr-readiness-state']").TextContent.Should().Be("Not ready");
        _api.Imports.Should().Be(0);
    }

    [Fact]
    public void ReviewSbomScansTheSelectedSourceScope_WhenOneIsSelected()
    {
        var cut = Render<DependencyReview>();
        cut.Find("[data-testid='dr-primary']").Change(SourceScopeFixture.Common.SnapshotId.ToString());

        cut.Find("[data-testid='dr-mode'][data-mode='Sbom']").Click();

        _api.ScanRequests.Single().Should().Equal(SourceScopeFixture.Common.SnapshotId);
    }

    [Theory]
    [InlineData("artifacts/bom.json", SbomFormat.CycloneDxJson)]
    [InlineData("sbom/app.cdx.json", SbomFormat.CycloneDxJson)]
    [InlineData("out/bom.xml", SbomFormat.CycloneDxXml)]
    [InlineData("sbom/app.spdx.json", SbomFormat.SpdxJson)]
    [InlineData("src/App/packages.lock.json", SbomFormat.NuGetLockFile)]
    [InlineData("src\\App\\packages.lock.json", SbomFormat.NuGetLockFile)]
    public void SupportedSbomFileNamesAreCandidates(string path, SbomFormat format) => SbomCandidates.Classify(path).Should().Be(format);

    [Theory]
    [InlineData("Directory.Packages.props")]
    [InlineData("src/App/App.csproj")]
    [InlineData("renovate.json")]
    [InlineData("package.json")]
    [InlineData("package-lock.json")]
    [InlineData("appsettings.json")]
    public void SourceDependencyDeclarationsAreNeverSbomCandidates(string path) => SbomCandidates.Classify(path).Should().BeNull();

    /// <summary>The M2LB.Common → M2LB shape: M2LB.Common produces M2LB.Common 2.4.1; M2LB references it from 21 projects (15 at 2.4.1, 6 at 2.3.0).</summary>
    private static DependencyReviewResult CommonToApp()
    {
        var consumers = Enumerable.Range(1, 21).Select(i => new DeclaredDependency
        {
            Repository = "M2LB", Manager = "nuget", DepType = "nuget", PackageName = "M2LB.Common", CurrentValue = i <= 15 ? "2.4.1" : "2.3.0",
            OwnerFile = $"Service{i}/Service{i}.csproj", ReferencedBy = [$"Service{i}/Service{i}.csproj"],
        }).ToList();
        return new DependencyReviewResult
        {
            RunId = Guid.NewGuid(), CompletedAt = DateTimeOffset.Parse("2026-10-22T06:07:00Z"), Label = "M2LB.Common + M2LB",
            Repositories = [new() { Repository = "M2LB.Common", Coverage = RenovateCoverage.Configured }, new() { Repository = "M2LB", Coverage = RenovateCoverage.Configured, Dependencies = consumers }],
            SourceScope = new BirkNext.SourceEvidence.ReviewSourceScope
            {
                Primary = new BirkNext.SourceEvidence.SourceScopeEntry { SnapshotId = SourceScopeFixture.Common.SnapshotId, Repository = "M2LB.Common" },
                Related = [new BirkNext.SourceEvidence.SourceScopeEntry { SnapshotId = SourceScopeFixture.App.SnapshotId, Repository = "M2LB" }],
            },
            SourceRelationships = [new("M2LB", "M2LB.Common", ["2.3.0", "2.4.1"], 21, "M2LB.Common", "2.4.1")],
        };
    }

    [Fact]
    public void MappingShowsProducerAndConsumer_WithPerVersionConsumers_AndNoIncompatibilityClaim()
    {
        var mapping = DependencySourceResultPresentation.Mappings(CommonToApp()).Single();

        (mapping.Producer, mapping.Consumer, mapping.PackageName, mapping.ProducerVersion).Should().Be(("M2LB.Common", "M2LB", "M2LB.Common", "2.4.1"));
        mapping.ReferencingProjects.Should().Be(21);
        mapping.ConsumerVersions.Select(v => (v.Version, v.Projects.Count)).Should().Equal(("2.4.1", 15), ("2.3.0", 6));
        mapping.State.Should().Be(DependencyMappingState.MappedWithVersionDifference);
        DependencySourceResultPresentation.Label(mapping.State).Should().Be("Mapped with version difference");
        DependencySourceResultPresentation.WhyIncluded(CommonToApp(), "M2LB").Should().Be("M2LB contains explicit references to M2LB.Common, produced by M2LB.Common.");
    }

    [Fact]
    public void MappingRendersAsCompactProducerConsumerCard()
    {
        _api.Source = CommonToApp();
        var cut = Render<DependencyReview>();
        cut.Find("[data-testid='dr-primary']").Change(SourceScopeFixture.Common.SnapshotId.ToString());
        cut.Find("[data-testid='dr-next-action']").Click();
        cut.WaitForElement("[data-testid='dr-summary']");

        cut.FindAll("[data-testid='dr-section']").Single(s => s.GetAttribute("data-section") == "mapping").Click();

        var card = cut.Find("[data-testid='dr-mapping-card']");
        card.GetAttribute("aria-label").Should().Be("M2LB.Common produces M2LB.Common, used by M2LB", "the relationship is understandable without the arrow");
        card.QuerySelector("[data-testid='dr-mapping-references']")!.TextContent.Should().Be("21 project references · 2.4.1 / 2.3.0");
        cut.Find("[data-testid='dr-mapping-projects-toggle']").Click();
        cut.FindAll("[data-testid='dr-mapping-version']").Select(v => v.TextContent).Should().Equal("2.4.1 · 15 projects (same as producer)", "2.3.0 · 6 projects");
        cut.Find("[data-testid='dr-mapping-why']").TextContent.Should().Contain("M2LB contains explicit references to M2LB.Common");
        cut.Find("[data-testid='dr-mapping']").TextContent.Should().NotContainAny("Incompatible", "incompatible.", "Broken", "Conflict");
    }

    [Fact]
    public void NoMappingIsNeverCalledUnrelated()
    {
        var single = CommonToApp() with { SourceRelationships = [] };
        DependencySourceResultPresentation.Mappings(single).Should().BeEmpty();
        DependencySourceResultPresentation.NoMapping(single).Should().Contain("No cross-source dependency mapping found").And.Contain("does not mean the sources are unrelated");
        DependencySourceResultPresentation.NoMapping(single with { Repositories = [single.Repositories[0]] }).Should().Contain("Only one source was reviewed");
    }

    [Fact]
    public void CentralDeclarationAloneIsNotUsage()
    {
        var central = new DeclaredDependency { Repository = "M2LB.Common", Manager = "nuget", DepType = "nuget", PackageName = "Polly", CurrentValue = "8.4.0", OwnerFile = "Directory.Packages.props", ReferencedBy = [] };
        var result = CommonToApp() with { Repositories = [new() { Repository = "M2LB.Common", Dependencies = [central] }] };

        var row = DependencyUsagePresentation.Build(result).Rows.Single();
        row.State.Should().Be(DependencyUsageState.NoUsageFound);
        DependencyUsagePresentation.Label(row.State).Should().Be("No usage found in selected source scope");
    }

    [Fact]
    public void RenovateProcessingNamesItsCause()
    {
        DependencySourceResultPresentation.Processing(new DeclaredDependency()).State.Should().Be("Processed");
        DependencySourceResultPresentation.Processing(new DeclaredDependency { IgnoredBy = "Configured ignorePaths (docker-compose*)." }).State.Should().Be("Not processed: path excluded");
        DependencySourceResultPresentation.Processing(new DeclaredDependency { IgnoredBy = "The azure-pipelines manager is disabled by Renovate's default and not enabled in this configuration." }).State
            .Should().Be("Not processed by manager configuration");
        DependencySourceResultPresentation.Processing(new DeclaredDependency { IgnoredBy = "Configured ignoreDeps (Foo)." }).State.Should().Be("Ignored by Renovate");
    }

    [Fact]
    public void SourceReviewNeverClaimsRegistryAdvisoryOrLicenseResults()
    {
        _api.Source = CommonToApp();
        var cut = Render<DependencyReview>();
        cut.Find("[data-testid='dr-primary']").Change(SourceScopeFixture.Common.SnapshotId.ToString());
        cut.Find("[data-testid='dr-next-action']").Click();
        cut.WaitForElement("[data-testid='dr-summary']");

        cut.Find("[data-testid='dr-summary-outside']").TextContent.Should().Contain("Not assessed in source review");
        cut.Find("[data-testid='dr-result']").TextContent.Should().NotContainAny("matched advisor", "License metadata", "Latest published stable");
    }
}
