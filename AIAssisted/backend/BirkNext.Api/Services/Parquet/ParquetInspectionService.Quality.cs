using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BirkNext.Api.Services.ContractTesting;
using BirkNext.Parquet;
using Path = System.IO.Path;

namespace BirkNext.Api.Services.Parquet;

/// <summary>
/// How rules see a file validated against a contract: contract column names resolve to the file's columns through documented or declared
/// mappings, values translate through documented value renames, plus the attached manifest and the approved protected-identity canaries.
/// Values never leave the scan: results carry counts and row numbers; group detail and drill-down filters are live only.
/// </summary>
public sealed record QualityContext
{
    public static readonly QualityContext None = new();
    public Dictionary<string, string> ColumnMap { get; init; } = new(StringComparer.Ordinal);
    /// <summary>contract column → (actual value → contract value), only for columns reached through a rename.</summary>
    public Dictionary<string, Dictionary<string, string>> ValueMap { get; init; } = new(StringComparer.Ordinal);
    public ParquetManifestSummary? Manifest { get; init; }
    public IReadOnlyList<string> Canaries { get; init; } = [];
    public bool CanaryCatalogAvailable { get; init; }
    public string? ContractVersion { get; init; }
    public string? RuleSetRef { get; init; }
    public string Map(string contractColumn) => ColumnMap.TryGetValue(contractColumn, out var a) ? a : contractColumn;
    public bool Mapped(string contractColumn) => ColumnMap.TryGetValue(contractColumn, out var a) && a != contractColumn;
    public string? Translate(string contractColumn, string? value) =>
        value is not null && ValueMap.TryGetValue(contractColumn, out var m) && m.TryGetValue(value, out var t) ? t : value;
    /// <summary>The file's values that mean a contract value (for drill-down filters on the file's own values).</summary>
    public List<string> Untranslate(string contractColumn, IEnumerable<string> values)
    {
        var list = values.ToList();
        if (ValueMap.TryGetValue(contractColumn, out var m)) list.AddRange(m.Where(kv => list.Contains(kv.Value)).Select(kv => kv.Key));
        return [.. list.Distinct()];
    }
}

public sealed partial class ParquetInspectionService
{
    public const int MaxGroups = 100_000;
    public const int MaxCategoryValues = 50;
    public const long MaxManifestBytes = 64 * 1024;
    private string ManifestPath(Guid id) => Path.Combine(Root, id.ToString("N") + ".manifest.json");

    /// <summary>Live sessions (not expired), newest first — for "recent files" (the caller validates its own ids; no file content).</summary>
    public List<ParquetOverview> Sessions(IEnumerable<Guid> ids) => [.. ids.Distinct().Take(20).Select(Session).Where(s => s is not null).Select(s => s!.Value.Overview).OrderByDescending(o => o.OpenedAt)];

    // ── Manifest (optional, attached to a session; only the documented fields are kept) ──────────────────────────────────────

    public (ParquetManifestSummary? Manifest, string? Error) AttachManifest(Guid id, string json)
    {
        if (Session(id) is not { } s) return (null, "The Parquet session has expired; open the file again.");
        if (Encoding.UTF8.GetByteCount(json) > MaxManifestBytes) return (null, "The manifest exceeds 64 KB.");
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (null, "The manifest is not a JSON object.");
            string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? Clip(v.GetString()) : null;
            long? Num(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;
            JsonElement? entry = null;
            if (root.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array)
            {
                var list = files.EnumerateArray().Where(f => f.ValueKind == JsonValueKind.Object).ToList();
                entry = list.FirstOrDefault(f => Str(f, "sha256") is { } sha && sha.Equals(s.Overview.Sha256, StringComparison.OrdinalIgnoreCase)) is { ValueKind: JsonValueKind.Object } bySha ? bySha
                    : list.Count == 1 ? list[0] : null;
            }
            var summary = new ParquetManifestSummary(Str(root, "period"), Str(root, "contractVersion"), Str(root, "lockedAt"),
                entry is { } e1 ? Str(e1, "name") : null, entry is { } e2 ? Str(e2, "sha256")?.ToLowerInvariant() : null, entry is { } e3 ? Num(e3, "rowCount") : null,
                entry is { } e4 ? Num(e4, "bytes") : null, "Attached by the tester (not read from storage)");
            File.WriteAllText(ManifestPath(id), JsonSerializer.Serialize(summary, Json));
            return (summary, null);
        }
        catch (JsonException) { return (null, "The manifest is not valid JSON."); }
    }

    public ParquetManifestSummary? Manifest(Guid id)
    {
        try { return File.Exists(ManifestPath(id)) ? JsonSerializer.Deserialize<ParquetManifestSummary>(File.ReadAllText(ManifestPath(id)), Json) : null; }
        catch (Exception ex) when (ex is IOException or JsonException) { return null; }
    }

    // ── Validation: one engine for every rule origin ─────────────────────────────────────────────────────────────────────────

    public (ParquetValidation? Validation, string? Error) Validate(Guid id, ParquetRuleSet rules, CancellationToken ct) => Validate(id, rules, QualityContext.None, ct);

    public (ParquetValidation? Validation, string? Error) Validate(Guid id, ParquetRuleSet rules, QualityContext context, CancellationToken ct)
    {
        if (Session(id) is not { } s) return (null, "The Parquet session has expired; open the file again.");
        var active = rules.Rules.Where(r => r.Review == RuleReviewState.Active).ToList();
        if (active.Count is 0 or > 200) return (null, "Give between 1 and 200 active rules.");
        var problems = active.Select(RuleProblem).Where(p => p is not null).ToList();
        if (problems.Count > 0) return (null, problems[0]);
        var results = new List<ParquetRuleResult>();
        var scanned = false;
        foreach (var rule in active)
        {
            ct.ThrowIfCancellationRequested();
            var r = Evaluate(s.File, s.Overview, rule, context, ct);
            if (r.Mode == EvaluationMode.Exact && r.RowsExamined > 0) scanned = true;
            results.Add(r with
            {
                RuleName = rule.Name ?? Describe(rule), Origin = rule.Origin, ContractVersion = rule.ContractVersion ?? context.ContractVersion, RequirementIds = rule.RequirementIds,
                Severity = rule.Severity, EvidenceRef = $"rule:{rule.Id}@v{rule.RuleVersion}/file:{s.Overview.Sha256[..12]}",
            });
        }
        var scope = scanned ? $"Full scan of all {N(s.File.Rows)} rows" : "Schema and metadata only";
        if (context.ColumnMap.Any(kv => kv.Key != kv.Value)) scope += $" · through column mapping {string.Join(", ", context.ColumnMap.Where(kv => kv.Key != kv.Value).Select(kv => $"{kv.Value}→{kv.Key}"))}";
        return (new ParquetValidation(rules.Name, rules.Version, results, scanned ? s.File.Rows : 0, scope) { ContractVersion = context.ContractVersion, RuleSetRef = context.RuleSetRef }, null);
    }

    /// <summary>Parameter validation before any scan: a malformed rule is refused, never evaluated half-way.</summary>
    internal static string? RuleProblem(ParquetRule r)
    {
        string? Need(bool ok, string what) => ok ? null : $"Rule {Clip(r.Id)}: {what}.";
        return r.Kind switch
        {
            ParquetRuleKind.ColumnExists or ParquetRuleKind.NotNull or ParquetRuleKind.TypeIs => Need(!string.IsNullOrWhiteSpace(r.Column), "choose a column")
                ?? (r.Kind == ParquetRuleKind.TypeIs ? Need(!string.IsNullOrWhiteSpace(r.Expected), "give the expected type") : null),
            ParquetRuleKind.Unique => Need(!string.IsNullOrWhiteSpace(r.Column) || r.Columns.Count > 0, "choose one or more columns"),
            ParquetRuleKind.AllowedValues => Need(!string.IsNullOrWhiteSpace(r.Column) && r.Values.Count > 0, "choose a column and its allowed values"),
            ParquetRuleKind.Range => Need(!string.IsNullOrWhiteSpace(r.Column) && (r.Min is not null || r.Max is not null), "choose a column and at least one bound"),
            ParquetRuleKind.RecordCount or ParquetRuleKind.RowGroupCount => Need(long.TryParse(r.Expected, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= 0, "give a whole number"),
            ParquetRuleKind.Pattern => Need(!string.IsNullOrWhiteSpace(r.Column) && FormatRegex(r.Expected) is not null, "choose a column and a format (YYYY, YYYY-MM or YYYY-MM-DD)"),
            ParquetRuleKind.NullUnless or ParquetRuleKind.RequiredWhen => Need(!string.IsNullOrWhiteSpace(r.Column) && !string.IsNullOrWhiteSpace(r.WhenColumn) && r.WhenValues.Count > 0, "choose the column, the condition column and its values"),
            ParquetRuleKind.OnlyWith => Need(!string.IsNullOrWhiteSpace(r.Column) && r.Values.Count > 0 && !string.IsNullOrWhiteSpace(r.WhenColumn) && r.WhenValues.Count > 0, "choose the values and the required combination"),
            ParquetRuleKind.SumIdentity => Need(!string.IsNullOrWhiteSpace(r.Column) && r.Values.Count > 0 && !string.IsNullOrWhiteSpace(r.Expected) && !string.IsNullOrWhiteSpace(r.ValueColumn), "choose the terms, the total and the measured column"),
            ParquetRuleKind.RollUp => Need(!string.IsNullOrWhiteSpace(r.Column) && !string.IsNullOrWhiteSpace(r.Expected) && !string.IsNullOrWhiteSpace(r.ValueColumn), "choose the category column, its total value and the measured column"),
            ParquetRuleKind.SortedBy => Need(r.Columns.Count > 0, "choose the sort columns"),
            ParquetRuleKind.Compression => Need(!string.IsNullOrWhiteSpace(r.Expected), "give the expected codec"),
            _ => null,
        };
    }

    internal static string Describe(ParquetRule r) => r.Kind switch
    {
        ParquetRuleKind.ColumnExists => $"{r.Column} is present",
        ParquetRuleKind.TypeIs => $"{r.Column} has type {r.Expected}",
        ParquetRuleKind.NotNull => $"{r.Column} is never null",
        ParquetRuleKind.Unique => $"{string.Join(" + ", r.Columns.Count > 0 ? r.Columns : [r.Column ?? ""])} is unique",
        ParquetRuleKind.AllowedValues => $"{r.Column} uses only allowed values",
        ParquetRuleKind.Range => $"{r.Column} is within {r.Min ?? "−∞"} … {r.Max ?? "+∞"}",
        ParquetRuleKind.RecordCount => $"The file has {r.Expected} rows",
        ParquetRuleKind.Pattern => $"{r.Column} has format {r.Expected}",
        ParquetRuleKind.NullUnless => $"{r.Column} is empty unless {r.WhenColumn} is {string.Join("/", r.WhenValues)}",
        ParquetRuleKind.RequiredWhen => $"{r.Column} is set when {r.WhenColumn} is {string.Join("/", r.WhenValues)}",
        ParquetRuleKind.OnlyWith => $"{string.Join("/", r.Values)} appear only with {r.WhenColumn} = {string.Join("/", r.WhenValues)}",
        ParquetRuleKind.SumIdentity => $"{string.Join(" + ", r.Values)} = {r.Expected} per {string.Join(", ", r.GroupColumns)}",
        ParquetRuleKind.RollUp => $"{r.Column} values add up to {r.Expected}",
        ParquetRuleKind.SortedBy => $"Rows sorted by {string.Join(", ", r.Columns)}",
        ParquetRuleKind.RowGroupCount => $"{r.Expected} row group(s)",
        ParquetRuleKind.Compression => $"Compression {r.Expected}",
        ParquetRuleKind.Manifest => "Matches the delivery manifest",
        ParquetRuleKind.NoIdentifierValues => "No personal identification numbers in undeclared columns",
        ParquetRuleKind.NoProtectedCanary => "No protected-identity canary value",
        _ => r.Kind.ToString(),
    };

    private ParquetRuleResult Evaluate(ParquetFile file, ParquetOverview ov, ParquetRule rule, QualityContext ctx, CancellationToken ct)
    {
        ParquetRuleResult R(ParquetRuleOutcome o, string expected, string actual, string scope, long rows, List<long>? failing, string why, EvaluationMode mode = EvaluationMode.Exact,
            long affected = 0, List<string>? details = null, List<ParquetFilter>? drill = null) =>
            new(rule.Id, rule.Kind, rule.Column, o, expected, actual, scope, rows, failing ?? [], why)
            { Mode = mode, AffectedRows = affected, Details = details ?? [], DrillFilters = drill ?? [] };
        ParquetFile.Leaf? Leaf(string? contractColumn) => contractColumn is null ? null : file.Leaves.FirstOrDefault(l => l.Path == ctx.Map(contractColumn));
        bool Present(string contractColumn) => Leaf(contractColumn) is not null || file.Leaves.Any(l => l.Path.StartsWith(ctx.Map(contractColumn) + ".", StringComparison.Ordinal));
        string Via(string? c) => c is not null && ctx.Mapped(c) ? $" (file column {ctx.Map(c)})" : "";

        switch (rule.Kind)
        {
            case ParquetRuleKind.ColumnExists:
                var present = Present(rule.Column!);
                return R(present ? ParquetRuleOutcome.Pass : ParquetRuleOutcome.Fail, "present", present ? "present" + Via(rule.Column) : "absent", "Schema", 0, null,
                    present ? "The column is in the schema." : "The column is not in the file's schema.", EvaluationMode.Metadata);
            case ParquetRuleKind.RecordCount:
                var ok = long.TryParse(rule.Expected, NumberStyles.Integer, CultureInfo.InvariantCulture, out var expectedRows);
                return R(!ok ? ParquetRuleOutcome.Inconclusive : expectedRows == file.Rows ? ParquetRuleOutcome.Pass : ParquetRuleOutcome.Fail, rule.Expected ?? "", N(file.Rows),
                    "File metadata (row count)", 0, null, "Compared with the row count in the file's footer.", EvaluationMode.Metadata);
            case ParquetRuleKind.RowGroupCount:
                var groups = file.RowGroups.Count;
                return R(groups.ToString(CultureInfo.InvariantCulture) == rule.Expected ? ParquetRuleOutcome.Pass : ParquetRuleOutcome.Fail, rule.Expected ?? "", groups.ToString(CultureInfo.InvariantCulture),
                    "File metadata (row groups)", 0, null, "Row groups as written in the footer.", EvaluationMode.Metadata);
            case ParquetRuleKind.Compression:
                var codecs = ov.Columns.SelectMany(c => c.Codecs).Distinct().ToList();
                var codecOk = codecs.Count > 0 && codecs.All(c => c.Equals(rule.Expected, StringComparison.OrdinalIgnoreCase));
                return R(codecOk ? ParquetRuleOutcome.Pass : ParquetRuleOutcome.Fail, rule.Expected ?? "", string.Join(", ", codecs), "File metadata (column chunks)", 0, null,
                    "Compression codec of every column chunk.", EvaluationMode.Metadata);
            case ParquetRuleKind.TypeIs:
                if (Leaf(rule.Column) is not { } tl)
                    return R(ParquetRuleOutcome.Fail, rule.Expected ?? "", "column absent", "Schema", 0, null, "The column is not in the file's schema.", EvaluationMode.Metadata);
                var actual = $"{ParquetFile.PhysicalNames[tl.Physical]}{(ParquetFile.LogicalName(tl.Node) is { } lt ? " / " + lt : "")}";
                var pass = rule.Expected is { Length: > 0 } e && (string.Equals(ParquetFile.PhysicalNames[tl.Physical], e, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(ParquetFile.LogicalName(tl.Node), e, StringComparison.OrdinalIgnoreCase) || (ParquetFile.LogicalName(tl.Node)?.StartsWith(e + "(", StringComparison.OrdinalIgnoreCase) ?? false));
                return R(pass ? ParquetRuleOutcome.Pass : ParquetRuleOutcome.Fail, rule.Expected ?? "", actual + Via(rule.Column), "Schema", 0, null, "Physical or logical type as written in the schema.", EvaluationMode.Metadata);
        }

        // Row-level rules: every referenced column must exist and be readable; otherwise the rule is Inconclusive (BLOCKED in coverage), never a pass.
        var needed = Needed(rule, ov).Distinct().ToList();
        var leaves = new Dictionary<string, ParquetFile.Leaf>(StringComparer.Ordinal);
        foreach (var c in needed)
        {
            if (Leaf(c) is not { } l) return R(ParquetRuleOutcome.Inconclusive, Describe(rule), $"column {c} absent", "—", 0, null, $"The rule needs column {c}{(ctx.Mapped(c) ? $" (mapped to {ctx.Map(c)})" : "")}, which is not in the file.", EvaluationMode.NotAvailable);
            if (l.NotReadable is { } unreadable) return R(ParquetRuleOutcome.Inconclusive, Describe(rule), "not readable", "—", 0, null, $"{c}: {unreadable}", EvaluationMode.NotAvailable);
            leaves[c] = l;
        }
        if (rule.Kind == ParquetRuleKind.Manifest && ctx.Manifest is null)
            return R(ParquetRuleOutcome.Inconclusive, "manifest fields match", "no manifest attached", "—", 0, null, "Attach the delivery's _manifest.json to check period, contract version, SHA-256, row count and bytes.", EvaluationMode.NotAvailable);
        if (rule.Kind == ParquetRuleKind.NoProtectedCanary && ctx.Canaries.Count == 0)
            return R(ParquetRuleOutcome.Inconclusive, "no protected canary value", "no canary available", "—", 0, null,
                ctx.CanaryCatalogAvailable ? "Test Data has no approved protected-identity reference to look for." : "Test Data could not be read.", EvaluationMode.NotAvailable);
        if (file.Rows > MaxScanRows) return R(ParquetRuleOutcome.Inconclusive, Describe(rule), "not scanned", "—", 0, null, $"Full scans are limited to {N(MaxScanRows)} rows.", EvaluationMode.NotAvailable);

        string scope = $"Full scan of all {N(file.Rows)} rows" + (needed.Any(ctx.Mapped) ? " (through the column mapping)" : "");
        var failing = new List<long>(); long failures = 0, row = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var formatRe = rule.Kind == ParquetRuleKind.Pattern ? FormatRegex(rule.Expected) : null;
        var groupsAcc = new Dictionary<string, (decimal Left, decimal Right, bool HasLeft, bool HasRight, List<string> Labels)>(StringComparer.Ordinal);
        string?[]? previous = null;
        var periods = new HashSet<string>(StringComparer.Ordinal);
        var identifierColumns = new HashSet<string>(StringComparer.Ordinal);
        var canaryHits = new HashSet<string>(StringComparer.Ordinal);
        long unattributed = 0;
        try
        {
            foreach (var group in file.RowGroups)
            {
                ct.ThrowIfCancellationRequested();
                var data = leaves.ToDictionary(kv => kv.Key, kv => file.ReadColumn(group.Index, kv.Value.Index), StringComparer.Ordinal);
                string? V(string c, int r) => ctx.Translate(c, ParquetFile.Format(data[c][r]));
                for (var r = 0; r < group.Rows; r++)
                {
                    row++;
                    bool bad;
                    switch (rule.Kind)
                    {
                        case ParquetRuleKind.NotNull: bad = data[rule.Column!][r] is null; break;
                        case ParquetRuleKind.Unique:
                            var keyCols = rule.Columns.Count > 0 ? rule.Columns : [rule.Column!];
                            var key = string.Join("\u001f", keyCols.Select(c => V(c, r) ?? "\u0000"));
                            bad = keyCols.Count == 1 && data[keyCols[0]][r] is null ? false : !seen.Add(key);
                            break;
                        case ParquetRuleKind.AllowedValues: bad = data[rule.Column!][r] is not null && !rule.Values.Contains(V(rule.Column!, r)!, StringComparer.Ordinal); break;
                        case ParquetRuleKind.Range:
                            var rv = data[rule.Column!][r];
                            bad = rv is not null && ((rule.Min is not null && Compare(rv, rule.Min) < 0) || (rule.Max is not null && Compare(rv, rule.Max) is > 0 or int.MinValue));
                            break;
                        case ParquetRuleKind.Pattern: bad = V(rule.Column!, r) is { } pv && !formatRe!.IsMatch(pv); break;
                        case ParquetRuleKind.NullUnless: bad = data[rule.Column!][r] is not null && !rule.WhenValues.Contains(V(rule.WhenColumn!, r) ?? "\u0000", StringComparer.Ordinal); break;
                        case ParquetRuleKind.RequiredWhen: bad = data[rule.Column!][r] is null && rule.WhenValues.Contains(V(rule.WhenColumn!, r) ?? "\u0000", StringComparer.Ordinal); break;
                        case ParquetRuleKind.OnlyWith:
                            bad = rule.Values.Contains(V(rule.Column!, r) ?? "\u0000", StringComparer.Ordinal) && !rule.WhenValues.Contains(V(rule.WhenColumn!, r) ?? "\u0000", StringComparer.Ordinal);
                            break;
                        case ParquetRuleKind.SortedBy:
                            var current = rule.Columns.Select(c => V(c, r)).ToArray();
                            bad = previous is not null && CompareKeys(previous, current) > 0;
                            previous = current;
                            break;
                        case ParquetRuleKind.SumIdentity or ParquetRuleKind.RollUp:
                            bad = false;
                            if (rule.Kind == ParquetRuleKind.RollUp && rule.WhenColumn is { } ex && rule.WhenValues.Contains(V(ex, r) ?? "\u0000", StringComparer.Ordinal)) break;
                            // A sum identity is per group: a row without a group value cannot be attributed (its not-null rule reports it).
                            if (rule.Kind == ParquetRuleKind.SumIdentity && rule.GroupColumns.Any(c => data[c][r] is null)) { unattributed++; break; }
                            var gk = string.Join("\u001f", rule.GroupColumns.Select(c => V(c, r) ?? "(null)"));
                            if (!groupsAcc.TryGetValue(gk, out var acc))
                            {
                                if (groupsAcc.Count >= MaxGroups) return R(ParquetRuleOutcome.Inconclusive, Describe(rule), "too many groups", "—", row, null, $"More than {N(MaxGroups)} groups.", EvaluationMode.NotAvailable);
                                acc = (0, 0, false, false, [.. rule.GroupColumns.Select(c => $"{c}={V(c, r) ?? "null"}")]);
                            }
                            var measure = V(rule.Column!, r);
                            if (data[rule.ValueColumn!][r] is not { } raw || !TryDecimal(raw, out var amount))
                                return R(ParquetRuleOutcome.Inconclusive, Describe(rule), "non-numeric or null measure", "—", row, [row], $"{rule.ValueColumn} must be a number on every row.", EvaluationMode.NotAvailable);
                            var isTotal = measure == rule.Expected;
                            var isTerm = rule.Kind == ParquetRuleKind.RollUp ? !isTotal : rule.Values.Contains(measure ?? "\u0000", StringComparer.Ordinal);
                            if (isTotal) acc = (acc.Left, acc.Right + amount, acc.HasLeft, true, acc.Labels);
                            else if (isTerm) acc = (acc.Left + amount, acc.Right, true, acc.HasRight, acc.Labels);
                            groupsAcc[gk] = acc;
                            break;
                        case ParquetRuleKind.Manifest:
                            bad = false;
                            if (rule.Column is { } pc && V(pc, r) is { } p) periods.Add(p);
                            break;
                        case ParquetRuleKind.NoIdentifierValues:
                            bad = false;
                            foreach (var c in needed)
                                if (data[c][r] is string text && ElevenDigits().IsMatch(text)) { bad = true; identifierColumns.Add(c); }
                            break;
                        case ParquetRuleKind.NoProtectedCanary:
                            bad = false;
                            foreach (var c in needed)
                                if (ParquetFile.Format(data[c][r]) is { Length: > 0 } text && ctx.Canaries.Any(k => text.Contains(k, StringComparison.Ordinal))) { bad = true; canaryHits.Add(c); }
                            break;
                        default: bad = false; break;
                    }
                    if (!bad) continue;
                    failures++;
                    if (failing.Count < 5) failing.Add(row);
                }
            }
        }
        catch (ParquetFormatException ex) { return R(ParquetRuleOutcome.Inconclusive, Describe(rule), "read failed", "—", row, null, ex.Message, EvaluationMode.NotAvailable); }

        switch (rule.Kind)
        {
            case ParquetRuleKind.SumIdentity or ParquetRuleKind.RollUp:
            {
                var bad = groupsAcc.Where(g => (g.Value.HasLeft || g.Value.HasRight) && g.Value.Left != g.Value.Right).ToList();
                var missingTotal = groupsAcc.Count(g => g.Value.HasLeft && !g.Value.HasRight);
                var details = bad.Take(20).Select(g => $"{string.Join(", ", g.Value.Labels)}: {(rule.Kind == ParquetRuleKind.RollUp ? "parts" : string.Join(" + ", rule.Values))} = {g.Value.Left.ToString(CultureInfo.InvariantCulture)}, {rule.Expected} = {(g.Value.HasRight ? g.Value.Right.ToString(CultureInfo.InvariantCulture) : "missing")}").ToList();
                var drill = bad.Count == 0 ? [] : rule.GroupColumns.Select((c, i) => (c, v: bad[0].Key.Split('\u001f')[i])).Where(x => x.v != "(null)")
                    .Select(x => new ParquetFilter(ctx.Map(x.c), ParquetFilterOperator.In, null, null, ctx.Untranslate(x.c, [x.v]))).ToList();
                return R(bad.Count == 0 ? (groupsAcc.Count == 0 ? ParquetRuleOutcome.Inconclusive : ParquetRuleOutcome.Pass) : ParquetRuleOutcome.Fail,
                    rule.Kind == ParquetRuleKind.RollUp ? $"parts add up to {rule.Expected} in every group" : $"{string.Join(" + ", rule.Values)} = {rule.Expected} in every group",
                    groupsAcc.Count == 0 ? "no group found" : bad.Count == 0 ? $"{N(groupsAcc.Count)} group(s) consistent" : $"{N(bad.Count)} of {N(groupsAcc.Count)} group(s) inconsistent" + (missingTotal > 0 ? $" ({N(missingTotal)} without a {rule.Expected} row)" : ""),
                    scope, row, null, $"Exact sums of {rule.ValueColumn} per {string.Join(", ", rule.GroupColumns)}. Group labels are shown here only and are not saved."
                        + (unattributed > 0 ? $" {N(unattributed)} row(s) without a {string.Join("/", rule.GroupColumns)} value could not be attributed." : ""),
                    affected: bad.Count, details: details, drill: drill);
            }
            case ParquetRuleKind.Manifest:
            {
                var m = ctx.Manifest!;
                var mismatches = new List<string>();
                if (m.ContractVersion is not null && rule.ContractVersion is not null && m.ContractVersion != rule.ContractVersion) mismatches.Add($"contract version {m.ContractVersion} ≠ {rule.ContractVersion}");
                if (m.Sha256 is null) mismatches.Add("no file entry with a SHA-256");
                else if (!m.Sha256.Equals(ov.Sha256, StringComparison.OrdinalIgnoreCase)) mismatches.Add("SHA-256 differs");
                if (m.RowCount is { } rc && rc != file.Rows) mismatches.Add($"rowCount {N(rc)} ≠ {N(file.Rows)}");
                if (m.Bytes is { } by && by != ov.SizeBytes) mismatches.Add($"bytes {N(by)} ≠ {N(ov.SizeBytes)}");
                if (m.Period is not null && rule.Column is not null && (periods.Count != 1 || !periods.Contains(m.Period))) mismatches.Add($"the {rule.Column} column holds {N(periods.Count)} value(s), not only the manifest period");
                return R(mismatches.Count == 0 ? ParquetRuleOutcome.Pass : ParquetRuleOutcome.Fail, "period, contract version, SHA-256, rowCount and bytes match",
                    mismatches.Count == 0 ? "all match" : string.Join("; ", mismatches), "Manifest + " + scope, row, null, "The manifest was attached by the tester; it was not read from storage.");
            }
        }
        var expectedText = rule.Kind switch
        {
            ParquetRuleKind.NotNull => "no nulls",
            ParquetRuleKind.Unique => $"unique {(rule.Columns.Count > 1 ? "combinations" : "values")}",
            ParquetRuleKind.AllowedValues => string.Join(", ", rule.Values),
            ParquetRuleKind.Range => $"{rule.Min ?? "−∞"} … {rule.Max ?? "+∞"}",
            ParquetRuleKind.Pattern => $"format {rule.Expected}",
            ParquetRuleKind.SortedBy => $"ascending by {string.Join(", ", rule.Columns)} (ordinal, null first)",
            ParquetRuleKind.NoIdentifierValues => "no 11-digit identification numbers",
            ParquetRuleKind.NoProtectedCanary => "no protected canary value",
            _ => Describe(rule),
        };
        var why = rule.Kind switch
        {
            ParquetRuleKind.NoIdentifierValues => failures == 0 ? $"Every value of {N(needed.Count)} column(s) was examined. Values are never shown." : $"Columns: {string.Join(", ", identifierColumns)}. Values are never shown.",
            ParquetRuleKind.NoProtectedCanary => failures == 0
                ? "No approved protected-identity canary was found. Absence is not proof that a protected child was handled correctly: that needs a correlated run with an expected result."
                : $"A protected-identity canary appears in {string.Join(", ", canaryHits)}. The value is never shown.",
            _ => failures == 0 ? "Every row was examined." : "Row numbers of the first violations are listed (values are not).",
        };
        var drillFilters = failures == 0 ? new List<ParquetFilter>() : rule.Kind switch
        {
            ParquetRuleKind.NotNull => [new(ctx.Map(rule.Column!), ParquetFilterOperator.IsNull)],
            ParquetRuleKind.AllowedValues => [new(ctx.Map(rule.Column!), ParquetFilterOperator.NotIn, null, null, ctx.Untranslate(rule.Column!, rule.Values))],
            ParquetRuleKind.NullUnless => [new(ctx.Map(rule.Column!), ParquetFilterOperator.NotNull), new(ctx.Map(rule.WhenColumn!), ParquetFilterOperator.NotIn, null, null, ctx.Untranslate(rule.WhenColumn!, rule.WhenValues))],
            ParquetRuleKind.RequiredWhen => [new(ctx.Map(rule.Column!), ParquetFilterOperator.IsNull), new(ctx.Map(rule.WhenColumn!), ParquetFilterOperator.In, null, null, ctx.Untranslate(rule.WhenColumn!, rule.WhenValues))],
            ParquetRuleKind.OnlyWith => [new(ctx.Map(rule.Column!), ParquetFilterOperator.In, null, null, ctx.Untranslate(rule.Column!, rule.Values)), new(ctx.Map(rule.WhenColumn!), ParquetFilterOperator.NotIn, null, null, ctx.Untranslate(rule.WhenColumn!, rule.WhenValues))],
            ParquetRuleKind.Range when rule.Min is not null && rule.Max is null => [new(ctx.Map(rule.Column!), ParquetFilterOperator.LessThan, rule.Min)],
            ParquetRuleKind.Range when rule.Max is not null && rule.Min is null => [new(ctx.Map(rule.Column!), ParquetFilterOperator.GreaterThan, rule.Max)],
            _ => [],
        };
        return R(failures == 0 ? ParquetRuleOutcome.Pass : ParquetRuleOutcome.Fail, expectedText, failures == 0 ? "all rows comply" : $"{N(failures)} row(s) do not",
            scope, row, failing, why, affected: failures, drill: drillFilters);
    }

    private static IEnumerable<string> Needed(ParquetRule r, ParquetOverview ov) => r.Kind switch
    {
        ParquetRuleKind.Unique => r.Columns.Count > 0 ? r.Columns : [r.Column!],
        ParquetRuleKind.NullUnless or ParquetRuleKind.RequiredWhen or ParquetRuleKind.OnlyWith => [r.Column!, r.WhenColumn!],
        ParquetRuleKind.SumIdentity or ParquetRuleKind.RollUp => [r.Column!, r.ValueColumn!, .. r.GroupColumns, .. (r.WhenColumn is { } w ? new[] { w } : [])],
        ParquetRuleKind.SortedBy => r.Columns,
        ParquetRuleKind.Manifest => r.Column is { } c ? [c] : [],
        // Identifier and canary scans: the listed columns, else every readable text column of the file.
        ParquetRuleKind.NoIdentifierValues or ParquetRuleKind.NoProtectedCanary => r.Columns.Count > 0 ? r.Columns
            : ov.Columns.Where(c => c.Readable && c.PhysicalType == "BYTE_ARRAY").Select(c => c.Path),
        _ => [r.Column!],
    };

    private static int CompareKeys(string?[] a, string?[] b)
    {
        for (var i = 0; i < a.Length; i++)
        {
            if (a[i] is null && b[i] is null) continue;
            if (a[i] is null) return -1;
            if (b[i] is null) return 1;
            var c = string.CompareOrdinal(a[i], b[i]);
            if (c != 0) return c;
        }
        return 0;
    }

    private static bool TryDecimal(object raw, out decimal value)
    {
        switch (raw)
        {
            case int or long or short or byte or decimal: value = Convert.ToDecimal(raw, CultureInfo.InvariantCulture); return true;
            case float or double: value = Convert.ToDecimal(raw, CultureInfo.InvariantCulture); return true;
            default: value = 0; return false;
        }
    }

    internal static Regex? FormatRegex(string? format) => format switch
    {
        "YYYY" => new Regex(@"^\d{4}$", RegexOptions.None, TimeSpan.FromSeconds(1)),
        "YYYY-MM" => new Regex(@"^\d{4}-(0[1-9]|1[0-2])$", RegexOptions.None, TimeSpan.FromSeconds(1)),
        "YYYY-MM-DD" => new Regex(@"^\d{4}-(0[1-9]|1[0-2])-(0[1-9]|[12]\d|3[01])$", RegexOptions.None, TimeSpan.FromSeconds(1)),
        _ => null,
    };

    [GeneratedRegex(@"(?<!\d)\d{11}(?!\d)")] private static partial Regex ElevenDigits();

    // ── Statistics (exact full scan; durable form keeps contract-enumerated categories only) ──────────────────────────────

    public (ParquetStatistics? Statistics, string? Error) Statistics(Guid id, QualityContext ctx, (string Row, string Column, string Value)? pivot, CancellationToken ct)
    {
        if (Session(id) is not { } s) return (null, "The Parquet session has expired; open the file again.");
        var file = s.File;
        if (file.Rows > MaxScanRows) return (null, $"A full scan is limited to {N(MaxScanRows)} rows; this file has {N(file.Rows)}.");
        var leaves = file.Leaves.Where(l => l.NotReadable is null).ToList();
        var acc = leaves.ToDictionary(l => l.Index, _ => new Acc());
        var categories = leaves.ToDictionary(l => l.Index, _ => new Dictionary<string, long>(StringComparer.Ordinal));
        var overflow = new HashSet<int>();
        var rowHashes = new HashSet<(ulong, ulong)>();
        long duplicates = 0;
        var pivotCells = new Dictionary<(string, string), decimal>();
        var pivotLeaves = pivot is { } pv ? (Row: file.Leaves.FirstOrDefault(l => l.Path == ctx.Map(pv.Row)), Column: file.Leaves.FirstOrDefault(l => l.Path == ctx.Map(pv.Column)), Value: file.Leaves.FirstOrDefault(l => l.Path == ctx.Map(pv.Value))) : default;
        var pivotOk = pivot is not null && pivotLeaves.Row is { NotReadable: null } && pivotLeaves.Column is { NotReadable: null } && pivotLeaves.Value is { NotReadable: null };
        try
        {
            foreach (var group in file.RowGroups)
            {
                ct.ThrowIfCancellationRequested();
                var data = leaves.ToDictionary(l => l.Index, l => file.ReadColumn(group.Index, l.Index));
                for (var r = 0; r < group.Rows; r++)
                {
                    var sb = new StringBuilder();
                    foreach (var l in leaves)
                    {
                        var v = data[l.Index][r];
                        acc[l.Index].Add(v);
                        var text = ParquetFile.Format(v);
                        sb.Append(text ?? "\u0000").Append('\u001f');
                        if (v is string && !overflow.Contains(l.Index) && text is not null)
                        {
                            var cat = categories[l.Index];
                            cat[text] = cat.GetValueOrDefault(text) + 1;
                            if (cat.Count > MaxCategoryValues) { overflow.Add(l.Index); cat.Clear(); }
                        }
                    }
                    var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
                    if (!rowHashes.Add((BitConverter.ToUInt64(hash, 0), BitConverter.ToUInt64(hash, 8)))) duplicates++;
                    if (pivotOk && data[pivotLeaves.Value!.Index][r] is { } amountRaw && TryDecimal(amountRaw, out var amount))
                    {
                        var rk = ctx.Translate(pivot!.Value.Row, ParquetFile.Format(data[pivotLeaves.Row!.Index][r])) ?? "(null)";
                        var ck = ctx.Translate(pivot.Value.Column, ParquetFile.Format(data[pivotLeaves.Column!.Index][r])) ?? "(null)";
                        pivotCells[(rk, ck)] = pivotCells.GetValueOrDefault((rk, ck)) + amount;
                    }
                }
            }
        }
        catch (ParquetFormatException ex) { return (null, ex.Message); }
        var columns = leaves.Select(l =>
        {
            var a = acc[l.Index];
            var numericOrDate = a.Min is int or long or float or double or decimal or DateOnly or DateTimeOffset;
            var identityLike = ContractTestDataRules.IsIdentity(l.Path) || ContractTestDataRules.IsClassification(l.Path);
            return new ColumnStatistic
            {
                Column = l.Path, Rows = a.Rows, Nulls = a.Nulls, Distinct = a.DistinctCapped ? null : a.Distinct.Count,
                Min = numericOrDate && !identityLike ? ParquetFile.Format(a.Min) : null, Max = numericOrDate && !identityLike ? ParquetFile.Format(a.Max) : null,
                Mode = EvaluationMode.Exact,
                Categories = overflow.Contains(l.Index) || identityLike ? [] : [.. categories[l.Index].OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => new CategoryCount(kv.Key, kv.Value))],
            };
        }).ToList();
        return (new ParquetStatistics
        {
            Rows = file.Rows, DuplicateRows = duplicates, Columns = columns, Mode = EvaluationMode.Exact, Scope = $"Full scan of all {N(file.Rows)} rows ({leaves.Count} readable column(s))",
            PivotRowColumn = pivotOk ? pivot!.Value.Row : null, PivotColumnColumn = pivotOk ? pivot!.Value.Column : null, PivotValueColumn = pivotOk ? pivot!.Value.Value : null,
            Pivot = [.. pivotCells.OrderBy(kv => kv.Key.Item1, StringComparer.Ordinal).ThenBy(kv => kv.Key.Item2, StringComparer.Ordinal).Select(kv => new AggregateCell(kv.Key.Item1, kv.Key.Item2, kv.Value))],
        }, null);
    }
}
