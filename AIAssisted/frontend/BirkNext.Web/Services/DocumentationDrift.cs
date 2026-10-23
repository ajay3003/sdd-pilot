using System.Text.RegularExpressions;
using BirkNext.Web.Models;
using BirkNext.Web.Services.ProjectImport;

namespace BirkNext.Web.Services;

public enum DriftOrigin { Authored, Generated }

public enum DriftIntent { VersionDrift, ConsistencyDrift, AuthoredVsGenerated }

/// <summary>Deterministic result kinds. Nothing here claims a semantic contradiction or names which side is wrong.</summary>
public enum DriftItemKind { Added, Removed, Changed, OnlyInLeft, OnlyInRight, StructureOnly, ConsistencyDiscrepancy }

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
        if (importId is not null && state.ProjectImports.FirstOrDefault(i => i.ImportId == importId) is { ProjectNameBasis: "ArchiveRoot" } record
            && normalized.StartsWith(record.ProjectName + "/", StringComparison.OrdinalIgnoreCase))
            return normalized[(record.ProjectName.Length + 1)..];
        return normalized;
    }

    /// <summary>Every comparable artifact in the workspace history: authored revisions and generated documents of every import's set.</summary>
    public static IReadOnlyList<DriftDocument> Catalog(SddLifecycleState state)
    {
        var labels = RevisionLabels(state);
        var imports = state.ProjectImports.ToDictionary(i => i.ImportId, StringComparer.Ordinal);
        var documents = new List<DriftDocument>();
        foreach (var revision in state.Revisions.Where(r => !string.IsNullOrWhiteSpace(r.Content)))
        {
            var importId = revision.ProjectImportId ?? ProjectImportScope.ImportIdOf(revision.WorkspaceScope);
            var path = revision.SourceReference ?? revision.FileName;
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

    private static string Display(string role) => role switch
    {
        "Spec" or "Specification" => "Specification",
        "DataModel" => "Data model",
        _ => role,
    };

    /// <summary>The text behind a document reference: the authored revision's content or the generated content store; null when not captured.</summary>
    public static string? Text(SddLifecycleState state, DriftDocument document) => document.Origin == DriftOrigin.Authored
        ? state.Revisions.FirstOrDefault(r => r.RevisionId == document.RevisionId)?.Content
        : state.GeneratedDocumentContents.GetValueOrDefault(document.Fingerprint);

    /// <summary>Authored ↔ Generated → Authored vs generated; the same logical artifact → Version drift; otherwise Consistency drift. The user may override.</summary>
    public static DriftIntent SuggestIntent(DriftDocument left, DriftDocument right) =>
        left.Origin != right.Origin ? DriftIntent.AuthoredVsGenerated
        : left.LogicalKey == right.LogicalKey ? DriftIntent.VersionDrift
        : DriftIntent.ConsistencyDrift;

    public static string Label(DriftIntent intent) => intent switch
    {
        DriftIntent.VersionDrift => "Version drift",
        DriftIntent.AuthoredVsGenerated => "Authored vs generated",
        _ => "Consistency drift",
    };

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
        if (left.Origin == right.Origin && left.Fingerprint == right.Fingerprint || Normalize(leftText) == Normalize(rightText))
            return new(intent, context, NoContentChange, [], new Dictionary<string, int>());
        var a = Sections(leftText);
        var b = Sections(rightText);
        foreach (var key in a.Keys.Union(b.Keys).OrderBy(k => (a.TryGetValue(k, out var x) ? x.Order : int.MaxValue, b.TryGetValue(k, out var y) ? y.Order : int.MaxValue)))
        {
            var inLeft = a.TryGetValue(key, out var l);
            var inRight = b.TryGetValue(key, out var r);
            var heading = (l ?? r)!.Heading;
            if (inLeft && !inRight)
                items.Add(new SddDriftItem { Kind = (intent == DriftIntent.VersionDrift ? DriftItemKind.Removed : DriftItemKind.OnlyInLeft).ToString(), Section = heading, Detail = "Section present only in the left document.", Left = [.. l!.Lines.Take(5)] });
            else if (!inLeft && inRight)
                items.Add(new SddDriftItem { Kind = (intent == DriftIntent.VersionDrift ? DriftItemKind.Added : DriftItemKind.OnlyInRight).ToString(), Section = heading, Detail = "Section present only in the right document.", Right = [.. r!.Lines.Take(5)] });
            else
            {
                var removed = l!.Lines.Except(r!.Lines, StringComparer.Ordinal).ToList();
                var added = r.Lines.Except(l.Lines, StringComparer.Ordinal).ToList();
                if (removed.Count == 0 && added.Count == 0)
                {
                    if (!l.Lines.SequenceEqual(r.Lines, StringComparer.Ordinal) || l.Level != r.Level)
                        items.Add(new SddDriftItem { Kind = DriftItemKind.StructureOnly.ToString(), Section = heading, Detail = l.Level != r.Level ? $"Heading level {l.Level} → {r.Level}; same content." : "Same content in a different order." });
                    continue;
                }
                var kind = intent switch { DriftIntent.VersionDrift => DriftItemKind.Changed, DriftIntent.AuthoredVsGenerated => DriftItemKind.ConsistencyDiscrepancy, _ => DriftItemKind.Changed };
                items.Add(new SddDriftItem
                {
                    Kind = kind.ToString(), Section = heading,
                    Detail = intent == DriftIntent.AuthoredVsGenerated ? $"{Neutral} {removed.Count} line(s) only on the left, {added.Count} only on the right."
                        : $"{removed.Count} line(s) removed, {added.Count} added.",
                    Left = [.. removed.Take(8)], Right = [.. added.Take(8)],
                });
            }
            if (items.Count >= MaxItems) break;
        }
        if (items.Count == 0) return new(intent, context, "Structure-only difference: same normalized content.", [], new Dictionary<string, int>());
        var counts = items.GroupBy(i => i.Kind).ToDictionary(g => g.Key, g => g.Count());
        var outcome = intent == DriftIntent.AuthoredVsGenerated
            ? $"{items.Count} section(s) differ. Consistency discrepancies need review; neither side is assumed correct."
            : $"{items.Count} section(s) differ.";
        return new(intent, context, outcome, items, counts);
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
        RevisionId = d.RevisionId, ProjectImportId = d.ProjectImportId, ProjectRevision = d.ProjectRevision, GeneratedSetId = d.GeneratedSetId,
        SourceSnapshotId = d.SourceSnapshotId, Generator = d.Generator,
    };

    /// <summary>Saves a comparison with exact identities and the result as computed; later imports never change it.</summary>
    public static SddDocumentationDriftComparison Save(SddLifecycleState state, DriftDocument left, DriftDocument right, DriftComparisonResult result, string? scope, DateTimeOffset now)
    {
        var saved = new SddDocumentationDriftComparison
        {
            CreatedAt = now, Intent = result.Intent.ToString(), RevisionContext = result.RevisionContext, Left = Side(left), Right = Side(right),
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
