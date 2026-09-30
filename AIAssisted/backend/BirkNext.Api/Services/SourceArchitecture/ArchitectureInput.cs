using System.Text.Json;
using System.Text.RegularExpressions;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Integrations;
using BirkNext.SourceArchitecture;

namespace BirkNext.Api.Services.SourceArchitecture;

/// <summary>One .NET project of the snapshot, with the files and configuration that belong to it (longest directory prefix wins).</summary>
internal sealed class ArchProject
{
    public string Path { get; init; } = "";
    public string Directory { get; init; } = "";
    public string Name { get; init; } = "";
    public string Module { get; init; } = "";
    public string Sdk { get; init; } = "";
    public string? OutputType { get; init; }
    public string Framework { get; init; } = "";
    public List<string> Packages { get; init; } = [];
    public List<string> ProjectReferences { get; init; } = [];
    public bool IsTest { get; init; }
    public bool IsAspireHost { get; init; }
    public List<CodeFile> Code { get; } = [];
    public List<ConfigFile> Configuration { get; } = [];
    public bool HasPackage(string prefix) => Packages.Any(p => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
}

/// <summary>A C# file with comments blanked out (positions and line numbers preserved), so commented-out code is never evidence.</summary>
internal sealed class CodeFile(string path, string content)
{
    public string Path { get; } = path;
    public string Text { get; } = ArchitectureText.StripComments(content);
    private int[]? _lines;
    public int Line(int index)
    {
        _lines ??= Text.Select((c, i) => (c, i)).Where(x => x.c == '\n').Select(x => x.i).ToArray();
        var pos = Array.BinarySearch(_lines, index);
        return (pos < 0 ? ~pos : pos) + 1;
    }
}

/// <summary>A flattened JSON configuration file (keys "A:B:0:C"). Values are held only in memory and only safe entity names ever leave the analyzer.</summary>
internal sealed record ConfigFile(string Path, string Environment, Dictionary<string, string> Values);

internal sealed record AspireResource(string Variable, string Method, string Name, string? ProjectType, string? Parent);

internal sealed record AspireEnvironment(string Key, string? EndpointOf, string? ResourceValue);

internal sealed record AspireProjectWiring(AspireResource Resource, List<string> References, List<AspireEnvironment> Environment, List<string> WaitFor, string File, int Line);

internal sealed record ComposeService(string Name, string File, int Line, string? BuildContext, string? Image, List<string> DependsOn, Dictionary<string, string> Environment);

/// <summary>Everything the extractors read: projects, their closure, configuration, development orchestration and container composition.</summary>
internal sealed class ArchitectureInput
{
    public Guid SourceSnapshotId { get; init; }
    public string Fingerprint { get; init; } = "";
    public List<ArchProject> Projects { get; init; } = [];
    public Dictionary<string, ArchProject> ByPath { get; init; } = [];
    public List<AspireProjectWiring> Aspire { get; init; } = [];
    public List<AspireResource> AspireResources { get; init; } = [];
    public List<ComposeService> Compose { get; init; } = [];
    public List<(string Path, string Text)> Dockerfiles { get; init; } = [];
    public List<string> Limitations { get; init; } = [];

    private readonly Dictionary<string, List<ArchProject>> _closures = [];

    /// <summary>The project and every project it references, transitively (tests excluded).</summary>
    public List<ArchProject> Closure(ArchProject project)
    {
        if (_closures.TryGetValue(project.Path, out var cached)) return cached;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<ArchProject>();
        var stack = new Stack<ArchProject>([project]);
        while (stack.Count > 0)
        {
            var p = stack.Pop();
            if (!seen.Add(p.Path)) continue;
            result.Add(p);
            foreach (var r in p.ProjectReferences) if (ByPath.TryGetValue(r, out var rp) && !rp.IsTest) stack.Push(rp);
        }
        return _closures[project.Path] = result;
    }

    public static ArchitectureInput From(Guid sourceSnapshotId, IqrSourceArchiveReader.Workspace workspace)
    {
        var limitations = new List<string>(workspace.Limitations);
        var projects = new List<ArchProject>();
        foreach (var file in workspace.Files.Where(f => f.Path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)).OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            var text = file.Content;
            var dir = file.Path.Contains('/') ? file.Path[..(file.Path.LastIndexOf('/') + 1)] : "";
            var name = System.IO.Path.GetFileNameWithoutExtension(file.Path);
            var packages = Regex.Matches(text, @"<PackageReference\s+Include=""([^""]+)""", RegexOptions.IgnoreCase).Select(m => m.Groups[1].Value).ToList();
            var refs = Regex.Matches(text, @"<ProjectReference\s+Include=""([^""]+)""", RegexOptions.IgnoreCase).Select(m => Normalize(dir + m.Groups[1].Value.Replace('\\', '/'))).ToList();
            var sdk = Regex.Match(text, @"<Project\s+Sdk=""([^""/]+)").Groups[1].Value;
            var isTest = packages.Any(p => p.StartsWith("Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase) || p.StartsWith("xunit", StringComparison.OrdinalIgnoreCase)
                    || p.StartsWith("NUnit", StringComparison.OrdinalIgnoreCase) || p.StartsWith("MSTest", StringComparison.OrdinalIgnoreCase))
                || Regex.IsMatch(text, @"<IsTestProject>\s*true", RegexOptions.IgnoreCase)
                || Regex.IsMatch(name, @"(^|[.\-])(Tests?|UnitTests|IntegrationTests|Specs)$", RegexOptions.IgnoreCase);
            projects.Add(new ArchProject
            {
                Path = file.Path, Directory = dir, Name = name, Module = file.Path.Contains('/') ? file.Path[..file.Path.IndexOf('/')] : "",
                Sdk = sdk, OutputType = Regex.Match(text, @"<OutputType>\s*([^<\s]+)").Groups[1].Value is { Length: > 0 } o ? o : null,
                Framework = Regex.Match(text, @"<TargetFrameworks?>\s*([^<]+)").Groups[1].Value.Trim(), Packages = packages, ProjectReferences = refs, IsTest = isTest,
                IsAspireHost = sdk.StartsWith("Aspire.AppHost", StringComparison.OrdinalIgnoreCase) || Regex.IsMatch(text, @"<IsAspireHost>\s*true", RegexOptions.IgnoreCase),
            });
        }
        ArchProject? Owner(string path) => projects.Where(p => path.StartsWith(p.Directory, StringComparison.Ordinal)).OrderByDescending(p => p.Directory.Length).FirstOrDefault();
        var configurationFiles = workspace.ConfigurationFiles ?? [];
        foreach (var file in workspace.Files.Concat(configurationFiles))
        {
            if (Owner(file.Path) is not { } owner) continue;
            if (file.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && !file.Path.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase)
                && !file.Path.Contains("/Migrations/", StringComparison.OrdinalIgnoreCase))
                owner.Code.Add(new CodeFile(file.Path, file.Content));
            else if (System.IO.Path.GetFileName(file.Path) is var fn && fn.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                && (fn.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase) || fn.Equals(".graphqlrc.json", StringComparison.OrdinalIgnoreCase)))
            {
                var values = FlattenJson(file.Content);
                if (values is null) { limitations.Add($"Configuration file could not be parsed: {ArchitectureText.Safe(file.Path)}"); continue; }
                var env = Regex.Match(fn, @"^appsettings\.(.+)\.json$", RegexOptions.IgnoreCase) is { Success: true } m ? m.Groups[1].Value : "";
                owner.Configuration.Add(new ConfigFile(file.Path, env, values));
            }
        }
        var input = new ArchitectureInput
        {
            SourceSnapshotId = sourceSnapshotId, Fingerprint = workspace.Archive.Sha256, Projects = projects,
            ByPath = projects.GroupBy(p => p.Path).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal),
            Dockerfiles = workspace.Files.Where(f => System.IO.Path.GetFileName(f.Path).Equals("Dockerfile", StringComparison.OrdinalIgnoreCase)).Select(f => (f.Path, f.Content)).ToList(),
            Limitations = limitations,
        };
        var (resources, wiring) = ParseAspire(projects.Where(p => p.IsAspireHost));
        input.AspireResources.AddRange(resources);
        input.Aspire.AddRange(wiring);
        foreach (var compose in workspace.Files.Concat(configurationFiles).Where(f => Regex.IsMatch(System.IO.Path.GetFileName(f.Path), @"^(docker-)?compose(\.[\w-]+)?\.ya?ml$", RegexOptions.IgnoreCase)))
        {
            var parsed = ParseCompose(compose.Path, compose.Content);
            if (parsed is null) limitations.Add($"Compose file could not be parsed: {ArchitectureText.Safe(compose.Path)}");
            else input.Compose.AddRange(parsed);
        }
        return input;
    }

    internal static string Normalize(string path)
    {
        var parts = new List<string>();
        foreach (var part in path.Split('/'))
        {
            if (part is "" or ".") continue;
            if (part == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); continue; }
            parts.Add(part);
        }
        return string.Join('/', parts);
    }

    internal static Dictionary<string, string>? FlattenJson(string content)
    {
        try
        {
            using var doc = JsonDocument.Parse(content, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            void Walk(JsonElement e, string prefix)
            {
                switch (e.ValueKind)
                {
                    case JsonValueKind.Object: foreach (var p in e.EnumerateObject()) Walk(p.Value, prefix.Length == 0 ? p.Name : $"{prefix}:{p.Name}"); break;
                    case JsonValueKind.Array: var i = 0; foreach (var item in e.EnumerateArray()) Walk(item, $"{prefix}:{i++}"); break;
                    case JsonValueKind.String: values[prefix] = e.GetString() ?? ""; break;
                    case JsonValueKind.Null: values[prefix] = ""; break;
                    default: values[prefix] = e.GetRawText(); break;
                }
            }
            Walk(doc.RootElement, "");
            return values;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// Development orchestration (.NET Aspire AppHost): resources and, per project resource, its WithReference / WithEnvironment / WaitFor wiring.
    /// Local orchestration evidence only — never deployed topology.
    /// </summary>
    private static (List<AspireResource>, List<AspireProjectWiring>) ParseAspire(IEnumerable<ArchProject> hosts)
    {
        var resources = new List<AspireResource>();
        var wiring = new List<AspireProjectWiring>();
        foreach (var file in hosts.SelectMany(h => h.Code))
        {
            var text = file.Text;
            foreach (Match m in Regex.Matches(text, @"var\s+(\w+)\s*=\s*(?:\w+\()?\s*(\w+)\s*\.\s*(Add\w+)\s*(?:<\s*Projects\.(\w+)\s*>)?\s*\(\s*""([^""]+)"""))
                resources.Add(new AspireResource(m.Groups[1].Value, m.Groups[3].Value, m.Groups[5].Value, m.Groups[4].Success ? m.Groups[4].Value : null,
                    m.Groups[2].Value is "builder" ? null : m.Groups[2].Value));
            foreach (var resource in resources.Where(r => r.ProjectType is not null))
            {
                var start = Regex.Match(text, $@"var\s+{Regex.Escape(resource.Variable)}\s*=");
                if (!start.Success) continue;
                var statement = ArchitectureText.Statement(text, start.Index);
                wiring.Add(new AspireProjectWiring(resource,
                    Regex.Matches(statement, @"\.WithReference\(\s*(\w+)").Select(x => x.Groups[1].Value).ToList(),
                    Regex.Matches(statement, @"\.WithEnvironment\(\s*""([^""]+)""\s*,\s*([^)]*\)?)\s*\)").Select(x =>
                    {
                        var value = x.Groups[2].Value.Trim();
                        var endpoint = Regex.Match(value, @"^(\w+)\s*\.\s*GetEndpoint\(");
                        return new AspireEnvironment(x.Groups[1].Value.Replace("__", ":"), endpoint.Success ? endpoint.Groups[1].Value : null,
                            Regex.IsMatch(value, @"^\w+$") && !value.StartsWith('"') ? value : null);
                    }).ToList(),
                    Regex.Matches(statement, @"\.WaitFor\(\s*(\w+)").Select(x => x.Groups[1].Value).ToList(), file.Path, file.Line(start.Index)));
            }
        }
        return (resources, wiring);
    }

    /// <summary>A small, tolerant reader for the compose subset that matters here (services, build, image, depends_on, environment).</summary>
    internal static List<ComposeService>? ParseCompose(string path, string content)
    {
        var services = new List<ComposeService>();
        var lines = content.Replace("\r", "").Split('\n');
        var inServices = false; int serviceIndent = -1;
        string? current = null; int currentLine = 0; string? section = null; int sectionIndent = -1;
        string? build = null, image = null; var depends = new List<string>(); var env = new Dictionary<string, string>(StringComparer.Ordinal);
        void Flush() { if (current is not null) services.Add(new ComposeService(current, path, currentLine, build, image, [.. depends], new(env))); build = image = null; depends.Clear(); env.Clear(); section = null; }
        for (var i = 0; i < lines.Length; i++)
        {
            var raw = lines[i];
            if (raw.TrimStart().StartsWith('#') || raw.Trim().Length == 0) continue;
            var indent = raw.Length - raw.TrimStart().Length;
            var line = raw.Trim();
            if (indent == 0) { Flush(); current = null; inServices = line.StartsWith("services:", StringComparison.Ordinal); serviceIndent = -1; continue; }
            if (!inServices) continue;
            if (serviceIndent < 0) serviceIndent = indent;
            if (indent == serviceIndent && line.EndsWith(':')) { Flush(); current = line.TrimEnd(':').Trim('"', '\''); currentLine = i + 1; continue; }
            if (current is null) continue;
            if (indent == serviceIndent + 2 || (section is null && indent > serviceIndent))
            {
                var kv = line.Split(':', 2);
                var key = kv[0].Trim();
                var value = kv.Length > 1 ? kv[1].Trim().Trim('"', '\'') : "";
                section = null;
                if (key == "image") image = value;
                else if (key == "build") { if (value.Length > 0) build = value; else { section = "build"; sectionIndent = indent; } }
                else if (key is "depends_on" or "environment") { section = key; sectionIndent = indent; }
                continue;
            }
            if (section is not null && indent > sectionIndent)
            {
                if (section == "build" && line.StartsWith("context:", StringComparison.Ordinal)) build = line["context:".Length..].Trim().Trim('"', '\'');
                else if (section == "depends_on") depends.Add(line.TrimStart('-', ' ').TrimEnd(':').Trim('"', '\''));
                else if (section == "environment")
                {
                    var item = line.TrimStart('-', ' ').Trim('"', '\'');
                    var sep = item.IndexOfAny(['=', ':']);
                    if (sep > 0) env[item[..sep].Trim()] = item[(sep + 1)..].Trim().Trim('"', '\'');
                }
            }
        }
        Flush();
        return services;
    }
}

internal static class ArchitectureText
{
    /// <summary>Blanks // and /* */ comments while keeping every character position and newline, and leaving string contents intact.</summary>
    public static string StripComments(string text)
    {
        var chars = text.ToCharArray();
        var inString = false; var verbatim = false; var inChar = false;
        for (var i = 0; i < chars.Length; i++)
        {
            var c = chars[i];
            if (inString)
            {
                if (verbatim) { if (c == '"') { if (i + 1 < chars.Length && chars[i + 1] == '"') i++; else inString = false; } }
                else if (c == '\\') i++;
                else if (c == '"' || c == '\n') inString = false;
                continue;
            }
            if (inChar) { if (c == '\\') i++; else if (c == '\'' || c == '\n') inChar = false; continue; }
            if (c == '"') { inString = true; verbatim = i > 0 && (chars[i - 1] == '@' || (chars[i - 1] == '$' && i > 1 && chars[i - 2] == '@')); continue; }
            if (c == '\'') { inChar = true; continue; }
            if (c == '/' && i + 1 < chars.Length && chars[i + 1] == '/')
            {
                while (i < chars.Length && chars[i] != '\n') chars[i++] = ' ';
                i--;
            }
            else if (c == '/' && i + 1 < chars.Length && chars[i + 1] == '*')
            {
                while (i < chars.Length && !(chars[i] == '*' && i + 1 < chars.Length && chars[i + 1] == '/')) { if (chars[i] != '\n') chars[i] = ' '; i++; }
                if (i < chars.Length) { chars[i] = ' '; if (i + 1 < chars.Length) chars[i + 1] = ' '; i++; }
            }
        }
        return new string(chars);
    }

    /// <summary>From <paramref name="start"/> to the end of the statement: the first ';' at bracket depth 0 (bounded to 4 000 characters).</summary>
    public static string Statement(string text, int start)
    {
        var depth = 0;
        var end = Math.Min(text.Length, start + 4000);
        for (var i = start; i < end; i++)
        {
            var c = text[i];
            if (c is '(' or '{' or '[') depth++;
            else if (c is ')' or '}' or ']') { depth--; if (depth < 0) return text[start..i]; }
            else if (c == ';' && depth == 0) return text[start..i];
        }
        return text[start..end];
    }

    public static string EnclosingType(string text, int index)
    {
        var matches = Regex.Matches(text[..Math.Min(index, text.Length)], @"\b(?:class|record|struct|interface)\s+(\w+)");
        return matches.Count > 0 ? matches[^1].Groups[1].Value : "(top-level statements)";
    }

    /// <summary>Identifiers, paths and labels only; the same allow-list sanitizer the source analyzer uses.</summary>
    public static string Safe(string value) => IqrSourceArchiveReader.SafeLabel(value);

    private static readonly Regex SecretShape = new(@"(?i)(password|pwd|secret|sharedaccess|accountkey|sig=|apikey|api_key|token|bearer|clientsecret|connectionstring|;\s*key\s*=|endpoint=sb://)", RegexOptions.Compiled);
    private static readonly Regex EntityName = new(@"^[A-Za-z0-9][A-Za-z0-9._\-/$]{0,199}$", RegexOptions.Compiled);
    private static readonly Regex EntityKey = new(@"(?i)(event\s*hub(name)?s?|hubnames?|topic(name)?s?|queue(name)?s?|koe(name)?|subscription(name)?s?|consumergroups?|containername|databasename|entity(name)?|channel)$", RegexOptions.Compiled);
    private static readonly Regex EntitySection = new(@"(?i)(topics?|queues?|koer|subscriptions?|abonnementer|eventhubs?|hubs)$", RegexOptions.Compiled);
    private static readonly Regex UrlKey = new(@"(?i)(baseurl|baseaddress|url|uri|endpoint|authority|instance|fqdn|namespace|blobendpoint|address)$", RegexOptions.Compiled);

    /// <summary>
    /// A configuration value that may leave the analyzer: a messaging/storage entity name under an entity-name key, or the scheme+host(+port+path)
    /// of an endpoint under a URL-like key. Anything credential-shaped, and every other value, is dropped.
    /// </summary>
    public static string? SafeValue(string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 300) return null;
        var last = key.Contains(':') ? Regex.Replace(key, @":\d+$", "")[(Regex.Replace(key, @":\d+$", "").LastIndexOf(':') + 1)..] : key;
        if (SecretShape.IsMatch(last) || SecretShape.IsMatch(value) || IntegrationPlatformSecrets.LooksLikeSecret(value)) return null;
        // Entity names: under an entity-name key, or under a section whose name says it holds topics/queues/subscriptions/hubs.
        if (EntityKey.IsMatch(last) || key.Split(':').SkipLast(1).Any(segment => EntitySection.IsMatch(segment)))
            return value.Split(',').Select(v => v.Trim()).All(v => EntityName.IsMatch(v)) ? value.Trim() : null;
        if (UrlKey.IsMatch(last) && Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "sb" or "amqps")
            return string.IsNullOrEmpty(uri.UserInfo) ? $"{uri.Scheme}://{uri.Authority}{uri.AbsolutePath.TrimEnd('/')}" : null;
        if (UrlKey.IsMatch(last) && EntityName.IsMatch(value) && value.Contains('.')) return value.Trim();
        return null;
    }
}

/// <summary>Shared secret-shape check (the integration catalog's rule), reused so architecture never keeps what IQR would refuse.</summary>
internal static class IntegrationPlatformSecrets
{
    public static bool LooksLikeSecret(string? value) => IntegrationRuntimeEvidenceSettings.LooksLikeSecret(value);
}
