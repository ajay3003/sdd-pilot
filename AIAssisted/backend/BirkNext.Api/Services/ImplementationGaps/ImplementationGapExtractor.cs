using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using BirkNext.ImplementationGaps;
using BirkNext.Interpretation;
using BirkNext.Sdd;
using BirkNext.Web.Services;

namespace BirkNext.Api.Services.ImplementationGaps;

/// <summary>
/// Extracts the SDD inputs of implementation-gap detection from project documents: tasks (via the shared <see cref="TaskExplorerService"/> parser,
/// plus the continuation bullets and exact lines it does not keep), "External gates" tables and inline <c>**Gate: …**</c> markers, decision-register
/// entries with their recorded status, contract and specification requirement ids. Deterministic and documented; nothing is inferred from keyword
/// similarity: a type is "expected" only when a task says to implement/create/add it, a package only when a task pins/adds it to a project.
/// </summary>
public static partial class ImplementationGapExtractor
{
    public sealed record Document(string Path, string Text);

    private static readonly string[] CodeExtensions = [".cs", ".csproj", ".razor", ".props", ".fs", ".vb", ".ts", ".tsx", ".js", ".py", ".sql", ".graphql", ".json", ".yml", ".yaml"];

    public static GapDocumentSet Extract(IEnumerable<Document> documents)
    {
        var docs = documents.Select(d => d with { Path = d.Path.Replace('\\', '/') }).OrderBy(d => d.Path, StringComparer.Ordinal).ToList();
        var refs = new List<GapDocumentRef>();
        var tasks = new List<GapTaskInput>();
        var gates = new List<GapGateInput>();
        var decisions = new List<GapDecisionEntry>();
        var registers = new List<string>();
        var titles = new Dictionary<string, string>(StringComparer.Ordinal);
        var contracts = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var specified = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var d in docs)
        {
            var role = Role(d);
            if (role is null) continue;
            refs.Add(new GapDocumentRef(d.Path, Sha256(d.Text), role));
            switch (role)
            {
                case "Tasks":
                    var parsed = Tasks(d);
                    tasks.AddRange(parsed);
                    gates.AddRange(GateTable(d));
                    foreach (var t in parsed)
                        foreach (Match m in DraftDecisionRe().Matches(t.Text))
                            titles.TryAdd(DecisionReferences.InText(m.Groups["id"].Value).FirstOrDefault() ?? m.Groups["id"].Value, m.Groups["title"].Value.Trim());
                    break;
                case "DecisionRegister":
                    registers.Add(d.Path);
                    decisions.AddRange(Decisions(d));
                    break;
                case "Contract":
                    contracts[d.Path] = Requirements(d.Text);
                    break;
                case "Specification":
                    foreach (var id in Requirements(d.Text)) specified.Add(id);
                    break;
            }
        }
        // Inline markers without a table row still gate their task.
        foreach (var t in tasks)
            foreach (var marker in t.GateMarkers)
                if (!gates.Any(g => g.Source.Path == t.DocumentPath && g.GateId == marker))
                    gates.Add(new GapGateInput { GateId = marker, Label = marker, DecisionIds = [.. DecisionReferences.InText(marker)], BlockedTaskIds = [t.TaskId], Source = new(t.DocumentPath, t.Line, "inline gate marker") });
        return new GapDocumentSet
        {
            Documents = refs, Tasks = tasks, Gates = gates, Decisions = decisions, DecisionRegisters = registers, DecisionTitles = titles,
            ContractRequirements = contracts, SpecifiedRequirementIds = [.. specified],
        };
    }

    /// <summary>Document role by name and content. Decision registers are recognised by ADR sections/index rows, not by the folder they sit in.</summary>
    public static string? Role(Document d)
    {
        var name = System.IO.Path.GetFileName(d.Path).ToLowerInvariant();
        if (name == "tasks.md") return "Tasks";
        if (name == "spec.md") return "Specification";
        if (name == "plan.md") return "Plan";
        if (d.Path.Contains("/contracts/", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".md", StringComparison.Ordinal)) return "Contract";
        if (name.EndsWith(".md", StringComparison.Ordinal) && (name.Contains("adr") || name.Contains("decision")) && Decisions(d).Count > 0) return "DecisionRegister";
        return null;
    }

    // ── Tasks ────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    public static List<GapTaskInput> Tasks(Document d)
    {
        var tree = TaskExplorerService.Parse(d.Text);
        var nodes = new Dictionary<string, BirkNext.Web.Models.TaskNode>(StringComparer.Ordinal);
        void Walk(IEnumerable<BirkNext.Web.Models.TaskNode> list) { foreach (var n in list) { if (n.TaskId is { } id) nodes.TryAdd(id, n); Walk(n.Children); } }
        Walk(tree.Roots);
        var moduleRoot = ModuleRoot(d.Path);
        var docFolder = d.Path.Contains('/') ? d.Path[..(d.Path.LastIndexOf('/') + 1)] : "";
        var lines = d.Text.Replace("\r\n", "\n").Split('\n');
        var result = new List<GapTaskInput>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? heading = null;
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].StartsWith('#')) heading = lines[i].TrimStart('#').Trim();
            var m = TaskLineRe().Match(lines[i]);
            if (!m.Success) continue;
            var id = NormalizeTaskId(m.Groups["id"].Value);
            if (!seen.Add(id)) continue; // a repeated id keeps its first line
            var continuation = new List<string>();
            for (var j = i + 1; j < lines.Length; j++)
            {
                var l = lines[j];
                if (TaskLineRe().IsMatch(l) || l.StartsWith('#') || (l.Length > 0 && !char.IsWhiteSpace(l[0]))) break;
                if (l.Trim().Length > 0) continuation.Add(l.Trim());
            }
            var mark = m.Groups["mark"].Value;
            var state = mark is "x" or "X" ? GapTaskState.Completed : mark == " " ? GapTaskState.NotStarted : mark is "~" or "-" or "/" ? GapTaskState.InProgress : GapTaskState.Unknown;
            nodes.TryGetValue(id, out var node);
            var main = lines[i].Trim();
            var all = main + "\n" + string.Join("\n", continuation);
            var (declared, packages) = Declarations(main);
            // Expected artifacts: concrete paths on the task's own line, inside its module. Paths in other modules ("as `Hendelse/…`", "from `…`"),
            // in continuation bullets, globs and ../ paths are references, never expectations.
            var mainPaths = Backticks(main).Where(IsPath).Where(IsConcrete).Distinct(StringComparer.Ordinal).ToList();
            var expected = mainPaths.Where(p => InModule(p, moduleRoot)).ToList();
            var referenced = Backticks(all).Where(IsPath).Where(p => !expected.Contains(p)).Distinct(StringComparer.Ordinal).ToList();
            var paths = Backticks(all).Where(IsPath).Distinct(StringComparer.Ordinal).ToList();
            var contractPaths = paths.Where(p => p.Contains("contracts/", StringComparison.OrdinalIgnoreCase))
                .Select(p => p.StartsWith(moduleRoot, StringComparison.Ordinal) || p.Count(c => c == '/') > 2 ? p : docFolder + p).Distinct(StringComparer.Ordinal).ToList();
            var gateMarkers = GateMarkerRe().Matches(main).Select(g => DecisionReferences.InText(g.Groups[1].Value).FirstOrDefault() ?? g.Groups[1].Value.Trim().TrimEnd('.')).Distinct().ToList();
            // Strict: the task's own expected path is a test file/project, or its line says "tests in `…`". The parser's keyword flag is too loose here
            // (an "Implement the retention guard … assert …" task is not a test task).
            var isTest = expected.Any(p => p.Contains("/tests/", StringComparison.OrdinalIgnoreCase) || p.StartsWith("tests/", StringComparison.OrdinalIgnoreCase) || p.EndsWith("Tests.cs", StringComparison.Ordinal))
                || TestTaskRe().IsMatch(main);
            result.Add(new GapTaskInput
            {
                TaskId = id, DocumentPath = d.Path, Line = i + 1, State = state, Text = main, Continuation = continuation,
                UserStory = node?.UserStoryTag ?? (UserStoryRe().Match(main) is { Success: true } us ? us.Groups[1].Value : null),
                Phase = node?.PhaseTitle,
                Paths = expected.Where(p => !contractPaths.Contains(p) && !contractPaths.Contains(docFolder + p)).ToList(),
                ReferencedPaths = referenced.Where(p => !contractPaths.Contains(p) && !contractPaths.Contains(docFolder + p)).ToList(),
                DeclaredTypes = declared, Packages = packages,
                MentionedIdentifiers = [.. CorrelationRules.NamedSymbols(all).Where(n => !declared.Contains(n) && !packages.Contains(n)).Take(40)],
                RequirementIds = Requirements(all), DecisionIds = [.. DecisionReferences.InText(all)], GateMarkers = gateMarkers,
                ReferencedTaskIds = [.. TaskIds(all).Where(t => t != id)], ContractPaths = contractPaths, IsTestTask = isTest,
                NotApplicableReason = NotApplicableMarkerRe().Match(main) is { Success: true } marker ? $"Marked \"{marker.Value.Trim()}\" in the task"
                    : heading is not null && NotApplicableHeadingRe().IsMatch(heading) ? $"Under the heading \"{heading}\"" : null,
            });
        }
        return result;
    }

    /// <summary>Types to implement and packages to add, from the task's own line. A type counts only inside an "Implement/Create/Add/Introduce/Define"
    /// clause (up to the location " in `…`"/" to `…`" or the sentence end); `Type.Method()` names its type; `Name(args)` alone is a method, not a type.
    /// A package counts only when the line adds/pins something to a project file (.csproj, "package", "pin").</summary>
    public static (List<string> Types, List<string> Packages) Declarations(string line)
    {
        var tokens = new List<string>();
        var masked = BacktickTokenRe().Replace(line, m => { tokens.Add(m.Groups[1].Value); return $"\u0001{tokens.Count - 1}\u0002"; });
        masked = BoldRe().Replace(masked, "");
        var packageLine = line.Contains(".csproj", StringComparison.OrdinalIgnoreCase) || PackageWordRe().IsMatch(line);
        var types = new List<string>();
        var packages = new List<string>();
        if (packageLine)
            foreach (var t in tokens)
                if (PackageIdRe().IsMatch(t) && !IsPath(t)) packages.Add(t);
        foreach (Match clause in DeclareClauseRe().Matches(masked))
            foreach (Match p in PlaceholderRe().Matches(clause.Groups["clause"].Value))
            {
                var t = tokens[int.Parse(p.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)];
                if (IsPath(t) || packages.Contains(t)) continue;
                var head = t.Split('(')[0];
                var parts = head.Split('.', StringSplitOptions.RemoveEmptyEntries);
                string? type = t.Contains('(') ? (parts.Length > 1 ? parts[^2] : null) : (parts.Length == 1 ? parts[0] : null);
                if (type is not null && TypeNameRe().IsMatch(type) && !types.Contains(type) && !FrameworkTypes.Contains(type)
                    && !type.EndsWith("Async", StringComparison.Ordinal)) types.Add(type);
            }
        return (types, packages.Distinct(StringComparer.Ordinal).ToList());
    }

    /// <summary>.NET/ASP.NET/EF/Azure SDK types a task uses but never declares: never an expected declaration (documented rule GAP-B).</summary>
    public static readonly IReadOnlySet<string> FrameworkTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "HttpClient", "IHttpClientFactory", "IHostedService", "BackgroundService", "IHost", "IHostEnvironment", "IConfiguration", "IOptions", "IOptionsMonitor", "IServiceCollection",
        "IServiceProvider", "ILogger", "ILoggerFactory", "WebApplication", "WebApplicationFactory", "Controller", "ControllerBase", "IActionResult", "ActionResult", "DbContext", "DbSet",
        "IQueryable", "IEnumerable", "Task", "ValueTask", "CancellationToken", "Exception", "TimeProvider", "FakeTimeProvider", "DateTimeOffset", "Guid", "JsonSerializer", "HealthCheckResult",
        "IHealthCheck", "IMiddleware", "RequestDelegate", "HttpContext", "BlobServiceClient", "BlobContainerClient", "BlobClient", "ServiceBusClient", "ServiceBusProcessor", "ServiceBusSender",
        "EventProcessorClient", "SecretClient", "KeyClient", "CryptographyClient", "DefaultAzureCredential", "TokenCredential", "ProblemDetails", "Results", "TypedResults",
    };

    public static IEnumerable<string> Backticks(string text) => BacktickTokenRe().Matches(text).Select(m => m.Groups[1].Value);

    public static bool IsPath(string token) =>
        token.Contains('/') && !token.Contains(' ') && !token.StartsWith("/", StringComparison.Ordinal) && !token.Contains("://", StringComparison.Ordinal)
        && (CodeExtensions.Any(e => token.EndsWith(e, StringComparison.OrdinalIgnoreCase)) || token.EndsWith(".md", StringComparison.OrdinalIgnoreCase));

    /// <summary>A concrete file path: no glob, no relative ../ or ./ prefix.</summary>
    public static bool IsConcrete(string path) => path.IndexOfAny(['*', '?', '{', '[']) < 0 && !path.StartsWith("../", StringComparison.Ordinal) && !path.StartsWith("./", StringComparison.Ordinal);

    private static readonly string[] ModuleRelativeFolders = ["src", "tests", "test", "source", ".pipeline", ".github", "docs", "scripts", "infra", "deploy", "specs", "contracts", "tools"];

    /// <summary>Within the tasks document's module: under its module root, or module-relative (src/…, tests/…, .pipeline/…). At repository root every path is.</summary>
    public static bool InModule(string path, string moduleRoot) =>
        moduleRoot.Length == 0 || path.StartsWith(moduleRoot, StringComparison.Ordinal) || ModuleRelativeFolders.Contains(path.Split('/')[0], StringComparer.Ordinal);

    public static bool IsCodePath(string path) => CodeExtensions.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    /// <summary>The module folder of an SDD document: everything before "specs/" (e.g. "Skole/" for "Skole/specs/001-x/tasks.md"); "" at repository root.</summary>
    public static string ModuleRoot(string documentPath)
    {
        var at = documentPath.IndexOf("specs/", StringComparison.Ordinal);
        return at > 0 && documentPath[at - 1] == '/' ? documentPath[..at] : "";
    }

    public static string NormalizeTaskId(string raw)
    {
        var m = TaskIdPartsRe().Match(raw);
        return m.Success ? $"T{m.Groups[1].Value.PadLeft(3, '0')}{m.Groups[2].Value}" : raw;
    }

    /// <summary>Task ids and ranges (T119–T122, T119-T122, T119 to T122) in text, expanded and normalized.</summary>
    public static List<string> TaskIds(string text)
    {
        var ids = new SortedSet<string>(StringComparer.Ordinal);
        foreach (Match m in TaskRangeRe().Matches(text))
        {
            var from = int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            var to = int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
            if (to >= from && to - from <= 200) for (var n = from; n <= to; n++) ids.Add($"T{n:000}");
        }
        foreach (Match m in TaskIdRe().Matches(TaskRangeRe().Replace(text, " "))) ids.Add(NormalizeTaskId(m.Value));
        return [.. ids];
    }

    public static List<string> Requirements(string text) =>
        [.. RequirementReferenceParser.Extract(text, requireUpperCase: true).Where(id => id.StartsWith("FR-", StringComparison.Ordinal) || id.StartsWith("NFR-", StringComparison.Ordinal) || id.StartsWith("SC-", StringComparison.Ordinal))];

    // ── Gates ────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Rows of any Markdown table whose header has a "Gate" column and a "Blocks" column (e.g. "## External gates").</summary>
    public static List<GapGateInput> GateTable(Document d)
    {
        var result = new List<GapGateInput>();
        var lines = d.Text.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i + 1 < lines.Length; i++)
        {
            if (!lines[i].TrimStart().StartsWith('|')) continue;
            var header = Cells(lines[i]);
            var gateCol = header.FindIndex(h => h.Equals("Gate", StringComparison.OrdinalIgnoreCase) || h.Equals("Gates", StringComparison.OrdinalIgnoreCase));
            var blocksCol = header.FindIndex(h => h.StartsWith("Block", StringComparison.OrdinalIgnoreCase));
            if (gateCol < 0 || blocksCol < 0 || !SeparatorRe().IsMatch(lines[i + 1])) continue;
            var ownerCol = header.FindIndex(h => h.Equals("Owner", StringComparison.OrdinalIgnoreCase));
            for (var j = i + 2; j < lines.Length && lines[j].TrimStart().StartsWith('|'); j++)
            {
                var row = Cells(lines[j]);
                if (row.Count <= Math.Max(gateCol, blocksCol)) continue;
                var label = row[gateCol];
                var decisionsInLabel = DecisionReferences.InText(label);
                result.Add(new GapGateInput
                {
                    GateId = decisionsInLabel.Count == 1 ? decisionsInLabel[0] : label, Label = label, DecisionIds = [.. decisionsInLabel],
                    BlockedTaskIds = TaskIds(row[blocksCol]), Blocks = row[blocksCol], Owner = ownerCol >= 0 && ownerCol < row.Count ? row[ownerCol] : null,
                    Source = new(d.Path, j + 1, "gate table row"),
                });
            }
            i++;
        }
        return result;
    }

    private static List<string> Cells(string line) => [.. line.Trim().Trim('|').Split('|').Select(c => c.Trim())];

    // ── Decision registers ───────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Decision entries with their recorded status: a section ("ADR-026" + underline, or "## ADR-026 …") with a <c>**Status:**</c> line,
    /// else the register's index table row (| [ADR-026](#adr-026) | Title | Status | …).</summary>
    public static List<GapDecisionEntry> Decisions(Document d)
    {
        var lines = d.Text.Replace("\r\n", "\n").Split('\n');
        var sections = new Dictionary<string, GapDecisionEntry>(StringComparer.Ordinal);
        var index = new Dictionary<string, GapDecisionEntry>(StringComparer.Ordinal);
        for (var i = 0; i < lines.Length; i++)
        {
            var setext = SetextAdrRe().Match(lines[i]);
            var atx = AtxAdrRe().Match(lines[i]);
            var isSection = (setext.Success && i + 1 < lines.Length && UnderlineRe().IsMatch(lines[i + 1])) || atx.Success;
            if (isSection)
            {
                var id = $"ADR-{(setext.Success ? setext : atx).Groups[1].Value}";
                string? title = null, status = null; var statusLine = i + 1;
                for (var j = i + 1; j < lines.Length && j < i + 80; j++)
                {
                    if (j > i + 1 && (SetextAdrRe().IsMatch(lines[j]) && j + 1 < lines.Length && UnderlineRe().IsMatch(lines[j + 1]) || AtxAdrRe().IsMatch(lines[j]))) break;
                    if (title is null && TitleRe().Match(lines[j]) is { Success: true } t) title = t.Groups[1].Value.Trim();
                    if (status is null && StatusRe().Match(lines[j]) is { Success: true } s) { status = StripLinks(s.Groups[1].Value).Trim(); statusLine = j + 1; }
                }
                if (status is not null) sections.TryAdd(id, new GapDecisionEntry(id, title, status, StatusOf(status), new(d.Path, statusLine, "decision section status")));
                continue;
            }
            if (IndexRowRe().Match(lines[i]) is { Success: true } row)
            {
                var id = $"ADR-{row.Groups[1].Value}";
                var cells = Cells(lines[i]);
                if (cells.Count >= 3) index.TryAdd(id, new GapDecisionEntry(id, cells[1], StripLinks(cells[2]), StatusOf(StripLinks(cells[2])), new(d.Path, i + 1, "decision index row")));
            }
        }
        foreach (var (id, entry) in index) sections.TryAdd(id, entry);
        return [.. sections.Values.OrderBy(e => e.Id, StringComparer.Ordinal)];
    }

    /// <summary>Recorded status → gate status. Only an explicit accepted/current status approves; superseded is not an approval.</summary>
    public static GapGateStatus StatusOf(string status)
    {
        var s = status.ToLowerInvariant();
        if (s.Contains("erstattet") || s.Contains("supersed") || s.Contains("deprecat")) return GapGateStatus.Unknown;
        if (s.Contains("avvist") || s.Contains("reject")) return GapGateStatus.Rejected;
        if (s.Contains("utkast") || s.Contains("draft") || s.Contains("foresl") || s.Contains("propos") || s.Contains("pending") || s.Contains("under behandling") || s.Contains("til godkjenning"))
            return GapGateStatus.Pending;
        if (s.Contains("gjeldende") || s.Contains("godkjent") || s.Contains("vedtatt") || s.Contains("approved") || s.Contains("accepted")) return GapGateStatus.Approved;
        return GapGateStatus.Unknown;
    }

    private static string StripLinks(string s) => LinkRe().Replace(s, "$1");

    private static string Sha256(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    [GeneratedRegex(@"^\s*[-*+]\s+\[(?<mark>[ xX~/-])\]\s+\**(?<id>T\d{2,4}[a-zA-Z]?)\b")] private static partial Regex TaskLineRe();
    [GeneratedRegex(@"^T(\d{2,4})([a-zA-Z]?)$")] private static partial Regex TaskIdPartsRe();
    [GeneratedRegex(@"\bT\d{2,4}[a-z]?\b")] private static partial Regex TaskIdRe();
    [GeneratedRegex(@"\bT(\d{2,4})\s*(?:[–—-]|\bto\b)\s*T(\d{2,4})\b")] private static partial Regex TaskRangeRe();
    [GeneratedRegex(@"`([^`\n]+)`")] private static partial Regex BacktickTokenRe();
    [GeneratedRegex(@"\*\*")] private static partial Regex BoldRe();
    [GeneratedRegex(@"\u0001(\d+)\u0002")] private static partial Regex PlaceholderRe();
    // The clause ends at its location (" in `…`") or at a word that starts a description of what the type holds/does (with, which, using, …).
    [GeneratedRegex(@"\b(?:Implement|Create|Add|Introduce|Define|Write)\b(?<clause>(?:[^.;:\u0001]|\u0001\d+\u0002)*?)(?=\s+(?:in|to|into|under)\s+\u0001|\s+(?:with|which|that|where|using|from|for|holding|containing|whose|carrying|of|on|as|implementing|extending|inheriting|via|through|wrapping|around|behind)\b|[.;:(](?:\s|$)|\s\(|$)")] private static partial Regex DeclareClauseRe();
    [GeneratedRegex(@"^[A-Z][A-Za-z0-9_]*$")] private static partial Regex TypeNameRe();
    [GeneratedRegex(@"^[A-Z][A-Za-z0-9]*(\.[A-Z][A-Za-z0-9]*)+$")] private static partial Regex PackageIdRe();
    [GeneratedRegex(@"\b(?:package|packages|NuGet|pin|pinned|pins)\b", RegexOptions.IgnoreCase)] private static partial Regex PackageWordRe();
    [GeneratedRegex(@"\*\*Gate:\s*([^*]+)\*\*")] private static partial Regex GateMarkerRe();
    [GeneratedRegex(@"\b(?:unit|integration|contract|end-to-end|e2e)?\s*tests?\s+in\s+`", RegexOptions.IgnoreCase)] private static partial Regex TestTaskRe();
    [GeneratedRegex(@"\[(US\d+)\]")] private static partial Regex UserStoryRe();
    [GeneratedRegex(@"Draft\s+(?<id>ADR-?\d{1,4})\s+[""“](?<title>[^""”]+)[""”]")] private static partial Regex DraftDecisionRe();
    [GeneratedRegex(@"^\s*\|?\s*:?-{3,}")] private static partial Regex SeparatorRe();
    [GeneratedRegex(@"^\s*ADR-(\d{3})\s*$")] private static partial Regex SetextAdrRe();
    [GeneratedRegex(@"^\s*#{1,6}\s+\[?ADR-(\d{3})\b")] private static partial Regex AtxAdrRe();
    [GeneratedRegex(@"^\s*-{3,}\s*$")] private static partial Regex UnderlineRe();
    [GeneratedRegex(@"\*\*(?:Tittel|Title):\*\*\s*(.+)$")] private static partial Regex TitleRe();
    [GeneratedRegex(@"\*\*Status:\*\*\s*(.+)$")] private static partial Regex StatusRe();
    [GeneratedRegex(@"^\s*\|\s*\[?ADR-(\d{3})\]?(?:\([^)]*\))?\s*\|")] private static partial Regex IndexRowRe();
    [GeneratedRegex(@"\[([^\]]+)\]\([^)]*\)")] private static partial Regex LinkRe();
    [GeneratedRegex(@"\b(?:deferred|optional|future|out of scope|post-mvp|later milestone|backlog)\b", RegexOptions.IgnoreCase)] private static partial Regex NotApplicableHeadingRe();
    [GeneratedRegex(@"[\[(](?:deferred|optional|out of scope|future|not in scope)[\])]", RegexOptions.IgnoreCase)] private static partial Regex NotApplicableMarkerRe();
}
