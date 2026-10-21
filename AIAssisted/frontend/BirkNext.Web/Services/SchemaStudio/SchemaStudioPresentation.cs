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
    public const string TestScope = "These tests check schema rules locally. They do not test business logic, external integrations or runtime delivery.";

    /// <summary>A package without a global root element: it defines reusable types and fields, not a complete message.</summary>
    public static bool IsLibrary(SchemaPackage package) => package.EffectiveSchema.Count == 0;

    /// <summary>Global declarations ("reusable schema definitions") across the package.</summary>
    public static int Definitions(SchemaPackage package) => package.Files.Sum(file => file.Declarations.Count);

    public static string LibraryExplanation(SchemaPackage package) =>
        $"It contains {Count(Definitions(package), "reusable schema definition", "reusable schema definitions")} that other XSDs can use, but no top-level message element.";

    /// <summary>"Schema valid" for a valid package; otherwise the validator's own label (never implies the integration is valid).</summary>
    public static string ValidityHeadline(string validation) => validation switch { "Valid" => "Schema valid", "Partial" => "Schema partly resolved", _ => "Schema not valid" };

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
        "element" => "Top-level element",
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

    /// <summary>Required/optional/repeated in normal words, from minOccurs/maxOccurs.</summary>
    public static string Occurrence(string min, string max) => (min, max) switch
    {
        ("1", "1") => "Required once",
        ("0", "1") => "Optional",
        ("0", "unbounded") => "Any number",
        ("1", "unbounded") => "At least one",
        (var lower, "unbounded") => $"At least {lower}",
        ("0", var upper) => $"Optional, up to {upper}",
        (var lower, var upper) when lower == upper => $"Exactly {lower}",
        (var lower, var upper) => $"Between {lower} and {upper}",
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
            is { Success: true } digits ? $"{digits.Groups[1].Value}-digit pattern" : "pattern rule";

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
