using BirkNext.Integrations;

namespace BirkNext.Web.Services;

/// <summary>
/// Groups the backend's Rapportmottak readiness rows for the run page: what was resolved automatically (with its source), what still needs
/// attention (only unresolved inputs, each with the owner that resolves it) and the TT02 readiness decision. Presentation only: every state
/// is the backend's own; nothing is recomputed, upgraded or resolved here.
/// </summary>
public static class SkoleNaervaerReadinessPresentation
{
    /// <summary>The run inputs a Rapportmottak submission needs, in the order a tester reads them.</summary>
    public static readonly IReadOnlyList<string> RunInputs =
    [
        "trusted-environment", "integration:report-intake", "altinn-environment", "altinn-app", "submission-contract", "public-metadata", "process-contract", "contract-impact",
        "idporten-client", "redirect-uri", "client-secret", "unattended-provider", "synthetic-reporter", "reporter-profile", "app-policy", "test-reporter", "synthetic-data",
    ];

    /// <summary>The unattended parts behind "Reporter authentication": when it is not ready, these say exactly what is missing.</summary>
    private static readonly string[] AuthenticationParts = ["unattended-provider", "idporten-client", "redirect-uri", "client-secret", "synthetic-reporter", "reporter-profile"];

    public sealed record Action(string Label, string? Href, string? Group);

    /// <summary>Run inputs the backend resolved (Ready), with their source.</summary>
    public static IReadOnlyList<JourneyPrerequisite> Resolved(IEnumerable<JourneyPrerequisite> rows)
    {
        var byKey = rows.GroupBy(row => row.Key).ToDictionary(group => group.Key, group => group.First());
        return RunInputs.Select(key => byKey.GetValueOrDefault(key)).Where(row => row is { State: JourneyReadinessState.Ready }).Select(row => row!).ToList();
    }

    /// <summary>
    /// Only what is unresolved for a submission: the mandatory rows that are not ready, with "Reporter authentication" replaced by its
    /// unresolved unattended parts (a missing secret is shown as exactly that), plus missing public metadata behind an incomplete contract.
    /// The first TT02 run is unattended, so its unresolved parts are listed even when the interactive TestID fallback satisfies the gate.
    /// </summary>
    public static IReadOnlyList<JourneyPrerequisite> NeedsAttention(IEnumerable<JourneyPrerequisite> rows, string scenarioId = "valid-report")
    {
        var all = rows.ToList();
        bool Open(JourneyPrerequisite row) => row.State != JourneyReadinessState.Ready;
        var result = new List<JourneyPrerequisite>();
        foreach (var row in all.Where(row => row.Mandatory && row.AppliesTo(scenarioId) && Open(row)))
        {
            if (row.Key == "reporter-auth")
            {
                var parts = AuthenticationParts.Select(key => all.FirstOrDefault(item => item.Key == key)).Where(item => item is not null && Open(item)).Select(item => item!).ToList();
                result.AddRange(parts.Count > 0 ? parts : [row]);
            }
            else if (row.Key == "submission-contract" && all.FirstOrDefault(item => item.Key == "public-metadata") is { } metadata && Open(metadata))
                result.AddRange([metadata, row]);
            else result.Add(row);
        }
        if (InteractiveFallbackOnly(all))
            result.InsertRange(0, AuthenticationParts.Select(key => all.FirstOrDefault(item => item.Key == key)).Where(item => item is not null && Open(item)).Select(item => item!));
        return result.DistinctBy(row => row.Key).ToList();
    }

    /// <summary>True when the run gate is met only through the interactive TestID sign-in fallback: unattended parts are still unresolved.</summary>
    public static bool InteractiveFallbackOnly(IEnumerable<JourneyPrerequisite> rows)
    {
        var all = rows.ToList();
        return all.FirstOrDefault(item => item.Key == "reporter-auth") is { State: JourneyReadinessState.Ready }
            && AuthenticationParts.Any(key => all.FirstOrDefault(item => item.Key == key) is { } part && part.State != JourneyReadinessState.Ready);
    }

    /// <summary>Where an unresolved input is resolved: its owner, never the run page (which stores no copy).</summary>
    public static Action ActionFor(JourneyPrerequisite row, string? profileId) => row.Key switch
    {
        "trusted-environment" => new("Open Target Environment", "/admin/system-settings?section=target-environments", null),
        _ when row.Key.StartsWith("integration:", StringComparison.Ordinal) => new("Open Target Environment → Integrations", IntegrationReviewPresentation.IntegrationsHref(profileId), null),
        "idporten-client" or "altinn-app" or "altinn-environment" or "test-reporter" => new("Open Altinn test configuration", "/integration-quality-review?view=test-configuration", null),
        "redirect-uri" or "client-secret" or "unattended-provider" or "reporter-auth" or "app-policy" => new("Configure local execution", null, "local"),
        "synthetic-reporter" or "reporter-profile" or "synthetic-data" => new("Open Test Data", "/test-data", null),
        "public-metadata" or "submission-contract" or "process-contract" => new("Read public TT02 metadata", null, "contract"),
        "contract-impact" => new("Review contract changes", null, "contract"),
        _ => new("View details", null, "advanced"),
    };

    /// <summary>
    /// The TT02 readiness decision from what is still unresolved, root cause first: ID-porten configuration, then reporter/test identity, then
    /// trusted target/integration, then payload/contract. Ready means ready for a first SUPERVISED submission — never that one is started.
    /// </summary>
    public static (string Code, string Label) Decision(IReadOnlyList<JourneyPrerequisite> needsAttention)
    {
        var keys = needsAttention.Select(row => row.Key).ToHashSet(StringComparer.Ordinal);
        bool Any(params string[] candidates) => candidates.Any(keys.Contains);
        if (keys.Count == 0) return ("A", "Ready for first supervised TT02 submission");
        if (Any("idporten-client", "redirect-uri", "client-secret", "unattended-provider", "reporter-auth")) return ("B", "ID-porten configuration incomplete");
        if (Any("synthetic-reporter", "reporter-profile", "reporter-role", "test-reporter", "synthetic-data")) return ("C", "Reporter / test identity incomplete");
        if (Any("trusted-environment") || keys.Any(key => key.StartsWith("integration:", StringComparison.Ordinal))) return ("D", "Trusted target / integration incomplete");
        if (Any("altinn-app", "altinn-environment", "submission-contract", "public-metadata", "process-contract", "app-policy", "contract-impact")) return ("E", "Payload / contract blocker");
        return ("F", "Other blocker (see technical details)");
    }
}
