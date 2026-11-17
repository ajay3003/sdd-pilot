using System.Text.Json;
using BirkNext.ApiReview;
using BirkNext.RuntimeSecurity;
using BirkNext.TechnicalHistory;
using BirkNext.Web.Models;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Web.Services;

/// <summary>
/// Stores finished specialist reviews (Frontend Quality Review, safe fuzzing, authorization checks) on the server, so their history survives a
/// browser change, belongs to the selected project and can be reopened from Technical Testing. The browser copy is kept as before and remains the
/// fallback; a failed server save never fails the review. The server refuses a report that would store a credential.
/// </summary>
public static class TechnicalReviewArchive
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static StoreTechnicalReviewRunRequest FrontendQuality(string? projectKey, string profileId, string? profileName, FrontendQualityReviewReport report)
    {
        var entry = FrontendQualityReviewHistory.Entry(profileId, report);
        var disposition = report.ReleaseDisposition?.ToString() ?? (report.Completeness is null ? "Completed" : report.Completeness.ToString()!);
        return new(TechnicalReviewKinds.FrontendQuality, projectKey, profileId, profileName, report.TargetUrl, null, entry.CompletedAt, disposition,
            entry.RequiredTotal, entry.IssueCount, $"{entry.IssueCount} issue(s) · {entry.RequiredAssessed} of {entry.RequiredTotal} required checks assessed",
            JsonSerializer.SerializeToElement(report, Json));
    }

    public static StoreTechnicalReviewRunRequest Fuzzing(string? projectKey, ApiFuzzingReport run) =>
        new(TechnicalReviewKinds.SafeFuzzing, projectKey, run.EnvironmentId, run.EnvironmentName, null, null, run.CompletedAt ?? run.StartedAt, run.Completeness.ToString(),
            run.CasesExecuted, run.Findings.Count, $"{run.CasesExecuted} of {run.CasesPlanned} cases executed · {run.Findings.Count} finding(s)", JsonSerializer.SerializeToElement(run, Json));

    public static StoreTechnicalReviewRunRequest Authorization(string? projectKey, AuthorizationRunReport report)
    {
        var unexpected = report.Observations.Count(o => o.Outcome is AuthorizationOutcome.UnexpectedAllow or AuthorizationOutcome.UnexpectedDeny);
        return new(TechnicalReviewKinds.Authorization, projectKey, report.EnvironmentId, report.EnvironmentName, null, null, report.CompletedAt == default ? report.StartedAt : report.CompletedAt,
            report.BlockedReason is null ? (unexpected > 0 ? "Unexpected access" : "Finished") : "Not run", report.Observations.Count, unexpected,
            report.BlockedReason is { } blocked ? $"Not run: {blocked}" : $"{report.Observations.Count} identity result(s) · {unexpected} unexpected", JsonSerializer.SerializeToElement(report, Json));
    }

    /// <summary>Stores a review for the selected project (best effort). Returns the stored run, or null when the server is unavailable or refused it.</summary>
    public static async Task<StoredTechnicalReviewRun?> StoreAsync(IServiceProvider services, Func<string?, StoreTechnicalReviewRunRequest> build)
    {
        if (services.GetService<ITechnicalHistoryApiService>() is not { } api) return null;
        string? projectKey = null;
        if (services.GetService<ICurrentWorkspaceProjection>() is { } projection)
            try { projectKey = (await projection.GetAsync()).ProjectSlug; } catch (InvalidOperationException) { }
        try
        {
            var (run, _) = await api.StoreReviewRunAsync(build(projectKey));
            return run;
        }
        catch (Exception e) when (e is JsonException or NotSupportedException) { return null; }
    }
}
