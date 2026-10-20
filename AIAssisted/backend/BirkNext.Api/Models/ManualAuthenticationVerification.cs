using System.Text.Json.Serialization;

namespace BirkNext.Api.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AuthenticationVerificationMode { Automated, ManualManagedEdge }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ManualAuthenticationVerificationStatus { NotRequired, Required, Pending, Passed, Failed, Stale }

/// <summary>Policy outcome, never proof of an authenticated browser session. Detection reports only what it established (reachability and
/// configuration); whether sign-in works is owned by runtime/browser verification and the Authentication tab's optional manual check.</summary>
public static class ManualAuthenticationVerification
{
    public const string DetectionMessage = "Target reached and application configuration detected.";

    public static void Apply(TargetEnvironmentDetectionResponse response)
    {
        if (!response.Success || response.Reachability is not (TargetReachability.Reachable or TargetReachability.AuthenticationRequired))
            return;
        response.ManualAuthenticationVerificationRequired = true;
        response.ManualAuthenticationVerificationStatus = ManualAuthenticationVerificationStatus.Required;
        response.State = TargetDetectionState.ManualAuthenticationVerificationRequired;
        response.BrowserRuntimeInspectionRequired = false;
        response.IsActivationReady = false;
        response.Message = DetectionMessage;
    }
}
