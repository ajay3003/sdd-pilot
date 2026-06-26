using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>
/// Groups candidates by architectural concern for the Architecture View.
/// A candidate may appear in multiple groups (multi-membership) because one
/// requirement often touches both security and an API, for example.
/// </summary>
public static class ArchitectureMapper
{
    private sealed record ConcernDef(string Key, string Label, string[] Keywords);

    private static readonly ConcernDef[] Concerns =
    [
        new("arch-api", "API & Interfaces",
        [
            " api", "endpoint", "rest ", "http", "get ", "post ", "put ", "delete ", "patch ",
            "openapi", "swagger", "graphql", "route", "controller", "url", "uri",
            "grensesnitt", "kontract",
        ]),
        new("arch-events", "Events & Messaging",
        [
            "event", "hendelse", "message", "melding", "service bus", "queue", "kø",
            "publish", "subscribe", "topic", "consumer", "producer", "stream", "bus",
            "kafka", "rabbitmq", "signalr",
        ]),
        new("arch-data", "Data & Storage",
        [
            "database", " db ", "repository", "storage", "lagring", "table", "dokument",
            "mongodb", "sql", "cosmos", "redis", "cache", "blob", "repo",
            "persistent", "datastore",
        ]),
        new("arch-security", "Security & Access",
        [
            "auth", "oauth", "oidc", "token", "role", "permission", "rettighet",
            "tilgang", "autorisasjon", "jwt", "claims", "scope", "sertifikat",
            "certificate", "encrypt", "krypter",
        ]),
        new("arch-integrations", "External Integrations",
        [
            "birk", "dsam", "freg", "external", "ekstern", "third-party", "tredjepart",
            "integrasjon", "import", "eksport", "adapter", "webhook",
        ]),
        new("arch-services", "Services & Components",
        [
            "service", "tjeneste", "module", "modul", "component", "komponent",
            "adapter", "gateway", "middleware", "provider", "worker", "job",
        ]),
        new("arch-observability", "Observability & Operations",
        [
            "nfr", "monitor", "logging", "metric", "trace", "alert", "dashboard",
            "performance", "ytelse", "availability", "tilgjengelighet", "skalering",
            "scale", "drift", "overvåk", "feil", "error handling", "retry",
        ]),
    ];

    public static IReadOnlyList<CapabilityGroup> Map(IReadOnlyList<ExtractionCandidate> candidates)
    {
        var buckets = new Dictionary<string, List<ExtractionCandidate>>(Concerns.Length, StringComparer.Ordinal);
        foreach (var c in Concerns)
            buckets[c.Key] = [];

        var unmapped = new List<ExtractionCandidate>();

        foreach (var candidate in candidates)
        {
            var text    = $"{candidate.Title} {candidate.ContextHeading}";
            var matched = false;

            foreach (var concern in Concerns)
            {
                foreach (var kw in concern.Keywords)
                {
                    if (text.Contains(kw, StringComparison.OrdinalIgnoreCase))
                    {
                        buckets[concern.Key].Add(candidate);
                        matched = true;
                        break; // stop checking keywords for THIS concern; continue to next concern
                    }
                }
                // intentionally no outer `if (matched) break` — multi-group membership allowed
            }

            if (!matched)
                unmapped.Add(candidate);
        }

        var result = new List<CapabilityGroup>(Concerns.Length + 1);
        foreach (var concern in Concerns)
        {
            if (buckets[concern.Key].Count > 0)
                result.Add(new CapabilityGroup(concern.Key, concern.Label, "", buckets[concern.Key]));
        }

        if (unmapped.Count > 0)
            result.Add(new CapabilityGroup("unmapped", "Uncategorized", "", unmapped));

        return result;
    }
}
