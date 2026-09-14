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
}
