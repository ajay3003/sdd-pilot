using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BirkNext.ContractSchemaEvidence;
using BirkNext.Integrations;
using BirkNext.Interpretation;
using BirkNext.TestEvidence;

namespace BirkNext.Api.Services.CanonicalInterpretation;

/// <summary>
/// Reads existing domain evidence into correlation input. It never re-parses source: Source Analysis snapshots supply packages, services,
/// endpoints (code-risk routes), messaging channels, contracts and canonical tests; Delivery snapshots supply features; domain profiles and test
/// packs supply declared aliases. Existing identity rules are reused (<see cref="ContractEvidenceQueries.Identity"/>, test StableIdentity,
/// <see cref="IntegrationTopology.ComponentNames.Stated"/>) rather than re-implemented.
/// </summary>
public static class CanonicalEvidenceAdapter
{
    public static CorrelationInput FromSourceSnapshot(string scope, IqrSourceSnapshot snapshot)
    {
        var sid = snapshot.Id.ToString();
        var entities = new List<CanonicalEntityFact>();
        var relations = new List<CanonicalRelationFact>();
        var packageKey = PackageIdentity(snapshot);
        var capabilities = PackageCapabilities(snapshot);
        var packageAttributes = new Dictionary<string, string> { ["archiveSha256"] = snapshot.Archive.Sha256, ["archive"] = snapshot.Archive.FileName };
        if (capabilities.Count > 0) packageAttributes["capabilities"] = string.Join(',', capabilities.Select(c => c.Capability));
        var package = new CanonicalEntityFact(CanonicalEntityType.Package, packageKey, snapshot.Repository?.DisplayName ?? snapshot.Archive.FileName,
            snapshot.Repository?.Key ?? snapshot.Archive.FileName, snapshot.Repository is null ? "archive file name (no repository identity recorded)" : $"repository identity ({snapshot.Repository.Basis})",
            [new("archive", $"sha256:{snapshot.Archive.Sha256}", snapshot.Archive.FileName, sid), .. capabilities.Select(c => new CorrelationEvidence("capability", c.Capability, c.Evidence, sid))],
            Attributes: packageAttributes);
        entities.Add(package);

        var components = snapshot.Architecture?.Components ?? [];
        var componentNames = components.ToDictionary(c => c.Id, c => c.Name, StringComparer.Ordinal);
        foreach (var c in components.OrderBy(c => c.Name, StringComparer.Ordinal))
        {
            var service = new CanonicalEntityFact(CanonicalEntityType.Service, CanonicalKeys.Normalize(c.Name), c.Name, c.Name, "declared project/component name",
                [new("component", c.SourceProject, c.ComponentType.ToString(), sid)]);
            entities.Add(service);
            relations.Add(new(package.Key, service.Key, CanonicalRelationType.Contains, RelationResolution.Explicit, CorrelationConfidence.Explicit, "CR-N-PACKAGE",
                [new("component", c.SourceProject, null, sid)], "The package's source contains this component's project."));
        }

        var symbols = new List<SourceSymbolInput>();
        // Declared production types (code-risk analyzer v3+): the general source-symbol table, identity = package + namespace + type.
        foreach (var t in snapshot.CodeRiskEvidence?.Symbols ?? [])
            symbols.Add(new(packageKey, t.File, t.Name, t.Namespace, t.Kind, null, null, [], [], OwnerOf(t.File, components), sid));
        foreach (var e in (snapshot.CodeRiskEvidence?.Endpoints ?? []).Where(e => e.Kind == "HTTP" && !string.IsNullOrWhiteSpace(e.Route)))
        {
            var method = e.Display.Split(' ', 2)[0];
            var owner = OwnerOf(e.File, components);
            var parts = e.Key.Split('|');
            var symbol = parts.Length > 2 && parts[2] != "minimal" ? parts[1] : $"minimal {method} {e.Route}";
            symbols.Add(new(packageKey, e.File, symbol, null, parts.Length > 2 && parts[2] == "minimal" ? "Endpoint" : "Controller", method, e.Route, [], [], owner, sid));
        }
        // GraphQL resolvers: the operation is the schema root (Query/Mutation/Subscription) + field; the resolver method implements it.
        foreach (var e in (snapshot.CodeRiskEvidence?.Endpoints ?? []).Where(e => e.Kind == "GraphQL" && !string.IsNullOrWhiteSpace(e.Route)))
        {
            var parts = e.Key.Split('|');
            if (parts.Length < 3) continue;
            var root = parts[2] is "Query" or "Mutation" or "Subscription" ? parts[2].ToUpperInvariant() : "QUERY";
            symbols.Add(new(packageKey, e.File, parts[1], null, "GraphQL", root, e.Route, [], [], OwnerOf(e.File, components), sid));
        }

        var channels = (snapshot.Architecture?.MessagingChannels ?? [])
            .Where(c => !string.IsNullOrWhiteSpace(c.EntityName) && !c.NameUnresolved)
            .Select(c => new MessagingChannelInput(c.Type.ToString(), c.EntityName!,
                [.. c.Producers.Select(p => componentNames.GetValueOrDefault(p.ComponentId)).OfType<string>()],
                [.. c.Consumers.Select(p => componentNames.GetValueOrDefault(p.ComponentId)).OfType<string>()],
                c.Producers.Concat(c.Consumers).SelectMany(p => p.Evidence).Select(x => $"{x.File}:{x.Line}").FirstOrDefault() ?? c.Id, sid))
            .ToList();

        var contracts = new List<ContractOccurrenceInput>();
        foreach (var c in snapshot.EvidenceDomains?.Contracts?.Contracts ?? [])
        {
            var hash = StructuralFingerprint(c);
            if (hash is null) continue; // no recorded structure: an occurrence without content cannot be compared, and is not guessed
            var producer = c.Producer is null ? null : componentNames.GetValueOrDefault(c.Producer) ?? c.Producer;
            contracts.Add(new(c.Type.ToString(), packageKey, snapshot.Repository?.Key, c.File, sid, hash, c.XmlSchema?.TargetNamespace,
                c.XmlSchema?.ExplicitVersion ?? c.Version, c.Name, snapshot.Commit, producer is null ? null : [producer], ContractEvidenceQueries.Identity(c)));
        }

        var tests = TestEvidenceQueries.FromInventory(snapshot.TestInventory)
            .Select(t => new TestInput(packageKey, t.File, t.FullyQualifiedName, t.ClassName,
                [.. t.Requirements.Where(r => r.Confidence is TestMappingConfidence.Explicit or TestMappingConfidence.Strong).Select(r => r.RequirementId)],
                [.. t.Requirements.Where(r => r.Confidence is TestMappingConfidence.Candidate or TestMappingConfidence.Inferred or TestMappingConfidence.Ambiguous).Select(r => r.RequirementId)],
                sid, t.Id))
            .ToList();

        var systems = components.SelectMany(c => IntegrationTopology.ComponentNames.Stated(c.Name) is { Count: 3 } stated
                ? [new SystemIdentityInput(CanonicalEntityType.Service, c.Name, [stated[1], stated[2]], $"name stated in source ({c.SourceProject})")]
                : Array.Empty<SystemIdentityInput>())
            .ToList();

        // Shared libraries: packages this source publishes, and internal shared projects; uses from project/package references.
        var libraries = new List<LibraryInput>();
        foreach (var published in snapshot.DependencyEvidence?.PublishedPackages ?? [])
            libraries.Add(new(packageKey, published.PackageId, published.Project, sid));
        foreach (var shared in (snapshot.Architecture?.SharedLibraries ?? []).Where(l => l.Internal))
            libraries.Add(new(packageKey, shared.Name, shared.SourceProject, sid));
        var uses = new List<LibraryUseInput>();
        foreach (var shared in (snapshot.Architecture?.SharedLibraries ?? []).Where(l => l.Internal))
            foreach (var component in shared.ReferencedByComponents)
                if (componentNames.GetValueOrDefault(component) is { } user) uses.Add(new(user, shared.Name, $"project reference to {shared.SourceProject}", sid));
        foreach (var d in snapshot.DependencyEvidence?.Dependencies ?? [])
            foreach (var file in d.ReferencedBy.Append(d.OwnerFile).Where(f => f.EndsWith("proj", StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.Ordinal))
                if (components.FirstOrDefault(c => string.Equals(c.SourceProject.Replace('\\', '/'), file.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)) is { } owner)
                    uses.Add(new(owner.Name, d.PackageName, $"package reference in {file}", sid));

        return new(scope, [], contracts, symbols, [], tests, systems, entities, relations, channels, null, libraries, uses);
    }

    /// <summary>
    /// What a package provides as evidence, from what Source Analysis found in it (file roles and domain evidence). A package can provide several
    /// (Terraform-iac-meldingsutveksler: Infrastructure + Contracts); none is exclusive, and nothing is derived from the package name.
    /// </summary>
    public static IReadOnlyList<(string Capability, string Evidence)> PackageCapabilities(IqrSourceSnapshot snapshot)
    {
        var roles = (snapshot.EvidenceDomains?.FileRoles ?? []).ToDictionary(r => r.Role, r => r.Files);
        int Files(BirkNext.SourceDomains.SourceFileRole role) => roles.GetValueOrDefault(role);
        var list = new List<(string, string)>();
        var production = snapshot.Projects.Count(p => !p.IsTest);
        var published = snapshot.DependencyEvidence?.PublishedPackages.Count ?? 0;
        var hosts = snapshot.Architecture?.Components.Count(c => !string.IsNullOrWhiteSpace(c.EntryPoint)) ?? 0;
        if (production > 0 && (hosts > 0 || published == 0)) list.Add(("ApplicationSource", $"{production} production project(s), {hosts} with an entry point"));
        if (published > 0) list.Add(("SharedSource", $"{published} published package(s)"));
        var infra = snapshot.EvidenceDomains?.Infrastructure;
        if (Files(BirkNext.SourceDomains.SourceFileRole.InfrastructureAsCode) > 0 || infra?.Resources.Count > 0)
            list.Add(("Infrastructure", $"{Files(BirkNext.SourceDomains.SourceFileRole.InfrastructureAsCode)} IaC file(s), {infra?.Resources.Count ?? 0} resource(s)"));
        var contracts = snapshot.EvidenceDomains?.Contracts?.Contracts.Count ?? 0;
        if (contracts > 0 || Files(BirkNext.SourceDomains.SourceFileRole.Contract) > 0)
            list.Add(("Contracts", $"{contracts} contract(s), {Files(BirkNext.SourceDomains.SourceFileRole.Contract)} contract file(s)"));
        if (Files(BirkNext.SourceDomains.SourceFileRole.Pipeline) > 0 || snapshot.EvidenceDomains?.CiCd?.Pipelines.Count > 0)
            list.Add(("Pipelines", $"{snapshot.EvidenceDomains?.CiCd?.Pipelines.Count ?? 0} pipeline definition(s)"));
        var tests = snapshot.TestInventory?.Definitions.Count ?? 0;
        if (tests > 0 || snapshot.Projects.Any(p => p.IsTest)) list.Add(("Tests", $"{tests} test(s), {snapshot.Projects.Count(p => p.IsTest)} test project(s)"));
        if (Files(BirkNext.SourceDomains.SourceFileRole.Configuration) > 0) list.Add(("Configuration", $"{Files(BirkNext.SourceDomains.SourceFileRole.Configuration)} configuration file(s)"));
        if (Files(BirkNext.SourceDomains.SourceFileRole.Documentation) > 0) list.Add(("Documentation", $"{Files(BirkNext.SourceDomains.SourceFileRole.Documentation)} documentation file(s)"));
        return list;
    }

    /// <summary>The package identity: the repository identity recorded at analysis, else the archive name without version/copy suffixes.</summary>
    public static string PackageIdentity(IqrSourceSnapshot snapshot) =>
        CanonicalKeys.Normalize(snapshot.Repository?.Key ?? System.Text.RegularExpressions.Regex.Replace(
            System.IO.Path.GetFileNameWithoutExtension(snapshot.Archive.FileName), @"\s*\(\d+\)$", ""));

    static string? OwnerOf(string file, IReadOnlyList<BirkNext.SourceArchitecture.ArchitectureComponent> components)
    {
        var path = file.Replace('\\', '/');
        return components
            .Select(c => (c.Name, Dir: System.IO.Path.GetDirectoryName(c.SourceProject.Replace('\\', '/'))?.Replace('\\', '/') ?? ""))
            .Where(c => c.Dir.Length > 0 && path.StartsWith(c.Dir + "/", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(c => c.Dir.Length)
            .Select(c => c.Name)
            .FirstOrDefault();
    }

    /// <summary>
    /// A fingerprint of the contract's parsed content (operations, types, XSD structure, declared version and namespace), independent of path and
    /// producer. Null when the analyzer recorded no structure (then two contracts cannot be shown to be identical). This is a structural
    /// fingerprint of what Source Analysis parsed, not a byte hash of the file.
    /// </summary>
    public static string? StructuralFingerprint(BirkNext.SourceDomains.SourceContract c)
    {
        if (c.Operations.Count == 0 && c.Types.Count == 0 && c.XmlSchema is null) return null;
        var content = JsonSerializer.Serialize(new { c.Type, c.Version, c.Operations, c.Types, c.XmlSchema });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
    }

    /// <summary>Delivery features as work-item intents (cases G/H).</summary>
    public static IReadOnlyList<WorkItemIntentInput> WorkItems(IEnumerable<(string Module, string FeatureKey, string Title, IReadOnlyList<string> Stories, string? Path)> features, string sourceSnapshotId) =>
        [.. features.Select(f => new WorkItemIntentInput(f.Module, f.FeatureKey, f.Title, f.Stories, f.Path, sourceSnapshotId))];

    /// <summary>Declared aliases from domain profiles and test packs (explicit, never heuristic).</summary>
    public static IReadOnlyList<SystemIdentityInput> DeclaredSystems(IEnumerable<IntegrationTopology.ITopologyProfile> profiles, IEnumerable<IntegrationAlias> packAliases)
    {
        var list = new List<SystemIdentityInput>();
        // A name a domain profile declares for a component (its name or one of its declared aliases) names that component. A test pack whose logical
        // name is such a declared name ("Skolenærværsrapport", declared by the profile for Skoletjenesten) adds its aliases to that component instead of
        // creating a second system that would then conflict with it. Exact declared names only; never similarity.
        var declaredName = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var profile in profiles)
            foreach (var c in profile.Components)
            {
                list.Add(new(CanonicalEntityType.System, c.Name, [.. c.Aliases], $"domain profile {profile.Id}"));
                foreach (var name in c.Aliases.Prepend(c.Name)) declaredName.TryAdd(CanonicalKeys.Normalize(name), c.Name);
            }
        foreach (var group in packAliases.GroupBy(a => a.Logical, StringComparer.OrdinalIgnoreCase))
        {
            var owner = declaredName.GetValueOrDefault(CanonicalKeys.Normalize(group.Key));
            list.Add(new(CanonicalEntityType.System, owner ?? group.Key, [.. group.Select(a => a.Technical).Append(owner is null ? null : group.Key).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase)],
                owner is null ? "test pack expected aliases" : $"test pack expected aliases (pack name \"{group.Key}\" is declared by the domain profile for {owner})"));
        }
        // One entity per declared name: profiles and packs that name the same system merge their alias lists (names equal after normalization).
        return [.. list.GroupBy(s => CanonicalKeys.Normalize(s.Name)).Select(g => new SystemIdentityInput(CanonicalEntityType.System, g.First().Name,
            [.. g.SelectMany(s => s.DeclaredAliases).Distinct(StringComparer.OrdinalIgnoreCase)], string.Join("; ", g.Select(s => s.Source).Distinct())))];
    }

    /// <summary>Merges several inputs (one per source snapshot) into one scope input.</summary>
    public static CorrelationInput Merge(string scope, IEnumerable<CorrelationInput> inputs)
    {
        var all = inputs.ToList();
        return new(scope,
            [.. all.SelectMany(i => i.WorkItems)], [.. all.SelectMany(i => i.Contracts)], [.. all.SelectMany(i => i.Symbols)], [.. all.SelectMany(i => i.Operations)],
            [.. all.SelectMany(i => i.Tests)], [.. all.SelectMany(i => i.Systems)], [.. all.SelectMany(i => i.AdditionalEntities ?? [])],
            [.. all.SelectMany(i => i.AdditionalRelations ?? [])], [.. all.SelectMany(i => i.Channels ?? [])], [.. all.SelectMany(i => i.Tasks ?? [])],
            [.. all.SelectMany(i => i.Libraries ?? [])], [.. all.SelectMany(i => i.LibraryUses ?? [])], [.. all.SelectMany(i => i.Structures ?? [])],
            [.. all.SelectMany(i => i.GuideScenarios ?? []).DistinctBy(s => (s.GuideKey, s.ScenarioId))]);
    }
}
