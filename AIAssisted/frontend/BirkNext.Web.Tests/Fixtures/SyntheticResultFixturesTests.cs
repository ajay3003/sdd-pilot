using System.Text.RegularExpressions;
using BirkNext.ApiReview;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Fixtures;

/// <summary>
/// The synthetic result fixture is deterministic, safe (loopback only, no secrets, marked synthetic), readable by the real browser stores,
/// and covers the states the result workspaces must render honestly. Set BIRKNEXT_SYNTHETIC_FIXTURE_OUT to a file path to export it for
/// the localhost browser checks; without it nothing is written.
/// </summary>
public sealed class SyntheticResultFixturesTests
{
    [Fact]
    public async Task Fixture_IsDeterministic()
    {
        (await SyntheticResultFixtures.ToJsonAsync()).Should().Be(await SyntheticResultFixtures.ToJsonAsync());
    }

    [Fact]
    public async Task Fixture_ContainsNoSecretAndAddressesOnlyTheLoopbackDiscardPort()
    {
        var json = await SyntheticResultFixtures.ToJsonAsync();
        json.Should().NotContainEquivalentOf("bearer ").And.NotContainEquivalentOf("access_token").And.NotContainEquivalentOf("password")
            .And.NotContainEquivalentOf("client_secret").And.NotContain("eyJ");
        // Reference links that ship with the WCAG registry and profile descriptions (documentation, never requested) are the only other hosts allowed.
        var reference = Hosts(System.Text.Json.JsonSerializer.Serialize(WcagRegistry.All) + System.Text.Json.JsonSerializer.Serialize(WcagProfiles.Norwegian)).ToHashSet();
        Hosts(json).Where(h => !reference.Contains(h)).Should().Equal(SyntheticResultFixtures.Host);
    }

    private static IEnumerable<string> Hosts(string text) =>
        Regex.Matches(text, @"https?://([^/""\\:]+)").Select(m => m.Groups[1].Value).Distinct();

    [Fact]
    public async Task Fixture_LoadsThroughTheRealStores_AsTheActiveSyntheticEnvironment()
    {
        var fixture = await SyntheticResultFixtures.BuildAsync();
        var js = new SyntheticResultFixtures.CapturingStorage(fixture.LocalStorage);

        var settings = new FrontendAnalysisSettingsService();
        await settings.LoadAsync(js);
        settings.Settings.Profiles.Should().ContainSingle();
        settings.ActiveProfile!.Id.Should().Be(SyntheticResultFixtures.ProfileId);
        settings.ActiveProfile.TargetUrl.Should().Be(SyntheticResultFixtures.TargetUrl);

        var fqr = new FrontendQualityReviewHistory(js);
        await fqr.LoadAsync(SyntheticResultFixtures.ProfileId);
        fqr.For(SyntheticResultFixtures.ProfileId).Select(r => r.Disposition).Should().Equal(
            FrontendQualityReleaseDisposition.ReviewRequired, FrontendQualityReleaseDisposition.Blocked,
            FrontendQualityReleaseDisposition.NoAutomatedBlockDetected, FrontendQualityReleaseDisposition.Blocked);
        fqr.For(SyntheticResultFixtures.ProfileId)[^1].Report.PreflightStatus.Should().Be(PreflightStatus.Unreachable, "one run executed nothing");

        var aqr = new ApiReviewHistoryService();
        await aqr.LoadAsync(js);
        var history = aqr.For(SyntheticResultFixtures.ProfileId);
        history.Runs.Should().HaveCount(2);
        history.LastReport!.Environment.EnvironmentId.Should().Be(SyntheticResultFixtures.ProfileId);
        history.LastReport.Environment.TargetUrl.Should().Be(SyntheticResultFixtures.TargetUrl, "the page shows a stored result only for the same target");
    }

    [Fact]
    public void LatestFrontendRun_HasOneRowPerLogicalIssue_AndKeepsDerivedApart()
    {
        var latest = SyntheticResultFixtures.FrontendRuns()[^1];
        var view = FrontendQualityResultPresentation.Build(latest);
        view.ActionableIssues.Select(i => i.LogicalId).Should().OnlyHaveUniqueItems();
        view.ActionableIssues.Should().ContainSingle(i => i.Category == FrontendQualityCategory.Accessibility, "three contrast routes are one logical issue");
        view.DerivedIssues.Should().NotBeEmpty();
        view.ActionableIssues.Should().NotContain(i => i.Derived);
        latest.Coverage!.RequiredAssessed.Should().Be(latest.Coverage.RequiredTotal);
    }

    [Fact]
    public void ApiRuns_CoverBlockedFailedNotTestedAndNeverReadAsAPass()
    {
        var older = SyntheticResultFixtures.ApiReport(new DateTime(2026, 11, 4, 9, 0, 0, DateTimeKind.Utc), blockedGraphQl: true);
        older.Targets.Should().Contain(t => t.Status == ApiReviewTargetStatus.Blocked);
        var latest = SyntheticResultFixtures.ApiReport(new DateTime(2026, 11, 5, 9, 0, 0, DateTimeKind.Utc), blockedGraphQl: false);
        latest.Targets.SelectMany(t => t.Checks).Select(c => c.Result).Should().Contain([ApiReviewCheckResult.Fail, ApiReviewCheckResult.NotTested, ApiReviewCheckResult.Warning]);
        ApiReviewPostRunPresentation.Outcome(latest).Assessment.Should().NotContainEquivalentOf("pass");
        SyntheticResultFixtures.ApiManualDecisions().Should().Contain(d => !d.Current, "superseded decisions are kept as audit history");
    }

    [Fact]
    public async Task Export_WritesOnlyWhenRequested()
    {
        var path = Environment.GetEnvironmentVariable("BIRKNEXT_SYNTHETIC_FIXTURE_OUT");
        if (string.IsNullOrWhiteSpace(path)) return;
        await File.WriteAllTextAsync(path, await SyntheticResultFixtures.ToJsonAsync());
        File.Exists(path).Should().BeTrue();
    }
}
