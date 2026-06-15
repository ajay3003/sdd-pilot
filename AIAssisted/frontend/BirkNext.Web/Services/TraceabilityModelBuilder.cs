using System.Text.RegularExpressions;
using BirkNext.Web.GraphQL;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

public static class TraceabilityModelBuilder
{
    private static readonly Regex FrIdRe  = new(@"\bFR-\d{3,4}\b", RegexOptions.Compiled);
    private static readonly Regex UsIdRe  = new(@"\bUS\d+\b",       RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static TraceabilityModel Build(
        string? specMarkdown,
        IReadOnlyList<ExtractionCandidate> candidates,
        IReadOnlyList<CandidateLinkEntry> links)
    {
        // ── Step 1: partition candidates ─────────────────────────────────────
        var requirements = candidates
            .Where(c => c.Classification == ScenarioKind.Requirement)
            .ToList();
        var tests = candidates
            .Where(c => c.Classification == ScenarioKind.Test)
            .ToList();

        var testById = tests.ToDictionary(t => t.CandidateId);

        // ── Step 2: build requirement → linked-test-GUIDs map ────────────────
        // Bidirectional — same logic as existing GetTraceabilityData
        var reqToTestIds = new Dictionary<Guid, HashSet<Guid>>();
        foreach (var link in links)
        {
            if (link.LinkType != CandidateLinkType.RequirementTest) continue;
            AddToMap(reqToTestIds, link.SourceId, link.TargetId);
            AddToMap(reqToTestIds, link.TargetId, link.SourceId);
        }

        // ── Step 3: parse spec for SC nodes ──────────────────────────────────
        // frId → list of scIds that reference it
        var frToScIds   = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var tracedScs   = new List<TracedSc>();

        if (!string.IsNullOrWhiteSpace(specMarkdown))
        {
            var specTree = SpecExplorerService.Parse(specMarkdown);
            var scNodes  = FlattenSpecNodes(specTree.Roots)
                .Where(n => n.NodeType == SpecNodeType.SuccessCriterion && n.SpecItemId is not null)
                .ToList();

            foreach (var sc in scNodes)
            {
                var scText   = (sc.Title + " " + (sc.FullContent ?? sc.Excerpt)).Trim();
                var linkedFr = FrIdRe.Matches(scText)
                    .Select(m => m.Value)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                foreach (var frId in linkedFr)
                {
                    if (!frToScIds.TryGetValue(frId, out var list))
                        frToScIds[frId] = list = [];
                    list.Add(sc.SpecItemId!);
                }

                tracedScs.Add(new TracedSc
                {
                    SpecItemId   = sc.SpecItemId!,
                    Title        = sc.Title,
                    Excerpt      = sc.Excerpt,
                    LinkedFrIds  = linkedFr,
                });
            }
        }

        // ── Step 4: build TracedRequirement for each requirement candidate ────
        var usedTestIds    = new HashSet<Guid>();
        var tracedReqs     = new List<TracedRequirement>(requirements.Count);

        foreach (var req in requirements)
        {
            var frId  = FirstMatch(FrIdRe,  req.Title);
            var usId  = FirstMatch(UsIdRe,  req.ContextHeading ?? req.Title);

            // Resolve linked tests — prefer explicit links
            var explicitTestGuids = reqToTestIds.TryGetValue(req.CandidateId, out var guids)
                ? guids.Where(testById.ContainsKey).ToList()
                : null;

            List<ExtractionCandidate> linkedTests;
            if (explicitTestGuids is { Count: > 0 })
            {
                linkedTests = explicitTestGuids.Select(g => testById[g]).ToList();
            }
            else
            {
                // Proximity fallback: same ContextHeading
                linkedTests = req.ContextHeading is not null
                    ? tests.Where(t => string.Equals(t.ContextHeading, req.ContextHeading, StringComparison.Ordinal)).ToList()
                    : [];
            }

            foreach (var t in linkedTests) usedTestIds.Add(t.CandidateId);

            var linkedScIds = frId is not null && frToScIds.TryGetValue(frId, out var scList)
                ? scList.ToList()
                : [];

            tracedReqs.Add(new TracedRequirement
            {
                CandidateId  = req.CandidateId,
                Title        = req.Title,
                FrId         = frId,
                UserStoryId  = usId,
                LinkedTests  = linkedTests,
                LinkedScIds  = linkedScIds,
                Status       = linkedTests.Count > 0 ? TraceCoverageStatus.Covered : TraceCoverageStatus.MissingTests,
            });
        }

        // ── Step 5: update TracedSc with test counts ─────────────────────────
        // Build frId → count of linked tests from tracedReqs
        var frToTestCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var tr in tracedReqs)
        {
            if (tr.FrId is not null)
                frToTestCount[tr.FrId] = tr.LinkedTests.Count;
        }

        var finalScs = tracedScs.Select(sc =>
        {
            var testCount = sc.LinkedFrIds.Sum(fr => frToTestCount.TryGetValue(fr, out var c) ? c : 0);
            var status = testCount > 0
                ? TraceCoverageStatus.Covered
                : sc.LinkedFrIds.Count == 0
                    ? TraceCoverageStatus.Orphaned
                    : TraceCoverageStatus.MissingTests;
            return new TracedSc
            {
                SpecItemId     = sc.SpecItemId,
                Title          = sc.Title,
                Excerpt        = sc.Excerpt,
                LinkedFrIds    = sc.LinkedFrIds,
                LinkedTestCount = testCount,
                Status         = status,
            };
        }).ToList();

        // ── Step 6: orphaned tests ────────────────────────────────────────────
        var orphanedTests = tests.Where(t => !usedTestIds.Contains(t.CandidateId)).ToList();

        return new TraceabilityModel
        {
            Requirements    = tracedReqs,
            SuccessCriteria = finalScs,
            OrphanedTests   = orphanedTests,
        };
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static void AddToMap(Dictionary<Guid, HashSet<Guid>> map, Guid key, Guid value)
    {
        if (!map.TryGetValue(key, out var set))
            map[key] = set = [];
        set.Add(value);
    }

    private static string? FirstMatch(Regex re, string? text) =>
        text is null ? null : re.Match(text) is { Success: true } m ? m.Value : null;

    private static IEnumerable<SpecNode> FlattenSpecNodes(IEnumerable<SpecNode> nodes)
    {
        foreach (var n in nodes)
        {
            yield return n;
            foreach (var child in FlattenSpecNodes(n.Children))
                yield return child;
        }
    }
}
