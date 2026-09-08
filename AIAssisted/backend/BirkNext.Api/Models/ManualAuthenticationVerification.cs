using System.Text.Json.Serialization;

namespace BirkNext.Api.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AuthenticationVerificationMode { Automated, ManualManagedEdge }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ManualAuthenticationVerificationStatus { NotRequired, Required, Pending, Passed, Failed, Stale }

/// <summary>Policy outcome, never proof of an authenticated browser session.</summary>
public static class ManualAuthenticationVerification
{
    public const string Reason = "Your organization requires sign-in using a managed Microsoft Edge work profile. BirkNext cannot verify this sign-in automatically.";

    public static void Apply(TargetEnvironmentDetectionResponse response)
    {
        if (!response.Success || response.Reachability is not (TargetReachability.Reachable or TargetReachability.AuthenticationRequired))
            return;
        response.ManualAuthenticationVerificationRequired = true;
        response.ManualAuthenticationVerificationStatus = ManualAuthenticationVerificationStatus.Required;
        response.State = TargetDetectionState.ManualAuthenticationVerificationRequired;
        response.BrowserRuntimeInspectionRequired = false;
        response.IsActivationReady = false;
        response.Message = Reason;
    }
}
