using System.Text.Json;
using BirkNext.BrowserCompanion;
using BirkNext.LocalHttpsProxy;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Microsoft.JSInterop;

namespace BirkNext.Web.Tests.Services;

public sealed class WcagArchitectureTests
{
    private const string Origin = "https://m2lbdev.bufetat.no";
    private static BrowserPageEvidence Evidence(string path = "/", string outcome = "Pass") => new()
    {
        ProfileId = "dev", PageOrigin = Origin, PagePath = path, VisitStartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
        CapturedAt = DateTimeOffset.UtcNow, SnapshotSequence = 1,
        Accessibility = new() { Axe = new() { State = "Completed", Version = AxeRuleCatalog.Version, EvidenceVersion = "visit-1",
            Rules = [new() { RuleId = "image-alt", CriterionIds = ["1.1.1"], Outcome = outcome, Count = 1 }] } }
    };

    [Fact]
    public void NorwegianProfileHasExactlyTheAuthoritative48UniqueKnown21Criteria()
    {
        var p = new WcagSettings().Profile;
        Assert.Equal(WcagProfiles.NorwegianId, p.ProfileId);
        Assert.True(p.IsLegalBaseline); Assert.Equal("Norway", p.LegalJurisdiction);
        Assert.Equal(48, p.CriterionIds.Count); Assert.Equal(48, p.CriterionIds.Distinct().Count());
        Assert.All(p.CriterionIds, id => Assert.Equal(WcagVersion.Wcag21, WcagRegistry.All.Single(c => c.CriterionId == id).Since));
        Assert.DoesNotContain("1.2.3", p.CriterionIds); Assert.DoesNotContain("1.2.4", p.CriterionIds);
        Assert.Contains("1.2.5", p.CriterionIds); Assert.Contains("4.1.1", p.CriterionIds);
        Assert.Equal(55, WcagProfiles.Extended.CriterionIds.Count);
        Assert.False(WcagProfiles.Extended.IsLegalBaseline);
    }

    [Theory]
    [InlineData("3.2.6")][InlineData("3.3.7")][InlineData("3.3.8")][InlineData("2.4.11")][InlineData("2.5.7")][InlineData("2.5.8")]
    public async Task SwitchingBothDirectionsChangesActualResultsAndHeadingWithoutRelabellingHistory(string extendedOnly)
    {
        var service = new EndpointDiscoveryService(); var js = new Store();
        await service.MergeBrowserEvidenceAsync(js, "dev", [Evidence()]);
        var s = service.GetSnapshot("dev"); var original = s.Quality.Assessment!;
        Assert.DoesNotContain(original.Results, r => r.Definition.CriterionId == extendedOnly);
        await service.SaveWcagSettingsAsync(js, "dev", new() { ProfileId = WcagProfiles.ExtendedId });
        var extended = s.Quality.Assessment!;
        Assert.Contains(extended.Results, r => r.Definition.CriterionId == extendedOnly);
        Assert.Contains("WCAG 2.2", extended.TargetLabel);
        Assert.Equal(55, WcagAssessmentSummary.Criteria(extended).Count);
        await service.SaveWcagSettingsAsync(js, "dev", new());
        Assert.Equal(48, WcagAssessmentSummary.Criteria(s.Quality.Assessment!).Count);
        Assert.Contains("WCAG 2.1", s.Quality.Assessment!.TargetLabel);
        Assert.DoesNotContain(s.Quality.Assessment.Results, r => r.Definition.CriterionId == extendedOnly);
        Assert.Contains("WCAG 2.2", extended.TargetLabel);
        Assert.Equal(WcagProfiles.NorwegianId, original.Profile!.ProfileId);
    }

    [Theory]
    [InlineData("Fail", WcagStatus.Fail)]
    [InlineData("ManualReviewRequired", WcagStatus.ManualReviewRequired)]
    [InlineData("Pass", WcagStatus.NotTested)]
    [InlineData("NotApplicable", WcagStatus.NotTested)]
    public async Task AutomaticIngestionPreservesEvidenceStrengthAndDeduplicates(string outcome, WcagStatus expected)
    {
        var service = new EndpointDiscoveryService(); var js = new Store(); var evidence = Evidence(outcome: outcome);
        Assert.True(await service.MergeBrowserEvidenceAsync(js, "dev", [evidence]));
        var state = service.GetSnapshot("dev").Quality;
        Assert.Equal(expected, state.Assessment!.Results.Single(r => r.Definition.CriterionId == "1.1.1").Status);
        Assert.Single(state.Assessment.Results.Single(r => r.Definition.CriterionId == "1.1.1").Checks);
        var executions = state.EvaluationCount;
        Assert.False(await service.MergeBrowserEvidenceAsync(js, "dev", [evidence]));
        Assert.Equal(executions, state.EvaluationCount);
        Assert.Equal(WcagStatus.NotTested, state.Assessment.Results.Single(r => r.Definition.CriterionId == "1.4.1").Status);
    }

    [Theory]
    [InlineData("https://access.mcas.ms", false)]
    [InlineData("https://samhandlingsrom-onmicrosoft-com.access.mcas.ms", false)]
    [InlineData("https://login.microsoftonline.com", false)]
    [InlineData("https://login.live.com", false)]
    [InlineData("https://cdn.example.org", false)]
    [InlineData(Origin, true)]
    public void OnlyExactApplicationOriginCreatesPages(string origin, bool expected)
    {
        var s = new EndpointDiscoverySnapshot { ApplicationOrigins = [Origin] };
        EndpointDiscoveryMerge.MergeBrowserEvidence(s, [Evidence() with { PageOrigin = origin }], DateTimeOffset.UtcNow);
        Assert.Equal(expected ? 1 : 0, s.Pages.Count);
        EndpointDiscoveryMerge.Merge(s, [new ObservedNetworkEndpoint { PageOrigin = origin, PagePath = "/", Host = "api.example.org", Path = "/api" }], DateTimeOffset.UtcNow);
        Assert.Equal(expected ? 1 : 0, s.Pages.Count);
        Assert.Equal(expected ? 0 : 1, s.Shared.Count);
    }

    [Fact]
    public void AuthRedirectPermissionDoesNotGrantPageOwnershipAndTargetAfterRedirectIsAccepted()
    {
        var profile = new FrontendAnalysisProfile { Id = "dev", TargetUrl = Origin };
        profile.Security.AllowedRedirectUrls.Add("https://samhandlingsrom-onmicrosoft-com.access.mcas.ms");
        Assert.Equal(new[] { Origin }, BrowserCompanionScope.ApprovedOrigins(profile));
        var s = new EndpointDiscoverySnapshot { ApplicationOrigins = [Origin] };
        EndpointDiscoveryMerge.MergeBrowserEvidence(s, [Evidence() with { PageOrigin = "https://access.mcas.ms" }, Evidence("/dashboard")], DateTimeOffset.UtcNow);
        Assert.Equal(Origin + "/dashboard", Assert.Single(s.Pages).Identity);
    }

    [Fact]
    public async Task PersistenceSurvivesDisconnectAndLegacyIdentityIsRetained()
    {
        var js = new Store(); var service = new EndpointDiscoveryService();
        await service.MergeBrowserEvidenceAsync(js, "dev", [Evidence()]);
        var reloaded = new EndpointDiscoveryService(); await reloaded.LoadAsync(js);
        Assert.Single(reloaded.GetSnapshot("dev").Pages);
        Assert.Equal(WcagProfiles.NorwegianId, reloaded.GetSnapshot("dev").Quality.Assessment!.Profile!.ProfileId);
        js.Json = "{\"dev\":{\"wcag\":{\"version\":\"Wcag22\",\"level\":\"AA\"},\"pages\":[]}}";
        await reloaded.LoadAsync(js);
        Assert.Equal("legacy-22-AA", reloaded.GetSnapshot("dev").Wcag.ProfileId);
        Assert.Contains("Legacy", reloaded.GetSnapshot("dev").Quality.Assessment!.TargetLabel);
        Assert.Contains("Profile unknown", JsonSerializer.Deserialize<WcagAssessment>("{}")!.TargetLabel);
    }

    [Fact]
    public void ExistingInfrastructurePagesAreQuarantinedWithReviewsAndTrafficPreserved()
    {
        var page = new PageAnalysis { PageOrigin = "https://access.mcas.ms", PagePath = "/", WcagReviews = [new() { CriterionId = "1.1.1" }],
            Endpoints = [new() { Host = "api.example.org", Path = "/api", PageOrigin = "https://access.mcas.ms", PagePath = "/" }] };
        var s = new EndpointDiscoverySnapshot { Pages = [page] };
        EndpointDiscoveryMerge.Reclassify(s);
        Assert.Empty(s.Pages); Assert.Single(s.Shared); Assert.Single(Assert.Single(s.ExcludedPageHistory).WcagReviews);
        EndpointDiscoveryMerge.Reclassify(s); Assert.Single(s.ExcludedPageHistory);
    }

    [Fact]
    public void ApplicationScopeIsNotMultipliedAndMissingEvidenceNeverPasses()
    {
        var s = new EndpointDiscoverySnapshot { Pages = Enumerable.Range(0, 5).Select(i => new PageAnalysis { PagePath = $"/{i}" }).ToList() };
        var a = BrowserQualityAssessmentService.Assess(s);
        Assert.Equal(48, WcagAssessmentSummary.Criteria(a).Count);
        Assert.Equal(3, a.Results.Count(r => r.Definition.RequiresCrossPageEvidence));
        Assert.All(a.Results, r => Assert.Equal(WcagStatus.NotTested, r.Status));
        Assert.Equal("Awaiting browser evidence", WcagAssessmentSummary.State(a));
    }

    [Fact]
    public async Task ProfileCanBeSelectedAndPersistedBeforeEvidence()
    {
        var service = new EndpointDiscoveryService(); var js = new Store();
        await service.SaveWcagSettingsAsync(js, "dev", new() { ProfileId = WcagProfiles.ExtendedId });
        var restored = new EndpointDiscoveryService(); await restored.LoadAsync(js);
        Assert.Equal(WcagProfiles.ExtendedId, restored.GetSnapshot("dev").Wcag.ProfileId);
    }

    internal sealed class Store : IJSRuntime
    {
        public string? Json;
        public ValueTask<T> InvokeAsync<T>(string id, object?[]? args)
        {
            if (id == "birkNextStorage.setDiscovery") Json = (string?)args![0];
            return new(id == "birkNextStorage.getDiscovery" ? (T)(object)Json! : default!);
        }
        public ValueTask<T> InvokeAsync<T>(string id, CancellationToken ct, object?[]? args) => InvokeAsync<T>(id, args);
    }
}
