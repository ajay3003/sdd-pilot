using BirkNext.AzureEnvironment;
using BirkNext.SourceDomains;

namespace BirkNext.Web.Services;

/// <summary>
/// Pure presentation of Azure Environment Analysis: tones and labels (never colour-only), grouping, relationship sentences and the next step.
/// Observed is inventory: tones mark "needs attention to read" (not authorized, throttled) — never pass/fail of the environment.
/// </summary>
public static class AzureEnvironmentPresentation
{
    public enum Tone { Complete, Partial, Attention, Muted }

    public static string Css(Tone tone) => tone switch
    {
        Tone.Complete => "sd-pill sd-pill-complete",
        Tone.Partial => "sd-pill sd-pill-partial",
        Tone.Attention => "sd-pill sd-pill-attention",
        _ => "sd-pill sd-pill-muted",
    };

    public static Tone ToneOf(AzureCapabilityState state) => state switch
    {
        AzureCapabilityState.Available => Tone.Complete,
        AzureCapabilityState.Partial or AzureCapabilityState.Throttled => Tone.Partial,
        AzureCapabilityState.NotAuthorized or AzureCapabilityState.Failed => Tone.Attention,
        _ => Tone.Muted,
    };

    public static (string Label, Tone Tone) Connection(AzureConnectionStatus s) => s.State switch
    {
        AzureConnectionState.SignedIn => ("Signed in", Tone.Complete),
        AzureConnectionState.SigningIn => ("Signing in…", Tone.Partial),
        AzureConnectionState.AwaitingDeviceCode => ("Waiting for device code", Tone.Partial),
        AzureConnectionState.Expired => ("Sign-in expired", Tone.Attention),
        AzureConnectionState.Failed => ("Not signed in", Tone.Attention),
        AzureConnectionState.NotConfigured => ("Not configured", Tone.Muted),
        _ => ("Signed out", Tone.Muted),
    };

    public static string Method(AzureSignInMethod method) => method switch
    {
        AzureSignInMethod.DedicatedEdgeProfile => "Dedicated Edge profile",
        AzureSignInMethod.DeviceCode => "Device code",
        _ => "—",
    };

    public static bool Pending(AzureConnectionStatus s) => s.State is AzureConnectionState.SigningIn or AzureConnectionState.AwaitingDeviceCode;
    public static bool CanRead(AzureConnectionStatus s) => s.State == AzureConnectionState.SignedIn;

    public static (string Label, Tone Tone) Status(AzureAnalysisStatus status) => status switch
    {
        AzureAnalysisStatus.Complete => ("Complete", Tone.Complete),
        AzureAnalysisStatus.Partial => ("Partial — some areas could not be read", Tone.Partial),
        AzureAnalysisStatus.NotAuthorized => ("Not authorized", Tone.Attention),
        _ => ("Could not be read", Tone.Attention),
    };

    public static (string Label, Tone Tone) State(DeclaredObservedState state) => (AzureEnvironmentText.Label(state), state switch
    {
        DeclaredObservedState.DeclaredAndObserved => Tone.Complete,
        DeclaredObservedState.ConfigurationDiffers or DeclaredObservedState.AmbiguousMatch => Tone.Partial,
        _ => Tone.Muted,
    });

    public static readonly DeclaredObservedState[] StateOrder =
        [DeclaredObservedState.ConfigurationDiffers, DeclaredObservedState.AmbiguousMatch, DeclaredObservedState.DeclaredOnly, DeclaredObservedState.UnableToVerify,
         DeclaredObservedState.DeclaredAndObserved, DeclaredObservedState.ObservedOnly];

    public static string Category(InfrastructureCategory category) => category switch
    {
        InfrastructureCategory.SecretStore => "Secret stores",
        InfrastructureCategory.ResourceContainer => "Resource groups",
        InfrastructureCategory.ApiGateway => "API gateways",
        InfrastructureCategory.ContainerRegistry => "Container registries",
        InfrastructureCategory.Dns => "DNS",
        InfrastructureCategory.Observability => "Monitoring",
        InfrastructureCategory.Database => "Databases",
        InfrastructureCategory.Cache => "Caches",
        _ => category.ToString(),
    };

    /// <summary>Resources grouped by category (resource groups last), each group sorted parent-first by id.</summary>
    public static IEnumerable<(InfrastructureCategory Category, List<ObservedResource> Resources)> ByCategory(IEnumerable<ObservedResource> resources, string? filter)
    {
        var f = filter?.Trim();
        return resources.Where(r => string.IsNullOrEmpty(f) || r.Name.Contains(f, StringComparison.OrdinalIgnoreCase) || r.CategoryDetail.Contains(f, StringComparison.OrdinalIgnoreCase)
                || (r.ResourceGroup?.Contains(f, StringComparison.OrdinalIgnoreCase) ?? false))
            .GroupBy(r => r.Category).OrderBy(g => g.Key == InfrastructureCategory.ResourceContainer ? 1 : 0).ThenBy(g => Category(g.Key))
            .Select(g => (g.Key, g.OrderBy(r => r.Id, StringComparer.OrdinalIgnoreCase).ToList()));
    }

    public static string NameOf(AzureEnvironmentSnapshot snapshot, string id) =>
        snapshot.Resources.FirstOrDefault(r => AzureIds.Same(r.Id, id))?.Name ?? AzureIds.Name(id);

    public static string Sentence(AzureEnvironmentSnapshot snapshot, ObservedRelationship r) =>
        $"{NameOf(snapshot, r.FromId)} {AzureEnvironmentText.Label(r.Kind)} {NameOf(snapshot, r.ToId)}{(r.TargetInScope ? "" : " (outside the analyzed scope)")}";

    public static string Area(ObservationArea area) => area switch
    {
        ObservationArea.Observability => "Monitoring",
        _ => area.ToString(),
    };

    /// <summary>The one next step for the connection card.</summary>
    public static string NextStep(AzureConnectionStatus s, bool hasSnapshot) => s.State switch
    {
        AzureConnectionState.NotConfigured => "An administrator must configure an Entra app registration before anyone can sign in.",
        AzureConnectionState.SignedOut or AzureConnectionState.Failed => s.DedicatedEdgeAvailable ? "Sign in with the dedicated Edge profile." : "Sign in with a device code.",
        AzureConnectionState.Expired => "Refresh access, or sign in again.",
        AzureConnectionState.SigningIn or AzureConnectionState.AwaitingDeviceCode => "Finish sign-in in the browser.",
        _ => hasSnapshot ? "Review the snapshot, or analyze again after changing the scope." : "Choose the subscriptions to analyze.",
    };

    public static string Utc(DateTimeOffset at) => $"{at.UtcDateTime:yyyy-MM-dd HH:mm} UTC";
}
