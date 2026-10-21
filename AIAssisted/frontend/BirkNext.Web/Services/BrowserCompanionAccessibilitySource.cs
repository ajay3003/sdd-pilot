using BirkNext.BrowserCompanion;
using BirkNext.Standards;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>Live Browser Companion state for one review: the live session and the approved origins of the Target Environment.</summary>
public sealed record BrowserCompanionLiveState(BrowserCompanionLiveSession Live, IReadOnlyList<string> ApprovedOrigins);

/// <summary>An Accessibility assessment taken from the live Browser Companion page, or the exact reason there is none.</summary>
public sealed record BrowserCompanionAccessibilityResult(AccessibilityResultDto? Result, string Reason, int RecordedPagesWithAxe);

/// <summary>
/// The Browser Companion as a Browser DOM provider for the Accessibility engine. The Companion's content script runs the bundled axe-core
/// in every approved live page on each visit and DOM change; this source turns the axe result of a page that is LIVE NOW (captured after
/// that page registered in the current live session) into an Accessibility result. Recorded evidence of pages that are no longer open is
/// counted, never presented as a fresh run. Read-only: nothing is sent to the page and no permission is used beyond the existing pairing.
/// </summary>
public interface IBrowserCompanionAccessibilitySource
{
    Task<BrowserCompanionLiveState> LiveAsync(FrontendAnalysisContext context, CancellationToken cancellationToken = default);
    Task<BrowserCompanionAccessibilityResult> CollectLiveAsync(FrontendAnalysisContext context, CancellationToken cancellationToken = default);
}

public sealed class BrowserCompanionAccessibilitySource(BrowserCompanionRuntime companion) : IBrowserCompanionAccessibilitySource
{
    public async Task<BrowserCompanionLiveState> LiveAsync(FrontendAnalysisContext context, CancellationToken cancellationToken = default)
    {
        var status = await StatusAsync(context);
        return new(status.EffectiveLive, BrowserCompanionScope.ApprovedOrigins(context.ActiveProfile));
    }

    public async Task<BrowserCompanionAccessibilityResult> CollectLiveAsync(FrontendAnalysisContext context, CancellationToken cancellationToken = default) =>
        BrowserCompanionAccessibility.FromLive(await StatusAsync(context), BrowserCompanionScope.ApprovedOrigins(context.ActiveProfile), context.TargetUrl);

    private async Task<BrowserCompanionStatus> StatusAsync(FrontendAnalysisContext context)
    {
        try
        {
            await companion.FollowAsync(context.ActiveProfile);
            await companion.RefreshAsync();
            return companion.For(context.ActiveProfile.Id);
        }
        catch (Exception) { return new BrowserCompanionStatus(); }
    }
}

/// <summary>Pure conversion of live Companion axe evidence into the Accessibility engine's result shape. Fully unit-testable.</summary>
public static class BrowserCompanionAccessibility
{
    public const string EngineName = "Accessibility (axe-core in the Browser Companion page)";
    public const string AccessLabel = "Browser Companion live page (axe)";
    public const string NotConnected = "Browser Companion is not connected, so it has no live DOM to assess.";
    public const string NoLivePage = "Browser Companion is connected, but no approved page of this Target Environment is open.";
    public const string NoLiveAxe = "The open page has no axe result from this live session yet. Reload or interact with the page in the paired browser, then run the review again.";

    public static BrowserCompanionAccessibilityResult FromLive(BrowserCompanionStatus status, IReadOnlyCollection<string> approvedOrigins, string targetUrl)
    {
        var live = status.EffectiveLive;
        var recorded = status.Pages.Count(p => p.Accessibility?.Axe?.State == "Completed");
        if (!live.ExtensionConnected) return new(null, NotConnected, recorded);
        var livePages = live.LivePages.Where(p => approvedOrigins.Contains(p.Origin, StringComparer.OrdinalIgnoreCase)).ToList();
        if (livePages.Count == 0) return new(null, NoLivePage, recorded);

        // Evidence counts as live only for a page that is open now and was captured after it registered (this content-script instance).
        var current = livePages
            .Select(page => status.Pages
                .Where(e => string.Equals(e.PageOrigin, page.Origin, StringComparison.OrdinalIgnoreCase) && e.PagePath == page.Route
                            && e.CapturedAt >= page.RegisteredAt && e.Accessibility?.Axe is { State: "Completed" })
                .OrderByDescending(e => e.CapturedAt).FirstOrDefault())
            .OfType<BrowserPageEvidence>().ToList();
        if (current.Count == 0) return new(null, NoLiveAxe, recorded);

        var rules = current.SelectMany(e => e.Accessibility!.Axe!.Rules.Select(rule => (Page: e, Rule: rule))).ToList();
        var failing = rules.Where(r => r.Rule.Outcome is "Fail" or "ManualReviewRequired")
            .GroupBy(r => (r.Rule.RuleId, r.Rule.Outcome))
            .Select(group =>
            {
                var rule = group.First().Rule;
                var kind = rule.Outcome == "Fail" ? AccessibilityFindingKindDto.Violation : AccessibilityFindingKindDto.NeedsManualReview;
                var impact = group.Select(r => r.Rule.Impact).FirstOrDefault(i => i is not null);
                var nodes = group.Sum(r => r.Rule.Count);
                var pages = string.Join(", ", group.Select(r => r.Page.PagePath).Distinct());
                return new AccessibilityFindingDto(
                    rule.RuleId, kind,
                    kind == AccessibilityFindingKindDto.Violation ? Severity(impact) : FrontendQualitySeverity.Info,
                    impact,
                    $"axe rule {rule.RuleId}",
                    kind == AccessibilityFindingKindDto.Violation
                        ? $"axe-core reported this rule as failing on {nodes} element{(nodes == 1 ? "" : "s")} in the live page ({pages})."
                        : $"axe-core could not decide this rule automatically on {nodes} element{(nodes == 1 ? "" : "s")} in the live page ({pages}); a person must check it.",
                    [.. rule.CriterionIds.Select(id => "wcag" + id.Replace(".", ""))], nodes, [], [], [],
                    group.First().Page.Accessibility!.Axe!.Version is { Length: > 0 } version
                        ? $"https://dequeuniversity.com/rules/axe/{string.Join('.', version.Split('.').Take(2))}/{rule.RuleId}" : null,
                    "Review the affected elements against the axe rule guidance and the referenced WCAG criteria.");
            }).ToList();

        var first = current[0];
        return new(new AccessibilityResultDto(
            ExecutionStatus: AccessibilityExecutionStatusDto.Assessed,
            EngineName: EngineName,
            AxeVersion: first.Accessibility!.Axe!.Version,
            BrowserName: first.BrowserName,
            RequestedUrl: targetUrl,
            FinalUrl: $"{first.PageOrigin}{first.PagePath}",
            StartedAt: current.Min(e => e.CapturedAt).UtcDateTime,
            CompletedAt: current.Max(e => e.CapturedAt).UtcDateTime,
            ViolationCount: failing.Count(f => f.Kind == AccessibilityFindingKindDto.Violation),
            IncompleteCount: failing.Count(f => f.Kind == AccessibilityFindingKindDto.NeedsManualReview),
            PassCount: rules.Count(r => r.Rule.Outcome == "Pass"),
            InapplicableCount: rules.Count(r => r.Rule.Outcome == "NotApplicable"),
            Findings: failing,
            Limitations:
            [
                $"axe-core ran inside {current.Count} live page{(current.Count == 1 ? "" : "s")} of the paired browser ({string.Join(", ", current.Select(e => e.PagePath).Distinct())}) through the Browser Companion; other pages were not assessed.",
                FrontendQualityExecutionPlanner.CompanionPageNote,
                "No element HTML or text leaves the browser: findings carry rule ids, outcomes, counts and WCAG references only.",
            ],
            ExecutionMode: AccessibilityExecutionModeDto.BrowserCompanionLivePage), "", recorded);
    }

    /// <summary>axe impact → BirkNext severity; a failing rule without a reported impact is Medium (older extensions report none).</summary>
    public static FrontendQualitySeverity Severity(string? impact) => impact switch
    {
        "critical" => FrontendQualitySeverity.Critical,
        "serious" => FrontendQualitySeverity.High,
        "minor" => FrontendQualitySeverity.Low,
        _ => FrontendQualitySeverity.Medium,
    };
}
