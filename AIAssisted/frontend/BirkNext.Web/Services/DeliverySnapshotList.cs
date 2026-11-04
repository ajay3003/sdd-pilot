using BirkNext.DeliveryReporting;

namespace BirkNext.Web.Services;

/// <summary>
/// Delivery Reports → Snapshots list: search (name, file, repository, branch, commit), role filter and sort. The effective date (what the source represents)
/// and the import date stay distinct; "latest" is the newest effective date — the snapshot a new comparison would treat as current by date.
/// </summary>
public static class DeliverySnapshotList
{
    public const string ByEffective = "effective", ByImported = "imported", ByName = "name";

    public static DeliverySnapshotSummary? Latest(IEnumerable<DeliverySnapshotSummary> snapshots) => DeliveryReportingPresentation.Sorted(snapshots).FirstOrDefault();

    public static IReadOnlyList<DeliverySnapshotSummary> Apply(IEnumerable<DeliverySnapshotSummary> snapshots, string search, DeliverySnapshotRole? role, string sort)
    {
        var q = search.Trim();
        var matching = snapshots.Where(s => (role is null || s.Role == role) && (q.Length == 0 || Matches(s, q)));
        return sort switch
        {
            ByImported => [.. matching.OrderByDescending(s => s.ImportedAt).ThenBy(s => s.Id)],
            ByName => [.. matching.OrderBy(DeliveryReportingPresentation.DisplayName, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Id)],
            _ => DeliveryReportingPresentation.Sorted(matching),
        };
    }

    private static bool Matches(DeliverySnapshotSummary s, string q) =>
        new[] { s.Label, DeliveryReportingPresentation.DisplayName(s), s.SourceFileName, s.GitCommit, s.AzureDevOps?.Repository, s.AzureDevOps?.Branch, s.AzureDevOps?.CommitId }
            .Any(v => v is { Length: > 0 } && v.Contains(q, StringComparison.OrdinalIgnoreCase));
}
