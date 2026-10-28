namespace BirkNext.Web.Services;

/// <summary>The one status vocabulary of Documentation Drift results. Colour always comes with this text.</summary>
public enum DriftStatus { Consistent, Difference, GeneratedOnly, AuthoredOnly, Conflict, ReviewCandidate, Unmatched }

/// <summary>Deterministic compatibility of two hand-picked documents for Advanced comparison. Never a block: Low/Unknown only warn.</summary>
public enum DriftCompatibility { High, Partial, Low, Unknown }

/// <summary>A result row in the shared vocabulary, grouped by the entity/section/flow/operation/type it belongs to.</summary>
public sealed record DriftResultRow(string Group, string Title, DriftStatus Status, string Detail, string? Authored, string? Generated, IReadOnlyList<string> Technical, string Kind = "");

/// <summary>
/// Pure presentation of Documentation Drift results and selectors: status vocabulary and tones (green consistent, amber difference/review,
/// red conflict, blue one-sided, grey unmatched), grouping, filtering and the Advanced comparison compatibility assessment. No AI, no scoring.
/// </summary>
public static class DocumentationDriftPresentation
{
    public static readonly IReadOnlyList<DriftStatus> Statuses = Enum.GetValues<DriftStatus>();

    public static string Label(DriftStatus status) => status switch
    {
        DriftStatus.GeneratedOnly => "Generated-only",
        DriftStatus.AuthoredOnly => "Authored-only",
        DriftStatus.ReviewCandidate => "Review candidate",
        _ => status.ToString(),
    };

    /// <summary>CSS tone suffix: dd-status-green / amber / red / blue / grey.</summary>
    public static string Tone(DriftStatus status) => status switch
    {
        DriftStatus.Consistent => "green",
        DriftStatus.Difference or DriftStatus.ReviewCandidate => "amber",
        DriftStatus.Conflict => "red",
        DriftStatus.GeneratedOnly or DriftStatus.AuthoredOnly => "blue",
        _ => "grey",
    };

    public static DriftStatus Status(ConsistencyCategory category) => category switch
    {
        ConsistencyCategory.Consistent => DriftStatus.Consistent,
        ConsistencyCategory.FieldDifference or ConsistencyCategory.ConstraintDifference or ConsistencyCategory.RelationshipDifference => DriftStatus.Difference,
        ConsistencyCategory.GeneratedOnly => DriftStatus.GeneratedOnly,
        ConsistencyCategory.AuthoredOnly => DriftStatus.AuthoredOnly,
        ConsistencyCategory.NotConcludedAuthoredConflict => DriftStatus.Conflict,
        _ => DriftStatus.Unmatched,
    };

    /// <summary>Text comparison kinds in the shared vocabulary; one-sided items follow the side's origin (authored/generated), else Unmatched.</summary>
    public static DriftStatus Status(DriftItemKind kind, DriftDocument? left, DriftDocument? right) => kind switch
    {
        DriftItemKind.Unchanged => DriftStatus.Consistent,
        DriftItemKind.Changed or DriftItemKind.ConsistencyDiscrepancy or DriftItemKind.Added or DriftItemKind.Removed => DriftStatus.Difference,
        DriftItemKind.StructureOnly => DriftStatus.ReviewCandidate,
        DriftItemKind.OnlyInLeft => OneSided(left),
        DriftItemKind.OnlyInRight => OneSided(right),
        _ => DriftStatus.Unmatched,
    };

    private static DriftStatus OneSided(DriftDocument? side) => side?.Origin switch
    {
        DriftOrigin.Generated => DriftStatus.GeneratedOnly,
        DriftOrigin.Authored => DriftStatus.AuthoredOnly,
        _ => DriftStatus.Unmatched,
    };

    /// <summary>Structured (Data Model) rows grouped by entity: "Order.Total" belongs to "Order".</summary>
    public static IReadOnlyList<DriftResultRow> Rows(DataModelConsistencyResult result) =>
        [.. result.Rows.Select(r => new DriftResultRow(EntityOf(r.Concept), r.Concept, Status(r.Category), r.Explanation, r.Authored, r.Generated,
            [.. r.AuthoredProvenance.Select(p => $"Authored: {p.Path} ({p.Section})").Distinct(), .. r.GeneratedPath is { } g ? new[] { $"Generated: {g}" } : []], r.Category.ToString()))];

    /// <summary>Text rows grouped by top-level section ("Overview › Flow" belongs to "Overview").</summary>
    public static IReadOnlyList<DriftResultRow> Rows(DriftComparisonResult result, DriftDocument? left, DriftDocument? right) =>
        [.. result.Items.Select(i =>
        {
            var kind = Enum.TryParse<DriftItemKind>(i.Kind, out var k) ? k : DriftItemKind.Changed;
            return new DriftResultRow(SectionOf(i.Section), i.Section, Status(kind, left, right), i.Detail,
                i.Left.Count > 0 ? string.Join(" · ", i.Left) : null, i.Right.Count > 0 ? string.Join(" · ", i.Right) : null, [], i.Kind);
        })];

    public static string EntityOf(string concept) => concept.Split('.', 2)[0];

    public static string SectionOf(string section)
    {
        foreach (var separator in new[] { " › ", " > ", " / " })
            if (section.IndexOf(separator, StringComparison.Ordinal) is > 0 and var at) return section[..at].Trim();
        return section.Trim().Length == 0 ? "Document" : section.Trim();
    }

    /// <summary>Status counts in vocabulary order (only non-zero).</summary>
    public static IReadOnlyList<(DriftStatus Status, int Count)> Counts(IEnumerable<DriftResultRow> rows) =>
        [.. rows.GroupBy(r => r.Status).OrderBy(g => g.Key).Select(g => (g.Key, g.Count()))];

    /// <summary>Filter (null = all) and case-insensitive search over group, title, detail and both sides.</summary>
    public static IReadOnlyList<DriftResultRow> Filter(IEnumerable<DriftResultRow> rows, DriftStatus? status, string? search) =>
        [.. rows.Where(r => (status is null || r.Status == status) && (string.IsNullOrWhiteSpace(search)
            || new[] { r.Group, r.Title, r.Detail, r.Authored, r.Generated }.Any(v => v?.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase) == true)))];

    /// <summary>
    /// Advanced comparison compatibility, deterministic: High = same document identity or same type and scope in one project; Partial = same
    /// document type; Low = different types; Unknown = a side without a known type/text. Scope equivalence is never assumed.
    /// </summary>
    public static (DriftCompatibility Level, string Reason) Compatibility(DriftDocument left, DriftDocument right)
    {
        if (!left.Supported || !right.Supported || !left.ContentAvailable || !right.ContentAvailable || string.IsNullOrWhiteSpace(left.TypeLabel) || string.IsNullOrWhiteSpace(right.TypeLabel))
            return (DriftCompatibility.Unknown, "The type or text of a selected document is not known, so compatibility cannot be assessed.");
        if (left.LogicalKey == right.LogicalKey)
            return (DriftCompatibility.High, "The same document identity.");
        var sameType = string.Equals(left.TypeLabel, right.TypeLabel, StringComparison.OrdinalIgnoreCase);
        if (sameType && string.Equals(left.Module ?? "", right.Module ?? "", StringComparison.OrdinalIgnoreCase) && left.LineageKey == right.LineageKey)
            return (DriftCompatibility.High, $"Both are {left.TypeLabel} documents of the same module and project.");
        if (sameType)
            return (DriftCompatibility.Partial, $"Both are {left.TypeLabel} documents, but module or project differs.");
        return (DriftCompatibility.Low, $"Different document types ({left.TypeLabel} ↔ {right.TypeLabel}); differences mostly reflect the type, not drift.");
    }

    public static string Label(DriftCompatibility c) => c.ToString();

    public static bool NeedsConfirmation(DriftCompatibility c) => c is DriftCompatibility.Low or DriftCompatibility.Unknown;

    /// <summary>Searchable combobox text for a document: "Type · path · import".</summary>
    public static string SearchText(DriftDocument d) => $"{d.TypeLabel} · {d.Path.Replace('\\', '/')} · {d.ProjectRevision ?? "manual"}";
}
