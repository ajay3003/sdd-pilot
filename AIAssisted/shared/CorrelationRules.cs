using System.Text.RegularExpressions;

namespace BirkNext.Interpretation;

// Correlation rules for cases G–N. Pure and deterministic (inputs are sorted before use, so the same facts always give the same entities, relations and
// order). Rule ids name the rule that produced each relation; the rule set is versioned so a stored interpretation snapshot can say which rules made it.
// Rules never merge two entities: the strongest heuristic outcome is a Candidate relation that a human confirms or rejects.

/// <summary>A feature or story as written in one module's specification (case G/H input).</summary>
/// <summary>One feature for intent correlation. <paramref name="CitedDecisions"/>: decision records (ADR-nnn) its spec.md cites (null when the
/// snapshot was imported before citations were recorded).</summary>
public sealed record WorkItemIntentInput(string Module, string FeatureKey, string Title, IReadOnlyList<string> StoryTitles, string? Path, string? SourceSnapshotId = null,
    IReadOnlyList<string>? CitedDecisions = null);

/// <summary>A contract file as found in one package of one source snapshot (case I/J input).</summary>
public sealed record ContractOccurrenceInput(
    string Kind, string Package, string? Repository, string Path, string SourceSnapshotId, string ContentHash, string? Namespace, string? DeclaredVersion,
    string? Title, string? SourceRevision = null, IReadOnlyList<string>? ReferencingServices = null, string? DeclaredIdentity = null);

/// <summary>A source symbol (class, method, handler) with what the source says about it (case L input).</summary>
public sealed record SourceSymbolInput(
    string Package, string Path, string Symbol, string? Namespace, string Kind, string? HttpMethod, string? Route, IReadOnlyList<string> RequirementIds,
    IReadOnlyList<string> ContractReferences, string? Service, string? SourceSnapshotId = null);

/// <summary>An API operation as the evidence identifies it (method + normalized route).</summary>
public sealed record ApiOperationInput(string Service, string Method, string Route, string Source, string? SourceSnapshotId = null);

/// <summary>A detected test and its requirement links as the test evidence classifies them (case M input). Execution status is deliberately absent.</summary>
public sealed record TestInput(
    string Package, string Path, string Name, string? Suite, IReadOnlyList<string> ExplicitTargets, IReadOnlyList<string> CandidateTargets, string? SourceSnapshotId = null,
    string? StableIdentity = null);

/// <summary>A messaging channel and the services that produce to / consume from it, as the source declares them.</summary>
public sealed record MessagingChannelInput(string Kind, string EntityName, IReadOnlyList<string> Producers, IReadOnlyList<string> Consumers, string Evidence, string? SourceSnapshotId = null);

/// <summary>A system / service with its declared alias names (case K input). Declared means: in a profile, a manifest or a configuration key.</summary>
public sealed record SystemIdentityInput(CanonicalEntityType Type, string Name, IReadOnlyList<string> DeclaredAliases, string Source);

/// <summary>A task as written in a feature's tasks file (case L/M input: a task that names a source symbol).</summary>
public sealed record TaskReferenceInput(string Module, string FeatureKey, string TaskKey, string Text, string? SourceSnapshotId = null);

/// <summary>A shared library a package publishes or declares as an internal shared project (e.g. M2LB.Common.Health).</summary>
public sealed record LibraryInput(string Package, string Name, string Source, string? SourceSnapshotId = null);

/// <summary>A service's stated use of a library (project reference or package reference). Only libraries some package in scope provides become relations.</summary>
public sealed record LibraryUseInput(string Service, string Library, string Evidence, string? SourceSnapshotId = null);

/// <summary>A requirement as written in one feature pack's spec (FR-001 in pack A is not FR-001 in pack B).</summary>
public sealed record WorkItemRequirementInput(string Key, string Text);

/// <summary>A user story or acceptance scenario defined in one feature pack's spec.</summary>
public sealed record WorkItemStoryInput(string Key, string Title, bool Scenario, bool Removed);

/// <summary>A task with the shared Phase 1 interpretation (<see cref="WorkItemInterpreter"/>) already applied by the reader: the canonical engine never
/// reclassifies a task, it persists what the one work-item engine decided.</summary>
public sealed record WorkItemTaskInput(string TaskKey, string Text, bool Completed, string? Phase, BirkNext.DeliveryReporting.DeliveryTaskRelationship Relationship,
    string? GroupKey, string? LinkedKey, string WorkItemRuleId, string? StoryReference);

/// <summary>One feature pack's work-item structure: requirements, stories/scenarios and interpreted tasks (cases A–F persisted canonically).</summary>
public sealed record WorkItemStructureInput(string Module, string FeatureKey, string Title, IReadOnlyList<WorkItemRequirementInput> Requirements,
    IReadOnlyList<WorkItemStoryInput> Stories, IReadOnlyList<WorkItemTaskInput> Tasks, string? SourceSnapshotId = null);

/// <summary>Everything one correlation pass reads, all scoped to one project.</summary>
public sealed record CorrelationInput(
    string Scope,
    IReadOnlyList<WorkItemIntentInput> WorkItems,
    IReadOnlyList<ContractOccurrenceInput> Contracts,
    IReadOnlyList<SourceSymbolInput> Symbols,
    IReadOnlyList<ApiOperationInput> Operations,
    IReadOnlyList<TestInput> Tests,
    IReadOnlyList<SystemIdentityInput> Systems,
    IReadOnlyList<CanonicalEntityFact>? AdditionalEntities = null,
    IReadOnlyList<CanonicalRelationFact>? AdditionalRelations = null,
    IReadOnlyList<MessagingChannelInput>? Channels = null,
    IReadOnlyList<TaskReferenceInput>? Tasks = null,
    IReadOnlyList<LibraryInput>? Libraries = null,
    IReadOnlyList<LibraryUseInput>? LibraryUses = null,
    IReadOnlyList<WorkItemStructureInput>? Structures = null,
    IReadOnlyList<GuideScenarioInput>? GuideScenarios = null)
{
    public static CorrelationInput Empty(string scope) => new(scope, [], [], [], [], [], []);
}

public static partial class CorrelationRules
{
    /// <summary>Bumped whenever a rule's outcome can change for the same input; stored on every interpretation snapshot.</summary>
    /// <remarks>correlation/3: feature-pack-scoped requirements, stories, groups and tasks (Phase 1 interpretation persisted), and explicit test links
    /// resolved to the feature pack they belong to (FR-001 is never merged across packs by its text).</remarks>
    /// <remarks>correlation/2: contract versions keep their version context (case J), occurrences use OCCURRENCE_OF, shared libraries + USES,
    /// task → source-symbol references (CR-L-TASK), conflicting declared aliases surface for review (CR-K-CONFLICT).</remarks>
    /// <remarks>correlation/4: features that declare the same decision record (ADR-nnn in title or folder) form one change intent (CR-G-DECISION);
    /// a feature that only cites it, or only shares the declaring titles' terms, is a candidate for review.</remarks>
    public const string RuleSetVersion = "correlation/4";

    public const string IntentRule = "CR-G-INTENT", CapabilityRule = "CR-H-CAPABILITY", ContractHashRule = "CR-I-HASH", ContractVersionRule = "CR-J-VERSION",
        ContractVersionConflictRule = "CR-J-CONFLICT", DeclaredAliasRule = "CR-K-DECLARED", AcronymAliasRule = "CR-K-ACRONYM", SymbolRouteRule = "CR-L-ROUTE",
        SymbolRequirementRule = "CR-L-REQUIREMENT", SymbolContractRule = "CR-L-CONTRACT", SymbolServiceRule = "CR-L-SERVICE", SymbolNameRule = "CR-L-NAME",
        TestExplicitRule = "CR-M-EXPLICIT", TestCandidateRule = "CR-M-CANDIDATE", TestSuiteRule = "CR-M-SUITE", OperationServiceRule = "CR-N-EXPOSES", ContractServiceRule = "CR-N-CONTRACT",
        ChannelRule = "CR-N-CHANNEL", AliasConflictRule = "CR-K-CONFLICT", TaskOwnerRule = "CR-L-TASK-OWNER", TaskSymbolRule = "CR-L-TASK", SymbolMemberRule = "CR-L-MEMBER",
        LibraryRule = "CR-N-LIBRARY", LibraryUseRule = "CR-N-USES", RequirementRule = "CR-W-REQUIREMENT", StoryRule = "CR-W-STORY", TaskGroupRule = "CR-W-TASK",
        TestScopeAmbiguityRule = "CR-M-SCOPE", DecisionRule = "CR-G-DECISION";

    /// <summary>Minimum Jaccard share of significant intent terms two features must have in common before a shared capability is even proposed…</summary>
    public const double IntentSimilarityThreshold = 0.34;

    /// <summary>…and at least this many shared terms (one shared word is never a shared intent).</summary>
    public const int IntentMinimumSharedTerms = 3;

    /// <summary>Runs every rule over the input. Nothing is merged; heuristic outcomes are Candidate relations.</summary>
    public static CorrelationResult Correlate(CorrelationInput input)
    {
        var entities = new Dictionary<string, CanonicalEntityFact>(StringComparer.Ordinal);
        var relations = new Dictionary<string, CanonicalRelationFact>(StringComparer.Ordinal);
        var aliases = new List<CanonicalAliasFact>();
        var limitations = new List<string>();

        void Entity(CanonicalEntityFact e)
        {
            if (entities.TryGetValue(e.Key, out var existing))
                entities[e.Key] = existing with { Evidence = [.. existing.Evidence.Concat(e.Evidence).DistinctBy(x => (x.Kind, x.Reference)).OrderBy(x => x.Reference, StringComparer.Ordinal)] };
            else
                entities[e.Key] = e;
        }
        void Relation(CanonicalRelationFact r)
        {
            // The stronger resolution wins when two rules state the same relation; evidence is unioned.
            if (relations.TryGetValue(r.Key, out var existing))
            {
                var keep = Strength(r.Resolution) > Strength(existing.Resolution) ? r : existing;
                relations[r.Key] = keep with { Evidence = [.. existing.Evidence.Concat(r.Evidence).DistinctBy(x => (x.Kind, x.Reference)).OrderBy(x => x.Reference, StringComparer.Ordinal)] };
            }
            else
                relations[r.Key] = r;
        }

        foreach (var e in input.AdditionalEntities ?? []) Entity(e);
        foreach (var r in input.AdditionalRelations ?? []) Relation(r);
        WorkItemIntents(input.WorkItems, Entity, Relation);
        DecisionIntents(input.WorkItems, Entity, Relation);
        WorkItemStructures(input.Structures ?? [], Entity, Relation);
        ContractOccurrences(input.Contracts, Entity, Relation);
        Systems(input.Systems, Entity, Relation, aliases, limitations);
        Operations(input.Operations, Entity, Relation);
        Channels(input.Channels ?? [], Entity, Relation);
        Symbols(input.Symbols, entities, Entity, Relation);
        Libraries(input.Libraries ?? [], input.LibraryUses ?? [], entities, Entity, Relation);
        TaskReferences(input.Tasks ?? [], input.Symbols, entities, Entity, Relation, limitations);
        Tests(input.Tests, entities, Entity, Relation, limitations);
        GuideScenarios(input.GuideScenarios ?? [], entities, relations, Entity, Relation, limitations);

        // A relation whose endpoint was never established is dropped and reported, never pointed at a fabricated entity.
        var dangling = relations.Values.Where(r => !entities.ContainsKey(r.SourceKey) || !entities.ContainsKey(r.TargetKey)).Select(r => r.Key).ToList();
        foreach (var key in dangling) relations.Remove(key);
        if (dangling.Count > 0) limitations.Add($"{dangling.Count} relation(s) dropped because an endpoint is not in the evidence.");

        return new(
            [.. entities.Values.OrderBy(e => e.Key, StringComparer.Ordinal)],
            [.. relations.Values.OrderBy(r => r.Key, StringComparer.Ordinal)],
            [.. aliases.DistinctBy(a => (a.EntityKey, CanonicalKeys.Normalize(a.Alias))).OrderBy(a => a.EntityKey, StringComparer.Ordinal).ThenBy(a => a.Alias, StringComparer.Ordinal)],
            limitations, RuleSetVersion);
    }

    public static int Strength(RelationResolution r) => r switch
    {
        RelationResolution.Explicit => 5, RelationResolution.Derived => 4, RelationResolution.Ambiguous => 3, RelationResolution.Candidate => 2,
        RelationResolution.Unresolved => 1, _ => 0,
    };

    // ---- Case G + H: same intent across modules → candidate shared capability ----

    static void WorkItemIntents(IReadOnlyList<WorkItemIntentInput> items, Action<CanonicalEntityFact> entity, Action<CanonicalRelationFact> relation)
    {
        var features = items.OrderBy(i => i.Module, StringComparer.Ordinal).ThenBy(i => i.FeatureKey, StringComparer.Ordinal).ToList();
        foreach (var f in features)
        {
            entity(new(CanonicalEntityType.Module, CanonicalKeys.Normalize(f.Module), f.Module, f.Module, "declared module folder", [new("module", f.Module)]));
            entity(FeatureFact(f));
            relation(new(ModuleKey(f.Module), FeatureFact(f).Key, CanonicalRelationType.Contains, RelationResolution.Explicit, CorrelationConfidence.Explicit,
                IntentRule, [new("path", f.Path ?? f.FeatureKey, null, f.SourceSnapshotId)], "The feature folder sits in this module."));
        }

        // Pairwise only across different modules; within a module two features are distinct work by construction.
        var terms = features.ToDictionary(f => f, IntentTerms);
        var groups = new List<List<WorkItemIntentInput>>();
        for (var i = 0; i < features.Count; i++)
            for (var j = i + 1; j < features.Count; j++)
            {
                var (a, b) = (features[i], features[j]);
                if (string.Equals(a.Module, b.Module, StringComparison.OrdinalIgnoreCase)) continue;
                if (Similarity(terms[a], terms[b]) < IntentSimilarityThreshold || terms[a].Intersect(terms[b]).Count() < IntentMinimumSharedTerms) continue;
                var group = groups.FirstOrDefault(g => g.Contains(a) || g.Contains(b));
                if (group is null) groups.Add([a, b]);
                else { if (!group.Contains(a)) group.Add(a); if (!group.Contains(b)) group.Add(b); }
            }

        foreach (var group in groups)
        {
            var shared = new SortedSet<string>(terms[group[0]], StringComparer.Ordinal);
            foreach (var member in group.Skip(1)) shared.IntersectWith(terms[member]);
            if (shared.Count == 0) shared = new SortedSet<string>(terms[group[0]].Intersect(terms[group[1]]), StringComparer.Ordinal);
            var identity = "candidate:" + string.Join('-', shared.Order(StringComparer.Ordinal));
            var name = string.Join(' ', shared.Order(StringComparer.Ordinal).Select(Capitalize));
            var capability = new CanonicalEntityFact(CanonicalEntityType.Capability, identity, name, identity, "candidate from shared intent terms (case H)",
                [.. group.Select(g => new CorrelationEvidence("feature", $"{g.Module}/{g.FeatureKey}", g.Title, g.SourceSnapshotId))],
                Attributes: new Dictionary<string, string> { ["candidate"] = "true", ["terms"] = string.Join(',', shared.Order(StringComparer.Ordinal)) });
            entity(capability);
            foreach (var f in group)
                relation(new(FeatureFact(f).Key, capability.Key, CanonicalRelationType.Implements, RelationResolution.Candidate, CorrelationConfidence.Medium, IntentRule,
                    [new("intent-terms", $"{f.Module}/{f.FeatureKey}", string.Join(',', terms[f].Intersect(shared).Order(StringComparer.Ordinal)), f.SourceSnapshotId)],
                    $"\"{f.Title}\" shares the intent terms {string.Join(", ", shared.Order(StringComparer.Ordinal))} with features in other modules. Wording alone never makes them one capability: confirm or reject."));
        }
    }

    // ---- Case G (decision): features declaring the same decision record → one change intent ----

    /// <summary>
    /// A decision record (ADR-nnn) named in the title or folder of two or more features is a change intent they were written under: each declaring
    /// feature Implements it (Derived — the author declared it). A feature whose spec.md only cites the decision, or whose title carries every term
    /// most declaring titles share (at least two) without any reference, is a Candidate a person confirms or rejects. Features stay separate.
    /// </summary>
    static void DecisionIntents(IReadOnlyList<WorkItemIntentInput> items, Action<CanonicalEntityFact> entity, Action<CanonicalRelationFact> relation)
    {
        var features = items.OrderBy(i => i.Module, StringComparer.Ordinal).ThenBy(i => i.FeatureKey, StringComparer.Ordinal).ToList();
        var declared = features.SelectMany(f => DecisionReferences.Declared(f.Title, f.FeatureKey).Select(d => (Decision: d, Feature: f)))
            .GroupBy(x => x.Decision, StringComparer.Ordinal).Where(g => g.Select(x => FeatureFact(x.Feature).Key).Distinct().Count() >= 2).OrderBy(g => g.Key, StringComparer.Ordinal);
        foreach (var group in declared)
        {
            var members = group.Select(x => x.Feature).DistinctBy(f => FeatureFact(f).Key).ToList();
            var titleTerms = members.Select(f => TitleTerms(f.Title)).ToList();
            // The intent's words: terms most declaring titles share (more than half), in title order. One differently worded member (a rename
            // under the same decision) does not erase the intent's name.
            var shared = titleTerms.SelectMany(t => t).Distinct(StringComparer.Ordinal)
                .Where(term => titleTerms.Count(t => t.Contains(term)) * 2 > titleTerms.Count && titleTerms.Count(t => t.Contains(term)) >= 2)
                .OrderBy(term => titleTerms.Select(t => t.ToList().IndexOf(term)).Where(i => i >= 0).Min()).ThenBy(term => term, StringComparer.Ordinal).ToList();
            var name = shared.Count > 0 ? $"{group.Key}: {string.Join(' ', shared.Select(Capitalize))}" : group.Key;
            var capability = new CanonicalEntityFact(CanonicalEntityType.Capability, "decision:" + group.Key.ToLowerInvariant(), name, group.Key,
                "change intent declared by a shared decision record (case G)",
                [.. members.Select(m => new CorrelationEvidence("feature", $"{m.Module}/{m.FeatureKey}", m.Title, m.SourceSnapshotId))],
                Attributes: new Dictionary<string, string> { ["kind"] = "change-intent", ["decision"] = group.Key, ["terms"] = string.Join(',', shared) });
            entity(capability);
            foreach (var f in members)
                relation(new(FeatureFact(f).Key, capability.Key, CanonicalRelationType.Implements, RelationResolution.Derived, CorrelationConfidence.High, DecisionRule,
                    [new("declared-decision", $"{f.Module}/{f.FeatureKey}", group.Key, f.SourceSnapshotId)],
                    $"\"{f.Title}\" declares {group.Key} in its title or folder name."));
            foreach (var f in features.Where(f => members.All(m => FeatureFact(m).Key != FeatureFact(f).Key)))
            {
                if (f.CitedDecisions?.Contains(group.Key, StringComparer.Ordinal) == true)
                    relation(new(FeatureFact(f).Key, capability.Key, CanonicalRelationType.Implements, RelationResolution.Candidate, CorrelationConfidence.Medium, DecisionRule,
                        [new("cited-decision", $"{f.Module}/{f.FeatureKey}", group.Key, f.SourceSnapshotId)],
                        $"spec.md of \"{f.Title}\" cites {group.Key} but the feature does not declare it: citing a decision does not by itself make the feature part of that change — confirm or reject."));
                else if (shared.Count >= 2 && shared.All(TitleTerms(f.Title).Contains))
                    relation(new(FeatureFact(f).Key, capability.Key, CanonicalRelationType.Implements, RelationResolution.Candidate, CorrelationConfidence.Low, DecisionRule,
                        [new("title-terms", $"{f.Module}/{f.FeatureKey}", string.Join(',', shared), f.SourceSnapshotId)],
                        $"\"{f.Title}\" shares the terms {string.Join(", ", shared)} with the features declaring {group.Key}, without any {group.Key} reference. Wording alone never decides membership: confirm or reject."));
            }
        }
    }

    /// <summary>Significant title terms in title order (no stories): the words the declaring titles have in common name the change intent.</summary>
    static IReadOnlyList<string> TitleTerms(string title) =>
        [.. IntentTerms(new WorkItemIntentInput("", "", title, [], null)).OrderBy(t => title.IndexOf(t, StringComparison.OrdinalIgnoreCase) is var i and >= 0 ? i : int.MaxValue).ThenBy(t => t, StringComparer.Ordinal)];

    /// <summary>The canonical Feature identity of a Delivery feature (module + feature folder key), as stored by case G.</summary>
    public static string FeatureIdentity(string module, string featureKey) => $"{CanonicalKeys.Normalize(module)}/{CanonicalKeys.Normalize(featureKey)}";

    static CanonicalEntityFact FeatureFact(WorkItemIntentInput f) =>
        new(CanonicalEntityType.Feature, FeatureIdentity(f.Module, f.FeatureKey), f.Title, $"{f.Module}/{f.FeatureKey}",
            "module + feature folder key", [new("feature", f.Path ?? f.FeatureKey, null, f.SourceSnapshotId)]);

    static string ModuleKey(string module) => CanonicalKeys.EntityKey(CanonicalEntityType.Module, CanonicalKeys.Normalize(module));

    static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "the", "a", "an", "and", "or", "of", "to", "for", "in", "on", "with", "by", "from", "as", "is", "be", "it", "this", "that", "at", "via", "into",
        "og", "i", "på", "til", "for", "med", "av", "en", "et", "som", "fra", "er", "det", "den", "de",
        "feature", "story", "user", "support", "add", "new", "update", "implement", "phase", "module", "service", "system", "basic", "improve",
    };

    /// <summary>Significant intent terms of a feature: title + story titles, lower-cased, stop words and short tokens removed, simple plural folding.</summary>
    public static IReadOnlySet<string> IntentTerms(WorkItemIntentInput item)
    {
        var text = string.Join(' ', [item.Title, .. item.StoryTitles]);
        var set = new SortedSet<string>(StringComparer.Ordinal);
        foreach (Match m in WordRe().Matches(text.ToLowerInvariant()))
        {
            var w = m.Value;
            if (w.Length < 4 || StopWords.Contains(w) || w.All(char.IsDigit)) continue;
            set.Add(w.EndsWith("ies", StringComparison.Ordinal) ? w[..^3] + "y" : w.EndsWith('s') && !w.EndsWith("ss", StringComparison.Ordinal) ? w[..^1] : w);
        }
        return set;
    }

    /// <summary>Jaccard similarity of two term sets (0 when either is empty).</summary>
    public static double Similarity(IReadOnlySet<string> a, IReadOnlySet<string> b) =>
        a.Count == 0 || b.Count == 0 ? 0 : (double)a.Intersect(b).Count() / a.Union(b).Count();

    static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    [GeneratedRegex(@"[\p{L}][\p{L}\p{Nd}]+")]
    private static partial Regex WordRe();

    // ---- Cases A–F persisted: feature-pack-scoped requirements, stories, groups and tasks ----

    /// <summary>A work-item identity inside one feature pack: module + feature folder + the id as written (FR-001, US1, S2, T010, group:US3, shared).</summary>
    public static string Scoped(string module, string featureKey, string id) => $"{CanonicalKeys.Normalize(module)}/{CanonicalKeys.Normalize(featureKey)}/{id}";

    /// <summary>Requirement text without the list marker and the id it repeats ("- **FR-001**: The system…" → "The system…"); display only, never identity.</summary>
    public static string RequirementText(string text, string key)
    {
        var t = text.Trim().TrimStart('-', '*', ' ').Replace("**", "").Trim();
        if (t.StartsWith(key, StringComparison.OrdinalIgnoreCase)) t = t[key.Length..].TrimStart(' ', ':', '-', '–', '.').Trim();
        return t.Length == 0 ? text.Trim() : t;
    }

    static void WorkItemStructures(IReadOnlyList<WorkItemStructureInput> structures, Action<CanonicalEntityFact> entity, Action<CanonicalRelationFact> relation)
    {
        foreach (var f in structures.OrderBy(f => f.Module, StringComparer.Ordinal).ThenBy(f => f.FeatureKey, StringComparer.Ordinal))
        {
            var feature = new CanonicalEntityFact(CanonicalEntityType.Feature, $"{CanonicalKeys.Normalize(f.Module)}/{CanonicalKeys.Normalize(f.FeatureKey)}", f.Title, $"{f.Module}/{f.FeatureKey}",
                "module + feature folder key", [new("feature", f.FeatureKey, f.Module, f.SourceSnapshotId)]);
            entity(feature);
            CorrelationEvidence Ev(string kind, string reference) => new(kind, $"{f.Module}/{f.FeatureKey}/{reference}", null, f.SourceSnapshotId);
            foreach (var r in f.Requirements.OrderBy(r => r.Key, StringComparer.Ordinal))
            {
                var id = NormalizeRequirement(r.Key) ?? r.Key;
                var fact = new CanonicalEntityFact(CanonicalEntityType.Requirement, Scoped(f.Module, f.FeatureKey, id), $"{r.Key}: {Shorten(RequirementText(r.Text, r.Key), 100)}", $"{f.Module}/{f.FeatureKey}/{r.Key}",
                    "module + feature pack + requirement id", [Ev("requirement", r.Key)], Attributes: Attrs(("id", id), ("module", f.Module), ("feature", f.FeatureKey), ("text", Shorten(r.Text, 400))));
                entity(fact);
                relation(new(feature.Key, fact.Key, CanonicalRelationType.Contains, RelationResolution.Explicit, CorrelationConfidence.Explicit, RequirementRule, [Ev("requirement", r.Key)],
                    "The requirement is written in this feature pack's specification."));
            }
            foreach (var st in f.Stories.OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                var fact = new CanonicalEntityFact(st.Scenario ? CanonicalEntityType.Scenario : CanonicalEntityType.UserStory, Scoped(f.Module, f.FeatureKey, st.Key),
                    $"{st.Key} {Shorten(st.Title, 100)}", $"{f.Module}/{f.FeatureKey}/{st.Key}", "module + feature pack + story key", [Ev("story", st.Key)],
                    Attributes: Attrs(("id", st.Key), ("module", f.Module), ("feature", f.FeatureKey), ("removed", st.Removed ? "true" : null)));
                entity(fact);
                relation(new(feature.Key, fact.Key, CanonicalRelationType.Contains, RelationResolution.Explicit, CorrelationConfidence.Explicit, StoryRule, [Ev("story", st.Key)],
                    st.Removed ? "The specification lists this story as removed." : $"The {(st.Scenario ? "scenario" : "user story")} is defined in this feature pack's specification."));
            }
            foreach (var t in f.Tasks.OrderBy(t => t.TaskKey, StringComparer.Ordinal))
            {
                var task = new CanonicalEntityFact(CanonicalEntityType.Task, Scoped(f.Module, f.FeatureKey, CanonicalKeys.Normalize(t.TaskKey)), $"{t.TaskKey} {Shorten(t.Text, 120)}",
                    $"{f.Module}/{f.FeatureKey}/{t.TaskKey}", "module + feature + task key", [Ev("task", t.TaskKey)],
                    Attributes: Attrs(("id", t.TaskKey), ("module", f.Module), ("feature", f.FeatureKey), ("completed", t.Completed ? "true" : "false"), ("phase", t.Phase),
                        ("relationship", t.Relationship.ToString()), ("workItemRule", t.WorkItemRuleId), ("storyReference", t.StoryReference)));
                entity(task);
                relation(new(feature.Key, task.Key, CanonicalRelationType.Contains, RelationResolution.Explicit, CorrelationConfidence.Explicit, TaskOwnerRule, [Ev("task", t.TaskKey)],
                    "The task is listed in this feature's tasks file. A checked box is not proof of implementation."));
                var why = $"Work-item rule {t.WorkItemRuleId} ({WorkItemInterpreter.Label(t.Relationship)}).";
                switch (t.Relationship)
                {
                    case BirkNext.DeliveryReporting.DeliveryTaskRelationship.ExplicitStory or BirkNext.DeliveryReporting.DeliveryTaskRelationship.StoryPhase when t.LinkedKey is not null:
                        relation(new(task.Key, CanonicalKeys.EntityKey(CanonicalEntityType.UserStory, Scoped(f.Module, f.FeatureKey, t.LinkedKey)), CanonicalRelationType.BelongsTo,
                            t.Relationship == BirkNext.DeliveryReporting.DeliveryTaskRelationship.ExplicitStory ? RelationResolution.Explicit : RelationResolution.Derived,
                            t.Relationship == BirkNext.DeliveryReporting.DeliveryTaskRelationship.ExplicitStory ? CorrelationConfidence.Explicit : CorrelationConfidence.High, TaskGroupRule,
                            [Ev("task", t.TaskKey)], why));
                        break;
                    case BirkNext.DeliveryReporting.DeliveryTaskRelationship.FunctionalGroup or BirkNext.DeliveryReporting.DeliveryTaskRelationship.ScenarioGroup:
                    {
                        var scenario = t.Relationship == BirkNext.DeliveryReporting.DeliveryTaskRelationship.ScenarioGroup;
                        var group = new CanonicalEntityFact(scenario ? CanonicalEntityType.ScenarioGroup : CanonicalEntityType.FunctionalGroup,
                            Scoped(f.Module, f.FeatureKey, "group:" + (t.GroupKey ?? "?")), $"{t.GroupKey} — {(scenario ? "Scenario group" : "Functional group")}",
                            $"{f.Module}/{f.FeatureKey}/{t.GroupKey}", "module + feature + group key", [Ev("group", t.GroupKey ?? "?")],
                            Attributes: Attrs(("id", t.GroupKey), ("module", f.Module), ("feature", f.FeatureKey)));
                        entity(group);
                        relation(new(feature.Key, group.Key, CanonicalRelationType.Contains, RelationResolution.Derived, CorrelationConfidence.High, TaskGroupRule, [Ev("group", t.GroupKey ?? "?")],
                            "Derived from the tasks file's [USn] tags (the spec defines no matching user stories)."));
                        relation(new(task.Key, group.Key, CanonicalRelationType.BelongsTo, RelationResolution.Derived, CorrelationConfidence.High, TaskGroupRule, [Ev("task", t.TaskKey)], why));
                        if (scenario && t.LinkedKey is not null)
                            relation(new(group.Key, CanonicalKeys.EntityKey(CanonicalEntityType.Scenario, Scoped(f.Module, f.FeatureKey, t.LinkedKey)), CanonicalRelationType.References,
                                RelationResolution.Derived, CorrelationConfidence.High, TaskGroupRule, [Ev("task", t.TaskKey)], "The phase title names this scenario."));
                        break;
                    }
                    case BirkNext.DeliveryReporting.DeliveryTaskRelationship.SharedFoundational:
                    {
                        var shared = new CanonicalEntityFact(CanonicalEntityType.SharedFoundationGroup, Scoped(f.Module, f.FeatureKey, "shared"), "Shared & foundational work",
                            $"{f.Module}/{f.FeatureKey}/shared", "module + feature", [Ev("group", "shared")], Attributes: Attrs(("module", f.Module), ("feature", f.FeatureKey)));
                        entity(shared);
                        relation(new(task.Key, shared.Key, CanonicalRelationType.BelongsTo, RelationResolution.Derived, CorrelationConfidence.High, TaskGroupRule, [Ev("task", t.TaskKey)],
                            why + " Shared and foundational work is not a missing user story."));
                        break;
                    }
                    case BirkNext.DeliveryReporting.DeliveryTaskRelationship.RemovedStoryReference when t.GroupKey is not null:
                        relation(new(task.Key, CanonicalKeys.EntityKey(CanonicalEntityType.UserStory, Scoped(f.Module, f.FeatureKey, t.GroupKey)), CanonicalRelationType.References,
                            RelationResolution.Unresolved, CorrelationConfidence.Explicit, TaskGroupRule, [Ev("task", t.TaskKey)],
                            why + " The task names a story the specification marks removed: update the task, restore the story or mark the task obsolete."));
                        break;
                    // Unresolved references ([US6] when US1–US5 exist, ranges) have no target: the task keeps the reference as an attribute and is a review item.
                }
            }
        }
    }

    // ---- Case I + J: contract identity ≠ occurrence; versions never collapse ----

    /// <summary>Contract identity: declared namespace (XSD/message) or declared title (OpenAPI/GraphQL/JSON), never the file name or path.</summary>
    public static string? ContractIdentity(ContractOccurrenceInput c)
    {
        if (!string.IsNullOrWhiteSpace(c.DeclaredIdentity)) return $"{c.Kind.ToLowerInvariant()}:{CanonicalKeys.Normalize(c.DeclaredIdentity)}";
        var declared = !string.IsNullOrWhiteSpace(c.Namespace) ? c.Namespace : c.Title;
        return string.IsNullOrWhiteSpace(declared) ? null : $"{c.Kind.ToLowerInvariant()}:{CanonicalKeys.Normalize(declared)}";
    }

    /// <summary>
    /// The version context of an occurrence (case J): its declared version ("v2.1"), else a version segment of its path ("contracts/v2/x.xsd",
    /// "Api.V1.Schema.xsd"), else null. Identical bytes under v1 and v2 stay two versions sharing one content hash; identical bytes in several
    /// packages under the same (or no) context stay one version with several occurrences (case I).
    /// </summary>
    public static string? VersionContext(ContractOccurrenceInput c)
    {
        if (!string.IsNullOrWhiteSpace(c.DeclaredVersion)) return "v" + c.DeclaredVersion.Trim().TrimStart('v', 'V').ToLowerInvariant();
        var path = c.Path.Replace('\\', '/');
        var dot = path.LastIndexOf('.');
        var m = PathVersionRe().Match(dot > path.LastIndexOf('/') ? path[..dot] : path);
        return m.Success ? "v" + m.Groups[1].Value.Replace('_', '.').ToLowerInvariant() : null;
    }

    [GeneratedRegex(@"(?:^|[/_.\-])[vV](\d+(?:[._]\d+)*)(?=$|[/_.\-])")]
    private static partial Regex PathVersionRe();

    /// <summary>Contract version identity: content hash, qualified by version context when the occurrence has one.</summary>
    public static string ContractVersionIdentity(ContractOccurrenceInput c) =>
        $"{c.Kind.ToLowerInvariant()}:sha256:{c.ContentHash.ToLowerInvariant()}{(VersionContext(c) is { } ctx ? "@" + ctx : "")}";

    static void ContractOccurrences(IReadOnlyList<ContractOccurrenceInput> contracts, Action<CanonicalEntityFact> entity, Action<CanonicalRelationFact> relation)
    {
        var ordered = contracts.OrderBy(c => c.SourceSnapshotId, StringComparer.Ordinal).ThenBy(c => c.Package, StringComparer.Ordinal).ThenBy(c => c.Path, StringComparer.Ordinal).ToList();
        foreach (var c in ordered)
        {
            var occurrence = new CanonicalEntityFact(CanonicalEntityType.ContractOccurrence, $"{c.SourceSnapshotId}|{CanonicalKeys.Normalize(c.Package)}|{c.Path.Replace('\\', '/')}",
                $"{c.Package}: {c.Path}", c.Path, "snapshot + package + path",
                [new("path", c.Path, c.Package, c.SourceSnapshotId)],
                Attributes: Attrs(("package", c.Package), ("repository", c.Repository), ("contentHash", c.ContentHash), ("kind", c.Kind), ("revision", c.SourceRevision), ("declaredVersion", c.DeclaredVersion)));
            entity(occurrence);

            // Identical bytes are the same contract content wherever they occur (case I); the occurrence records stay separate. The version context
            // (declared version or path version segment) is part of the version identity, so v1/ and v2/ copies of the same bytes stay apart (case J).
            var context = VersionContext(c);
            var version = new CanonicalEntityFact(CanonicalEntityType.ContractVersion, ContractVersionIdentity(c),
                $"{c.Title ?? c.Namespace ?? System.IO.Path.GetFileName(c.Path)}{(context is null ? "" : $" {context}")}", c.ContentHash,
                context is null ? "content hash" : "content hash + version context",
                [new("contentHash", c.ContentHash, c.Path, c.SourceSnapshotId)], c.DeclaredVersion ?? context?.TrimStart('v'),
                Attrs(("namespace", c.Namespace), ("declaredVersion", c.DeclaredVersion), ("kind", c.Kind), ("contentHash", c.ContentHash.ToLowerInvariant()),
                    ("versionContext", context), ("versionContextSource", context is null ? null : c.DeclaredVersion is null ? "path" : "declared")));
            entity(version);
            relation(new(occurrence.Key, version.Key, CanonicalRelationType.OccurrenceOf, RelationResolution.Derived, CorrelationConfidence.High, ContractHashRule,
                [new("contentHash", c.ContentHash, c.Path, c.SourceSnapshotId)], context is null
                    ? "The file content hashes to this contract version."
                    : $"The file content hashes to this contract version in version context {context} ({(c.DeclaredVersion is null ? "from its path" : "declared")})."));

            var identity = ContractIdentity(c);
            if (identity is null) continue; // no declared identity: the version stands alone rather than being grouped by file name
            var contract = new CanonicalEntityFact(CanonicalEntityType.Contract, identity, c.Title ?? c.Namespace!, c.Namespace ?? c.Title!, "declared namespace/title",
                [new("declared", c.Namespace ?? c.Title!, c.Kind, c.SourceSnapshotId)], Attributes: Attrs(("kind", c.Kind), ("namespace", c.Namespace)));
            entity(contract);
            relation(new(version.Key, contract.Key, CanonicalRelationType.BelongsTo, RelationResolution.Derived, CorrelationConfidence.High, ContractVersionRule,
                [new("declared", c.Namespace ?? c.Title!, c.DeclaredVersion, c.SourceSnapshotId)], "The version declares this contract's namespace/title."));
        }

        // Same declared identity and declared version but different content is a conflict to review, never a silent collapse (case J).
        foreach (var clash in ordered.Where(c => ContractIdentity(c) is not null && c.DeclaredVersion is not null)
                     .GroupBy(c => (Identity: ContractIdentity(c)!, c.DeclaredVersion))
                     .Where(g => g.Select(c => c.ContentHash.ToLowerInvariant()).Distinct().Count() > 1))
        {
            var versions = clash.Select(ContractVersionIdentity).Distinct().Order(StringComparer.Ordinal).ToList();
            for (var i = 1; i < versions.Count; i++)
                relation(new(CanonicalKeys.EntityKey(CanonicalEntityType.ContractVersion, versions[i]),
                    CanonicalKeys.EntityKey(CanonicalEntityType.ContractVersion, versions[0]),
                    CanonicalRelationType.CandidateSameAs, RelationResolution.Ambiguous, CorrelationConfidence.Low, ContractVersionConflictRule,
                    [.. clash.Select(c => new CorrelationEvidence("path", c.Path, c.Package, c.SourceSnapshotId))],
                    $"Both declare {clash.Key.Identity} version {clash.Key.DeclaredVersion} but their content differs. Compatibility is never inferred from the name: review."));
        }

        foreach (var c in ordered.Where(c => c.ReferencingServices is { Count: > 0 }))
            foreach (var service in c.ReferencingServices!.Order(StringComparer.Ordinal))
            {
                entity(ServiceFact(service, "contract reference"));
                relation(new(ServiceKey(service), CanonicalKeys.EntityKey(CanonicalEntityType.ContractVersion, ContractVersionIdentity(c)),
                    CanonicalRelationType.References, RelationResolution.Explicit, CorrelationConfidence.Explicit, ContractServiceRule,
                    [new("reference", c.Path, service, c.SourceSnapshotId)], "The service's source references this contract file."));
            }
    }

    static IReadOnlyDictionary<string, string> Attrs(params (string Key, string? Value)[] pairs) =>
        pairs.Where(p => !string.IsNullOrWhiteSpace(p.Value)).ToDictionary(p => p.Key, p => p.Value!, StringComparer.Ordinal);

    // ---- Case K: aliases are scoped, never identity ----

    static void Systems(IReadOnlyList<SystemIdentityInput> systems, Action<CanonicalEntityFact> entity, Action<CanonicalRelationFact> relation, List<CanonicalAliasFact> aliases,
        List<string> limitations)
    {
        var ordered = systems.OrderBy(s => s.Name, StringComparer.Ordinal).ThenBy(s => s.Type).ToList();
        var declared = new List<CanonicalAliasFact>();
        foreach (var s in ordered)
        {
            var fact = new CanonicalEntityFact(s.Type, CanonicalKeys.Normalize(s.Name), s.Name, s.Name, "declared system name", [new("declared", s.Source, s.Name)]);
            entity(fact);
            foreach (var alias in s.DeclaredAliases.Where(a => !string.IsNullOrWhiteSpace(a)).Order(StringComparer.Ordinal))
                declared.Add(new(fact.Key, alias, AliasState.Explicit, s.Source, DeclaredAliasRule));
        }

        // Two declarations giving the same alias to different entities of the same kind disagree: neither applies, both are proposed, and the pair is surfaced
        // for review. Picking the first or the last declaration would silently remap every consumer.
        foreach (var group in declared.GroupBy(a => (Type: a.EntityKey[..a.EntityKey.IndexOf('|')], Alias: CanonicalKeys.Normalize(a.Alias)))
                     .OrderBy(g => g.Key.Type, StringComparer.Ordinal).ThenBy(g => g.Key.Alias, StringComparer.Ordinal))
        {
            var owners = group.Select(a => a.EntityKey).Distinct().Order(StringComparer.Ordinal).ToList();
            if (owners.Count == 1) { aliases.AddRange(group); continue; }
            aliases.AddRange(group.Select(a => a with { State = AliasState.Proposed, RuleId = AliasConflictRule, Source = $"{a.Source} (conflicting declaration)" }));
            for (var i = 1; i < owners.Count; i++)
                relation(new(owners[i], owners[0], CanonicalRelationType.CandidateSameAs, RelationResolution.Ambiguous, CorrelationConfidence.Low, AliasConflictRule,
                    [.. group.Select(a => new CorrelationEvidence("declared-alias", a.Source, a.Alias))],
                    $"\"{group.First().Alias}\" is declared as an alias of more than one entity. Neither declaration is applied until a reviewer decides which one it names (or that they are the same)."));
            limitations.Add($"Alias \"{group.First().Alias}\" is declared for {owners.Count} different entities; it is not applied until reviewed.");
        }

        // An acronym that matches another system's initials is only a proposal: the same letters can name different systems in different projects.
        foreach (var s in ordered)
            foreach (var other in ordered)
            {
                if (ReferenceEquals(s, other) || s.Type != other.Type) continue;
                if (!IsAcronym(s.Name) || !AcronymMatches(s.Name, other.Name)) continue;
                if (other.DeclaredAliases.Any(a => string.Equals(a, s.Name, StringComparison.OrdinalIgnoreCase))) continue; // already explicit
                if (declared.Any(a => CanonicalKeys.Normalize(a.Alias) == CanonicalKeys.Normalize(s.Name))) continue; // declared (or in conflict) elsewhere
                aliases.Add(new(CanonicalKeys.EntityKey(other.Type, CanonicalKeys.Normalize(other.Name)), s.Name, AliasState.Proposed, $"{s.Source} ↔ {other.Source}", AcronymAliasRule));
            }
    }

    static bool IsAcronym(string name) => name.Length is >= 2 and <= 6 && name.All(char.IsUpper);

    /// <summary>"MU" matches "Meldingsutveksleren" (Norwegian compound: meldings·utveksleren) and "Meldings Utveksler"; never a single word's first letter alone.</summary>
    public static bool AcronymMatches(string acronym, string name)
    {
        var words = WordRe().Matches(name).Select(m => m.Value).ToList();
        if (words.Count >= 2)
            return words.Count == acronym.Length && words.Select(w => char.ToUpperInvariant(w[0])).SequenceEqual(acronym);
        // One compound word: each acronym letter must start a known compound joint (linking -s- / -e-), in order.
        var word = name.ToLowerInvariant();
        if (acronym.Length != 2 || word.Length < 6 || char.ToLowerInvariant(acronym[0]) != word[0]) return false;
        var second = char.ToLowerInvariant(acronym[1]);
        for (var i = 3; i < word.Length - 3; i++)
            if (word[i] == second && (word[i - 1] == 's' || word[i - 1] == 'e')) return true;
        return false;
    }

    // ---- Case N inputs: messaging channels (declared producer/consumer endpoints only) ----

    static void Channels(IReadOnlyList<MessagingChannelInput> channels, Action<CanonicalEntityFact> entity, Action<CanonicalRelationFact> relation)
    {
        foreach (var c in channels.OrderBy(c => c.Kind, StringComparer.Ordinal).ThenBy(c => c.EntityName, StringComparer.Ordinal))
        {
            var channel = new CanonicalEntityFact(CanonicalEntityType.MessagingChannel, $"{c.Kind.ToLowerInvariant()}:{CanonicalKeys.Normalize(c.EntityName)}", c.EntityName, c.EntityName,
                "channel kind + entity name", [new("channel", c.Evidence, c.Kind, c.SourceSnapshotId)], Attributes: Attrs(("kind", c.Kind)));
            entity(channel);
            foreach (var (services, type) in new[] { (c.Producers, CanonicalRelationType.Produces), (c.Consumers, CanonicalRelationType.Consumes) })
                foreach (var service in services.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal))
                {
                    entity(ServiceFact(service, "messaging endpoint"));
                    relation(new(ServiceKey(service), channel.Key, type, RelationResolution.Explicit, CorrelationConfidence.Explicit, ChannelRule,
                        [new("channel", c.Evidence, service, c.SourceSnapshotId)], $"The source declares {service} as a {(type == CanonicalRelationType.Produces ? "producer" : "consumer")} of {c.EntityName}."));
                }
        }
    }

    // ---- Case L: source symbols ----

    static CanonicalEntityFact ServiceFact(string service, string method) =>
        new(CanonicalEntityType.Service, CanonicalKeys.Normalize(service), service, service, method, [new("service", service)]);

    static string ServiceKey(string service) => CanonicalKeys.EntityKey(CanonicalEntityType.Service, CanonicalKeys.Normalize(service));

    /// <summary>Operation identity: service + upper-case method + route with parameter names folded to {} (so {id} and {orderId} are one operation).</summary>
    public static string OperationIdentity(string service, string method, string route) =>
        $"{CanonicalKeys.Normalize(service)}|{method.ToUpperInvariant()} {NormalizeRoute(route)}";

    public static string NormalizeRoute(string route)
    {
        var r = RouteParamRe().Replace(route.Trim(), "{}").TrimEnd('/');
        r = r.StartsWith('/') ? r : "/" + r;
        return r.ToLowerInvariant();
    }

    [GeneratedRegex(@"\{[^}]*\}|:[A-Za-z_][A-Za-z0-9_]*")]
    private static partial Regex RouteParamRe();

    static void Operations(IReadOnlyList<ApiOperationInput> operations, Action<CanonicalEntityFact> entity, Action<CanonicalRelationFact> relation)
    {
        foreach (var op in operations.OrderBy(o => OperationIdentity(o.Service, o.Method, o.Route), StringComparer.Ordinal))
        {
            var fact = OperationFact(op.Service, op.Method, op.Route, op.Source, op.SourceSnapshotId);
            entity(fact);
            entity(ServiceFact(op.Service, "operation owner"));
            relation(new(ServiceKey(op.Service), fact.Key, CanonicalRelationType.Exposes, RelationResolution.Explicit, CorrelationConfidence.Explicit, OperationServiceRule,
                [new(op.Source, $"{op.Method.ToUpperInvariant()} {op.Route}", op.Service, op.SourceSnapshotId)], "The evidence lists this operation under the service."));
        }
    }

    static CanonicalEntityFact OperationFact(string service, string method, string route, string source, string? snapshot) =>
        new(CanonicalEntityType.ApiOperation, OperationIdentity(service, method, route), $"{method.ToUpperInvariant()} {route}", $"{method.ToUpperInvariant()} {route}",
            "service + method + normalized route", [new(source, $"{method.ToUpperInvariant()} {route}", service, snapshot)]);

    static void Symbols(IReadOnlyList<SourceSymbolInput> symbols, IReadOnlyDictionary<string, CanonicalEntityFact> known, Action<CanonicalEntityFact> entity, Action<CanonicalRelationFact> relation)
    {
        var operationsByRoute = known.Values.Where(e => e.Type == CanonicalEntityType.ApiOperation).ToList();
        foreach (var s in symbols.OrderBy(s => s.Package, StringComparer.Ordinal).ThenBy(s => s.Path, StringComparer.Ordinal).ThenBy(s => s.Symbol, StringComparer.Ordinal))
        {
            var qualified = string.IsNullOrWhiteSpace(s.Namespace) ? s.Symbol : $"{s.Namespace}.{s.Symbol}";
            var fact = new CanonicalEntityFact(CanonicalEntityType.SourceSymbol, $"{CanonicalKeys.Normalize(s.Package)}|{qualified}", qualified, qualified,
                "package + fully qualified symbol (file moves keep identity)", [new("symbol", s.Path, s.Kind, s.SourceSnapshotId)], Attributes: Attrs(("kind", s.Kind), ("path", s.Path)));
            entity(fact);
            var evidence = new CorrelationEvidence("symbol", $"{s.Path}#{s.Symbol}", s.Kind, s.SourceSnapshotId);

            if (!string.IsNullOrWhiteSpace(s.Service))
            {
                entity(ServiceFact(s.Service, "source ownership"));
                relation(new(fact.Key, ServiceKey(s.Service), CanonicalRelationType.BelongsTo, RelationResolution.Derived, CorrelationConfidence.High, SymbolServiceRule,
                    [evidence], "The symbol is declared in this service's project."));
            }

            if (s.HttpMethod is not null && s.Route is not null && !string.IsNullOrWhiteSpace(s.Service))
            {
                var op = OperationFact(s.Service, s.HttpMethod, s.Route, "route attribute", s.SourceSnapshotId);
                entity(op);
                relation(new(ServiceKey(s.Service), op.Key, CanonicalRelationType.Exposes, RelationResolution.Explicit, CorrelationConfidence.Explicit, OperationServiceRule, [evidence], "Declared by a route attribute in the service's source."));
                relation(new(fact.Key, op.Key, CanonicalRelationType.Implements, RelationResolution.Explicit, CorrelationConfidence.Explicit, SymbolRouteRule, [evidence],
                    $"The symbol declares {s.HttpMethod.ToUpperInvariant()} {s.Route}."));
            }
            else if (s.Kind is "Controller" or "Handler" or "Endpoint")
            {
                // No route attribute: a symbol named like an operation's last route segment is only a candidate.
                var stem = SymbolStem(s.Symbol);
                foreach (var op in operationsByRoute.Where(o => stem.Length >= 4 && LastSegment(o.IdentityKey).Equals(stem, StringComparison.Ordinal)
                                                                && (s.Service is null || o.IdentityKey.StartsWith(CanonicalKeys.Normalize(s.Service) + "|", StringComparison.Ordinal))))
                    relation(new(fact.Key, op.Key, CanonicalRelationType.Implements, RelationResolution.Candidate, CorrelationConfidence.Low, SymbolNameRule, [evidence],
                        $"\"{s.Symbol}\" is named like {op.DisplayName}. A matching name is not proof: confirm or reject."));
            }

            foreach (var req in s.RequirementIds.Select(NormalizeRequirement).Where(r => r is not null).Distinct().Order(StringComparer.Ordinal))
            {
                entity(RequirementFact(req!, "symbol reference", s.SourceSnapshotId));
                relation(new(fact.Key, CanonicalKeys.EntityKey(CanonicalEntityType.Requirement, req!), CanonicalRelationType.References, RelationResolution.Explicit,
                    CorrelationConfidence.Explicit, SymbolRequirementRule, [evidence], $"The source names {req} explicitly. A reference is not proof the requirement is implemented."));
            }

            foreach (var hash in s.ContractReferences.Order(StringComparer.Ordinal))
                foreach (var target in known.Values.Where(e => e.Type == CanonicalEntityType.ContractVersion && e.SourceIdentity.Equals(hash, StringComparison.OrdinalIgnoreCase))
                             .OrderBy(e => e.Key, StringComparer.Ordinal).ToList())
                    relation(new(fact.Key, target.Key, CanonicalRelationType.References, RelationResolution.Explicit, CorrelationConfidence.Explicit, SymbolContractRule, [evidence],
                        "The symbol loads or declares this contract file."));
        }

        // A member symbol (an endpoint method "Ns.Type.Method") belongs to its declared type symbol when both are in the same package.
        var all = known.Values.Where(e => e.Type == CanonicalEntityType.SourceSymbol).ToDictionary(e => e.IdentityKey, StringComparer.Ordinal);
        foreach (var member in all.Values.Where(e => e.Attribute("kind") is "Controller" or "Endpoint" or "GraphQL").OrderBy(e => e.IdentityKey, StringComparer.Ordinal).ToList())
        {
            var dot = member.IdentityKey.LastIndexOf('.');
            if (dot <= member.IdentityKey.IndexOf('|')) continue;
            if (all.TryGetValue(member.IdentityKey[..dot], out var owner) && owner.Attribute("kind") is "Class" or "Record" or "Struct")
                relation(new(owner.Key, member.Key, CanonicalRelationType.Contains, RelationResolution.Derived, CorrelationConfidence.High, SymbolMemberRule,
                    [new("symbol", member.Attribute("path") ?? member.SourceIdentity)], "The member is declared in this type."));
        }
    }

    /// <summary>The simple type name of a symbol identity ("pkg|Ns.Outer.Type" → "Type").</summary>
    static string SimpleName(CanonicalEntityFact symbol) => symbol.IdentityKey[(symbol.IdentityKey.LastIndexOf('.') is var d and > 0 && d > symbol.IdentityKey.IndexOf('|') ? d + 1 : symbol.IdentityKey.IndexOf('|') + 1)..];

    // ---- Shared libraries (M2LB.Common): Package CONTAINS Library, Service USES Library ----

    static void Libraries(IReadOnlyList<LibraryInput> libraries, IReadOnlyList<LibraryUseInput> uses, IReadOnlyDictionary<string, CanonicalEntityFact> known,
        Action<CanonicalEntityFact> entity, Action<CanonicalRelationFact> relation)
    {
        foreach (var l in libraries.OrderBy(l => l.Name, StringComparer.Ordinal).ThenBy(l => l.Package, StringComparer.Ordinal))
        {
            var segments = l.Name.Split('.', StringSplitOptions.RemoveEmptyEntries);
            var fact = new CanonicalEntityFact(CanonicalEntityType.Library, CanonicalKeys.Normalize(l.Name), l.Name, l.Name, "published package / shared project name",
                [new("library", l.Source, l.Package, l.SourceSnapshotId)], Attributes: Attrs(("capability", segments.Length > 1 ? segments[^1] : null), ("package", l.Package)));
            entity(fact);
            var package = CanonicalKeys.EntityKey(CanonicalEntityType.Package, l.Package);
            if (known.ContainsKey(package))
                relation(new(package, fact.Key, CanonicalRelationType.Contains, RelationResolution.Explicit, CorrelationConfidence.Explicit, LibraryRule,
                    [new("library", l.Source, l.Package, l.SourceSnapshotId)], "The package's source publishes this library."));
        }
        // Only libraries a package in scope provides: a third-party package reference is a dependency, not a shared capability of this project.
        var provided = libraries.Select(l => CanonicalKeys.Normalize(l.Name)).ToHashSet(StringComparer.Ordinal);
        foreach (var u in uses.Where(u => provided.Contains(CanonicalKeys.Normalize(u.Library)))
                     .OrderBy(u => u.Service, StringComparer.Ordinal).ThenBy(u => u.Library, StringComparer.Ordinal))
        {
            entity(ServiceFact(u.Service, "library consumer"));
            relation(new(ServiceKey(u.Service), CanonicalKeys.EntityKey(CanonicalEntityType.Library, CanonicalKeys.Normalize(u.Library)), CanonicalRelationType.Uses,
                RelationResolution.Explicit, CorrelationConfidence.Explicit, LibraryUseRule, [new("reference", u.Evidence, u.Library, u.SourceSnapshotId)],
                $"The service's project references {u.Library}; the shared implementation is not duplicated per service."));
        }
    }

    // ---- Case L/M: a task that names a source symbol ----

    /// <summary>Identifiers a task can name: `backticked` names, File.cs names and PascalCase compounds (two or more humps).</summary>
    public static IReadOnlyList<string> NamedSymbols(string text)
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (Match m in BacktickRe().Matches(text))
        {
            var raw = m.Groups[1].Value;
            if (raw.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) { names.Add(System.IO.Path.GetFileNameWithoutExtension(raw)); continue; }
            var head = raw.Split('(')[0];
            var parts = head.Split('.', StringSplitOptions.RemoveEmptyEntries);
            // `Type.Method()` names the type; `Ns.Type` names the type; a lone `Type` names itself.
            if (parts.Length > 0) names.Add(raw.Contains('(') && parts.Length > 1 ? parts[^2] : parts[^1]);
        }
        foreach (Match m in FileNameRe().Matches(text)) names.Add(m.Groups[1].Value);
        foreach (Match m in PascalRe().Matches(text)) names.Add(m.Value);
        return [.. names.Where(n => n.Length >= 4)];
    }

    [GeneratedRegex(@"`([A-Za-z_][A-Za-z0-9_.()]*)`")]
    private static partial Regex BacktickRe();

    [GeneratedRegex(@"\b([A-Z][A-Za-z0-9_]+)\.cs\b")]
    private static partial Regex FileNameRe();

    [GeneratedRegex(@"\b[A-Z][a-z0-9]+(?:[A-Z][a-z0-9]+)+\b")]
    private static partial Regex PascalRe();

    /// <summary>A symbol belongs to a Delivery module when one dot-segment of its owning service is the module name (exact, case-insensitive).</summary>
    public static bool InModule(string? service, string module) =>
        service is not null && (string.Equals(service, module, StringComparison.OrdinalIgnoreCase)
            || service.Split('.', StringSplitOptions.RemoveEmptyEntries).Any(seg => string.Equals(seg, module, StringComparison.OrdinalIgnoreCase)));

    static void TaskReferences(IReadOnlyList<TaskReferenceInput> tasks, IReadOnlyList<SourceSymbolInput> symbolInputs, IReadOnlyDictionary<string, CanonicalEntityFact> known,
        Action<CanonicalEntityFact> entity, Action<CanonicalRelationFact> relation, List<string> limitations)
    {
        if (tasks.Count == 0) return;
        // Type-level symbols by simple name, with the service that owns them (from the symbol input).
        var owners = symbolInputs.GroupBy(s => $"{CanonicalKeys.Normalize(s.Package)}|{(string.IsNullOrWhiteSpace(s.Namespace) ? s.Symbol : $"{s.Namespace}.{s.Symbol}")}")
            .ToDictionary(g => g.Key, g => g.First().Service, StringComparer.Ordinal);
        var byName = known.Values.Where(e => e.Type == CanonicalEntityType.SourceSymbol && e.Attribute("kind") is "Class" or "Record" or "Struct" or "Interface" or "Enum")
            .GroupBy(SimpleName, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.OrderBy(e => e.Key, StringComparer.Ordinal).ToList(), StringComparer.Ordinal);
        var ambiguousElsewhere = 0;
        foreach (var t in tasks.OrderBy(t => t.Module, StringComparer.Ordinal).ThenBy(t => t.FeatureKey, StringComparer.Ordinal).ThenBy(t => t.TaskKey, StringComparer.Ordinal))
        {
            var names = NamedSymbols(t.Text).Where(byName.ContainsKey).ToList();
            if (names.Count == 0) continue;
            var feature = CanonicalKeys.EntityKey(CanonicalEntityType.Feature, $"{CanonicalKeys.Normalize(t.Module)}/{CanonicalKeys.Normalize(t.FeatureKey)}");
            var task = new CanonicalEntityFact(CanonicalEntityType.Task, $"{CanonicalKeys.Normalize(t.Module)}/{CanonicalKeys.Normalize(t.FeatureKey)}/{CanonicalKeys.Normalize(t.TaskKey)}",
                $"{t.TaskKey} {Shorten(t.Text, 120)}", $"{t.Module}/{t.FeatureKey}/{t.TaskKey}", "module + feature + task key", [new("task", t.TaskKey, t.FeatureKey, t.SourceSnapshotId)]);
            entity(task);
            if (known.ContainsKey(feature))
                relation(new(feature, task.Key, CanonicalRelationType.Contains, RelationResolution.Explicit, CorrelationConfidence.Explicit, TaskOwnerRule,
                    [new("task", t.TaskKey, t.FeatureKey, t.SourceSnapshotId)], "The task is listed in this feature's tasks file."));
            foreach (var name in names)
            {
                var candidates = byName[name];
                var inModule = candidates.Where(c => InModule(owners.GetValueOrDefault(c.IdentityKey), t.Module)).ToList();
                var evidence = new CorrelationEvidence("task-text", $"{t.Module}/{t.FeatureKey}/{t.TaskKey}", name, t.SourceSnapshotId);
                if (inModule.Count == 1)
                    relation(new(task.Key, inModule[0].Key, CanonicalRelationType.References, RelationResolution.Explicit, CorrelationConfidence.Explicit, TaskSymbolRule, [evidence],
                        $"The task names \"{name}\" and exactly one type with that name exists in module {t.Module} ({inModule[0].DisplayName}). A reference is not proof the task is implemented."));
                else if (inModule.Count > 1)
                    foreach (var c in inModule)
                        relation(new(task.Key, c.Key, CanonicalRelationType.References, RelationResolution.Ambiguous, CorrelationConfidence.Low, TaskSymbolRule, [evidence],
                            $"The task names \"{name}\", and {inModule.Count} types in module {t.Module} have that name. Choose the one it means."));
                else if (candidates.Count == 1)
                    relation(new(task.Key, candidates[0].Key, CanonicalRelationType.References, RelationResolution.Candidate, CorrelationConfidence.Low, TaskSymbolRule, [evidence],
                        $"The task names \"{name}\"; the only type with that name is outside module {t.Module} ({candidates[0].DisplayName}). Confirm or reject."));
                else ambiguousElsewhere++;
            }
        }
        if (ambiguousElsewhere > 0)
            limitations.Add($"{ambiguousElsewhere} symbol name(s) named in tasks match several types outside the task's module; no link was proposed.");
    }

    static string Shorten(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    static string SymbolStem(string symbol)
    {
        var s = symbol;
        foreach (var suffix in new[] { "Controller", "Handler", "Endpoint", "Endpoints", "Async" })
            if (s.EndsWith(suffix, StringComparison.Ordinal) && s.Length > suffix.Length) s = s[..^suffix.Length];
        return s.ToLowerInvariant();
    }

    static string LastSegment(string operationIdentity)
    {
        var route = operationIdentity[(operationIdentity.IndexOf(' ') + 1)..];
        return route.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault(p => p != "{}") ?? "";
    }

    // ---- Case M: Test → VERIFIES ----

    /// <summary>FR-1 / FR-001 / fr-01 → FR-001; SC likewise; US1 / US-1 → US1. Anything else is returned unchanged upper-cased (other explicit ids).</summary>
    public static string? NormalizeRequirement(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        var m = RequirementRe().Match(id.Trim());
        if (!m.Success) return id.Trim().ToUpperInvariant();
        var prefix = m.Groups[1].Value.ToUpperInvariant();
        var number = int.Parse(m.Groups[2].Value);
        return prefix == "US" ? $"US{number}" : $"{prefix}-{number:000}";
    }

    [GeneratedRegex(@"^(FR|SC|NFR|AC|US)[-_ ]?0*(\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex RequirementRe();

    static CanonicalEntityFact RequirementFact(string id, string method, string? snapshot) =>
        new(id.StartsWith("US", StringComparison.Ordinal) ? CanonicalEntityType.UserStory : CanonicalEntityType.Requirement, id, id, id, method, [new("requirement", id, null, snapshot)]);

    static void Tests(IReadOnlyList<TestInput> tests, IReadOnlyDictionary<string, CanonicalEntityFact> known, Action<CanonicalEntityFact> entity, Action<CanonicalRelationFact> relation, List<string> limitations)
    {
        var scopedById = known.Values.Where(e => e.Type is CanonicalEntityType.Requirement or CanonicalEntityType.UserStory && e.Attribute("module") is not null && e.Attribute("id") is not null)
            .GroupBy(e => e.Attribute("id")!, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.OrderBy(e => e.Key, StringComparer.Ordinal).ToList(), StringComparer.Ordinal);
        var scopeDropped = 0;
        var unresolved = 0;
        var unresolvedIds = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var t in tests.OrderBy(t => t.Package, StringComparer.Ordinal).ThenBy(t => t.Path, StringComparer.Ordinal).ThenBy(t => t.Name, StringComparer.Ordinal))
        {
            var fact = new CanonicalEntityFact(CanonicalEntityType.Test, t.StableIdentity ?? $"{CanonicalKeys.Normalize(t.Package)}|{t.Name}", t.Name, t.StableIdentity ?? t.Name,
                t.StableIdentity is null ? "package + test name" : "repository + project + fully qualified test name",
                [new("test", t.Path, t.Suite, t.SourceSnapshotId)], Attributes: Attrs(("path", t.Path), ("suite", t.Suite)));
            entity(fact);
            var evidence = new CorrelationEvidence("test", $"{t.Path}#{t.Name}", null, t.SourceSnapshotId);
            if (!string.IsNullOrWhiteSpace(t.Suite))
            {
                var suite = new CanonicalEntityFact(CanonicalEntityType.TestSuite, $"{CanonicalKeys.Normalize(t.Package)}|{t.Suite}", t.Suite, t.Suite, "package + suite", [evidence]);
                entity(suite);
                relation(new(suite.Key, fact.Key, CanonicalRelationType.Contains, RelationResolution.Explicit, CorrelationConfidence.Explicit, TestSuiteRule, [evidence], "The test is declared in this suite."));
            }

            foreach (var target in t.ExplicitTargets.Select(x => VerifyTarget(x, known)).Distinct().OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                if (target.Key is null) { unresolved++; unresolvedIds.UnionWith(t.ExplicitTargets.Where(x => VerifyTarget(x, known).Key is null)); continue; }
                // A requirement id the evidence also defines per feature pack is linked to the pack the test belongs to — never to all packs that use the id.
                var scoped = ScopedTargets(target.Id!, t, scopedById);
                if (scoped.Matches.Count > 0)
                {
                    if (scoped.Chosen is { } one)
                        relation(new(fact.Key, one.Key, CanonicalRelationType.Verifies, RelationResolution.Explicit, CorrelationConfidence.Explicit, TestExplicitRule, [evidence],
                            $"The test names {target.Id} explicitly and {scoped.Why}. This says what it is meant to verify, not that it ran or passed."));
                    else
                        foreach (var m in scoped.Matches)
                            relation(new(fact.Key, m.Key, CanonicalRelationType.Verifies, RelationResolution.Ambiguous, CorrelationConfidence.Low, TestScopeAmbiguityRule, [evidence],
                                $"The test names {target.Id}, which {scoped.Matches.Count} feature packs define, and nothing in the test says which one. Choose the pack it verifies."));
                    continue;
                }
                if (!known.ContainsKey(target.Key)) entity(RequirementFact(target.Id!, "explicit test reference", t.SourceSnapshotId));
                relation(new(fact.Key, target.Key, CanonicalRelationType.Verifies, RelationResolution.Explicit, CorrelationConfidence.Explicit, TestExplicitRule, [evidence],
                    $"The test names {target.Id} explicitly. This says what it is meant to verify, not that it ran or passed."));
            }
            foreach (var target in t.CandidateTargets.Select(x => VerifyTarget(x, known)).Distinct().OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                if (target.Key is null) { unresolved++; unresolvedIds.UnionWith(t.CandidateTargets.Where(x => VerifyTarget(x, known).Key is null)); continue; }
                if (t.ExplicitTargets.Select(NormalizeRequirement).Contains(target.Id)) continue; // explicit already
                var scopedCandidate = ScopedTargets(target.Id!, t, scopedById);
                if (scopedCandidate.Matches.Count > 0)
                {
                    // A naming-only guess is proposed only when the test's own module narrows it to one pack; otherwise it is not proposed at all.
                    if (scopedCandidate.Chosen is { } one)
                        relation(new(fact.Key, one.Key, CanonicalRelationType.Verifies, RelationResolution.Candidate, CorrelationConfidence.Low, TestCandidateRule, [evidence],
                            $"{target.Id} is only mentioned near the test (a class comment, a prose comment or a string such as an assertion message), not declared as what it verifies ({scopedCandidate.Why}). A candidate is never counted as traceability or coverage until confirmed."));
                    else scopeDropped++;
                    continue;
                }
                if (!known.ContainsKey(target.Key)) entity(RequirementFact(target.Id!, "candidate test reference", t.SourceSnapshotId));
                relation(new(fact.Key, target.Key, CanonicalRelationType.Verifies, RelationResolution.Candidate, CorrelationConfidence.Low, TestCandidateRule, [evidence],
                    $"{target.Id} is only mentioned near the test (a class comment, a prose comment or a string such as an assertion message), not declared as what it verifies. A candidate is never counted as traceability or coverage until confirmed."));
            }
        }
        if (unresolved > 0)
            limitations.Add($"{unresolved} test link(s) could not be resolved to a requirement, story, capability or operation (e.g. {string.Join(", ", unresolvedIds.Take(6))}).");
        if (scopeDropped > 0)
            limitations.Add($"{scopeDropped} naming-only test link(s) name an id several feature packs define and the test's module does not narrow it; no candidate was proposed.");
    }

    /// <summary>
    /// The feature-pack-scoped requirements/stories with this id, and the one the test belongs to: the only pack that defines it, or the only one in the
    /// module the test's project/path names (a dot, slash or pipe segment equal to the module). Null <c>Chosen</c> means the test does not say which.
    /// </summary>
    static (IReadOnlyList<CanonicalEntityFact> Matches, CanonicalEntityFact? Chosen, string Why) ScopedTargets(string id, TestInput t, IReadOnlyDictionary<string, List<CanonicalEntityFact>> scopedById)
    {
        if (!scopedById.TryGetValue(id, out var matches)) return ([], null, "");
        if (matches.Count == 1) return (matches, matches[0], $"only {matches[0].Attribute("module")}/{matches[0].Attribute("feature")} defines it");
        var segments = $"{t.Path}|{t.Name}|{t.StableIdentity}|{t.Package}".Split(['/', '\\', '.', '|'], StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var inModule = matches.Where(m => segments.Contains(m.Attribute("module")!)).ToList();
        return inModule.Count == 1 ? (matches, inModule[0], $"the test belongs to module {inModule[0].Attribute("module")}, where only {inModule[0].Attribute("feature")} defines it") : (matches, null, "");
    }

    /// <summary>A test target is an operation key ("GET /x"), a known entity key, or a requirement/story id.</summary>
    static (string? Key, string? Id) VerifyTarget(string raw, IReadOnlyDictionary<string, CanonicalEntityFact> known)
    {
        if (known.ContainsKey(raw)) return (raw, known[raw].IdentityKey);
        var id = NormalizeRequirement(raw);
        if (id is null) return (null, null);
        if (!RequirementRe().IsMatch(id.Replace("-", ""))) return (null, null);
        var type = id.StartsWith("US", StringComparison.Ordinal) ? CanonicalEntityType.UserStory : CanonicalEntityType.Requirement;
        return (CanonicalKeys.EntityKey(type, id), id);
    }
}

/// <summary>Case N: a chain walk over stored relations. Partial chains are returned as they are; a missing link is reported, never filled in.</summary>
public static class CorrelationChain
{
    public sealed record Hop(string From, string To, CanonicalRelationType Type, RelationResolution Resolution, RelationReviewState Review, string RuleId);

    public sealed record Step(CanonicalEntityType Type, IReadOnlyList<Hop> Links, bool Missing);

    /// <summary>The default cross-domain path: Feature → Capability → Service → API operation → Contract version → Test.</summary>
    public static readonly CanonicalEntityType[] DefaultPath =
        [CanonicalEntityType.Feature, CanonicalEntityType.Capability, CanonicalEntityType.Service, CanonicalEntityType.ApiOperation, CanonicalEntityType.ContractVersion, CanonicalEntityType.Test];

    /// <summary>
    /// Walks from <paramref name="start"/> along <paramref name="path"/>: each step follows any relation (either direction) to an entity of the next type.
    /// A step with no link is marked Missing and the walk continues from the previous frontier only if a later step can still be reached from it.
    /// </summary>
    public static IReadOnlyList<Step> Walk(string start, IReadOnlyList<CanonicalEntityType> path, IReadOnlyList<Hop> relations, Func<string, CanonicalEntityType?> typeOf)
    {
        var steps = new List<Step>();
        var frontier = new HashSet<string>(StringComparer.Ordinal) { start };
        foreach (var next in path.Skip(1))
        {
            var links = relations
                .Where(r => r.Resolution != RelationResolution.Rejected && r.Review != RelationReviewState.Rejected)
                .Where(r => (frontier.Contains(r.From) && typeOf(r.To) == next) || (frontier.Contains(r.To) && typeOf(r.From) == next))
                .OrderBy(r => r.From, StringComparer.Ordinal).ThenBy(r => r.To, StringComparer.Ordinal).ToList();
            steps.Add(new(next, links, links.Count == 0));
            if (links.Count > 0)
                frontier = [.. links.Select(l => frontier.Contains(l.From) ? l.To : l.From)];
        }
        return steps;
    }
}
