using BirkNext.ApiReview;
using BirkNext.LocalHttpsProxy;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>
/// GraphQL client/server compatibility, in the reader's terms. Three dimensions are kept apart everywhere this is shown:
/// runtime (the review never executes an observed business operation; <c>query { __typename }</c> is its own probe), contract
/// compatibility (does the observed document validate against the trusted schema?) and schema drift (did the schema change?).
/// "Not assessed" is never shown as "0 compatible", and the observed count never shrinks because nothing could be assessed.
/// </summary>
public static class ApiReviewGraphQlCompatibilityPresentation
{
    public static string StatusLabel(GraphQlCompatibilityStatus status) => status switch
    {
        GraphQlCompatibilityStatus.Compatible => "Compatible",
        GraphQlCompatibilityStatus.Incompatible => "Incompatible",
        _ => "Not assessed",
    };

    public static string StatusTone(GraphQlCompatibilityStatus? status) => status switch
    {
        GraphQlCompatibilityStatus.Compatible => "pass",
        GraphQlCompatibilityStatus.Incompatible => "fail",
        _ => "nottested",
    };

    public static string SchemaSourceLabel(GraphQlSchemaSource source) => source switch
    {
        GraphQlSchemaSource.RuntimeIntrospection => "Runtime GraphQL schema",
        GraphQlSchemaSource.ConfiguredArtifact => "Configured schema artifact",
        _ => "None",
    };

    /// <summary>The source as used by this run: a stored SDL artifact reads "Configured SDL", never "runtime schema".</summary>
    public static string SchemaSourceLabel(ApiReviewGraphQlCompatibility compatibility) =>
        compatibility.SchemaSource == GraphQlSchemaSource.ConfiguredArtifact && compatibility.ConfiguredArtifact is { UsedForCompatibility: true }
            ? "Configured SDL"
            : SchemaSourceLabel(compatibility.SchemaSource);

    /// <summary>"m2lb-schema.graphql · sha256 1a2b3c4d5e6f · updated 2026-09-25 13:10" — what this run had, from its own snapshot.</summary>
    public static string? ArtifactLabel(ApiReviewGraphQlCompatibility compatibility) => compatibility.ConfiguredArtifact is { } a
        ? $"{a.FileName} · sha256 {a.ShortHash} · updated {a.UpdatedAt.ToUniversalTime():yyyy-MM-dd HH:mm} UTC" + (a.UsedForCompatibility ? "" : " · available as fallback (not used)")
        : null;

    /// <summary>Runtime column: the review validates observed operations, it does not run them.</summary>
    public static string RuntimeLabel(GraphQlOperationType type) => type switch
    {
        GraphQlOperationType.Mutation => "Not executed — contract validation only",
        GraphQlOperationType.Subscription => "Not tested",
        _ => "Not executed (observed only)",
    };

    /// <summary>"5 of 6 observed operations compatible · 1 incompatible", or "Not assessed — schema unavailable".</summary>
    public static string Summary(ApiReviewGraphQlCompatibility? compatibility)
    {
        if (compatibility is null || compatibility.Observed == 0) return "No observed operations";
        var noun = $"{compatibility.Observed} observed operation{(compatibility.Observed == 1 ? "" : "s")}";
        if (compatibility.Assessed == 0)
            return compatibility.SchemaSource == GraphQlSchemaSource.None
                ? $"{noun} · compatibility not assessed — schema unavailable"
                : $"{noun} · compatibility not assessed — operation documents not captured";
        var parts = new List<string> { $"{compatibility.Compatible} of {compatibility.Observed} observed operation{(compatibility.Observed == 1 ? "" : "s")} compatible" };
        if (compatibility.Incompatible > 0) parts.Add($"{compatibility.Incompatible} incompatible");
        if (compatibility.NotAssessed > 0) parts.Add($"{compatibility.NotAssessed} not assessed");
        return string.Join(" · ", parts);
    }

    /// <summary>Contract coverage: how much of the observed inventory compatibility could look at.</summary>
    public static string Coverage(ApiReviewGraphQlCompatibility compatibility) =>
        compatibility.Observed == 0 ? "No observed operations"
        : compatibility.Assessed == compatibility.Observed ? "Complete"
        : compatibility.Assessed == 0 ? (compatibility.SchemaSource == GraphQlSchemaSource.None ? "Unavailable — no schema" : "Unavailable — no operation documents")
        : $"Partial — {compatibility.Assessed} of {compatibility.Observed} assessed";

    /// <summary>The Overview line across GraphQL services. Null when no service observed an operation.</summary>
    public static string? OverviewSummary(ApiReviewReport report)
    {
        var all = report.Targets.Where(t => t.Target.ApiType == ApiReviewTargetType.GraphQl).Select(t => t.GraphQlCompatibility).OfType<ApiReviewGraphQlCompatibility>().Where(c => c.Observed > 0).ToList();
        if (all.Count == 0) return null;
        var assessed = all.Sum(c => c.Assessed);
        if (assessed == 0) return all.All(c => c.SchemaSource == GraphQlSchemaSource.None) ? "Compatibility not assessed — schema unavailable" : "Compatibility not assessed";
        var compatible = all.Sum(c => c.Compatible);
        var incompatible = all.Sum(c => c.Incompatible);
        var notAssessed = all.Sum(c => c.NotAssessed);
        return $"Compatibility: {compatible} compatible · {incompatible} incompatible" + (notAssessed > 0 ? $" · {notAssessed} not assessed" : "");
    }

    public const string RecommendedAction = "Update the frontend operation to the current contract, or restore the server field if its removal or change was unintended. Regenerate generated GraphQL client code if applicable.";

    /// <summary>
    /// Pre-run: whether compatibility CAN be assessed for the selected GraphQL targets — never a result. Documents come from
    /// Endpoint Discovery; the schema from runtime introspection during the review, or a configured artifact.
    /// </summary>
    public static IReadOnlyList<ApiReviewReadinessItem> Readiness(IReadOnlyList<ApiReviewTarget> graphQlTargets, bool introspectionUnavailablePreviously,
        IReadOnlyDictionary<string, GraphQlSchemaArtifact>? artifacts = null)
    {
        var items = new List<ApiReviewReadinessItem>();
        foreach (var target in graphQlTargets)
        {
            var observed = target.Operations.Where(o => o.OperationType != GraphQlOperationType.None).ToList();
            if (observed.Count == 0) continue;
            var withDocument = observed.Count(o => !string.IsNullOrWhiteSpace(o.Document));
            var noun = $"{observed.Count} observed frontend operation{(observed.Count == 1 ? "" : "s")}";
            if (withDocument == 0)
            {
                items.Add(new($"{noun} · operation documents not captured yet — compatibility will not be assessed until Endpoint Discovery observes them again", ApiReviewReadinessItemState.Missing));
                continue;
            }
            var persisted = observed.Count(o => o.DocumentOmission == GraphQlDocumentOmission.PersistedQueryHashOnly);
            var oversize = observed.Count(o => o.DocumentOmission == GraphQlDocumentOmission.ExceededRetentionLimit);
            var documents = withDocument == observed.Count ? "" : $" ({withDocument} with a captured document"
                + (persisted > 0 ? $", {persisted} persisted-query hash only" : "") + (oversize > 0 ? $", {oversize} over the retained-size limit" : "") + ")";
            if (artifacts?.TryGetValue(target.TargetId, out var artifact) == true)
                items.Add(new($"{noun}{documents} · runtime introspection will be attempted first; configured SDL {artifact.FileName} is available as fallback — compatibility can be assessed", ApiReviewReadinessItemState.Ok));
            else if (!string.IsNullOrWhiteSpace(target.ContractSource))
                items.Add(new($"{noun}{documents} · configured schema artifact available — compatibility can be assessed", ApiReviewReadinessItemState.Ok));
            else if (introspectionUnavailablePreviously)
                items.Add(new($"{noun}{documents} · no configured schema artifact and the runtime schema was unavailable last time — compatibility is assessed only if introspection succeeds (add a trusted SDL under Review details → GraphQL schema artifacts)", ApiReviewReadinessItemState.Warning));
            else
                items.Add(new($"{noun}{documents} · runtime schema will be retrieved during the review for compatibility", ApiReviewReadinessItemState.Ok));
        }
        return items;
    }
}
