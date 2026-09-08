using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BirkNext.Web.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AuthenticationVerificationMode { Automated, ManualManagedEdge }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ManualAuthenticationVerificationStatus { NotRequired, Required, Pending, Passed, Failed, Stale }

/// <summary>User attestation of target behavior, not transferable authentication state.</summary>
public sealed class ManualAuthenticationVerificationEvidence
{
    public AuthenticationVerificationMode Method { get; set; } = AuthenticationVerificationMode.ManualManagedEdge;
    public ManualAuthenticationVerificationStatus Result { get; set; }
    public DateTimeOffset? VerifiedAt { get; set; }
    public string ProfileId { get; set; } = "";
    public string Origin { get; set; } = "";
    public string ContextFingerprint { get; set; } = "";

    public ManualAuthenticationVerificationStatus StatusFor(FrontendAnalysisProfile profile) =>
        ContextFingerprint == Fingerprint(profile) && ProfileId == profile.Id ? Result : ManualAuthenticationVerificationStatus.Stale;

    public static string Fingerprint(FrontendAnalysisProfile profile)
    {
        // Store a digest, never URL query data or authentication configuration values.
        var context = JsonSerializer.Serialize(new { profile.Id, profile.TargetUrl, profile.EnvironmentType,
            profile.Authentication, profile.RequestTimeoutSeconds, profile.RetryCount,
            profile.Security, profile.ExpectedApiGateway, profile.AllowedRestHosts, profile.AllowedGraphQlEndpoints });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(context)));
    }

    public static ManualAuthenticationVerificationEvidence Record(FrontendAnalysisProfile profile, ManualAuthenticationVerificationStatus result) => new()
    {
        Result = result,
        VerifiedAt = result is ManualAuthenticationVerificationStatus.Passed or ManualAuthenticationVerificationStatus.Failed ? DateTimeOffset.UtcNow : null,
        ProfileId = profile.Id,
        Origin = Uri.TryCreate(profile.TargetUrl, UriKind.Absolute, out var url) ? url.GetLeftPart(UriPartial.Authority) : "",
        ContextFingerprint = Fingerprint(profile)
    };
}
