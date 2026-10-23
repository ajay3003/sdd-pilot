using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BirkNext.Api.Services.SourceAnalysis.Evidence;
using BirkNext.Integrations;

namespace BirkNext.Api.Services.SetupProfiles;

/// <summary>A parsed profile node (map, list or scalar) with its path — the one shape YAML and JSON both parse into.</summary>
public sealed class SetupNode
{
    public string Path { get; init; } = "";
    public string? Scalar { get; init; }
    public List<(string Key, SetupNode Value)>? Map { get; init; }
    public List<SetupNode>? Items { get; init; }
    public bool IsMap => Map is not null;
    public bool IsList => Items is not null;

    public SetupNode? this[string key] => Map?.FirstOrDefault(entry => string.Equals(entry.Key, key, StringComparison.OrdinalIgnoreCase)).Value;
    public string? Str(string key) => this[key]?.Scalar;
}

/// <summary>
/// Parses a setup profile (safe YAML subset or JSON) with size and depth limits: no custom tags, no anchor/alias expansion, no executable
/// constructs, duplicate keys reported. Content is never logged.
/// </summary>
public static class SetupProfileParser
{
    public const int MaxBytes = 64 * 1024;
    public const int MaxDepth = 12;

    public static (SetupNode? Root, SetupProfileFormat Format, List<SetupProfileIssue> Issues) Parse(string fileName, string content)
    {
        var issues = new List<SetupProfileIssue>();
        var extension = System.IO.Path.GetExtension(fileName ?? "").ToLowerInvariant();
        var format = extension == ".json" || content.TrimStart().StartsWith('{') ? SetupProfileFormat.Json : SetupProfileFormat.Yaml;
        if (extension is not (".yaml" or ".yml" or ".json" or ""))
        {
            issues.Add(new("", SetupIssueSeverity.Error, $"Only YAML (.yaml, .yml) or JSON (.json) setup profiles are accepted, not {extension}."));
            return (null, format, issues);
        }
        if (Encoding.UTF8.GetByteCount(content) > MaxBytes)
        {
            issues.Add(new("", SetupIssueSeverity.Error, $"The setup profile is larger than {MaxBytes / 1024} KB; a setup profile is a small text file."));
            return (null, format, issues);
        }
        try
        {
            var root = format == SetupProfileFormat.Json ? Json(content, issues) : Yaml(content, issues);
            if (root is not null && Depth(root) > MaxDepth) { issues.Add(new("", SetupIssueSeverity.Error, $"The profile is nested deeper than {MaxDepth} levels.")); return (null, format, issues); }
            return (root, format, issues);
        }
        catch (JsonException ex)
        {
            issues.Add(new("", SetupIssueSeverity.Error, $"The JSON is not valid: {ex.Message.Split('.')[0]} (line {(ex.LineNumber ?? 0) + 1})."));
            return (null, format, issues);
        }
    }

    private static int Depth(SetupNode node) => 1 + Math.Max(node.Map?.Select(entry => Depth(entry.Value)).DefaultIfEmpty(0).Max() ?? 0, node.Items?.Select(Depth).DefaultIfEmpty(0).Max() ?? 0);

    private static SetupNode? Yaml(string content, List<SetupProfileIssue> issues)
    {
        if (Regex.IsMatch(content, @"(^|[\s:\[,-])(!!?[A-Za-z]|&[A-Za-z0-9_]+\s|\*[A-Za-z0-9_]+(\s|$))", RegexOptions.Multiline))
        {
            issues.Add(new("", SetupIssueSeverity.Error, "YAML tags, anchors and aliases are not allowed in a setup profile."));
            return null;
        }
        // The subset reader would keep an unclosed flow collection as text; a profile must be unambiguous, so it is an error here.
        if (content.Replace("\r\n", "\n").Split('\n').FirstOrDefault(line => Regex.IsMatch(line, @"^\s*[^#""']*:\s*[\[{]") && line.Count(c => c is '[' or '{') != line.Count(c => c is ']' or '}')) is { } unclosed)
        {
            issues.Add(new("", SetupIssueSeverity.Error, $"The YAML is not valid: an inline list or map is not closed (\"{SetupProfileSchema.Safe(unclosed.Trim())}\")."));
            return null;
        }
        var documents = MiniYaml.Documents(content, out var partial);
        if (documents.Count != 1 || documents[0].Kind != YamlKind.Map)
        {
            issues.Add(new("", SetupIssueSeverity.Error, documents.Count == 0 ? "The YAML is empty or not valid." : documents.Count > 1 ? "A setup profile is one YAML document." : "The YAML root must be a mapping."));
            return null;
        }
        if (partial) issues.Add(new("", SetupIssueSeverity.Error, "The YAML could not be read completely (unsupported syntax or indentation)."));
        return partial ? null : FromYaml(documents[0], "", issues);
    }

    private static SetupNode FromYaml(YamlNode node, string path, List<SetupProfileIssue> issues) => node.Kind switch
    {
        YamlKind.Map => new SetupNode { Path = path, Map = Entries(node.Entries.Select(entry => (entry.Key, FromYaml(entry.Value, Join(path, entry.Key), issues))).ToList(), path, issues) },
        YamlKind.Seq => new SetupNode { Path = path, Items = [.. node.Items.Select((item, index) => FromYaml(item, $"{path}[{index}]", issues))] },
        _ => new SetupNode { Path = path, Scalar = node.Value },
    };

    private static SetupNode? Json(string content, List<SetupProfileIssue> issues)
    {
        using var document = JsonDocument.Parse(content, new JsonDocumentOptions { MaxDepth = MaxDepth + 2, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
        if (document.RootElement.ValueKind != JsonValueKind.Object) { issues.Add(new("", SetupIssueSeverity.Error, "The JSON root must be an object.")); return null; }
        return FromJson(document.RootElement, "", issues);
    }

    private static SetupNode FromJson(JsonElement element, string path, List<SetupProfileIssue> issues) => element.ValueKind switch
    {
        JsonValueKind.Object => new SetupNode { Path = path, Map = Entries(element.EnumerateObject().Select(property => (property.Name, FromJson(property.Value, Join(path, property.Name), issues))).ToList(), path, issues) },
        JsonValueKind.Array => new SetupNode { Path = path, Items = [.. element.EnumerateArray().Select((item, index) => FromJson(item, $"{path}[{index}]", issues))] },
        JsonValueKind.Null => new SetupNode { Path = path, Scalar = null },
        JsonValueKind.String => new SetupNode { Path = path, Scalar = element.GetString() },
        _ => new SetupNode { Path = path, Scalar = element.GetRawText() },
    };

    /// <summary>Duplicate keys are an error (the parser would otherwise keep one silently).</summary>
    private static List<(string, SetupNode)> Entries(List<(string Key, SetupNode Value)> entries, string path, List<SetupProfileIssue> issues)
    {
        foreach (var duplicate in entries.GroupBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
            issues.Add(new(Join(path, duplicate.Key), SetupIssueSeverity.Error, $"Duplicate property \"{duplicate.Key}\"."));
        return [.. entries.DistinctBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)];
    }

    public static string Join(string path, string key) => path.Length == 0 ? key : $"{path}.{key}";
}

/// <summary>
/// The typed, versioned profile schema (v1) and its safety rules. Unknown properties are reported; secret-like, identity-like and trust/verification
/// properties are REJECTED with their reason (never silently stripped, and their values are never echoed).
/// </summary>
public static class SetupProfileSchema
{
    public const int SupportedVersion = 1;

    private static readonly string[] SecretWords =
        ["password", "passwd", "secret", "clientsecret", "token", "accesstoken", "refreshtoken", "bearer", "privatekey", "apikey", "connectionstring", "certificate",
         "pat", "personalaccesstoken", "credential", "credentials", "sharedaccesskey", "accountkey", "sas"];
    private static readonly string[] TrustWords =
        ["trusted", "trustedexecution", "executionallowed", "activeexecution", "allowexecution", "approved", "approval", "approvedbytestlead", "testleadapproval", "attestation",
         "authorizationconfirmed", "policyconfirmed", "mappingconfirmed", "confirmed", "sourceverified", "runtimeverified", "verified", "passed", "result", "results",
         "status", "health", "healthy", "deployed", "contractcompatible", "compatible", "readiness", "ready", "executionpermission",
         // Performance: readiness, network/TLS evidence, run results and baseline promotion are evidence, never imported desired state.
         "readinesspassed", "networkready", "networkreachable", "reachable", "reachability", "tlstrusted", "tlsverified", "tlspassed", "performancepassed",
         "baselinepromoted", "promoted", "executionapproved"];
    private static readonly string[] IdentityWords = ["fodselsnummer", "fnr", "nationalid", "personnummer", "ssn", "dufnummer", "duf"];

    /// <summary>Known properties per path pattern ("journeys.*.integration" …). Anything else is reported as unsupported.</summary>
    private static readonly Dictionary<string, string[]> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        [""] = ["version", "profile", "environment", "journeys", "aliases", "metadata", "manualRequirements", "securityClassification", "performance"],
        ["profile"] = ["name", "description", "testPack", "environment", "environmentType"],
        ["environment"] = ["expectedName"],
        ["journeys.*"] = ["integration", "sourceEvidence", "contracts", "executionProvider", "altinn"],
        ["journeys.*.integration"] = ["logicalName", "type", "producer", "consumer", "endpoint", "channel", "sourceResource", "producerRole", "producerImplementation"],
        ["journeys.*.integration.producer"] = ["technology", "logicalName", "component"],
        ["journeys.*.integration.consumer"] = ["logicalName", "component"],
        ["journeys.*.integration.channel"] = ["name"],
        ["journeys.*.integration.sourceResource"] = ["database", "schema", "table"],
        ["journeys.*.sourceEvidence"] = ["logicalSource", "snapshotName", "snapshotId"],
        ["journeys.*.contracts"] = ["producer", "consumer"],
        ["journeys.*.contracts.producer"] = ["reference"],
        ["journeys.*.contracts.consumer"] = ["sourcePath", "snapshotId"],
        ["journeys.*.executionProvider"] = ["providerId", "resource"],
        ["journeys.*.altinn"] = ["environment", "appId", "baseUrl"],
        ["aliases[]"] = ["logical", "technical"],
        ["metadata"] = ["generatedBy", "generatedAt", "sanitized", "containsSecrets", "source"],
        ["securityClassification"] = ["environment", "personGraphql", "testData"],
        ["securityClassification.personGraphql"] = ["endpointRef"],
        ["performance"] = ["targetRef", "definition", "scenario", "workload", "thresholds", "resourceObservation", "localExecution"],
        ["performance.definition"] = ["name", "description"],
        ["performance.scenario"] = ["type", "name", "steps", "selection"],
        ["performance.scenario.steps[]"] = ["name", "method", "path", "expectedStatusCodes", "thinkTimeMs", "graphqlQuery", "operationRef", "testDataRef"],
        ["performance.scenario.steps[].operationRef"] = ["type", "operationId", "method", "path", "field", "contract"],
        ["performance.scenario.steps[].testDataRef"] = ["profile", "mappings", "selection"],
        ["performance.workload"] = ["purpose", "mode", "virtualUsers", "requestsPerSecond", "warmUpSeconds", "rampUpSeconds", "steadyStateSeconds", "rampDownSeconds"],
        ["performance.thresholds[]"] = ["metric", "operator", "value", "unit", "severity"],
        ["performance.resourceObservation"] = ["enabled"],
        ["performance.localExecution"] = ["caBundleRequired"],
        ["securityClassification.testData"] = ["level0ChildAlias", "level1ChildAlias", "level2ChildAlias", "level3ChildAlias", "unauthorizedIdentityAlias", "authorizedIdentityAlias"],
    };

    public static string Normalize(string key) => new([.. key.ToLowerInvariant().Where(char.IsLetterOrDigit)]);

    /// <summary>Validates structure and safety. Never mutates anything.</summary>
    public static List<SetupProfileIssue> Validate(SetupNode root)
    {
        var issues = new List<SetupProfileIssue>();
        var version = root.Str("version");
        if (version is null) issues.Add(new("version", SetupIssueSeverity.Error, "The profile has no version. Add \"version: 1\"; an unversioned profile is never accepted."));
        else if (!int.TryParse(version, out var number) || number < 1) issues.Add(new("version", SetupIssueSeverity.Error, $"\"{Safe(version)}\" is not a profile version."));
        else if (number != SupportedVersion) issues.Add(new("version", SetupIssueSeverity.Error, $"Profile version {number} is not supported by this BirkNext build (supported: {SupportedVersion})."));
        if (root["profile"] is not { IsMap: true }) issues.Add(new("profile", SetupIssueSeverity.Error, "The profile has no \"profile\" section (name, testPack, environment)."));
        Walk(root, "", issues);
        if (root["metadata"]?.Str("containsSecrets") is { } contains && contains.Equals("true", StringComparison.OrdinalIgnoreCase))
            issues.Add(new("metadata.containsSecrets", SetupIssueSeverity.Error, "A profile that declares it contains secrets cannot be imported."));
        return issues;
    }

    private static void Walk(SetupNode node, string pattern, List<SetupProfileIssue> issues)
    {
        if (node.IsList) { foreach (var item in node.Items!) Walk(item, pattern + "[]", issues); return; }
        if (!node.IsMap) { CheckValue(node, issues); return; }
        foreach (var (key, value) in node.Map!)
        {
            var path = SetupProfileParser.Join(node.Path, key);
            var normalized = Normalize(key);
            if (SecretWords.Any(word => normalized == word || normalized.EndsWith(word, StringComparison.Ordinal) && word.Length >= 5))
            { issues.Add(new(path, SetupIssueSeverity.Rejected, $"Field \"{key}\" cannot be imported because secrets are not allowed in setup profiles. It is configured in the backend secret store.")); continue; }
            // CA trust material is local execution configuration (PerformanceTests:Container:CaBundlePath); a profile may only say it is required.
            if (normalized.Contains("cabundle", StringComparison.Ordinal) && normalized != "cabundlerequired")
            { issues.Add(new(path, SetupIssueSeverity.Rejected, $"Field \"{key}\" cannot be imported: the corporate CA bundle is local execution configuration (PerformanceTests:Container:CaBundlePath). A profile may only declare caBundleRequired.")); continue; }
            if (IdentityWords.Contains(normalized))
            { issues.Add(new(path, SetupIssueSeverity.Rejected, $"Field \"{key}\" cannot be imported: personal identifiers are never part of a setup profile (use approved Test Data aliases).")); continue; }
            if (TrustWords.Contains(normalized))
            { issues.Add(new(path, SetupIssueSeverity.Rejected, $"Field \"{key}\" cannot be imported: trust, approval, confirmation and verification state is decided by people and evidence, never by a profile.")); continue; }
            // Journey ids are the only free keys; each journey is validated against the journey schema.
            if (pattern == "journeys") { Walk(value, "journeys.*", issues); continue; }
            // Test Data mappings: placeholder → central Test Data alias (free keys; values are aliases, still checked for secret/identity look).
            if (pattern == "performance.scenario.steps[].testDataRef.mappings") { Walk(value, pattern + ".*", issues); continue; }
            if (!Known.TryGetValue(pattern, out var known) || !known.Contains(key, StringComparer.OrdinalIgnoreCase))
            { issues.Add(new(path, SetupIssueSeverity.Warning, $"Unsupported property \"{key}\" is ignored.")); continue; }
            Walk(value, SetupProfileParser.Join(pattern, key), issues);
        }
    }

    private static readonly Regex Jwt = new(@"eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}", RegexOptions.CultureInvariant);
    private static readonly Regex NationalId = new(@"(?<!\d)\d{11}(?!\d)", RegexOptions.CultureInvariant);

    /// <summary>Values that look like secrets or personal identifiers are rejected whatever their key is called.</summary>
    private static void CheckValue(SetupNode node, List<SetupProfileIssue> issues)
    {
        if (node.Scalar is not { } value) return;
        if (Jwt.IsMatch(value) || value.Contains("-----BEGIN", StringComparison.Ordinal)
            || Regex.IsMatch(value, @"(?i)(password|pwd|accountkey|sharedaccesskey|client_secret)\s*=") || Regex.IsMatch(value, @"(?i)://[^/\s:@]+:[^/\s@]+@"))
            issues.Add(new(node.Path, SetupIssueSeverity.Rejected, "This value looks like a secret (token, key, certificate or credential) and cannot be imported."));
        else if (NationalId.IsMatch(value))
            issues.Add(new(node.Path, SetupIssueSeverity.Rejected, "This value looks like a personal identifier (11 digits) and cannot be imported."));
    }

    /// <summary>Is a path (or one of its ancestors) rejected or unsupported? Such values never reach the change plan.</summary>
    public static bool Usable(string path, IReadOnlyList<SetupProfileIssue> issues) =>
        !issues.Any(issue => issue.Severity != SetupIssueSeverity.Error && issue.Path.Length > 0 && (path == issue.Path || path.StartsWith(issue.Path + ".", StringComparison.Ordinal)));

    /// <summary>Never echo a rejected value; for a harmless one, a bounded preview.</summary>
    public static string Safe(string value) => value.Length > 40 ? value[..40] + "…" : value;
}
