using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using BirkNext.Dependencies;

namespace BirkNext.Api.Services.DependencyReview;

/// <summary>One file of an uploaded repository archive, in memory only; never persisted.</summary>
public sealed record RepositoryFile(string Path, string Content);

/// <summary>
/// Declared dependencies by Renovate manager, from source only (no registry lookup): NuGet (csproj PackageReference, central package management
/// in Directory.Packages.props, global.json SDK, .config/dotnet-tools.json), Dockerfile FROM, docker-compose image, and Azure Pipelines container
/// images. Transitive dependencies are out of scope: only declared dependencies are inventoried. Under central package management a package's
/// version belongs to Directory.Packages.props, listed once with the projects that reference it — never once per csproj.
/// </summary>
public static class DependencyInventory
{
    public sealed record ManagerFiles(string Manager, List<string> Files);

    public static bool IsInventoryFile(string path)
    {
        var name = System.IO.Path.GetFileName(path);
        return name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || name.Equals("Directory.Packages.props", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Directory.Build.props", StringComparison.OrdinalIgnoreCase) || name.Equals("global.json", StringComparison.OrdinalIgnoreCase)
            || name.Equals("dotnet-tools.json", StringComparison.OrdinalIgnoreCase) || IsDockerfile(path) || IsCompose(path) || IsAzurePipeline(path);
    }

    /// <summary>The ecosystem of a manifest BirkNext does NOT read natively (null for NuGet/Docker, which it reads, and for non-manifests).</summary>
    public static string? UnsupportedEcosystem(string path) => System.IO.Path.GetFileName(path).ToLowerInvariant() switch
    {
        "pom.xml" or "build.gradle" or "build.gradle.kts" => "Maven / Gradle",
        "package.json" or "package-lock.json" or "yarn.lock" or "pnpm-lock.yaml" => "npm",
        "requirements.txt" or "pyproject.toml" or "pipfile" or "poetry.lock" or "setup.py" => "pip / Poetry",
        "go.mod" => "Go modules",
        "cargo.toml" => "Cargo",
        "gemfile" => "Bundler",
        "composer.json" => "Composer",
        _ => null,
    };

    /// <summary>Unsupported-ecosystem manifests in an archive, by entry name only (content is never read). Bounded to 50.</summary>
    public static List<string> UnsupportedManifests(byte[] archive)
    {
        try
        {
            using var zip = new System.IO.Compression.ZipArchive(new MemoryStream(archive), System.IO.Compression.ZipArchiveMode.Read);
            return zip.Entries.Select(e => e.FullName.Replace('\\', '/').TrimStart('/'))
                .Where(p => !p.Split('/').SkipLast(1).Any(s => s.ToLowerInvariant() is "node_modules" or "bin" or "obj" or ".git" or "vendor"))
                .Select(p => (Path: p, Ecosystem: UnsupportedEcosystem(p))).Where(x => x.Ecosystem is not null)
                .OrderBy(x => x.Path, StringComparer.Ordinal).Take(50).Select(x => $"{x.Ecosystem}: {x.Path}").ToList();
        }
        catch (InvalidDataException) { return []; }
    }

    public static bool IsDockerfile(string path)
    {
        var name = System.IO.Path.GetFileName(path);
        return name.Equals("Dockerfile", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Dockerfile.", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".Dockerfile", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsCompose(string path) => Regex.IsMatch(System.IO.Path.GetFileName(path), @"^(docker-)?compose([.-][\w.-]+)?\.ya?ml$", RegexOptions.IgnoreCase);

    public static bool IsAzurePipeline(string path) =>
        (path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
        && (path.Contains("azure-pipelines", StringComparison.OrdinalIgnoreCase) || path.Split('/').Any(s => s.Equals(".pipeline", StringComparison.OrdinalIgnoreCase) || s.Equals(".azuredevops", StringComparison.OrdinalIgnoreCase))
            || System.IO.Path.GetFileName(path).StartsWith("renovate-pipeline", StringComparison.OrdinalIgnoreCase));

    private static int Line(string content, int index) => index < 0 ? 1 : content.AsSpan(0, Math.Min(index, content.Length)).Count('\n') + 1;

    public static (List<DeclaredDependency> Dependencies, List<ManagerFiles> Managers) Build(string repository, IReadOnlyList<RepositoryFile> files)
    {
        var deps = new List<DeclaredDependency>();
        var managers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        void Manager(string name, string file) { if (!managers.TryGetValue(name, out var list)) managers[name] = list = []; if (!list.Contains(file)) list.Add(file); }

        // ── NuGet: central package management first, so csproj references without a version are attributed to their owner.
        var central = new List<(string Dir, string File, Dictionary<string, (string Version, int Line)> Versions)>();
        foreach (var props in files.Where(f => System.IO.Path.GetFileName(f.Path).Equals("Directory.Packages.props", StringComparison.OrdinalIgnoreCase)))
        {
            var versions = new Dictionary<string, (string, int)>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in Xml(props.Content)?.Descendants().Where(e => e.Name.LocalName == "PackageVersion") ?? [])
                if (e.Attribute("Include")?.Value is { } id && e.Attribute("Version")?.Value is { } version)
                    versions[id] = (version, ((System.Xml.IXmlLineInfo)e).LineNumber);
            central.Add((Dir(props.Path), props.Path, versions));
            Manager("nuget", props.Path);
        }
        var centralUse = new Dictionary<(string File, string Id), List<string>>();
        foreach (var project in files.Where(f => f.Path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || System.IO.Path.GetFileName(f.Path).Equals("Directory.Build.props", StringComparison.OrdinalIgnoreCase)))
        {
            var xml = Xml(project.Content);
            if (xml is null) continue;
            Manager("nuget", project.Path);
            var owner = central.Where(c => project.Path.StartsWith(c.Dir, StringComparison.Ordinal)).OrderByDescending(c => c.Dir.Length).Select(c => ((string Dir, string File, Dictionary<string, (string Version, int Line)> Versions)?)c).FirstOrDefault();
            foreach (var reference in xml.Descendants().Where(e => e.Name.LocalName is "PackageReference" or "GlobalPackageReference"))
            {
                var id = reference.Attribute("Include")?.Value ?? reference.Attribute("Update")?.Value;
                if (id is null) continue;
                var version = reference.Attribute("VersionOverride")?.Value ?? reference.Attribute("Version")?.Value
                    ?? reference.Elements().FirstOrDefault(c => c.Name.LocalName is "Version" or "VersionOverride")?.Value;
                var line = ((System.Xml.IXmlLineInfo)reference).LineNumber;
                if (version is null && owner is { } cpm && cpm.Versions.ContainsKey(id))
                {
                    var key = (cpm.File, id);
                    if (!centralUse.TryGetValue(key, out var users)) centralUse[key] = users = [];
                    users.Add(project.Path);
                    continue;
                }
                deps.Add(Nuget(repository, id, version, project.Path, line, "nuget", [project.Path]));
            }
            foreach (var sdk in xml.Descendants().Where(e => e.Name.LocalName == "Project" && e.Attribute("Sdk")?.Value is { } s && s.Contains('/')))
            {
                var parts = sdk.Attribute("Sdk")!.Value.Split('/');
                deps.Add(Nuget(repository, parts[0], parts[1], project.Path, 1, "msbuild-sdk", [project.Path]));
            }
        }
        foreach (var (dir, file, versions) in central)
            foreach (var (id, (version, line)) in versions)
                deps.Add(Nuget(repository, id, version, file, line, "nuget", centralUse.TryGetValue((file, id), out var users) ? users : []));

        foreach (var global in files.Where(f => System.IO.Path.GetFileName(f.Path).Equals("global.json", StringComparison.OrdinalIgnoreCase)))
        {
            Manager("nuget", global.Path);
            if (Json(global.Content) is not { } doc) continue;
            if (doc.RootElement.TryGetProperty("sdk", out var sdk) && sdk.TryGetProperty("version", out var v) && v.GetString() is { } sdkVersion)
                deps.Add(new DeclaredDependency { Repository = repository, Manager = "nuget", Datasource = "dotnet-version", DepType = "dotnet-sdk", PackageName = "dotnet-sdk",
                    CurrentValue = sdkVersion, OwnerFile = global.Path, Line = Line(global.Content, global.Content.IndexOf("\"version\"", StringComparison.Ordinal)), ReferencedBy = [global.Path] });
            if (doc.RootElement.TryGetProperty("msbuild-sdks", out var sdks) && sdks.ValueKind == JsonValueKind.Object)
                foreach (var p in sdks.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String))
                    deps.Add(Nuget(repository, p.Name, p.Value.GetString(), global.Path, Line(global.Content, global.Content.IndexOf($"\"{p.Name}\"", StringComparison.Ordinal)), "msbuild-sdk", [global.Path]));
        }
        foreach (var tools in files.Where(f => System.IO.Path.GetFileName(f.Path).Equals("dotnet-tools.json", StringComparison.OrdinalIgnoreCase)))
        {
            Manager("nuget", tools.Path);
            if (Json(tools.Content) is not { } doc || !doc.RootElement.TryGetProperty("tools", out var list) || list.ValueKind != JsonValueKind.Object) continue;
            foreach (var tool in list.EnumerateObject())
                if (tool.Value.TryGetProperty("version", out var v))
                    deps.Add(Nuget(repository, tool.Name, v.GetString(), tools.Path, Line(tools.Content, tools.Content.IndexOf($"\"{tool.Name}\"", StringComparison.Ordinal)), "nuget", [tools.Path]));
        }

        // ── Docker images.
        foreach (var dockerfile in files.Where(f => IsDockerfile(f.Path)))
        {
            Manager("dockerfile", dockerfile.Path);
            var stages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in Regex.Matches(dockerfile.Content, @"^\s*FROM\s+(?:--platform=\S+\s+)?(?<image>\S+)(?:\s+AS\s+(?<alias>\S+))?", RegexOptions.Multiline | RegexOptions.IgnoreCase))
            {
                var image = m.Groups["image"].Value;
                // FROM an earlier build stage, scratch or an ARG-built reference is not an image dependency.
                var skip = stages.Contains(image) || image.Equals("scratch", StringComparison.OrdinalIgnoreCase) || image.Contains('$');
                if (m.Groups["alias"].Success) stages.Add(m.Groups["alias"].Value);
                if (!skip) deps.Add(Image(repository, "dockerfile", image, dockerfile.Path, Line(dockerfile.Content, m.Index)));
            }
        }
        foreach (var compose in files.Where(f => IsCompose(f.Path)))
        {
            Manager("docker-compose", compose.Path);
            foreach (Match m in Regex.Matches(compose.Content, @"^\s*image:\s*[""']?(?<image>[^\s""'#]+)", RegexOptions.Multiline))
                if (!m.Groups["image"].Value.Contains('$')) deps.Add(Image(repository, "docker-compose", m.Groups["image"].Value, compose.Path, Line(compose.Content, m.Index)));
        }
        foreach (var pipeline in files.Where(f => IsAzurePipeline(f.Path)))
        {
            Manager("azure-pipelines", pipeline.Path);
            foreach (Match m in Regex.Matches(pipeline.Content, @"^\s*(?:-\s*)?(?:image|container):\s*[""']?(?<image>[\w.\-/]+:[\w.\-]+)[""']?\s*$", RegexOptions.Multiline))
                deps.Add(Image(repository, "azure-pipelines", m.Groups["image"].Value, pipeline.Path, Line(pipeline.Content, m.Index)));
        }
        return (deps, managers.Select(m => new ManagerFiles(m.Key, m.Value)).OrderBy(m => m.Manager, StringComparer.Ordinal).ToList());
    }

    private static DeclaredDependency Nuget(string repository, string id, string? version, string file, int line, string depType, List<string> referencedBy) => new()
    {
        Repository = repository, Manager = "nuget", Datasource = "nuget", DepType = depType, PackageName = id, CurrentValue = version,
        IsRange = version is not null && (version.IndexOfAny(['[', '(', ',', '*']) >= 0), OwnerFile = file, Line = line, ReferencedBy = referencedBy,
    };

    private static DeclaredDependency Image(string repository, string manager, string image, string file, int line)
    {
        var digest = image.Contains('@') ? image[(image.IndexOf('@') + 1)..] : null;
        var reference = digest is null ? image : image[..image.IndexOf('@')];
        var slash = reference.LastIndexOf('/');
        var colon = reference.LastIndexOf(':');
        var (name, tag) = colon > slash ? (reference[..colon], reference[(colon + 1)..]) : (reference, null);
        return new DeclaredDependency { Repository = repository, Manager = manager, Datasource = "docker", DepType = manager == "dockerfile" ? "final" : null, PackageName = name,
            CurrentValue = tag, Digest = digest, OwnerFile = file, Line = line, ReferencedBy = [file] };
    }

    private static string Dir(string path) => path.Contains('/') ? path[..(path.LastIndexOf('/') + 1)] : "";

    private static XDocument? Xml(string content)
    {
        try { return XDocument.Parse(content, LoadOptions.SetLineInfo); } catch (System.Xml.XmlException) { return null; }
    }

    private static JsonDocument? Json(string content)
    {
        try { return JsonDocument.Parse(content, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }); }
        catch (JsonException) { return null; }
    }
}
