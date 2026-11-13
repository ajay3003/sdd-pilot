using System.Text.Json.Serialization;

namespace BirkNext.Glossary;

// ── Project glossary: BirkNext owns the CAPABILITY, the imported project owns the CONTENT ────────────────────────────────
// A project's own glossary (e.g. M2LB docs/glossary.md) is evidence of THAT project's Source Analysis snapshot: detected and parsed when the
// snapshot is analyzed, stored with it, and read by every consumer from there. It is never global BirkNext vocabulary, never applied to
// BirkNext UI or status words, never copied into settings, and another project never inherits it. A new snapshot = a new interpretation;
// historical analyses keep the glossary of their own snapshot.

/// <summary>What a glossary-like document is for its project, from document evidence — a file name alone never makes a glossary authoritative.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GlossaryAuthority
{
    /// <summary>The document states its authority, or other project documents point to it as the naming rule.</summary>
    Authoritative,
    /// <summary>A proposal / pull-request item list for the glossary: never merged into the authoritative terms.</summary>
    Proposed,
    /// <summary>Glossary-like, without authority evidence (module-local list, inventory).</summary>
    Supplementary,
    Unknown,
    /// <summary>Two or more documents claim authority: needs review, no silent winner.</summary>
    Conflict,
}

/// <summary>
/// What a glossary table row is, from its table's own term-column header. A glossary document also holds tables that are not terms: naming
/// conventions ("Kind | Convention | Example"), pinned-name owner groups ("Owner | Names | Examples"), known inconsistencies ("Issue | Where | …")
/// and its sources ("Source | Status"). Those rows stay evidence under their own section but are never terms. Any other header is a term table.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GlossaryRowKind
{
    Term,
    NamingConvention,
    PinnedNames,
    KnownInconsistency,
    Source,
}

/// <summary>One glossary-like document of the project's snapshot, with why it has its authority.</summary>
public sealed record ProjectGlossaryArtifact
{
    /// <summary>Stable within the project: the normalised source path.</summary>
    public string GlossaryId { get; init; } = "";
    public string SourcePath { get; init; } = "";
    public string Title { get; init; } = "";
    public GlossaryAuthority Authority { get; init; }
    /// <summary>Plain-language evidence for the authority ("States it is the single list for the whole platform"; "Referenced by 12 project documents").</summary>
    public List<string> AuthorityEvidence { get; init; } = [];
    public string ContentHash { get; init; } = "";
    /// <summary>Source-defined sections (heading path), in document order.</summary>
    public List<string> Sections { get; init; } = [];
    /// <summary>Project documents that refer to this glossary by path.</summary>
    public List<string> ReferencedBy { get; init; } = [];
    public int EntryCount { get; init; }
}

/// <summary>
/// One row of a glossary table, as the source states it. Only what the source format holds: the term (first column), its preferred form and
/// meaning when the table has such columns, a decision marker when the row carries one (✅ / ⚠ / a Status cell), and every cell by its own
/// header. The category is the source's own section ("2.1 The child and identity", "3. Pinned names …"), never a BirkNext category.
/// </summary>
public sealed record ProjectGlossaryEntry
{
    public string EntryId { get; init; } = "";
    public string GlossaryId { get; init; } = "";
    public string Term { get; init; } = "";
    public string? PreferredForm { get; init; }
    public string? Meaning { get; init; }
    public string Category { get; init; } = "";
    public string? Decision { get; init; }
    public Dictionary<string, string> Columns { get; init; } = [];
    public int Line { get; init; }
    /// <summary>Term, or a structural row of the glossary (convention, pinned-name group, known inconsistency, source). Snapshots analyzed before
    /// analyzer v2 carry Term for every row.</summary>
    public GlossaryRowKind Kind { get; init; } = GlossaryRowKind.Term;
}

/// <summary>The project's glossary evidence of one Source Analysis snapshot.</summary>
public sealed record ProjectGlossarySnapshot
{
    public Guid SourceSnapshotId { get; init; }
    /// <summary>The project the snapshot belongs to (archive / project name): terminology never exists without its project.</summary>
    public string ProjectName { get; init; } = "";
    public int AnalyzerVersion { get; init; }
    public DateTimeOffset AnalyzedAt { get; init; }
    public List<ProjectGlossaryArtifact> Glossaries { get; init; } = [];
    public List<ProjectGlossaryEntry> Entries { get; init; } = [];
    public List<string> Limitations { get; init; } = [];

    [JsonIgnore] public IEnumerable<ProjectGlossaryArtifact> Authoritative => Glossaries.Where(g => g.Authority == GlossaryAuthority.Authoritative);
    [JsonIgnore] public bool HasConflict => Glossaries.Any(g => g.Authority == GlossaryAuthority.Conflict);
}

/// <summary>A term resolved within ONE project's glossary (never across projects).</summary>
public sealed record ProjectTermResolution(ProjectGlossaryEntry Entry, ProjectGlossaryArtifact Glossary, string MatchedOn);

/// <summary>
/// The project-scoped terminology queries every consumer uses (Source Analysis, Document Quality, Documentation Drift, correlation): they read
/// one snapshot's glossary evidence only. Terminology equivalence is a terminology relation — it never merges implementation identities.
/// </summary>
public static class ProjectTerminology
{
    public const string NoGlossary = "No project glossary detected. BirkNext still analyzes the project; a glossary only enriches terminology checks.";

    /// <summary>Authoritative terms only (proposals and conflicts never resolve as approved terminology; structural rows are not terms).</summary>
    public static IReadOnlyList<ProjectTermResolution> Resolve(ProjectGlossarySnapshot? glossary, string text)
    {
        if (glossary is null || string.IsNullOrWhiteSpace(text)) return [];
        var key = Normalize(text);
        var authoritative = glossary.Authoritative.ToDictionary(g => g.GlossaryId, StringComparer.Ordinal);
        return [.. glossary.Entries.Where(e => authoritative.ContainsKey(e.GlossaryId) && e.Kind == GlossaryRowKind.Term)
            .Select(e => Normalize(e.Term) == key ? new ProjectTermResolution(e, authoritative[e.GlossaryId], "term")
                : Forms(e.PreferredForm).Any(f => Normalize(f) == key) ? new ProjectTermResolution(e, authoritative[e.GlossaryId], "preferred form") : null)
            .OfType<ProjectTermResolution>()];
    }

    /// <summary>The forms an entry names: its term and every preferred form ("`PrivatInstitusjon`, `StatligInstitusjon`" → two).</summary>
    public static IReadOnlyList<string> Aliases(ProjectGlossaryEntry entry) =>
        [.. new[] { entry.Term }.Concat(Forms(entry.PreferredForm)).Where(f => f.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)];

    public static IEnumerable<ProjectGlossaryEntry> Proposals(ProjectGlossarySnapshot? glossary) =>
        glossary is null ? [] : glossary.Entries.Where(e => glossary.Glossaries.Any(g => g.GlossaryId == e.GlossaryId && g.Authority == GlossaryAuthority.Proposed));

    public static IEnumerable<ProjectGlossaryEntry> TermsOf(ProjectGlossarySnapshot? glossary, GlossaryAuthority authority) =>
        glossary is null ? [] : glossary.Entries.Where(e => glossary.Glossaries.Any(g => g.GlossaryId == e.GlossaryId && g.Authority == authority));

    /// <summary>Terms of the authoritative glossary: Term rows only, never a convention, pinned-name group, known inconsistency or source row.
    /// A snapshot from analyzer v1 (every row Term) falls back to the source section for known inconsistencies.</summary>
    public static IEnumerable<ProjectGlossaryEntry> AuthoritativeTerms(ProjectGlossarySnapshot? glossary) =>
        TermsOf(glossary, GlossaryAuthority.Authoritative).Where(e => e.Kind == GlossaryRowKind.Term && !IsLegacyInconsistency(e));

    public static IEnumerable<ProjectGlossaryEntry> KnownInconsistencies(ProjectGlossarySnapshot? glossary) =>
        TermsOf(glossary, GlossaryAuthority.Authoritative).Where(e => e.Kind == GlossaryRowKind.KnownInconsistency || IsLegacyInconsistency(e));

    /// <summary>Structural rows of the authoritative glossary by kind (conventions, pinned-name groups, sources) — evidence, not terms.</summary>
    public static IReadOnlyList<(GlossaryRowKind Kind, int Count)> StructuralRows(ProjectGlossarySnapshot? glossary) =>
        [.. TermsOf(glossary, GlossaryAuthority.Authoritative).Where(e => e.Kind is GlossaryRowKind.NamingConvention or GlossaryRowKind.PinnedNames or GlossaryRowKind.Source)
            .GroupBy(e => e.Kind).OrderBy(g => g.Key).Select(g => (g.Key, g.Count()))];

    public static string RowKindLabel(GlossaryRowKind kind) => kind switch
    {
        GlossaryRowKind.NamingConvention => "naming convention",
        GlossaryRowKind.PinnedNames => "pinned-name group",
        GlossaryRowKind.KnownInconsistency => "known inconsistency",
        GlossaryRowKind.Source => "source",
        _ => "term",
    };

    private static bool IsLegacyInconsistency(ProjectGlossaryEntry e) =>
        e.Kind == GlossaryRowKind.Term && e.Category.Contains("inconsisten", StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<string> Forms(string? cell) =>
        string.IsNullOrWhiteSpace(cell) ? [] : [.. cell.Split([',', ';', '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Clean).Where(f => f.Length > 0 && f.Length <= 80)];

    /// <summary>Cell text without Markdown emphasis and code ticks.</summary>
    public static string Clean(string cell) => cell.Replace("`", "").Replace("**", "").Trim();

    private static string Normalize(string text) => Clean(text).ToLowerInvariant();

    public static string AuthorityLabel(GlossaryAuthority authority) => authority switch
    {
        GlossaryAuthority.Authoritative => "Authoritative glossary",
        GlossaryAuthority.Proposed => "Proposed glossary changes",
        GlossaryAuthority.Supplementary => "Supplementary glossary",
        GlossaryAuthority.Conflict => "Authority conflict · needs review",
        _ => "Glossary (authority unknown)",
    };
}
