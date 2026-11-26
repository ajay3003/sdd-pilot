using System.Diagnostics;
using System.Text.Json;
using BirkNext.TestPlanning;
using BirkNext.Web.Services;
using FluentAssertions;
using Xunit.Abstractions;
using S = BirkNext.Web.Services.ScenarioBrowserPresentation;

namespace BirkNext.Web.Tests.Services;

/// <summary>Measurement harness (app-wide performance pass, 2026-11-26): times each step of Finn og forstå's load pipeline on a REAL saved discovery
/// (set BIRKNEXT_DISCOVERY_JSON to a /discover/last response). Without the file the test does nothing, so normal runs are unaffected.</summary>
public sealed class DiscoverPipelineTimingTests(ITestOutputHelper output)
{
    [Fact]
    public void Times_the_load_pipeline_on_a_real_discovery_when_one_is_provided()
    {
        var path = Environment.GetEnvironmentVariable("BIRKNEXT_DISCOVERY_JSON");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        var bytes = File.ReadAllBytes(path);
        var sw = Stopwatch.StartNew();
        long Step(string name, Action a) { sw.Restart(); a(); var ms = sw.ElapsedMilliseconds; output.WriteLine($"TIMING {name,-34} {ms,7} ms"); return ms; }

        ScenarioDiscoveryResult d = null!;
        Step("deserialize (System.Text.Json)", () => d = JsonSerializer.Deserialize<ScenarioDiscoveryResult>(bytes, new JsonSerializerOptions(JsonSerializerDefaults.Web))!);
        List<S.Item> items = [];
        Step("Items (presentation)", () => items = S.Items(d.Candidates, new Dictionary<string, S.Prepared>()));
        Step("WithScenarioTypes (classify all)", () => items = S.WithScenarioTypes(items, new Dictionary<string, ScenarioTypeDecision>()));
        Step("Apply default filter", () => _ = S.Apply(items, new S.Filter()));
        IReadOnlyList<ScenarioGrouping.Group> tree = [];
        Step("Build Story>ScenarioType", () => tree = ScenarioGrouping.Build(items, ScenarioGroupAxis.Story, ScenarioGroupAxis.ScenarioType));
        Step("TypeSummary per story", () => { foreach (var g in tree) _ = ScenarioGrouping.TypeSummary(g.Items); });
        Step("Suitability (all items)", () => { foreach (var i in items) _ = i.Suitability; });
        Step("Readiness facts (all)", () => { foreach (var c in d.Candidates) _ = ScenarioPreparationEngine.FactsOf(c, []); });
        output.WriteLine($"candidates={d.Candidates.Count} items={items.Count} bytes={bytes.Length}");
        items.Should().NotBeEmpty();
    }
}
