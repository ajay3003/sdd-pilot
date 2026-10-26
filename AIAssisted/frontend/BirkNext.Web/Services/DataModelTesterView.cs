using System.Text;
using System.Text.RegularExpressions;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>How two names were found to be the same structure or field. Never fuzzy: exact (case-insensitive) after removing the parser's
/// "Name (table: Name)" heading suffix, or equal after the project's Norwegian identifier transliteration (æ→ae, ø→oe, å→aa).</summary>
public enum DataModelNameMatch { Exact, NormalizedImplementationName }

/// <summary>Logical identity of documented structures and fields.</summary>
public static partial class DataModelIdentity
{
    public const string NormalizedLabel = "Matched via normalized implementation name";
    public const string NormalizedBasis = "Norwegian characters in identifiers are written æ→ae, ø→oe, å→aa (project naming rule, e.g. KjønnType → KjoennType)";

    /// <summary>"BarnType (table: BarnType)" → "BarnType": the heading names the entity; the table is its mapping.</summary>
    public static string LogicalName(string name) => TableSuffix().Match(name) is { Success: true } m ? m.Groups["entity"].Value.Trim() : name.Trim();

    /// <summary>The table named in a "Name (table: Table)" heading, else null.</summary>
    public static string? TableName(string name) => TableSuffix().Match(name) is { Success: true } m ? m.Groups["table"].Value.Trim() : null;

    /// <summary>The project's identifier rule for Norwegian characters; nothing else is changed.</summary>
    public static string Transliterate(string name)
    {
        var sb = new StringBuilder(name.Length + 4);
        foreach (var c in name)
            sb.Append(c switch { 'æ' => "ae", 'Æ' => "Ae", 'ø' => "oe", 'Ø' => "Oe", 'å' => "aa", 'Å' => "Aa", _ => c.ToString() });
        return sb.ToString();
    }

    public static DataModelNameMatch? Match(string a, string b)
    {
        var x = LogicalName(a);
        var y = LogicalName(b);
        if (x.Equals(y, StringComparison.OrdinalIgnoreCase)) return DataModelNameMatch.Exact;
        return Transliterate(x).Equals(Transliterate(y), StringComparison.OrdinalIgnoreCase) ? DataModelNameMatch.NormalizedImplementationName : null;
    }

    [GeneratedRegex(@"^(?<entity>[^()]+?)\s*\(table:\s*(?<table>[^)]+)\)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex TableSuffix();
}

public enum LogicalStructureOrigin { Feature, Shared, Both }

/// <summary>A difference between the feature and the shared definition of one logical structure — something to review, not a defect.</summary>
public sealed record OriginDifference(string Field, string Kind);

/// <summary>
/// One logical structure of the Combined view with the evidence of each origin kept apart. With both origins, the feature definition is the
/// one used in the Combined view (the existing override rule); the shared definition stays available — fields are never merged.
/// </summary>
public sealed record LogicalStructure(string Name, DataEntity? Feature, DataEntity? Shared, DataModelNameMatch? Match, IReadOnlyList<OriginDifference> Differences)
{
    public LogicalStructureOrigin Origin => Feature is null ? LogicalStructureOrigin.Shared : Shared is null ? LogicalStructureOrigin.Feature : LogicalStructureOrigin.Both;
    public DataEntity Effective => Feature ?? Shared!;

    public string OriginLabel => Origin switch
    {
        LogicalStructureOrigin.Feature => "Feature model",
        LogicalStructureOrigin.Shared => "Shared model",
        _ => "Feature + shared model",
    };
}

public enum FieldRequirement { Required, Optional, Unknown }

/// <summary>Declared = a key the document declares; Inferred = only the field name suggests it (never shown as explicit).</summary>
public enum IdentifierEvidence { None, Declared, Inferred }

public enum SecurityBasis { StatedInDocument, ReferencesSecurityStructure, FieldName, SecurityStructure }

/// <summary>One field in tester terms. Every part comes from the document's own declarations; inferred parts say so.</summary>
public sealed record FieldRule(DataColumn Column, FieldRequirement Requirement, IdentifierEvidence Identifier, string? Length, bool LengthExact, string? Range,
    string? Uniqueness, string? References, string? Default, SecurityBasis? Security, bool Sensitive)
{
    public string RequirementText => Requirement switch { FieldRequirement.Required => "Required", FieldRequirement.Optional => "Optional", _ => "Requirement unknown" };

    /// <summary>"Required · Exactly 4 characters · references Kommune".</summary>
    public string Summary => string.Join(" · ", new[] { RequirementText, Length, Range, Uniqueness, References is { } r ? $"references {r}" : null, Default is { } d ? $"default {d}" : null }
        .Where(s => !string.IsNullOrEmpty(s)));

    public string? SecurityText => Security switch
    {
        SecurityBasis.StatedInDocument => "The document's notes for this field mention security or classification.",
        SecurityBasis.ReferencesSecurityStructure => $"References the security classification structure {References}.",
        SecurityBasis.FieldName => "Inferred from field name (security/classification term).",
        SecurityBasis.SecurityStructure => "Part of a security classification structure (inferred from the structure name).",
        _ => null,
    };
}

/// <summary>A generic, deterministic test condition from an explicit model constraint — a condition to test, never an expected result.</summary>
public sealed record TestCondition(string Field, string Condition, string Basis);

/// <summary>A structure testers treat as reference data: referenced by other structures and referencing none itself.</summary>
public sealed record ReferenceDataStructure(DataEntity Entity, string Name, string? Key, string? UniqueValue, string? ActiveField, string? SortField, IReadOnlyList<string> CodeFields,
    IReadOnlyList<string> UsedBy)
{
    public const string Basis = "Referenced by other structures and references none itself (from the declared relationships)";
}

/// <summary>A relationship in tester words; the technical path stays available.</summary>
public sealed record FriendlyRelationship(DataRelationship Relationship, string Text, string? Via, string Cardinality, string Technical);

public sealed record DataModelTesterSummary(int Structures, int Required, int Optional, int Unknown, int Relationships, int ReferenceData, int SecurityFields, int SensitiveFields);

/// <summary>
/// Testable data rules from the normalized data model — only what the document declares (nullability, keys, UNIQUE, lengths, CHECK, FK
/// relationships, defaults). Names are used only where the existing model already uses them (inferred identifiers, sensitive-name patterns)
/// and are then labelled as inferred. No business meaning, expected outcome or value is invented.
/// </summary>
public static partial class DataModelTesterView
{
    public static FieldRequirement Requirement(DataColumn c) =>
        c.IsPrimaryKey && !c.KeyInferred ? FieldRequirement.Required
        : c.Nullable switch { false => FieldRequirement.Required, true => FieldRequirement.Optional, _ => FieldRequirement.Unknown };

    public static IdentifierEvidence Identifier(DataColumn c) =>
        c.IsPrimaryKey ? c.KeyInferred ? IdentifierEvidence.Inferred : IdentifierEvidence.Declared
        : !c.IsForeignKey && c.Name.Length > 2 && c.Name.EndsWith("Id", StringComparison.Ordinal) ? IdentifierEvidence.Inferred
        : IdentifierEvidence.None;

    /// <summary>"Exactly 4 characters" only with an explicit length check; "Maximum N characters" for a declared maximum (varchar(N), MaxLength(N)).</summary>
    public static (string? Text, bool Exact) Length(DataColumn c)
    {
        var text = $"{c.Type} {c.Description}";
        if (LengthCheck().Match(text) is { Success: true } exact) return ($"Exactly {exact.Groups[1].Value} characters", true);
        if (VarLength().Match(c.Type ?? "") is { Success: true } v) return ($"Maximum {v.Groups[1].Value} characters", false);
        if (MaxLength().Match(text) is { Success: true } m) return ($"Maximum {m.Groups[1].Value} characters", false);
        if (FixedLength().Match(c.Type ?? "") is { Success: true } f) return ($"{f.Groups[1].Value} characters (fixed-width type)", false);
        return (null, false);
    }

    public static string? Range(DataColumn c) =>
        Between().Match(c.Description ?? "") is { Success: true } b ? $"Between {b.Groups[1].Value} and {b.Groups[2].Value}"
        : Comparison().Match(c.Description ?? "") is { Success: true } cmp ? cmp.Groups[1].Value switch
        {
            ">=" => $"At least {cmp.Groups[2].Value}", ">" => $"Greater than {cmp.Groups[2].Value}",
            "<=" => $"At most {cmp.Groups[2].Value}", _ => $"Less than {cmp.Groups[2].Value}",
        } : null;

    private static string? Uniqueness(DataEntity entity, DataColumn c, DataModelDocument document)
    {
        var unique = c.IsUnique || document.Indexes.Any(i => i.IsUnique && i.Columns.Count == 1 && SameStructure(i.EntityName, entity.Name)
            && i.Columns[0].Equals(c.Name, StringComparison.OrdinalIgnoreCase));
        if (!unique || c.IsPrimaryKey) return null;
        return (c.Description ?? "").Contains("filtered", StringComparison.OrdinalIgnoreCase) ? "Must be unique when set" : "Must be unique";
    }

    private static string? Default(DataColumn c) => DefaultValue().Match(c.Description ?? "") is { Success: true } m ? m.Groups[1].Value.Trim().TrimEnd('.') : null;

    public static DataRelationship? ReferenceOf(DataEntity entity, DataColumn c, DataModelDocument document) =>
        document.Relationships.FirstOrDefault(r => SameStructure(r.SourceEntity, entity.Name) && r.SourceColumn.Equals(c.Name, StringComparison.OrdinalIgnoreCase));

    public static bool IsSecurityName(string name) => SecurityTerm().IsMatch(name);

    public static FieldRule Rule(DataEntity entity, DataColumn c, DataModelDocument document)
    {
        var reference = ReferenceOf(entity, c, document);
        var target = reference is null ? null : DataModelIdentity.LogicalName(reference.TargetEntity);
        var (length, exact) = Length(c);
        // A reference to a security structure first: its name in "FK → SikkerhetsnivaaType" is not the document describing the field.
        SecurityBasis? security = target is not null && SecurityTerm().IsMatch(target) ? SecurityBasis.ReferencesSecurityStructure
            : SecurityTerm().IsMatch(c.Description ?? "") ? SecurityBasis.StatedInDocument
            : SecurityTerm().IsMatch(c.Name) ? SecurityBasis.FieldName
            : SecurityTerm().IsMatch(DataModelIdentity.LogicalName(entity.Name)) ? SecurityBasis.SecurityStructure
            : null;
        return new FieldRule(c, Requirement(c), Identifier(c), length, exact, Range(c), Uniqueness(entity, c, document), target, Default(c), security,
            DataModelAnalysisService.IsSensitiveByName(c));
    }

    public static IReadOnlyList<FieldRule> Rules(DataEntity entity, DataModelDocument document) => [.. entity.Columns.Select(c => Rule(entity, c, document))];

    /// <summary>Generic test conditions from explicit constraints only. Conditions, never expected outcomes.</summary>
    public static IReadOnlyList<TestCondition> Conditions(DataEntity entity, DataModelDocument document)
    {
        var references = ReferenceData(document);
        var conditions = new List<TestCondition>();
        foreach (var rule in Rules(entity, document))
        {
            var field = rule.Column.Name;
            if (rule.Requirement == FieldRequirement.Required && !rule.Column.IsPrimaryKey && rule.Default is null)
                conditions.Add(new(field, "Test missing value", "Required"));
            if (rule.Uniqueness is { } unique)
                conditions.Add(new(field, "Test duplicate value", unique));
            if (rule.Length is { } length)
                conditions.Add(new(field, rule.LengthExact ? "Test exact length and a shorter / longer value" : "Test maximum length / over-limit value", length));
            if (rule.Range is { } range)
                conditions.Add(new(field, "Test boundary values", range));
            if (rule.References is { } target)
            {
                conditions.Add(new(field, rule.Requirement == FieldRequirement.Optional ? "Test valid, invalid and no reference" : "Test valid and invalid reference", $"References {target}"));
                if (references.FirstOrDefault(r => DataModelIdentity.Match(r.Name, target) is not null)?.ActiveField is { } active)
                    conditions.Add(new(field, "Test active/inactive value", $"{target}.{active}"));
            }
        }
        return conditions;
    }

    /// <summary>Structures referenced by another structure that reference none themselves. Enums stay enums; tables are never forced into them.</summary>
    public static IReadOnlyList<ReferenceDataStructure> ReferenceData(DataModelDocument document)
    {
        var result = new List<ReferenceDataStructure>();
        foreach (var entity in document.Entities)
        {
            var usedBy = document.Relationships.Where(r => SameStructure(r.TargetEntity, entity.Name) && !SameStructure(r.SourceEntity, entity.Name))
                .Select(r => r.Source).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (usedBy.Count == 0) continue;
            if (entity.Columns.Any(c => c.IsForeignKey && !c.KeyInferred) || document.Relationships.Any(r => SameStructure(r.SourceEntity, entity.Name))) continue;
            var key = entity.Columns.FirstOrDefault(c => c.IsPrimaryKey);
            result.Add(new ReferenceDataStructure(entity, DataModelIdentity.LogicalName(entity.Name), key?.Name,
                entity.Columns.FirstOrDefault(c => !c.IsPrimaryKey && Uniqueness(entity, c, document) is not null)?.Name,
                entity.Columns.FirstOrDefault(c => ActiveName().IsMatch(c.Name))?.Name,
                entity.Columns.FirstOrDefault(c => SortName().IsMatch(c.Name))?.Name,
                [.. entity.Columns.Where(c => !c.IsPrimaryKey && CodeName().IsMatch(c.Name)).Select(c => c.Name)],
                usedBy));
        }
        return result;
    }

    public static FriendlyRelationship Friendly(DataRelationship r, DataModelDocument document)
    {
        var source = DataModelIdentity.LogicalName(r.SourceEntity);
        var target = DataModelIdentity.LogicalName(r.TargetEntity);
        var entity = document.Entities.FirstOrDefault(e => SameStructure(e.Name, r.SourceEntity));
        var column = entity?.Columns.FirstOrDefault(c => c.Name.Equals(r.SourceColumn, StringComparison.OrdinalIgnoreCase));
        var cardinality = column is null ? "Cardinality not stated" : Requirement(column) switch
        {
            FieldRequirement.Required => $"Each {source} references one {target}",
            FieldRequirement.Optional => $"Each {source} may reference one {target} (optional)",
            _ => "Whether the reference is required is not stated",
        };
        if (column is not null && entity is not null && Uniqueness(entity, column, document) is not null) cardinality += $" · at most one {source} per {target}";
        return new FriendlyRelationship(r, $"{source} references {target}", r.SourceColumn is { Length: > 0 } via ? via : null, cardinality,
            $"{r.Source} → {r.Target}{(string.IsNullOrEmpty(r.RelationshipType) ? "" : $" · {r.RelationshipType}")}");
    }

    public static DataModelTesterSummary Summary(DataModelDocument document)
    {
        var rules = document.Entities.SelectMany(e => Rules(e, document)).ToList();
        return new DataModelTesterSummary(document.Entities.Count,
            rules.Count(r => r.Requirement == FieldRequirement.Required), rules.Count(r => r.Requirement == FieldRequirement.Optional),
            rules.Count(r => r.Requirement == FieldRequirement.Unknown), document.Relationships.Count, ReferenceData(document).Count,
            rules.Count(r => r.Security is not null), rules.Count(r => r.Sensitive));
    }

    /// <summary>Same-structure check used across the model: exact logical name or the project's transliteration rule.</summary>
    public static bool SameStructure(string a, string b) => DataModelIdentity.Match(a, b) is not null;

    [GeneratedRegex(@"CHECK\s*\(\s*LEN\s*\(?\s*\w*\s*\)?\s*=\s*(\d+)\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex LengthCheck();
    [GeneratedRegex(@"^\s*(?:n?varchar|string)\s*\(\s*(\d+)\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex VarLength();
    [GeneratedRegex(@"MaxLength\s*\(\s*(\d+)\s*\)|\bmax(?:imum)?\s+length\s*:?\s*(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex MaxLength();
    [GeneratedRegex(@"^\s*n?char\s*\(\s*(\d+)\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex FixedLength();
    [GeneratedRegex(@"BETWEEN\s+(-?[\d.]+)\s+AND\s+(-?[\d.]+)", RegexOptions.IgnoreCase)]
    private static partial Regex Between();
    [GeneratedRegex(@"CHECK\s*\(\s*\w+\s*(>=|<=|>|<)\s*(-?[\d.]+)\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex Comparison();
    [GeneratedRegex(@"\bDefault:\s*([^,;|]+)", RegexOptions.IgnoreCase)]
    private static partial Regex DefaultValue();
    [GeneratedRegex(@"secur|sikkerhet|gradert|classif|klassifi|adressebeskytt|kode\s?[67]\b", RegexOptions.IgnoreCase)]
    private static partial Regex SecurityTerm();
    [GeneratedRegex(@"^(?:er)?aktiv$|^is_?active$|^active$", RegexOptions.IgnoreCase)]
    private static partial Regex ActiveName();
    [GeneratedRegex(@"sort", RegexOptions.IgnoreCase)]
    private static partial Regex SortName();
    [GeneratedRegex(@"(?:kode|code)$", RegexOptions.IgnoreCase)]
    private static partial Regex CodeName();
}
