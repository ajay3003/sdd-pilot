using BirkNext.Dependencies;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// Dependency Review evidence-source cards and the source-free dependency health review: stored inventory selection without any archive,
/// SBOM upload with validation, deployed evidence capture, summary/table/filters/detail, refresh as a new run, history opened as recorded,
/// export, and the wording rules (latest published stable, no matched advisories observed, security evidence unavailable, not configured).
/// </summary>
public sealed class DependencyHealthPageTests : BunitContext
{
    private sealed class FakeApi : IDependencyReviewApiService
    {
        public List<InventorySummary> Inventories { get; } = [];
        public List<DependencyHealthRunSummary> HealthRuns { get; } = [];
        public Dictionary<Guid, DependencyHealthRun> Stored { get; } = [];
        public DependencyHealthRequest? LastRequest { get; private set; }
        public Guid? LastRefresh { get; private set; }
        public (string FileName, SbomRole Role, string? Environment)? LastImport { get; private set; }
        public InventoryImportResult? ImportResult { get; set; }
        public InventoryImportResult? CaptureResult { get; set; }
        public DeployedCaptureRequest? LastCapture { get; private set; }
        public Func<DependencyHealthRequest, DependencyHealthRun>? Build { get; set; }
        public int SourceUploads { get; private set; }

        public Task<(DependencyReviewResult? Result, string? Error)> RunAsync(string label, IReadOnlyList<(string FileName, Stream Content)> archives, (string Repository, string FileName, Stream Content)? configOverride, CancellationToken ct = default)
        { SourceUploads++; return Task.FromResult<(DependencyReviewResult?, string?)>((null, "not used")); }
        public Task<IReadOnlyList<DependencyReviewRunSummary>> HistoryAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<DependencyReviewRunSummary>>([]);
        public Task<DependencyReviewResult?> GetAsync(Guid runId, CancellationToken ct = default) => Task.FromResult<DependencyReviewResult?>(null);
        public Task<(PolicySimulation? Simulation, string? Error)> SimulateAsync(PolicySimulationRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<InventorySummary>> InventoriesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<InventorySummary>>(Inventories.ToList());

        public Task<InventoryImportResult> ImportInventoryAsync(string fileName, Stream content, SbomRole role, string? environment, string? name, CancellationToken ct = default)
        {
            LastImport = (fileName, role, environment);
            if (ImportResult?.Inventory is { } inventory) Inventories.Insert(0, inventory);
            return Task.FromResult(ImportResult!);
        }

        public Task<InventoryImportResult> CaptureDeployedAsync(DeployedCaptureRequest request, CancellationToken ct = default)
        {
            LastCapture = request;
            if (CaptureResult?.Inventory is { } inventory) Inventories.Insert(0, inventory);
            return Task.FromResult(CaptureResult!);
        }

        public Task<(DependencyHealthRun? Run, string? Error)> RunHealthAsync(DependencyHealthRequest request, CancellationToken ct = default)
        {
            LastRequest = request;
            var run = (Build ?? (r => Run(r)))(request);
            Stored[run.RunId] = run;
            HealthRuns.Insert(0, new DependencyHealthRunSummary(run.RunId, run.CompletedAt, run.Label, run.Inventory.Name, run.Summary.Dependencies, 1, run.RefreshOf));
            return Task.FromResult<(DependencyHealthRun?, string?)>((run, null));
        }

        public Task<IReadOnlyList<DependencyHealthRunSummary>> HealthHistoryAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<DependencyHealthRunSummary>>(HealthRuns.ToList());
        public Task<DependencyHealthRun?> GetHealthAsync(Guid runId, CancellationToken ct = default) => Task.FromResult(Stored.GetValueOrDefault(runId));

        public Task<(DependencyHealthRun? Run, string? Error)> RefreshHealthAsync(Guid runId, CancellationToken ct = default)
        {
            LastRefresh = runId;
            var run = Run(Stored[runId].Request, latest: "1.3.0") with { RefreshOf = runId };
            Stored[run.RunId] = run;
            HealthRuns.Insert(0, new DependencyHealthRunSummary(run.RunId, run.CompletedAt, run.Label, run.Inventory.Name, 2, 1, runId));
            return Task.FromResult<(DependencyHealthRun?, string?)>((run, null));
        }
    }

    private static readonly InventorySummary Build = new()
    {
        Id = Guid.NewGuid(), Name = "Build 2026.09.28", SourceType = InventorySourceType.Sbom, SourceName = "m2lb.cdx.json", Stage = InventoryStage.Packaged,
        CapturedAt = DateTimeOffset.Parse("2026-09-28T15:10:00Z"), RecordedAt = DateTimeOffset.Parse("2026-09-28T15:12:00Z"), Dependencies = 2, Freshness = InventoryFreshness.Current, FreshnessDetail = "Captured 1 day(s) ago.",
    };

    private static readonly InventorySummary OldSource = Build with
    {
        Id = Guid.NewGuid(), Name = "M2LB · source review 2026-06-01", SourceType = InventorySourceType.SourceReview, Stage = InventoryStage.Declared, Repository = "M2LB", SourceRunId = Guid.NewGuid(),
        CapturedAt = DateTimeOffset.Parse("2026-06-01T00:00:00Z"), Freshness = InventoryFreshness.Stale, FreshnessDetail = "Captured 120 day(s) ago (stale after 30 days, DependencyReview:InventoryStaleAfterDays).",
    };

    private static DependencyHealthItem Item(string name, string version, VersionStatus status, AdvisoryState security, string? latest, LicenseState license = LicenseState.Detected) => new()
    {
        Key = $"nuget:{name.ToLowerInvariant()}@{version}", Locations = [$"component {name}"],
        Dependency = new InventoryDependency { PackageName = name, Version = version, PackageManager = "nuget", Stage = InventoryStage.Packaged, Relationship = DependencyRelationship.Direct, Location = $"component {name}",
            Purl = $"pkg:nuget/{name}@{version}", Hashes = [new DependencyHash("SHA-256", new string('a', 64))], Licenses = license == LicenseState.Detected ? ["MIT"] : [], LicenseFieldPresent = true },
        Registry = new RegistryObservation { Registry = "nuget.org", State = RegistryState.Observed, LatestStable = latest, ObservedPublished = DateTimeOffset.Parse("2025-01-10T00:00:00Z"), RetrievedAt = DateTimeOffset.Parse("2026-09-29T09:45:00Z"), Detail = "Observed version is listed." },
        VersionStatus = status, VersionDetail = $"{DependencyHealthLabels.LatestStable} is {latest}. Being behind is not a defect by itself.", ObservedAgeDays = 627,
        Security = security switch
        {
            AdvisoryState.Affected => new SecurityEvidence { State = security, Source = "OSV (osv.dev)", RetrievedAt = DateTimeOffset.Parse("2026-09-29T09:46:00Z"), FixedIn = "2.0.0",
                Detail = "1.0.0 is inside the affected range of GHSA-major-fix-0001. Exploitability is not assessed. Fixed in 2.0.0 per the advisory source — not a tested or recommended upgrade.",
                Advisories = [new AdvisoryObservation { Id = "GHSA-major-fix-0001", Aliases = ["CVE-2026-1000"], AffectedRanges = ["introduced 0, fixed 2.0.0"], FixedVersions = ["2.0.0"], AffectsObservedVersion = true, SourceSeverity = "HIGH", Source = "OSV (osv.dev)", Url = "https://osv.dev/vulnerability/GHSA-major-fix-0001" }] },
            AdvisoryState.AdvisorySourceUnavailable => new SecurityEvidence { State = security, Source = "OSV (osv.dev)", Detail = "Security evidence unavailable: OSV is unavailable (HTTP 503). This is not \"0 vulnerabilities\"." },
            _ => new SecurityEvidence { State = security, Source = "OSV (osv.dev)", Detail = "No matched advisories observed in OSV (osv.dev) for the package. This is not a statement that the dependency is safe." },
        },
        License = new LicenseEvidence { State = license, Licenses = license == LicenseState.Detected ? ["MIT"] : [], Sources = ["SBOM"], Policy = LicensePolicyState.NotConfigured, Detail = "License policy: not configured — no approval or compliance claim." },
        Remediation = security == AdvisoryState.Affected ? new RemediationPolicyCheck { State = RemediationPolicyState.BlockedByPolicy, FixedVersion = "2.0.0", Explanation = "Security remediation requires a version currently blocked by Renovate policy. The configuration is not changed.", PolicySource = "Renovate policy snapshot of M2LB" } : null,
        Stages =
        [
            new(InventoryStage.Declared, "Not assessed", null, null), new(InventoryStage.Resolved, "Not assessed", null, null), new(InventoryStage.Packaged, "Observed", version, "Build 2026.09.28"),
            new(InventoryStage.Deployed, "Not assessed", null, null), new(InventoryStage.RuntimeObserved, "Not assessed", null, "No evidence source proves runtime loading."),
        ],
    };

    private static DependencyHealthRun Run(DependencyHealthRequest request, string latest = "1.2.0") => new()
    {
        RunId = Guid.NewGuid(), CompletedAt = DateTimeOffset.Parse("2026-09-29T09:47:00Z"), Label = "Build 2026.09.28", Request = request,
        Inventory = new DependencyInventorySnapshot { Id = request.InventoryId, Name = "Build 2026.09.28", SourceType = InventorySourceType.Sbom, Stage = InventoryStage.Packaged, CapturedAt = DateTimeOffset.Parse("2026-09-28T15:10:00Z"), Provenance = "CycloneDX JSON 1.5 m2lb.cdx.json" },
        Freshness = InventoryFreshness.Current, FreshnessDetail = "Captured 1 day(s) ago.",
        Items = [Item("Foo", "1.0.0", VersionStatus.MajorBehind, AdvisoryState.Affected, "2.0.0"), Item("Bar", "2.1.0", VersionStatus.MinorBehind, AdvisoryState.NoMatchedAdvisoryObserved, latest),
                 Item("Baz", "3.0.0", VersionStatus.Current, AdvisoryState.AdvisorySourceUnavailable, "3.0.0", LicenseState.NotDeclared)],
        Summary = new DependencyHealthSummary { Dependencies = 3, RegistryObserved = 3, Current = 1, MinorBehind = 1, MajorBehind = 1, AffectedDependencies = 1, AdvisorySourceUnavailable = 1, NoMatchedAdvisory = 1, LicenseDetected = 2, LicenseUnknown = 1 },
        Categories =
        [
            new("Inventory", ReviewCategoryState.Ready, "SBOM · Packaged · 3 dependencies"), new("Version health", ReviewCategoryState.Ready, "0 patch behind · 1 minor behind · 1 major behind"),
            new("Security advisories", ReviewCategoryState.NeedsReview, "1 affected · 1 advisory source unavailable"), new("Deprecated / unlisted", ReviewCategoryState.Ready, "0 deprecated"),
            new("License metadata", ReviewCategoryState.Partial, "2 detected · 1 unknown · policy not configured"), new("Renovate policy", ReviewCategoryState.NotAssessed, "Not assessed — no source/config selected."),
            new("Automation", ReviewCategoryState.NotConfigured, "Not configured."), new("Deployment comparison", ReviewCategoryState.NotAssessed, "Not assessed — no deployed evidence selected."),
        ],
        Sources =
        [
            new EvidenceSourceStatus { Category = CheckCategory.Registry, Name = "nuget.org", State = "Used", RetrievedAt = DateTimeOffset.Parse("2026-09-29T09:45:00Z"), Detail = "GET-only metadata lookups." },
            new EvidenceSourceStatus { Category = CheckCategory.Security, Name = "OSV (osv.dev)", State = "Partial", Detail = "1 unavailable (reported as \"Security evidence unavailable\", never as 0)." },
        ],
        Automation = new AutomationEvidence { State = AutomationState.NotConfigured, Provider = "Azure DevOps", Detail = "No repository provider is configured." },
        Observations =
        [
            new HealthObservation { Id = "advisory:GHSA-major-fix-0001", Category = CheckCategory.Security, Kind = ObservationKind.Finding, Severity = DependencyFindingSeverity.NeedsReview, SourceSeverity = "HIGH", PackageName = "Foo", Title = "Foo 1.0.0 is affected by GHSA-major-fix-0001", Detail = "Exploitability is not assessed." },
            new HealthObservation { Id = "advisory-unavailable", Category = CheckCategory.Security, Kind = ObservationKind.Limitation, Title = "Security evidence unavailable for 1 dependency", Detail = "The advisory source did not answer for them; this is not \"0 vulnerabilities\"." },
        ],
        Limitations = ["Latest published stable is what the registry lists — not a recommended version. Outdated is not vulnerable.", "No matched advisory is not a safety claim; vulnerable is not proven exploitable."],
    };

    private readonly FakeApi _api = new();

    public DependencyHealthPageTests()
    {
        _api.Inventories.AddRange([Build, OldSource]);
        Services.AddSingleton<IDependencyReviewApiService>(_api);
        Services.AddSingleton<IReportExportService, ReportExportService>();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private IRenderedComponent<DependencyReview> RunStored()
    {
        var cut = Render<DependencyReview>();
        cut.Find("[data-testid='dh-inventory']").Change(Build.Id.ToString());
        cut.Find("[data-testid='dh-run']").Click();
        cut.WaitForElement("[data-testid='dh-result']");
        return cut;
    }

    [Fact]
    public void EvidenceSourceCardsSaySourceReviewIsOneOfFour()
    {
        var cut = Render<DependencyReview>();

        cut.FindAll("[data-testid='dr-sources'] > section").Select(s => s.GetAttribute("data-testid")).Should().Equal("dr-setup", "dh-inventory-card", "dh-sbom-card", "dh-deployed-card");
        cut.Find("[data-testid='dr-setup']").TextContent.Should().Contain("Review from source").And.Contain("Test Renovate policy");
        cut.Find("[data-testid='dh-inventory-card']").TextContent.Should().Contain("without source upload").And.Contain("Cannot assess");
        cut.Find("[data-testid='dh-run']").HasAttribute("disabled").Should().BeTrue();
        cut.Find("[data-testid='dh-sbom-run']").HasAttribute("disabled").Should().BeTrue();
        cut.FindComponents<InputFile>().Select(f => f.Instance.AdditionalAttributes!["aria-label"]).Should().Equal("Choose repository archives (.zip)", "Choose a Renovate config file", "Choose an SBOM or packages.lock.json");
    }

    [Fact]
    public void StoredInventoryIsReviewedWithoutUploadingSource()
    {
        var cut = Render<DependencyReview>();

        cut.Find("[data-testid='dh-inventory']").Change(OldSource.Id.ToString());
        cut.Find("[data-testid='dh-inventory-freshness']").TextContent.Should().Contain("Stale").And.Contain("120 day(s)");
        cut.Find("[data-testid='dh-inventory']").Change(Build.Id.ToString());
        cut.Find("[data-testid='dh-run']").Click();

        _api.SourceUploads.Should().Be(0);
        _api.LastRequest!.InventoryId.Should().Be(Build.Id);
        _api.LastRequest.PolicyRunId.Should().BeNull();
        cut.Find("[data-testid='dh-tile-policy']").TextContent.Should().Contain("Not assessed");
        cut.Find("[data-testid='dh-tile-automation']").TextContent.Should().Contain("Not configured");
        cut.Find("[data-testid='dh-tile-deployment']").TextContent.Should().Contain("Not assessed");
        cut.Find("[data-testid='dh-tile-security']").TextContent.Should().Contain("1 affected").And.Contain("1 advisory source unavailable");
        cut.Find("[data-testid='dh-tile-version']").TextContent.Should().Contain("2 updates observed");
        cut.Find("[data-testid='dh-status']").TextContent.Should().Contain("stored");
    }

    [Fact]
    public void RenovatePolicyAndComparisonsAreSentOnlyWhenChosen()
    {
        var cut = Render<DependencyReview>();
        cut.Find("[data-testid='dh-inventory']").Change(Build.Id.ToString());
        cut.Find("[data-testid='dh-options-toggle']").Click();
        cut.Find("[data-testid='dh-policy']").Change(OldSource.Id.ToString());
        cut.Find("[data-testid='dh-baseline']").Change(OldSource.Id.ToString());

        cut.Find("[data-testid='dh-run']").Click();

        _api.LastRequest!.PolicyRunId.Should().Be(OldSource.SourceRunId);
        _api.LastRequest.PolicyRepository.Should().Be("M2LB");
        _api.LastRequest.BaselineInventoryId.Should().Be(OldSource.Id);
        _api.LastRequest.ComparisonInventoryId.Should().BeNull();
        _api.LastRequest.DeployedInventoryId.Should().BeNull();
    }

    [Fact]
    public void WordingNeverClaimsSafeRecommendedOrZero()
    {
        var cut = RunStored();

        var result = cut.Find("[data-testid='dh-result']");
        result.QuerySelectorAll("th").Select(h => h.TextContent).Should().Contain(DependencyHealthLabels.LatestStable);
        cut.Find("[data-testid='dh-filter'][data-filter='All']").Click();
        var security = cut.FindAll("[data-testid='dh-row-security']").Select(s => s.TextContent).ToList();
        security.Should().Contain("No matched advisories observed").And.Contain("Security evidence unavailable").And.Contain("Affected (1)");
        result.TextContent.Should().NotContain("Recommended").And.NotContain("Safe ").And.NotContain("0 vulnerabilities found");
    }

    [Fact]
    public void IssuesFilterIsDefaultAndFiltersNarrowTheTable()
    {
        var cut = RunStored();

        cut.Find("[data-testid='dh-filter'][data-filter='Issues']").GetAttribute("aria-pressed").Should().Be("true");
        cut.FindAll("[data-testid='dh-row']").Select(r => r.GetAttribute("data-package")).Should().Equal("Foo");
        cut.Find("[data-testid='dh-filter'][data-filter='Security']").Click();
        cut.FindAll("[data-testid='dh-row']").Select(r => r.GetAttribute("data-package")).Should().BeEquivalentTo("Foo", "Baz");
        cut.Find("[data-testid='dh-filter'][data-filter='LicenseUnknown']").Click();
        cut.FindAll("[data-testid='dh-row']").Should().ContainSingle().Which.GetAttribute("data-package").Should().Be("Baz");
        cut.Find("[data-testid='dh-filter'][data-filter='All']").Click();
        cut.Find("[data-testid='dh-search']").Input("ba");
        cut.FindAll("[data-testid='dh-row']").Select(r => r.GetAttribute("data-package")).Should().BeEquivalentTo("Bar", "Baz");
        cut.Find("[data-testid='dh-row-count']").GetAttribute("role").Should().Be("status");
    }

    [Fact]
    public void DetailPanelShowsProvenanceStagesAdvisoryAndPolicyCrossCheck()
    {
        var cut = RunStored();

        var toggle = cut.Find("[data-testid='dh-row'][data-package='Foo'] [data-testid='dh-row-toggle']");
        toggle.GetAttribute("aria-expanded").Should().Be("false");
        toggle.Click();

        cut.Find("[data-testid='dh-row'][data-package='Foo'] [data-testid='dh-row-toggle']").GetAttribute("aria-expanded").Should().Be("true");
        var detail = cut.Find("[data-testid='dh-detail']");
        detail.TextContent.Should().Contain("component Foo").And.Contain("pkg:nuget/Foo@1.0.0").And.Contain("no age threshold applied");
        cut.Find("[data-testid='dh-stages'] [data-stage='RuntimeObserved']").TextContent.Should().Contain("Runtime loaded").And.Contain("Not assessed");
        cut.Find("[data-testid='dh-stages'] [data-stage='Packaged']").TextContent.Should().Contain("1.0.0");
        cut.Find("[data-testid='dh-detail-fixed']").TextContent.Should().Contain("2.0.0").And.Contain("not a tested or recommended upgrade");
        cut.Find("[data-testid='dh-detail-remediation']").TextContent.Should().Contain("blocked by Renovate policy").And.Contain("not changed");
        cut.Find("[data-testid='dh-detail-license']").TextContent.Should().Contain("Policy not configured");
        cut.Find("[data-testid='dh-advisories']").TextContent.Should().Contain("CVE-2026-1000").And.Contain("HIGH");
        cut.Find("[data-testid='dh-detail-close']").Click();
        cut.FindAll("[data-testid='dh-detail']").Should().BeEmpty();
    }

    [Fact]
    public void SbomUploadValidatesThenReviewsTheNewInventory()
    {
        var sbom = Build with { Id = Guid.NewGuid(), Name = "QA deployed SBOM", Stage = InventoryStage.Deployed, Environment = "qa" };
        _api.ImportResult = new InventoryImportResult(new SbomValidation { Format = SbomFormat.CycloneDxJson, SpecVersion = "1.5", Valid = true, Components = 2, Direct = 1, Transitive = 1, Ecosystems = new() { ["nuget"] = 2 }, HasDependencyGraph = true, WithHashes = 2 }, sbom, null);
        var cut = Render<DependencyReview>();

        cut.FindComponents<InputFile>()[2].UploadFiles(InputFileContent.CreateFromText("{}", "qa.cdx.json"));
        cut.Find("[data-testid='dh-sbom-role']").Change(SbomRole.DeployedArtifact.ToString());
        cut.Find("[data-testid='dh-sbom-env']").Change("qa");
        cut.Find("[data-testid='dh-sbom-run']").Click();
        cut.WaitForElement("[data-testid='dh-result']");

        _api.LastImport.Should().Be(("qa.cdx.json", SbomRole.DeployedArtifact, "qa"));
        _api.LastRequest!.InventoryId.Should().Be(sbom.Id);
        cut.Find("[data-testid='dh-sbom-validation']").GetAttribute("data-valid").Should().Be("true");
        cut.Find("[data-testid='dh-sbom-coverage']").TextContent.Should().Contain("2 components").And.Contain("1 transitive").And.Contain("Completeness is not claimed");
    }

    [Fact]
    public void InvalidSbomShowsValidationErrorsAndRunsNothing()
    {
        _api.ImportResult = new InventoryImportResult(new SbomValidation { Format = SbomFormat.CycloneDxJson, Valid = false, Errors = ["/components/0: Required properties [\"type\"] are not present"] }, null,
            "The document is not a valid CycloneDX JSON; no inventory was created.");
        var cut = Render<DependencyReview>();

        cut.FindComponents<InputFile>()[2].UploadFiles(InputFileContent.CreateFromText("{}", "bad.json"));
        cut.Find("[data-testid='dh-sbom-run']").Click();

        cut.Find("[data-testid='dh-error']").GetAttribute("role").Should().Be("alert");
        cut.Find("[data-testid='dh-error']").TextContent.Should().Contain("no inventory was created");
        cut.Find("[data-testid='dh-sbom-validation']").TextContent.Should().Contain("Not valid").And.Contain("Required properties");
        _api.LastRequest.Should().BeNull();
        cut.FindAll("[data-testid='dh-result']").Should().BeEmpty();
    }

    [Fact]
    public void DeployedCaptureIsSelectedForTheNextReview()
    {
        var deployed = Build with { Id = Guid.NewGuid(), Name = "qa.example.test (qa) · deployed assemblies", SourceType = InventorySourceType.Deployment, Stage = InventoryStage.Deployed, Dependencies = 40 };
        _api.CaptureResult = new InventoryImportResult(null, deployed, null);
        var cut = Render<DependencyReview>();

        cut.Find("[data-testid='dh-deployed-url']").Input("https://qa.example.test");
        cut.Find("[data-testid='dh-deployed-capture']").Click();
        cut.Find("[data-testid='dh-status']").TextContent.Should().Contain("40 deployed assemblies").And.Contain("versions not proven");
        cut.Find("[data-testid='dh-inventory']").Change(Build.Id.ToString());
        cut.Find("[data-testid='dh-run']").Click();

        _api.LastCapture!.TargetUrl.Should().Be("https://qa.example.test");
        _api.LastRequest!.DeployedInventoryId.Should().Be(deployed.Id);
    }

    [Fact]
    public void RefreshCreatesANewRunAndHistoryOpensTheOldOneAsRecorded()
    {
        var cut = RunStored();
        var first = _api.HealthRuns.Single().RunId;

        cut.Find("[data-testid='dh-refresh']").Click();

        _api.LastRefresh.Should().Be(first);
        cut.Find("[data-testid='dh-status']").TextContent.Should().Contain("new run").And.Contain("earlier run is unchanged");
        cut.Find("[data-testid='dh-run-meta']").TextContent.Should().Contain("refresh of an earlier run");
        cut.Find("[data-testid='dh-history']").Change(first.ToString());
        cut.Find("[data-testid='dh-result']").GetAttribute("data-run").Should().Be(first.ToString());
        cut.Find("[data-testid='dh-status']").TextContent.Should().Contain("exactly as recorded");
        cut.Find("[data-testid='dh-filter'][data-filter='All']").Click();
        cut.Find("[data-testid='dh-row'][data-package='Bar']").TextContent.Should().Contain("1.2.0");
    }

    [Fact]
    public void AutomationRuntimeIsSeparateFromStaticConfiguration()
    {
        _api.Build = r => Run(r) with
        {
            Automation = new AutomationEvidence
            {
                State = AutomationState.Observed, Provider = "Azure DevOps", Detail = "1 open Renovate PR(s). A successful run does not mean dependencies are current.", DependencyDashboard = "Not read",
                StaticConfiguration = "Renovate configuration (static, from source review 2026-09-28 10:00 UTC): Ready — Configured in renovate.json.",
                LastSuccessfulRun = new AutomationRun("20260921.1", null, DateTimeOffset.Parse("2026-09-21T02:09:00Z"), "succeeded", null),
                OpenPullRequests = [new DependencyPullRequest { Id = "17", Title = "Update dependency Serilog to v3.1.2", PackageName = "Serilog", FromVersion = "3.1.1", ToVersion = "3.1.2", UpdateType = DependencyUpdateType.Patch, AgeDays = 12, Status = "active" }],
            },
        };
        var cut = RunStored();

        cut.Find("[data-testid='dh-automation-state']").TextContent.Should().Be("Observed");
        cut.Find("[data-testid='dh-automation-static']").TextContent.Should().Contain("static");
        var pr = cut.Find("[data-testid='dh-pr']").TextContent;
        pr.Should().Contain("3.1.1 → 3.1.2").And.Contain("Patch").And.Contain("12 days").And.Contain("Observed · active").And.Contain("Not provided");
        pr.Should().NotContain("stale", "no stale threshold is configured");
        cut.Find("[data-testid='dh-automation']").TextContent.Should().Contain("not an upgrade that is ready or safe");
    }

    [Fact]
    public void ExportCarriesEverySectionFromTheSnapshot()
    {
        var cut = RunStored();

        cut.Find("[data-testid='dh-export']").Click();

        JSInterop.Invocations.Should().Contain(i => i.Identifier == "downloadHtmlFile");
        var html = new ReportExportService().ExportDependencyHealth(_api.Stored.Values.Single());
        foreach (var section in new[] { "Review sources", "Inventory summary", "Dependency health", "Registry metadata", "Security advisories", "License metadata", "Supply-chain metadata", "Renovate policy", "Automation runtime", "Deployment evidence", "Drift / baseline", "Limitations", "Findings" })
            html.Should().Contain($"<h2>{section}</h2>");
        html.Should().Contain("No matched advisories observed").And.Contain("Security evidence unavailable").And.Contain("not a safety claim").And.Contain(DependencyHealthLabels.LatestStable);
    }

    [Fact]
    public void PresentationLabelsKeepSemanticsDistinct()
    {
        DependencyHealthPresentation.CategoryLabel(ReviewCategoryState.Ready).Should().Be("Assessed");
        DependencyHealthPresentation.Tone(ReviewCategoryState.Ready).Should().NotBe("ok", "assessed is not a green pass");
        DependencyHealthLabels.Registry(RegistryState.RegistryUnavailable).Should().NotBe(DependencyHealthLabels.Registry(RegistryState.PackageNotFound));
        DependencyHealthLabels.Stage(InventoryStage.Declared).Should().NotBe(DependencyHealthLabels.Stage(InventoryStage.Deployed));
        DependencyHealthLabels.LicensePolicy(LicensePolicyState.NotConfigured).Should().Be("Policy not configured");
        DependencyHealthPresentation.IsIssue(Item("Bar", "2.1.0", VersionStatus.MajorBehind, AdvisoryState.NoMatchedAdvisoryObserved, "3.0.0")).Should().BeFalse("being behind alone is not an issue");
    }
}
