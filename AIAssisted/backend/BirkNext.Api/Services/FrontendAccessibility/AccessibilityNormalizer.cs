using System.Text.Json;

namespace BirkNext.Api.Services.FrontendAccessibility;

public sealed class AccessibilityNormalizer(AccessibilityEvidenceSanitizer sanitizer)
{
    public static AccessibilityFindingSeverity MapSeverity(string? impact) => impact?.ToLowerInvariant() switch
    {
        "critical" => AccessibilityFindingSeverity.Critical,
        "serious" => AccessibilityFindingSeverity.High,
        "moderate" => AccessibilityFindingSeverity.Medium,
        "minor" => AccessibilityFindingSeverity.Low,
        _ => AccessibilityFindingSeverity.Info
    };

    public List<AccessibilityFinding> Normalize(JsonElement items, AccessibilityFindingKind kind)
    {
        var findings = new List<AccessibilityFinding>();
        foreach (var item in items.EnumerateArray())
        {
            var nodes = item.TryGetProperty("nodes", out var nodesElement)
                ? nodesElement.EnumerateArray().ToList()
                : [];
            var selectors = new List<string>();
            var snippets = new List<string>();
            var summaries = new List<string>();
            foreach (var node in nodes.Take(AccessibilityEvidenceSanitizer.MaxNodesPerRule))
            {
                if (node.TryGetProperty("target", out var targets))
                    selectors.AddRange(targets.EnumerateArray().Select(t => sanitizer.SanitizeSelector(t.ToString())));
                if (node.TryGetProperty("html", out var html)) snippets.Add(sanitizer.SanitizeHtml(html.GetString()));
                if (node.TryGetProperty("failureSummary", out var summary)) summaries.Add(sanitizer.SanitizeSummary(summary.GetString()));
            }

            var ruleId = item.GetProperty("id").GetString() ?? "unknown";
            findings.Add(new AccessibilityFinding(
                ruleId,
                kind,
                MapSeverity(item.TryGetProperty("impact", out var impact) ? impact.GetString() : null),
                item.TryGetProperty("impact", out impact) ? impact.GetString() : null,
                item.TryGetProperty("help", out var help) ? help.GetString() ?? ruleId : ruleId,
                item.TryGetProperty("description", out var description) ? description.GetString() ?? string.Empty : string.Empty,
                item.TryGetProperty("tags", out var tags) ? tags.EnumerateArray().Select(t => t.GetString() ?? "").Where(t => t.Length > 0).ToList() : [],
                nodes.Count,
                selectors.Distinct().ToList(),
                snippets.Distinct().ToList(),
                summaries.Distinct().ToList(),
                item.TryGetProperty("helpUrl", out var helpUrl) ? helpUrl.GetString() : null,
                kind == AccessibilityFindingKind.Violation
                    ? $"Fix elements affected by axe rule '{ruleId}' and retest."
                    : $"Manually review elements identified by axe rule '{ruleId}'."));
        }
        return findings;
    }
}
