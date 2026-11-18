namespace BirkNext.Api.Services.AuthenticatedReview;

public sealed class AuthenticatedReviewOptions
{
    public const string SectionName = "AuthenticatedReview";
    public bool Enabled { get; set; }
    public string Runtime { get; set; } = "Unsupported";
    public int AbsoluteLifetimeMinutes { get; set; } = 45;
    public int InactivityTimeoutMinutes { get; set; } = 15;
    public bool AllowSyntheticHttpOrigins { get; set; }

    internal TimeSpan AbsoluteLifetime => TimeSpan.FromMinutes(Math.Clamp(AbsoluteLifetimeMinutes, 10, 120));
    internal TimeSpan InactivityTimeout => TimeSpan.FromMinutes(Math.Clamp(InactivityTimeoutMinutes, 5, 60));

    /// <summary>True when Enabled or Runtime is set in configuration: that explicit choice wins over Local Tester Mode.</summary>
    internal bool RuntimeConfigured { get; set; } = true;

    /// <summary>Local Tester Mode as it applies to this instance (configured and listening on loopback only); set at startup.</summary>
    internal Func<bool>? LocalTesterModeActive { get; set; }

    /// <summary>
    /// The backend runs on the tester's own computer, so it may open a browser there, run the loopback HTTPS inspection proxy and change the
    /// current user's certificate trust (each with its own confirmation). An explicit AuthenticatedReview Enabled/Runtime decides; without
    /// one, Local Tester Mode decides (2026-11-18) — so development, a local Release run, a pipeline artifact and the Windows Tester Package
    /// behave the same, and an instance reachable from other machines never qualifies.
    /// </summary>
    internal bool IsLocalWorkstation => RuntimeConfigured
        ? Enabled && string.Equals(Runtime, "LocalWorkstation", StringComparison.Ordinal)
        : LocalTesterModeActive?.Invoke() == true;

    /// <summary>The effective runtime, for logs and diagnostics.</summary>
    internal string EffectiveRuntime => IsLocalWorkstation ? "LocalWorkstation" : RuntimeConfigured ? Runtime : "Unsupported (Local Tester Mode off)";
}
