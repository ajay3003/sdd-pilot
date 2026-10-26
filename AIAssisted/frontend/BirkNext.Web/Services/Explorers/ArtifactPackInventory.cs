namespace BirkNext.Web.Services.Explorers;

/// <summary>How a document relates to the viewed Artifact Pack.</summary>
public enum PackDocumentClass
{
    /// <summary>The pack's own document of a role (its spec, plan, tasks, data model).</summary>
    PrimaryPackLocal,
    /// <summary>The module's or project's document of a role, applying because the pack has none of its own (e.g. the Constitution).</summary>
    SharedPrimary,
    /// <summary>Another authored document in the pack folder (research, quickstart, contracts, decisions, an additional spec …).</summary>
    SupportingPackLocal,
    /// <summary>An authored module/project document that applies: a shared document of a role, or one the pack explicitly references.</summary>
    SupportingShared,
    /// <summary>Generated documentation (autodoc …): derived documentation, never authored intent or implementation evidence.</summary>
    Generated,
    /// <summary>The Source Analysis snapshot of the project import: a reference, not a document.</summary>
    SourceSnapshotReference,
    /// <summary>A candidate document whose relation to the pack could not be established.</summary>
    Unclassified,
}

public enum PackDocumentOrigin { Authored, Generated }

/// <summary>
/// One document of an Artifact Pack's inventory with its provenance: identity, kind, exact path, where it applies, revision, fingerprint,
/// currentness and why it is included. <see cref="Family"/> is the role whose document set it extends (a quickstart the plan references
/// belongs to the plan's documents); null when it extends none.
/// </summary>
public sealed record PackDocument(
    string Id,
    string Kind,
    WorkspaceArtifactType? Role,
    WorkspaceArtifactType? Family,
    string DisplayName,
    string Path,
    string Module,
    string? Pack,
    PackDocumentOrigin Origin,
    ArtifactScope Scope,
    int? Revision,
    string? Fingerprint,
    ExplorerArtifactCurrentness Currentness,
    PackDocumentClass Class,
    IReadOnlyList<string> Reasons,
    string? Text)
{
    public bool IsPrimary => Class is PackDocumentClass.PrimaryPackLocal or PackDocumentClass.SharedPrimary;
    public bool IsSupporting => Class is PackDocumentClass.SupportingPackLocal or PackDocumentClass.SupportingShared;
    public bool IsAuthored => Origin == PackDocumentOrigin.Authored && Class != PackDocumentClass.SourceSnapshotReference;
    public string FileName => Path[(Path.LastIndexOf('/') + 1)..];
    public string Reason => string.Join(" · ", Reasons);

    public string ClassLabel => Class switch
    {
        PackDocumentClass.PrimaryPackLocal => "Primary · pack-local",
        PackDocumentClass.SharedPrimary => Scope == ArtifactScope.Project ? "Primary · shared project" : "Primary · shared module",
        PackDocumentClass.SupportingPackLocal => "Supporting · pack-local",
        PackDocumentClass.SupportingShared => Scope == ArtifactScope.Project ? "Supporting · shared project" : "Supporting · shared module",
        PackDocumentClass.Generated => "Generated documentation",
        PackDocumentClass.SourceSnapshotReference => "Source snapshot reference",
        _ => "Unclassified",
    };
}

/// <summary>A document that was considered and left out of the pack's review scope, with the reason.</summary>
public sealed record PackDocumentExclusion(string Path, string Kind, string Reason);

/// <summary>
/// The canonical document inventory of one Artifact Pack: which documents belong to it, which shared documents apply, which supporting and
/// generated documents are relevant, which were left out — and why. Built once by <see cref="IArtifactExplorerContext"/> from the same
/// discovery and resolution as the explorers; every review and explorer reads it instead of scanning files itself.
/// </summary>
public sealed record ArtifactPackInventory(
    string? Scope,
    string Module,
    string? Pack,
    IReadOnlyList<PackDocument> Documents,
    IReadOnlyList<PackDocumentExclusion> Excluded,
    IReadOnlyList<string> Limitations)
{
    public static ArtifactPackInventory Empty(string? scope) => new(scope, ArtifactModules.ProjectLevel, null, [], [], []);

    public IReadOnlyList<PackDocument> Primary => [.. Documents.Where(d => d.IsPrimary)];
    public IReadOnlyList<PackDocument> Supporting => [.. Documents.Where(d => d.IsSupporting)];
    public IReadOnlyList<PackDocument> Generated => [.. Documents.Where(d => d.Class == PackDocumentClass.Generated)];
    public PackDocument? SourceSnapshot => Documents.FirstOrDefault(d => d.Class == PackDocumentClass.SourceSnapshotReference);

    /// <summary>The authored documents a documentation review reads by default: primary plus supporting (never generated, never history).</summary>
    public IReadOnlyList<PackDocument> AuthoredForReview => [.. Documents.Where(d => d.IsAuthored && (d.IsPrimary || d.IsSupporting) && !string.IsNullOrEmpty(d.Text))];

    public PackDocument? PrimaryOf(WorkspaceArtifactType role) => Documents.FirstOrDefault(d => d.IsPrimary && d.Role == role);

    /// <summary>
    /// A role's document set: its primary document first, then the other authored documents of the same family (pack-local before shared).
    /// Never a sibling pack's document; generated documents only when <paramref name="includeGenerated"/>.
    /// </summary>
    public IReadOnlyList<PackDocument> Family(WorkspaceArtifactType role, bool includeGenerated = false) =>
        [.. Documents.Where(d => d.Family == role && (d.IsPrimary || d.IsSupporting || includeGenerated && d.Class == PackDocumentClass.Generated))
            .OrderBy(d => d.IsPrimary ? 0 : d.Class == PackDocumentClass.SupportingPackLocal ? 1 : d.Class == PackDocumentClass.SupportingShared ? 2 : 3)
            .ThenBy(d => d.Path, StringComparer.Ordinal)];

    /// <summary>
    /// Pack-local documents of a role's family whose requirement IDs are pack-scoped: the primary document and the pack's own supporting
    /// documents of that family. Shared module documents are excluded — their FR-001 is not this pack's FR-001.
    /// </summary>
    public IReadOnlyList<PackDocument> PackScopedFamily(WorkspaceArtifactType role) =>
        [.. Family(role).Where(d => d.Class is PackDocumentClass.PrimaryPackLocal or PackDocumentClass.SupportingPackLocal)];

    public int CountFor(WorkspaceArtifactType role) => Family(role).Count;
}

/// <summary>One candidate document of the scope as the inventory builder sees it (an explorer artifact of any role, or a supporting document).</summary>
public sealed record PackInventoryCandidate(ExplorerArtifact Artifact, Func<string?> Text, int HistoricalVersions = 0);

/// <summary>A generated document of the import's Generated Documentation Set (path under the archive, its module root and fingerprint).</summary>
public sealed record PackInventoryGenerated(string Path, string ModuleRoot, string Kind, string Fingerprint, string? Generator, Func<string?> Text);

/// <summary>Pure builder of <see cref="ArtifactPackInventory"/>: same input, same inventory. No I/O.</summary>
public static class ArtifactPackInventoryBuilder
{
    private static readonly WorkspaceArtifactType[] ReferenceOrder =
        [WorkspaceArtifactType.Plan, WorkspaceArtifactType.Specification, WorkspaceArtifactType.Tasks, WorkspaceArtifactType.DataModel];

    /// <param name="candidates">Every document of the scope (every role, every module); the builder keeps the module's and the project's.</param>
    /// <param name="primaryIds">The artifact each role resolves to for the viewed pack (the explorers' resolution), by role.</param>
    public static ArtifactPackInventory Build(string? scope, string module, string? pack, IReadOnlyList<PackInventoryCandidate> candidates,
        IReadOnlyDictionary<WorkspaceArtifactType, string> primaryIds, IReadOnlyList<PackInventoryGenerated>? generated = null,
        PackDocument? sourceSnapshot = null, IReadOnlyList<string>? limitations = null)
    {
        var documents = new List<PackDocument>();
        var excluded = new List<PackDocumentExclusion>();
        var notes = new List<string>(limitations ?? []);
        var inModule = candidates.Where(c => c.Artifact.Module == module
            || module != ArtifactModules.ProjectLevel && c.Artifact.Module == ArtifactModules.ProjectLevel).ToList();
        var primarySet = primaryIds.Values.ToHashSet(StringComparer.Ordinal);

        // Applicable documents: the pack's own and the shared ones. Sibling packs are listed as excluded, never read.
        var applicable = new List<PackInventoryCandidate>();
        foreach (var candidate in inModule)
        {
            var a = candidate.Artifact;
            if (a.Feature is not null && a.Feature != pack)
            {
                excluded.Add(new(PathOf(a), KindOf(a), $"Sibling Artifact Pack {a.Feature} — never merged into this pack"));
                continue;
            }
            if (a.Currentness != ExplorerArtifactCurrentness.Current && !primarySet.Contains(a.Id))
            {
                excluded.Add(new(PathOf(a), KindOf(a), $"{a.Currentness} content version — not reviewed by default"));
                continue;
            }
            applicable.Add(candidate);
        }

        // Explicit references among applicable documents: who references whom (strong evidence, wins over location heuristics).
        var paths = applicable.Select(c => PathOf(c.Artifact)).ToList();
        var referencedBy = new Dictionary<string, List<(string From, WorkspaceArtifactType? Role)>>(StringComparer.Ordinal);
        foreach (var source in applicable.Where(c => c.Artifact.Feature == pack || primarySet.Contains(c.Artifact.Id)))
        {
            if (source.Text() is not { Length: > 0 } text) continue;
            foreach (var target in SupportingDocuments.References(PathOf(source.Artifact), text, paths))
            {
                if (!referencedBy.TryGetValue(target, out var list)) referencedBy[target] = list = [];
                list.Add((PathOf(source.Artifact), primarySet.Contains(source.Artifact.Id) ? source.Artifact.Role : null));
            }
        }

        foreach (var candidate in applicable)
        {
            var a = candidate.Artifact;
            var path = PathOf(a);
            var scopeOf = ArtifactPacks.ScopeOf(a);
            var isSupportingRole = a.Role == WorkspaceArtifactType.SupportingDocument;
            var refs = referencedBy.GetValueOrDefault(path) ?? [];
            var reasons = new List<string>();
            var generatedDoc = SupportingDocuments.IsGenerated(path);
            string? kindReason = null;
            PackDocumentClass cls;
            WorkspaceArtifactType? family;

            if (primarySet.Contains(a.Id))
            {
                cls = generatedDoc ? PackDocumentClass.Generated : scopeOf == ArtifactScope.Pack ? PackDocumentClass.PrimaryPackLocal : PackDocumentClass.SharedPrimary;
                family = a.Role;
                reasons.Add(scopeOf == ArtifactScope.Pack ? $"Primary {ArtifactExplorerRoles.Label(a.Role)} of the Artifact Pack"
                    : $"Shared {ArtifactExplorerRoles.Label(a.Role)} — the Artifact Pack has none of its own");
            }
            else
            {
                // Family: the role of the same kind, else the role of the primary document that explicitly references it.
                family = !isSupportingRole && a.Role != WorkspaceArtifactType.Research ? a.Role
                    : ReferenceOrder.Cast<WorkspaceArtifactType?>().FirstOrDefault(r => refs.Any(x => x.Role == r));
                // Fallback for the pack's own unreferenced documents only: the Spec-Kit phase their kind belongs to. Grouping, never authority.
                if (family is null && scopeOf == ArtifactScope.Pack && !generatedDoc)
                {
                    family = KindOf(a) switch
                    {
                        "Research" or "Quickstart" or "Contract" or "Decision (ADR)" or "Architecture" => WorkspaceArtifactType.Plan,
                        "Checklist" => WorkspaceArtifactType.Specification,
                        _ => null,
                    };
                    if (family is { } byKind) kindReason = $"{ArtifactExplorerRoles.Label(byKind)}-phase document by kind (not referenced)";
                }
                // A project-level document outside the pack's module applies only by explicit reference (the explorers use project-level
                // documents only when the module has none of its own): a repository-wide docs/ folder is never read wholesale.
                var outsideModule = a.Module != module;
                if (generatedDoc) cls = PackDocumentClass.Generated;
                else if (scopeOf == ArtifactScope.Pack) cls = PackDocumentClass.SupportingPackLocal;
                else if ((!isSupportingRole && !outsideModule) || refs.Count > 0) cls = PackDocumentClass.SupportingShared;
                else
                {
                    excluded.Add(new(path, KindOf(a), outsideModule ? "Project-level document not referenced by this Artifact Pack"
                        : "Shared supporting document not referenced by this Artifact Pack"));
                    continue;
                }
                if (generatedDoc) reasons.Add(scopeOf == ArtifactScope.Project ? "Generated project documentation" : "Generated module documentation");
                else if (scopeOf == ArtifactScope.Pack) reasons.Add("Same Artifact Pack");
                else if (!isSupportingRole) reasons.Add(scopeOf == ArtifactScope.Project ? "Project-shared documentation" : "Module-shared documentation");
                if (kindReason is not null) reasons.Add(kindReason);
            }
            foreach (var r in refs.Where(r => r.Role is not null).Select(r => r.Role!.Value).Distinct())
                reasons.Add($"Explicitly referenced by {ArtifactExplorerRoles.Label(r)}");
            if (refs.Count > 0 && refs.All(r => r.Role is null))
                reasons.Add($"Referenced by {string.Join(", ", refs.Select(r => r.From[(r.From.LastIndexOf('/') + 1)..]).Distinct())}");

            documents.Add(new PackDocument(a.Id, KindOf(a), isSupportingRole ? null : a.Role, family, a.DisplayName, path, a.Module, a.Feature,
                generatedDoc ? PackDocumentOrigin.Generated : PackDocumentOrigin.Authored, scopeOf, a.Revision, FingerprintOf(candidate),
                a.Currentness, cls, reasons, candidate.Text()));
            if (candidate.HistoricalVersions > 0)
                excluded.Add(new(path, KindOf(a), $"{candidate.HistoricalVersions} earlier content version{(candidate.HistoricalVersions == 1 ? "" : "s")} — history is not reviewed by default"));
        }

        // Generated documentation of the import's set for this module (not stored as authored revisions): derived documentation only.
        foreach (var g in generated ?? [])
        {
            if (g.ModuleRoot != module && !(module == ArtifactModules.ProjectLevel && g.ModuleRoot.Length == 0)) continue;
            if (documents.Any(d => string.Equals(d.Path, g.Path, StringComparison.Ordinal))) continue;
            documents.Add(new PackDocument("generated:" + g.Path, g.Kind, null, g.Kind.Contains("DataModel", StringComparison.OrdinalIgnoreCase) ? WorkspaceArtifactType.DataModel : null,
                g.Path[(g.Path.LastIndexOf('/') + 1)..], g.Path, module, null, PackDocumentOrigin.Generated,
                module.Length == 0 ? ArtifactScope.Project : ArtifactScope.Module, null, g.Fingerprint, ExplorerArtifactCurrentness.Current,
                PackDocumentClass.Generated, [g.Generator is { Length: > 0 } gen ? $"Generated documentation ({gen}) of the module" : "Generated documentation of the module"], g.Text()));
        }

        if (sourceSnapshot is not null) documents.Add(sourceSnapshot);
        var ordered = documents.OrderBy(d => (int)d.Class).ThenBy(d => d.Role is { } r ? (int)r : 99).ThenBy(d => d.Path, StringComparer.Ordinal).ToList();
        return new ArtifactPackInventory(scope, module, pack, ordered,
            [.. excluded.DistinctBy(e => (e.Path, e.Reason)).OrderBy(e => e.Path, StringComparer.Ordinal)], notes);
    }

    private static string PathOf(ExplorerArtifact a) => (a.SourcePath ?? a.FileName).Replace('\\', '/');

    private static string KindOf(ExplorerArtifact a) => a.Role switch
    {
        WorkspaceArtifactType.SupportingDocument => SupportingDocuments.KindOf(PathOf(a)),
        WorkspaceArtifactType.Research => "Research",
        _ => ArtifactExplorerRoles.Label(a.Role),
    };

    private static string? FingerprintOf(PackInventoryCandidate c) => c.Text() is { Length: > 0 } text ? ArtifactFingerprint.Compute(text) : null;
}
