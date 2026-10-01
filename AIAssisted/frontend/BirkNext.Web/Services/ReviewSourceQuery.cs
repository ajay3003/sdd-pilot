namespace BirkNext.Web.Services;

/// <summary>The query-string form of a source scope for the review "source-scope" endpoints (ids only).</summary>
public static class ReviewSourceQuery
{
    public static string Of(ReviewSourceScopeRequest? scope) => scope is null ? ""
        : $"&primary={scope.PrimarySnapshotId}" + string.Concat(scope.RelatedSnapshotIds.Select(id => $"&related={id}"))
          + string.Concat(scope.ExcludedSuggestions.Select(e => $"&excluded={Uri.EscapeDataString(e)}"));
}
