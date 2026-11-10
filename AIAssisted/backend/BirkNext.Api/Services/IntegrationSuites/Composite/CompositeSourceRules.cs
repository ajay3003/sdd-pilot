using System.Text.RegularExpressions;
using BirkNext.Api.Services.IntegrationJourneys;
using BirkNext.Integrations;
using BirkNext.IntegrationSuites;
using BirkNext.SourceDomains;

namespace BirkNext.Api.Services.IntegrationSuites.Composite;

/// <summary>
/// The pure rules of multi-source suites (no I/O): the compact facts of one snapshot, stage → source discovery, cross-stage static compatibility
/// and source-coverage readiness. A source is recognised by words of its top folders, project and component names (the same word matcher as the
/// journey packs' source roles), never by archive names or text similarity; exclusion words rule a folder out (an MM folder named after MU is
/// not MU). Compatibility is what the sources declare — never a runtime claim.
/// </summary>
public static partial class CompositeSourceRules
{
    // ── Facts ───────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    public sealed record ContractFact(string Id, string File, string Module, string Name, SourceContractType Type, string? TargetNamespace,
        List<string> Operations, List<string> FieldNames, List<string> ValidatorModules, List<string> ReferencingModules);

    public sealed record ChannelFact(string Module, string Role, string Name);

    /// <summary>The compact, content-free facts of one snapshot that discovery and compatibility need (kept per snapshot id; immutable).</summary>
    public sealed record SnapshotFacts
    {
        public Guid Id { get; init; }
        public string ProjectKey { get; init; } = "";
        public string ArchiveFileName { get; init; } = "";
        public string ArchiveSha256 { get; init; } = "";
        public DateTimeOffset AnalyzedAt { get; init; }
        public string Status { get; init; } = "";
        /// <summary>Top folder → the names that describe it (the folder, its projects and components).</summary>
        public Dictionary<string, List<string>> Units { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public bool HasApplicationCode { get; init; }
        public bool HasInfrastructure { get; init; }
        public List<ContractFact> Contracts { get; init; } = [];
        public List<ChannelFact> Channels { get; init; } = [];
        /// <summary>Retained dataset contracts: (top folder, name).</summary>
        public List<(string Module, string Name)> DatasetContracts { get; init; } = [];
    }

    public static SnapshotFacts Facts(IqrSourceSnapshot s, string projectKey, IReadOnlyList<(string Path, string Name)> datasetContracts)
    {
        var paths = (s.TargetIndex?.Files ?? []).Select(f => f.RelativePath).ToList();
        if (paths.Count == 0) paths = [.. s.Projects.Select(p => p.Path)];
        var root = WrappingRoot(paths);
        string Top(string path)
        {
            var p = path.Replace('\\', '/').TrimStart('/');
            if (root is not null && p.StartsWith(root + "/", StringComparison.Ordinal)) p = p[(root.Length + 1)..];
            var i = p.IndexOf('/');
            return i < 0 ? "" : p[..i]; // a file in the archive root belongs to no folder
        }
        var units = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        void Add(string top, string name)
        {
            if (top.Length == 0 || top.StartsWith('.')) return;
            if (!units.TryGetValue(top, out var names)) units[top] = names = [top];
            if (name.Length > 0 && !names.Contains(name, StringComparer.Ordinal)) names.Add(name);
        }
        foreach (var path in paths) Add(Top(path), "");
        var projectTop = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in s.Projects)
        {
            var top = Top(project.Path);
            Add(top, project.Name);
            projectTop.TryAdd(project.Name, top);
        }
        var componentTop = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var component in s.Architecture?.Components ?? [])
        {
            var top = projectTop.TryGetValue(component.SourceProject, out var t) ? t : component.SourceProject.Length > 0 ? Top(component.SourceProject) : "";
            if (top.Length == 0) continue;
            Add(top, component.Name);
            componentTop[component.Id] = top;
        }
        var contracts = (s.EvidenceDomains?.Contracts?.Contracts ?? []).Select(c => new ContractFact(
            c.Id, c.File, Top(c.File), c.Name, c.Type, c.XmlSchema?.TargetNamespace,
            [.. c.Operations.Select(o => o.Name).Take(200)],
            [.. c.Types.SelectMany(t => t.Fields.Select(f => f.Name)).Concat(c.Operations.SelectMany(o => o.Parameters)).Distinct(StringComparer.OrdinalIgnoreCase).Take(400)],
            [.. c.CodeReferences.Where(r => r.Kind == "Validator").Select(r => Top(r.File)).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)],
            [.. c.CodeReferences.Select(r => Top(r.File)).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)])).ToList();
        var channels = new List<ChannelFact>();
        foreach (var channel in s.Architecture?.MessagingChannels ?? [])
        {
            var name = channel.EntityName ?? channel.Name;
            foreach (var p in channel.Producers) if (componentTop.TryGetValue(p.ComponentId, out var top)) channels.Add(new(top, "Publish", name));
            foreach (var c in channel.Consumers) if (componentTop.TryGetValue(c.ComponentId, out var top)) channels.Add(new(top, "Consume", name));
        }
        var infra = s.EvidenceDomains?.Infrastructure is { } i && (i.Formats.Count > 0 || i.Resources.Count > 0);
        return new SnapshotFacts
        {
            Id = s.Id, ProjectKey = projectKey, ArchiveFileName = s.Archive.FileName, ArchiveSha256 = s.Archive.Sha256, AnalyzedAt = s.AnalyzedAt, Status = s.Status.ToString(),
            Units = units, HasApplicationCode = s.Projects.Any(p => !p.IsTest) || (s.Architecture?.Components.Count ?? 0) > 0, HasInfrastructure = infra,
            Contracts = contracts, Channels = [.. channels.Distinct()],
            DatasetContracts = [.. datasetContracts.Select(d => (Top(d.Path), d.Name))],
        };
    }

    /// <summary>The single folder every path starts with, when the archive wraps everything in one folder (it is not a module).</summary>
    private static string? WrappingRoot(IReadOnlyList<string> paths)
    {
        var normalized = paths.Select(p => p.Replace('\\', '/').TrimStart('/')).Where(p => p.Contains('/')).ToList();
        if (normalized.Count == 0 || normalized.Count != paths.Count) return null;
        var first = normalized.Select(p => p[..p.IndexOf('/')]).Distinct(StringComparer.Ordinal).ToList();
        return first.Count == 1 ? first[0] : null;
    }

    // ── Recognition ─────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The folders of a snapshot that hold a stage's source: a name word equals a stage token and no name word equals an excluded token.</summary>
    public static List<string> Modules(SnapshotFacts facts, StageSourceRequirement requirement)
    {
        if (requirement.Tokens.Count == 0) return [];
        return [.. facts.Units.Where(u => ArchitectureRuleEvaluator.MatchesAny(requirement.Tokens, [.. u.Value])
                && !(requirement.ExcludedTokens.Count > 0 && ArchitectureRuleEvaluator.MatchesAny(requirement.ExcludedTokens, [.. u.Value])))
            .Select(u => u.Key).Order(StringComparer.OrdinalIgnoreCase)];
    }

    private static List<string> MatchedNames(SnapshotFacts facts, IReadOnlyList<string> modules, StageSourceRequirement requirement) =>
        [.. modules.SelectMany(m => facts.Units[m].Where(n => ArchitectureRuleEvaluator.MatchesAny(requirement.Tokens, n)).Select(n => n == m ? $"Folder: {m}" : $"Project/component: {n}"))
            .Distinct(StringComparer.Ordinal).Take(6)];

    public static List<string> Artifacts(SnapshotFacts facts, IReadOnlyCollection<string> modules, bool dataset)
    {
        var inScope = facts.Contracts.Where(c => modules.Contains(c.Module, StringComparer.OrdinalIgnoreCase)).ToList();
        var list = new List<string>();
        list.AddRange(inScope.Where(c => c.Type is SourceContractType.OpenApi or SourceContractType.GraphQlSchema or SourceContractType.AsyncApi)
            .Select(c => $"{c.Type}: {c.File} ({c.Operations.Count} operations)").Take(6));
        list.AddRange(inScope.Where(c => c.Type == SourceContractType.XmlSchema && c.TargetNamespace is { Length: > 0 }).Select(c => $"XSD: {c.TargetNamespace}").Distinct().Take(6));
        if (dataset) list.AddRange(facts.DatasetContracts.Where(d => modules.Contains(d.Module, StringComparer.OrdinalIgnoreCase)).Select(d => $"Dataset contract: {d.Name}").Distinct().Take(4));
        return list;
    }

    // ── Discovery ───────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Candidate sources per stage. Only the newest snapshot of each project competes (older ones are history); approved associations make a stage
    /// CONFIRMED; one candidate project = CANDIDATE; several = AMBIGUOUS; none = MISSING — a stage covered only by a built-in Test Pack reference is
    /// CONFIRMED by that reference. Snapshots that match no stage are supporting evidence (Infrastructure as Code, libraries), never alternatives.
    /// </summary>
    public static CompositeDiscovery Discover(IntegrationSuiteScope scope, IReadOnlyList<SnapshotFacts> all, CompositeSourceContext? context)
    {
        var current = all.Where(f => f.Status != nameof(SourceAnalysisStatus.Failed)).GroupBy(f => f.ProjectKey, StringComparer.Ordinal)
            .Select(g => (Newest: g.OrderByDescending(x => x.AnalyzedAt).ThenByDescending(x => x.Id).First(), Older: g.Count() - 1)).ToList();
        var stages = new List<CompositeStageDiscovery>();
        var matched = new HashSet<Guid>();
        foreach (var template in scope.Stages)
        {
            var requirement = template.Source;
            if (requirement is null) continue;
            var candidates = new List<CompositeSourceCandidate>();
            foreach (var (facts, older) in current)
            {
                var modules = Modules(facts, requirement);
                if (requirement.DatasetContractEvidence)
                    modules = [.. modules.Concat(facts.DatasetContracts.Select(d => d.Module).Where(m => m.Length > 0)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)];
                if (modules.Count == 0) continue;
                matched.Add(facts.Id);
                var artifacts = Artifacts(facts, modules, requirement.DatasetContractEvidence);
                var names = MatchedNames(facts, modules, requirement);
                if (requirement.DatasetContractEvidence && names.Count == 0) names = [.. facts.DatasetContracts.Select(d => $"Dataset contract: {d.Name}").Distinct().Take(3)];
                candidates.Add(Candidate(facts, older, CompositeSourceKind.Application, names, modules, artifacts));
            }
            var approved = context?.Snapshots.Where(a => a.Approval == CompositeApproval.Approved && a.Stages.Contains(template.StageId)).ToList() ?? [];
            var projects = candidates.Select(c => c.ProjectKey).Distinct(StringComparer.Ordinal).Count();
            var cls = approved.Count > 0 ? CompositeMatchClass.Confirmed
                : candidates.Count == 0 ? requirement.ReferenceSource is not null ? CompositeMatchClass.Confirmed : CompositeMatchClass.Missing
                : projects > 1 ? CompositeMatchClass.Ambiguous : CompositeMatchClass.Candidate;
            var missing = new List<string>();
            if (candidates.Count == 0 && requirement.ReferenceSource is null) missing.Add($"No imported source recognised for {template.Label} (looked for {string.Join(", ", requirement.Tokens)}).");
            if (candidates.Count == 0 && requirement.ReferenceSource is not null) missing.Add($"No imported source: only the built-in reference ({requirement.ReferenceSource}).");
            if (template.StageId == "bufdata" && candidates.All(c => !c.MatchedOn.Any(m => m.StartsWith("Folder", StringComparison.Ordinal) || m.StartsWith("Project", StringComparison.Ordinal))))
                missing.Add("No consumer-side source of the downstream recipient; only the producer's dataset contract.");
            stages.Add(new CompositeStageDiscovery
            {
                StageId = template.StageId, Label = template.Label, Mandatory = requirement.Mandatory, Class = cls, Candidates = [.. candidates.OrderBy(c => c.ProjectKey, StringComparer.Ordinal)],
                ReferenceSource = requirement.ReferenceSource, Missing = missing,
                Explanation = cls switch
                {
                    CompositeMatchClass.Confirmed when approved.Count > 0 => $"Approved: {string.Join(", ", approved.Select(a => a.ProjectKey).Distinct())}.",
                    CompositeMatchClass.Confirmed => $"Covered by the built-in {requirement.ReferenceSource}.",
                    CompositeMatchClass.Ambiguous => $"{projects} projects claim this stage — choose one; nothing is merged.",
                    CompositeMatchClass.Candidate => "One candidate project — a person must approve it.",
                    _ => "No recognised source.",
                },
            });
        }
        var supporting = current.Where(c => !matched.Contains(c.Newest.Id))
            .Select(c => Candidate(c.Newest, c.Older, c.Newest.HasInfrastructure && !c.Newest.HasApplicationCode ? CompositeSourceKind.Infrastructure : CompositeSourceKind.Library,
                c.Newest.HasInfrastructure ? ["Infrastructure as Code"] : ["No stage of this integration recognised"], [], []))
            .ToList();
        // A snapshot that failed analysis only because it has no application code (Infrastructure as Code alone) is still supporting evidence: listed with
        // its Failed status, never a stage source and never associable.
        var knownProjects = current.Select(c => c.Newest.ProjectKey).ToHashSet(StringComparer.Ordinal);
        supporting.AddRange(all.Where(f => f.Status == nameof(SourceAnalysisStatus.Failed) && f.HasInfrastructure && !knownProjects.Contains(f.ProjectKey))
            .GroupBy(f => f.ProjectKey, StringComparer.Ordinal)
            .Select(g => (Newest: g.OrderByDescending(x => x.AnalyzedAt).ThenByDescending(x => x.Id).First(), Older: g.Count() - 1))
            .Select(c => Candidate(c.Newest, c.Older, CompositeSourceKind.Infrastructure, ["Infrastructure as Code (analysis status Failed: no application code)"], [], [])));
        supporting = [.. supporting.OrderBy(c => c.Kind).ThenBy(c => c.ProjectKey, StringComparer.Ordinal)];
        return new CompositeDiscovery
        {
            ScopeId = scope.ScopeId, ScopeName = scope.DisplayName, Stages = stages, Supporting = supporting, SnapshotsConsidered = all.Count,
            Limitations = ["Recognition uses folder, project and component names and the contracts in those folders; it is static source evidence, not a deployed topology."],
        };
    }

    private static CompositeSourceCandidate Candidate(SnapshotFacts f, int older, CompositeSourceKind kind, List<string> matchedOn, List<string> modules, List<string> artifacts) => new()
    {
        SnapshotId = f.Id, ProjectKey = f.ProjectKey, ArchiveFileName = f.ArchiveFileName, ArchiveSha256 = f.ArchiveSha256, AnalyzedAt = f.AnalyzedAt, AnalysisStatus = f.Status,
        Kind = kind, MatchedOn = matchedOn, Modules = modules, Artifacts = artifacts, OlderSnapshots = older,
        Confidence = kind != CompositeSourceKind.Application ? "Supporting" : artifacts.Count > 0 ? "High (names and stage artifacts)" : "Medium (names only)",
    };

    // ── Compatibility ───────────────────────────────────────────────────────────────────────────────────────────────────────────

    [GeneratedRegex(@"[._-]v?\d+([._]\d+)*.*$", RegexOptions.IgnoreCase)] private static partial Regex VersionSuffix();

    /// <summary>The schema family of a namespace: the namespace without its version suffix (…skolenaervaer.v2.0.1.flat → …skolenaervaer).</summary>
    public static string Family(string ns) => VersionSuffix().Replace(ns.TrimEnd('/'), "");

    public sealed record Side(string StageId, List<(SnapshotFacts Facts, List<string> Modules, bool Approved)> Sources, string? Reference);

    /// <summary>Static compatibility of each consecutive transition, per dimension, from the sources associated with the two stages.</summary>
    public static List<CompositeTransition> Compatibility(IntegrationSuiteScope scope, IReadOnlyDictionary<string, Side> sides, IReadOnlyList<CompositeTransitionAcceptance> accepted,
        string? acceptedBy)
    {
        var list = new List<CompositeTransition>();
        var stages = scope.Stages.Where(s => s.Source is not null).ToList();
        for (var i = 0; i + 1 < stages.Count; i++)
        {
            var from = stages[i]; var to = stages[i + 1];
            var channel = from.Source!.ChannelToNext;
            var a = sides.TryGetValue(from.StageId, out var x) ? x : new Side(from.StageId, [], from.Source.ReferenceSource);
            var b = sides.TryGetValue(to.StageId, out var y) ? y : new Side(to.StageId, [], to.Source!.ReferenceSource);
            var dims = new List<CompatibilityDimension> { Identity(a, b) };
            if (channel is not TransitionChannel.Internal)
            {
                dims.Add(Xml(a, b, channel));
                if (channel is TransitionChannel.HttpApi or TransitionChannel.Webhook) dims.Add(Api(a, b));
                if (channel is TransitionChannel.Message) dims.Add(Messaging(a, b));
                if (channel is TransitionChannel.Dataset or TransitionChannel.Message && to.Source!.DatasetContractEvidence) dims.Add(Dataset(b));
                dims.Add(Correlation(a, b, from.Source.CorrelationFields));
            }
            dims.Add(new("Authentication", TransitionCompatibility.NotAssessed, "Bound per Target Environment when a run is planned; never inferred from source.", []));
            dims.Add(new("Environment", TransitionCompatibility.NotAssessed, "Source evidence does not prove both sides are deployed together.", []));
            var key = $"{from.StageId}->{to.StageId}";
            var acceptance = accepted.FirstOrDefault(t => t.TransitionKey == key);
            var overall = Overall(dims);
            list.Add(new CompositeTransition
            {
                Key = key, FromStage = from.StageId, ToStage = to.StageId, Channel = channel, Dimensions = dims, Overall = overall,
                Accepted = acceptance is not null && overall is not TransitionCompatibility.Compatible, AcceptedBy = acceptance is null ? null : acceptedBy, AcceptedReason = acceptance?.Reason,
            });
        }
        return list;
    }

    public static TransitionCompatibility Overall(IReadOnlyList<CompatibilityDimension> dims)
    {
        var assessed = dims.Where(d => d.State != TransitionCompatibility.NotAssessed).ToList();
        if (assessed.Any(d => d.State == TransitionCompatibility.Incompatible)) return TransitionCompatibility.Incompatible;
        if (assessed.Any(d => d.State == TransitionCompatibility.MissingEvidence)) return TransitionCompatibility.MissingEvidence;
        if (assessed.Any(d => d.State == TransitionCompatibility.PotentiallyCompatible)) return TransitionCompatibility.PotentiallyCompatible;
        return assessed.Count > 0 ? TransitionCompatibility.Compatible : TransitionCompatibility.NotAssessed;
    }

    private static CompatibilityDimension Identity(Side a, Side b)
    {
        if (a.Sources.Count == 0 && a.Reference is null || b.Sources.Count == 0 && b.Reference is null)
            return new("Source version", TransitionCompatibility.MissingEvidence, "A side has no associated source.", []);
        var shared = a.Sources.Select(s => s.Facts.Id).Intersect(b.Sources.Select(s => s.Facts.Id)).ToList();
        var evidence = a.Sources.Concat(b.Sources).Select(s => $"{s.Facts.ProjectKey} · {s.Facts.ArchiveFileName} · {Short(s.Facts.ArchiveSha256)}").Distinct().ToList();
        if (shared.Count > 0) return new("Source version", TransitionCompatibility.Compatible, "Both stages come from the same source snapshot.", evidence);
        if (a.Sources.Count == 0 || b.Sources.Count == 0) return new("Source version", TransitionCompatibility.PotentiallyCompatible, "One side is the built-in Test Pack reference (no imported source).", evidence);
        var approved = a.Sources.All(s => s.Approved) && b.Sources.All(s => s.Approved);
        return new("Source version", approved ? TransitionCompatibility.Compatible : TransitionCompatibility.PotentiallyCompatible,
            approved ? "Different projects; both associations approved for this integration (the versions were reviewed together)." : "Different projects; the association is not approved yet.", evidence);
    }

    private static List<(ContractFact Contract, SnapshotFacts Facts)> InModules(Side side, Func<ContractFact, bool> predicate) =>
        [.. side.Sources.SelectMany(s => s.Facts.Contracts.Where(c => s.Modules.Contains(c.Module, StringComparer.OrdinalIgnoreCase) && predicate(c)).Select(c => (c, s.Facts)))];

    private static CompatibilityDimension Xml(Side a, Side b, TransitionChannel channel)
    {
        var xa = InModules(a, c => c.Type == SourceContractType.XmlSchema && c.TargetNamespace is { Length: > 0 });
        var xb = InModules(b, c => c.Type == SourceContractType.XmlSchema && c.TargetNamespace is { Length: > 0 });
        var messageLike = channel is TransitionChannel.Message or TransitionChannel.Webhook;
        if (xa.Count == 0 || xb.Count == 0)
            return new("Message schema (XML)", messageLike ? TransitionCompatibility.MissingEvidence : TransitionCompatibility.NotAssessed,
                xa.Count == 0 && xb.Count == 0 ? "Neither side declares an XSD in its stage folders." : $"Only the {(xa.Count > 0 ? "sending" : "receiving")} side declares an XSD in its stage folders.",
                [.. xa.Concat(xb).Select(x => $"{x.Contract.File} ({x.Contract.TargetNamespace})").Take(6)]);
        var na = xa.Select(x => x.Contract.TargetNamespace!).Distinct(StringComparer.Ordinal).ToList();
        var nb = xb.Select(x => x.Contract.TargetNamespace!).Distinct(StringComparer.Ordinal).ToList();
        var families = na.Select(Family).Intersect(nb.Select(Family), StringComparer.Ordinal).ToList();
        if (families.Count == 0)
            return new("Message schema (XML)", messageLike ? TransitionCompatibility.MissingEvidence : TransitionCompatibility.NotAssessed, "The two sides declare no common schema family.", []);
        var problems = new List<string>(); var matches = new List<string>();
        foreach (var family in families)
        {
            var fa = na.Where(n => Family(n) == family).ToList(); var fb = nb.Where(n => Family(n) == family).ToList();
            var common = fa.Intersect(fb, StringComparer.Ordinal).ToList();
            if (common.Count > 0) matches.AddRange(common);
            else problems.Add($"{family}: sender declares {string.Join(", ", fa.Select(n => n[family.Length..].TrimStart('.', '/')))}; receiver {string.Join(", ", fb.Select(n => n[family.Length..].TrimStart('.', '/')))}");
        }
        var validators = xb.Where(x => x.Contract.ValidatorModules.Count > 0).Select(x => $"{x.Contract.File} validated in {string.Join(", ", x.Contract.ValidatorModules)}").Take(3);
        var evidence = xa.Concat(xb).Where(x => families.Contains(Family(x.Contract.TargetNamespace!))).Select(x => $"{x.Facts.ProjectKey}: {x.Contract.File}").Distinct().Take(8).Concat(validators).ToList();
        if (problems.Count > 0)
            return new("Message schema (XML)", TransitionCompatibility.Incompatible,
                $"Namespace/version mismatch — {string.Join("; ", problems)}. No mapping between the versions is declared; the payload may still be transported opaquely (a review question, not proof).", evidence);
        return new("Message schema (XML)", TransitionCompatibility.Compatible, $"Both sides declare {string.Join(", ", matches.Distinct())}.", evidence);
    }

    private static CompatibilityDimension Api(Side a, Side b)
    {
        var apis = InModules(b, c => c.Type is SourceContractType.OpenApi or SourceContractType.GraphQlSchema);
        if (apis.Count == 0)
            return new("API contract", b.Reference is not null ? TransitionCompatibility.NotAssessed : TransitionCompatibility.MissingEvidence,
                b.Reference is not null ? "The receiving side is the built-in reference." : "The receiving side declares no REST/GraphQL contract in its stage folders.", []);
        var senderModules = a.Sources.SelectMany(s => s.Modules).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var referenced = apis.Where(x => x.Contract.ReferencingModules.Any(senderModules.Contains)).ToList();
        var evidence = apis.Select(x => $"{x.Facts.ProjectKey}: {x.Contract.File}").Distinct().Take(6).ToList();
        return referenced.Count > 0
            ? new("API contract", TransitionCompatibility.Compatible, $"Declared by the receiver and referenced in the sender's source ({string.Join(", ", referenced.Select(r => r.Contract.Name).Distinct())}).", evidence)
            : new("API contract", TransitionCompatibility.PotentiallyCompatible, "Declared by the receiver; the sender's use of it is not visible in source.", evidence);
    }

    private static CompatibilityDimension Messaging(Side a, Side b)
    {
        var pub = a.Sources.SelectMany(s => s.Facts.Channels.Where(c => c.Role == "Publish" && s.Modules.Contains(c.Module, StringComparer.OrdinalIgnoreCase)).Select(c => (c.Name, s.Facts.ProjectKey))).ToList();
        var con = b.Sources.SelectMany(s => s.Facts.Channels.Where(c => c.Role == "Consume" && s.Modules.Contains(c.Module, StringComparer.OrdinalIgnoreCase)).Select(c => (c.Name, s.Facts.ProjectKey))).ToList();
        static string N(string n) => ArchitectureRuleEvaluator.Normalize(n);
        var common = pub.Select(p => N(p.Name)).Intersect(con.Select(c => N(c.Name))).ToList();
        var evidence = pub.Select(p => $"{p.ProjectKey} publishes {p.Name}").Concat(con.Select(c => $"{c.ProjectKey} consumes {c.Name}")).Distinct().Take(8).ToList();
        if (common.Count > 0) return new("Messaging channel", TransitionCompatibility.Compatible, $"Declared on both sides: {string.Join(", ", pub.Where(p => common.Contains(N(p.Name))).Select(p => p.Name).Distinct())} (declared, not observed).", evidence);
        if (pub.Count == 0 && con.Count == 0) return new("Messaging channel", TransitionCompatibility.MissingEvidence, "No publish/consume declaration found on either side in the stage folders.", []);
        return new("Messaging channel", TransitionCompatibility.MissingEvidence,
            pub.Count == 0 ? "The receiving side consumes a channel the sending side's source does not publish." : con.Count == 0 ? "The sending side publishes; no consumer source of the receiving side is associated." : "Published and consumed channel names differ.", evidence);
    }

    private static CompatibilityDimension Dataset(Side b)
    {
        var docs = b.Sources.SelectMany(s => s.Facts.DatasetContracts.Select(d => $"{s.Facts.ProjectKey}: {d.Name}")).Distinct().ToList();
        return docs.Count == 0
            ? new("Dataset contract", TransitionCompatibility.MissingEvidence, "No dataset contract is retained for the delivery.", [])
            : new("Dataset contract", TransitionCompatibility.PotentiallyCompatible, "SPECIFIED_BUT_NOT_RUNTIME_VERIFIED: the dataset contract is specified by the producer; no delivery has been observed.", docs);
    }

    private static CompatibilityDimension Correlation(Side a, Side b, IReadOnlyList<string> fields)
    {
        if (fields.Count == 0) return new("Correlation fields", TransitionCompatibility.NotAssessed, "No correlation identifiers declared for this boundary.", []);
        HashSet<string> Names(Side side) => side.Sources.SelectMany(s => s.Facts.Contracts.Where(c => s.Modules.Contains(c.Module, StringComparer.OrdinalIgnoreCase)).SelectMany(c => c.FieldNames))
            .Select(ArchitectureRuleEvaluator.Normalize).ToHashSet(StringComparer.Ordinal);
        var na = Names(a); var nb = Names(b);
        var found = fields.Where(f => na.Contains(ArchitectureRuleEvaluator.Normalize(f)) && nb.Contains(ArchitectureRuleEvaluator.Normalize(f))).ToList();
        if (found.Count > 0) return new("Correlation fields", TransitionCompatibility.PotentiallyCompatible, $"Both sides document {string.Join(", ", found)} (the run must still carry it).", []);
        var one = fields.Where(f => na.Contains(ArchitectureRuleEvaluator.Normalize(f)) || nb.Contains(ArchitectureRuleEvaluator.Normalize(f))).ToList();
        return new("Correlation fields", TransitionCompatibility.MissingEvidence,
            one.Count > 0 ? $"Only one side documents {string.Join(", ", one)}; how the identifier crosses the boundary is not evidenced." : $"Neither side documents {string.Join(", ", fields)}.", []);
    }

    // ── Source-coverage readiness (level A) ─────────────────────────────────────────────────────────────────────────────────────

    public static CompositeReadinessLevel SourceCoverage(IReadOnlyList<CompositeStageMapping> mappings, IReadOnlyList<CompositeTransition> transitions)
    {
        var items = new List<string>();
        var mandatory = mappings.Where(m => m.Mandatory).ToList();
        var missing = mandatory.Where(m => m.Class == CompositeMatchClass.Missing).ToList();
        var review = mandatory.Where(m => m.Class is CompositeMatchClass.Candidate or CompositeMatchClass.Ambiguous).ToList();
        var incompatible = transitions.Where(t => t.Overall == TransitionCompatibility.Incompatible && !t.Accepted).ToList();
        items.AddRange(missing.Select(m => $"{m.Label}: no source"));
        items.AddRange(review.Select(m => $"{m.Label}: {(m.Class == CompositeMatchClass.Ambiguous ? "ambiguous" : "candidate")} — approve a source"));
        items.AddRange(incompatible.Select(t => $"{t.FromStage} → {t.ToStage}: incompatible ({string.Join("; ", t.Dimensions.Where(d => d.State == TransitionCompatibility.Incompatible).Select(d => d.Name))})"));
        var covered = mandatory.Count(m => m.Class == CompositeMatchClass.Confirmed);
        var state = incompatible.Count > 0 ? CompositeReadiness.Blocked : missing.Count > 0 ? CompositeReadiness.Partial : review.Count > 0 ? CompositeReadiness.NeedsReview : CompositeReadiness.Ready;
        return new("Source coverage", state, $"{covered} of {mandatory.Count} mandatory stage(s) have an approved source; {transitions.Count(t => t.Overall == TransitionCompatibility.Incompatible)} incompatible transition(s).", items);
    }

    public static string Short(string sha) => sha.Length > 12 ? sha[..12] : sha;
}
