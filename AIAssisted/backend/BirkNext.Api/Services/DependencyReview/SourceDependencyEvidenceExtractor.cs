using Path = System.IO.Path;
using System.IO.Compression;
using System.Text.RegularExpressions;
using BirkNext.Dependencies;

namespace BirkNext.Api.Services.DependencyReview;

/// <summary>
/// The bridge Source Analysis calls once per uploaded archive: the same file subset the Dependency Review always read (manifests, Renovate
/// configuration candidates, pipeline YAML) becomes structured, secret-safe evidence on the immutable source snapshot — declared
/// dependencies, manager files, redacted Renovate configs, automation summaries, published packages and external project references.
/// No review is performed here and nothing is contacted; Dependency Review interprets the evidence later.
/// </summary>
public static class SourceDependencyEvidenceExtractor
{
    private static readonly Regex Wrapped = new(@"\s*(\(\d+\)|_\d+_)\s*$");
    private static readonly Regex SecretLine = new("(\"[^\"]*(password|token|secret|encrypted|authorization|apikey|api_key|privatekey|private_key|credential)[^\"]*\"\\s*:\\s*)(\"[^\"]*\"|[^,}\\]\\s]+)", RegexOptions.IgnoreCase);

    /// <summary>"M2LB (2).zip", "M2LB _2_.zip" → "M2LB".</summary>
    public static string ArchiveRepositoryName(string fileName) =>
        Wrapped.Replace(Path.GetFileNameWithoutExtension(Path.GetFileName(fileName.Replace('\\', '/'))), "").Trim();

    public static string Key(string name) => name.Trim().ToLowerInvariant();

    /// <summary>Repository identity: a single solution at the archive root names it; otherwise the archive file name does.</summary>
    public static SourceRepositoryIdentity Identity(string fileName, byte[] bytes)
    {
        try
        {
            using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            var paths = zip.Entries.Where(e => e.Length > 0 || !e.FullName.EndsWith('/')).Select(e => e.FullName.Replace('\\', '/').TrimStart('/')).Where(p => p.Length > 0).ToList();
            var tops = paths.Select(p => p.Split('/')[0]).Distinct(StringComparer.Ordinal).ToList();
            if (tops.Count == 1 && paths.All(p => p.Contains('/'))) paths = paths.Select(p => p[(tops[0].Length + 1)..]).ToList();
            var solutions = paths.Where(p => !p.Contains('/') && (p.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))).ToList();
            if (solutions.Count == 1)
            {
                var name = Path.GetFileNameWithoutExtension(solutions[0]);
                return new(Key(name), name, $"Root solution file {solutions[0]}");
            }
        }
        catch (InvalidDataException) { }
        var archive = ArchiveRepositoryName(fileName);
        return new(Key(archive), archive, "Archive file name");
    }

    /// <summary>Evidence for one archive, or null with the reason when the archive cannot be read.</summary>
    public static (SourceDependencyEvidence? Evidence, string? Error) Extract(string repository, byte[] bytes, string fileName = "archive.zip")
    {
        var (files, error) = DependencyReviewService.ReadArchive(fileName, bytes);
        return error is not null ? (null, error) : (FromFiles(repository, files) with { UnsupportedManifests = DependencyInventory.UnsupportedManifests(bytes) }, null);
    }

    public static SourceDependencyEvidence FromFiles(string repository, IReadOnlyList<RepositoryFile> files)
    {
        var (dependencies, managers) = DependencyInventory.Build(repository, files);
        var renovate = files.Where(f => RenovateConfig.IsConfigCandidate(f.Path))
            .Where(f => !f.Path.Equals("package.json", StringComparison.OrdinalIgnoreCase) || f.Content.Contains("\"renovate\"", StringComparison.Ordinal))
            .Select(f => new SourceRenovateFile(f.Path, RenovateConfig.Hash(f.Content), Redacted(f))).ToList();
        return new SourceDependencyEvidence
        {
            Dependencies = dependencies, Managers = managers.Select(m => new SourceManagerFiles(m.Manager, m.Files)).ToList(), RenovateFiles = renovate,
            Automation = DependencyReviewBuilder.AutomationEvidence(files), PublishedPackages = PublishedPackages(files), ExternalProjectReferences = ExternalReferences(files),
        };
    }

    /// <summary>The configuration with secret-looking keys and values replaced; an unparseable file keeps its text with secret-looking values masked.</summary>
    private static string Redacted(RepositoryFile file)
    {
        var parsed = RenovateConfig.Parse(file.Path, file.Content);
        return parsed.Root is { } root ? RenovateConfig.Redact(root)!.ToJsonString() : SecretLine.Replace(file.Content, m => m.Groups[1].Value + "\"" + RenovateConfig.Redacted + "\"");
    }

    /// <summary>The review input for a snapshot's evidence: redacted configs as files, everything else pre-extracted.</summary>
    public static RepositoryInput Input(string repository, string fingerprint, SourceDependencyEvidence evidence, (string Name, string Content)? configOverride = null) =>
        new(repository, fingerprint, evidence.RenovateFiles.Select(f => new RepositoryFile(f.Path, f.Content)).ToList(), configOverride)
        {
            Extracted = new ExtractedEvidence(evidence.Dependencies.Select(d => d with { Repository = repository }).ToList(),
                evidence.Managers.Select(m => new DependencyInventory.ManagerFiles(m.Manager, m.Files)).ToList(), evidence.Automation,
                evidence.RenovateFiles.ToDictionary(f => f.Path, f => f.Sha256, StringComparer.OrdinalIgnoreCase)),
            UnsupportedManifests = evidence.UnsupportedManifests,
        };

    private static readonly Regex PackageIdRx = new(@"<PackageId>\s*([^<]+?)\s*</PackageId>", RegexOptions.IgnoreCase);
    private static readonly Regex VersionRx = new(@"<(?:Version|PackageVersion|VersionPrefix)>\s*([^<]+?)\s*</", RegexOptions.IgnoreCase);
    private static readonly Regex NotPackable = new(@"<IsPackable>\s*false\s*</IsPackable>", RegexOptions.IgnoreCase);
    private static readonly Regex ProjectRefRx = new(@"<ProjectReference\s+Include=""([^""]+)""", RegexOptions.IgnoreCase);

    /// <summary>Packages a project declares it produces: an explicit PackageId, in the project or the nearest Directory.Build.props above it.</summary>
    public static List<SourcePublishedPackage> PublishedPackages(IReadOnlyList<RepositoryFile> files)
    {
        var props = files.Where(f => Path.GetFileName(f.Path).Equals("Directory.Build.props", StringComparison.OrdinalIgnoreCase)).ToList();
        var result = new List<SourcePublishedPackage>();
        foreach (var project in files.Where(f => f.Path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
        {
            if (NotPackable.IsMatch(project.Content)) continue;
            var directory = Path.GetDirectoryName(project.Path)?.Replace('\\', '/') ?? "";
            var nearest = props.Where(p => { var d = Path.GetDirectoryName(p.Path)?.Replace('\\', '/') ?? ""; return d.Length == 0 || directory == d || directory.StartsWith(d + "/", StringComparison.Ordinal); })
                .OrderByDescending(p => p.Path.Length).FirstOrDefault();
            var id = PackageIdRx.Match(project.Content) is { Success: true } own ? own.Groups[1].Value : nearest is not null && PackageIdRx.Match(nearest.Content) is { Success: true } inherited ? inherited.Groups[1].Value : null;
            if (id is null) continue;
            var name = Path.GetFileNameWithoutExtension(project.Path);
            id = id.Replace("$(MSBuildProjectName)", name, StringComparison.OrdinalIgnoreCase);
            if (id.Contains("$(")) continue; // other MSBuild expressions are not evaluated: no identity claimed
            var version = VersionRx.Match(project.Content) is { Success: true } v ? v.Groups[1].Value : nearest is not null && VersionRx.Match(nearest.Content) is { Success: true } pv ? pv.Groups[1].Value : null;
            result.Add(new(id, project.Path, version is not null && version.Contains("$(") ? null : version));
        }
        return result;
    }

    /// <summary>ProjectReferences whose resolved path leaves the archive root (source outside this repository).</summary>
    public static List<SourceExternalProjectReference> ExternalReferences(IReadOnlyList<RepositoryFile> files)
    {
        var result = new List<SourceExternalProjectReference>();
        foreach (var project in files.Where(f => f.Path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
        {
            var segments = (Path.GetDirectoryName(project.Path)?.Replace('\\', '/') ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
            foreach (Match m in ProjectRefRx.Matches(project.Content))
            {
                var reference = m.Groups[1].Value;
                var path = new List<string>(segments);
                var escapes = false;
                foreach (var part in reference.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (part == ".") continue;
                    if (part == "..") { if (path.Count == 0) { escapes = true; break; } path.RemoveAt(path.Count - 1); }
                    else path.Add(part);
                }
                if (escapes) result.Add(new(project.Path, reference, Path.GetFileName(reference.Replace('\\', '/'))));
            }
        }
        return result;
    }
}
