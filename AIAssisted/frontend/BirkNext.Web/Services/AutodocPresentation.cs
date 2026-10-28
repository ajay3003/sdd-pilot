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

    /// <summary>The filters shown by default; the rest sit behind "Advanced filters".</summary>
    public static readonly IReadOnlyList<AutodocFilter> BasicFilters = [AutodocFilter.All, AutodocFilter.WithAutodoc, AutodocFilter.WithoutAutodoc, AutodocFilter.NeedsReview];
    public static readonly IReadOnlyList<AutodocFilter> AdvancedFilters = [.. Filters.Except(BasicFilters)];

    /// <summary>The project policy statement shown when no module has generated documentation: neutral, never a failure.</summary>
    public const string NeutralExplanation = "No project policy requiring generated documentation was detected.";

    public static string Label(AutodocFilter filter) => filter switch
    {
        AutodocFilter.All => "All",
        AutodocFilter.WithAutodoc => "With generated docs",
        AutodocFilter.WithoutAutodoc => "Without",
        AutodocFilter.MaintainDocs => "Generator workflow (maintain-docs)",
        AutodocFilter.Partial => "Partial output",
        AutodocFilter.PotentiallyStale => "Potentially stale",
        AutodocFilter.UnknownFreshness => "Unknown freshness",
        AutodocFilter.NeedsReview => "Needs review",
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

    /// <summary>"Generated docs: Configured / Partial / Error / Not configured" — the column value; "Not configured" is neutral.</summary>
    public static string Usage(AutodocUsage usage) => GeneratedDocumentationText.Label(usage);

    /// <summary>A module is a review item when its health suggests review or its generated output does not parse.</summary>
    public static bool IsReviewItem(AutodocModuleInventory module) => module.Health == GeneratedDocumentationHealthStatus.Warning || module.ParseErrors > 0 || module.Usage == AutodocUsage.Error;

    public static IReadOnlyList<AutodocModuleInventory> ReviewItems(AutodocDiagnostics diagnostics) => [.. diagnostics.Modules.Where(IsReviewItem)];

    /// <summary>Overall generated-documentation state of the snapshot: Available, Partial or Not configured (neutral).</summary>
    public static string State(AutodocDiagnostics diagnostics) =>
        diagnostics.ModulesUsingAutodoc == 0 ? "Not configured"
        : diagnostics.Modules.Any(m => m.Usage == AutodocUsage.Partial) ? "Partial"
        : "Available";

    /// <summary>Overall health: Neutral (nothing configured), Error (parse failures), Review suggested, Partial, Available. Deterministic precedence.</summary>
    public static string OverallHealth(AutodocDiagnostics diagnostics) =>
        diagnostics.ModulesUsingAutodoc == 0 && diagnostics.ParseErrors == 0 ? "Neutral"
        : diagnostics.ParseErrors > 0 || diagnostics.Modules.Any(m => m.Usage == AutodocUsage.Error) ? "Error"
        : diagnostics.Modules.Any(m => m.Health == GeneratedDocumentationHealthStatus.Warning) ? "Review suggested"
        : diagnostics.Modules.Any(m => m.Usage == AutodocUsage.Partial) ? "Partial"
        : "Available";

    public static string OverallTone(string health) => health switch
    {
        "Available" => "complete",
        "Partial" => "partial",
        "Review suggested" or "Error" => "attention",
        _ => "muted",
    };

    public static string UsageTone(AutodocUsage usage) => usage switch
    {
        AutodocUsage.Configured => "complete",
        AutodocUsage.Partial => "partial",
        AutodocUsage.Error => "attention",
        _ => "muted",
    };
}
