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

    /// <summary>The Setup Library with one saved item opened (Open in Setup Library after Save).</summary>
    public static string LibraryItemHref(string environmentId, Guid itemId) => $"{LibraryHref(environmentId)}&item={itemId:D}";

    /// <summary>Setup Library's own storage vs a downloaded file: two different copies, said once wherever both are offered.</summary>
    public const string StorageNote = "Setup Library stores an internal reusable copy in BirkNext. Export creates a separate portable YAML/JSON file; BirkNext does not track downloaded files.";

    /// <summary>"Downloaded JSON package: altinn.setup.json" — the browser download only; BirkNext does not know where the file was saved.</summary>
    public static string Downloaded(SetupPackageFormat format, string fileName) => $"Downloaded {(format == SetupPackageFormat.Json ? "JSON" : "YAML")} package: {fileName}";

    /// <summary>"Saved internally to Setup Library as “Name” · v1", from the item the backend returned.</summary>
    public static string SavedToLibrary(SetupLibraryItemView item) => $"Saved internally to Setup Library as “{item.Name}” · v{item.Versions.FirstOrDefault()?.VersionNumber}";

    /// <summary>The library list status: Ready is the package's own validity, never readiness of a destination (that is preflight).</summary>
    public static (string Label, string? Help) LibraryStatus(SetupLibraryStatus status) => status == SetupLibraryStatus.Ready
        ? ("Package ready", "Package is valid. Destination readiness is checked during preflight.")
        : (status == SetupLibraryStatus.NeedsReview ? "Needs review" : status.ToString(), null);

    /// <summary>Test readiness after preflight: Ready, or a warning with the number of unresolved test items (never an Apply blocker).</summary>
    public static (string Status, string Tone)? TestStatus(SetupPreflightResult result)
    {
        if (!result.Dependencies.Any(d => d.Phase == SetupDependencyPhase.Test)) return null;
        var open = result.TestTotal - result.TestResolved;
        return open == 0 ? ("Test readiness: Ready", "ok")
            : ($"Test readiness warning · {open} item{(open == 1 ? "" : "s")} unresolved — these do not block Apply", "warn");
    }

    /// <summary>"Configuration already matches this package." when every change row is Same (and, when they are what remains, "Only unresolved
    /// dependencies remain."). Null otherwise.</summary>
    public static string? AlreadyMatches(SetupPreflightResult result)
    {
        if (result.Changes.Count == 0 || result.Changes.Any(c => c.Classification != "Same")) return null;
        var open = result.Dependencies.Any(d => d.Dependency.Required && d.State != SetupDependencyState.Resolved);
        return "Configuration already matches this package." + (open ? " Only unresolved dependencies remain." : "");
    }

    /// <summary>Dependencies grouped by the owner that resolves them, unresolved groups first; one action per group.</summary>
    public static IReadOnlyList<(string Owner, string Href, string Action, IReadOnlyList<SetupDependencyResolution> Items, bool Resolved)> Groups(IEnumerable<SetupDependencyResolution> dependencies) =>
        [.. dependencies.GroupBy(d => (Resolved: d.State == SetupDependencyState.Resolved, d.Owner, d.OwnerHref, Action: d.ActionLabel ?? $"Open {d.Owner}"))
            .OrderBy(g => g.Key.Resolved).ThenBy(g => g.Key.Owner, StringComparer.Ordinal)
            .Select(g => (g.Key.Owner, g.Key.OwnerHref, g.Key.Action, (IReadOnlyList<SetupDependencyResolution>)[.. g.OrderBy(d => !d.Dependency.Required)], g.Key.Resolved))];

    /// <summary>After Apply: what is applied and what testing still needs.</summary>
    public static string AppliedOutcome(SetupApplyPackageResult applied, SetupPreflightResult? before)
    {
        if (!applied.Applied) return applied.Outcome;
        var basis = applied.Preflight ?? before;
        var open = basis is null ? 0 : basis.TestTotal - basis.TestResolved;
        return open == 0 ? applied.Outcome : $"Setup applied. Test readiness still has {open} unresolved item{(open == 1 ? "" : "s")}.";
    }

    public static string Label(SetupReadinessState state) => state switch
    {
        SetupReadinessState.Ready => "Ready",
        SetupReadinessState.Partial => "Partial",
        SetupReadinessState.Blocked => "Blocked",
        SetupReadinessState.Observed => "Observed",
        SetupReadinessState.NotVerified => "Not verified",
        SetupReadinessState.NoExecutor => "No executor",
        SetupReadinessState.NotApplicable => "Not applicable",
        SetupReadinessState.Verified => "Verified",
        _ => "Not assessed",
    };

    /// <summary>A one-line explanation of a state (tooltip), or null when the label says it all.</summary>
    public static string? Help(SetupReadinessState state) => state switch
    {
        SetupReadinessState.NotAssessed => "No assessment has been performed yet.",
        SetupReadinessState.NotVerified => "Verification is possible but no verification evidence exists yet.",
        SetupReadinessState.NoExecutor => "BirkNext does not currently provide automated execution for this provider.",
        SetupReadinessState.NotApplicable => "This does not apply to this provider.",
        SetupReadinessState.Observed => "Observed evidence; not a verification.",
        _ => null,
    };

    public static string Tone(SetupReadinessState state) => state switch
    {
        SetupReadinessState.Ready => "ok",
        SetupReadinessState.Partial => "warn",
        SetupReadinessState.Blocked => "bad",
        SetupReadinessState.Observed => "info",
        SetupReadinessState.Verified => "ok",
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
        $"{dependency.Kind switch { SetupDependencyKind.TestData => "Test Data", SetupDependencyKind.ProviderCapability => "Capability", SetupDependencyKind.SourceSnapshot => "Source", SetupDependencyKind.ExternalIntegration => "Connected system", _ => dependency.Kind.ToString() }}: "
        + (dependency.Role is { } role && role != dependency.Reference ? $"{role} ({dependency.Reference})" : dependency.Reference);

    /// <summary>Setup apply readiness: "Setup ready to apply" / "Import blocked · N blocking dependencies unresolved" / "Package invalid". Test-only
    /// gaps never make it blocked (see <see cref="TestStatus"/>).</summary>
    public static (string Status, string Tone) Status(SetupPreflightResult result)
    {
        if (!result.Valid) return ("Package invalid", "bad");
        if (result.CanApply) return ("Setup ready to apply", "ok");
        var open = result.RequiredTotal - result.RequiredResolved;
        return (open > 0 ? $"Import blocked · {open} blocking dependenc{(open == 1 ? "y" : "ies")} unresolved" : "Import blocked", "bad");
    }

    public static string RequiredLine(SetupPreflightResult result) => $"{result.RequiredResolved} / {result.RequiredTotal} resolved";
    public static string TestLine(SetupPreflightResult result) => $"{result.TestResolved} / {result.TestTotal} resolved";

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
