using System.Text.Json.Serialization;

namespace BirkNext.Dependencies;

// ── Shared Dependency Evidence ─────────────────────────────────────────────────────────────────────────────────────────
//
// Dependency evidence is produced by a family of providers (source snapshot, existing inventory, SBOM/lock file, deployed capture, Renovate policy,
// registry metadata, security advisories, license metadata, internal package relationships) and stored once: source declarations on the immutable
// Source Analysis snapshot, inventories as immutable records, enrichment inside each immutable review run. Dependency Review is the primary consumer;
// Source Analysis, Technology Coverage, Pipeline Review, Impact Analysis, traceability and documentation read the same evidence through
// DependencyEvidenceQueries. Layers are never collapsed into "dependency verified":
//   declared ≠ resolved ≠ SBOM ≠ registry metadata ≠ advisory match ≠ license metadata ≠ Renovate policy ≠ Renovate execution ≠ deployed ≠ runtime-loaded.
// A package dependency is never a runtime integration; Terraform providers/modules are an IaC layer, not software package health.

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DependencyEvidenceLayer { Declared, Resolved, Sbom, Registry, Security, License, Policy, PolicyExecution, Deployed, RuntimeLoaded, InternalRelationship }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DependencyProviderKind { Inventory, Enrichment, Policy, Relationship }

/// <summary>What one dependency evidence provider contributes: the layer it fills, which review modes use it, what it can and cannot prove,
/// whether it needs the network (online enrichment is explicit and optional; source extraction works offline) and its actual support.</summary>
public sealed record DependencyEvidenceProviderDescriptor(
    string Key, string Name, DependencyProviderKind Kind, DependencyEvidenceLayer Layer, IReadOnlyList<string> Modes, string Proves, string CannotProve,
    bool RequiresNetwork, IReadOnlyList<string> Formats, string Support);

/// <summary>The dependency evidence provider family (the catalog Dependency Review, Technology Coverage and documentation read).</summary>
public static class DependencyEvidenceProviders
{
    public const string SourceMode = "source", InventoryMode = "inventory", SbomMode = "sbom", DeployedMode = "deployed";

    public static IReadOnlyList<DependencyEvidenceProviderDescriptor> All { get; } =
    [
        new("source-snapshot", "Source dependency evidence", DependencyProviderKind.Inventory, DependencyEvidenceLayer.Declared, [SourceMode],
            "Declared dependencies with their owner file and consuming projects, central package management, project references, packages a source produces, Renovate files and automation definitions — extracted once during Source Analysis.",
            "Resolved versions, transitive dependencies, deployment or runtime loading.", false,
            ["*.csproj PackageReference", "Directory.Packages.props", "Directory.Build.props", "global.json", "dotnet-tools.json", "Dockerfile / compose images", "pipeline container images"], "Supported"),
        new("existing-inventory", "Stored dependency inventory", DependencyProviderKind.Inventory, DependencyEvidenceLayer.Resolved, [InventoryMode],
            "A stored, immutable inventory (identity, origin, capture time, stage, provenance) reviewed again with current enrichment.",
            "That the inventory is still current: it may be stale.", false, ["Stored inventory"], "Supported"),
        new("sbom", "SBOM / lock-file inventory", DependencyProviderKind.Inventory, DependencyEvidenceLayer.Sbom, [SbomMode],
            "Resolved components with identifiers, hashes, licenses, suppliers and — when the document states them — dependency relationships.",
            "That a target currently runs that artifact, unless the SBOM is linked to a deployment.", false,
            ["CycloneDX JSON", "CycloneDX XML", "SPDX JSON", "NuGet packages.lock.json (resolved inventory, not a full SBOM)"], "Supported"),
        new("deployed-capture", "Deployed dependency capture", DependencyProviderKind.Inventory, DependencyEvidenceLayer.Deployed, [DeployedMode],
            "Framework/package assets a deployed target exposes, through the capture methods it supports (classic Blazor boot manifest, modern Blazor import map / dotnet.js assets, service-worker asset list, Browser Companion runtime evidence).",
            "A complete inventory when only some assets are visible; runtime loading of every component. One failed method is not 'no deployment evidence'.", true,
            ["Blazor WASM boot assets", "Browser Companion page evidence"], "Partial"),
        new("renovate-policy", "Renovate policy", DependencyProviderKind.Policy, DependencyEvidenceLayer.Policy, [SourceMode, InventoryMode, SbomMode],
            "Enabled managers, grouping, update-type rules, automerge, schedules, ignored dependencies and version constraints, evaluated offline. Synthetic policy simulation tests the configured rules.",
            "That Renovate runs, opened a PR, merged an update or that tests passed — Renovate is never executed.", false, ["renovate.json", "renovate.json5", ".github/renovate.json", "package.json renovate key"], "Partial"),
        new("renovate-execution", "Renovate execution evidence", DependencyProviderKind.Enrichment, DependencyEvidenceLayer.PolicyExecution, [SourceMode, InventoryMode, SbomMode],
            "Renovate pipeline runs and dependency pull requests, read from Azure DevOps when it is configured.",
            "That an update was merged or that its tests passed, unless the run or PR says so. Without access, execution is Not assessed.", true, ["Azure DevOps runs / pull requests"], "Partial"),
        new("registry-metadata", "Registry metadata", DependencyProviderKind.Enrichment, DependencyEvidenceLayer.Registry, [SourceMode, InventoryMode, SbomMode, DeployedMode],
            "Latest published stable, deprecation and listing status, as read from the registry at run time (read-only, cached; private package patterns are never sent).",
            "A recommended version; outdated is not vulnerable; registry unavailable is not 'package missing'.", true, ["nuget.org"], "Partial"),
        new("security-advisories", "Security advisories", DependencyProviderKind.Enrichment, DependencyEvidenceLayer.Security, [SourceMode, InventoryMode, SbomMode, DeployedMode],
            "Matched advisories for packages the advisory source covers, with the coverage of this run.",
            "Safety: no matched advisory is not 'secure', and an unavailable source never means there are no vulnerabilities.", true, ["OSV (osv.dev)"], "Partial"),
        new("license-metadata", "License metadata", DependencyProviderKind.Enrichment, DependencyEvidenceLayer.License, [SourceMode, InventoryMode, SbomMode, DeployedMode],
            "License expressions from the inventory (SBOM) or registry metadata; Known, Multiple, Unknown or Not declared.",
            "Compliance — unless a license policy is configured, license metadata is descriptive only.", false, ["SBOM license fields", "registry license metadata"], "Partial"),
        new("internal-packages", "Internal package relationships", DependencyProviderKind.Relationship, DependencyEvidenceLayer.InternalRelationship, [SourceMode],
            "Packages a source snapshot produces (PackageId + version) and every project, in any snapshot, that consumes them with its declared version — across snapshots without merging them.",
            "Compatibility: a different version is 'Mapped with version difference', never a conflict or a break.", false, ["PackageId / Version in csproj or Directory.Build.props"], "Supported"),
    ];

    public static IEnumerable<DependencyEvidenceProviderDescriptor> ForMode(string mode) => All.Where(p => p.Modes.Contains(mode));
    public static DependencyEvidenceProviderDescriptor Get(string key) => All.Single(p => p.Key == key);
}

/// <summary>An ecosystem-qualified package identity: <c>nuget:foo</c> and <c>npm:foo</c> are different packages.</summary>
public sealed record DependencyCoordinate(string Ecosystem, string Name)
{
    public string Key => $"{Ecosystem.ToLowerInvariant()}:{Name.ToLowerInvariant()}";
    public override string ToString() => $"{Name} ({Ecosystem})";

    public static string EcosystemOf(string manager) => manager.ToLowerInvariant() switch
    {
        "nuget" or "msbuild-sdk" or "dotnet-tool" => "nuget",
        "npm" or "yarn" or "pnpm" => "npm",
        "dockerfile" or "docker-compose" or "docker" or "azure-pipelines" => "docker",
        var other => other,
    };
}

/// <summary>One source snapshot's stored dependency evidence, with the provenance every consumer shows. <paramref name="ArchiveSha256"/> identifies
/// the analyzed content: re-analyses of an identical archive are one source, not extra producers or consumers.</summary>
public sealed record DependencySourceEvidence(Guid SnapshotId, string Source, DateTimeOffset AnalyzedAt, SourceDependencyEvidence Evidence, string? ArchiveSha256 = null);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum InternalConsumerState { Mapped, MappedWithVersionDifference, VersionNotComparable }

public sealed record InternalPackageConsumer(Guid SnapshotId, string Source, string Project, string? DeclaredVersion, string File, int Line, InternalConsumerState State);

/// <summary>An internal package a source produces and every project that consumes it — a package dependency, never a runtime integration.</summary>
public sealed record InternalPackageMapping(DependencyCoordinate Package, Guid ProducerSnapshotId, string ProducerSource, string ProducerProject, string? ProducerVersion,
    IReadOnlyList<InternalPackageConsumer> Consumers)
{
    public int Aligned => Consumers.Count(c => c.State == InternalConsumerState.Mapped);
    public int VersionDifferences => Consumers.Count(c => c.State == InternalConsumerState.MappedWithVersionDifference);
    public int NotComparable => Consumers.Count(c => c.State == InternalConsumerState.VersionNotComparable);
    public IReadOnlyList<string> ConsumerVersions => [.. Consumers.Select(c => c.DeclaredVersion ?? "(not stated)").Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)];
}

/// <summary>One project's dependency on a package, from source declarations (direct, or a project reference).</summary>
public sealed record ProjectDependency(Guid SnapshotId, string Source, string Project, DependencyCoordinate Package, string? DeclaredVersion, bool IsRange, string OwnerFile, int Line, string Relationship);

/// <summary>A package whose change may affect consumers: exact declarations are high-confidence source impact, never a breakage claim.</summary>
public sealed record DependencyImpact(string Project, Guid SnapshotId, string Source, string? DeclaredVersion, string Confidence, string Reason);

/// <summary>Coverage of one enrichment dimension in a run: how much of the inventory the source could assess, kept apart from the findings.</summary>
public sealed record DependencyCoverageDimension(string Dimension, int Assessed, int Total, string Text, bool Complete);

public static class DependencyEvidenceQueries
{
    private static string Version(DeclaredDependency d) => d.CurrentValue ?? "";

    /// <summary>Sources with distinct content: snapshots of an identical archive (same SHA-256) count once — the first in the caller's order is
    /// kept, never the newest by date. Different archives with the same file name stay separate.</summary>
    public static IEnumerable<DependencySourceEvidence> DistinctContent(IEnumerable<DependencySourceEvidence> sources) =>
        sources.DistinctBy(s => string.IsNullOrWhiteSpace(s.ArchiveSha256) ? s.SnapshotId.ToString() : s.ArchiveSha256.ToLowerInvariant());

    /// <summary>Every project → package dependency declared in the sources (a central declaration counts once per consuming project, never twice).</summary>
    public static IEnumerable<ProjectDependency> ProjectDependencies(IEnumerable<DependencySourceEvidence> sources) =>
        DistinctContent(sources).SelectMany(s => s.Evidence.Dependencies.SelectMany(d => (d.ReferencedBy.Count > 0 ? d.ReferencedBy : [d.OwnerFile]).Distinct(StringComparer.Ordinal)
                .Select(project => new ProjectDependency(s.SnapshotId, s.Source, project, new DependencyCoordinate(DependencyCoordinate.EcosystemOf(d.Manager), d.PackageName),
                    d.CurrentValue, d.IsRange, d.OwnerFile, d.Line, "Direct (declared)")))
            .Concat(s.Evidence.ExternalProjectReferences.Select(r => new ProjectDependency(s.SnapshotId, s.Source, r.FromProject, new DependencyCoordinate("project", r.ProjectFileName),
                null, false, r.FromProject, 0, "Project reference (source outside this repository)"))));

    public static IEnumerable<ProjectDependency> DependenciesForProject(IEnumerable<DependencySourceEvidence> sources, string project) =>
        ProjectDependencies(sources).Where(d => d.Project.Equals(project, StringComparison.OrdinalIgnoreCase) || System.IO.Path.GetFileNameWithoutExtension(d.Project).Equals(project, StringComparison.OrdinalIgnoreCase));

    public static IEnumerable<ProjectDependency> ConsumersForPackage(IEnumerable<DependencySourceEvidence> sources, DependencyCoordinate package) =>
        ProjectDependencies(sources).Where(d => d.Package.Key == package.Key);

    /// <summary>Every source that declares it produces the package (several producers are all returned — none is picked by date).</summary>
    public static IEnumerable<(DependencySourceEvidence Source, SourcePublishedPackage Package)> ProducersForInternalPackage(IEnumerable<DependencySourceEvidence> sources, string packageId) =>
        sources.SelectMany(s => s.Evidence.PublishedPackages.Where(p => p.PackageId.Equals(packageId, StringComparison.OrdinalIgnoreCase)).Select(p => (s, p)));

    /// <summary>
    /// Internal package producer → consumer mappings across separate snapshots (never merged): for each package a source produces, every
    /// project in the given sources that declares it. Same version → Mapped; a different exact version → Mapped with version difference;
    /// a range, variable or missing version → Version not comparable. Never "conflict", "broken" or "incompatible".
    /// </summary>
    public static IReadOnlyList<InternalPackageMapping> InternalPackageMappings(IReadOnlyList<DependencySourceEvidence> sources)
    {
        var result = new List<InternalPackageMapping>();
        var consumers = ProjectDependencies(sources).Where(d => d.Package.Ecosystem == "nuget").ToLookup(d => d.Package.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var producer in DistinctContent(sources))
            foreach (var package in producer.Evidence.PublishedPackages.DistinctBy(p => p.PackageId.ToLowerInvariant()))
            {
                var uses = consumers[package.PackageId].Where(c => !(c.SnapshotId == producer.SnapshotId && c.Project == package.Project)).ToList();
                if (uses.Count == 0) continue;
                result.Add(new InternalPackageMapping(new DependencyCoordinate("nuget", package.PackageId), producer.SnapshotId, producer.Source, package.Project, package.Version,
                    [.. uses.Select(u => new InternalPackageConsumer(u.SnapshotId, u.Source, u.Project, u.DeclaredVersion, u.OwnerFile, u.Line, State(package.Version, u.DeclaredVersion, u.IsRange)))
                        .OrderBy(c => c.State).ThenBy(c => c.Project, StringComparer.Ordinal)]));
            }
        return result;
    }

    public static InternalConsumerState State(string? produced, string? declared, bool isRange) =>
        string.IsNullOrWhiteSpace(produced) || string.IsNullOrWhiteSpace(declared) || isRange || declared.Contains("$(") || declared.Contains('*') ? InternalConsumerState.VersionNotComparable
        : string.Equals(produced.Trim(), declared.Trim(), StringComparison.OrdinalIgnoreCase) ? InternalConsumerState.Mapped
        : InternalConsumerState.MappedWithVersionDifference;

    public static string Label(InternalConsumerState state) => state switch
    {
        InternalConsumerState.Mapped => "Mapped",
        InternalConsumerState.MappedWithVersionDifference => "Mapped with version difference",
        _ => "Version not comparable",
    };

    public static IEnumerable<InternalPackageConsumer> VersionDifferences(IEnumerable<InternalPackageMapping> mappings) =>
        mappings.SelectMany(m => m.Consumers.Where(c => c.State == InternalConsumerState.MappedWithVersionDifference));

    /// <summary>Projects potentially affected when a package changes: exact declarations are high-confidence source impact, a range/variable
    /// declaration is possible impact. No breakage is claimed and no test is marked failed.</summary>
    public static IReadOnlyList<DependencyImpact> FindDependencyImpact(IEnumerable<DependencySourceEvidence> sources, DependencyCoordinate package) =>
        [.. ConsumersForPackage(sources, package).Select(c => new DependencyImpact(c.Project, c.SnapshotId, c.Source, c.DeclaredVersion,
            c.IsRange || c.DeclaredVersion is null ? "Possible impact" : "High-confidence source impact",
            c.IsRange || c.DeclaredVersion is null ? $"Declares {c.Package.Name} without an exact version; the effect of a change is possible, not established."
                : $"Declares {c.Package.Name} {c.DeclaredVersion}; a change to the package may affect this project. Compatibility is not assessed."))];

    /// <summary>Advisory coverage of a run: "No matched advisories in available evidence · coverage incomplete" when lookups were unavailable —
    /// never "0 vulnerabilities".</summary>
    public static DependencyCoverageDimension AdvisoryCoverage(DependencyHealthRun run)
    {
        var total = run.Items.Count;
        var affected = run.Items.Count(i => i.Security.State == AdvisoryState.Affected);
        var assessed = run.Items.Count(i => i.Security.State is AdvisoryState.Affected or AdvisoryState.NotAffectedByMatchedAdvisory or AdvisoryState.NoMatchedAdvisoryObserved);
        var gaps = total - assessed;
        var text = affected > 0 ? $"{affected} package(s) match an advisory" : "No matched advisories in available evidence";
        if (gaps > 0) text += $" · advisory coverage incomplete ({gaps} of {total} not assessed or unavailable)";
        return new("Advisory coverage", assessed, total, text, gaps == 0);
    }

    /// <summary>License metadata coverage: descriptive ("94% license metadata available · 2 unknown/not declared"), never compliance unless a policy is configured.</summary>
    public static DependencyCoverageDimension LicenseCoverage(DependencyHealthRun run)
    {
        var total = run.Items.Count;
        var known = run.Items.Count(i => i.License.State is LicenseState.Detected or LicenseState.Multiple);
        var missing = total - known;
        var percent = total == 0 ? 0 : (int)Math.Round(100.0 * known / total);
        return new("License coverage", known, total, total == 0 ? "No dependencies" : $"{percent}% license metadata available{(missing > 0 ? $" · {missing} unknown/not declared" : "")}", missing == 0);
    }

    /// <summary>Registry coverage: how many items the registry could be read for (private and unsupported items are not assessed, not missing).</summary>
    public static DependencyCoverageDimension RegistryCoverage(DependencyHealthRun run)
    {
        var total = run.Items.Count;
        var observed = run.Items.Count(i => i.Registry.State == RegistryState.Observed);
        return new("Registry coverage", observed, total, $"Registry metadata for {observed} of {total}", observed == total);
    }

    public static IReadOnlyList<DependencyCoverageDimension> Coverage(DependencyHealthRun run) =>
    [
        new("Inventory coverage", run.Items.Count, run.Items.Count, $"{run.Items.Count} package(s) from {run.Inventory.Name} ({run.Inventory.Stage})", true),
        RegistryCoverage(run), AdvisoryCoverage(run), LicenseCoverage(run),
        new("Policy coverage", run.PolicySource is null ? 0 : 1, 1, run.PolicySource is null ? "No Renovate policy selected" : $"Renovate policy: {run.PolicySource}", run.PolicySource is not null),
        new("Deployment coverage", run.Deployed is null ? 0 : 1, 1, run.Deployed is null ? "No deployed evidence compared" : $"Compared with deployed evidence {run.Deployed.Name}", run.Deployed is not null),
    ];
}

/// <summary>Dependency evidence for one requirement or quality control: what the source implements and — separately — that execution is not assessed.</summary>
public sealed record DependencyRequirementEvidence(string Control, string Implementation, string Detail)
{
    public string Execution => "Not assessed";
}

public static class DependencyTraceability
{
    /// <summary>Controls a requirement names (automated updates, pinning, internal package consistency, SBOM), answered from stored source dependency
    /// evidence. Configuration is implementation evidence only — never "verified".</summary>
    public static IReadOnlyList<DependencyRequirementEvidence> For(IReadOnlyList<DependencySourceEvidence> sources, string requirement)
    {
        var text = requirement.ToLowerInvariant();
        var result = new List<DependencyRequirementEvidence>();
        var renovate = sources.SelectMany(s => s.Evidence.RenovateFiles.Select(f => $"{s.Source}: {f.Path}")).ToList();
        if ((text.Contains("update") || text.Contains("upgrade")) && (text.Contains("automat") || text.Contains("renovate") || text.Contains("dependabot")))
            result.Add(new("Automated dependency updates", renovate.Count > 0 ? "Update policy configured in source (Renovate)" : "No update automation configuration in the selected sources",
                renovate.Count > 0 ? string.Join("; ", renovate) + ". Renovate running, PRs and merged updates are not established by configuration." : "Configuration may exist outside these sources."));
        if (text.Contains("pin") && (text.Contains("depend") || text.Contains("version") || text.Contains("package")))
        {
            var declared = sources.SelectMany(s => s.Evidence.Dependencies).ToList();
            result.Add(new("Dependency pinning", $"{declared.Count(d => !d.IsRange && d.CurrentValue is not null)} exact / {declared.Count(d => d.IsRange)} range declarations",
                "Declared values only; resolved versions need a lock file or SBOM."));
        }
        if (text.Contains("internal package") || (text.Contains("consistent") && text.Contains("version")))
        {
            var mappings = DependencyEvidenceQueries.InternalPackageMappings(sources);
            result.Add(new("Internal package consistency", $"{mappings.Count} internal package(s) · {mappings.Sum(m => m.VersionDifferences)} consumer(s) with a version difference",
                "A version difference is not an incompatibility."));
        }
        if (text.Contains("sbom") || text.Contains("software bill of materials"))
            result.Add(new("SBOM availability", "Not established from source declarations", "Import or generate an SBOM; pipeline SBOM generation steps are configuration, not the SBOM."));
        return result;
    }
}

public static class DependencyDocumentation
{
    /// <summary>A Markdown dependency summary from normalized evidence: inventory counts by manager, internal package relationships, version
    /// differences and update policy — never raw manifests or the full inventory.</summary>
    public static string Markdown(DependencySourceEvidence source, IReadOnlyList<InternalPackageMapping> mappings)
    {
        var e = source.Evidence;
        var lines = new List<string>
        {
            $"## Dependencies — {source.Source}", "",
            $"Source evidence analyzed {source.AnalyzedAt:yyyy-MM-dd HH:mm} UTC. Declared dependencies only: resolved, deployed and runtime-loaded versions are other evidence layers.", "",
            "### Inventory", "",
        };
        foreach (var g in e.Dependencies.GroupBy(d => DependencyCoordinate.EcosystemOf(d.Manager)).OrderBy(g => g.Key, StringComparer.Ordinal))
            lines.Add($"- {g.Key}: {g.Select(d => d.PackageName.ToLowerInvariant()).Distinct().Count()} package(s), {g.Count()} declaration(s)");
        if (e.UnsupportedManifests.Count > 0) lines.Add($"- Not read natively: {string.Join(", ", e.UnsupportedManifests)}");
        lines.AddRange(["", "### Internal packages", ""]);
        if (mappings.Count == 0) lines.Add("No internal package producer/consumer relationship in the selected sources.");
        foreach (var m in mappings)
            lines.Add($"- {m.Package.Name} {m.ProducerVersion ?? "(version not declared)"} produced by {m.ProducerSource}: {m.Consumers.Count} consumer(s), {m.Aligned} mapped, {m.VersionDifferences} mapped with version difference");
        lines.AddRange(["", "### Update policy", "", e.RenovateFiles.Count == 0 ? "No Renovate configuration in this source." : $"Renovate configuration: {string.Join(", ", e.RenovateFiles.Select(f => f.Path))} (policy only; execution not assessed).", "",
            "Security and license coverage come from Dependency Review runs; none is implied by this summary."]);
        return string.Join('\n', lines);
    }
}
