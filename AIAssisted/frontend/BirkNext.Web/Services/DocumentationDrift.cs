using System.Text.RegularExpressions;
using BirkNext.Web.Models;
using BirkNext.Web.Services.ProjectImport;

namespace BirkNext.Web.Services;

public enum DriftOrigin { Authored, Generated }

public enum DriftIntent { VersionDrift, ConsistencyDrift, AuthoredVsGenerated }

/// <summary>Deterministic result kinds. Nothing here claims a semantic contradiction or names which side is wrong.</summary>
public enum DriftItemKind { Added, Removed, Changed, OnlyInLeft, OnlyInRight, StructureOnly, ConsistencyDiscrepancy, Unchanged }

/// <summary>
/// A comparable documentation artifact as Documentation Drift sees it — a reference into the existing history, never a copy: an authored
/// artifact revision (Specification, Constitution, Plan, Tasks, Data Model, authored Markdown) or a generated document of an import's
/// Generated Documentation Set. <see cref="LogicalKey"/> is the stable identity across revisions (role/kind + path, archive root removed);
/// <see cref="Fingerprint"/> is the content identity.
/// </summary>
public sealed record DriftDocument(
    string Id, DriftOrigin Origin, string LogicalKey, string DisplayName, string Path, string TypeLabel, string Fingerprint,
    string? ProjectImportId, string? ProjectRevision, string? ArchiveName, Guid? RevisionId, int? RevisionNumber, string? GeneratedSetId,
    Guid? SourceSnapshotId, string? Module, string? Generator, bool ContentAvailable, bool Supported, string? WorkspaceScope);

public sealed record DriftComparisonResult(
    DriftIntent Intent, string RevisionContext, string Outcome, IReadOnlyList<SddDriftItem> Items, IReadOnlyDictionary<string, int> Counts);

public sealed record GeneratedSetComparison(int Unchanged, int Changed, int Added, int Removed,
    IReadOnlyList<(string Key, string State, DriftDocument? Left, DriftDocument? Right)> Rows);

/// <summary>
/// One project revision (an import) for the Overview: imported source, its source snapshot and generated documentation availability. A
/// revision without a generated set reads "Not present" — neutral, never an error or a missing requirement.
/// </summary>
public sealed record DriftRevisionSummary(string Revision, string ImportId, string ArchiveName, Guid? SourceSnapshotId, int AuthoredDocuments,
    string GeneratedState, int GeneratedDocuments, int GeneratedModules);

/// <summary>Generated documentation inventory between two consecutive project revisions: coverage first, then the inventory difference.</summary>
public sealed record GeneratedInventoryChange(string LeftRevision, string RightRevision, int LeftDocuments, int RightDocuments, int LeftModules, int RightModules,
    GeneratedSetComparison? Inventory)
{
    public bool CoverageChanged => LeftDocuments != RightDocuments || LeftModules != RightModules;

    /// <summary>"Generated documentation coverage increased from 1 module / 2 documents to 15 modules / 91 documents."</summary>
    public string CoverageText => !CoverageChanged
        ? $"Generated documentation coverage unchanged: {RightModules} module(s) / {RightDocuments} document(s)."
        : $"Generated documentation coverage {(RightDocuments >= LeftDocuments ? "increased" : "decreased")} from {LeftModules} module{(LeftModules == 1 ? "" : "s")} / {LeftDocuments} document{(LeftDocuments == 1 ? "" : "s")} to {RightModules} module{(RightModules == 1 ? "" : "s")} / {RightDocuments} document{(RightDocuments == 1 ? "" : "s")}.";
}

/// <summary>A deterministic authored ↔ generated pairing for the Drift tab, with its review status from saved comparisons (exact fingerprints).</summary>
public sealed record DriftCandidate(string Title, DriftDocument Authored, DriftDocument Generated, string Status);

/// <summary>One document identity (same origin, type and stable path) and its versions per project revision, for the Changes tab.</summary>
public sealed record DriftIdentity(string LogicalKey, string Label, DriftOrigin Origin, IReadOnlyList<DriftDocument> Versions);

/// <summary>
/// Documentation Drift: compares documentation artifacts by version and by consistency. It consumes the existing provenance — Project Import
/// records (a project revision is an import), append-only authored artifact revisions, and each import's Generated Documentation Set linked
/// to its source snapshot — and owns only artifact selection, intent, the comparison and its saved results. Authored documents are intent and
/// design evidence, generated documents are implementation-derived documentation; neither is implementation truth, raw source stays the
/// Source Analysis authority, and a comparison never declares a winner.
/// </summary>
public static class DocumentationDrift
{
    public const int AnalysisVersion = 1;
    public const string Description = "Compare authored and generated documentation artifacts to identify version and consistency drift.";
    public const string SameRevision = "Same project revision";
    public const string DifferentRevisions = "Different project revisions";
    public const string UnknownRevision = "No shared project revision";
    public const string SelectDifferent = "Select two different document versions/artifacts.";
    public const string Unsupported = "This artifact type is not supported by Documentation Drift yet.";
    public const string NoContentChange = "No content change";
    public const string Neutral = "The authored and generated documentation describe this differently.";
    public const string NeutralReview = "This may indicate documentation drift and requires review. Neither side is assumed correct.";
    public const string SourceBoundary = "Generated documentation is implementation-derived. Use Source Analysis for raw source evidence.";
    public const string CoverageExpansionNote = "Most differences are due to generated-document coverage changing between project revisions, not necessarily content drift in equivalent documents.";
    public const string NotPresent = "Not present";
    public const string SelectDocument = "Select a document to compare.";
    public const string NoResult = "Select two documents and run Compare.";
    public const string NoEarlierRevision = "No earlier revision of this document is available.";
    public const string GeneratedNotPresent = "Generated documentation not present for this project revision.";
    public const string NotPresentMeaning = "Not present means no generated documentation set was present for that imported project revision. It is neutral — not an error, an unhealthy state, a missing requirement or an import failure. An earlier set is never reused for a later revision.";
    public const string ComparisonBasis = "Deterministic text and structure comparison: sections by heading, normalized lines. Semantic change, possible contradiction and terminology drift are not assessed.";
    public const string WholeDocument = "Whole document";
    private const int MaxItems = 300;

    public static string SetIdFor(string importId) => "genset-" + importId;

    public static bool IsMarkdown(string path) => path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase);

    /// <summary>Project revisions are the workspace's imports, numbered R1… in import order (one record per exact archive).</summary>
    public static IReadOnlyDictionary<string, string> RevisionLabels(SddLifecycleState state) =>
        state.ProjectImports.OrderBy(i => i.ImportedAt).ThenBy(i => i.ImportId, StringComparer.Ordinal)
            .Select((i, n) => (i.ImportId, Label: $"R{n + 1}")).ToDictionary(x => x.ImportId, x => x.Label, StringComparer.Ordinal);

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

    /// <summary>Every comparable artifact in the workspace history: authored revisions and generated documents of every import's set.</summary>
    public static IReadOnlyList<DriftDocument> Catalog(SddLifecycleState state)
    {
        var labels = RevisionLabels(state);
        var imports = state.ProjectImports.ToDictionary(i => i.ImportId, StringComparer.Ordinal);
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
            var stable = StablePath(state, importId, path);
            var supported = IsMarkdown(path) || !System.IO.Path.HasExtension(path);
            documents.Add(new DriftDocument("rev:" + revision.RevisionId, DriftOrigin.Authored,
                $"authored|{revision.Role}|{(importId is null ? revision.WorkspaceScope + "|" : "")}{stable.ToLowerInvariant()}", Display(revision.Role) + " · " + System.IO.Path.GetFileName(path),
                path, Display(revision.Role), revision.Fingerprint, importId, importId is null ? null : labels.GetValueOrDefault(importId), importId is null ? null : imports.GetValueOrDefault(importId)?.ArchiveFileName,
                revision.RevisionId, revision.Revision, null, importId is null ? null : imports.GetValueOrDefault(importId)?.SourceSnapshotId, null, null, true, supported, revision.WorkspaceScope));
        }
        foreach (var set in state.GeneratedDocumentationSets)
        {
            var label = labels.GetValueOrDefault(set.ProjectImportId);
            foreach (var artifact in set.Artifacts)
            {
                var stable = StablePath(state, set.ProjectImportId, artifact.RelativePath);
                var module = ModuleName(state, set.ProjectImportId, artifact.ModuleRoot);
                documents.Add(new DriftDocument($"gen:{set.SetId}|{artifact.RelativePath}", DriftOrigin.Generated, $"generated|{module.ToLowerInvariant()}|{stable.ToLowerInvariant()}",
                    $"{module} · {System.IO.Path.GetFileName(artifact.RelativePath)}", artifact.RelativePath, "Generated · " + artifact.Kind, artifact.Fingerprint, set.ProjectImportId, label,
                    imports.GetValueOrDefault(set.ProjectImportId)?.ArchiveFileName, null, null, set.SetId, set.SourceSnapshotId, module, artifact.Generator,
                    artifact.ContentCaptured && state.GeneratedDocumentContents.ContainsKey(artifact.Fingerprint), IsMarkdown(artifact.RelativePath), ProjectImportScope.For(set.ProjectImportId)));
            }
        }
        return [.. documents.OrderBy(d => d.Origin).ThenBy(d => d.LogicalKey, StringComparer.Ordinal).ThenBy(d => d.ProjectRevision ?? "", StringComparer.Ordinal)];
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

    /// <summary>"Authored · Specification · specs/001-x/spec.md · R4 · rev 2" / "Generated · Data Model · Person/autodoc/data-model.md · R4".</summary>
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

    /// <summary>Document identities with their versions (oldest first) — the same identity rule as Version drift, never a name-similarity guess.</summary>
    public static IReadOnlyList<DriftIdentity> Identities(IReadOnlyList<DriftDocument> catalog) =>
        [.. catalog.Where(d => d.Supported).GroupBy(d => d.LogicalKey).Select(g =>
        {
            var first = g.First();
            return new DriftIdentity(g.Key, $"{first.Origin} · {KindLabel(first)} · {StablePathOf(first)}", first.Origin, Versions(catalog, first));
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

    /// <summary>
    /// The default intent for a pair: authored ↔ generated → Consistency drift; the same document identity (authored ↔ authored or generated ↔
    /// generated) → Version drift; two different documents of one origin → Consistency drift (they are not versions of one artifact).
    /// </summary>
    public static DriftIntent SuggestIntent(DriftDocument left, DriftDocument right) =>
        left.Origin != right.Origin ? DriftIntent.ConsistencyDrift
        : left.LogicalKey == right.LogicalKey ? DriftIntent.VersionDrift
        : DriftIntent.ConsistencyDrift;

    /// <summary>
    /// Intents that mean something for the pair: authored ↔ generated is a consistency comparison only (they are never versions of each other);
    /// documents of the same origin may be compared as versions or for consistency.
    /// </summary>
    public static IReadOnlyList<DriftIntent> AllowedIntents(DriftDocument? left, DriftDocument? right) =>
        left is not null && right is not null && left.Origin != right.Origin ? [DriftIntent.ConsistencyDrift] : [DriftIntent.VersionDrift, DriftIntent.ConsistencyDrift];

    /// <summary>"Version comparison" or "Consistency comparison" — what the selected pair is.</summary>
    public static string ComparisonType(DriftIntent intent) => intent == DriftIntent.VersionDrift ? "Version comparison" : "Consistency comparison";

    /// <summary>Identity note for two documents of one origin that are not the same artifact: never silently treated as versions.</summary>
    public static string? IdentityNote(DriftDocument? left, DriftDocument? right) =>
        left is not null && right is not null && left.Origin == right.Origin && left.LogicalKey != right.LogicalKey
            ? "Possible equivalent document — review required. Different paths or types are never treated as versions of one artifact; compared for consistency only."
            : null;

    public static string Label(DriftIntent intent) => intent switch
    {
        DriftIntent.VersionDrift => "Version drift",
        // Saved comparisons from before the intents were merged keep reading correctly.
        _ => "Consistency drift",
    };

    /// <summary>
    /// A result kind in the words of the compared pair: "Added in R4" / "Removed since R3" / "Changed between R3 and R4" for versions;
    /// "Only in authored documentation" / "Only in generated documentation" for authored ↔ generated. Never names a correct side.
    /// </summary>
    public static string Label(DriftItemKind kind, DriftDocument? left, DriftDocument? right)
    {
        var (l, r) = (left?.ProjectRevision, right?.ProjectRevision);
        var crossOrigin = left is not null && right is not null && left.Origin != right.Origin;
        return kind switch
        {
            DriftItemKind.Added when r is not null => $"Added in {r}",
            DriftItemKind.Removed when l is not null => $"Removed since {l}",
            DriftItemKind.Changed when l is not null && r is not null && l != r => $"Changed between {l} and {r}",
            DriftItemKind.OnlyInLeft when crossOrigin => $"Only in {left!.Origin.ToString().ToLowerInvariant()} documentation",
            DriftItemKind.OnlyInRight when crossOrigin => $"Only in {right!.Origin.ToString().ToLowerInvariant()} documentation",
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

    /// <summary>Context only: same import, different imports, or no shared revision (manual documents). Never a staleness verdict.</summary>
    public static string RevisionContext(DriftDocument left, DriftDocument right) =>
        left.ProjectImportId is null || right.ProjectImportId is null ? UnknownRevision
        : left.ProjectImportId == right.ProjectImportId ? SameRevision : DifferentRevisions;

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

    /// <summary>Why a pair cannot be compared, or null. The same artifact at the same revision and fingerprint is never compared with itself.</summary>
    public static string? Guard(DriftDocument? left, DriftDocument? right)
    {
        if (left is null || right is null) return "Select a document for each side.";
        if (!left.Supported || !right.Supported) return Unsupported;
        if (left.Id == right.Id || left.LogicalKey == right.LogicalKey && left.Fingerprint == right.Fingerprint && left.ProjectImportId == right.ProjectImportId && left.RevisionId == right.RevisionId)
            return SelectDifferent;
        if (!left.ContentAvailable || !right.ContentAvailable) return "The text of a selected document was not captured with its import, so it cannot be compared.";
        return null;
    }

    /// <summary>Other versions of the same logical artifact, newest project revision last (for "Previous revision" / "Current revision").</summary>
    public static IReadOnlyList<DriftDocument> Versions(IReadOnlyList<DriftDocument> catalog, DriftDocument document) =>
        [.. catalog.Where(d => d.LogicalKey == document.LogicalKey).OrderBy(d => d.ProjectRevision is { } r ? int.Parse(r[1..]) : 0).ThenBy(d => d.RevisionNumber ?? 0)];

    /// <summary>
    /// Suggested generated documents for an authored one: those of the Generated Documentation Set of the same project revision, ranked by
    /// document kind for the role. "Suggested comparison" only — never an equivalent document, never selected automatically.
    /// </summary>
    public static IReadOnlyList<DriftDocument> SuggestedGenerated(IReadOnlyList<DriftDocument> catalog, DriftDocument authored)
    {
        if (authored.Origin != DriftOrigin.Authored || authored.ProjectImportId is null) return [];
        string[] preferred = authored.TypeLabel switch
        {
            "Specification" => ["Overview", "Architecture", "DataFlow", "ApiContract"],
            "Data model" => ["DataModel"],
            "Plan" => ["Architecture", "Overview"],
            "Constitution" => ["Architecture", "Overview"],
            _ => ["Overview"],
        };
        return [.. catalog.Where(d => d.Origin == DriftOrigin.Generated && d.ProjectImportId == authored.ProjectImportId && d.Supported)
            .OrderBy(d => Array.FindIndex(preferred, k => d.TypeLabel.EndsWith(k, StringComparison.Ordinal)) is var i && i < 0 ? int.MaxValue : i)
            .ThenBy(d => d.Path, StringComparer.Ordinal).Take(10)];
    }

    /// <summary>
    /// Structure-aware comparison of two Markdown documents with the shared tokenizer: sections by heading path (heading text, levels ignored),
    /// each section's normalized lines (list items, table rows, prose, code) compared as sets. Identical content → "No content change".
    /// Version drift reports Added/Removed/Changed; the other intents report Only in left/right and, for authored vs generated, a neutral
    /// consistency discrepancy. A line diff never becomes a claimed contradiction.
    /// </summary>
    public static DriftComparisonResult Compare(DriftDocument left, string leftText, DriftDocument right, string rightText, DriftIntent intent)
    {
        var context = RevisionContext(left, right);
        var items = new List<SddDriftItem>();
        // Authored ↔ generated is always a neutral consistency comparison (also for comparisons saved as "Authored vs generated").
        var neutral = intent == DriftIntent.AuthoredVsGenerated || intent == DriftIntent.ConsistencyDrift && left.Origin != right.Origin;
        if (left.Origin == right.Origin && left.Fingerprint == right.Fingerprint || Normalize(leftText) == Normalize(rightText))
            return new(intent, context, NoContentChange, [], new Dictionary<string, int>());
        var a = Sections(leftText);
        var b = Sections(rightText);
        var unchanged = 0;
        foreach (var key in a.Keys.Union(b.Keys).OrderBy(k => (a.TryGetValue(k, out var x) ? x.Order : int.MaxValue, b.TryGetValue(k, out var y) ? y.Order : int.MaxValue)))
        {
            var inLeft = a.TryGetValue(key, out var l);
            var inRight = b.TryGetValue(key, out var r);
            var heading = (l ?? r)!.Heading;
            if (inLeft && !inRight)
                items.Add(new SddDriftItem { Kind = (intent == DriftIntent.VersionDrift ? DriftItemKind.Removed : DriftItemKind.OnlyInLeft).ToString(), Section = heading, Detail = neutral ? $"Section present only in the {left.Origin.ToString().ToLowerInvariant()} document." : "Section present only in the left document.", Left = [.. l!.Lines.Take(5)] });
            else if (!inLeft && inRight)
                items.Add(new SddDriftItem { Kind = (intent == DriftIntent.VersionDrift ? DriftItemKind.Added : DriftItemKind.OnlyInRight).ToString(), Section = heading, Detail = neutral ? $"Section present only in the {right.Origin.ToString().ToLowerInvariant()} document." : "Section present only in the right document.", Right = [.. r!.Lines.Take(5)] });
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
                var kind = intent == DriftIntent.VersionDrift ? DriftItemKind.Changed : neutral ? DriftItemKind.ConsistencyDiscrepancy : DriftItemKind.Changed;
                items.Add(new SddDriftItem
                {
                    Kind = kind.ToString(), Section = heading,
                    Detail = neutral ? $"{Neutral} {removed.Count} line(s) only in the {left.Origin.ToString().ToLowerInvariant()} document, {added.Count} only in the {right.Origin.ToString().ToLowerInvariant()} document."
                        : $"{removed.Count} line(s) removed, {added.Count} added.",
                    Left = [.. removed.Take(8)], Right = [.. added.Take(8)],
                });
            }
            if (items.Count >= MaxItems) break;
        }
        if (items.Count == 0) return new(intent, context, "Structure-only difference: same normalized content.", [], new Dictionary<string, int>());
        var counts = items.GroupBy(i => i.Kind).ToDictionary(g => g.Key, g => g.Count());
        if (unchanged > 0) counts[DriftItemKind.Unchanged.ToString()] = unchanged;
        var same = unchanged > 0 ? $" {unchanged} unchanged." : "";
        var outcome = neutral
            ? $"{items.Count} section(s) differ.{same} Consistency discrepancies need review; neither side is assumed correct."
            : $"{items.Count} section(s) differ.{same}";
        return new(intent, context, outcome, items, counts);
    }

    /// <summary>Every project revision with its imported source, source snapshot and generated documentation availability, oldest first.</summary>
    public static IReadOnlyList<DriftRevisionSummary> Revisions(SddLifecycleState state, IReadOnlyList<DriftDocument> catalog)
    {
        var labels = RevisionLabels(state);
        return [.. state.ProjectImports.OrderBy(i => i.ImportedAt).ThenBy(i => i.ImportId, StringComparer.Ordinal).Select(i =>
        {
            var set = state.GeneratedDocumentationSets.FirstOrDefault(s => s.ProjectImportId == i.ImportId);
            var generated = set?.State == "Present" ? set.Artifacts.Count : 0;
            return new DriftRevisionSummary(labels[i.ImportId], i.ImportId, i.ArchiveFileName, i.SourceSnapshotId,
                catalog.Count(d => d.Origin == DriftOrigin.Authored && d.ProjectImportId == i.ImportId),
                set?.State switch { "Present" => "Present", "NotPresent" => NotPresent, _ => "Not evaluated (no source snapshot)" },
                generated, set?.State == "Present" ? set.Artifacts.Select(a => a.ModuleId).Distinct().Count() : 0);
        })];
    }

    /// <summary>
    /// Generated documentation history: each consecutive pair of project revisions — coverage (documents and modules) first, then the inventory
    /// difference (an inventory change, never drift defects). A revision without a set counts 0, never an earlier set.
    /// </summary>
    public static IReadOnlyList<GeneratedInventoryChange> GeneratedHistory(SddLifecycleState state, IReadOnlyList<DriftDocument> catalog)
    {
        var revisions = Revisions(state, catalog);
        var changes = new List<GeneratedInventoryChange>();
        for (var i = 1; i < revisions.Count; i++)
        {
            var (a, b) = (revisions[i - 1], revisions[i]);
            if (a.GeneratedDocuments == 0 && b.GeneratedDocuments == 0) continue;
            var inventory = CompareSets(catalog, SetIdFor(a.ImportId), SetIdFor(b.ImportId));
            changes.Add(new GeneratedInventoryChange(a.Revision, b.Revision, a.GeneratedDocuments, b.GeneratedDocuments, a.GeneratedModules, b.GeneratedModules, inventory));
        }
        return changes;
    }

    /// <summary>
    /// Deterministic authored ↔ generated candidates in the same project revision: an authored data model and the generated data model of
    /// its module; an authored architecture document and the generated architecture document of its module. Nothing is paired by a similar
    /// name, and nothing is compared automatically. Status comes from saved comparisons of exactly these fingerprints.
    /// </summary>
    public static IReadOnlyList<DriftCandidate> Candidates(IReadOnlyList<DriftDocument> catalog, IReadOnlyList<SddDocumentationDriftComparison> saved)
    {
        var latestImport = catalog.Where(d => d.ProjectRevision is not null).OrderBy(d => int.Parse(d.ProjectRevision![1..])).LastOrDefault()?.ProjectImportId;
        if (latestImport is null) return [];
        var current = catalog.Where(d => d.ProjectImportId == latestImport && d.Supported && d.ContentAvailable).ToList();
        var candidates = new List<DriftCandidate>();
        foreach (var generated in current.Where(d => d.Origin == DriftOrigin.Generated && d.Module is not null))
        {
            var kind = generated.TypeLabel.Replace("Generated · ", "");
            // Module membership by the stable path's leading folders; a repository-level generated document pairs only with authored
            // documents outside every module that has its own generated documentation.
            var moduleRoots = current.Where(d => d.Origin == DriftOrigin.Generated && d.Module is not null and not "(repository)").Select(d => d.Module! + "/").Distinct().ToList();
            bool Under(DriftDocument d, string root) => StablePathOf(d).StartsWith(root, StringComparison.OrdinalIgnoreCase);
            bool InModule(DriftDocument d) => generated.Module == "(repository)" ? !moduleRoots.Any(r => Under(d, r)) : Under(d, generated.Module + "/");
            var authored = kind switch
            {
                "DataModel" => current.Where(d => d.Origin == DriftOrigin.Authored && d.TypeLabel == "Data model" && InModule(d)),
                "Architecture" => current.Where(d => d.Origin == DriftOrigin.Authored && InModule(d)
                    && System.IO.Path.GetFileName(d.Path).Contains("architecture", StringComparison.OrdinalIgnoreCase)),
                _ => [],
            };
            foreach (var a in authored)
            {
                var pairSaved = saved.FirstOrDefault(s => s.Left.Fingerprint == a.Fingerprint && s.Right.Fingerprint == generated.Fingerprint
                                                          || s.Left.Fingerprint == generated.Fingerprint && s.Right.Fingerprint == a.Fingerprint);
                var status = pairSaved is null ? "Not compared" : pairSaved.Items.Count == 0 ? "No deterministic discrepancy detected" : "Differences found — needs review";
                candidates.Add(new DriftCandidate($"Authored {(kind == "DataModel" ? "Data Model" : "architecture")} ↔ Generated {(kind == "DataModel" ? "Data Model" : "architecture")} · {generated.Module}",
                    a, generated, status));
            }
        }
        return candidates;
    }

    /// <summary>Generated set ↔ generated set: unchanged, changed, added and removed documents by module + path (fingerprint decides change).</summary>
    public static GeneratedSetComparison CompareSets(IReadOnlyList<DriftDocument> catalog, string leftSetId, string rightSetId)
    {
        var left = catalog.Where(d => d.GeneratedSetId == leftSetId).ToDictionary(d => d.LogicalKey, StringComparer.Ordinal);
        var right = catalog.Where(d => d.GeneratedSetId == rightSetId).ToDictionary(d => d.LogicalKey, StringComparer.Ordinal);
        var rows = left.Keys.Union(right.Keys).Order(StringComparer.Ordinal).Select(key =>
        {
            var l = left.GetValueOrDefault(key);
            var r = right.GetValueOrDefault(key);
            var state = l is null ? "Added" : r is null ? "Removed" : l.Fingerprint == r.Fingerprint ? "Unchanged" : "Changed";
            return (key, state, l, r);
        }).ToList();
        return new(rows.Count(x => x.state == "Unchanged"), rows.Count(x => x.state == "Changed"), rows.Count(x => x.state == "Added"), rows.Count(x => x.state == "Removed"), rows);
    }

    /// <summary>Documentation changes in a project revision against the previous one: authored and generated artifacts (by logical key).</summary>
    public static IReadOnlyList<(string Key, string Origin, string State)> RevisionChanges(SddLifecycleState state, IReadOnlyList<DriftDocument> catalog, string importId)
    {
        var ordered = state.ProjectImports.OrderBy(i => i.ImportedAt).ThenBy(i => i.ImportId, StringComparer.Ordinal).Select(i => i.ImportId).ToList();
        var index = ordered.IndexOf(importId);
        if (index <= 0) return [];
        var previous = ordered[index - 1];
        IEnumerable<DriftDocument> Of(string id) => catalog.Where(d => d.ProjectImportId == id);
        var before = Of(previous).GroupBy(d => d.LogicalKey).ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
        var after = Of(importId).GroupBy(d => d.LogicalKey).ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
        return [.. before.Keys.Union(after.Keys).Order(StringComparer.Ordinal).Select(key =>
        {
            var b = before.GetValueOrDefault(key);
            var a = after.GetValueOrDefault(key);
            var stateText = b is null ? "Added" : a is null ? "Removed" : b.Fingerprint == a.Fingerprint ? "Unchanged" : "Changed";
            return (key, (a ?? b)!.Origin.ToString(), stateText);
        })];
    }

    public static SddDriftSide Side(DriftDocument d) => new()
    {
        DocumentId = d.Id, Origin = d.Origin.ToString(), LogicalKey = d.LogicalKey, DisplayName = d.DisplayName, Path = d.Path, Fingerprint = d.Fingerprint,
        RevisionId = d.RevisionId, RevisionNumber = d.RevisionNumber, ProjectImportId = d.ProjectImportId, ProjectRevision = d.ProjectRevision, GeneratedSetId = d.GeneratedSetId,
        SourceSnapshotId = d.SourceSnapshotId, Generator = d.Generator,
    };

    /// <summary>Saves a comparison with exact identities and the result as computed; later imports never change it.</summary>
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

    /// <summary>The documents a saved comparison referenced, resolved by their exact ids — or null when that revision is no longer in the workspace.</summary>
    public static (DriftDocument? Left, DriftDocument? Right) Resolve(IReadOnlyList<DriftDocument> catalog, SddDocumentationDriftComparison saved) =>
        (catalog.FirstOrDefault(d => d.Id == saved.Left.DocumentId && d.Fingerprint == saved.Left.Fingerprint),
         catalog.FirstOrDefault(d => d.Id == saved.Right.DocumentId && d.Fingerprint == saved.Right.Fingerprint));

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
