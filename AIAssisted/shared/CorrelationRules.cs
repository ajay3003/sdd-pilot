using System.Text.RegularExpressions;

namespace BirkNext.Interpretation;

// Correlation rules for cases G–N. Pure and deterministic (inputs are sorted before use, so the same facts always give the same entities, relations and
// order). Rule ids name the rule that produced each relation; the rule set is versioned so a stored interpretation snapshot can say which rules made it.
// Rules never merge two entities: the strongest heuristic outcome is a Candidate relation that a human confirms or rejects.

/// <summary>A feature or story as written in one module's specification (case G/H input).</summary>
public sealed record WorkItemIntentInput(string Module, string FeatureKey, string Title, IReadOnlyList<string> StoryTitles, string? Path, string? SourceSnapshotId = null);

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
    IReadOnlyList<MessagingChannelInput>? Channels = null)
{
    public static CorrelationInput Empty(string scope) => new(scope, [], [], [], [], [], []);
}

public static partial class CorrelationRules
{
    /// <summary>Bumped whenever a rule's outcome can change for the same input; stored on every interpretation snapshot.</summary>
    public const string RuleSetVersion = "correlation/1";

    public const string IntentRule = "CR-G-INTENT", CapabilityRule = "CR-H-CAPABILITY", ContractHashRule = "CR-I-HASH", ContractVersionRule = "CR-J-VERSION",
        ContractVersionConflictRule = "CR-J-CONFLICT", DeclaredAliasRule = "CR-K-DECLARED", AcronymAliasRule = "CR-K-ACRONYM", SymbolRouteRule = "CR-L-ROUTE",
        SymbolRequirementRule = "CR-L-REQUIREMENT", SymbolContractRule = "CR-L-CONTRACT", SymbolServiceRule = "CR-L-SERVICE", SymbolNameRule = "CR-L-NAME",
        TestExplicitRule = "CR-M-EXPLICIT", TestCandidateRule = "CR-M-CANDIDATE", TestSuiteRule = "CR-M-SUITE", OperationServiceRule = "CR-N-EXPOSES", ContractServiceRule = "CR-N-CONTRACT",
        ChannelRule = "CR-N-CHANNEL";

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
        ContractOccurrences(input.Contracts, Entity, Relation);
        Systems(input.Systems, Entity, aliases);
        Operations(input.Operations, Entity, Relation);
        Channels(input.Channels ?? [], Entity, Relation);
        Symbols(input.Symbols, entities, Entity, Relation);
        Tests(input.Tests, entities, Entity, Relation, limitations);

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

    static CanonicalEntityFact FeatureFact(WorkItemIntentInput f) =>
        new(CanonicalEntityType.Feature, $"{CanonicalKeys.Normalize(f.Module)}/{CanonicalKeys.Normalize(f.FeatureKey)}", f.Title, $"{f.Module}/{f.FeatureKey}",
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

    // ---- Case I + J: contract identity ≠ occurrence; versions never collapse ----

    /// <summary>Contract identity: declared namespace (XSD/message) or declared title (OpenAPI/GraphQL/JSON), never the file name or path.</summary>
    public static string? ContractIdentity(ContractOccurrenceInput c)
    {
        if (!string.IsNullOrWhiteSpace(c.DeclaredIdentity)) return $"{c.Kind.ToLowerInvariant()}:{CanonicalKeys.Normalize(c.DeclaredIdentity)}";
        var declared = !string.IsNullOrWhiteSpace(c.Namespace) ? c.Namespace : c.Title;
        return string.IsNullOrWhiteSpace(declared) ? null : $"{c.Kind.ToLowerInvariant()}:{CanonicalKeys.Normalize(declared)}";
    }

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

            // Identical bytes are the same contract content wherever they occur (case I); the occurrence records stay separate.
            var version = new CanonicalEntityFact(CanonicalEntityType.ContractVersion, $"{c.Kind.ToLowerInvariant()}:sha256:{c.ContentHash.ToLowerInvariant()}",
                $"{c.Title ?? c.Namespace ?? System.IO.Path.GetFileName(c.Path)}{(c.DeclaredVersion is null ? "" : $" v{c.DeclaredVersion}")}", c.ContentHash, "content hash",
                [new("contentHash", c.ContentHash, c.Path, c.SourceSnapshotId)], c.DeclaredVersion,
                Attrs(("namespace", c.Namespace), ("declaredVersion", c.DeclaredVersion), ("kind", c.Kind)));
            entity(version);
            relation(new(occurrence.Key, version.Key, CanonicalRelationType.BelongsTo, RelationResolution.Derived, CorrelationConfidence.High, ContractHashRule,
                [new("contentHash", c.ContentHash, c.Path, c.SourceSnapshotId)], "The file content hashes to this contract version."));

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
            var hashes = clash.Select(c => (Kind: c.Kind.ToLowerInvariant(), Hash: c.ContentHash.ToLowerInvariant())).Distinct().OrderBy(h => h.Hash, StringComparer.Ordinal).ToList();
            for (var i = 1; i < hashes.Count; i++)
                relation(new(CanonicalKeys.EntityKey(CanonicalEntityType.ContractVersion, $"{hashes[i].Kind}:sha256:{hashes[i].Hash}"),
                    CanonicalKeys.EntityKey(CanonicalEntityType.ContractVersion, $"{hashes[0].Kind}:sha256:{hashes[0].Hash}"),
                    CanonicalRelationType.CandidateSameAs, RelationResolution.Ambiguous, CorrelationConfidence.Low, ContractVersionConflictRule,
                    [.. clash.Select(c => new CorrelationEvidence("path", c.Path, c.Package, c.SourceSnapshotId))],
                    $"Both declare {clash.Key.Identity} version {clash.Key.DeclaredVersion} but their content differs. Compatibility is never inferred from the name: review."));
        }

        foreach (var c in ordered.Where(c => c.ReferencingServices is { Count: > 0 }))
            foreach (var service in c.ReferencingServices!.Order(StringComparer.Ordinal))
            {
                entity(ServiceFact(service, "contract reference"));
                relation(new(ServiceKey(service), CanonicalKeys.EntityKey(CanonicalEntityType.ContractVersion, $"{c.Kind.ToLowerInvariant()}:sha256:{c.ContentHash.ToLowerInvariant()}"),
                    CanonicalRelationType.References, RelationResolution.Explicit, CorrelationConfidence.Explicit, ContractServiceRule,
                    [new("reference", c.Path, service, c.SourceSnapshotId)], "The service's source references this contract file."));
            }
    }

    static IReadOnlyDictionary<string, string> Attrs(params (string Key, string? Value)[] pairs) =>
        pairs.Where(p => !string.IsNullOrWhiteSpace(p.Value)).ToDictionary(p => p.Key, p => p.Value!, StringComparer.Ordinal);

    // ---- Case K: aliases are scoped, never identity ----

    static void Systems(IReadOnlyList<SystemIdentityInput> systems, Action<CanonicalEntityFact> entity, List<CanonicalAliasFact> aliases)
    {
        var ordered = systems.OrderBy(s => s.Name, StringComparer.Ordinal).ToList();
        foreach (var s in ordered)
        {
            var fact = new CanonicalEntityFact(s.Type, CanonicalKeys.Normalize(s.Name), s.Name, s.Name, "declared system name", [new("declared", s.Source, s.Name)]);
            entity(fact);
            foreach (var alias in s.DeclaredAliases.Where(a => !string.IsNullOrWhiteSpace(a)).Order(StringComparer.Ordinal))
                aliases.Add(new(fact.Key, alias, AliasState.Explicit, s.Source, DeclaredAliasRule));
        }

        // An acronym that matches another system's initials is only a proposal: the same letters can name different systems in different projects.
        foreach (var s in ordered)
            foreach (var other in ordered)
            {
                if (ReferenceEquals(s, other) || s.Type != other.Type) continue;
                if (!IsAcronym(s.Name) || !AcronymMatches(s.Name, other.Name)) continue;
                if (other.DeclaredAliases.Any(a => string.Equals(a, s.Name, StringComparison.OrdinalIgnoreCase))) continue; // already explicit
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
            {
                var target = known.Values.FirstOrDefault(e => e.Type == CanonicalEntityType.ContractVersion && e.SourceIdentity.Equals(hash, StringComparison.OrdinalIgnoreCase));
                if (target is null) continue;
                relation(new(fact.Key, target.Key, CanonicalRelationType.References, RelationResolution.Explicit, CorrelationConfidence.Explicit, SymbolContractRule, [evidence],
                    "The symbol loads or declares this contract file."));
            }
        }
    }

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
                if (!known.ContainsKey(target.Key)) entity(RequirementFact(target.Id!, "explicit test reference", t.SourceSnapshotId));
                relation(new(fact.Key, target.Key, CanonicalRelationType.Verifies, RelationResolution.Explicit, CorrelationConfidence.Explicit, TestExplicitRule, [evidence],
                    $"The test names {target.Id} explicitly. This says what it is meant to verify, not that it ran or passed."));
            }
            foreach (var target in t.CandidateTargets.Select(x => VerifyTarget(x, known)).Distinct().OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                if (target.Key is null) { unresolved++; unresolvedIds.UnionWith(t.CandidateTargets.Where(x => VerifyTarget(x, known).Key is null)); continue; }
                if (t.ExplicitTargets.Select(NormalizeRequirement).Contains(target.Id)) continue; // explicit already
                if (!known.ContainsKey(target.Key)) entity(RequirementFact(target.Id!, "candidate test reference", t.SourceSnapshotId));
                relation(new(fact.Key, target.Key, CanonicalRelationType.Verifies, RelationResolution.Candidate, CorrelationConfidence.Low, TestCandidateRule, [evidence],
                    $"The test looks related to {target.Id} by naming only. A candidate is never counted as traceability until confirmed."));
            }
        }
        if (unresolved > 0)
            limitations.Add($"{unresolved} test link(s) could not be resolved to a requirement, story, capability or operation (e.g. {string.Join(", ", unresolvedIds.Take(6))}).");
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
