using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>
/// One row of the Frontend Review Engines settings surface: the saved activation, the coverage policy and the
/// runtime prerequisite the engine needs before it can execute.
/// </summary>
/// <param name="EngineId">Stable engine identity.</param>
/// <param name="DisplayName">User-facing engine name.</param>
/// <param name="Clarification">What the engine actually is, where the name alone is ambiguous.</param>
/// <param name="Enabled">Saved per-Target-Environment activation.</param>
/// <param name="Policy">Coverage policy: Required engines must be assessed for required coverage to complete.</param>
/// <param name="Requires">The runtime prerequisite. Enabled does not mean this prerequisite is satisfied.</param>
public sealed record FrontendReviewEngineRow(
    FrontendQualityEngineId EngineId,
    string DisplayName,
    string Clarification,
    bool Enabled,
    FrontendQualityEngineRequirement Policy,
    string Requires)
{
    /// <summary>A Required engine that is switched off can never be assessed, so required coverage stays incomplete.</summary>
    public bool RequiredButDisabled => Policy == FrontendQualityEngineRequirement.Required && !Enabled;
}

/// <summary>
/// Presentation for the Frontend Review Engines tab.
///
/// Scope is deliberate and narrow: these eight engines are consumed by the Frontend Quality Review orchestration path
/// only. API Quality Review and Integration Quality Review read none of them — API Quality Review derives its policy
/// from Performance Thresholds and the Environment Type and its targets from Endpoint Discovery, and Integration
/// Quality Review is driven by the per-integration <c>Enabled</c> flag under Integrations.
/// </summary>
public static class FrontendReviewEnginePresentation
{
    public const string ScopeNote =
        "These settings apply to Frontend Quality Review only. API Quality Review and Integration Quality Review use their own review configuration.";

    /// <summary>Help for the Saved state column: persisted per-target selection — never availability.</summary>
    public const string SavedStateHelp =
        "Controls whether the engine is selected for this target. Enabled is saved target configuration, not proof that the engine is available.";

    /// <summary>Help for the Coverage policy column. Required is about assessment, never about passing, and never locks the engine on.</summary>
    public const string CoveragePolicyHelp =
        "Required means the engine must be assessed for required coverage. It does not mean the engine must pass, and you can still switch it off. Optional engines may add coverage but are not needed to complete required coverage.";

    /// <summary>Help for the Capability column: this page owns no capability; it is evaluated by Frontend Quality Review.</summary>
    public const string CapabilityHelp =
        "Actual availability is checked by Frontend Quality Review at review time. This page runs no capability checks.";

    /// <summary>The Capability cell of every row: ownership, not a state. Live capability is never computed or cached here.</summary>
    public const string CapabilityCell = "Checked in FQR";

    /// <summary>What the restore action does: it resets only the Enabled/Disabled selection of this target (never coverage policy).</summary>
    public const string RestoreLabel = "Restore default engine selection";

    public const string RestoreConfirmation =
        "This restores the default Enabled/Disabled selection for this Target Environment. Coverage policy, other Target Environments, system capabilities, installed tools and Frontend Quality Review history are not changed.";

    /// <summary>Short per-row meaning of the coverage policy value.</summary>
    public static string PolicyHelp(FrontendQualityEngineRequirement policy) => policy == FrontendQualityEngineRequirement.Required
        ? "Must be assessed for required coverage. It does not have to pass."
        : "May contribute additional coverage but is not required for required coverage completion.";

    public static string RequiredButDisabledWarning(IEnumerable<FrontendReviewEngineRow> rows) =>
        $"Required engine(s) disabled — {string.Join(", ", rows.Where(r => r.RequiredButDisabled).Select(r => r.DisplayName))}. Required coverage cannot complete until they are enabled.";

    /// <summary>
    /// The runtime prerequisite each engine needs, derived from the same access requirements the orchestrator and
    /// readiness layers use. This is a static requirement, never a live availability claim.
    /// </summary>
    public static string Requires(FrontendQualityEngineId id) => id switch
    {
        FrontendQualityEngineId.StaticSecurity => "Public HTTP",
        FrontendQualityEngineId.PassivePerformance => "Public HTTP",
        FrontendQualityEngineId.PassiveSecurity => "Container runtime (ZAP)",
        FrontendQualityEngineId.Accessibility => "Browser DOM (Playwright + axe)",
        FrontendQualityEngineId.Lighthouse => "Node + Chrome",
        FrontendQualityEngineId.BrowserRuntime => "Browser DOM (Playwright)",
        FrontendQualityEngineId.BrowserQuality => "Browser Companion pairing",
        FrontendQualityEngineId.PerformanceQuality => "Browser Companion and/or Local HTTPS proxy evidence",
        _ => "—",
    };

    /// <summary>Display-only disambiguation. Never a persisted id and never a rename.</summary>
    public static string Clarification(FrontendQualityEngineId id) => id switch
    {
        FrontendQualityEngineId.StaticSecurity => "Anonymous HTTP security review",
        FrontendQualityEngineId.PassivePerformance => "Anonymous HTTP asset and WASM analysis",
        FrontendQualityEngineId.PassiveSecurity => "OWASP ZAP passive scan",
        FrontendQualityEngineId.Accessibility => "axe-core in a BirkNext browser",
        FrontendQualityEngineId.Lighthouse => "Google Lighthouse lab run",
        FrontendQualityEngineId.BrowserRuntime => "BirkNext Playwright browser",
        FrontendQualityEngineId.BrowserQuality => "Browser Companion evidence assessment",
        FrontendQualityEngineId.PerformanceQuality => "Browser and proxy performance assessment",
        _ => "",
    };

    /// <summary>Rows in a stable, policy-first order: Required engines first, then Optional, each alphabetically.</summary>
    public static IReadOnlyList<FrontendReviewEngineRow> Rows(
        FrontendAnalysisFeatureToggles toggles, FrontendQualityEngineRequirementSettings requirements)
    {
        var policy = requirements.ToPolicy();
        return Enum.GetValues<FrontendQualityEngineId>()
            .Select(id => new FrontendReviewEngineRow(
                id,
                FrontendQualityActiveEngines.DisplayName(id),
                Clarification(id),
                FrontendQualityActiveEngines.IsEnabled(id, toggles),
                policy.GetRequirement(id),
                Requires(id)))
            .OrderBy(r => r.Policy == FrontendQualityEngineRequirement.Required ? 0 : 1)
            .ThenBy(r => r.DisplayName, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Applies a toggle change for one engine to the draft. Mirrors <see cref="FrontendQualityActiveEngines.IsEnabled"/>.</summary>
    public static void SetEnabled(FrontendAnalysisFeatureToggles toggles, FrontendQualityEngineId id, bool enabled)
    {
        switch (id)
        {
            case FrontendQualityEngineId.StaticSecurity: toggles.EnableSecurityEngine = enabled; break;
            case FrontendQualityEngineId.PassivePerformance: toggles.EnablePerformanceEngine = enabled; break;
            case FrontendQualityEngineId.BrowserRuntime: toggles.EnableBrowserRuntimeEngine = enabled; break;
            case FrontendQualityEngineId.Accessibility: toggles.EnableAccessibilityEngine = enabled; break;
            case FrontendQualityEngineId.Lighthouse: toggles.EnableLighthouseEngine = enabled; break;
            case FrontendQualityEngineId.PassiveSecurity: toggles.EnablePassiveSecurityEngine = enabled; break;
            case FrontendQualityEngineId.BrowserQuality: toggles.EnableBrowserQualityEngine = enabled; break;
            case FrontendQualityEngineId.PerformanceQuality: toggles.EnablePerformanceQualityEngine = enabled; break;
        }
    }
}
