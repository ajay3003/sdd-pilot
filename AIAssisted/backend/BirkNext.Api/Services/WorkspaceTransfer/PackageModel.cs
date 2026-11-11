using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BirkNext.Api.Models;
using BirkNext.Api.Services.SetupProfiles;
using BirkNext.WorkspaceTransfer;

namespace BirkNext.Api.Services.WorkspaceTransfer;

/// <summary>Object types in a .birknext package (the inventory's <c>type</c>). Adding a type is a format change: unknown types are UNSUPPORTED on import.</summary>
public static class PackageTypes
{
    public const string Snapshot = "source-snapshot";
    public const string Contract = "contract";
    public const string ParquetRules = "parquet-rule-set";
    public const string Suite = "integration-suite";
    public const string Composite = "composite-source-context";
    public const string Association = "source-association";
    public const string ConnectedSystems = "connected-systems";
    public const string Platform = "catalog-platform";
    public const string Integration = "catalog-integration";
    public const string EvidenceBinding = "evidence-binding";
    public const string ContractArtifact = "contract-artifact";
    public const string GraphQlSchema = "graphql-schema";
    public const string MessageFlows = "message-flows";
    public const string Flow = "critical-e2e-flow";
    public const string Module = "critical-e2e-module";
    public const string TestDataAsset = "test-data-asset";
    public const string TestDataValues = "test-data-values";
    public const string Profile = "environment-profile";
    public const string History = "historical-record";

    public static readonly IReadOnlySet<string> Known = new HashSet<string>(StringComparer.Ordinal)
    {
        Snapshot, Contract, ParquetRules, Suite, Composite, Association, ConnectedSystems, Platform, Integration, EvidenceBinding, ContractArtifact,
        GraphQlSchema, MessageFlows, Flow, Module, TestDataAsset, TestDataValues, Profile, History,
    };

    /// <summary>Types bound to the integration's environment: they need an environment mapping on import.</summary>
    public static readonly IReadOnlySet<string> EnvironmentScoped = new HashSet<string>(StringComparer.Ordinal)
    {
        ConnectedSystems, Platform, Integration, EvidenceBinding, ContractArtifact, GraphQlSchema, MessageFlows, Flow, Module, Profile,
    };

    /// <summary>Apply order: what others depend on first.</summary>
    public static int Order(string type) => type switch
    {
        Snapshot => 0, Association => 1, Contract => 2, ParquetRules => 3, Suite => 4, Composite => 5, Platform => 6, Integration => 7, EvidenceBinding => 8,
        ContractArtifact => 9, GraphQlSchema => 10, MessageFlows => 11, TestDataAsset => 12, TestDataValues => 13, ConnectedSystems => 14, Module => 15, Flow => 16,
        Profile => 17, History => 18, _ => 99,
    };

    public static string Folder(string type) => type switch
    {
        Snapshot => "sources", Contract => "contracts", ParquetRules => "test-definitions", Suite => "integration-suites", Composite => "topology",
        Association => "reviews", ConnectedSystems => "connected-systems", Platform or Integration or EvidenceBinding or MessageFlows => "connected-systems",
        ContractArtifact or GraphQlSchema => "schemas", Flow or Module => "browser-e2e", TestDataAsset or TestDataValues => "test-data", Profile => "environments",
        History => "evidence", _ => "artifacts",
    };
}

// Payloads: the owners' own records (EF entities / store records), so nothing is re-modelled and nothing is lost in translation.
public sealed record SnapshotPayload(IqrSourceSnapshotRecord Record, SourceSchemaArtifactRecord? Schema, List<SourceContractDocumentRecord> Documents);
public sealed record ContractPayload(ApiContractRow Contract, List<ApiContractVersionRow> Versions, List<ContractTestRow> Tests, List<ContractTestRevisionRow> TestRevisions,
    List<ContractEndpointBindingRow> Bindings);
public sealed record ConnectedSystemsPayload(string SetupPackageJson, IReadOnlyList<string> Systems, IReadOnlyList<string> Excluded);
public sealed record ModulePayload(string EnvironmentId, BirkNext.CriticalE2E.CriticalE2ERegisteredModule Module);
public sealed record ProfilePayload(string ProfileJson, IReadOnlyList<string> RemovedKeys);
public sealed record HistoryPayload(string Kind, string OriginalId, string? OriginalProject, string? OriginalEnvironmentId, DateTimeOffset? OriginalTimestamp, JsonElement Document);

/// <summary>An object with its payload while a package is being built or read.</summary>
public sealed record PackageEntry(PortableObject Object, JsonNode Payload);

/// <summary>
/// Scans package content for values that must never travel. It reuses the setup-package rules (<see cref="SetupProfileSchema"/>):
/// <list type="bullet">
/// <item><b>Blocking</b> — JWTs, PEM blocks, credential assignments (password=…, AccountKey=…), credentials in URLs, and secret-named fields holding a
/// value.</item>
/// <item><b>Sensitive</b> (acknowledgement required) — 11-digit identifier-shaped numbers.</item>
/// </list>
/// Values are never echoed: a finding names the location and the rule only.
/// </summary>
public static partial class PackageSecretScanner
{
    public const string Blocking = "BLOCKING";
    public const string Sensitive = "SENSITIVE";
    private static readonly string[] SecretNames = ["password", "passwd", "clientsecret", "secret", "accesstoken", "refreshtoken", "bearertoken", "privatekey", "apikey", "connectionstring", "pat", "protectedpat", "sessioncookie", "cookie"];

    [GeneratedRegex(@"(?<!\d)\d{11}(?!\d)")] private static partial Regex ElevenDigits();

    public static List<SecurityFinding> Scan(string location, JsonNode? node)
    {
        var findings = new List<SecurityFinding>();
        Walk(location, node, null, findings);
        return findings;
    }

    private static void Walk(string path, JsonNode? node, string? key, List<SecurityFinding> findings)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var (k, v) in o) Walk($"{path}.{k}", v, k, findings);
                break;
            case JsonArray a:
                for (var i = 0; i < a.Count; i++) Walk($"{path}[{i}]", a[i], key, findings);
                break;
            case JsonValue v when v.TryGetValue<string>(out var text) && text.Length > 0:
                // Nested JSON documents stored as text (EvidenceJson, DocumentJson…) are scanned as JSON too.
                if (text.Length > 1 && (text[0] == '{' || text[0] == '[') && TryParse(text) is { } inner) { Walk(path, inner, key, findings); break; }
                if (key is not null && SecretNames.Contains(SetupProfileSchema.Normalize(key)) && !IsPlaceholder(text))
                    findings.Add(new(Blocking, path, $"A field named \"{key}\" holds a value; secrets never travel in packages."));
                else if (SetupProfileSchema.ValueIssue(new SetupNode { Path = path, Scalar = text }) is { } issue)
                    findings.Add(issue.Message.Contains("personal identifier", StringComparison.Ordinal)
                        ? new(Sensitive, path, "An 11-digit identifier-shaped value (possible national identity number).")
                        : new(Blocking, path, "A value that looks like a token, key, certificate or credential."));
                break;
        }
    }

    private static bool IsPlaceholder(string text) => text is "***" or "<redacted>" or "[redacted]" || text.StartsWith("[redacted", StringComparison.Ordinal) || bool.TryParse(text, out _);

    private static JsonNode? TryParse(string text)
    {
        try { return JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { MaxDepth = 64 }); }
        catch (JsonException) { return null; }
    }

    /// <summary>Deterministic redaction for diagnostic packages: identifier-shaped numbers become a fixed marker (same input → same output).</summary>
    public static string RedactIdentifiers(string text) => ElevenDigits().Replace(text, m => "[redacted-id-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(m.Value)))[..6].ToLowerInvariant() + "]");

    /// <summary>Redacts every string value of a JSON tree (never the serialized text: its escapes, e.g. ", would hide or shift digits).</summary>
    public static JsonNode? RedactIdentifiers(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var key in o.Select(kv => kv.Key).ToList()) o[key] = RedactIdentifiers(o[key]?.DeepClone());
                return o;
            case JsonArray a:
                for (var i = 0; i < a.Count; i++) a[i] = RedactIdentifiers(a[i]?.DeepClone());
                return a;
            case JsonValue v when v.TryGetValue<string>(out var text):
                return JsonValue.Create(RedactIdentifiers(text));
            default:
                return node;
        }
    }
}

public static class PackageIds
{
    /// <summary>A safe, unique entry name for a logical id (readable prefix + hash).</summary>
    public static string EntryName(string type, string logicalId)
    {
        var readable = new string(logicalId.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        if (readable.Length > 60) readable = readable[..60].Trim('-');
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(type + "\n" + logicalId)))[..10].ToLowerInvariant();
        return $"{PackageTypes.Folder(type)}/{type}/{(readable.Length == 0 ? "object" : readable)}-{hash}.json";
    }

    public static string Fingerprint(JsonNode payload) => TransferJson.Sha256(payload.ToJsonString());

    public static string Key(string type, string logicalId) => $"{type}|{logicalId}";
}
