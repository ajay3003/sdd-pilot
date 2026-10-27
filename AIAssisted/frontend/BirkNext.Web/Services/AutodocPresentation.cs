using BirkNext.GeneratedDocumentation;

namespace BirkNext.Web.Services;

/// <summary>Filters of the Autodoc inventory. Usage, freshness and health are separate facts; "Not configured" is neutral, never a problem.</summary>
public enum AutodocFilter { All, WithAutodoc, WithoutAutodoc, MaintainDocs, Partial, PotentiallyStale, UnknownFreshness, NeedsReview, ParseErrors }

/// <summary>Which modules the inventory lists: every module, API-capable modules only, or non-API modules.</summary>
public enum AutodocApiScope { All, Api, NonApi }

/// <summary>Pure presentation of Autodoc diagnostics (Generated Documentation Health): filter semantics and labels. Status is always text.</summary>
public static class AutodocPresentation
{
    public static readonly IReadOnlyList<AutodocFilter> Filters = Enum.GetValues<AutodocFilter>();

    public static string Label(AutodocFilter filter) => filter switch
    {
        AutodocFilter.All => "All modules",
        AutodocFilter.WithAutodoc => "With autodoc",
        AutodocFilter.WithoutAutodoc => "Without autodoc (not configured)",
        AutodocFilter.MaintainDocs => "maintain-docs detected",
        AutodocFilter.Partial => "Partial",
        AutodocFilter.PotentiallyStale => "Potentially stale",
        AutodocFilter.UnknownFreshness => "Unknown freshness",
        AutodocFilter.NeedsReview => "Review suggested",
        _ => "Parse/errors",
    };

    public static string Label(AutodocApiScope scope) => scope switch { AutodocApiScope.Api => "API modules only", AutodocApiScope.NonApi => "Non-API modules", _ => "All module types" };

    public static bool Matches(AutodocModuleInventory module, AutodocFilter filter, AutodocApiScope scope) =>
        (scope == AutodocApiScope.All || (scope == AutodocApiScope.Api) == module.ApiCapable) && filter switch
        {
            AutodocFilter.All => true,
            AutodocFilter.WithAutodoc => module.Usage != AutodocUsage.NotConfigured,
            AutodocFilter.WithoutAutodoc => module.Usage == AutodocUsage.NotConfigured,
            AutodocFilter.MaintainDocs => module.Generator?.Contains("maintain-docs", StringComparison.OrdinalIgnoreCase) == true
                || module.WorkflowSources.Any(w => w.Contains("maintain-docs", StringComparison.OrdinalIgnoreCase)),
            AutodocFilter.Partial => module.Usage == AutodocUsage.Partial,
            AutodocFilter.PotentiallyStale => module.Freshness == GeneratedDocumentationFreshnessStatus.Stale,
            AutodocFilter.UnknownFreshness => module.Usage != AutodocUsage.NotConfigured
                && module.Freshness is GeneratedDocumentationFreshnessStatus.Unknown or GeneratedDocumentationFreshnessStatus.NotEnoughEvidence,
            AutodocFilter.NeedsReview => module.Health == GeneratedDocumentationHealthStatus.Warning,
            _ => module.ParseErrors > 0 || module.Usage == AutodocUsage.Error,
        };

    public static int Count(AutodocDiagnostics diagnostics, AutodocFilter filter, AutodocApiScope scope) => diagnostics.Modules.Count(m => Matches(m, filter, scope));

    /// <summary>Not configured → "Not applicable" freshness and a neutral "—" health: absence of autodoc is never stale, missing or unhealthy.</summary>
    public static string Freshness(AutodocModuleInventory module) => module.Usage == AutodocUsage.NotConfigured ? "Not applicable"
        : module.Freshness is GeneratedDocumentationFreshnessStatus.Unknown or GeneratedDocumentationFreshnessStatus.NotEnoughEvidence ? "Not enough evidence"
        : GeneratedDocumentationText.Label(module.Freshness);

    public static string Health(AutodocModuleInventory module) => module.Health is { } health ? GeneratedDocumentationText.Label(health) : "Neutral";

    public static string UsageTone(AutodocUsage usage) => usage switch
    {
        AutodocUsage.Configured => "complete",
        AutodocUsage.Partial => "partial",
        AutodocUsage.Error => "attention",
        _ => "muted",
    };
}
