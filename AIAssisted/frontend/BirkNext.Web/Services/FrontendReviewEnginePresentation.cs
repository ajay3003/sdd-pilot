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
        "These engines apply to Frontend Quality Review only. API Quality Review and Integration Quality Review are not affected by them.";

    public const string EnabledVersusAvailableNote =
        "Enabled is saved configuration, not a capability. An enabled engine still needs its prerequisite to be available; Frontend Quality Review reports the live capability status before a review runs.";

    public const string RequiredPolicyNote =
        "Required means the engine must be assessed for required coverage to complete. It does not mean the engine must pass, and it does not prevent you from switching the engine off.";

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
