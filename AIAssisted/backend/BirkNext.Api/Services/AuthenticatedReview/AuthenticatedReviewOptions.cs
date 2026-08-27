namespace BirkNext.Api.Services.AuthenticatedReview;

public sealed class AuthenticatedReviewOptions
{
    public const string SectionName = "AuthenticatedReview";
    public bool Enabled { get; set; }
    public string Runtime { get; set; } = "Unsupported";
    public int AbsoluteLifetimeMinutes { get; set; } = 45;
    public int InactivityTimeoutMinutes { get; set; } = 15;

    internal TimeSpan AbsoluteLifetime => TimeSpan.FromMinutes(Math.Clamp(AbsoluteLifetimeMinutes, 10, 120));
    internal TimeSpan InactivityTimeout => TimeSpan.FromMinutes(Math.Clamp(InactivityTimeoutMinutes, 5, 60));
    internal bool IsLocalWorkstation => Enabled && string.Equals(Runtime, "LocalWorkstation", StringComparison.Ordinal);
}
