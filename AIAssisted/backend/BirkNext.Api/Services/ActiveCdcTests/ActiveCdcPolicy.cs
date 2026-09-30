using BirkNext.Integrations;
using BirkNext.CriticalE2E;

namespace BirkNext.Api.Services.ActiveCdcTests;

/// <summary>
/// Backend configuration of active CDC tests (<c>ActiveCdcTests</c> section). Off by default. There is deliberately no connection string,
/// SAS or key setting: the sender uses the instance's Azure identity (<see cref="Integrations.IIntegrationAzureCredential"/>) or nothing.
/// </summary>
public sealed record ActiveCdcOptions
{
    public bool Enabled { get; init; }
    /// <summary>Event Hubs a synthetic event may be sent to — namespace FQDN + hub, both exact (case-insensitive). Empty = none.</summary>
    public IReadOnlyList<ApprovedDestination> AllowedDestinations { get; init; } = [];
    /// <summary>Reserved synthetic PersonPK range agreed for BirkNext test data. Unset = no run (a guessed range could collide with real rows).</summary>
    public int? SyntheticPersonPkMin { get; init; }
    public int? SyntheticPersonPkMax { get; init; }
    public int SendTimeoutSeconds { get; init; } = 30;
    public int ObservationSeconds { get; init; } = 180;
    public int PollSeconds { get; init; } = 15;
    public int MaxPayloadBytes { get; init; } = 8192;
    /// <summary>Archive SHA-256 values re-reviewed for the invalid fixture beyond the built-in reviewed archive (identifiers only).</summary>
    public IReadOnlyList<string> InvalidFixtureReviewedArchives { get; init; } = [];

    public sealed record ApprovedDestination(string NamespaceFqdn, string EventHub);

    public static ActiveCdcOptions From(IConfiguration configuration)
    {
        var section = configuration.GetSection("ActiveCdcTests");
        return new ActiveCdcOptions
        {
            Enabled = section.GetValue("Enabled", false),
            AllowedDestinations = section.GetSection("AllowedDestinations").GetChildren()
                .Select(c => new ApprovedDestination(c["NamespaceFqdn"] ?? "", c["EventHub"] ?? ""))
                .Where(d => d.NamespaceFqdn.Length > 0 && d.EventHub.Length > 0).ToList(),
            SyntheticPersonPkMin = section.GetValue<int?>("SyntheticPersonPkMin"),
            SyntheticPersonPkMax = section.GetValue<int?>("SyntheticPersonPkMax"),
            SendTimeoutSeconds = Math.Clamp(section.GetValue("SendTimeoutSeconds", 30), 5, 60),
            ObservationSeconds = Math.Clamp(section.GetValue("ObservationSeconds", 180), 0, 600),
            PollSeconds = Math.Clamp(section.GetValue("PollSeconds", 15), 1, 60),
            MaxPayloadBytes = Math.Clamp(section.GetValue("MaxPayloadBytes", 8192), 1024, 65536),
            InvalidFixtureReviewedArchives = section.GetSection("InvalidFixtureReviewedArchives").GetChildren().Select(c => c.Value ?? "").Where(v => v.Length == 64).ToList(),
        };
    }
}

/// <summary>An Event Hub destination that passed every policy check. Only <see cref="ActiveCdcPolicy"/> creates one.</summary>
public sealed class ApprovedCdcDestination
{
    internal ApprovedCdcDestination(string namespaceFqdn, string eventHub) { NamespaceFqdn = namespaceFqdn; EventHub = eventHub; }
    public string NamespaceFqdn { get; }
    public string EventHub { get; }
}

/// <summary>
/// Where an active CDC test may send. Enforced in the backend on readiness, on start, and again immediately before the send — the UI is
/// never the guard. Production and unknown environments are refused; the destination must be enrolled in backend configuration AND its
/// namespace/hub names must carry the claimed environment's marker and no production marker (a second guard against a spoofed type).
/// </summary>
public sealed class ActiveCdcPolicy(ActiveCdcOptions options)
{
    private static readonly string[] ActiveEnvironments = ["Development", "QA"];
    private static readonly string[] ProductionMarkers = ["prod", "prd", "production", "live"];

    public ActiveCdcOptions Options => options;

    /// <summary>Only Development and QA — narrower than Critical E2E automation (which also allows Local/Test/RC), and never Production or unknown.</summary>
    public static string? EnvironmentBlock(string? environmentType)
    {
        if (!CriticalE2EEnvironmentPolicy.AllowsAutomation(environmentType)) return CriticalE2EEnvironmentPolicy.BlockedReason(environmentType);
        return ActiveEnvironments.Contains(environmentType!.Trim(), StringComparer.OrdinalIgnoreCase) ? null
            : $"Active CDC tests run only against Development or QA, not {environmentType.Trim()}.";
    }

    public static string EnvironmentMarker(string environmentType) =>
        environmentType.Trim().Equals("QA", StringComparison.OrdinalIgnoreCase) ? "qa" : "dev";

    /// <summary>Tokens of a host/entity name split on the separators Azure names use.</summary>
    internal static string[] Tokens(string value) => value.ToLowerInvariant().Split(['-', '.', '_', '/', ':'], StringSplitOptions.RemoveEmptyEntries);

    public static bool LooksLikeProduction(string? value) =>
        !string.IsNullOrWhiteSpace(value) && Tokens(value).Any(t => ProductionMarkers.Contains(t) || t.StartsWith("prod", StringComparison.Ordinal));

    /// <summary>Approves the destination or says why not. Every check is backend-side and independent of what the browser shows.</summary>
    public (ApprovedCdcDestination? Approved, string Reason) Approve(string? environmentType, string? namespaceFqdn, string? eventHub, string? targetUrl)
    {
        if (!options.Enabled) return (null, "Active CDC tests are disabled for this BirkNext instance (ActiveCdcTests:Enabled is not true).");
        if (EnvironmentBlock(environmentType) is { } env) return (null, env);
        if (string.IsNullOrWhiteSpace(namespaceFqdn) || string.IsNullOrWhiteSpace(eventHub)) return (null, "The integration has no Event Hubs namespace FQDN or Event Hub name configured.");
        var fqdn = namespaceFqdn.Trim().TrimEnd('/');
        var hub = eventHub.Trim();
        if (!fqdn.EndsWith(".servicebus.windows.net", StringComparison.OrdinalIgnoreCase) || fqdn.Contains('/') || fqdn.Contains(':'))
            return (null, "The namespace is not an Azure Event Hubs namespace host (*.servicebus.windows.net).");
        if (LooksLikeProduction(fqdn) || LooksLikeProduction(hub)) return (null, "The destination name carries a production marker.");
        if (Uri.TryCreate(targetUrl, UriKind.Absolute, out var target) && LooksLikeProduction(target.Host))
            return (null, "The selected target application host carries a production marker.");
        var marker = EnvironmentMarker(environmentType!);
        if (!Tokens(fqdn).Concat(Tokens(hub)).Any(t => t == marker || t.EndsWith(marker, StringComparison.Ordinal)))
            return (null, $"Neither the namespace nor the hub name carries the {environmentType!.Trim()} marker \"{marker}\".");
        if (!options.AllowedDestinations.Any(d => d.NamespaceFqdn.Trim().TrimEnd('/').Equals(fqdn, StringComparison.OrdinalIgnoreCase) && d.EventHub.Trim().Equals(hub, StringComparison.OrdinalIgnoreCase)))
            return (null, "This namespace/hub is not enrolled for active tests in backend configuration (ActiveCdcTests:AllowedDestinations).");
        return (new ApprovedCdcDestination(fqdn, hub), "Enrolled DEV/QA destination; environment and name guards passed.");
    }

    /// <summary>The reserved synthetic PersonPK range, or why there is none.</summary>
    public (int Min, int Max)? PersonPkRange(out string reason)
    {
        if (options.SyntheticPersonPkMin is not { } min || options.SyntheticPersonPkMax is not { } max)
        {
            reason = "No reserved synthetic PersonPK range is configured (ActiveCdcTests:SyntheticPersonPkMin/Max). A guessed key could collide with a real row.";
            return null;
        }
        if (min > max || (long)max - min > 10_000_000 || min == 0)
        {
            reason = "The configured synthetic PersonPK range is invalid (min must be ≤ max, non-zero, and the range at most 10 000 000 keys).";
            return null;
        }
        reason = $"Reserved synthetic PersonPK range {min}–{max}.";
        return (min, max);
    }
}
