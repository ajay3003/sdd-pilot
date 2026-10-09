using BirkNext.CriticalE2E;

namespace BirkNext.Api.Services.ActiveEventTesting;

/// <summary>
/// Backend configuration of active event execution (<c>ActiveEventTesting</c> section; the earlier <c>ActiveCdcTests</c> section is read
/// as a fallback for the same keys). Off by default. There is deliberately no connection string, SAS or key setting: the Event Hub sender
/// uses the instance's Azure identity or nothing. Environment trust is not configured here — see <see cref="TrustedExecutionEnvironmentRegistry"/>.
/// </summary>
public sealed record ActiveEventOptions
{
    public bool Enabled { get; init; }
    /// <summary>Final allowlist: Event Hubs a synthetic event may be sent to — namespace FQDN + hub, both exact (case-insensitive). Empty = none.</summary>
    public IReadOnlyList<ApprovedDestination> AllowedDestinations { get; init; } = [];
    public int SendTimeoutSeconds { get; init; } = 30;
    public int ObservationSeconds { get; init; } = 180;
    public int PollSeconds { get; init; } = 15;
    public int MaxPayloadBytes { get; init; } = 8192;
    /// <summary>Upper bound on events one run may send, whatever a provider declares.</summary>
    public int MaxEventsPerRun { get; init; } = 10;

    public sealed record ApprovedDestination(string NamespaceFqdn, string EventHub);

    public static ActiveEventOptions From(IConfiguration configuration)
    {
        var current = configuration.GetSection("ActiveEventTesting");
        var legacy = configuration.GetSection("ActiveCdcTests");
        IConfigurationSection Pick(string key) => current.GetSection(key).Exists() ? current.GetSection(key) : legacy.GetSection(key);
        int Int(string key, int fallback) => Pick(key).Value is { } text && int.TryParse(text, out var value) ? value : fallback;
        return new ActiveEventOptions
        {
            Enabled = bool.TryParse(Pick("Enabled").Value, out var enabled) && enabled,
            AllowedDestinations = Pick("AllowedDestinations").GetChildren()
                .Select(c => new ApprovedDestination(c["NamespaceFqdn"] ?? "", c["EventHub"] ?? ""))
                .Where(d => d.NamespaceFqdn.Length > 0 && d.EventHub.Length > 0).ToList(),
            SendTimeoutSeconds = Math.Clamp(Int("SendTimeoutSeconds", 30), 5, 60),
            ObservationSeconds = Math.Clamp(Int("ObservationSeconds", 180), 0, 600),
            PollSeconds = Math.Clamp(Int("PollSeconds", 15), 1, 60),
            MaxPayloadBytes = Math.Clamp(Int("MaxPayloadBytes", 8192), 1024, 65536),
            MaxEventsPerRun = Math.Clamp(Int("MaxEventsPerRun", 10), 1, 50),
        };
    }
}

/// <summary>An Event Hub destination that passed every policy check. Only <see cref="ActiveEventPolicy"/> creates one.</summary>
public sealed class ApprovedEventHubDestination
{
    internal ApprovedEventHubDestination(string namespaceFqdn, string eventHub) { NamespaceFqdn = namespaceFqdn; EventHub = eventHub; }
    public string NamespaceFqdn { get; }
    public string EventHub { get; }
}

/// <summary>
/// Where an active event may be sent. Enforced in the backend on readiness, on start, and again immediately before each send — the UI is
/// never the guard. Production and unknown environments are refused; the destination must be enrolled in backend configuration AND its
/// namespace/hub names must carry the environment's marker and no production marker; the trusted target URL must not be a production host.
/// Scenario providers cannot change any of this: they can only add their own blocking checks.
/// </summary>
public sealed class ActiveEventPolicy(ActiveEventOptions options)
{
    private static readonly string[] ActiveEnvironments = ["Development", "QA"];
    private static readonly string[] ProductionMarkers = ["prod", "prd", "production", "live"];

    public ActiveEventOptions Options => options;

    /// <summary>Only Development and QA — narrower than Critical E2E automation (which also allows Local/Test/RC), and never Production or unknown.</summary>
    public static string? EnvironmentBlock(string? environmentType)
    {
        if (!CriticalE2EEnvironmentPolicy.AllowsAutomation(environmentType)) return CriticalE2EEnvironmentPolicy.BlockedReason(environmentType);
        return ActiveEnvironments.Contains(environmentType!.Trim(), StringComparer.OrdinalIgnoreCase) ? null
            : $"Active event tests run only against Development or QA, not {environmentType.Trim()}.";
    }

    public static string EnvironmentMarker(string environmentType) =>
        environmentType.Trim().Equals("QA", StringComparison.OrdinalIgnoreCase) ? "qa" : "dev";

    /// <summary>Tokens of a host/entity name split on the separators Azure names use.</summary>
    internal static string[] Tokens(string value) => value.ToLowerInvariant().Split(['-', '.', '_', '/', ':'], StringSplitOptions.RemoveEmptyEntries);

    public static bool LooksLikeProduction(string? value) =>
        !string.IsNullOrWhiteSpace(value) && Tokens(value).Any(t => ProductionMarkers.Contains(t) || t.StartsWith("prod", StringComparison.Ordinal));

    /// <summary>Approves the destination or says why not. Every check is backend-side and independent of what the browser shows.</summary>
    public (ApprovedEventHubDestination? Approved, string Reason) Approve(string? environmentType, string? namespaceFqdn, string? eventHub, string? targetUrl)
    {
        if (!options.Enabled) return (null, "Active event tests are disabled for this BirkNext instance (ActiveEventTesting:Enabled is not true).");
        if (EnvironmentBlock(environmentType) is { } env) return (null, env);
        if (string.IsNullOrWhiteSpace(namespaceFqdn) || string.IsNullOrWhiteSpace(eventHub)) return (null, "The integration has no Event Hubs namespace FQDN or Event Hub name configured.");
        var fqdn = namespaceFqdn.Trim().TrimEnd('/');
        var hub = eventHub.Trim();
        if (!fqdn.EndsWith(".servicebus.windows.net", StringComparison.OrdinalIgnoreCase) || fqdn.Contains('/') || fqdn.Contains(':'))
            return (null, "The namespace is not an Azure Event Hubs namespace host (*.servicebus.windows.net).");
        if (LooksLikeProduction(fqdn) || LooksLikeProduction(hub)) return (null, "The destination name carries a production marker.");
        if (!string.IsNullOrWhiteSpace(targetUrl))
        {
            if (!Uri.TryCreate(targetUrl, UriKind.Absolute, out var target)) return (null, "The trusted environment's target URL is not an absolute URL.");
            if (LooksLikeProduction(target.Host)) return (null, "The trusted environment's target application host carries a production marker.");
        }
        var marker = EnvironmentMarker(environmentType!);
        if (!Tokens(fqdn).Concat(Tokens(hub)).Any(t => t == marker || t.EndsWith(marker, StringComparison.Ordinal)))
            return (null, $"Neither the namespace nor the hub name carries the {environmentType!.Trim()} marker \"{marker}\".");
        if (!options.AllowedDestinations.Any(d => d.NamespaceFqdn.Trim().TrimEnd('/').Equals(fqdn, StringComparison.OrdinalIgnoreCase) && d.EventHub.Trim().Equals(hub, StringComparison.OrdinalIgnoreCase)))
            return (null, "This namespace/hub is not enrolled for active tests in backend configuration (ActiveEventTesting:AllowedDestinations).");
        return (new ApprovedEventHubDestination(fqdn, hub), "Enrolled DEV/QA destination; environment, target URL and name guards passed.");
    }
}
