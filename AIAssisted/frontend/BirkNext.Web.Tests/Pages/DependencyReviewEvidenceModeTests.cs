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
        public InventoryImportResult? Capture { get; set; }
        public Task<InventoryImportResult> CaptureDeployedAsync(DeployedCaptureRequest request, CancellationToken ct = default)
        {
            if (Capture?.Inventory is { } inventory) Inventories.Insert(0, inventory);
            return Task.FromResult(Capture ?? throw new NotSupportedException());
        }
        public Func<DependencyHealthRequest, DependencyHealthRun>? Build { get; set; }
        public Task<(DependencyHealthRun? Run, string? Error)> RunHealthAsync(DependencyHealthRequest request, CancellationToken ct = default)
        {
            LastRequest = request;
            var run = (Build ?? HealthRun)(request);
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
        var targets = new Moq.Mock<IFrontendAnalysisSettingsService>();
        targets.SetupGet(t => t.IsLoaded).Returns(true);
        targets.SetupGet(t => t.Settings).Returns(new BirkNext.Web.Models.FrontendAnalysisSettings { Profiles =
            [new BirkNext.Web.Models.FrontendAnalysisProfile { Id = "dev", Name = "Development", EnvironmentType = BirkNext.Web.Models.FrontendEnvironmentType.Development, TargetUrl = "https://m2lbdev.example.test/" }] });
        Services.AddSingleton(targets.Object);
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

    private static DeploymentCaptureReport Report(CaptureOverall overall, params (string Key, string Name, CaptureStrategyOutcome Outcome, int Artifacts)[] strategies) => new()
    {
        Framework = "Blazor WebAssembly", RuntimeLayout = "Modern Blazor runtime", Overall = overall, Complete = overall == CaptureOverall.Captured,
        Capability = overall == CaptureOverall.Captured ? CaptureCapability.Supported : CaptureCapability.Partial,
        Summary = overall == CaptureOverall.NotCaptured ? "Deployment evidence not captured: no supported capture strategy produced deployment evidence (3 attempted)." : "Deployment evidence captured with limitations: 2 runtime artifacts observed.",
        Strategies = [.. strategies.Select(s => new CaptureStrategyResult { Strategy = s.Key, Name = s.Name, Outcome = s.Outcome, Artifacts = s.Artifacts, Detail = s.Outcome == CaptureStrategyOutcome.NotDetected ? "Not detected · HTTP 404." : "detail", Technical = "GET https://m2lbdev.example.test/_framework/blazor.boot.json → HTTP 404" })],
    };

    private IRenderedComponent<DependencyReview> ReadyToCapture()
    {
        var cut = Render<DependencyReview>();
        cut.Find("[data-testid='dr-mode'][data-mode='Deployed']").Click();
        cut.Find("[data-testid='dh-deployed-known']").Change(Common.Id.ToString());
        cut.Find("[data-testid='dh-deployed-target']").Change("dev");
        cut.Find("[data-testid='dr-next-action']").TextContent.Should().Be("Capture deployment evidence");
        return cut;
    }

    [Fact]
    public void NotCapturedDeployment_IsAResultWithAMethodBreakdown_NotARedBootManifestError()
    {
        _api.Capture = new InventoryImportResult(null, null, "Deployment evidence not captured") { Capture = Report(CaptureOverall.NotCaptured,
            ("classic-boot-manifest", "Classic Blazor boot manifest", CaptureStrategyOutcome.NotDetected, 0), ("modern-runtime", "Blazor runtime metadata", CaptureStrategyOutcome.Detected, 0),
            ("browser-runtime", "Browser runtime evidence", CaptureStrategyOutcome.NotAvailable, 0)) };
        var cut = ReadyToCapture();

        cut.Find("[data-testid='dr-next-action']").Click();

        cut.FindAll("[data-testid='dh-error']").Should().BeEmpty("a 404 from one method is not a terminal red error");
        cut.Find("[data-testid='dh-capture-summary']").TextContent.Should().StartWith("Not captured").And.Contain("no supported capture strategy produced deployment evidence");
        cut.Find("[data-testid='dh-capture-capability']").TextContent.Should().StartWith("Partial");
        cut.Find("[data-testid='dh-capture-details-body']").HasAttribute("hidden").Should().BeTrue("technical strategy details are secondary");
        cut.Find("[data-testid='dh-capture-details-toggle']").Click();
        cut.Find("[data-testid='dh-capture-strategy'][data-strategy='classic-boot-manifest']").TextContent.Should().Contain("Not detected").And.Contain("HTTP 404");
        cut.Find("[data-testid='dr-readiness-state']").TextContent.Should().Be("Not ready");
        cut.Find("[data-testid='dh-deployed-facts']").TextContent.Should().Contain("Deployment evidenceNot captured");
    }

    [Fact]
    public void PartialCapture_LeadsWithLimitations_AndComparisonCanContinue()
    {
        var deployed = Common with { Id = Guid.NewGuid(), Name = "m2lbdev.example.test (Development) · deployed artifacts", SourceType = InventorySourceType.Deployment, Stage = InventoryStage.Deployed, Dependencies = 2 };
        _api.Capture = new InventoryImportResult(null, deployed, null) { Capture = Report(CaptureOverall.Partial, ("browser-runtime", "Browser runtime evidence", CaptureStrategyOutcome.Partial, 2)) };
        var cut = ReadyToCapture();

        cut.Find("[data-testid='dr-next-action']").Click();

        cut.Find("[data-testid='dh-capture-summary']").TextContent.Should().StartWith("Captured with limitations");
        cut.Find("[data-testid='dh-capture-facts']").TextContent.Should().Contain("Capture methods usedBrowser runtime evidence").And.Contain("Package versionsNot available from this evidence")
            .And.Contain("Not available (browser evidence has no integrity values)");
        cut.Find("[data-testid='dr-readiness-state']").TextContent.Should().Be("Ready to run");
        cut.Find("[data-testid='dr-next-action']").TextContent.Should().Be("Run deployment comparison");
    }

    [Fact]
    public void DeploymentComparisonResult_ShowsCounts_ArtifactsAndTarget_WithoutPackageVersions()
    {
        var deployedSnapshot = new DependencyInventorySnapshot
        {
            Id = Guid.NewGuid(), Name = "deployed", Environment = "Development", SourceType = InventorySourceType.Deployment, Stage = InventoryStage.Deployed, CapturedAt = DateTimeOffset.Parse("2026-10-22T09:00:00Z"),
            Capture = Report(CaptureOverall.Partial, ("browser-runtime", "Browser runtime evidence", CaptureStrategyOutcome.Partial, 2)),
            Dependencies = [new InventoryDependency { PackageName = "WolverineFx.wasm", PackageManager = "assembly", ObservedBy = ["Browser runtime evidence"] }, new InventoryDependency { PackageName = "dotnet.native.wasm", PackageManager = "runtime artifact", ObservedBy = ["Browser runtime evidence"] }],
        };
        var deployed = Common with { Id = deployedSnapshot.Id, Name = "deployed", SourceType = InventorySourceType.Deployment, Stage = InventoryStage.Deployed };
        _api.Capture = new InventoryImportResult(null, deployed, null) { Capture = deployedSnapshot.Capture };
        _api.Build = r => HealthRun(r) with
        {
            Deployed = deployedSnapshot,
            Comparisons = [new InventoryComparison { Kind = ComparisonKind.ExpectedVsDeployed, LeftName = Common.Name, RightName = "deployed", Unchanged = 0, Entries =
            [
                new ComparisonEntry { PackageName = "WolverineFx.wasm", State = ComparisonState.MappingUnresolved, Detail = "no authoritative mapping" },
                new ComparisonEntry { PackageName = "dotnet.native.wasm", State = ComparisonState.ObservedNotExpected, Detail = "not mapped" },
                new ComparisonEntry { PackageName = "Polly", State = ComparisonState.NotObservedInEvidence, Detail = "absence is not concluded" },
            ] }],
        };
        var cut = ReadyToCapture();
        cut.Find("[data-testid='dr-next-action']").Click();
        cut.Find("[data-testid='dr-next-action']").Click();
        cut.WaitForElement("[data-testid='dh-result']");

        cut.Find("[data-testid='dh-summary-differences']").TextContent.Should().Contain("Observed artifacts2").And.Contain("Expected not observed1").And.Contain("Unexpected1").And.Contain("Unresolved mapping1");
        cut.Find("[data-testid='dh-partial-comparison']").TextContent.Should().Contain("not a definite absence");
        cut.FindAll("[data-testid='dh-section']").Single(s => s.GetAttribute("data-section") == "dependencies").TextContent.Should().Be("Deployed artifacts");
        cut.FindAll("[data-testid='dh-section']").Single(s => s.GetAttribute("data-section") == "dependencies").Click();
        var wolverine = cut.Find("[data-testid='dh-artifact'][data-artifact='WolverineFx.wasm']").TextContent;
        wolverine.Should().Contain("Mapping unresolved").And.Contain("Browser runtime evidence").And.NotContain("6.51");
        cut.Find("[data-testid='dh-result']").TextContent.Should().Contain("Package version: not available from deployment evidence");
        cut.FindAll("[data-testid='dh-section']").Single(s => s.GetAttribute("data-section") == "differences").Click();
        cut.Find("[data-testid='dh-difference-groups']").TextContent.Should().Contain("Unresolved mapping").And.Contain("Observations");
        cut.FindAll("[data-testid='dh-section']").Single(s => s.GetAttribute("data-section") == "target").Click();
        cut.Find("[data-testid='dh-target-capture']").TextContent.Should().Contain("Blazor WebAssembly");
    }

    [Fact]
    public void A_run_is_current_only_for_the_evidence_selected_in_its_mode_and_stays_in_history()
    {
        var cut = RunInventoryReview();
        cut.Find("[data-testid='dh-inventory']").Change("");
        cut.FindAll("[data-testid='dh-result']").Should().BeEmpty("no inventory is selected, so no run is the current result");
        cut.Find("[data-testid='dr-history-disclosure-toggle']").Click();
        cut.FindAll("[data-testid='dh-history'] option").Skip(1).Should().ContainSingle("the run stays in History");
    }

    [Fact]
    public void Zero_matched_advisories_with_unavailable_coverage_is_never_presented_as_zero_vulnerabilities()
    {
        var cut = RunInventoryReview();
        cut.Find("[data-testid='dh-security-badge']").TextContent.Should().Be("Coverage incomplete");
        cut.Find("[data-testid='dh-advisory-coverage']").TextContent.Should().StartWith("No matched advisories in available evidence").And.Contain("advisory coverage incomplete (1 of 1");
        cut.Find("[data-testid='dh-headline']").TextContent.Should().Contain("No matched advisories in available evidence").And.NotContain("0 affected");
        cut.Markup.Should().NotContain("0 vulnerabilities");
        cut.FindAll("[data-testid='dh-coverage-dimension']").Select(d => d.TextContent.Split(':')[0]).Should()
            .Equal("Inventory coverage", "Registry coverage", "Advisory coverage", "License coverage", "Policy coverage", "Deployment coverage");
    }

    [Fact]
    public void Each_mode_names_the_evidence_providers_it_uses()
    {
        var cut = Render<DependencyReview>();
        cut.Find("[data-testid='dr-mode'][data-mode='Sbom']").Click();
        cut.FindAll("[data-testid='dr-mode-provider']").Select(p => p.GetAttribute("data-provider")).Should().Contain(["sbom", "registry-metadata", "security-advisories", "license-metadata"]).And.NotContain("source-snapshot");
        cut.Find("[data-testid='dr-mode'][data-mode='Deployed']").Click();
        cut.Find("[data-testid='dr-mode-provider'][data-provider='deployed-capture']").TextContent.Should().Contain("One failed method is not 'no deployment evidence'");
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
