using BirkNext.PerformanceTests;

namespace BirkNext.Web.Services;

/// <summary>Visual tone of a capability state. Maps onto the shared <c>sd-pill</c> tones. A missing engine, runtime, metric or outdated
/// evidence is a tool limitation (never <see cref="Danger"/>); Danger is only for an actual analysis/runtime error.</summary>
public enum CapabilityTone { Positive, Caution, Neutral, Planned, Danger }

/// <summary>One status pill: text (always shown), a decorative glyph and its tone.</summary>
public sealed record CapabilityStatus(string Label, string Glyph, CapabilityTone Tone)
{
    /// <summary>Shared pill tone class (Technology Coverage / Source Analysis).</summary>
    public string PillClass => Tone switch
    {
        CapabilityTone.Positive => "sd-pill-complete",
        CapabilityTone.Caution => "sd-pill-partial",
        CapabilityTone.Danger => "sd-pill-attention",
        _ => "sd-pill-muted",
    };
}

public sealed record CapabilityChip(string Label, bool Supported);

/// <summary>
/// Pure presentation of System Settings → Performance Test Engines. Every value comes from the backend capability model
/// (<see cref="PerformanceProviderStatus"/>, <see cref="ResourceProviderCapability"/>); nothing here knows a version, image or provider by name.
/// </summary>
public static class PerformanceEngineCapabilityPresentation
{
    /// <summary>Engine status. "Ready" only when the backend probe says the provider, runtime and image are usable (Available).</summary>
    public static CapabilityStatus Engine(ProviderAvailability availability) => availability switch
    {
        ProviderAvailability.Available => new("Ready", "✓", CapabilityTone.Positive),
        ProviderAvailability.ImageMissing => new("Image missing", "○", CapabilityTone.Neutral),
        ProviderAvailability.RuntimeUnavailable => new("Runtime unavailable", "○", CapabilityTone.Neutral),
        ProviderAvailability.Misconfigured => new("Misconfigured", "!", CapabilityTone.Caution),
        ProviderAvailability.VersionUnsupported => new("Unsupported version", "!", CapabilityTone.Caution),
        _ => new("Unavailable", "○", CapabilityTone.Neutral),
    };

    /// <summary>Execution runtime status (Podman): Available or the reason it is not.</summary>
    public static CapabilityStatus Runtime(ProviderAvailability availability) => availability == ProviderAvailability.Available
        ? new("Available", "✓", CapabilityTone.Positive)
        : Engine(availability);

    /// <summary>Resource provider / scope status. Partial, Unavailable, Unsupported and Not implemented stay distinct in text, glyph and tone.</summary>
    public static CapabilityStatus Resource(string availability) => availability switch
    {
        "Available" => new("Available", "✓", CapabilityTone.Positive),
        "Partial" => new("Partial", "◐", CapabilityTone.Caution),
        "Unsupported" => new("Unsupported", "⊘", CapabilityTone.Neutral),
        "Not implemented" => new("Not implemented", "◌", CapabilityTone.Planned),
        "Unavailable" => new("Unavailable", "○", CapabilityTone.Neutral),
        _ => new(string.IsNullOrWhiteSpace(availability) ? "Unknown" : availability, "○", CapabilityTone.Neutral),
    };

    /// <summary>Unsupported and Not implemented providers are listed apart from the providers that exist on this installation.</summary>
    public static bool IsCurrent(ResourceProviderCapability p) => p.Availability is not ("Unsupported" or "Not implemented");

    /// <summary>Deterministic order: Available, Partial, Unavailable, Unsupported, Not implemented; ties keep the backend order.</summary>
    public static List<ResourceProviderCapability> Order(IEnumerable<ResourceProviderCapability> providers) =>
        providers.Select((p, i) => (p, i)).OrderBy(x => Rank(x.p.Availability)).ThenBy(x => x.i).Select(x => x.p).ToList();

    private static int Rank(string availability) => availability switch
    {
        "Available" => 0,
        "Partial" => 1,
        "Unavailable" => 2,
        "Unsupported" => 3,
        "Not implemented" => 4,
        _ => 2,
    };

    /// <summary>Engines: Ready first, then by display name.</summary>
    public static List<PerformanceProviderStatus> Order(IEnumerable<PerformanceProviderStatus> engines) =>
        engines.OrderBy(e => e.Availability == ProviderAvailability.Available ? 0 : 1).ThenBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Summary: Ready when any engine is ready, otherwise the first engine's state. Null when no engine is installed.</summary>
    public static CapabilityStatus? EngineSummary(IReadOnlyList<PerformanceProviderStatus> engines) =>
        engines.Count == 0 ? null : Engine(Order(engines)[0].Availability);

    /// <summary>
    /// Summary of current resource providers (Unsupported / Not implemented are excluded — they are not part of this installation):
    /// Available when every current provider is fully available, Unavailable when none can observe anything, otherwise Partial.
    /// This is a capability summary, never a score.
    /// </summary>
    public static CapabilityStatus? ResourceSummary(IReadOnlyList<ResourceProviderCapability> providers)
    {
        var current = providers.Where(IsCurrent).ToList();
        if (current.Count == 0) return null;
        if (current.All(p => p.Availability == "Available")) return Resource("Available");
        if (current.All(p => p.Availability == "Unavailable")) return Resource("Unavailable");
        return Resource("Partial");
    }

    /// <summary>Workload capability chips, in a fixed order; unsupported ones stay visible as neutral chips for this engine.</summary>
    public static IReadOnlyList<CapabilityChip> Chips(PerformanceProviderCapabilities c) =>
    [
        new("HTTP", c.Http),
        new("GraphQL queries", c.GraphQl),
        new("Virtual users", c.Modes.Contains(WorkloadMode.VirtualUsers)),
        new("Arrival rate", c.Modes.Contains(WorkloadMode.ArrivalRate)),
        new("Cancellation", c.Cancellation),
    ];

    /// <summary>Image reference for display: the default registry prefix is dropped ("docker.io/grafana/k6:1.0.0" → "grafana/k6:1.0.0").
    /// The exact reference stays in Technical details.</summary>
    public static string ShortImage(string image)
    {
        foreach (var prefix in new[] { "docker.io/library/", "docker.io/", "index.docker.io/" })
            if (image.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return image[prefix.Length..];
        return image;
    }

    public static string RuntimeText(PerformanceRuntimeStatus runtime) =>
        string.IsNullOrWhiteSpace(runtime.Version) ? runtime.DisplayName : $"{runtime.DisplayName} {runtime.Version}";

    /// <summary>Per-metric availability of a resource provider: supported metrics, then metrics unavailable on this host.</summary>
    public static IReadOnlyList<(string Label, bool Available)> Metrics(ResourceProviderCapability p) =>
        [.. p.Metrics.Select(m => (ResourceFormat.Label(m), true)), .. p.UnavailableMetrics.Except(p.Metrics).Select(m => (ResourceFormat.Label(m), false))];
}
