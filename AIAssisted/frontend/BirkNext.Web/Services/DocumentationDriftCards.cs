using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>
/// The one primary state of a module + documentation-area card, in review-priority order (first = most urgent). Derived only from the existing
/// candidate-group state and the saved comparison — never a new classification. Absence on either side is neutral (never a difference).
/// </summary>
public enum DriftCardState { Conflict, Difference, ReviewRequired, ReadyToCompare, GeneratedMissing, AuthoredMissing, NotComparable, Consistent }

/// <summary>One Drift/Overview card: the candidate group, its primary state, a secondary line and the saved review facts.</summary>
public sealed record DriftCard(DriftCandidateGroup Group, DriftCardState State, string Secondary, int Differences, int Conflicts, DateTimeOffset? ReviewedAt,
    SddDocumentationDriftComparison? Saved)
{
    public string Module => Group.Module;
    public string Area => DocumentationDriftComposite.ConcernLabel(Group.Concern);
    public int AuthoredCount => Group.Contributors.Count;
    public string AuthoredText => AuthoredCount == 0 ? "Missing" : $"{AuthoredCount} doc{(AuthoredCount == 1 ? "" : "s")}";
    public string GeneratedText => Group.Generated is not null ? "Available" : Group.GeneratedCandidates.Count > 1 ? $"{Group.GeneratedCandidates.Count} candidates" : "Missing";
}

public static class DocumentationDriftCards
{
    public static DriftCard From(DriftCandidateGroup group, IReadOnlyList<SddDocumentationDriftComparison> saved)
    {
        var match = group.Generated is { } generated && group.Contributors.Count > 0 ? DocumentationDriftComposite.SavedFor(saved, group.Contributors, generated) : null;
        var differences = match?.Items.Count(i => i.Kind != nameof(ConsistencyCategory.Consistent)) ?? 0;
        // The composite comparison's own conflict category: authored statements that disagree and could not be concluded.
        var conflicts = match?.Items.Count(i => i.Kind == nameof(ConsistencyCategory.NotConcludedAuthoredConflict)) ?? 0;
        var (state, secondary) = group.State switch
        {
            DriftGroupState.ComparisonAvailable when match is null => (DriftCardState.ReadyToCompare, "Not yet reviewed"),
            DriftGroupState.ComparisonAvailable when conflicts > 0 => (DriftCardState.Conflict, $"{conflicts} authored conflict{(conflicts == 1 ? "" : "s")} · review saved"),
            DriftGroupState.ComparisonAvailable when differences > 0 => (DriftCardState.Difference, $"{differences} concept{(differences == 1 ? "" : "s")} differ · review saved"),
            DriftGroupState.ComparisonAvailable => (DriftCardState.Consistent, $"Reviewed {match!.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm}"),
            DriftGroupState.MultipleGeneratedCandidates => (DriftCardState.ReviewRequired, $"{group.GeneratedCandidates.Count} generated candidates — choose one"),
            DriftGroupState.GeneratedNotPresent => (DriftCardState.GeneratedMissing, "Not comparable: generated documentation missing"),
            DriftGroupState.AuthoredNotPresent => (DriftCardState.AuthoredMissing, "Not comparable: authored intent missing"),
            _ => (DriftCardState.NotComparable, "Structured comparison is not supported for this area"),
        };
        return new DriftCard(group, state, secondary, differences, conflicts, match?.CreatedAt, match);
    }

    public static IReadOnlyList<DriftCard> Cards(IEnumerable<DriftCandidateGroup> groups, IReadOnlyList<SddDocumentationDriftComparison> saved) =>
        Sort([.. groups.Select(g => From(g, saved))], DriftCardSort.Priority);

    public static string Label(DriftCardState state) => state switch
    {
        DriftCardState.ReviewRequired => "Review required",
        DriftCardState.ReadyToCompare => "Ready to compare",
        DriftCardState.GeneratedMissing => "Generated missing",
        DriftCardState.AuthoredMissing => "Authored missing",
        DriftCardState.NotComparable => "Not comparable",
        _ => state.ToString(),
    };

    /// <summary>Semantic tone (CSS suffix): green consistent, blue ready, amber difference/review, red conflict, grey absence/not comparable.</summary>
    public static string Tone(DriftCardState state) => state switch
    {
        DriftCardState.Consistent => "consistent",
        DriftCardState.ReadyToCompare => "ready",
        DriftCardState.Difference or DriftCardState.ReviewRequired => "attention",
        DriftCardState.Conflict => "conflict",
        _ => "neutral",
    };

    public static IReadOnlyList<DriftCard> Sort(IEnumerable<DriftCard> cards, DriftCardSort sort) => sort switch
    {
        DriftCardSort.Module => [.. cards.OrderBy(c => c.Module, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Area, StringComparer.OrdinalIgnoreCase)],
        DriftCardSort.Area => [.. cards.OrderBy(c => c.Area, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Module, StringComparer.OrdinalIgnoreCase)],
        _ => [.. cards.OrderBy(c => (int)c.State).ThenBy(c => c.Module, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Area, StringComparer.OrdinalIgnoreCase)],
    };

    /// <summary>Module, area, authored and generated document names/paths, case-insensitive.</summary>
    public static IReadOnlyList<DriftCard> Filter(IEnumerable<DriftCard> cards, DriftCardState? state, string? search) => [.. cards.Where(c =>
        (state is null || c.State == state) && (string.IsNullOrWhiteSpace(search) || SearchText(c).Contains(search.Trim(), StringComparison.OrdinalIgnoreCase)))];

    private static string SearchText(DriftCard c) => string.Join(' ', new[] { c.Module, c.Area, c.Group.Generated?.DisplayName, c.Group.Generated?.Path }
        .Concat(c.Group.Contributors.SelectMany(d => new[] { d.DisplayName, d.Path })).Where(t => !string.IsNullOrEmpty(t)));

    /// <summary>Where a missing side is resolved: Generated Documentation Health for the import, or the Explorer of the authored area.</summary>
    public static (string Label, string Href)? ResolveAction(DriftCard card) => card.State switch
    {
        DriftCardState.GeneratedMissing => ("Open Generated Documentation Health", $"{NavigationCatalog.GeneratedDocumentationHealthRoute}?import={Uri.EscapeDataString(card.Group.ImportId)}"),
        DriftCardState.AuthoredMissing => card.Group.Concern switch
        {
            DriftConcern.DataModel => ("Open Data Model Explorer", "data-model-explorer"),
            DriftConcern.Specification => ("Open Specification Explorer", "specification-explorer"),
            DriftConcern.Plan or DriftConcern.Architecture => ("Open Plan Explorer", "plan-explorer"),
            DriftConcern.Tasks => ("Open Task Explorer", "task-explorer"),
            DriftConcern.Constitution => ("Open Constitution Explorer", "constitution-explorer"),
            _ => ("Open Generated Documentation Explorer", $"{NavigationCatalog.GeneratedDocumentationExplorerRoute}?import={Uri.EscapeDataString(card.Group.ImportId)}"),
        },
        _ => null,
    };
}

public enum DriftCardSort { Priority, Module, Area }
