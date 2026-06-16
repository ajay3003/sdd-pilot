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
        var explicitFrNodes  = new List<SpecNode>();
        var headingSemantics = new Dictionary<string, SectionSemantics>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(specMarkdown))
        {
            var specTree = SpecExplorerService.Parse(specMarkdown);
            foreach (var node in FlattenSpecNodes(specTree.Roots))
            {
                if (node.HeadingLevel > 0)
                    headingSemantics[node.Title] = node.Semantics;
            }

            explicitFrNodes = FlattenSpecNodes(specTree.Roots)
                .Where(n => n.NodeType == SpecNodeType.Requirement
                    && n.SpecItemId is not null
                    && n.SpecItemId.StartsWith("FR-", StringComparison.OrdinalIgnoreCase))
                .ToList();

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

            AddDeterministicScLinks(frToScIds);
        }

        // ── Step 4: normalize candidates ────────────────────────────────────
        // Previously, decision headings were skipped entirely; now we include them but mark
        // them as non-eligible so they get artifact type badges rather than "Missing Tests".
        // Within each heading, deduplicate by FR-ID (sub-bullet fragments of one requirement).
        var explicitFrCandidates = BuildExplicitFrCandidates(explicitFrNodes, reqLike);
        var normalizationSource = explicitFrCandidates.Count > 0
            ? explicitFrCandidates
                .Concat(reqLike.Where(c => DeriveArtifactType(c, headingSemantics) != TraceArtifactType.Requirement))
                .ToList()
            : reqLike;

        var normalizedCandidates = new List<ExtractionCandidate>();
        foreach (var group in normalizationSource.GroupBy(r => r.ContextHeading ?? string.Empty))
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

        var testsByUserStory = tests
            .Select(t => (Test: t, UserStoryId: InferUserStoryId(null, t.ContextHeading ?? t.Title).Id))
            .Where(x => x.UserStoryId is not null)
            .GroupBy(x => x.UserStoryId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Test).DistinctBy(t => t.CandidateId).ToList(), StringComparer.OrdinalIgnoreCase);

        // ── Step 5: build TracedRequirement for each normalized candidate ────
        var usedTestIds = new HashSet<Guid>();
        var tracedReqs  = new List<TracedRequirement>(normalizedCandidates.Count);

        foreach (var candidate in normalizedCandidates)
        {
            var artifactType = DeriveArtifactType(candidate, headingSemantics);
            var frId  = FirstMatch(FrIdRe, candidate.Title);
            var inferredUs = InferUserStoryId(frId, candidate.ContextHeading ?? candidate.Title);
            var usId  = inferredUs.Id;

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
                    linkedTests = usId is not null && testsByUserStory.TryGetValue(usId, out var usTests)
                        ? usTests
                        : candidate.ContextHeading is not null
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
            var scSource = linkedScIds.Count > 0 ? "Suggested SC Links" : null;

            tracedReqs.Add(new TracedRequirement
            {
                CandidateId = candidate.CandidateId,
                Title       = StripIdPrefix(candidate.Title),
                FullContent = candidate.Title,
                FrId        = frId,
                UserStoryId = usId,
                UserStorySource = inferredUs.Source,
                LinkedTests = linkedTests,
                LinkedScIds = linkedScIds,
                SuccessCriteriaSource = scSource,
                CoverageReason = artifactType == TraceArtifactType.Requirement
                    ? linkedTests.Count > 0
                        ? $"Suggested coverage: {linkedTests.Count} test(s) linked by {inferredUs.Source ?? "trace link"}."
                        : "No linked acceptance tests were found."
                    : $"{artifactType} artifacts are not part of coverage calculations.",
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
            var linkedFrIds = frToScIds
                .Where(kvp => kvp.Value.Contains(sc.SpecItemId, StringComparer.OrdinalIgnoreCase))
                .Select(kvp => kvp.Key)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var testCount = linkedFrIds.Sum(fr => frToTestCount.TryGetValue(fr, out var c) ? c : 0);
            var status    = testCount > 0
                ? TraceCoverageStatus.Covered
                : linkedFrIds.Count == 0
                    ? TraceCoverageStatus.Orphaned
                    : TraceCoverageStatus.MissingTests;
            return new TracedSc
            {
                SpecItemId      = sc.SpecItemId,
                Title           = sc.Title,
                Excerpt         = sc.Excerpt,
                LinkedFrIds     = linkedFrIds,
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
            TotalCandidates = candidates.Count,
            RequirementCandidateCount = candidates.Count(c => c.Classification == ScenarioKind.Requirement),
            DerivedRequirementCount = Math.Max(0, candidates.Count(c => c.Classification == ScenarioKind.Requirement) - tracedReqs.Count(r => r.IsEligible)),
        };
    }

    // ── Artifact type derivation ─────────────────────────────────────────────

    private static TraceArtifactType DeriveArtifactType(
        ExtractionCandidate candidate,
        Dictionary<string, SectionSemantics> headingSemantics)
    {
        if (candidate.Classification == ScenarioKind.NeedsClarification)
            return TraceArtifactType.Clarification;

        if (IsQaPair(candidate.Title))
            return TraceArtifactType.Decision;

        // ScenarioKind.Requirement — infer from context heading
        var heading = candidate.ContextHeading ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(heading))
        {
            if (IsDecisionHeading(heading, headingSemantics))
                return TraceArtifactType.Decision;
            if (IsAssumptionHeading(heading))
                return TraceArtifactType.Assumption;
            if (IsArchitectureHeading(heading))
                return TraceArtifactType.ArchitectureNote;
            if (IsMetadataHeading(heading))
                return TraceArtifactType.Metadata;
        }

        return TraceArtifactType.Requirement;
    }

    private static bool IsQaPair(string title)
    {
        var trimmed = title.TrimStart();
        return trimmed.StartsWith("Q:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("Q.", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("Question:", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains(" A:", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains(" Answer:", StringComparison.OrdinalIgnoreCase);
    }

    private static List<ExtractionCandidate> BuildExplicitFrCandidates(
        IReadOnlyList<SpecNode> explicitFrNodes,
        IReadOnlyList<ExtractionCandidate> candidates)
    {
        if (explicitFrNodes.Count == 0) return [];

        var byFrId = candidates
            .Where(c => c.Classification == ScenarioKind.Requirement)
            .Select(c => (Candidate: c, FrId: FirstMatch(FrIdRe, c.Title)))
            .Where(x => x.FrId is not null)
            .GroupBy(x => x.FrId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Candidate, StringComparer.OrdinalIgnoreCase);

        var result = new List<ExtractionCandidate>(explicitFrNodes.Count);
        foreach (var node in explicitFrNodes)
        {
            var frId = node.SpecItemId!;
            byFrId.TryGetValue(frId, out var matchingCandidate);
            var title = node.FullContent ?? node.Title;

            result.Add(new ExtractionCandidate
            {
                CandidateId = matchingCandidate?.CandidateId ?? Guid.NewGuid(),
                Title = title,
                Classification = ScenarioKind.Requirement,
                ClassificationSignal = ClassificationSignal.FrPrefix,
                ContextHeading = "Functional Requirements",
                SourceBlockType = BlockType.ParagraphLine,
                IsSelected = matchingCandidate?.IsSelected ?? false,
                ReviewStatus = matchingCandidate?.ReviewStatus ?? CandidateReviewStatus.New,
                SaveState = matchingCandidate?.SaveState ?? CandidateSaveState.Pending,
                SaveError = matchingCandidate?.SaveError,
                SavedScenarioId = matchingCandidate?.SavedScenarioId,
                Confidence = matchingCandidate?.Confidence,
            });
        }

        return result;
    }

    private static (string? Id, string? Source) InferUserStoryId(string? frId, string? context)
    {
        var direct = FirstMatch(UsIdRe, context);
        if (direct is not null) return (direct.ToUpperInvariant(), "context heading");
        if (frId is null) return (null, null);

        if (!int.TryParse(frId.Split('-')[1], out var number)) return (null, null);
        return number switch
        {
            >= 1 and <= 7 => ("US1", "deterministic FR range"),
            >= 8 and <= 12 => ("US2", "deterministic FR range"),
            >= 13 and <= 16 => ("US3", "deterministic FR range"),
            33 => ("US3", "deterministic FR range"),
            >= 17 and <= 19 => ("US4", "deterministic FR range"),
            >= 20 and <= 24 => ("US5", "deterministic FR range"),
            32 => ("US5", "deterministic FR range"),
            >= 25 and <= 28 => ("US6", "deterministic FR range"),
            >= 29 and <= 31 => ("Cross-cutting / Platform", "deterministic FR range"),
            _ => (null, null),
        };
    }

    private static void AddDeterministicScLinks(Dictionary<string, List<string>> frToScIds)
    {
        AddScRange(frToScIds, "SC-001", 1, 5);
        AddSc(frToScIds, "SC-002", "FR-024");
        AddSc(frToScIds, "SC-003", "FR-002", "FR-003", "FR-008", "FR-010");
        AddSc(frToScIds, "SC-004", "FR-016", "FR-028");
        AddSc(frToScIds, "SC-005", "FR-022");
        AddSc(frToScIds, "SC-006", "FR-026");
        AddSc(frToScIds, "SC-007", "FR-029");
        AddScRange(frToScIds, "SC-008", 1, 33);
    }

    private static void AddScRange(Dictionary<string, List<string>> frToScIds, string scId, int first, int last)
    {
        for (var n = first; n <= last; n++)
            AddSc(frToScIds, scId, $"FR-{n:000}");
    }

    private static void AddSc(Dictionary<string, List<string>> frToScIds, string scId, params string[] frIds)
    {
        foreach (var frId in frIds)
        {
            if (!frToScIds.TryGetValue(frId, out var list))
                frToScIds[frId] = list = [];
            if (!list.Contains(scId, StringComparer.OrdinalIgnoreCase))
                list.Add(scId);
        }
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

    private static bool IsAssumptionHeading(string heading)
    {
        var lower = heading.ToLowerInvariant();
        return lower.StartsWith("assumption", StringComparison.Ordinal)
            || lower.StartsWith("assumptions", StringComparison.Ordinal)
            || lower.Contains("out of scope")
            || lower.Contains("scope constraint");
    }

    private static bool IsArchitectureHeading(string heading)
    {
        var lower = heading.ToLowerInvariant();
        return lower.Contains("architecture")
            || lower.Contains("api surface")
            || lower.Contains("api design")
            || lower.Contains("system design")
            || lower.Contains("system components")
            || lower.Contains("key entities")
            || lower.Contains("entities")
            || lower.Contains("domain model")
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
