using System.Text.RegularExpressions;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>
/// Groups candidates into logical flows for the Flow View.
/// Primary signal: the spec's own ContextHeading (stripped of ID prefixes).
/// Fallback for headingless candidates: generic verb-cluster keywords.
/// Works across all spec styles — event, user-story, capability, integration, pipeline.
/// </summary>
public static class CapabilityMapper
{
    private sealed record FallbackDef(string Key, string Label, string[] Keywords);

    private static readonly FallbackDef[] FallbackFlows =
    [
        new("flow-search-query", "Search & Query",
        [
            "søk", "søke", "finn", "hent", "filter", "list", "oppslag",
            "search", "find", "query", "retrieve", "lookup", "get",
        ]),
        new("flow-create-ingest", "Create & Ingest",
        [
            "motta", "lagre", "opprett", "ny ", "innhenting", "registrer",
            "create", "add", "insert", "store", "save", "ingest", "register", "receive",
        ]),
        new("flow-update-modify", "Update & Modify",
        [
            "oppdater", "endre", "modifiser",
            "update", "modify", "edit", "change", "patch", "alter",
        ]),
        new("flow-delete-archive", "Delete & Archive",
        [
            "slett", "arkiver", "fjern",
            "delete", "remove", "archive", "purge", "deactivate",
        ]),
        new("flow-auth-access", "Authenticate & Authorize",
        [
            "auth", "login", "tilgang", "rettighet", "oauth", "oidc", "token", "rolle",
            "authorization", "permission", "access control", "authenticate",
        ]),
        new("flow-sync-integrate", "Sync & Integrate",
        [
            "sync", "integrasjon", "integrer", "import", "eksport", "kobling",
            "synchronize", "integrate", "publish", "subscribe", "replicate",
        ]),
        new("flow-notify-publish", "Notify & Publish",
        [
            "varsle", "publiser", "send", "hendelse", "melding",
            "notify", "publish", "event", "emit", "dispatch", "broadcast",
        ]),
        new("flow-monitor-audit", "Monitor & Audit",
        [
            "overvåk", "audit", "logg", "historikk", "revisjon",
            "monitor", "track", "trace", "audit log", "history",
        ]),
    ];

    private static readonly Regex IdPrefixRe = new(
        @"^(FR|NFR|REQ|US|UC|SC|TS|TC|AC|F|E)-?\s*\d+[\s.:–\-–—]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex NumPrefixRe = new(
        @"^\d+[\s.:]+",
        RegexOptions.Compiled);

    public static IReadOnlyList<CapabilityGroup> Map(IReadOnlyList<ExtractionCandidate> candidates)
    {
        // ── Pass 1: group by normalized ContextHeading ─────────────────────
        var headingBuckets = new Dictionary<string, List<ExtractionCandidate>>(StringComparer.OrdinalIgnoreCase);
        var headingOrder   = new List<string>();
        var headingLabels  = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var noHeading      = new List<ExtractionCandidate>();

        foreach (var candidate in candidates)
        {
            if (candidate.ContextHeading is not null)
            {
                var key   = NormalizeKey(candidate.ContextHeading);
                var label = DeriveFlowName(candidate.ContextHeading);
                if (!headingBuckets.ContainsKey(key))
                {
                    headingBuckets[key] = [];
                    headingOrder.Add(key);
                    headingLabels[key]  = label;
                }
                headingBuckets[key].Add(candidate);
            }
            else
            {
                noHeading.Add(candidate);
            }
        }

        // ── Pass 2: assign headingless candidates to fallback flows ─────────
        var fallbackBuckets = FallbackFlows.ToDictionary(
            f => f.Key, _ => new List<ExtractionCandidate>(), StringComparer.Ordinal);
        var unmapped = new List<ExtractionCandidate>();

        foreach (var candidate in noHeading)
        {
            var text    = candidate.Title;
            var matched = false;
            foreach (var fb in FallbackFlows)
            {
                foreach (var kw in fb.Keywords)
                {
                    if (text.Contains(kw, StringComparison.OrdinalIgnoreCase))
                    {
                        fallbackBuckets[fb.Key].Add(candidate);
                        matched = true;
                        break;
                    }
                }
                if (matched) break;
            }
            if (!matched)
                unmapped.Add(candidate);
        }

        // ── Build result ────────────────────────────────────────────────────
        var result = new List<CapabilityGroup>(headingOrder.Count + FallbackFlows.Length + 1);

        foreach (var key in headingOrder)
        {
            var items = headingBuckets[key];
            if (items.Count > 0)
                result.Add(new CapabilityGroup(key, headingLabels[key], "", items));
        }

        foreach (var fb in FallbackFlows)
        {
            if (fallbackBuckets[fb.Key].Count > 0)
                result.Add(new CapabilityGroup(fb.Key, fb.Label, "", fallbackBuckets[fb.Key]));
        }

        if (unmapped.Count > 0)
            result.Add(new CapabilityGroup("unmapped", "Unmapped / Needs Review", "", unmapped));

        return result;
    }

    /// <summary>Strips FR-01, US-02, "1.2 " ID prefixes to get a clean flow name.</summary>
    private static string DeriveFlowName(string heading)
    {
        var name = IdPrefixRe.Replace(heading, "");
        name     = NumPrefixRe.Replace(name, "");
        return string.IsNullOrWhiteSpace(name) ? heading : name.Trim();
    }

    /// <summary>Stable, URL-safe key for deduplication across headings.</summary>
    private static string NormalizeKey(string heading)
    {
        var lower = heading.ToLowerInvariant();
        var clean = Regex.Replace(lower, @"[^\wÀ-ɏ]+", "-");
        return clean.Trim('-');
    }
}
