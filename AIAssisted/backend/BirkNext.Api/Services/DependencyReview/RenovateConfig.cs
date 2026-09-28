using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BirkNext.Dependencies;

namespace BirkNext.Api.Services.DependencyReview;

/// <summary>
/// Renovate repository configuration: discovery in Renovate's own config-file order (the first existing file is the one Renovate reads — not an
/// arbitrary pick), lenient JSON parsing (comments and trailing commas; other JSON5 syntax is reported as not supported, not as invalid), secret
/// redaction, and a check of keys against the subset BirkNext evaluates. Validity against Renovate's full schema is not claimed here.
/// </summary>
public static class RenovateConfig
{
    /// <summary>Renovate's repository config file names, in the order Renovate looks for them.</summary>
    public static readonly string[] FileOrder =
    [
        "renovate.json", "renovate.json5", ".github/renovate.json", ".github/renovate.json5", ".gitlab/renovate.json", ".gitlab/renovate.json5",
        ".renovaterc", ".renovaterc.json", ".renovaterc.json5", "package.json",
    ];

    public static bool IsConfigCandidate(string path) => FileOrder.Contains(path, StringComparer.OrdinalIgnoreCase)
        || System.IO.Path.GetFileName(path).StartsWith("renovate", StringComparison.OrdinalIgnoreCase) && (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".json5", StringComparison.OrdinalIgnoreCase));

    private static readonly Regex SecretKey = new("(password|token|secret|encrypted|authorization|apikey|api_key|privatekey|private_key|credential)", RegexOptions.IgnoreCase);
    private static readonly Regex SecretValue = new("(bearer\\s+\\S+|ghp_\\w+|glpat-\\S+|-----BEGIN)", RegexOptions.IgnoreCase);
    public const string Redacted = "[redacted]";

    /// <summary>Keys BirkNext evaluates. Other keys are listed as "not evaluated", never as invalid.</summary>
    public static readonly HashSet<string> SupportedTopLevel = new(StringComparer.Ordinal)
    {
        "$schema", "extends", "enabled", "enabledManagers", "ignoreDeps", "ignorePaths", "packageRules", "automerge", "automergeType", "platformAutomerge",
        "schedule", "timezone", "rangeStrategy", "dependencyDashboard", "dependencyDashboardApproval", "labels", "addLabels", "groupName", "groupSlug",
        "minimumReleaseAge", "vulnerabilityAlerts", "osvVulnerabilityAlerts", "hostRules", "registryUrls", "ignoreUnstable", "prCreation", "separateMajorMinor",
        "nuget", "dockerfile", "docker-compose", "azure-pipelines", "github-actions", "npm", "customManagers", "regexManagers",
        // Presentation-only settings (they do not change which updates are allowed): recognised, not evaluated.
        "semanticCommits", "recreateWhen", "fetchChangeLogs", "commitMessageTopic", "branchPrefix", "prConcurrentLimit", "prHourlyLimit", "prBodyNotes",
        "azureWorkItemId", "commitMessagePrefix", "commitMessageAction", "prTitle", "reviewers", "assignees", "description",
    };

    public static readonly HashSet<string> SupportedMatchers = new(StringComparer.Ordinal)
    {
        "matchManagers", "matchDatasources", "matchPackageNames", "matchDepNames", "matchFileNames", "matchUpdateTypes", "matchDepTypes", "matchCategories",
        "matchPackagePatterns", "matchPackagePrefixes", "excludePackageNames", "excludePackagePatterns", "excludePackagePrefixes", "matchPaths",
    };

    /// <summary>Matchers Renovate deprecated/migrated (they still act through migration, so BirkNext evaluates them and flags them).</summary>
    public static readonly HashSet<string> DeprecatedMatchers = new(StringComparer.Ordinal)
    {
        "matchPackagePatterns", "matchPackagePrefixes", "excludePackageNames", "excludePackagePatterns", "excludePackagePrefixes", "matchPaths",
    };

    public static readonly HashSet<string> PolicySettings = new(StringComparer.Ordinal)
    {
        "enabled", "automerge", "automergeType", "platformAutomerge", "groupName", "groupSlug", "schedule", "allowedVersions", "rangeStrategy",
        "dependencyDashboardApproval", "labels", "addLabels", "minimumReleaseAge", "ignoreUnstable", "prCreation", "versioning", "commitMessageTopic", "prBodyNotes", "description",
    };

    public sealed record Parsed(JsonObject? Root, string? Error, bool UnsupportedSyntax);

    /// <summary>JSON with comments/trailing commas; package.json uses its "renovate" key.</summary>
    public static Parsed Parse(string path, string content)
    {
        try
        {
            var node = JsonNode.Parse(content, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (path.EndsWith("package.json", StringComparison.OrdinalIgnoreCase))
                return node?["renovate"] is JsonObject renovate ? new(renovate, null, false) : new(null, "package.json has no \"renovate\" key.", false);
            return node is JsonObject root ? new(root, null, false) : new(null, "The configuration is not a JSON object.", false);
        }
        catch (JsonException ex)
        {
            var json5 = path.EndsWith(".json5", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".renovaterc", StringComparison.OrdinalIgnoreCase);
            return new(null, json5 ? $"JSON5 syntax beyond comments and trailing commas is not supported by BirkNext ({ex.Message.Split('.')[0]})." : $"Invalid JSON: {ex.Message.Split(". Path")[0]}", json5);
        }
    }

    /// <summary>A deep copy with every secret-looking key or value replaced; hostRules keep only hostType/matchHost.</summary>
    public static JsonNode? Redact(JsonNode? node, string? key = null)
    {
        if (key is not null && SecretKey.IsMatch(key)) return JsonValue.Create(Redacted);
        switch (node)
        {
            case JsonObject obj:
            {
                var copy = new JsonObject();
                foreach (var (k, v) in obj) copy[k] = Redact(v, k);
                return copy;
            }
            case JsonArray rules when key == "hostRules":
                return new JsonArray(rules.Select(r => (JsonNode?)new JsonObject(
                    (r as JsonObject)?.Where(p => p.Key is "hostType" or "matchHost" or "domainName" or "hostName")
                        .Select(p => KeyValuePair.Create(p.Key, p.Value?.DeepClone())) ?? [])).ToArray());
            case JsonArray array: return new JsonArray(array.Select(i => Redact(i, key)).ToArray());
            case JsonValue value when value.TryGetValue<string>(out var s) && SecretValue.IsMatch(s): return JsonValue.Create(Redacted);
            default: return node?.DeepClone();
        }
    }

    public static string Hash(string content) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    public static List<string> Strings(JsonNode? node) => node switch
    {
        JsonArray array => array.Select(i => i is JsonValue v && v.TryGetValue<string>(out var s) ? s : i?.ToJsonString()).OfType<string>().ToList(),
        JsonValue value when value.TryGetValue<string>(out var s) => [s],
        null => [],
        _ => [node.ToJsonString()],
    };

    public static string Text(JsonNode? node) => node switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonArray a => string.Join(", ", Strings(a)),
        null => "",
        _ => node.ToJsonString(),
    };

    /// <summary>Built-in presets (config:, group:, :x, workarounds: …) are part of Renovate and cannot be expanded offline by BirkNext.</summary>
    public static bool IsBuiltInPreset(string preset) =>
        preset.StartsWith(':') || Regex.IsMatch(preset, "^(config|group|workarounds|replacements|helpers|schedule|security|monorepo|packages|preview|npm|docker|default|mergeConfidence|customManagers|abandonments|regexManagers):", RegexOptions.IgnoreCase);
}
