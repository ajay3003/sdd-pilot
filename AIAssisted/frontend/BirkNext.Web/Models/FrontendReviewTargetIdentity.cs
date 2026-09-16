namespace BirkNext.Web.Models;

/// <summary>Non-secret identity captured at review start and retained for history and exports.</summary>
public sealed record FrontendReviewTargetIdentity(
    string EnvironmentId,
    string Name,
    string EnvironmentType,
    string TargetUrl,
    DateTime StartedAt);
