using System.Text.RegularExpressions;
using BirkNext.Web.GraphQL;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

public static class TraceabilityModelBuilder
{
    private static readonly Regex FrIdRe = new(@"\bFR-\d{3,4}\b", RegexOptions.Compiled);
    private static readonly Regex UsIdRe = new(@"\bUS\d+\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Strip leading spec-ID prefix from display titles ("FR-001: ..." → "...")
    private static readonly Regex IdPrefixRe = new(
        @"^(FR|SC|US|AC|TS|NFR|REQ|AS)-?\s*\d+[\s.:–\-]+\s*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ISO-date headings are Q/A decision sessions, not user-story lanes
    private static readonly Regex DateHeadingRe = new(@"^\d{4}-\d{2}-\d{2}\b", RegexOptions.Compiled);

    public static TraceabilityModel Build(
        string? specMarkdown,
        IReadOnlyList<ExtractionCandidate> candidates,
        IReadOnlyList<CandidateLinkEntry> links)
    {
        // ── Step 1: partition candidates ─────────────────────────────────────
        var allRequirements = candidates
            .Where(c => c.Classification == ScenarioKind.Requirement)
            .ToList();
        var tests = candidates
            .Where(c => c.Classification == ScenarioKind.Test)
            .ToList();

        var testById = tests.ToDictionary(t => t.CandidateId);

        // ── Step 2: build requirement → linked-test-GUIDs map ────────────────
        var reqToTestIds = new Dictionary<Guid, HashSet<Guid>>();
        foreach (var link in links)
        {
            if (link.LinkType != CandidateLinkType.RequirementTest) continue;
            AddToMap(reqToTestIds, link.SourceId, link.TargetId);
            AddToMap(reqToTestIds, link.TargetId, link.SourceId);
        }

        // ── Step 3: parse spec for SC nodes and heading semantics ─────────────
        var frToScIds        = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var tracedScs        = new List<TracedSc>();
        var headingSemantics = new Dictionary<string, SectionSemantics>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(specMarkdown))
        {
            var specTree = SpecExplorerService.Parse(specMarkdown);
            foreach (var node in FlattenSpecNodes(specTree.Roots))
            {
                if (node.HeadingLevel > 0)
                    headingSemantics[node.Title] = node.Semantics;
            }

            var scNodes = FlattenSpecNodes(specTree.Roots)
                .Where(n => n.NodeType == SpecNodeType.SuccessCriterion && n.SpecItemId is not null)
                .ToList();

            foreach (var sc in scNodes)
            {
                var scText  = (sc.Title + " " + (sc.FullContent ?? sc.Excerpt)).Trim();
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
                    SpecItemId  = sc.SpecItemId!,
                    Title       = sc.Title,
                    Excerpt     = sc.Excerpt,
                    LinkedFrIds = linkedFr,
                });
            }
        }

        // ── Step 4: normalize requirements ────────────────────────────────────
        // (a) Skip headings that are Q/A decision sessions (ISO-date headings, Clarifications).
        //     These are never requirements — they become 187-count inflation when included.
        // (b) Within each heading, deduplicate by FR-ID. Multiple candidates sharing the same
        //     FR-ID are sub-bullet fragments of one requirement; only the first is canonical.
        var normalizedReqs = new List<ExtractionCandidate>();
        foreach (var group in allRequirements.GroupBy(r => r.ContextHeading ?? string.Empty))
        {
            if (!string.IsNullOrWhiteSpace(group.Key) && IsDecisionHeading(group.Key, headingSemantics))
                continue;

            var seenFrIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var req in group)
            {
                var frId = FirstMatch(FrIdRe, req.Title);
                if (frId is not null && !seenFrIds.Add(frId))
                    continue; // skip duplicate sub-bullet fragment of the same FR
                normalizedReqs.Add(req);
            }
        }

        // Build FR-ID → aggregate test IDs so that test links on deduplicated sub-bullet
        // candidates are still resolved when we process the canonical candidate.
        var frIdToTestIds = new Dictionary<string, HashSet<Guid>>(StringComparer.OrdinalIgnoreCase);
        foreach (var req in allRequirements)
        {
            if (!reqToTestIds.TryGetValue(req.CandidateId, out var testIds)) continue;
            var frId = FirstMatch(FrIdRe, req.Title);
            if (frId is null) continue;
            if (!frIdToTestIds.TryGetValue(frId, out var set)) frIdToTestIds[frId] = set = [];
            foreach (var id in testIds) set.Add(id);
        }

        // ── Step 5: build TracedRequirement for each normalized requirement ────
        var usedTestIds = new HashSet<Guid>();
        var tracedReqs  = new List<TracedRequirement>(normalizedReqs.Count);

        foreach (var req in normalizedReqs)
        {
            var frId = FirstMatch(FrIdRe, req.Title);
            var usId = FirstMatch(UsIdRe, req.ContextHeading ?? req.Title);

            // 1. Prefer explicit links to this candidate's GUID.
            // 2. Fall back to aggregate links across all candidates with the same FR-ID.
            // 3. Last resort: proximity (same ContextHeading).
            List<ExtractionCandidate> linkedTests;
            if (reqToTestIds.TryGetValue(req.CandidateId, out var directIds) && directIds.Count > 0)
            {
                linkedTests = directIds.Where(testById.ContainsKey).Select(g => testById[g]).ToList();
            }
            else if (frId is not null && frIdToTestIds.TryGetValue(frId, out var frIds) && frIds.Count > 0)
            {
                linkedTests = frIds.Where(testById.ContainsKey).Select(g => testById[g]).ToList();
            }
            else
            {
                linkedTests = req.ContextHeading is not null
                    ? tests.Where(t => string.Equals(t.ContextHeading, req.ContextHeading, StringComparison.Ordinal)).ToList()
                    : [];
            }

            foreach (var t in linkedTests) usedTestIds.Add(t.CandidateId);

            var linkedScIds = frId is not null && frToScIds.TryGetValue(frId, out var scList)
                ? scList.ToList()
                : (List<string>)[];

            tracedReqs.Add(new TracedRequirement
            {
                CandidateId = req.CandidateId,
                Title       = StripIdPrefix(req.Title),
                FrId        = frId,
                UserStoryId = usId,
                LinkedTests = linkedTests,
                LinkedScIds = linkedScIds,
                Status      = linkedTests.Count > 0 ? TraceCoverageStatus.Covered : TraceCoverageStatus.MissingTests,
            });
        }

        // ── Step 6: update TracedSc with test counts ─────────────────────────
        var frToTestCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var tr in tracedReqs)
        {
            if (tr.FrId is not null)
                frToTestCount[tr.FrId] = tr.LinkedTests.Count;
        }

        var finalScs = tracedScs.Select(sc =>
        {
            var testCount = sc.LinkedFrIds.Sum(fr => frToTestCount.TryGetValue(fr, out var c) ? c : 0);
            var status    = testCount > 0
                ? TraceCoverageStatus.Covered
                : sc.LinkedFrIds.Count == 0
                    ? TraceCoverageStatus.Orphaned
                    : TraceCoverageStatus.MissingTests;
            return new TracedSc
            {
                SpecItemId      = sc.SpecItemId,
                Title           = sc.Title,
                Excerpt         = sc.Excerpt,
                LinkedFrIds     = sc.LinkedFrIds,
                LinkedTestCount = testCount,
                Status          = status,
            };
        }).ToList();

        // ── Step 7: orphaned tests ────────────────────────────────────────────
        var orphanedTests = tests.Where(t => !usedTestIds.Contains(t.CandidateId)).ToList();

        return new TraceabilityModel
        {
            Requirements    = tracedReqs,
            SuccessCriteria = finalScs,
            OrphanedTests   = orphanedTests,
        };
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static bool IsDecisionHeading(
        string heading,
        Dictionary<string, SectionSemantics> semantics)
    {
        if (semantics.TryGetValue(heading, out var sem) && sem == SectionSemantics.Clarifications)
            return true;
        if (DateHeadingRe.IsMatch(heading)) return true;
        var lower = heading.ToLowerInvariant();
        return (lower.Contains("clarification") && lower.Contains("session"))
            || lower.StartsWith("q/a", StringComparison.Ordinal)
            || lower.StartsWith("q&a", StringComparison.Ordinal)
            || lower.StartsWith("decisions", StringComparison.Ordinal);
    }

    private static string StripIdPrefix(string title)
    {
        var result = IdPrefixRe.Replace(title, "");
        return string.IsNullOrWhiteSpace(result) ? title : result.Trim();
    }

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
