namespace BirkNext.Web.Services;

/// <summary>
/// Product maturity of a whole feature — a different axis from configuration ("Not configured"), applicability ("N/A") and evidence ("Not
/// verified"). Unavailable = the capability cannot currently be relied on for normal use, whatever is configured.
/// </summary>
public enum FeatureAvailabilityState { Available, Preview, Unavailable }

/// <summary>
/// Status of one visible part of a feature, in the shared vocabulary: "Available", "Partial" (exists but incomplete), "Unavailable",
/// "Not configured" (setup exists, nothing configured) or "Not verified" (implemented, never verified end to end).
/// </summary>
public sealed record FeatureCapabilityStatus(string Key, string Label, string Status, string Detail);

/// <summary>A feature's availability, why, and the audit-backed status of each visible part (never roadmap items).</summary>
public sealed record FeatureAvailabilityInfo(string FeatureId, string Name, FeatureAvailabilityState State, string Summary, IReadOnlyList<FeatureCapabilityStatus> Capabilities)
{
    public bool IsUnavailable => State == FeatureAvailabilityState.Unavailable;
    public FeatureCapabilityStatus? Capability(string key) => Capabilities.FirstOrDefault(c => c.Key == key);
}

/// <summary>
/// The one source of truth for product availability, keyed by the navigation <see cref="NavItem.FeatureId"/>. Features absent here are
/// Available. Read by the page itself, the sidebar badge and Recommended Workflow (an unavailable capability is never recommended).
/// </summary>
public static class FeatureAvailabilityCatalog
{
    public const string AzureEnvironmentAnalysis = "environment-analysis";

    // Audit 2026-10-29: sign-in (MSAL, dedicated Edge profile / device code, in-memory tokens), read-only Resource Manager/Resource Graph
    // collection, snapshot storage and declared-vs-observed comparison are implemented and unit-tested against recorded responses, but need
    // an Entra app registration that does not exist and have never run against a live tenant. The page's runtime-quality and delivery
    // sections show other features' recorded evidence without any Azure correlation.
    private static readonly IReadOnlyDictionary<string, FeatureAvailabilityInfo> Features = new Dictionary<string, FeatureAvailabilityInfo>(StringComparer.Ordinal)
    {
        [AzureEnvironmentAnalysis] = new(AzureEnvironmentAnalysis, "Azure Environment Analysis", FeatureAvailabilityState.Unavailable,
            "Azure Environment Analysis is not currently available for normal use. The read-only sign-in, inventory and comparison code exists, but the complete analysis workflow has never been verified against a live Azure tenant. Configuration alone will not make it fully usable.",
            [
                new("connection", "Azure sign-in (read-only)", "Not verified",
                    "Implemented (dedicated Edge profile or device code; tokens kept in memory only). Needs an Entra app registration and has never been exercised against a live tenant."),
                new("inventory", "Resource inventory snapshot", "Not verified",
                    "Read-only Resource Manager / Resource Graph collection and snapshot storage are implemented and tested with recorded responses; they depend on sign-in and have not been verified live."),
                new("declared-observed", "Declared vs observed comparison", "Not verified",
                    "Compares source-declared infrastructure with a stored Azure snapshot; needs a snapshot, which needs sign-in."),
                new("runtime-quality", "Runtime quality evidence", "Partial",
                    "Shows recorded Frontend/API review evidence for this Target Environment. It is not correlated with Azure resources."),
                new("delivery", "Delivery path (pipeline source)", "Partial",
                    "Shows delivery evidence declared in source. Azure observations are not correlated with pipeline deployments."),
            ]),
    };

    public static FeatureAvailabilityInfo? Find(string? featureId) => featureId is not null && Features.TryGetValue(featureId, out var info) ? info : null;

    public static bool IsUnavailable(string? featureId) => Find(featureId)?.IsUnavailable == true;

    public static string Label(FeatureAvailabilityState state) => state switch
    {
        FeatureAvailabilityState.Preview => "Preview",
        FeatureAvailabilityState.Unavailable => "Unavailable",
        _ => "Available",
    };

    /// <summary>CSS tone (sd-pill suffix) of a capability status word: never colour alone — the word is always shown.</summary>
    public static string Tone(string status) => status switch
    {
        "Available" => "complete",
        "Partial" or "Not verified" => "partial",
        "Unavailable" => "attention",
        _ => "muted",
    };
}
