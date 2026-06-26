using System.Text.RegularExpressions;
using BirkNext.Web.GraphQL;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

public static class DocumentViewModelBuilder
{
    private static readonly Regex FrIdRe  = new(@"\bFR-\d{3,4}\b",                        RegexOptions.Compiled);
    private static readonly Regex ScIdRe  = new(@"\bSC-\d{3,4}\b",                        RegexOptions.Compiled);
    private static readonly Regex AcIdRe  = new(@"\b(AC|TS)-?\d{3,4}\b",                  RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DateHeadingRe = new(@"^\d{4}-\d{2}-\d{2}\b",            RegexOptions.Compiled);

    public static DocumentViewModel Build(
        string? specMarkdown,
        IReadOnlyList<ExtractionCandidate> candidates,
        IReadOnlyList<CandidateLinkEntry> links)
    {
        if (string.IsNullOrWhiteSpace(specMarkdown))
            return BuildFromCandidates(candidates);

        var specTree = SpecExplorerService.Parse(specMarkdown);
        if (specTree.Roots.Count == 0)
            return BuildFromCandidates(candidates);

        return BuildFromSpecTree(specTree, candidates);
    }

    // ── Spec-tree mode ────────────────────────────────────────────────────────

    private static DocumentViewModel BuildFromSpecTree(
        SpecTree specTree,
        IReadOnlyList<ExtractionCandidate> candidates)
    {
        var bySpecId = BuildSpecIdLookup(candidates);
        var matched  = new HashSet<Guid>();
        var sections = new List<DocumentSection>();

        foreach (var root in specTree.Roots)
        {
            var section = BuildSection(root, isInsideDecision: false, bySpecId, matched);
            if (section is not null)
                sections.Add(section);
        }

        var unmatched = candidates
            .Where(c => !matched.Contains(c.CandidateId))
            .Select(CandidateToArtifact)
            .ToList();

        var all = FlattenArtifacts(sections).ToList();

        return new DocumentViewModel
        {
            Sections             = sections,
            UnmatchedArtifacts   = unmatched,
            HasSpecTree          = true,
            RequirementCount     = all.Count(a => a.ArtifactType == DocumentArtifactType.Requirement),
            UserStoryCount       = all.Count(a => a.ArtifactType == DocumentArtifactType.UserStory),
            TestCount            = all.Count(a => a.ArtifactType == DocumentArtifactType.AcceptanceTest),
            SuccessCriteriaCount = all.Count(a => a.ArtifactType == DocumentArtifactType.SuccessCriterion),
            ClarificationCount   = all.Count(a => a.ArtifactType == DocumentArtifactType.Clarification),
            DecisionCount        = all.Count(a => a.ArtifactType == DocumentArtifactType.Decision),
            EntityCount          = all.Count(a => a.ArtifactType == DocumentArtifactType.Entity),
            ApiSurfaceItemCount  = all.Count(a => a.ArtifactType == DocumentArtifactType.ApiSurfaceItem),
        };
    }

    private static DocumentSection? BuildSection(
        SpecNode node,
        bool isInsideDecision,
        Dictionary<string, List<ExtractionCandidate>> bySpecId,
        HashSet<Guid> matched)
    {
        if (node.HeadingLevel <= 0) return null;

        bool isDecision = isInsideDecision || IsDecisionHeadingTitle(node.Title);

        var section = new DocumentSection
        {
            Title             = node.Title,
            HeadingLevel      = node.HeadingLevel,
            Semantics         = node.Semantics,
            IsDecisionSection = isDecision,
        };

        foreach (var child in node.Children)
        {
            if (child.HeadingLevel > 0)
            {
                var sub = BuildSection(child, isDecision, bySpecId, matched);
                if (sub is not null)
                    section.SubSections.Add(sub);
            }
            else
            {
                var artifact = BuildArtifact(child, isDecision, bySpecId, matched);
                if (artifact is not null)
                    section.Artifacts.Add(artifact);
            }
        }

        return section.HasContent ? section : null;
    }

    private static DocumentArtifact? BuildArtifact(
        SpecNode node,
        bool inDecisionSection,
        Dictionary<string, List<ExtractionCandidate>> bySpecId,
        HashSet<Guid> matched)
    {
        // Q/A pairs inside a decision section → Decision artifacts
        var effectiveType = (inDecisionSection && node.NodeType == SpecNodeType.QaPair)
            ? SpecNodeType.DecisionNode
            : node.NodeType;

        var artifactType = MapNodeType(effectiveType);
        if (artifactType is null) return null;

        var artifact = new DocumentArtifact
        {
            ArtifactType  = artifactType.Value,
            Title         = node.Title,
            Excerpt       = node.Excerpt,
            FullContent   = node.FullContent,
            SpecItemId    = node.SpecItemId,
            QuestionText  = node.QuestionText,
            AnswerText    = node.AnswerText,
        };

        if (node.SpecItemId is not null && bySpecId.TryGetValue(node.SpecItemId, out var cands))
        {
            var c = cands.FirstOrDefault(x => !matched.Contains(x.CandidateId));
            if (c is not null)
            {
                artifact.LinkedCandidate = c;
                matched.Add(c.CandidateId);
            }
        }

        return artifact;
    }

    // ── Candidate-only fallback ────────────────────────────────────────────────

    private static DocumentViewModel BuildFromCandidates(IReadOnlyList<ExtractionCandidate> candidates)
    {
        var sections      = new List<DocumentSection>();
        int reqCount = 0, testCount = 0, clrCount = 0, decCount = 0;

        foreach (var group in candidates
            .GroupBy(c => c.ContextHeading ?? "(Uncategorized)")
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            bool isDecision = IsDecisionHeadingTitle(group.Key);
            var section = new DocumentSection
            {
                Title             = group.Key == "(Uncategorized)" ? "Uncategorized" : group.Key,
                HeadingLevel      = 2,
                IsDecisionSection = isDecision,
            };

            if (isDecision)
            {
                foreach (var c in group)
                {
                    section.Artifacts.Add(new DocumentArtifact
                    {
                        ArtifactType     = DocumentArtifactType.Decision,
                        Title            = c.Title,
                        LinkedCandidate  = c,
                    });
                    decCount++;
                }
            }
            else
            {
                var seenFrIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var c in group)
                {
                    if (c.Classification == ScenarioKind.Requirement)
                    {
                        var frMatch = FrIdRe.Match(c.Title);
                        if (frMatch.Success && !seenFrIds.Add(frMatch.Value))
                            continue;

                        section.Artifacts.Add(new DocumentArtifact
                        {
                            ArtifactType    = DocumentArtifactType.Requirement,
                            Title           = c.Title,
                            SpecItemId      = frMatch.Success ? frMatch.Value : null,
                            LinkedCandidate = c,
                        });
                        reqCount++;
                    }
                    else if (c.Classification == ScenarioKind.Test)
                    {
                        section.Artifacts.Add(new DocumentArtifact
                        {
                            ArtifactType    = DocumentArtifactType.AcceptanceTest,
                            Title           = c.Title,
                            LinkedCandidate = c,
                        });
                        testCount++;
                    }
                    else
                    {
                        section.Artifacts.Add(new DocumentArtifact
                        {
                            ArtifactType    = DocumentArtifactType.Clarification,
                            Title           = c.Title,
                            LinkedCandidate = c,
                        });
                        clrCount++;
                    }
                }
            }

            if (section.HasContent)
                sections.Add(section);
        }

        return new DocumentViewModel
        {
            Sections           = sections,
            HasSpecTree        = false,
            RequirementCount   = reqCount,
            TestCount          = testCount,
            ClarificationCount = clrCount,
            DecisionCount      = decCount,
        };
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static DocumentArtifactType? MapNodeType(SpecNodeType type) => type switch
    {
        SpecNodeType.Requirement      => DocumentArtifactType.Requirement,
        SpecNodeType.UserStory        => DocumentArtifactType.UserStory,
        SpecNodeType.StoryContext     => DocumentArtifactType.UserStory,
        SpecNodeType.AcceptanceTest   => DocumentArtifactType.AcceptanceTest,
        SpecNodeType.BddScenario      => DocumentArtifactType.AcceptanceTest,
        SpecNodeType.SuccessCriterion => DocumentArtifactType.SuccessCriterion,
        SpecNodeType.Clarification    => DocumentArtifactType.Clarification,
        SpecNodeType.QaPair           => DocumentArtifactType.Clarification,
        SpecNodeType.DecisionNode     => DocumentArtifactType.Decision,
        SpecNodeType.Entity           => DocumentArtifactType.Entity,
        SpecNodeType.DomainItem       => DocumentArtifactType.Entity,
        SpecNodeType.ApiSurfaceItem   => DocumentArtifactType.ApiSurfaceItem,
        SpecNodeType.EdgeCase         => DocumentArtifactType.EdgeCase,
        SpecNodeType.Assumption       => DocumentArtifactType.Assumption,
        _                             => null,
    };

    private static Dictionary<string, List<ExtractionCandidate>> BuildSpecIdLookup(
        IReadOnlyList<ExtractionCandidate> candidates)
    {
        var result = new Dictionary<string, List<ExtractionCandidate>>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in candidates)
        {
            foreach (Match m in FrIdRe.Matches(c.Title))  AddTo(result, m.Value, c);
            foreach (Match m in ScIdRe.Matches(c.Title))  AddTo(result, m.Value, c);
            foreach (Match m in AcIdRe.Matches(c.Title))  AddTo(result, m.Value, c);
        }
        return result;
    }

    private static void AddTo<TKey, TVal>(Dictionary<TKey, List<TVal>> dict, TKey key, TVal val)
        where TKey : notnull
    {
        if (!dict.TryGetValue(key, out var list))
            dict[key] = list = [];
        list.Add(val);
    }

    private static DocumentArtifact CandidateToArtifact(ExtractionCandidate c)
    {
        var frMatch = FrIdRe.Match(c.Title);
        return new DocumentArtifact
        {
            ArtifactType    = c.Classification switch
            {
                ScenarioKind.Requirement        => DocumentArtifactType.Requirement,
                ScenarioKind.Test               => DocumentArtifactType.AcceptanceTest,
                ScenarioKind.NeedsClarification => DocumentArtifactType.Clarification,
                _                               => DocumentArtifactType.Clarification,
            },
            Title           = c.Title,
            SpecItemId      = frMatch.Success ? frMatch.Value : null,
            LinkedCandidate = c,
        };
    }

    private static IEnumerable<DocumentArtifact> FlattenArtifacts(IEnumerable<DocumentSection> sections)
    {
        foreach (var s in sections)
        {
            foreach (var a in s.Artifacts) yield return a;
            foreach (var a in FlattenArtifacts(s.SubSections)) yield return a;
        }
    }

    private static bool IsDecisionHeadingTitle(string heading)
    {
        if (DateHeadingRe.IsMatch(heading)) return true;
        var lower = heading.ToLowerInvariant();
        return lower.StartsWith("q/a",       StringComparison.Ordinal)
            || lower.StartsWith("q&a",       StringComparison.Ordinal)
            || lower.StartsWith("decisions", StringComparison.Ordinal)
            || (lower.Contains("clarification") && lower.Contains("session"));
    }
}
