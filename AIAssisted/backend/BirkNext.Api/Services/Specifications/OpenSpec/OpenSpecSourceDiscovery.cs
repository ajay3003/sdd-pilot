namespace BirkNext.Api.Services.Specifications.OpenSpec;

/// <summary>
/// Classifies the documents of one import by the documented OpenSpec layout only (folder structure, never document text):
/// <code>
/// openspec/specs/&lt;capability&gt;/spec.md                       current baseline
/// openspec/changes/&lt;id&gt;/{proposal,design,tasks}.md           active change documents
/// openspec/changes/&lt;id&gt;/specs/&lt;capability&gt;/spec.md         active delta specification
/// openspec/changes/archive/&lt;dated-id&gt;/…                     archived change (history only)
/// openspec/config.yaml                                       declared schema
/// </code>
/// Not every Markdown file is a specification: other files inside openspec/ are supporting documents; files outside every openspec/ folder are not
/// OpenSpec source; custom schema definitions (openspec/schemas/…), unsafe or duplicate paths are unsupported — each with its reason.
/// Nothing is downloaded and no repository script is executed.
/// </summary>
public static class OpenSpecSourceDiscovery
{
    public sealed record Input(string Path, string Content);

    public sealed record Classified(OpenSpecDocument Document, string Content, IReadOnlyList<string> CapabilityFolders, string? Scope);

    public sealed record Result(List<Classified> Documents, List<string> Roots, string? CommonArchiveFolder);

    private static readonly HashSet<string> ChangeDocuments = new(StringComparer.OrdinalIgnoreCase) { "proposal.md", "design.md", "tasks.md" };

    /// <summary>Archive-relative, forward slashes, no leading slash; null when the path is unsafe (traversal, drive, rooted, control characters).</summary>
    public static string? SafePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var p = path.Replace('\\', '/').Trim();
        if (p.StartsWith('/') || p.Contains(':') || p.Any(c => char.IsControl(c))) return null;
        var segments = p.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(s => s is "." or "..")) return null;
        return string.Join('/', segments);
    }

    public static Result Classify(IEnumerable<Input> inputs, CancellationToken ct = default)
    {
        var all = inputs.ToList();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var safe = new List<(string Path, string Original, string Content, string? Problem)>();
        foreach (var d in all)
        {
            var path = SafePath(d.Path);
            if (path is null) { safe.Add((d.Path ?? "", d.Path ?? "", d.Content ?? "", "Usikker sti (traversering, rotsti eller kontrolltegn): dokumentet er ikke lest.")); continue; }
            if (!seen.Add(path)) { safe.Add((path, d.Path ?? "", d.Content ?? "", "Duplikat kildesti (samme sti finnes allerede, uavhengig av store/små bokstaver): bare første forekomst er lest.")); continue; }
            safe.Add((path, d.Path ?? "", d.Content ?? "", null));
        }
        var usable = safe.Where(s => s.Problem is null).Select(s => s.Path).ToList();
        // One archive folder above everything (a ZIP of "m2lb-main/…"): left out of the identity scope so a renamed archive folder keeps identities.
        var firsts = usable.Select(p => p.Split('/')).ToList();
        var common = firsts.Count > 0 && firsts.All(s => s.Length > 1) && firsts.Select(s => s[0]).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1 ? firsts[0][0] : null;
        var roots = usable.Select(RootOf).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(r => r, StringComparer.Ordinal).ToList();

        var result = new List<Classified>();
        foreach (var (path, _, content, problem) in safe)
        {
            ct.ThrowIfCancellationRequested();
            var sha = OpenSpecIdentity.Sha(content);
            var bytes = System.Text.Encoding.UTF8.GetByteCount(content);
            if (problem is not null)
            {
                result.Add(new(new OpenSpecDocument(path, OpenSpecDocumentKind.Unsupported, sha, bytes, null, null, null, problem), content, [], null));
                continue;
            }
            var root = RootOf(path);
            if (root is null)
            {
                result.Add(new(new OpenSpecDocument(path, OpenSpecDocumentKind.NotOpenSpec, sha, bytes, null, null, null, "Ligger utenfor openspec/-mappen."), content, [], null));
                continue;
            }
            var scope = roots.Count <= 1 ? "" : OpenSpecIdentity.ScopeOf(ScopeFolders(root, common));
            var rel = path[(root.Length + 1)..].Split('/');
            var (kind, capability, change, reason) = Kind(rel);
            var capKey = capability.Count > 0 ? OpenSpecIdentity.CapabilityKey(capability) : null;
            if (kind is OpenSpecDocumentKind.CurrentSpecification or OpenSpecDocumentKind.ActiveDeltaSpecification or OpenSpecDocumentKind.ArchivedDeltaSpecification
                && content.Length > OpenSpecLimits.MaxDocumentChars)
                (kind, reason) = (OpenSpecDocumentKind.Unsupported, $"Dokumentet er større enn grensen på {OpenSpecLimits.MaxDocumentChars / (1024 * 1024)} MB tegn og er ikke tolket.");
            result.Add(new(new OpenSpecDocument(path, kind, sha, bytes, root, capKey, change, reason), content, capability, scope));
        }
        return new(result, roots, common);
    }

    /// <summary>The openspec/ folder a path lies in (its path up to and including the first «openspec» folder), or null.</summary>
    public static string? RootOf(string path)
    {
        var segments = path.Split('/');
        for (var i = 0; i < segments.Length - 1; i++)
            if (segments[i].Equals("openspec", StringComparison.OrdinalIgnoreCase)) return string.Join('/', segments[..(i + 1)]);
        return null;
    }

    private static IEnumerable<string> ScopeFolders(string root, string? common)
    {
        var folders = root.Split('/').ToList();
        if (common is not null && folders.Count > 1 && folders[0].Equals(common, StringComparison.OrdinalIgnoreCase)) folders.RemoveAt(0);
        return folders;
    }

    private static (OpenSpecDocumentKind Kind, List<string> Capability, string? Change, string? Reason) Kind(string[] rel)
    {
        static bool Is(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);
        var file = rel[^1];
        if (rel.Length == 1 && (Is(file, "config.yaml") || Is(file, "config.yml"))) return (OpenSpecDocumentKind.Configuration, [], null, null);
        if (Is(rel[0], "schemas"))
            return (OpenSpecDocumentKind.Unsupported, [], null, "Egendefinert skjema (openspec/schemas): BirkNext leser det ikke og kjører det ikke; strukturen er ikke verifisert.");
        if (!file.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            return (OpenSpecDocumentKind.Unsupported, [], null, "Ikke et Markdown-dokument eller en støttet konfigurasjonsfil.");
        if (Is(rel[0], "specs"))
            return rel.Length >= 3 && Is(file, "spec.md") ? (OpenSpecDocumentKind.CurrentSpecification, [.. rel[1..^1]], null, null)
                : (OpenSpecDocumentKind.SupportingDocument, [], null, rel.Length >= 3 ? "Støttedokument i en capability-mappe (ikke spec.md)." : "Dokument direkte i specs/ (ikke en capability).");
        if (Is(rel[0], "changes"))
        {
            if (rel.Length >= 2 && Is(rel[1], "archive"))
            {
                if (rel.Length < 4) return (OpenSpecDocumentKind.SupportingDocument, [], null, "Dokument direkte i changes/archive/.");
                var archived = rel[2];
                return rel.Length >= 6 && Is(rel[3], "specs") && Is(file, "spec.md")
                    ? (OpenSpecDocumentKind.ArchivedDeltaSpecification, [.. rel[4..^1]], archived, null)
                    : (OpenSpecDocumentKind.ArchivedChangeDocument, [], archived, null);
            }
            if (rel.Length < 3) return (OpenSpecDocumentKind.SupportingDocument, [], null, "Dokument direkte i changes/ (ikke i en endringsmappe).");
            var change = rel[1];
            if (rel.Length == 3 && ChangeDocuments.Contains(file)) return (OpenSpecDocumentKind.ActiveChangeDocument, [], change, null);
            if (rel.Length >= 5 && Is(rel[2], "specs") && Is(file, "spec.md")) return (OpenSpecDocumentKind.ActiveDeltaSpecification, [.. rel[3..^1]], change, null);
            return (OpenSpecDocumentKind.SupportingDocument, [], change, "Annet dokument i en endringsmappe.");
        }
        return (OpenSpecDocumentKind.SupportingDocument, [], null, Is(file, "project.md") ? "Prosjektkontekst (project.md)." : Is(file, "AGENTS.md") ? "Agentinstruksjoner (AGENTS.md)." : "Støttedokument.");
    }

    /// <summary>The declared schema of openspec/config.yaml (top-level «schema:» only; YAML is read as text, nothing is resolved or downloaded).</summary>
    public static string? SchemaOf(string? configYaml)
    {
        if (string.IsNullOrWhiteSpace(configYaml)) return null;
        foreach (var raw in configYaml.Split('\n').Take(500))
        {
            if (raw.Length == 0 || char.IsWhiteSpace(raw[0]) || raw.TrimStart().StartsWith('#')) continue;
            var line = raw.TrimEnd('\r');
            if (!line.StartsWith("schema:", StringComparison.Ordinal)) continue;
            var value = line["schema:".Length..].Split('#')[0].Trim().Trim('"', '\'');
            return value.Length == 0 ? null : value.Length > 100 ? value[..100] : value;
        }
        return null;
    }

    /// <summary>Schemas whose structure BirkNext verifies: the built-in spec-driven workflow (also when no config declares one).</summary>
    public static bool IsBuiltInSchema(string? schema) => schema is null || schema.Equals("spec-driven", StringComparison.OrdinalIgnoreCase);
}
