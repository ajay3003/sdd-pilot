using BirkNext.ProjectActivity;
using BirkNext.Web.Services.Operations;

namespace BirkNext.Web.Services.ProjectActivity;

/// <summary>A transient operation of this browser session (Source Analysis running, an import in progress) — never project history.</summary>
public sealed record CurrentOperationRow(Guid Id, string Title, string Stage, string Href);

/// <summary>
/// Dashboard wording for project activity: a badge that never implies QA success for an import, a short semantic icon (text, so state is never
/// colour-only), a readable timestamp, and the deep link. Pure.
/// </summary>
public static class ProjectActivityPresentation
{
    public const string EmptyTitle = "No recent activity yet.";
    public const string EmptyText = "Imports, completed reviews, configuration changes and other significant project activity will appear here.";

    public static string Badge(ProjectActivityStatus status) => status switch
    {
        ProjectActivityStatus.Imported => "Imported",
        ProjectActivityStatus.Attention => "Attention",
        ProjectActivityStatus.Failed => "Failed",
        ProjectActivityStatus.Configured => "Configured",
        ProjectActivityStatus.Generated => "Generated",
        _ => "Completed",
    };

    /// <summary>Dashboard pill tone: an import is neutral (imported ≠ passed), failures and attention stand out, completion is informational.</summary>
    public static string Tone(ProjectActivityStatus status) => status switch
    {
        ProjectActivityStatus.Failed => "attention",
        ProjectActivityStatus.Attention => "setup",
        _ => "neutral",
    };

    /// <summary>A small text glyph per category (decorative; the title and badge carry the meaning).</summary>
    public static string Icon(ProjectActivityCategory category) => category switch
    {
        ProjectActivityCategory.Import => "⇩",
        ProjectActivityCategory.Review => "◎",
        ProjectActivityCategory.Run => "▶",
        ProjectActivityCategory.Configuration => "⚙",
        _ => "▤",
    };

    /// <summary>"07:01" today, "1 Nov 07:01" earlier, in the viewer's local time.</summary>
    public static string When(DateTimeOffset at, DateTimeOffset now)
    {
        var local = at.ToLocalTime();
        return local.Date == now.ToLocalTime().Date
            ? local.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture)
            : local.ToString("d MMM HH:mm", System.Globalization.CultureInfo.InvariantCulture);
    }

    public static string LinkLabel(ProjectActivityItem item) => item.Category switch
    {
        ProjectActivityCategory.Import => "View import",
        ProjectActivityCategory.Run => "View run",
        ProjectActivityCategory.Review => "View result",
        _ => "Open",
    };

    /// <summary>Running session operations only (finished ones are not history: imports and runs record their own activity).</summary>
    public static IReadOnlyList<CurrentOperationRow> CurrentOperations(IEnumerable<OperationSnapshot> operations) =>
        [.. operations.Where(o => o.IsActive).OrderByDescending(o => o.StartedAt)
            .Select(o => new CurrentOperationRow(o.Id, o.Title, o.Status == OperationStatus.Queued ? "Queued" : string.IsNullOrWhiteSpace(o.Stage) ? "Running…" : o.Stage, o.DetailsRoute))];
}
