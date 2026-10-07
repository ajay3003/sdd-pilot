using System.Text.RegularExpressions;
using BirkNext.Sdd;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>
/// Deterministic Specification entity and relation projection used by Specification Explorer.
/// Parser tree → unique logical entities → local relations (structural nesting and explicit identifier references only)
/// → one primary tree parent per entity. Review candidates are projected separately and reference entities; they never
/// change entity counts. No text-similarity or AI linking happens here, and nothing here is a coverage verdict.
/// </summary>
public static class SpecificationReviewProjection
{
    /// <summary>Default number of review candidates shown before "Show all".</summary>
    public const int CandidatePreviewSize = 15;

    private static readonly Regex LeadingReference = new(@"^[\W_]*" + RequirementReferenceParser.ReferencePattern,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex UserStoryHeading = new(@"^\s*user\s+story\s+(\d{1,4})\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    public static SpecificationEntityProjection Build(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return SpecificationEntityProjection.Empty;
        return Build(SpecExplorerService.Parse(markdown));
    }

    public static SpecificationEntityProjection Build(SpecTree tree)
    {
        var items = new List<Item>();
        Collect(tree.Roots, [], items);

        // ── Identity: one entity per logical identity; repeated definitions are counted once and reported. ──
        var entities = new List<SpecLogicalEntity>();
        var keyOfNode = new Dictionary<SpecNode, string>();
        var displayOfKey = new Dictionary<string, string>(StringComparer.Ordinal);
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        var ordinals = new Dictionary<string, int>(StringComparer.Ordinal);
        var scenarioOrdinals = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            var entity = Identify(item, keyOfNode, displayOfKey, ordinals, scenarioOrdinals);
            occurrences[entity.Key] = occurrences.GetValueOrDefault(entity.Key) + 1;
            keyOfNode[item.Node] = entity.Key;
            displayOfKey.TryAdd(entity.Key, entity.DisplayId);
            if (occurrences[entity.Key] == 1) entities.Add(entity);
        }
        var byKey = entities.ToDictionary(e => e.Key, StringComparer.Ordinal);
        var duplicates = occurrences.Where(o => o.Value > 1)
            .Select(o => new SpecDuplicateIdentity(byKey[o.Key].Kind, byKey[o.Key].DisplayId, o.Value))
            .OrderBy(d => d.DisplayId, StringComparer.Ordinal).ToList();

        // ── Relations: structural nesting and explicit identifier references only. ──
        var relations = new List<SpecRelation>();
        void Add(string source, string target, SpecRelationType type, SpecRelationProvenance provenance)
        {
            if (source == target) return;
            // One relation per (source, target, type); structural nesting is recorded before explicit references and wins.
            if (relations.Any(r => r.SourceKey == source && r.TargetKey == target && r.Type == type)) return;
            relations.Add(new(source, target, type, provenance));
        }

        foreach (var item in items)
        {
            var key = keyOfNode[item.Node];
            if (occurrences[key] > 1 && !ReferenceEquals(FirstNode(items, keyOfNode, key), item.Node)) continue;
            var requirementAncestor = NearestAncestor(item, keyOfNode, SpecEntityKind.Requirement, items);
            var storyAncestor = NearestAncestor(item, keyOfNode, SpecEntityKind.UserStory, items);
            switch (item.Kind)
            {
                case SpecEntityKind.Test:
                    if (requirementAncestor is not null) Add(key, requirementAncestor, SpecRelationType.Verifies, SpecRelationProvenance.StructuralNesting);
                    if (storyAncestor is not null) Add(key, storyAncestor, SpecRelationType.BelongsTo, SpecRelationProvenance.StructuralNesting);
                    foreach (var target in ExplicitTargets(OwnText(item.Node), byKey, key))
                    {
                        if (target.Kind == SpecEntityKind.Requirement) Add(key, target.Key, SpecRelationType.Verifies, SpecRelationProvenance.ExplicitReference);
                        else if (target.Kind == SpecEntityKind.UserStory) Add(key, target.Key, SpecRelationType.BelongsTo, SpecRelationProvenance.ExplicitReference);
                    }
                    break;
                case SpecEntityKind.Clarification:
                    if (requirementAncestor is not null) Add(key, requirementAncestor, SpecRelationType.Clarifies, SpecRelationProvenance.StructuralNesting);
                    if (storyAncestor is not null) Add(key, storyAncestor, SpecRelationType.Clarifies, SpecRelationProvenance.StructuralNesting);
                    foreach (var target in ExplicitTargets(OwnText(item.Node), byKey, key))
                        if (target.Kind is SpecEntityKind.Requirement or SpecEntityKind.UserStory or SpecEntityKind.SuccessCriterion)
                            Add(key, target.Key, SpecRelationType.Clarifies, SpecRelationProvenance.ExplicitReference);
                    break;
                case SpecEntityKind.Requirement:
                    if (storyAncestor is not null) Add(key, storyAncestor, SpecRelationType.BelongsTo, SpecRelationProvenance.StructuralNesting);
                    foreach (var target in ExplicitTargets(OwnText(item.Node), byKey, key))
                        if (target.Kind == SpecEntityKind.UserStory)
                            Add(key, target.Key, SpecRelationType.BelongsTo, SpecRelationProvenance.ExplicitReference);
                    break;
            }
        }

        // ── Primary tree parent: deterministic precedence, otherwise none. ──
        var parents = new Dictionary<string, SpecPrimaryParent>(StringComparer.Ordinal);
        foreach (var entity in entities)
        {
            var parent = ChoosePrimaryParent(entity, relations, byKey);
            if (parent is not null) parents[entity.Key] = parent;
        }

        return new SpecificationEntityProjection(entities, relations, parents, duplicates);
    }

    /// <summary>
    /// Precedence: (1) structural nesting under a Requirement; (2) exactly one explicitly referenced Requirement;
    /// (3) structural User Story context; (4) exactly one explicitly referenced User Story; (5) no primary parent.
    /// Several explicit Requirement references never pick one of them: the item stays at its User Story or specification level.
    /// </summary>
    private static SpecPrimaryParent? ChoosePrimaryParent(SpecLogicalEntity entity, List<SpecRelation> relations, Dictionary<string, SpecLogicalEntity> byKey)
    {
        if (entity.Kind is not (SpecEntityKind.Test or SpecEntityKind.Clarification or SpecEntityKind.Requirement)) return null;
        var own = relations.Where(r => r.SourceKey == entity.Key).ToList();
        SpecLogicalEntity Target(SpecRelation r) => byKey[r.TargetKey];

        if (entity.Kind != SpecEntityKind.Requirement)
        {
            var toRequirements = own.Where(r => Target(r).Kind == SpecEntityKind.Requirement && r.Type is SpecRelationType.Verifies or SpecRelationType.Clarifies).ToList();
            var nested = toRequirements.FirstOrDefault(r => r.Provenance == SpecRelationProvenance.StructuralNesting);
            if (nested is not null) return new(nested.TargetKey, nested.Type, nested.Provenance);
            if (toRequirements.Count == 1) return new(toRequirements[0].TargetKey, toRequirements[0].Type, toRequirements[0].Provenance);
        }

        var toStories = own.Where(r => Target(r).Kind == SpecEntityKind.UserStory).ToList();
        var nestedStory = toStories.FirstOrDefault(r => r.Provenance == SpecRelationProvenance.StructuralNesting);
        if (nestedStory is not null) return new(nestedStory.TargetKey, nestedStory.Type, nestedStory.Provenance);
        if (toStories.Count == 1) return new(toStories[0].TargetKey, toStories[0].Type, toStories[0].Provenance);
        return null;
    }

    /// <summary>Review candidates as typed records referencing the logical entity they describe, when the document identifies it:
    /// a leading identifier (FR-004: …), a structured Given/When/Then scenario whose parts all appear in the candidate, or a
    /// clarification question quoted by the candidate. Each rule must match exactly one entity; otherwise no entity is referenced.</summary>
    public static IReadOnlyList<SpecReviewCandidate> BuildCandidates(SpecificationEntityProjection projection, IReadOnlyList<ExtractionCandidate> candidates) =>
        candidates.Select(c => new SpecReviewCandidate(
            c.CandidateId,
            ReviewCandidateTypes.From(c.Classification),
            c.Title,
            c.ContextHeading,
            ResolveEntity(projection, c.Title))).ToList();

    public static SpecificationReviewSummary Summarize(SpecificationEntityProjection projection, IReadOnlyList<SpecReviewCandidate> candidates) => new(
        projection.Count(SpecEntityKind.Requirement),
        projection.Count(SpecEntityKind.Test),
        projection.Count(SpecEntityKind.Clarification),
        candidates.Count,
        candidates.Count(c => c.Type == ReviewCandidateType.Requirement),
        candidates.Count(c => c.Type == ReviewCandidateType.Test),
        candidates.Count(c => c.Type == ReviewCandidateType.Clarification));

    /// <summary>Candidates of <paramref name="type"/> (all when null) matching <paramref name="search"/> over identifier, text,
    /// Given/When/Then, clarification answer and section context (case-insensitive).</summary>
    public static IReadOnlyList<SpecReviewCandidate> Filter(IReadOnlyList<SpecReviewCandidate> candidates, ReviewCandidateType? type, string? search)
    {
        var term = search?.Trim();
        return candidates
            .Where(c => type is null || c.Type == type)
            .Where(c => string.IsNullOrEmpty(term) || SearchText(c).Contains(term, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private static string SearchText(SpecReviewCandidate c) => string.Join('\n', new[]
    {
        c.Text, c.ContextHeading, c.Entity?.DisplayId, c.Entity?.Key, c.Entity?.Text,
        c.Entity?.Given, c.Entity?.When, c.Entity?.Then, c.Entity?.Answer,
    }.Where(s => !string.IsNullOrEmpty(s)));

    private static SpecLogicalEntity? ResolveEntity(SpecificationEntityProjection projection, string text)
    {
        var leading = LeadingReference.Match(text);
        if (leading.Success && projection.Find(RequirementReferenceParser.Normalize(leading.Groups["id"].Value)) is { } identified)
            return identified;

        var normalized = Normalize(text);
        var scenarios = projection.OfKind(SpecEntityKind.Test)
            .Where(t => t.HasStructuredScenario
                && new[] { t.Given, t.When, t.Then }.Where(p => !string.IsNullOrWhiteSpace(p)).All(p => normalized.Contains(Normalize(p!), StringComparison.Ordinal)))
            .Take(2).ToList();
        if (scenarios.Count == 1) return scenarios[0];

        var questions = projection.OfKind(SpecEntityKind.Clarification)
            .Where(c => Normalize(c.Text) is { Length: >= 10 } q && normalized.Contains(q, StringComparison.Ordinal))
            .Take(2).ToList();
        return questions.Count == 1 ? questions[0] : null;
    }

    private static string Normalize(string text) => Whitespace.Replace(text.Replace("*", "").Replace("`", ""), " ").Trim().ToLowerInvariant();

    // ── Tree walk ────────────────────────────────────────────────────────────

    private sealed record Item(SpecNode Node, SpecEntityKind Kind, IReadOnlyList<SpecNode> Ancestors, string? DocumentId);

    private static void Collect(IEnumerable<SpecNode> nodes, List<SpecNode> ancestors, List<Item> items)
    {
        foreach (var node in nodes)
        {
            var item = Classify(node, ancestors);
            if (item is not null) items.Add(item);
            ancestors.Add(node);
            Collect(node.Children, ancestors, items);
            ancestors.RemoveAt(ancestors.Count - 1);
        }
    }

    private static Item? Classify(SpecNode node, List<SpecNode> ancestors)
    {
        var path = ancestors.AsEnumerable().Reverse().ToList(); // nearest ancestor first
        if (SpecExplorerService.IsRequirementNode(node, out var requirementId))
            return new(node, SpecEntityKind.Requirement, path, requirementId);
        return node.NodeType switch
        {
            SpecNodeType.BddScenario or SpecNodeType.AcceptanceTest => new(node, SpecEntityKind.Test, path, DocumentId(node)),
            SpecNodeType.QaPair or SpecNodeType.Clarification => new(node, SpecEntityKind.Clarification, path, DocumentId(node)),
            SpecNodeType.UserStory => new(node, SpecEntityKind.UserStory, path, DocumentId(node) ?? StoryNumberId(node.Title)),
            SpecNodeType.SuccessCriterion => new(node, SpecEntityKind.SuccessCriterion, path, DocumentId(node)),
            _ => null,
        };
    }

    private static string? DocumentId(SpecNode node) =>
        string.IsNullOrWhiteSpace(node.SpecItemId) ? null : RequirementReferenceParser.Normalize(node.SpecItemId);

    /// <summary>Spec-Kit convention: "User Story 2" is US-002 (the same compact form references such as "US2" normalize to).</summary>
    private static string? StoryNumberId(string title) =>
        UserStoryHeading.Match(title) is { Success: true } m ? RequirementReferenceParser.Normalize($"US-{m.Groups[1].Value}") : null;

    private static SpecLogicalEntity Identify(Item item, Dictionary<SpecNode, string> keyOfNode, Dictionary<string, string> displayOfKey, Dictionary<string, int> ordinals, Dictionary<string, int> scenarioOrdinals)
    {
        var node = item.Node;
        int Next(string bucket) => ordinals[bucket] = ordinals.GetValueOrDefault(bucket) + 1;
        switch (item.Kind)
        {
            case SpecEntityKind.Requirement:
            {
                var text = CleanText(node.FullContent ?? node.Title, item.DocumentId);
                if (item.DocumentId is { } id) return new(id, item.Kind, id, true, text);
                var n = Next("REQ");
                return new($"REQ#{n}", item.Kind, $"Requirement {n}", false, text);
            }
            case SpecEntityKind.UserStory:
            {
                if (item.DocumentId is { } id)
                {
                    var display = UserStoryHeading.Match(node.Title) is { Success: true } m ? $"User Story {m.Groups[1].Value}" : id;
                    return new(id, item.Kind, display, true, node.Title);
                }
                var n = Next("US");
                return new($"US#{n}", item.Kind, $"User story {n}", false, node.Title);
            }
            case SpecEntityKind.SuccessCriterion:
            {
                var text = CleanText(node.FullContent ?? node.Title, item.DocumentId);
                if (item.DocumentId is { } id) return new(id, item.Kind, id, true, text);
                var n = Next("SC");
                return new($"SC#{n}", item.Kind, $"Success criterion {n}", false, text);
            }
            case SpecEntityKind.Test:
            {
                var text = CleanText(node.FullContent ?? node.Title, item.DocumentId);
                if (item.DocumentId is { } id) return new(id, item.Kind, id, true, text, node.BddGiven, node.BddWhen, node.BddThen);
                // Position-based identity inside the nearest User Story (document order), otherwise across the specification.
                var story = item.Ancestors.FirstOrDefault(a => a.NodeType == SpecNodeType.UserStory);
                if (story is not null && keyOfNode.TryGetValue(story, out var storyKey))
                {
                    var k = scenarioOrdinals[storyKey] = scenarioOrdinals.GetValueOrDefault(storyKey) + 1;
                    var storyLabel = UserStoryHeading.Match(story.Title) is { Success: true } sm ? $"US {sm.Groups[1].Value}" : displayOfKey[storyKey];
                    return new($"{storyKey}/S{k}", item.Kind, $"{storyLabel} · Scenario {k}", false, text, node.BddGiven, node.BddWhen, node.BddThen);
                }
                var n = Next("TEST");
                return new($"TEST#{n}", item.Kind, $"Scenario {n}", false, text, node.BddGiven, node.BddWhen, node.BddThen);
            }
            default:
            {
                var question = node.QuestionText ?? node.Title;
                if (item.DocumentId is { } id) return new(id, item.Kind, id, true, question, Answer: node.AnswerText);
                var n = Next("CL");
                return new($"CL#{n}", item.Kind, $"Clarification {n}", false, question, Answer: node.AnswerText);
            }
        }
    }

    /// <summary>Display text without Markdown list/emphasis markers, hard-wrap whitespace or a leading copy of the identifier
    /// (the identifier is shown separately).</summary>
    private static string CleanText(string text, string? id)
    {
        var clean = Whitespace.Replace(text.Replace("**", "").Replace("__", ""), " ").Trim();
        clean = Regex.Replace(clean, @"^(?:[-*+]|\d+[.)])\s+", "");
        if (id is not null)
        {
            var leading = LeadingReference.Match(clean);
            if (leading.Success && RequirementReferenceParser.Normalize(leading.Groups["id"].Value) == id)
                clean = clean[(leading.Index + leading.Length)..].TrimStart(':', ' ', '-', '—', '.');
        }
        return clean.Length == 0 ? text.Trim() : clean;
    }

    private static SpecNode? FirstNode(List<Item> items, Dictionary<SpecNode, string> keyOfNode, string key) =>
        items.FirstOrDefault(i => keyOfNode[i.Node] == key)?.Node;

    private static string? NearestAncestor(Item item, Dictionary<SpecNode, string> keyOfNode, SpecEntityKind kind, List<Item> items)
    {
        foreach (var ancestor in item.Ancestors)
        {
            var match = items.FirstOrDefault(i => ReferenceEquals(i.Node, ancestor));
            if (match is not null && match.Kind == kind) return keyOfNode[ancestor];
        }
        return null;
    }

    /// <summary>The item's own text: its title, its own content and structured scenario/Q&amp;A fields. Child items are not included,
    /// so a reference inside a nested item never becomes a reference of its parent.</summary>
    private static string OwnText(SpecNode node) => string.Join('\n', new[]
    {
        node.Title, node.FullContent, node.BddGiven, node.BddWhen, node.BddThen, node.QuestionText, node.AnswerText,
    }.Where(s => !string.IsNullOrEmpty(s)));

    private static IEnumerable<SpecLogicalEntity> ExplicitTargets(string text, Dictionary<string, SpecLogicalEntity> byKey, string selfKey) =>
        RequirementReferenceParser.Extract(text)
            .Where(id => id != selfKey)
            .Select(id => byKey.GetValueOrDefault(id))
            .OfType<SpecLogicalEntity>();
}
