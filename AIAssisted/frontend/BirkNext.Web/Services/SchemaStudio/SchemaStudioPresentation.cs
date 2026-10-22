using BirkNext.Schemas;

namespace BirkNext.Web.Services.SchemaStudio;

/// <summary>One category of schema rules with how many the package declares (Constraints summary).</summary>
public sealed record SchemaRuleCount(string Category, int Count);

/// <summary>Summary of a package comparison, counted from the declaration-level change lines.</summary>
public sealed record SchemaChangeSummary(int Added, int Removed, int RulesChanged, int NamespacesChanged, int NeedsReview);

/// <summary>
/// Plain-language wording for Schema Studio: the concept a tester reads first, the exact XSD term second. Every statement maps a schema fact
/// (declaration kind, built-in type, facet, occurrence) — nothing is inferred from names and no schema semantics live here. The one shared
/// explanation of a library XSD (no top-level message element) is used by every view that meets one, so the wording never drifts.
/// </summary>
public static class SchemaStudioPresentation
{
    public const string LibraryHeadline = "This XSD is a reusable schema library.";
    public const string LibraryStructureHeadline = "This XSD does not define a complete message hierarchy.";
    public const string LibraryTechnical = "Technical detail: no global root element is declared.";
    public const string ValidityScope = "The XSD itself is structurally valid. Business rules and runtime integration behavior are not verified here.";

    /// <summary>What Schema Studio checks itself — shown next to the package status so "valid" never reads as "the integration is valid".</summary>
    public static readonly IReadOnlyList<string> VerifiedHere = ["XSD structure", "imports/includes", "schema-defined rules", "local XML validation"];

    /// <summary>What a valid XSD package does not prove.</summary>
    public static readonly IReadOnlyList<string> NotVerifiedHere = ["business rules", "producer/consumer compatibility", "runtime integration behavior", "deployed schema version", "live message delivery"];

    /// <summary>"All imports/includes resolved", "2 of 3 imports/includes resolved" or "No imports/includes" — resolution within this imported package only.</summary>
    public static string ResolutionLine(SchemaPackage package)
    {
        var links = package.Files.Sum(file => file.Links.Count);
        var unresolved = package.Files.Sum(file => file.Links.Count(link => link.ResolvedFile is null));
        return links == 0 ? "No imports/includes" : unresolved == 0 ? "All imports/includes resolved" : $"{links - unresolved} of {links} imports/includes resolved";
    }

    /// <summary>The file holding the (first) top-level message element: the entry schema. Null for a library package.</summary>
    public static SchemaFile? EntrySchema(SchemaPackage package) => package.Files.FirstOrDefault(f => f.Declarations.Any(d => d.Kind == "element"));

    /// <summary>Files other than the entry schema: imported/included reusable schemas.</summary>
    public static int SupportingSchemas(SchemaPackage package) => EntrySchema(package) is null ? package.Files.Count : package.Files.Count - 1;

    /// <summary>The distinct target namespaces of the package, in file order.</summary>
    public static IReadOnlyList<string> Namespaces(SchemaPackage package) =>
        [.. package.Files.Select(f => f.TargetNamespace).OfType<string>().Where(n => n.Length > 0).Distinct(StringComparer.Ordinal)];

    /// <summary>
    /// A short readable name for a namespace URI, derived only from its own last segments: "urn:x:applikasjonskvittering:v2" or
    /// ".../applikasjonskvittering/v2" → "Applikasjonskvittering v2". Returns the URI unchanged when no safe short form exists.
    /// </summary>
    public static string ShortNamespace(string uri)
    {
        var segments = uri.Split(['/', ':', '#'], StringSplitOptions.RemoveEmptyEntries)
            .Where(s => !s.Contains('.') || System.Text.RegularExpressions.Regex.IsMatch(s, @"^v?\d+(\.\d+)*$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)).ToList();
        if (segments.Count == 0) return uri;
        var last = segments[^1];
        static bool IsVersion(string s) => System.Text.RegularExpressions.Regex.IsMatch(s, @"^v?\d+([._]\d+)*$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        string name;
        if (IsVersion(last) && segments.Count >= 2 && !IsVersion(segments[^2])) name = $"{segments[^2]} {(last.StartsWith('v') ? last : "v" + last)}";
        else if (!IsVersion(last)) name = last;
        else return uri;
        if (name.Length < 3 || name.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return uri;
        name = name.Replace('_', ' ').Replace('-', ' ');
        return char.ToUpperInvariant(name[0]) + name[1..];
    }
    public const string TestScope = "These tests check schema rules locally. They do not test business logic, external integrations or runtime delivery.";

    /// <summary>A package without a global root element: it defines reusable types and fields, not a complete message.</summary>
    public static bool IsLibrary(SchemaPackage package) => package.EffectiveSchema.Count == 0;

    /// <summary>Global declarations ("reusable schema definitions") across the package.</summary>
    public static int Definitions(SchemaPackage package) => package.Files.Sum(file => file.Declarations.Count);

    public static string LibraryExplanation(SchemaPackage package) =>
        $"It contains {Count(Definitions(package), "reusable schema definition", "reusable schema definitions")} that other XSDs can use, but no top-level message element.";

    /// <summary>The package's structural status in words that never imply the integration is valid.</summary>
    public static string ValidityHeadline(string validation) => validation switch { "Valid" => "XSD package structurally valid", "Partial" => "XSD package partly resolved", _ => "XSD package not structurally valid" };

    /// <summary>The top-level message element(s) — "None" for a library XSD.</summary>
    public static string MessageRoot(SchemaPackage package) => package.EffectiveSchema.Count switch
    {
        0 => "None",
        1 => package.EffectiveSchema[0].Name,
        var roots => $"{roots} top-level elements",
    };

    /// <summary>Imports and includes between the package's files ("None" when the package has none).</summary>
    public static string Dependencies(SchemaPackage package)
    {
        var links = package.Files.Sum(file => file.Links.Count);
        var unresolved = package.Files.Sum(file => file.Links.Count(link => link.ResolvedFile is null));
        return links == 0 ? "None" : $"{Count(links, "import/include", "imports/includes")}{(unresolved > 0 ? $" · {unresolved} unresolved" : "")}";
    }

    public static string DependencyDetail(SchemaPackage package) =>
        $"{package.Files.Sum(file => file.Links.Count)} imports/includes · {package.Files.Sum(file => file.Links.Count(link => link.ResolvedFile is null))} unresolved";

    /// <summary>The meaning of a global declaration: "Reusable object" (complex type), "Text type" (simple type on xs:string), "Message element".</summary>
    public static string DeclarationMeaning(SchemaDeclaration declaration) => declaration.Kind switch
    {
        "complexType" => "Reusable object",
        "simpleType" => BuiltInLabel(declaration.BaseType ?? declaration.Type) is { } builtIn ? $"{builtIn} type" : "Restricted value type",
        "element" => "Top-level message element",
        "attribute" => "Attribute",
        _ => declaration.Kind,
    };

    /// <summary>The exact XSD declaration kind, for technical detail.</summary>
    public static string KindLabel(string kind) => kind switch
    {
        "simpleType" => "Simple type", "complexType" => "Complex type", "element" => "Element", "attribute" => "Attribute", _ => kind,
    };

    /// <summary>Readable built-in XML Schema types (xs:string → Text); null for anything that is not a built-in.</summary>
    public static string? BuiltInLabel(string? lexical)
    {
        if (lexical is null || !lexical.StartsWith("xs:", StringComparison.Ordinal) && !lexical.StartsWith("xsd:", StringComparison.Ordinal)) return null;
        return XmlSchemaPackageAnalyzer.LocalName(lexical) switch
        {
            "string" or "normalizedString" or "token" => "Text",
            "boolean" => "True/false",
            "integer" or "int" or "long" or "short" or "byte" or "nonNegativeInteger" or "positiveInteger" or "negativeInteger" or "nonPositiveInteger"
                or "unsignedInt" or "unsignedLong" or "unsignedShort" or "unsignedByte" => "Whole number",
            "decimal" or "double" or "float" => "Number",
            "date" => "Date",
            "dateTime" => "Date and time",
            "time" => "Time",
            "gYear" => "Year",
            "anyURI" => "Web address",
            "base64Binary" => "Binary data",
            _ => null,
        };
    }

    /// <summary>The readable base of a type: "Text" for xs:string, otherwise the declared base/type name without its prefix.</summary>
    public static string BaseLabel(SchemaDeclaration declaration) =>
        BuiltInLabel(declaration.BaseType ?? declaration.Type) ?? XmlSchemaPackageAnalyzer.LocalName(declaration.BaseType ?? declaration.Type) ?? "—";

    /// <summary>Required/optional/repeated in normal words, from minOccurs/maxOccurs — the one wording every Schema Studio view uses
    /// (the exact "1..1" stays available as technical detail).</summary>
    public static string Occurrence(string min, string max) => (min, max) switch
    {
        ("1", "1") => "Required once",
        ("0", "1") => "Optional, max once",
        ("0", "0") => "Not allowed",
        ("0", "unbounded") => "Optional, repeating",
        ("1", "unbounded") => "Required, repeating",
        (var lower, "unbounded") => $"At least {lower} times",
        ("0", var upper) => $"Optional, up to {upper} times",
        (var lower, var upper) when lower == upper => $"Exactly {lower} times",
        (var lower, var upper) => $"{lower}–{upper} times",
    };

    /// <summary>Exact occurrence as written ("1..1", "0..*").</summary>
    public static string ExactOccurrence(string min, string max) => $"{min}..{(max == "unbounded" ? "*" : max)}";

    /// <summary>The tester-facing name of an XSD rule; the exact facet name stays available as technical detail.</summary>
    public static string RuleName(string facetKind) => facetKind switch
    {
        "Occurrence" => "Required / optional / repeated",
        "minLength" => "Minimum length",
        "maxLength" => "Maximum length",
        "length" => "Exact length",
        "pattern" => "Pattern",
        "enumeration" => "Allowed value",
        "minInclusive" => "Minimum value",
        "maxInclusive" => "Maximum value",
        "minExclusive" => "Greater than",
        "maxExclusive" => "Less than",
        "totalDigits" => "Maximum digits",
        "fractionDigits" => "Maximum decimals",
        "whiteSpace" => "Whitespace handling",
        _ => facetKind,
    };

    /// <summary>The Constraints summary label of a category, saying what is counted: allowed values are counted per value, not per field.</summary>
    public static string SummaryLabel(string category) => category switch
    {
        "Occurrence" => "Occurrence rules",
        "Length" => "Length rules",
        "Pattern" => "Pattern rules",
        "Enumeration" => "Allowed-value entries",
        "Numeric bounds" => "Numeric bounds",
        _ => CategoryLabel(category),
    };

    /// <summary>The plain meaning of one facet value: "1 character" for a length, the recognized pattern meaning, the value otherwise.
    /// The exact facet value stays available next to it.</summary>
    public static string RuleMeaning(string facetKind, string value) => facetKind switch
    {
        "minLength" or "maxLength" or "length" => SchemaReadablePresentation.Characters(value),
        "pattern" => SchemaReadablePresentation.PatternLabel(value),
        "totalDigits" => Count(int.TryParse(value, out var digits) ? digits : 0, "digit", "digits"),
        "fractionDigits" => Count(int.TryParse(value, out var decimals) ? decimals : 0, "decimal", "decimals"),
        _ => value,
    };

    /// <summary>The tester-facing name of a rule category (the Constraints summary and filter).</summary>
    public static string CategoryLabel(string category) => category switch
    {
        "Enumeration" => "Allowed values",
        "Occurrence" => "Required / optional / repeated",
        _ => category,
    };

    /// <summary>Rule categories in a fixed reading order, with counts (zero-count categories included so the summary is complete).</summary>
    public static IReadOnlyList<SchemaRuleCount> RuleCounts(IEnumerable<string> categories)
    {
        var counts = categories.GroupBy(c => c).ToDictionary(g => g.Key, g => g.Count());
        string[] order = ["Occurrence", "Length", "Pattern", "Enumeration", "Numeric bounds"];
        return [.. order.Select(c => new SchemaRuleCount(c, counts.GetValueOrDefault(c))),
            .. counts.Keys.Except(order).Order(StringComparer.Ordinal).Select(c => new SchemaRuleCount(c, counts[c]))];
    }

    /// <summary>A short rule summary for a type row: "12 chars · 12-digit pattern", "3 allowed values", "2 fields".</summary>
    public static string RuleSummary(SchemaDeclaration declaration)
    {
        var parts = new List<string>();
        var facets = declaration.Facets;
        var min = facets.FirstOrDefault(f => f.Kind == "minLength")?.Value;
        var max = facets.FirstOrDefault(f => f.Kind == "maxLength")?.Value;
        if (facets.FirstOrDefault(f => f.Kind == "length") is { } exact) parts.Add($"{exact.Value} chars");
        else if (min is not null && min == max) parts.Add($"{min} chars");
        else
        {
            if (min is not null) parts.Add($"min {min} chars");
            if (max is not null) parts.Add($"max {max} chars");
        }
        foreach (var pattern in facets.Where(f => f.Kind == "pattern")) parts.Add(PatternSummary(pattern.Value));
        if (facets.Count(f => f.Kind == "enumeration") is > 0 and var values) parts.Add(Count(values, "allowed value", "allowed values"));
        if (facets.FirstOrDefault(f => f.Kind is "minInclusive" or "minExclusive") is { } lower) parts.Add($"from {lower.Value}");
        if (facets.FirstOrDefault(f => f.Kind is "maxInclusive" or "maxExclusive") is { } upper) parts.Add($"to {upper.Value}");
        if (declaration.Children.Count > 0) parts.Add(Count(declaration.Children.Count, "field", "fields"));
        if (declaration.Kind == "complexType" && declaration.BaseType is { } extends) parts.Add($"extends {XmlSchemaPackageAnalyzer.LocalName(extends)}");
        return parts.Count == 0 ? "—" : string.Join(" · ", parts);
    }

    /// <summary>"12-digit pattern" for digit-count patterns; every other pattern is reported neutrally as "pattern rule".</summary>
    public static string PatternSummary(string pattern) =>
        System.Text.RegularExpressions.Regex.Match(pattern, @"^(?:\\d|\[0-9\])\{(\d+)\}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50))
            is { Success: true } digits ? $"{digits.Groups[1].Value}-digit pattern"
            : SchemaPatternMeaning.Recognize(pattern) ?? "pattern rule";

    /// <summary>"Type / Field" — the last two segments of a schema path, for compact tables (the full path stays in the evidence detail).</summary>
    public static string ShortPath(string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length <= 2 ? string.Join(" / ", segments) : string.Join(" / ", segments[^2..]);
    }

    /// <summary>The file name of a path (full path on hover/details only).</summary>
    public static string FileName(string? path) => string.IsNullOrEmpty(path) ? "—" : path.Replace('\\', '/').Split('/')[^1];

    /// <summary>A short documentation preview (first line, bounded); the full text sits behind "Show documentation".</summary>
    public static (string Preview, bool Truncated) DocumentationPreview(string documentation, int limit = 90)
    {
        var text = SchemaReadablePresentation.Normalize(documentation);
        var firstLine = text.Split('\n')[0];
        return firstLine.Length <= limit && firstLine.Length == text.Length ? (text, false) : (firstLine.Length <= limit ? firstLine : firstLine[..limit].TrimEnd() + "…", true);
    }

    /// <summary>Category of a package-search hit in normal terms: Field, Type, Rule, Namespace or File.</summary>
    public static string SearchCategory(string match, string tab) => match switch
    {
        "filename" => "File",
        "namespace" => "Namespace",
        "field" => "Field",
        "declaration" => tab == "Types" ? "Type" : "Definition",
        _ => "Rule",
    };

    /// <summary>Counts change lines by kind; namespaces changed = target namespaces present in only one of the two versions.</summary>
    public static SchemaChangeSummary Changes(IReadOnlyCollection<string> lines, SchemaPackage before, SchemaPackage after)
    {
        static HashSet<string> Namespaces(SchemaPackage p) => [.. p.Files.Select(f => f.TargetNamespace ?? "")];
        var namespaces = Namespaces(before); namespaces.SymmetricExceptWith(Namespaces(after));
        return new(lines.Count(l => l.Contains("declaration added", StringComparison.Ordinal)), lines.Count(l => l.Contains("declaration removed", StringComparison.Ordinal)),
            lines.Count(l => l.Contains("constraints changed", StringComparison.Ordinal)), namespaces.Count,
            lines.Count(l => l.StartsWith("Needs review", StringComparison.Ordinal) || l.StartsWith("Potentially breaking", StringComparison.Ordinal)));
    }

    public static string Count(int count, string one, string many) => $"{count} {(count == 1 ? one : many)}";
}
