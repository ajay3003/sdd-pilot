using BirkNext.RuntimeAuthentication;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>
/// Frontend Quality Review before a run, as guided steps: target &amp; scope → authentication → quality checks → readiness → run → findings.
/// Derived from the page's existing landing state (readiness, capability rows, scope, resolved runtime authentication, run gating) — no second
/// readiness model. A configured mode that is not used says why; authenticated API traffic is never authenticated page content (DOM); execution
/// completed ≠ review complete ≠ coverage complete ≠ manual review complete.
/// </summary>
public static class FrontendQualityWorkflow
{
    public static string ScopeLabel(FrontendReviewAccessScope scope) => scope switch
    {
        FrontendReviewAccessScope.PublicOnly => "Public only",
        FrontendReviewAccessScope.AuthenticatedOnly => "Authenticated only",
        _ => "Public + authenticated",
    };

    /// <summary>Why the selected scope includes signed-in pages but no engine will sign in (null when they agree).</summary>
    public static string? ScopeAuthenticationMismatch(FrontendReviewAccessScope scope, RuntimeAuthenticationResolution? authentication) =>
        scope != FrontendReviewAccessScope.PublicOnly && authentication is { Effective: RuntimeAuthenticationMode.None } a
            ? (a.Requested == RuntimeAuthenticationMode.Auto ? $"Auto chose None ({a.Reason.TrimEnd('.')})." : "Authentication None is selected.")
              + $" The review scope is {ScopeLabel(scope)}, but with None no engine signs in, so only public pages are reviewed in this run; signed-in pages are reported as not assessed, never passed."
              + " Select Attended to review signed-in pages. Authenticated API traffic captured by the proxy is not signed-in page content."
            : null;

    public static IReadOnlyList<GuidedStep> Derive(string? targetUrl, FrontendReviewAccessScope scope, WcagAssessmentProfile? wcag, RuntimeAuthenticationResolution? authentication,
        IReadOnlyList<FrontendQualityCapabilityRow> capabilities, FrontendQualityReviewReadiness readiness, bool evidenceLoaded, string? runDisabledReason)
    {
        var summary = new FrontendQualityPreRunEngineSummary(capabilities);
        var steps = new List<GuidedStep>
        {
            string.IsNullOrWhiteSpace(targetUrl)
                ? new("target", "Select target and scope", GuidedStepState.Current, "No Frontend URL", "Save a Frontend URL for the active Target Environment.")
                : new("target", "Select target and scope", GuidedStepState.Done, ScopeLabel(scope),
                    $"{targetUrl} · {ScopeLabel(scope)}{(wcag is null ? "" : $" · WCAG profile {wcag.DisplayName} ({wcag.CriterionIds.Count} criteria)")}. The scope is never widened for you.",
                    Evidence: evidenceLoaded ? null : "Loading browser evidence (pages, live session, DOM)…"),
        };

        var mismatch = ScopeAuthenticationMismatch(scope, authentication);
        steps.Add(scope == FrontendReviewAccessScope.PublicOnly
            ? new("authentication", "Configure authentication", GuidedStepState.Done, "Not needed", "Public only: no engine signs in.")
            : authentication is null
                ? new("authentication", "Configure authentication", GuidedStepState.Waiting, "Resolving", "The effective authentication mode is being resolved.")
                : mismatch is not null
                    ? new("authentication", "Configure authentication", GuidedStepState.Current, "Signed-in pages not reviewed", mismatch)
                    : new("authentication", "Configure authentication", GuidedStepState.Done, authentication.Effective.ToString(),
                        "Signed-in pages are reviewed only where a signed-in browser session gives the engines the page content; authenticated API traffic alone is not signed-in page content."));

        var required = summary.RequiredLimitations;
        var optional = summary.OptionalLimitations;
        steps.Add(required.Count > 0
            ? new("checks", "Select quality checks", GuidedStepState.Current, $"{required.Count} required check{(required.Count == 1 ? "" : "s")} unavailable",
                $"Unavailable: {string.Join(", ", required.Select(r => r.DisplayName))}. A required check that cannot run is never shown as passed. Engine details are under Review details.")
            : new("checks", "Select quality checks", GuidedStepState.Done, optional.Count > 0 ? $"{optional.Count} optional unavailable" : "Checks available",
                optional.Count > 0 ? $"Not available this run, reported as limitations and never as passed: {string.Join(", ", optional.Select(r => r.DisplayName))}." : "Every check this review needs can run."));

        steps.Add(readiness.Level switch
        {
            FrontendQualityReviewReadinessLevel.Ready => new("readiness", "Check readiness", GuidedStepState.Done, readiness.Title, readiness.Message),
            FrontendQualityReviewReadinessLevel.Limited => new("readiness", "Check readiness", GuidedStepState.Done, "Ready with limitations", readiness.Message),
            FrontendQualityReviewReadinessLevel.Blocked => new("readiness", "Check readiness", GuidedStepState.Blocked, readiness.Title, readiness.Message),
            _ => new("readiness", "Check readiness", GuidedStepState.Waiting, "Assessing", "Readiness is assessed once the Target Environment and browser evidence are loaded."),
        });

        steps.Add(readiness.CanRun && runDisabledReason is null
            ? new("run", "Run frontend review", GuidedStepState.Current, "Ready to start",
                "Runs only when you select Run below. Execution completed is not review complete, coverage complete or manual review complete.")
            : new("run", "Run frontend review", GuidedStepState.Waiting, "Not available",
                "Runs only when you select Run below. Execution completed is not review complete, coverage complete or manual review complete.", DisabledReason: runDisabledReason));

        steps.Add(new("findings", "Review findings and actions", GuidedStepState.Waiting, "After a run",
            "Logical issues (one per problem) with the source findings behind them, coverage, WCAG criteria needing manual assessment, and history. No findings is not a pass, and automated scans do not establish WCAG conformance."));

        var first = steps.FindIndex(s => s.State == GuidedStepState.Current);
        return [.. steps.Select((s, i) => i > first && first >= 0 && s.State == GuidedStepState.Current ? s with { State = GuidedStepState.Waiting } : s)];
    }
}
