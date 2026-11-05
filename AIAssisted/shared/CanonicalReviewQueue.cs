using System.Text.Json.Serialization;

namespace BirkNext.Interpretation;

// The one shared review model for unresolved canonical relationships. Every surface (Relationship Review, Dashboard, Traceability, Topology, IQR, Project
// Evidence) reads these items from the same query and writes decisions to the same registry, so one ambiguity is one question, asked once.

/// <summary>A decision blocks or changes an interpretation; a suggestion is an optional proposal (naming-only) that nothing depends on until confirmed.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ReviewItemKind { Decision, Suggestion }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ReviewItemType
{
    /// <summary>Two declarations or a decision and the source evidence disagree.</summary>
    IdentityConflict,
    /// <summary>A decision was made on evidence that has since changed.</summary>
    StaleDecision,
    AliasConfirmation,
    CapabilityRelation,
    SourceSymbol,
    TestLink,
    ContractVersion,
    /// <summary>A task names a removed or undefined story (fixed in the tasks file, not by a mapping).</summary>
    StoryReference,
}

/// <summary>One review item. <see cref="TargetKey"/> is what a decision is recorded against (relation key or alias target key); null when the resolution
/// is a change in the source itself (a story reference).</summary>
public sealed record ReviewItemView(
    string ReviewItemId, ReviewItemKind Kind, ReviewItemType Type, int Priority, string Reason, ReviewTargetKind? TargetKind, string? TargetKey,
    CanonicalRelationView? Relation, CanonicalAliasView? Alias, CanonicalEntityView? Subject, IReadOnlyList<string> AffectedConsumers, string RuleId,
    string? RuleSetVersion, string State, Guid? InterpretationId, DateTimeOffset LastEvaluated, string? Resolution,
    IReadOnlyList<CanonicalRelationView>? Candidates = null);

public sealed record ReviewQueueView(int Decisions, int Suggestions, IReadOnlyDictionary<ReviewItemType, int> DecisionsByType, IReadOnlyDictionary<ReviewItemType, int> SuggestionsByType,
    CanonicalPage<ReviewItemView> Page, Guid? InterpretationId);

// ---- End-to-end canonical traceability (Requirement → work item → task → implementation → API/contract → test), built only from relations ----

/// <summary>Scope-wide counts of the canonical work-item graph. Document, implementation and test evidence are separate counts — never one percentage.</summary>
public sealed record TraceSummaryView(Guid? InterpretationId, int Features, int Requirements, int UserStories, int Scenarios, int FunctionalGroups, int ScenarioGroups,
    int Tasks, int SharedFoundationTasks, int TasksWithImplementation, int RequirementsWithExplicitTests, int RequirementsWithCandidateTestsOnly, int RequirementsWithoutTests,
    int UnresolvedStoryReferences, int RemovedStoryReferences, IReadOnlyList<string> Limitations);

public sealed record TraceFeatureRow(CanonicalEntityView Feature, int Requirements, int WorkItems, int Tasks, int TasksWithImplementation, int RequirementsWithExplicitTests,
    int RequirementsWithoutTests, int StoryReferenceIssues);

/// <summary>One linked entity and the relation that links it (its state and "why linked" travel with it).</summary>
public sealed record TraceLink(CanonicalEntityView Entity, CanonicalRelationView Relation);

public sealed record TraceRequirement(CanonicalEntityView Requirement, IReadOnlyList<TraceLink> Tests);

public sealed record TraceTask(CanonicalEntityView Task, IReadOnlyList<TraceLink> Implementation, IReadOnlyList<TraceLink> Operations, IReadOnlyList<TraceLink> Contracts,
    IReadOnlyList<TraceLink> Tests);

/// <summary>A user story, scenario, functional/scenario group, shared work or the unresolved references of a feature, with its tasks.</summary>
public sealed record TraceWorkItem(CanonicalEntityView? Item, string Kind, IReadOnlyList<TraceTask> Tasks);

public sealed record TraceFeatureDetail(CanonicalEntityView Feature, IReadOnlyList<TraceRequirement> Requirements, IReadOnlyList<TraceWorkItem> WorkItems,
    IReadOnlyList<string> Gaps, Guid? InterpretationId);

public static class CanonicalReviewQueue
{
    /// <summary>
    /// Classifies a relation that needs attention, or null when it does not. Order: a decision contradicting evidence → identity conflict (0); conflicting
    /// declarations (1); a stale decision (2); ambiguity between several targets (3); a removed story reference (4); naming-only candidates are suggestions
    /// (10+). Ranked by what the ambiguity affects, never by how many evidence rows it has.
    /// </summary>
    public static (ReviewItemKind Kind, ReviewItemType Type, int Priority)? Classify(string ruleId, RelationResolution resolution, RelationReviewState review, bool active)
    {
        if (!active || review is RelationReviewState.Confirmed or RelationReviewState.Rejected) return null;
        if (review == RelationReviewState.Conflict) return (ReviewItemKind.Decision, ReviewItemType.IdentityConflict, 0);
        if (ruleId == CorrelationRules.AliasConflictRule) return (ReviewItemKind.Decision, ReviewItemType.IdentityConflict, 1);
        if (review == RelationReviewState.RevalidationRequired) return (ReviewItemKind.Decision, ReviewItemType.StaleDecision, 2);
        if (resolution == RelationResolution.Ambiguous) return (ReviewItemKind.Decision, TypeOf(ruleId), 3);
        if (resolution == RelationResolution.Unresolved) return (ReviewItemKind.Decision, ReviewItemType.StoryReference, 4);
        if (resolution == RelationResolution.Candidate)
            return (ReviewItemKind.Suggestion, TypeOf(ruleId), TypeOf(ruleId) switch { ReviewItemType.CapabilityRelation => 10, ReviewItemType.SourceSymbol => 11, _ => 12 });
        return null;
    }

    /// <summary>
    /// The one question an ambiguous relation belongs to: a test naming an id several packs define, or a task naming several same-named types, is ONE choice
    /// among its alternatives (grouped by the side they share); conflicting declarations are grouped by the entity they all compete with.
    /// </summary>
    public static string AmbiguityGroup(string ruleId, Guid sourceId, Guid targetId) =>
        ruleId is CorrelationRules.AliasConflictRule or CorrelationRules.ContractVersionConflictRule ? $"{ruleId}|to|{targetId}" : $"{ruleId}|from|{sourceId}";

    /// <summary>Decisions and suggestions over a set of relations, counting one decision per ambiguity group (the same rule the queue uses).</summary>
    public static (int Decisions, int Suggestions) Count(IEnumerable<(string RuleId, RelationResolution Resolution, RelationReviewState Review, Guid Source, Guid Target)> relations)
    {
        var list = relations.ToList();
        var closed = list.Where(r => r.Resolution == RelationResolution.Ambiguous && r.Review == RelationReviewState.Confirmed)
            .Select(r => AmbiguityGroup(r.RuleId, r.Source, r.Target)).ToHashSet(StringComparer.Ordinal);
        int decisions = 0, suggestions = 0;
        var groups = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in list)
        {
            if (Classify(r.RuleId, r.Resolution, r.Review, true) is not { } c) continue;
            if (c.Kind == ReviewItemKind.Suggestion) { suggestions++; continue; }
            if (r.Resolution == RelationResolution.Ambiguous && r.Review is RelationReviewState.NotReviewed or RelationReviewState.Deferred)
            {
                var g = AmbiguityGroup(r.RuleId, r.Source, r.Target);
                if (closed.Contains(g) || !groups.Add(g)) continue;
            }
            decisions++;
        }
        return (decisions, suggestions);
    }

    public static ReviewItemType TypeOf(string ruleId) => ruleId switch
    {
        CorrelationRules.IntentRule or CorrelationRules.CapabilityRule => ReviewItemType.CapabilityRelation,
        CorrelationRules.SymbolNameRule or CorrelationRules.TaskSymbolRule => ReviewItemType.SourceSymbol,
        CorrelationRules.TestCandidateRule or CorrelationRules.TestScopeAmbiguityRule => ReviewItemType.TestLink,
        CorrelationRules.ContractVersionConflictRule => ReviewItemType.ContractVersion,
        CorrelationRules.AliasConflictRule or CorrelationRules.AcronymAliasRule or CorrelationRules.DeclaredAliasRule => ReviewItemType.AliasConfirmation,
        CorrelationRules.TaskGroupRule => ReviewItemType.StoryReference,
        _ => ReviewItemType.SourceSymbol,
    };

    /// <summary>Where a decision about an entity of this type matters (prioritisation and "Affects:" in every surface).</summary>
    public static IReadOnlyList<string> AffectedConsumers(params CanonicalEntityType[] types)
    {
        var set = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var t in types)
            foreach (var c in t switch
            {
                CanonicalEntityType.Feature or CanonicalEntityType.Requirement or CanonicalEntityType.UserStory or CanonicalEntityType.Scenario or CanonicalEntityType.Task
                    or CanonicalEntityType.FunctionalGroup or CanonicalEntityType.ScenarioGroup or CanonicalEntityType.SharedFoundationGroup => ["Delivery", "Traceability", "Functional Quality Scope"],
                CanonicalEntityType.Capability => ["Delivery", "Traceability", "Functional Quality Scope"],
                CanonicalEntityType.Test or CanonicalEntityType.TestSuite => ["Traceability", "Test Coverage", "Functional Quality Scope"],
                CanonicalEntityType.System or CanonicalEntityType.Service => ["Integration Topology", "Integration Quality Review", "Project Evidence"],
                CanonicalEntityType.Package or CanonicalEntityType.Library or CanonicalEntityType.Repository => ["Project Evidence", "Source Analysis"],
                CanonicalEntityType.Contract or CanonicalEntityType.ContractVersion or CanonicalEntityType.ContractOccurrence => ["API Quality Review", "Integration Quality Review", "Source Analysis"],
                CanonicalEntityType.ApiOperation => ["API Quality Review", "Performance", "Traceability"],
                CanonicalEntityType.SourceSymbol => ["Traceability", "Code Risk"],
                CanonicalEntityType.MessagingChannel => ["Integration Topology", "Integration Quality Review"],
                _ => Array.Empty<string>(),
            })
                set.Add(c);
        return [.. set];
    }

    public static string Label(ReviewItemType t) => t switch
    {
        ReviewItemType.IdentityConflict => "Identity conflict",
        ReviewItemType.StaleDecision => "Decision to recheck",
        ReviewItemType.AliasConfirmation => "Alias",
        ReviewItemType.CapabilityRelation => "Shared capability",
        ReviewItemType.SourceSymbol => "Code reference",
        ReviewItemType.TestLink => "Test link",
        ReviewItemType.ContractVersion => "Contract version",
        _ => "Story reference",
    };

    /// <summary>What resolves a story-reference item: it is fixed in the tasks file, never by a mapping in BirkNext.</summary>
    public const string StoryReferenceResolution = "Fix the reference in the tasks file (update the task, restore the story, or mark the task obsolete), then re-import.";
}
