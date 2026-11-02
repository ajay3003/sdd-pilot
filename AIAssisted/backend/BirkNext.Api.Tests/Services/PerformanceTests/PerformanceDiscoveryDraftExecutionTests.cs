using System.Text.Json;
using BirkNext.Api.Services.PerformanceTests;
using BirkNext.LocalHttpsProxy;
using BirkNext.PerformanceTests;
using FluentAssertions;

namespace BirkNext.Api.Tests.Services.PerformanceTests;

/// <summary>
/// "Discovered · review required" drafts never execute: not in the k6 script, not in the comparison fingerprint (baselines stay comparable),
/// and a definition whose every step is a draft is not runnable. Discovery bookkeeping never changes what runs are compared by.
/// </summary>
public sealed class PerformanceDiscoveryDraftExecutionTests
{
    private const string Target = "https://paymenthub-qa.example.test";

    private static PerformanceTestDefinition WithDrafts(PerformanceTestDefinition d)
    {
        var observed = new[] { "/api/orders/7", "/api/reports" }.Select(path => new ObservedNetworkEndpoint
        {
            Category = ObservedTrafficCategory.Rest, Host = "paymenthub-qa.example.test", Method = "GET", Path = path, Count = 4, LastStatus = 200,
            LastObservedAt = new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero),
        });
        var current = PerformanceScenarioEngine.Build([], null, null, Target, "QA", observed, [], null, false).Operations.Where(o => o.Observation is not null).ToList();
        return PerformanceDiscoveryDrafts.Reconcile(d, current, "run-1").Definition;
    }

    [Fact]
    public void Drafts_are_not_executed_and_do_not_change_the_comparison_fingerprint()
    {
        var d = PerformanceTestReviewTests.Definition();
        var drafted = WithDrafts(d);

        drafted.Scenario.Steps.Should().HaveCount(3);
        PerformanceTestStore.ComparisonFingerprint(drafted).Should().Be(PerformanceTestStore.ComparisonFingerprint(d));
        var script = K6ScriptGenerator.Generate(drafted, PerformanceTestReviewTests.Data(), "/out.json");
        script.Should().Contain("/api/payments/").And.NotContain("/api/reports").And.NotContain("/api/orders/");
    }

    [Fact]
    public void Discovery_bookkeeping_of_an_executed_step_is_not_part_of_the_comparison_fingerprint()
    {
        var d = PerformanceTestReviewTests.Definition();
        var tracked = d with { Scenario = d.Scenario with { Steps = [PerformanceDiscoveryDrafts.SetTracking(d.Scenario.Steps[0], ObservationTracking.FollowLatest) with
            { Discovery = new StepDiscovery { State = StepDiscoveryState.Reviewed, Tracking = ObservationTracking.FollowLatest, RunId = "run-9", ObservedAt = DateTimeOffset.UnixEpoch } }] } };

        PerformanceTestStore.ComparisonFingerprint(tracked).Should().Be(PerformanceTestStore.ComparisonFingerprint(d));
    }

    [Fact]
    public void A_definition_with_only_drafts_is_not_runnable()
    {
        var onlyDrafts = WithDrafts(PerformanceTestReviewTests.Definition(x => x with { Scenario = x.Scenario with { Steps = [], TestDataProfileId = null } }));

        PerformanceTestSafety.Check(onlyDrafts, null, new PerformanceTestOptions()).Should()
            .Contain(i => i.State == PerformanceReadinessState.InvalidScenario && i.Message == "No step will run: every step is disabled or a discovered draft awaiting review.");
    }

    [Fact]
    public void Legacy_definitions_serialize_without_discovery_fields()
    {
        var json = JsonSerializer.Serialize(PerformanceTestReviewTests.Definition(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        json.Should().NotContain("discovery").And.NotContain("dismissedDiscoveries");
    }
}
