using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>Owns materialization and evidence revisions. Rendering never executes accessibility rules.</summary>
public static class BrowserQualityAssessmentService
{
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
