using System.Text.Json;
using BirkNext.Api.Data.CanonicalInterpretation;
using BirkNext.Api.Data.DeliveryReporting;
using BirkNext.Api.Services.IntegrationTopology;
using BirkNext.DeliveryReporting;
using BirkNext.Interpretation;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services.DeliveryReporting;

public sealed class FunctionalQualityScopeException(string message) : Exception(message);

/// <summary>
/// Functional Quality Scope: a projection over the newest Delivery snapshot of a project (features, user stories, scenarios, acceptance
/// criteria, requirements — already parsed by Delivery) with relation-based scope resolution:
/// <list type="bullet">
/// <item>Direct: features of the scope's root modules (a Delivery module, or the modules a domain profile's primary flow declares).</item>
/// <item>Upstream / Downstream: modules one hop away on a declared domain-profile flow (module ↔ component through the profile's exact project
/// aliases, never text similarity).</item>
/// <item>Related capability: another module's feature that implements the same canonical Capability through an explicit, derived or
/// reviewer-confirmed relation; a candidate capability is only suggested, never included.</item>
/// </list>
/// Technical work (migrations, framework swaps, IaC/pipeline) is excluded by default. Coverage reads canonical VERIFIES relations; Test Data reads
/// the test-design assessment's stable asset ids. Nothing is copied: saved scopes hold roots and explicit item references only.
/// </summary>
public sealed class FunctionalQualityScopeService(DeliveryReportingDbContext db, TimeProvider clock, IEnumerable<ITopologyProfile>? profiles = null,
    CanonicalInterpretationDbContext? canonical = null, TestData.TestDataCatalog? testData = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly IReadOnlyList<ITopologyProfile> _profiles = [.. profiles ?? []];

    private sealed record Feature(Guid Id, string Module, string FeatureKey, string Title, string? SpecPath, string RequirementsJson);
    private sealed record Raw(string Key, ScopeArtifactType Type, string DisplayId, string Title, string Text, Feature Feature, string? ParentKey, string? Priority, int? Line);
    private sealed record Requirement(string Key, string Text);

    // ── Available scopes ──────────────────────────────────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<AvailableScope>> AvailableAsync(Guid projectId, CancellationToken ct)
    {
        var (snapshot, features) = await LatestAsync(projectId, ct);
        var modules = features.GroupBy(f => f.Module).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase).ToList();
        var result = new List<AvailableScope>();
        foreach (var profile in _profiles)
        {
            var mapped = modules.Select(g => g.Key).Where(m => ComponentOf(m, profile) is not null).ToList();
            if (mapped.Count > 0) result.Add(new($"profile:{profile.Id}", profile.Label, "Domain flow", $"{mapped.Count} module(s) on the declared flow: {string.Join(", ", mapped)}"));
        }
        result.AddRange(modules.Select(g => new AvailableScope($"module:{g.Key}", g.Key, "Module", $"{g.Count()} feature(s)")));
        result.AddRange((await db.Scopes.AsNoTracking().Where(s => s.ProjectId == projectId).OrderBy(s => s.Name).ToListAsync(ct))
            .Select(s => new AvailableScope($"custom:{s.Id}", s.Name, "Custom", s.Description ?? $"Version {s.Version}", s.Id)));
        return result;
    }

    // ── Resolution ────────────────────────────────────────────────────────────────────────────────────────────────────

    public async Task<ScopeResolution> ResolveAsync(Guid projectId, ScopeQuery query, CancellationToken ct)
    {
        var definition = await DefinitionAsync(projectId, query, ct);
        var (snapshot, features) = await LatestAsync(projectId, ct);
        var limitations = new List<string>();
        if (snapshot is null)
            return new(definition, null, null, FunctionalQualityClassifier.RuleSetVersion, [], Summary([], 0), [], ["No Delivery snapshot is imported for this project yet."],
                FunctionalQualityScopeText.Precedence);

        var raw = await ItemsAsync(snapshot.Value.Id, features, ct);
        var modules = features.Select(f => f.Module).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var (direct, related) = await ScopeModulesAsync(definition, modules, features, ct);
        var included = new Dictionary<string, (ScopeRelation Relation, string Reason)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (module, reason) in direct) included[module] = (ScopeRelation.Direct, reason);
        if (definition.IncludeRelated || query.IncludeRelated)
            foreach (var r in related.Where(r => r.Included && !included.ContainsKey(r.Module))) included[r.Module] = (r.Relation, r.Reason);

        var coverage = await CoverageIndexAsync(ct);
        if (coverage is null) limitations.Add("No canonical test evidence is interpreted yet: coverage is Not assessed (Correlation Review → Interpret the Source Analysis snapshots).");
        var assessments = (await db.TestDesignAssessments.AsNoTracking().Where(a => a.ProjectId == projectId).ToListAsync(ct)).ToDictionary(a => a.ItemKey, StringComparer.Ordinal);
        var excludes = definition.ExplicitExcludes.ToHashSet(StringComparer.Ordinal);
        var includes = definition.ExplicitIncludes.ToHashSet(StringComparer.Ordinal);
        // How many features use each id (any item type): "US1", "SC-004", "AC-1" are reused by nearly every feature, so a test naming one cannot
        // say which feature it verifies.
        var reqFeatureCount = raw.GroupBy(r => CorrelationRules.NormalizeRequirement(r.DisplayId) ?? r.DisplayId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(r => r.Feature.Id).Distinct().Count(), StringComparer.OrdinalIgnoreCase);

        var items = new List<ScopeItem>();
        var technicalExcluded = 0;
        foreach (var r in raw)
        {
            var (category, rule) = FunctionalQualityClassifier.Classify(r.Text, r.Feature.Title);
            var explicitlyIncluded = includes.Contains(r.Key);
            // Precedence: explicit exclusion > explicit inclusion > smart inclusion.
            if (excludes.Contains(r.Key)) continue;
            ScopeRelation relation; string reason; ScopeInclusion inclusion;
            if (included.TryGetValue(r.Feature.Module, out var membership))
            {
                if (category == QualityCategory.Technical && !query.IncludeTechnical && !explicitlyIncluded) { technicalExcluded++; continue; }
                (relation, reason, inclusion) = (membership.Relation, $"{membership.Reason} · feature {r.Feature.FeatureKey}", explicitlyIncluded ? ScopeInclusion.ExplicitInclude : ScopeInclusion.Smart);
            }
            else if (explicitlyIncluded) (relation, reason, inclusion) = (ScopeRelation.ExplicitlyIncluded, "Added to this scope by a person.", ScopeInclusion.ExplicitInclude);
            else continue;
            var (cov, covDetail) = Coverage(r, coverage, reqFeatureCount);
            assessments.TryGetValue(r.Key, out var assessment);
            items.Add(new(r.Key, r.Type, r.DisplayId, Trim(r.Title, 300), category, rule, r.Feature.Module, r.Feature.FeatureKey, r.Feature.Title, r.ParentKey, r.Priority, relation, inclusion,
                reason, cov, covDetail, TestDataState(assessment), Testability(assessment), false, r.Feature.SpecPath, r.Line));
        }
        // An explicitly included item no longer in the current source stays visible as unavailable (never silently dropped).
        foreach (var key in includes.Where(k => raw.All(r => r.Key != k)).Order(StringComparer.Ordinal))
            items.Add(new(key, TypeOfKey(key), key[(key.LastIndexOf('/') + 1)..], "Unavailable in current source", QualityCategory.Unclassified, "fq.unavailable", ModuleOfKey(key), "", "",
                null, null, ScopeRelation.ExplicitlyIncluded, ScopeInclusion.ExplicitInclude, "Explicitly included earlier; the item is not in the newest Delivery snapshot.",
                ScopeCoverage.NotAssessed, null, ScopeTestDataState.Unknown, TestabilityClassification.Unclassified, true, null, null));

        if (definition.Roots.Count == 0 && includes.Count == 0) limitations.Add("This scope has no smart roots and no explicit items yet: add items from the list of available items.");
        var ordered = items.OrderBy(i => i.Relation).ThenBy(i => i.Module, StringComparer.OrdinalIgnoreCase).ThenBy(i => i.FeatureKey, StringComparer.Ordinal)
            .ThenBy(i => i.ParentKey ?? i.Key, StringComparer.Ordinal).ThenBy(i => i.Type).ThenBy(i => i.Key, StringComparer.Ordinal).ToList();
        return new(definition, snapshot.Value.Id, snapshot.Value.Label, FunctionalQualityClassifier.RuleSetVersion, ordered, Summary(ordered, technicalExcluded), related, limitations,
            FunctionalQualityScopeText.Precedence);
    }

    /// <summary>
    /// Which of the given Delivery features a domain-flow or saved scope covers, with the reason: features of its direct modules (plus related
    /// modules the scope itself includes) and features holding an explicitly included item. Same definition and module rules as
    /// <see cref="ResolveAsync"/> — a scoped view never re-implements the scope.
    /// </summary>
    public async Task<(ScopeDefinition Definition, Dictionary<string, string> Features)> FeatureMembershipAsync(Guid projectId, string scopeKey,
        IReadOnlyList<(string Module, string FeatureKey, string Title)> features, CancellationToken ct)
    {
        var definition = await DefinitionAsync(projectId, new ScopeQuery(scopeKey, null, false), ct);
        var rows = features.Select(f => new Feature(Guid.Empty, f.Module, f.FeatureKey, f.Title, null, "[]")).ToList();
        var modules = rows.Select(f => f.Module).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var (direct, related) = await ScopeModulesAsync(definition, modules, rows, ct);
        var reasons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (module, reason) in direct) reasons.TryAdd(module, reason);
        if (definition.IncludeRelated)
            foreach (var r in related.Where(r => r.Included)) reasons.TryAdd(r.Module, r.Reason);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in features.Where(f => reasons.ContainsKey(f.Module))) result[f.FeatureKey] = reasons[f.Module];
        foreach (var key in definition.ExplicitIncludes)
        {
            var parts = key.Contains(':') ? key[(key.IndexOf(':') + 1)..].Split('/') : [];
            if (parts.Length >= 2 && features.FirstOrDefault(f => f.FeatureKey == parts[1] && string.Equals(f.Module, parts[0], StringComparison.OrdinalIgnoreCase)) is { FeatureKey: not null } hit)
                result.TryAdd(hit.FeatureKey, "Holds an item a person added to this scope");
        }
        return (definition, result);
    }

    /// <summary>Every item of the newest snapshot (all modules), for the custom-scope builder's "available items" list.</summary>
    public Task<ScopeResolution> AllItemsAsync(Guid projectId, bool includeTechnical, CancellationToken ct) =>
        ResolveAsync(projectId, new ScopeQuery(null, null, false, includeTechnical, new ScopeDefinition { Name = "All items", Roots = ["*"] }), ct);

    /// <summary>The builder's candidate list: top-level items (stories, scenarios, requirements) matching <paramref name="search"/>, at most
    /// <paramref name="take"/>, so the browser never downloads the whole project to offer a few rows.</summary>
    public async Task<ScopeResolution> CandidatesAsync(Guid projectId, bool includeTechnical, string? search, int take, CancellationToken ct)
    {
        var all = await AllItemsAsync(projectId, includeTechnical, ct);
        var q = search?.Trim();
        var items = all.Items.Where(i => i.ParentKey is null && (string.IsNullOrEmpty(q) || i.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
            || i.DisplayId.Contains(q, StringComparison.OrdinalIgnoreCase) || i.Module.Contains(q, StringComparison.OrdinalIgnoreCase)
            || i.FeatureKey.Contains(q, StringComparison.OrdinalIgnoreCase))).Take(Math.Clamp(take, 1, 200)).ToList();
        return all with { Items = items };
    }

    public async Task<ScopeItemDetail?> DetailAsync(Guid projectId, string key, CancellationToken ct)
    {
        var (snapshot, features) = await LatestAsync(projectId, ct);
        if (snapshot is null) return null;
        var raw = await ItemsAsync(snapshot.Value.Id, features, ct);
        if (raw.FirstOrDefault(r => r.Key == key) is not { } item) return null;
        var all = await AllItemsAsync(projectId, true, ct);
        var view = all.Items.FirstOrDefault(i => i.Key == key);
        if (view is null) return null;
        var children = all.Items.Where(i => i.ParentKey == key).ToList();
        var assessment = await db.TestDesignAssessments.AsNoTracking().FirstOrDefaultAsync(a => a.ProjectId == projectId && a.ItemKey == key, ct);
        return new(view, item.Text, children, view.CoverageDetail is { } d ? [d] : [], assessment is null ? null : View(assessment));
    }

    // ── Scope roots and relations ─────────────────────────────────────────────────────────────────────────────────────

    private async Task<(List<(string Module, string Reason)> Direct, List<ScopeRelatedModule> Related)> ScopeModulesAsync(ScopeDefinition definition, IReadOnlyList<string> modules,
        IReadOnlyList<Feature> features, CancellationToken ct)
    {
        var direct = new List<(string, string)>();
        foreach (var root in definition.Roots)
        {
            if (root == "*") { direct.AddRange(modules.Select(m => (m, "In the project"))); continue; }
            if (root.StartsWith("profile:", StringComparison.Ordinal) && _profiles.FirstOrDefault(p => p.Id == root[8..]) is { } profile)
            {
                // The profile's primary (first) declared flow is the scope's own flow; modules on its other flows are related.
                var primary = profile.Paths.FirstOrDefault()?.Steps.SelectMany(s => new[] { s.From, s.To }).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
                foreach (var m in modules)
                    if (ComponentOf(m, profile) is { } component && primary.Contains(component))
                        direct.Add((m, $"Owned by {component} on the {profile.Label} flow \"{profile.Paths[0].Label}\" (declared domain profile)"));
                continue;
            }
            var module = root.StartsWith("module:", StringComparison.Ordinal) ? root[7..] : root;
            if (modules.FirstOrDefault(m => string.Equals(m, module, StringComparison.OrdinalIgnoreCase)) is { } found) direct.Add((found, $"Owned by module {found}"));
        }
        var directModules = direct.Select(d => d.Item1).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var related = new List<ScopeRelatedModule>();

        // One hop on declared domain-profile flows: the neighbour of a direct module's component.
        foreach (var profile in _profiles)
            foreach (var path in profile.Paths)
                foreach (var step in path.Steps)
                    foreach (var m in modules.Where(m => !directModules.Contains(m)))
                    {
                        var component = ComponentOf(m, profile);
                        if (component is null) continue;
                        var neighbourIsDirect = (string? other) => other is not null && directModules.Any(d => string.Equals(ComponentOf(d, profile), other, StringComparison.OrdinalIgnoreCase));
                        if (string.Equals(step.From, component, StringComparison.OrdinalIgnoreCase) && neighbourIsDirect(step.To))
                            Add(new(m, ScopeRelation.Upstream, $"Upstream: {component} → {step.To} on \"{path.Label}\" ({profile.Label} domain profile)", true));
                        else if (string.Equals(step.To, component, StringComparison.OrdinalIgnoreCase) && neighbourIsDirect(step.From))
                            Add(new(m, ScopeRelation.Downstream, $"Downstream: {step.From} → {component} on \"{path.Label}\" ({profile.Label} domain profile)", true));
                    }

        // Shared canonical capabilities: confirmed / explicit / derived links include the module; candidates are suggestions only.
        if (canonical is not null && directModules.Count > 0)
        {
            try
            {
                var featureKeys = features.Select(f => $"{CanonicalKeys.Normalize(f.Module)}/{CanonicalKeys.Normalize(f.FeatureKey)}").ToHashSet(StringComparer.Ordinal);
                var rows = await (from r in canonical.Relations.AsNoTracking()
                                  join f in canonical.Entities.AsNoTracking() on r.SourceEntityId equals f.Id
                                  join c in canonical.Entities.AsNoTracking() on r.TargetEntityId equals c.Id
                                  where r.Active && r.RelationType == nameof(CanonicalRelationType.Implements) && f.EntityType == nameof(CanonicalEntityType.Feature)
                                        && c.EntityType == nameof(CanonicalEntityType.Capability) && r.ReviewState != nameof(RelationReviewState.Rejected)
                                  select new { Feature = f.IdentityKey, Capability = c.DisplayName, CapabilityId = c.Id, r.Resolution, r.ReviewState }).ToListAsync(ct);
                var ours = rows.Where(r => featureKeys.Contains(r.Feature) && directModules.Contains(ModuleOfFeatureKey(r.Feature, features))).Select(r => r.CapabilityId).ToHashSet();
                foreach (var r in rows.Where(r => ours.Contains(r.CapabilityId) && featureKeys.Contains(r.Feature)))
                {
                    var module = ModuleOfFeatureKey(r.Feature, features);
                    if (module.Length == 0 || directModules.Contains(module)) continue;
                    var established = r.ReviewState == nameof(RelationReviewState.Confirmed) || r.Resolution is nameof(RelationResolution.Explicit) or nameof(RelationResolution.Derived);
                    Add(new(module, ScopeRelation.RelatedCapability, established
                        ? $"Shares the capability \"{r.Capability}\" (confirmed canonical relation)"
                        : $"Suggested: may share the capability \"{r.Capability}\" (candidate — confirm in Correlation Review or include items explicitly)", established));
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Data.Common.DbException) { /* canonical store unavailable: no capability relations */ }
        }
        return (direct, related);

        void Add(ScopeRelatedModule candidate)
        {
            var existing = related.FindIndex(r => string.Equals(r.Module, candidate.Module, StringComparison.OrdinalIgnoreCase));
            if (existing < 0) related.Add(candidate);
            else if (!related[existing].Included && candidate.Included) related[existing] = candidate;
        }
    }

    private static string ModuleOfFeatureKey(string identityKey, IReadOnlyList<Feature> features) =>
        features.FirstOrDefault(f => $"{CanonicalKeys.Normalize(f.Module)}/{CanonicalKeys.Normalize(f.FeatureKey)}" == identityKey)?.Module ?? "";

    /// <summary>The profile component a Delivery module implements: the component's name, or one dot-separated segment of one of its declared
    /// project aliases ("M2LB.SkoleService.Api" → SkoleService), compared exactly (case-insensitive). Never a similarity match. A segment that
    /// several components' aliases share ("M2LB", "Api", "Host") identifies none of them.</summary>
    public static string? ComponentOf(string module, ITopologyProfile profile)
    {
        if (profile.Components.FirstOrDefault(c => string.Equals(c.Name, module, StringComparison.OrdinalIgnoreCase)) is { } named) return named.Name;
        var owners = profile.Components.Where(c => c.Aliases.Any(a => a.Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Any(seg => string.Equals(seg, module, StringComparison.OrdinalIgnoreCase)))).Select(c => c.Name).ToList();
        return owners.Count == 1 ? owners[0] : null;
    }

    // ── Items, coverage, Test Data, testability ───────────────────────────────────────────────────────────────────────

    private async Task<((Guid Id, string Label)? Snapshot, List<Feature> Features)> LatestAsync(Guid projectId, CancellationToken ct)
    {
        var s = await db.Snapshots.AsNoTracking().Where(x => x.ProjectId == projectId).OrderByDescending(x => x.EffectiveAt).ThenByDescending(x => x.ImportedAt)
            .Select(x => new { x.Id, x.Label }).FirstOrDefaultAsync(ct);
        if (s is null) return (null, []);
        var features = await db.FeatureSnapshots.AsNoTracking().Where(f => f.SnapshotId == s.Id)
            .Select(f => new Feature(f.Id, f.Module, f.FeatureKey, f.Title, f.SpecPath, f.RequirementsJson)).ToListAsync(ct);
        return ((s.Id, s.Label), features);
    }

    private async Task<List<Raw>> ItemsAsync(Guid snapshotId, IReadOnlyList<Feature> features, CancellationToken ct)
    {
        var byId = features.ToDictionary(f => f.Id);
        var stories = await db.UserStorySnapshots.AsNoTracking().Where(s => s.SnapshotId == snapshotId && !s.Removed).ToListAsync(ct);
        var criteria = await db.AcceptanceCriterionSnapshots.AsNoTracking().Where(a => a.SnapshotId == snapshotId).ToListAsync(ct);
        var items = new List<Raw>();
        foreach (var s in stories.Where(s => byId.ContainsKey(s.FeatureSnapshotId)))
        {
            var f = byId[s.FeatureSnapshotId];
            var scenario = s.Kind == "Scenario" || (s.Kind is null && s.StoryKey.StartsWith('S'));
            items.Add(new(Key("story", f, s.StoryKey), scenario ? ScopeArtifactType.Scenario : ScopeArtifactType.UserStory, s.StoryKey, s.Title,
                $"{s.Title} {s.Description}", f, null, s.Priority, s.SourceLine));
        }
        foreach (var a in criteria.Where(a => byId.ContainsKey(a.FeatureSnapshotId)))
        {
            var f = byId[a.FeatureSnapshotId];
            items.Add(new(Key("ac", f, a.CriterionKey), ScopeArtifactType.AcceptanceCriterion, a.CriterionKey, a.Text, a.Text, f,
                a.StoryKey is null ? null : Key("story", f, a.StoryKey), null, a.SourceLine));
        }
        foreach (var f in features)
            foreach (var r in Requirements(f.RequirementsJson))
                items.Add(new(Key("req", f, r.Key), ScopeArtifactType.Requirement, r.Key, r.Text, r.Text, f, null, null, null));
        return items;
    }

    public static string Key(string kind, string module, string featureKey, string itemKey) => $"{kind}:{module}/{featureKey}/{itemKey}";
    private static string Key(string kind, Feature f, string itemKey) => Key(kind, f.Module, f.FeatureKey, itemKey);

    private static IEnumerable<Requirement> Requirements(string json)
    {
        try { return JsonSerializer.Deserialize<List<Requirement>>(string.IsNullOrWhiteSpace(json) ? "[]" : json, Json) ?? []; }
        catch (JsonException) { return []; }
    }

    private static ScopeArtifactType TypeOfKey(string key) => key.Split(':')[0] switch { "ac" => ScopeArtifactType.AcceptanceCriterion, "req" => ScopeArtifactType.Requirement, _ => ScopeArtifactType.UserStory };
    private static string ModuleOfKey(string key) => key.Contains(':') ? key[(key.IndexOf(':') + 1)..].Split('/')[0] : "";

    /// <summary>Canonical VERIFIES index: requirement/story identity → (explicit, candidate) test counts. Null when nothing is interpreted.</summary>
    private async Task<Dictionary<string, (int Explicit, int Candidate)>?> CoverageIndexAsync(CancellationToken ct)
    {
        if (canonical is null) return null;
        try
        {
            var rows = await (from r in canonical.Relations.AsNoTracking()
                              join t in canonical.Entities.AsNoTracking() on r.TargetEntityId equals t.Id
                              where r.Active && r.RelationType == nameof(CanonicalRelationType.Verifies) && r.ReviewState != nameof(RelationReviewState.Rejected)
                              select new { t.IdentityKey, r.Resolution, r.ReviewState }).ToListAsync(ct);
            if (rows.Count == 0) return null;
            return rows.GroupBy(r => r.IdentityKey, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key,
                g => (g.Count(r => r.Resolution is nameof(RelationResolution.Explicit) or nameof(RelationResolution.Derived) || r.ReviewState == nameof(RelationReviewState.Confirmed)),
                      g.Count(r => r.Resolution is nameof(RelationResolution.Candidate) or nameof(RelationResolution.Ambiguous) && r.ReviewState != nameof(RelationReviewState.Confirmed))),
                StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Data.Common.DbException) { return null; }
    }

    private static (ScopeCoverage, string?) Coverage(Raw r, Dictionary<string, (int Explicit, int Candidate)>? index, IReadOnlyDictionary<string, int> reqFeatures)
    {
        if (index is null) return (ScopeCoverage.NotAssessed, null);
        if (r.Type is not (ScopeArtifactType.Requirement or ScopeArtifactType.UserStory or ScopeArtifactType.AcceptanceCriterion)) return (ScopeCoverage.NotAssessed, null);
        var id = CorrelationRules.NormalizeRequirement(r.DisplayId) ?? r.DisplayId;
        // correlation/3: tests are linked to the feature pack's own requirement/story (module/feature/id) when the evidence says which pack.
        if (index.TryGetValue(CorrelationRules.Scoped(r.Feature.Module, r.Feature.FeatureKey, id), out var scoped))
            return scoped.Explicit > 0
                ? (ScopeCoverage.AutomatedCoverage, $"{scoped.Explicit} test(s) explicitly verify {id} of this feature pack (canonical VERIFIES).")
                : (ScopeCoverage.CandidateCoverageOnly, $"{scoped.Candidate} suggested or ambiguous test link(s) to {id} of this feature pack — not confirmed coverage.");
        if (!index.TryGetValue(id, out var links)) return (ScopeCoverage.NoKnownCoverage, "No canonical test relation names this item.");
        // A test that names "FR-001" cannot say which feature it means when several features use that id: never claimed as coverage.
        var shared = reqFeatures.TryGetValue(id, out var n) && n > 1;
        if (links.Explicit > 0 && !shared) return (ScopeCoverage.AutomatedCoverage, $"{links.Explicit} test(s) explicitly verify {id} (canonical VERIFIES).");
        if (links.Explicit > 0) return (ScopeCoverage.CandidateCoverageOnly, $"{links.Explicit} test(s) name {id}, but {n} features use that id — the test does not say which.");
        return (ScopeCoverage.CandidateCoverageOnly, $"{links.Candidate} candidate test link(s) to {id} — not confirmed coverage.");
    }

    private ScopeTestDataState TestDataState(DeliveryTestDesignAssessmentRecord? a)
    {
        if (a is null) return ScopeTestDataState.Unknown;
        var ids = JsonSerializer.Deserialize<List<string>>(a.RequiredTestDataJson, Json) ?? [];
        if (ids.Count == 0) return ScopeTestDataState.NotRequired;
        if (testData is null) return ScopeTestDataState.Unknown;
        return ids.All(id => testData.ResolveReference(id) is not null) ? ScopeTestDataState.Available : ScopeTestDataState.MissingRequired;
    }

    private static TestabilityClassification Testability(DeliveryTestDesignAssessmentRecord? a) =>
        a is not null && Enum.TryParse<TestabilityClassification>(a.Testability, out var t) ? t : TestabilityClassification.Unclassified;

    private static ScopeSummary Summary(IReadOnlyList<ScopeItem> items, int technicalExcluded) => new(
        items.Count(i => i.Type == ScopeArtifactType.UserStory), items.Count(i => i.Type == ScopeArtifactType.Scenario),
        items.Count(i => i.Type == ScopeArtifactType.AcceptanceCriterion), items.Count(i => i.Type == ScopeArtifactType.Requirement), 0,
        items.Where(i => !i.Unavailable).GroupBy(i => i.Category).ToDictionary(g => g.Key, g => g.Count()),
        items.Where(i => !i.Unavailable).GroupBy(i => i.Coverage).ToDictionary(g => g.Key, g => g.Count()), technicalExcluded, items.Count(i => i.Unavailable));

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    // ── Definitions: built-in and custom (references only) ────────────────────────────────────────────────────────────

    private async Task<ScopeDefinition> DefinitionAsync(Guid projectId, ScopeQuery query, CancellationToken ct)
    {
        if (query.Draft is { } draft) return draft with { IncludeRelated = draft.IncludeRelated || query.IncludeRelated };
        var customId = query.CustomScopeId ?? (query.ScopeKey?.StartsWith("custom:", StringComparison.Ordinal) == true && Guid.TryParse(query.ScopeKey[7..], out var g) ? g : null);
        if (customId is { } id)
        {
            var s = await db.Scopes.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.ProjectId == projectId, ct) ?? throw new FunctionalQualityScopeException("Unknown scope.");
            var refs = await db.ScopeReferences.AsNoTracking().Where(r => r.ScopeId == id).ToListAsync(ct);
            return View(s, refs) with { IncludeRelated = s.IncludeRelated || query.IncludeRelated };
        }
        if (string.IsNullOrWhiteSpace(query.ScopeKey)) throw new FunctionalQualityScopeException("Choose a scope.");
        var name = query.ScopeKey.StartsWith("profile:", StringComparison.Ordinal) ? _profiles.FirstOrDefault(p => p.Id == query.ScopeKey[8..])?.Label ?? query.ScopeKey : query.ScopeKey[(query.ScopeKey.IndexOf(':') + 1)..];
        return new ScopeDefinition { Name = name, Roots = [query.ScopeKey], IncludeRelated = query.IncludeRelated, BuiltIn = true };
    }

    private static ScopeDefinition View(DeliveryScopeRecord s, IReadOnlyList<DeliveryScopeReferenceRecord> refs) => new()
    {
        Id = s.Id, Name = s.Name, Description = s.Description, Roots = JsonSerializer.Deserialize<List<string>>(s.RootsJson, Json) ?? [], IncludeRelated = s.IncludeRelated,
        ExplicitIncludes = [.. refs.Where(r => r.Mode == nameof(ScopeInclusion.ExplicitInclude)).Select(r => r.ItemKey).Order(StringComparer.Ordinal)],
        ExplicitExcludes = [.. refs.Where(r => r.Mode == nameof(ScopeInclusion.ExplicitExclude)).Select(r => r.ItemKey).Order(StringComparer.Ordinal)],
        Version = s.Version,
    };

    public async Task<ScopeDefinition> SaveAsync(Guid projectId, ScopeDefinition definition, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(definition.Name)) throw new FunctionalQualityScopeException("A scope needs a name.");
        if (!await db.Projects.AnyAsync(p => p.Id == projectId, ct)) throw new FunctionalQualityScopeException("Unknown project.");
        var now = clock.GetUtcNow();
        var name = definition.Name.Trim();
        if (await db.Scopes.AnyAsync(s => s.ProjectId == projectId && s.Name == name && s.Id != (definition.Id ?? Guid.Empty), ct))
            throw new FunctionalQualityScopeException($"A scope named \"{name}\" already exists.");
        DeliveryScopeRecord record;
        if (definition.Id is { } id)
        {
            record = await db.Scopes.FirstOrDefaultAsync(s => s.Id == id && s.ProjectId == projectId, ct) ?? throw new FunctionalQualityScopeException("Unknown scope.");
            record.Version++;
        }
        else db.Scopes.Add(record = new DeliveryScopeRecord { Id = Guid.NewGuid(), ProjectId = projectId, CreatedAt = now });
        record.Name = name;
        record.Description = string.IsNullOrWhiteSpace(definition.Description) ? null : definition.Description.Trim();
        record.RootsJson = JsonSerializer.Serialize(definition.Roots.Where(r => r != "*").Distinct(StringComparer.Ordinal).ToList(), Json);
        record.IncludeRelated = definition.IncludeRelated;
        record.UpdatedAt = now;
        var existing = await db.ScopeReferences.Where(r => r.ScopeId == record.Id).ToListAsync(ct);
        db.ScopeReferences.RemoveRange(existing.Where(r => !definition.ExplicitIncludes.Contains(r.ItemKey) && !definition.ExplicitExcludes.Contains(r.ItemKey)));
        foreach (var (key, mode) in definition.ExplicitIncludes.Select(k => (k, ScopeInclusion.ExplicitInclude)).Concat(definition.ExplicitExcludes.Select(k => (k, ScopeInclusion.ExplicitExclude))))
            if (existing.FirstOrDefault(r => r.ItemKey == key) is { } row) row.Mode = mode.ToString();
            else db.ScopeReferences.Add(new DeliveryScopeReferenceRecord { Id = Guid.NewGuid(), ScopeId = record.Id, ItemKey = key, Mode = mode.ToString(), AddedAt = now });
        await db.SaveChangesAsync(ct);
        return View(record, await db.ScopeReferences.AsNoTracking().Where(r => r.ScopeId == record.Id).ToListAsync(ct));
    }

    /// <summary>Adds (or switches) one explicit reference. Exclusion and inclusion of the same item replace each other (one row per item).</summary>
    public async Task<ScopeDefinition> ReferenceAsync(Guid projectId, Guid scopeId, ScopeReferenceRequest request, CancellationToken ct)
    {
        if (request.Mode == ScopeInclusion.Smart) throw new FunctionalQualityScopeException("Use remove to return an item to smart membership.");
        var scope = await db.Scopes.FirstOrDefaultAsync(s => s.Id == scopeId && s.ProjectId == projectId, ct) ?? throw new FunctionalQualityScopeException("Unknown scope.");
        var row = await db.ScopeReferences.FirstOrDefaultAsync(r => r.ScopeId == scopeId && r.ItemKey == request.ItemKey, ct);
        if (row is null) db.ScopeReferences.Add(new DeliveryScopeReferenceRecord { Id = Guid.NewGuid(), ScopeId = scopeId, ItemKey = request.ItemKey, Mode = request.Mode.ToString(), AddedAt = clock.GetUtcNow() });
        else row.Mode = request.Mode.ToString();
        scope.Version++;
        scope.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return View(scope, await db.ScopeReferences.AsNoTracking().Where(r => r.ScopeId == scopeId).ToListAsync(ct));
    }

    /// <summary>Removes an explicit reference (the item itself is never touched).</summary>
    public async Task<ScopeDefinition> RemoveReferenceAsync(Guid projectId, Guid scopeId, string itemKey, CancellationToken ct)
    {
        var scope = await db.Scopes.FirstOrDefaultAsync(s => s.Id == scopeId && s.ProjectId == projectId, ct) ?? throw new FunctionalQualityScopeException("Unknown scope.");
        if (await db.ScopeReferences.FirstOrDefaultAsync(r => r.ScopeId == scopeId && r.ItemKey == itemKey, ct) is { } row)
        {
            db.ScopeReferences.Remove(row);
            scope.Version++;
            scope.UpdatedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
        }
        return View(scope, await db.ScopeReferences.AsNoTracking().Where(r => r.ScopeId == scopeId).ToListAsync(ct));
    }

    public async Task<ScopeDefinition> CloneAsync(Guid projectId, Guid scopeId, CancellationToken ct)
    {
        var source = await DefinitionAsync(projectId, new ScopeQuery(null, scopeId, false), ct);
        var name = source.Name + " (copy)";
        for (var i = 2; await db.Scopes.AnyAsync(s => s.ProjectId == projectId && s.Name == name, ct); i++) name = $"{source.Name} (copy {i})";
        return await SaveAsync(projectId, source with { Id = null, Name = name, Version = 1 }, ct);
    }

    /// <summary>Deletes the scope definition and its references only; frozen scope snapshots keep their membership (scope id becomes empty).</summary>
    public async Task<bool> DeleteAsync(Guid projectId, Guid scopeId, CancellationToken ct)
    {
        var scope = await db.Scopes.FirstOrDefaultAsync(s => s.Id == scopeId && s.ProjectId == projectId, ct);
        if (scope is null) return false;
        db.ScopeReferences.RemoveRange(await db.ScopeReferences.Where(r => r.ScopeId == scopeId).ToListAsync(ct));
        foreach (var snap in await db.ScopeSnapshots.Where(s => s.ScopeId == scopeId).ToListAsync(ct)) snap.ScopeId = null;
        db.Scopes.Remove(scope);
        await db.SaveChangesAsync(ct);
        return true;
    }

    // ── Test-design assessment and frozen snapshots ──────────────────────────────────────────────────────────────────

    public async Task<TestDesignAssessmentView> AssessAsync(Guid projectId, TestDesignAssessmentView assessment, CancellationToken ct)
    {
        var row = await db.TestDesignAssessments.FirstOrDefaultAsync(a => a.ProjectId == projectId && a.ItemKey == assessment.ItemKey, ct);
        if (row is null) db.TestDesignAssessments.Add(row = new DeliveryTestDesignAssessmentRecord { Id = Guid.NewGuid(), ProjectId = projectId, ItemKey = assessment.ItemKey });
        row.Testability = assessment.Testability.ToString();
        row.VerificationTypesJson = JsonSerializer.Serialize(assessment.VerificationTypes.Distinct().ToList(), Json);
        row.RequiredTestDataJson = JsonSerializer.Serialize(assessment.RequiredTestDataAssetIds.Distinct().ToList(), Json);
        row.Comment = string.IsNullOrWhiteSpace(assessment.Comment) ? null : assessment.Comment.Trim();
        row.ReviewedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return View(row);
    }

    private static TestDesignAssessmentView View(DeliveryTestDesignAssessmentRecord a) => new(a.ItemKey, Enum.TryParse<TestabilityClassification>(a.Testability, out var t) ? t : TestabilityClassification.Unclassified,
        JsonSerializer.Deserialize<List<string>>(a.VerificationTypesJson, Json) ?? [], JsonSerializer.Deserialize<List<string>>(a.RequiredTestDataJson, Json) ?? [], a.Comment, a.ReviewedAt);

    /// <summary>Freezes the scope's exact membership now (for a report or finalized review). The live scope keeps evolving; this never changes.</summary>
    public async Task<ScopeSnapshotView> FreezeAsync(Guid projectId, ScopeQuery query, CancellationToken ct)
    {
        var resolution = await ResolveAsync(projectId, query, ct);
        var record = new DeliveryScopeSnapshotRecord
        {
            Id = Guid.NewGuid(), ProjectId = projectId, ScopeId = resolution.Scope.Id, ScopeName = resolution.Scope.Name, ScopeVersion = resolution.Scope.Version,
            DeliverySnapshotId = resolution.SnapshotId, RuleSetVersion = $"{resolution.RuleSetVersion};{WorkItemInterpreter.RuleSetVersion}",
            ItemsJson = JsonSerializer.Serialize(resolution.Items.Select(i => new { i.Key, i.Type, i.Category, i.Relation, i.Inclusion, i.Coverage }), Json),
            ItemCount = resolution.Items.Count, CreatedAt = clock.GetUtcNow(),
        };
        db.ScopeSnapshots.Add(record);
        await db.SaveChangesAsync(ct);
        return new(record.Id, record.ScopeId, record.ScopeName, record.ScopeVersion, record.DeliverySnapshotId, record.RuleSetVersion, record.ItemCount, record.CreatedAt);
    }

    public async Task<IReadOnlyList<ScopeSnapshotView>> SnapshotsAsync(Guid projectId, CancellationToken ct) =>
        [.. (await db.ScopeSnapshots.AsNoTracking().Where(s => s.ProjectId == projectId).OrderByDescending(s => s.CreatedAt).Take(50).ToListAsync(ct))
            .Select(s => new ScopeSnapshotView(s.Id, s.ScopeId, s.ScopeName, s.ScopeVersion, s.DeliverySnapshotId, s.RuleSetVersion, s.ItemCount, s.CreatedAt))];
}
