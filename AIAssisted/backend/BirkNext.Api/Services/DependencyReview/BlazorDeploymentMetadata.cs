using System.Text.Json;
using System.Text.RegularExpressions;

namespace BirkNext.Api.Services.DependencyReview;

/// <summary>One deployed artifact named by structured deployment metadata. Name is the logical (un-fingerprinted) file name.</summary>
public sealed record DeployedArtifact(string Name, string File, string? Integrity, string Type);

/// <summary>
/// Structured Blazor WebAssembly deployment metadata, parsed without executing or pattern-matching minified code: the classic boot manifest
/// (blazor.boot.json), the boot configuration .NET embeds in dotnet.js between explicit /*json-start*/ and /*json-end*/ markers, the PWA
/// service-worker asset manifest, and the HTML import map that resolves a fingerprinted dotnet.js. Each parser returns null when its input
/// does not have the documented shape — it never guesses.
/// </summary>
public static partial class BlazorDeploymentMetadata
{
    private static readonly string[] AssemblySections = ["assembly", "coreAssembly", "lazyAssembly"];
    private static readonly string[] RuntimeSections = ["wasmNative", "jsModuleNative", "jsModuleRuntime", "runtime"];

    /// <summary>
    /// Artifacts of a Blazor boot configuration. Supports the classic shape (resources.assembly = { "X.dll": "sha256-…" }) and the newer
    /// list shape (resources.assembly = [{ "virtualPath": "X.wasm", "name": "X.abc123.wasm", "integrity": "sha256-…" }]). Null when the JSON
    /// has no resources object; an empty list when it has one without artifacts.
    /// </summary>
    public static List<DeployedArtifact>? BootConfigArtifacts(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !TryGet(doc.RootElement, "resources", out var resources) || resources.ValueKind != JsonValueKind.Object) return null;
            var artifacts = new List<DeployedArtifact>();
            foreach (var section in AssemblySections) Read(resources, section, "assembly", artifacts);
            foreach (var section in RuntimeSections) Read(resources, section, "runtime", artifacts);
            return [.. artifacts.DistinctBy(a => a.Name, StringComparer.OrdinalIgnoreCase).OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)];
        }
        catch (JsonException) { return null; }
    }

    private static void Read(JsonElement resources, string section, string type, List<DeployedArtifact> into)
    {
        if (!TryGet(resources, section, out var value)) return;
        if (value.ValueKind == JsonValueKind.Object)
            foreach (var p in value.EnumerateObject())
                into.Add(new(p.Name, p.Name, p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : null, type));
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.Object))
            {
                var file = String(item, "name");
                var logical = String(item, "virtualPath") ?? file;
                if (logical is null) continue;
                into.Add(new(logical, file ?? logical, String(item, "integrity"), type));
            }
    }

    /// <summary>The boot configuration .NET embeds in dotnet.js, only when delimited by the explicit json-start/json-end markers.</summary>
    public static string? EmbeddedBootConfig(string dotnetJs)
    {
        const string start = "/*json-start*/", end = "/*json-end*/";
        var s = dotnetJs.IndexOf(start, StringComparison.Ordinal);
        var e = s < 0 ? -1 : dotnetJs.IndexOf(end, s + start.Length, StringComparison.Ordinal);
        return s < 0 || e < 0 ? null : dotnetJs[(s + start.Length)..e].Trim();
    }

    /// <summary>
    /// _framework artifacts of a Blazor PWA service-worker asset manifest (self.assetsManifest = { "assets": [{ "hash": "sha256-…", "url":
    /// "_framework/X.wasm" }], "version": "…" }). Null when the file is not that object.
    /// </summary>
    public static List<DeployedArtifact>? ServiceWorkerArtifacts(string js)
    {
        var marker = js.IndexOf("assetsManifest", StringComparison.Ordinal);
        if (marker < 0) return null;
        var open = js.IndexOf('{', marker);
        var close = js.LastIndexOf('}');
        if (open < 0 || close <= open) return null;
        try
        {
            using var doc = JsonDocument.Parse(js[open..(close + 1)], new JsonDocumentOptions { AllowTrailingCommas = true });
            if (!TryGet(doc.RootElement, "assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;
            return [.. assets.EnumerateArray().Where(a => a.ValueKind == JsonValueKind.Object)
                .Select(a => (Url: String(a, "url"), Hash: String(a, "hash")))
                .Where(a => a.Url is not null && a.Url.Contains("_framework/", StringComparison.Ordinal))
                .Select(a => new DeployedArtifact(System.IO.Path.GetFileName(a.Url!), System.IO.Path.GetFileName(a.Url!), a.Hash, Kind(a.Url!)))
                .DistinctBy(a => a.Name, StringComparer.OrdinalIgnoreCase).OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)];
        }
        catch (JsonException) { return null; }
    }

    /// <summary>The fingerprinted dotnet.js path an HTML import map resolves "_framework/dotnet.js" to, when the page has an import map.</summary>
    public static string? ImportMapDotnetJs(string html)
    {
        foreach (Match m in ImportMap().Matches(html))
        {
            try
            {
                using var doc = JsonDocument.Parse(m.Groups["json"].Value);
                if (!TryGet(doc.RootElement, "imports", out var imports) || imports.ValueKind != JsonValueKind.Object) continue;
                foreach (var p in imports.EnumerateObject())
                    if (p.Name.EndsWith("_framework/dotnet.js", StringComparison.Ordinal) && p.Value.ValueKind == JsonValueKind.String) return p.Value.GetString();
            }
            catch (JsonException) { }
        }
        return null;
    }

    /// <summary>Whether the page references the Blazor WebAssembly runtime (a positive detection, never inferred from absence).</summary>
    public static bool ReferencesBlazorRuntime(string html) => BlazorScript().IsMatch(html);

    /// <summary>The logical artifact name: a fingerprinted file (X.abc12345.wasm) has the same logical name as X.wasm.</summary>
    public static string Logical(string file) => Fingerprint().Replace(System.IO.Path.GetFileName(file), "$1$2");

    public static string Kind(string file) => System.IO.Path.GetExtension(file).ToLowerInvariant() switch
    {
        ".wasm" when !file.Contains("dotnet.native", StringComparison.OrdinalIgnoreCase) => "assembly",
        ".dll" => "assembly",
        ".pdb" => "symbols",
        ".dat" => "globalization data",
        _ => "runtime",
    };

    private static bool TryGet(JsonElement element, string name, out JsonElement value)
    {
        foreach (var p in element.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) { value = p.Value; return true; }
        value = default;
        return false;
    }

    private static string? String(JsonElement element, string name) => TryGet(element, name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    [GeneratedRegex(@"<script[^>]*type\s*=\s*[""']importmap[""'][^>]*>(?<json>.*?)</script>", RegexOptions.IgnoreCase | RegexOptions.Singleline)] private static partial Regex ImportMap();
    [GeneratedRegex(@"_framework/(blazor\.webassembly|blazor\.web|dotnet)(\.[a-z0-9]+)?\.js", RegexOptions.IgnoreCase)] private static partial Regex BlazorScript();
    [GeneratedRegex(@"^(.+?)\.[a-z0-9]{8,12}(\.(?:wasm|dll|js|dat|pdb))$", RegexOptions.IgnoreCase)] private static partial Regex Fingerprint();
}
