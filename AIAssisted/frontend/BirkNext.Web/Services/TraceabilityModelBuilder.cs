using System.Text.RegularExpressions;
using BirkNext.Web.GraphQL;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

public static class TraceabilityModelBuilder
{
    // Not inside another document's namespace ("UT-FR-001", "[SR-FR-013]") (2026-11-25).
    private static readonly Regex FrIdRe = new(@"(?<![\w-])FR-\d{3,4}\b", RegexOptions.Compiled);
    private static readonly Regex UsIdRe = new(@"\bUS\d+\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex IdPrefixRe = new(
        @"^(FR|SC|US|AC|TS|NFR|REQ|AS)-?\s*\d+[\s.:–\-]+\s*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Matches ISO date anywhere in the heading so "Session 2026-03-06" is caught.
    private static readonly Regex DateHeadingRe = new(@"\b\d{4}-\d{2}-\d{2}\b", RegexOptions.Compiled);
    private static readonly Regex ScenarioIdentityRe = new(@"[^\p{L}\p{N}]", RegexOptions.Compiled);

    public static TraceabilityModel Build(
        string? specMarkdown,
        IReadOnlyList<ExtractionCandidate> candidates,
        IReadOnlyList<CandidateLinkEntry> links)
    {
        // Exclude rejected artifacts — they must not contribute to Traceability or coverage.
        var activeCandidates = candidates
            .Where(c => c.ReviewStatus != CandidateReviewStatus.Rejected)
            .ToList();

        // ── Step 1: partition candidates ─────────────────────────────────────
        // Include NeedsClarification alongside Requirement so they appear in the matrix
        // with the correct artifact type badge rather than being silently dropped.
        var reqLike = activeCandidates
            .Where(c => c.Classification == ScenarioKind.Requirement
                     || c.Classification == ScenarioKind.NeedsClarification)
            .ToList();
        var tests = activeCandidates
            .Where(c => c.Classification == ScenarioKind.Test)
            .ToList();
        var scenarioTextByCandidateId = new Dictionary<Guid, TraceabilityScenarioText>();

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
            var allSpecNodes = FlattenSpecNodes(specTree.Roots).ToList();
            foreach (var node in allSpecNodes)
            {
                if (node.HeadingLevel > 0)
                    headingSemantics[node.Title] = node.Semantics;
            }

            // Match extracted logical test candidates to the parser's structured scenario
            // fields. The candidate remains the source of traceability identity/counts;
            // the parsed Specification supplies complete display text without Razor parsing.
            var scenariosByIdentity = allSpecNodes
                .Where(n => n.NodeType == SpecNodeType.BddScenario &&
                            (!string.IsNullOrWhiteSpace(n.BddGiven) || !string.IsNullOrWhiteSpace(n.BddWhen) || !string.IsNullOrWhiteSpace(n.BddThen)))
                .Select(n => (Node: n, Identity: NormalizeScenario($"Given {n.BddGiven} When {n.BddWhen} Then {n.BddThen}")))
                .GroupBy(x => x.Identity, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().Node, StringComparer.Ordinal);
            foreach (var test in tests)
            {
                var identity = NormalizeScenario(test.Title);
                if (scenariosByIdentity.TryGetValue(identity, out var scenario))
                    scenarioTextByCandidateId[test.CandidateId] = new(scenario.BddGiven, scenario.BddWhen, scenario.BddThen);
                else if (identity.Length >= 32)
                {
                    // Workspaces may still contain a candidate saved before wrapped list
                    // continuations were retained. Recover display text only when its
                    // substantial prefix identifies exactly one parsed scenario.
                    var prefixMatches = scenariosByIdentity
                        .Where(pair => pair.Key.StartsWith(identity, StringComparison.Ordinal))
                        .Select(pair => pair.Value)
                        .DistinctBy(node => node.Id)
                        .ToList();
                    if (prefixMatches.Count == 1)
                    {
                        var matched = prefixMatches[0];
                        scenarioTextByCandidateId[test.CandidateId] = new(matched.BddGiven, matched.BddWhen, matched.BddThen);
                    }
                }
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

            // No fixed FR → SC table (2026-11-25): success criteria link to requirements only where the source names them.
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
                NeedsReviewWarning = candidate.ReviewStatus == CandidateReviewStatus.NeedsReview,
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
            ScenarioTextByCandidateId = scenarioTextByCandidateId,
            TotalTests      = totalTests,
            TotalCandidates = activeCandidates.Count,
            RequirementCandidateCount = activeCandidates.Count(c => c.Classification == ScenarioKind.Requirement),
            DerivedRequirementCount = Math.Max(0, activeCandidates.Count(c => c.Classification == ScenarioKind.Requirement) - tracedReqs.Count(r => r.IsEligible)),
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

    /// <summary>
    /// The user story an item belongs to: only an explicit story id in its context heading or title (2026-11-25). The former fallback mapped FR
    /// numbers to stories by fixed ranges (FR-001..007 → US1, FR-008..012 → US2, …) written for one specification and applied to every other; a
    /// numeric range is never a relationship. Without an explicit id the story is unknown.
    /// </summary>
    private static (string? Id, string? Source) InferUserStoryId(string? frId, string? context)
    {
        var direct = FirstMatch(UsIdRe, context);
        return direct is not null ? (direct.ToUpperInvariant(), "context heading") : (null, null);
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

    private static string NormalizeScenario(string? text) =>
        ScenarioIdentityRe.Replace(text ?? string.Empty, string.Empty).ToUpperInvariant();

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
