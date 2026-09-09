using System.Text.Json;

namespace BirkNext.Web.Models;

/// <summary>
/// Rule: applying the detector's own proposal is NOT a detection-invalidating configuration change.
///
/// The manual authentication verification context is fingerprinted from the profile as it was when
/// detection ran. A later edit only invalidates that context when it diverges from the detection
/// evidence. Copying the detector's own proposal (authentication type, authority, tenant, client ID,
/// redirect URLs, suggested environment type) into the draft keeps the profile consistent with the
/// evidence that produced it, so it must not demand another Detect settings run.
///
/// This helper produces a copy of the current profile in which every field that exactly equals the
/// detector's proposal is reset to its detection-time value. Fingerprinting that copy yields the
/// detection-time fingerprint when, and only when, all remaining differences are the detector's own
/// proposals. Any independent edit (a different authority, a hand-typed client ID, a changed URL)
/// survives the reset and changes the fingerprint.
/// </summary>
public static class DetectionProposalConsistency
{
    public static string ConsistentFingerprint(FrontendAnalysisProfile current, FrontendAnalysisProfile detectionSnapshot, TargetEnvironmentDetectionResult detection) =>
        ManualAuthenticationVerificationEvidence.Fingerprint(NeutralizeAppliedProposals(current, detectionSnapshot, detection));

    public static FrontendAnalysisProfile NeutralizeAppliedProposals(FrontendAnalysisProfile current, FrontendAnalysisProfile detectionSnapshot, TargetEnvironmentDetectionResult detection)
    {
        var clone = JsonSerializer.Deserialize<FrontendAnalysisProfile>(JsonSerializer.Serialize(current))!;
        var auth = clone.Authentication;
        var snapshot = detectionSnapshot.Authentication;

        if (detection.DetectedAuthenticationType != FrontendAuthenticationType.None &&
            auth.AuthenticationType == detection.DetectedAuthenticationType)
            auth.AuthenticationType = snapshot.AuthenticationType;

        if (MatchesProposal(auth.ExpectedAuthority, detection.DetectedAuthority))
            auth.ExpectedAuthority = snapshot.ExpectedAuthority;

        // Apply prefers the concrete tenant GUID and falls back to the tenant mode.
        var proposedTenant = !string.IsNullOrWhiteSpace(detection.DetectedTenantId) ? detection.DetectedTenantId : detection.TenantMode;
        if (MatchesProposal(auth.ExpectedTenant, proposedTenant))
            auth.ExpectedTenant = snapshot.ExpectedTenant;

        if (MatchesProposal(auth.ExpectedClientId, detection.DetectedClientId))
            auth.ExpectedClientId = snapshot.ExpectedClientId;

        if (detection.DetectedRedirectUrls.Count > 0)
        {
            // Apply merges detected redirect URLs into the existing list without duplicates.
            var merged = new HashSet<string>(snapshot.AllowedRedirectUrls, StringComparer.OrdinalIgnoreCase);
            foreach (var url in detection.DetectedRedirectUrls) merged.Add(url);
            if (merged.SetEquals(auth.AllowedRedirectUrls))
                auth.AllowedRedirectUrls = [.. snapshot.AllowedRedirectUrls];
        }

        if (detection.SuggestedEnvironmentType is { } suggested && clone.EnvironmentType == suggested)
            clone.EnvironmentType = detectionSnapshot.EnvironmentType;

        return clone;
    }

    private static bool MatchesProposal(string? current, string? proposed) =>
        !string.IsNullOrWhiteSpace(proposed) && string.Equals(current, proposed, StringComparison.Ordinal);
}
