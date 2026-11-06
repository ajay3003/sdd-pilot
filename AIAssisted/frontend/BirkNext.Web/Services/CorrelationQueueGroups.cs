using BirkNext.Interpretation;

namespace BirkNext.Web.Services;

public sealed record CorrelationQueueGroup(string Key, string Label, IReadOnlyList<ReviewItemView> Items);

/// <summary>
/// Groups one page of the shared review queue for the Relationship Review list: by review type (identity conflicts first, as the queue orders
/// them), by source entity or by target entity, so related candidate relationships are reviewed together. Grouping never changes an item or
/// its order inside a group; the server's priority order is kept.
/// </summary>
public static class CorrelationQueueGroups
{
    public const string ByType = "type", BySource = "source", ByTarget = "target";

    public static IReadOnlyList<CorrelationQueueGroup> Group(IReadOnlyList<ReviewItemView> items, string by) =>
    [
        .. items.Select((item, index) => (item, index)).GroupBy(x => Key(x.item, by), StringComparer.Ordinal)
            .OrderBy(g => g.Min(x => x.index))
            .Select(g => new CorrelationQueueGroup(g.Key, Label(g.First().item, by), [.. g.Select(x => x.item)])),
    ];

    private static string Key(ReviewItemView item, string by) => by switch
    {
        BySource => item.Relation?.Source.Key ?? item.Subject?.Key ?? "(no source)",
        ByTarget => item.Relation?.Target.Key ?? item.Subject?.Key ?? "(no target)",
        _ => item.Type.ToString(),
    };

    private static string Label(ReviewItemView item, string by) => by switch
    {
        BySource => item.Relation?.Source.DisplayName ?? item.Subject?.DisplayName ?? "No source entity",
        ByTarget => item.Relation?.Target.DisplayName ?? item.Subject?.DisplayName ?? "No target entity",
        _ => CanonicalReviewQueue.Label(item.Type),
    };

    public static string TypeOption(ReviewItemType type, int count) => $"{CanonicalReviewQueue.Label(type)} ({count})";
}
