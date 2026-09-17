using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>Owns materialization and evidence revisions. Rendering never executes accessibility rules.</summary>
public static class BrowserQualityAssessmentService
{
    public static void SelectProfile(EndpointDiscoverySnapshot snapshot, WcagSettings settings)
    {
        snapshot.Wcag = new WcagSettings { ProfileId = WcagProfiles.Resolve(settings.ProfileId).ProfileId };
    }

    public static void RecordReview(EndpointDiscoverySnapshot snapshot, string? pageIdentity, WcagManualReview review)
    {
        var definition = WcagRegistry.For(snapshot.Wcag).SingleOrDefault(d => d.CriterionId == review.CriterionId)
            ?? throw new ArgumentException("Unknown criterion for this target.");
        if (review.Result is not (WcagStatus.Pass or WcagStatus.Fail or WcagStatus.NotApplicable))
            throw new ArgumentException("A manual decision must be Pass, Fail or NotApplicable.");
        var page = pageIdentity is null ? null : snapshot.Pages.SingleOrDefault(p => p.Identity == pageIdentity)
            ?? throw new ArgumentException("Page does not exist.");
        if (definition.RequiresCrossPageEvidence != (page is null)) throw new ArgumentException("Incorrect review scope.");
        // Do not silently attach an editor opened on a prior generation to a newer snapshot.
        if (review.Generation != (page?.AnalysisGeneration ?? 0) || review.Version != snapshot.Wcag.Version || review.AssessmentProfileId != snapshot.Wcag.ProfileId ||
            review.ScopeGeneration != (page is null ? WcagAssessmentEngine.ScopeGeneration(snapshot) : ""))
            throw new InvalidOperationException("Analysis changed. Reopen the review against the current generation.");
        var safe = review with
        {
            Comment = WcagReviewText.Validate(review.Comment, 1000),
            EvidenceNote = WcagReviewText.Validate(review.EvidenceNote, 1000),
            ReviewedBy = WcagReviewText.Validate(review.ReviewedBy, 100),
            ReviewedAt = DateTimeOffset.UtcNow,
        };
        if (string.IsNullOrWhiteSpace(safe.ReviewedBy) || string.IsNullOrWhiteSpace(safe.EvidenceNote))
            throw new ArgumentException("Reviewer and evidence note are required.");
        var reviews = page?.WcagReviews ?? snapshot.WcagApplicationReviews;
        if (reviews.Count >= 1000) throw new InvalidOperationException("Manual review history limit reached.");
        reviews.Add(safe);
    }

    public static WcagAssessment Assess(EndpointDiscoverySnapshot snapshot)
    {
        var revision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            Engine = 2, snapshot.Wcag.ProfileId, snapshot.WcagApplicationReviews,
            Pages = snapshot.Pages.Select(p => new { p.Identity, p.AnalysisGeneration, p.BrowserEvidence, p.WcagReviews })
        }))));
        if (snapshot.Quality.EvidenceRevision != revision || snapshot.Quality.Assessment?.Profile?.ProfileId != snapshot.Wcag.ProfileId)
        {
            snapshot.Quality.Assessment = WcagAssessmentEngine.Evaluate(snapshot);
            snapshot.Quality.EvidenceRevision = revision;
            snapshot.Quality.EvaluationCount++;
        }
        return snapshot.Quality.Assessment!;
    }

    public static WcagAssessment ForPage(WcagAssessment assessment, PageAnalysis? page) => page is null ? assessment : assessment with
    {
        Results = assessment.Results.Where(r => r.Page == page.Identity || r.Definition.RequiresCrossPageEvidence).ToList()
    };
}

public sealed record WcagCriterionSummary(WcagCriterionDefinition Definition, WcagStatus Status, IReadOnlyList<WcagCriterionResult> Instances)
{
    public string Scope => Definition.RequiresCrossPageEvidence ? "Application / process" : "Page";
    public int EvidenceCount => Instances.Count(r => r.LastTested is not null || r.ManualReview is not null && !r.ManualReviewStale);
}

/// <summary>Criterion-level presentation; explicit review outcomes and manual-only coverage remain separate.</summary>
public static class WcagAssessmentSummary
{
    public static IReadOnlyList<WcagCriterionSummary> Criteria(WcagAssessment assessment)
    {
        var definitions = assessment.Profile is { } profile
            ? WcagRegistry.All.Where(d => profile.CriterionIds.Contains(d.CriterionId))
            : assessment.Results.Select(r => r.Definition).DistinctBy(d => d.CriterionId);
        return definitions.OrderBy(d => Version.Parse(d.CriterionId)).Select(d =>
        {
            var instances = assessment.Results.Where(r => r.Definition.CriterionId == d.CriterionId).ToList();
            var status = instances.Any(r => r.Status == WcagStatus.Fail) ? WcagStatus.Fail
                : instances.Any(r => r.Status == WcagStatus.ManualReviewRequired) ? WcagStatus.ManualReviewRequired
                : instances.Count == 0 || instances.Any(r => r.Status == WcagStatus.NotTested) ? WcagStatus.NotTested
                : instances.All(r => r.Status == WcagStatus.NotApplicable) ? WcagStatus.NotApplicable : WcagStatus.Pass;
            return new WcagCriterionSummary(d, status, instances);
        }).ToList();
    }
    public static string State(WcagAssessment assessment) => assessment.Results.Any(r => r.Status == WcagStatus.ManualReviewRequired)
        ? "Manual review required" : assessment.Results.Any(r => r.LastTested is not null) ? "Partially assessed"
        : assessment.PagesWithEvidence > 0 ? "Automation available — no checks executed" : "Awaiting browser evidence";
}
