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

    // A heading is a decision lane if it starts with an ISO date or is a Q/A clarification session.
    // Matches ISO date anywhere in heading so "Session 2026-03-06" is caught as a decision lane.
    private static readonly Regex DateHeadingRe = new(@"\b\d{4}-\d{2}-\d{2}\b", RegexOptions.Compiled);
    private static readonly Regex PriorityRe    = new(@"\(P([1-3])\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

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

        // Parse spec: collect SC→FR reverse map, BDD node data, heading semantics
        var scByFrId         = new Dictionary<string, List<FlowSc>>(StringComparer.OrdinalIgnoreCase);
        var bddByTitle       = new Dictionary<string, (string? Given, string? When, string? Then)>(StringComparer.OrdinalIgnoreCase);
        var headingSemantics = new Dictionary<string, SectionSemantics>(StringComparer.OrdinalIgnoreCase);
        var allSpecScs       = new List<FlowSc>(); // every SC from the spec, regardless of FR links

        if (!string.IsNullOrWhiteSpace(specMarkdown))
        {
            var specTree = SpecExplorerService.Parse(specMarkdown);
            foreach (var node in FlattenNodes(specTree.Roots))
            {
                // Build heading-title → semantics map for decision-lane detection
                if (node.HeadingLevel > 0)
                    headingSemantics[node.Title] = node.Semantics;

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
                    allSpecScs.Add(sc);
                    foreach (var frId in linkedFrIds)
                    {
                        if (!scByFrId.TryGetValue(frId, out var list)) scByFrId[frId] = list = [];
                        list.Add(sc);
                    }
                }
                else if (node.NodeType is SpecNodeType.BddScenario or SpecNodeType.AcceptanceTest
                         && (node.BddGiven is not null || node.BddWhen is not null || node.BddThen is not null))
                {
                    var bddTuple = (node.BddGiven, node.BddWhen, node.BddThen);
                    // Index by raw title, stripped title, and first-160-chars for robust lookup
                    bddByTitle[node.Title] = bddTuple;
                    var stripped = StripIdPrefix(node.Title);
                    bddByTitle[stripped] = bddTuple;
                    if (node.Title.Length > 160)
                        bddByTitle[node.Title[..160]] = bddTuple;
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

        var stories      = new List<FlowStory>(headingOrder.Count);
        var linkedScIds  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var heading in headingOrder)
        {
            var reqs  = headingReqs.TryGetValue(heading, out var r)  ? r : [];
            var tests = headingTests.TryGetValue(heading, out var t) ? t : [];

            var isDecisionLane = !string.IsNullOrWhiteSpace(heading)
                                 && IsDecisionHeading(heading, headingSemantics);

            var usedTestIds = new HashSet<Guid>();
            var flowReqs    = new List<FlowRequirement>(reqs.Count);

            // Deduplicate by FR-ID: when multiple candidate fragments reference the same FR-001,
            // keep only the first (the canonical declaration). This prevents 187-req inflation
            // when each sub-bullet of an FR becomes its own candidate.
            var seenFrIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var req in reqs)
            {
                var frId = FirstMatch(FrIdRe, req.Title);

                // Skip duplicate: a second candidate with the same FR-ID is a sub-bullet fragment
                if (frId is not null && !seenFrIds.Add(frId))
                    continue;

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

                var reqLinkedScIds = frId is not null && scByFrId.TryGetValue(frId, out var scList)
                    ? scList.Select(s => s.SpecItemId).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                    : (List<string>)[];

                foreach (var scId in reqLinkedScIds)
                    linkedScIds.Add(scId);

                flowReqs.Add(new FlowRequirement
                {
                    Title       = StripIdPrefix(req.Title),
                    FrId        = frId,
                    CandidateId = req.CandidateId,
                    LinkedTests = linkedTests,
                    LinkedScIds = reqLinkedScIds,
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

            foreach (var sc in flowScs)
                linkedScIds.Add(sc.SpecItemId);

            var isUnmapped = string.IsNullOrWhiteSpace(heading);

            stories.Add(new FlowStory
            {
                Key             = isUnmapped ? "unassigned" : heading,
                Title           = isUnmapped ? "Unassigned" : StripIdPrefix(heading),
                StoryId         = ExtractUsId(heading),
                Priority        = isUnmapped ? 0 : ExtractPriority(heading),
                Requirements    = flowReqs,
                AllTests        = allTests,
                SuccessCriteria = flowScs,
                IsUnmapped      = isUnmapped,
                IsDecisionLane  = isDecisionLane,
            });
        }

        // SCs from the spec that were not shown in any story's SC step
        var unlinkedScs = allSpecScs
            .Where(sc => !linkedScIds.Contains(sc.SpecItemId))
            .DistinctBy(s => s.SpecItemId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Sort: mapped stories by priority (P1 first, then P2/P3, then unspecified),
        // then unmapped, then decision lanes — preserving document order within each group.
        var sorted = stories
            .Where(s => !s.IsUnmapped && !s.IsDecisionLane)
            .OrderBy(s => s.Priority == 0 ? int.MaxValue : s.Priority)
            .Concat(stories.Where(s => s.IsUnmapped))
            .Concat(stories.Where(s => s.IsDecisionLane))
            .ToList();

        return new FlowModel
        {
            Stories                  = sorted,
            UnlinkedSuccessCriteria  = unlinkedScs,
        };
    }

    /// <summary>
    /// Returns true when a heading represents a Q/A or decision session rather than a user story.
    /// Detected via spec semantics (Clarifications) or heading text patterns.
    /// </summary>
    private static bool IsDecisionHeading(
        string heading,
        Dictionary<string, SectionSemantics> headingSemantics)
    {
        if (headingSemantics.TryGetValue(heading, out var sem) &&
            sem == SectionSemantics.Clarifications)
            return true;

        // ISO-date headings like "2026-03-06" or "Session 2026-03-06"
        if (DateHeadingRe.IsMatch(heading)) return true;

        var lower = heading.ToLowerInvariant();

        // Q/A session or clarification session patterns
        return (lower.Contains("clarification") && lower.Contains("session"))
            || lower.StartsWith("q/a", StringComparison.Ordinal)
            || lower.StartsWith("q&a", StringComparison.Ordinal)
            || lower.StartsWith("decisions", StringComparison.Ordinal);
    }

    private static FlowTest MakeFlowTest(
        ExtractionCandidate c,
        string? linkedFrId,
        Dictionary<string, (string? Given, string? When, string? Then)> bddByTitle)
    {
        // Try multiple lookup keys to handle title mismatches between the spec parser and
        // the extraction pipeline (e.g., "Scenario 1: Given..." vs "Given...").
        (string? Given, string? When, string? Then) bdd = default;
        if (!bddByTitle.TryGetValue(c.Title, out bdd))
        {
            var stripped = StripIdPrefix(c.Title);
            if (!bddByTitle.TryGetValue(stripped, out bdd) && stripped.Length > 160)
                bddByTitle.TryGetValue(stripped[..160], out bdd);
        }

        return new FlowTest
        {
            Title       = StripIdPrefix(c.Title),
            CandidateId = c.CandidateId,
            BddGiven    = bdd.Given,
            BddWhen     = bdd.When,
            BddThen     = bdd.Then,
            LinkedFrId  = linkedFrId,
        };
    }

    private static string? ExtractUsId(string heading) =>
        UsIdRe.Match(heading) is { Success: true } m ? m.Value : null;

    private static int ExtractPriority(string heading) =>
        PriorityRe.Match(heading) is { Success: true } m
            ? int.TryParse(m.Groups[1].Value, out var p) ? p : 0
            : 0;

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
