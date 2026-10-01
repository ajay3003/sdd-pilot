using BirkNext.Dependencies;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// Dependency Review page: per-repository Renovate coverage (M2LB configured, M2LB.Common missing), findings with Needs review first, the
/// synthetic simulation matrix labelled "Synthetic candidate" (never latest/available/published), distinct outcomes, the single-package
/// simulator against the stored run, history and export.
/// </summary>
public sealed class DependencyReviewPageTests : BunitContext
{
    private sealed class FakeApi : IDependencyReviewApiService
    {
        public DependencyReviewResult? Result { get; set; }
        public List<DependencyReviewRunSummary> History { get; } = [];
        public SourceDependencyReviewRequest? LastSourceRequest { get; private set; }
        public ReviewSourceOptions Options { get; set; } = new() { Snapshots = [SourceScopeFixture.App, SourceScopeFixture.Common] };
        public List<RelatedSourceCandidate> Candidates { get; set; } = [SourceScopeFixture.CommonCandidate];
        public PolicySimulationRequest? LastSimulation { get; private set; }
        public string? SimulationError { get; set; }

        public Task<ReviewSourceOptions> SourceScopeAsync(string environmentId, ReviewSourceScopeRequest? scope, CancellationToken ct = default) =>
            Task.FromResult(SourceScopeFixture.Resolve(Options, scope, Candidates));
        public Task<(DependencyReviewResult? Result, string? Error)> RunSourceAsync(SourceDependencyReviewRequest request, CancellationToken ct = default)
        {
            LastSourceRequest = request;
            History.Insert(0, new DependencyReviewRunSummary(Result!.RunId, Result.CompletedAt, Result.Label, Result.Repositories.Count, 3));
            return Task.FromResult<(DependencyReviewResult?, string?)>((Result, null));
        }

        public Task<IReadOnlyList<DependencyReviewRunSummary>> HistoryAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<DependencyReviewRunSummary>>(History);
        public Task<DependencyReviewResult?> GetAsync(Guid runId, CancellationToken ct = default) => Task.FromResult(Result?.RunId == runId ? Result : null);

        public Task<(PolicySimulation? Simulation, string? Error)> SimulateAsync(PolicySimulationRequest request, CancellationToken ct = default)
        {
            LastSimulation = request;
            if (SimulationError is not null) return Task.FromResult<(PolicySimulation?, string?)>((null, SimulationError));
            return Task.FromResult<(PolicySimulation?, string?)>((Sim(request.PackageName, "Custom", request.CandidateVersion, DependencyUpdateType.Major, PolicyResult.Blocked, "Matched #2. Renovate update disabled by policy.") with { Repository = request.Repository, CurrentValue = request.CurrentVersion }, null));
        }

        // Dependency health is covered by DependencyHealthPageTests; the source review page works with no stored inventory.
        public Task<IReadOnlyList<InventorySummary>> InventoriesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<InventorySummary>>([]);
        public Task<InventoryImportResult> ImportInventoryAsync(string fileName, Stream content, SbomRole role, string? environment, string? name, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<InventoryImportResult> CaptureDeployedAsync(DeployedCaptureRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<(DependencyHealthRun? Run, string? Error)> RunHealthAsync(DependencyHealthRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<DependencyHealthRunSummary>> HealthHistoryAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<DependencyHealthRunSummary>>([]);
        public Task<DependencyHealthRun?> GetHealthAsync(Guid runId, CancellationToken ct = default) => Task.FromResult<DependencyHealthRun?>(null);
        public Task<(DependencyHealthRun? Run, string? Error)> RefreshHealthAsync(Guid runId, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private static PolicySimulation Sim(string package, string scenario, string? candidate, DependencyUpdateType type, PolicyResult result, string why, bool? automerge = true) => new()
    {
        Repository = "M2LB", PackageName = package, Manager = "nuget", OwnerFile = "Aspire/Aspire.csproj", CurrentValue = "13.4.6", CandidateVersion = candidate, Scenario = scenario,
        UpdateType = type, Result = result, Explanation = why, Effective = new EffectivePolicy { Automerge = automerge, Sources = new() { ["automerge"] = "repository config" } },
        MatchedRules = [new RuleMatch { RuleIndex = 2, Description = "Aspire - NuGet packages (MAJOR)", MatchedOn = ["matchFileNames [Aspire/**] matches Aspire/Aspire.csproj"] }],
        Limitations = ["Preset config:recommended is not expanded; its rules are not part of this result."],
    };

    private static DependencyReviewResult Fixture() => new()
    {
        RunId = Guid.NewGuid(), CompletedAt = DateTimeOffset.UtcNow, Label = "M2LB + M2LB.Common", EvaluationMechanism = "BirkNext internal evaluator for an explicit subset of Renovate configuration semantics.",
        UnsupportedSemantics = ["Built-in and external presets (e.g. config:recommended) are not expanded."],
        Categories =
        [
            new("Configuration", ReviewCategoryState.Ready, "Parsed."), new("Coverage", ReviewCategoryState.Partial, "Missing for M2LB.Common."),
            new("Policy", ReviewCategoryState.NeedsReview, "2 rule/policy observation(s) need review."), new("Simulation", ReviewCategoryState.Partial, "5 synthetic scenario(s)."),
            new("Security-update policy", ReviewCategoryState.NotConfigured, "M2LB: Not configured."), new("Runtime automation", ReviewCategoryState.NotAssessed, "Renovate runs are not read."),
        ],
        Repositories =
        [
            new RepositoryDependencyReview
            {
                Repository = "M2LB", ArchiveSha256 = "aaaaaaaaaaaaaaaaaaaa", ConfigHash = "bbbbbbbbbbbbbbbb", Coverage = RenovateCoverage.Configured, CoverageDetail = "Configured in renovate.json.",
                ConfigFiles = [new RenovateConfigFile { Repository = "M2LB", Path = "renovate.json", Used = true, SyntaxValid = true, Note = "Renovate reads this file." }],
                Extends = ["config:recommended"], UnresolvedPresets = ["config:recommended"], ConfigSyntaxValid = true, NormalizedConfig = "{}",
                RepositorySettings = new() { ["automerge"] = "true", ["hostRules"] = "{\"hostType\":\"nuget\",\"matchHost\":\"pkgs.dev.azure.com\"}" },
                Managers =
                [
                    new ManagerCoverage { Manager = "nuget", Files = 53, Dependencies = 352, State = ManagerState.Enabled, Detail = "352 of 352 dependencies processed." },
                    new ManagerCoverage { Manager = "azure-pipelines", Files = 30, Dependencies = 1, State = ManagerState.DisabledByDefault, Detail = "Disabled by default." },
                ],
                Dependencies = [new DeclaredDependency { Repository = "M2LB", Manager = "nuget", PackageName = "Aspire.Hosting", CurrentValue = "13.4.6", OwnerFile = "Aspire/Aspire.csproj", Line = 12 }],
                Rules = [new RenovateRule { Index = 38, Description = "CdcReplay - NuGet packages (MAJOR)", Matchers = new() { ["matchFileNames"] = ["CdcReplay/**"] }, Location = new SourceProvenance("renovate.json", 411) }],
                Findings =
                [
                    new DependencyFinding { RuleId = "preset-unresolved", Severity = DependencyFindingSeverity.Info, Repository = "M2LB", Title = "Presets not expanded", Detail = "Partial." },
                    new DependencyFinding { RuleId = "rule-scope-inconsistent", Severity = DependencyFindingSeverity.NeedsReview, Repository = "M2LB", RuleIndex = 38, Title = "Renovate rule name/scope appears inconsistent with package matcher",
                        Detail = "Rule #38 targets CdcReplay, but its text refers to Aspire.", Evidence = ["renovate.json:411"] },
                ],
                Simulations =
                [
                    Sim("Aspire.Hosting", "Patch", "13.4.7", DependencyUpdateType.Patch, PolicyResult.Allowed, "Matched #1. Allowed by policy."),
                    Sim("Aspire.Hosting", "Major", "14.0.0", DependencyUpdateType.Major, PolicyResult.Allowed, "Matched #2. Allowed by policy; automerge enabled."),
                    Sim("Polly", "Major", "9.0.0", DependencyUpdateType.Major, PolicyResult.Blocked, "Renovate update disabled by policy."),
                    Sim("Serilog", "Minor", "3.2.0", DependencyUpdateType.Minor, PolicyResult.DeferredBySchedule, "Allowed by policy, but only within schedule."),
                    Sim("Compose", "Patch", null, DependencyUpdateType.NotAssessable, PolicyResult.Ignored, "Ignored by Renovate: Configured ignorePaths. This is not \"up to date\"."),
                ],
                SecurityUpdatePolicy = new("Security-update policy", ReviewCategoryState.NotConfigured, "No vulnerability source is connected, so no vulnerability claim is made."),
                RuntimeAutomation = new("Runtime automation", ReviewCategoryState.NotAssessed, "Pipeline renovate-pipeline.yml: Renovate image renovate/renovate:44. Whether Renovate runs successfully is not assessed."),
                DriftSincePrevious = "Changed since the previous review: added CdcReplay - NuGet packages (MAJOR).",
            },
            new RepositoryDependencyReview
            {
                Repository = "M2LB.Common", Coverage = RenovateCoverage.Missing, CoverageDetail = "No Renovate configuration file in the repository.",
                Managers = [new ManagerCoverage { Manager = "nuget", Files = 11, Dependencies = 26, State = ManagerState.NoConfig }],
                Findings = [new DependencyFinding { RuleId = "renovate-missing", Severity = DependencyFindingSeverity.Warning, Repository = "M2LB.Common", Title = "Renovate is not configured for this dependency-bearing repository", Detail = "Not a runtime failure." }],
            },
        ],
    };

    private readonly FakeApi _api = new() { Result = Fixture() };

    public DependencyReviewPageTests()
    {
        Services.AddSingleton<IDependencyReviewApiService>(_api);
        Services.AddSingleton<IReportExportService, ReportExportService>();
        var targets = new Moq.Mock<IFrontendAnalysisSettingsService>();
        targets.SetupGet(t => t.IsLoaded).Returns(true);
        targets.SetupGet(t => t.Settings).Returns(new BirkNext.Web.Models.FrontendAnalysisSettings { Profiles =
        [
            new BirkNext.Web.Models.FrontendAnalysisProfile { Id = "dev", Name = "Dev", EnvironmentType = BirkNext.Web.Models.FrontendEnvironmentType.Development, TargetUrl = "https://m2lbdev.example.test/" },
            new BirkNext.Web.Models.FrontendAnalysisProfile { Id = "blank", Name = "No URL", EnvironmentType = BirkNext.Web.Models.FrontendEnvironmentType.QA },
        ] });
        Services.AddSingleton(targets.Object);
        SourceScopeFixture.Register(this);
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private IRenderedComponent<DependencyReview> RunReview(bool withConfig = false)
    {
        var cut = Render<DependencyReview>();
        cut.Find("[data-testid='dr-primary']").Change(SourceScopeFixture.App.SnapshotId.ToString());
        cut.Find("[data-testid='dr-related-include']").Click();
        if (withConfig)
        {
            cut.Find("[data-testid='dr-override'] button").Click();
            cut.FindComponents<InputFile>()[0].UploadFiles(InputFileContent.CreateFromText("{}", "renovate.json"));
        }
        cut.Find("[data-testid='dr-run']").Click();
        cut.WaitForElement("[data-testid='dr-summary']");
        return cut;
    }

    [Fact]
    public void RunIsDisabledUntilASourceSnapshotIsChosen()
    {
        var cut = Render<DependencyReview>();

        cut.Find("[data-testid='dr-run']").HasAttribute("disabled").Should().BeTrue();
        cut.Find("[data-testid='dr-primary']").Change(SourceScopeFixture.App.SnapshotId.ToString());
        cut.Find("[data-testid='dr-run']").HasAttribute("disabled").Should().BeFalse();
        cut.Find("[data-testid='dr-primary-summary']").TextContent.Should().Contain("M2LB").And.Contain("c850a1b2…");
        cut.FindAll("[data-testid='dr-summary']").Should().BeEmpty();
    }

    [Fact]
    public void RepositoriesAreEvaluatedSeparatelyWithCoverage()
    {
        var cut = RunReview();

        (_api.LastSourceRequest!.PrimarySnapshotId, _api.LastSourceRequest.RelatedSnapshotIds.Single()).Should().Be((SourceScopeFixture.App.SnapshotId, SourceScopeFixture.Common.SnapshotId));
        var tabs = cut.FindAll("[data-testid='dr-repo-tab']");
        tabs.Select(t => t.GetAttribute("data-repository")).Should().Equal("M2LB", "M2LB.Common");
        tabs[1].TextContent.Should().Contain("Missing");
        tabs[1].Click();
        cut.Find("[data-testid='dr-repo']").GetAttribute("data-repository").Should().Be("M2LB.Common");
        cut.Find("[data-testid='dr-coverage']").TextContent.Should().Be("Missing");
        cut.Find("[data-testid='dr-no-config']").TextContent.Should().Contain("No Renovate configuration");
        cut.FindAll("[data-testid='dr-matrix']").Should().BeEmpty();
    }

    [Fact]
    public void SuppliedConfigIsSentForTheChosenRepository()
    {
        RunReview(withConfig: true);

        _api.LastSourceRequest!.ConfigOverrides.Single().Should().Match<SourceConfigOverride>(o => o.Repository == "M2LB" && o.FileName == "renovate.json");
    }

    [Fact]
    public void FindingsShowNeedsReviewFirst()
    {
        var cut = RunReview();

        var findings = cut.FindAll("[data-testid='dr-finding']");
        findings[0].GetAttribute("data-rule").Should().Be("rule-scope-inconsistent");
        findings[0].TextContent.Should().Contain("Needs review").And.Contain("CdcReplay").And.Contain("Aspire");
        cut.Find("[data-testid='dr-drift']").TextContent.Should().Contain("added CdcReplay");
    }

    [Fact]
    public void SimulationUsesSyntheticCandidateWordingOnly()
    {
        var cut = RunReview();

        var matrix = cut.Find("[data-testid='dr-matrix']");
        matrix.QuerySelectorAll("th").Select(h => h.TextContent).Should().Contain(DependencyLabels.SyntheticCandidate);
        cut.Find("[data-testid='dr-repo']").TextContent.Should().NotContainAny("Latest", "latest version", "Available version", "Published");
    }

    [Fact]
    public void OutcomesStayDistinct()
    {
        var cut = RunReview();

        var results = cut.FindAll("[data-testid='dr-sim']").Select(r => r.TextContent).ToList();
        results.Should().Contain(r => r.Contains("Deferred by schedule"));
        results.Should().Contain(r => r.Contains("Ignored by Renovate") && !r.Contains("Up to date"));
        results.Should().Contain(r => r.Contains("Blocked"));
        results.Should().Contain(r => r.Contains("14.0.0") && r.Contains("Allowed · Automerge"));
    }

    [Fact]
    public void ResultFilterNarrowsTheMatrix()
    {
        var cut = RunReview();

        cut.FindAll("[data-testid='dr-filter']").Single(b => b.GetAttribute("data-result") == "Blocked").Click();

        cut.FindAll("[data-testid='dr-sim']").Should().ContainSingle().Which.GetAttribute("data-result").Should().Be("Blocked");
        cut.Find("[data-testid='dr-filter-all']").Click();
        cut.FindAll("[data-testid='dr-sim']").Should().HaveCount(5);
    }

    [Fact]
    public void WhyThisPolicyExplainsMatchedRulesAndLimitations()
    {
        var cut = RunReview();

        cut.FindAll("[data-testid='dr-sim']")[1].QuerySelector("button")!.Click();

        var explanation = cut.Find("[data-testid='dr-explanation']");
        explanation.TextContent.Should().Contain("Rule #2").And.Contain("matchFileNames [Aspire/**]").And.Contain("automerge from repository config");
        cut.Find("[data-testid='dr-limitations']").TextContent.Should().Contain("config:recommended");
    }

    [Fact]
    public void SinglePackageSimulationRunsAgainstTheStoredRun()
    {
        var cut = RunReview();

        cut.Find("[data-testid='dr-single-package']").Change("0");
        cut.Find("[data-testid='dr-single-current']").GetAttribute("value").Should().Be("13.4.6");
        cut.Find("[data-testid='dr-single-candidate']").GetAttribute("value").Should().Be("14.0.0");
        cut.Find("[data-testid='dr-single-run']").Click();

        _api.LastSimulation!.RunId.Should().Be(_api.Result!.RunId);
        _api.LastSimulation.File.Should().Be("Aspire/Aspire.csproj");
        cut.Find("[data-testid='dr-single-outcome']").TextContent.Should().Be("Blocked");
        cut.Find("[data-testid='dr-single-result']").TextContent.Should().Contain(DependencyLabels.SyntheticCandidate);
    }

    [Fact]
    public void SinglePackageSimulationErrorIsShown()
    {
        _api.SimulationError = "Package, current version and synthetic candidate are required.";
        var cut = RunReview();

        cut.Find("[data-testid='dr-single-run']").Click();

        cut.Find("[data-testid='dr-single-error']").TextContent.Should().Contain("required");
    }

    [Fact]
    public void SecurityAndRuntimeAreNotClaimed()
    {
        var cut = RunReview();

        cut.Find("[data-testid='dr-security']").TextContent.Should().Contain("Not configured").And.Contain("no vulnerability claim");
        cut.Find("[data-testid='dr-runtime']").TextContent.Should().Contain("Not assessed");
        cut.Find("[data-testid='dr-category'][data-category='Runtime automation']").TextContent.Should().Contain("Not assessed");
    }

    [Fact]
    public void HistoryOpensAStoredRun()
    {
        _api.History.Add(new DependencyReviewRunSummary(_api.Result!.RunId, _api.Result.CompletedAt, "stored", 2, 3));
        var cut = Render<DependencyReview>();

        cut.Find("[data-testid='dr-history']").Change(_api.Result.RunId.ToString());

        cut.Find("[data-testid='dr-headline']").TextContent.Should().Contain("2 repositories reviewed").And.Contain("Renovate missing for M2LB.Common");
        cut.Find("[data-testid='dr-status']").TextContent.Should().Contain("stored review");
    }

    [Fact]
    public void ExportDownloadsHtml()
    {
        var cut = RunReview();

        cut.Find("[data-testid='dr-export']").Click();

        JSInterop.Invocations.Should().Contain(i => i.Identifier == "downloadHtmlFile");
        var html = new ReportExportService().ExportDependencyReview(_api.Result!);
        html.Should().Contain(DependencyLabels.SyntheticCandidate).And.Contain("M2LB.Common").And.Contain("CdcReplay").And.Contain("nothing here is a vulnerability status");
    }

    [Fact]
    public void PresentationToneAndLabels()
    {
        DependencyReviewPresentation.Tone(PolicyResult.DeferredBySchedule).Should().NotBe(DependencyReviewPresentation.Tone(PolicyResult.Blocked));
        DependencyLabels.Result(PolicyResult.Ignored).Should().Be("Ignored by Renovate");
        DependencyReviewPresentation.Outcome(Sim("x", "Major", "2.0.0", DependencyUpdateType.Major, PolicyResult.Allowed, "", automerge: null)).Should().Be("Allowed · Automerge not set");
    }
}
