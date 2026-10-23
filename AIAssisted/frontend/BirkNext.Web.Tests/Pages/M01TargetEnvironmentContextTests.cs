using System.Text.Json;
using BirkNext.Integrations;
using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// M01-BUG-01: M01 consumes the platform's active Target Environment through the one context factory, from the persisted Target Environment
/// settings — no page-local selection, no fallback. Each state says what is true (active / configured but none active / none configured /
/// unresolved), source evidence stays available without an environment, runtime evidence belongs to the environment it ran in, and viewing
/// the page runs nothing.
/// </summary>
public sealed class M01TargetEnvironmentContextTests : BunitContext
{
    private sealed class RecordingApi : IClassificationReviewApiService
    {
        public List<string> Calls { get; } = [];
        public Dictionary<string, ClassificationReviewResult> LatestByEnvironment { get; } = [];
        public Task<ClassificationOverview> OverviewAsync(string environmentId, CancellationToken ct = default)
        {
            Calls.Add($"overview:{environmentId}");
            return Task.FromResult(new ClassificationOverview { Latest = LatestByEnvironment.GetValueOrDefault(environmentId) });
        }
        public Task<ClassificationScopeOptions> SourceScopeAsync(string environmentId, ReviewSourceScopeRequest? scope, CancellationToken ct = default)
        {
            Calls.Add($"source-scope:{environmentId}");
            return Task.FromResult(new ClassificationScopeOptions { Coverage = ClassificationSourceCoverage.Rows(null) });
        }
        public Task<(ClassificationTestContext? Context, string? Error)> SaveContextAsync(string environmentId, ClassificationTestContext context, CancellationToken ct = default)
        {
            Calls.Add("save-context");
            return Task.FromResult<(ClassificationTestContext?, string?)>((context, null));
        }
        public Task ClearContextAsync(string environmentId, CancellationToken ct = default) { Calls.Add("clear-context"); return Task.CompletedTask; }
        public Task<ClassificationReviewResult> RunAsync(string environmentId, ClassificationRunRequest request, CancellationToken ct = default) { Calls.Add("run"); throw new InvalidOperationException("not in these tests"); }
        public Task<ClassificationReviewResult?> GetRunAsync(Guid runId, CancellationToken ct = default) => Task.FromResult<ClassificationReviewResult?>(null);
    }

    private readonly RecordingApi _api = new();

    private static FrontendAnalysisProfile Profile(string id, string name, FrontendEnvironmentType type = FrontendEnvironmentType.Development) =>
        new() { Id = id, Name = name, EnvironmentType = type, TargetUrl = $"https://{id}.example.invalid/", Performance = new(), CoreWebVitals = new(), Security = new(), Features = new() };

    private static string Stored(string? activeId, params FrontendAnalysisProfile[] profiles) =>
        JsonSerializer.Serialize(new FrontendAnalysisSettings { ActiveProfileId = activeId, Profiles = [.. profiles] });

    /// <summary>The real settings service and the real context factory over browser storage, as the app wires them.</summary>
    private IRenderedComponent<SecurityClassificationReview> Page(string storedSettings)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        JSInterop.Setup<string?>("birkNextStorage.getItem", _ => true).SetResult(storedSettings);
        var settings = new FrontendAnalysisSettingsService();
        Services.AddSingleton<IFrontendAnalysisSettingsService>(settings);
        Services.AddSingleton<IAuthenticatedBrowserSessionService>(new PlaceholderAuthenticatedBrowserSessionService(settings));
        Services.AddSingleton<IFrontendAnalysisContextFactory, FrontendAnalysisContextFactory>();
        Services.AddSingleton<IClassificationReviewApiService>(_api);
        Services.AddSingleton(new FeatureVisibilityService());
        Services.AddSingleton<IReportExportService, ReportExportService>();
        return Render<SecurityClassificationReview>();
    }

    private static string Text(IRenderedComponent<SecurityClassificationReview> cut, string id) => cut.Find($"[data-testid={id}]").TextContent.Trim();

    // ── The shared rule ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheResolutionDistinguishesEveryState()
    {
        var dev = Profile("dev", "Development");
        var qa = Profile("qa", "QA");
        TargetEnvironmentResolution.From(new() { Profiles = [dev, qa], ActiveProfileId = "dev" }, dev).State.Should().Be(TargetEnvironmentResolutionState.Active);
        TargetEnvironmentResolution.From(new() { Profiles = [dev, qa] }, null).Should().Be(new TargetEnvironmentResolution { State = TargetEnvironmentResolutionState.NoneSelected, ConfiguredCount = 2 });
        TargetEnvironmentResolution.From(new() { Profiles = [] }, null).State.Should().Be(TargetEnvironmentResolutionState.NoneConfigured);
        var stale = TargetEnvironmentResolution.From(new() { Profiles = [dev], ActiveProfileId = "deleted" }, null);
        (stale.State, stale.ActiveId, stale.Detail).Should().Be((TargetEnvironmentResolutionState.Unresolved, "deleted", TargetEnvironmentResolution.NoLongerExists));
        var error = TargetEnvironmentResolution.From(new() { Profiles = [dev, qa], ActiveProfileId = "dev", ActiveResolutionError = "Multiple active Target Environments were found in saved settings." }, null);
        (error.State, error.Detail).Should().Be((TargetEnvironmentResolutionState.Unresolved, "Multiple active Target Environments were found in saved settings."));
    }

    [Fact]
    public async Task TheSameStoredSelectionResolvesTheSameEnvironmentAfterAReload()
    {
        var stored = Stored("dev", Profile("dev", "Development"), Profile("qa", "QA"));
        async Task<FrontendAnalysisContext> Load()
        {
            // A reload is a fresh settings service reading the same browser storage.
            var settings = new FrontendAnalysisSettingsService();
            var js = new Moq.Mock<Microsoft.JSInterop.IJSRuntime>();
            js.Setup(j => j.InvokeAsync<string?>("birkNextStorage.getItem", Moq.It.IsAny<object?[]?>())).Returns(new ValueTask<string?>(stored));
            return await new FrontendAnalysisContextFactory(settings, new PlaceholderAuthenticatedBrowserSessionService(settings), js.Object).GetActiveContextAsync();
        }

        var first = await Load();
        var second = await Load();
        (first.ActiveProfileOrNull?.Id, second.ActiveProfileOrNull?.Id).Should().Be(("dev", "dev"));
        second.TargetEnvironment.State.Should().Be(TargetEnvironmentResolutionState.Active);
    }

    // ── The page ────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheActiveEnvironmentIsShown_AndOnlyItsStoredReviewsAreRead()
    {
        var cut = Page(Stored("dev", Profile("dev", "Development"), Profile("qa", "QA")));

        cut.WaitForAssertion(() => cut.Find("[data-testid=sc-target-active]"));
        Text(cut, "sc-target-active").Should().StartWith("Target Environment: Development").And.Contain("Non-production");
        cut.FindAll("[data-testid=sc-no-target]").Should().BeEmpty("an active environment is never reported as missing");
        cut.Find("[data-testid=sc-target-link]").TextContent.Should().Be("View Target Environment");
        cut.Find("[data-testid=sc-target-link]").GetAttribute("href").Should().EndWith("&profile=dev");
        Text(cut, "sc-evidence-target").Should().Be("Development · configured (not a runtime result)");
        Text(cut, "sc-evidence-runtime").Should().Be("Not assessed in this environment");
        cut.FindAll("[data-testid=sc-setup]").Should().ContainSingle();
        _api.Calls.Should().BeEquivalentTo(["overview:dev", "source-scope:dev"], "viewing reads; it never saves, clears or runs");
    }

    [Fact]
    public void ConfiguredButNoneActive_IsSaidAsSuch_AndSourceEvidenceStillLoads()
    {
        var cut = Page(Stored(null, Profile("dev", "Development"), Profile("qa", "QA"), Profile("local", "Local", FrontendEnvironmentType.Local)));

        cut.WaitForAssertion(() => cut.Find("[data-testid=sc-no-target]"));
        Text(cut, "sc-no-target").Should().Be("No Target Environment is currently selected.");
        cut.Find("[data-testid=sc-target]").TextContent.Should().Contain("3 Target Environments are configured, but none is active").And.Contain("Set as Active");
        Text(cut, "sc-target-link").Should().Be("Select Target Environment");
        cut.FindAll("[data-testid=sc-source-evidence]").Should().ContainSingle("source evidence belongs to the snapshot, not to an environment");
        Text(cut, "sc-evidence-runtime").Should().Be("Not assessed · no Target Environment");
        cut.FindAll("[data-testid=sc-setup]").Should().BeEmpty("the runtime test context belongs to an environment");
        _api.Calls.Should().Equal("source-scope:");
    }

    [Fact]
    public void NoEnvironmentConfigured()
    {
        var cut = Page(Stored(null));
        cut.WaitForAssertion(() => cut.Find("[data-testid=sc-no-target]"));
        Text(cut, "sc-no-target").Should().Be("No Target Environment is configured.");
        Text(cut, "sc-target-link").Should().Be("Configure Target Environment");
        Text(cut, "sc-evidence-target").Should().Be("Not configured");
    }

    [Fact]
    public void AStaleSelectionIsUnresolved_NotNoneSelected_AndNoOtherEnvironmentIsUsed()
    {
        var cut = Page(Stored("deleted-env", Profile("dev", "Development"), Profile("qa", "QA")));

        cut.WaitForAssertion(() => cut.Find("[data-testid=sc-target-unresolved]"));
        Text(cut, "sc-target-unresolved").Should().Be("The selected Target Environment could not be resolved.");
        Text(cut, "sc-target-detail").Should().Be("Selected Target Environment no longer exists. No other environment is used instead.");
        cut.FindAll("[data-testid=sc-no-target]").Should().BeEmpty();
        _api.Calls.Should().NotContain(c => c.StartsWith("overview:"), "no environment's reviews are read for an unresolved selection");
    }

    [Fact]
    public void RuntimeEvidenceOfAnotherEnvironmentIsNeverShown()
    {
        _api.LatestByEnvironment["qa"] = new ClassificationReviewResult { RunId = Guid.NewGuid(), EnvironmentId = "qa", CompletedAt = new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero) };
        var cut = Page(Stored("dev", Profile("dev", "Development"), Profile("qa", "QA")));

        cut.WaitForAssertion(() => cut.Find("[data-testid=sc-evidence-runtime]"));
        Text(cut, "sc-evidence-runtime").Should().Be("Not assessed in this environment");
    }

    [Fact]
    public void TheExportNamesTheEnvironmentTheRunBelongedTo()
    {
        var run = new ClassificationReviewResult { RunId = Guid.NewGuid(), EnvironmentId = "dev", EnvironmentName = "Development" };
        string? subtitle = null;
        ClassificationExport.Build(run, (_, _) => "", b => b, e => e ?? "", (_, _, sub, body) => { subtitle = sub; return body; });
        subtitle.Should().StartWith("Target Environment Development (dev) · run ");
    }
}
