using BirkNext.Dependencies;
using NuGet.Versioning;

namespace BirkNext.Api.Services.DependencyReview;

/// <summary>
/// Neutral comparisons between two inventories. A transitive SBOM component is not source drift; a boot manifest without versions proves
/// neither a match nor an absence; a baseline change is a change, not a defect. Nothing here assigns severity.
/// </summary>
public static class InventoryComparer
{
    private static bool SameVersion(string? a, string? b) =>
        a is not null && b is not null && (NuGetVersion.TryParse(a, out var x) && NuGetVersion.TryParse(b, out var y) ? x == y : string.Equals(a, b, StringComparison.OrdinalIgnoreCase));

    /// <summary>Package → the distinct versions an inventory has for it (a package can be declared in several files).</summary>
    private static Dictionary<string, List<InventoryDependency>> Index(DependencyInventorySnapshot inventory) =>
        inventory.Dependencies.GroupBy(InventorySources.Key).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

    private static string? Versions(List<InventoryDependency> items) =>
        items.Select(i => i.Version ?? i.VersionRange).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList() is { Count: > 0 } v ? string.Join(", ", v) : null;

    private static HashComparison Hashes(InventoryDependency a, InventoryDependency b)
    {
        var shared = a.Hashes.Join(b.Hashes, h => h.Algorithm, h => h.Algorithm, (x, y) => (x, y), StringComparer.OrdinalIgnoreCase).ToList();
        if (shared.Count == 0) return HashComparison.NotAvailable;
        return shared.All(p => string.Equals(p.x.Value, p.y.Value, StringComparison.OrdinalIgnoreCase)) ? HashComparison.Matched : HashComparison.Mismatch;
    }

    /// <summary>Declared source dependencies vs an SBOM/lock inventory. Differences of a directly declared exact version are an evidence conflict.</summary>
    public static InventoryComparison SourceVsSbom(DependencyInventorySnapshot source, DependencyInventorySnapshot sbom)
    {
        var left = Index(source);
        var right = Index(sbom);
        var entries = new List<ComparisonEntry>();
        var unchanged = 0;
        foreach (var (key, declared) in left)
        {
            var first = declared[0];
            if (!right.TryGetValue(key, out var present))
            {
                entries.Add(new ComparisonEntry { PackageName = first.PackageName, PackageManager = first.PackageManager, State = ComparisonState.DeclaredNotInSbom, LeftValue = Versions(declared),
                    Detail = $"Declared in source, not present in {sbom.Name}. The SBOM's completeness is not claimed." });
                continue;
            }
            var exact = declared.Where(d => d.Version is not null).ToList();
            if (exact.Count == 0)
            {
                entries.Add(new ComparisonEntry { PackageName = first.PackageName, PackageManager = first.PackageManager, State = ComparisonState.NotComparable, LeftValue = Versions(declared), RightValue = Versions(present),
                    Detail = "Declared as a range/floating value; the resolved version in the SBOM is not a conflict." });
                continue;
            }
            if (exact.All(d => present.Any(p => SameVersion(d.Version, p.Version)))) { unchanged++; continue; }
            entries.Add(new ComparisonEntry
            {
                PackageName = first.PackageName, PackageManager = first.PackageManager, State = ComparisonState.EvidenceConflict, LeftValue = Versions(exact), RightValue = Versions(present),
                Detail = $"Evidence conflict — needs review: {source.Name} declares {Versions(exact)}, {sbom.Name} has {Versions(present)}. Neither source is chosen.",
            });
        }
        foreach (var (key, present) in right.Where(r => !left.ContainsKey(r.Key)))
            entries.Add(new ComparisonEntry
            {
                PackageName = present[0].PackageName, PackageManager = present[0].PackageManager, State = ComparisonState.PresentNotDeclaredDirectly, RightValue = Versions(present),
                Detail = present.All(p => p.Relationship == DependencyRelationship.Transitive) ? "Present in the SBOM as a transitive component; not source drift." : "Present in the SBOM, not declared directly in source.",
            });
        return new InventoryComparison
        {
            Kind = ComparisonKind.SourceVsSbom, LeftInventoryId = source.Id, LeftName = source.Name, RightInventoryId = sbom.Id, RightName = sbom.Name, Entries = Order(entries), Unchanged = unchanged,
            Limitations = ["Only packages with an exact declared version can conflict; ranges and transitive components are not drift."],
        };
    }

    /// <summary>Expected inventory vs deployed evidence. Version states only where the deployed evidence carries versions.</summary>
    public static InventoryComparison ExpectedVsDeployed(DependencyInventorySnapshot expected, DependencyInventorySnapshot deployed)
    {
        var left = Index(expected);
        var right = deployed.Dependencies.GroupBy(d => d.PackageName.ToLowerInvariant()).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var versioned = deployed.Dependencies.Any(d => d.Version is not null);
        var entries = new List<ComparisonEntry>();
        var matched = 0;
        foreach (var (_, items) in left)
        {
            var first = items[0];
            var observed = right.GetValueOrDefault(first.PackageName.ToLowerInvariant());
            if (observed is null)
            {
                entries.Add(new ComparisonEntry
                {
                    PackageName = first.PackageName, PackageManager = first.PackageManager, LeftValue = Versions(items),
                    State = versioned ? ComparisonState.MissingInDeployment : ComparisonState.NotComparable,
                    Detail = versioned ? $"Not present in {deployed.Name}." : $"Not observed in {deployed.Name}; this evidence does not prove absence (assembly names are not package identities).",
                });
                continue;
            }
            var deployedVersion = Versions(observed);
            var expectedExact = items.Select(i => i.Version).OfType<string>().ToList();
            if (deployedVersion is null || expectedExact.Count == 0 || observed.All(o => o.Version is null))
            {
                entries.Add(new ComparisonEntry { PackageName = first.PackageName, PackageManager = first.PackageManager, State = ComparisonState.NotComparable, LeftValue = Versions(items), RightValue = deployedVersion,
                    Hash = Hashes(first, observed[0]),
                    Detail = observed.All(o => o.Version is null) ? $"Present in {deployed.Name}; the deployed version is not proven by this evidence." : "The expected inventory has no exact version." });
                continue;
            }
            var same = expectedExact.Any(e => observed.Any(o => SameVersion(e, o.Version)));
            if (same) matched++;
            entries.Add(new ComparisonEntry
            {
                PackageName = first.PackageName, PackageManager = first.PackageManager, State = same ? ComparisonState.Matched : ComparisonState.DifferentVersion,
                LeftValue = string.Join(", ", expectedExact.Distinct()), RightValue = deployedVersion, Hash = Hashes(first, observed[0]),
                Detail = same ? "Expected and deployed versions match." : "Deployed version differs from the expected inventory. Severity is not inferred.",
            });
        }
        if (versioned)
            foreach (var extra in deployed.Dependencies.Where(d => !left.Values.Any(l => l[0].PackageName.Equals(d.PackageName, StringComparison.OrdinalIgnoreCase))).GroupBy(d => d.PackageName.ToLowerInvariant()))
                entries.Add(new ComparisonEntry { PackageName = extra.First().PackageName, PackageManager = extra.First().PackageManager, State = ComparisonState.AdditionalInDeployment, RightValue = Versions(extra.ToList()),
                    Detail = $"In {deployed.Name}, not in {expected.Name} (for a declared-only inventory this includes transitive components)." });
        return new InventoryComparison
        {
            Kind = ComparisonKind.ExpectedVsDeployed, LeftInventoryId = expected.Id, LeftName = expected.Name, RightInventoryId = deployed.Id, RightName = deployed.Name,
            Entries = Order(entries.Where(e => e.State != ComparisonState.Matched).Concat(entries.Where(e => e.State == ComparisonState.Matched)).ToList()), Unchanged = matched,
            Limitations = versioned ? ["Deployed evidence is taken as stated by its source; runtime loading is not assessed."]
                : ["The deployed evidence carries no versions: presence is shown, versions are Not comparable, and absence is not concluded.", .. deployed.Limitations],
        };
    }

    /// <summary>Previous inventory (baseline) vs current: neutral change categories only.</summary>
    public static InventoryComparison Baseline(DependencyInventorySnapshot baseline, DependencyInventorySnapshot current)
    {
        var before = Index(baseline);
        var after = Index(current);
        var entries = new List<ComparisonEntry>();
        var unchanged = 0;
        foreach (var (key, now) in after)
        {
            var first = now[0];
            if (!before.TryGetValue(key, out var then))
            {
                entries.Add(new ComparisonEntry { PackageName = first.PackageName, PackageManager = first.PackageManager, State = ComparisonState.Added, RightValue = Versions(now), Detail = "Not in the baseline." });
                continue;
            }
            var changed = false;
            if (!string.Equals(Versions(then), Versions(now), StringComparison.OrdinalIgnoreCase))
            {
                entries.Add(new ComparisonEntry { PackageName = first.PackageName, PackageManager = first.PackageManager, State = ComparisonState.VersionChanged, LeftValue = Versions(then), RightValue = Versions(now), Detail = "Version changed since the baseline." });
                changed = true;
            }
            var licensesThen = string.Join(" | ", then.SelectMany(t => t.Licenses).Distinct().OrderBy(s => s));
            var licensesNow = string.Join(" | ", now.SelectMany(t => t.Licenses).Distinct().OrderBy(s => s));
            if (then.Any(t => t.LicenseFieldPresent) && now.Any(t => t.LicenseFieldPresent) && licensesThen != licensesNow)
            {
                entries.Add(new ComparisonEntry { PackageName = first.PackageName, PackageManager = first.PackageManager, State = ComparisonState.LicenseChanged, LeftValue = licensesThen.Length == 0 ? "none declared" : licensesThen,
                    RightValue = licensesNow.Length == 0 ? "none declared" : licensesNow, Detail = "License metadata changed. Not a policy violation unless a license policy says so." });
                changed = true;
            }
            var registryThen = then.Select(t => t.Registry).OfType<string>().Distinct().ToList();
            var registryNow = now.Select(t => t.Registry).OfType<string>().Distinct().ToList();
            if (registryThen.Count > 0 && registryNow.Count > 0 && !registryThen.SequenceEqual(registryNow, StringComparer.OrdinalIgnoreCase))
            {
                entries.Add(new ComparisonEntry { PackageName = first.PackageName, PackageManager = first.PackageManager, State = ComparisonState.SourceChanged, LeftValue = string.Join(", ", registryThen), RightValue = string.Join(", ", registryNow), Detail = "Package source/registry changed." });
                changed = true;
            }
            if (!changed && Hashes(then[0], now[0]) == HashComparison.Mismatch)
            {
                entries.Add(new ComparisonEntry { PackageName = first.PackageName, PackageManager = first.PackageManager, State = ComparisonState.MetadataChanged, LeftValue = Versions(then), RightValue = Versions(now),
                    Hash = HashComparison.Mismatch, Detail = "Same version, different hash. Integrity is not concluded from this alone." });
                changed = true;
            }
            if (!changed) unchanged++;
        }
        foreach (var (key, then) in before.Where(b => !after.ContainsKey(b.Key)))
            entries.Add(new ComparisonEntry { PackageName = then[0].PackageName, PackageManager = then[0].PackageManager, State = ComparisonState.Removed, LeftValue = Versions(then), Detail = "In the baseline, not in the current inventory." });
        return new InventoryComparison
        {
            Kind = ComparisonKind.Baseline, LeftInventoryId = baseline.Id, LeftName = baseline.Name, RightInventoryId = current.Id, RightName = current.Name, Entries = Order(entries), Unchanged = unchanged,
            Limitations = baseline.SourceType == current.SourceType ? [] : [$"The baseline ({DependencyHealthLabels.Source(baseline.SourceType)}) and the current inventory ({DependencyHealthLabels.Source(current.SourceType)}) come from different kinds of source; differences may reflect that."],
        };
    }

    private static List<ComparisonEntry> Order(List<ComparisonEntry> entries) => entries.OrderBy(e => e.State).ThenBy(e => e.PackageName, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// Declared / resolved / packaged / deployed / runtime-loaded for one package across the inventories in the review. A stage with no
    /// inventory is "Not assessed"; runtime loading is always "Not assessed" (no source proves it; an SBOM entry never does).
    /// </summary>
    public static List<StageObservation> Stages(InventoryDependency dep, IReadOnlyList<DependencyInventorySnapshot> inventories)
    {
        var key = InventorySources.Key(dep);
        var result = new List<StageObservation>();
        foreach (var stage in new[] { InventoryStage.Declared, InventoryStage.Resolved, InventoryStage.Packaged, InventoryStage.Deployed })
        {
            var ofStage = inventories.Where(i => i.Stage == stage).ToList();
            if (ofStage.Count == 0) { result.Add(new StageObservation(stage, "Not assessed", null, null)); continue; }
            var hits = ofStage.SelectMany(i => i.Dependencies.Where(d => InventorySources.Key(d) == key
                || stage == InventoryStage.Deployed && i.SourceType == InventorySourceType.Deployment && d.PackageName.Equals(dep.PackageName, StringComparison.OrdinalIgnoreCase)).Select(d => (Inventory: i, Dep: d))).ToList();
            if (hits.Count == 0) { result.Add(new StageObservation(stage, "Not observed", null, string.Join(", ", ofStage.Select(i => i.Name)))); continue; }
            var version = Versions(hits.Select(h => h.Dep).ToList());
            result.Add(new StageObservation(stage, hits.All(h => h.Dep.Version is null) ? "Observed (version not proven)" : "Observed", version, string.Join(", ", hits.Select(h => h.Inventory.Name).Distinct())));
        }
        result.Add(new StageObservation(InventoryStage.RuntimeObserved, "Not assessed", null, "No evidence source proves runtime loading."));
        return result;
    }
}
