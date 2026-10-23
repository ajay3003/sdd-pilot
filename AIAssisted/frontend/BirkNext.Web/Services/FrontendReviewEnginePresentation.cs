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

    /// <summary>The page intro: the engines belong to BirkNext's review system; the target only chooses which of them it is eligible for.</summary>
    public const string Intro =
        "Review engines are part of BirkNext's review system, not of the target. This tab only decides which engines Frontend Quality Review may use for this Target Environment. It checks no availability, runs nothing and contacts no target.";

    /// <summary>One line: how many engines are enabled, how coverage is split, and where availability is decided.</summary>
    public static string Summary(IReadOnlyCollection<FrontendReviewEngineRow> rows) =>
        $"{rows.Count(r => r.Enabled)} enabled · {rows.Count(r => r.Policy == FrontendQualityEngineRequirement.Required)} required · " +
        $"{rows.Count(r => r.Policy == FrontendQualityEngineRequirement.Optional)} optional · availability checked at review time";

    /// <summary>Help for the Saved state column: persisted per-target selection — never availability.</summary>
    public const string SavedStateHelp =
        "Enabled means eligible for Frontend Quality Review on this target. It does not mean the engine is available, will run, or is required.";

    /// <summary>Help for the Coverage policy column. Required is about assessment, never about passing, and never locks the engine on.</summary>
    public const string CoveragePolicyHelp =
        "Required means the engine must be assessed for required coverage. It does not mean the engine must pass, and you can still switch it off. Optional engines may add coverage but are not needed to complete required coverage.";

    /// <summary>Help for the Capability column: this page owns no capability; it is evaluated by Frontend Quality Review.</summary>
    public const string CapabilityHelp =
        "Frontend Quality Review checks availability at review time and is the authority on what runs. This page runs no capability checks.";

    /// <summary>The Capability cell of every row: ownership, not a state. Live capability is never computed or cached here.</summary>
    public const string CapabilityCell = "Checked at review time";

    /// <summary>What the restore action does: it resets only the Enabled/Disabled selection of this target (never coverage policy).</summary>
    public const string RestoreLabel = "Restore recommended defaults";

    public const string RestoreConfirmation =
        "This enables all eight engines and restores the recommended coverage (HTTP Security and HTTP Performance required, the rest optional) for this Target Environment only. Other Target Environments, system capabilities, installed tools and Frontend Quality Review history are not changed.";

    /// <summary>Short per-row meaning of the coverage policy value.</summary>
    public static string PolicyHelp(FrontendQualityEngineRequirement policy) => policy == FrontendQualityEngineRequirement.Required
        ? "Must be assessed for required coverage. It does not have to pass."
        : "May contribute additional coverage but is not required for required coverage completion.";

    public static string RequiredButDisabledWarning(IEnumerable<FrontendReviewEngineRow> rows) =>
        $"Required engine(s) disabled — {string.Join(", ", rows.Where(r => r.RequiredButDisabled).Select(r => r.DisplayName))}. Required coverage cannot complete until they are enabled.";

    /// <summary>
    /// The runtime prerequisite each engine needs, derived from the same access requirements the orchestrator and
    /// readiness layers use. This is a static requirement, never a live availability claim; the planner resolves the provider
    /// (Playwright, an authenticated browser, a live Browser Companion page, a Chromium/CDP target) at review time.
    /// </summary>
    public static string Requires(FrontendQualityEngineId id) => id switch
    {
        FrontendQualityEngineId.StaticSecurity => "Public HTTP",
        FrontendQualityEngineId.PassivePerformance => "Public HTTP",
        FrontendQualityEngineId.PassiveSecurity => "Container runtime (ZAP)",
        FrontendQualityEngineId.Accessibility => FrontendQualityExecutionPlanner.BrowserDomLabel + " (provider resolved at review time)",
        FrontendQualityEngineId.Lighthouse => FrontendQualityExecutionPlanner.LighthouseLabel,
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
        FrontendQualityEngineId.PassiveSecurity => "OWASP ZAP passive scan; enabling it never starts an active scan",
        FrontendQualityEngineId.Accessibility => "axe-core in a Browser DOM",
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

    /// <summary>
    /// Why Passive Web Security (ZAP) cannot run for this target according to the server's configuration layers (no readiness probe), or null
    /// when the server allows it. An unavailable engine is never presented as a usable switch: it can be switched off, not on.
    /// </summary>
    public static string? ServerUnavailableReason(FrontendQualityEngineId id, FrontendQualityEngineStatusDto? status) =>
        id != FrontendQualityEngineId.PassiveSecurity || status is null ? null
        : !status.Layer1Allowed ? "Installation policy does not allow container security engines."
        : !status.Layer2Enabled ? "Passive Web Security is disabled in System Settings."
        : PassiveWebSecurityUnavailable(status) is { } reason ? reason.Summary
        : null;

    public const string PassiveWebSecurityTrustOwner =
        "Passive Web Security trust is owned by the BirkNext server configuration (FrontendPassiveSecurity:TrustedProfiles), one registered origin per Target Environment; it is never extended to other hosts, subdomains or ports.";
    public const string ApiReviewSecurityIsNotZap =
        "API Quality Review's security checks are BirkNext's own read-only header and response checks; they do not use this engine, so their availability says nothing about it.";

    /// <summary>
    /// The one phrasing of why Passive Web Security (OWASP ZAP) cannot run for the target, from the server's typed reasons: not
    /// registered, registered for another origin, Production, or the provider itself unavailable. Null when none applies. Each case
    /// needs a different action, so they are never collapsed into one generic "unavailable".
    /// </summary>
    public static (string Summary, string Technical)? PassiveWebSecurityUnavailable(FrontendQualityEngineStatusDto status) =>
        status.Reasons.Contains(FrontendQualityEngineUnavailableReasonDto.ProductionBlocked)
            ? ("Passive Web Security is blocked for Production targets.", PassiveWebSecurityTrustOwner)
        : status.Reasons.Contains(FrontendQualityEngineUnavailableReasonDto.TargetOriginNotTrusted)
            ? ("Target not trusted for Passive Web Security: the BirkNext server registered this Target Environment for a different origin than this frontend URL.",
               $"{PassiveWebSecurityTrustOwner} {ApiReviewSecurityIsNotZap}")
        : status.Reasons.Contains(FrontendQualityEngineUnavailableReasonDto.TargetNotTrusted)
            ? ("Target not trusted for Passive Web Security: the BirkNext server has not registered this Target Environment for passive scanning.",
               $"{PassiveWebSecurityTrustOwner} Every scan of an unregistered target would be skipped. {ApiReviewSecurityIsNotZap}")
        : status.Layer3Readiness?.IsAvailable == false
            ? ($"Passive Web Security provider unavailable: {status.Layer3Readiness.StatusReason ?? "the OWASP ZAP runtime is not ready on the server."}",
               "The target trust check passed or was not reached; the provider (OWASP ZAP container runtime) is what is missing.")
        : null;

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
