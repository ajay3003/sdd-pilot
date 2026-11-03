using BirkNext.ApiReview;
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
    string Environment,
    string? ProfileId = null,
    string? EnvironmentFingerprint = null);

internal interface IRuntimeReviewScoped
{
    string? ProfileId { get; }
    string? EnvironmentFingerprint { get; }
    void Clear();
}

public sealed class RuntimeReviewSessionState<TReport> : IRuntimeReviewScoped
{
    public RuntimeReviewStatus Status { get; private set; } = RuntimeReviewStatus.NotRun;
    public TReport? Report { get; private set; }
    public string? TargetUrl { get; private set; }
    public string? ProfileName { get; private set; }
    /// <summary>The Target Environment (profile id) the result belongs to; a result is never shown as current for another environment.</summary>
    public string? ProfileId { get; private set; }
    /// <summary>The environment's target and authentication when the result was produced (see <see cref="RuntimeReviewSessionService.Fingerprint"/>).</summary>
    public string? EnvironmentFingerprint { get; private set; }
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
        ProfileId = context.ProfileId;
        EnvironmentFingerprint = context.EnvironmentFingerprint;
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
        ProfileId = context.ProfileId;
        EnvironmentFingerprint = context.EnvironmentFingerprint;
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
        ProfileId = context.ProfileId;
        EnvironmentFingerprint = context.EnvironmentFingerprint;
        Environment = context.Environment;
        RunTimestamp = DateTimeOffset.UtcNow;
        ErrorMessage = errorMessage;
    }

    public void Clear()
    {
        Status = RuntimeReviewStatus.NotRun;
        Report = default;
        TargetUrl = null;
        ProfileName = null;
        ProfileId = null;
        EnvironmentFingerprint = null;
        Environment = null;
        RunTimestamp = null;
        ErrorMessage = null;
    }
}

/// <summary>
/// This browser session's latest runtime review results — per review, and only for the active Target Environment: switching the active
/// environment clears results of the previous one (each review's own history keeps them per environment). A run that was started before a
/// switch still shows its own result, labelled with the environment it ran against (its ProfileId/ProfileName), never relabelled.
/// </summary>
public sealed class RuntimeReviewSessionService : IDisposable
{
    private readonly IFrontendAnalysisSettingsService? _settings;

    public RuntimeReviewSessionService() { }

    public RuntimeReviewSessionService(IFrontendAnalysisSettingsService settings)
    {
        _settings = settings;
        _settings.Changed += OnSettingsChanged;
    }

    public void Dispose() { if (_settings is not null) _settings.Changed -= OnSettingsChanged; }

    private void OnSettingsChanged() => ScopeTo(_settings?.ActiveProfile?.Id, _settings?.ActiveProfile is { } p ? Fingerprint(p) : null);

    /// <summary>
    /// Clears every result that belongs to another Target Environment than <paramref name="activeProfileId"/>, or to the same environment before its
    /// target or authentication changed (<paramref name="activeFingerprint"/>): a result is never current for a target it did not review.
    /// </summary>
    public void ScopeTo(string? activeProfileId, string? activeFingerprint = null)
    {
        foreach (var state in new IRuntimeReviewScoped[] { SecurityReview, PerformanceReview, QualityReview, ApiQualityReview, IntegrationQualityReview })
            if (state.ProfileId is { } id && (id != activeProfileId
                || activeFingerprint is not null && state.EnvironmentFingerprint is { } fp && fp != activeFingerprint)) state.Clear();
    }

    /// <summary>
    /// What a runtime result depends on in its Target Environment's configuration: the frontend, API and GraphQL targets and the configured API auth
    /// type. The effective (observed) browser authentication is deliberately left out — the context factory applies it to its copy, so it would
    /// differ from the stored profile without any configuration change.
    /// </summary>
    public static string Fingerprint(FrontendAnalysisProfile p) =>
        string.Join("|", p.TargetUrl?.Trim(), p.RestBaseUrl?.Trim(), p.GraphQlEndpoint?.Trim(), p.ApiAuth?.AuthType.ToString());

    public RuntimeReviewSessionState<WasmSecurityReviewReport>     SecurityReview    { get; } = new();
    public RuntimeReviewSessionState<WasmPerformanceReviewReport>  PerformanceReview { get; } = new();
    public RuntimeReviewSessionState<FrontendQualityReviewReport>  QualityReview     { get; } = new();
    public RuntimeReviewSessionState<ApiReviewReport>       ApiQualityReview  { get; } = new();
    public RuntimeReviewSessionState<BirkNext.Integrations.IntegrationReviewResult> IntegrationQualityReview { get; } = new();

    public void MarkSecurityRunning(FrontendAnalysisContext context) =>
        SecurityReview.MarkRunning(CreateSnapshot(context));

    public void SaveSecurityResult(WasmSecurityReviewReport report, FrontendAnalysisContext context) =>
        Save(SecurityReview, report, CreateSnapshot(context), ToOffset(report.ScannedAt));

    public void MarkSecurityFailed(FrontendAnalysisContext context, string errorMessage) =>
        SecurityReview.Fail(CreateSnapshot(context), errorMessage);

    public void ClearSecurityResult() => SecurityReview.Clear();

    public void MarkPerformanceRunning(FrontendAnalysisContext context) =>
        PerformanceReview.MarkRunning(CreateSnapshot(context));

    public void SavePerformanceResult(WasmPerformanceReviewReport report, FrontendAnalysisContext context) =>
        Save(PerformanceReview, report, CreateSnapshot(context), ToOffset(report.ReviewedAt));

    public void MarkPerformanceFailed(FrontendAnalysisContext context, string errorMessage) =>
        PerformanceReview.Fail(CreateSnapshot(context), errorMessage);

    public void ClearPerformanceResult() => PerformanceReview.Clear();

    public void MarkQualityRunning(FrontendAnalysisContext context) =>
        QualityReview.MarkRunning(CreateSnapshot(context));

    public void SaveQualityResult(FrontendQualityReviewReport report, FrontendAnalysisContext context) =>
        Save(QualityReview, report, CreateSnapshot(context), ToOffset(report.GeneratedAt));

    public void MarkQualityFailed(FrontendAnalysisContext context, string errorMessage) =>
        QualityReview.Fail(CreateSnapshot(context), errorMessage);

    public void ClearQualityResult() => QualityReview.Clear();

    public void MarkApiQualityRunning(FrontendAnalysisContext context) =>
        ApiQualityReview.MarkRunning(CreateApiQualitySnapshot(context));

    public void SaveApiQualityResult(ApiReviewReport report, FrontendAnalysisContext context) =>
        Save(ApiQualityReview, report, CreateApiQualitySnapshot(context), report.GeneratedAt);

    public void MarkApiQualityFailed(FrontendAnalysisContext context, string errorMessage) =>
        ApiQualityReview.Fail(CreateApiQualitySnapshot(context), errorMessage);

    public void ClearApiQualityResult() => ApiQualityReview.Clear();

    public void MarkIntegrationQualityRunning(FrontendAnalysisContext context) =>
        IntegrationQualityReview.MarkRunning(CreateIntegrationQualitySnapshot(context));

    public void SaveIntegrationQualityResult(BirkNext.Integrations.IntegrationReviewResult result, FrontendAnalysisContext context) =>
        Save(IntegrationQualityReview, result, new RuntimeReviewContextSnapshot($"{result.TopicsReviewed} topics", context.ActiveProfile.Name, context.ActiveProfile.EnvironmentType.ToString(), context.ActiveProfile.Id, Fingerprint(context.ActiveProfile)), result.CompletedAt);

    private void Save<TReport>(RuntimeReviewSessionState<TReport> state, TReport report, RuntimeReviewContextSnapshot context, DateTimeOffset at)
    {
        state.Complete(report, context, at);
    }

    public void MarkIntegrationQualityFailed(FrontendAnalysisContext context, string errorMessage) =>
        IntegrationQualityReview.Fail(CreateIntegrationQualitySnapshot(context), errorMessage);

    public void ClearIntegrationQualityResult() => IntegrationQualityReview.Clear();

    /// <summary>
    /// Clear all runtime review session state.
    /// Called by ApplicationRuntimeResetService after backend database reset.
    /// </summary>
    public void ClearAll()
    {
        SecurityReview.Clear();
        PerformanceReview.Clear();
        QualityReview.Clear();
        ApiQualityReview.Clear();
        IntegrationQualityReview.Clear();
    }

    private static RuntimeReviewContextSnapshot CreateSnapshot(FrontendAnalysisContext context) =>
        new(
            context.TargetUrl,
            context.ActiveProfile.Name,
            context.ActiveProfile.EnvironmentType.ToString(),
            context.ActiveProfile.Id,
            Fingerprint(context.ActiveProfile));

    private static RuntimeReviewContextSnapshot CreateApiQualitySnapshot(FrontendAnalysisContext context) =>
        new(
            context.RestBaseUrl ?? context.TargetUrl,
            context.ActiveProfile.Name,
            context.ActiveProfile.EnvironmentType.ToString(),
            context.ActiveProfile.Id,
            Fingerprint(context.ActiveProfile));

    private static RuntimeReviewContextSnapshot CreateIntegrationQualitySnapshot(FrontendAnalysisContext context) =>
        new("Configured integrations", context.ActiveProfile.Name, context.ActiveProfile.EnvironmentType.ToString(), context.ActiveProfile.Id, Fingerprint(context.ActiveProfile));

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
