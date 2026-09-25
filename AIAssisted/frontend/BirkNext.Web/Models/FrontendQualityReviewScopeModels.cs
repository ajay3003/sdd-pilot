namespace BirkNext.Web.Models;

/// <summary>
/// Which access paths a Frontend Quality Review covers. What the review COVERS, never whether the target application uses
/// sign-in: a public review of an application with Microsoft Entra ID configured is PublicOnly, and that is not a claim
/// that the application has no authentication.
/// </summary>
public enum FrontendReviewAccessScope
{
    PublicOnly,
    AuthenticatedOnly,
    PublicAndAuthenticated,
}

/// <summary>One access path (public frontend, authenticated frontend) as this review can use it.</summary>
public enum FrontendQualityAccessPathState
{
    /// <summary>Part of the configured scope and usable by at least one engine.</summary>
    Available,
    /// <summary>Part of the configured scope, but no usable context exists (session missing, method cannot render pages, blocked).</summary>
    Unavailable,
    /// <summary>Part of the configured scope, but no sign-in provider is configured for the Target Environment.</summary>
    NotConfigured,
    /// <summary>Outside the configured scope for this review.</summary>
    NotIncluded,
}

/// <summary>
/// The access path one engine uses, reduced to what it covers. Browser Companion evidence is its own path: it comes from the
/// user's own browser, and the sign-in state of each captured page is not recorded, so it never counts as authenticated
/// coverage (nor as public coverage) by itself.
/// </summary>
public enum FrontendQualityEngineAccessPath
{
    Public,
    Authenticated,
    CompanionEvidence,
}

/// <summary>
/// The review's access scope before it runs: what the Target Environment scopes the review to (<see cref="Configured"/>),
/// what each path can do right now, and what the engines that can actually run will cover (<see cref="Effective"/>).
/// Authentication configured, authenticated access included, authenticated access available and authenticated review
/// executed are four different facts; this record carries the middle two, the configuration and the result live elsewhere.
/// </summary>
public sealed record FrontendQualityReviewScope(
    FrontendReviewAccessScope Configured,
    FrontendQualityAccessPathState PublicAccess,
    FrontendQualityAccessPathState AuthenticatedAccess,
    /// <summary>Why the authenticated path is in its state, in one sentence.</summary>
    string AuthenticatedDetail,
    /// <summary>An authenticated browser session exists even though this review does not include the authenticated path.</summary>
    bool AuthenticatedSessionAvailable,
    /// <summary>What the engines able to run will cover; null when no engine can cover either path.</summary>
    FrontendReviewAccessScope? Effective,
    IReadOnlyList<FrontendQualityEngineScopeEntry> Engines)
{
    /// <summary>The configured scope is only partly reachable: the authenticated part is out of reach for this run.</summary>
    public bool Narrowed => Effective is { } effective && effective != Configured;

    /// <summary>Only Browser Companion engines can run: evidence exists, but it proves neither access path.</summary>
    public bool CompanionOnly => Effective is null && Engines.Any(e => e.Available && e.Path == FrontendQualityEngineAccessPath.CompanionEvidence);
}

/// <summary>One active engine's access path for this review, and whether it can use it right now.</summary>
public sealed record FrontendQualityEngineScopeEntry(
    FrontendQualityEngineId EngineId,
    string DisplayName,
    FrontendQualityEngineAccessPath Path,
    bool Available);

/// <summary>What a finished review actually reached, from the engines that assessed the target.</summary>
public sealed record FrontendQualityExecutedScope(
    FrontendReviewAccessScope? Configured,
    /// <summary>Paths covered by assessed engines; null when no engine assessed either path.</summary>
    FrontendReviewAccessScope? Executed,
    IReadOnlyList<string> PublicEngines,
    IReadOnlyList<string> AuthenticatedEngines,
    IReadOnlyList<string> CompanionEngines)
{
    /// <summary>The run covered less than it was configured to.</summary>
    public bool Partial => Configured is { } configured && Executed != configured;
}
