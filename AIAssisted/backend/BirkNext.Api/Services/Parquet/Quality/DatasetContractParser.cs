using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using BirkNext.Parquet;

namespace BirkNext.Api.Services.Parquet.Quality;

/// <summary>
/// Reads Parquet dataset contracts from a source document (Markdown, as retained with a Source Analysis snapshot). Structural conventions only:
/// a dataset section is a heading naming a <c>container/…parquet</c> path followed by a column table (Column | type | Null | content); the document
/// header states the contract version. Rules are generated only from statements this parser recognises exactly (backticked arithmetic identities,
/// "add up to", "appear only with", "sorted by", enumerated value cells, format tokens, manifest and determinism facts). Every other statement of a
/// dataset section is still returned as a requirement with <see cref="DatasetContractRequirement.Machine"/> false — listed as NOT SUPPORTED,
/// never dropped. Nothing is inferred from a file name. Pure: no I/O.
/// </summary>
public static partial class DatasetContractParser
{
    public const string ParserVersion = "dataset-contract-md/1";

    /// <summary>Cheap pre-filter for retention: a Markdown document that mentions Parquet and has a column table with type and null columns.</summary>
    public static bool LooksLikeDatasetContract(string text) =>
        text.Contains("parquet", StringComparison.OrdinalIgnoreCase) && Lines(text).Any(l => IsColumnHeader(Cells(l)));

    /// <summary>A related document that states renames for a contract version (e.g. a wire-changes list): mentions the version with an arrow.</summary>
    public static bool MentionsVersionChange(string text, string version) =>
        text.Contains(version, StringComparison.Ordinal) && (text.Contains('→') || text.Contains("->", StringComparison.Ordinal));

    public static List<DatasetContract> Parse(string path, string text, string sha256, IEnumerable<(string Path, string Text)>? related = null)
    {
        var lines = Lines(text);
        var version = "";
        var versionLine = 0;
        for (var i = 0; i < lines.Count && version.Length == 0; i++)
            if (VersionLabel().IsMatch(lines[i]) && Backticks(lines[i]).FirstOrDefault() is { } v) { version = v; versionLine = i + 1; }
        var (governance, governanceReason) = Governance(lines, version);
        var containers = new List<string>();
        foreach (var l in lines)
        {
            var cells = Cells(l);
            if (cells.Count >= 2 && cells[0].StartsWith("Container", StringComparison.OrdinalIgnoreCase)) containers.AddRange(Backticks(cells[1]));
        }
        // Document-level requirements shared by every dataset of the document.
        var shared = new List<DatasetContractRequirement>();
        var manifestFields = new List<string>();
        string? currentHeading = null;
        var headingLine = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            if (Heading().Match(l) is { Success: true } h) { currentHeading = h.Groups["t"].Value.Trim(); headingLine = i + 1; continue; }
            var cells = Cells(l);
            if (currentHeading is not null && currentHeading.Contains("Location", StringComparison.OrdinalIgnoreCase) && cells.Count >= 2 && !cells[0].All(c => c is '-' or ':' or ' ')
                && cells[0].Length > 0 && !IsColumnHeader(cells))
            {
                var key = cells[0];
                var isPath = key.StartsWith("Path", StringComparison.OrdinalIgnoreCase);
                shared.Add(new DatasetContractRequirement
                {
                    Id = $"L{i + 1}", Category = "Delivery", Text = $"{key}: {Plain(cells[1])}", Line = i + 1, Machine = false,
                    Reason = isPath ? "A locally opened file has no storage path; the partition is checked through the manifest's period when a manifest is attached."
                        : "Storage and delivery behaviour; it cannot be verified from the Parquet file alone.",
                });
            }
            if (currentHeading is not null && currentHeading.Contains("Manifest", StringComparison.OrdinalIgnoreCase) && (l.TrimStart().StartsWith('"') || l.TrimStart().StartsWith('{')))
                foreach (Match m in JsonKey().Matches(l)) if (!manifestFields.Contains(m.Groups["k"].Value)) manifestFields.Add(m.Groups["k"].Value);
        }
        for (var i = 0; i < lines.Count; i++)
        {
            if (Heading().Match(lines[i]) is not { Success: true } dh || !dh.Groups["t"].Value.Contains("Determinism", StringComparison.OrdinalIgnoreCase)) continue;
            var end = i + 1;
            while (end < lines.Count && !Heading().IsMatch(lines[end])) end++;
            foreach (var (sentence, n) in ParagraphSentences(lines, i + 1, end))
                shared.Add(new DatasetContractRequirement { Id = $"L{n}" + (shared.Count(r => r.Line == n) is var dc and > 0 ? $".{dc + 1}" : ""), Category = "Determinism", Text = sentence, Line = n,
                    Machine = DeterminismMachine(sentence), Reason = DeterminismMachine(sentence) ? null : "Producer behaviour (re-running the export); not checkable from one file." });
        }
        if (manifestFields.Count > 0)
            shared.Add(new DatasetContractRequirement { Id = "manifest", Category = "Delivery manifest", Text = $"A _manifest.json with {string.Join(", ", manifestFields)} describes the file (period, contract version, SHA-256, row count, bytes).", Line = headingLine, Machine = true });

        var renames = new List<DatasetRename>();
        string? predecessor = null;
        if (versionLine > 0)
            foreach (var (from, to) in Arrows(lines[versionLine - 1]))
            {
                if (to == version) { predecessor = from; continue; }
                renames.Add(new DatasetRename(from, to, "", path, versionLine));
            }
        foreach (var (rPath, rText) in related ?? [])
        {
            var rLines = Lines(rText);
            // Only the block that is about this contract (a line naming the version transition or a container, plus its indented sub-lines):
            // the same words renamed for another artifact (e.g. a database table) are not a statement about the Parquet file.
            foreach (var i in ScopedLines(rLines, version, containers))
                foreach (var (from, to) in Arrows(rLines[i]))
                {
                    if (to == version) { predecessor ??= from; continue; }
                    renames.Add(new DatasetRename(from, to, "", rPath, i + 1));
                }
        }

        var result = new List<DatasetContract>();
        for (var i = 0; i < lines.Count; i++)
        {
            if (Heading().Match(lines[i]) is not { Success: true } h) continue;
            var title = h.Groups["t"].Value.Trim();
            var dataPath = Backticks(title).FirstOrDefault(b => b.EndsWith(".parquet", StringComparison.OrdinalIgnoreCase));
            // A dataset section: its column table comes before the next heading.
            var end = i + 1;
            while (end < lines.Count && !Heading().IsMatch(lines[end])) end++;
            var header = Enumerable.Range(i + 1, end - i - 1).FirstOrDefault(j => IsColumnHeader(Cells(lines[j])), -1);
            if (header < 0) continue;
            // A Parquet dataset section names a .parquet path or types its columns as Parquet types; other column tables (e.g. database
            // entities in a data-model document) are not Parquet contracts.
            if (dataPath is null && !Cells(lines[header]).Any(c => c.Contains("Parquet", StringComparison.OrdinalIgnoreCase))) continue;
            var container = dataPath?.Split('/')[0];
            var key = container ?? Slug(title);
            var columns = new List<DatasetContractColumn>();
            var j2 = header + 2;
            var cellsHeader = Cells(lines[header]);
            int typeIx = cellsHeader.FindIndex(c => c.Contains("type", StringComparison.OrdinalIgnoreCase)),
                nullIx = cellsHeader.FindIndex(c => c.StartsWith("Null", StringComparison.OrdinalIgnoreCase)),
                contentIx = cellsHeader.FindIndex(c => c.Contains("Content", StringComparison.OrdinalIgnoreCase) || c.Contains("Description", StringComparison.OrdinalIgnoreCase));
            for (; j2 < end && lines[j2].TrimStart().StartsWith('|'); j2++)
            {
                var c = Cells(lines[j2]);
                if (c.Count <= Math.Max(typeIx, nullIx)) continue;
                var name = Backticks(c[0]).FirstOrDefault() ?? c[0].Trim();
                if (name.Length == 0) continue;
                var documented = Plain(c[typeIx]);
                var (physical, logical, nested) = TypeOf(documented);
                var content = contentIx >= 0 && contentIx < c.Count ? c[contentIx] : "";
                var (values, format) = ValuesOf(content);
                columns.Add(new DatasetContractColumn
                {
                    Name = name, DocumentedType = documented, PhysicalType = physical, LogicalType = logical, Nested = nested,
                    Nullable = Plain(c[nullIx]).StartsWith("yes", StringComparison.OrdinalIgnoreCase), AllowedValues = values, Format = format, Content = Plain(content), Line = j2 + 1,
                });
            }
            var requirements = new List<DatasetContractRequirement>();
            foreach (var col in columns)
                requirements.Add(new DatasetContractRequirement { Id = $"col-{col.Name}", Category = "Schema", Text = $"Column {col.Name}: {col.DocumentedType}, {(col.Nullable ? "nullable" : "not null")}{(col.AllowedValues.Count > 0 ? $", values {string.Join(", ", col.AllowedValues)}" : col.Format is { } f ? $", format {f}" : "")}.", Line = col.Line, Machine = true });
            var sortKeys = new List<string>();
            // Prose of the section (outside the table): every sentence is a requirement; recognised ones are machine-checkable.
            // Markdown wraps prose: sentences are read from whole paragraphs, each attributed to the line it starts on.
            foreach (var (sentence, n) in ParagraphSentences(lines, i + 1, end))
            {
                var (category, machine, reason) = Classify(sentence, columns);
                if (SortedBy().Match(sentence) is { Success: true } sm) sortKeys = Backticks(sm.Value).Where(b => columns.Any(c => c.Name == b)).ToList();
                requirements.Add(new DatasetContractRequirement { Id = $"L{n}" + (requirements.Count(r => r.Line == n) is var cnt and > 0 ? $".{cnt + 1}" : ""), Category = category, Text = sentence, Line = n, Machine = machine, Reason = reason });
            }
            foreach (var col in columns.Where(c => c.Nullable && OnlyFor().Match(c.Content) is { Success: true }))
                requirements.Add(new DatasetContractRequirement { Id = $"col-{col.Name}-condition", Category = "Conditional", Text = $"{col.Name}: {col.Content}", Line = col.Line, Machine = true });
            requirements.AddRange(shared);
            var classified = renames.Select(r => r with
            {
                Kind = columns.Any(c => c.Name == r.To) ? "column" : columns.Any(c => c.AllowedValues.Contains(r.To)) ? "value" : "",
            }).Where(r => r.Kind.Length > 0).DistinctBy(r => (r.From, r.To, r.Kind)).ToList();
            result.Add(new DatasetContract
            {
                Key = key, Title = Plain(title.Split(" — ")[0].Split(" - ")[0]), Version = version, Governance = governance, GovernanceReason = governanceReason,
                DocumentPath = path, DocumentSha256 = sha256, Container = container, PathPattern = dataPath is null ? null : string.Join('/', dataPath.Split('/').Skip(1)),
                Columns = columns, SortKeys = sortKeys, Requirements = requirements, Renames = classified, PredecessorVersion = predecessor, ManifestFields = manifestFields, Line = i + 1,
            });
        }
        return result;
    }

    private static (string Category, bool Machine, string? Reason) Classify(string sentence, List<DatasetContractColumn> columns)
    {
        var s = sentence;
        if (Identity().IsMatch(s) && SumTerms(s, columns) is not null) return ("Aggregate consistency", true, null);
        if (AddsUpTo().Match(s) is { Success: true } a && columns.Any(c => c.AllowedValues.Contains(a.Groups["v"].Value))) return ("Aggregate consistency", true, null);
        if (OnlyWith().Match(s) is { Success: true }) return ("Allowed combinations", true, null);
        if (SortedBy().IsMatch(s)) return ("Ordering", true, null);
        if (s.Contains("Never present", StringComparison.OrdinalIgnoreCase) || s.Contains("fødselsnummer", StringComparison.OrdinalIgnoreCase) && s.Contains("never", StringComparison.OrdinalIgnoreCase))
            return ("Confidentiality", true, null);
        if (Kode67().IsMatch(s))
            return ("Confidentiality", false, "Protected-record exclusion cannot be proven from the file: it needs a correlated protected-child test scenario with an expected result (NEEDS DOMAIN DECISION).");
        if (s.Contains("percentage", StringComparison.OrdinalIgnoreCase)) return ("Presentation", false, "Not a property of a single file value; covered by the closed column set (no unexpected column).");
        return ("Description", false, "Descriptive or producer-side statement; no rule is generated from prose BirkNext does not recognise.");
    }

    /// <summary>Backticked identity <c>A + B + C = D</c> whose terms are values of one enumerated column; null when not exactly that.</summary>
    internal static (List<string> Terms, string Total, DatasetContractColumn Column, string? GroupBy)? SumTerms(string sentence, List<DatasetContractColumn> columns)
    {
        if (Identity().Match(sentence) is not { Success: true } m) return null;
        var left = m.Groups["l"].Value.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
        var right = m.Groups["r"].Value.Trim();
        var column = columns.FirstOrDefault(c => c.AllowedValues.Contains(right) && left.All(c.AllowedValues.Contains));
        if (column is null) return null;
        var group = ForEach().Match(sentence) is { Success: true } g && columns.Any(c => c.Name == g.Groups["c"].Value) ? g.Groups["c"].Value : null;
        return (left, right, column, group);
    }

    private static bool DeterminismMachine(string sentence) =>
        sentence.Contains("row group", StringComparison.OrdinalIgnoreCase) || CompressionName().IsMatch(sentence);

    internal static int? RowGroups(string sentence) =>
        sentence.Contains("one row group", StringComparison.OrdinalIgnoreCase) || sentence.Contains("single row group", StringComparison.OrdinalIgnoreCase) ? 1 : null;

    internal static string? Compression(string sentence) => CompressionName().Match(sentence) is { Success: true } m ? m.Groups["c"].Value.ToUpperInvariant() : null;

    private static (ContractGovernance, string) Governance(List<string> lines, string version)
    {
        var status = lines.FirstOrDefault(l => StatusLabel().IsMatch(l));
        if (status is not null && status.Contains("approved", StringComparison.OrdinalIgnoreCase)) return (ContractGovernance.Approved, Plain(status));
        if (version.Contains("draft", StringComparison.OrdinalIgnoreCase) || version.Contains("utkast", StringComparison.OrdinalIgnoreCase))
            return (ContractGovernance.Draft, $"Contract version {version} is a draft" + (lines.FirstOrDefault(l => l.Contains("planning baseline", StringComparison.OrdinalIgnoreCase)) is not null ? " (the document calls it the planning baseline)." : "."));
        return (ContractGovernance.Unknown, version.Length == 0 ? "No contract version stated." : $"Contract version {version}; no approval status stated.");
    }

    internal static (string? Physical, string? Logical, bool Nested) TypeOf(string documented)
    {
        var t = documented.Replace("`", "").Trim();
        if (t.StartsWith("LIST", StringComparison.OrdinalIgnoreCase) || t.StartsWith("STRUCT", StringComparison.OrdinalIgnoreCase) || t.StartsWith("MAP", StringComparison.OrdinalIgnoreCase))
            return (null, t.Split('<')[0].ToUpperInvariant(), true);
        var inner = TypeParen().Match(t) is { Success: true } p ? p.Groups["i"].Value.Trim() : null;
        var head = t.Split('(')[0].Trim().ToUpperInvariant();
        string? Logical(string? s) => s is null ? null : s.ToUpperInvariant() switch { "UTF8" or "STRING" => "STRING", var x => x };
        return head switch
        {
            "UTF8" or "STRING" => ("BYTE_ARRAY", "STRING", false),
            "BOOLEAN" or "INT32" or "INT64" or "INT96" or "FLOAT" or "DOUBLE" or "BYTE_ARRAY" or "FIXED_LEN_BYTE_ARRAY" => (head, Logical(inner), false),
            "DATE" => ("INT32", "DATE", false),
            _ => (null, Logical(head.Length > 0 ? head : null), false),
        };
    }

    internal static (List<string> Values, string? Format) ValuesOf(string content)
    {
        var c = content.Trim();
        if (FormatOnly().Match(c) is { Success: true } f) return ([], f.Groups["f"].Value);
        if (EnumOnly().IsMatch(c)) return (Backticks(c), null);
        if (ValuesList().Match(c) is { Success: true } v) return (Backticks(v.Groups["v"].Value), null);
        return ([], null);
    }

    private static IEnumerable<(string From, string To)> Arrows(string line)
    {
        foreach (Match m in Arrow().Matches(line)) yield return (m.Groups["a"].Value, m.Groups["b"].Value);
    }

    /// <summary>Indices of the lines that belong to a block about this contract: a starting line that names the version transition
    /// (<c>`x` → `version`</c>) or a container of the contract, and the following lines indented deeper than it.</summary>
    internal static IEnumerable<int> ScopedLines(List<string> lines, string version, IReadOnlyCollection<string> containers)
    {
        static int Indent(string l) => l.Length - l.TrimStart().Length;
        for (var i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            var starts = (version.Length > 0 && Arrows(l).Any(a => a.To == version)) || containers.Any(c => l.Contains('`' + c, StringComparison.Ordinal));
            if (!starts) continue;
            yield return i;
            var indent = Indent(l);
            var j = i + 1;
            for (; j < lines.Count && (lines[j].Trim().Length == 0 || Indent(lines[j]) > indent); j++)
                if (lines[j].Trim().Length > 0) yield return j;
            i = j - 1;
        }
    }

    /// <summary>Sentences of the prose paragraphs in [from, to): consecutive non-blank lines outside tables, code fences and list-item starts
    /// form one paragraph (Markdown soft wraps); each sentence carries the 1-based line it starts on.</summary>
    internal static IEnumerable<(string Sentence, int Line)> ParagraphSentences(List<string> lines, int from, int to)
    {
        var fence = false;
        var parts = new List<(string Text, int Line)>();
        IEnumerable<(string, int)> Flush()
        {
            if (parts.Count == 0) yield break;
            var sb = new StringBuilder();
            var starts = new List<(int Offset, int Line)>();
            foreach (var (t, n) in parts) { if (sb.Length > 0) sb.Append(' '); starts.Add((sb.Length, n)); sb.Append(t); }
            parts.Clear();
            var text = sb.ToString();
            var offset = 0;
            foreach (var raw in SentenceSplit().Split(text))
            {
                var at = text.IndexOf(raw, offset, StringComparison.Ordinal);
                offset = at < 0 ? offset : at + raw.Length;
                var t = raw.Trim();
                if (t.Length < 12) continue;
                var line = starts.LastOrDefault(s => s.Offset <= Math.Max(0, at)).Line;
                yield return (t, line == 0 ? starts[0].Line : line);
            }
        }
        for (var k = from; k < to && k < lines.Count; k++)
        {
            var l = lines[k];
            var trimmed = l.Trim();
            if (trimmed.StartsWith("```", StringComparison.Ordinal)) { foreach (var x in Flush()) yield return x; fence = !fence; continue; }
            if (fence) continue;
            if (trimmed.Length == 0 || trimmed.StartsWith('|') || Heading().IsMatch(l)) { foreach (var x in Flush()) yield return x; continue; }
            if (trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed.StartsWith("* ", StringComparison.Ordinal) || trimmed.StartsWith('>')) foreach (var x in Flush()) yield return x;
            parts.Add((trimmed.TrimStart('-', '*', '>', ' '), k + 1));
        }
        foreach (var x in Flush()) yield return x;
    }

    private static IEnumerable<(string Sentence, int Line)> Sentences(string line, int lineNo)
    {
        var text = line.Trim().TrimStart('-', '*', '>', ' ');
        if (text.StartsWith("```", StringComparison.Ordinal) || text.StartsWith('{') || text.StartsWith('"') || text.StartsWith('}') || text.StartsWith(']')) yield break;
        foreach (var s in SentenceSplit().Split(text))
        {
            var t = s.Trim();
            if (t.Length >= 12) yield return (t, lineNo);
        }
    }

    internal static List<string> Lines(string text) => [.. text.Replace("\r\n", "\n").Split('\n')];

    internal static List<string> Cells(string line)
    {
        var t = line.Trim();
        if (!t.StartsWith('|')) return [];
        return [.. t.Trim('|').Split('|').Select(c => c.Trim())];
    }

    private static bool IsColumnHeader(List<string> cells) =>
        cells.Count >= 3 && cells[0].Equals("Column", StringComparison.OrdinalIgnoreCase) && cells.Any(c => c.Contains("type", StringComparison.OrdinalIgnoreCase))
        && cells.Any(c => c.StartsWith("Null", StringComparison.OrdinalIgnoreCase));

    internal static List<string> Backticks(string text) => [.. Tick().Matches(text).Select(m => m.Groups["v"].Value)];

    private static string Plain(string text) => text.Replace("**", "").Replace("`", "").Trim();

    private static string Slug(string text)
    {
        var sb = new StringBuilder();
        foreach (var ch in text.ToLowerInvariant()) sb.Append(char.IsLetterOrDigit(ch) ? ch : '-');
        return string.Join('-', sb.ToString().Split('-', StringSplitOptions.RemoveEmptyEntries));
    }

    [GeneratedRegex(@"^\s{0,3}#{2,4}\s+(?<t>.+)$")] private static partial Regex Heading();
    [GeneratedRegex(@"`(?<v>[^`]+)`")] private static partial Regex Tick();
    [GeneratedRegex(@"(?i)(contract\s+version|kontraktversjon)")] private static partial Regex VersionLabel();
    [GeneratedRegex(@"(?i)^\s*\**(status|governance|approval)\**\s*:")] private static partial Regex StatusLabel();
    [GeneratedRegex(@"`(?<a>[^`]+)`\s*(?:→|->)\s*`(?<b>[^`]+)`")] private static partial Regex Arrow();
    [GeneratedRegex(@"`(?<l>[A-Z0-9_]+(?:\s*\+\s*[A-Z0-9_]+)+)\s*=\s*(?<r>[A-Z0-9_]+)`")] private static partial Regex Identity();
    [GeneratedRegex(@"for each\s+`(?<c>[^`]+)`", RegexOptions.IgnoreCase)] private static partial Regex ForEach();
    [GeneratedRegex(@"add(?:s)? up to\s+`(?<v>[^`]+)`", RegexOptions.IgnoreCase)] private static partial Regex AddsUpTo();
    [GeneratedRegex(@"(?<vals>(?:`[^`]+`(?:\s*(?:,|and)\s*)?)+)\s+appear only with\s+`(?<c>[A-Za-z0-9_]+)\s*=\s*(?<v>[^`]+)`", RegexOptions.IgnoreCase)] internal static partial Regex OnlyWith();
    [GeneratedRegex(@"sorted by\s+(?:`[^`]+`(?:\s*,\s*|\s+and\s+)?)+", RegexOptions.IgnoreCase)] private static partial Regex SortedBy();
    [GeneratedRegex(@"^Only for\s+`?(?<v>[A-Za-z0-9_]+)`?", RegexOptions.IgnoreCase)] internal static partial Regex OnlyFor();
    [GeneratedRegex(@"(?i)kode\s*6\s*/\s*7")] private static partial Regex Kode67();
    [GeneratedRegex(@"(?i)compression\s*\((?<c>snappy|gzip|zstd|lz4|brotli|uncompressed)\)")] private static partial Regex CompressionName();
    [GeneratedRegex(@"""(?<k>[A-Za-z][A-Za-z0-9]*)""\s*:")] private static partial Regex JsonKey();
    [GeneratedRegex(@"\((?<i>.+)\)\s*$")] private static partial Regex TypeParen();
    [GeneratedRegex(@"^`(?<f>Y{4}(?:-MM(?:-DD)?)?)`$")] private static partial Regex FormatOnly();
    [GeneratedRegex(@"^`[A-Z0-9_]+`(?:\s*(?:,|or|and)\s*`[A-Z0-9_]+`)+$")] private static partial Regex EnumOnly();
    [GeneratedRegex(@"\(values\s+(?<v>`[^)]*?)(?:;|\))", RegexOptions.IgnoreCase)] private static partial Regex ValuesList();
    [GeneratedRegex(@"(?<=[.!?])\s+(?=[A-Z`*])")] private static partial Regex SentenceSplit();

    internal static string Invariant(long n) => n.ToString(CultureInfo.InvariantCulture);
}
