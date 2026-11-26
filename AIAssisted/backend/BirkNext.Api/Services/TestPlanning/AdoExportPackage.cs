using System.IO.Compression;
using System.Text;
using BirkNext.TestPlanning;

namespace BirkNext.Api.Services.TestPlanning;

/// <summary>
/// Builds the export package of a (hierarchical) plan (2026-11-26). Pure apart from the caller's exporter callbacks:
/// <list type="bullet">
/// <item><c>Testplanoversikt.csv</c> — every ready case with its suite path, test level and source identity (a working document, not an import file).</item>
/// <item><c>TestSuite-struktur.csv</c> — the suite hierarchy manifest: stable suite id, parent id, order, path, test level, case count and the file to
/// import into it (empty suites are listed, with no file). Not an Azure DevOps import file.</item>
/// <item>One import file per suite that holds ready cases, in a folder per parent suite, made by the existing exporter (same columns, steps and
/// validation as the single-file export).</item>
/// <item><c>Importveiledning.txt</c> — generated from the manifest: create the Static Test Suites parent before child, then import each file into
/// its own suite.</item>
/// </list>
/// </summary>
public static class AdoExportPackage
{
    public sealed record Result(byte[] Zip, int Rows, List<string> Files, List<ManifestRow> Manifest);

    public sealed record ManifestRow(string SuiteId, string? ParentId, int Order, string Path, string? TestLevel, int Cases, string? File);

    private const string NoSuite = "Uten testgruppe";

    public static Result Build(TestPlanDraft plan, IReadOnlySet<string> readyCaseIds, Func<List<TestCaseDraft>, AdoExportPreview> preview,
        Func<AdoExportPreview, byte[]> file, string extension)
    {
        var suites = plan.Suites.ToList();
        var ready = plan.Cases.Where(c => readyCaseIds.Contains(c.Source.ScenarioId)).ToList();
        var orphans = ready.Where(c => suites.All(s => s.Id != c.SuiteId)).ToList();
        var manifest = new List<ManifestRow>();
        var files = new List<string>();
        var rows = 0;
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Add(string name, byte[] bytes)
            {
                var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                using var s = entry.Open();
                s.Write(bytes);
            }
            string UniqueName(string folder, string stem)
            {
                var name = $"{folder}{stem}.{extension}";
                for (var n = 2; !usedNames.Add(name); n++) name = $"{folder}{stem}-{n}.{extension}";
                return name;
            }
            foreach (var (suite, depth) in PlanStructure.Flatten(suites))
            {
                var mine = ready.Where(c => c.SuiteId == suite.Id).ToList();
                var path = PlanStructure.PathOf(suites, suite.Id);
                string? name = null;
                if (mine.Count > 0)
                {
                    var p = preview(mine);
                    if (p.Rows.Count > 0)
                    {
                        var parents = path.Split(" / ")[..^1].Select(Safe).ToArray();
                        name = UniqueName(parents.Length == 0 ? "" : string.Join("/", parents) + "/", Safe(suite.Name));
                        Add(name, file(p));
                        files.Add(name);
                        rows += p.Rows.Count;
                    }
                }
                manifest.Add(new(suite.Id, suite.ParentId, suite.Order, path, suite.TestLevel is { } l ? TestLevelClassifier.Label(l) : null, mine.Count, name));
            }
            if (orphans.Count > 0)
            {
                var p = preview(orphans);
                var name = UniqueName("", Safe(NoSuite));
                Add(name, file(p));
                files.Add(name);
                rows += p.Rows.Count;
                manifest.Add(new("", null, int.MaxValue, NoSuite, null, orphans.Count, name));
            }
            Add("TestSuite-struktur.csv", Csv(["Testgruppe-id", "Overordnet testgruppe-id", "Rekkefølge", "Sti", "Testnivå", "Testtilfeller", "Importfil"],
                manifest.Select(m => new[] { m.SuiteId, m.ParentId ?? "", m.Order == int.MaxValue ? "" : m.Order.ToString(), m.Path, m.TestLevel ?? "", m.Cases.ToString(), m.File ?? "" })));
            Add("Testplanoversikt.csv", Csv(["Testgruppe", "Testtilfelle", "Testnivå", "Status", "BirkNext-id", "Kildeidentitet"],
                ready.Select(c => new[]
                {
                    suites.Any(s => s.Id == c.SuiteId) ? PlanStructure.PathOf(suites, c.SuiteId) : NoSuite, c.EffectiveTitle, TestLevelClassifier.Label(TestLevelClassifier.Of(c.Generated.CandidateType).Level),
                    c.Review.ToString(), c.Source.ScenarioId, c.Generated.SourceIdentity ?? "",
                })));
            Add("Importveiledning.txt", Encoding.UTF8.GetBytes(Guide(plan, manifest)));
        }
        return new(stream.ToArray(), rows, files, manifest);
    }

    /// <summary>The import guide, generated from the manifest (only suites that exist in this plan, parent before child).</summary>
    public static string Guide(TestPlanDraft plan, IReadOnlyList<ManifestRow> manifest)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Importveiledning – {plan.PlanName} (revisjon {plan.Revision})");
        sb.AppendLine();
        sb.AppendLine("Viktig: Import fra fil oppretter IKKE testgrupper (Static Test Suites) i Azure DevOps. Hver fil importeres inn i en testgruppe som allerede finnes.");
        sb.AppendLine("Testgruppe-strukturen står i TestSuite-struktur.csv. Den er en oversikt, ikke en importfil.");
        sb.AppendLine();
        sb.AppendLine("1. Åpne Azure DevOps Test Plans og velg eller opprett testplanen.");
        sb.AppendLine("2. Opprett testgruppene som Static Test Suites, overordnet gruppe før undergruppe:");
        foreach (var m in manifest.Where(m => m.SuiteId.Length > 0))
            sb.AppendLine($"   - {m.Path}{(m.Cases == 0 ? " (tom testgruppe)" : "")}");
        sb.AppendLine("3. Velg hver testgruppe som har en importfil, og importer filen til den:");
        var step = 0;
        foreach (var m in manifest.Where(m => m.File is not null))
            sb.AppendLine($"   {++step}) {m.Path}  ←  {m.File}  ({m.Cases} testtilfelle{(m.Cases == 1 ? "" : "r")})");
        sb.AppendLine("4. Kontroller antall testtilfeller og teststeg i hver testgruppe mot TestSuite-struktur.csv og Testplanoversikt.csv.");
        sb.AppendLine("5. Kontroller at testgruppene ligger i riktig hierarki.");
        sb.AppendLine();
        sb.AppendLine("Filene er laget av BirkNext uten kontakt med Azure DevOps. Ingenting er importert ennå.");
        return sb.ToString();
    }

    /// <summary>A safe file or folder name: letters, digits and dashes; never empty; at most 60 characters.</summary>
    public static string Safe(string name)
    {
        var s = new string([.. name.Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')]);
        while (s.Contains("--", StringComparison.Ordinal)) s = s.Replace("--", "-", StringComparison.Ordinal);
        s = s.Trim('-');
        return s.Length == 0 ? "testgruppe" : s[..Math.Min(s.Length, 60)];
    }

    private static byte[] Csv(string[] header, IEnumerable<string[]> rows)
    {
        static string Cell(string v) => v.IndexOfAny([';', '"', '\n', '\r']) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(";", header.Select(Cell)));
        foreach (var r in rows) sb.AppendLine(string.Join(";", r.Select(Cell)));
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(sb.ToString())];
    }
}
