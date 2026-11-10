using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BirkNext.Api.Services.ContractTesting;
using BirkNext.Parquet;

namespace BirkNext.Api.Services.Parquet.Quality;

/// <summary>
/// Rule drafts from a parsed dataset contract. Column facts (presence, type, nullability, enumerated values, format) are CONTRACT_DERIVED and
/// active; business statements (sum identities, roll-ups, allowed combinations, conditional nulls, ordering) are CONTRACT_DERIVED but stay
/// NEEDS REVIEW until a person approves them (then APPROVED_BUSINESS_RULE). Statements without a rule stay in the requirement list as NOT
/// SUPPORTED. Without a contract, SCHEMA_DERIVED rules describe only the file's own schema (readability, not correctness).
/// </summary>
public static class ParquetRuleGenerator
{
    public const int GeneratorVersion = 1;

    public static RuleGeneration Generate(DatasetContract contract, ContractAssociation association, ParquetRuleSetVersion? approved)
    {
        var rules = new List<ParquetRule>();
        var limitations = new List<string>();
        var prov = (int line) => $"{contract.DocumentPath}:L{line}";
        var flat = contract.Columns.Where(c => !c.Nested).ToList();
        ParquetRule Rule(string id, ParquetRuleKind kind, string requirement, int line, string severity, string semantics, bool business = false) => new()
        {
            Id = $"{contract.Key}.{id}", Kind = kind, Origin = RuleOrigin.ContractDerived, Review = business ? RuleReviewState.NeedsReview : RuleReviewState.Active,
            Severity = severity, RequirementIds = [requirement], Provenance = prov(line), ContractVersion = contract.Version, RuleVersion = GeneratorVersion, Semantics = semantics,
        };

        foreach (var c in contract.Columns)
        {
            var req = $"col-{c.Name}";
            rules.Add(Rule($"{c.Name}.exists", ParquetRuleKind.ColumnExists, req, c.Line, "High", $"The contract lists column {c.Name}.") with { Column = c.Name });
            if (c.Nested) { limitations.Add($"{c.Name} is a nested {c.LogicalType} column: its presence is checked; element values are not read (nested columns are not readable by the explorer)."); continue; }
            if ((c.LogicalType ?? c.PhysicalType) is { } type)
                rules.Add(Rule($"{c.Name}.type", ParquetRuleKind.TypeIs, req, c.Line, "High", $"Documented type {c.DocumentedType}.") with { Column = c.Name, Expected = type });
            if (!c.Nullable)
                rules.Add(Rule($"{c.Name}.not-null", ParquetRuleKind.NotNull, req, c.Line, "High", "The Null column of the contract says no.") with { Column = c.Name });
            if (c.AllowedValues.Count > 0)
                rules.Add(Rule($"{c.Name}.values", ParquetRuleKind.AllowedValues, req, c.Line, "High", $"The contract enumerates {c.AllowedValues.Count} value(s).") with { Column = c.Name, Values = [.. c.AllowedValues] });
            if (c.Format is { } f)
                rules.Add(Rule($"{c.Name}.format", ParquetRuleKind.Pattern, req, c.Line, "Medium", $"The contract gives the format {f}.") with { Column = c.Name, Expected = f });
            if (c.Nullable && DatasetContractParser.OnlyFor().Match(c.Content) is { Success: true } only
                && flat.FirstOrDefault(o => o.Name != c.Name && o.AllowedValues.Contains(only.Groups["v"].Value)) is { } condition)
                rules.Add(Rule($"{c.Name}.only-for", ParquetRuleKind.NullUnless, $"col-{c.Name}-condition", c.Line, "Medium",
                    $"Reads \"{c.Content}\" as: {c.Name} is empty unless {condition.Name} = {only.Groups["v"].Value}. Whether it must also be set for that value is not stated (needs review).", business: true)
                    with { Column = c.Name, WhenColumn = condition.Name, WhenValues = [only.Groups["v"].Value] });
        }
        var measures = flat.Where(c => !c.Nullable && c.PhysicalType is "INT32" or "INT64" && c.AllowedValues.Count == 0).ToList();
        foreach (var r in contract.Requirements.Where(r => r.Machine))
        {
            var identity = DatasetContractParser.SumTerms(r.Text, contract.Columns);
            if (identity is { } id)
            {
                if (measures.Count == 1)
                    rules.Add(Rule($"{r.Id}.sum", ParquetRuleKind.SumIdentity, r.Id, r.Line, "High",
                        $"{string.Join(" + ", id.Terms)} = {id.Total} (sums of {measures[0].Name}){(id.GroupBy is { } g ? $" for each {g}" : "")}, as stated in the contract.", business: true)
                        with { Column = id.Column.Name, Values = id.Terms, Expected = id.Total, ValueColumn = measures[0].Name, GroupColumns = id.GroupBy is { } gb ? [gb] : [] });
                else limitations.Add($"{r.Id}: the identity needs exactly one integer measure column; the contract has {measures.Count}.");
            }
            if (System.Text.RegularExpressions.Regex.Match(r.Text, @"add(?:s)? up to\s+`(?<v>[^`]+)`", System.Text.RegularExpressions.RegexOptions.IgnoreCase) is { Success: true } up
                && flat.FirstOrDefault(c => c.AllowedValues.Contains(up.Groups["v"].Value)) is { } category && measures.Count == 1)
            {
                var only = contract.Requirements.Select(x => DatasetContractParser.OnlyWith().Match(x.Text)).FirstOrDefault(m => m.Success && m.Groups["c"].Value == category.Name && m.Groups["v"].Value.Trim() == up.Groups["v"].Value);
                var excludeColumn = only is null ? null : flat.FirstOrDefault(c => DatasetContractParser.Backticks(only.Groups["vals"].Value).All(c.AllowedValues.Contains));
                var groups = flat.Where(c => c.Name != category.Name && c.Name != measures[0].Name).Select(c => c.Name).ToList();
                rules.Add(Rule($"{r.Id}.rollup", ParquetRuleKind.RollUp, r.Id, r.Line, "High",
                    $"The other {category.Name} values add up to {up.Groups["v"].Value} for each {string.Join(", ", groups)}" + (excludeColumn is null ? "." : $", leaving out the {excludeColumn.Name} values the contract allows only with {up.Groups["v"].Value}."), business: true)
                    with { Column = category.Name, Expected = up.Groups["v"].Value, ValueColumn = measures[0].Name, GroupColumns = groups, WhenColumn = excludeColumn?.Name,
                        WhenValues = excludeColumn is null ? [] : DatasetContractParser.Backticks(only!.Groups["vals"].Value) });
            }
            if (DatasetContractParser.OnlyWith().Match(r.Text) is { Success: true } ow)
            {
                var values = DatasetContractParser.Backticks(ow.Groups["vals"].Value);
                if (flat.FirstOrDefault(c => values.All(c.AllowedValues.Contains)) is { } col)
                    rules.Add(Rule($"{r.Id}.only-with", ParquetRuleKind.OnlyWith, r.Id, r.Line, "Medium",
                        $"{string.Join(", ", values)} appear only with {ow.Groups["c"].Value} = {ow.Groups["v"].Value.Trim()}.", business: true)
                        with { Column = col.Name, Values = values, WhenColumn = ow.Groups["c"].Value, WhenValues = [ow.Groups["v"].Value.Trim()] });
            }
            if (r.Category == "Ordering" && contract.SortKeys.Count > 0)
                rules.Add(Rule($"{r.Id}.sorted", ParquetRuleKind.SortedBy, r.Id, r.Line, "Low",
                    $"Rows sorted by {string.Join(", ", contract.SortKeys)} in ordinal (byte) order. Where nulls sort is not stated; nulls first is assumed (needs review).", business: true)
                    with { Columns = [.. contract.SortKeys] });
            if (r.Category == "Confidentiality")
            {
                // Columns the contract documents as carrying an identity (e.g. BirkID, reporter identity) are excluded; every other text column is scanned.
                var declared = contract.Columns.Where(c => IdentityCarrier(c)).Select(c => c.Name).ToList();
                var scan = flat.Where(c => c.PhysicalType == "BYTE_ARRAY" && !declared.Contains(c.Name)).Select(c => c.Name).ToList();
                rules.Add(Rule($"{r.Id}.identifiers", ParquetRuleKind.NoIdentifierValues, r.Id, r.Line, "High",
                    $"No 11-digit personal identification number in {string.Join(", ", scan)}" + (declared.Count > 0 ? $"; {string.Join(", ", declared)} carry identities by contract and are not scanned." : "."))
                    with { Columns = scan });
            }
            if (r.Category == "Determinism" && DatasetContractParser.RowGroups(r.Text) is { } rg)
                rules.Add(Rule($"{r.Id}.row-groups", ParquetRuleKind.RowGroupCount, r.Id, r.Line, "Low", $"The contract fixes {rg} row group.") with { Expected = rg.ToString(System.Globalization.CultureInfo.InvariantCulture) });
            if (r.Category == "Determinism" && DatasetContractParser.Compression(r.Text) is { } codec)
                rules.Add(Rule($"{r.Id}.codec", ParquetRuleKind.Compression, r.Id, r.Line, "Low", $"The contract fixes {codec} compression.") with { Expected = codec });
            if (r.Id == "manifest")
            {
                var period = flat.FirstOrDefault(c => c.Format is not null)?.Name;
                rules.Add(Rule("manifest", ParquetRuleKind.Manifest, r.Id, r.Line, "Medium",
                    $"The attached _manifest.json matches the file (SHA-256, rowCount, bytes, contract version{(period is null ? "" : $", and every {period} equals the manifest period")}).") with { Column = period });
            }
        }
        // Kode 6/7 exclusion: the canary check is partial evidence (a found canary is a definite failure; absence proves nothing).
        foreach (var r in contract.Requirements.Where(r => r.Category == "Confidentiality" && !r.Machine))
            rules.Add(Rule($"{r.Id}.canary", ParquetRuleKind.NoProtectedCanary, r.Id, r.Line, "High",
                "Looks for approved protected-identity canaries from Test Data in every text column. A hit fails the requirement; no hit is not proof of correct protected-record handling.") with { Columns = [] });

        rules = ApplyApproval(rules, approved);
        var name = $"{contract.Title} {contract.Version}";
        return new RuleGeneration
        {
            Association = association, RuleSetName = name, RuleSetRef = Ref(association.ProjectKey, contract.Key, contract.Version, rules), Rules = rules,
            Requirements = contract.Requirements, Limitations = limitations,
        };
    }

    /// <summary>Rules of the file's own schema when no contract is selected: they prove the file is readable as written, not that it is correct.</summary>
    public static RuleGeneration FromSchema(ParquetOverview overview) => new()
    {
        RuleSetName = $"Schema of {overview.FileName}", RuleSetRef = $"schema:{overview.SchemaFingerprint}",
        Rules = [.. overview.Columns.SelectMany(c => new[]
        {
            new ParquetRule { Id = $"schema.{c.Path}.exists", Kind = ParquetRuleKind.ColumnExists, Column = c.Path, Origin = RuleOrigin.SchemaDerived, Severity = "Low", Provenance = "file schema", Semantics = "Present in the file's own schema." },
            new ParquetRule { Id = $"schema.{c.Path}.type", Kind = ParquetRuleKind.TypeIs, Column = c.Path, Expected = c.LogicalType ?? c.PhysicalType, Origin = RuleOrigin.SchemaDerived, Severity = "Low", Provenance = "file schema", Semantics = "Type as written in the file's own schema." },
        })],
        Limitations = ["No contract selected: schema-derived rules only describe the file itself (readability), not whether it is correct."],
    };

    /// <summary>A generated business rule becomes APPROVED_BUSINESS_RULE (active) only when an approved rule-set version holds the same rule with the
    /// same parameters; a rule rejected there stays rejected. User-authored rules of the approved set are added as they were approved.</summary>
    public static List<ParquetRule> ApplyApproval(List<ParquetRule> generated, ParquetRuleSetVersion? approved)
    {
        if (approved is null) return generated;
        var byId = approved.Rules.ToDictionary(r => r.Id, StringComparer.Ordinal);
        var result = generated.Select(r =>
        {
            if (!byId.TryGetValue(r.Id, out var a) || Fingerprint(a) != Fingerprint(r)) return r;
            if (a.Review == RuleReviewState.Rejected) return r with { Review = RuleReviewState.Rejected };
            return r.Review == RuleReviewState.NeedsReview && a.Review == RuleReviewState.Active ? r with { Review = RuleReviewState.Active, Origin = RuleOrigin.ApprovedBusinessRule } : r;
        }).ToList();
        result.AddRange(approved.Rules.Where(a => a.Origin == RuleOrigin.UserAuthored && result.All(r => r.Id != a.Id)));
        return result;
    }

    /// <summary>Parameters only (kind, columns, values, expected) — not names or wording.</summary>
    public static string Fingerprint(ParquetRule r) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
    {
        r.Kind, r.Column, r.Columns, r.Values, r.Expected, r.Min, r.Max, r.WhenColumn, r.WhenValues, r.ValueColumn, r.GroupColumns, r.ContractVersion,
    }))))[..16].ToLowerInvariant();

    public static string Ref(string project, string contract, string version, IEnumerable<ParquetRule> rules) =>
        $"{project}/{contract}@{version}/rules:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", rules.Select(r => r.Id + ":" + r.Review + ":" + Fingerprint(r))))))[..12].ToLowerInvariant()}";

    private static bool IdentityCarrier(DatasetContractColumn c) =>
        ContractTestDataRules.IsIdentity(c.Name) || System.Text.RegularExpressions.Regex.IsMatch(c.Content, @"(?i)(birkid|fødselsnummer|fodselsnummer|orgnr|identity|identitet)");
}

/// <summary>Expected contract schema versus the file's actual schema, through the association's column mappings.</summary>
public static class ParquetSchemaChecker
{
    public static ParquetSchemaCheck Check(DatasetContract contract, ParquetOverview file, IReadOnlyList<ColumnMapping> mappings)
    {
        string Map(string c) => mappings.FirstOrDefault(m => m.Expected == c)?.Actual ?? c;
        var findings = new List<SchemaFinding>();
        var covered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in contract.Columns)
        {
            var a = Map(c.Name);
            var expectedType = c.DocumentedType + (c.Nullable ? " · nullable" : " · not null");
            if (a != c.Name)
                findings.Add(new(c.Name, a, SchemaFindingKind.Mapped, c.Name, a, "Info", $"Matched through a {mappings.First(m => m.Expected == c.Name).Provenance.ToLowerInvariant()} mapping ({a} → {c.Name})."));
            if (c.Nested)
            {
                var leaves = file.Columns.Where(x => x.Path.StartsWith(a + ".", StringComparison.Ordinal)).ToList();
                foreach (var l in leaves) covered.Add(l.Path);
                findings.Add(leaves.Count == 0
                    ? new(c.Name, null, SchemaFindingKind.Missing, expectedType, "absent", "High", "The nested column is not in the file.")
                    : new(c.Name, a, SchemaFindingKind.Nested, expectedType, $"group with {leaves.Count} leaf column(s)", "Info", "Present as a nested group; element types are listed in the technical schema view, not compared field by field."));
                continue;
            }
            var col = file.Columns.FirstOrDefault(x => x.Path == a);
            if (col is null) { findings.Add(new(c.Name, null, SchemaFindingKind.Missing, expectedType, "absent", "High", "A contract column is missing from the file.")); continue; }
            covered.Add(col.Path);
            var actualType = col.PhysicalType + (col.LogicalType is { } lt ? $" ({lt})" : "") + (col.Optional ? " · nullable" : " · not null");
            if (c.PhysicalType is { } p && !p.Equals(col.PhysicalType, StringComparison.OrdinalIgnoreCase))
                findings.Add(new(c.Name, a, SchemaFindingKind.PhysicalType, expectedType, actualType, "High", $"Physical type {col.PhysicalType} instead of {p}."));
            else if (c.LogicalType is { } l && !string.Equals(l, col.LogicalType, StringComparison.OrdinalIgnoreCase))
            {
                var decimalScale = l.StartsWith("DECIMAL", StringComparison.OrdinalIgnoreCase) && (col.LogicalType ?? "").StartsWith("DECIMAL", StringComparison.OrdinalIgnoreCase);
                findings.Add(new(c.Name, a, decimalScale ? SchemaFindingKind.DecimalScale : SchemaFindingKind.LogicalType, expectedType, actualType, "High",
                    decimalScale ? $"Decimal precision/scale {col.LogicalType} instead of {l}." : $"Logical type {col.LogicalType ?? "none"} instead of {l}."));
            }
            else if (!c.Nullable && col.Optional)
                findings.Add(new(c.Name, a, SchemaFindingKind.Nullability, expectedType, actualType, "Warning", "The file declares the column optional; the contract says not null. The data check (not null) decides."));
            else
                findings.Add(new(c.Name, a, SchemaFindingKind.Match, expectedType, actualType, "Info", c.Nullable && !col.Optional ? "Matches (the file is stricter: required)." : "Matches."));
        }
        foreach (var extra in file.Columns.Where(x => !covered.Contains(x.Path)))
        {
            var sensitive = ContractTestDataRules.IsIdentity(extra.Path) || ContractTestDataRules.IsClassification(extra.Path);
            findings.Add(new(extra.Path, extra.Path, SchemaFindingKind.Unexpected, "not in the contract", extra.PhysicalType + (extra.LogicalType is { } lt ? $" ({lt})" : ""),
                sensitive ? "High" : "Warning", sensitive ? "An identifier- or classification-like column the contract does not list." : "Not in the contract. The contract does not say whether added columns are allowed (review)."));
        }
        var compatibility = findings.Any(f => f.Severity == "High") ? SchemaCompatibility.Incompatible
            : findings.Any(f => f.Severity == "Warning") ? SchemaCompatibility.CompatibleWithWarnings : SchemaCompatibility.Compatible;
        int missing = findings.Count(f => f.Kind == SchemaFindingKind.Missing), unexpected = findings.Count(f => f.Kind == SchemaFindingKind.Unexpected),
            types = findings.Count(f => f.Kind is SchemaFindingKind.PhysicalType or SchemaFindingKind.LogicalType or SchemaFindingKind.DecimalScale);
        var summary = compatibility switch
        {
            SchemaCompatibility.Compatible => $"All {contract.Columns.Count} contract columns match.",
            SchemaCompatibility.CompatibleWithWarnings => $"Compatible with warnings: {findings.Count(f => f.Severity == "Warning")} item(s) to review.",
            _ => $"Incompatible: {missing} missing, {types} type mismatch(es), {unexpected} unexpected column(s).",
        };
        if (mappings.Any(m => m.Actual != m.Expected)) summary += $" Through {mappings.Count(m => m.Actual != m.Expected)} column mapping(s).";
        return new ParquetSchemaCheck(compatibility, findings, summary);
    }
}
