using System.Text.Json;
using System.Text.Json.Serialization;
using BirkNext.TestCoverage;
using BirkNext.TestPlanning;
using FluentAssertions;
using Xunit.Abstractions;

namespace BirkNext.Web.Tests.Services;

/// <summary>The compact discovery wire form (2026-11-26) is lossless: Expand(Compact(r)) serialises exactly like r, each story and test is sent
/// once, a shared story is one instance after expansion, and conflicting content under one key is kept whole (never merged).</summary>
public sealed class ScenarioDiscoveryWireTests(ITestOutputHelper output)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private static CoverageUserStory Story() => new() { Key = "spec.md#US1", StoryId = "US1", Ordinal = 1, Title = "Rapport", Priority = "P1", SpecificationPath = "spec.md" };

    private static DiscoveredScenario C(string id, CoverageUserStory? story, params CandidateTestAssociation[] tests) => new()
    {
        CandidateId = id, ProjectKey = "p", CapabilityId = "specification", Title = "Scenario " + id, Story = story, NumberInStory = 1, ExistingTests = [.. tests],
    };

    private static CandidateTestAssociation T(string id, string basis, bool confirmed) =>
        new(id, "ReportIsStored", "tests/ReportTests.cs:12", basis, confirmed) { Origin = TestScenarioLinkOrigin.SharedRequirement, Layer = "Unit", ClassName = "ReportTests", MethodName = "ReportIsStored", SourceFingerprint = "fp" };

    [Fact]
    public void Compact_sends_each_story_and_test_once_and_expand_restores_the_same_result()
    {
        // Two equal stories as separate instances (as read from storage) and one test linked twice with different per-link facts.
        var result = new ScenarioDiscoveryResult
        {
            DiscoveryId = "d", ProjectKey = "p",
            Candidates = [C("a", Story(), T("t1", "names FR-001", true)), C("b", Story(), T("t1", "names FR-002", false)), C("c", null)],
        };

        var compact = ScenarioDiscoveryWire.Compact(result);
        compact.StoryTable.Should().ContainSingle();
        compact.TestTable.Should().ContainSingle();
        compact.Candidates.Take(2).Should().OnlyContain(c => c.Story == null && c.StoryRef == "spec.md#US1");
        compact.Candidates[0].ExistingTests.Single().Should().Match<CandidateTestAssociation>(t => t.Name == "" && t.File == null && t.Basis == "names FR-001");

        var wire = JsonSerializer.Serialize(compact, Json);
        var expanded = ScenarioDiscoveryWire.Expand(JsonSerializer.Deserialize<ScenarioDiscoveryResult>(wire, Json)!);
        JsonSerializer.Serialize(expanded, Json).Should().Be(JsonSerializer.Serialize(result, Json), "lossless");
        expanded.Candidates[0].Story.Should().BeSameAs(expanded.Candidates[1].Story, "a shared story is one instance");
        expanded.Candidates[1].ExistingTests.Single().Should().Match<CandidateTestAssociation>(t => t.Basis == "names FR-002" && !t.Confirmed && t.ClassName == "ReportTests");
    }

    [Fact]
    public void Different_content_under_the_same_key_is_kept_whole_and_an_uncompacted_result_is_unchanged()
    {
        var other = Story() with { Title = "Rapport (endret)" };
        var result = new ScenarioDiscoveryResult { DiscoveryId = "d", ProjectKey = "p", Candidates = [C("a", Story()), C("b", other)] };
        var compact = ScenarioDiscoveryWire.Compact(result);
        compact.Candidates[1].Story!.Title.Should().Be("Rapport (endret)", "never merged with the first story of that key");
        JsonSerializer.Serialize(ScenarioDiscoveryWire.Expand(compact), Json).Should().Be(JsonSerializer.Serialize(result, Json));
        ScenarioDiscoveryWire.Expand(result).Should().BeSameAs(result);
    }

    [Fact]
    public void On_a_real_discovery_it_is_lossless_and_reports_the_size_when_one_is_provided()
    {
        var path = Environment.GetEnvironmentVariable("BIRKNEXT_DISCOVERY_JSON");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        var bytes = File.ReadAllBytes(path);
        var result = JsonSerializer.Deserialize<ScenarioDiscoveryResult>(bytes, Json)!;
        var full = JsonSerializer.SerializeToUtf8Bytes(result, Json);
        var compact = JsonSerializer.SerializeToUtf8Bytes(ScenarioDiscoveryWire.Compact(result), Json);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var back = ScenarioDiscoveryWire.Expand(JsonSerializer.Deserialize<ScenarioDiscoveryResult>(compact, Json)!);
        var compactRead = sw.ElapsedMilliseconds;
        sw.Restart(); _ = JsonSerializer.Deserialize<ScenarioDiscoveryResult>(full, Json); var fullRead = sw.ElapsedMilliseconds;
        output.WriteLine($"SIZE full={full.Length} compact={compact.Length} ({100.0 * compact.Length / full.Length:0.0} %) candidates={result.Candidates.Count}");
        output.WriteLine($"READ full={fullRead} ms compact+expand={compactRead} ms (.NET desktop)");
        JsonSerializer.Serialize(back, Json).Should().Be(JsonSerializer.Serialize(result, Json));
    }
}
