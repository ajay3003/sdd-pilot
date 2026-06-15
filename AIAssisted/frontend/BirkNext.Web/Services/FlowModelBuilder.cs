using System.Text.RegularExpressions;
using BirkNext.Web.GraphQL;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

public static class FlowModelBuilder
{
    private static readonly Regex FrIdRe = new(@"\bFR-\d{3,4}\b", RegexOptions.Compiled);
    private static readonly Regex UsIdRe = new(@"\bUS[-\s]?\d+\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex IdPrefixRe = new(
        @"^(FR|SC|US|AC|TS|NFR|REQ)-?\s*\d+[\s.:–\-]+\s*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static FlowModel Build(
        string? specMarkdown,
        IReadOnlyList<ExtractionCandidate> candidates,
        IReadOnlyList<CandidateLinkEntry> links)
    {
        var reqCandidates  = candidates.Where(c => c.Classification == ScenarioKind.Requirement).ToList();
        var testCandidates = candidates.Where(c => c.Classification == ScenarioKind.Test).ToList();
        var testById       = testCandidates.ToDictionary(t => t.CandidateId);

        // Bidirectional req→test link map
        var reqToTestIds = new Dictionary<Guid, HashSet<Guid>>();
        foreach (var link in links.Where(l => l.LinkType == CandidateLinkType.RequirementTest))
        {
            AddToMap(reqToTestIds, link.SourceId, link.TargetId);
            AddToMap(reqToTestIds, link.TargetId, link.SourceId);
        }

        // Parse spec: collect SC→FR reverse map and BDD node data
        var scByFrId    = new Dictionary<string, List<FlowSc>>(StringComparer.OrdinalIgnoreCase);
        var bddByTitle  = new Dictionary<string, (string? Given, string? When, string? Then)>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(specMarkdown))
        {
            var specTree = SpecExplorerService.Parse(specMarkdown);
            foreach (var node in FlattenNodes(specTree.Roots))
            {
                if (node.NodeType == SpecNodeType.SuccessCriterion && node.SpecItemId is not null)
                {
                    var content    = node.Title + " " + node.Excerpt;
                    var linkedFrIds = FrIdRe.Matches(content)
                        .Select(m => m.Value)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    var sc = new FlowSc
                    {
                        SpecItemId  = node.SpecItemId,
                        Title       = StripIdPrefix(node.Title),
                        Excerpt     = node.Excerpt,
                        LinkedFrIds = linkedFrIds,
                    };
                    foreach (var frId in linkedFrIds)
                    {
                        if (!scByFrId.TryGetValue(frId, out var list)) scByFrId[frId] = list = [];
                        list.Add(sc);
                    }
                }
                else if (node.NodeType is SpecNodeType.BddScenario or SpecNodeType.AcceptanceTest
                         && (node.BddGiven is not null || node.BddWhen is not null || node.BddThen is not null))
                {
                    bddByTitle[node.Title] = (node.BddGiven, node.BddWhen, node.BddThen);
                }
            }
        }

        // Group candidates by ContextHeading, preserving insertion order
        var headingOrder = new List<string>();
        var headingReqs  = new Dictionary<string, List<ExtractionCandidate>>(StringComparer.OrdinalIgnoreCase);
        var headingTests = new Dictionary<string, List<ExtractionCandidate>>(StringComparer.OrdinalIgnoreCase);

        foreach (var c in reqCandidates)
        {
            var key = c.ContextHeading ?? string.Empty;
            if (!headingReqs.ContainsKey(key)) { headingReqs[key] = []; headingOrder.Add(key); }
            headingReqs[key].Add(c);
        }
        foreach (var c in testCandidates)
        {
            var key = c.ContextHeading ?? string.Empty;
            if (!headingTests.ContainsKey(key))
            {
                headingTests[key] = [];
                if (!headingReqs.ContainsKey(key)) headingOrder.Add(key);
            }
            headingTests[key].Add(c);
        }

        var stories = new List<FlowStory>(headingOrder.Count);

        foreach (var heading in headingOrder)
        {
            var reqs  = headingReqs.TryGetValue(heading, out var r)  ? r : [];
            var tests = headingTests.TryGetValue(heading, out var t) ? t : [];

            var usedTestIds = new HashSet<Guid>();
            var flowReqs    = new List<FlowRequirement>(reqs.Count);

            foreach (var req in reqs)
            {
                var frId = FirstMatch(FrIdRe, req.Title);

                // Prefer explicit links; fall back to same-ContextHeading proximity
                List<FlowTest> linkedTests;
                if (reqToTestIds.TryGetValue(req.CandidateId, out var testGuids) && testGuids.Count > 0)
                {
                    linkedTests = testGuids
                        .Where(testById.ContainsKey)
                        .Select(g => MakeFlowTest(testById[g], frId, bddByTitle))
                        .ToList();
                }
                else
                {
                    linkedTests = tests
                        .Where(tt => string.Equals(tt.ContextHeading, req.ContextHeading, StringComparison.Ordinal))
                        .Select(tt => MakeFlowTest(tt, frId, bddByTitle))
                        .ToList();
                }

                foreach (var ft in linkedTests)
                    if (ft.CandidateId.HasValue) usedTestIds.Add(ft.CandidateId.Value);

                var linkedScIds = frId is not null && scByFrId.TryGetValue(frId, out var scList)
                    ? scList.Select(s => s.SpecItemId).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                    : (List<string>)[];

                flowReqs.Add(new FlowRequirement
                {
                    Title       = StripIdPrefix(req.Title),
                    FrId        = frId,
                    CandidateId = req.CandidateId,
                    LinkedTests = linkedTests,
                    LinkedScIds = linkedScIds,
                });
            }

            // All tests in this story (with FR link badge)
            var allTests = tests.Select(tt =>
            {
                var linkedFrId = flowReqs
                    .FirstOrDefault(r => r.LinkedTests.Any(lt => lt.CandidateId == tt.CandidateId))
                    ?.FrId;
                return MakeFlowTest(tt, linkedFrId, bddByTitle);
            }).ToList();

            // SC for this story — collect distinct SC items linked to any FR in this story
            var storyFrIds = flowReqs
                .Where(r => r.FrId is not null)
                .Select(r => r.FrId!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var flowScs = scByFrId
                .Where(kv => storyFrIds.Contains(kv.Key))
                .SelectMany(kv => kv.Value)
                .DistinctBy(s => s.SpecItemId, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var isUnmapped = string.IsNullOrWhiteSpace(heading);

            stories.Add(new FlowStory
            {
                Key             = isUnmapped ? "unassigned" : heading,
                Title           = isUnmapped ? "Unassigned" : StripIdPrefix(heading),
                StoryId         = ExtractUsId(heading),
                Requirements    = flowReqs,
                AllTests        = allTests,
                SuccessCriteria = flowScs,
                IsUnmapped      = isUnmapped,
            });
        }

        return new FlowModel { Stories = stories };
    }

    private static FlowTest MakeFlowTest(
        ExtractionCandidate c,
        string? linkedFrId,
        Dictionary<string, (string? Given, string? When, string? Then)> bddByTitle)
    {
        bddByTitle.TryGetValue(c.Title, out var bdd);
        return new FlowTest
        {
            Title      = StripIdPrefix(c.Title),
            CandidateId = c.CandidateId,
            BddGiven   = bdd.Given,
            BddWhen    = bdd.When,
            BddThen    = bdd.Then,
            LinkedFrId = linkedFrId,
        };
    }

    private static string? ExtractUsId(string heading) =>
        UsIdRe.Match(heading) is { Success: true } m ? m.Value : null;

    private static string? FirstMatch(Regex re, string? text) =>
        text is null ? null : re.Match(text) is { Success: true } m ? m.Value : null;

    private static string StripIdPrefix(string title)
    {
        var result = IdPrefixRe.Replace(title, "");
        return string.IsNullOrWhiteSpace(result) ? title : result.Trim();
    }

    private static void AddToMap(Dictionary<Guid, HashSet<Guid>> map, Guid key, Guid value)
    {
        if (!map.TryGetValue(key, out var set)) map[key] = set = [];
        set.Add(value);
    }

    private static IEnumerable<SpecNode> FlattenNodes(IEnumerable<SpecNode> nodes)
    {
        foreach (var n in nodes)
        {
            yield return n;
            foreach (var child in FlattenNodes(n.Children))
                yield return child;
        }
    }
}
