using System.Text.Json.Serialization;

namespace BirkNext.BuildInfo;

// BirkNext runtime profiles: HOW this installation runs (where PostgreSQL and k6 come from), not who uses it. One application, one API, one
// EF Core model, one evidence model; the profile only selects the runtime providers. Model: docs/architecture/birknext-runtime-profiles.md.

/// <summary>
/// DeveloperContainer: the source-checkout/developer setup (PostgreSQL in Podman, k6 in a container). TesterNative: the self-contained Windows
/// Tester Package (bundled native PostgreSQL managed by the launcher, bundled native k6.exe; no Podman, Docker, WSL2 or Hyper-V).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RuntimeProfile { DeveloperContainer, TesterNative }

public static class RuntimeProfiles
{
    /// <summary>Configuration key (environment variable <c>BirkNext__RuntimeProfile</c>). Set by the launcher; never inferred from installed tools.</summary>
    public const string ConfigurationKey = "BirkNext:RuntimeProfile";
    /// <summary>Set by the Tester Package launcher: the distribution it started from.</summary>
    public const string DistributionKey = "BirkNext:Distribution";
    public const string WindowsTesterPackage = "WindowsTesterPackage";

    public static string DisplayName(RuntimeProfile profile) => profile switch
    {
        RuntimeProfile.TesterNative => "Tester (native Windows)",
        _ => "Developer (containers)",
    };

    /// <summary>
    /// Parses a configured profile. Null/empty = not configured (the caller applies its default); an unknown value is an error, never a
    /// silent default: an ambiguous profile could connect to the wrong database.
    /// </summary>
    public static (RuntimeProfile? Profile, string? Error) Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return (null, null);
        foreach (var p in Enum.GetValues<RuntimeProfile>())
            if (string.Equals(p.ToString(), value.Trim(), StringComparison.OrdinalIgnoreCase)) return (p, null);
        return (null, $"Unknown runtime profile '{value}'. Use DeveloperContainer or TesterNative.");
    }
}

/// <summary>The running installation's runtime (GET api/system/runtime). Read-only; no secrets, connection strings or user paths.</summary>
public sealed record RuntimeEnvironmentInfo
{
    public RuntimeProfile Profile { get; init; }
    public string ProfileName { get; init; } = "";
    /// <summary>Where the profile came from: the launcher/configuration, or the source-checkout default.</summary>
    public string ProfileSource { get; init; } = "";
    public string Distribution { get; init; } = "";
    public string DatabaseMode { get; init; } = "";
    public string DatabaseStatus { get; init; } = "";
    public string? DatabaseServerVersion { get; init; }
    public string PerformanceRunner { get; init; } = "";
    public string PerformanceRunnerAvailability { get; init; } = "";
    public string? K6Version { get; init; }
    public string PerformanceRunnerDetail { get; init; } = "";
    public List<string> Notes { get; init; } = [];
    public DateTimeOffset EvaluatedAt { get; init; }
}
