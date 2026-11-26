namespace BirkNext.TestPlanning;

/// <summary>
/// Compact wire form of a discovery result (2026-11-26, measured on M2LB: 2,000 candidates, 9.4 MB). A user story was repeated on every one of its
/// acceptance scenarios (152 distinct stories, 18 % of the bytes) and an existing test on every scenario it relates to (4,336 links, 33 %).
/// <see cref="Compact"/> sends each story and each test once; <see cref="Expand"/> rebuilds exactly the same candidates (a story shared by
/// reference), so nothing that reads a discovery changes. Lossless: Expand(Compact(r)) equals r field by field.
/// </summary>
public static class ScenarioDiscoveryWire
{
    public static ScenarioDiscoveryResult Compact(ScenarioDiscoveryResult result)
    {
        if (result.StoryTable is not null || result.TestTable is not null) return result;
        var stories = new Dictionary<string, BirkNext.TestCoverage.CoverageUserStory>(StringComparer.Ordinal);
        // A story holds lists (record equality would compare them by reference): two stories are the same when their JSON is the same.
        var storyJson = new Dictionary<string, string>(StringComparer.Ordinal);
        var tests = new Dictionary<string, CandidateTestAssociation>(StringComparer.Ordinal);
        var candidates = result.Candidates.Select(c =>
        {
            string? storyRef = null;
            if (c.Story is { Key.Length: > 0 } story)
            {
                var json = System.Text.Json.JsonSerializer.Serialize(story);
                if (!storyJson.TryGetValue(story.Key, out var known)) { storyJson[story.Key] = json; stories[story.Key] = story; known = json; }
                if (known == json) storyRef = story.Key; // the same key with other content: kept whole on the candidate
            }
            var links = c.ExistingTests.Select(t =>
            {
                var shared = t with { Basis = "", Confirmed = true, Origin = default };
                if (tests.TryGetValue(t.TestId, out var held) && held != shared) return t; // the same id with other details: kept whole
                tests[t.TestId] = shared;
                return new CandidateTestAssociation(t.TestId, "", null, t.Basis, t.Confirmed) { Origin = t.Origin };
            }).ToList();
            return c with { Story = storyRef is null ? c.Story : null, StoryRef = storyRef, ExistingTests = links };
        }).ToList();
        return result with { Candidates = candidates, StoryTable = stories, TestTable = tests };
    }

    public static ScenarioDiscoveryResult Expand(ScenarioDiscoveryResult result)
    {
        if (result.StoryTable is null && result.TestTable is null) return result;
        var stories = result.StoryTable ?? [];
        var tests = result.TestTable ?? [];
        var candidates = result.Candidates.Select(c => c with
        {
            Story = c.StoryRef is { } key && stories.TryGetValue(key, out var story) ? story : c.Story,
            StoryRef = null,
            ExistingTests = [.. c.ExistingTests.Select(t => t.Name.Length == 0 && t.File is null && tests.TryGetValue(t.TestId, out var full)
                ? full with { Basis = t.Basis, Confirmed = t.Confirmed, Origin = t.Origin } : t)],
        }).ToList();
        return result with { Candidates = candidates, StoryTable = null, TestTable = null };
    }
}
