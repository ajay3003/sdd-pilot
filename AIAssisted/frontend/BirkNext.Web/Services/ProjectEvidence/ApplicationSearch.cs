using BirkNext.Integrations;

namespace BirkNext.Web.Services.ProjectEvidence;

/// <summary>Where a search result comes from. Registered applications are chosen as they are; the others are linkable sources an application
/// can be created from (after confirming its name) — never applications by themselves.</summary>
public enum ApplicationSearchKind { Registered, ImportedProject, ImportedSource, DeliveryProject }

/// <summary>One Change Application search result: its name, kind, a one-line description and the evidence it would link.</summary>
public sealed record ApplicationSearchResult(string Name, ApplicationSearchKind Kind, string Detail, ProjectEvidencePackage? Package = null,
    SourceSetupSnapshot? Snapshot = null, IReadOnlyList<string>? Aliases = null)
{
    public bool IsRegistered => Kind == ApplicationSearchKind.Registered;
}

/// <summary>
/// Change Application search over everything BirkNext already knows, so an imported application never has to be re-created by hand:
/// <list type="bullet">
/// <item>Registered applications — Target Application identities already used by Target Environments (chosen as they are; never duplicated).</item>
/// <item>Imported projects — Import Project packages that hold application source or a documentation project.</item>
/// <item>Imported sources — Source Management / Source Analysis snapshots that hold application code (a Terraform or library-only package is
/// evidence for an application, never the application).</item>
/// <item>Delivery projects — Delivery Reports projects.</item>
/// </list>
/// A linkable source whose name is already a registered application is not listed twice: the registered one is shown with what it links.
/// Matching is case-insensitive and partial over the name and known aliases, and ignores separators ("m2lb language" finds "M2LB-language").
/// Pure, so every rule is testable.
/// </summary>
public static class ApplicationSearch
{
    public static string GroupLabel(ApplicationSearchKind kind) => kind switch
    {
        ApplicationSearchKind.Registered => "Registered applications",
        ApplicationSearchKind.ImportedProject => "Available BirkNext projects",
        ApplicationSearchKind.ImportedSource => "Imported application sources",
        _ => "Delivery projects",
    };

    public static IReadOnlyList<ApplicationSearchResult> Build(IEnumerable<(string Name, int Environments)> registered, IReadOnlyList<ProjectEvidencePackage> packages,
        IReadOnlyList<SourceSetupSnapshot> snapshots, IEnumerable<string> deliveryProjects)
    {
        var results = new List<ApplicationSearchResult>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var linked = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        void Link(string name, string what) { if (!linked.TryGetValue(name, out var l)) linked[name] = l = []; l.Add(what); }

        var registeredList = registered.Where(r => !string.IsNullOrWhiteSpace(r.Name)).GroupBy(r => r.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => (Name: g.Key, Environments: g.Sum(x => x.Environments))).ToList();
        foreach (var r in registeredList) names.Add(r.Name);

        var candidates = new List<ApplicationSearchResult>();
        foreach (var p in packages.Where(ProjectSetupPlanner.IsLogicalProjectCandidate).OrderBy(p => p.IsApplicationCandidate ? 0 : 1).ThenBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase))
            candidates.Add(new(p.DisplayName, ApplicationSearchKind.ImportedProject, $"Imported project · {p.ClassificationReason}", p, Aliases: [Stem(p.ArchiveFileName)]));
        var importedSnapshots = packages.Select(p => p.SnapshotId).OfType<Guid>().ToHashSet();
        foreach (var s in snapshots.Where(s => !importedSnapshots.Contains(s.SnapshotId) && s.ImportId is null && HasApplicationCode(s))
                     .GroupBy(s => SourceName(s), StringComparer.OrdinalIgnoreCase).Select(g => g.OrderByDescending(s => s.AnalyzedAt).First()))
            candidates.Add(new(SourceName(s), ApplicationSearchKind.ImportedSource,
                $"Imported source · {s.ArchiveFileName} · analyzed {s.AnalyzedAt.ToLocalTime():yyyy-MM-dd}", Snapshot: s, Aliases: [Stem(s.ArchiveFileName), s.Repository ?? ""]));
        foreach (var d in deliveryProjects.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).Distinct(StringComparer.OrdinalIgnoreCase))
            candidates.Add(new(d, ApplicationSearchKind.DeliveryProject, "Delivery Reports project"));

        foreach (var c in candidates)
        {
            if (names.Contains(c.Name)) { Link(c.Name, $"{GroupLabel(c.Kind).ToLowerInvariant()}: {c.Name}"); continue; }
            if (results.Any(r => string.Equals(r.Name, c.Name, StringComparison.OrdinalIgnoreCase))) continue;
            results.Add(c);
        }
        return
        [
            .. registeredList.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).Select(r => new ApplicationSearchResult(r.Name, ApplicationSearchKind.Registered,
                $"Existing target application · used by {r.Environments} environment{(r.Environments == 1 ? "" : "s")}"
                + (linked.TryGetValue(r.Name, out var l) ? $" · links {string.Join(", ", l.Distinct())}" : ""))),
            .. results,
        ];
    }

    /// <summary>Case-insensitive, partial, separator-insensitive matching over name and aliases; registered applications first, then exact, prefix
    /// and contains matches. An empty query lists everything.</summary>
    public static IReadOnlyList<ApplicationSearchResult> Search(IReadOnlyList<ApplicationSearchResult> results, string? query)
    {
        var q = Compact(query ?? "");
        if (q.Length == 0) return results;
        int Score(ApplicationSearchResult r)
        {
            var best = int.MaxValue;
            foreach (var text in new[] { r.Name }.Concat(r.Aliases ?? []).Where(t => t.Length > 0).Select(Compact))
                best = Math.Min(best, text == q ? 0 : text.StartsWith(q, StringComparison.Ordinal) ? 1 : text.Contains(q, StringComparison.Ordinal) ? 2 : int.MaxValue);
            return best;
        }
        return [.. results.Select(r => (r, s: Score(r))).Where(x => x.s != int.MaxValue).OrderBy(x => x.r.IsRegistered ? 0 : 1).ThenBy(x => x.s).ThenBy(x => x.r.Kind)
            .Select(x => x.r)];
    }

    /// <summary>An application-code snapshot: its code area found something, or it has service/shared projects. Infrastructure-only never counts.</summary>
    public static bool HasApplicationCode(SourceSetupSnapshot s) =>
        s.Areas.FirstOrDefault(a => a.Area == "Application code")?.Status is { } code ? code is not ("NotDetected" or "NotAnalyzed" or "Unsupported" or "NotApplicable" or "Failed")
        : s.ServiceProjectCount + s.SharedProjectCount > 0;

    public static string SourceName(SourceSetupSnapshot s) => s.Repository is { Length: > 0 } repo ? repo : Stem(s.ArchiveFileName);

    private static string Stem(string file) => file.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? file[..^4] : file;

    private static string Compact(string text) => new([.. text.ToLowerInvariant().Where(char.IsLetterOrDigit)]);
}
