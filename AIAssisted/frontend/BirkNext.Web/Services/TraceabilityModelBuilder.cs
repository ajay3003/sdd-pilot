using System.Text.RegularExpressions;
using BirkNext.Web.GraphQL;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

public static class TraceabilityModelBuilder
{
    private static readonly Regex FrIdRe = new(@"\bFR-\d{3,4}\b", RegexOptions.Compiled);
    private static readonly Regex UsIdRe = new(@"\bUS\d+\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex IdPrefixRe = new(
        @"^(FR|SC|US|AC|TS|NFR|REQ|AS)-?\s*\d+[\s.:–\-]+\s*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Matches ISO date anywhere in the heading so "Session 2026-03-06" is caught.
    private static readonly Regex DateHeadingRe = new(@"\b\d{4}-\d{2}-\d{2}\b", RegexOptions.Compiled);

    public static TraceabilityModel Build(
        string? specMarkdown,
        IReadOnlyList<ExtractionCandidate> candidates,
        IReadOnlyList<CandidateLinkEntry> links)
    {
        // ── Step 1: partition candidates ─────────────────────────────────────
        // Include NeedsClarification alongside Requirement so they appear in the matrix
        // with the correct artifact type badge rather than being silently dropped.
        var reqLike = candidates
            .Where(c => c.Classification == ScenarioKind.Requirement
                     || c.Classification == ScenarioKind.NeedsClarification)
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
                    SpecItemId  = sc.SpecItemId!,
                    Title       = sc.Title,
                    Excerpt     = sc.Excerpt,
                    LinkedFrIds = linkedFr,
                });
            }
        }

        // ── Step 4: normalize candidates ────────────────────────────────────
        // Previously, decision headings were skipped entirely; now we include them but mark
        // them as non-eligible so they get artifact type badges rather than "Missing Tests".
        // Within each heading, deduplicate by FR-ID (sub-bullet fragments of one requirement).
        var normalizedCandidates = new List<ExtractionCandidate>();
        foreach (var group in reqLike.GroupBy(r => r.ContextHeading ?? string.Empty))
        {
            var seenFrIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in group)
            {
                var frId = FirstMatch(FrIdRe, c.Title);
                if (frId is not null && !seenFrIds.Add(frId))
                    continue;
                normalizedCandidates.Add(c);
            }
        }

        // Build FR-ID → aggregate test IDs (for sub-bullet dedup resolution)
        var allEligibleReqs = reqLike.Where(c => c.Classification == ScenarioKind.Requirement).ToList();
        var frIdToTestIds = new Dictionary<string, HashSet<Guid>>(StringComparer.OrdinalIgnoreCase);
        foreach (var req in allEligibleReqs)
        {
            if (!reqToTestIds.TryGetValue(req.CandidateId, out var testIds)) continue;
            var frId = FirstMatch(FrIdRe, req.Title);
            if (frId is null) continue;
            if (!frIdToTestIds.TryGetValue(frId, out var set)) frIdToTestIds[frId] = set = [];
            foreach (var id in testIds) set.Add(id);
        }

        // ── Step 5: build TracedRequirement for each normalized candidate ────
        var usedTestIds = new HashSet<Guid>();
        var tracedReqs  = new List<TracedRequirement>(normalizedCandidates.Count);

        foreach (var candidate in normalizedCandidates)
        {
            var artifactType = DeriveArtifactType(candidate, headingSemantics);
            var frId  = FirstMatch(FrIdRe, candidate.Title);
            var usId  = FirstMatch(UsIdRe, candidate.ContextHeading ?? candidate.Title);

            // Only link tests to coverage-eligible requirements
            List<ExtractionCandidate> linkedTests = [];
            if (artifactType == TraceArtifactType.Requirement)
            {
                if (reqToTestIds.TryGetValue(candidate.CandidateId, out var directIds) && directIds.Count > 0)
                {
                    linkedTests = directIds.Where(testById.ContainsKey).Select(g => testById[g]).ToList();
                }
                else if (frId is not null && frIdToTestIds.TryGetValue(frId, out var frIds) && frIds.Count > 0)
                {
                    linkedTests = frIds.Where(testById.ContainsKey).Select(g => testById[g]).ToList();
                }
                else
                {
                    linkedTests = candidate.ContextHeading is not null
                        ? tests.Where(t => string.Equals(t.ContextHeading, candidate.ContextHeading, StringComparison.Ordinal)).ToList()
                        : [];
                }

                foreach (var t in linkedTests) usedTestIds.Add(t.CandidateId);
            }

            var linkedScIds = frId is not null && frToScIds.TryGetValue(frId, out var scList)
                ? scList.ToList()
                : (List<string>)[];

            var status = artifactType == TraceArtifactType.Requirement
                ? (linkedTests.Count > 0 ? TraceCoverageStatus.Covered : TraceCoverageStatus.MissingTests)
                : TraceCoverageStatus.NotEligible;

            tracedReqs.Add(new TracedRequirement
            {
                CandidateId = candidate.CandidateId,
                Title       = StripIdPrefix(candidate.Title),
                FrId        = frId,
                UserStoryId = usId,
                LinkedTests = linkedTests,
                LinkedScIds = linkedScIds,
                ArtifactType = artifactType,
                Status      = status,
            });
        }

        // ── Step 6: update TracedSc with test counts ─────────────────────────
        var frToTestCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var tr in tracedReqs.Where(r => r.IsEligible))
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

        // ── Step 7: orphaned tests and distinct total ────────────────────────
        // Orphaned = tests not linked to any coverage-eligible requirement.
        var orphanedTests = tests.Where(t => !usedTestIds.Contains(t.CandidateId)).ToList();

        // TotalTests uses distinct IDs to avoid double-counting when proximity linking
        // assigns the same test to multiple requirements in the same heading.
        var totalTests = usedTestIds.Count + orphanedTests.Count;

        return new TraceabilityModel
        {
            Requirements    = tracedReqs,
            SuccessCriteria = finalScs,
            OrphanedTests   = orphanedTests,
            TotalTests      = totalTests,
        };
    }

    // ── Artifact type derivation ─────────────────────────────────────────────

    private static TraceArtifactType DeriveArtifactType(
        ExtractionCandidate candidate,
        Dictionary<string, SectionSemantics> headingSemantics)
    {
        if (candidate.Classification == ScenarioKind.NeedsClarification)
            return TraceArtifactType.Clarification;

        // ScenarioKind.Requirement — infer from context heading
        var heading = candidate.ContextHeading ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(heading))
        {
            if (IsDecisionHeading(heading, headingSemantics))
                return TraceArtifactType.Decision;
            if (IsArchitectureHeading(heading))
                return TraceArtifactType.ArchitectureNote;
            if (IsMetadataHeading(heading))
                return TraceArtifactType.Metadata;
        }

        return TraceArtifactType.Requirement;
    }

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

    private static bool IsArchitectureHeading(string heading)
    {
        var lower = heading.ToLowerInvariant();
        return lower.Contains("architecture")
            || lower.Contains("api surface")
            || lower.Contains("api design")
            || lower.Contains("system design")
            || lower.Contains("system components")
            || lower.Contains("data model")
            || lower.Contains("technical design");
    }

    private static bool IsMetadataHeading(string heading)
    {
        var lower = heading.ToLowerInvariant();
        return lower.StartsWith("metadata", StringComparison.Ordinal)
            || lower.StartsWith("configuration", StringComparison.Ordinal)
            || lower.StartsWith("settings", StringComparison.Ordinal);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

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
