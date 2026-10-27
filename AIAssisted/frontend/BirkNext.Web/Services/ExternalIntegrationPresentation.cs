using BirkNext.Integrations;

namespace BirkNext.Web.Services;

/// <summary>
/// Shared presentation for External Integrations, setup import/export and the Setup Library: the same five readiness dimensions for every
/// provider, dependency states as text, and owner links. Generic: provider-specific wording comes from the provider's descriptor.
/// </summary>
public static class ExternalIntegrationPresentation
{
    public static string ExternalHref(string environmentId, string? focus = null) =>
        $"/admin/system-settings?section=target-environments&tab=external&profile={Uri.EscapeDataString(environmentId)}" + (focus is null ? "" : $"&focus={Uri.EscapeDataString(focus)}");

    public static string LibraryHref(string environmentId) => $"/admin/system-settings?section=target-environments&tab=library&profile={Uri.EscapeDataString(environmentId)}";

    public static string Label(SetupReadinessState state) => state switch
    {
        SetupReadinessState.Ready => "Ready",
        SetupReadinessState.Partial => "Partial",
        SetupReadinessState.Blocked => "Blocked",
        SetupReadinessState.Observed => "Observed",
        _ => "Not verified",
    };

    public static string Tone(SetupReadinessState state) => state switch
    {
        SetupReadinessState.Ready => "ok",
        SetupReadinessState.Partial => "warn",
        SetupReadinessState.Blocked => "bad",
        SetupReadinessState.Observed => "info",
        _ => "muted",
    };

    public static IReadOnlyList<SetupReadinessItem> Dimensions(ExternalIntegrationSummary summary) =>
        [summary.Configuration, summary.Dependencies, summary.Authentication, summary.Runtime, summary.Execution];

    public static string Label(SetupDependencyState state) => state switch
    {
        SetupDependencyState.Resolved => "Resolved",
        SetupDependencyState.Missing => "Missing",
        SetupDependencyState.Ambiguous => "Ambiguous",
        SetupDependencyState.Incompatible => "Incompatible",
        SetupDependencyState.NotApproved => "Not approved",
        _ => "Missing · Optional",
    };

    public static string Tone(SetupDependencyState state) => state switch
    {
        SetupDependencyState.Resolved => "ok",
        SetupDependencyState.OptionalMissing => "muted",
        _ => "bad",
    };

    public static string DependencyLabel(SetupDependency dependency) =>
        $"{dependency.Kind switch { SetupDependencyKind.TestData => "Test Data", SetupDependencyKind.ProviderCapability => "Capability", SetupDependencyKind.SourceSnapshot => "Source", SetupDependencyKind.ExternalIntegration => "External integration", _ => dependency.Kind.ToString() }}: "
        + (dependency.Role is { } role && role != dependency.Reference ? $"{role} ({dependency.Reference})" : dependency.Reference);

    /// <summary>"IMPORT READY" / "IMPORT BLOCKED" / "PACKAGE INVALID" with the reason line.</summary>
    public static (string Status, string Tone) Status(SetupPreflightResult result) =>
        !result.Valid ? ("PACKAGE INVALID", "bad") : result.CanApply ? ("IMPORT READY", "ok") : ("IMPORT BLOCKED", "bad");

    public static string RequiredLine(SetupPreflightResult result) => $"{result.RequiredResolved} / {result.RequiredTotal} resolved";

    public static string? OptionalLine(SetupPreflightResult result) => result.OptionalMissing == 0 ? null
        : $"Import can proceed with {result.OptionalMissing} optional capabilit{(result.OptionalMissing == 1 ? "y" : "ies")} unavailable.";

    public static string ScopeLabel(SetupScope scope) => scope switch
    {
        SetupScope.Integration => "Single external integration",
        SetupScope.Provider => "Provider",
        _ => "Entire target environment",
    };

    public static string ProviderLabel(ExternalProviderType type, IReadOnlyList<ExternalProviderDescriptor> providers) =>
        providers.FirstOrDefault(provider => provider.Type == type)?.DisplayName ?? type.ToString();

    /// <summary>"5 configured · 4 detected · 1 conflict · 1 missing" (Detect result line).</summary>
    public static string DetectionLine(AltinnDetectionSummary summary) =>
        $"{summary.Configured} configured · {summary.Detected} detected · {summary.Conflicts} conflict{(summary.Conflicts == 1 ? "" : "s")} · {summary.Missing} missing";
}
