using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BirkNext.Dependencies;
using CycloneDX.Models;

namespace BirkNext.Api.Services.DependencyReview;

/// <summary>
/// Dependency inventory sources other than a fresh archive upload: a stored source review (declared dependencies), a CycloneDX (JSON/XML) or
/// SPDX JSON SBOM parsed and schema-validated by the official CycloneDX .NET libraries, and a NuGet packages.lock.json (resolved graph). Every
/// item keeps where it came from; nothing is ever called runtime-loaded, and an invalid document never becomes an inventory.
/// </summary>
public static class InventorySources
{
    public const long MaxDocumentBytes = 20 * 1024 * 1024;

    public static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>Stable id for the inventory derived from one repository of a stored source review.</summary>
    public static Guid SourceReviewInventoryId(Guid runId, string repository) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes($"source-review:{runId:N}:{repository}"))[..16]);

    /// <summary>One declared-dependency inventory per repository of a stored source review. Declared only: no transitive dependencies.</summary>
    public static List<DependencyInventorySnapshot> FromSourceReview(DependencyReviewResult run) => run.Repositories.Where(r => r.Dependencies.Count > 0).Select(repo => new DependencyInventorySnapshot
    {
        Id = SourceReviewInventoryId(run.RunId, repo.Repository), Name = $"{repo.Repository} · source review {run.CompletedAt.ToUniversalTime():yyyy-MM-dd HH:mm} UTC",
        SourceType = InventorySourceType.SourceReview, SourceName = repo.Repository, Stage = InventoryStage.Declared,
        CapturedAt = run.CompletedAt, RecordedAt = run.CompletedAt, Repository = repo.Repository, SourceRunId = run.RunId, ContentSha256 = repo.ArchiveSha256,
        Provenance = $"Declared dependencies from repository archive {repo.Repository} (sha256 {Short(repo.ArchiveSha256)}), stored source review {run.RunId:D}.",
        Limitations =
        [
            "Declared dependencies only: transitive dependencies are not inventoried from source.",
            "A declared value is not a packaged, deployed or runtime-loaded version.",
        ],
        Dependencies = repo.Dependencies.Select(d => new InventoryDependency
        {
            PackageName = d.PackageName, Version = d.IsRange || string.IsNullOrWhiteSpace(d.CurrentValue) ? null : d.CurrentValue, VersionRange = d.IsRange ? d.CurrentValue : null,
            PackageManager = d.Manager, Datasource = d.Datasource, Relationship = DependencyRelationship.Direct, Stage = InventoryStage.Declared,
            Location = d.Line > 0 ? $"{d.OwnerFile}:{d.Line}" : d.OwnerFile, Repository = repo.Repository,
            Hashes = d.Digest is { } digest ? [new DependencyHash("digest", digest)] : [],
        }).ToList(),
    }).ToList();

    private static string Short(string? sha) => string.IsNullOrEmpty(sha) ? "—" : sha[..Math.Min(12, sha.Length)];

    /// <summary>The ecosystem a dependency belongs to for matching across inventories (Docker managers share one).</summary>
    public static string Ecosystem(InventoryDependency dep) => dep.PackageManager switch
    {
        "dockerfile" or "docker-compose" or "azure-pipelines" or "oci" => "docker",
        _ when dep.Datasource == "dotnet-version" => "dotnet-version",
        var m => m,
    };

    public static string Key(InventoryDependency dep) => $"{Ecosystem(dep)}:{dep.PackageName.ToLowerInvariant()}";

    /// <summary>
    /// Freshness of an inventory: Unknown without a capture time; Stale when a newer capture of the same source exists or it is older than the
    /// configured threshold. The inventory's freshness is not a statement about the deployment.
    /// </summary>
    public static (InventoryFreshness Freshness, string Detail) Freshness(DependencyInventorySnapshot inventory, IEnumerable<DependencyInventorySnapshot> all, int staleAfterDays, DateTimeOffset now)
    {
        if (inventory.CapturedAt is not { } captured)
            return (InventoryFreshness.Unknown, $"The source states no capture time (recorded by BirkNext {inventory.RecordedAt.ToUniversalTime():yyyy-MM-dd HH:mm} UTC).");
        var age = (int)Math.Floor((now - captured).TotalDays);
        var newer = all.Where(o => o.Id != inventory.Id && o.SourceType == inventory.SourceType && string.Equals(o.Repository ?? o.SourceName, inventory.Repository ?? inventory.SourceName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(o.Environment, inventory.Environment, StringComparison.OrdinalIgnoreCase) && o.CapturedAt > captured).OrderByDescending(o => o.CapturedAt).FirstOrDefault();
        if (newer is not null)
            return (InventoryFreshness.Stale, $"Captured {age} day(s) ago; a newer inventory of the same source was captured {newer.CapturedAt!.Value.ToUniversalTime():yyyy-MM-dd HH:mm} UTC.");
        return age > staleAfterDays
            ? (InventoryFreshness.Stale, $"Captured {age} day(s) ago (stale after {staleAfterDays} days, DependencyReview:InventoryStaleAfterDays).")
            : (InventoryFreshness.Current, $"Captured {age} day(s) ago (stale after {staleAfterDays} days, DependencyReview:InventoryStaleAfterDays).");
    }

    public static InventorySummary Summarize(DependencyInventorySnapshot inventory, IEnumerable<DependencyInventorySnapshot> all, int staleAfterDays, DateTimeOffset now)
    {
        var (freshness, detail) = Freshness(inventory, all, staleAfterDays, now);
        return new InventorySummary
        {
            Id = inventory.Id, Name = inventory.Name, SourceType = inventory.SourceType, SourceName = inventory.SourceName, Stage = inventory.Stage, CapturedAt = inventory.CapturedAt,
            RecordedAt = inventory.RecordedAt, Environment = inventory.Environment, BuildId = inventory.BuildId, Repository = inventory.Repository, SourceRunId = inventory.SourceRunId,
            Dependencies = inventory.Dependencies.Count, Freshness = freshness, FreshnessDetail = detail,
        };
    }

    // ── Package URLs ────────────────────────────────────────────────────────────────────────────────────────────────

    public sealed record Purl(string Type, string? Namespace, string Name, string? Version, Dictionary<string, string> Qualifiers);

    /// <summary>pkg:type/namespace/name@version?qualifiers#subpath (percent-decoded); null when not a package URL.</summary>
    public static Purl? ParsePurl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !value.StartsWith("pkg:", StringComparison.OrdinalIgnoreCase)) return null;
        var rest = value[4..].TrimStart('/');
        var hash = rest.IndexOf('#');
        if (hash >= 0) rest = rest[..hash];
        var qualifiers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var query = rest.IndexOf('?');
        if (query >= 0)
        {
            foreach (var pair in rest[(query + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
                if (pair.Split('=', 2) is [var k, var v]) qualifiers[k] = Uri.UnescapeDataString(v);
            rest = rest[..query];
        }
        string? version = null;
        var at = rest.LastIndexOf('@');
        if (at > rest.LastIndexOf('/')) { version = Uri.UnescapeDataString(rest[(at + 1)..]); rest = rest[..at]; }
        var segments = rest.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray();
        if (segments.Length < 2) return null;
        return new Purl(segments[0].ToLowerInvariant(), segments.Length > 2 ? string.Join('/', segments[1..^1]) : null, segments[^1], version, qualifiers);
    }

    private static (string Name, string Manager) Identity(Purl? purl, string? group, string name) => purl switch
    {
        null => (group is null ? name : $"{group}/{name}", "unknown"),
        { Type: "nuget" } => (purl.Name, "nuget"),
        { Type: "docker" or "oci" } => (purl.Namespace is null ? purl.Name : $"{purl.Namespace}/{purl.Name}", "docker"),
        _ => (purl.Namespace is null ? purl.Name : $"{purl.Namespace}/{purl.Name}", purl.Type),
    };

    // ── SBOM / lock file ────────────────────────────────────────────────────────────────────────────────────────────

    public static SbomFormat Detect(string fileName, string content)
    {
        var text = content.TrimStart('﻿', ' ', '\r', '\n', '\t');
        if (text.StartsWith('<')) return text.Contains("cyclonedx.org/schema/bom", StringComparison.OrdinalIgnoreCase) ? SbomFormat.CycloneDxXml : SbomFormat.Unknown;
        if (!text.StartsWith('{')) return SbomFormat.Unknown;
        try
        {
            using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return SbomFormat.Unknown;
            if (root.TryGetProperty("bomFormat", out var bf) && bf.ValueKind == JsonValueKind.String && bf.GetString() == "CycloneDX") return SbomFormat.CycloneDxJson;
            if (root.TryGetProperty("spdxVersion", out _)) return SbomFormat.SpdxJson;
            if (root.TryGetProperty("dependencies", out var deps) && deps.ValueKind == JsonValueKind.Object && root.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.Number)
                return SbomFormat.NuGetLockFile;
        }
        catch (JsonException) { }
        return SbomFormat.Unknown;
    }

    public sealed record ImportInput(string FileName, byte[] Bytes, SbomRole Role, string? Environment, string? Name, DateTimeOffset Now);

    /// <summary>Parses and validates the document; returns an inventory only when the document is valid.</summary>
    public static (SbomValidation Validation, DependencyInventorySnapshot? Inventory) Import(ImportInput input)
    {
        if (input.Bytes.Length == 0) return (Invalid(SbomFormat.Unknown, "The document is empty."), null);
        if (input.Bytes.Length > MaxDocumentBytes) return (Invalid(SbomFormat.Unknown, $"The document is larger than the {MaxDocumentBytes / (1024 * 1024)} MB limit."), null);
        var content = Encoding.UTF8.GetString(input.Bytes).TrimStart('﻿');
        var format = Detect(input.FileName, content);
        try
        {
            return format switch
            {
                SbomFormat.CycloneDxJson => CycloneDx(input, content, json: true),
                SbomFormat.CycloneDxXml => CycloneDx(input, content, json: false),
                SbomFormat.SpdxJson => Spdx(input, content),
                SbomFormat.NuGetLockFile => LockFile(input, content),
                _ => (Invalid(SbomFormat.Unknown, content.Contains("SPDXVersion:", StringComparison.Ordinal)
                    ? "SPDX tag/value documents are not supported; upload SPDX JSON, CycloneDX JSON/XML or a NuGet packages.lock.json."
                    : "Unsupported document: expected CycloneDX JSON/XML, SPDX JSON or a NuGet packages.lock.json."), null),
            };
        }
        catch (Exception ex) when (ex is JsonException or System.Xml.XmlException or InvalidOperationException or FormatException or ArgumentException)
        {
            // A document that passes schema validation but cannot be read is still rejected — never a partial inventory.
            return (Invalid(format, $"The document could not be read ({ex.GetType().Name})."), null);
        }
    }

    private static SbomValidation Invalid(SbomFormat format, params string[] errors) => new() { Format = format, Valid = false, Errors = [.. errors] };

    /// <summary>The CycloneDX validators return one line per schema node; keep "path: reason" lines, never the instance JSON.</summary>
    internal static List<string> CondenseSchemaMessages(IEnumerable<string>? messages)
    {
        var lines = (messages ?? []).ToList();
        var result = new List<string>();
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (!line.StartsWith("On instance:", StringComparison.Ordinal))
            {
                if (!line.StartsWith('{') && !line.StartsWith('[') && !line.StartsWith("http", StringComparison.Ordinal) && !line.StartsWith("Validation failed:", StringComparison.Ordinal)
                    && lines.Count(l => l.StartsWith("On instance:", StringComparison.Ordinal)) == 0)
                    result.Add(Clip(line));
                continue;
            }
            var path = line["On instance:".Length..].Trim().TrimEnd(':');
            var reason = i >= 2 ? lines[i - 2] : "Schema mismatch";
            if (reason.StartsWith("Some ", StringComparison.Ordinal)) continue; // container nodes; the specific child reason follows
            result.Add(Clip($"{(path.Length == 0 ? "/" : path)}: {reason}"));
        }
        if (result.Count == 0 && lines.Count > 0) result.Add("The document does not match the schema.");
        return result.Distinct().Take(12).ToList();
    }

    private static string Clip(string s) => s.Length > 300 ? s[..300] + "…" : s;

    private static (SbomValidation, DependencyInventorySnapshot?) CycloneDx(ImportInput input, string content, bool json)
    {
        var format = json ? SbomFormat.CycloneDxJson : SbomFormat.CycloneDxXml;
        string? spec;
        (bool Valid, IEnumerable<string>? Messages) validation;
        if (json)
        {
            using (var doc = JsonDocument.Parse(content)) spec = doc.RootElement.TryGetProperty("specVersion", out var sv) ? sv.GetString() : null;
            var result = CycloneDX.Json.Validator.Validate(content);
            validation = (result.Valid, result.Messages);
        }
        else
        {
            spec = Regex.Match(content, @"cyclonedx\.org/schema/bom/(?<v>\d+\.\d+)") is { Success: true } m ? m.Groups["v"].Value : null;
            if (spec is null || !Enum.TryParse<CycloneDX.SpecificationVersion>("v" + spec.Replace('.', '_'), out var version))
                return (Invalid(format, $"Unsupported CycloneDX XML schema version {spec ?? "(none)"}."), null);
            var result = CycloneDX.Xml.Validator.Validate(content, version);
            validation = (result.Valid, result.Messages);
        }
        if (!validation.Valid) return (Invalid(format, CondenseSchemaMessages(validation.Messages).ToArray()) with { SpecVersion = spec }, null);

        var bom = json ? CycloneDX.Json.Serializer.Deserialize(content) : CycloneDX.Xml.Serializer.Deserialize(content);
        var components = Flatten(bom.Components ?? []).ToList();
        var rootRef = bom.Metadata?.Component?.BomRef;
        var graph = (bom.Dependencies ?? []).Where(d => d.Ref is not null).GroupBy(d => d.Ref!).ToDictionary(g => g.Key, g => g.SelectMany(d => d.Dependencies ?? []).Select(x => x.Ref).OfType<string>().ToList());
        var (direct, transitive) = Relationships(graph, rootRef is null ? [] : [rootRef]);
        var stage = input.Role == SbomRole.DeployedArtifact ? InventoryStage.Deployed : InventoryStage.Packaged;
        var items = components.Select(c =>
        {
            var purl = ParsePurl(c.Purl);
            var (name, manager) = Identity(purl, c.Group, c.Name ?? "");
            var licenses = (c.Licenses ?? []).Select(l => l.License?.Id ?? l.License?.Name ?? l.Expression).OfType<string>().Where(s => s.Length > 0).ToList();
            return new InventoryDependency
            {
                PackageName = name, Version = string.IsNullOrWhiteSpace(c.Version) ? null : c.Version, PackageManager = manager, Datasource = manager == "unknown" ? null : manager,
                Relationship = c.BomRef is { } r && direct.Contains(r) ? DependencyRelationship.Direct : c.BomRef is { } t && transitive.Contains(t) ? DependencyRelationship.Transitive : DependencyRelationship.Unknown,
                Stage = stage, Location = $"component {c.BomRef ?? name}", Licenses = licenses, LicenseFieldPresent = true, Purl = c.Purl, Cpe = c.Cpe,
                Hashes = (c.Hashes ?? []).Where(h => !string.IsNullOrEmpty(h.Content)).Select(h => new DependencyHash(h.Alg.ToString().Replace('_', '-'), h.Content)).ToList(),
                Registry = purl?.Qualifiers.GetValueOrDefault("repository_url"), Publisher = c.Publisher ?? c.Supplier?.Name,
            };
        }).ToList();
        var notAssessed = new List<string>();
        if (bom.Services is { Count: > 0 } services) notAssessed.Add($"{services.Count} service(s) are not part of the dependency inventory.");
        if (bom.Vulnerabilities is { Count: > 0 } vulns) notAssessed.Add($"The SBOM's own vulnerabilities section ({vulns.Count}) is not used as advisory evidence.");
        if (bom.Compositions is { Count: > 0 }) notAssessed.Add("Compositions (completeness assertions) are not assessed: completeness is not claimed.");
        if (bom.Signature is not null) notAssessed.Add("The document signature is not verified.");
        var component = bom.Metadata?.Component;
        var captured = bom.Metadata?.Timestamp is { } ts ? new DateTimeOffset(DateTime.SpecifyKind(ts, DateTimeKind.Utc)) : (DateTimeOffset?)null;
        return Complete(input, format, spec, items, graph.Count > 0, notAssessed, captured,
            artifact: component is null ? null : $"{component.Name}{(component.Version is null ? "" : "@" + component.Version)}",
            defaultName: component?.Name is { } n ? $"{n}{(component.Version is null ? "" : " " + component.Version)} · SBOM" : null);
    }

    private static IEnumerable<Component> Flatten(IEnumerable<Component> components)
    {
        foreach (var c in components)
        {
            yield return c;
            foreach (var nested in Flatten(c.Components ?? [])) yield return nested;
        }
    }

    /// <summary>Direct = what a root depends on; transitive = reachable beyond that. Without a root or graph, the relationship stays Unknown.</summary>
    private static (HashSet<string> Direct, HashSet<string> Transitive) Relationships(Dictionary<string, List<string>> graph, IReadOnlyCollection<string> roots)
    {
        var direct = new HashSet<string>(roots.SelectMany(r => graph.GetValueOrDefault(r, [])), StringComparer.Ordinal);
        var transitive = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>(direct);
        while (queue.Count > 0)
            foreach (var next in graph.GetValueOrDefault(queue.Dequeue(), []))
                if (!direct.Contains(next) && !roots.Contains(next) && transitive.Add(next)) queue.Enqueue(next);
        return (direct, transitive);
    }

    private static (SbomValidation, DependencyInventorySnapshot?) Spdx(ImportInput input, string content)
    {
        var validation = CycloneDX.Spdx.Validation.JsonValidator.Validate(content);
        string? spec;
        using (var doc = JsonDocument.Parse(content)) spec = doc.RootElement.TryGetProperty("spdxVersion", out var sv) ? sv.GetString() : null;
        if (!validation.Valid) return (Invalid(SbomFormat.SpdxJson, CondenseSchemaMessages(validation.Messages).ToArray()) with { SpecVersion = spec }, null);
        var document = CycloneDX.Spdx.Serialization.JsonSerializer.Deserialize(content);
        var relationships = document.Relationships ?? [];
        var roots = relationships.Where(r => r.RelationshipType.ToString() == "DESCRIBES").Select(r => r.RelatedSpdxElement).Concat(document.DocumentDescribes ?? []).ToHashSet(StringComparer.Ordinal);
        var graph = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        void Edge(string from, string to) { if (!graph.TryGetValue(from, out var list)) graph[from] = list = []; list.Add(to); }
        foreach (var r in relationships)
            switch (r.RelationshipType.ToString())
            {
                case "DEPENDS_ON": Edge(r.SpdxElementId, r.RelatedSpdxElement); break;
                case "DEPENDENCY_OF": Edge(r.RelatedSpdxElement, r.SpdxElementId); break;
            }
        var (direct, transitive) = Relationships(graph, roots);
        var packages = (document.Packages ?? []).ToList();
        // The described package is the subject of the document, not one of its dependencies — unless it is the only package.
        if (packages.Count > 1) packages = packages.Where(p => !roots.Contains(p.SPDXID)).ToList();
        var stage = input.Role == SbomRole.DeployedArtifact ? InventoryStage.Deployed : InventoryStage.Packaged;
        static string? License(string? value) => string.IsNullOrWhiteSpace(value) || value is "NOASSERTION" or "NONE" ? null : value;
        var items = packages.Select(p =>
        {
            var refs = p.ExternalRefs ?? [];
            var purlText = refs.FirstOrDefault(r => r.ReferenceType == "purl")?.ReferenceLocator;
            var purl = ParsePurl(purlText);
            var (name, manager) = Identity(purl, null, p.Name ?? "");
            var licenses = new[] { License(p.LicenseDeclared), License(p.LicenseConcluded) }.OfType<string>().Distinct().ToList();
            return new InventoryDependency
            {
                PackageName = name, Version = string.IsNullOrWhiteSpace(p.VersionInfo) ? null : p.VersionInfo, PackageManager = manager, Datasource = manager == "unknown" ? null : manager,
                Relationship = direct.Contains(p.SPDXID) ? DependencyRelationship.Direct : transitive.Contains(p.SPDXID) ? DependencyRelationship.Transitive : DependencyRelationship.Unknown,
                Stage = stage, Location = $"package {p.SPDXID}", Licenses = licenses, LicenseFieldPresent = true, Purl = purlText,
                Cpe = refs.FirstOrDefault(r => r.ReferenceType is "cpe23Type" or "cpe22Type")?.ReferenceLocator,
                Hashes = (p.Checksums ?? []).Select(c => new DependencyHash(c.Algorithm.ToString().Replace('_', '-'), c.ChecksumValue)).ToList(),
                Registry = purl?.Qualifiers.GetValueOrDefault("repository_url"), Publisher = p.Supplier is { } s && s != "NOASSERTION" ? s : null,
            };
        }).ToList();
        var captured = document.CreationInfo?.Created is { } created && created != default ? new DateTimeOffset(DateTime.SpecifyKind(created, DateTimeKind.Utc)) : (DateTimeOffset?)null;
        var notAssessed = new List<string>();
        if (document.Files is { Count: > 0 } files) notAssessed.Add($"{files.Count} file entr{(files.Count == 1 ? "y is" : "ies are")} not part of the dependency inventory.");
        if (document.Snippets is { Count: > 0 }) notAssessed.Add("Snippets are not assessed.");
        return Complete(input, SbomFormat.SpdxJson, spec, items, graph.Count > 0, notAssessed, captured, artifact: document.Name, defaultName: document.Name is { } n ? $"{n} · SBOM" : null);
    }

    private static (SbomValidation, DependencyInventorySnapshot?) LockFile(ImportInput input, string content)
    {
        using var doc = JsonDocument.Parse(content);
        var root = doc.RootElement;
        var version = root.GetProperty("version").GetInt32();
        if (version is < 1 or > 2) return (Invalid(SbomFormat.NuGetLockFile, $"Unsupported packages.lock.json version {version}."), null);
        var byKey = new Dictionary<(string, string), InventoryDependency>();
        var errors = new List<string>();
        foreach (var framework in root.GetProperty("dependencies").EnumerateObject())
        {
            if (framework.Value.ValueKind != JsonValueKind.Object) { errors.Add($"dependencies.{framework.Name} is not an object."); continue; }
            foreach (var package in framework.Value.EnumerateObject())
            {
                var p = package.Value;
                var type = p.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (type == "Project") continue; // project references are not packages
                var resolved = p.TryGetProperty("resolved", out var r) ? r.GetString() : null;
                if (resolved is null) { errors.Add($"{framework.Name}/{package.Name} has no resolved version."); continue; }
                var key = (package.Name.ToLowerInvariant(), resolved);
                if (byKey.ContainsKey(key)) continue;
                byKey[key] = new InventoryDependency
                {
                    PackageName = package.Name, Version = resolved, VersionRange = p.TryGetProperty("requested", out var req) ? req.GetString() : null, PackageManager = "nuget", Datasource = "nuget",
                    Relationship = type == "Direct" ? DependencyRelationship.Direct : type is "Transitive" or "CentralTransitive" ? DependencyRelationship.Transitive : DependencyRelationship.Unknown,
                    Stage = InventoryStage.Resolved, Location = $"{input.FileName} · {framework.Name}",
                    Hashes = p.TryGetProperty("contentHash", out var h) && h.GetString() is { Length: > 0 } hash ? [new DependencyHash("SHA-512 (contentHash, base64)", hash)] : [],
                };
            }
        }
        if (errors.Count > 0) return (Invalid(SbomFormat.NuGetLockFile, [.. errors.Take(12)]) with { SpecVersion = version.ToString() }, null);
        return Complete(input, SbomFormat.NuGetLockFile, version.ToString(), byKey.Values.ToList(), graph: true,
            ["A lock file records what restore resolved; it has no capture time and no license metadata."], captured: null, artifact: null, defaultName: null);
    }

    private static (SbomValidation, DependencyInventorySnapshot?) Complete(ImportInput input, SbomFormat format, string? spec, List<InventoryDependency> items, bool graph, List<string> notAssessed,
        DateTimeOffset? captured, string? artifact, string? defaultName)
    {
        var warnings = new List<string>();
        var missingVersions = items.Count(i => i.Version is null);
        if (missingVersions > 0) warnings.Add($"{missingVersions} component(s) have no version: kept with version Unknown; registry, version and advisory comparisons are limited for them.");
        if (items.Count == 0) warnings.Add("The document lists no components.");
        var validation = new SbomValidation
        {
            Format = format, SpecVersion = spec, Valid = true, Warnings = warnings, Components = items.Count,
            Direct = items.Count(i => i.Relationship == DependencyRelationship.Direct), Transitive = items.Count(i => i.Relationship == DependencyRelationship.Transitive),
            UnknownRelationship = items.Count(i => i.Relationship == DependencyRelationship.Unknown),
            Ecosystems = items.GroupBy(i => i.PackageManager).OrderByDescending(g => g.Count()).ToDictionary(g => g.Key, g => g.Count()),
            MissingVersions = missingVersions, MissingIdentifiers = items.Count(i => i.Purl is null && i.Cpe is null && format != SbomFormat.NuGetLockFile),
            MissingLicenses = format == SbomFormat.NuGetLockFile ? items.Count : items.Count(i => i.Licenses.Count == 0), WithHashes = items.Count(i => i.Hashes.Count > 0),
            HasDependencyGraph = graph, NotAssessed = notAssessed,
        };
        var isLock = format == SbomFormat.NuGetLockFile;
        var stage = isLock ? InventoryStage.Resolved : input.Role == SbomRole.DeployedArtifact ? InventoryStage.Deployed : InventoryStage.Packaged;
        var snapshot = new DependencyInventorySnapshot
        {
            Id = Guid.NewGuid(), Name = string.IsNullOrWhiteSpace(input.Name) ? defaultName ?? input.FileName : input.Name.Trim(),
            SourceType = isLock ? InventorySourceType.LockFile : InventorySourceType.Sbom, SourceName = input.FileName, Stage = stage, CapturedAt = captured, RecordedAt = input.Now,
            Environment = string.IsNullOrWhiteSpace(input.Environment) ? null : input.Environment.Trim(), ArtifactId = artifact, Format = format, FormatVersion = spec,
            ContentSha256 = Sha256(input.Bytes), Dependencies = items,
            Provenance = $"{DependencyHealthLabels.Format(format)}{(spec is null ? "" : " " + spec)} {input.FileName} (sha256 {Short(Sha256(input.Bytes))})"
                + (isLock ? ", resolved by NuGet restore." : input.Role == SbomRole.DeployedArtifact ? $", stated to describe what is deployed{(input.Environment is { Length: > 0 } e ? $" to {e}" : "")}." : ", describing a build artifact."),
            Limitations =
            [
                .. isLock ? new[] { "Resolved at restore time — not proof of what was packaged or deployed." } : [ "An SBOM entry is not proof that a component is loaded at runtime.", "Completeness of the SBOM is not claimed." ],
                .. captured is null ? new[] { "The document states no creation time: freshness is Unknown." } : [],
            ],
        };
        return (validation, snapshot);
    }
}
