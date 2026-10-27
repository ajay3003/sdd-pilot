using System.Text.RegularExpressions;
using BirkNext.Web.Models;
using BirkNext.Web.Services.ProjectImport;

namespace BirkNext.Web.Services;

public enum DriftOrigin { Authored, Generated }

/// <summary>
/// The comparison mode. <see cref="VersionDrift"/>: the same document identity in two imports/revisions of one project lineage.
/// <see cref="ConsistencyDrift"/>: authored ↔ generated in the same import (consistency, never history). <see cref="Manual"/>: any two
/// documents the user chose, not assumed equivalent. <see cref="AuthoredVsGenerated"/> is kept only so older saved comparisons load.
/// </summary>
public enum DriftIntent { VersionDrift, ConsistencyDrift, AuthoredVsGenerated, Manual }

/// <summary>Deterministic result kinds. Nothing here claims a semantic contradiction or names which side is wrong.</summary>
public enum DriftItemKind { Added, Removed, Changed, OnlyInLeft, OnlyInRight, StructureOnly, ConsistencyDiscrepancy, Unchanged }

/// <summary>How an authored and a generated document relate. No numeric confidence: equivalent scope needs deterministic evidence.</summary>
public enum DriftPairing { Equivalent, RelatedScopeDiffers }

/// <summary>
/// A comparable documentation artifact as Documentation Drift sees it — a reference into the existing history, never a copy: an authored
/// artifact revision or a generated document of an import's Generated Documentation Set. <see cref="LogicalKey"/> is the stable identity
/// within one project lineage (origin, lineage, role/module and archive-root-relative path); <see cref="Fingerprint"/> is the content
/// identity. <see cref="ProjectRevision"/> is the user-facing import label ("M2LB · Import 2"); <see cref="GlobalRevision"/> is the
/// workspace-wide import order ("R4"), kept for technical details only — it is never version history.
/// </summary>
public sealed record DriftDocument(
    string Id, DriftOrigin Origin, string LogicalKey, string DisplayName, string Path, string TypeLabel, string Fingerprint,
    string? ProjectImportId, string? ProjectRevision, string? ArchiveName, Guid? RevisionId, int? RevisionNumber, string? GeneratedSetId,
    Guid? SourceSnapshotId, string? Module, string? Generator, bool ContentAvailable, bool Supported, string? WorkspaceScope,
    string? LineageKey = null, DateTimeOffset? ImportedAt = null, string? GlobalRevision = null);

public sealed record DriftComparisonResult(
    DriftIntent Intent, string RevisionContext, string Outcome, IReadOnlyList<SddDriftItem> Items, IReadOnlyDictionary<string, int> Counts);

/// <summary>
/// One import as Documentation Drift presents it. <see cref="LineageKey"/> groups imports of one logical project; <see cref="Number"/> is
/// the import's position within that lineage only ("Import 2"), never across unrelated projects.
/// </summary>
public sealed record DriftImport(string ImportId, string LineageKey, string ProjectName, int Number, string GlobalRevision, string ArchiveName,
    Guid? SourceSnapshotId, DateTimeOffset ImportedAt)
{
    public string Label => $"{ProjectName} · Import {Number}";
}

/// <summary>An imported project/source for the Overview: authored documents and generated documentation availability (neutral when absent).</summary>
public sealed record DriftImportSummary(DriftImport Import, int AuthoredDocuments, string GeneratedState, int GeneratedDocuments, int GeneratedModules);

/// <summary>An authored ↔ generated pairing in one import, with the scope of each side, the basis and the status from saved comparisons.</summary>
public sealed record DriftCandidate(string Title, DriftDocument Authored, DriftDocument Generated, string Status, DriftPairing Pairing,
    string AuthoredScope, string GeneratedScope, string Basis);

/// <summary>One document identity (same lineage, origin, type and stable path) and its versions in that lineage's imports, oldest first.</summary>
public sealed record DriftIdentity(string LogicalKey, string Label, DriftOrigin Origin, IReadOnlyList<DriftDocument> Versions, string? LineageKey = null);

/// <summary>
/// Documentation Drift: compares documentation artifacts by version and by consistency. It consumes the existing provenance — Project Import
/// records, append-only authored artifact revisions, and each import's Generated Documentation Set linked to its source snapshot — and owns
/// only artifact selection, comparison mode, the comparison and its saved results. Different imported projects are never revisions of each
/// other: version history exists only inside one project lineage. Authored documents are intent and design evidence, generated documents are
/// implementation-derived documentation; neither is implementation truth, raw source stays the Source Analysis authority, and a comparison
/// never declares a winner. Generated documentation coverage and freshness belong to Generated Documentation Health.
/// </summary>
public static class DocumentationDrift
{
    public const int AnalysisVersion = 2;
    public const string Description = "Compare versions of the same documentation and review authored ↔ generated consistency.";
    public const string SameRevision = "Same import";
    public const string DifferentRevisions = "Different imports of the same project";
    public const string UnrelatedImports = "Different imported projects — not revisions of each other";
    public const string UnknownRevision = "No shared import";
    public const string SelectDifferent = "Select two different document versions or artifacts.";
    public const string Unsupported = "This artifact type is not supported by Documentation Drift yet.";
    public const string NoContentChange = "No content change";
    public const string Neutral = "The authored documentation and generated documentation describe this differently.";
    public const string NeutralReview = "Authored documentation represents intent/design. Generated documentation is implementation-derived. A discrepancy requires review; neither side is automatically authoritative.";
    public const string SourceBoundary = "Generated documentation is implementation-derived. Use Source Analysis for raw source evidence.";
    public const string ManualNote = "Manual document comparison — these artifacts are not assumed to describe equivalent scope. The result lists document differences only.";
    public const string NotPresent = "Not present";
    public const string SelectDocument = "Select a document to compare.";
    public const string NoResult = "Select two documents and run Compare.";
    public const string NoEarlierRevision = "No earlier comparable revision is available.";
    public const string NoOtherRevision = "No earlier/later revision of this document is available.";
    public const string GeneratedNotPresent = "Generated documentation not present for this import.";
    public const string NotPresentMeaning = "Generated documentation not present means no generated documentation set was present for that import. It is neutral — not an error, an unhealthy state, a missing requirement or an import failure. A set from another import is never reused.";
    public const string LineageRule = "Imports belong to the same project only when they share the authored project root folder. Different imported projects are never treated as revisions of each other; import order alone never creates version history.";
    public const string GeneratedReviewNote = "Generated documentation coverage, freshness and generator diagnostics are reviewed separately.";
    public const string ComparisonBasis = "Deterministic text and structure comparison: sections by heading, normalized lines. Semantic change, possible contradiction and terminology drift are not assessed.";
    public const string WholeDocument = "Whole document";
    private const int MaxItems = 300;

    public static string SetIdFor(string importId) => "genset-" + importId;

    public static bool IsMarkdown(string path) => path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase);

    // ── Imports and lineages ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The project lineage of an import. Only the authored project root folder (the archive's single top-level folder, recorded as the
    /// project name with basis ArchiveRoot) identifies a project across imports — an exact name, never a similar one. Without it the import
    /// is its own lineage: losing a comparison is better than fabricating version history.
    /// </summary>
    public static string LineageKeyOf(SddProjectImportRecord record) =>
        record.ProjectNameBasis == "ArchiveRoot" && !string.IsNullOrWhiteSpace(record.ProjectName)
            ? "project:" + record.ProjectName.Trim().ToLowerInvariant()
            : "import:" + record.ImportId;

    /// <summary>Workspace-wide import order ("R1"…): storage/technical order only, never a project revision.</summary>
    public static IReadOnlyDictionary<string, string> RevisionLabels(SddLifecycleState state) =>
        state.ProjectImports.OrderBy(i => i.ImportedAt).ThenBy(i => i.ImportId, StringComparer.Ordinal)
            .Select((i, n) => (i.ImportId, Label: $"R{n + 1}")).ToDictionary(x => x.ImportId, x => x.Label, StringComparer.Ordinal);

    /// <summary>Every import with its lineage and lineage-local number, oldest first.</summary>
    public static IReadOnlyList<DriftImport> Imports(SddLifecycleState state)
    {
        var global = RevisionLabels(state);
        return [.. state.ProjectImports.GroupBy(LineageKeyOf).SelectMany(lineage => lineage
                .OrderBy(i => i.ImportedAt).ThenBy(i => i.ImportId, StringComparer.Ordinal)
                .Select((i, n) => new DriftImport(i.ImportId, lineage.Key, ProjectNameOf(i), n + 1, global[i.ImportId], i.ArchiveFileName, i.SourceSnapshotId, i.ImportedAt)))
            .OrderBy(i => i.ImportedAt).ThenBy(i => i.ImportId, StringComparer.Ordinal)];
    }

    private static string ProjectNameOf(SddProjectImportRecord record) =>
        !string.IsNullOrWhiteSpace(record.ProjectName) ? record.ProjectName
        : System.IO.Path.GetFileNameWithoutExtension(record.ArchiveFileName) is { Length: > 0 } name ? name : record.ImportId;

    /// <summary>Lineages with more than one import: the only places version comparison can exist.</summary>
    public static int VersionComparableLineages(IReadOnlyList<DriftImport> imports) => imports.GroupBy(i => i.LineageKey).Count(g => g.Count() > 1);

    /// <summary>The path without the archive's single top-level folder (when the project name came from it), so versions of a project match.</summary>
    public static string StablePath(SddLifecycleState state, string? importId, string path)
    {
        var normalized = path.Replace('\\', '/').TrimStart('/');
        if (importId is not null && state.ProjectImports.FirstOrDefault(i => i.ImportId == importId) is { ProjectNameBasis: "ArchiveRoot" } record)
        {
            if (normalized.StartsWith(record.ProjectName + "/", StringComparison.OrdinalIgnoreCase)) return normalized[(record.ProjectName.Length + 1)..];
            // The archive's top-level folder itself is the project root.
            if (normalized.TrimEnd('/').Equals(record.ProjectName, StringComparison.OrdinalIgnoreCase)) return "";
        }
        return normalized;
    }

    // ── Catalog ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Every comparable artifact in the workspace history: authored revisions and generated documents of every import's set.</summary>
    public static IReadOnlyList<DriftDocument> Catalog(SddLifecycleState state)
    {
        var imports = Imports(state).ToDictionary(i => i.ImportId, StringComparer.Ordinal);
        var documents = new List<DriftDocument>();
        // A document the import's generated-documentation evidence identified is generated, even when Project Import also kept it as an
        // artifact revision (e.g. autodoc/data-model.md under the Data Model role): it is listed once, as generated, never on both sides.
        var generatedPaths = state.GeneratedDocumentationSets.SelectMany(s => s.Artifacts.Select(a => (s.ProjectImportId, Path: a.RelativePath.Replace('\\', '/'))))
            .ToHashSet();
        foreach (var revision in state.Revisions.Where(r => !string.IsNullOrWhiteSpace(r.Content)))
        {
            var importId = revision.ProjectImportId ?? ProjectImportScope.ImportIdOf(revision.WorkspaceScope);
            var path = revision.SourceReference ?? revision.FileName;
            if (importId is not null && generatedPaths.Contains((importId, path.Replace('\\', '/')))) continue;
            var import = importId is null ? null : imports.GetValueOrDefault(importId);
            var stable = StablePath(state, importId, path);
            var supported = IsMarkdown(path) || !System.IO.Path.HasExtension(path);
            // Identity within the lineage; manual workspace documents keep their workspace scope as their own lineage.
            var lineage = import?.LineageKey ?? "workspace:" + revision.WorkspaceScope;
            documents.Add(new DriftDocument("rev:" + revision.RevisionId, DriftOrigin.Authored,
                $"authored|{lineage}|{revision.Role}|{stable.ToLowerInvariant()}", Display(revision.Role) + " · " + System.IO.Path.GetFileName(path),
                path, Display(revision.Role), revision.Fingerprint, importId, import?.Label, import?.ArchiveName,
                revision.RevisionId, revision.Revision, null, import?.SourceSnapshotId, null, null, true, supported, revision.WorkspaceScope,
                lineage, import?.ImportedAt, import?.GlobalRevision));
        }
        foreach (var set in state.GeneratedDocumentationSets)
        {
            var import = imports.GetValueOrDefault(set.ProjectImportId);
            var lineage = import?.LineageKey ?? "import:" + set.ProjectImportId;
            foreach (var artifact in set.Artifacts)
            {
                var stable = StablePath(state, set.ProjectImportId, artifact.RelativePath);
                var module = ModuleName(state, set.ProjectImportId, artifact.ModuleRoot);
                documents.Add(new DriftDocument($"gen:{set.SetId}|{artifact.RelativePath}", DriftOrigin.Generated, $"generated|{lineage}|{module.ToLowerInvariant()}|{stable.ToLowerInvariant()}",
                    $"{module} · {System.IO.Path.GetFileName(artifact.RelativePath)}", artifact.RelativePath, "Generated · " + artifact.Kind, artifact.Fingerprint, set.ProjectImportId, import?.Label,
                    import?.ArchiveName, null, null, set.SetId, set.SourceSnapshotId, module, artifact.Generator,
                    artifact.ContentCaptured && state.GeneratedDocumentContents.ContainsKey(artifact.Fingerprint), IsMarkdown(artifact.RelativePath), ProjectImportScope.For(set.ProjectImportId),
                    lineage, import?.ImportedAt, import?.GlobalRevision));
            }
        }
        return [.. documents.OrderBy(d => d.Origin).ThenBy(d => d.LogicalKey, StringComparer.Ordinal).ThenBy(d => d.ImportedAt ?? DateTimeOffset.MaxValue).ThenBy(d => d.RevisionNumber ?? 0)];
    }

    private static string ModuleName(SddLifecycleState state, string importId, string moduleRoot)
    {
        var root = StablePath(state, importId, moduleRoot).Trim('/');
        return root.Length == 0 ? "(repository)" : root;
    }

    /// <summary>"Data Model", "Architecture", "Specification": the document type without origin prefix.</summary>
    public static string KindLabel(DriftDocument d)
    {
        if (d.Origin == DriftOrigin.Authored) return d.TypeLabel == "Data model" ? "Data Model" : d.TypeLabel;
        var kind = d.TypeLabel.Replace("Generated · ", "");
        return kind switch
        {
            "DataModel" => "Data Model", "DataFlow" => "Data Flow", "ApiContract" => "API Contract", "GraphQlSchema" => "GraphQL Schema", "MessageBus" => "Message Bus",
            _ => Regex.Replace(kind, "(?<=[a-z])(?=[A-Z])", " "),
        };
    }

    /// <summary>The stable path (archive root removed) as the logical key holds it, for display and module matching.</summary>
    public static string StablePathOf(DriftDocument d)
    {
        var stable = d.LogicalKey[(d.LogicalKey.LastIndexOf('|') + 1)..];
        // The key is lower-cased for matching; show the document's own spelling of the same tail.
        var path = d.Path.Replace('\\', '/');
        return path.EndsWith(stable, StringComparison.OrdinalIgnoreCase) ? path[^stable.Length..] : stable;
    }

    /// <summary>"Authored · Specification · specs/001-x/spec.md · M2LB · Import 1 · rev 2" / "Generated · Data Model · Person/autodoc/data-model.md · M2LB · Import 1".</summary>
    public static string OptionText(DriftDocument d) =>
        $"{d.Origin} · {KindLabel(d)} · {d.Path.Replace('\\', '/')} · {d.ProjectRevision ?? "manual"}{(d.RevisionNumber is { } n ? $" · rev {n}" : "")}";

    /// <summary>Fingerprint context only: identical fingerprints are "No content change", different ones "Content changed". Never semantic equivalence.</summary>
    public static string ContentState(DriftDocument left, DriftDocument right) => left.Fingerprint == right.Fingerprint ? NoContentChange : "Content changed";

    /// <summary>The Spec-Kit feature (artifact pack) of an authored document, or "Shared / project-level" outside packs.</summary>
    public static string PackOf(DriftDocument d) => Explorers.ArtifactModules.FeatureOf(d.Path) ?? "Shared / project-level";

    /// <summary>Module of a document: generated documents carry theirs; authored ones use the folder before <c>specs/</c> or "Project-level".</summary>
    public static string ModuleOf(DriftDocument d)
    {
        if (d.Module is { } module) return module;
        var stable = StablePathOf(d).Split('/');
        var specs = Array.FindIndex(stable, f => f.Equals("specs", StringComparison.OrdinalIgnoreCase) || f.Equals(".specify", StringComparison.OrdinalIgnoreCase));
        return specs > 0 ? string.Join('/', stable[..specs]) : Explorers.ArtifactModules.ProjectLevelLabel;
    }

    /// <summary>"Artifact Pack 001-person-module", "Module Person", "Repository" or "Project-level": the scope a document describes.</summary>
    public static string ScopeOf(DriftDocument d) =>
        d.Origin == DriftOrigin.Generated
            ? d.Module is null or "(repository)" ? "Repository" : $"Module {d.Module}"
            : Explorers.ArtifactModules.FeatureOf(d.Path) is { } pack ? $"Artifact Pack {pack}"
            : ModuleOf(d) is var m && m != Explorers.ArtifactModules.ProjectLevelLabel ? $"Module {m}" : "Project-level";

    /// <summary>Document identities with their versions (oldest first) — the same lineage, origin, type and path; never a name-similarity guess.</summary>
    public static IReadOnlyList<DriftIdentity> Identities(IReadOnlyList<DriftDocument> catalog) =>
        [.. catalog.Where(d => d.Supported).GroupBy(d => d.LogicalKey).Select(g =>
        {
            var first = g.First();
            return new DriftIdentity(g.Key, $"{first.Origin} · {KindLabel(first)} · {StablePathOf(first)}", first.Origin, Versions(catalog, first), first.LineageKey);
        }).OrderBy(i => i.Origin).ThenBy(i => i.Label, StringComparer.OrdinalIgnoreCase)];

    private static string Display(string role) => role switch
    {
        "Spec" or "Specification" => "Specification",
        "DataModel" => "Data model",
        "SupportingDocument" => "Supporting documentation",
        _ => role,
    };

    /// <summary>The text behind a document reference: the authored revision's content or the generated content store; null when not captured.</summary>
    public static string? Text(SddLifecycleState state, DriftDocument document) => document.Origin == DriftOrigin.Authored
        ? state.Revisions.FirstOrDefault(r => r.RevisionId == document.RevisionId)?.Content
        : state.GeneratedDocumentContents.GetValueOrDefault(document.Fingerprint);

    // ── Modes ───────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Same identity → Version comparison; authored ↔ generated in one import → consistency; anything else → Manual comparison.</summary>
    public static DriftIntent SuggestIntent(DriftDocument left, DriftDocument right) =>
        Eligibility(DriftIntent.VersionDrift, left, right) is null ? DriftIntent.VersionDrift
        : Eligibility(DriftIntent.ConsistencyDrift, left, right) is null ? DriftIntent.ConsistencyDrift
        : DriftIntent.Manual;

    /// <summary>Modes that mean something for the pair; Manual is always possible.</summary>
    public static IReadOnlyList<DriftIntent> AllowedIntents(DriftDocument? left, DriftDocument? right) =>
        left is null || right is null ? [DriftIntent.VersionDrift, DriftIntent.ConsistencyDrift, DriftIntent.Manual]
        : [.. new[] { DriftIntent.VersionDrift, DriftIntent.ConsistencyDrift, DriftIntent.Manual }.Where(i => Eligibility(i, left, right) is null)];

    /// <summary>Why the pair does not fit the mode, or null.</summary>
    public static string? Eligibility(DriftIntent intent, DriftDocument left, DriftDocument right) => intent switch
    {
        DriftIntent.VersionDrift when left.LogicalKey != right.LogicalKey || left.LineageKey != right.LineageKey =>
            "Version comparison needs the same document identity in two imports or revisions of one project. Use Manual comparison for other documents.",
        DriftIntent.ConsistencyDrift or DriftIntent.AuthoredVsGenerated when left.Origin == right.Origin || left.ProjectImportId is null || left.ProjectImportId != right.ProjectImportId =>
            "Authored ↔ generated consistency compares an authored and a generated document of the same import.",
        _ => null,
    };

    /// <summary>"Version comparison", "Authored ↔ generated consistency" or "Manual comparison".</summary>
    public static string ComparisonType(DriftIntent intent) => intent switch
    {
        DriftIntent.VersionDrift => "Version comparison",
        DriftIntent.Manual => "Manual comparison",
        _ => "Authored ↔ generated consistency",
    };

    public static string Label(DriftIntent intent) => ComparisonType(intent);

    /// <summary>
    /// "Cross-pack comparison" when the two authored documents belong to different artifact packs (feature folders): allowed only because the
    /// user chose both sides, and labelled so it is never read as revision drift. Null for the same pack or documents outside packs.
    /// </summary>
    public static string? PackContext(DriftDocument left, DriftDocument right)
    {
        if (left.Origin != DriftOrigin.Authored || right.Origin != DriftOrigin.Authored) return null;
        var (a, b) = (Explorers.ArtifactModules.FeatureOf(left.Path), Explorers.ArtifactModules.FeatureOf(right.Path));
        return a is not null && b is not null && !string.Equals(a, b, StringComparison.Ordinal)
            ? $"Cross-pack comparison — pack {a} vs pack {b}. These are different features, not revisions of one artifact."
            : null;
    }

    /// <summary>A result kind in the words of the compared pair. Never names a correct side.</summary>
    public static string Label(DriftItemKind kind, DriftDocument? left, DriftDocument? right)
    {
        var (l, r) = (left?.ProjectRevision, right?.ProjectRevision);
        var crossOrigin = left is not null && right is not null && left.Origin != right.Origin;
        return kind switch
        {
            DriftItemKind.Added when r is not null => $"Added in {r}",
            DriftItemKind.Removed when l is not null => $"Not present in {r ?? "the later version"}",
            DriftItemKind.Changed when l is not null && r is not null && l != r => $"Changed between {l} and {r}",
            DriftItemKind.OnlyInLeft when crossOrigin => $"Only in {left!.Origin.ToString().ToLowerInvariant()} documentation",
            DriftItemKind.OnlyInRight when crossOrigin => $"Only in {right!.Origin.ToString().ToLowerInvariant()} documentation",
            DriftItemKind.ConsistencyDiscrepancy => "Described differently",
            _ => Label(kind),
        };
    }

    public static string Label(DriftItemKind kind) => kind switch
    {
        DriftItemKind.OnlyInLeft => "Only in left",
        DriftItemKind.OnlyInRight => "Only in right",
        DriftItemKind.StructureOnly => "Structure-only difference",
        DriftItemKind.ConsistencyDiscrepancy => "Consistency discrepancy",
        _ => kind.ToString(),
    };

    /// <summary>Context only: same import, imports of one project, unrelated imported projects, or manual documents. Never a staleness verdict.</summary>
    public static string RevisionContext(DriftDocument left, DriftDocument right) =>
        left.ProjectImportId is null || right.ProjectImportId is null ? UnknownRevision
        : left.ProjectImportId == right.ProjectImportId ? SameRevision
        : left.LineageKey == right.LineageKey ? DifferentRevisions : UnrelatedImports;

    /// <summary>Why a pair cannot be compared at all, or null. The same artifact revision is never compared with itself.</summary>
    public static string? Guard(DriftDocument? left, DriftDocument? right)
    {
        if (left is null || right is null) return "Select a document for each side.";
        if (!left.Supported || !right.Supported) return Unsupported;
        if (left.Id == right.Id || left.LogicalKey == right.LogicalKey && left.Fingerprint == right.Fingerprint && left.ProjectImportId == right.ProjectImportId && left.RevisionId == right.RevisionId)
            return SelectDifferent;
        if (!left.ContentAvailable || !right.ContentAvailable) return "The text of a selected document was not captured with its import, so it cannot be compared.";
        return null;
    }

    /// <summary>Guard plus the mode's eligibility: what Compare checks before it runs.</summary>
    public static string? Guard(DriftIntent intent, DriftDocument? left, DriftDocument? right) =>
        Guard(left, right) ?? Eligibility(intent, left!, right!);

    /// <summary>Versions of the same document identity in its own lineage, oldest import first.</summary>
    public static IReadOnlyList<DriftDocument> Versions(IReadOnlyList<DriftDocument> catalog, DriftDocument document) =>
        [.. catalog.Where(d => d.LogicalKey == document.LogicalKey).OrderBy(d => d.ImportedAt ?? DateTimeOffset.MaxValue).ThenBy(d => d.RevisionNumber ?? 0)];

    // ── Comparison ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Structure-aware comparison of two Markdown documents with the shared tokenizer: sections by heading path (heading text, levels ignored),
    /// each section's normalized lines compared as sets. Identical content → "No content change". Version comparison reports Added/Removed/
    /// Changed; authored ↔ generated reports a neutral "described differently"; Manual reports document differences only.
    /// </summary>
    public static DriftComparisonResult Compare(DriftDocument left, string leftText, DriftDocument right, string rightText, DriftIntent intent)
    {
        var context = RevisionContext(left, right);
        var items = new List<SddDriftItem>();
        var version = intent == DriftIntent.VersionDrift;
        var neutral = !version && intent != DriftIntent.Manual && left.Origin != right.Origin;
        if (left.Origin == right.Origin && left.Fingerprint == right.Fingerprint || Normalize(leftText) == Normalize(rightText))
            return new(intent, context, NoContentChange, [], new Dictionary<string, int>());
        var a = Sections(leftText);
        var b = Sections(rightText);
        var unchanged = 0;
        string Side(DriftDocument d) => neutral ? d.Origin.ToString().ToLowerInvariant() : d == left ? "left" : "right";
        foreach (var key in a.Keys.Union(b.Keys).OrderBy(k => (a.TryGetValue(k, out var x) ? x.Order : int.MaxValue, b.TryGetValue(k, out var y) ? y.Order : int.MaxValue)))
        {
            var inLeft = a.TryGetValue(key, out var l);
            var inRight = b.TryGetValue(key, out var r);
            var heading = (l ?? r)!.Heading;
            if (inLeft && !inRight)
                items.Add(new SddDriftItem { Kind = (version ? DriftItemKind.Removed : DriftItemKind.OnlyInLeft).ToString(), Section = heading, Detail = $"Section present only in the {Side(left)} document.", Left = [.. l!.Lines.Take(5)] });
            else if (!inLeft && inRight)
                items.Add(new SddDriftItem { Kind = (version ? DriftItemKind.Added : DriftItemKind.OnlyInRight).ToString(), Section = heading, Detail = $"Section present only in the {Side(right)} document.", Right = [.. r!.Lines.Take(5)] });
            else
            {
                var removed = l!.Lines.Except(r!.Lines, StringComparer.Ordinal).ToList();
                var added = r.Lines.Except(l.Lines, StringComparer.Ordinal).ToList();
                if (removed.Count == 0 && added.Count == 0)
                {
                    if (!l.Lines.SequenceEqual(r.Lines, StringComparer.Ordinal) || l.Level != r.Level)
                        items.Add(new SddDriftItem { Kind = DriftItemKind.StructureOnly.ToString(), Section = heading, Detail = l.Level != r.Level ? $"Heading level {l.Level} → {r.Level}; same content." : "Same content in a different order." });
                    else unchanged++;
                    continue;
                }
                items.Add(new SddDriftItem
                {
                    Kind = (neutral ? DriftItemKind.ConsistencyDiscrepancy : DriftItemKind.Changed).ToString(), Section = heading,
                    Detail = neutral ? $"{Neutral} {removed.Count} line(s) only in the authored document, {added.Count} only in the generated document."
                        : version ? $"{removed.Count} line(s) removed, {added.Count} added."
                        : $"{removed.Count} line(s) only in the left document, {added.Count} only in the right document.",
                    Left = [.. removed.Take(8)], Right = [.. added.Take(8)],
                });
            }
            if (items.Count >= MaxItems) break;
        }
        if (items.Count == 0) return new(intent, context, "Structure-only difference: same normalized content.", [], new Dictionary<string, int>());
        var counts = items.GroupBy(i => i.Kind).ToDictionary(g => g.Key, g => g.Count());
        if (unchanged > 0) counts[DriftItemKind.Unchanged.ToString()] = unchanged;
        var same = unchanged > 0 ? $" {unchanged} unchanged." : "";
        var outcome = intent == DriftIntent.Manual ? $"Document differences: {items.Count} section(s) differ.{same}"
            : neutral ? $"{items.Count} section(s) differ.{same} Consistency discrepancies need review; neither side is assumed correct."
            : $"{items.Count} section(s) differ.{same}";
        return new(intent, context, outcome, items, counts);
    }

    // ── Overview ────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Every imported project/source with its authored documents and generated documentation availability, oldest first.</summary>
    public static IReadOnlyList<DriftImportSummary> ImportSummaries(SddLifecycleState state, IReadOnlyList<DriftDocument> catalog) =>
        [.. Imports(state).Select(i =>
        {
            var set = state.GeneratedDocumentationSets.FirstOrDefault(s => s.ProjectImportId == i.ImportId);
            var present = set?.State == "Present";
            return new DriftImportSummary(i,
                catalog.Count(d => d.Origin == DriftOrigin.Authored && d.ProjectImportId == i.ImportId),
                set?.State switch { "Present" => "Present", "NotPresent" => NotPresent, _ => "Not evaluated (no source snapshot)" },
                present ? set!.Artifacts.Count : 0, present ? set!.Artifacts.Select(a => a.ModuleId).Distinct().Count() : 0);
        })];

    /// <summary>The import Drift and Overview focus on: the workspace's current import, else the most recent one.</summary>
    public static string? FocusImportId(SddLifecycleState state) =>
        state.CurrentProjectImportId is { } id && state.ProjectImports.Any(i => i.ImportId == id) ? id
        : state.ProjectImports.OrderBy(i => i.ImportedAt).LastOrDefault()?.ImportId;

    // ── Pairing ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Authored ↔ generated pairs in one import. A Data Model and a generated Data Model, or an architecture document and the generated
    /// architecture, of the same module are related — but the same module and type are not evidence of equivalent scope. A pair is
    /// <see cref="DriftPairing.Equivalent"/> only when the generated documentation is rooted at the authored document's own folder (the
    /// generated set describes exactly that scope). Nothing is compared automatically; status comes from saved comparisons of exactly these
    /// fingerprints.
    /// </summary>
    public static IReadOnlyList<DriftCandidate> Candidates(IReadOnlyList<DriftDocument> catalog, IReadOnlyList<SddDocumentationDriftComparison> saved, string? importId)
    {
        if (importId is null) return [];
        var current = catalog.Where(d => d.ProjectImportId == importId && d.Supported && d.ContentAvailable).ToList();
        var moduleRoots = current.Where(d => d.Origin == DriftOrigin.Generated && d.Module is not null and not "(repository)").Select(d => d.Module! + "/").Distinct().ToList();
        var candidates = new List<DriftCandidate>();
        foreach (var generated in current.Where(d => d.Origin == DriftOrigin.Generated && d.Module is not null))
        {
            var kind = generated.TypeLabel.Replace("Generated · ", "");
            bool Under(DriftDocument d, string root) => StablePathOf(d).StartsWith(root, StringComparison.OrdinalIgnoreCase);
            bool InModule(DriftDocument d) => generated.Module == "(repository)" ? !moduleRoots.Any(r => Under(d, r)) : Under(d, generated.Module + "/");
            var authored = kind switch
            {
                "DataModel" => current.Where(d => d.Origin == DriftOrigin.Authored && d.TypeLabel == "Data model" && InModule(d)),
                "Architecture" => current.Where(d => d.Origin == DriftOrigin.Authored && InModule(d)
                    && System.IO.Path.GetFileName(d.Path).Contains("architecture", StringComparison.OrdinalIgnoreCase)),
                _ => [],
            };
            var label = kind == "DataModel" ? "Data Model" : "architecture";
            foreach (var a in authored)
            {
                var authoredFolder = System.IO.Path.GetDirectoryName(StablePathOf(a))?.Replace('\\', '/') ?? "";
                var equivalent = generated.Module is { } m && m != "(repository)" && string.Equals(authoredFolder, m, StringComparison.OrdinalIgnoreCase);
                var (authoredScope, generatedScope) = (ScopeOf(a), ScopeOf(generated));
                var basis = equivalent
                    ? "The generated documentation is rooted at the authored document's own folder: both describe the same scope."
                    : $"Same module and document type, but the authored document is scoped to {authoredScope} and the generated document to {generatedScope}. Not assumed equivalent.";
                candidates.Add(new DriftCandidate($"Authored {label} ↔ Generated {label} · {generated.Module}", a, generated, Status(saved, a, generated),
                    equivalent ? DriftPairing.Equivalent : DriftPairing.RelatedScopeDiffers, authoredScope, generatedScope, basis));
            }
        }
        return candidates;
    }

    /// <summary>"Comparison candidate" until a saved comparison of exactly these fingerprints exists; then its outcome.</summary>
    public static string Status(IReadOnlyList<SddDocumentationDriftComparison> saved, DriftDocument a, DriftDocument b)
    {
        var pairSaved = saved.FirstOrDefault(s => s.Left.Fingerprint == a.Fingerprint && s.Right.Fingerprint == b.Fingerprint
                                                  || s.Left.Fingerprint == b.Fingerprint && s.Right.Fingerprint == a.Fingerprint);
        return pairSaved is null ? "Comparison candidate" : pairSaved.Items.Count == 0 ? "No material difference" : "Differences found";
    }

    // ── Saved comparisons ───────────────────────────────────────────────────────────────────────────────────────────────

    public static SddDriftSide Side(DriftDocument d) => new()
    {
        DocumentId = d.Id, Origin = d.Origin.ToString(), LogicalKey = d.LogicalKey, DisplayName = d.DisplayName, Path = d.Path, Fingerprint = d.Fingerprint,
        RevisionId = d.RevisionId, RevisionNumber = d.RevisionNumber, ProjectImportId = d.ProjectImportId, ProjectRevision = d.ProjectRevision, LineageKey = d.LineageKey,
        GeneratedSetId = d.GeneratedSetId, SourceSnapshotId = d.SourceSnapshotId, Generator = d.Generator,
    };

    /// <summary>Saves a comparison with exact identities, mode and the result as computed; later imports never change it.</summary>
    public static SddDocumentationDriftComparison Save(SddLifecycleState state, DriftDocument left, DriftDocument right, DriftComparisonResult result, string? scope, DateTimeOffset now)
    {
        var saved = new SddDocumentationDriftComparison
        {
            CreatedAt = now, Intent = result.Intent.ToString(), Scope = WholeDocument, RevisionContext = result.RevisionContext, Left = Side(left), Right = Side(right),
            Outcome = result.Outcome, Counts = new(result.Counts), Items = [.. result.Items], AnalysisVersion = AnalysisVersion, WorkspaceScope = scope,
        };
        state.DocumentationDriftComparisons.Insert(0, saved);
        if (state.DocumentationDriftComparisons.Count > 50) state.DocumentationDriftComparisons.RemoveAt(state.DocumentationDriftComparisons.Count - 1);
        return saved;
    }

    /// <summary>The documents a saved comparison referenced, resolved by their exact ids and fingerprints — or null when that revision is gone.</summary>
    public static (DriftDocument? Left, DriftDocument? Right) Resolve(IReadOnlyList<DriftDocument> catalog, SddDocumentationDriftComparison saved) =>
        (catalog.FirstOrDefault(d => d.Id == saved.Left.DocumentId && d.Fingerprint == saved.Left.Fingerprint),
         catalog.FirstOrDefault(d => d.Id == saved.Right.DocumentId && d.Fingerprint == saved.Right.Fingerprint));

    /// <summary>The saved mode; comparisons saved before modes existed: authored ↔ generated stays consistency, same-origin version stays version.</summary>
    public static DriftIntent SavedIntent(SddDocumentationDriftComparison saved) =>
        Enum.TryParse<DriftIntent>(saved.Intent, out var intent) ? intent == DriftIntent.AuthoredVsGenerated ? DriftIntent.ConsistencyDrift : intent : DriftIntent.Manual;

    // ── Markdown sections ───────────────────────────────────────────────────────────────────────────────────────────────

    private sealed record Section(string Heading, int Level, int Order, List<string> Lines);

    private static Dictionary<string, Section> Sections(string text)
    {
        var sections = new Dictionary<string, Section>(StringComparer.Ordinal);
        var path = new List<(int Level, string Text)>();
        var current = new Section("(document start)", 0, 0, []);
        sections[""] = current;
        var tokens = MarkdownTokenizer.Tokenize(text);
        // A single leading H1 is the document title: compared on its own, never prefixed to every section (so differently titled documents align).
        var headings = tokens.Where(t => t.Kind == MarkdownTokenKind.Heading).ToList();
        var title = headings.Count > 0 && headings[0].HeadingLevel == 1 && headings.Count(h => h.HeadingLevel == 1) == 1 ? headings[0] : null;
        foreach (var token in tokens)
        {
            if (token == title)
            {
                sections["(document title)"] = new Section("(document title)", 1, sections.Count, [Clean(token.Content)]);
                continue;
            }
            if (token.Kind == MarkdownTokenKind.Heading)
            {
                var heading = Clean(token.Content);
                while (path.Count > 0 && path[^1].Level >= token.HeadingLevel) path.RemoveAt(path.Count - 1);
                path.Add((token.HeadingLevel, heading));
                var key = string.Join(" › ", path.Select(p => p.Text.ToLowerInvariant()));
                if (!sections.TryGetValue(key, out current!))
                    sections[key] = current = new Section(string.Join(" › ", path.Select(p => p.Text)), token.HeadingLevel, sections.Count, []);
                continue;
            }
            var line = token.Kind switch
            {
                MarkdownTokenKind.Blank or MarkdownTokenKind.TableSeparator or MarkdownTokenKind.HorizontalRule or MarkdownTokenKind.FencedCodeStart or MarkdownTokenKind.FencedCodeEnd => null,
                MarkdownTokenKind.TableRow => string.Join(" | ", (token.TableCells ?? []).Select(Clean)),
                MarkdownTokenKind.FencedCodeLine => token.RawLine.Trim(),
                _ => Clean(token.Content),
            };
            if (!string.IsNullOrWhiteSpace(line)) current.Lines.Add(line);
        }
        if (sections[""].Lines.Count == 0) sections.Remove("");
        return sections;
    }

    private static string Clean(string value) => Regex.Replace(MarkdownTokenizer.NormalizeInlineMarkup(value ?? ""), @"\s+", " ").Trim();

    private static string Normalize(string text) => Regex.Replace(text.Replace("\r\n", "\n"), @"[ \t]+\n", "\n").Trim();
}
