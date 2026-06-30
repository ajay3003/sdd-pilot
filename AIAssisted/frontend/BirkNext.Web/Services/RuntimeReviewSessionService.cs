using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

public enum RuntimeReviewStatus
{
    NotRun,
    Running,
    Completed,
    Failed
}

public sealed record RuntimeReviewContextSnapshot(
    string TargetUrl,
    string ProfileName,
    string Environment);

public sealed class RuntimeReviewSessionState<TReport>
{
    public RuntimeReviewStatus Status { get; private set; } = RuntimeReviewStatus.NotRun;
    public TReport? Report { get; private set; }
    public string? TargetUrl { get; private set; }
    public string? ProfileName { get; private set; }
    public string? Environment { get; private set; }
    public DateTimeOffset? RunTimestamp { get; private set; }
    public string? ErrorMessage { get; private set; }

    public bool HasResult => Report is not null;

    internal void MarkRunning(RuntimeReviewContextSnapshot context)
    {
        Status = RuntimeReviewStatus.Running;
        Report = default;
        TargetUrl = context.TargetUrl;
        ProfileName = context.ProfileName;
        Environment = context.Environment;
        RunTimestamp = DateTimeOffset.UtcNow;
        ErrorMessage = null;
    }

    internal void Complete(TReport report, RuntimeReviewContextSnapshot context, DateTimeOffset runTimestamp)
    {
        Status = RuntimeReviewStatus.Completed;
        Report = report;
        TargetUrl = context.TargetUrl;
        ProfileName = context.ProfileName;
        Environment = context.Environment;
        RunTimestamp = runTimestamp;
        ErrorMessage = null;
    }

    internal void Fail(RuntimeReviewContextSnapshot context, string errorMessage)
    {
        Status = RuntimeReviewStatus.Failed;
        Report = default;
        TargetUrl = context.TargetUrl;
        ProfileName = context.ProfileName;
        Environment = context.Environment;
        RunTimestamp = DateTimeOffset.UtcNow;
        ErrorMessage = errorMessage;
    }

    internal void Clear()
    {
        Status = RuntimeReviewStatus.NotRun;
        Report = default;
        TargetUrl = null;
        ProfileName = null;
        Environment = null;
        RunTimestamp = null;
        ErrorMessage = null;
    }
}

public sealed class RuntimeReviewSessionService
{
    public RuntimeReviewSessionState<WasmSecurityReviewReport> SecurityReview { get; } = new();
    public RuntimeReviewSessionState<WasmPerformanceReviewReport> PerformanceReview { get; } = new();

    public void MarkSecurityRunning(FrontendAnalysisContext context) =>
        SecurityReview.MarkRunning(CreateSnapshot(context));

    public void SaveSecurityResult(WasmSecurityReviewReport report, FrontendAnalysisContext context) =>
        SecurityReview.Complete(report, CreateSnapshot(context), ToOffset(report.ScannedAt));

    public void MarkSecurityFailed(FrontendAnalysisContext context, string errorMessage) =>
        SecurityReview.Fail(CreateSnapshot(context), errorMessage);

    public void ClearSecurityResult() => SecurityReview.Clear();

    public void MarkPerformanceRunning(FrontendAnalysisContext context) =>
        PerformanceReview.MarkRunning(CreateSnapshot(context));

    public void SavePerformanceResult(WasmPerformanceReviewReport report, FrontendAnalysisContext context) =>
        PerformanceReview.Complete(report, CreateSnapshot(context), ToOffset(report.ReviewedAt));

    public void MarkPerformanceFailed(FrontendAnalysisContext context, string errorMessage) =>
        PerformanceReview.Fail(CreateSnapshot(context), errorMessage);

    public void ClearPerformanceResult() => PerformanceReview.Clear();

    private static RuntimeReviewContextSnapshot CreateSnapshot(FrontendAnalysisContext context) =>
        new(
            context.TargetUrl,
            context.ActiveProfile.Name,
            context.ActiveProfile.EnvironmentType.ToString());

    private static DateTimeOffset ToOffset(DateTime timestamp)
    {
        var utc = timestamp.Kind switch
        {
            DateTimeKind.Utc => timestamp,
            DateTimeKind.Local => timestamp.ToUniversalTime(),
            _ => DateTime.SpecifyKind(timestamp, DateTimeKind.Utc)
        };

        return new DateTimeOffset(utc);
    }
}
