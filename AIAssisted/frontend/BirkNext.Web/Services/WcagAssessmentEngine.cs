using BirkNext.BrowserCompanion;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

public static class WcagAssessmentEngine
{
    // Guidance (headings, landmarks, tabindex) is intentionally absent: it is not proof of failure.
    private static readonly HashSet<string> DeterministicFailures =
    ["a11y-image-alt", "a11y-image-name", "a11y-page-title", "a11y-document-lang",
     "a11y-button-name", "a11y-link-name", "a11y-control-label", "a11y-aria-reference",
     "a11y-dialog-name", "label-in-name", "text-contrast"];

    public static string ScopeGeneration(EndpointDiscoverySnapshot snapshot) => string.Join("|",
        snapshot.Pages.OrderBy(p => p.Identity, StringComparer.Ordinal)
            .Select(p => $"{p.Identity}:{p.AnalysisGeneration}"));

    public static WcagAssessment Evaluate(EndpointDiscoverySnapshot snapshot, PageAnalysis? selected = null)
    {
        var results = new List<WcagCriterionResult>();
        var definitions = WcagRegistry.For(snapshot.Wcag).ToList();
        foreach (var page in selected is null ? snapshot.Pages : new List<PageAnalysis> { selected })
            foreach (var definition in definitions.Where(d => !d.RequiresCrossPageEvidence))
                results.Add(EvaluatePage(snapshot, page, definition));
        foreach (var definition in definitions.Where(d => d.RequiresCrossPageEvidence))
            results.Add(EvaluateCrossPage(snapshot, definition));
        return new WcagAssessment { Version = snapshot.Wcag.Version, Level = snapshot.Wcag.Level, Results = results };
    }

    private static WcagCriterionResult EvaluatePage(EndpointDiscoverySnapshot snapshot, PageAnalysis page, WcagCriterionDefinition d)
    {
        var evidence = page.BrowserEvidence;
        var a = evidence?.Accessibility;
        var checks = a?.Checks.Where(c => d.SupportedChecks.Contains(c.CheckId)).ToList() ?? [];
        var failures = checks.Where(c => DeterministicFailures.Contains(c.CheckId)).Sum(c => c.Failed);
        // Older companions report only failures, so their empty list cannot prove execution or absence.
        if (checks.Count == 0)
            failures = a?.Findings.Where(f => d.SupportedChecks.Contains(f.RuleId) && DeterministicFailures.Contains(f.RuleId)).Sum(f => f.Count) ?? 0;
        var obsolete = d.CriterionId == "4.1.1" && snapshot.Wcag.Version == WcagVersion.Wcag22;
        var noMedia = a is { MediaScopeComplete: true, VideoCount: 0, AudioCount: 0 } &&
            d.CriterionId is "1.2.1" or "1.2.2" or "1.2.3" or "1.2.4" or "1.2.5";
        var noVideo = a is { MediaScopeComplete: true, VideoCount: 0 } &&
            d.CriterionId is "1.2.2" or "1.2.3" or "1.2.4" or "1.2.5";
        var tested = checks.Any(c => c.Outcome != "NotTested");
        var status = obsolete || noMedia || noVideo ? WcagStatus.NotApplicable
            : failures > 0 ? WcagStatus.Fail
            : a is null ? WcagStatus.NotTested
            : d.RequiresInteraction && !tested ? WcagStatus.NotTested
            : d.RequiresManualReview ? WcagStatus.ManualReviewRequired
            : checks.Count == d.SupportedChecks.Count && checks.All(c => c.Outcome == "Pass" && c.Uncertain == 0)
                ? WcagStatus.Pass : WcagStatus.NotTested;
        var result = new WcagCriterionResult
        {
            Definition = d, Page = page.Identity, Generation = page.AnalysisGeneration, Status = status,
            Checks = checks,
            Findings = failures, Confidence = failures > 0 ? WcagConfidence.High : tested ? WcagConfidence.Medium : null,
            EvidenceSource = a is null || d.AutomationLevel == WcagAutomation.Manual ? WcagEvidenceSource.Manual : WcagEvidenceSource.DOM,
            LastTested = tested || noMedia || noVideo || failures > 0 ? evidence?.CapturedAt : null,
            AutomatedEvidence = obsolete ? "Not applicable to WCAG 2.2 conformance target."
                : noMedia || noVideo ? "No relevant media detected in a complete native DOM media scope."
                : failures > 0 ? $"{failures} deterministic failure(s) in tested elements."
                : !tested ? "Not tested by automation. Human review is required for unobserved behavior."
                : $"No automated failure detected in tested elements. {checks.Sum(c => c.Uncertain)} observation(s) require confirmation; semantic and unobserved behavior still require review.",
        };
        return ApplyManual(result, page.WcagReviews, snapshot.Wcag.Version, "", obsolete);
    }

    private static WcagCriterionResult EvaluateCrossPage(EndpointDiscoverySnapshot snapshot, WcagCriterionDefinition d)
    {
        var pages = snapshot.Pages.Where(p => p.BrowserEvidence?.Accessibility?.Checks.Any(c => c.CheckId == "navigation-structure") == true).ToList();
        var compared = pages.Count >= 2;
        var structures = pages.Select(p => d.CriterionId == "3.2.4"
            ? p.BrowserEvidence!.Accessibility!.ComponentStructure : p.BrowserEvidence!.Accessibility!.NavigationStructure);
        var different = compared && structures.Select(s => string.Join(",", s)).Distinct().Count() > 1;
        return ApplyManual(new WcagCriterionResult
        {
            Definition = d, Page = "Application", Status = compared ? WcagStatus.ManualReviewRequired : WcagStatus.NotTested,
            EvidenceSource = WcagEvidenceSource.CrossPage, Confidence = compared ? WcagConfidence.Low : null,
            LastTested = compared ? pages.Max(p => p.BrowserEvidence!.CapturedAt) : null,
            AutomatedEvidence = !compared ? "At least two fresh page analyses are required."
                : $"Compared structural counts across {pages.Count} pages. {(different ? "Differences require review." : "No structural difference detected.")} Names, order, equivalent functions and complete navigation paths require human review.",
        }, snapshot.WcagApplicationReviews, snapshot.Wcag.Version, ScopeGeneration(snapshot));
    }

    private static WcagCriterionResult ApplyManual(WcagCriterionResult result, IEnumerable<WcagManualReview> reviews,
        WcagVersion version, string scope, bool obsolete = false)
    {
        var review = reviews.Where(r => r.CriterionId == result.Definition.CriterionId).OrderByDescending(r => r.ReviewedAt).FirstOrDefault();
        if (review is null) return result;
        var stale = review.Generation != result.Generation || review.Version != version || review.ScopeGeneration != scope;
        // A human approval cannot hide a current deterministic failure. Obsolete criteria stay N/A.
        return result with
        {
            ManualReview = review, ManualReviewStale = stale,
            Status = stale || obsolete || result.Status == WcagStatus.Fail ? result.Status : review.Result,
            EvidenceSource = stale || obsolete || result.Status == WcagStatus.Fail ? result.EvidenceSource : WcagEvidenceSource.Manual,
        };
    }
}
