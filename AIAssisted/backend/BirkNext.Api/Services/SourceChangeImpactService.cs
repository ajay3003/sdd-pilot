using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.Api.Services.SourceAnalysis;
using BirkNext.Dependencies;
using BirkNext.Integrations;
using BirkNext.SourceArchitecture;
using BirkNext.SourceObservability;
using BirkNext.SourceDomains;
using BirkNext.DatabaseArchitecture;
using BirkNext.SourceImpact;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services;

/// <summary>Builds technical impact from exact Source Analysis snapshots, then enriches it with persisted QA traceability.</summary>
public sealed class SourceChangeImpactService(AppDbContext db, IReviewSourceEvidenceProvider sources)
{
    public async Task<IReadOnlyList<ImpactSnapshotOption>> ListSnapshotsAsync(string environmentId, CancellationToken ct)
    {
        if (!sources.SourceAnalysisEnabled) return [];
        var snapshots = await sources.ListAsync(environmentId, ct);
        return snapshots.Select(s => new ImpactSnapshotOption(s.Id, ReviewSourceEvidenceProvider.Identity(s).DisplayName,
            s.Archive.FileName, s.Archive.Sha256, s.AnalyzedAt, s.Status.ToString())).ToList();
    }

    public async Task<ImpactSnapshotList> SnapshotListAsync(string environmentId, CancellationToken ct) =>
        new(sources.SourceAnalysisEnabled, sources.SourceAnalysisEnabled ? (await ListSnapshotsAsync(environmentId, ct)).ToList() : []);

    public async Task<SourceChangeImpactReport?> AnalyzeAsync(SourceChangeImpactRequest request, CancellationToken ct)
    {
        if (!sources.SourceAnalysisEnabled || string.IsNullOrWhiteSpace(request.EnvironmentId) ||
            request.BaselineSnapshotId == Guid.Empty || request.TargetSnapshotId == Guid.Empty || request.BaselineSnapshotId == request.TargetSnapshotId)
            return null;
        var before = await sources.ResolveAsync(request.EnvironmentId, request.BaselineSnapshotId, ct);
        var after = await sources.ResolveAsync(request.EnvironmentId, request.TargetSnapshotId, ct);
        if (before is null || after is null || ReviewSourceEvidenceProvider.Identity(before).Key != ReviewSourceEvidenceProvider.Identity(after).Key)
            return null;

        var limitations = new List<string>();
        var changes = Compare(before, after, limitations);
        if (changes.Count == 0 && !string.Equals(before.Archive.Sha256, after.Archive.Sha256, StringComparison.OrdinalIgnoreCase))
            changes.Add(new ImpactChange("unclassified-snapshot-change", ImpactChangeDomain.Unclassified, ImpactChangeKind.Changed,
                "Unclassified source change", after.Id.ToString(), "Unclassified source change", "Snapshot fingerprints differ, but no supported structured comparison identified the change. Impact is unknown; review required.", []));
        var impacts = BuildTechnicalImpacts(before, after, changes, limitations);

        // Traceability is deliberately a second stage: no links can remove or downgrade a technical impact.
        var files = impacts.SelectMany(i => i.SourceFiles).Select(NormalizePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var codeLinks = string.IsNullOrWhiteSpace(request.ProjectId)
            ? []
            : await (from link in db.CodeLinks
                     join file in db.CodeFiles on link.CodeFileId equals file.Id
                     where link.ProjectId == request.ProjectId && file.ProjectId == request.ProjectId
                     select new { link.ScenarioId, link.ScenarioKind, file.FilePath }).ToListAsync(ct);
        var scenarioIds = codeLinks.Where(l => files.Contains(NormalizePath(l.FilePath))).Select(l => l.ScenarioId).Distinct().ToList();
        var scenarios = string.IsNullOrWhiteSpace(request.ProjectId) ? [] : await db.Scenarios.Where(s => s.ProjectId == request.ProjectId && scenarioIds.Contains(s.Id)).ToListAsync(ct);
        var byId = scenarios.ToDictionary(s => s.Id);
        var linked = codeLinks.Where(l => files.Contains(NormalizePath(l.FilePath)) && byId.ContainsKey(l.ScenarioId))
            .GroupBy(l => l.ScenarioId).ToDictionary(g => g.Key, g => g.Select(x => x.FilePath).Distinct(StringComparer.OrdinalIgnoreCase).ToList());

        var requirements = linked.Keys.Where(id => byId[id].Kind == ScenarioKind.Requirement).ToHashSet();
        var requirementItems = requirements.Select(id => new ImpactTraceabilityItem(id, "Requirement", byId[id].Title,
            $"Source file linked through Code Traceability: {string.Join(", ", linked[id])}", linked[id])).ToList();
        var requirementTestLinks = string.IsNullOrWhiteSpace(request.ProjectId) ? [] : await db.TraceLinks.Where(t => t.ProjectId == request.ProjectId && t.LinkType == TraceLinkType.Covers &&
            t.SourceKind == TraceLinkArtifactKind.Scenario && t.TargetKind == TraceLinkArtifactKind.Scenario && requirements.Contains(t.TargetId)).ToListAsync(ct);
        var testIds = linked.Keys.Where(id => byId[id].Kind == ScenarioKind.Test).ToHashSet();
        foreach (var l in requirementTestLinks) testIds.Add(l.SourceId);
        var testScenarios = string.IsNullOrWhiteSpace(request.ProjectId) ? [] : await db.Scenarios.Where(s => s.ProjectId == request.ProjectId && testIds.Contains(s.Id) && s.Kind == ScenarioKind.Test).ToListAsync(ct);
        var requirementById = requirementItems.ToDictionary(x => x.ScenarioId);
        var recommendations = testScenarios.Select(t =>
        {
            var direct = linked.TryGetValue(t.Id, out var testFiles);
            var reqs = requirementTestLinks.Where(l => l.SourceId == t.Id).Select(l => l.TargetId).Where(requirementById.ContainsKey).Distinct().ToList();
            var reasons = new List<string>();
            if (direct) reasons.Add($"Test source file linked to impacted source: {string.Join(", ", testFiles!)}");
            reasons.AddRange(reqs.Select(id => $"Covers impacted requirement {requirementById[id].Title}"));
            return new ImpactTestRecommendation(t.Id, t.Title, "Test scenario", string.Join("; ", reasons), reasons);
        }).Where(x => x.Paths.Count > 0).DistinctBy(x => x.TestId).ToList();

        var coveredRequirementIds = requirementTestLinks.Select(l => l.TargetId).ToHashSet();
        var coverageGaps = impacts.Count == 0 ? [] : requirements.Count == 0
            ? ["Technical impact exists, but no linked requirement was found. Traceability is incomplete."]
            : requirementItems.Where(r => !coveredRequirementIds.Contains(r.ScenarioId)).Select(r => $"Impacted requirement {r.Title} has no linked test.").ToList();
        if (impacts.Count > 0 && recommendations.Count == 0) coverageGaps.Add("Technical impact exists, but no linked test was found. Impact coverage gap: needs review.");
        if (string.IsNullOrWhiteSpace(request.ProjectId) && impacts.Count > 0) limitations.Add("No workspace project was selected; technical impact was calculated without requirement or test traceability.");
        else if (scenarios.Count == 0 && impacts.Count > 0) limitations.Add("No Code Traceability links matched the impacted source locations; technical impact remains valid.");

        var identityBefore = ReviewSourceEvidenceProvider.Identity(before);
        var identityAfter = ReviewSourceEvidenceProvider.Identity(after);
        return new(before.Id, identityBefore.DisplayName, before.Archive.FileName, before.Archive.Sha256, before.AnalyzedAt,
            after.Id, identityAfter.DisplayName, after.Archive.FileName, after.Archive.Sha256, after.AnalyzedAt,
            changes, impacts, requirementItems, [], recommendations, coverageGaps.Distinct().ToList(), limitations.Distinct().ToList(),
            "Not calculated for source-change impact; no source-change risk score is currently defined.");
    }

    private static List<ImpactChange> Compare(IqrSourceSnapshot before, IqrSourceSnapshot after, List<string> limitations)
    {
        var output = new List<ImpactChange>();
        if (before.Status != SourceAnalysisStatus.Ready || after.Status != SourceAnalysisStatus.Ready)
            limitations.Add("One or both Source Analysis snapshots are partial; unobserved domains may still have impact.");
        if (before.EvidenceDomains?.Domains.Any(d => d.Status is SourceDomainStatus.Partial or SourceDomainStatus.Unsupported or SourceDomainStatus.FailedAnalysis) == true ||
            after.EvidenceDomains?.Domains.Any(d => d.Status is SourceDomainStatus.Partial or SourceDomainStatus.Unsupported or SourceDomainStatus.FailedAnalysis) == true)
            limitations.Add("One or more source-evidence domains are partial, unsupported, or failed; impact is limited to available evidence.");
        if (before.Architecture is { } ba && after.Architecture is { } aa)
            output.AddRange(ArchitectureDiff.Compare(ba, aa).Select((c, i) => new ImpactChange($"architecture:{i}:{c.Key}", ImpactChangeDomain.Architecture,
                Enum.Parse<ImpactChangeKind>(c.Kind.ToString()), c.Area, c.Key, c.Name, c.Detail,
                ArchitectureFiles(after.Architecture, c.Area, c.Key).Concat(ArchitectureFiles(before.Architecture, c.Area, c.Key)).Distinct(StringComparer.OrdinalIgnoreCase).ToList())));
        else limitations.Add("Architecture comparison unavailable for one or both selected snapshots.");

        if (before.EvidenceDomains is { } be && after.EvidenceDomains is { } ae)
            output.AddRange(SourceEvidenceDiff.Compare(be, ae).Select((c, i) => new ImpactChange($"{c.Domain}:{i}:{c.Key}", c.Domain switch
                { SourceEvidenceDomain.Contracts => ImpactChangeDomain.Contracts, SourceEvidenceDomain.Configuration => ImpactChangeDomain.Configuration,
                  SourceEvidenceDomain.Infrastructure => ImpactChangeDomain.Infrastructure, SourceEvidenceDomain.CiCd => ImpactChangeDomain.CiCd, _ => ImpactChangeDomain.Dependencies },
                Enum.Parse<ImpactChangeKind>(c.Kind.ToString()), c.Area, c.Key, c.Name, c.Detail,
                EvidenceFiles(after.EvidenceDomains, c.Domain, c.Key).Concat(EvidenceFiles(before.EvidenceDomains, c.Domain, c.Key)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                c.ContractChange == ContractChangeClass.None ? null : c.ContractChange.ToString())));
        else limitations.Add("Infrastructure, configuration, CI/CD and contract comparisons unavailable for one or both selected snapshots.");

        if (before.DatabaseArchitecture is { } bd && after.DatabaseArchitecture is { } ad)
            output.AddRange(DatabaseSchemaComparison.Compare(bd, ad).Select((c, i) => new ImpactChange($"database:{i}:{c.Object}", ImpactChangeDomain.Database,
                c.Kind.StartsWith("Added", StringComparison.Ordinal) ? ImpactChangeKind.Added : c.Kind.StartsWith("Removed", StringComparison.Ordinal) ? ImpactChangeKind.Removed : ImpactChangeKind.Changed,
                c.Kind, c.Object, c.Object, c.Detail, DatabaseFiles(after.DatabaseArchitecture, c.Object).Concat(DatabaseFiles(before.DatabaseArchitecture, c.Object)).Distinct(StringComparer.OrdinalIgnoreCase).ToList())));
        else limitations.Add("Database comparison unavailable for one or both selected snapshots.");

        if (before.Observability is { } bo && after.Observability is { } ao)
            output.AddRange(SourceObservabilityComparison.Compare(bo, ao).Select((c, i) => new ImpactChange($"observability:{i}:{c.EntityId}:{c.Detail}", ImpactChangeDomain.Observability,
                c.Kind == "Added" ? ImpactChangeKind.Added : c.Kind == "No longer found" ? ImpactChangeKind.NoLongerFound : ImpactChangeKind.Changed,
                "Observability evidence", $"{c.EntityId ?? "snapshot"}|{c.Subject}|{c.Detail}", c.Subject, c.Detail,
                ArchitectureFiles(after.Architecture, "Component", c.EntityId ?? "").Concat(ArchitectureFiles(before.Architecture, "Component", c.EntityId ?? "")).Distinct(StringComparer.OrdinalIgnoreCase).ToList())));
        else limitations.Add("Observability comparison unavailable for one or both selected snapshots.");
        if (before.DependencyEvidence is not null || after.DependencyEvidence is not null)
        {
            static string Key(DeclaredDependency d) => $"{d.Manager}|{d.PackageName}|{d.OwnerFile}";
            var old = (before.DependencyEvidence?.Dependencies ?? []).GroupBy(Key).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var next = (after.DependencyEvidence?.Dependencies ?? []).GroupBy(Key).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            foreach (var key in next.Keys.Union(old.Keys, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
            {
                old.TryGetValue(key, out var b); next.TryGetValue(key, out var a);
                if (b is null || a is null || b.CurrentValue != a.CurrentValue)
                {
                    var item = a ?? b!;
                    output.Add(new ImpactChange($"dependency:{key}", ImpactChangeDomain.Dependencies,
                        b is null ? ImpactChangeKind.Added : a is null ? ImpactChangeKind.Removed : ImpactChangeKind.Changed,
                        "Package dependency", key, item.PackageName, b is null ? $"Declared at {item.CurrentValue}" : a is null ? $"No longer declared (was {item.CurrentValue})" : $"Version declaration changed: {b.CurrentValue} → {a.CurrentValue}",
                        new[] { b?.OwnerFile, a?.OwnerFile }.Where(f => !string.IsNullOrWhiteSpace(f)).Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList()));
                }
            }
        }
        return output;
    }

    private static List<TechnicalImpactItem> BuildTechnicalImpacts(IqrSourceSnapshot before, IqrSourceSnapshot after, List<ImpactChange> changes, List<string> limitations)
    {
        var items = new Dictionary<string, TechnicalImpactItem>(StringComparer.OrdinalIgnoreCase);
        void Add(string type, string id, string name, ImpactChangeDomain domain, TechnicalImpactLevel level, string state, string reason,
            ImpactChange change, string relationship, string basis, IEnumerable<string>? files = null)
        {
            if (string.IsNullOrWhiteSpace(id)) return;
            var key = $"{type}:{id}";
            var baseLevel = change.Domain is ImpactChangeDomain.CiCd or ImpactChangeDomain.Observability or ImpactChangeDomain.Unclassified || state is "Unresolved" or "Conflict"
                ? TechnicalImpactLevel.NeedsReview : state == "Inferred" ? TechnicalImpactLevel.Potential : TechnicalImpactLevel.Direct;
            var path = new List<ImpactPathStep> { new(change.EntityType, change.EntityKey, change.Name, "Source change", "Confirmed", change.Detail) };
            if (type != change.EntityType || id != change.EntityKey) path.Add(new(type, id, name, relationship, state, basis));
            var sourceFiles = (files ?? []).Where(f => !string.IsNullOrWhiteSpace(f)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (items.TryGetValue(key, out var old))
                items[key] = old with { Level = Stronger(old.Level, level), EvidenceState = Weaker(old.EvidenceState, state), SourceChangeIds = old.SourceChangeIds.Append(change.Id).Distinct().ToList(), SourceFiles = old.SourceFiles.Concat(sourceFiles).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                    Path = old.Path.Concat(path.Skip(1)).Distinct().ToList(), Reason = string.Join("; ", new[] { old.Reason, reason }.Distinct()) };
            else items[key] = new(key, type, id, name, domain, type == change.EntityType ? baseLevel : level, state, reason, path, [change.Id], sourceFiles);
        }

        foreach (var c in changes)
        {
            var changeState = ChangeEvidenceState(c, before, after);
            Add(c.EntityType, c.EntityKey, c.Name, c.Domain, TechnicalImpactLevel.Direct, changeState, $"{c.Kind} source change: {c.Detail}", c, "Changed entity", "Compared from selected immutable snapshots.", c.SourceFiles);
            var removed = c.Kind is ImpactChangeKind.Removed or ImpactChangeKind.NoLongerFound;
            var arch = removed ? before.Architecture : after.Architecture;
            var evidence = removed ? before.EvidenceDomains : after.EvidenceDomains;
            if (c.Domain == ImpactChangeDomain.Architecture && arch is not null)
            {
                if (c.EntityType == "Component")
                {
                    var component = arch.Components.FirstOrDefault(x => x.Id == c.EntityKey);
                    if (component is not null)
                        foreach (var dep in arch.Dependencies.Where(d => d.FromComponentId == component.Id && d.ToId is not null))
                            Add("Dependency", dep.Id, dep.TargetReference ?? dep.ToId!, c.Domain, dep.EvidenceState is ArchitectureEvidenceState.Confirmed or ArchitectureEvidenceState.StronglySupported ? TechnicalImpactLevel.Indirect : TechnicalImpactLevel.Potential,
                                dep.EvidenceState.ToString(), "Changed component has an explicit source dependency.", c, "Component dependency", dep.DependencyType.ToString(), dep.Evidence.Select(e => e.File));
                }
                if (c.EntityType == "Dependency")
                {
                    var d = arch.Dependencies.FirstOrDefault(x => x.Id == c.EntityKey);
                    if (d is not null) Add("Component", d.FromComponentId, arch.Components.FirstOrDefault(x => x.Id == d.FromComponentId)?.Name ?? d.FromComponentId, c.Domain,
                        d.EvidenceState is ArchitectureEvidenceState.Confirmed or ArchitectureEvidenceState.StronglySupported ? TechnicalImpactLevel.Direct : TechnicalImpactLevel.Potential,
                        d.EvidenceState.ToString(), "The changed dependency is declared by this component.", c, "Declared by", d.DependencyType.ToString(), d.Evidence.Select(e => e.File));
                    if (d?.ToId is { } toId && arch.Components.FirstOrDefault(x => x.Id == toId) is { } target)
                        Add("Component", target.Id, target.Name, c.Domain, d.EvidenceState is ArchitectureEvidenceState.Confirmed or ArchitectureEvidenceState.StronglySupported ? TechnicalImpactLevel.Direct : TechnicalImpactLevel.Potential,
                            d.EvidenceState.ToString(), "The changed dependency explicitly targets this component.", c, "Dependency target", d.DependencyType.ToString(), target.Evidence.Select(e => e.File));
                }
                if (c.EntityType == "Messaging channel")
                {
                    var channel = arch.MessagingChannels.FirstOrDefault(x => x.Id == c.EntityKey);
                    if (channel is not null)
                        foreach (var endpoint in channel.Producers.Concat(channel.Consumers)) Add("Component", endpoint.ComponentId,
                            arch.Components.FirstOrDefault(x => x.Id == endpoint.ComponentId)?.Name ?? endpoint.ComponentId, c.Domain,
                            endpoint.State is ArchitectureEvidenceState.Confirmed or ArchitectureEvidenceState.StronglySupported ? TechnicalImpactLevel.Direct : TechnicalImpactLevel.Potential,
                            endpoint.State.ToString(), "Component is declared as a producer or consumer of the changed channel.", c, "Messaging endpoint", endpoint.Role.ToString(), endpoint.Evidence.Select(e => e.File));
                }
                if (c.EntityType == "Datastore")
                {
                    var store = arch.DataStores.FirstOrDefault(x => x.Id == c.EntityKey);
                    if (store is not null) foreach (var componentId in store.ReferencedByComponents) Add("Component", componentId,
                        arch.Components.FirstOrDefault(x => x.Id == componentId)?.Name ?? componentId, c.Domain, TechnicalImpactLevel.Direct, store.Confidence.ToString(),
                        "Component explicitly references the changed datastore.", c, "Datastore reference", store.Usage, store.Evidence.Select(e => e.File));
                }
                if (c.EntityType == "External system")
                    foreach (var d in arch.Dependencies.Where(d => d.ToId == c.EntityKey))
                        Add("Component", d.FromComponentId, arch.Components.FirstOrDefault(x => x.Id == d.FromComponentId)?.Name ?? d.FromComponentId, c.Domain,
                            d.EvidenceState is ArchitectureEvidenceState.Confirmed or ArchitectureEvidenceState.StronglySupported ? TechnicalImpactLevel.Direct : TechnicalImpactLevel.Potential,
                            d.EvidenceState.ToString(), "Component dependency explicitly targets the changed external system.", c, "External-system dependency", d.DependencyType.ToString(), d.Evidence.Select(e => e.File));
            }
            if (c.Domain == ImpactChangeDomain.Contracts && evidence is not null)
                foreach (var link in evidence.CrossDomain.Links.Where(l => (l.Type is SourceEvidenceLinkType.ContractProducedByComponent or SourceEvidenceLinkType.ContractConsumedByComponent) && l.FromId == $"contract:{c.EntityKey.Split('|')[0]}" && l.ToId is not null))
                    Add("Component", link.ToId!.Replace("arch:component:", "", StringComparison.Ordinal), link.ToLabel, c.Domain, link.Type == SourceEvidenceLinkType.ContractConsumedByComponent ? TechnicalImpactLevel.Indirect :
                        link.State is ArchitectureEvidenceState.Confirmed or ArchitectureEvidenceState.StronglySupported ? TechnicalImpactLevel.Direct : TechnicalImpactLevel.Potential,
                        link.State.ToString(), "Source Analysis links this contract to a producer or consumer component.", c, SourceDomainText.Label(link.Type), link.Basis,
                        arch?.Components.FirstOrDefault(x => $"arch:component:{x.Id}" == link.ToId)?.Evidence.Select(e => e.File));
            if (c.Domain is ImpactChangeDomain.Configuration or ImpactChangeDomain.Infrastructure && evidence is not null)
            {
                var configId = c.Domain == ImpactChangeDomain.Configuration
                    ? evidence.Configuration.Entries.FirstOrDefault(x => $"{x.File}|{x.NormalizedKey}" == c.EntityKey)?.Id
                    : null;
                foreach (var link in evidence.CrossDomain.Links.Where(l => l.ToId is not null &&
                    (l.FromId == $"config:{configId}" || l.ToId == c.EntityKey || l.ToId == $"infra:{c.EntityKey}")))
                {
                    Add("Infrastructure/Configuration relationship", link.ToId ?? link.FromId, link.ToLabel, c.Domain,
                        link.State is ArchitectureEvidenceState.Confirmed or ArchitectureEvidenceState.StronglySupported ? TechnicalImpactLevel.Direct : TechnicalImpactLevel.Potential,
                        link.State.ToString(), "Existing Source Analysis cross-domain relationship connects this change to the technical entity.", c, SourceDomainText.Label(link.Type), link.Basis);
                    if (arch is not null && link.Type == SourceEvidenceLinkType.ApplicationUsesInfrastructureResource)
                    {
                        var channelId = link.FromId.Replace("arch:channel:", "", StringComparison.Ordinal);
                        var channel = arch.MessagingChannels.FirstOrDefault(x => x.Id == channelId);
                        if (channel is not null) foreach (var endpoint in channel.Producers.Concat(channel.Consumers))
                            Add("Component", endpoint.ComponentId, arch.Components.FirstOrDefault(x => x.Id == endpoint.ComponentId)?.Name ?? endpoint.ComponentId, c.Domain,
                                endpoint.State is ArchitectureEvidenceState.Confirmed or ArchitectureEvidenceState.StronglySupported ? TechnicalImpactLevel.Indirect : TechnicalImpactLevel.Potential,
                                endpoint.State.ToString(), "A component uses the messaging channel linked to the changed infrastructure resource.", c,
                                "Component → messaging channel → infrastructure resource", link.Basis, endpoint.Evidence.Select(e => e.File));
                    }
                }
                if (c.Domain == ImpactChangeDomain.Configuration && arch is not null)
                    foreach (var config in arch.ConfigurationReferences.Where(x => x.Key.Equals(evidence.Configuration.Entries.FirstOrDefault(e => $"{e.File}|{e.NormalizedKey}" == c.EntityKey)?.Key, StringComparison.OrdinalIgnoreCase)))
                        Add("Component", config.ComponentId, arch.Components.FirstOrDefault(x => x.Id == config.ComponentId)?.Name ?? config.ComponentId,
                            c.Domain, TechnicalImpactLevel.Potential, "Inferred", "Architecture evidence says this component reads the changed configuration key; source/runtime behavior is not asserted.",
                            c, "Configuration reference", config.Purpose, config.Files);
            }
            var selectedDatabase = removed ? before.DatabaseArchitecture : after.DatabaseArchitecture;
            if (c.Domain == ImpactChangeDomain.Database && selectedDatabase is { } db && arch is not null)
            {
                var table = db.Databases.SelectMany(x => x.Schemas).SelectMany(x => x.Tables).FirstOrDefault(t => c.Name.StartsWith(t.LogicalName, StringComparison.OrdinalIgnoreCase));
                if (table is not null)
                {
                    var ownerDatabase = db.Databases.FirstOrDefault(d => d.Schemas.SelectMany(s => s.Tables).Any(t => t.Id == table.Id));
                    foreach (var store in arch.DataStores.Where(s => ownerDatabase is not null && s.DatabaseModelId == ownerDatabase.Id))
                        foreach (var componentId in store.ReferencedByComponents) Add("Component", componentId, arch.Components.FirstOrDefault(x => x.Id == componentId)?.Name ?? componentId,
                            c.Domain, c.EntityType.Contains("index", StringComparison.OrdinalIgnoreCase) ? TechnicalImpactLevel.Potential : TechnicalImpactLevel.Direct,
                            store.Confidence.ToString(), c.EntityType.Contains("index", StringComparison.OrdinalIgnoreCase) ? "Index change is operational/performance relevant; functional test impact is not assumed." : "Component references the database model containing the changed table.",
                            c, "Database model reference", store.Usage, store.Evidence.Select(e => e.File));
                }
            }
            if (c.Domain == ImpactChangeDomain.CiCd)
                Add("Pipeline", c.EntityKey, c.Name, c.Domain, TechnicalImpactLevel.NeedsReview, "Unresolved", "Pipeline/deployment source changed; release impact needs review.", c, "Pipeline change", "No application behavior is inferred.", c.SourceFiles);
            if (c.Domain == ImpactChangeDomain.Dependencies && arch is not null)
            {
                var dependencyEvidence = removed ? before.DependencyEvidence : after.DependencyEvidence;
                var dep = (dependencyEvidence?.Dependencies ?? []).FirstOrDefault(d => $"{d.Manager}|{d.PackageName}|{d.OwnerFile}".Equals(c.EntityKey, StringComparison.OrdinalIgnoreCase));
                foreach (var component in arch.Components.Where(x => dep is not null && dep.ReferencedBy.Contains(x.SourceProject, StringComparer.OrdinalIgnoreCase)))
                    Add("Component", component.Id, component.Name, c.Domain, TechnicalImpactLevel.Potential, "Inferred", "The declared package is referenced by this project; compatibility or vulnerability is not inferred.", c,
                        "Project declares dependency", dep!.PackageName, component.Evidence.Select(e => e.File));
            }
            if (c.Domain == ImpactChangeDomain.Observability && arch is not null)
            {
                var id = c.EntityKey.Split('|')[0];
                var component = arch.Components.FirstOrDefault(x => x.Id == id);
                if (component is not null) Add("Component", component.Id, component.Name, c.Domain, TechnicalImpactLevel.Potential, "Inferred",
                    "Observability source changed for this component; operational validation may be relevant, functional test impact is not assumed.", c,
                    "Observability evidence names component", c.Detail, component.Evidence.Select(e => e.File));
            }
        }
        if (changes.Count == 0) limitations.Add("No structured changes were produced by the supported Source Analysis comparisons. This does not prove no impact.");
        return items.Values.OrderBy(i => i.Level).ThenBy(i => i.Domain).ThenBy(i => i.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IEnumerable<string> ArchitectureFiles(ArchitectureSnapshot? snapshot, string area, string key) => snapshot is null ? [] : area switch
    {
        "Component" => snapshot.Components.FirstOrDefault(x => x.Id == key)?.Evidence.Select(e => e.File) ?? [],
        "Dependency" => snapshot.Dependencies.FirstOrDefault(x => x.Id == key)?.Evidence.Select(e => e.File) ?? [],
        "Messaging channel" => snapshot.MessagingChannels.FirstOrDefault(x => x.Id == key)?.Evidence.Select(e => e.File) ?? [],
        "Datastore" => snapshot.DataStores.FirstOrDefault(x => x.Id == key)?.Evidence.Select(e => e.File) ?? [],
        "External system" => snapshot.ExternalSystems.FirstOrDefault(x => x.Id == key)?.Evidence.Select(e => e.File) ?? [],
        _ => [],
    };
    private static IEnumerable<string> EvidenceFiles(SourceEvidenceDomainsSnapshot? evidence, SourceEvidenceDomain domain, string key)
    {
        if (evidence is null) return [];
        if (domain == SourceEvidenceDomain.Infrastructure) return evidence.Infrastructure.Resources.FirstOrDefault(x => x.Id == key)?.File is { } f ? [f] : [];
        if (domain == SourceEvidenceDomain.Configuration) return evidence.Configuration.Entries.FirstOrDefault(x => $"{x.File}|{x.NormalizedKey}" == key)?.File is { } f ? [f] : [];
        if (domain == SourceEvidenceDomain.Contracts)
        {
            var id = key.Split('|')[0]; return evidence.Contracts.Contracts.FirstOrDefault(x => x.Id == id)?.File is { } f ? [f] : [];
        }
        if (domain == SourceEvidenceDomain.CiCd) return evidence.CiCd.Pipelines.FirstOrDefault(x => x.Id == key)?.File is { } f ? [f] : [];
        return [];
    }
    private static IEnumerable<string> DatabaseFiles(DatabaseArchitectureSnapshot? snapshot, string name) => snapshot?.Databases.SelectMany(d => d.Schemas).SelectMany(s => s.Tables)
        .Where(t => name.StartsWith(t.LogicalName, StringComparison.OrdinalIgnoreCase)).SelectMany(t => t.Evidence).Select(e => e.Path) ?? [];
    private static string NormalizePath(string path) => path.Replace('\\', '/').TrimStart('/');
    private static string ChangeEvidenceState(ImpactChange change, IqrSourceSnapshot before, IqrSourceSnapshot after)
    {
        static string? ArchitectureState(ArchitectureSnapshot? a, ImpactChange c) => c.EntityType switch
        {
            "Component" => a?.Components.FirstOrDefault(x => x.Id == c.EntityKey)?.EvidenceState.ToString(),
            "Dependency" => a?.Dependencies.FirstOrDefault(x => x.Id == c.EntityKey)?.EvidenceState.ToString(),
            "Messaging channel" => a?.MessagingChannels.FirstOrDefault(x => x.Id == c.EntityKey)?.Confidence.ToString(),
            "Datastore" => a?.DataStores.FirstOrDefault(x => x.Id == c.EntityKey)?.Confidence.ToString(),
            "External system" => a?.ExternalSystems.FirstOrDefault(x => x.Id == c.EntityKey)?.Confidence.ToString(),
            _ => null,
        };
        var result = ArchitectureState(after.Architecture, change) ?? ArchitectureState(before.Architecture, change);
        if (result is not null) return result;
        if (change.Domain == ImpactChangeDomain.Contracts)
        {
            var id = change.EntityKey.Split('|')[0];
            return after.EvidenceDomains?.Contracts.Contracts.FirstOrDefault(x => x.Id == id)?.EvidenceState.ToString()
                ?? before.EvidenceDomains?.Contracts.Contracts.FirstOrDefault(x => x.Id == id)?.EvidenceState.ToString() ?? "Confirmed";
        }
        if (change.Domain == ImpactChangeDomain.Configuration)
            return after.EvidenceDomains?.Configuration.Entries.FirstOrDefault(x => $"{x.File}|{x.NormalizedKey}" == change.EntityKey)?.EvidenceState.ToString()
                ?? before.EvidenceDomains?.Configuration.Entries.FirstOrDefault(x => $"{x.File}|{x.NormalizedKey}" == change.EntityKey)?.EvidenceState.ToString() ?? "Confirmed";
        if (change.Domain == ImpactChangeDomain.Infrastructure)
            return after.EvidenceDomains?.Infrastructure.Resources.FirstOrDefault(x => x.Id == change.EntityKey)?.EvidenceState.ToString()
                ?? before.EvidenceDomains?.Infrastructure.Resources.FirstOrDefault(x => x.Id == change.EntityKey)?.EvidenceState.ToString() ?? "Confirmed";
        if (change.Domain == ImpactChangeDomain.Database)
            return after.DatabaseArchitecture?.Databases.SelectMany(d => d.Schemas).SelectMany(s => s.Tables).FirstOrDefault(t => change.Name.StartsWith(t.LogicalName, StringComparison.OrdinalIgnoreCase))?.EvidenceState.ToString()
                ?? before.DatabaseArchitecture?.Databases.SelectMany(d => d.Schemas).SelectMany(s => s.Tables).FirstOrDefault(t => change.Name.StartsWith(t.LogicalName, StringComparison.OrdinalIgnoreCase))?.EvidenceState.ToString() ?? "Confirmed";
        return change.Domain is ImpactChangeDomain.CiCd or ImpactChangeDomain.Observability or ImpactChangeDomain.Unclassified ? "Unresolved" : "Confirmed";
    }
    private static TechnicalImpactLevel Stronger(TechnicalImpactLevel a, TechnicalImpactLevel b) => Rank(a) <= Rank(b) ? a : b;
    private static int Rank(TechnicalImpactLevel value) => value switch { TechnicalImpactLevel.Direct => 0, TechnicalImpactLevel.Indirect => 1, TechnicalImpactLevel.Potential => 2, _ => 3 };
    private static string Weaker(string a, string b) => a == "Conflict" || b == "Conflict" ? "Conflict" : a == "Unresolved" || b == "Unresolved" ? "Unresolved" : a == "Inferred" || b == "Inferred" ? "Inferred" : a == "StronglySupported" || b == "StronglySupported" ? "StronglySupported" : a;
}
