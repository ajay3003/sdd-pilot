using BirkNext.LocalHttpsProxy;

namespace BirkNext.Web.Models;

/// <summary>
/// Builds the non-secret <see cref="AuthenticatedReviewIdentity"/> a review passes to the backend so authenticated API-backed checks
/// can be routed through the active environment's memory-only proxy context. Carries the saved authenticated-testing method plus the
/// profile id and the same context fingerprint the proxy binds its credential to. Never carries a token.
/// </summary>
public static class ReviewAuthenticationIdentity
{
    public static AuthenticatedReviewIdentity For(FrontendAnalysisProfile? profile) =>
        profile is null
            ? new AuthenticatedReviewIdentity(AuthenticatedTestingMethod.ManagedEdgeCdp, null, null)
            : new AuthenticatedReviewIdentity(profile.Authentication.AuthenticatedTestingMethod, profile.Id, LocalHttpsProxyScope.Fingerprint(profile));

    /// <summary>
    /// Identity for a review page: the factory-computed identity of the SAVED profile when present (the context's
    /// <see cref="FrontendAnalysisContext.ActiveProfile"/> is a data-minimized copy whose fingerprint would not match the proxy
    /// session), otherwise derived from the context's profile copy.
    /// </summary>
    public static AuthenticatedReviewIdentity ForContext(FrontendAnalysisContext? context) =>
        context is null ? For((FrontendAnalysisProfile?)null)
        : context.ReviewIdentity is { ProfileId: { Length: > 0 } } identity ? identity
        : For(context.ActiveProfile);
}
