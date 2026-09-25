using BirkNext.ApiReview;
using BirkNext.LocalHttpsProxy;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

public sealed class ApiReviewPerformancePolicyTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 25, 10, 0, 0, TimeSpan.Zero);

    private static ObservedNetworkEndpoint Gql(string name, params ObservedRequestSample[] samples) => new()
    {
        Provenance = RequestProvenance.ApplicationTraffic, Category = ObservedTrafficCategory.GraphQl, Scheme = "https", Host = "api-dev.bufetat.no", Port = 443, Path = "/graphql", Method = "POST",
        Count = samples.Length, FirstObservedAt = T0, LastObservedAt = T0, OperationType = GraphQlOperationType.Query, OperationName = name, Confidence = ObservedEndpointConfidence.Verified,
        PageOrigin = "https://m2lbdev.bufetat.no", PagePath = "/", Samples = samples.ToList(), LastStatus = 200,
    };

    private static ApiReviewOperation Resolve(params ObservedNetworkEndpoint[] endpoints)
    {
        var snapshot = new EndpointDiscoverySnapshot();
        EndpointDiscoveryMerge.Merge(snapshot, endpoints, T0);
        var profile = new FrontendAnalysisProfile { Id = "dev", Name = "DEV", EnvironmentType = FrontendEnvironmentType.Development, TargetUrl = "https://m2lbdev.bufetat.no/" };
        var context = new FrontendAnalysisContext { ActiveProfile = profile, TargetUrl = profile.TargetUrl, ReviewIdentity = new AuthenticatedReviewIdentity(AuthenticatedTestingMethod.LocalHttpsProxy, "dev", "FP") };
        return ApiReviewTargetResolver.Resolve(context, snapshot).Single(t => t.ApiType == ApiReviewTargetType.GraphQl).Operations.Single();
    }

    [Fact]
    public void ObservedGraphQlSize_UsesOnlyUncompressed2xxSamples()
    {
        var op = Resolve(Gql("HentRoller",
            new ObservedRequestSample(T0, 40, 200, 700_000, ResponseEncoded: false),
            new ObservedRequestSample(T0, 40, 200, 90_000, ResponseEncoded: true),     // compressed transfer size: not the payload
            new ObservedRequestSample(T0, 40, 200, 2_000_000, ResponseEncoded: null),  // recorded before the flag existed: unknown
            new ObservedRequestSample(T0, 40, 500, 3_000_000, ResponseEncoded: false)));
        op.ObservedResponseBytes.Should().Be(700_000);
        op.ObservedResponseSamples.Should().Be(1);
    }

    [Fact]
    public void NoUsableSample_LeavesTheSizeUnknown_NeverZero()
    {
        var op = Resolve(Gql("HentRoller", new ObservedRequestSample(T0, 40, 200, 90_000, ResponseEncoded: true)));
        op.ObservedResponseBytes.Should().BeNull();
        op.ObservedResponseSamples.Should().Be(0);
    }

    [Theory]
    [InlineData(FrontendThresholdMode.Default, ApiReviewPerformanceProfile.Default)]
    [InlineData(FrontendThresholdMode.Strict, ApiReviewPerformanceProfile.Strict)]
    [InlineData(FrontendThresholdMode.Custom, ApiReviewPerformanceProfile.Custom)]
    public void ProfileIdentity_ComesFromTheSelectedMode(FrontendThresholdMode mode, ApiReviewPerformanceProfile expected) =>
        ApiReviewPresentation.ProfileOf(mode).Should().Be(expected);

    [Fact]
    public void PolicySummary_NewResult_ListsEveryAqrOwnedValue()
    {
        var rows = ApiReviewPresentation.PerformancePolicy(new ApiReviewPolicy
        {
            PerformanceProfile = ApiReviewPerformanceProfile.Custom, SlowWarningMs = 1500, LatencySource = "Single Request Latency", AverageLatencyWarningMs = 500,
            RestPayloadWarningBytes = 500L * 1024, GraphQlPayloadWarningBytes = 1024L * 1024, CompressionMinimumBytes = 1024,
        }).ToDictionary(r => r.Label, r => r.Value);
        rows["Profile"].Should().Be("Custom");
        rows["Single request latency"].Should().Be("warning > 1500 ms (Single Request Latency)");
        rows["Average API latency"].Should().StartWith("warning > 500 ms");
        rows["REST payload"].Should().Be("warning > 512,000 bytes (500 KB)");
        rows["GraphQL payload"].Should().StartWith("warning > 1,048,576 bytes (1 MB)");
        rows["Compression minimum payload"].Should().Be("1,024 bytes (1 KB)");
    }

    [Fact]
    public void Export_CarriesTheProfileAndEveryThreshold()
    {
        var report = new ApiReviewReport { Policy = new ApiReviewPolicy { PerformanceProfile = ApiReviewPerformanceProfile.Strict, SlowWarningMs = 1000, AverageLatencyWarningMs = 300, CompressionMinimumBytes = 1024, GraphQlPayloadWarningBytes = 500L * 1024, RestPayloadWarningBytes = 250L * 1024 } };
        var html = new ReportExportService().ExportApiReview(report, "Test");
        html.Should().Contain("Performance policy · Profile</dt><dd>Strict").And.Contain("Performance policy · Average API latency").And.Contain("Performance policy · Compression minimum payload</dt><dd>1,024 bytes (1 KB)");
        var legacy = new ReportExportService().ExportApiReview(new ApiReviewReport { Policy = new ApiReviewPolicy { SlowWarningMs = 500, SlowPoorMs = 1000 } }, "Test");
        legacy.Should().Contain("Performance policy · Profile</dt><dd>Profile not recorded");
    }

    [Fact]
    public void Presets_AndStoredProfiles_UseTheOneKilobyteCompressionMinimum()
    {
        var service = new FrontendAnalysisSettingsService();
        service.GetDefaultThresholds().CompressionMinPayloadBytes.Should().Be(1024);
        service.GetStrictThresholds().CompressionMinPayloadBytes.Should().Be(1024);
        new FrontendPerformanceThresholds().CompressionMinPayloadBytes.Should().Be(1024, "profiles stored before the setting existed read the default");
    }
}
