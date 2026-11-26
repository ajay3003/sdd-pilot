using System.Text.RegularExpressions;

namespace BirkNext.TestPlanning;

/// <summary>One test written down in a project's test specification document (2026-11-26), e.g. «**TEST-E-01** — Inngrep-hendelse fra adapter til
/// GraphQL» in «HendelseService/docs/Hendelsestjenesten – Testspesifikasjon.md». Typed facts only — the document itself is never stored.
/// <see cref="Id"/> is NOT globally unique (TEST-E-01 exists in several modules): the identity is the file plus the id.</summary>
public sealed record DocumentedTest
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    /// <summary>The source document (archive-relative path).</summary>
    public string File { get; init; } = "";
    /// <summary>The module the document belongs to (the first folder of its path).</summary>
    public string Module { get; init; } = "";
    public int Line { get; init; }
    /// <summary>The section heading the test stands under, as written («4. Ende-til-ende-tester»).</summary>
    public string? Section { get; init; }
    /// <summary>The level the DOCUMENT declares (id prefix / section). Source-declared only — never a verified test boundary.</summary>
    public ScenarioTestLevel DeclaredLevel { get; init; } = ScenarioTestLevel.Unclassified;
    public List<string> Given { get; init; } = [];
    public List<DocumentedTestStep> Steps { get; init; } = [];
}

/// <summary>One «Når …» (with its «Og …» continuations) and the «Forventet …» that follows it.</summary>
public sealed record DocumentedTestStep(string Action, string? Expected);

/// <summary>The documented tests of one source snapshot (typed evidence; <see cref="RuleVersion"/> versions the reading rule).</summary>
public sealed record DocumentedTestSnapshot
{
    public string RuleVersion { get; init; } = DocumentedTestReader.RuleVersion;
    public List<DocumentedTest> Tests { get; init; } = [];
    /// <summary>Test specification documents that were read (archive-relative paths).</summary>
    public List<string> Documents { get; init; } = [];
    public List<string> Limitations { get; init; } = [];
}

/// <summary>
/// Reads documented tests from test specification Markdown (2026-11-26). A document qualifies by its NAME (… Testspesifikasjon / Test
/// Specification …), never because it merely mentions a test id (spec.md, plan.md and tasks.md cite TEST-E ids without defining them).
/// A test starts at a bold id line «**TEST-X-NN** — title»; its body is «Gitt / Når / Og / Forventet» (or Given / When / And / Then / Expected),
/// with «Forventet:» followed by «- …» items. Deterministic and pure.
/// </summary>
public static partial class DocumentedTestReader
{
    public const string RuleVersion = "documented-tests/1";
    public const int MaxTests = 500;

    [GeneratedRegex(@"test[\s_.\-–—]*spe(?:sifikasjon|cification|c)\b|testspesifikasjon", RegexOptions.IgnoreCase)]
    private static partial Regex DocumentNameRe();

    [GeneratedRegex(@"^\s*\*\*(?<id>TEST-(?<kind>[A-Z]+)-\d+[A-Za-z]?)\*\*\s*(?:[—–\-:]\s*)?(?<title>.*)$")]
    private static partial Regex TestHeadRe();

    [GeneratedRegex(@"^\s*(?:#{1,6}\s+(?<h>.+?)\s*#*\s*$)")]
    private static partial Regex AtxHeadingRe();

    [GeneratedRegex(@"^\s*(?:-{3,}|={3,})\s*$")]
    private static partial Regex SetextUnderlineRe();

    [GeneratedRegex(@"^(?<kw>Gitt|Given|Når|Naar|When|Og|And|Men|But|Forventet|Expected|Then|Så)\b\s*:?\s*(?<rest>.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex ClauseRe();

    [GeneratedRegex(@"ende[\s\-–]*til[\s\-–]*ende|end[\s\-–]*to[\s\-–]*end|\bE2E\b", RegexOptions.IgnoreCase)]
    private static partial Regex E2eHeadingRe();

    [GeneratedRegex(@"integrasjon|integration", RegexOptions.IgnoreCase)]
    private static partial Regex IntegrationHeadingRe();

    [GeneratedRegex(@"enhet|unit|komponent|component", RegexOptions.IgnoreCase)]
    private static partial Regex ComponentHeadingRe();

    /// <summary>Whether a Markdown file is a test specification by its name.</summary>
    public static bool IsTestSpecification(string path) =>
        path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) && DocumentNameRe().IsMatch(System.IO.Path.GetFileNameWithoutExtension(path.Replace('\\', '/')));

    public static DocumentedTestSnapshot Read(IEnumerable<(string Path, string Content)> files)
    {
        var tests = new List<DocumentedTest>();
        var documents = new List<string>();
        var limitations = new List<string>();
        foreach (var (path, content) in files.Where(f => IsTestSpecification(f.Path)).OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            var normalized = path.Replace('\\', '/').TrimStart('/');
            documents.Add(normalized);
            tests.AddRange(ReadDocument(normalized, content));
        }
        // One file + id is one test; a repeated id in one file keeps its first definition.
        var distinct = tests.DistinctBy(t => (t.File, t.Id)).ToList();
        if (distinct.Count < tests.Count) limitations.Add($"{tests.Count - distinct.Count} repeated test id(s) within one document were read once.");
        if (distinct.Count > MaxTests) { limitations.Add($"Only the first {MaxTests} documented tests are kept."); distinct = distinct.Take(MaxTests).ToList(); }
        return new DocumentedTestSnapshot { Tests = distinct, Documents = documents, Limitations = limitations };
    }

    public static IEnumerable<DocumentedTest> ReadDocument(string path, string content)
    {
        var lines = content.Replace("\r\n", "\n").Split('\n');
        var module = path.Split('/', StringSplitOptions.RemoveEmptyEntries) is { Length: > 1 } parts ? parts[0] : "";
        string? section = null;
        for (var i = 0; i < lines.Length; i++)
        {
            if (AtxHeadingRe().Match(lines[i]) is { Success: true } atx && !TestHeadRe().IsMatch(lines[i])) { section = Clean(atx.Groups["h"].Value); continue; }
            if (i + 1 < lines.Length && SetextUnderlineRe().IsMatch(lines[i + 1]) && lines[i].Trim().Length > 0 && !lines[i].TrimStart().StartsWith('|')) { section = Clean(lines[i]); continue; }
            if (TestHeadRe().Match(lines[i]) is not { Success: true } head) continue;
            // The body: until the next test, heading or horizontal rule.
            var body = new List<string>();
            var j = i + 1;
            for (; j < lines.Length; j++)
            {
                if (TestHeadRe().IsMatch(lines[j]) || AtxHeadingRe().IsMatch(lines[j]) || Regex.IsMatch(lines[j], @"^\s*(\*\s*){3,}\s*$")
                    || j + 1 < lines.Length && SetextUnderlineRe().IsMatch(lines[j + 1]) && lines[j].Trim().Length > 0) break;
                body.Add(lines[j]);
            }
            var (given, steps) = Clauses(body);
            var id = head.Groups["id"].Value;
            yield return new DocumentedTest
            {
                Id = id, Title = Clean(head.Groups["title"].Value) is { Length: > 0 } t ? t : id, File = path, Module = module, Line = i + 1, Section = section,
                DeclaredLevel = Level(head.Groups["kind"].Value, section), Given = given, Steps = steps,
            };
            i = j - 1;
        }
    }

    /// <summary>The DECLARED level: the id letter (E = ende-til-ende, I = integrasjon, U/K/C = enhet/komponent), else the section heading.</summary>
    public static ScenarioTestLevel Level(string kind, string? section) => kind.ToUpperInvariant() switch
    {
        "E" => ScenarioTestLevel.EndToEnd,
        "I" => ScenarioTestLevel.Integration,
        "U" or "K" or "C" => ScenarioTestLevel.Component,
        _ => section is null ? ScenarioTestLevel.Unclassified
            : E2eHeadingRe().IsMatch(section) ? ScenarioTestLevel.EndToEnd
            : IntegrationHeadingRe().IsMatch(section) ? ScenarioTestLevel.Integration
            : ComponentHeadingRe().IsMatch(section) ? ScenarioTestLevel.Component : ScenarioTestLevel.Unclassified,
    };

    private static (List<string> Given, List<DocumentedTestStep> Steps) Clauses(List<string> body)
    {
        var given = new List<string>();
        var steps = new List<(List<string> Action, List<string> Expected)>();
        var mode = "";
        foreach (var raw in body)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('|')) continue;
            if (line.StartsWith("- ") || line.StartsWith("* ") || line.StartsWith("• "))
            {
                var item = Clean(line[2..]);
                if (mode == "expected" && steps.Count > 0) steps[^1].Expected.Add(item);
                else if (mode == "when" && steps.Count > 0) steps[^1].Action.Add(item);
                else if (mode == "given") given.Add(item);
                continue;
            }
            if (ClauseRe().Match(line) is not { Success: true } m) continue;
            var kw = m.Groups["kw"].Value.ToLowerInvariant();
            var rest = Clean(m.Groups["rest"].Value);
            switch (kw)
            {
                case "gitt" or "given":
                    mode = "given"; if (rest.Length > 0) given.Add(rest); break;
                case "når" or "naar" or "when":
                    mode = "when"; steps.Add(([rest], [])); break;
                case "forventet" or "expected" or "then" or "så":
                    mode = "expected";
                    if (steps.Count == 0) steps.Add(([], []));
                    if (rest.Length > 0) steps[^1].Expected.Add(rest);
                    break;
                default: // Og / And / Men / But continue the current clause
                    if (rest.Length == 0) break;
                    if (mode == "given") given.Add(rest);
                    else if (mode == "when" && steps.Count > 0) steps[^1].Action.Add(rest);
                    else if (mode == "expected" && steps.Count > 0) steps[^1].Expected.Add(rest);
                    break;
            }
        }
        return (given, [.. steps.Select(s => new DocumentedTestStep(string.Join(" og ", s.Action.Where(a => a.Length > 0)), s.Expected.Count == 0 ? null : string.Join("; ", s.Expected)))]);
    }

    private static string Clean(string s) => Regex.Replace(s.Replace("**", "").Replace("`", ""), @"\s+", " ").Trim().TrimEnd(':').Trim();
}
