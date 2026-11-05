using System.Text.Json;
using System.Text.RegularExpressions;
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

        var evidence = await EvidenceContextAsync(query.Environment, ct);
        var coverage = evidence.Applied ? await CoverageIndexAsync(evidence, ct) : null;
        if (coverage is null) limitations.Add(CoverageLimitation(evidence));
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
            FunctionalQualityScopeText.Precedence) { Evidence = evidence };
    }

    private static string CoverageLimitation(ScopeEvidenceContext evidence) => evidence switch
    {
        { Available.Count: 0 } => "No canonical test evidence is interpreted yet: coverage is Not assessed (Correlation Review → Interpret the Source Analysis snapshots).",
        { Applied: false } => $"Test evidence is interpreted for {evidence.Available.Count} environments: choose an environment (or All environments, aggregated) to see coverage. " +
            "Evidence from one environment never answers for another.",
        _ => $"No canonical test evidence is interpreted for environment {evidence.Environment}: coverage is Not assessed there (other environments are not used in its place).",
    };

    /// <summary>
    /// The environment whose canonical evidence is used. Each canonical interpretation scope is one Target Environment, so a relation always belongs to
    /// exactly one environment; nothing is assigned to an environment it was not interpreted for. <paramref name="requested"/>: an environment id,
    /// <see cref="ScopeEnvironments.All"/> (aggregated, labelled), or null — the only environment with evidence when there is exactly one.
    /// </summary>
    public async Task<ScopeEvidenceContext> EvidenceContextAsync(string? requested, CancellationToken ct)
    {
        IReadOnlyList<ScopeEvidenceEnvironment> available = [];
        if (canonical is not null)
            try
            {
                var rows = await canonical.Relations.AsNoTracking().Where(r => r.Active && r.RelationType == nameof(CanonicalRelationType.Verifies))
                    .Select(r => new { r.Scope, r.SourceEntityId }).ToListAsync(ct);
                available = [.. rows.GroupBy(r => r.Scope, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal)
                    .Select(g => new ScopeEvidenceEnvironment(g.Key, g.Count(), g.Select(r => r.SourceEntityId).Distinct().Count()))];
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Data.Common.DbException) { /* canonical store unavailable */ }
        if (requested == ScopeEnvironments.All)
            return new(null, true, available, "All environments, aggregated: every test relationship keeps its own environment — this is not evidence from one environment.");
        if (requested is { Length: > 0 })
            return new(requested, false, available, available.Any(a => a.Id == requested)
                ? "Source relationships interpreted for this environment's source snapshots — discovery, not execution."
                : "No test evidence is interpreted for this environment.");
        if (available.Count == 1) return new(available[0].Id, false, available, "The only environment with interpreted test evidence — source relationships, not executions.");
        return new(null, false, available, available.Count == 0 ? "No test evidence is interpreted for any environment yet." : $"Test evidence exists for {available.Count} environments — choose one.");
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
    public Task<ScopeResolution> AllItemsAsync(Guid projectId, bool includeTechnical, CancellationToken ct, string? environment = null) =>
        ResolveAsync(projectId, new ScopeQuery(null, null, false, includeTechnical, new ScopeDefinition { Name = "All items", Roots = ["*"] }, environment), ct);

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

    public async Task<ScopeItemDetail?> DetailAsync(Guid projectId, string key, CancellationToken ct, string? environment = null)
    {
        var (snapshot, features) = await LatestAsync(projectId, ct);
        if (snapshot is null) return null;
        var raw = await ItemsAsync(snapshot.Value.Id, features, ct);
        if (raw.FirstOrDefault(r => r.Key == key) is not { } item) return null;
        var all = await AllItemsAsync(projectId, true, ct, environment);
        var view = all.Items.FirstOrDefault(i => i.Key == key);
        if (view is null) return null;
        var children = all.Items.Where(i => i.ParentKey == key).ToList();
        var assessment = await db.TestDesignAssessments.AsNoTracking().FirstOrDefaultAsync(a => a.ProjectId == projectId && a.ItemKey == key, ct);
        var id = CorrelationRules.NormalizeRequirement(item.DisplayId) ?? item.DisplayId;
        var sharedId = raw.Where(r => string.Equals(CorrelationRules.NormalizeRequirement(r.DisplayId) ?? r.DisplayId, id, StringComparison.OrdinalIgnoreCase))
            .Select(r => r.Feature.Id).Distinct().Count() > 1;
        var evidence = all.Evidence ?? await EvidenceContextAsync(environment, ct);
        var scopedKey = CorrelationRules.Scoped(item.Feature.Module, item.Feature.FeatureKey, id);
        var tests = evidence.Applied ? await TestLinksAsync(scopedKey, id, sharedId, evidence, ct) : null;
        var schemas = evidence.Applied ? await SchemaLinksAsync(item, scopedKey, evidence, ct) : null;
        return new(view, item.Text, children, view.CoverageDetail is { } d ? [d] : [], assessment is null ? null : View(assessment))
        {
            Tests = tests ?? [], TestsAssessed = tests is not null, Schemas = schemas ?? [], SchemasAssessed = schemas is not null, Evidence = evidence,
        };
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

    /// <summary>Canonical VERIFIES index of one environment (or all, aggregated): requirement/story identity → (explicit, candidate) test counts.
    /// Null when nothing is interpreted there.</summary>
    private async Task<Dictionary<string, (int Explicit, int Candidate)>?> CoverageIndexAsync(ScopeEvidenceContext evidence, CancellationToken ct)
    {
        if (canonical is null) return null;
        var (allEnvironments, environment) = (evidence.CrossEnvironment, evidence.Environment);
        try
        {
            var rows = await (from r in canonical.Relations.AsNoTracking()
                              join t in canonical.Entities.AsNoTracking() on r.TargetEntityId equals t.Id
                              where r.Active && r.RelationType == nameof(CanonicalRelationType.Verifies) && r.ReviewState != nameof(RelationReviewState.Rejected)
                                    && (allEnvironments || r.Scope == environment)
                              select new { t.IdentityKey, r.Resolution, r.ReviewState }).ToListAsync(ct);
            if (rows.Count == 0) return null;
            return rows.GroupBy(r => r.IdentityKey, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key,
                g => (g.Count(r => r.Resolution is nameof(RelationResolution.Explicit) or nameof(RelationResolution.Derived) || r.ReviewState == nameof(RelationReviewState.Confirmed)),
                      g.Count(r => r.Resolution is nameof(RelationResolution.Candidate) or nameof(RelationResolution.Ambiguous) && r.ReviewState != nameof(RelationReviewState.Confirmed))),
                StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Data.Common.DbException) { return null; }
    }

    /// <summary>
    /// The individual VERIFIES relations behind an item's coverage, classified exactly as <see cref="Coverage"/> counts them: the feature pack's own id first;
    /// an unscoped id several features share is never explicit. Null when nothing is interpreted. Read-only: decisions go to the shared registry.
    /// </summary>
    private async Task<IReadOnlyList<ScopeTestLink>?> TestLinksAsync(string scopedKey, string id, bool sharedId, ScopeEvidenceContext evidence, CancellationToken ct)
    {
        if (canonical is null) return null;
        var (allEnvironments, environment) = (evidence.CrossEnvironment, evidence.Environment);
        try
        {
            if (!await canonical.Relations.AsNoTracking().AnyAsync(r => r.RelationType == nameof(CanonicalRelationType.Verifies) && (allEnvironments || r.Scope == environment), ct)) return null;
            var rows = await (from r in canonical.Relations.AsNoTracking()
                              join t in canonical.Entities.AsNoTracking() on r.TargetEntityId equals t.Id
                              join s in canonical.Entities.AsNoTracking() on r.SourceEntityId equals s.Id
                              where r.Active && r.RelationType == nameof(CanonicalRelationType.Verifies) && (t.IdentityKey == scopedKey || t.IdentityKey == id)
                                    && (allEnvironments || r.Scope == environment)
                              select new { Relation = r, Target = t.IdentityKey, Test = s }).ToListAsync(ct);
            // The feature pack's own requirement wins: an unscoped relation is only used when the pack has none.
            var scoped = rows.Where(x => string.Equals(x.Target, scopedKey, StringComparison.OrdinalIgnoreCase)).ToList();
            var chosen = scoped.Count > 0 ? scoped : rows;
            var unscopedShared = scoped.Count == 0 && sharedId;
            var testIds = chosen.Select(x => x.Test.Id).Distinct().ToList();
            var locations = (await canonical.Occurrences.AsNoTracking().Where(o => testIds.Contains(o.EntityId)).ToListAsync(ct))
                .GroupBy(o => o.EntityId).ToDictionary(g => g.Key, g => g.Select(o => o.Detail ?? o.OccurrenceKey).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)));
            return [.. chosen.Select(x =>
            {
                var r = x.Relation;
                var kind = r.ReviewState == nameof(RelationReviewState.Rejected) ? ScopeTestLinkKind.Rejected
                    : !unscopedShared && (r.Resolution is nameof(RelationResolution.Explicit) or nameof(RelationResolution.Derived) || r.ReviewState == nameof(RelationReviewState.Confirmed))
                        ? ScopeTestLinkKind.Explicit : ScopeTestLinkKind.Candidate;
                var evidence = ParseEvidence(r.EvidenceJson);
                var explanation = unscopedShared && kind != ScopeTestLinkKind.Rejected
                    ? $"{r.Explanation} The test names {id}, which several features use — it does not say which feature it verifies.".Trim()
                    : r.Explanation;
                return new ScopeTestLink(r.RelationKey, r.Scope, kind, x.Test.IdentityKey, x.Test.DisplayName.Length > 0 ? x.Test.DisplayName : x.Test.IdentityKey,
                    locations.GetValueOrDefault(x.Test.Id), r.Resolution, r.Confidence, r.RuleId, explanation, r.ReviewState, evidence,
                    "Execution evidence not linked — a discovered test is not an executed test.");
            }).OrderBy(t => t.Kind).ThenBy(t => t.TestName, StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Data.Common.DbException) { return null; }
    }

    private static readonly HashSet<string> GenericContractNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "schema", "schema.graphql", "schema.json", "openapi", "openapi.yaml", "openapi.yml", "openapi.json", "swagger.json", "graphql-schema", "queries", "mutations",
    };

    /// <summary>
    /// The schemas and contracts associated with one item, from the canonical contract evidence of the same environment (Contract ← ContractVersion ←
    /// ContractOccurrence, Service —References→ ContractVersion):
    /// <list type="bullet">
    /// <item>Confirmed / Explicit: a canonical relation between the item and the contract (a person's confirmation, or an explicit/derived relation).</item>
    /// <item>Explicit: the item's own text names the contract (its name, namespace or file name — never a generic name such as "schema.graphql").</item>
    /// <item>Candidate: the contract belongs to the item's module (its file lies under the module, or a service of the module references it). The item
    /// does not say it uses it: a suggestion, never a statement.</item>
    /// </list>
    /// No association carries validation: an existing schema is not a validation result. Null when no contract evidence is interpreted there.
    /// </summary>
    private async Task<IReadOnlyList<ScopeSchemaLink>?> SchemaLinksAsync(Raw item, string scopedKey, ScopeEvidenceContext evidence, CancellationToken ct)
    {
        if (canonical is null) return null;
        var (allEnvironments, environment) = (evidence.CrossEnvironment, evidence.Environment);
        string[] types = [nameof(CanonicalEntityType.Contract), nameof(CanonicalEntityType.ContractVersion), nameof(CanonicalEntityType.ContractOccurrence), nameof(CanonicalEntityType.Service)];
        try
        {
            var entities = await canonical.Entities.AsNoTracking().Where(e => (allEnvironments || e.Scope == environment) && types.Contains(e.EntityType)).ToListAsync(ct);
            var contracts = entities.Where(e => e.EntityType == nameof(CanonicalEntityType.Contract)).ToList();
            if (contracts.Count == 0) return null;
            var byId = entities.ToDictionary(e => e.Id);
            var itemEntities = await canonical.Entities.AsNoTracking().Where(e => (allEnvironments || e.Scope == environment) && e.IdentityKey == scopedKey
                && (e.EntityType == nameof(CanonicalEntityType.Requirement) || e.EntityType == nameof(CanonicalEntityType.UserStory))).Select(e => e.Id).ToListAsync(ct);
            var ids = byId.Keys.Concat(itemEntities).ToList();
            var relations = await canonical.Relations.AsNoTracking().Where(r => r.Active && (allEnvironments || r.Scope == environment)
                && (ids.Contains(r.SourceEntityId) || ids.Contains(r.TargetEntityId))).ToListAsync(ct);
            var occurrenceEntityIds = entities.Where(e => e.EntityType == nameof(CanonicalEntityType.ContractOccurrence)).Select(e => e.Id).ToList();
            var occurrences = (await canonical.Occurrences.AsNoTracking().Where(o => occurrenceEntityIds.Contains(o.EntityId)).ToListAsync(ct))
                .GroupBy(o => o.EntityId).ToDictionary(g => g.Key, g => g.OrderBy(o => o.OccurrenceKey, StringComparer.Ordinal).ToList());

            List<Guid> Sources(string type, IEnumerable<Guid> targets) { var set = targets.ToHashSet(); return [.. relations.Where(r => r.RelationType == type && set.Contains(r.TargetEntityId)).Select(r => r.SourceEntityId)]; }
            var itemIds = itemEntities.ToHashSet();
            var module = item.Feature.Module;
            var links = new Dictionary<string, ScopeSchemaLink>(StringComparer.Ordinal);
            foreach (var c in contracts)
            {
                var versions = Sources(nameof(CanonicalRelationType.BelongsTo), [c.Id]).Where(byId.ContainsKey).Select(v => byId[v]).ToList();
                var versionIds = versions.Select(v => v.Id).ToHashSet();
                var found = Sources(nameof(CanonicalRelationType.OccurrenceOf), versionIds).SelectMany(o => occurrences.GetValueOrDefault(o) ?? []).ToList();
                var services = Sources(nameof(CanonicalRelationType.References), versionIds).Where(byId.ContainsKey).Select(s => byId[s].DisplayName).ToList();
                var (kind, ns) = ContractAttributes(c.AttributesJson);
                var version = versions.Select(v => v.Version).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

                // 1. A canonical relation between the item and the contract (or one of its versions).
                var stated = relations.Where(r => r.ReviewState != nameof(RelationReviewState.Rejected)
                    && (itemIds.Contains(r.SourceEntityId) && (r.TargetEntityId == c.Id || versionIds.Contains(r.TargetEntityId))
                        || itemIds.Contains(r.TargetEntityId) && (r.SourceEntityId == c.Id || versionIds.Contains(r.SourceEntityId)))).ToList();
                // 2. The item's text names the contract.
                var named = NamesOf(c, ns, found).FirstOrDefault(n => Regex.IsMatch(item.Text, $@"(?<![\w.]){Regex.Escape(n)}(?![\w])", RegexOptions.IgnoreCase));
                // 3. The contract belongs to the item's module.
                var owned = module.Length > 0 && (found.Any(o => string.Equals(o.OccurrenceKey.Split('/', 2)[0], module, StringComparison.OrdinalIgnoreCase))
                    || services.Any(s => s.Split('.').Any(seg => string.Equals(seg, module, StringComparison.OrdinalIgnoreCase))));
                if (stated.Count == 0 && named is null && !owned) continue;

                var occurrence = found.FirstOrDefault(o => named is not null && o.OccurrenceKey.EndsWith(named, StringComparison.OrdinalIgnoreCase))
                    ?? found.FirstOrDefault(o => string.Equals(o.OccurrenceKey.Split('/', 2)[0], module, StringComparison.OrdinalIgnoreCase)) ?? found.FirstOrDefault();
                var more = found.Count > 1 ? $" Found in {found.Count} places; showing {occurrence?.OccurrenceKey}." : "";
                ScopeSchemaLinkKind linkKind; string relationship, confidence, rule, explanation;
                if (stated.FirstOrDefault(r => r.ReviewState == nameof(RelationReviewState.Confirmed)) is { } confirmed)
                    (linkKind, relationship, confidence, rule, explanation) = (ScopeSchemaLinkKind.Confirmed, $"{confirmed.RelationType} (canonical relation)", "Confirmed by a person", confirmed.RuleId, confirmed.Explanation);
                else if (stated.FirstOrDefault(r => r.Resolution is nameof(RelationResolution.Explicit) or nameof(RelationResolution.Derived)) is { } explicitRelation)
                    (linkKind, relationship, confidence, rule, explanation) = (ScopeSchemaLinkKind.Explicit, $"{explicitRelation.RelationType} (canonical relation)", explicitRelation.Confidence, explicitRelation.RuleId, explicitRelation.Explanation);
                else if (named is not null)
                    (linkKind, relationship, confidence, rule, explanation) = (ScopeSchemaLinkKind.Explicit, "Named in the requirement", "High", "schema.named-in-requirement",
                        $"The {FunctionalQualityScopeText.TypeLabel(item.Type).ToLowerInvariant()} names “{named}”.");
                else if (stated.FirstOrDefault() is { } candidateRelation)
                    (linkKind, relationship, confidence, rule, explanation) = (ScopeSchemaLinkKind.Candidate, $"{candidateRelation.RelationType} (canonical candidate)", candidateRelation.Confidence, candidateRelation.RuleId, candidateRelation.Explanation);
                else
                    (linkKind, relationship, confidence, rule, explanation) = (ScopeSchemaLinkKind.Candidate, $"Same module ({module})", "Low", "schema.module-ownership",
                        $"The {module} module's source holds this contract{(services.Count > 0 ? $" (referenced by {string.Join(", ", services.Distinct().Take(3))})" : "")}; the item does not name it — confirm before relying on it.");
                links[c.IdentityKey] = new(c.IdentityKey, c.DisplayName, kind, ns, version, occurrence?.Detail, Guid.TryParse(occurrence?.SourceSnapshotId, out var snapshotId) ? snapshotId : null,
                    occurrence?.OccurrenceKey, linkKind, relationship, confidence, rule, explanation + more, ScopeSchemaValidation.NotPerformed,
                    "No validation result is linked to this item — an existing schema is not a validation.", c.Scope);
            }
            return [.. links.Values.OrderBy(l => l.Kind == ScopeSchemaLinkKind.Confirmed ? 0 : l.Kind == ScopeSchemaLinkKind.Explicit ? 1 : 2).ThenBy(l => l.Name, StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Data.Common.DbException) { return null; }
    }

    private static (string Kind, string? Namespace) ContractAttributes(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            string? Str(string name) => doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            return (Str("kind") ?? "Contract", Str("namespace"));
        }
        catch (JsonException) { return ("Contract", null); }
    }

    /// <summary>Names a requirement may use for a contract: its display name, namespace (and its last segment), identity name and file names —
    /// specific ones only (at least 8 characters with a separator; generic names such as "schema.graphql" identify nothing).</summary>
    private static IEnumerable<string> NamesOf(CanonicalEntityRecord contract, string? ns, IEnumerable<CanonicalOccurrenceRecord> occurrences)
    {
        var names = new List<string?> { contract.DisplayName, ns, ns?.TrimEnd('/').Split('/').LastOrDefault(), contract.IdentityKey.Split('|').LastOrDefault() };
        names.AddRange(occurrences.Select(o => o.OccurrenceKey.Split('/').LastOrDefault()));
        return names.OfType<string>().Select(n => n.Trim()).Where(n => n.Length >= 8 && n.IndexOfAny(['.', '-', ' ', '_']) > 0 && !GenericContractNames.Contains(n))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(n => n.Length);
    }

    private static IReadOnlyList<ScopeTestEvidence> ParseEvidence(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "[]" : json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];
            static string? Str(JsonElement e, string name) =>
                e.EnumerateObject().FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) is { Value.ValueKind: JsonValueKind.String } p ? p.Value.GetString() : null;
            return [.. doc.RootElement.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object)
                .Select(e => new ScopeTestEvidence(Str(e, "kind") ?? "", Str(e, "reference") ?? "", Str(e, "detail")))];
        }
        catch (JsonException) { return []; }
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
