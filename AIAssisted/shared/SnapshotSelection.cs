using System.Globalization;
using System.Text.RegularExpressions;

namespace BirkNext.Snapshots;

// One selection model for snapshot pickers. Identity is the SnapshotId (the option value and the only lookup key); the label is presentation built from
// metadata; pointer/role names (Current, Latest, Approved, Baseline) are badges, never part of identity or of the stored name.

/// <summary>One option of a snapshot picker. <see cref="SnapshotId"/> is the option value; everything else is display metadata.</summary>
public sealed record SnapshotSelectionOption(
    string SnapshotId, string SnapshotType, DateTimeOffset? EffectiveAt, DateTimeOffset CreatedAt, string? SourceLabel, string? Summary,
    IReadOnlyList<string> Pointers, bool IsSelectable = true, string? DisabledReason = null);

public static partial class SnapshotSelectionLabels
{
    static readonly CultureInfo Display = CultureInfo.GetCultureInfo("en-GB");

    /// <summary>The one deterministic picker order: newest effective first, then newest stored/imported, then SnapshotId (never list order).</summary>
    public static IReadOnlyList<SnapshotSelectionOption> Sort(IEnumerable<SnapshotSelectionOption> options) =>
        [.. options.OrderByDescending(o => o.EffectiveAt ?? o.CreatedAt).ThenByDescending(o => o.CreatedAt).ThenBy(o => o.SnapshotId, StringComparer.Ordinal)];

    public const string SortExplanation = "Newest effective date first; same effective date: imported later first.";

    /// <summary>
    /// A unique, readable one-line label per option, in the given list. Base: effective date (with time when it is not midnight). When several options share
    /// that, the import time is added; then the source; the short SnapshotId suffix is only the last resort. A compact domain summary and the pointer badges
    /// follow (as text, so the label is understandable without colour or markup in a native select).
    /// </summary>
    public static IReadOnlyDictionary<string, string> Labels(IReadOnlyList<SnapshotSelectionOption> options, TimeZoneInfo zone)
    {
        string Effective(SnapshotSelectionOption o)
        {
            var at = TimeZoneInfo.ConvertTime(o.EffectiveAt ?? o.CreatedAt, zone);
            return at.TimeOfDay == TimeSpan.Zero ? at.ToString("d MMM yyyy", Display) : at.ToString("d MMM yyyy HH:mm", Display);
        }
        string Imported(SnapshotSelectionOption o) => $"imported {TimeZoneInfo.ConvertTime(o.CreatedAt, zone).ToString("d MMM HH:mm", Display)}";

        var keys = options.ToDictionary(o => o.SnapshotId, o => Effective(o), StringComparer.Ordinal);
        void Refine(Func<SnapshotSelectionOption, string?> part)
        {
            foreach (var group in options.GroupBy(o => keys[o.SnapshotId], StringComparer.Ordinal).Where(g => g.Count() > 1).ToList())
                foreach (var o in group)
                    if (part(o) is { Length: > 0 } extra) keys[o.SnapshotId] = $"{keys[o.SnapshotId]} · {extra}";
        }
        Refine(Imported);
        Refine(o => o.SourceLabel);
        Refine(o => $"…{ShortId(o.SnapshotId)}");

        return options.ToDictionary(o => o.SnapshotId, o =>
            string.Join(" · ", new[] { keys[o.SnapshotId], o.Summary, o.Pointers.Count == 0 ? null : string.Join(", ", o.Pointers),
                o.IsSelectable ? null : o.DisabledReason ?? "unavailable" }.Where(p => !string.IsNullOrWhiteSpace(p))), StringComparer.Ordinal);
    }

    public static string ShortId(string id)
    {
        var compact = id.Replace("-", "", StringComparison.Ordinal);
        return compact.Length <= 4 ? compact.ToUpperInvariant() : compact[^4..].ToUpperInvariant();
    }

    /// <summary>
    /// True when a stored name is only "{role} {yyyy-MM-dd}" — the form earlier imports generated (e.g. "Current 2026-11-04"). Such a name carries a role and a
    /// date, not a name, so presentation ignores it (the date and role are shown from metadata). Stored history is never rewritten.
    /// </summary>
    public static bool IsGeneratedRoleDateName(string? name) => name is not null && GeneratedRoleDateRe().IsMatch(name.Trim());

    [GeneratedRegex(@"^(Current|Baseline|Ad[\s-]?hoc|Gjeldende|Nåværende|Grunnlinje)\s+\d{4}-\d{2}-\d{2}$", RegexOptions.IgnoreCase)]
    private static partial Regex GeneratedRoleDateRe();

    /// <summary>Resolves a selection saved as a label (legacy): one exact match → its id; several → ambiguous (never the first); none → unresolved.</summary>
    public static (string? SnapshotId, string? Problem) ResolveLegacyLabel(string label, IEnumerable<(string SnapshotId, string Label)> candidates)
    {
        var matches = candidates.Where(c => string.Equals(c.Label, label, StringComparison.Ordinal)).Select(c => c.SnapshotId).Distinct().ToList();
        return matches.Count switch
        {
            1 => (matches[0], null),
            0 => (null, $"The saved selection \"{label}\" matches no stored snapshot. Select the intended snapshot."),
            _ => (null, $"{matches.Count} snapshots match the saved legacy label \"{label}\". Select the intended snapshot."),
        };
    }
}
